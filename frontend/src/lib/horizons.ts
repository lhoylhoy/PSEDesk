import type { Horizon } from '../types/signals';

/**
 * Copy shown in the terminal for each horizon. Kept in one place so the header, the
 * table, the tape strip and the ticker dialog stay consistent with the server-side
 * HorizonProfile mandates.
 */
export interface HorizonMeta {
  /** Short tab/word form, e.g. "Week". */
  label: string;
  /** Table heading, e.g. "Week model · 10 names screened". */
  bookTitle: string;
  /** One-line mandate under the heading. */
  mandate: string;
  /** Tape-strip "desk stance" tile. */
  stance: string;
  /** Holding period phrasing for the ticker dialog. */
  holding: string;
  /** Simple-mode tab label, e.g. "Quick (Today)". */
  friendlyTab: string;
  /** Simple-mode heading in plain English. */
  friendlyTitle: string;
  /** Simple-mode holding guidance without quant jargon. */
  friendlySummary: string;
}

export const horizonMeta: Record<Horizon, HorizonMeta> = {
  DAY: {
    label: 'Day',
    bookTitle: 'Day model · 10 names screened',
    mandate:
      'Opening-range momentum inside the 09:30–15:35 PHT session. Plans are clamped to the PSE static band and flat by the closing auction.',
    stance: 'Intraday momentum',
    holding: 'same session',
    friendlyTab: 'Quick (Today)',
    friendlyTitle: 'Intraday names screened (same day)',
    friendlySummary:
      'Entries and exits land on the same trading day. This is the fastest and riskiest horizon — the model only ever sizes money you can afford to lose.',
  },
  WEEK: {
    label: 'Week',
    bookTitle: 'Week model · 10 names screened',
    mandate:
      'VCP and Stage-2 momentum holds of one to three weeks, sized for fee drag and board-lot granularity.',
    stance: 'Swing long',
    holding: '1–3 weeks',
    friendlyTab: 'Short-Term (1–3 Wks)',
    friendlyTitle: 'Short-term names screened (1–3 weeks)',
    friendlySummary:
      'Planned holding is about one to three weeks. The model looks for stocks in a steady uptrend that have paused briefly and are now turning back up.',
  },
  MONTH: {
    label: 'Month',
    bookTitle: 'Month model · 10 names screened',
    mandate:
      'Quality-and-value compounders with improving estimates, rebalanced monthly around PH macro and sector rotation.',
    stance: 'Core accumulation',
    holding: '1–3 months',
    friendlyTab: 'Medium (1–3 Mos)',
    friendlyTitle: 'Medium-term names screened (1–3 months)',
    friendlySummary:
      'Planned holding is one to three months. The model favours solid, well-priced companies with improving prospects and a supportive Philippine backdrop.',
  },
  YEAR: {
    label: 'Year',
    bookTitle: 'Year model · 10 names screened',
    mandate:
      'Multi-year compounders bought in Weinstein Stage 2 with dividend support, reviewed each quarter rather than each close.',
    stance: 'Compound',
    holding: '12+ months',
    friendlyTab: 'Long-Term (1+ Yr)',
    friendlyTitle: 'Long-term names screened (1+ year)',
    friendlySummary:
      'Planned holding is a year or more. The model looks for strong companies in long-term uptrends that also pay dividends, reviewed every quarter rather than every close.',
  },
};

