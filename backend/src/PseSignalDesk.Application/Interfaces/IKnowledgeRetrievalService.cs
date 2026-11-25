using PseSignalDesk.Application.Models;

namespace PseSignalDesk.Application.Interfaces;

/// <summary>
/// The AI Money Manager's "brain": seeds the curated playbooks, ingests uploaded books
/// and retrieves horizon-specific guardrails for a signal run. Retrieval must always
/// degrade to deterministic lexical + tag ranking rather than fail the run when no
/// embedding model is configured.
/// </summary>
public interface IKnowledgeRetrievalService
{
    Task<IReadOnlyList<KnowledgeDocumentDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeSnippet>> RetrieveAsync(
        KnowledgeQuery query,
        CancellationToken cancellationToken = default);

    Task<KnowledgeIngestionResult> IndexBuiltInAsync(CancellationToken cancellationToken = default);

    Task<KnowledgeDocumentDto> IngestAsync(
        KnowledgeUploadRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
