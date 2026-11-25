using UglyToad.PdfPig;

namespace PseSignalDesk.Infrastructure.Knowledge;

/// <summary>
/// Extracts text from an uploaded PDF book. PdfPig is deterministic and offline; a
/// scanned (image-only) PDF yields little or no text, which the ingestion path reports as
/// an explicit error instead of indexing an empty document.
/// </summary>
public static class PdfTextExtractor
{
    public static string Extract(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        using var stream = new MemoryStream(pdfBytes);
        using var document = PdfDocument.Open(stream);

        var builder = new System.Text.StringBuilder();

        foreach (var page in document.GetPages())
        {
            var text = page.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.AppendLine(text.Trim());
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
