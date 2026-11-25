using System.Text;
using System.Text.RegularExpressions;
using PseSignalDesk.Infrastructure.Configuration;

namespace PseSignalDesk.Infrastructure.Knowledge;

/// <summary>
/// One Markdown section ready to be persisted as a retrievable chunk.
/// </summary>
public sealed record ParsedKnowledgeChunk(int Ordinal, string SectionTitle, string Reference, string Content);

/// <summary>
/// A parsed playbook: front-matter metadata plus the section chunks it produced.
/// </summary>
public sealed record ParsedKnowledgeDocument(
    string Slug,
    string Title,
    bool IsMandatory,
    IReadOnlyList<string> Horizons,
    IReadOnlyList<string> Categories,
    IReadOnlyList<ParsedKnowledgeChunk> Chunks);

/// <summary>
/// Parses the small, dependency-free Markdown dialect used by the built-in playbooks:
/// a YAML-ish front-matter block and <c>##</c> headings as chunk boundaries. A heading
/// becomes a citation anchor, which is why sections must be self-contained.
/// </summary>
public static class KnowledgeDocumentParser
{
    private static readonly string[] AllHorizons = { "DAY", "WEEK", "MONTH", "YEAR" };

    private static readonly Regex HeadingRegex = new(@"^##\s+(?<title>.+?)\s*$", RegexOptions.Compiled);

    public static ParsedKnowledgeDocument Parse(
        string rawText,
        string fallbackSlug,
        string? fallbackTitle,
        KnowledgeBaseOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawText);

        var text = rawText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var (frontMatter, body) = SplitFrontMatter(text);

        var slug = frontMatter.TryGetValue("slug", out var slugValue) && !string.IsNullOrWhiteSpace(slugValue)
            ? KnowledgeText.Slugify(slugValue)
            : KnowledgeText.NormalizeSlug(fallbackSlug);

        var title = frontMatter.TryGetValue("title", out var titleValue) && !string.IsNullOrWhiteSpace(titleValue)
            ? titleValue.Trim()
            : string.IsNullOrWhiteSpace(fallbackTitle) ? slug : fallbackTitle.Trim();

        var isMandatory = false;

        if (frontMatter.TryGetValue("mandatory", out var mandatoryValue) &&
            bool.TryParse(mandatoryValue.Trim(), out var parsedMandatory))
        {
            isMandatory = parsedMandatory;
        }

        var horizons = ReadList(frontMatter, "horizons", AllHorizons);
        var categories = ReadList(frontMatter, "categories", new[] { "GENERAL" });

        var chunks = Chunk(body, slug, options.MaxChunkCharacters);

        return new ParsedKnowledgeDocument(slug, title, isMandatory, horizons, categories, chunks);
    }

    /// <summary>
    /// Splits the body on level-2 headings, then hard-splits any section longer than
    /// <paramref name="maxCharacters"/> on paragraph boundaries so a single chunk never
    /// dominates the prompt.
    /// </summary>
    public static IReadOnlyList<ParsedKnowledgeChunk> Chunk(string body, string slug, int maxCharacters)
    {
        var limit = Math.Max(400, maxCharacters);
        var chunks = new List<ParsedKnowledgeChunk>();
        var sectionTitle = "Overview";
        var buffer = new StringBuilder();

        void Commit()
        {
            var content = Normalize(buffer.ToString());
            buffer.Clear();

            if (content.Length < 40)
            {
                return;
            }

            foreach (var part in SplitLong(content, limit))
            {
                var ordinal = chunks.Count;
                var suffix = ordinal == 0 ? string.Empty : $"-{ordinal + 1}";
                var reference = $"{slug}#{KnowledgeText.Slugify(sectionTitle)}{suffix}";

                chunks.Add(new ParsedKnowledgeChunk(ordinal, sectionTitle, reference, part));
            }
        }

        foreach (var line in body.Split('\n'))
        {
            var match = HeadingRegex.Match(line);
            if (match.Success)
            {
                Commit();
                sectionTitle = match.Groups["title"].Value.Trim();
                continue;
            }

            buffer.AppendLine(line);
        }

        Commit();
        return chunks;
    }

    private static IEnumerable<string> SplitLong(string content, int limit)
    {
        if (content.Length <= limit)
        {
            yield return content;
            yield break;
        }

        var paragraphs = content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > limit)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            current.AppendLine(paragraph);
            current.AppendLine();
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim(), @"\n{3,}", "\n\n").Trim();

    private static (Dictionary<string, string> FrontMatter, string Body) SplitFrontMatter(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return (result, text);
        }

        var lines = text.Split('\n');
        var end = -1;

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            return (result, text);
        }

        for (var i = 1; i < end; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = lines[i][..separator].Trim();
            var value = lines[i][(separator + 1)..].Trim().Trim('[', ']');

            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        var body = string.Join('\n', lines.Skip(end + 1));
        return (result, body);
    }

    private static IReadOnlyList<string> ReadList(
        IReadOnlyDictionary<string, string> frontMatter,
        string key,
        IReadOnlyList<string> fallback)
    {
        if (!frontMatter.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var items = value
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim().ToUpperInvariant())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return items.Count == 0 ? fallback : items;
    }

    /// <summary>
    /// Splits a plain-text book (PDF extraction included) into paragraph groups of at most
    /// <paramref name="maxCharacters"/>, using the first line of each group as its title.
    /// </summary>
    public static IReadOnlyList<ParsedKnowledgeChunk> ChunkPlainText(
        string text,
        string slug,
        int maxCharacters)
    {
        var limit = Math.Max(400, maxCharacters);
        var normalized = Regex.Replace(
            text.Replace("\r\n", "\n", StringComparison.Ordinal),
            @"[ \t]+",
            " ");

        var paragraphs = normalized
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => paragraph.Trim())
            .Where(paragraph => paragraph.Length > 0)
            .ToList();

        var chunks = new List<ParsedKnowledgeChunk>();
        var current = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > limit)
            {
                CommitPlain(current, slug, chunks);
            }

            current.AppendLine(paragraph);
            current.AppendLine();
        }

        if (current.Length > 0)
        {
            CommitPlain(current, slug, chunks);
        }

        return chunks;
    }

    /// <summary>
    /// SHA-256 of the raw text, used to skip re-chunking and re-embedding unchanged files.
    /// </summary>
    public static string ContentHash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static void CommitPlain(StringBuilder buffer, string slug, ICollection<ParsedKnowledgeChunk> chunks)
    {
        var content = buffer.ToString().Trim();
        buffer.Clear();

        if (content.Length < 40)
        {
            return;
        }

        var ordinal = chunks.Count;
        var firstLine = content.Split('\n', 2)[0].Trim();
        var sectionTitle = firstLine.Length is > 0 and <= 90 ? firstLine : $"Part {ordinal + 1}";

        chunks.Add(new ParsedKnowledgeChunk(
            ordinal,
            sectionTitle,
            $"{slug}#part-{ordinal + 1}",
            content));
    }
}
