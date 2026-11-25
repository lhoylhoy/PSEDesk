namespace PseSignalDesk.Application.Models;

/// <summary>
/// A PSE Edge disclosure/catalyst surfaced to the LLM for fundamental auditing.
/// <para>
/// <see cref="FiledAtPht"/> is the time the filing hit the wire, Manila time, when the source
/// publishes one. It exists so a replay can prove the document was readable before a session
/// closed: a filing dated the same day at 17:40 was not part of that session's information set,
/// and treating it as if it were is a point-in-time leak.
/// </para>
/// </summary>
public sealed record StockDisclosure(
    DateOnly Date,
    string Headline,
    string Category,
    string Source = "PSE Edge",
    TimeOnly? FiledAtPht = null);
