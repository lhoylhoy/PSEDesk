using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Infrastructure.Configuration;

namespace PseSignalDesk.Infrastructure.AI;

/// <summary>
/// Thin, typed client for Ollama Cloud's chat API. It sends the response contract as a JSON
/// schema, controls the sampling/context knobs the desk needs to reproduce a run, enforces the
/// strict JSON contract and performs exactly one corrective retry when parsing fails. A thinking
/// model that spends the whole completion budget on its reasoning trace and answers with an empty
/// message is retried once without thinking instead of failing the book.
/// </summary>
public sealed class OllamaSignalEngine : IOllamaSignalEngine
{
    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parsed once: the schema is a constant, and re-parsing it per horizon would be pointless
    /// allocation. The node is only ever read, never mutated.
    /// </summary>
    private static readonly JsonNode ResponseSchema = JsonNode.Parse(OllamaPromptBuilder.ResponseSchemaJson)!;

    private static readonly JsonNode FastExtractionSchema = JsonNode.Parse(OllamaPromptBuilder.FastExtractionSchemaJson)!;

    /// <summary>Plain JSON mode, the fallback when an endpoint rejects schema-constrained output.</summary>
    private static readonly JsonNode JsonMode = JsonValue.Create("json")!;

    private readonly HttpClient _httpClient;
    private readonly OllamaCloudOptions _options;
    private readonly SignalJsonParser _parser;
    private readonly ILogger<OllamaSignalEngine> _logger;
    private readonly string _archiveRoot;

    /// <summary>
    /// A <see cref="OllamaCloudOptions.FastExtractionModel"/> the endpoint does not serve is a
    /// configuration error, not a transient fault. Once the upstream answers <c>404</c> the pass
    /// stays off for the life of this instance so the remaining horizons of the same session do
    /// not repeat (and log) the same failure.
    /// </summary>
    private volatile bool _fastExtractionUnavailable;

    /// <summary>
    /// An endpoint that rejects the response schema with <c>400</c> falls back to plain JSON
    /// mode once and stays there, so one unsupported knob cannot fail every horizon.
    /// </summary>
    private volatile bool _structuredOutputUnavailable;

    /// <summary>
    /// The model that answered an empty assistant message because its reasoning trace consumed
    /// the whole completion budget. <c>num_predict</c> caps thinking <em>and</em> answer tokens, so
    /// once one call proves the trace can exhaust it, this engine instance asks that model not to
    /// think again (the corrective retry and an evaluation loop reuse it). Per model, because the
    /// cheap fast-extraction model is a different caller.
    /// </summary>
    private volatile string? _reasoningOverrunModel;

    public OllamaSignalEngine(
        HttpClient httpClient,
        IOptions<OllamaCloudOptions> options,
        SignalJsonParser parser,
        ILogger<OllamaSignalEngine> logger,
        IHostEnvironment? environment = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _parser = parser;
        _logger = logger;
        _archiveRoot = environment?.ContentRootPath ?? AppContext.BaseDirectory;
    }

    public string EngineName => $"Ollama Cloud ({_options.Model})";

    public async Task<OllamaSignalResult> GenerateSignalAsync(
        OllamaSignalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.HasApiKey)
        {
            throw new OllamaConfigurationException(
                "Ollama Cloud is not configured. Set the OLLAMA_API_KEY environment variable (or " +
                "OllamaCloud:ApiKey / dotnet user-secrets) and run the pipeline again. There is no offline " +
                "ranking engine: the desk is live-only.");
        }

        var stopwatch = Stopwatch.StartNew();
        var enriched = await EnrichWithFastExtractionAsync(request, cancellationToken);

        var systemPrompt = OllamaPromptBuilder.BuildSystemPrompt(enriched.Horizon);
        var userPrompt = OllamaPromptBuilder.BuildUserPrompt(enriched);
        var promptHash = OllamaPromptBuilder.ComputePromptHash(systemPrompt, userPrompt);

        var messages = new List<ChatMessage>
        {
            new("system", systemPrompt),
            new("user", userPrompt)
        };

        var completion = await SendRankingChatAsync(messages, cancellationToken);
        var parse = _parser.Parse(completion.Content);
        var retryCount = completion.RetryCount;

        if (!parse.IsSuccess)
        {
            _logger.LogWarning(
                "Ollama Cloud response failed validation ({Errors}). Sending one corrective retry.",
                string.Join(" | ", parse.Errors));

            retryCount++;
            messages.Add(new ChatMessage("assistant", completion.Content));
            messages.Add(new ChatMessage("user", OllamaPromptBuilder.BuildCorrectionPrompt(parse.Errors, enriched)));

            completion = await SendRankingChatAsync(messages, cancellationToken);
            retryCount += completion.RetryCount;
            parse = _parser.Parse(completion.Content);

            if (!parse.IsSuccess)
            {
                throw new SignalGenerationException(
                    "Ollama Cloud returned an invalid signal payload after one corrective retry: " +
                    string.Join(" | ", parse.Errors));
            }
        }

        stopwatch.Stop();

        var metadata = new OllamaGenerationMetadata(
            EngineName,
            _options.Model,
            promptHash,
            _options.Temperature,
            _options.Seed,
            systemPrompt.Length + userPrompt.Length,
            completion.PromptTokens,
            completion.CompletionTokens,
            retryCount,
            (int)stopwatch.ElapsedMilliseconds,
            completion.SchemaConstrained,
            !string.IsNullOrWhiteSpace(enriched.FastExtractionSummary));

        await ArchivePromptAsync(enriched, systemPrompt, userPrompt, completion.Content, metadata, cancellationToken);

        return new OllamaSignalResult(parse.Output!, metadata);
    }

    private async Task ArchivePromptAsync(
        OllamaSignalRequest request,
        string systemPrompt,
        string userPrompt,
        string rawResponse,
        OllamaGenerationMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (!_options.ArchivePrompts || string.IsNullOrWhiteSpace(_options.PromptArchiveDirectory))
        {
            return;
        }

        try
        {
            var directory = Path.IsPathRooted(_options.PromptArchiveDirectory)
                ? _options.PromptArchiveDirectory
                : Path.Combine(_archiveRoot, _options.PromptArchiveDirectory);
            Directory.CreateDirectory(directory);

            var shortHash = metadata.PromptHash.Length >= 12 ? metadata.PromptHash[..12] : metadata.PromptHash;
            var path = Path.Combine(directory, $"{request.TradeDate:yyyy-MM-dd}_{request.Horizon}_{shortHash}.json");
            var payload = JsonSerializer.Serialize(new
            {
                request.TradeDate,
                Horizon = request.Horizon.ToString(),
                metadata.Model,
                metadata.PromptHash,
                metadata.Temperature,
                metadata.Seed,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                RawResponse = rawResponse,
                ArchivedAtUtc = DateTime.UtcNow
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

            await File.WriteAllTextAsync(path, payload, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not archive prompt for {Date} {Horizon}.", request.TradeDate, request.Horizon);
        }
    }

    /// <summary>
    /// The reasoning call. A non-success answer is rethrown as
    /// <see cref="SignalGenerationException"/> so the API keeps mapping a failed ranking to
    /// <c>502</c>, exactly as before the optional pass learned to inspect the status code.
    /// </summary>
    private async Task<ChatCompletion> SendRankingChatAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendStructuredChatAsync(
                messages,
                _options.Model,
                ResponseSchema,
                cancellationToken);
        }
        catch (ChatResponseException ex)
        {
            throw new SignalGenerationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Sends one chat call with the response contract as a JSON schema. If the endpoint rejects
    /// the schema (<c>400</c>), the call is retried once in plain JSON mode and the schema is
    /// disabled for the life of this instance: a provider that does not support structured
    /// output must not take the desk down.
    /// </summary>
    private async Task<ChatCompletion> SendStructuredChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        JsonNode schema,
        CancellationToken cancellationToken)
    {
        var constrained = _options.UseStructuredOutput && !_structuredOutputUnavailable;

        try
        {
            return await SendChatAsync(
                messages,
                model,
                constrained ? schema : JsonMode,
                constrained,
                cancellationToken);
        }
        catch (ChatResponseException ex) when (constrained && ex.Status == HttpStatusCode.BadRequest)
        {
            _structuredOutputUnavailable = true;
            _logger.LogWarning(
                "Ollama Cloud rejected the response schema (HTTP 400). Falling back to JSON mode for the " +
                "rest of this run. Detail: {Detail}",
                Truncate(ex.Message, 300));

            return await SendChatAsync(messages, model, JsonMode, schemaConstrained: false, cancellationToken);
        }
    }

    /// <summary>
    /// Maps <see cref="OllamaCloudOptions.ReasoningEffort"/> onto the request's <c>think</c>
    /// field. Blank or unrecognised values are omitted rather than sent, so a typo in
    /// configuration degrades to the provider default instead of failing a live run. A model
    /// that already exhausted its completion budget on a reasoning trace is asked not to think
    /// for the rest of this engine instance: repeating the overrun would just lose another call.
    /// </summary>
    private object? ResolveThinkingMode(string model)
    {
        if (_reasoningOverrunModel is { } overrun &&
            string.Equals(overrun, model, StringComparison.Ordinal))
        {
            return false;
        }

        var effort = _options.ReasoningEffort.Trim().ToLowerInvariant();

        return effort switch
        {
            "" => null,
            "off" or "false" or "none" or "disabled" => false,
            "on" or "true" => true,
            "low" or "medium" or "high" or "max" => effort,
            _ => null
        };
    }

    /// <summary>
    /// Only the knobs that are actually configured are sent. Explicit nulls would be rejected
    /// by the endpoint, and a zero or negative bound is meaningless.
    /// </summary>
    private Dictionary<string, object> BuildSamplingOptions()
    {
        var options = new Dictionary<string, object>
        {
            ["temperature"] = _options.Temperature
        };

        if (_options.Seed is int seed)
        {
            options["seed"] = seed;
        }

        if (_options.NumCtx > 0)
        {
            options["num_ctx"] = _options.NumCtx;
        }

        if (_options.NumPredict is int numPredict && numPredict > 0)
        {
            options["num_predict"] = numPredict;
        }

        if (_options.TopP is decimal topP && topP > 0m && topP <= 1m)
        {
            options["top_p"] = topP;
        }

        return options;
    }

    /// <summary>
    /// Sends one chat call. A thinking model can spend the whole completion budget
    /// (<see cref="OllamaCloudOptions.NumPredict"/>) inside its reasoning trace and return an
    /// empty <c>content</c>; that is a recoverable configuration mismatch, not a missing book.
    /// The call is retried once with <c>think: false</c> so the whole budget is available for
    /// the JSON answer, and that model is not asked to think again on this engine instance.
    /// </summary>
    private Task<ChatCompletion> SendChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        JsonNode format,
        bool schemaConstrained,
        CancellationToken cancellationToken)
        => SendChatAsync(
            messages,
            model,
            format,
            schemaConstrained,
            thinkingOverride: null,
            retriedWithoutThinking: false,
            cancellationToken);

    private async Task<ChatCompletion> SendChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        JsonNode format,
        bool schemaConstrained,
        object? thinkingOverride,
        bool retriedWithoutThinking,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), "api/chat");

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false,
            ["format"] = format
        };

        var thinking = thinkingOverride ?? ResolveThinkingMode(model);
        if (thinking is not null)
        {
            body["think"] = thinking;
        }

        var sampling = BuildSamplingOptions();
        if (sampling.Count > 0)
        {
            body["options"] = sampling;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        httpRequest.Content = JsonContent.Create(body);

        HttpResponseMessage response;

        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SignalGenerationException(
                $"Ollama Cloud did not respond within {_options.RequestTimeoutSeconds} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SignalGenerationException("Could not reach Ollama Cloud at " + endpoint + ".", ex);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new ChatResponseException(
                    response.StatusCode,
                    $"Ollama Cloud returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                    Truncate(payload, 600));
            }

            OllamaChatEnvelope? envelope;

            try
            {
                envelope = JsonSerializer.Deserialize<OllamaChatEnvelope>(payload, EnvelopeJsonOptions);
            }
            catch (JsonException ex)
            {
                throw new SignalGenerationException(
                    "Ollama Cloud returned a malformed response envelope: " + Truncate(payload, 400), ex);
            }

            var reply = envelope?.Message?.Content;
            if (string.IsNullOrWhiteSpace(reply))
            {
                var reasoningCharacters = envelope?.Message?.Thinking?.Length ?? 0;
                var completionTokens = envelope?.EvalCount;
                var reportedReason = envelope?.DoneReason;
                var doneReason = string.IsNullOrWhiteSpace(reportedReason) ? "unreported" : reportedReason;

                // The reasoning trace and the stop reason are what explain an empty answer, so
                // they belong in the operator log. The caller gets an actionable sentence
                // instead of the upstream body (which is model output, not a diagnosis).
                _logger.LogWarning(
                    "Ollama Cloud returned an empty assistant message for '{Model}' (done_reason: {DoneReason}, " +
                    "completion tokens: {CompletionTokens}, thinking characters: {ThinkingCharacters}). Body: {Body}",
                    model,
                    doneReason,
                    completionTokens,
                    reasoningCharacters,
                    Truncate(payload, 2_000));

                if (!retriedWithoutThinking && thinking is not false)
                {
                    // num_predict caps thinking + answer tokens together. Retrying with thinking
                    // off leaves the whole budget for the JSON contract, and remembering the
                    // model keeps a corrective retry or evaluation loop from paying the same
                    // overrun again.
                    _reasoningOverrunModel = model;

                    _logger.LogWarning(
                        "Retrying '{Model}' once with thinking disabled so its completion budget can be used " +
                        "for the answer. Set OllamaCloud:ReasoningEffort=off (or lower it) or raise " +
                        "OllamaCloud:NumPredict to skip this extra call.",
                        model);

                    return await SendChatAsync(
                        messages,
                        model,
                        format,
                        schemaConstrained,
                        thinkingOverride: false,
                        retriedWithoutThinking: true,
                        cancellationToken);
                }

                throw new SignalGenerationException(
                    DescribeEmptyMessage(model, doneReason, completionTokens, retriedWithoutThinking));
            }

            var completion = new ChatCompletion(
                reply,
                envelope!.PromptEvalCount,
                envelope.EvalCount,
                schemaConstrained);

            return retriedWithoutThinking ? completion with { RetryCount = 1 } : completion;
        }
    }

    private static string DescribeEmptyMessage(
        string model,
        string doneReason,
        int? completionTokens,
        bool retriedWithoutThinking)
    {
        var tokens = completionTokens is int count ? $", {count} completion tokens" : string.Empty;
        var recovery = retriedWithoutThinking
            ? " It also answered with an empty message after one retry with thinking disabled."
            : string.Empty;

        return $"Ollama Cloud returned an empty assistant message for '{model}' " +
            $"(the provider stopped with '{doneReason}'{tokens}).{recovery} A reasoning model can spend the " +
            "whole completion budget before writing an answer: set OllamaCloud:ReasoningEffort=off (or lower " +
            "it) or raise OllamaCloud:NumPredict.";
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength] + "...";

    /// <summary>
    /// Cheap dual-model pass: the fast extraction model reads the disclosure/trap feed and
    /// returns one compact note that is prepended to the reasoning model's prompt. The pass
    /// is strictly optional — any failure leaves the request untouched.
    /// </summary>
    private async Task<OllamaSignalRequest> EnrichWithFastExtractionAsync(
        OllamaSignalRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.HasFastExtractionModel || _fastExtractionUnavailable || request.Candidates.Count == 0)
        {
            return request;
        }

        var messages = new List<ChatMessage>
        {
            new(
                "system",
                "You are a terse Philippine equity disclosure screener. Read the candidate feed and answer with JSON only."),
            new("user", OllamaPromptBuilder.BuildFastExtractionPrompt(request))
        };

        try
        {
            var completion = await SendStructuredChatAsync(
                messages,
                _options.FastExtractionModel,
                FastExtractionSchema,
                cancellationToken);
            var json = SignalJsonParser.ExtractJsonObject(completion.Content);

            if (json is null)
            {
                _logger.LogWarning("Fast extraction returned no JSON object; continuing without a screening note.");
                return request;
            }

            var note = JsonSerializer.Deserialize<FastExtractionNote>(json, EnvelopeJsonOptions);
            var summary = note?.Summary?.Trim();

            if (string.IsNullOrWhiteSpace(summary))
            {
                return request;
            }

            if (note!.TrapFlags is { Count: > 0 } flags)
            {
                summary += " Trap flags: " + string.Join("; ", flags.Take(6));
            }

            return request with { FastExtractionSummary = summary };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChatResponseException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // HTTP 404 means the endpoint does not serve the configured model name, so every
            // later horizon would fail the same way. Switch the optional pass off and say once,
            // plainly, what to change; the reasoning model is untouched.
            _fastExtractionUnavailable = true;

            _logger.LogWarning(
                "Fast extraction is off for this process: Ollama Cloud does not serve the model " +
                "'{Model}' (HTTP 404). Point OllamaCloud:FastExtractionModel (or OLLAMA_FAST_MODEL) " +
                "at a model your account can reach, or leave it blank to skip the pass. The reasoning " +
                "model ({ReasoningModel}) is unaffected.",
                _options.FastExtractionModel,
                _options.Model);

            return request;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fast extraction pass failed; continuing with the reasoning model only.");
            return request;
        }
    }

    /// <summary>
    /// A non-success HTTP answer from the chat endpoint, carrying the status code so the
    /// optional fast-extraction pass can tell "this model does not exist" apart from a
    /// transient fault. The reasoning path converts it back to
    /// <see cref="SignalGenerationException"/>.
    /// </summary>
    private sealed class ChatResponseException : Exception
    {
        public ChatResponseException(HttpStatusCode status, string message) : base(message)
        {
            Status = status;
        }

        public HttpStatusCode Status { get; }
    }

    private sealed class FastExtractionNote
    {
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("trap_flags")]
        public List<string>? TrapFlags { get; set; }
    }

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    /// <summary>
    /// One accepted assistant answer plus the usage figures the endpoint reported, so a run can
    /// record what it actually cost. <see cref="RetryCount"/> counts the calls spent recovering
    /// an empty answer, so <c>ModelRetryCount</c> reflects every retry the engine actually made.
    /// </summary>
    private sealed record ChatCompletion(
        string Content,
        int? PromptTokens,
        int? CompletionTokens,
        bool SchemaConstrained,
        int RetryCount = 0);

    private sealed class OllamaChatEnvelope
    {
        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int? PromptEvalCount { get; set; }

        [JsonPropertyName("eval_count")]
        public int? EvalCount { get; set; }

        /// <summary>
        /// Why the generation stopped: <c>stop</c> for a finished answer, <c>length</c> when the
        /// completion budget (<c>num_predict</c>) ran out — the signal that a reasoning trace
        /// consumed the whole allowance before the answer was written.
        /// </summary>
        [JsonPropertyName("done_reason")]
        public string? DoneReason { get; set; }
    }

    private sealed class OllamaChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        /// <summary>
        /// The model's reasoning trace when thinking is enabled. It is never returned as the
        /// answer, but its length and the stop reason are what explain an empty <c>content</c>.
        /// </summary>
        [JsonPropertyName("thinking")]
        public string? Thinking { get; set; }
    }
}

