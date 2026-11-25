using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.Infrastructure.Knowledge;
using PseSignalDesk.Infrastructure.Persistence;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// Exercises the SQLite RAG library end to end with embeddings switched off, which is the
/// offline-first path: BM25 plus horizon/trap boosts and the mandatory guardrail injection
/// must all work without a network call.
/// </summary>
public sealed class SqliteKnowledgeServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly PseSignalDbContext _db;
    private readonly KnowledgeRepository _repository;
    private readonly SqliteKnowledgeService _service;
    private readonly KnowledgeBaseOptions _options = new()
    {
        RootPath = "knowledge-base",
        MaxSnippets = 3,
        MaxChunkCharacters = 600,
        EnableEmbeddings = false,
        IndexOnStartup = false,
    };

    public SqliteKnowledgeServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new PseSignalDbContext(
            new DbContextOptionsBuilder<PseSignalDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _repository = new KnowledgeRepository(_db);
        _service = new SqliteKnowledgeService(
            _repository,
            new OllamaEmbeddingClient(
                new HttpClient(),
                Options.Create(new OllamaCloudOptions { ApiKey = string.Empty, EmbeddingModel = string.Empty }),
                NullLogger<OllamaEmbeddingClient>.Instance),
            Options.Create(_options),
            new FakeHostEnvironment(Path.GetTempPath()),
            NullLogger<SqliteKnowledgeService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RetrieveAsync_returns_nothing_when_the_library_is_empty()
    {
        var snippets = await _service.RetrieveAsync(new KnowledgeQuery("vcp breakout", SignalHorizon.WEEK));

        Assert.Empty(snippets);
    }

    [Fact]
    public async Task RetrieveAsync_injects_the_mandatory_guardrails_before_any_playbook()
    {
        await SeedAsync(
            "pse-rules",
            "PSE Trading Rules",
            mandatory: true,
            horizons: "DAY,WEEK,MONTH,YEAR",
            ("Static price band", "The exchange enforces a plus fifty percent ceiling and a minus thirty percent floor around the reference close."),
            ("Board lots", "Every order must be a whole board lot and the lot size is set by the price bracket."));

        await SeedAsync(
            "week-momentum",
            "Weekly VCP momentum",
            mandatory: false,
            horizons: "WEEK",
            ("VCP breakout", "A volatility contraction breakout needs a volume expansion above the twenty day average to confirm demand."));

        var snippets = await _service.RetrieveAsync(
            new KnowledgeQuery("vcp breakout volume expansion", SignalHorizon.WEEK, "Industrial"));

        Assert.Equal(3, snippets.Count);
        Assert.All(snippets.Take(2), snippet => Assert.Equal("pse-rules", snippet.DocumentSlug));
        Assert.Contains(snippets, snippet => snippet.DocumentSlug == "week-momentum");
    }

    [Fact]
    public async Task RetrieveAsync_favours_the_chunk_tagged_with_the_requested_horizon()
    {
        const string body =
            "Core accumulation rules that favour steady earnings, sensible leverage and a margin of safety in the price.";

        await SeedAsync("daily-desk", "Daily desk", mandatory: false, horizons: "DAY", ("Core", body));
        await SeedAsync("yearly-desk", "Yearly desk", mandatory: false, horizons: "YEAR", ("Core", body));

        var snippets = await _service.RetrieveAsync(
            new KnowledgeQuery("core accumulation earnings leverage", SignalHorizon.YEAR));

        Assert.Equal("yearly-desk", snippets[0].DocumentSlug);
        Assert.Contains("YEAR", snippets[0].Horizons);
    }


    [Fact]
    public async Task RetrieveAsync_boosts_a_chunk_that_mentions_the_flagged_trap()
    {
        const string body =
            "Position sizing on the weekly book uses a fixed fraction of the budget and an invalidation level below support.";
        const string withTrap =
            body + " Check the SRO dilution timetable before adding to the name.";

        await SeedAsync("momentum-reader", "Momentum reader", mandatory: false, horizons: "WEEK", ("Sizing", body));
        await SeedAsync("trap-reader", "Trap reader", mandatory: false, horizons: "WEEK", ("Sizing", withTrap));

        var snippets = await _service.RetrieveAsync(
            new KnowledgeQuery(
                "position sizing invalidation support",
                SignalHorizon.WEEK,
                null,
                new[] { PseTrapFlag.SRO_DILUTION }));

        Assert.Equal("trap-reader", snippets[0].DocumentSlug);
        Assert.True(snippets[0].Score > snippets[1].Score);
    }

    [Fact]
    public async Task DeleteAsync_refuses_a_builtin_document_and_removes_an_upload()
    {
        await SeedAsync("pse-rules", "PSE Trading Rules", mandatory: true, horizons: "WEEK", ("Rules", new string('r', 80)));

        var upload = await _service.IngestAsync(
            new KnowledgeUploadRequest(
                "trading-book.md",
                "Trading book",
                new[] { "WEEK" },
                new[] { "RISK" },
                TextContent: $"## Position sizing\n\n{new string('s', 120)}"));

        var builtin = (await _service.ListAsync()).Single(item => item.Slug == "pse-rules");

        Assert.False(await _service.DeleteAsync(builtin.Id));
        Assert.True(await _service.DeleteAsync(upload.Id));
        Assert.DoesNotContain(await _service.ListAsync(), item => item.Id == upload.Id);
    }

    [Fact]
    public async Task IngestAsync_chunks_a_markdown_upload_and_scopes_it_to_the_requested_horizons()
    {
        var document = await _service.IngestAsync(
            new KnowledgeUploadRequest(
                "Trading Book.md",
                null,
                new[] { "day", "week" },
                new[] { "risk", "sizing" },
                TextContent: $"## Fee drag\n\n{new string('f', 120)}\n\n## Board lots\n\n{new string('b', 120)}"));

        Assert.Equal("upload", document.Source);
        Assert.Equal("Trading Book", document.Title);
        Assert.Equal(new[] { "DAY", "WEEK" }, document.Horizons);
        Assert.Equal(new[] { "RISK", "SIZING" }, document.Categories);
        Assert.False(document.IsMandatory);
        Assert.Equal(2, document.ChunkCount);
        Assert.Equal(0, document.EmbeddedChunkCount);
        Assert.StartsWith("upload-trading-book-", document.Slug);

        var stored = (await _service.ListAsync()).Single();
        Assert.Equal(document.Slug, stored.Slug);
        Assert.Equal(2, stored.ChunkCount);
    }

    [Fact]
    public async Task IngestAsync_rejects_an_upload_without_usable_text()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.IngestAsync(
                new KnowledgeUploadRequest(
                    "empty.md",
                    null,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    TextContent: "   ")));
    }

    [Fact]
    public async Task IndexBuiltInAsync_seeds_the_markdown_playbooks_and_skips_unchanged_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pse-kb-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "knowledge-base");
        Directory.CreateDirectory(folder);

        await File.WriteAllTextAsync(
            Path.Combine(folder, "pse_rules.md"),
            $"---\nslug: pse-rules\ntitle: PSE Trading Rules\nmandatory: true\nhorizons: DAY, WEEK\ncategories: RULES\n---\n\n## Static band\n\n{new string('c', 120)}\n");

        var service = CreateService(new FakeHostEnvironment(root));

        try
        {
            var first = await service.IndexBuiltInAsync();

            Assert.Equal(1, first.DocumentsIndexed);
            Assert.Equal(0, first.DocumentsSkipped);
            Assert.Equal(1, first.ChunksIndexed);
            Assert.Equal(0, first.ChunksEmbedded);

            var second = await service.IndexBuiltInAsync();

            Assert.Equal(0, second.DocumentsIndexed);
            Assert.Equal(1, second.DocumentsSkipped);
            Assert.Equal(0, second.ChunksIndexed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private SqliteKnowledgeService CreateService(IHostEnvironment environment) =>
        new(
            _repository,
            new OllamaEmbeddingClient(
                new HttpClient(),
                Options.Create(new OllamaCloudOptions { ApiKey = string.Empty, EmbeddingModel = string.Empty }),
                NullLogger<OllamaEmbeddingClient>.Instance),
            Options.Create(_options),
            environment,
            NullLogger<SqliteKnowledgeService>.Instance);

    private async Task SeedAsync(
        string slug,
        string title,
        bool mandatory,
        string horizons,
        params (string Section, string Content)[] sections)
    {
        var document = new KnowledgeDocument
        {
            Slug = slug,
            Title = title,
            Source = "builtin",
            SourcePath = $"{slug}.md",
            HorizonsCsv = horizons,
            CategoriesCsv = "TECHNICAL",
            IsMandatory = mandatory,
            ContentHash = $"hash-{slug}",
            ChunkCount = sections.Length,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Chunks = sections
                .Select((section, index) => new KnowledgeChunk
                {
                    Ordinal = index,
                    SectionTitle = section.Section,
                    Reference = $"{slug}#{KnowledgeText.Slugify(section.Section)}",
                    Content = section.Content,
                    HorizonsCsv = horizons,
                    CategoriesCsv = "TECHNICAL"
                })
                .ToList()
        };

        await _repository.SaveAsync(document, replaceChunks: true);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "PseSignalDesk.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
