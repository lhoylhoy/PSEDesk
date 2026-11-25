namespace PseSignalDesk.Application.Models;

/// <summary>
/// One tracked PSE name, exposed so an everyday investor can look a stock up by the company
/// name they know ("Jollibee", "SM Prime") instead of memorising the ticker. It carries no
/// prices, sizes or odds: it is metadata only, and every number still comes from the live
/// feed and the desk's own engine.
/// </summary>
public sealed record CatalogStockDto(string Ticker, string CompanyName, string Sector);
