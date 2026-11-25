using Microsoft.EntityFrameworkCore;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Infrastructure.Persistence;

/// <summary>
/// EF Core store for the desk's own book: the single capital account plus every open and
/// closed position. Kept apart from <see cref="SignalRepository"/> so the blotter can change
/// without touching the research ledger.
/// </summary>
public sealed class PortfolioRepository : IPortfolioRepository
{
    private readonly PseSignalDbContext _db;

    public PortfolioRepository(PseSignalDbContext db)
    {
        _db = db;
    }

    public async Task<PortfolioAccount> GetOrCreateAccountAsync(CancellationToken cancellationToken = default)
    {
        var account = await _db.PortfolioAccounts
            .OrderBy(entity => entity.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (account is not null)
        {
            return account;
        }

        account = new PortfolioAccount();
        _db.PortfolioAccounts.Add(account);
        await _db.SaveChangesAsync(cancellationToken);

        return account;
    }

    public async Task<PortfolioAccount> UpdateAccountAsync(
        PortfolioAccount account,
        CancellationToken cancellationToken = default)
    {
        _db.PortfolioAccounts.Update(account);
        await _db.SaveChangesAsync(cancellationToken);

        return account;
    }

    public async Task<IReadOnlyList<PortfolioTrade>> GetOpenTradesAsync(CancellationToken cancellationToken = default) =>
        await _db.PortfolioTrades
            .Where(trade => trade.Status == PortfolioTradeStatus.OPEN)
            .OrderBy(trade => trade.OpenedDate)
            .ThenBy(trade => trade.Ticker)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PortfolioTrade>> GetClosedTradesAsync(
        int take = 100,
        CancellationToken cancellationToken = default) =>
        await _db.PortfolioTrades
            .AsNoTracking()
            .Where(trade => trade.Status == PortfolioTradeStatus.CLOSED)
            .OrderByDescending(trade => trade.ClosedDate)
            .ThenByDescending(trade => trade.Id)
            .Take(Math.Max(1, take))
            .ToListAsync(cancellationToken);

    public Task<PortfolioTrade?> GetTradeAsync(int id, CancellationToken cancellationToken = default) =>
        _db.PortfolioTrades.FirstOrDefaultAsync(trade => trade.Id == id, cancellationToken);

    public async Task<PortfolioTrade> AddTradeAsync(
        PortfolioTrade trade,
        CancellationToken cancellationToken = default)
    {
        _db.PortfolioTrades.Add(trade);
        await _db.SaveChangesAsync(cancellationToken);

        return trade;
    }

    public async Task<PortfolioTrade> UpdateTradeAsync(
        PortfolioTrade trade,
        CancellationToken cancellationToken = default)
    {
        _db.PortfolioTrades.Update(trade);
        await _db.SaveChangesAsync(cancellationToken);

        return trade;
    }
}
