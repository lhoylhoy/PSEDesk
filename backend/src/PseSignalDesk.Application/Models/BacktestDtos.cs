namespace PseSignalDesk.Application.Models;

/// <summary>One simulated round trip from the walk-forward backtester.</summary>
/// <summary>
/// One closed replay trade. <see cref="NetPnlPhp"/> is what the book actually banked: the
/// exchange's fee schedule on both legs <i>and</i> the round-trip slippage the clip paid when it
/// was opened, which is why <see cref="SlippagePhp"/> is published alongside it.
/// </summary>
public sealed record BacktestTradeDto(
    string Ticker,
    DateOnly EntryDate,
    decimal EntryPrice,
    DateOnly ExitDate,
    decimal ExitPrice,
    int Shares,
    decimal NetPnlPhp,
    decimal NetR,
    string ExitReason,
    int BarsHeld,
    /// <summary>Tick-spread and impact cost this clip paid on the way in and out, in pesos.</summary>
    decimal SlippagePhp = 0m);

/// <summary>
/// One session of the mark-to-market equity curve. <see cref="BenchmarkEquityPhp"/> is the
/// same starting capital held in <c>PSEI.PS</c> from the first replayed session, so the two
/// lines are directly comparable; it stays null when the feed returned no index bars.
/// </summary>
public sealed record BacktestEquityPointDto(
    DateOnly Date,
    decimal EquityPhp,
    decimal DrawdownPct,
    decimal? BenchmarkEquityPhp = null);

/// <summary>
/// Causal walk-forward result: what the horizon's rule actually compounded on the real
/// PSE tape once the 0.690% round trip, the slippage estimate and the board lots were paid,
/// measured against buying and holding the PSE index over the exact same window.
/// </summary>
public sealed record BacktestResultDto(
    string Horizon,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal StartingCapitalPhp,
    decimal EndingEquityPhp,
    decimal NetReturnPct,
    decimal MaxDrawdownPct,
    int TotalTrades,
    decimal WinRate,
    decimal ExpectancyR,
    decimal ProfitFactor,
    decimal TotalFeesPaidPhp,
    int SessionsEvaluated,
    IReadOnlyList<BacktestEquityPointDto> EquityCurve,
    IReadOnlyList<BacktestTradeDto> Trades,
    // Benchmark and risk-adjusted return. Every appended field is optional so a client
    // written before the alpha proof still deserializes a replay.
    /// <summary>Return of holding <c>PSEI.PS</c> over the same window, in percent; null without index bars.</summary>
    decimal? BenchmarkReturnPct = null,
    /// <summary>
    /// Alpha: the desk's slippage-adjusted return minus the index's. Charging the desk the
    /// full round-trip slippage while the benchmark pays nothing makes this comparison the
    /// conservative one, so a positive number is evidence rather than an artefact.
    /// </summary>
    decimal? ExcessReturnPct = null,
    /// <summary>Compound annual growth rate of the replayed equity curve, in percent.</summary>
    decimal CagrPct = 0m,
    /// <summary>Annualised Sharpe of the daily curve returns (risk-free 0), null under 20 sessions.</summary>
    decimal? AnnualizedSharpeRatio = null,
    /// <summary>CAGR divided by the max drawdown; null when the replay never drew down.</summary>
    decimal? CalmarRatio = null,
    /// <summary>Round-trip tick-spread and market-impact cost charged across every round trip, in pesos.</summary>
    decimal TotalSlippagePhp = 0m,
    /// <summary>The net return with the slippage estimate deducted as well as the fees, in percent.</summary>
    decimal NetReturnAfterSlippagePct = 0m);
