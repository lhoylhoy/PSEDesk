using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Models;

/// <summary>
/// Deterministic parameters for one signal horizon. Everything the desk tunes per
/// horizon (history requirement, RSI band, ATR multiples, disclosure lookback,
/// hysteresis threshold and the net fee-adjusted reward gate) lives here so the
/// behaviour of DAY, WEEK, MONTH and YEAR is reviewable in one place.
/// </summary>
public sealed record HorizonProfile(
    SignalHorizon Horizon,
    string Mission,
    int MinHistoryBars,
    decimal RsiIdealLow,
    decimal RsiIdealHigh,
    decimal AtrStopMultiplier,
    decimal AtrTargetMultiplier,
    int DisclosureLookbackDays,
    int HysteresisConvictionDelta,
    bool RequireNetRewardRisk,
    decimal MinimumNetRiskReward,
    IReadOnlyList<string> PreferredCategories,
    int ForwardWindowBars,
    decimal TargetNetRiskReward,
    decimal MinimumExpectedValueR)
{
    public const decimal DefaultMinimumNetRiskReward = 2.0m;

    public static IReadOnlyList<HorizonProfile> All { get; } = new[]
    {
        new HorizonProfile(
            SignalHorizon.DAY,
            Mission:
                "Intraday to next-session momentum. Trade only liquid names with a fresh catalyst, " +
                "respect the +/- static price band and make sure the move still pays the round-trip fees.",
            MinHistoryBars: 60,
            RsiIdealLow: 50m,
            RsiIdealHigh: 72m,
            AtrStopMultiplier: 1.2m,
            AtrTargetMultiplier: 2.6m,
            DisclosureLookbackDays: 7,
            HysteresisConvictionDelta: 5,
            RequireNetRewardRisk: true,
            MinimumNetRiskReward: DefaultMinimumNetRiskReward,
            PreferredCategories: new[] { "TECHNICAL", "MOMENTUM", "RISK", "POSITION_SIZING" },
            ForwardWindowBars: 3,
            TargetNetRiskReward: 2.0m,
            MinimumExpectedValueR: 0.20m),
        new HorizonProfile(
            SignalHorizon.WEEK,
            Mission:
                "One to three week swing book. Buy Stage 2 advancing names breaking out of a volume " +
                "contraction, or pullbacks into EMA20/SMA50 with a defined stop below structure.",
            MinHistoryBars: 200,
            RsiIdealLow: 45m,
            RsiIdealHigh: 68m,
            AtrStopMultiplier: 1.5m,
            AtrTargetMultiplier: 3.0m,
            DisclosureLookbackDays: 21,
            HysteresisConvictionDelta: 10,
            RequireNetRewardRisk: false,
            MinimumNetRiskReward: DefaultMinimumNetRiskReward,
            PreferredCategories: new[] { "TECHNICAL", "MOMENTUM", "RISK", "POSITION_SIZING" },
            ForwardWindowBars: 10,
            TargetNetRiskReward: 1.5m,
            MinimumExpectedValueR: 0.15m),
        new HorizonProfile(
            SignalHorizon.MONTH,
            Mission:
                "One to six month position book. Own quality compounders and macro beneficiaries while " +
                "the earnings cycle and BSP policy direction stay supportive; size for volatility.",
            MinHistoryBars: 200,
            RsiIdealLow: 40m,
            RsiIdealHigh: 65m,
            AtrStopMultiplier: 2.0m,
            AtrTargetMultiplier: 4.0m,
            DisclosureLookbackDays: 60,
            HysteresisConvictionDelta: 12,
            RequireNetRewardRisk: false,
            MinimumNetRiskReward: DefaultMinimumNetRiskReward,
            PreferredCategories: new[] { "FUNDAMENTAL", "VALUE", "GROWTH", "MACRO", "SECTOR_ROTATION" },
            ForwardWindowBars: 42,
            TargetNetRiskReward: 1.6m,
            MinimumExpectedValueR: 0.20m),
        new HorizonProfile(
            SignalHorizon.YEAR,
            Mission:
                "Multi-year compounding book. Prefer high return on capital, sane leverage and a PEG " +
                "near or below one; ignore intraday noise and let the business carry the position.",
            MinHistoryBars: 200,
            RsiIdealLow: 35m,
            RsiIdealHigh: 60m,
            AtrStopMultiplier: 2.5m,
            AtrTargetMultiplier: 5.0m,
            DisclosureLookbackDays: 120,
            HysteresisConvictionDelta: 15,
            RequireNetRewardRisk: false,
            MinimumNetRiskReward: DefaultMinimumNetRiskReward,
            PreferredCategories: new[] { "FUNDAMENTAL", "VALUE", "GROWTH", "MACRO" },
            ForwardWindowBars: 126,
            TargetNetRiskReward: 1.8m,
            MinimumExpectedValueR: 0.25m)
    };

    public static HorizonProfile For(SignalHorizon horizon) =>
        All.First(profile => profile.Horizon == horizon);

    public DateTime CutoffUtc(DateTime nowUtc) => nowUtc.AddDays(-DisclosureLookbackDays);
}
