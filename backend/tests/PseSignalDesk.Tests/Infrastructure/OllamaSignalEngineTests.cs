using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Infrastructure.AI;
using PseSignalDesk.Infrastructure.Configuration;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// The engine is exercised against a stub <see cref="HttpMessageHandler"/> that routes by the
/// <c>model</c> field of the request body: no socket is opened and nothing about the live
/// Ollama Cloud catalogue is assumed.
/// </summary>
public sealed class OllamaSignalEngineTests
{
    private const string ReasoningModel = "deepseek-v4.1-flash";
    private const string FastModel = "glm-5.3-flash";

    private const string ModelNotFound = """{"error": "model 'x' not found. Try glm-5.3-flash:cloud."}""";

    private const string ValidPayload = """
        {
          "top_10": [
            {
              "rank": 1,
              "ticker": "ICT",
              "conviction_score": 88,
              "entry_price": 506.5,
              "stop_loss": 473.2,
              "target_price": 575.0,
              "thesis": "Breakout above the resistance shelf on 2.4x volume.",
              "bear_case": "A close under 473 invalidates the breakout."
            }
          ],
          "dropped_from_yesterday": []
        }
        """;

    [Fact]
    public async Task GenerateSignalAsync_keeps_the_ranking_when_the_fast_model_is_not_served()
    {
        var handler = new ModelRoutingHandler(FastModel, HttpStatusCode.NotFound, ModelNotFound, ReasoningModel, ValidPayload);
        var engine = CreateEngine(handler, fastModel: FastModel);

        var result = await engine.GenerateSignalAsync(BuildRequest());

        // A wrong fast-model name is an optional-pass problem: the book still comes back.
        Assert.Single(result.Output.Top10);
        Assert.Equal("ICT", result.Output.Top10[0].Ticker);
        Assert.Equal(1, handler.CallsFor(FastModel));
        Assert.Equal(1, handler.CallsFor(ReasoningModel));
    }

    [Fact]
    public async Task GenerateSignalAsync_stops_calling_a_fast_model_the_endpoint_rejects()
    {
        var handler = new ModelRoutingHandler(FastModel, HttpStatusCode.NotFound, ModelNotFound, ReasoningModel, ValidPayload);
        var engine = CreateEngine(handler, fastModel: FastModel);

        // Two horizons of the same session.
        await engine.GenerateSignalAsync(BuildRequest());
        await engine.GenerateSignalAsync(BuildRequest() with { Horizon = SignalHorizon.DAY });

        // One 404 is enough: the pass switches itself off instead of throwing once per horizon.
        Assert.Equal(1, handler.CallsFor(FastModel));
        Assert.Equal(2, handler.CallsFor(ReasoningModel));
    }

    [Fact]
    public async Task GenerateSignalAsync_still_reports_a_ranking_failure_as_signal_generation()
    {
        var handler = new ModelRoutingHandler(
            ReasoningModel, HttpStatusCode.NotFound, ModelNotFound,
            FastModel, """{"summary": "Nothing structural to add."}""");
        var engine = CreateEngine(handler, fastModel: FastModel);

        var error = await Assert.ThrowsAsync<SignalGenerationException>(
            () => engine.GenerateSignalAsync(BuildRequest()));

        // The API contract is unchanged: a failed ranking is a 502 with the upstream detail.
        Assert.Contains("Ollama Cloud returned HTTP 404", error.Message, StringComparison.Ordinal);
        Assert.Contains("not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateSignalAsync_skips_the_fast_pass_when_no_fast_model_is_configured()
    {
        var handler = new ModelRoutingHandler(FastModel, HttpStatusCode.NotFound, ModelNotFound, ReasoningModel, ValidPayload);
        var engine = CreateEngine(handler, fastModel: "   ");

        await engine.GenerateSignalAsync(BuildRequest());

        Assert.Equal(0, handler.CallsFor(FastModel));
        Assert.Equal(1, handler.CallsFor(ReasoningModel));
    }

    [Fact]
    public async Task GenerateSignalAsync_constrains_the_response_with_a_json_schema_and_sends_the_sampling_controls()
    {
        var handler = new CapturingHandler((_, _) => ChatOk(ValidPayload));
        var engine = CreateEngine(handler);

        await engine.GenerateSignalAsync(BuildRequest());

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var format = body.RootElement.GetProperty("format");
        Assert.Equal("object", format.GetProperty("type").GetString());

        var pickProperties = format.GetProperty("properties")
            .GetProperty("top_10")
            .GetProperty("items")
            .GetProperty("properties");

        Assert.True(pickProperties.TryGetProperty("ticker", out _));
        Assert.True(pickProperties.TryGetProperty("bear_case", out _));

        // The server assigns rank from array order, so the model is no longer asked for fields
        // the pipeline would overwrite.
        Assert.False(pickProperties.TryGetProperty("rank", out _));
        Assert.False(pickProperties.TryGetProperty("drift_status", out _));

        var options = body.RootElement.GetProperty("options");
        Assert.Equal(0.1m, options.GetProperty("temperature").GetDecimal());
        Assert.Equal(42, options.GetProperty("seed").GetInt32());
        Assert.Equal(16_384, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(4_096, options.GetProperty("num_predict").GetInt32());

        // No effort configured means no think field at all, so a provider that rejects it is
        // never handed one by default.
        Assert.False(body.RootElement.TryGetProperty("think", out _));
    }

    [Fact]
    public async Task GenerateSignalAsync_forwards_the_configured_reasoning_effort_as_think()
    {
        var handler = new CapturingHandler((_, _) => ChatOk(ValidPayload));
        var engine = CreateEngine(handler, new OllamaCloudOptions
        {
            BaseUrl = "https://ollama.test",
            ApiKey = "test-key",
            Model = ReasoningModel,
            ReasoningEffort = "high",
            FastExtractionModel = string.Empty
        });

        await engine.GenerateSignalAsync(BuildRequest());

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("high", body.RootElement.GetProperty("think").GetString());
    }

    [Fact]
    public async Task GenerateSignalAsync_falls_back_to_plain_json_mode_when_the_endpoint_rejects_the_schema()
    {
        var handler = new CapturingHandler((document, _) =>
            document.RootElement.GetProperty("format").ValueKind == JsonValueKind.Object
                ? Error(HttpStatusCode.BadRequest, """{"error": "format must be json or a schema"}""")
                : ChatOk(ValidPayload));

        var engine = CreateEngine(handler);

        var result = await engine.GenerateSignalAsync(BuildRequest());

        // One rejected schema is retried once, not once per horizon.
        Assert.Equal(2, handler.Bodies.Count);
        Assert.False(result.Metadata.SchemaConstrained);
        Assert.Single(result.Output.Top10);

        using (var retry = JsonDocument.Parse(handler.Bodies[1]))
        {
            Assert.Equal("json", retry.RootElement.GetProperty("format").GetString());
        }

        await engine.GenerateSignalAsync(BuildRequest() with { Horizon = SignalHorizon.DAY });

        using var later = JsonDocument.Parse(handler.Bodies[2]);
        Assert.Equal("json", later.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task GenerateSignalAsync_records_the_prompt_fingerprint_and_the_provider_token_usage()
    {
        var handler = new CapturingHandler((_, _) => ChatOk(ValidPayload, promptTokens: 8_123, completionTokens: 640));
        var engine = CreateEngine(handler);

        var first = await engine.GenerateSignalAsync(BuildRequest());
        var second = await engine.GenerateSignalAsync(BuildRequest());

        Assert.Equal(8_123, first.Metadata.PromptTokens);
        Assert.Equal(640, first.Metadata.CompletionTokens);
        Assert.Equal(ReasoningModel, first.Metadata.Model);
        Assert.Equal(0, first.Metadata.RetryCount);
        Assert.True(first.Metadata.SchemaConstrained);
        Assert.False(first.Metadata.FastExtractionApplied);
        Assert.True(first.Metadata.PromptCharacters > 1_000);
        Assert.Equal(64, first.Metadata.PromptHash.Length);

        // Same session and same inputs means the same fingerprint: that is what makes two runs
        // comparable instead of merely similar.
        Assert.Equal(first.Metadata.PromptHash, second.Metadata.PromptHash);
    }

    private static HttpResponseMessage ChatOk(
        string content,
        int? promptTokens = null,
        int? completionTokens = null)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            model = ReasoningModel,
            message = new { role = "assistant", content },
            prompt_eval_count = promptTokens,
            eval_count = completionTokens
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static OllamaSignalEngine CreateEngine(ModelRoutingHandler handler, string fastModel) =>
        CreateEngine(handler, new OllamaCloudOptions
        {
            BaseUrl = "https://ollama.test",
            ApiKey = "test-key",
            Model = ReasoningModel,
            FastExtractionModel = fastModel
        });

    private static OllamaSignalEngine CreateEngine(HttpMessageHandler handler, OllamaCloudOptions? options = null) =>
        new(
            new HttpClient(handler),
            Options.Create(options ?? new OllamaCloudOptions
            {
                BaseUrl = "https://ollama.test",
                ApiKey = "test-key",
                Model = ReasoningModel,
                // The optional pre-pass is exercised by its own tests: a single-call test must
                // stay a single call, so the default here is "no fast model".
                FastExtractionModel = string.Empty
            }),
            new SignalJsonParser(),
            NullLogger<OllamaSignalEngine>.Instance);

    private static OllamaSignalRequest BuildRequest() =>
        new(
            TradeDate: new DateOnly(2026, 11, 24),
            Horizon: SignalHorizon.WEEK,
            Regime: MarketRegime.BULLISH,
            RegimeNote: "PSEi above its 50D and 200D averages.",
            PseiClose: 8_514.08m,
            NetForeignFlowPhp: null,
            UsdPhp: 58.27m,
            Candidates: new List<SignalCandidateInput>
            {
                new(
                    Ticker: "ICT",
                    CompanyName: "International Container Terminal Services, Inc.",
                    Sector: "Industrial",
                    ClosePrice: 430m,
                    ChangePct: 1.25m,
                    AvgTurnover20dPhp: 250_000_000m,
                    VolumeRatioVs20d: 1.4m,
                    Rsi14: 58.2m,
                    Ema20: 421m,
                    Sma50: 405m,
                    Sma200: 372m,
                    Atr14: 9.5m,
                    SupportLevel: 415m,
                    ResistanceLevel: 448m,
                    NetForeignTodayPhp: null,
                    QuantPreScore: 74,
                    Disclosures: Array.Empty<StockDisclosure>())
            },
            YesterdayTop10: Array.Empty<PriorSignalInput>());

    /// <summary>
    /// Records every request body and answers through a supplied delegate, so a test can assert
    /// on the exact JSON the engine sends (schema, options, think) without a socket.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<JsonDocument, int, HttpResponseMessage> _respond;

        public CapturingHandler(Func<JsonDocument, int, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Bodies.Add(body);

            using var document = JsonDocument.Parse(body);

            return _respond(document, Bodies.Count);
        }
    }

    private sealed class ModelRoutingHandler : HttpMessageHandler
    {
        private readonly string _failingModel;
        private readonly HttpStatusCode _failingStatus;
        private readonly string _failingBody;
        private readonly string _healthyModel;
        private readonly string _healthyContent;
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        public ModelRoutingHandler(
            string failingModel,
            HttpStatusCode failingStatus,
            string failingBody,
            string healthyModel,
            string healthyContent)
        {
            _failingModel = failingModel;
            _failingStatus = failingStatus;
            _failingBody = failingBody;
            _healthyModel = healthyModel;
            _healthyContent = healthyContent;
        }

        public int CallsFor(string model) => _calls.TryGetValue(model, out var count) ? count : 0;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("https://ollama.test/api/chat", request.RequestUri!.ToString());

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            using var document = JsonDocument.Parse(body);
            var model = document.RootElement.GetProperty("model").GetString() ?? string.Empty;
            _calls.AddOrUpdate(model, 1, (_, count) => count + 1);

            if (string.Equals(model, _failingModel, StringComparison.Ordinal))
            {
                return Json(_failingStatus, _failingBody);
            }

            if (string.Equals(model, _healthyModel, StringComparison.Ordinal))
            {
                var envelope = JsonSerializer.Serialize(new
                {
                    model,
                    message = new { role = "assistant", content = _healthyContent }
                });

                return Json(HttpStatusCode.OK, envelope);
            }

            return Json(HttpStatusCode.BadRequest, """{"error": "unexpected model name in test"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
