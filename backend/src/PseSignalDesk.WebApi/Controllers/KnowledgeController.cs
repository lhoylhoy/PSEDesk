using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.WebApi.Services;

namespace PseSignalDesk.WebApi.Controllers;

/// <summary>
/// REST surface for the AI Money Manager's knowledge base: list the indexed playbooks and
/// uploads, re-index the shipped Markdown files, upload personal research notes or licensed
/// documents, and delete an upload. Built-in playbooks are protected from deletion.
/// </summary>
[ApiController]
[Route("api/knowledge")]
public sealed class KnowledgeController : ControllerBase
{
    private const long MaxUploadBytes = 20L * 1024 * 1024;

    private readonly IKnowledgeRetrievalService _knowledge;
    private readonly ILogger<KnowledgeController> _logger;

    public KnowledgeController(
        IKnowledgeRetrievalService knowledge,
        ILogger<KnowledgeController> logger)
    {
        _knowledge = knowledge;
        _logger = logger;
    }

    /// <summary>
    /// Every indexed document with its horizon/category tags and embedding telemetry.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<KnowledgeDocumentDto>>> List(
        CancellationToken cancellationToken)
    {
        var documents = await _knowledge.ListAsync(cancellationToken);
        return Ok(documents);
    }

    /// <summary>
    /// Re-syncs the shipped Markdown playbooks. Unchanged files are skipped by content hash,
    /// so a manual re-index does not re-embed the whole library.
    /// </summary>
    [HttpPost("reindex")]
    [EnableRateLimiting(DeskRateLimitPolicies.Model)]
    public async Task<ActionResult<KnowledgeIngestionResult>> Reindex(CancellationToken cancellationToken)
    {
        var result = await _knowledge.IndexBuiltInAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Uploads a Markdown, plain-text or PDF document to ground the reasoning model. The uploader
    /// must attest that they hold the rights to index the document, that it contains no personal
    /// information or material non-public information, and that its retrieved excerpts will be
    /// sent to the configured Ollama Cloud endpoint. The file is scoped to the selected horizons
    /// so it never outranks the horizon-specific playbooks.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    [EnableRateLimiting(DeskRateLimitPolicies.Model)]
    public async Task<ActionResult<KnowledgeDocumentDto>> Upload(
        [FromForm] IFormFile? file,
        [FromForm] string? title,
        [FromForm] string? horizons,
        [FromForm] string? categories,
        [FromForm] bool confirmRightsAndCloudProcessing = false,
        CancellationToken cancellationToken = default)
    {
        if (!confirmRightsAndCloudProcessing)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Rights and cloud-processing confirmation required",
                Detail = "Confirm that you hold the rights or permission to index this document, that it " +
                         "contains no personal information or material non-public information, and that its " +
                         "retrieved passages are sent to the configured Ollama Cloud endpoint. Send " +
                         "confirmRightsAndCloudProcessing=true with the upload."
            });
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Missing file",
                Detail = "Attach a research note or licensed document as multipart form data in the 'file' " +
                         "field (Markdown, TXT or PDF)."
            });
        }

        if (file.Length > MaxUploadBytes)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "File too large",
                Detail = "The knowledge-base upload limit is 20 MB."
            });
        }

        if (!KnowledgeUploadPolicy.IsSupported(file.FileName))
        {
            var extension = Path.GetExtension(file.FileName);

            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Unsupported file type",
                Detail = (string.IsNullOrEmpty(extension)
                        ? "The upload has no file extension."
                        : $"'{extension}' is not an indexable document format.") +
                    $" Upload {KnowledgeUploadPolicy.SupportedFormats}."
            });
        }

        var isPdf = file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        byte[] content;

        using (var buffer = new MemoryStream())
        {
            await file.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        var request = new KnowledgeUploadRequest(
            file.FileName,
            title,
            ParseTags(horizons) ?? Array.Empty<string>(),
            ParseTags(categories) ?? Array.Empty<string>(),
            TextContent: isPdf ? null : System.Text.Encoding.UTF8.GetString(content),
            BinaryContent: isPdf ? content : null);

        try
        {
            var document = await _knowledge.IngestAsync(request, cancellationToken);
            return CreatedAtAction(nameof(List), document);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Upload could not be indexed",
                Detail = ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Knowledge-base upload {File} failed.", file.FileName);
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Knowledge-base ingestion failed",
                detail: ex.Message);
        }
    }

    /// <summary>
    /// Deletes an uploaded document. Built-in playbooks are read-only (HTTP 409).
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var documents = await _knowledge.ListAsync(cancellationToken);
        var document = documents.FirstOrDefault(item => item.Id == id);

        if (document is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Unknown document",
                Detail = $"No knowledge-base document has id {id}."
            });
        }

        if (document.Source == "builtin")
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Built-in playbook",
                Detail = $"{document.Slug} ships with the desk and cannot be deleted. Edit the Markdown file and re-index instead."
            });
        }

        var deleted = await _knowledge.DeleteAsync(id, cancellationToken);

        return deleted
            ? NoContent()
            : Problem(statusCode: StatusCodes.Status409Conflict, title: "Delete refused");
    }

    private static IReadOnlyList<string>? ParseTags(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(tag => tag.Trim())
                .Where(tag => tag.Length > 0)
                .ToList();
}

