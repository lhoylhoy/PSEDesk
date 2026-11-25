using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// Locks the keyword rules that map PSE Edge headlines onto the disclosure taxonomy and
/// the structural trap flags. These are the rules the quant filter uses to drop a name
/// before the model ever ranks it, so a regression here silently re-admits dilution risk.
/// </summary>
public sealed class PseDisclosureClassifierTests
{
    [Theory]
    [InlineData("Notice of Stock Rights Offering", DisclosureCategory.SRO, PseTrapFlag.SRO_DILUTION)]
    [InlineData("Suspension of trading pending clarification", DisclosureCategory.TRADING_HALT, PseTrapFlag.TRADING_HALT_RISK)]
    [InlineData("Tender offer for all outstanding common shares", DisclosureCategory.TENDER_OFFER, PseTrapFlag.TENDER_OFFER_PIN)]
    [InlineData("Follow-on offering of 200 million shares", DisclosureCategory.FOO, PseTrapFlag.FOO_PLACEMENT_OVERHANG)]
    [InlineData("Declaration of cash dividends", DisclosureCategory.DIVIDEND, PseTrapFlag.EX_DIVIDEND_DROP)]
    [InlineData("Share buy-back programme", DisclosureCategory.SHARE_BUYBACK, PseTrapFlag.NONE)]
    [InlineData("PSEi rebalancing effective 1 December", DisclosureCategory.INDEX_REBALANCE, PseTrapFlag.NONE)]
    [InlineData("Statement of beneficial ownership (SEC Form 23-B)", DisclosureCategory.FORM_23B, PseTrapFlag.NONE)]
    [InlineData("Quarterly report (SEC Form 17-Q)", DisclosureCategory.FORM_17Q, PseTrapFlag.NONE)]
    [InlineData("Annual report (SEC Form 17-A)", DisclosureCategory.FORM_17A, PseTrapFlag.NONE)]
    [InlineData("Press release: investor briefing deck", DisclosureCategory.FORM_17C, PseTrapFlag.NONE)]
    public void Classify_maps_a_headline_onto_the_disclosure_taxonomy(
        string headline,
        DisclosureCategory expectedCategory,
        PseTrapFlag expectedFlag)
    {
        var classification = PseDisclosureClassifier.Classify(headline);

        Assert.Equal(expectedCategory, classification.Category);
        Assert.Equal(expectedFlag, classification.TrapFlag);
        Assert.False(string.IsNullOrWhiteSpace(classification.Rationale));
    }

    [Fact]
    public void Classify_treats_a_missing_headline_as_an_unclassified_filing()
    {
        var classification = PseDisclosureClassifier.Classify("   ");

        Assert.Equal(DisclosureCategory.GENERAL, classification.Category);
        Assert.Equal(PseTrapFlag.NONE, classification.TrapFlag);
    }

    [Theory]
    [InlineData(PseTrapFlag.SRO_DILUTION, true)]
    [InlineData(PseTrapFlag.TENDER_OFFER_PIN, true)]
    [InlineData(PseTrapFlag.TRADING_HALT_RISK, true)]
    [InlineData(PseTrapFlag.FOO_PLACEMENT_OVERHANG, false)]
    [InlineData(PseTrapFlag.EX_DIVIDEND_DROP, false)]
    [InlineData(PseTrapFlag.NONE, false)]
    public void IsHardDisqualifier_disqualifies_only_dilution_pin_and_halt(
        PseTrapFlag flag,
        bool expected)
    {
        Assert.Equal(expected, PseDisclosureClassifier.IsHardDisqualifier(flag));
    }

    [Fact]
    public void DetectTrapFlags_dedupes_and_orders_flags_by_severity()
    {
        var disclosures = new[]
        {
            new StockDisclosure(new DateOnly(2026, 11, 20), "Declaration of cash dividends", "DIVIDEND"),
            new StockDisclosure(new DateOnly(2026, 11, 21), "Notice of Stock Rights Offering", "SRO"),
            new StockDisclosure(new DateOnly(2026, 11, 22), "Update on the stock rights offer timetable", "SRO"),
        };

        var flags = PseDisclosureClassifier.DetectTrapFlags(disclosures);

        Assert.Equal(new[] { PseTrapFlag.SRO_DILUTION, PseTrapFlag.EX_DIVIDEND_DROP }, flags);
        Assert.True(PseDisclosureClassifier.HasHardDisqualifier(flags));
        Assert.Empty(PseDisclosureClassifier.DetectTrapFlags(null));
    }

    [Fact]
    public void Describe_explains_every_flag_in_trader_language()
    {
        foreach (var flag in Enum.GetValues<PseTrapFlag>())
        {
            Assert.False(string.IsNullOrWhiteSpace(PseDisclosureClassifier.Describe(flag)));
        }
    }
}
