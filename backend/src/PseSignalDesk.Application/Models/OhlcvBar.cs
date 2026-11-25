namespace PseSignalDesk.Application.Models;

/// <summary>
/// One daily OHLCV candle for a PSE ticker. Volume is in shares.
/// </summary>
public sealed record OhlcvBar(
    DateOnly Date,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);
