using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.Infrastructure.Knowledge;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// Pins the Markdown dialect of the built-in playbooks: front-matter metadata, level-2
/// headings as chunk boundaries and citation anchors that stay stable across re-indexing.
/// </summary>
public sealed class KnowledgeDocumentParserTests
{
    private static readonly KnowledgeBaseOptions Options = new() { MaxChunkCharacters = 600 };

    private const string Playbook = """
        ---
        slug: pse-rules
        title: PSE Trading Rules
        mandatory: true
        horizons: DAY, WEEK
        categories: RULES, RISK
        ---

        # PSE trading rules

        Intro paragraph that is comfortably longer than the minimum chunk length filter.

        ## Static price band

        The exchange enforces a +50% ceiling and a -30% floor around the reference close.

        ## Board lots

        Every order must be a whole board lot, and the lot size is set by the price bracket.
        """;

    [Fact]
    public void Parse_reads_the_front_matter_and_splits_on_level_two_headings()
    {
        var document = KnowledgeDocumentParser.Parse(Playbook, "pse_rules.md", null, Options);

        Assert.Equal("pse-rules", document.Slug);
        Assert.Equal("PSE Trading Rules", document.Title);
        Assert.True(document.IsMandatory);
        Assert.Equal(new[] { "DAY", "WEEK" }, document.Horizons);
        Assert.Equal(new[] { "RULES", "RISK" }, document.Categories);
        Assert.Equal(3, document.Chunks.Count);
        Assert.Equal("Overview", document.Chunks[0].SectionTitle);
        Assert.Equal(0, document.Chunks[0].Ordinal);
        Assert.Equal("Static price band", document.Chunks[1].SectionTitle);
        // The numeric suffix is the chunk's position in the document, so anchors stay unique.
        Assert.Equal("pse-rules#static-price-band-2", document.Chunks[1].Reference);
        Assert.StartsWith("pse-rules#board-lots", document.Chunks[2].Reference);
    }

    [Fact]
    public void Parse_falls_back_to_the_file_name_when_there_is_no_front_matter()
    {
        var document = KnowledgeDocumentParser.Parse(
            "## Only section\n\nA paragraph long enough to be kept as a chunk by the parser.",
            "Trading Desk Notes.md",
            "Desk notes",
            Options);

        Assert.Equal("trading-desk-notes", document.Slug);
        Assert.Equal("Desk notes", document.Title);
        Assert.False(document.IsMandatory);
        Assert.Equal(new[] { "DAY", "WEEK", "MONTH", "YEAR" }, document.Horizons);
        Assert.Equal(new[] { "GENERAL" }, document.Categories);
        Assert.Single(document.Chunks);
        Assert.Equal("Only section", document.Chunks[0].SectionTitle);
    }

    [Fact]
    public void Chunk_drops_sections_shorter_than_the_minimum_useful_length()
    {
        var chunks = KnowledgeDocumentParser.Chunk(
            $"## Tiny\n\ntoo short\n\n## Real\n\n{new string('x', 80)}",
            "book",
            400);

        Assert.Single(chunks);
        Assert.Equal("Real", chunks[0].SectionTitle);
        Assert.Equal("book#real", chunks[0].Reference);
    }

    [Fact]
    public void Chunk_splits_a_long_section_on_paragraph_boundaries_with_unique_references()
    {
        var paragraph = new string('a', 300);
        var chunks = KnowledgeDocumentParser.Chunk(
            $"## Long\n\n{paragraph}\n\n{paragraph}\n\n{paragraph}",
            "book",
            400);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.True(chunk.Content.Length <= 400));
        Assert.All(chunks, chunk => Assert.StartsWith("book#long", chunk.Reference));
        Assert.Equal(3, chunks.Select(chunk => chunk.Reference).Distinct().Count());
    }

    [Fact]
    public void ChunkPlainText_groups_pdf_paragraphs_and_titles_each_part_by_its_first_line()
    {
        var text = $"Risk sizing\n\n{new string('b', 200)}\n\nPosition sizing\n\n{new string('c', 200)}";

        var chunks = KnowledgeDocumentParser.ChunkPlainText(text, "upload-book", 400);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Risk sizing", chunks[0].SectionTitle);
        Assert.Equal("upload-book#part-1", chunks[0].Reference);
        Assert.Equal("upload-book#part-2", chunks[1].Reference);
    }

    [Fact]
    public void ContentHash_is_stable_and_changes_with_the_body()
    {
        var hash = KnowledgeDocumentParser.ContentHash("alpha");

        Assert.Equal(hash, KnowledgeDocumentParser.ContentHash("alpha"));
        Assert.NotEqual(hash, KnowledgeDocumentParser.ContentHash("beta"));
        Assert.Equal(64, hash.Length);
    }
}
