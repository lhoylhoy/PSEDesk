using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.WebApi.Controllers;

namespace PseSignalDesk.Tests.WebApi;

/// <summary>
/// Uploading a document into the retrieval store copies its text to a cloud LLM endpoint and
/// indexes it, so the API refuses the upload until the caller attests to the rights and the
/// cloud processing. These tests pin that gate and prove nothing is ingested without it.
/// </summary>
public sealed class KnowledgeControllerTests
{
    [Fact]
    public async Task Upload_answers_400_when_the_rights_attestation_is_missing()
    {
        var controller = CreateController(new UntouchableKnowledgeService());

        var result = await controller.Upload(
            file: null,
            title: "Snipped article",
            horizons: null,
            categories: null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);

        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("rights", problem.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confirmRightsAndCloudProcessing=true", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_answers_400_for_a_missing_file_once_the_attestation_is_given()
    {
        var controller = CreateController(new UntouchableKnowledgeService());

        var result = await controller.Upload(
            file: null,
            title: null,
            horizons: null,
            categories: null,
            confirmRightsAndCloudProcessing: true);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);

        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("Missing file", problem.Title);
        Assert.Contains("licensed document", problem.Detail, StringComparison.Ordinal);
    }

    private static KnowledgeController CreateController(IKnowledgeRetrievalService knowledge) =>
        new(knowledge, NullLogger<KnowledgeController>.Instance);

    /// <summary>
    /// Fails loudly if reached: the attestation gate must answer before any ingestion happens, so
    /// a guard regression surfaces here instead of quietly shipping a document to the cloud model.
    /// </summary>
    private sealed class UntouchableKnowledgeService : IKnowledgeRetrievalService
    {
        public Task<IReadOnlyList<KnowledgeDocumentDto>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The knowledge base must not be read.");

        public Task<IReadOnlyList<KnowledgeSnippet>> RetrieveAsync(
            KnowledgeQuery query,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The knowledge base must not be read.");

        public Task<KnowledgeIngestionResult> IndexBuiltInAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The knowledge base must not be re-indexed.");

        public Task<KnowledgeDocumentDto> IngestAsync(
            KnowledgeUploadRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An unconfirmed upload must never be ingested.");

        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The knowledge base must not be written.");
    }
}
