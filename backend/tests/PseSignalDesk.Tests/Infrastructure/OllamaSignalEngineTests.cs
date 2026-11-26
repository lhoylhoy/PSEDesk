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

        var output = await engine.GenerateSignalAsync(BuildRequest());

        // A wrong fast-model name is an optional-pass problem: the book still comes back.
        Assert.Single(output.Top10);
        Assert.Equal("ICT", output.Top10[0].Ticker);
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

    private static OllamaSignalEngine CreateEngine(ModelRoutingHandler handler, string fastModel) =>
        new(
            new HttpClient(handler),
            Options.Create(new OllamaCloudOptions
            {
                BaseUrl = "https://ollama.test",
                ApiKey = "test-key",
                Model = ReasoningModel,
                FastExtractionModel = fastModel
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
