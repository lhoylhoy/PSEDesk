using PseSignalDesk.Domain.Entities;

namespace PseSignalDesk.Application.Interfaces;

/// <summary>
/// Persistence boundary for the desk's own book: one capital account plus the open and
/// closed positions it has taken. Kept separate from the signal repository so the blotter
/// can evolve without touching the research ledger.
/// </summary>
public interface IPortfolioRepository
{
    /// <summary>
    /// Loads the single desk account, creating a default <c>PHP 100,000</c> row when the
    /// book is empty so the blotter never has to guess a bankroll.
    /// </summary>
    Task<PortfolioAccount> GetOrCreateAccountAsync(CancellationToken cancellationToken = default);

    Task<PortfolioAccount> UpdateAccountAsync(PortfolioAccount account, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PortfolioTrade>> GetOpenTradesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PortfolioTrade>> GetClosedTradesAsync(
        int take = 100,
        CancellationToken cancellationToken = default);

    Task<PortfolioTrade?> GetTradeAsync(int id, CancellationToken cancellationToken = default);

    Task<PortfolioTrade> AddTradeAsync(PortfolioTrade trade, CancellationToken cancellationToken = default);

    Task<PortfolioTrade> UpdateTradeAsync(PortfolioTrade trade, CancellationToken cancellationToken = default);
}
