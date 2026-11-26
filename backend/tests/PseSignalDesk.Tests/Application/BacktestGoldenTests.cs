using System.Diagnostics;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// The output-equivalence guard for the backend performance work. Every number the replay
/// publishes — the curve, the trades, the fees, the slippage, alpha, CAGR, Sharpe — is
/// serialised and compared with a checked-in snapshot. The fixture is synthetic and seeded, so
/// the test never touches the network and any drift in an indicator, a plan level or a fee
/// fails the run instead of quietly changing a desk number.
/// </summary>
public sealed class BacktestGoldenTests
{
    public static TheoryData<SignalHorizon> Horizons => new()
    {
        SignalHorizon.DAY,
        SignalHorizon.WEEK,
        SignalHorizon.MONTH,
        SignalHorizon.YEAR
    };

    [Theory]
    [MemberData(nameof(Horizons))]
    public async Task Backtest_output_matches_the_recorded_snapshot(SignalHorizon horizon)
    {
        var result = await RunAsync(horizon);

        GoldenSnapshot.AssertMatches($"backtest_{horizon}.json", GoldenSnapshot.Serialize(result));
    }

    /// <summary>
    /// Prints the replay's elapsed time and allocations. Skipped by default because a timing
    /// figure is a baseline to compare by hand, not an assertion a busy CI box can honour.
    /// Measured in Release on 2026-11-26 over the same seeded fixture (three runs each side):
    /// pre-optimisation 6,554 / 6,327 / 6,224 ms and 1,708.4 MB on every run; afterwards
    /// 4,741 / 5,649 / 6,247 ms and 1,401–1,404 MB. Wall-clock noise on a laptop is ±30%, so the
    /// allocation figure is the reliable signal.
    /// </summary>
    [Fact(Skip = "Manual baseline: run it to print the replay's elapsed time and allocations.")]
    public async Task Benchmark_reports_elapsed_time_and_allocations()
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();

        foreach (var horizon in new[] { SignalHorizon.DAY, SignalHorizon.WEEK, SignalHorizon.MONTH, SignalHorizon.YEAR })
        {
            var result = await RunAsync(horizon);
            Console.WriteLine(
                $"{horizon}: {result.TotalTrades} trades, {result.EquityCurve.Count} sessions, " +
                $"end {result.EndingEquityPhp:N2}, {result.NetReturnPct:N2}%");
        }

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Console.WriteLine(
            $"Four-horizon replay: {stopwatch.Elapsed.TotalMilliseconds:N0} ms, " +
            $"{allocated / 1024d / 1024d:N1} MB allocated on this thread.");

        Assert.True(stopwatch.Elapsed >= TimeSpan.Zero);
    }

    private static async Task<BacktestResultDto> RunAsync(SignalHorizon horizon)
    {
        var batch = SyntheticMarketFixture.BuildBatch();
        var service = new BacktestService(
            new SyntheticMarketFixture.FixedMarketDataProvider(batch),
            new QuantitativeFilterService());

        return await service.RunAsync(horizon, batch.TradeDate, lookbackDays: 900);
    }
}
