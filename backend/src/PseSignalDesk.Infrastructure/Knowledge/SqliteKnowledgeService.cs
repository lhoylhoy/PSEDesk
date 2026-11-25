using System.Buffers.Binary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Infrastructure.Configuration;

namespace PseSignalDesk.Infrastructure.Knowledge;

/// <summary>
/// SQLite-backed RAG library for the AI Money Manager. Retrieval fuses BM25 lexical
/// ranking with cosine similarity over persisted embeddings through reciprocal-rank
/// fusion, then applies horizon, sector and structural-trap boosts. The mandatory
/// <c>pse_rules</c> guardrails are always injected, so a prompt can never rank a trade
/// without the exchange mechanics and trap taxonomy in front of the model.
/// </summary>
public sealed class SqliteKnowledgeService : IKnowledgeRetrievalService
{
    private const double ReciprocalRankConstant = 60d;
    private const int MandatorySnippetCount = 2;
    private const int MaxSnippetCap = 12;

    private readonly IKnowledgeRepository _repository;
    private readonly OllamaEmbeddingClient _embeddings;
    private readonly KnowledgeBaseOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SqliteKnowledgeService> _logger;

    public SqliteKnowledgeService(
        IKnowledgeRepository repository,
        OllamaEmbeddingClient embeddings,
        IOptions<KnowledgeBaseOptions> options,
        IHostEnvironment environment,
        ILogger<SqliteKnowledgeService> logger)
    {
        _repository = repository;
        _embeddings = embeddings;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public Task<IReadOnlyList<KnowledgeDocumentDto>> ListAsync(CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken);

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var document = await _repository.FindByIdAsync(id, cancellationToken);

        if (document is null)
        {
            return false;
        }

        if (document.Source == "builtin")
        {
            _logger.LogWarning("Refusing to delete the built-in playbook {Slug}.", document.Slug);
            return false;
        }

        return await _repository.DeleteAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeSnippet>> RetrieveAsync(
        KnowledgeQuery query,
        CancellationToken cancellationToken = default)
    {
        var chunks = await _repository.GetAllChunksAsync(cancellationToken);

        if (chunks.Count == 0)
        {
            _logger.LogWarning("The knowledge base is empty; the prompt will run without playbook grounding.");
            return Array.Empty<KnowledgeSnippet>();
        }

        var maxSnippets = Math.Clamp(
            query.MaxSnippets <= 0 ? _options.MaxSnippets : query.MaxSnippets,
            1,
            MaxSnippetCap);

        var queryTokens = KnowledgeText.Tokenize(
            $"{query.QueryText} {query.Horizon} {query.Sector}");

        var lexicalScores = ScoreLexical(chunks, queryTokens);
        var vectorScores = await ScoreVectorAsync(chunks, query, cancellationToken);
        var fused = Fuse(chunks, lexicalScores, vectorScores, query);

        var snippets = SelectSnippets(chunks, fused, maxSnippets);

        _logger.LogInformation(
            "Knowledge retrieval for {Horizon} ({Sector}) returned {Selected}/{Total} sections " +
            "(lexical tokens {Tokens}, vector {Vector}).",
            query.Horizon,
            query.Sector ?? "all",
            snippets.Count,
            chunks.Count,
            queryTokens.Count,
            vectorScores is null ? "off" : "on");

        return snippets;
    }
    /// <summary>
    /// Okapi BM25 over section title plus body. Deterministic, model-free and cheap enough
    /// to run on every request: the library holds a few hundred chunks, not a corpus.
    /// </summary>
    private static double[] ScoreLexical(IReadOnlyList<KnowledgeChunk> chunks, IReadOnlyList<string> queryTokens)
    {
        var scores = new double[chunks.Count];

        if (queryTokens.Count == 0)
        {
            return scores;
        }

        const double k1 = 1.2d;
        const double b = 0.75d;

        var documents = new string[chunks.Count][];
        var lengths = new int[chunks.Count];
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalLength = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            var tokens = KnowledgeText.Tokenize($"{chunks[i].SectionTitle} {chunks[i].Content}")
                .ToArray();

            documents[i] = tokens;
            lengths[i] = tokens.Length;
            totalLength += tokens.Length;

            foreach (var token in tokens.Distinct(StringComparer.Ordinal))
            {
                documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;
            }
        }

        var averageLength = chunks.Count == 0 ? 0d : (double)totalLength / chunks.Count;

        if (averageLength <= 0d)
        {
            return scores;
        }

        foreach (var term in queryTokens.Distinct(StringComparer.Ordinal))
        {
            var frequency = documentFrequency.GetValueOrDefault(term);

            if (frequency == 0)
            {
                continue;
            }

            var idf = Math.Log(1d + ((chunks.Count - frequency + 0.5d) / (frequency + 0.5d)));

            for (var i = 0; i < chunks.Count; i++)
            {
                var occurrences = documents[i].Count(token => string.Equals(token, term, StringComparison.Ordinal));

                if (occurrences == 0)
                {
                    continue;
                }

                var denominator = occurrences + k1 * (1d - b + b * lengths[i] / averageLength);
                scores[i] += idf * occurrences * (k1 + 1d) / denominator;
            }
        }

        return scores;
    }

    /// <summary>
    /// Cosine similarity between the query embedding and each persisted chunk vector.
    /// Returns <c>null</c> when embeddings are disabled, unconfigured or unavailable, which
    /// is the signal for the caller to fall back to lexical ranking only.
    /// </summary>
    private async Task<double[]?> ScoreVectorAsync(
        IReadOnlyList<KnowledgeChunk> chunks,
        KnowledgeQuery query,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableEmbeddings || !_embeddings.IsConfigured)
        {
            return null;
        }

        if (!chunks.Any(chunk => chunk.EmbeddingBlob is { Length: >= 4 }))
        {
            return null;
        }

        var texts = new[]
        {
            $"{query.Horizon} horizon. {query.QueryText} {query.Sector} " +
            string.Join(' ', query.TrapFlags ?? Array.Empty<Domain.Enums.PseTrapFlag>())
        };

        var embedded = await _embeddings.EmbedAsync(texts, cancellationToken);
        var queryVector = embedded is { Count: > 0 } ? embedded[0] : null;

        if (queryVector is null)
        {
            return null;
        }

        var queryNorm = Norm(queryVector);

        if (queryNorm <= 0d)
        {
            return null;
        }

        var scores = new double[chunks.Count];

        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].EmbeddingBlob is not { Length: >= 4 } blob)
            {
                continue;
            }

            var vector = DecodeVector(blob);

            if (vector.Length != queryVector.Length)
            {
                continue;
            }

            var norm = Norm(vector);

            if (norm <= 0d)
            {
                continue;
            }

            double dot = 0d;

            for (var dimension = 0; dimension < vector.Length; dimension++)
            {
                dot += vector[dimension] * queryVector[dimension];
            }

            scores[i] = Math.Clamp((dot / (norm * queryNorm) + 1d) / 2d, 0d, 1d);
        }

        return scores;
    }

    /// <summary>
    /// Reciprocal-rank fusion of the lexical and vector rankings plus metadata boosts.
    /// A chunk that ranks well on both signals outranks one that only wins on a keyword.
    /// </summary>
    private static double[] Fuse(
        IReadOnlyList<KnowledgeChunk> chunks,
        double[] lexical,
        double[]? vector,
        KnowledgeQuery query)
    {
        var fused = new double[chunks.Count];

        AddRankContributions(fused, lexical);

        if (vector is not null)
        {
            AddRankContributions(fused, vector);
        }

        var horizon = query.Horizon.ToString();
        var sectorTokens = KnowledgeText.Tokenize(query.Sector);
        var trapTokens = KnowledgeText.Tokenize(string.Join(' ', query.TrapFlags ?? Array.Empty<Domain.Enums.PseTrapFlag>()));

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];

            if (ContainsTag(chunk.HorizonsCsv, horizon))
            {
                fused[i] += 0.012d;
            }

            if (sectorTokens.Count > 0 &&
                sectorTokens.Any(token => chunk.Content.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                fused[i] += 0.010d;
            }

            if (trapTokens.Count > 0)
            {
                var matches = trapTokens.Count(token =>
                    chunk.Content.Contains(token, StringComparison.OrdinalIgnoreCase));

                fused[i] += Math.Min(matches, 3) * 0.008d;
            }

            if (chunk.Document?.IsMandatory == true)
            {
                fused[i] += 0.004d;
            }
        }

        return fused;
    }

    private static void AddRankContributions(double[] fused, double[] scores)
    {
        var ranked = Enumerable.Range(0, scores.Length)
            .Where(index => scores[index] > 0d)
            .OrderByDescending(index => scores[index])
            .ThenBy(index => index);

        var rank = 0;

        foreach (var index in ranked)
        {
            rank++;
            fused[index] += 1d / (ReciprocalRankConstant + rank);
        }
    }

    /// <summary>
    /// Builds the final snippet list: the strongest mandatory guardrail sections first,
    /// then the best remaining sections until the budget is full.
    /// </summary>
    private static IReadOnlyList<KnowledgeSnippet> SelectSnippets(
        IReadOnlyList<KnowledgeChunk> chunks,
        double[] fused,
        int maxSnippets)
    {
        var order = Enumerable.Range(0, chunks.Count)
            .OrderByDescending(index => fused[index])
            .ThenBy(index => index)
            .ToList();

        var selected = new List<int>(maxSnippets);

        foreach (var index in order)
        {
            if (chunks[index].Document?.IsMandatory != true)
            {
                continue;
            }

            selected.Add(index);

            if (selected.Count >= Math.Min(MandatorySnippetCount, maxSnippets))
            {
                break;
            }
        }

        foreach (var index in order)
        {
            if (selected.Count >= maxSnippets)
            {
                break;
            }

            if (!selected.Contains(index))
            {
                selected.Add(index);
            }
        }

        return selected
            .Select(index => new KnowledgeSnippet(
                chunks[index].Document?.Slug ?? "unknown",
                chunks[index].SectionTitle,
                chunks[index].Reference,
                chunks[index].Content,
                SplitTags(chunks[index].HorizonsCsv),
                SplitTags(chunks[index].CategoriesCsv),
                Math.Round(fused[index], 6)))
            .ToList();
    }

    private static bool ContainsTag(string csv, string value) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(tag => string.Equals(tag, value, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> SplitTags(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double Norm(float[] vector)
    {
        double sum = 0d;

        foreach (var value in vector)
        {
            sum += value * (double)value;
        }

        return Math.Sqrt(sum);
    }

    /// <summary>
    /// Reads the little-endian <c>float32</c> blob, the exact layout <c>sqlite-vec</c> uses,
    /// so the vectors stay portable to the SQLite extension later.
    /// </summary>
    private static float[] DecodeVector(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(i * sizeof(float), sizeof(float))));
        }

        return vector;
    }

    private static byte[] EncodeVector(float[] vector)
    {
        var blob = new byte[vector.Length * sizeof(float)];

        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                blob.AsSpan(i * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(vector[i]));
        }

        return blob;
    }

    /// <summary>
    /// Seeds or refreshes the built-in Markdown playbooks. A file whose SHA-256 matches the
    /// stored hash is skipped completely, so a restart costs no embedding calls. Playbooks
    /// removed from disk are deleted from the index.
    /// </summary>
    public async Task<KnowledgeIngestionResult> IndexBuiltInAsync(CancellationToken cancellationToken = default)
    {
        var root = ResolveKnowledgeRoot();

        if (root is null)
        {
            _logger.LogWarning(
                "No knowledge-base folder was found. Place the Markdown playbooks under {RootPath}.",
                _options.RootPath);

            return new KnowledgeIngestionResult(0, 0, 0, 0, 0, DescribeEmbeddingModel());
        }

        var files = Directory
            .EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();

        var activeSlugs = new List<string>(files.Count);
        var indexed = 0;
        var updated = 0;
        var skipped = 0;
        var chunksIndexed = 0;
        var chunksEmbedded = 0;

        foreach (var file in files)
        {
            try
            {
                var raw = await File.ReadAllTextAsync(file, cancellationToken);
                var parsed = KnowledgeDocumentParser.Parse(raw, Path.GetFileName(file), null, _options);
                activeSlugs.Add(parsed.Slug);

                var hash = KnowledgeDocumentParser.ContentHash(raw);
                var existing = await _repository.FindBySlugAsync(parsed.Slug, cancellationToken);

                if (existing is not null && existing.Source == "builtin" && existing.ContentHash == hash)
                {
                    skipped++;
                    continue;
                }

                var relativePath = Path.GetRelativePath(root, file);
                var document = BuildDocument(parsed, "builtin", relativePath, hash);

                chunksIndexed += document.Chunks.Count;
                chunksEmbedded += await EmbedChunksAsync(document.Chunks, cancellationToken);

                await _repository.SaveAsync(document, replaceChunks: true, cancellationToken);

                if (existing is null)
                {
                    indexed++;
                }
                else
                {
                    updated++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Playbook {File} could not be indexed and was skipped.", file);
            }
        }

        var removed = await _repository.DeleteBuiltInsNotInAsync(activeSlugs, cancellationToken);

        _logger.LogInformation(
            "Knowledge base synced from {Root}: {Indexed} new, {Updated} updated, {Skipped} unchanged, " +
            "{Chunks} chunks ({Embedded} embedded), {Removed} stale document(s) removed.",
            root,
            indexed,
            updated,
            skipped,
            chunksIndexed,
            chunksEmbedded,
            removed);

        return new KnowledgeIngestionResult(
            indexed,
            updated,
            skipped,
            chunksIndexed,
            chunksEmbedded,
            DescribeEmbeddingModel());
    }

    /// <summary>
    /// Ingests a user book (Markdown, plain text or PDF). Uploaded books are never
    /// mandatory: they inform ranking when they match, and they can always be deleted.
    /// </summary>
    public async Task<KnowledgeDocumentDto> IngestAsync(
        KnowledgeUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fileName = (request.FileName ?? string.Empty).Trim();

        if (fileName.Length == 0)
        {
            throw new ArgumentException("A file name is required for a knowledge-base upload.", nameof(request));
        }

        var text = ResolveUploadText(request, fileName);

        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 40)
        {
            throw new ArgumentException(
                "The upload contained no usable text. Scanned PDFs need OCR before they can be indexed.",
                nameof(request));
        }

        var hash = KnowledgeDocumentParser.ContentHash(text);
        var slug = $"upload-{KnowledgeText.NormalizeSlug(fileName)}-{hash[..8]}";
        var horizons = NormalizeTags(request.Horizons, new[] { "DAY", "WEEK", "MONTH", "YEAR" });
        var categories = NormalizeTags(request.Categories, new[] { "GENERAL" });
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? Path.GetFileNameWithoutExtension(fileName)
            : request.Title.Trim();

        var chunks = IsMarkdown(fileName)
            ? KnowledgeDocumentParser.Parse(text, slug, title, _options).Chunks
            : KnowledgeDocumentParser.ChunkPlainText(text, slug, _options.MaxChunkCharacters);

        if (chunks.Count == 0)
        {
            throw new ArgumentException("The upload produced no indexable sections.", nameof(request));
        }

        var now = DateTime.UtcNow;
        var document = new KnowledgeDocument
        {
            Slug = slug,
            Title = title,
            Source = "upload",
            SourcePath = fileName,
            HorizonsCsv = string.Join(',', horizons),
            CategoriesCsv = string.Join(',', categories),
            IsMandatory = false,
            ContentHash = hash,
            ChunkCount = chunks.Count,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Chunks = chunks.Select(chunk => new KnowledgeChunk
            {
                Ordinal = chunk.Ordinal,
                SectionTitle = chunk.SectionTitle,
                Reference = chunk.Reference,
                Content = chunk.Content,
                HorizonsCsv = string.Join(',', horizons),
                CategoriesCsv = string.Join(',', categories)
            }).ToList()
        };

        var embedded = await EmbedChunksAsync(document.Chunks, cancellationToken);
        await _repository.SaveAsync(document, replaceChunks: true, cancellationToken);

        var saved = await _repository.FindBySlugAsync(slug, cancellationToken);

        _logger.LogInformation(
            "Indexed upload {File} as {Slug}: {Chunks} chunk(s), {Embedded} embedded.",
            fileName,
            slug,
            chunks.Count,
            embedded);

        return new KnowledgeDocumentDto(
            saved?.Id ?? 0,
            slug,
            title,
            "upload",
            fileName,
            horizons,
            categories,
            false,
            chunks.Count,
            embedded,
            hash,
            now);
    }

    private static KnowledgeDocument BuildDocument(
        ParsedKnowledgeDocument parsed,
        string source,
        string sourcePath,
        string hash)
    {
        var now = DateTime.UtcNow;
        var horizonsCsv = string.Join(',', parsed.Horizons);
        var categoriesCsv = string.Join(',', parsed.Categories);

        return new KnowledgeDocument
        {
            Slug = parsed.Slug,
            Title = parsed.Title,
            Source = source,
            SourcePath = sourcePath,
            HorizonsCsv = horizonsCsv,
            CategoriesCsv = categoriesCsv,
            IsMandatory = parsed.IsMandatory,
            ContentHash = hash,
            ChunkCount = parsed.Chunks.Count,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Chunks = parsed.Chunks.Select(chunk => new KnowledgeChunk
            {
                Ordinal = chunk.Ordinal,
                SectionTitle = chunk.SectionTitle,
                Reference = chunk.Reference,
                Content = chunk.Content,
                HorizonsCsv = horizonsCsv,
                CategoriesCsv = categoriesCsv
            }).ToList()
        };
    }

    /// <summary>
    /// Embeds the chunks in one batched call and stores the vectors. Returns how many
    /// chunks ended up with a vector; zero simply means lexical-only retrieval.
    /// </summary>
    private async Task<int> EmbedChunksAsync(
        IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableEmbeddings || !_embeddings.IsConfigured || chunks.Count == 0)
        {
            return 0;
        }

        var texts = chunks
            .Select(chunk => $"{chunk.SectionTitle}\n{chunk.Content}")
            .ToList();

        var vectors = await _embeddings.EmbedAsync(texts, cancellationToken);

        if (vectors is null)
        {
            return 0;
        }

        var embedded = 0;

        for (var i = 0; i < chunks.Count && i < vectors.Count; i++)
        {
            if (vectors[i] is not { Length: > 0 } vector)
            {
                continue;
            }

            chunks[i].EmbeddingBlob = EncodeVector(vector);
            chunks[i].EmbeddingDimensions = vector.Length;
            embedded++;
        }

        return embedded;
    }

    /// <summary>
    /// Looks for the playbook folder next to the content root (development) and next to the
    /// published binaries (production copy of the <c>knowledge-base/</c> folder).
    /// </summary>
    private string? ResolveKnowledgeRoot()
    {
        var configured = _options.RootPath;

        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.IsPathRooted(configured) ? configured : Path.Combine(_environment.ContentRootPath, configured),
            Path.Combine(AppContext.BaseDirectory, configured)
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string ResolveUploadText(KnowledgeUploadRequest request, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(request.TextContent))
        {
            return request.TextContent;
        }

        if (request.BinaryContent is not { Length: > 0 } binary)
        {
            return string.Empty;
        }

        if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return PdfTextExtractor.Extract(binary);
        }

        return System.Text.Encoding.UTF8.GetString(binary);
    }

    private static bool IsMarkdown(string fileName) =>
        fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> NormalizeTags(
        IReadOnlyList<string>? values,
        IReadOnlyList<string> fallback)
    {
        if (values is null || values.Count == 0)
        {
            return fallback;
        }

        var tags = values
            .Select(value => (value ?? string.Empty).Trim().ToUpperInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tags.Count == 0 ? fallback : tags;
    }

    private string DescribeEmbeddingModel() =>
        _options.EnableEmbeddings && _embeddings.IsConfigured ? _embeddings.Model : "none";
}

