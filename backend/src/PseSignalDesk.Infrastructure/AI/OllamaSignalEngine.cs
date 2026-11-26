using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Infrastructure.Configuration;

namespace PseSignalDesk.Infrastructure.AI;

/// <summary>
/// Thin, typed client for Ollama Cloud's chat API. It enforces the strict JSON
/// contract and performs exactly one corrective retry when parsing fails.
/// </summary>
public sealed class OllamaSignalEngine : IOllamaSignalEngine
{
    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly OllamaCloudOptions _options;
    private readonly SignalJsonParser _parser;
    private readonly ILogger<OllamaSignalEngine> _logger;

    /// <summary>
    /// A <see cref="OllamaCloudOptions.FastExtractionModel"/> the endpoint does not serve is a
    /// configuration error, not a transient fault. Once the upstream answers <c>404</c> the pass
    /// stays off for the life of this instance so the remaining horizons of the same session do
    /// not repeat (and log) the same failure.
    /// </summary>
    private volatile bool _fastExtractionUnavailable;

    public OllamaSignalEngine(
        HttpClient httpClient,
        IOptions<OllamaCloudOptions> options,
        SignalJsonParser parser,
        ILogger<OllamaSignalEngine> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _parser = parser;
        _logger = logger;
    }

    public string EngineName => $"Ollama Cloud ({_options.Model})";

    public async Task<OllamaSignalOutput> GenerateSignalAsync(
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

        var enriched = await EnrichWithFastExtractionAsync(request, cancellationToken);

        var messages = new List<ChatMessage>
        {
            new("system", OllamaPromptBuilder.BuildSystemPrompt(enriched.Horizon)),
            new("user", OllamaPromptBuilder.BuildUserPrompt(enriched))
        };

        var firstResponse = await SendRankingChatAsync(messages, cancellationToken);
        var firstParse = _parser.Parse(firstResponse);

        if (firstParse.IsSuccess)
        {
            return firstParse.Output!;
        }

        _logger.LogWarning(
            "Ollama Cloud response failed validation ({Errors}). Sending one corrective retry.",
            string.Join(" | ", firstParse.Errors));

        messages.Add(new ChatMessage("assistant", firstResponse));
        messages.Add(new ChatMessage("user", OllamaPromptBuilder.BuildCorrectionPrompt(firstParse.Errors, enriched)));

        var retryResponse = await SendRankingChatAsync(messages, cancellationToken);
        var retryParse = _parser.Parse(retryResponse);

        if (retryParse.IsSuccess)
        {
            return retryParse.Output!;
        }

        throw new SignalGenerationException(
            "Ollama Cloud returned an invalid signal payload after one corrective retry: " +
            string.Join(" | ", retryParse.Errors));
    }

    /// <summary>
    /// The reasoning call. A non-success answer is rethrown as
    /// <see cref="SignalGenerationException"/> so the API keeps mapping a failed ranking to
    /// <c>502</c>, exactly as before the optional pass learned to inspect the status code.
    /// </summary>
    private async Task<string> SendRankingChatAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendChatAsync(messages, _options.Model, cancellationToken);
        }
        catch (ChatResponseException ex)
        {
            throw new SignalGenerationException(ex.Message, ex);
        }
    }

    private async Task<string> SendChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), "api/chat");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        httpRequest.Content = JsonContent.Create(new
        {
            model,
            messages,
            stream = false,
            format = "json",
            options = new { temperature = 0.1 }
        });

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
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new ChatResponseException(
                    response.StatusCode,
                    $"Ollama Cloud returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                    Truncate(body, 600));
            }

            OllamaChatEnvelope? envelope;

            try
            {
                envelope = JsonSerializer.Deserialize<OllamaChatEnvelope>(body, EnvelopeJsonOptions);
            }
            catch (JsonException ex)
            {
                throw new SignalGenerationException(
                    "Ollama Cloud returned a malformed response envelope: " + Truncate(body, 400), ex);
            }

            var content = envelope?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new SignalGenerationException(
                    "Ollama Cloud returned an empty assistant message. " + Truncate(body, 400));
            }

            return content;
        }
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
            var raw = await SendChatAsync(messages, _options.FastExtractionModel, cancellationToken);
            var json = SignalJsonParser.ExtractJsonObject(raw);

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

    private sealed class OllamaChatEnvelope
    {
        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; set; }
    }

    private sealed class OllamaChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}

