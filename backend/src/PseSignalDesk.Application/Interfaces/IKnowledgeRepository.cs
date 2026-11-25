using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;

namespace PseSignalDesk.Application.Interfaces;

/// <summary>
/// Persistence boundary for the SQLite knowledge base (documents plus their embedded
/// chunks). Implementations own the storage detail; retrieval and ingestion only see
/// entities.
/// </summary>
public interface IKnowledgeRepository
{
    Task<IReadOnlyList<KnowledgeDocumentDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<KnowledgeDocument?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<KnowledgeDocument?> FindByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or replaces a document. When <paramref name="replaceChunks"/> is false the
    /// existing chunks (and their embeddings) are kept, which is how an unchanged
    /// built-in Markdown file avoids a re-embedding pass.
    /// </summary>
    Task SaveAsync(
        KnowledgeDocument document,
        bool replaceChunks,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeChunk>> GetAllChunksAsync(CancellationToken cancellationToken = default);

    Task<int> DeleteBuiltInsNotInAsync(
        IReadOnlyCollection<string> activeSlugs,
        CancellationToken cancellationToken = default);
}
