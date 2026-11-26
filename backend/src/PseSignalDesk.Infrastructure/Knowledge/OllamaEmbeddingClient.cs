using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.Infrastructure.Diagnostics;

namespace PseSignalDesk.Infrastructure.Knowledge;

/// <summary>
/// Minimal Ollama embeddings client (<c>POST /api/embed</c>). Embedding failures are
/// returned as <c>null</c> rather than thrown: the knowledge base is an enhancement and
/// retrieval degrades to lexical scoring when the model is unavailable.
/// </summary>
public sealed class OllamaEmbeddingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly OllamaCloudOptions _options;
    private readonly ILogger<OllamaEmbeddingClient> _logger;
    private readonly IFeedHealthRegistry _feedHealth;

    public OllamaEmbeddingClient(
        HttpClient httpClient,
        IOptions<OllamaCloudOptions> options,
        ILogger<OllamaEmbeddingClient> logger,
        IFeedHealthRegistry? feedHealth = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _feedHealth = feedHealth ?? new FeedHealthRegistry();
    }

    public bool IsConfigured => _options.HasApiKey && _options.HasEmbeddingModel;

    public string Model => _options.EmbeddingModel;

    /// <summary>
    /// Embeds a batch of texts. Returns <c>null</c> when no model is configured or the
    /// upstream call fails; a partial response is padded with <c>null</c> entries so the
    /// caller can keep the successfully embedded chunks.
    /// </summary>
    public async Task<IReadOnlyList<float[]?>?> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || texts.Count == 0)
        {
            return null;
        }

        var endpoint = new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), "api/embed");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { model = _options.EmbeddingModel, input = texts })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _feedHealth.RecordFailure(
                    FeedSource.Embeddings,
                    $"HTTP {(int)response.StatusCode} from /api/embed for model '{_options.EmbeddingModel}'.");

                _logger.LogWarning(
                    "Ollama embeddings returned HTTP {Status}; falling back to lexical retrieval.",
                    (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<OllamaEmbedEnvelope>(JsonOptions, cancellationToken);
            var vectors = body?.Embeddings;

            if (vectors is null || vectors.Count == 0)
            {
                _feedHealth.RecordDegraded(
                    FeedSource.Embeddings,
                    "the endpoint answered with an empty embedding payload; retrieval stays lexical.");

                _logger.LogWarning("Ollama embeddings returned an empty payload; falling back to lexical retrieval.");
                return null;
            }

            _feedHealth.RecordSuccess(
                FeedSource.Embeddings,
                $"{vectors.Count} vector(s) from '{_options.EmbeddingModel}' ({vectors[0].Length} dimensions).",
                vectors.Count);

            var result = new List<float[]?>(texts.Count);

            for (var i = 0; i < texts.Count; i++)
            {
                result.Add(i < vectors.Count && vectors[i].Length > 0 ? vectors[i] : null);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _feedHealth.RecordFailure(
                FeedSource.Embeddings,
                $"{ex.GetType().Name} while calling /api/embed; retrieval stays lexical.");

            _logger.LogWarning(ex, "Ollama embeddings call failed; falling back to lexical retrieval.");
            return null;
        }
    }

    private sealed class OllamaEmbedEnvelope
    {
        [JsonPropertyName("embeddings")]
        public List<float[]>? Embeddings { get; set; }
    }
}
