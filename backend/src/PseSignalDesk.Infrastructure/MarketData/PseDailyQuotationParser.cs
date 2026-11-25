using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PseSignalDesk.Infrastructure.MarketData;

/// <summary>
/// Deterministic parser for the official PSE Daily Quotation Report. It accepts the exchange's
/// PDF, a CSV export of it, or a tab-delimited text dump, and only reports tickers the desk
/// tracks. Anything that cannot be recognized yields an empty report: a missing foreign-flow
/// figure must stay "unreported" rather than become a fabricated zero.
/// <para>
/// Number parsing follows accounting convention: <c>(1,234.50)</c> is <c>-1234.50</c>. The PDF
/// path reconstructs text lines from word bounding boxes, because the report is a table whose
/// columns are positional rather than delimited.
/// </para>
/// </summary>
public sealed class PseDailyQuotationParser : IDailyQuotationParser
{
    private static readonly string[] PdfMagic = { "%PDF" };

    private static readonly Regex UsDatePattern = new(
        @"(?<m>\d{1,2})/(?<d>\d{1,2})/(?<y>\d{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IsoDatePattern = new(
        @"(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LongDatePattern = new(
        @"(?<mon>January|February|March|April|May|June|July|August|September|October|November|December)\s+(?<d>\d{1,2}),?\s+(?<y>\d{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] TickerHeaderNames = { "ticker", "symbol", "security", "stock", "code" };
    private static readonly string[] ForeignHeaderNames =
    {
        "net foreign", "netforeign", "foreign net", "net fgn", "foreign flow", "net foreign flow"
    };
    private static readonly string[] CloseHeaderNames = { "close", "last", "closing" };
    private static readonly string[] ValueHeaderNames = { "value", "turnover", "amount" };
    private static readonly string[] StatusHeaderNames = { "status", "remarks", "note", "trading status" };

    private readonly IReadOnlySet<string> _trackedTickers;

    /// <summary>Default parser, restricted to the desk's tracked PSE catalog.</summary>
    public PseDailyQuotationParser()
        : this(PseTickerCatalog.All.Select(info => info.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase))
    {
    }

    /// <summary>Test seam: restricts parsing to an explicit ticker universe.</summary>
    public PseDailyQuotationParser(IReadOnlySet<string> trackedTickers)
    {
        _trackedTickers = trackedTickers ?? throw new ArgumentNullException(nameof(trackedTickers));
    }

    public ParsedDailyQuotationReport Parse(string fileName, byte[] content)
    {
        if (content is null || content.Length == 0)
        {
            return ParsedDailyQuotationReport.Empty;
        }

        try
        {
            var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            var isPdf = extension == ".pdf" || LooksLikePdf(content);

            if (isPdf)
            {
                return ParseLines(ExtractPdfLines(content));
            }

            var text = DecodeText(content);
            var delimited = TryParseDelimited(text);
            return delimited ?? ParseLines(SplitLines(text));
        }
        catch (Exception)
        {
            // A malformed or unrelated file is a data problem for the operator, not a reason to
            // fail a live run. The empty report keeps the foreign dial "unreported".
            return ParsedDailyQuotationReport.Empty;
        }
    }

    private static bool LooksLikePdf(byte[] content)
    {
        if (content.Length < 4)
        {
            return false;
        }

        var header = Encoding.ASCII.GetString(content, 0, 4);
        return PdfMagic.Any(magic => header.StartsWith(magic, StringComparison.Ordinal));
    }

    private static string DecodeText(byte[] content)
    {
        using var reader = new StreamReader(
            new MemoryStream(content),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(NormalizeWhitespace)
            .Where(line => line.Length > 0)
            .ToList();

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Rebuilds the PDF's visual rows: words are sorted top-to-bottom, then grouped into lines
    /// when their baselines differ by at most a few points, and read left-to-right inside the
    /// line. This is what makes the positional table parseable as text.
    /// </summary>
    private static IReadOnlyList<string> ExtractPdfLines(byte[] content)
    {
        const double lineTolerance = 3.0;
        var lines = new List<string>();

        using var stream = new MemoryStream(content);
        using var document = PdfDocument.Open(stream);

        foreach (var page in document.GetPages())
        {
            var words = page.GetWords()
                .OrderByDescending(word => (double)word.BoundingBox.Bottom)
                .ThenBy(word => (double)word.BoundingBox.Left)
                .ToList();

            var current = new List<Word>();
            double? baseline = null;

            foreach (var word in words)
            {
                var bottom = (double)word.BoundingBox.Bottom;

                if (baseline is null || Math.Abs(bottom - baseline.Value) <= lineTolerance)
                {
                    baseline ??= bottom;
                    current.Add(word);
                    continue;
                }

                FlushLine(current, lines);
                current = new List<Word> { word };
                baseline = bottom;
            }

            FlushLine(current, lines);
        }

        return lines.Where(line => line.Length > 0).ToList();
    }

    private static void FlushLine(List<Word> words, List<string> lines)
    {
        if (words.Count == 0)
        {
            return;
        }

        var text = string.Join(' ', words
            .OrderBy(word => (double)word.BoundingBox.Left)
            .Select(word => word.Text));

        var normalized = NormalizeWhitespace(text);
        if (normalized.Length > 0)
        {
            lines.Add(normalized);
        }
    }

    /// <summary>
    /// Whitespace-layout path (used by the PDF and by plain-text dumps): the ticker is the first
    /// tracked token on the row, every numeric token after it is a column, the last number is
    /// the net foreign figure and the second-to-last is the peso value turnover. The suspended
    /// block is a separate text-only section, so its rows carry no numbers.
    /// </summary>
    private ParsedDailyQuotationReport ParseLines(IReadOnlyList<string> lines)
    {
        var byTicker = new Dictionary<string, PseForeignFlowQuote>(StringComparer.OrdinalIgnoreCase);
        var suspended = new List<string>();
        DateOnly? reportDate = null;
        decimal? marketFlow = null;
        var suspendedSection = false;

        foreach (var line in lines)
        {
            reportDate ??= ParseDate(line);

            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hasNumbers = tokens.Any(token => TryParseNumber(token, out _));

            if (IsTotalLine(tokens))
            {
                var numbers = tokens
                    .Select(token => TryParseNumber(token, out var value) ? value : (decimal?)null)
                    .Where(value => value is not null)
                    .Select(value => value!.Value)
                    .ToList();

                if (numbers.Count > 0)
                {
                    marketFlow ??= numbers[^1];
                }

                suspendedSection = false;
                continue;
            }

            if (line.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase))
            {
                AddSuspendedTokens(tokens, suspended);

                if (!hasNumbers)
                {
                    suspendedSection = true;
                    continue;
                }
            }

            if (suspendedSection)
            {
                if (!hasNumbers)
                {
                    AddSuspendedTokens(tokens, suspended);
                    continue;
                }

                suspendedSection = false;
            }

            TryAddRow(tokens, byTicker);
        }

        return new ParsedDailyQuotationReport(reportDate, marketFlow, byTicker, suspended);
    }

    private bool TryAddRow(IReadOnlyList<string> tokens, IDictionary<string, PseForeignFlowQuote> byTicker)
    {
        string? ticker = null;
        var symbolIndex = -1;

        for (var index = 0; index < tokens.Count; index++)
        {
            var candidate = NormalizeTicker(tokens[index]);
            if (candidate is not null && _trackedTickers.Contains(candidate))
            {
                ticker = candidate;
                symbolIndex = index;
                break;
            }
        }

        if (ticker is null)
        {
            return false;
        }

        // Columns are positional, so a missing cell must stay in place. Collapsing the row onto
        // its numeric tokens shifts every column after a dash: a "-" net foreign figure would
        // hand the peso turnover to the foreign column and report real money as foreign flow.
        var columns = CollectColumns(tokens, symbolIndex + 1);

        if (columns.Count == 0)
        {
            return false;
        }

        var netForeign = columns[^1];
        if (netForeign is null)
        {
            // The exchange printed no net foreign figure for this name. That is unreported, and
            // the row is skipped instead of borrowing the neighbouring turnover column.
            return false;
        }

        decimal? closePrice = columns.Count >= 4 ? columns[3] : null;
        decimal? valueTurnover = columns.Count >= 2 && columns[^2] is >= 0m ? columns[^2] : null;

        byTicker[ticker] = new PseForeignFlowQuote(ticker, netForeign.Value, closePrice, valueTurnover);
        return true;
    }

    /// <summary>
    /// Maps the tokens after the ticker onto report columns. A numeric token is its value, a
    /// dash/<c>N/A</c> placeholder is an explicit null column, and the descriptive words of the
    /// security name (which precede the numbers) are ignored. Placeholders are only honoured once
    /// the numeric block has started, so a hyphen inside a company name cannot inject a column.
    /// </summary>
    private static List<decimal?> CollectColumns(IReadOnlyList<string> tokens, int start)
    {
        var columns = new List<decimal?>();
        var inColumnBlock = false;

        for (var index = start; index < tokens.Count; index++)
        {
            if (TryParseNumber(tokens[index], out var value))
            {
                columns.Add(value);
                inColumnBlock = true;
                continue;
            }

            if (inColumnBlock && IsMissingMarker(tokens[index]))
            {
                columns.Add(null);
            }
        }

        return columns;
    }

    private static bool IsMissingMarker(string? token)
    {
        var text = (token ?? string.Empty).Trim().Trim('"').Trim();

        return text.Length > 0 &&
               text is "-" or "--" or "—" or "–" or "n/a" or "N/A" or "na" or "NA" or "nil" or "NIL";
    }

    private bool IsTotalLine(IReadOnlyList<string> tokens)
    {
        var text = string.Join(' ', tokens);

        if (!text.Contains("TOTAL", StringComparison.OrdinalIgnoreCase) ||
            (!text.Contains("FOREIGN", StringComparison.OrdinalIgnoreCase) &&
             !text.Contains("NET", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // A company name could contain "Total" (e.g. "Total Philippines"); a real total row has
        // no tracked ticker of its own.
        return !tokens.Any(token =>
            NormalizeTicker(token) is { } ticker && _trackedTickers.Contains(ticker));
    }


    private void AddSuspendedTokens(IReadOnlyList<string> tokens, ICollection<string> suspended)
    {
        foreach (var token in tokens)
        {
            var ticker = NormalizeTicker(token);

            if (ticker is null || !_trackedTickers.Contains(ticker))
            {
                continue;
            }

            if (!suspended.Contains(ticker, StringComparer.OrdinalIgnoreCase))
            {
                suspended.Add(ticker);
            }
        }
    }

    private static string? NormalizeTicker(string? token)
    {
        var cleaned = (token ?? string.Empty).Trim().Trim('"').TrimEnd('*').Trim();

        if (cleaned.Length is < 2 or > 8 ||
            !cleaned.All(character => char.IsLetterOrDigit(character) || character == '.'))
        {
            return null;
        }

        return cleaned.ToUpperInvariant();
    }

    private static DateOnly? ParseDate(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var iso = IsoDatePattern.Match(line);
        if (iso.Success &&
            DateOnly.TryParseExact(
                iso.Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var isoDate))
        {
            return isoDate;
        }

        var us = UsDatePattern.Match(line);
        if (us.Success)
        {
            var month = int.Parse(us.Groups["m"].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(us.Groups["d"].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(us.Groups["y"].Value, CultureInfo.InvariantCulture);

            if (month is >= 1 and <= 12 &&
                day >= 1 &&
                day <= DateTime.DaysInMonth(year, month))
            {
                return new DateOnly(year, month, day);
            }
        }

        var longDate = LongDatePattern.Match(line);
        if (longDate.Success &&
            DateOnly.TryParseExact(
                $"{longDate.Groups["mon"].Value} {longDate.Groups["d"].Value}, {longDate.Groups["y"].Value}",
                "MMMM d, yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedLongDate))
        {
            return parsedLongDate;
        }

        return null;
    }

    /// <summary>
    /// Tolerant numeric read: thousands separators, an optional peso sign and the accounting
    /// negative convention <c>(1,234.50)</c>. A dash or an explicit "n/a" is unreported, not 0.
    /// </summary>
    private static bool TryParseNumber(string? token, out decimal value)
    {
        value = 0m;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var text = token.Trim().Trim('"').Trim();

        if (text.Length == 0 ||
            text is "-" or "--" or "—" or "n/a" or "N/A" or "na" or "NA")
        {
            return false;
        }

        var negative = false;

        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            negative = true;
            text = text[1..^1];
        }

        if (text.EndsWith('%'))
        {
            text = text[..^1];
        }

        text = text
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("₱", string.Empty, StringComparison.Ordinal)
            .Replace("PHP", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (text.StartsWith('+'))
        {
            text = text[1..];
        }

        if (!decimal.TryParse(
                text,
                NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        value = negative ? -Math.Abs(parsed) : parsed;
        return true;
    }


    /// <summary>
    /// CSV/TSV path: a header row naming the ticker and net-foreign columns makes the layout
    /// explicit, so column order does not matter. Returns null when no header is recognizable,
    /// which lets the whitespace parser try instead.
    /// </summary>
    private ParsedDailyQuotationReport? TryParseDelimited(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var delimiter = DetectDelimiter(lines);
        if (delimiter is null)
        {
            return null;
        }

        for (var headerIndex = 0; headerIndex < Math.Min(lines.Length, 40); headerIndex++)
        {
            var header = SplitDelimited(lines[headerIndex], delimiter.Value);

            if (header.Count < 2)
            {
                continue;
            }

            var tickerColumn = FindHeaderIndex(header, TickerHeaderNames);
            var foreignColumn = FindHeaderIndex(header, ForeignHeaderNames);

            if (tickerColumn < 0 || foreignColumn < 0)
            {
                continue;
            }

            var closeColumn = FindHeaderIndex(header, CloseHeaderNames);
            var valueColumn = FindHeaderIndex(header, ValueHeaderNames);
            var statusColumn = FindHeaderIndex(header, StatusHeaderNames);

            var byTicker = new Dictionary<string, PseForeignFlowQuote>(StringComparer.OrdinalIgnoreCase);
            var suspended = new List<string>();
            DateOnly? reportDate = headerIndex > 0
                ? ParseDate(string.Join(' ', lines.Take(headerIndex)))
                : null;
            decimal? marketFlow = null;

            for (var rowIndex = headerIndex + 1; rowIndex < lines.Length; rowIndex++)
            {
                var cells = SplitDelimited(lines[rowIndex], delimiter.Value);

                if (cells.Count == 0)
                {
                    continue;
                }

                reportDate ??= ParseDate(string.Join(' ', cells));

                if (IsTotalRow(cells))
                {
                    var totals = cells
                        .Select(cell => TryParseNumber(cell, out var total) ? total : (decimal?)null)
                        .Where(total => total is not null)
                        .Select(total => total!.Value)
                        .ToList();

                    if (totals.Count > 0)
                    {
                        marketFlow ??= totals[^1];
                    }

                    continue;
                }

                if (foreignColumn >= cells.Count)
                {
                    continue;
                }

                var ticker = NormalizeTicker(tickerColumn < cells.Count ? cells[tickerColumn] : null);
                if (ticker is null || !_trackedTickers.Contains(ticker))
                {
                    continue;
                }

                if (statusColumn >= 0 &&
                    statusColumn < cells.Count &&
                    cells[statusColumn].Contains("SUSPEND", StringComparison.OrdinalIgnoreCase))
                {
                    if (!suspended.Contains(ticker, StringComparer.OrdinalIgnoreCase))
                    {
                        suspended.Add(ticker);
                    }

                    continue;
                }

                // Exports without a status column still flag halts inline, so scan the whole row.
                if (statusColumn < 0 &&
                    cells.Any(cell => cell.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!suspended.Contains(ticker, StringComparer.OrdinalIgnoreCase))
                    {
                        suspended.Add(ticker);
                    }

                    continue;
                }

                if (!TryParseNumber(cells[foreignColumn], out var netForeign))
                {
                    continue;
                }

                decimal? closePrice = closeColumn >= 0 && closeColumn < cells.Count &&
                                      TryParseNumber(cells[closeColumn], out var close)
                    ? close
                    : null;

                decimal? valueTurnover = valueColumn >= 0 && valueColumn < cells.Count &&
                                         TryParseNumber(cells[valueColumn], out var turnover) &&
                                         turnover >= 0m
                    ? turnover
                    : null;

                byTicker[ticker] = new PseForeignFlowQuote(ticker, netForeign, closePrice, valueTurnover);
            }

            if (byTicker.Count == 0 && marketFlow is null)
            {
                return null;
            }

            return new ParsedDailyQuotationReport(reportDate, marketFlow, byTicker, suspended);
        }

        return null;
    }


    private static char? DetectDelimiter(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Take(20))
        {
            if (line.Contains('\t'))
            {
                return '\t';
            }
        }

        foreach (var line in lines.Take(20))
        {
            if (line.Contains(';'))
            {
                return ';';
            }
        }

        foreach (var line in lines.Take(20))
        {
            if (line.Contains(','))
            {
                return ',';
            }
        }

        return null;
    }

    private static IReadOnlyList<string> SplitDelimited(string line, char delimiter)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var character in line)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (character == delimiter && !inQuotes)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static int FindHeaderIndex(IReadOnlyList<string> header, IReadOnlyList<string> names)
    {
        for (var index = 0; index < header.Count; index++)
        {
            var normalized = NormalizeHeader(header[index]);

            if (normalized.Length == 0)
            {
                continue;
            }

            foreach (var name in names)
            {
                if (normalized.Contains(NormalizeHeader(name), StringComparison.Ordinal))
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static string NormalizeHeader(string value) =>
        new((value ?? string.Empty)
            .Where(character => char.IsLetterOrDigit(character))
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static bool IsTotalRow(IReadOnlyList<string> cells)
    {
        var text = string.Join(' ', cells);

        return text.Contains("TOTAL", StringComparison.OrdinalIgnoreCase) &&
               (text.Contains("FOREIGN", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("NET", StringComparison.OrdinalIgnoreCase));
    }

}
