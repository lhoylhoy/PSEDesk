namespace PseSignalDesk.Application.Models;

/// <summary>
/// A PSE Edge disclosure/catalyst surfaced to the LLM for fundamental auditing.
/// </summary>
public sealed record StockDisclosure(
    DateOnly Date,
    string Headline,
    string Category,
    string Source = "PSE Edge");
