using System.Text.Json;
using System.Text.Json.Serialization;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Result of parsing and validating a model response. Errors are human-readable and
/// are fed back into the corrective retry prompt.
/// </summary>
public sealed record SignalParseResult(
    OllamaSignalOutput? Output,
    IReadOnlyList<string> Errors,
    string? ExtractedJson)
{
    public bool IsSuccess => Output is not null && Errors.Count == 0;

    public static SignalParseResult Success(OllamaSignalOutput output, string extractedJson) =>
        new(output, Array.Empty<string>(), extractedJson);

    public static SignalParseResult Failure(IReadOnlyList<string> errors, string? extractedJson = null) =>
        new(null, errors, extractedJson);
}

/// <summary>
/// Defensive parser for LLM output: strips markdown fences, extracts the outermost
/// JSON object from prose and enforces the trading contract before anything is saved.
/// </summary>
public sealed class SignalJsonParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public SignalParseResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SignalParseResult.Failure(new[] { "The model returned an empty response." });
        }

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return SignalParseResult.Failure(new[]
            {
                "No JSON object could be found in the response. Return a single JSON object with top_10 and dropped_from_yesterday."
            });
        }

        OllamaSignalOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<OllamaSignalOutput>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return SignalParseResult.Failure(new[] { $"The response was not valid JSON: {ex.Message}" }, json);
        }

        if (output is null)
        {
            return SignalParseResult.Failure(new[] { "The JSON payload deserialized to null." }, json);
        }

        output.Top10 ??= new List<OllamaTopPick>();
        output.DroppedFromYesterday ??= new List<OllamaDrop>();

        var errors = Validate(output);
        return errors.Count == 0
            ? SignalParseResult.Success(output, json)
            : SignalParseResult.Failure(errors, json);
    }

    /// <summary>
    /// Extracts the outermost balanced JSON object, ignoring braces inside strings.
    /// </summary>
    public static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return raw.Substring(start, i - start + 1);
                    }

                    break;
            }
        }

        return null;
    }

    private static List<string> Validate(OllamaSignalOutput output)
    {
        var errors = new List<string>();
        var picks = output.Top10;

        if (picks.Count == 0)
        {
            errors.Add("top_10 must contain at least one pick.");
        }

        if (picks.Count > 10)
        {
            errors.Add($"top_10 must contain at most 10 picks (received {picks.Count}).");
        }

        var seenTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < picks.Count; index++)
        {
            var pick = picks[index];
            var ticker = (pick.Ticker ?? string.Empty).Trim().ToUpperInvariant();
            var label = ticker.Length == 0 ? $"pick #{index + 1}" : ticker;

            if (ticker.Length == 0)
            {
                errors.Add($"{label}: ticker is required.");
            }
            else if (!seenTickers.Add(ticker))
            {
                errors.Add($"{label}: duplicate ticker in top_10.");
            }

            if (pick.EntryPrice <= 0m)
            {
                errors.Add($"{label}: entry_price must be greater than zero.");
            }

            if (pick.StopLoss <= 0m || pick.StopLoss >= pick.EntryPrice)
            {
                errors.Add($"{label}: stop_loss must be greater than zero and below entry_price.");
            }

            if (pick.TargetPrice <= pick.EntryPrice)
            {
                errors.Add($"{label}: target_price must be above entry_price.");
            }

            if (pick.ConvictionScore < 0m || pick.ConvictionScore > 100m)
            {
                errors.Add($"{label}: conviction_score must be between 0 and 100.");
            }

            if (string.IsNullOrWhiteSpace(pick.Thesis))
            {
                errors.Add($"{label}: thesis is required.");
            }
        }

        foreach (var drop in output.DroppedFromYesterday)
        {
            var ticker = (drop.Ticker ?? string.Empty).Trim().ToUpperInvariant();

            if (ticker.Length == 0)
            {
                errors.Add("dropped_from_yesterday: every entry needs a ticker.");
            }

            if (!Enum.TryParse<DropCategory>(drop.DropCategory, ignoreCase: true, out var category) ||
                !Enum.IsDefined(category))
            {
                errors.Add(
                    $"{(ticker.Length == 0 ? "dropped ticker" : ticker)}: drop_category '{drop.DropCategory}' is invalid; expected INVALIDATED, TARGET_HIT or DISPLACED.");
            }

            if (string.IsNullOrWhiteSpace(drop.Reason))
            {
                errors.Add($"{(ticker.Length == 0 ? "dropped ticker" : ticker)}: drop reason is required.");
            }
        }

        return errors;
    }
}

