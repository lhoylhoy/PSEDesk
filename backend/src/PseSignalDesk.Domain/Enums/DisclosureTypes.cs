namespace PseSignalDesk.Domain.Enums;

/// <summary>
/// Deterministic classification of a PSE Edge filing or headline. The classifier maps
/// raw templates ("17-Q", "Stock Rights Offering", "Cash Dividends") onto this enum so
/// the prompt, the trap filter and the UI all speak the same vocabulary.
/// </summary>
public enum DisclosureCategory
{
    FORM_17Q,
    FORM_17A,
    FORM_17C,
    FORM_23B,
    DIVIDEND,
    SRO,
    FOO,
    TENDER_OFFER,
    SHARE_BUYBACK,
    TRADING_HALT,
    INDEX_REBALANCE,
    GENERAL
}

/// <summary>
/// Structural PSE traps that a purely technical screen cannot see. The desk treats the
/// first three as hard disqualifiers (see <c>knowledge-base/pse_rules.md</c>) and the
/// rest as prompt/UI warnings.
/// </summary>
public enum PseTrapFlag
{
    NONE,
    SRO_DILUTION,
    FOO_PLACEMENT_OVERHANG,
    TENDER_OFFER_PIN,
    EX_DIVIDEND_DROP,
    TRADING_HALT_RISK,
    STATIC_CEILING_CHASE,
    STATIC_FLOOR_BREAKDOWN
}
