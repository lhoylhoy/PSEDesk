namespace PseSignalDesk.Infrastructure.Configuration;

/// <summary>
/// Ollama Cloud configuration. The API key can also be supplied through the
/// OLLAMA_API_KEY environment variable or dotnet user-secrets. There is no offline
/// engine: without a key the pipeline fails with HTTP 400.
/// </summary>
public sealed class OllamaCloudOptions
{
    public const string SectionName = "OllamaCloud";

    public string BaseUrl { get; set; } = "https://ollama.com";

    /// <summary>
    /// The reasoning model that ranks the Top 10. It sees the retrieved playbooks and the
    /// deterministic candidate table.
    /// </summary>
    public string Model { get; set; } = "deepseek-v4.1-flash";

    /// <summary>
    /// Cheap model used for the fast pre-extraction pass (one compact disclosure/trap
    /// summary per trading day). Leave blank to skip the pass entirely.
    /// </summary>
    public string FastExtractionModel { get; set; } = "qwen3.5:4b";

    /// <summary>
    /// Embedding model used to vectorise knowledge-base chunks. Blank disables vector
    /// search and leaves retrieval on the lexical + tag path.
    /// </summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    public string ApiKey { get; set; } = string.Empty;

    public int RequestTimeoutSeconds { get; set; } = 120;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public bool HasFastExtractionModel => !string.IsNullOrWhiteSpace(FastExtractionModel);

    public bool HasEmbeddingModel => !string.IsNullOrWhiteSpace(EmbeddingModel);
}
