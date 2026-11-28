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
    /// summary per trading day). Leave blank to skip the pass entirely. It must be a model
    /// the endpoint actually serves: an unknown name answers HTTP 404 and (because the pass
    /// is optional) simply switches it off for the process.
    /// </summary>
    public string FastExtractionModel { get; set; } = "glm-5.3-flash";

    /// <summary>
    /// Embedding model used to vectorise knowledge-base chunks. Blank disables vector
    /// search and leaves retrieval on the lexical + tag path.
    /// </summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    public string ApiKey { get; set; } = string.Empty;

    public int RequestTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Sampling temperature for the ranking call. The desk wants a considered, repeatable
    /// book rather than variety, so this stays low.
    /// </summary>
    public decimal Temperature { get; set; } = 0.1m;

    /// <summary>
    /// Fixed sampling seed, so two calls with the same prompt make the same choices where the
    /// provider honours it. Null leaves the provider's own seeding in charge.
    /// </summary>
    public int? Seed { get; set; } = 42;

    /// <summary>
    /// Context window requested from the provider. The ranking prompt for a full 25-candidate
    /// run is roughly 8k tokens, so a window that is too small silently truncates the tail of
    /// the prompt (yesterday's list and the task line) instead of failing loudly.
    /// </summary>
    public int NumCtx { get; set; } = 16_384;

    /// <summary>
    /// Upper bound on the completion length. It covers the assistant's answer <em>and</em> a thinking
    /// model's reasoning trace, so a value that is too small can leave a model with nothing but
    /// thinking tokens and an empty answer; the engine then retries once with thinking disabled.
    /// Null leaves the provider default.
    /// </summary>
    public int? NumPredict { get; set; } = 4_096;

    /// <summary>
    /// Nucleus sampling cut-off. Null leaves the provider default (1.0).
    /// </summary>
    public decimal? TopP { get; set; }

    /// <summary>
    /// Reasoning effort for models that support thinking: blank leaves the provider default,
    /// <c>off</c>/<c>false</c> disables it, and <c>on</c>/<c>true</c>/<c>low</c>/<c>medium</c>/
    /// <c>high</c>/<c>max</c> are forwarded as the <c>think</c> request field. An unrecognised
    /// value is ignored rather than sent, so a typo cannot fail a live run. A model that answers
    /// with an empty message because its reasoning trace consumed the whole completion budget is
    /// retried once with thinking disabled and then left on that setting for this engine instance.
    /// </summary>
    public string ReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// Send the response contract as a JSON schema instead of plain JSON mode. Schema-constrained
    /// output is materially less likely to be malformed; if the endpoint rejects the schema the
    /// engine falls back to JSON mode once and logs it.
    /// </summary>
    public bool UseStructuredOutput { get; set; } = true;

    /// <summary>
    /// When true, persists the exact system prompt, user prompt and raw assistant response per
    /// generation under <see cref="PromptArchiveDirectory"/> for offline replay and prompt audits.
    /// </summary>
    public bool ArchivePrompts { get; set; }

    public string PromptArchiveDirectory { get; set; } = "prompt-archive";

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public bool HasFastExtractionModel => !string.IsNullOrWhiteSpace(FastExtractionModel);

    public bool HasEmbeddingModel => !string.IsNullOrWhiteSpace(EmbeddingModel);
}
