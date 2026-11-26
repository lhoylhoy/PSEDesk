namespace PseSignalDesk.WebApi.Services;

/// <summary>
/// The document formats the knowledge base can actually index. Anything else used to be decoded as
/// UTF-8 text and stored, so a binary upload became a page of mojibake in the prompt instead of a
/// clear <c>400</c>.
/// </summary>
public static class KnowledgeUploadPolicy
{
    /// <summary>Lower-case, leading-dot extensions accepted by <c>POST /api/knowledge/upload</c>.</summary>
    private static readonly string[] Extensions = { ".md", ".markdown", ".txt", ".pdf" };

    /// <summary>True only for a file whose extension is on the allowlist (case-insensitive).</summary>
    public static bool IsSupported(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);

        return extension.Length > 0 &&
               Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The human-readable allowlist used in the <c>400</c> detail.</summary>
    public const string SupportedFormats =
        "Markdown (.md, .markdown), plain text (.txt) or PDF (.pdf)";
}
