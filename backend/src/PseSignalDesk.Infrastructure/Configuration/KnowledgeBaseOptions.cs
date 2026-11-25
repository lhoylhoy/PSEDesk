namespace PseSignalDesk.Infrastructure.Configuration;

/// <summary>
/// Configuration for the SQLite RAG knowledge base that grounds the AI Money Manager.
/// The Markdown playbooks shipped under <c>knowledge-base/</c> are seeded on startup and
/// re-embedded only when their content hash changes.
/// </summary>
public sealed class KnowledgeBaseOptions
{
    public const string SectionName = "KnowledgeBase";

    /// <summary>
    /// Folder holding the curated Markdown playbooks. Resolved against the content root,
    /// so a published build reads the copy that MSBuild places next to the binaries.
    /// </summary>
    public string RootPath { get; set; } = "knowledge-base";

    /// <summary>
    /// Maximum sections injected into one prompt (mandatory guardrails included).
    /// </summary>
    public int MaxSnippets { get; set; } = 6;

    /// <summary>
    /// Upper bound on the characters of one section. Uploaded books are split further so
    /// a whole chapter can never crowd the prompt.
    /// </summary>
    public int MaxChunkCharacters { get; set; } = 1600;

    /// <summary>
    /// Embeddings are optional: when the model is blank or the Ollama call fails, scoring
    /// falls back to lexical BM25 plus horizon/sector/trap boosts.
    /// </summary>
    public bool EnableEmbeddings { get; set; } = true;

    /// <summary>
    /// Runs the built-in playbook pass at startup. Ingestion failures are logged and never
    /// stop the API from serving signals.
    /// </summary>
    public bool IndexOnStartup { get; set; } = true;
}
