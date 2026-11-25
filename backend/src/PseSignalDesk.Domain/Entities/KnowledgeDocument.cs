namespace PseSignalDesk.Domain.Entities;

/// <summary>
/// One curated playbook or uploaded research document indexed into the SQLite RAG library.
/// The document row carries the horizon/category tags and the content hash so an
/// unchanged file is never re-chunked or re-embedded on startup.
/// </summary>
public class KnowledgeDocument
{
    public int Id { get; set; }

    /// <summary>
    /// Stable identifier used inside citations, e.g. <c>pse_rules</c> or
    /// <c>upload:2f6c1a</c>. Unique.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// <c>builtin</c> for the shipped Markdown playbooks, <c>upload</c> for user books.
    /// Built-in documents are protected from deletion.
    /// </summary>
    public string Source { get; set; } = "builtin";

    /// <summary>
    /// Relative path of the built-in Markdown file, or the original upload file name.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated horizon tags (DAY, WEEK, MONTH, YEAR).
    /// </summary>
    public string HorizonsCsv { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated playbook categories (TECHNICAL, RISK, FUNDAMENTAL, MACRO, ...).
    /// </summary>
    public string CategoriesCsv { get; set; } = string.Empty;

    /// <summary>
    /// True for guardrail documents (the PSE structural-trap rules) that are injected
    /// into every prompt regardless of the retrieval score.
    /// </summary>
    public bool IsMandatory { get; set; }

    /// <summary>
    /// SHA-256 of the raw source text. Unchanged hashes skip re-embedding.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    public int ChunkCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<KnowledgeChunk> Chunks { get; set; } = new();
}

/// <summary>
/// One retrievable slice of a knowledge document (a Markdown section or a page range
/// of an uploaded book) with an optional vector embedding.
/// </summary>
public class KnowledgeChunk
{
    public int Id { get; set; }

    public int KnowledgeDocumentId { get; set; }

    public KnowledgeDocument? Document { get; set; }

    /// <summary>
    /// Position of the chunk inside the document, so citations stay stable.
    /// </summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// Markdown heading the chunk belongs to ("Ex-dividend price drops"). Used both for
    /// the citation fragment (<c>pse_rules#ex-dividend-price-drops</c>) and for the
    /// lexical index.
    /// </summary>
    public string SectionTitle { get; set; } = string.Empty;

    /// <summary>
    /// Citation fragment: <c>{documentSlug}#{section-slug}</c>.
    /// </summary>
    public string Reference { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string HorizonsCsv { get; set; } = string.Empty;

    public string CategoriesCsv { get; set; } = string.Empty;

    /// <summary>
    /// Little-endian IEEE-754 <c>float32</c> vector, i.e. the exact binary layout used by
    /// <c>sqlite-vec</c>. Null when no embedding model is configured (retrieval then
    /// falls back to BM25 plus metadata ranking).
    /// </summary>
    public byte[]? EmbeddingBlob { get; set; }

    public int EmbeddingDimensions { get; set; }
}
