using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Tests.Application;

public sealed class QuantitativeFilterServiceTests
{
    private readonly QuantitativeFilterService _service = new();

    [Fact]
    public void ComputeSma_averages_the_most_recent_window()
    {
        var closes = Enumerable.Range(1, 10).Select(value => (decimal)value).ToList();

        Assert.Equal(9m, QuantitativeFilterService.ComputeSma(closes, 3));
        Assert.Null(QuantitativeFilterService.ComputeSma(closes, 11));
    }

    [Fact]
    public void ComputeEma_returns_the_mean_for_a_flat_series()
    {
        var closes = Enumerable.Repeat(5m, 30).ToList();

        Assert.Equal(5m, QuantitativeFilterService.ComputeEma(closes, 20));
    }

    [Fact]
    public void ComputeEmaSeries_matches_ComputeEma_on_every_prefix()
    {
        // The series is the O(n) form of "recompute the EMA from scratch at every bar". If the two
        // ever disagree by one unit in the last place, the +1.5R trail fires on a different session
        // and the replay's numbers move, so every index is pinned here.
        var closes = Enumerable.Range(0, 240)
            .Select(index => 20m + (index % 17) - (index % 5) + (index * 0.125m))
            .ToList();

        var series = QuantitativeFilterService.ComputeEmaSeries(closes);

        Assert.Equal(closes.Count, series.Length);

        for (var index = 0; index < closes.Count; index++)
        {
            var prefix = closes.Take(index + 1).ToList();
            var expected = QuantitativeFilterService.ComputeEma(prefix);

            Assert.Equal(expected, series[index]);

            if (index < QuantitativeFilterService.EmaPeriod - 1)
            {
                Assert.Null(series[index]);
            }
        }
    }

    [Fact]
    public void IsAscendingByDate_accepts_a_replay_prefix_and_rejects_an_out_of_order_series()
    {
        var ascending = Enumerable.Range(0, 5)
            .Select(offset => new OhlcvBar(new DateOnly(2026, 11, 2).AddDays(offset), 1m, 1m, 1m, 1m, 1L))
            .ToList();

        Assert.True(QuantitativeFilterService.IsAscendingByDate(ascending));
        Assert.True(QuantitativeFilterService.IsAscendingByDate(new[] { ascending[0] }));

        var swapped = new List<OhlcvBar> { ascending[0], ascending[2], ascending[1], ascending[3] };

        Assert.False(QuantitativeFilterService.IsAscendingByDate(swapped));
    }

    [Fact]
    public void ComputeRsi_saturates_on_one_way_trends_and_returns_50_when_flat()
    {
        var rising = Enumerable.Range(1, 30).Select(value => (decimal)value).ToList();
        var falling = Enumerable.Range(1, 30).Reverse().Select(value => (decimal)value).ToList();
        var flat = Enumerable.Repeat(10m, 30).ToList();

        Assert.Equal(100m, QuantitativeFilterService.ComputeRsi(rising));
        Assert.Equal(0m, QuantitativeFilterService.ComputeRsi(falling));
        Assert.Equal(50m, QuantitativeFilterService.ComputeRsi(flat));
        Assert.Null(QuantitativeFilterService.ComputeRsi(rising.Take(14).ToList()));
    }

    [Fact]
    public void ComputeAtr_uses_wilders_true_range()
    {
        var bars = Enumerable.Range(0, 20)
            .Select(day => new OhlcvBar(
                new DateOnly(2026, 1, 1).AddDays(day),
                Open: 100m,
                High: 101m,
                Low: 99m,
                Close: 100m,
                Volume: 1_000_000))
            .ToList();

        Assert.Equal(2m, QuantitativeFilterService.ComputeAtr(bars));
        Assert.Null(QuantitativeFilterService.ComputeAtr(bars.Take(10).ToList()));
    }

    [Fact]
    public void EvaluateStock_requires_two_hundred_candles()
    {
        var shortStock = BuildStock("ICT", BuildTrendBars(100m, 0.2m, 199), 2_000_000, 50_000_000m);
        Assert.Null(_service.EvaluateStock(shortStock, new DateOnly(2026, 11, 24)));

        var fullStock = BuildStock("ICT", BuildTrendBars(100m, 0.2m, 200), 2_000_000, 50_000_000m);
        var metric = _service.EvaluateStock(fullStock, new DateOnly(2026, 11, 24));

        Assert.NotNull(metric);
        Assert.Equal("ICT", metric.Ticker);
        Assert.InRange(metric.QuantPreScore, 0, 100);
        Assert.True(metric.ClosePrice > metric.Sma50);
        Assert.True(metric.Sma50 > metric.Sma200);
        Assert.True(metric.AvgTurnover20dPhp > QuantitativeFilterService.LiquidityFloorPhp);
    }

    [Fact]
    public void SelectCandidates_enforces_the_liquidity_floor()
    {
        var liquid = BuildStock("ICT", BuildTrendBars(100m, 0.2m, 220, volume: 2_000_000), 2_000_000, 20_000_000m);
        var illiquid = BuildStock("THIN", BuildTrendBars(50m, 0.1m, 220, volume: 100, volumeIncrement: 0), 100, 1_000m);

        var batch = BuildBatch(new DateOnly(2026, 11, 24), liquid, illiquid);
        var metrics = _service.EvaluateBatch(batch);

        Assert.Equal(2, metrics.Count);

        var candidates = _service.SelectCandidates(metrics);

        Assert.Single(candidates);
        Assert.Equal("ICT", candidates[0].Ticker);
        Assert.True(QuantitativeFilterService.MeetsLiquidityFloor(candidates[0].AvgTurnover20dPhp));
        Assert.False(QuantitativeFilterService.MeetsLiquidityFloor(4_999_999.99m));
    }

    [Fact]
    public void SelectCandidates_rejects_a_mean_that_one_block_trade_propped_up()
    {
        // PHP 40M of average turnover, but the median session printed PHP 400k: the mean is a
        // single block trade and the name cannot be exited in one clip.
        var blockTrade = BuildRankedMetric("BLOCK", score: 90, avgTurnover: 40_000_000m, medianTurnover: 400_000m, activeDays: 20);

        // Median is fine, but it only traded on 12 of 20 sessions.
        var sparse = BuildRankedMetric("SPARSE", score: 80, avgTurnover: 40_000_000m, medianTurnover: 30_000_000m, activeDays: 12);

        var robust = BuildRankedMetric("ROBUST", score: 70, avgTurnover: 40_000_000m, medianTurnover: 30_000_000m, activeDays: 20);

        var candidates = _service.SelectCandidates(new[] { blockTrade, sparse, robust });

        Assert.Single(candidates);
        Assert.Equal("ROBUST", candidates[0].Ticker);

        // A row written before the robust columns existed reports zero for both and must not be
        // rejected by a gate that did not exist when it was stored.
        Assert.True(QuantitativeFilterService.MeetsLiquidityFloor(9_000_000m, medianTurnover20dPhp: 0m, activeTradingDays20d: 0));
        Assert.False(QuantitativeFilterService.MeetsLiquidityFloor(9_000_000m, 400_000m, 20));
        Assert.False(QuantitativeFilterService.MeetsLiquidityFloor(9_000_000m, 9_000_000m, 15));
    }

    [Fact]
    public void ComputeMedianTurnover20d_uses_the_middle_session_not_the_mean()
    {
        var window = new List<OhlcvBar>
        {
            new(new DateOnly(2026, 11, 2), 10m, 10m, 10m, 10m, 100),
            new(new DateOnly(2026, 11, 3), 10m, 10m, 10m, 10m, 100),
            new(new DateOnly(2026, 11, 4), 10m, 10m, 10m, 10m, 1_000_000)
        };

        // Mean turnover is PHP 3.33M; the median is PHP 1,000.
        Assert.Equal(1_000m, QuantitativeFilterService.ComputeMedianTurnover20d(window));
        Assert.Equal(0m, QuantitativeFilterService.ComputeMedianTurnover20d(Array.Empty<OhlcvBar>()));
    }

    [Fact]
    public void EvaluateStock_scrubs_the_live_foreign_print_from_a_historical_replay()
    {
        var stock = BuildStock(
            "ICT",
            BuildTrendBars(100m, 0.2m, 220),
            2_000_000,
            netForeign: 12_000_000m);
        var tradeDate = new DateOnly(2026, 11, 24);

        var live = _service.EvaluateStock(stock, tradeDate);
        var replay = _service.EvaluateStock(stock, tradeDate, includeLiveForeignFlow: false);

        Assert.Equal(12_000_000m, live!.NetForeignTodayPhp);
        Assert.Null(replay!.NetForeignTodayPhp);
    }

    [Fact]
    public void EvaluateStock_only_reads_disclosures_that_were_on_the_wire_before_the_close()
    {
        var tradeDate = new DateOnly(2026, 11, 24);
        var stock = BuildStock(
            "ICT",
            BuildTrendBars(100m, 0.2m, 220),
            2_000_000,
            netForeign: null,
            disclosures: new[]
            {
                new StockDisclosure(new DateOnly(2026, 11, 20), "Board approved a joint venture", "Material"),
                new StockDisclosure(tradeDate, "Trading halt lifted at the close", "Halt", FiledAtPht: new TimeOnly(17, 40)),
                new StockDisclosure(tradeDate, "Quarterly results released at the bell", "Results", FiledAtPht: new TimeOnly(9, 30)),
                new StockDisclosure(new DateOnly(2026, 12, 15), "Rights offering priced", "Rights")
            });

        var metric = _service.EvaluateStock(stock, tradeDate);

        var headlines = MarketDataMapper
            .DeserializeDisclosures(metric!.RecentDisclosuresJson)
            .Select(disclosure => disclosure.Headline)
            .ToList();

        Assert.Contains("Board approved a joint venture", headlines);
        Assert.Contains("Quarterly results released at the bell", headlines);
        Assert.DoesNotContain("Trading halt lifted at the close", headlines);
        Assert.DoesNotContain("Rights offering priced", headlines);
    }

    [Fact]
    public void SelectCandidates_honours_an_explicit_cap_and_sorts_by_score()
    {
        var metrics = Enumerable.Range(0, 12)
            .Select(index => BuildMetric($"T{index:00}", score: index * 5))
            .ToList();

        var candidates = _service.SelectCandidates(metrics, maxCandidates: 5);

        Assert.Equal(5, candidates.Count);
        Assert.Equal("T11", candidates[0].Ticker);
        Assert.All(
            candidates.Zip(candidates.Skip(1)),
            pair => Assert.True(pair.First.QuantPreScore >= pair.Second.QuantPreScore));
    }

    [Fact]
    public void ComputePreScore_rewards_a_clean_trend_and_penalises_an_extended_one()
    {
        var healthy = QuantitativeFilterService.ComputePreScore(
            close: 120m,
            ema20: 115m,
            sma50: 110m,
            sma200: 100m,
            rsi: 55m,
            volumeRatio: 1.8m,
            avgTurnover20dPhp: 20_000_000m,
            netForeignTodayPhp: 2_000_000m,
            atr: 2m,
            support: 115m,
            resistance: 126m);

        var extended = QuantitativeFilterService.ComputePreScore(
            close: 80m,
            ema20: 90m,
            sma50: 85m,
            sma200: 95m,
            rsi: 80m,
            volumeRatio: 1.0m,
            avgTurnover20dPhp: 20_000_000m,
            netForeignTodayPhp: -5_000_000m,
            atr: 2m,
            support: 70m,
            resistance: 78m);

        Assert.Equal(95, healthy);
        Assert.Equal(19, extended);
        Assert.True(healthy > extended);
    }

    [Fact]
    public void ComputePreScore_normalises_the_technical_score_when_foreign_flow_is_unreported()
    {
        var reported = QuantitativeFilterService.ComputePreScore(
            close: 120m,
            ema20: 115m,
            sma50: 110m,
            sma200: 100m,
            rsi: 55m,
            volumeRatio: 1.8m,
            avgTurnover20dPhp: 20_000_000m,
            netForeignTodayPhp: 2_000_000m,
            atr: 2m,
            support: 115m,
            resistance: 126m);

        var unreported = QuantitativeFilterService.ComputePreScore(
            close: 120m,
            ema20: 115m,
            sma50: 110m,
            sma200: 100m,
            rsi: 55m,
            volumeRatio: 1.8m,
            avgTurnover20dPhp: 20_000_000m,
            netForeignTodayPhp: null,
            atr: 2m,
            support: 115m,
            resistance: 126m);

        // The verified technical pillars max out at 85, so 80 raw points rescale to 94
        // instead of silently losing the foreign-flow pillar's 15 points.
        Assert.Equal(95, reported);
        Assert.Equal(94, unreported);
        Assert.InRange(unreported, 0, 100);
    }

    [Fact]
    public void EvaluateStock_keeps_net_foreign_null_when_the_feed_does_not_report_it()
    {
        var stock = BuildStock("ICT", BuildTrendBars(100m, 0.2m, 220), 2_000_000, netForeign: null);

        var metric = _service.EvaluateStock(stock, new DateOnly(2026, 11, 24));

        Assert.NotNull(metric);
        Assert.Null(metric.NetForeignTodayPhp);
        Assert.InRange(metric.QuantPreScore, 0, 100);
        Assert.True(metric.ClosePrice > 0m);
    }

    [Fact]
    public void EvaluateStock_skips_a_series_without_enough_history_for_the_200_day_trend()
    {
        var stock = BuildStock("ICT", BuildTrendBars(100m, 0.2m, QuantitativeFilterService.MinBarsRequired - 1), 2_000_000, netForeign: null);

        Assert.Null(_service.EvaluateStock(stock, new DateOnly(2026, 11, 24)));
    }

    [Fact]
    public void EvaluateStock_records_the_session_range_and_probabilistic_inputs()
    {
        var bars = BuildTrendBars(100m, 0.2m, 220);
        var indexBars = BuildTrendBars(8_000m, 2m, 220);
        var stock = BuildStock("ICT", bars, 2_000_000, 50_000_000m);

        var metric = _service.EvaluateStock(stock, new DateOnly(2026, 11, 24), indexBars);

        Assert.NotNull(metric);
        Assert.Equal(bars[^1].Open, metric.OpenPrice);
        Assert.Equal(bars[^1].High, metric.HighPrice);
        Assert.Equal(bars[^1].Low, metric.LowPrice);
        Assert.True(metric.AtrPct > 0m);
        Assert.Equal(1.0m, metric.VolatilityContractionRatio);
        Assert.True(metric.RelativeStrength63d > 0m);
        Assert.InRange(metric.EmpiricalWinRate!.Value, 0m, 1m);
    }

    [Fact]
    public void ComputeRelativeStrength63d_expresses_outperformance_in_percentage_points()
    {
        var stockBars = BuildTrendBars(100m, 1m, 65);
        var flatIndex = Enumerable.Range(0, 65)
            .Select(index => new OhlcvBar(
                new DateOnly(2026, 1, 5).AddDays(index),
                Open: 100m,
                High: 100.5m,
                Low: 99.5m,
                Close: 100m,
                Volume: 1_000_000))
            .ToList();

        var relativeStrength = QuantitativeFilterService.ComputeRelativeStrength63d(stockBars, flatIndex);

        Assert.NotNull(relativeStrength);
        Assert.InRange(relativeStrength!.Value, 61m, 62m);
        Assert.Null(QuantitativeFilterService.ComputeRelativeStrength63d(stockBars, null));
        Assert.Null(QuantitativeFilterService.ComputeRelativeStrength63d(stockBars.Take(63).ToList(), flatIndex));
    }

    [Fact]
    public void ComputeVolatilityContractionRatio_flags_a_tightening_range()
    {
        var bars = new List<OhlcvBar>();
        var date = new DateOnly(2026, 1, 5);

        for (var index = 0; index < 20; index++)
        {
            bars.Add(new OhlcvBar(date.AddDays(index), 100m, 101m, 99m, 100m, 1_000_000));
        }

        // The last six sessions coil into a ±0.10 range while the base is ±1.00 wide.
        for (var index = 20; index < 30; index++)
        {
            bars.Add(new OhlcvBar(date.AddDays(index), 100m, 100.1m, 99.9m, 100m, 900_000));
        }

        var ratio = QuantitativeFilterService.ComputeVolatilityContractionRatio(bars);

        Assert.NotNull(ratio);
        Assert.True(ratio < 0.85m, $"ratio {ratio} should register as a contraction.");
        Assert.Null(QuantitativeFilterService.ComputeVolatilityContractionRatio(bars.Take(10).ToList()));
    }

    [Fact]
    public void ComputeMarketBreadth_reports_participation_and_the_stage_two_count()
    {
        var strong = BuildMetric("ICT", 90);
        strong.ClosePrice = 120m;
        strong.Sma50 = 100m;
        strong.Sma200 = 95m;
        strong.WeinsteinStage = "STAGE_2";

        var above50Only = BuildMetric("BDO", 80);
        above50Only.ClosePrice = 11m;
        above50Only.Sma50 = 10m;
        above50Only.Sma200 = 12m;
        above50Only.WeinsteinStage = "STAGE_1";

        var weak = BuildMetric("THIN", 20);
        weak.Sma50 = 11m;
        weak.Sma200 = 12m;
        weak.WeinsteinStage = "STAGE_4";

        var breadth = QuantitativeFilterService.ComputeMarketBreadth(new[] { strong, above50Only, weak });

        Assert.Equal(66.7m, breadth.AboveSma50Pct);
        Assert.Equal(33.3m, breadth.AboveSma200Pct);
        Assert.Equal(1, breadth.Stage2Count);
        Assert.Equal(3, breadth.EvaluatedStockCount);

        var empty = QuantitativeFilterService.ComputeMarketBreadth(Array.Empty<StockDailyMetric>());
        Assert.Null(empty.AboveSma50Pct);
        Assert.Equal(0, empty.EvaluatedStockCount);
    }

    [Fact]
    public void ComputeHorizonScore_rewards_relative_strength_and_volatility_contraction()
    {
        var baseline = BuildMetric("ICT", 50);
        var improved = BuildMetric("ICT", 50);
        improved.RelativeStrength63d = 3.5m;
        improved.VolatilityContractionRatio = 0.72m;

        var profile = HorizonProfile.For(SignalHorizon.WEEK);
        var baselineScore = QuantitativeFilterService.ComputeHorizonScore(baseline, profile);
        var improvedScore = QuantitativeFilterService.ComputeHorizonScore(improved, profile);

        Assert.Equal(8m, improvedScore - baselineScore);
    }
    [Fact]
    public void ComputeHorizonScore_penalises_an_entry_stretched_far_above_ema20()
    {
        // 2.5 ATRs above the 20-day mean is a chase: the next few weeks of noise is give-back.
        var baseline = BuildMetric("ICT", 50);
        var stretched = BuildMetric("ICT", 50);
        stretched.Ema20 = 9m;

        var weekProfile = HorizonProfile.For(SignalHorizon.WEEK);

        Assert.Equal(
            -6m,
            QuantitativeFilterService.ComputeHorizonScore(stretched, weekProfile) -
            QuantitativeFilterService.ComputeHorizonScore(baseline, weekProfile));
    }

    [Fact]
    public void ComputeHorizonScore_lets_the_yearly_book_buy_an_extended_name()
    {
        // A position held through a cycle is not entered for the next month's range, so the
        // yearly book keeps its score where the shorter books pay for chasing.
        var baseline = BuildMetric("ICT", 50);
        var stretched = BuildMetric("ICT", 50);
        stretched.Ema20 = 9m;

        var yearProfile = HorizonProfile.For(SignalHorizon.YEAR);

        Assert.Equal(
            0m,
            QuantitativeFilterService.ComputeHorizonScore(stretched, yearProfile) -
            QuantitativeFilterService.ComputeHorizonScore(baseline, yearProfile));
    }

    [Fact]
    public void ComputeHorizonScore_rewards_a_pullback_inside_a_rising_trend()
    {
        // A close at its 20-day mean, in a name above its 200-day average, is the entry a longer
        // horizon gets paid for. The daily book is excluded: it needs a range, not a pullback.
        var baseline = BuildMetric("ICT", 50);
        var pullback = BuildMetric("ICT", 50);
        pullback.Ema20 = pullback.ClosePrice;

        var monthProfile = HorizonProfile.For(SignalHorizon.MONTH);
        var dayProfile = HorizonProfile.For(SignalHorizon.DAY);

        Assert.Equal(
            5m,
            QuantitativeFilterService.ComputeHorizonScore(pullback, monthProfile) -
            QuantitativeFilterService.ComputeHorizonScore(baseline, monthProfile));

        Assert.Equal(
            0m,
            QuantitativeFilterService.ComputeHorizonScore(pullback, dayProfile) -
            QuantitativeFilterService.ComputeHorizonScore(baseline, dayProfile));
    }

    [Fact]
    public void ComputeHorizonScore_needs_a_rising_200_day_trend_for_a_pullback_to_count()
    {
        // Both fixtures sit below the 200-day average, so the structural-trend sleeve is zero for
        // both and only the pullback condition can differ.
        var baseline = BuildMetric("ICT", 50);
        baseline.Sma50 = 5m;
        baseline.Sma200 = 12m;

        var broken = BuildMetric("ICT", 50);
        broken.Sma50 = 5m;
        broken.Sma200 = 12m;
        broken.Ema20 = broken.ClosePrice;

        var monthProfile = HorizonProfile.For(SignalHorizon.MONTH);

        Assert.Equal(
            0m,
            QuantitativeFilterService.ComputeHorizonScore(broken, monthProfile) -
            QuantitativeFilterService.ComputeHorizonScore(baseline, monthProfile));
    }



    [Fact]
    public void ComputeTrailingDividends_sums_only_the_last_three_hundred_sixty_five_days()
    {
        var tradeDate = new DateOnly(2026, 11, 24);
        var dividends = new[]
        {
            new StockDividendEvent(new DateOnly(2025, 11, 01), 0.50m),
            new StockDividendEvent(new DateOnly(2026, 06, 01), 1.00m),
            new StockDividendEvent(new DateOnly(2026, 11, 10), 1.25m),
            new StockDividendEvent(new DateOnly(2027, 01, 05), 9.99m)
        };

        var (perShare, yieldPct, lastExDate) =
            QuantitativeFilterService.ComputeTrailingDividends(dividends, closePrice: 100m, tradeDate);

        Assert.Equal(2.25m, perShare);
        Assert.Equal(2.25m, yieldPct);
        Assert.Equal(new DateOnly(2026, 11, 10), lastExDate);
    }

    [Fact]
    public void ComputeTrailingDividends_reports_unreported_instead_of_zero()
    {
        var tradeDate = new DateOnly(2026, 11, 24);

        Assert.Equal((null, null, (DateOnly?)null), QuantitativeFilterService.ComputeTrailingDividends(null, 100m, tradeDate));
        Assert.Equal(
            (null, null, (DateOnly?)null),
            QuantitativeFilterService.ComputeTrailingDividends(
                Array.Empty<StockDividendEvent>(),
                100m,
                tradeDate));
        Assert.Equal(
            (null, null, (DateOnly?)null),
            QuantitativeFilterService.ComputeTrailingDividends(
                new[] { new StockDividendEvent(new DateOnly(2025, 01, 02), 1m) },
                100m,
                tradeDate));
    }

    [Fact]
    public void EvaluateStock_surfaces_the_trailing_yield_and_the_ex_dividend_markdown()
    {
        var tradeDate = new DateOnly(2026, 11, 24);
        var stock = BuildStock(
            "REIT",
            BuildTrendBars(20m, 0.05m, 220),
            2_000_000,
            50_000_000m,
            dividends: new[] { new StockDividendEvent(tradeDate.AddDays(-1), 1.25m) });

        var metric = _service.EvaluateStock(stock, tradeDate);

        Assert.NotNull(metric);
        Assert.Equal(1.25m, metric.TrailingDividendPerShare);
        Assert.Equal(tradeDate.AddDays(-1), metric.LastExDividendDate);
        Assert.True(metric.TrailingDividendYieldPct > 0m);
        Assert.Contains("ex-dividend", metric.RecentDisclosuresJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateStock_reports_the_accumulation_and_quality_factors()
    {
        var bars = BuildTrendBars(100m, 0.5m, 220);
        var stock = BuildStock("ICT", bars, 2_000_000, 5_000_000m);

        var metric = _service.EvaluateStock(stock, bars[^1].Date);

        Assert.NotNull(metric);

        // One-way tape: every session advances, so up volume has no down volume to divide by
        // (the ratio saturates at the documented 10x cap).
        Assert.Equal(10m, metric!.UpDownVolumeRatio20d);

        // The close sits two-thirds up each session's range: location (+1.0 - +0.5) / 1.5.
        Assert.Equal(0.3333m, metric.ChaikinMoneyFlow20d);

        // Monotonic 63-session advance: the net move is the whole path travelled.
        Assert.Equal(1m, metric.TrendEfficiency63d);
        Assert.Equal(0m, metric.MaxDrawdown252dPct);
    }

    [Fact]
    public void EvaluateStock_prices_beta_against_the_psei_series_when_supplied()
    {
        var bars = BuildTrendBars(100m, 0.5m, 220);
        var stock = BuildStock("ICT", bars, 2_000_000, 5_000_000m);

        var withoutIndex = _service.EvaluateStock(stock, bars[^1].Date);
        var withIndex = _service.EvaluateStock(stock, bars[^1].Date, bars);

        Assert.Null(withoutIndex!.Beta252d);

        // The index series is the same series, so the empirical beta is exactly 1.00.
        Assert.Equal(1m, withIndex!.Beta252d);
    }

    [Fact]
    public void EvaluateStock_records_a_base_rate_for_every_horizon_sleeve()
    {
        var bars = BuildTrendBars(100m, 0.5m, 220);
        var stock = BuildStock("ICT", bars, 2_000_000, 5_000_000m);

        var metric = _service.EvaluateStock(stock, bars[^1].Date);

        Assert.NotNull(metric);
        Assert.NotNull(metric!.EmpiricalWinRateDay);
        Assert.NotNull(metric.EmpiricalWinRateMonth);
        Assert.NotNull(metric.EmpiricalWinRateYear);

        // The WEEK figure keeps the legacy column, and the accessor never grades a short-horizon
        // plan off another book's barrier geometry.
        Assert.Equal(metric.EmpiricalWinRate, metric.EmpiricalWinRateFor(SignalHorizon.WEEK));
        Assert.Equal(metric.EmpiricalWinRateDay, metric.EmpiricalWinRateFor(SignalHorizon.DAY));
        Assert.Equal(metric.EmpiricalWinRateMonth, metric.EmpiricalWinRateFor(SignalHorizon.MONTH));
        Assert.Equal(metric.EmpiricalWinRateYear, metric.EmpiricalWinRateFor(SignalHorizon.YEAR));
    }

    [Fact]
    public void ComputeUpDownVolumeRatio_divides_advancing_volume_by_declining_volume()
    {
        var bars = BuildVolumeBars(upSessions: 10, upVolume: 300, downVolume: 100);

        Assert.Equal(3m, QuantitativeFilterService.ComputeUpDownVolumeRatio(bars));
        // No declining volume at all saturates at the 10x cap rather than dividing by zero.
        Assert.Equal(10m, QuantitativeFilterService.ComputeUpDownVolumeRatio(BuildVolumeBars(20, 300, 0)));
        Assert.Null(QuantitativeFilterService.ComputeUpDownVolumeRatio(bars.Take(10).ToList()));
        Assert.Null(QuantitativeFilterService.ComputeUpDownVolumeRatio(BuildVolumeBars(20, 0, 0)));
    }

    [Fact]
    public void ComputeChaikinMoneyFlow_measures_the_close_location_in_the_range()
    {
        var date = new DateOnly(2026, 1, 5);
        var nearHigh = Enumerable.Range(0, 20)
            .Select(index => new OhlcvBar(date.AddDays(index), 100m, 101m, 99m, 100.9m, 1_000_000))
            .ToList();
        var nearLow = Enumerable.Range(0, 20)
            .Select(index => new OhlcvBar(date.AddDays(index), 100m, 101m, 99m, 99.1m, 1_000_000))
            .ToList();
        var locked = Enumerable.Range(0, 20)
            .Select(index => new OhlcvBar(date.AddDays(index), 100m, 100m, 100m, 100m, 1_000_000))
            .ToList();

        // ((close - low) - (high - close)) / range: (1.9 - 0.1) / 2 = +0.9 and (0.1 - 1.9) / 2 = -0.9.
        Assert.Equal(0.9m, QuantitativeFilterService.ComputeChaikinMoneyFlow(nearHigh));
        Assert.Equal(-0.9m, QuantitativeFilterService.ComputeChaikinMoneyFlow(nearLow));

        // A limit-locked window carries no location information at all.
        Assert.Null(QuantitativeFilterService.ComputeChaikinMoneyFlow(locked));
        Assert.Null(QuantitativeFilterService.ComputeChaikinMoneyFlow(nearHigh.Take(4).ToList()));
    }

    [Fact]
    public void ComputeMaxDrawdownPct_reports_the_worst_peak_to_trough_close()
    {
        var closes = new List<decimal> { 80m, 90m, 100m, 75m, 95m, 70m, 96m, 97m };
        var padded = Enumerable.Repeat(60m, 60).Concat(closes).ToList();

        // Peak 100 -> trough 70 is a 30% drawdown; the later 95 print never beats it.
        Assert.Equal(30m, QuantitativeFilterService.ComputeMaxDrawdownPct(padded));
        Assert.Null(QuantitativeFilterService.ComputeMaxDrawdownPct(closes));
        Assert.Equal(0m, QuantitativeFilterService.ComputeMaxDrawdownPct(Enumerable.Repeat(50m, 70).ToList()));
    }

    [Fact]
    public void ComputeRealizedVolatilityPct_annualises_close_to_close_returns()
    {
        Assert.Equal(0m, QuantitativeFilterService.ComputeRealizedVolatilityPct(Enumerable.Repeat(100m, 21).ToList()));

        var volatileCloses = new List<decimal>();
        for (var index = 0; index < 21; index++)
        {
            volatileCloses.Add(index % 2 == 0 ? 100m : 110m);
        }

        var vol = QuantitativeFilterService.ComputeRealizedVolatilityPct(volatileCloses);

        Assert.NotNull(vol);
        Assert.True(vol > 50m, $"a +/-10% daily oscillation should annualise far above 50%, got {vol}.");
        Assert.Null(QuantitativeFilterService.ComputeRealizedVolatilityPct(volatileCloses.Take(10).ToList()));
    }

    [Fact]
    public void ComputeTrendEfficiency_separates_a_trend_from_chop()
    {
        var bars = BuildTrendBars(100m, 1m, 64);
        var choppy = new List<OhlcvBar>();
        var date = new DateOnly(2026, 1, 5);

        for (var index = 0; index < 64; index++)
        {
            var close = index % 2 == 0 ? 100m : 101m;
            choppy.Add(new OhlcvBar(date.AddDays(index), close, close, close, close, 1_000_000));
        }

        Assert.Equal(1m, QuantitativeFilterService.ComputeTrendEfficiency(bars));

        var chop = QuantitativeFilterService.ComputeTrendEfficiency(choppy);
        Assert.NotNull(chop);
        Assert.True(chop < 0.1m, $"an alternating series scored {chop}, which is not chop.");
        Assert.Null(QuantitativeFilterService.ComputeTrendEfficiency(bars.Take(10).ToList()));
    }

    [Fact]
    public void ComputeBeta_is_the_slope_of_the_return_regression()
    {
        var index = new List<OhlcvBar>();
        var doubled = new List<OhlcvBar>();
        var date = new DateOnly(2026, 1, 5);
        var indexClose = 100m;
        var doubledClose = 100m;

        index.Add(new OhlcvBar(date, indexClose, indexClose, indexClose, indexClose, 1_000_000));
        doubled.Add(new OhlcvBar(date, doubledClose, doubledClose, doubledClose, doubledClose, 1_000_000));

        for (var step = 1; step < 80; step++)
        {
            var indexFactor = step % 2 == 1 ? 1.01m : 0.99m;
            indexClose *= indexFactor;
            doubledClose *= 1m + ((indexFactor - 1m) * 2m);

            index.Add(new OhlcvBar(date.AddDays(step), indexClose, indexClose, indexClose, indexClose, 1_000_000));
            doubled.Add(new OhlcvBar(date.AddDays(step), doubledClose, doubledClose, doubledClose, doubledClose, 1_000_000));
        }

        Assert.Equal(1m, QuantitativeFilterService.ComputeBeta(index, index));
        Assert.Equal(2m, QuantitativeFilterService.ComputeBeta(doubled, index));

        Assert.Null(QuantitativeFilterService.ComputeBeta(index, null));
        Assert.Null(QuantitativeFilterService.ComputeBeta(index, Array.Empty<OhlcvBar>()));

        // A flat index has no variance, so the ratio is undefined rather than infinite.
        var flat = index.Select(bar => bar with { Close = 8_500m }).ToList();
        Assert.Null(QuantitativeFilterService.ComputeBeta(index, flat));

        // Fewer than 60 overlapping sessions is not a beta, it is a guess.
        Assert.Null(QuantitativeFilterService.ComputeBeta(
            index.Take(40).ToList(),
            index.Take(40).ToList()));
    }

    private static List<OhlcvBar> BuildVolumeBars(int upSessions, decimal upVolume, decimal downVolume)
    {
        var date = new DateOnly(2026, 1, 5);
        var price = 100m;
        var bars = new List<OhlcvBar> { new(date, price, price, price, price, 0) };

        for (var index = 1; index <= 20; index++)
        {
            var advancing = index <= upSessions;
            price += advancing ? 1m : -1m;
            bars.Add(new OhlcvBar(
                date.AddDays(index),
                price,
                price,
                price,
                price,
                (long)(advancing ? upVolume : downVolume)));
        }

        return bars;
    }

    [Fact]
    public void ComputePreScore_prices_confirmed_foreign_buying_above_unreported_flow()
    {
        var unreported = QuantitativeFilterService.ComputePreScore(
            100m, 98m, 95m, 90m, rsi: 55m, volumeRatio: 1.0m, avgTurnover20dPhp: 100_000_000m,
            netForeignTodayPhp: null, atr: 2m, support: 97m, resistance: 104m);

        var confirmedBuying = QuantitativeFilterService.ComputePreScore(
            100m, 98m, 95m, 90m, rsi: 55m, volumeRatio: 1.0m, avgTurnover20dPhp: 100_000_000m,
            netForeignTodayPhp: 8_000_000m, atr: 2m, support: 97m, resistance: 104m);

        var confirmedSelling = QuantitativeFilterService.ComputePreScore(
            100m, 98m, 95m, 90m, rsi: 55m, volumeRatio: 1.0m, avgTurnover20dPhp: 100_000_000m,
            netForeignTodayPhp: -8_000_000m, atr: 2m, support: 97m, resistance: 104m);

        // 8M on 100M of 20-day turnover is an 8% ratio, above the 5% "strong" bar.
        Assert.True(confirmedBuying > unreported);
        Assert.True(confirmedSelling < unreported);
    }

    [Fact]
    public void MergeRollingDisclosures_keeps_a_structural_trap_inside_the_window_and_caps_the_list()
    {
        var tradeDate = new DateOnly(2026, 11, 24);

        var current = new[]
        {
            new StockDisclosure(tradeDate, "Press release: ICT wins a terminal concession", "Disclosure")
        };

        var historical = new List<StockDisclosure>
        {
            new(tradeDate.AddDays(-30), "Stock Rights Offering of up to 10 billion pesos", "Disclosure"),
            new(tradeDate.AddDays(-400), "Annual report", "Disclosure")
        };

        for (var day = 1; day <= 8; day++)
        {
            historical.Add(new StockDisclosure(
                tradeDate.AddDays(-day),
                $"Routine 17-C quarterly report {day}",
                "Disclosure"));
        }

        var merged = QuantitativeFilterService.MergeRollingDisclosures(current, historical, tradeDate);

        Assert.InRange(merged.Count, 1, QuantitativeFilterService.MaxRollingDisclosures);
        Assert.Contains(merged, disclosure =>
            disclosure.Headline.Contains("Rights Offering", StringComparison.OrdinalIgnoreCase));

        // A filing older than the rolling window is dropped, not silently carried forward.
        Assert.DoesNotContain(merged, disclosure => disclosure.Headline == "Annual report");

        var dates = merged.Select(disclosure => disclosure.Date).ToList();
        Assert.Equal(dates.OrderByDescending(date => date), dates);
    }

    [Fact]
    public void MergeRollingDisclosures_deduplicates_the_same_filing_seen_twice()
    {
        var tradeDate = new DateOnly(2026, 11, 24);
        var filing = new StockDisclosure(tradeDate, "Cash dividend declaration", "Disclosure");

        var merged = QuantitativeFilterService.MergeRollingDisclosures(
            new[] { filing },
            new[] { filing with { Headline = "  cash dividend declaration  " } },
            tradeDate);

        Assert.Single(merged);
    }

    [Fact]
    public void ScopeDisclosures_limits_a_book_to_its_own_lookback_window()
    {
        var tradeDate = new DateOnly(2026, 11, 24);
        var disclosures = new[]
        {
            new StockDisclosure(tradeDate.AddDays(-2), "Board approves capex program", "Disclosure"),
            new StockDisclosure(tradeDate.AddDays(-30), "Follow-on offering priced", "Disclosure")
        };

        var week = QuantitativeFilterService.ScopeDisclosures(
            disclosures,
            tradeDate,
            HorizonProfile.For(SignalHorizon.WEEK).DisclosureLookbackDays);
        var year = QuantitativeFilterService.ScopeDisclosures(
            disclosures,
            tradeDate,
            HorizonProfile.For(SignalHorizon.YEAR).DisclosureLookbackDays);

        Assert.Single(week);
        Assert.Equal(2, year.Count);
        Assert.Empty(QuantitativeFilterService.ScopeDisclosures(null, tradeDate, 7));
    }

    private static MarketDataBatch BuildBatch(DateOnly tradeDate, params StockMarketData[] stocks) =>
        new(
            tradeDate,
            PseiClose: 8_500m,
            PseiSma50: 8_400m,
            PseiSma200: 8_100m,
            NetForeignFlowPhp: 1_200_000_000m,
            UsdPhp: 58.3m,
            MarketRegime: MarketRegime.BULLISH,
            RegimeNote: "Risk-on tape.",
            Stocks: stocks,
            Source: "unit-test");

    private static StockMarketData BuildStock(
        string ticker,
        IReadOnlyList<OhlcvBar> bars,
        long volume,
        decimal? netForeign,
        IReadOnlyList<StockDividendEvent>? dividends = null,
        IReadOnlyList<StockDisclosure>? disclosures = null) =>
        new(
            ticker,
            CompanyName: $"{ticker} Holdings",
            Sector: "Industrial",
            Bars: bars,
            NetForeignTodayPhp: netForeign,
            Disclosures: disclosures ?? Array.Empty<StockDisclosure>(),
            Dividends: dividends);

    [Fact]
    public void SelectDisclosureFanout_is_exactly_the_candidate_list_before_the_trap_screen()
    {
        var service = new QuantitativeFilterService();
        var metrics = new[]
        {
            BuildMetric("AAA", 90),
            BuildMetric("BBB", 70),
            BuildMetric("CCC", 80)
        };

        var candidates = service.SelectCandidates(metrics, SignalHorizon.WEEK, 2)
            .Select(metric => metric.Ticker)
            .ToList();

        // With nothing disqualified, the names the fan-out re-reads are the names that could
        // reach the book — reordering here would leave a candidate without its own filings.
        Assert.Equal(candidates, service.SelectDisclosureFanout(metrics, SignalHorizon.WEEK, 2));
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void SelectDisclosureFanout_skips_illiquid_names_and_honours_a_zero_limit()
    {
        var service = new QuantitativeFilterService();
        var illiquid = BuildRankedMetric(
            "ILLQ",
            99,
            avgTurnover: 1_000_000m,
            medianTurnover: 900_000m,
            activeDays: 20);
        var liquid = BuildMetric("LIQD", 10);

        Assert.Equal(
            new[] { "LIQD" },
            service.SelectDisclosureFanout(new[] { illiquid, liquid }, SignalHorizon.DAY, 25));

        // Zero is the rollback: no request is worth making, so nothing is selected.
        Assert.Empty(service.SelectDisclosureFanout(new[] { liquid }, SignalHorizon.DAY, 0));
    }

    private static StockDailyMetric BuildMetric(string ticker, int score) =>
        new()
        {
            TradeDate = new DateOnly(2026, 11, 24),
            Ticker = ticker,
            CompanyName = $"{ticker} Holdings",
            Sector = "Industrial",
            ClosePrice = 10m,
            ChangePct = 1m,
            AvgTurnover20dPhp = 25_000_000m,
            VolumeRatioVs20d = 1.1m,
            Rsi14 = 55m,
            Ema20 = 9.5m,
            Sma50 = 9m,
            Sma200 = 8m,
            Atr14 = 0.4m,
            SupportLevel = 9m,
            ResistanceLevel = 11m,
            NetForeignTodayPhp = 1_000_000m,
            QuantPreScore = score
        };

    private static StockDailyMetric BuildRankedMetric(
        string ticker,
        int score,
        decimal avgTurnover,
        decimal medianTurnover,
        int activeDays)
    {
        var metric = BuildMetric(ticker, score);
        metric.AvgTurnover20dPhp = avgTurnover;
        metric.MedianTurnover20dPhp = medianTurnover;
        metric.ActiveTradingDays20d = activeDays;

        return metric;
    }

    private static List<OhlcvBar> BuildTrendBars(
        decimal start,
        decimal dailyStep,
        int count,
        long volume = 2_000_000,
        long volumeIncrement = 1_000)
    {
        var bars = new List<OhlcvBar>(count);
        var date = new DateOnly(2026, 1, 5);
        var price = start;

        for (var index = 0; index < count; index++)
        {
            var open = price;
            var close = price + dailyStep;

            bars.Add(new OhlcvBar(
                date.AddDays(index),
                Open: open,
                High: Math.Max(open, close) + 0.5m,
                Low: Math.Min(open, close) - 0.5m,
                Close: close,
                Volume: volume + (volumeIncrement * index)));

            price = close;
        }

        return bars;
    }
}

