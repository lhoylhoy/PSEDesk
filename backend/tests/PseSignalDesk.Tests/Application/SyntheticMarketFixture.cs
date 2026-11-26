using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;
using PseSignalDesk.Infrastructure.MarketData;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// A deterministic synthetic tape for the golden-output tests. It is test data, never a
/// production feed: <c>PseMarketDataProvider</c> still fails loudly when the live endpoint is
/// down, and nothing here is reachable from the API.
/// <para>
/// The series are built from a fixed seed so every run produces the same candles, the same
/// entries and exits, and therefore the same replayed equity curve. That is what makes a
/// byte-for-byte snapshot a meaningful guard for the performance refactors: any change in an
/// indicator, a plan level or a fee shows up as a diff.
/// </para>
/// </summary>
internal static class SyntheticMarketFixture
{
    public const int Seed = 20261126;
    public const int TickerCount = 24;
    public const int BarCount = 420;

    /// <summary>The newest session of every generated series.</summary>
    public static readonly DateOnly LastSession = new(2026, 11, 24);

    /// <summary>
    /// One complete batch: a steadily rising PSEi (so the causal regime gate stays open), and
    /// <paramref name="tickerCount"/> catalog names with an oscillating uptrend and periodic
    /// volume expansion, which is what lets the horizon entry rules fire.
    /// </summary>
    public static MarketDataBatch BuildBatch(int tickerCount = TickerCount, int barCount = BarCount)
    {
        var dates = BuildTradingDates(barCount, LastSession);
        var indexBars = BuildIndexBars(dates);
        var indexCloses = indexBars.Select(bar => bar.Close).ToList();
        var tickers = PseTickerCatalog.All.Take(tickerCount).ToList();
        var stocks = new List<StockMarketData>(tickers.Count);

        for (var position = 0; position < tickers.Count; position++)
        {
            stocks.Add(BuildStock(tickers[position], dates, Seed + position));
        }

        return new MarketDataBatch(
            TradeDate: dates[^1],
            PseiClose: indexBars[^1].Close,
            PseiSma50: QuantitativeFilterService.ComputeSma(indexCloses, QuantitativeFilterService.SmaShortPeriod)!.Value,
            PseiSma200: QuantitativeFilterService.ComputeSma(indexCloses, QuantitativeFilterService.SmaLongPeriod)!.Value,
            NetForeignFlowPhp: 1_500_000_000m,
            UsdPhp: 58.42m,
            MarketRegime: MarketRegime.BULLISH,
            RegimeNote: "Synthetic deterministic tape for the golden-output tests.",
            Stocks: stocks,
            Source: "synthetic-golden",
            IndexBars: indexBars);
    }

    /// <summary>The trading dates of the fixture, oldest first, weekends and holidays excluded.</summary>
    public static IReadOnlyList<DateOnly> BuildTradingDates(int barCount, DateOnly lastSession)
    {
        var dates = new List<DateOnly>(barCount);
        var date = lastSession;

        while (dates.Count < barCount)
        {
            if (PseTradingSessionRules.IsTradingDay(date))
            {
                dates.Add(date);
            }

            date = date.AddDays(-1);
        }

        dates.Reverse();
        return dates;
    }

    /// <summary>A PSEi series that compounds upward with a small weekly wave, so the regime gate is bullish.</summary>
    public static IReadOnlyList<OhlcvBar> BuildIndexBars(IReadOnlyList<DateOnly> dates)
    {
        var random = new Random(Seed);
        var bars = new List<OhlcvBar>(dates.Count);
        var price = 7_200m;

        for (var index = 0; index < dates.Count; index++)
        {
            var drift = 0.0007 + (0.0022 * Math.Sin(index / 17.0));
            var shock = (random.NextDouble() - 0.5) * 0.006;
            var close = decimal.Round(price * (1m + (decimal)(drift + shock)), 2, MidpointRounding.AwayFromZero);
            var open = decimal.Round(price, 2, MidpointRounding.AwayFromZero);
            var high = decimal.Round(Math.Max(open, close) * 1.006m, 2, MidpointRounding.AwayFromZero);
            var low = decimal.Round(Math.Min(open, close) * 0.994m, 2, MidpointRounding.AwayFromZero);

            bars.Add(new OhlcvBar(
                dates[index],
                open,
                Math.Max(high, Math.Max(open, close)),
                Math.Min(low, Math.Min(open, close)),
                close,
                1_000_000L));

            price = close;
        }

        return bars;
    }

    /// <summary>
    /// One ticker's candles: a drifting uptrend with a multi-week wave (so RSI lands inside each
    /// horizon's band on the recovery legs) and periodic volume expansion (so the volume gate
    /// fires). Every price is tick-rounded, exactly like a parsed live candle.
    /// </summary>
    public static List<OhlcvBar> BuildBars(IReadOnlyList<DateOnly> dates, int seed)
    {
        var random = new Random(seed);
        var bars = new List<OhlcvBar>(dates.Count);
        var price = PseBoardLotService.RoundToValidTick(8m + ((decimal)random.NextDouble() * 120m));
        var phase = random.NextDouble() * Math.PI * 2;
        var baseVolume = 300_000 + random.Next(0, 1_400_000);

        // Every third name grinds upward slowly instead of swinging. Those series are what let the
        // exit ladder — the +1R break-even ratchet and the +1.5R EMA20 trail — actually run.
        var grinder = seed % 3 == 0;

        for (var index = 0; index < dates.Count; index++)
        {
            var drift = grinder
                ? 0.0019 + (0.0055 * Math.Sin((index / 23.0) + phase))
                : 0.0011 + (0.0115 * Math.Sin((index / 11.0) + phase));
            var shock = (random.NextDouble() - 0.5) * (grinder ? 0.009 : 0.016);
            var open = price;
            var close = PseBoardLotService.RoundToValidTick(Math.Max(1m, price * (1m + (decimal)(drift + shock))));
            var high = PseBoardLotService.RoundToValidTick(Math.Max(open, close) * 1.012m);
            var low = PseBoardLotService.RoundToValidTick(Math.Min(open, close) * 0.988m);

            var spike = index % 13 == 7 || index % 17 == 3;
            var volume = (long)(baseVolume * (spike ? 2.6 + random.NextDouble() : 0.75 + (random.NextDouble() * 0.5)));

            bars.Add(new OhlcvBar(
                dates[index],
                Open: open,
                High: Math.Max(high, Math.Max(open, close)),
                Low: Math.Min(low, Math.Min(open, close)),
                Close: close,
                Volume: Math.Max(1, volume)));

            price = close;
        }

        return bars;
    }

    private static StockMarketData BuildStock(PseTickerInfo info, IReadOnlyList<DateOnly> dates, int seed) =>
        new(
            info.Ticker,
            info.CompanyName,
            info.Sector,
            BuildBars(dates, seed),
            NetForeignTodayPhp: null,
            Disclosures: Array.Empty<StockDisclosure>(),
            Dividends: Array.Empty<StockDividendEvent>());

    /// <summary>Hands one prepared batch to a service without touching the network.</summary>
    internal sealed class FixedMarketDataProvider : IMarketDataProvider
    {
        private readonly MarketDataBatch _batch;

        public FixedMarketDataProvider(MarketDataBatch batch) => _batch = batch;

        public Task<MarketDataBatch> FetchAsync(DateOnly tradeDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(_batch);

        public Task<MarketDataBatch> FetchAsync(
            DateOnly tradeDate,
            int? lookbackDaysOverride,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_batch);
    }
}
