using System.Globalization;
using Microsoft.Extensions.Logging;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// The desk's own book. It owns every rule the blotter must obey — risk-based board-lot
/// sizing, the fee-paid buy cost, the single-position cap, portfolio heat, and the exit
/// signals an open position has triggered — so the browser never becomes the source of a
/// peso figure or a stop instruction.
/// </summary>
public sealed class PortfolioService
{
    private const int ClosedTradePageSize = 100;
    private const int MarkLookbackSessions = 20;

    private readonly IPortfolioRepository _portfolio;
    private readonly ISignalRepository _signals;
    private readonly SignalOutcomeService? _outcomes;
    private readonly ILogger<PortfolioService>? _logger;

    public PortfolioService(
        IPortfolioRepository portfolio,
        ISignalRepository signals,
        ILogger<PortfolioService>? logger = null,
        SignalOutcomeService? outcomes = null)
    {
        _portfolio = portfolio;
        _signals = signals;
        _logger = logger;
        _outcomes = outcomes;
    }

    /// <summary>
    /// Capital context for sizing one order from the current book: equity, free cash, heat
    /// room and the pod-shop dials (drawdown, sector room, the name's own ATR and any open
    /// entry in it). Null when the account has no equity left, because no size is defensible
    /// then.
    /// </summary>
    public async Task<OrderTicketContext?> GetOrderTicketContextAsync(
        string? ticker = null,
        CancellationToken cancellationToken = default,
        SignalHorizon? horizon = null) =>
        (await GetBookSizingSnapshotAsync(cancellationToken, horizon))?.For(ticker);

    /// <summary>
    /// Reads the book <b>once</b> and returns a snapshot that can size every candidate on a run
    /// against its own sector, ATR, pyramid state and cap room. Before this existed a signal run
    /// built a single ticker-less context, so every ticket on the list was sized as if it were
    /// the first position in an empty book: no sector cap, no ATR-aware slippage, and no
    /// pyramid rules. Null when the account has no equity.
    /// </summary>
    public async Task<BookSizingSnapshot?> GetBookSizingSnapshotAsync(
        CancellationToken cancellationToken = default,
        SignalHorizon? horizon = null)
    {
        var (account, equityPhp, openRiskPhp) = await LoadCapitalAsync(cancellationToken);
        if (equityPhp <= 0m)
        {
            return null;
        }

        var (_, drawdownPct) = await UpdatePeakAndDrawdownAsync(account, equityPhp, cancellationToken);
        var open = await _portfolio.GetOpenTradesAsync(cancellationToken);
        var snapshot = await _signals.GetLatestSnapshotAsync(cancellationToken);
        var metrics = snapshot is null
            ? Array.Empty<StockDailyMetric>()
            : await _signals.GetMetricsAsync(snapshot.TradeDate, cancellationToken);
        var metricsByTicker = metrics.ToDictionary(metric => metric.Ticker, StringComparer.OrdinalIgnoreCase);

        // The live-drift throttle belongs to a horizon's book, so it is only evaluated when the
        // caller names one. A ledger read must never break sizing: a failure logs and leaves the
        // ticket unthrottled rather than handing the terminal a 500 on a cash page.
        LiveDriftSnapshot? liveDrift = null;
        if (horizon is SignalHorizon evaluatedHorizon && _outcomes is not null)
        {
            try
            {
                liveDrift = await _outcomes.EvaluateLiveDriftAsync(evaluatedHorizon, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Live drift for {Horizon} could not be evaluated; this book is sized unthrottled.",
                    evaluatedHorizon);
            }
        }

        return new BookSizingSnapshot(
            account,
            equityPhp,
            openRiskPhp,
            drawdownPct,
            snapshot?.PseiRealizedVol20dPct,
            open,
            metricsByTicker,
            liveDrift);
    }

    /// <summary>
    /// Order ticket for one candidate. Levels are normalized onto the PSE tick grid first,
    /// so the ticket always describes an order the exchange would accept.
    /// </summary>
    /// <param name="edge">
    /// The edge assessment this candidate was priced on, when the caller already has it. Passing
    /// it lets the governor size off the real quarter-Kelly stake instead of a flat risk unit;
    /// null keeps the pre-edge sizing exactly.
    /// </param>
    public async Task<OrderTicketDto> GetOrderTicketAsync(
        string ticker,
        decimal entryPrice,
        decimal stopLoss,
        decimal targetPrice,
        EdgeVerdict verdict,
        CancellationToken cancellationToken = default,
        EdgeAssessment? edge = null,
        SignalHorizon? horizon = null)
    {
        var context = await GetOrderTicketContextAsync(ticker, cancellationToken, horizon) ??
                      throw new PortfolioRuleViolationException(
                          "The desk account has no equity. Set the starting capital before sizing an order.");

        var plan = PseBoardLotService.NormalizeTradePlan(entryPrice, stopLoss, targetPrice);
        var turnoverPhp = await ResolveTurnoverAsync(ticker, cancellationToken);

        return OrderTicketCalculator.Build(context, plan.Entry, plan.StopLoss, plan.Target, verdict, turnoverPhp, edge);
    }

    /// <summary>
    /// Applies a partial settings update. Percentages are clamped to defensible ranges and
    /// a starting-capital change with no trades on the book also resets the cash balance,
    /// so a fresh bankroll does not leave the ledger inconsistent.
    /// </summary>
    public async Task<PortfolioAccountDto> UpdateSettingsAsync(
        PortfolioSettingsUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var account = await _portfolio.GetOrCreateAccountAsync(cancellationToken);
        var trades = await _portfolio.GetOpenTradesAsync(cancellationToken);
        var closed = await _portfolio.GetClosedTradesAsync(1, cancellationToken);

        if (request.StartingCapitalPhp is decimal capital)
        {
            if (capital <= 0m)
            {
                throw new PortfolioRuleViolationException("Starting capital must be greater than zero.");
            }

            account.StartingCapitalPhp = decimal.Round(capital, 2, MidpointRounding.AwayFromZero);

            if (trades.Count == 0 && closed.Count == 0)
            {
                account.CashBalancePhp = account.StartingCapitalPhp;
            }
        }

        if (request.CashBalancePhp is decimal cash)
        {
            if (cash < 0m)
            {
                throw new PortfolioRuleViolationException("Cash balance cannot be negative.");
            }

            account.CashBalancePhp = decimal.Round(cash, 2, MidpointRounding.AwayFromZero);
        }

        if (request.MaxRiskPerTradePct is decimal riskPct)
        {
            account.MaxRiskPerTradePct = decimal.Round(Math.Clamp(riskPct, 0.1m, 10m), 2, MidpointRounding.AwayFromZero);
        }

        if (request.SmallSizeRiskMultiplier is decimal multiplier)
        {
            account.SmallSizeRiskMultiplier = decimal.Round(Math.Clamp(multiplier, 0.1m, 1m), 2, MidpointRounding.AwayFromZero);
        }

        if (request.MaxPositionPct is decimal positionPct)
        {
            account.MaxPositionPct = decimal.Round(Math.Clamp(positionPct, 1m, 100m), 2, MidpointRounding.AwayFromZero);
        }

        if (request.MaxPortfolioHeatPct is decimal heatPct)
        {
            account.MaxPortfolioHeatPct = decimal.Round(Math.Clamp(heatPct, 1m, 50m), 2, MidpointRounding.AwayFromZero);
        }

        if (request.MaxSectorExposurePct is decimal sectorPct)
        {
            account.MaxSectorExposurePct = decimal.Round(Math.Clamp(sectorPct, 5m, 100m), 2, MidpointRounding.AwayFromZero);
        }

        if (request.MaxPositionsPerSector is int positionsPerSector)
        {
            account.MaxPositionsPerSector = Math.Clamp(positionsPerSector, 1, 10);
        }

        if (request.SoftDrawdownThresholdPct is decimal softPct)
        {
            account.SoftDrawdownThresholdPct = decimal.Round(Math.Clamp(softPct, 1m, 50m), 2, MidpointRounding.AwayFromZero);
        }

        if (request.HardDrawdownThresholdPct is decimal hardPct)
        {
            account.HardDrawdownThresholdPct = decimal.Round(Math.Clamp(hardPct, 2m, 60m), 2, MidpointRounding.AwayFromZero);
        }

        if (account.HardDrawdownThresholdPct <= account.SoftDrawdownThresholdPct)
        {
            throw new PortfolioRuleViolationException(
                "The hard drawdown limit must be strictly beyond the soft limit: the soft one scales risk, the hard one halts it.");
        }

        if (request.TargetAnnualVolPct is decimal targetVol)
        {
            account.TargetAnnualVolPct = decimal.Round(Math.Clamp(targetVol, 5m, 60m), 2, MidpointRounding.AwayFromZero);
        }

        account.UpdatedAtUtc = DateTime.UtcNow;
        var saved = await _portfolio.UpdateAccountAsync(account, cancellationToken);

        _logger?.LogInformation(
            "Portfolio settings updated: capital {Capital}, cash {Cash}, risk {Risk}%, heat cap {Heat}%.",
            saved.StartingCapitalPhp,
            saved.CashBalancePhp,
            saved.MaxRiskPerTradePct,
            saved.MaxPortfolioHeatPct);

        return ToAccountDto(saved);
    }

    /// <summary>
    /// Logs a new position. Prices are snapped to the tick grid, the order is rejected
    /// unless the share count is a whole board lot, the all-in buy cost fits the free cash,
    /// the position stays inside the single-position cap and the book stays under its heat
    /// limit. The buy cost is then debited from cash.
    /// </summary>
    public async Task<OpenPositionDto> OpenTradeAsync(
        OpenTradeRequest request,
        CancellationToken cancellationToken = default)
    {
        var ticker = (request.Ticker ?? string.Empty).Trim().ToUpperInvariant();
        if (ticker.Length == 0)
        {
            throw new PortfolioRuleViolationException("Provide a PSE ticker, for example ICT or BDO.");
        }

        if (request.Shares <= 0)
        {
            throw new PortfolioRuleViolationException("Share count must be greater than zero.");
        }

        var plan = PseBoardLotService.NormalizeTradePlan(request.EntryPrice, request.StopLoss, request.TargetPrice);
        var boardLot = PseBoardLotService.GetBoardLotSize(plan.Entry);

        if (boardLot <= 0 || request.Shares % boardLot != 0)
        {
            throw new PortfolioRuleViolationException(
                $"{ticker} trades in {boardLot}-share board lots at PHP {Format(plan.Entry)}; " +
                $"{request.Shares} is not a whole multiple.");
        }

        var buyCostPhp = PseBoardLotService.ComputeBuyTotalCostPhp(request.Shares, plan.Entry);
        var netRiskPhp = PseBoardLotService.ComputeInitialNetRiskPhp(request.Shares, plan.Entry, plan.StopLoss);

        if (netRiskPhp <= 0m)
        {
            throw new PortfolioRuleViolationException(
                "The stop is not below the entry, so the position carries no measurable risk.");
        }

        var (account, equityPhp, openRiskPhp) = await LoadCapitalAsync(cancellationToken);

        // A second ticket on a ticker that is already open is either an illegal average-down or
        // a legal pyramid add, and both are decided here rather than in the browser.
        var existing = (await _portfolio.GetOpenTradesAsync(cancellationToken))
            .FirstOrDefault(trade => string.Equals(trade.Ticker, ticker, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return await PyramidIntoOpenTradeAsync(
                existing,
                request,
                plan,
                buyCostPhp,
                account,
                equityPhp,
                openRiskPhp,
                cancellationToken);
        }

        if (buyCostPhp > account.CashBalancePhp)
        {
            throw new PortfolioRuleViolationException(
                $"All-in buy cost of PHP {Format(buyCostPhp)} exceeds the PHP {Format(account.CashBalancePhp)} cash balance.");
        }

        var positionPct = equityPhp > 0m ? buyCostPhp / equityPhp * 100m : 100m;
        if (positionPct > account.MaxPositionPct)
        {
            throw new PortfolioRuleViolationException(
                $"This position would be {Format(positionPct)}% of equity, above the " +
                $"{Format(account.MaxPositionPct)}% single-position cap.");
        }

        var heatPct = equityPhp > 0m ? (openRiskPhp + netRiskPhp) / equityPhp * 100m : 100m;
        if (heatPct > account.MaxPortfolioHeatPct)
        {
            throw new PortfolioRuleViolationException(
                $"This trade lifts portfolio heat to {Format(heatPct)}%, above the " +
                $"{Format(account.MaxPortfolioHeatPct)}% cap.");
        }

        var trade = new PortfolioTrade
        {
            Ticker = ticker,
            CompanyName = string.IsNullOrWhiteSpace(request.CompanyName) ? ticker : request.CompanyName.Trim(),
            Sector = string.IsNullOrWhiteSpace(request.Sector) ? "PSE" : request.Sector.Trim(),
            Horizon = request.Horizon,
            EdgeVerdict = request.EdgeVerdict,
            SignalItemId = request.SignalItemId,
            Status = PortfolioTradeStatus.OPEN,
            OpenedDate = request.OpenedDate ?? await ResolveLatestSessionDateAsync(cancellationToken),
            Shares = request.Shares,
            EntryPrice = plan.Entry,
            InitialStopLoss = plan.StopLoss,
            CurrentStopLoss = plan.StopLoss,
            TargetPrice = plan.Target,
            BuyCostPhp = buyCostPhp,
            InitialNetRiskPhp = netRiskPhp,
            Notes = (request.Notes ?? string.Empty).Trim()
        };

        account.CashBalancePhp = decimal.Round(account.CashBalancePhp - buyCostPhp, 2, MidpointRounding.AwayFromZero);
        account.UpdatedAtUtc = DateTime.UtcNow;

        var saved = await _portfolio.AddTradeAsync(trade, cancellationToken);
        await _portfolio.UpdateAccountAsync(account, cancellationToken);

        _logger?.LogInformation(
            "Opened {Shares} {Ticker} at PHP {Entry} (stop {Stop}, target {Target}); {RiskPhp} PHP at risk.",
            saved.Shares,
            saved.Ticker,
            saved.EntryPrice,
            saved.CurrentStopLoss,
            saved.TargetPrice,
            saved.InitialNetRiskPhp);

        return BuildPosition(saved, FallbackMark(saved), 0);
    }

    /// <summary>
    /// Legal pyramiding only. A second ticket on a ticker that is already open must enter above
    /// the live entry, the live stop must already sit at break-even, the position must be at
    /// least +1R in profit, and it may only be added to twice — each clip smaller than the last.
    /// The merged position keeps the first plan's stop and target, averages the entry up on the
    /// actual fee-paid costs and re-measures its open risk at the live stop.
    /// </summary>
    private async Task<OpenPositionDto> PyramidIntoOpenTradeAsync(
        PortfolioTrade existing,
        OpenTradeRequest request,
        (decimal Entry, decimal StopLoss, decimal Target) plan,
        decimal buyCostPhp,
        PortfolioAccount account,
        decimal equityPhp,
        decimal openRiskPhp,
        CancellationToken cancellationToken)
    {
        var mark = (await LoadMarkToMarketAsync(new[] { existing }, cancellationToken))
            .TryGetValue(existing.Ticker, out var foundMark)
            ? foundMark
            : FallbackMark(existing);

        var unrealizedR = existing.InitialNetRiskPhp > 0m
            ? decimal.Round(
                (PseBoardLotService.ComputeSellNetProceedsPhp(existing.Shares, mark.Close) - existing.BuyCostPhp) /
                existing.InitialNetRiskPhp,
                4,
                MidpointRounding.AwayFromZero)
            : 0m;

        var breakEvenStop = PseBoardLotService.ComputeBreakEvenStopPrice(existing.EntryPrice);

        if (plan.Entry <= existing.EntryPrice)
        {
            throw new PortfolioRuleViolationException(
                $"No averaging down or flat re-entry: {existing.Ticker} is already open at PHP " +
                $"{Format(existing.EntryPrice)} and this entry is PHP {Format(plan.Entry)}.");
        }

        if (existing.CurrentStopLoss < breakEvenStop)
        {
            throw new PortfolioRuleViolationException(
                $"Pyramiding requires the {existing.Ticker} stop already raised to the break-even tick of PHP " +
                $"{Format(breakEvenStop)}; it is at PHP {Format(existing.CurrentStopLoss)}.");
        }

        if (unrealizedR < OrderTicketCalculator.PyramidMinUnrealizedR)
        {
            throw new PortfolioRuleViolationException(
                $"Pyramiding requires at least +{Format(OrderTicketCalculator.PyramidMinUnrealizedR)}R unrealized " +
                $"profit; {existing.Ticker} is at {Format(unrealizedR)}R.");
        }

        if (existing.PyramidCount >= OrderTicketCalculator.MaxPyramidAdds)
        {
            throw new PortfolioRuleViolationException(
                $"{existing.Ticker} already carries the maximum of {OrderTicketCalculator.MaxPyramidAdds} pyramid " +
                "adds: bank part of the winner instead of adding a third clip.");
        }

        if (plan.Entry >= existing.TargetPrice)
        {
            throw new PortfolioRuleViolationException(
                $"The add would enter at PHP {Format(plan.Entry)}, at or above the position's target of PHP " +
                $"{Format(existing.TargetPrice)}: bank the winner instead.");
        }

        if (buyCostPhp > account.CashBalancePhp)
        {
            throw new PortfolioRuleViolationException(
                $"All-in buy cost of PHP {Format(buyCostPhp)} exceeds the PHP {Format(account.CashBalancePhp)} cash balance.");
        }

        var combinedCostPhp = decimal.Round(existing.BuyCostPhp + buyCostPhp, 2, MidpointRounding.AwayFromZero);
        var combinedPct = equityPhp > 0m ? combinedCostPhp / equityPhp * 100m : 100m;

        if (combinedPct > account.MaxPositionPct)
        {
            throw new PortfolioRuleViolationException(
                $"The add would take {existing.Ticker} to {Format(combinedPct)}% of equity, above the " +
                $"{Format(account.MaxPositionPct)}% single-position cap.");
        }

        // The new clip is only protected down to the position's live stop, so that is where its
        // risk is measured; the existing position's risk is already inside openRiskPhp.
        var clipRiskPhp = Math.Max(
            0m,
            buyCostPhp - PseBoardLotService.ComputeSellNetProceedsPhp(request.Shares, existing.CurrentStopLoss));
        var heatPct = equityPhp > 0m ? (openRiskPhp + clipRiskPhp) / equityPhp * 100m : 100m;

        if (heatPct > account.MaxPortfolioHeatPct)
        {
            throw new PortfolioRuleViolationException(
                $"The add lifts portfolio heat to {Format(heatPct)}%, above the " +
                $"{Format(account.MaxPortfolioHeatPct)}% cap.");
        }

        var mergedShares = existing.Shares + request.Shares;
        var weightedEntry = PseBoardLotService.RoundToValidTick(
            ((existing.Shares * existing.EntryPrice) + (request.Shares * plan.Entry)) / mergedShares);

        existing.EntryPrice = weightedEntry;
        existing.Shares = mergedShares;
        existing.BuyCostPhp = combinedCostPhp;
        existing.InitialNetRiskPhp = Math.Max(
            0m,
            decimal.Round(
                combinedCostPhp - PseBoardLotService.ComputeSellNetProceedsPhp(mergedShares, existing.CurrentStopLoss),
                2,
                MidpointRounding.AwayFromZero));
        existing.PyramidCount += 1;

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            var note = request.Notes.Trim();
            existing.Notes = string.IsNullOrWhiteSpace(existing.Notes) ? note : $"{existing.Notes}; {note}";
        }

        account.CashBalancePhp = decimal.Round(account.CashBalancePhp - buyCostPhp, 2, MidpointRounding.AwayFromZero);
        account.UpdatedAtUtc = DateTime.UtcNow;

        var saved = await _portfolio.UpdateTradeAsync(existing, cancellationToken);
        await _portfolio.UpdateAccountAsync(account, cancellationToken);

        _logger?.LogInformation(
            "Pyramid add {Add} on {Ticker}: {Shares} shares at PHP {Entry}; the position is now {Total} shares " +
            "at PHP {Average} with {R}R unrealized.",
            saved.PyramidCount,
            saved.Ticker,
            request.Shares,
            plan.Entry,
            saved.Shares,
            saved.EntryPrice,
            unrealizedR);

        return BuildPosition(saved, mark, 0);
    }

    /// <summary>Moves the live stop of an open position, e.g. to the fee-paid break-even tick.</summary>
    public async Task<OpenPositionDto> UpdateStopAsync(
        int id,
        UpdateStopRequest request,
        CancellationToken cancellationToken = default)
    {
        var trade = await _portfolio.GetTradeAsync(id, cancellationToken) ??
                    throw new PortfolioRuleViolationException($"Position {id} does not exist.");

        if (trade.Status != PortfolioTradeStatus.OPEN)
        {
            throw new PortfolioRuleViolationException($"Position {id} is closed; its stop can no longer move.");
        }

        var stop = PseBoardLotService.RoundToValidTick(request.StopLoss);
        if (stop <= 0m)
        {
            throw new PortfolioRuleViolationException("The stop must be a positive PSE price.");
        }

        if (stop >= trade.TargetPrice)
        {
            throw new PortfolioRuleViolationException(
                $"The stop of PHP {Format(stop)} is at or above the target of PHP {Format(trade.TargetPrice)}.");
        }

        trade.CurrentStopLoss = stop;
        var saved = await _portfolio.UpdateTradeAsync(trade, cancellationToken);

        _logger?.LogInformation("{Ticker}: stop moved to PHP {Stop}.", saved.Ticker, saved.CurrentStopLoss);

        var mark = (await LoadMarkToMarketAsync(new[] { saved }, cancellationToken))
            .TryGetValue(saved.Ticker, out var found)
            ? found
            : FallbackMark(saved);

        return BuildPosition(saved, mark, 0);
    }

    /// <summary>
    /// Settles a position at the actual fill. The sell leg is deducted, the realized peso
    /// and R result are stored, and the proceeds are credited back to cash.
    /// </summary>
    public async Task<ClosedTradeDto> CloseTradeAsync(
        int id,
        CloseTradeRequest request,
        CancellationToken cancellationToken = default)
    {
        var trade = await _portfolio.GetTradeAsync(id, cancellationToken) ??
                    throw new PortfolioRuleViolationException($"Position {id} does not exist.");

        if (trade.Status != PortfolioTradeStatus.OPEN)
        {
            throw new PortfolioRuleViolationException($"Position {id} is already closed.");
        }

        var exitPrice = PseBoardLotService.RoundToValidTick(request.ExitPrice);
        if (exitPrice <= 0m)
        {
            throw new PortfolioRuleViolationException("The exit price must be a positive PSE price.");
        }

        var proceedsPhp = PseBoardLotService.ComputeSellNetProceedsPhp(trade.Shares, exitPrice);
        var realizedPhp = PseBoardLotService.ComputeRealizedNetPnlPhp(trade.Shares, trade.EntryPrice, exitPrice);

        trade.Status = PortfolioTradeStatus.CLOSED;
        trade.ClosedDate = request.ClosedDate ?? await ResolveLatestSessionDateAsync(cancellationToken);
        trade.ExitPrice = exitPrice;
        trade.SellProceedsPhp = proceedsPhp;
        trade.RealizedNetPnlPhp = realizedPhp;
        trade.RealizedNetR = trade.InitialNetRiskPhp > 0m
            ? decimal.Round(realizedPhp / trade.InitialNetRiskPhp, 4, MidpointRounding.AwayFromZero)
            : null;

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            trade.Notes = request.Notes.Trim();
        }

        var account = await _portfolio.GetOrCreateAccountAsync(cancellationToken);
        account.CashBalancePhp = decimal.Round(account.CashBalancePhp + proceedsPhp, 2, MidpointRounding.AwayFromZero);
        account.UpdatedAtUtc = DateTime.UtcNow;

        var saved = await _portfolio.UpdateTradeAsync(trade, cancellationToken);
        await _portfolio.UpdateAccountAsync(account, cancellationToken);

        _logger?.LogInformation(
            "Closed {Shares} {Ticker} at PHP {Exit}: PHP {Pnl} realized ({R}R).",
            saved.Shares,
            saved.Ticker,
            saved.ExitPrice,
            saved.RealizedNetPnlPhp,
            saved.RealizedNetR);

        return ToClosedDto(saved);
    }

    /// <summary>
    /// Banks part of an open position without abandoning it. The trimmed slice becomes its own
    /// closed trade carrying its proportional share of the original fee-paid cost and entry
    /// risk, the remaining shares keep the rest, the proceeds are credited to cash, and a trim
    /// that prints above break-even ratchets the live stop up to it.
    /// </summary>
    public async Task<TrimTradeResultDto> TrimTradeAsync(
        int id,
        TrimTradeRequest request,
        CancellationToken cancellationToken = default)
    {
        var trade = await _portfolio.GetTradeAsync(id, cancellationToken) ??
                    throw new PortfolioRuleViolationException($"Position {id} does not exist.");

        if (trade.Status != PortfolioTradeStatus.OPEN)
        {
            throw new PortfolioRuleViolationException($"Position {id} is already closed; there is nothing to trim.");
        }

        if (request.Shares <= 0 || request.Shares >= trade.Shares)
        {
            throw new PortfolioRuleViolationException(
                $"Trim between 1 and {trade.Shares - 1} shares; closing the whole position is a different action.");
        }

        var exitPrice = PseBoardLotService.RoundToValidTick(request.ExitPrice);
        if (exitPrice <= 0m)
        {
            throw new PortfolioRuleViolationException("The trim price must be a positive PSE price.");
        }

        var exitBoardLot = PseBoardLotService.GetBoardLotSize(exitPrice);
        if (exitBoardLot <= 0 || request.Shares % exitBoardLot != 0)
        {
            throw new PortfolioRuleViolationException(
                $"{trade.Ticker} trades in {exitBoardLot}-share board lots at PHP {Format(exitPrice)}; the " +
                $"{request.Shares}-share trim is not a whole multiple.");
        }

        var remainingShares = trade.Shares - request.Shares;
        var entryBoardLot = PseBoardLotService.GetBoardLotSize(trade.EntryPrice);
        if (entryBoardLot <= 0 || remainingShares % entryBoardLot != 0)
        {
            throw new PortfolioRuleViolationException(
                $"The {remainingShares} shares left behind are not a whole multiple of the " +
                $"{entryBoardLot}-share board lot {trade.Ticker} was bought in.");
        }

        // The slice carries its proportional share of the original cost and entry risk, so the
        // blended R of every slice always reproduces the position's own result.
        var ratio = (decimal)request.Shares / trade.Shares;
        var sliceBuyCostPhp = decimal.Round(trade.BuyCostPhp * ratio, 2, MidpointRounding.AwayFromZero);
        var sliceRiskPhp = decimal.Round(trade.InitialNetRiskPhp * ratio, 2, MidpointRounding.AwayFromZero);
        var proceedsPhp = PseBoardLotService.ComputeSellNetProceedsPhp(request.Shares, exitPrice);
        var realizedPhp = decimal.Round(proceedsPhp - sliceBuyCostPhp, 2, MidpointRounding.AwayFromZero);

        var slice = new PortfolioTrade
        {
            Ticker = trade.Ticker,
            CompanyName = trade.CompanyName,
            Sector = trade.Sector,
            Horizon = trade.Horizon,
            EdgeVerdict = trade.EdgeVerdict,
            SignalItemId = trade.SignalItemId,
            Status = PortfolioTradeStatus.CLOSED,
            OpenedDate = trade.OpenedDate,
            ClosedDate = request.ClosedDate ?? await ResolveLatestSessionDateAsync(cancellationToken),
            Shares = request.Shares,
            EntryPrice = trade.EntryPrice,
            InitialStopLoss = trade.InitialStopLoss,
            CurrentStopLoss = trade.CurrentStopLoss,
            TargetPrice = trade.TargetPrice,
            BuyCostPhp = sliceBuyCostPhp,
            InitialNetRiskPhp = sliceRiskPhp,
            ExitPrice = exitPrice,
            SellProceedsPhp = proceedsPhp,
            RealizedNetPnlPhp = realizedPhp,
            RealizedNetR = sliceRiskPhp > 0m
                ? decimal.Round(realizedPhp / sliceRiskPhp, 4, MidpointRounding.AwayFromZero)
                : null,
            PyramidCount = trade.PyramidCount,
            Notes = string.IsNullOrWhiteSpace(request.Notes)
                ? $"Trimmed {request.Shares} of {trade.Shares} shares"
                : request.Notes.Trim()
        };

        trade.Shares = remainingShares;
        trade.BuyCostPhp = decimal.Round(trade.BuyCostPhp - sliceBuyCostPhp, 2, MidpointRounding.AwayFromZero);
        trade.InitialNetRiskPhp = decimal.Round(
            Math.Max(0m, trade.InitialNetRiskPhp - sliceRiskPhp),
            2,
            MidpointRounding.AwayFromZero);

        // Banking part of the position above the break-even tick is what funds the free ride:
        // the remainder cannot lose money even if the ratcheted stop fills tomorrow.
        var breakEvenStop = PseBoardLotService.ComputeBreakEvenStopPrice(trade.EntryPrice);
        if (exitPrice >= breakEvenStop &&
            breakEvenStop < trade.TargetPrice &&
            trade.CurrentStopLoss < breakEvenStop)
        {
            trade.CurrentStopLoss = breakEvenStop;
        }

        var account = await _portfolio.GetOrCreateAccountAsync(cancellationToken);
        account.CashBalancePhp = decimal.Round(account.CashBalancePhp + proceedsPhp, 2, MidpointRounding.AwayFromZero);
        account.UpdatedAtUtc = DateTime.UtcNow;

        var savedSlice = await _portfolio.AddTradeAsync(slice, cancellationToken);
        var savedRemaining = await _portfolio.UpdateTradeAsync(trade, cancellationToken);
        await _portfolio.UpdateAccountAsync(account, cancellationToken);

        _logger?.LogInformation(
            "Trimmed {TrimShares} of {Ticker} at PHP {Exit}: PHP {Pnl} realized ({R}R); {Remaining} shares left " +
            "with the stop at PHP {Stop}.",
            request.Shares,
            savedSlice.Ticker,
            savedSlice.ExitPrice,
            savedSlice.RealizedNetPnlPhp,
            savedSlice.RealizedNetR,
            savedRemaining.Shares,
            savedRemaining.CurrentStopLoss);

        var mark = (await LoadMarkToMarketAsync(new[] { savedRemaining }, cancellationToken))
            .TryGetValue(savedRemaining.Ticker, out var foundMark)
            ? foundMark
            : FallbackMark(savedRemaining);

        return new TrimTradeResultDto(BuildPosition(savedRemaining, mark, 0), ToClosedDto(savedSlice));
    }

    public async Task<PortfolioSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var account = await _portfolio.GetOrCreateAccountAsync(cancellationToken);
        var open = await _portfolio.GetOpenTradesAsync(cancellationToken);
        var closed = await _portfolio.GetClosedTradesAsync(ClosedTradePageSize, cancellationToken);

        var marks = await LoadMarkToMarketAsync(open, cancellationToken);
        var sessionDates = (await _signals.GetSnapshotDatesAsync(cancellationToken))
            .OrderBy(date => date)
            .ToList();

        var positions = new List<OpenPositionDto>(open.Count);
        foreach (var trade in open.OrderBy(t => t.OpenedDate).ThenBy(t => t.Ticker, StringComparer.Ordinal))
        {
            var mark = marks.TryGetValue(trade.Ticker, out var foundMark) ? foundMark : FallbackMark(trade);
            var sessionsHeld = sessionDates.Count(date => date > trade.OpenedDate);
            positions.Add(BuildPosition(trade, mark, sessionsHeld));
        }

        var marketValuePhp = positions.Sum(position =>
            PseBoardLotService.ComputeSellNetProceedsPhp(position.Shares, position.LastPrice));
        var totalEquityPhp = decimal.Round(account.CashBalancePhp + marketValuePhp, 2, MidpointRounding.AwayFromZero);
        var investedPhp = decimal.Round(positions.Sum(position => position.BuyCostPhp), 2, MidpointRounding.AwayFromZero);
        var unrealizedPhp = decimal.Round(positions.Sum(position => position.UnrealizedNetPnlPhp), 2, MidpointRounding.AwayFromZero);
        var realizedPhp = decimal.Round(
            closed.Sum(trade => trade.RealizedNetPnlPhp ?? 0m),
            2,
            MidpointRounding.AwayFromZero);

        // Open heat is what the book loses if every live stop fills today, after both fee
        // legs. A stop already above break-even contributes zero heat, not negative risk.
        var openRiskPhp = decimal.Round(
            open.Sum(trade => Math.Max(
                0m,
                trade.BuyCostPhp - PseBoardLotService.ComputeSellNetProceedsPhp(trade.Shares, trade.CurrentStopLoss))),
            2,
            MidpointRounding.AwayFromZero);

        var openHeatPct = totalEquityPhp > 0m
            ? decimal.Round(openRiskPhp / totalEquityPhp * 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;

        var closedCount = closed.Count;
        var wins = closed.Count(trade => (trade.RealizedNetPnlPhp ?? 0m) > 0m);
        var winRate = closedCount == 0
            ? 0m
            : decimal.Round((decimal)wins / closedCount, 4, MidpointRounding.AwayFromZero);

        // Expectancy telemetry in R: the per-trade pulse of the edge, independent of position
        // size. Only settled trades whose entry risk was recorded carry an R to average.
        var realizedRs = closed
            .Where(trade => trade.RealizedNetR is not null)
            .Select(trade => trade.RealizedNetR!.Value)
            .ToList();

        var expectancyR = realizedRs.Count == 0
            ? 0m
            : decimal.Round(realizedRs.Average(), 4, MidpointRounding.AwayFromZero);

        var grossWinR = realizedRs.Where(value => value > 0m).Sum();
        var grossLossR = Math.Abs(realizedRs.Where(value => value < 0m).Sum());
        var winCount = realizedRs.Count(value => value > 0m);
        var lossCount = realizedRs.Count(value => value < 0m);

        var profitFactor = realizedRs.Count == 0
            ? 0m
            : grossLossR == 0m
                ? (grossWinR > 0m ? 99m : 0m)
                : decimal.Round(grossWinR / grossLossR, 2, MidpointRounding.AwayFromZero);

        var payoffRatio = winCount == 0 || lossCount == 0
            ? (decimal?)null
            : decimal.Round(
                (grossWinR / winCount) / (grossLossR / lossCount),
                2,
                MidpointRounding.AwayFromZero);

        var alerts = new List<string>();
        foreach (var position in positions.Where(p => p.Action != nameof(PositionActionSignal.HOLD)))
        {
            alerts.Add($"{position.Ticker}: {position.Action.Replace('_', ' ')} — {position.ActionNote}");
        }

        if (openHeatPct > account.MaxPortfolioHeatPct)
        {
            alerts.Add(
                $"Portfolio heat is {Format(openHeatPct)}% against a {Format(account.MaxPortfolioHeatPct)}% cap: " +
                "no new risk until a position is trimmed or closed.");
        }

        var (peakEquityPhp, drawdownPct) = await UpdatePeakAndDrawdownAsync(account, totalEquityPhp, cancellationToken);
        var riskPosture = ResolveRiskPosture(account, drawdownPct);

        if (riskPosture == RiskGovernorState.Halted)
        {
            alerts.Add(
                $"The book is {Format(drawdownPct)}% below its PHP {Format(peakEquityPhp)} peak, past the " +
                $"{Format(account.HardDrawdownThresholdPct)}% hard limit: new risk is halted.");
        }
        else if (riskPosture == RiskGovernorState.Defensive)
        {
            alerts.Add(
                $"The book is {Format(drawdownPct)}% below its PHP {Format(peakEquityPhp)} peak, past the " +
                $"{Format(account.SoftDrawdownThresholdPct)}% soft limit: per-trade risk is scaled down.");
        }

        var sectorExposure = await BuildSectorExposureAsync(positions, account, totalEquityPhp, cancellationToken);

        foreach (var sector in sectorExposure.Where(entry => entry.ExposurePct > entry.MaxSectorExposurePct))
        {
            alerts.Add(
                $"{sector.Sector} is {Format(sector.ExposurePct)}% of equity against a " +
                $"{Format(sector.MaxSectorExposurePct)}% sector cap: no new name in that cluster.");
        }

        return new PortfolioSummaryDto(
            ToAccountDto(account),
            totalEquityPhp,
            investedPhp,
            unrealizedPhp,
            realizedPhp,
            openRiskPhp,
            openHeatPct,
            positions.Count,
            closedCount,
            winRate,
            positions,
            closed
                .OrderByDescending(trade => trade.ClosedDate ?? trade.OpenedDate)
                .ThenByDescending(trade => trade.Id)
                .Select(ToClosedDto)
                .ToList(),
            BuildRealizedEquityCurve(account, closed),
            alerts,
            drawdownPct,
            peakEquityPhp,
            riskPosture,
            sectorExposure,
            expectancyR,
            profitFactor,
            payoffRatio);
    }

    /// <summary>
    /// Sector heat for the book: how much of equity each PSE sector already occupies and how
    /// many names it holds, measured against the account's own caps. Positions whose ticker
    /// is missing from the newest session land in "Unclassified" instead of being dropped.
    /// </summary>
    private async Task<IReadOnlyList<SectorExposureDto>> BuildSectorExposureAsync(
        IReadOnlyList<OpenPositionDto> positions,
        PortfolioAccount account,
        decimal equityPhp,
        CancellationToken cancellationToken)
    {
        if (positions.Count == 0 || equityPhp <= 0m)
        {
            return Array.Empty<SectorExposureDto>();
        }

        var snapshot = await _signals.GetLatestSnapshotAsync(cancellationToken);
        var metrics = snapshot is null
            ? Array.Empty<StockDailyMetric>()
            : await _signals.GetMetricsAsync(snapshot.TradeDate, cancellationToken);
        var sectorByTicker = metrics.ToDictionary(
            metric => metric.Ticker,
            metric => metric.Sector,
            StringComparer.OrdinalIgnoreCase);

        return positions
            .GroupBy(position => string.IsNullOrWhiteSpace(sectorByTicker.GetValueOrDefault(position.Ticker))
                ? "Unclassified"
                : sectorByTicker[position.Ticker])
            .Select(group => new SectorExposureDto(
                group.Key,
                decimal.Round(group.Sum(position => position.BuyCostPhp) / equityPhp * 100m, 2, MidpointRounding.AwayFromZero),
                group.Count(),
                account.MaxSectorExposurePct,
                account.MaxPositionsPerSector))
            .OrderByDescending(entry => entry.ExposurePct)
            .ThenBy(entry => entry.Sector, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Lazily seeds the equity high-water mark, raises it when the book makes a new high and
    /// returns the current drawdown against it. The peak is persisted so a restart cannot
    /// forget that the pod is in a drawdown.
    /// </summary>
    private async Task<(decimal PeakEquityPhp, decimal DrawdownPct)> UpdatePeakAndDrawdownAsync(
        PortfolioAccount account,
        decimal equityPhp,
        CancellationToken cancellationToken)
    {
        var roundedEquity = decimal.Round(equityPhp, 2, MidpointRounding.AwayFromZero);
        var seeded = Math.Max(account.StartingCapitalPhp, roundedEquity);
        var peak = account.PeakEquityPhp is decimal stored && stored > 0m ? stored : seeded;

        if (roundedEquity > peak)
        {
            peak = roundedEquity;
        }

        if (account.PeakEquityPhp != peak)
        {
            account.PeakEquityPhp = peak;
            account.UpdatedAtUtc = DateTime.UtcNow;
            await _portfolio.UpdateAccountAsync(account, cancellationToken);
        }

        var drawdownPct = peak > 0m
            ? decimal.Round(Math.Max(0m, (peak - roundedEquity) / peak * 100m), 2, MidpointRounding.AwayFromZero)
            : 0m;

        return (peak, drawdownPct);
    }

    /// <summary>The governor state a drawdown implies, using the account's own thresholds.</summary>
    private static string ResolveRiskPosture(PortfolioAccount account, decimal drawdownPct)
    {
        if (account.HardDrawdownThresholdPct > 0m && drawdownPct >= account.HardDrawdownThresholdPct)
        {
            return RiskGovernorState.Halted;
        }

        return account.SoftDrawdownThresholdPct > 0m && drawdownPct >= account.SoftDrawdownThresholdPct
            ? RiskGovernorState.Defensive
            : RiskGovernorState.Normal;
    }

    /// <summary>Equity, cash and open heat used to size or validate a new order.</summary>
    private async Task<(PortfolioAccount Account, decimal EquityPhp, decimal OpenRiskPhp)> LoadCapitalAsync(
        CancellationToken cancellationToken)
    {
        var account = await _portfolio.GetOrCreateAccountAsync(cancellationToken);
        var open = await _portfolio.GetOpenTradesAsync(cancellationToken);
        var marks = await LoadMarkToMarketAsync(open, cancellationToken);

        var marketValuePhp = open.Sum(trade =>
        {
            var mark = marks.TryGetValue(trade.Ticker, out var found) ? found : FallbackMark(trade);
            return PseBoardLotService.ComputeSellNetProceedsPhp(trade.Shares, mark.Close);
        });

        var openRiskPhp = open.Sum(trade => Math.Max(
            0m,
            trade.BuyCostPhp - PseBoardLotService.ComputeSellNetProceedsPhp(trade.Shares, trade.CurrentStopLoss)));

        return (account, account.CashBalancePhp + marketValuePhp, openRiskPhp);
    }

    /// <summary>
    /// Finds the newest stored session for each open ticker by walking back through the
    /// stored snapshot dates. A name that has not printed since the entry keeps its entry
    /// price as the mark instead of inventing a quote.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, MarkToMarket>> LoadMarkToMarketAsync(
        IReadOnlyList<PortfolioTrade> open,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MarkToMarket>(StringComparer.OrdinalIgnoreCase);
        var wanted = open.Select(trade => trade.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
        {
            return result;
        }

        var dates = (await _signals.GetSnapshotDatesAsync(cancellationToken))
            .OrderByDescending(date => date)
            .Take(MarkLookbackSessions)
            .ToList();

        foreach (var date in dates)
        {
            if (wanted.All(result.ContainsKey))
            {
                break;
            }

            var metrics = await _signals.GetMetricsAsync(date, cancellationToken);
            foreach (var metric in metrics)
            {
                if (!wanted.Contains(metric.Ticker) || result.ContainsKey(metric.Ticker))
                {
                    continue;
                }

                result[metric.Ticker] = new MarkToMarket(
                    date,
                    metric.HighPrice ?? metric.ClosePrice,
                    metric.LowPrice ?? metric.ClosePrice,
                    metric.ClosePrice,
                    metric.Ema20,
                    HasSession: true);
            }
        }

        return result;
    }

    private async Task<decimal> ResolveTurnoverAsync(string ticker, CancellationToken cancellationToken)
    {
        var normalized = (ticker ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length == 0)
        {
            return 0m;
        }

        var snapshot = await _signals.GetLatestSnapshotAsync(cancellationToken);
        if (snapshot is null)
        {
            return 0m;
        }

        var metric = await _signals.GetMetricAsync(snapshot.TradeDate, normalized, cancellationToken);

        return metric?.AvgTurnover20dPhp ?? 0m;
    }

    private async Task<DateOnly> ResolveLatestSessionDateAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _signals.GetLatestSnapshotAsync(cancellationToken);

        return snapshot?.TradeDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
    }

    private static MarkToMarket FallbackMark(PortfolioTrade trade) =>
        new(trade.OpenedDate, trade.EntryPrice, trade.EntryPrice, trade.EntryPrice, trade.EntryPrice, HasSession: false);

    /// <summary>
    /// The bank-half instruction the blotter offers: the largest slice at or below half the
    /// position that is a whole board lot at the <b>exit</b> price and leaves a whole board lot
    /// at the <b>entry</b> price. Both legs matter because PSE lots coarsen as a price falls
    /// (PHP 5.00 trades in 100-share lots, PHP 4.99 in 1,000-share lots), so a slice that looked
    /// like a round lot at entry can be unsellable at today's mark. Returns 0 when no legal trim
    /// exists — the UI then offers no button instead of a quantity the server would reject.
    /// </summary>
    private static int ComputeRecommendedTrimShares(PortfolioTrade trade, MarkToMarket mark)
    {
        var entryBoardLot = PseBoardLotService.GetBoardLotSize(trade.EntryPrice);
        var exitBoardLot = PseBoardLotService.GetBoardLotSize(mark.Close);
        if (entryBoardLot <= 0 || exitBoardLot <= 0)
        {
            return 0;
        }

        for (var slice = trade.Shares / 2 / exitBoardLot * exitBoardLot;
             slice >= exitBoardLot;
             slice -= exitBoardLot)
        {
            if ((trade.Shares - slice) % entryBoardLot == 0)
            {
                return slice;
            }
        }

        return 0;
    }

    /// <summary>
    /// Marks one open position to the session and derives its exit instruction. Every
    /// number is fee-paid: the unrealized P&amp;L already deducts the sell leg the position
    /// would pay today, and the R denominator is the all-in risk recorded at entry.
    /// </summary>
    private static OpenPositionDto BuildPosition(PortfolioTrade trade, MarkToMarket mark, int sessionsHeld)
    {
        var proceedsPhp = PseBoardLotService.ComputeSellNetProceedsPhp(trade.Shares, mark.Close);
        var unrealizedPhp = decimal.Round(proceedsPhp - trade.BuyCostPhp, 2, MidpointRounding.AwayFromZero);
        var unrealizedR = trade.InitialNetRiskPhp > 0m
            ? decimal.Round(unrealizedPhp / trade.InitialNetRiskPhp, 4, MidpointRounding.AwayFromZero)
            : 0m;

        var breakEven = PseBoardLotService.ComputeBreakEvenStopPrice(trade.EntryPrice);
        var profile = HorizonProfile.For(trade.Horizon);
        var (action, note) = ResolveAction(trade, mark, unrealizedR, breakEven, sessionsHeld, profile);

        var recommendedTrimShares = ComputeRecommendedTrimShares(trade, mark);

        return new OpenPositionDto(
            trade.Id,
            trade.Ticker,
            trade.CompanyName,
            trade.Sector,
            trade.Horizon.ToString(),
            trade.EdgeVerdict.ToString(),
            trade.OpenedDate,
            trade.Shares,
            trade.EntryPrice,
            trade.InitialStopLoss,
            trade.CurrentStopLoss,
            trade.TargetPrice,
            trade.BuyCostPhp,
            trade.InitialNetRiskPhp,
            mark.Close,
            mark.Ema20 > 0m ? mark.Ema20 : null,
            unrealizedPhp,
            unrealizedR,
            breakEven,
            action.ToString(),
            note,
            sessionsHeld,
            trade.PyramidCount,
            recommendedTrimShares);
    }

    /// <summary>
    /// Deterministic exit ladder, most urgent first: an honoured stop, a reached target, a
    /// free-trade stop move at +1R, an EMA20 trail at +1.5R, and the horizon time stop.
    /// A bar that touched both barriers resolves as a stop-out, the conservative reading of
    /// daily OHLC.
    /// </summary>
    private static (PositionActionSignal Action, string Note) ResolveAction(
        PortfolioTrade trade,
        MarkToMarket mark,
        decimal unrealizedR,
        decimal breakEvenStop,
        int sessionsHeld,
        HorizonProfile profile)
    {
        if (mark.HasSession && mark.Low <= trade.CurrentStopLoss)
        {
            return (
                PositionActionSignal.STOP_BREACHED,
                $"The session traded PHP {Format(mark.Low)} through the stop of PHP {Format(trade.CurrentStopLoss)}: " +
                $"honour it, the loss is about {Format(unrealizedR)}R after fees.");
        }

        if (mark.HasSession && mark.High >= trade.TargetPrice)
        {
            return (
                PositionActionSignal.TARGET_REACHED,
                $"The session reached the target of PHP {Format(trade.TargetPrice)} (high PHP {Format(mark.High)}): " +
                "take the profit or trail the stop, do not widen the plan.");
        }

        if (unrealizedR >= 1.0m && trade.CurrentStopLoss < breakEvenStop)
        {
            return (
                PositionActionSignal.MOVE_STOP_TO_BREAKEVEN,
                $"Net +{Format(unrealizedR)}R: move the stop to the fee-paid break-even tick of PHP {Format(breakEvenStop)} " +
                "so the trade cannot turn into a loss.");
        }

        if (unrealizedR >= 1.5m && mark.Ema20 > trade.CurrentStopLoss)
        {
            return (
                PositionActionSignal.TRAIL_STOP_EMA20,
                $"Net +{Format(unrealizedR)}R: trail the stop to EMA20 at PHP {Format(mark.Ema20)}.");
        }

        if (profile.ForwardWindowBars > 0 && sessionsHeld >= profile.ForwardWindowBars && unrealizedR < 0.5m)
        {
            return (
                PositionActionSignal.TIME_STOP_DUE,
                $"{sessionsHeld} sessions held against a {profile.ForwardWindowBars}-session {profile.Horizon} window " +
                $"with only {Format(unrealizedR)}R of progress: recycle the capital.");
        }

        return (
            PositionActionSignal.HOLD,
            $"No barrier touched; net {Format(unrealizedR)}R with {sessionsHeld} session(s) held.");
    }

    private static IReadOnlyList<PortfolioEquityPointDto> BuildRealizedEquityCurve(
        PortfolioAccount account,
        IReadOnlyList<PortfolioTrade> closed)
    {
        var curve = new List<PortfolioEquityPointDto>();
        var equityPhp = account.StartingCapitalPhp;
        var peakPhp = equityPhp;

        foreach (var group in closed
                     .Where(trade => trade.ClosedDate is not null)
                     .GroupBy(trade => trade.ClosedDate!.Value)
                     .OrderBy(group => group.Key))
        {
            equityPhp += group.Sum(trade => trade.RealizedNetPnlPhp ?? 0m);
            peakPhp = Math.Max(peakPhp, equityPhp);

            curve.Add(new PortfolioEquityPointDto(
                group.Key,
                decimal.Round(equityPhp, 2, MidpointRounding.AwayFromZero),
                peakPhp > 0m
                    ? decimal.Round((peakPhp - equityPhp) / peakPhp * 100m, 2, MidpointRounding.AwayFromZero)
                    : 0m));
        }

        return curve;
    }

    private static PortfolioAccountDto ToAccountDto(PortfolioAccount account) => new(
        account.StartingCapitalPhp,
        account.CashBalancePhp,
        account.MaxRiskPerTradePct,
        account.SmallSizeRiskMultiplier,
        account.MaxPositionPct,
        account.MaxPortfolioHeatPct,
        account.UpdatedAtUtc,
        account.PeakEquityPhp,
        account.MaxSectorExposurePct,
        account.MaxPositionsPerSector,
        account.SoftDrawdownThresholdPct,
        account.HardDrawdownThresholdPct,
        account.TargetAnnualVolPct);

    private static ClosedTradeDto ToClosedDto(PortfolioTrade trade) => new(
        trade.Id,
        trade.Ticker,
        trade.CompanyName,
        trade.Horizon.ToString(),
        trade.OpenedDate,
        trade.ClosedDate,
        trade.Shares,
        trade.EntryPrice,
        trade.ExitPrice ?? 0m,
        trade.SellProceedsPhp ?? 0m,
        trade.RealizedNetPnlPhp ?? 0m,
        trade.RealizedNetR,
        trade.Notes);

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Latest session price for one position plus the values the exit rules read.
    /// <paramref name="HasSession"/> is false for the entry-price fallback, because a position
    /// that has not printed since it was opened has no low or high to breach.
    /// </summary>
    private sealed record MarkToMarket(
        DateOnly Date,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Ema20,
        bool HasSession);
}

