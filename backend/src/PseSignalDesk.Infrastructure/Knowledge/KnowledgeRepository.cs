using Microsoft.EntityFrameworkCore;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;

namespace PseSignalDesk.Infrastructure.Knowledge;

/// <summary>
/// EF Core persistence for the RAG library. Documents and their chunks are written in one
/// transaction so a failed embedding pass can never leave a document with a partial index.
/// </summary>
public sealed class KnowledgeRepository : IKnowledgeRepository
{
    private readonly Persistence.PseSignalDbContext _db;

    public KnowledgeRepository(Persistence.PseSignalDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<KnowledgeDocumentDto>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await _db.KnowledgeDocuments
            .AsNoTracking()
            .OrderByDescending(d => d.IsMandatory)
            .ThenBy(d => d.Source)
            .ThenBy(d => d.Slug)
            .Select(d => new KnowledgeDocumentDto(
                d.Id,
                d.Slug,
                d.Title,
                d.Source,
                d.SourcePath,
                d.HorizonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries),
                d.CategoriesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries),
                d.IsMandatory,
                d.Chunks.Count,
                d.Chunks.Count(c => c.EmbeddingBlob != null),
                d.ContentHash,
                d.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

    public Task<KnowledgeDocument?> FindBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default) =>
        _db.KnowledgeDocuments
            .Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.Slug == slug, cancellationToken);

    public Task<KnowledgeDocument?> FindByIdAsync(
        int id,
        CancellationToken cancellationToken = default) =>
        _db.KnowledgeDocuments
            .Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task SaveAsync(
        KnowledgeDocument document,
        bool replaceChunks,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await _db.KnowledgeDocuments
            .Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.Slug == document.Slug, cancellationToken);

        if (existing is null)
        {
            _db.KnowledgeDocuments.Add(document);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return;
        }

        existing.Title = document.Title;
        existing.Source = document.Source;
        existing.SourcePath = document.SourcePath;
        existing.HorizonsCsv = document.HorizonsCsv;
        existing.CategoriesCsv = document.CategoriesCsv;
        existing.IsMandatory = document.IsMandatory;
        existing.ContentHash = document.ContentHash;
        existing.ChunkCount = document.ChunkCount;
        existing.UpdatedAtUtc = document.UpdatedAtUtc;

        if (replaceChunks)
        {
            _db.KnowledgeChunks.RemoveRange(existing.Chunks);

            foreach (var chunk in document.Chunks)
            {
                chunk.Id = 0;
                chunk.KnowledgeDocumentId = existing.Id;
                chunk.Document = null;
                existing.Chunks.Add(chunk);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _db.ChangeTracker.Clear();
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var document = await _db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (document is null)
        {
            return false;
        }

        _db.KnowledgeDocuments.Remove(document);
        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();
        return true;
    }

    public async Task<IReadOnlyList<KnowledgeChunk>> GetAllChunksAsync(
        CancellationToken cancellationToken = default) =>
        await _db.KnowledgeChunks
            .AsNoTracking()
            .Include(c => c.Document)
            .OrderBy(c => c.KnowledgeDocumentId)
            .ThenBy(c => c.Ordinal)
            .ToListAsync(cancellationToken);

    public async Task<int> DeleteBuiltInsNotInAsync(
        IReadOnlyCollection<string> activeSlugs,
        CancellationToken cancellationToken = default)
    {
        var stale = await _db.KnowledgeDocuments
            .Where(d => d.Source == "builtin" && !activeSlugs.Contains(d.Slug))
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        _db.KnowledgeDocuments.RemoveRange(stale);
        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();
        return stale.Count;
    }
}
