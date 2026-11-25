using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// One deterministic classification of a PSE Edge headline.
/// </summary>
public sealed record DisclosureClassification(
    DisclosureCategory Category,
    PseTrapFlag TrapFlag,
    string Rationale);

/// <summary>
/// Maps raw PSE Edge filing templates and headlines onto the desk's disclosure
/// taxonomy and structural trap flags. Pure and keyword based, so the prompt, the
/// candidate filter and the UI always agree on what a filing means, and tests can pin
/// every rule without a network call.
/// </summary>
public static class PseDisclosureClassifier
{
    /// <summary>
    /// Flags that disqualify a candidate outright. A rights offering, a tender offer or a
    /// live trading halt can make a perfect chart untradeable, so the quant filter removes
    /// the name before the model ever sees it (see <c>knowledge-base/pse_rules.md</c>).
    /// </summary>
    public static bool IsHardDisqualifier(PseTrapFlag flag) =>
        flag is PseTrapFlag.SRO_DILUTION
            or PseTrapFlag.TENDER_OFFER_PIN
            or PseTrapFlag.TRADING_HALT_RISK;

    public static DisclosureClassification Classify(string? headline)
    {
        var text = (headline ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return new DisclosureClassification(DisclosureCategory.GENERAL, PseTrapFlag.NONE, "No headline text.");
        }

        if (ContainsAny(text, "trading halt", "trading suspension", "suspended", "suspension of trading", "halted"))
        {
            return new DisclosureClassification(
                DisclosureCategory.TRADING_HALT,
                PseTrapFlag.TRADING_HALT_RISK,
                "Trading halt or suspension language blocks new long exposure until the exchange resumes the name.");
        }

        if (ContainsAny(text, "stock rights", "rights offering", "rights offer", "rights issue", "sro"))
        {
            return new DisclosureClassification(
                DisclosureCategory.SRO,
                PseTrapFlag.SRO_DILUTION,
                "Stock rights offering: dilution and an ex-rights overhang make the chart unreliable.");
        }

        if (ContainsAny(text, "tender offer", "tender", "delisting tender"))
        {
            return new DisclosureClassification(
                DisclosureCategory.TENDER_OFFER,
                PseTrapFlag.TENDER_OFFER_PIN,
                "Tender offer: price pins near the offer and the float can collapse if acceptances pass the threshold.");
        }

        if (ContainsAny(
                text,
                "follow-on offering",
                "follow on offering",
                "follow-on",
                "private placement",
                "overnight placement",
                "overnight top-up",
                "top-up placement",
                "placement of shares",
                "block sale"))
        {
            return new DisclosureClassification(
                DisclosureCategory.FOO,
                PseTrapFlag.FOO_PLACEMENT_OVERHANG,
                "Follow-on or placement supply overhang: expect discounted paper and absorption pressure near the offer price.");
        }

        if (ContainsAny(text, "cash dividend", "cash dividends", "dividend", "ex-dividend", "ex-date"))
        {
            return new DisclosureClassification(
                DisclosureCategory.DIVIDEND,
                PseTrapFlag.EX_DIVIDEND_DROP,
                "Dividend event: the ex-date markdown is mechanical, not a technical breakdown, and buying cum-date for yield is not free.");
        }

        if (ContainsAny(text, "share buy-back", "share buyback", "buyback", "buy-back", "treasury shares"))
        {
            return new DisclosureClassification(
                DisclosureCategory.SHARE_BUYBACK,
                PseTrapFlag.NONE,
                "Share buy-back program: supportive demand, not a structural trap.");
        }

        if (ContainsAny(text, "index inclusion", "index deletion", "rebalancing", "psei rebalancing", "free-float"))
        {
            return new DisclosureClassification(
                DisclosureCategory.INDEX_REBALANCE,
                PseTrapFlag.NONE,
                "Index review: passive flows can distort the print near the effective date.");
        }

        if (ContainsAny(text, "23-b", "23b", "beneficial ownership", "statement of ownership"))
        {
            return new DisclosureClassification(
                DisclosureCategory.FORM_23B,
                PseTrapFlag.NONE,
                "Ownership filing (SEC Form 23-B).");
        }

        if (ContainsAny(text, "17-q", "17q", "quarterly report", "quarterly results", "quarterly financial"))
        {
            return new DisclosureClassification(
                DisclosureCategory.FORM_17Q,
                PseTrapFlag.NONE,
                "Quarterly report (SEC Form 17-Q).");
        }

        if (ContainsAny(text, "17-a", "17a", "annual report", "annual financial statements"))
        {
            return new DisclosureClassification(
                DisclosureCategory.FORM_17A,
                PseTrapFlag.NONE,
                "Annual report (SEC Form 17-A).");
        }

        if (ContainsAny(text, "17-c", "17c", "material information", "press release", "investor briefing"))
        {
            return new DisclosureClassification(
                DisclosureCategory.FORM_17C,
                PseTrapFlag.NONE,
                "Current report (SEC Form 17-C): needs an explicit catalyst read.");
        }

        return new DisclosureClassification(
            DisclosureCategory.GENERAL,
            PseTrapFlag.NONE,
            "Unclassified announcement.");
    }

    /// <summary>
    /// Classifies a disclosure list and returns the distinct trap flags in enum order.
    /// </summary>
    public static IReadOnlyList<PseTrapFlag> DetectTrapFlags(IEnumerable<StockDisclosure>? disclosures)
    {
        if (disclosures is null)
        {
            return Array.Empty<PseTrapFlag>();
        }

        var flags = new HashSet<PseTrapFlag>();

        foreach (var disclosure in disclosures)
        {
            var classification = Classify(disclosure.Headline);
            if (classification.TrapFlag != PseTrapFlag.NONE)
            {
                flags.Add(classification.TrapFlag);
            }
        }

        return flags.OrderBy(flag => flag).ToList();
    }

    public static bool HasHardDisqualifier(IEnumerable<PseTrapFlag>? flags) =>
        flags is not null && flags.Any(IsHardDisqualifier);

    public static string Tag(DisclosureCategory category) => $"[{category}]";

    public static string Describe(PseTrapFlag flag) => flag switch
    {
        PseTrapFlag.NONE => "No structural trap detected.",
        PseTrapFlag.SRO_DILUTION =>
            "Stock rights offering: dilution plus an ex-rights overhang. Avoid from announcement through the listing of the rights shares.",
        PseTrapFlag.FOO_PLACEMENT_OVERHANG =>
            "Follow-on or placement: discounted supply is likely to cap the tape until the paper is absorbed.",
        PseTrapFlag.TENDER_OFFER_PIN =>
            "Tender offer: the price pins near the offer and public-float rules can suspend trading after acceptances.",
        PseTrapFlag.EX_DIVIDEND_DROP =>
            "Ex-dividend markdown: the drop is mechanical. Compare it with the declared dividend before treating it as a stop-out.",
        PseTrapFlag.TRADING_HALT_RISK =>
            "Trading halt or suspension risk: the name may not be orderable, so no new long is proposed.",
        PseTrapFlag.STATIC_CEILING_CHASE =>
            "Static +50% ceiling: chasing here leaves no room inside the exchange's daily price band.",
        PseTrapFlag.STATIC_FLOOR_BREAKDOWN =>
            "Static -30% floor: the name is at the bottom of the daily price band and longs are unproven.",
        _ => flag.ToString()
    };

    private static bool ContainsAny(string text, params string[] phrases) =>
        phrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}
