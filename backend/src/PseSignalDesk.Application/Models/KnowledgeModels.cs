using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Models;

/// <summary>
/// One retrieved slice of a knowledge-base playbook, ready to be injected into the
/// prompt and cited back by the model.
/// </summary>
public sealed record KnowledgeSnippet(
    string DocumentSlug,
    string SectionTitle,
    string Reference,
    string Content,
    IReadOnlyList<string> Horizons,
    IReadOnlyList<string> Categories,
    double Score);

/// <summary>
/// API view of one indexed knowledge document (built-in playbook or uploaded book).
/// </summary>
public sealed record KnowledgeDocumentDto(
    int Id,
    string Slug,
    string Title,
    string Source,
    string SourcePath,
    IReadOnlyList<string> Horizons,
    IReadOnlyList<string> Categories,
    bool IsMandatory,
    int ChunkCount,
    int EmbeddedChunkCount,
    string ContentHash,
    DateTime UpdatedAtUtc);

/// <summary>
/// Upload payload for a Markdown, text or PDF trading book. The controller fills
/// <see cref="TextContent"/> for Markdown/TXT and <see cref="BinaryContent"/> for PDF.
/// </summary>
public sealed record KnowledgeUploadRequest(
    string FileName,
    string? Title,
    IReadOnlyList<string> Horizons,
    IReadOnlyList<string> Categories,
    string? TextContent = null,
    byte[]? BinaryContent = null);

/// <summary>
/// Retrieval request for one signal run: the horizon mission plus the candidate context
/// that should steer the ranking.
/// </summary>
public sealed record KnowledgeQuery(
    string QueryText,
    SignalHorizon Horizon,
    string? Sector = null,
    IReadOnlyList<PseTrapFlag>? TrapFlags = null,
    int MaxSnippets = 6);

/// <summary>
/// Outcome of a knowledge-base (re)index pass. Chunk counts are what the API reports so
/// the UI can show embedding telemetry honestly.
/// </summary>
public sealed record KnowledgeIngestionResult(
    int DocumentsIndexed,
    int DocumentsUpdated,
    int DocumentsSkipped,
    int ChunksIndexed,
    int ChunksEmbedded,
    string EmbeddingModel);
