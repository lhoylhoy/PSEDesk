using Microsoft.Extensions.Configuration;
using PseSignalDesk.Infrastructure.Configuration;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// The market-data options are the only place where a configuration list is appended to a list the
/// class already initialized. These tests pin the normalization that keeps the index resolution
/// and the heavyweight proxy honest when a deployed section repeats the defaults.
/// </summary>
public sealed class MarketDataOptionsTests
{
    [Fact]
    public void Normalize_drops_the_entries_a_repeated_configuration_section_appends()
    {
        var options = new MarketDataOptions();

        // What ConfigurationBinder produces for the shipped appsettings.json: the class defaults
        // plus every configured entry, i.e. 2 index symbols and 24 constituents for 12 names.
        options.IndexYahooSymbols.Add("PSEI.PS");
        options.IndexProxyConstituents = options.IndexProxyConstituents
            .Concat(options.IndexProxyConstituents)
            .ToList();

        options.Normalize();

        Assert.Equal(new[] { "PSEI.PS" }, options.IndexYahooSymbols);
        Assert.Equal(12, options.IndexProxyConstituents.Count);
        Assert.Equal(
            options.IndexProxyConstituents.Count,
            options.IndexProxyConstituents.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Normalize_undoes_the_doubling_a_real_configuration_section_causes()
    {
        // The root cause, reproduced with the framework's own binder and the same shape the shipped
        // appsettings.json has: the class defaults stay and every configured entry is added on top.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ConfiguredMarketDataSection())
            .Build();

        var options = new MarketDataOptions();
        configuration.GetSection(MarketDataOptions.SectionName).Bind(options);

        // The framework appends instead of replacing: 12 code defaults + 12 configured entries.
        Assert.Equal(2, options.IndexYahooSymbols.Count);
        Assert.Equal(24, options.IndexProxyConstituents.Count);

        options.Normalize();

        Assert.Equal(new[] { "PSEI.PS" }, options.IndexYahooSymbols);
        Assert.Equal(
            new[] { "TEL", "SM", "SMPH", "ALI", "BDO", "BPI", "ICT", "JFC", "MER", "AC", "URC", "GLO" },
            options.IndexProxyConstituents);
    }

    private static Dictionary<string, string?> ConfiguredMarketDataSection()
    {
        var section = new Dictionary<string, string?>
        {
            ["MarketData:IndexYahooSymbols:0"] = "PSEI.PS"
        };

        string[] constituents =
            { "TEL", "SM", "SMPH", "ALI", "BDO", "BPI", "ICT", "JFC", "MER", "AC", "URC", "GLO" };

        for (var index = 0; index < constituents.Length; index++)
        {
            section[$"MarketData:IndexProxyConstituents:{index}"] = constituents[index];
        }

        return section;
    }

    [Fact]
    public void Normalize_keeps_the_configured_order_and_accepts_an_extra_name()
    {
        var options = new MarketDataOptions
        {
            IndexProxyConstituents = new List<string> { "TEL", " sm ", "TEL", "", "  ", "MBT" }
        };

        options.Normalize();

        Assert.Equal(new[] { "TEL", "sm", "MBT" }, options.IndexProxyConstituents);
    }

    [Fact]
    public void DistinctNonBlank_is_case_insensitive_and_never_null()
    {
        Assert.Empty(MarketDataOptions.DistinctNonBlank(null));

        // The first spelling wins, so a duplicate in different casing never changes a symbol.
        Assert.Equal(
            new[] { "PHP=X", "PSEI.PS" },
            MarketDataOptions.DistinctNonBlank(new[] { "PHP=X", "PSEI.PS", " php=x ", "psei.ps" }));
    }
}
