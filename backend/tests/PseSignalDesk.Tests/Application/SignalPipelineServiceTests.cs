using Microsoft.Extensions.Logging.Abstractions;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Tests.Application;

public sealed class SignalPipelineServiceTests
{
    private static readonly DateOnly DayOne = new(2026, 11, 23);
    private static readonly DateOnly DayTwo = new(2026, 11, 24);

    [Fact]
    public async Task RunAsync_recomputes_drift_and_classifies_drops_deterministically()
    {
        var repository = new FakeSignalRepository();
        repository.AddRun(BuildPreviousRun());
        repository.Snapshots[DayTwo] = BuildSnapshot(DayTwo);
        repository.Metrics[DayTwo] = new List<StockDailyMetric>
        {
            BuildMetric(DayTwo, "AAA", close: 104m, score: 90),
            BuildMetric(DayTwo, "DDD", close: 11m, score: 80),
            BuildMetric(DayTwo, "BBB", close: 46m, score: 70),
            BuildMetric(DayTwo, "CCC", close: 27m, score: 60)
        };

        var engine = new FakeSignalEngine(new OllamaSignalOutput
        {
            Top10 =
            {
                BuildPick("AAA", entry: 100m, stop: 95m, target: 110m),
                BuildPick("DDD", entry: 10m, stop: 9m, target: 13m)
            },
            DroppedFromYesterday =
            {
                new OllamaDrop
                {
                    Ticker = "BBB",
                    PreviousRank = 2,
                    DropCategory = "DISPLACED",
                    Reason = "The model called this displaced."
                }
            }
        });

        var pipeline = CreatePipeline(repository, engine);

        var dto = await pipeline.RunAsync(DayTwo, SignalHorizon.WEEK);

        Assert.Equal(2, dto.Items.Count);

        var held = dto.Items.Single(item => item.Ticker == "AAA");
        Assert.Equal("HELD", held.DriftStatus);
        Assert.Equal(1, held.PreviousRank);

        var upgraded = dto.Items.Single(item => item.Ticker == "DDD");
        Assert.Equal("UPGRADED", upgraded.DriftStatus);
        Assert.Equal(4, upgraded.PreviousRank);

        Assert.DoesNotContain(dto.Items, item => item.Ticker is "BBB" or "CCC");

        var invalidated = dto.Drops.Single(drop => drop.Ticker == "BBB");
        Assert.Equal("INVALIDATED", invalidated.DropCategory);
        Assert.Contains("prior stop", invalidated.Reason, StringComparison.Ordinal);

        var targetHit = dto.Drops.Single(drop => drop.Ticker == "CCC");
        Assert.Equal("TARGET_HIT", targetHit.DropCategory);
        Assert.Contains("prior target", targetHit.Reason, StringComparison.Ordinal);

        Assert.Equal(1, repository.SaveRunCalls);
        Assert.NotNull(repository.SavedRun);
        Assert.Equal("FakeEngine", engine.EngineName);

        // The quant table the model was shown is persisted with every pick, so the scorecard can
        // later ask whether the model's order beat the screen's own.
        var savedAaa = repository.SavedRun!.Items.Single(item => item.Ticker == "AAA");
        Assert.Equal(90, savedAaa.QuantPreScore);
        Assert.Equal(1, savedAaa.QuantRank);

        var savedDdd = repository.SavedRun.Items.Single(item => item.Ticker == "DDD");
        Assert.Equal(80, savedDdd.QuantPreScore);
        Assert.Equal(2, savedDdd.QuantRank);

        // Provenance travels with the stored run: which model, which prompt, what it cost.
        Assert.Equal("FakeEngine", repository.SavedRun.EngineName);
        Assert.Equal("fake-model", repository.SavedRun.ModelName);
        Assert.Equal("fake-prompt-hash", repository.SavedRun.PromptHash);
        Assert.Equal(4_200, repository.SavedRun.ModelLatencyMs);
        Assert.True(repository.SavedRun.SchemaConstrained);

        // ...and the same provenance is on the API view of the run.
        Assert.NotNull(dto.ModelProvenance);
        Assert.Equal("fake-model", dto.ModelProvenance!.Model);
    }

    [Fact]
    public async Task RunAsync_fails_instead_of_topping_up_when_the_model_returns_only_unknown_tickers()
    {
        var repository = new FakeSignalRepository();
        repository.Snapshots[DayTwo] = BuildSnapshot(DayTwo);
        repository.Metrics[DayTwo] = Enumerable.Range(0, 12)
            .Select(index => BuildMetric(DayTwo, $"T{index:00}", close: 10m + index, score: 100 - index))
            .ToList();

        // ZZZ is not in the liquid candidate set, so the pick is discarded. With no
        // quant-only top-up left, the run must fail rather than invent a buy list.
        var engine = new FakeSignalEngine(new OllamaSignalOutput
        {
            Top10 = { BuildPick("ZZZ", entry: 10m, stop: 9m, target: 12m) }
        });

        var pipeline = CreatePipeline(repository, engine);

        await Assert.ThrowsAsync<SignalGenerationException>(
            () => pipeline.RunAsync(DayTwo, SignalHorizon.WEEK));

        Assert.Equal(0, repository.SaveRunCalls);
    }

    [Fact]
    public async Task RunAsync_discards_model_picks_that_come_back_without_a_thesis()
    {
        var repository = new FakeSignalRepository();
        repository.Snapshots[DayTwo] = BuildSnapshot(DayTwo);
        repository.Metrics[DayTwo] = new List<StockDailyMetric>
        {
            BuildMetric(DayTwo, "AAA", close: 104m, score: 90),
            BuildMetric(DayTwo, "BBB", close: 46m, score: 70)
        };

        var thesisless = BuildPick("AAA", entry: 100m, stop: 95m, target: 110m);
        thesisless.Thesis = "   ";

        var engine = new FakeSignalEngine(new OllamaSignalOutput
        {
            Top10 =
            {
                thesisless,
                BuildPick("BBB", entry: 45m, stop: 43m, target: 52m)
            }
        });

        var pipeline = CreatePipeline(repository, engine);

        var dto = await pipeline.RunAsync(DayTwo, SignalHorizon.WEEK);

        var item = Assert.Single(dto.Items);
        Assert.Equal("BBB", item.Ticker);
        Assert.Equal(1, item.Rank);
    }

    [Fact]
    public async Task RunAsync_throws_when_the_provider_has_no_market_data()
    {
        var repository = new FakeSignalRepository();
        var provider = new FakeMarketDataProvider(MarketDataBatch.Empty(DayTwo));
        var pipeline = CreatePipeline(repository, new FakeSignalEngine(new OllamaSignalOutput()), provider);

        await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => pipeline.RunAsync(DayTwo, SignalHorizon.WEEK));

        Assert.Equal(0, repository.SaveRunCalls);
    }

    [Fact]
    public async Task RunAsync_persists_freshly_computed_metrics_when_no_snapshot_exists()
    {
        var repository = new FakeSignalRepository();
        var provider = new FakeMarketDataProvider(BuildBatch(DayTwo, "ICT", "BDO", "SM"));
        var engine = new FakeSignalEngine(new OllamaSignalOutput
        {
            Top10 =
            {
                BuildPick("ICT", entry: 100m, stop: 95m, target: 110m),
                BuildPick("BDO", entry: 100m, stop: 95m, target: 110m),
                BuildPick("SM", entry: 100m, stop: 95m, target: 110m)
            }
        });
        var pipeline = CreatePipeline(repository, engine, provider);

        var dto = await pipeline.RunAsync(DayTwo, SignalHorizon.WEEK);

        Assert.Equal(3, dto.Items.Count);
        Assert.Equal(1, repository.SaveMarketDataCalls);
        Assert.Equal(1, repository.SaveRunCalls);
        Assert.True(provider.WasCalled);
        Assert.NotNull(engine.LastRequest);
        Assert.Equal(3, engine.LastRequest!.Candidates.Count);
    }

    [Fact]
    public async Task RunAsync_disqualifies_a_name_whose_rights_offering_only_the_company_page_names()
    {
        // The market-wide Edge table has no company column, so a filing whose subject does not
        // name its issuer reaches the trap screen attributable to nobody. The per-company re-read
        // is what attaches it — and the name must be gone before the model ever sees the list.
        var repository = new FakeSignalRepository();
        var provider = new FakeMarketDataProvider(
            BuildBatch(DayTwo, "ICT", "BDO", "SM"),
            new Dictionary<string, IReadOnlyList<StockDisclosure>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ICT"] = new[] { new StockDisclosure(DayTwo, "Stock Rights Offering", "Disclosure") }
            });
        var engine = new FakeSignalEngine(new OllamaSignalOutput
        {
            Top10 =
            {
                BuildPick("BDO", entry: 100m, stop: 95m, target: 110m),
                BuildPick("SM", entry: 100m, stop: 95m, target: 110m)
            }
        });
        var pipeline = CreatePipeline(repository, engine, provider);

        var dto = await pipeline.RunAsync(DayTwo, SignalHorizon.WEEK);

        // The trap was found before the screen ran: the fan-out covered a name it was about to drop.
        Assert.NotNull(provider.RequestedTickers);
        Assert.Contains("ICT", provider.RequestedTickers!);

        Assert.NotNull(engine.LastRequest);
        Assert.DoesNotContain(engine.LastRequest!.Candidates, candidate => candidate.Ticker == "ICT");
        Assert.Contains(engine.LastRequest.Candidates, candidate => candidate.Ticker == "BDO");
        Assert.Equal(2, dto.Items.Count);
    }

    [Fact]
    public async Task RunAsync_clamps_a_day_plan_inside_the_static_band_and_keeps_only_retrieved_citations()
    {
        var repository = new FakeSignalRepository();
        repository.Snapshots[DayTwo] = BuildSnapshot(DayTwo);
        repository.Metrics[DayTwo] = new List<StockDailyMetric>
        {
            BuildMetric(DayTwo, "AAA", close: 100m, score: 90)
        };

        // A 90% target on a ₱100 close breaches the +50% static ceiling, and only one of
        // the two cited references was actually retrieved, so the other must be dropped.
        var pick = BuildPick("AAA", entry: 101m, stop: 97m, target: 190m);
        pick.CitedRules = new List<string>
        {
            "pse_rules#static-price-band",
            "pse_rules#an-invented-section"
        };

        var engine = new FakeSignalEngine(new OllamaSignalOutput { Top10 = { pick } });
        var knowledge = new FakeKnowledgeService(
            new KnowledgeSnippet(
                "pse_rules",
                "Static price band",
                "pse_rules#static-price-band",
                "The exchange enforces a +50% ceiling and a -30% floor.",
                new[] { "DAY", "WEEK" },
                new[] { "RULES" },
                0.51d));

        var dto = await CreatePipeline(repository, engine, knowledge: knowledge)
            .RunAsync(DayTwo, SignalHorizon.DAY);

        var item = Assert.Single(dto.Items);

        Assert.Equal(1, knowledge.RetrieveCalls);
        Assert.Equal(SignalHorizon.DAY, knowledge.LastQuery!.Horizon);
        Assert.True(item.TargetPrice <= 150m, $"target {item.TargetPrice} escaped the static ceiling.");
        Assert.True(item.EntryPrice > item.StopLoss);
        Assert.Equal(new[] { "pse_rules#static-price-band" }, item.Citations);
        // ₱100,000 at ₱101 buys 990 shares, which is 99 board lots of 10.
        Assert.Equal(10, item.BoardLotSize);
        Assert.Equal(990, item.BoardLotShares);
        Assert.NotNull(engine.LastRequest);
        Assert.Single(engine.LastRequest!.ReferencePlaybooks!);
    }

    [Fact]
    public async Task RunAsync_survives_a_knowledge_base_without_playbooks()
    {
        var repository = new FakeSignalRepository();
        repository.Snapshots[DayTwo] = BuildSnapshot(DayTwo);
        repository.Metrics[DayTwo] = new List<StockDailyMetric>
        {
            BuildMetric(DayTwo, "AAA", close: 100m, score: 90)
        };

        var pick = BuildPick("AAA", entry: 100m, stop: 95m, target: 112m);
        pick.CitedRules = new List<string> { "pse_rules#invented" };

        var engine = new FakeSignalEngine(new OllamaSignalOutput { Top10 = { pick } });
        var pipeline = CreatePipeline(repository, engine, knowledge: new FakeKnowledgeService());

        var dto = await pipeline.RunAsync(DayTwo, SignalHorizon.WEEK);

        var item = Assert.Single(dto.Items);
        Assert.Empty(item.Citations);
        Assert.Empty(engine.LastRequest!.ReferencePlaybooks!);
    }

    [Fact]
    public async Task RunAsync_assigns_server_owned_odds_a_verdict_and_a_bear_case()
    {
        var repository = new FakeSignalRepository();
        repository.Snapshots[DayTwo] = BuildSnapshot(DayTwo);
        repository.Metrics[DayTwo] = new List<StockDailyMetric>
        {
            BuildMetric(DayTwo, "AAA", close: 104m, score: 90)
        };

        var engine = new FakeSignalEngine(new OllamaSignalOutput
        {
            Top10 = { BuildPick("AAA", entry: 100m, stop: 95m, target: 115m) }
        });

        var dto = await CreatePipeline(repository, engine).RunAsync(DayTwo, SignalHorizon.WEEK);

        var item = Assert.Single(dto.Items);

        // The plan is graded on slippage-inclusive friction: the 0.690% round trip plus a
        // round trip's worth of tick spread (PHP 0.10 at PHP 100 = 0.20%), so the net R:R is
        // lower than the 2.51 a fee-only model would print. That gap is the point.
        Assert.Equal(
            0.00890m,
            PseBoardLotService.ComputeEffectiveRoundTripFeeRate(
                entryPrice: 100m,
                orderNotionalPhp: SignalPipelineService.DefaultPositionBudgetPhp,
                avgTurnover20dPhp: 25_000_000m,
                atrPct: null));

        // A 2.39:1 net reward-to-risk at ~46% win probability clears the WEEK gate. The 2.5M
        // net foreign flow on 25M of 20-day turnover is a 10% ratio, so the DQR pillar adds two
        // points of probability to the ~44% technical read.
        Assert.Equal(2.39m, item.NetRiskRewardRatio);
        Assert.Equal(0.2947m, item.BreakEvenWinRate);
        Assert.InRange(item.WinProbability, 0.43m, 0.44m);
        Assert.True(item.ExpectedValueR > 0.2m);
        Assert.True(item.QuarterKellyFraction > 0m);
        Assert.Equal("TRADE", item.EdgeVerdict);

        // The model supplied no pre-mortem, so the deterministic fallback must appear.
        Assert.StartsWith("A close below PHP 95 invalidates the trend setup", item.BearCase, StringComparison.Ordinal);
        Assert.Contains("win probability of 43% is only a shrunk estimate", item.BearCase, StringComparison.Ordinal);

        Assert.Equal(1, dto.TradeCount);
        Assert.Equal(0, dto.SmallSizeCount);
        Assert.Equal(0, dto.WatchOnlyCount);
        Assert.InRange(dto.RecommendedCashPct, 0m, 100m);
        Assert.NotNull(dto.Breadth);
        Assert.Equal(1, dto.Breadth!.EvaluatedStockCount);

        Assert.NotNull(engine.LastRequest);
        Assert.NotNull(engine.LastRequest!.Breadth);
    }

    [Fact]
    public async Task RunAsync_keeps_the_model_pre_mortem_when_it_is_short_and_actionable()
    {
        var repository = new FakeSignalRepository();
        repository.Snapshots[DayTwo] = BuildSnapshot(DayTwo);
        repository.Metrics[DayTwo] = new List<StockDailyMetric>
        {
            BuildMetric(DayTwo, "AAA", close: 104m, score: 90)
        };

        var pick = BuildPick("AAA", entry: 100m, stop: 95m, target: 115m);
        pick.BearCase = "A close below 95 on heavy volume invalidates the breakout.";

        var engine = new FakeSignalEngine(new OllamaSignalOutput { Top10 = { pick } });

        var dto = await CreatePipeline(repository, engine).RunAsync(DayTwo, SignalHorizon.WEEK);

        Assert.Equal(
            "A close below 95 on heavy volume invalidates the breakout.",
            Assert.Single(dto.Items).BearCase);
    }

    private static SignalPipelineService CreatePipeline(
        ISignalRepository repository,
        IOllamaSignalEngine engine,
        IMarketDataProvider? provider = null,
        IKnowledgeRetrievalService? knowledge = null,
        PortfolioService? portfolio = null) =>
        new(
            provider ?? new FakeMarketDataProvider(MarketDataBatch.Empty(DayTwo)),
            new QuantitativeFilterService(),
            engine,
            repository,
            NullLogger<SignalPipelineService>.Instance,
            knowledge,
            portfolioService: portfolio);

    [Fact]
    public async Task RunAsync_publishes_the_sector_rotation_to_the_prompt_and_the_candidates()
    {
        var repository = new FakeSignalRepository();
        repository.Snapshots[DayTwo] = BuildSnapshot(DayTwo);
        repository.Metrics[DayTwo] = new List<StockDailyMetric>
        {
            BuildMetric(DayTwo, "AAA", close: 104m, score: 90),
            BuildMetric(DayTwo, "BBB", close: 44m, score: 70)
        };

        var bank = BuildMetric(DayTwo, "CCC", close: 150m, score: 60);
        bank.Sector = "Banks";
        bank.Sma50 = 140m;
        bank.WeinsteinStage = "STAGE_2";
        bank.RelativeStrength63d = 5m;
        repository.Metrics[DayTwo].Add(bank);

        var engine = new FakeSignalEngine(new OllamaSignalOutput
        {
            Top10 = { BuildPick("AAA", entry: 100m, stop: 95m, target: 115m) }
        });

        // Sector rotation is a pure function of the session's metrics, so the run publishes it
        // without a network call and the prompt can rank clusters.
        var dto = await CreatePipeline(repository, engine).RunAsync(DayTwo, SignalHorizon.WEEK);

        var rotation = Assert.IsAssignableFrom<IReadOnlyList<SectorRotationDto>>(engine.LastRequest!.SectorRotation);
        Assert.Equal(2, rotation.Count);

        var banks = rotation[0];
        Assert.Equal("Banks", banks.Sector);
        Assert.Equal(SectorQuadrant.Leading, banks.Quadrant);

        var aaa = engine.LastRequest.Candidates.Single(candidate => candidate.Ticker == "AAA");
        // Industrials: full breadth but no Stage 2 names, so 30 / 0.55 = 54.5 → IMPROVING.
        Assert.Equal(SectorQuadrant.Improving, aaa.SectorQuadrant);

        // The rotation is persisted with the snapshot it was computed from.
        Assert.NotNull(repository.Snapshots[DayTwo].SectorRotationJson);
        Assert.All(dto.Items, item => Assert.Null(item.OrderTicket));
    }

    [Fact]
    public async Task RunAsync_attaches_order_tickets_when_the_desk_book_is_available()
    {
        var repository = new FakeSignalRepository();
        repository.Snapshots[DayTwo] = BuildSnapshot(DayTwo);
        repository.Metrics[DayTwo] = new List<StockDailyMetric>
        {
            BuildMetric(DayTwo, "AAA", close: 104m, score: 90)
        };

        var engine = new FakeSignalEngine(new OllamaSignalOutput
        {
            Top10 = { BuildPick("AAA", entry: 100m, stop: 95m, target: 115m) }
        });

        var portfolio = new PortfolioService(new StubPortfolioRepository(), repository);
        var dto = await CreatePipeline(repository, engine, portfolio: portfolio).RunAsync(DayTwo, SignalHorizon.WEEK);

        // "Run now" answers with the same ticket the blotter would size, so the button never
        // has to do the arithmetic in the browser.
        var ticket = Assert.IsType<OrderTicketDto>(Assert.Single(dto.Items).OrderTicket);
        Assert.True(ticket.Shares > 0);
        Assert.Equal(0, ticket.Shares % ticket.BoardLotSize);
        Assert.Equal(RiskGovernorState.Normal, ticket.RiskGovernor);
    }

    [Fact]
    public async Task RunAsync_survives_a_broken_book_without_losing_the_run()
    {
        var repository = new FakeSignalRepository();
        repository.Snapshots[DayTwo] = BuildSnapshot(DayTwo);
        repository.Metrics[DayTwo] = new List<StockDailyMetric>
        {
            BuildMetric(DayTwo, "AAA", close: 104m, score: 90)
        };

        var engine = new FakeSignalEngine(new OllamaSignalOutput
        {
            Top10 = { BuildPick("AAA", entry: 100m, stop: 95m, target: 115m) }
        });

        var portfolio = new PortfolioService(new BrokenPortfolioRepository(), repository);
        var dto = await CreatePipeline(repository, engine, portfolio: portfolio).RunAsync(DayTwo, SignalHorizon.WEEK);

        // The picks and their server-owned odds still stand; only the ticket is absent.
        var item = Assert.Single(dto.Items);
        Assert.Null(item.OrderTicket);
        Assert.NotEmpty(item.WinProbability.ToString());
    }

    private static SignalRun BuildPreviousRun() =>
        new()
        {
            Id = 1,
            RunDate = DayOne,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DayOne.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            RawModelResponseJson = "{}",
            Items = new List<SignalItem>
            {
                BuildPriorItem("AAA", 1, entry: 100m, stop: 95m, target: 110m),
                BuildPriorItem("BBB", 2, entry: 50m, stop: 47m, target: 60m),
                BuildPriorItem("CCC", 3, entry: 20m, stop: 18m, target: 26m),
                BuildPriorItem("DDD", 4, entry: 10m, stop: 9m, target: 13m)
            }
        };

    private static SignalItem BuildPriorItem(string ticker, int rank, decimal entry, decimal stop, decimal target) =>
        new()
        {
            Ticker = ticker,
            Rank = rank,
            DriftStatus = DriftStatus.NEW,
            ConvictionScore = 80,
            EntryPrice = entry,
            StopLoss = stop,
            TargetPrice = target,
            RiskRewardRatio = PseBoardLotService.ComputeRiskReward(entry, stop, target),
            Thesis = $"{ticker} prior thesis.",
            DeviationReason = string.Empty
        };

    private static OllamaTopPick BuildPick(string ticker, decimal entry, decimal stop, decimal target) =>
        new()
        {
            Ticker = ticker,
            ConvictionScore = 80,
            EntryPrice = entry,
            StopLoss = stop,
            TargetPrice = target,
            Thesis = $"{ticker} breaks out on expanding volume."
        };

    private static DailyMarketSnapshot BuildSnapshot(DateOnly tradeDate) =>
        new()
        {
            TradeDate = tradeDate,
            PseiClose = 8_514.08m,
            PseiSma50 = 8_409.71m,
            PseiSma200 = 8_101.25m,
            NetForeignFlowPhp = 1_280_000_000m,
            UsdPhp = 58.27m,
            MarketRegime = MarketRegime.BULLISH,
            RegimeNote = "Risk-on tape."
        };

    private static StockDailyMetric BuildMetric(DateOnly tradeDate, string ticker, decimal close, int score) =>
        new()
        {
            TradeDate = tradeDate,
            Ticker = ticker,
            CompanyName = $"{ticker} Holdings",
            Sector = "Industrial",
            ClosePrice = close,
            ChangePct = 1.2m,
            AvgTurnover20dPhp = 25_000_000m,
            VolumeRatioVs20d = 1.4m,
            Rsi14 = 55m,
            Ema20 = close - 0.5m,
            Sma50 = close - 1m,
            Sma200 = close - 2m,
            Atr14 = 0.5m,
            SupportLevel = close - 1m,
            ResistanceLevel = close + 2m,
            NetForeignTodayPhp = 2_500_000m,
            QuantPreScore = score
        };

    private static MarketDataBatch BuildBatch(DateOnly tradeDate, params string[] tickers)
    {
        var stocks = tickers
            .Select(ticker => new StockMarketData(
                ticker,
                CompanyName: $"{ticker} Holdings",
                Sector: "Industrial",
                Bars: BuildBars(),
                NetForeignTodayPhp: 5_000_000m,
                Disclosures: Array.Empty<StockDisclosure>()))
            .ToList();

        return new MarketDataBatch(
            tradeDate,
            PseiClose: 8_514.08m,
            PseiSma50: 8_409.71m,
            PseiSma200: 8_101.25m,
            NetForeignFlowPhp: 1_280_000_000m,
            UsdPhp: 58.27m,
            MarketRegime: MarketRegime.BULLISH,
            RegimeNote: "Risk-on tape.",
            Stocks: stocks,
            Source: "unit-test");
    }

    private static List<OhlcvBar> BuildBars()
    {
        const int count = 220;
        var bars = new List<OhlcvBar>(count);
        var date = new DateOnly(2026, 1, 5);
        var price = 100m;

        for (var index = 0; index < count; index++)
        {
            var close = price + 0.15m;

            bars.Add(new OhlcvBar(
                date.AddDays(index),
                Open: price,
                High: close + 0.4m,
                Low: price - 0.4m,
                Close: close,
                Volume: 3_000_000 + (index * 1_000)));

            price = close;
        }

        return bars;
    }

    private sealed class FakeSignalRepository : ISignalRepository
    {
        public Dictionary<DateOnly, DailyMarketSnapshot> Snapshots { get; } = new();

        public Dictionary<DateOnly, List<StockDailyMetric>> Metrics { get; } = new();

        public Dictionary<(DateOnly Date, SignalHorizon Horizon), SignalRun> Runs { get; } = new();

        public SignalRun? SavedRun { get; private set; }

        public int SaveRunCalls { get; private set; }

        public int SaveMarketDataCalls { get; private set; }

        public void AddRun(SignalRun run) => Runs[(run.RunDate, run.Horizon)] = run;

        public Task<DailyMarketSnapshot?> GetSnapshotAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshots.GetValueOrDefault(tradeDate));

        public Task<DailyMarketSnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshots.Count == 0
                ? null
                : Snapshots.OrderBy(entry => entry.Key).Last().Value);

        public Task<IReadOnlyList<StockDailyMetric>> GetMetricsAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<StockDailyMetric> metrics = Metrics.TryGetValue(tradeDate, out var stored)
                ? stored
                : Array.Empty<StockDailyMetric>();

            return Task.FromResult(metrics);
        }

        public Task<StockDailyMetric?> GetMetricAsync(
            DateOnly tradeDate,
            string ticker,
            CancellationToken cancellationToken = default)
        {
            var metric = Metrics.TryGetValue(tradeDate, out var stored)
                ? stored.FirstOrDefault(item => string.Equals(item.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
                : null;

            return Task.FromResult(metric);
        }

        public Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DateOnly> dates = Snapshots.Keys.OrderBy(date => date).ToList();
            return Task.FromResult(dates);
        }

        public Task<IReadOnlyList<DateOnly>> GetRunDatesAsync(
            SignalHorizon horizon,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DateOnly> dates = Runs.Keys
                .Where(key => key.Horizon == horizon)
                .Select(key => key.Date)
                .OrderBy(date => date)
                .ToList();

            return Task.FromResult(dates);
        }

        public Task<SignalRun?> GetLatestRunAsync(
            SignalHorizon horizon,
            CancellationToken cancellationToken = default)
        {
            var run = Runs
                .Where(entry => entry.Key.Horizon == horizon)
                .OrderBy(entry => entry.Key.Date)
                .Select(entry => entry.Value)
                .LastOrDefault();

            return Task.FromResult(run);
        }

        public Task<SignalRun?> GetRunAsync(
            DateOnly runDate,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Runs.GetValueOrDefault((runDate, horizon)));

        public Task<SignalRun?> GetPreviousRunAsync(
            DateOnly beforeDate,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default)
        {
            var run = Runs
                .Where(entry => entry.Key.Horizon == horizon && entry.Key.Date < beforeDate)
                .OrderBy(entry => entry.Key.Date)
                .Select(entry => entry.Value)
                .LastOrDefault();

            return Task.FromResult(run);
        }

        public Task<IReadOnlyList<SignalRun>> GetRunsForTickerAsync(
            string ticker,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SignalRun> runs = Runs
                .Where(entry =>
                    entry.Key.Horizon == horizon &&
                    entry.Value.Items.Any(item => string.Equals(item.Ticker, ticker, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(entry => entry.Key.Date)
                .Select(entry => entry.Value)
                .ToList();

            return Task.FromResult(runs);
        }

        public Task<IReadOnlyList<SignalRun>> GetAllRunsAsync(
            SignalHorizon? horizon = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SignalRun> runs = Runs
                .Where(entry => horizon is null || entry.Key.Horizon == horizon)
                .OrderBy(entry => entry.Key.Date)
                .Select(entry => entry.Value)
                .ToList();

            return Task.FromResult(runs);
        }

        public Task<IReadOnlyList<StockDailyMetric>> GetMetricsRangeAsync(
            DateOnly fromDate,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<StockDailyMetric> metrics = Metrics
                .Where(entry => entry.Key >= fromDate)
                .OrderBy(entry => entry.Key)
                .SelectMany(entry => entry.Value)
                .ToList();

            return Task.FromResult(metrics);
        }

        public int ResolvedOutcomeCalls { get; private set; }

        public IReadOnlyList<SignalItem> ResolvedItems { get; private set; } = Array.Empty<SignalItem>();

        public Task UpdateResolvedOutcomesAsync(
            IReadOnlyList<SignalItem> items,
            CancellationToken cancellationToken = default)
        {
            ResolvedOutcomeCalls++;
            ResolvedItems = items;
            return Task.CompletedTask;
        }

        public Task SaveMarketDataAsync(
            MarketDataBatch batch,
            IReadOnlyList<StockDailyMetric> metrics,
            CancellationToken cancellationToken = default)
        {
            SaveMarketDataCalls++;
            Snapshots[batch.TradeDate] = MarketDataMapper.ToSnapshot(batch);
            Metrics[batch.TradeDate] = metrics.ToList();
            return Task.CompletedTask;
        }

        public Task SaveSignalRunAsync(SignalRun run, CancellationToken cancellationToken = default)
        {
            SaveRunCalls++;
            SavedRun = run;
            Runs[(run.RunDate, run.Horizon)] = run;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMarketDataProvider : IMarketDataProvider
    {
        private readonly MarketDataBatch _batch;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<StockDisclosure>> _companyDisclosures;

        public FakeMarketDataProvider(
            MarketDataBatch batch,
            IReadOnlyDictionary<string, IReadOnlyList<StockDisclosure>>? companyDisclosures = null)
        {
            _batch = batch;
            _companyDisclosures = companyDisclosures
                ?? new Dictionary<string, IReadOnlyList<StockDisclosure>>(StringComparer.OrdinalIgnoreCase);
        }

        public bool WasCalled { get; private set; }

        /// <summary>The tickers the pipeline asked the exchange to re-read, in order.</summary>
        public IReadOnlyList<string>? RequestedTickers { get; private set; }

        public Task<MarketDataBatch> FetchAsync(DateOnly tradeDate, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(_batch);
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<StockDisclosure>>> FetchCompanyDisclosuresAsync(
            IReadOnlyList<string> tickers,
            DateOnly sessionDate,
            CancellationToken cancellationToken = default)
        {
            RequestedTickers = tickers;

            return Task.FromResult(_companyDisclosures);
        }
    }

    private sealed class FakeKnowledgeService : IKnowledgeRetrievalService
    {
        private readonly IReadOnlyList<KnowledgeSnippet> _snippets;

        public FakeKnowledgeService(params KnowledgeSnippet[] snippets)
        {
            _snippets = snippets;
        }

        public int RetrieveCalls { get; private set; }

        public KnowledgeQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<KnowledgeSnippet>> RetrieveAsync(
            KnowledgeQuery query,
            CancellationToken cancellationToken = default)
        {
            RetrieveCalls++;
            LastQuery = query;

            IReadOnlyList<KnowledgeSnippet> filtered = _snippets
                .Take(Math.Max(1, query.MaxSnippets))
                .ToList();

            return Task.FromResult(filtered);
        }

        public Task<IReadOnlyList<KnowledgeDocumentDto>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocumentDto>>(Array.Empty<KnowledgeDocumentDto>());

        public Task<KnowledgeIngestionResult> IndexBuiltInAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new KnowledgeIngestionResult(0, 0, 0, 0, 0, "none"));

        public Task<KnowledgeDocumentDto> IngestAsync(
            KnowledgeUploadRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeSignalEngine : IOllamaSignalEngine
    {
        private readonly OllamaSignalOutput _output;

        public FakeSignalEngine(OllamaSignalOutput output)
        {
            _output = output;
        }

        public string EngineName => "FakeEngine";

        public OllamaSignalRequest? LastRequest { get; private set; }

        public Task<OllamaSignalResult> GenerateSignalAsync(
            OllamaSignalRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;

            var metadata = new OllamaGenerationMetadata(
                EngineName,
                "fake-model",
                "fake-prompt-hash",
                Temperature: 0.1m,
                Seed: 42,
                PromptCharacters: 12_000,
                PromptTokens: 3_000,
                CompletionTokens: 1_200,
                RetryCount: 0,
                LatencyMs: 4_200,
                SchemaConstrained: true,
                FastExtractionApplied: false);

            return Task.FromResult(new OllamaSignalResult(_output, metadata));
        }
    }

    /// <summary>A healthy book: the standard PHP 1,000,000 desk account, no open trades.</summary>
    private sealed class StubPortfolioRepository : IPortfolioRepository
    {
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

        public Task<PortfolioAccount> GetOrCreateAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);

        public Task<PortfolioAccount> UpdateAccountAsync(
            PortfolioAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(account);

        public Task<IReadOnlyList<PortfolioTrade>> GetOpenTradesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PortfolioTrade>>(Array.Empty<PortfolioTrade>());

        public Task<IReadOnlyList<PortfolioTrade>> GetClosedTradesAsync(
            int take = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PortfolioTrade>>(Array.Empty<PortfolioTrade>());

        public Task<PortfolioTrade?> GetTradeAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult<PortfolioTrade?>(null);

        public Task<PortfolioTrade> AddTradeAsync(
            PortfolioTrade trade,
            CancellationToken cancellationToken = default) =>
            throw new PortfolioRuleViolationException("This stub book does not accept trades.");

        public Task<PortfolioTrade> UpdateTradeAsync(
            PortfolioTrade trade,
            CancellationToken cancellationToken = default) =>
            throw new PortfolioRuleViolationException("This stub book does not accept trades.");
    }

    /// <summary>A book whose storage is down: the run must not fail because of it.</summary>
    private sealed class BrokenPortfolioRepository : IPortfolioRepository
    {
        public Task<PortfolioAccount> GetOrCreateAccountAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("portfolio storage is unavailable");

        public Task<PortfolioAccount> UpdateAccountAsync(
            PortfolioAccount account,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("portfolio storage is unavailable");

        public Task<IReadOnlyList<PortfolioTrade>> GetOpenTradesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("portfolio storage is unavailable");

        public Task<IReadOnlyList<PortfolioTrade>> GetClosedTradesAsync(
            int take = 100,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("portfolio storage is unavailable");

        public Task<PortfolioTrade?> GetTradeAsync(int id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("portfolio storage is unavailable");

        public Task<PortfolioTrade> AddTradeAsync(
            PortfolioTrade trade,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("portfolio storage is unavailable");

        public Task<PortfolioTrade> UpdateTradeAsync(
            PortfolioTrade trade,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("portfolio storage is unavailable");
    }
}


