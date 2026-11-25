using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// One session's read of the desk's book — account, open trades and the day's metric map —
/// captured <b>once</b> so every candidate on a signal run can be sized against its own sector,
/// ATR, pyramid state and cap room without re-reading storage per ticker.
/// <para>
/// A signal run used to build a single ticker-less context, which meant every ticket on the list
/// was sized as if it were the first position of an empty book: the per-sector name cap, the
/// ATR-aware slippage estimate and the pyramid rules never fired. <see cref="For"/> derives the
/// exact same <see cref="OrderTicketContext"/> the single-ticker endpoint sees, from this one read.
/// </para>
/// </summary>
public sealed class BookSizingSnapshot
{
    private readonly PortfolioAccount _account;
    private readonly decimal _equityPhp;
    private readonly decimal _openRiskPhp;
    private readonly decimal _drawdownPct;
    private readonly decimal? _regimeVolPct;
    private readonly IReadOnlyList<PortfolioTrade> _openTrades;
    private readonly IReadOnlyDictionary<string, StockDailyMetric> _metricsByTicker;

    internal BookSizingSnapshot(
        PortfolioAccount account,
        decimal equityPhp,
        decimal openRiskPhp,
        decimal drawdownPct,
        decimal? regimeVolPct,
        IReadOnlyList<PortfolioTrade> openTrades,
        IReadOnlyDictionary<string, StockDailyMetric> metricsByTicker)
    {
        _account = account;
        _equityPhp = equityPhp;
        _openRiskPhp = openRiskPhp;
        _drawdownPct = drawdownPct;
        _regimeVolPct = regimeVolPct;
        _openTrades = openTrades;
        _metricsByTicker = metricsByTicker;
    }

    /// <summary>Book equity the size is a fraction of.</summary>
    public decimal EquityPhp => decimal.Round(_equityPhp, 2, MidpointRounding.AwayFromZero);

    /// <summary>The metric captured for that ticker on the book's session, when the desk tracks it.</summary>
    public StockDailyMetric? MetricFor(string? ticker) =>
        _metricsByTicker.TryGetValue(Normalize(ticker), out var metric) ? metric : null;

    /// <summary>
    /// The sizing context for one candidate: its sector room, its own ATR (for the slippage
    /// estimate) and, when the name is already open, the pyramid state of that position.
    /// A null or unknown ticker yields the book-level context, which is what the run-wide
    /// fallback uses.
    /// </summary>
    public OrderTicketContext For(string? ticker)
    {
        var normalized = Normalize(ticker);
        _metricsByTicker.TryGetValue(normalized, out var targetMetric);
        var sector = targetMetric?.Sector;

        decimal? sectorExposurePct = null;
        var sectorPositionCount = 0;

        if (!string.IsNullOrWhiteSpace(sector))
        {
            var sectorTrades = _openTrades
                .Where(trade => _metricsByTicker.TryGetValue(trade.Ticker, out var tradeMetric) &&
                                string.Equals(tradeMetric.Sector, sector, StringComparison.OrdinalIgnoreCase))
                .ToList();

            sectorPositionCount = sectorTrades.Count;
            sectorExposurePct = decimal.Round(
                sectorTrades.Sum(trade => trade.BuyCostPhp) / _equityPhp * 100m,
                2,
                MidpointRounding.AwayFromZero);
        }

        var existingTrade = _openTrades
            .FirstOrDefault(trade => string.Equals(trade.Ticker, normalized, StringComparison.OrdinalIgnoreCase));

        // A same-ticker ticket is a pyramid candidate, so the context carries how far the open
        // position already is in R, whether its stop is at break-even, how many adds it has
        // taken and how much of the position cap it already occupies.
        decimal? existingUnrealizedR = null;
        var existingStopAtBreakEven = false;

        if (existingTrade is not null)
        {
            var markPrice = targetMetric?.ClosePrice is decimal close && close > 0m ? close : existingTrade.EntryPrice;
            var markProceedsPhp = PseBoardLotService.ComputeSellNetProceedsPhp(existingTrade.Shares, markPrice);

            existingUnrealizedR = existingTrade.InitialNetRiskPhp > 0m
                ? decimal.Round(
                    (markProceedsPhp - existingTrade.BuyCostPhp) / existingTrade.InitialNetRiskPhp,
                    4,
                    MidpointRounding.AwayFromZero)
                : 0m;

            existingStopAtBreakEven = existingTrade.CurrentStopLoss >=
                                      PseBoardLotService.ComputeBreakEvenStopPrice(existingTrade.EntryPrice);
        }

        return new OrderTicketContext(
            EquityPhp,
            _account.CashBalancePhp,
            _account.MaxRiskPerTradePct,
            _account.SmallSizeRiskMultiplier,
            _account.MaxPositionPct,
            decimal.Round(_openRiskPhp / _equityPhp * 100m, 2, MidpointRounding.AwayFromZero),
            _account.MaxPortfolioHeatPct,
            DrawdownPct: _drawdownPct,
            SoftDrawdownThresholdPct: _account.SoftDrawdownThresholdPct,
            HardDrawdownThresholdPct: _account.HardDrawdownThresholdPct,
            RegimeVolPct: _regimeVolPct,
            TargetAnnualVolPct: _account.TargetAnnualVolPct,
            SectorExposurePct: sectorExposurePct,
            MaxSectorExposurePct: _account.MaxSectorExposurePct,
            SectorPositionCount: sectorPositionCount,
            MaxPositionsPerSector: _account.MaxPositionsPerSector,
            Sector: sector,
            ExistingEntryPrice: existingTrade?.EntryPrice,
            AtrPct: targetMetric?.AtrPct,
            ExistingUnrealizedR: existingUnrealizedR,
            ExistingStopAtBreakEven: existingStopAtBreakEven,
            ExistingPyramidCount: existingTrade?.PyramidCount ?? 0,
            ExistingPositionCostPhp: existingTrade?.BuyCostPhp);
    }

    private static string Normalize(string? ticker) =>
        (ticker ?? string.Empty).Trim().ToUpperInvariant();
}
