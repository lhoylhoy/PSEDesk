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

    /// <summary>
    /// Per-horizon net reward-to-risk floors. A uniform 2.0 floor is unimplementable: every
    /// horizon builds its plan from ATR multiples with a <b>gross</b> 2.0:1 shape
    /// (<c>1.2/2.6</c>, <c>1.5/3.0</c>, <c>2.0/4.0</c>, <c>2.5/5.0</c>), and after the 0.690%
    /// round trip a 2%-6% stop lands at only 1.23-1.69 net. Gating all four books at 2.0 would
    /// therefore reject every ATR-built plan instead of rejecting the bad ones. Each floor below
    /// is what its own geometry can reach with room to spare; the DAY book, whose levels come
    /// from the model rather than the ATR multiples, keeps the strict 2.0.
    /// </summary>
    public const decimal WeekMinimumNetRiskReward = 1.50m;

    public const decimal MonthMinimumNetRiskReward = 1.40m;

    public const decimal YearMinimumNetRiskReward = 1.40m;

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
            RequireNetRewardRisk: true,
            MinimumNetRiskReward: WeekMinimumNetRiskReward,
            PreferredCategories: new[] { "TECHNICAL", "MOMENTUM", "RISK", "POSITION_SIZING" },
            ForwardWindowBars: 10,
            TargetNetRiskReward: 1.5m,
            MinimumExpectedValueR: 0.15m),
        new HorizonProfile(
            SignalHorizon.MONTH,
            Mission:
                "One to six month position book. Own smooth-trending leaders and macro/sector-rotation " +
                "beneficiaries near 52-week highs with controlled drawdowns, supportive cash yield where " +
                "reported, and clean PSE Edge disclosures; size for volatility.",
            MinHistoryBars: 200,
            RsiIdealLow: 40m,
            RsiIdealHigh: 65m,
            AtrStopMultiplier: 2.0m,
            AtrTargetMultiplier: 4.0m,
            DisclosureLookbackDays: 60,
            HysteresisConvictionDelta: 12,
            RequireNetRewardRisk: true,
            MinimumNetRiskReward: MonthMinimumNetRiskReward,
            PreferredCategories: new[] { "FUNDAMENTAL", "VALUE", "GROWTH", "MACRO", "SECTOR_ROTATION" },
            ForwardWindowBars: 42,
            TargetNetRiskReward: 1.6m,
            MinimumExpectedValueR: 0.20m),
        new HorizonProfile(
            SignalHorizon.YEAR,
            Mission:
                "Multi-year compounding book. Prefer smooth institutional uptrends (high trend efficiency), " +
                "low 52-week drawdowns, defensive beta, supportive cash dividend yield where reported, and " +
                "clean corporate disclosures; ignore short-term noise.",
            MinHistoryBars: 200,
            RsiIdealLow: 35m,
            RsiIdealHigh: 60m,
            AtrStopMultiplier: 2.5m,
            AtrTargetMultiplier: 5.0m,
            DisclosureLookbackDays: 120,
            HysteresisConvictionDelta: 15,
            RequireNetRewardRisk: true,
            MinimumNetRiskReward: YearMinimumNetRiskReward,
            PreferredCategories: new[] { "FUNDAMENTAL", "VALUE", "GROWTH", "MACRO" },
            ForwardWindowBars: 126,
            TargetNetRiskReward: 1.8m,
            MinimumExpectedValueR: 0.25m)
    };

    public static HorizonProfile For(SignalHorizon horizon) =>
        All.First(profile => profile.Horizon == horizon);

    /// <summary>
    /// Minimum volume expansion this book requires at entry. A MONTH/YEAR position does not need
    /// the 1.2x breakout print an intraday idea does; demanding one deleted most of the
    /// long-horizon training sample for no economic reason. Every book still demands at least
    /// average participation — a ratio of exactly 1.0 is "no expansion at all", which is what a
    /// name trading on its own average prints on every quiet session.
    /// </summary>
    public decimal MinimumVolumeRatio => Horizon switch
    {
        SignalHorizon.DAY => 1.2m,
        SignalHorizon.WEEK => 1.2m,
        SignalHorizon.MONTH => 1.1m,
        _ => 1.0m
    };

    public DateTime CutoffUtc(DateTime nowUtc) => nowUtc.AddDays(-DisclosureLookbackDays);
}
