using PseSignalDesk.Application.Models;

namespace PseSignalDesk.Application.Interfaces;

/// <summary>
/// Parses the official PSE Daily Quotation Report. Implementations must be pure: the same
/// bytes always produce the same report, and an unreadable or unrelated file must return an
/// empty report rather than fabricate zero foreign flows.
/// </summary>
public interface IDailyQuotationParser
{
    /// <summary>
    /// Parses <paramref name="content"/> using the file name to choose the PDF or delimited
    /// path. Supported extensions: <c>.pdf</c>, <c>.csv</c>, <c>.txt</c> and <c>.tsv</c>.
    /// </summary>
    ParsedDailyQuotationReport Parse(string fileName, byte[] content);
}
