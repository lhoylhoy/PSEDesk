using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PseSignalDesk.Infrastructure.Knowledge;

/// <summary>
/// Tokenisation and text helpers shared by the chunker and the lexical scorer. The
/// tokenizer is deliberately simple (lowercase, alphanumeric split, light plural
/// stripping) so retrieval stays deterministic and testable without a model.
/// </summary>
public static class KnowledgeText
{
    private static readonly Regex NonAlphaNumeric = new("[^a-z0-9]+", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "has", "have", "if",
        "in", "into", "is", "it", "its", "of", "on", "or", "that", "the", "their", "then",
        "there", "these", "they", "this", "to", "was", "were", "will", "with", "you",
        "your", "can", "not", "but", "all", "any", "more", "than", "when", "which", "who",
        "we", "our", "us", "so", "do", "does", "than", "only", "also", "such", "very"
    };

    /// <summary>
    /// Splits text into normalised tokens, dropping stop words and single characters.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var builder = new StringBuilder();

        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                continue;
            }

            Flush(builder, tokens);
        }

        Flush(builder, tokens);
        return tokens;
    }

    /// <summary>
    /// Builds a citation-safe anchor from a heading: lowercase, alphanumeric groups joined
    /// by single dashes (<c>"Ex-dividend price drops"</c> → <c>ex-dividend-price-drops</c>).
    /// </summary>
    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "section";
        }

        var slug = NonAlphaNumeric
            .Replace(value.ToLowerInvariant(), "-")
            .Trim('-');

        // Collapse runs of dashes produced by punctuation clusters.
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length == 0)
        {
            return "section";
        }

        return slug.Length <= 80 ? slug : slug[..80].Trim('-');
    }

    /// <summary>
    /// Normalises a user-supplied slug (upload file names included) to the same alphabet
    /// as <see cref="Slugify"/>.
    /// </summary>
    public static string NormalizeSlug(string? value) =>
        Slugify(Path.GetFileNameWithoutExtension(value ?? string.Empty));

    public static string FormatPercent(double value) =>
        (value * 100d).ToString("0.0", CultureInfo.InvariantCulture);

    private static void Flush(StringBuilder builder, ICollection<string> tokens)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var token = builder.ToString();
        builder.Clear();

        if (token.Length < 2 || StopWords.Contains(token))
        {
            return;
        }

        tokens.Add(token);
    }
}
