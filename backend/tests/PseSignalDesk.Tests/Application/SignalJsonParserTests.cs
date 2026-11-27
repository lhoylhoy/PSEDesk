using PseSignalDesk.Application.Services;

namespace PseSignalDesk.Tests.Application;

public sealed class SignalJsonParserTests
{
    private readonly SignalJsonParser _parser = new();

    private const string ValidPayload = """
        {
          "top_10": [
            {
              "rank": 1,
              "ticker": "ICT",
              "conviction_score": 88,
              "entry_price": 506.5,
              "stop_loss": 473.2,
              "target_price": 575.0,
              "thesis": "Breakout above the {resistance} shelf on 2.4x volume.",
              "deviation_reason": null
            },
            {
              "rank": 2,
              "ticker": "BDO",
              "conviction_score": "76",
              "entry_price": "148.10",
              "stop_loss": "140.10",
              "target_price": "164.50",
              "thesis": "Foreign inflow into the banks."
            }
          ],
          "dropped_from_yesterday": [
            {
              "ticker": "RLC",
              "previous_rank": 2,
              "drop_category": "DISPLACED",
              "reason": "Rotated into higher-conviction names."
            }
          ]
        }
        """;

    [Fact]
    public void Parse_accepts_a_clean_json_object_and_numeric_strings()
    {
        var result = _parser.Parse(ValidPayload);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Output!.Top10.Count);
        Assert.Equal(76m, result.Output.Top10[1].ConvictionScore);
        Assert.Equal(148.10m, result.Output.Top10[1].EntryPrice);
        Assert.Single(result.Output.DroppedFromYesterday);
    }

    [Fact]
    public void Parse_strips_markdown_fences_and_surrounding_chatter()
    {
        var raw = $"""
            Here is the desk view for today.

            ```json
            {ValidPayload}
            ```

            Let me know if you need the raw screen.
            """;

        var result = _parser.Parse(raw);

        Assert.True(result.IsSuccess);
        Assert.Contains("\"top_10\"", result.ExtractedJson);
    }

    [Fact]
    public void ExtractJsonObject_ignores_braces_inside_strings()
    {
        const string raw = """{"top_10":[{"ticker":"ICT","thesis":"gap through {resistance}"}]}""";

        var extracted = SignalJsonParser.ExtractJsonObject(raw);

        Assert.Equal(raw, extracted);
    }

    [Fact]
    public void Parse_rejects_a_payload_without_top_10()
    {
        const string raw = """{"top_10": [], "dropped_from_yesterday": []}""";

        var result = _parser.Parse(raw);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("top_10", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_rejects_duplicate_tickers()
    {
        const string raw = """
            {
              "top_10": [
                { "rank": 1, "ticker": "ICT", "conviction_score": 80, "entry_price": 10, "stop_loss": 9, "target_price": 12, "thesis": "a" },
                { "rank": 2, "ticker": "ict", "conviction_score": 70, "entry_price": 10, "stop_loss": 9, "target_price": 12, "thesis": "b" }
              ]
            }
            """;

        var result = _parser.Parse(raw);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_rejects_stops_on_the_wrong_side_of_the_entry()
    {
        const string raw = """
            {
              "top_10": [
                { "rank": 1, "ticker": "ICT", "conviction_score": 80, "entry_price": 10, "stop_loss": 11, "target_price": 12, "thesis": "a" }
              ]
            }
            """;

        var result = _parser.Parse(raw);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("stop_loss", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_rejects_an_unknown_drop_category()
    {
        const string raw = """
            {
              "top_10": [
                { "rank": 1, "ticker": "ICT", "conviction_score": 80, "entry_price": 10, "stop_loss": 9, "target_price": 12, "thesis": "a" }
              ],
              "dropped_from_yesterday": [
                { "ticker": "RLC", "drop_category": "MOONSHOT", "reason": "n/a" }
              ]
            }
            """;

        var result = _parser.Parse(raw);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("drop_category", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ignores_a_model_that_still_echoes_rank_and_drift_status()
    {
        // Older prompts asked for rank and drift_status. A model that keeps sending them (with a
        // nonsense drift value) must not burn the one corrective retry on fields the server owns.
        const string raw = """
            {
              "top_10": [
                {
                  "rank": 9,
                  "ticker": "ICT",
                  "conviction_score": 80,
                  "entry_price": 10,
                  "stop_loss": 9,
                  "target_price": 12,
                  "thesis": "a",
                  "drift_status": "MOONSHOT",
                  "previous_rank": 42
                }
              ]
            }
            """;

        var result = _parser.Parse(raw);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Output!.Top10);
    }

    [Fact]
    public void Parse_fails_cleanly_when_there_is_no_json_object()
    {
        var result = _parser.Parse("I could not produce a list today.");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("No JSON object", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_fails_cleanly_on_an_empty_response()
    {
        var result = _parser.Parse("   ");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }
}
