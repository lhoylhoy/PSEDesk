using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// Research is the only endpoint that answers for a ticker the daily catalog does not track,
/// so these tests pin the three paths it must keep apart: a usable live chart, the latest
/// stored session, and no data at all. They also pin the honesty flags the dialog prints
/// (live vs stored, catalog membership, the turnover floor) and the invariants that are
/// never negotiable: a tick-valid plan, a clamped DAY band, bounded odds and a
/// board-lot-sized ticket.
/// </summary>
public sealed class TickerResearchServiceTests
{
    [Fact]
    public async Task ResearchAsync_prices_a_live_chart_with_a_tick_valid_plan_and_a_ticket()
    {
        var bars = BuildOscillatingBars(260);
        var service = CreateService(new StubMarketDataProvider(BuildStock(bars), isTracked: true));

        var research = await service.ResearchAsync("  ict  ", SignalHorizon.WEEK);

        Assert.Equal("ICT", research.Ticker);
        Assert.Equal("live", research.DataSource);
        Assert.True(research.InCatalog);
        Assert.True(research.PassesLiquidityFloor);
        Assert.Equal(bars[^1].Date, research.AsOfDate);
        Assert.Equal(nameof(SignalHorizon.WEEK), research.Horizon);
        Assert.Empty(research.TrapFlags);

        Assert.Equal(PseBoardLotService.RoundToValidTick(research.Plan.EntryPrice), research.Plan.EntryPrice);
        Assert.Equal(PseBoardLotService.RoundToValidTick(research.Plan.StopLoss), research.Plan.StopLoss);
        Assert.Equal(PseBoardLotService.RoundToValidTick(research.Plan.TargetPrice), research.Plan.TargetPrice);
        Assert.True(research.Plan.StopLoss < research.Plan.EntryPrice);
        Assert.True(research.Plan.TargetPrice > research.Plan.EntryPrice);
        Assert.True(research.Plan.NetRiskRewardRatio > 0m);
        Assert.True(research.Plan.AtrStopDistance > 0m);

        Assert.InRange(research.WinProbability, 0.05m, 0.95m);
        Assert.Contains(
            research.EdgeVerdict,
            new[] { nameof(EdgeVerdict.TRADE), nameof(EdgeVerdict.SMALL_SIZE), nameof(EdgeVerdict.WATCH_ONLY) });
        Assert.Contains("invalidates the setup", research.BearCase, StringComparison.Ordinal);

        var ticket = Assert.IsType<OrderTicketDto>(research.OrderTicket);
        Assert.Equal(PseBoardLotService.GetBoardLotSize(research.Plan.EntryPrice), ticket.BoardLotSize);
        Assert.Equal(
            PseBoardLotService.ComputeBreakEvenStopPrice(research.Plan.EntryPrice),
            ticket.BreakEvenStopPrice);
        Assert.True(ticket.BreakEvenStopPrice > research.Plan.EntryPrice);

        if (research.EdgeVerdict == nameof(EdgeVerdict.WATCH_ONLY))
        {
            // A study, not an order: no shares can be risk-sized for a name the desk would not buy.
            Assert.Equal(0, ticket.Shares);
            Assert.Equal(0, ticket.Lots);
            Assert.Contains(ticket.Warnings, warning => warning.Contains("WATCH_ONLY", StringComparison.Ordinal));
        }
        else
        {
            Assert.True(ticket.Shares > 0);
            Assert.Equal(0, ticket.Shares % ticket.BoardLotSize);
            Assert.Equal(ticket.Shares / ticket.BoardLotSize, ticket.Lots);
            Assert.True(ticket.BuyOutlayPhp > 0m);
            Assert.True(ticket.NetRiskPhp > 0m);
            Assert.True(ticket.NetRewardPhp > 0m);
            Assert.False(ticket.LiquidityCapped);
        }
    }

    [Fact]
    public async Task ResearchAsync_clamps_a_DAY_plan_inside_the_static_band()
    {
        var bars = BuildOscillatingBars(260);
        var service = CreateService(new StubMarketDataProvider(BuildStock(bars), isTracked: true));

        var research = await service.ResearchAsync("ICT", SignalHorizon.DAY);

        var limits = PseBoardLotService.GetStaticPriceLimits(research.Metrics.ClosePrice);

        Assert.True(research.Plan.StopLoss >= limits.StaticFloor);
        Assert.True(research.Plan.TargetPrice <= limits.StaticCeiling);
        Assert.True(research.Plan.StopLoss < research.Plan.EntryPrice);
        Assert.True(research.Plan.TargetPrice > research.Plan.EntryPrice);
    }

    [Fact]
    public async Task ResearchAsync_uses_the_latest_stored_session_when_the_feed_cannot_answer()
    {
        var repository = new StubSignalRepository
        {
            LatestSnapshot = new DailyMarketSnapshot { TradeDate = new DateOnly(2026, 11, 24) },
            StoredMetric = BuildStoredMetric(new DateOnly(2026, 11, 24), close: 812m)
        };
        var service = CreateService(new NonResearchMarketDataProvider(), repository);

        var research = await service.ResearchAsync("sm", SignalHorizon.MONTH);

        Assert.Equal("SM", research.Ticker);
        Assert.Equal("stored", research.DataSource);
        Assert.False(research.InCatalog);
        Assert.Equal(new DateOnly(2026, 11, 24), research.AsOfDate);
        Assert.Equal(812m, research.Metrics.ClosePrice);
        Assert.True(research.PassesLiquidityFloor);
        Assert.Equal(PseBoardLotService.RoundToValidTick(812m), research.Plan.EntryPrice);
        Assert.Contains(
            research.EdgeVerdict,
            new[] { nameof(EdgeVerdict.TRADE), nameof(EdgeVerdict.SMALL_SIZE), nameof(EdgeVerdict.WATCH_ONLY) });
    }

    [Fact]
    public async Task ResearchAsync_ignores_a_live_series_too_short_to_score()
    {
        var repository = new StubSignalRepository
        {
            LatestSnapshot = new DailyMarketSnapshot { TradeDate = new DateOnly(2026, 11, 24) },
            StoredMetric = BuildStoredMetric(new DateOnly(2026, 11, 24), close: 812m)
        };
        var service = CreateService(
            new StubMarketDataProvider(BuildStock(BuildOscillatingBars(40)), isTracked: true),
            repository);

        var research = await service.ResearchAsync("ICT", SignalHorizon.WEEK);

        // 40 sessions cannot carry a 200-day regime, so the stored session wins; the service
        // never pads the series to reach the minimum bar count.
        Assert.Equal("stored", research.DataSource);
        Assert.Equal(new DateOnly(2026, 11, 24), research.AsOfDate);
        Assert.Equal(812m, research.Metrics.ClosePrice);
    }

    [Fact]
    public async Task ResearchAsync_explains_that_nothing_is_available()
    {
        var service = CreateService(new NonResearchMarketDataProvider());

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => service.ResearchAsync("ZZZ", SignalHorizon.WEEK));

        Assert.Contains("ZZZ", error.Message, StringComparison.Ordinal);
        Assert.Contains("no live chart", error.Message, StringComparison.Ordinal);
        Assert.Contains("no stored session", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResearchAsync_rejects_a_blank_ticker()
    {
        var service = CreateService(new NonResearchMarketDataProvider());

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => service.ResearchAsync("   ", SignalHorizon.WEEK));

        Assert.Contains("Provide a PSE ticker", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResearchAsync_prices_the_index_relative_factors_from_the_psei_series()
    {
        var bars = BuildOscillatingBars(260);
        var provider = new StubMarketDataProvider(BuildStock(bars), isTracked: true) { IndexBars = bars };
        var service = CreateService(provider);

        var withIndex = await service.ResearchAsync("ICT", SignalHorizon.WEEK);

        // Relative strength and beta need the index: the single-name path now asks for the
        // PSEi series instead of silently reporting those factors as unreported.
        Assert.NotNull(withIndex.Metrics.RelativeStrength63d);
        Assert.Equal(1m, withIndex.Metrics.Beta252d);

        var withoutIndex = await CreateService(
            new StubMarketDataProvider(BuildStock(bars), isTracked: true)).ResearchAsync("ICT", SignalHorizon.WEEK);

        Assert.Null(withoutIndex.Metrics.Beta252d);
    }

    [Fact]
    public async Task ResearchAsync_grades_the_sector_dial_from_the_last_stored_rotation()
    {
        var bars = BuildOscillatingBars(260);

        var withoutRotation = await CreateService(
            new StubMarketDataProvider(BuildStock(bars), isTracked: true)).ResearchAsync("ICT", SignalHorizon.WEEK);

        var leading = new[]
        {
            new SectorRotationDto("Industrials", 3, 4.5m, 100m, 100m, 2.5m, 88m, SectorQuadrant.Leading)
        };
        var repository = new StubSignalRepository
        {
            LatestSnapshot = new DailyMarketSnapshot
            {
                TradeDate = new DateOnly(2026, 11, 24),
                SectorRotationJson = MarketDataMapper.SerializeSectorRotation(leading)
            }
        };

        var withRotation = await CreateService(
            new StubMarketDataProvider(BuildStock(bars), isTracked: true),
            repository).ResearchAsync("ICT", SignalHorizon.WEEK);

        Assert.Equal(
            0.02m,
            decimal.Round(withRotation.WinProbability - withoutRotation.WinProbability, 4));
    }

    [Fact]
    public void GetCatalog_republishes_the_provider_catalog_untouched()
    {
        var catalog = new[]
        {
            new CatalogStockDto("JFC", "Jollibee Foods Corporation", "Consumer"),
            new CatalogStockDto("BDO", "BDO Unibank, Inc.", "Financials")
        };
        var service = CreateService(new StubMarketDataProvider(BuildStock(BuildOscillatingBars(260)), isTracked: true)
        {
            Catalog = catalog
        });

        var republished = service.GetCatalog();

        Assert.Equal(catalog, republished);
    }

    [Fact]
    public void GetCatalog_is_empty_when_the_provider_tracks_no_names()
    {
        var service = CreateService(new NonResearchMarketDataProvider());

        Assert.Empty(service.GetCatalog());
    }

    [Fact]
    public async Task ResearchAsync_resolves_an_unambiguous_company_name_to_its_ticker()
    {
        var provider = new StubMarketDataProvider(BuildStock(BuildOscillatingBars(260)), isTracked: true)
        {
            Catalog = new[]
            {
                new CatalogStockDto("JFC", "Jollibee Foods Corporation", "Consumer"),
                new CatalogStockDto("BDO", "BDO Unibank, Inc.", "Financials")
            }
        };
        var service = CreateService(provider);

        // Everyday readers type the company, not the symbol. The name is resolved before the
        // feed is called, so the plan, ticket and labels all describe JFC — never "JOLLIBEE".
        var research = await service.ResearchAsync("  jollibee  ", SignalHorizon.WEEK);

        Assert.Equal("JFC", research.Ticker);
        Assert.Equal("JFC", provider.LastRequestedSymbol);
        Assert.True(research.InCatalog);
    }

    [Fact]
    public async Task ResearchAsync_lets_a_direct_ticker_win_over_a_longer_company_name()
    {
        var provider = new StubMarketDataProvider(BuildStock(BuildOscillatingBars(260)), isTracked: true)
        {
            Catalog = new[]
            {
                new CatalogStockDto("SM", "SM Investments Corporation", "Holding Firms"),
                new CatalogStockDto("SMPH", "SM Prime Holdings, Inc.", "Property")
            }
        };
        var service = CreateService(provider);

        var research = await service.ResearchAsync("sm", SignalHorizon.WEEK);

        Assert.Equal("SM", research.Ticker);
        Assert.Equal("SM", provider.LastRequestedSymbol);
    }

    [Fact]
    public async Task ResearchAsync_never_guesses_between_ambiguous_company_names()
    {
        var provider = new StubMarketDataProvider(BuildStock(BuildOscillatingBars(260)), isTracked: true)
        {
            Catalog = new[]
            {
                new CatalogStockDto("BPI", "Bank of the Philippine Islands", "Financials"),
                new CatalogStockDto("SECB", "Security Bank Corporation", "Financials")
            }
        };
        var service = CreateService(provider);

        // "bank" matches two names, so the text passes through unchanged rather than pricing
        // whichever row happened to sort first.
        var research = await service.ResearchAsync("bank", SignalHorizon.WEEK);

        Assert.Equal("BANK", research.Ticker);
        Assert.Equal("BANK", provider.LastRequestedSymbol);
        Assert.False(research.InCatalog);
    }

    [Fact]
    public async Task ResearchAsync_attaches_a_tick_valid_wave_plan_to_every_answer()
    {
        var service = CreateService(new StubMarketDataProvider(BuildStock(BuildOscillatingBars(260)), isTracked: true));

        var research = await service.ResearchAsync("ICT", SignalHorizon.WEEK);

        var plan = Assert.IsType<WaveTradePlanDto>(research.WavePlan);
        Assert.Equal(research.Plan.EntryPrice, plan.IdealBuyPrice);
        Assert.Equal(research.Plan.StopLoss, plan.CutLossPrice);
        Assert.Equal(research.Plan.TargetPrice, plan.TargetPrice);
        Assert.True(plan.MaxChasePrice >= plan.IdealBuyPrice);
        Assert.True(plan.MaxChasePrice <= plan.TargetPrice);
        Assert.True(plan.IdealBuyPrice < plan.BreakEvenStopPrice);
        Assert.True(plan.BreakEvenStopPrice <= plan.LockBreakEvenTriggerPrice);
        Assert.True(plan.LockBreakEvenTriggerPrice <= plan.TrailWaveTriggerPrice);
        Assert.True(plan.TrailWaveTriggerPrice <= plan.TargetPrice);

        foreach (var price in new[]
                 {
                     plan.IdealBuyPrice,
                     plan.CutLossPrice,
                     plan.BreakEvenStopPrice,
                     plan.MaxChasePrice,
                     plan.LockBreakEvenTriggerPrice,
                     plan.TrailWaveTriggerPrice
                 })
        {
            Assert.Equal(PseBoardLotService.RoundToValidTick(price), price);
        }
    }

    [Fact]
    public async Task ResearchAsync_answers_a_position_check_against_the_owners_average_cost()
    {
        var service = CreateService(new StubMarketDataProvider(BuildStock(BuildOscillatingBars(260)), isTracked: true));
        var priced = await service.ResearchAsync("ICT", SignalHorizon.WEEK);
        var cost = decimal.Round(priced.Metrics.ClosePrice * 0.95m, 2);

        var owned = await service.ResearchAsync(
            "ICT",
            SignalHorizon.WEEK,
            averageCost: cost,
            shares: 200);

        var advice = Assert.IsType<PositionAdviceDto>(owned.PositionAdvice);
        Assert.Equal("ICT", advice.Ticker);
        Assert.Equal(PseBoardLotService.RoundToValidTick(cost), advice.AverageCost);
        Assert.Equal(priced.Metrics.ClosePrice, advice.CurrentPrice);
        Assert.Equal(200, advice.Shares);
        Assert.True(advice.SuggestedStopPrice < advice.CurrentPrice);
        Assert.True(advice.SuggestedTargetPrice > advice.AverageCost);
        Assert.True(advice.BreakEvenPrice > advice.AverageCost);
        Assert.True(advice.NetReturnPct > 0m);
        Assert.NotNull(advice.UnrealizedNetPnlPhp);
        Assert.InRange(advice.UnrealizedR, -5m, 10m);
        Assert.Equal(100, advice.RecommendedTrimShares);
        Assert.Contains(
            advice.Action,
            new[] { "HOLD", "MOVE_STOP_TO_BREAKEVEN", "TRAIL_STOP_EMA20", "TARGET_REACHED" });

        foreach (var price in new[]
                 {
                     advice.SuggestedStopPrice,
                     advice.BreakEvenPrice,
                     advice.SuggestedTargetPrice
                 })
        {
            Assert.Equal(PseBoardLotService.RoundToValidTick(price), price);
        }
    }

    [Fact]
    public async Task ResearchAsync_leaves_the_position_check_empty_without_an_average_cost()
    {
        var service = CreateService(new StubMarketDataProvider(BuildStock(BuildOscillatingBars(260)), isTracked: true));

        var unowned = await service.ResearchAsync("ICT", SignalHorizon.WEEK);
        var sharesOnly = await service.ResearchAsync("ICT", SignalHorizon.WEEK, shares: 100);

        Assert.Null(unowned.PositionAdvice);
        Assert.Null(sharesOnly.PositionAdvice);
    }

    private static TickerResearchService CreateService(
        IMarketDataProvider marketData,
        StubSignalRepository? repository = null)
    {
        repository ??= new StubSignalRepository();

        return new TickerResearchService(
            marketData,
            repository,
            new QuantitativeFilterService(),
            new ProbabilisticEdgeService(),
            new PortfolioService(new FakePortfolioRepository(), repository));
    }

    private static StockMarketData BuildStock(IReadOnlyList<OhlcvBar> bars) =>
        new(
            "ICT",
            CompanyName: "International Container Terminal Services, Inc.",
            Sector: "Industrials",
            Bars: bars,
            NetForeignTodayPhp: null,
            Disclosures: Array.Empty<StockDisclosure>());

    private static StockDailyMetric BuildStoredMetric(DateOnly tradeDate, decimal close) =>
        new()
        {
            TradeDate = tradeDate,
            Ticker = "SM",
            CompanyName = "SM Investments Corporation",
            Sector = "Holding Firms",
            ClosePrice = close,
            ChangePct = 0.6m,
            AvgTurnover20dPhp = 250_000_000m,
            VolumeRatioVs20d = 1.1m,
            Rsi14 = 57m,
            Ema20 = close - 4m,
            Sma50 = close - 9m,
            Sma150 = close - 14m,
            Sma200 = close - 18m,
            Atr14 = 12m,
            SupportLevel = close - 15m,
            ResistanceLevel = close + 20m,
            QuantPreScore = 78,
            WeinsteinStage = "STAGE_2",
            RelativeStrength63d = 3.4m,
            VolatilityContractionRatio = 0.82m,
            AtrPct = 1.48m,
            EmpiricalWinRate = 0.46m,
            TrailingDividendPerShare = 15m,
            TrailingDividendYieldPct = 1.85m,
            LastExDividendDate = new DateOnly(2026, 9, 18),
            OpenPrice = close - 2m,
            HighPrice = close + 6m,
            LowPrice = close - 8m
        };

    /// <summary>
    /// A choppy but rising tape (+0.8, +0.4, -0.6 per session) so RSI and the trend averages
    /// are both real, with PHP 50M of daily turnover to clear the desk's PHP 5M floor.
    /// </summary>
    private static List<OhlcvBar> BuildOscillatingBars(int barCount)
    {
        var bars = new List<OhlcvBar>(barCount);
        var date = new DateOnly(2025, 1, 6);
        var price = 50m;

        for (var index = 0; index < barCount; index++)
        {
            var close = price + ((index % 3) switch { 0 => 0.8m, 1 => 0.4m, _ => -0.6m });

            bars.Add(new OhlcvBar(
                date.AddDays(index),
                Open: price,
                High: Math.Max(price, close) + 2.0m,
                Low: Math.Min(price, close) - 2.0m,
                Close: close,
                Volume: 1_000_000));

            price = close;
        }

        return bars;
    }

    /// <summary>Live feed stub: one symbol, plus whatever catalog answer the test needs.</summary>
    private sealed class StubMarketDataProvider : IMarketDataProvider
    {
        private readonly StockMarketData _stock;
        private readonly bool _isTracked;

        public StubMarketDataProvider(StockMarketData stock, bool isTracked)
        {
            _stock = stock;
            _isTracked = isTracked;
        }

        /// <summary>The PSEi series the provider can serve, if any.</summary>
        public IReadOnlyList<OhlcvBar> IndexBars { get; init; } = Array.Empty<OhlcvBar>();

        /// <summary>The tracked catalog the provider publishes, empty when the test does not need one.</summary>
        public IReadOnlyList<CatalogStockDto> Catalog { get; init; } = Array.Empty<CatalogStockDto>();

        /// <summary>The last symbol handed to the feed, so name resolution is observable.</summary>
        public string? LastRequestedSymbol { get; private set; }

        public Task<MarketDataBatch> FetchAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Research never asks for a full batch.");

        public Task<StockMarketData?> FetchSingleStockAsync(
            string symbol,
            DateOnly tradeDate,
            CancellationToken cancellationToken = default)
        {
            LastRequestedSymbol = symbol;
            return Task.FromResult<StockMarketData?>(_stock);
        }

        public Task<IReadOnlyList<OhlcvBar>> FetchIndexBarsAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(IndexBars);

        public IReadOnlyList<CatalogStockDto> GetCatalog() => Catalog;

        public bool IsTracked(string symbol) =>
            Catalog.Count > 0
                ? Catalog.Any(stock => string.Equals(stock.Ticker, symbol, StringComparison.OrdinalIgnoreCase))
                : _isTracked;
    }

    /// <summary>
    /// A provider with no single-symbol support and no catalog. The inherited
    /// <see cref="IMarketDataProvider.FetchSingleStockAsync"/> throws, which is the
    /// "feed cannot answer" path research must survive by falling back to stored rows.
    /// </summary>
    private sealed class NonResearchMarketDataProvider : IMarketDataProvider
    {
        public Task<MarketDataBatch> FetchAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("No daily batch in these tests.");
    }

    /// <summary>Repository stub: only the two reads research performs are implemented.</summary>
    private sealed class StubSignalRepository : ISignalRepository
    {
        public DailyMarketSnapshot? LatestSnapshot { get; set; }

        public StockDailyMetric? StoredMetric { get; set; }

        public Task<DailyMarketSnapshot?> GetSnapshotAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DailyMarketSnapshot?>(null);

        public Task<DailyMarketSnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LatestSnapshot);

        public Task<IReadOnlyList<StockDailyMetric>> GetMetricsAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StockDailyMetric>>(Array.Empty<StockDailyMetric>());

        public Task<StockDailyMetric?> GetMetricAsync(
            DateOnly tradeDate,
            string ticker,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(StoredMetric);

        public Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DateOnly>>(Array.Empty<DateOnly>());

        public Task<IReadOnlyList<DateOnly>> GetRunDatesAsync(
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DateOnly>>(Array.Empty<DateOnly>());

        public Task<SignalRun?> GetLatestRunAsync(
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SignalRun?>(null);

        public Task<SignalRun?> GetRunAsync(
            DateOnly runDate,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SignalRun?>(null);

        public Task<SignalRun?> GetPreviousRunAsync(
            DateOnly beforeDate,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SignalRun?>(null);

        public Task<IReadOnlyList<SignalRun>> GetRunsForTickerAsync(
            string ticker,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SignalRun>>(Array.Empty<SignalRun>());

        public Task<IReadOnlyList<SignalRun>> GetAllRunsAsync(
            SignalHorizon? horizon = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SignalRun>>(Array.Empty<SignalRun>());

        public Task<IReadOnlyList<StockDailyMetric>> GetMetricsRangeAsync(
            DateOnly fromDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StockDailyMetric>>(Array.Empty<StockDailyMetric>());

        public Task UpdateResolvedOutcomesAsync(
            IReadOnlyList<SignalItem> items,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveMarketDataAsync(
            MarketDataBatch batch,
            IReadOnlyList<StockDailyMetric> metrics,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveSignalRunAsync(SignalRun run, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// In-memory book with the same defaults the API creates on first read, so the ticket is
    /// sized against PHP 1,000,000 of equity and the standard 1% / 20% / 6% caps.
    /// </summary>
    private sealed class FakePortfolioRepository : IPortfolioRepository
    {
        private int _nextId = 1;

        public PortfolioAccount Account { get; } = new()
        {
            Id = 1,
            StartingCapitalPhp = 1_000_000m,
            CashBalancePhp = 1_000_000m,
            MaxRiskPerTradePct = 1m,
            SmallSizeRiskMultiplier = 0.5m,
            MaxPositionPct = 20m,
            MaxPortfolioHeatPct = 6m
        };

        public List<PortfolioTrade> Trades { get; } = new();

        public Task<PortfolioAccount> GetOrCreateAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);

        public Task<PortfolioAccount> UpdateAccountAsync(
            PortfolioAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);

        public Task<IReadOnlyList<PortfolioTrade>> GetOpenTradesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PortfolioTrade>>(
                Trades.Where(trade => trade.Status == PortfolioTradeStatus.OPEN).ToList());

        public Task<IReadOnlyList<PortfolioTrade>> GetClosedTradesAsync(
            int take = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PortfolioTrade>>(
                Trades
                    .Where(trade => trade.Status == PortfolioTradeStatus.CLOSED)
                    .OrderByDescending(trade => trade.ClosedDate)
                    .Take(take)
                    .ToList());

        public Task<PortfolioTrade?> GetTradeAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Trades.FirstOrDefault(trade => trade.Id == id));

        public Task<PortfolioTrade> AddTradeAsync(
            PortfolioTrade trade,
            CancellationToken cancellationToken = default)
        {
            trade.Id = _nextId++;
            Trades.Add(trade);
            return Task.FromResult(trade);
        }

        public Task<PortfolioTrade> UpdateTradeAsync(
            PortfolioTrade trade,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(trade);
    }
}
