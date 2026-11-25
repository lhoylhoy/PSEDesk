import type { Horizon } from '../types/signals';

/**
 * Copy shown in the terminal for each horizon. Kept in one place so the header, the
 * table, the tape strip and the ticker dialog stay consistent with the server-side
 * HorizonProfile mandates.
 */
export interface HorizonMeta {
  /** Short tab/word form, e.g. "Week". */
  label: string;
  /** Table heading, e.g. "Top 10 weekly buys". */
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
    bookTitle: 'Top 10 day trades',
    mandate:
      'Opening-range momentum inside the 09:30–15:35 PHT session. Plans are clamped to the PSE static band and flat by the closing auction.',
    stance: 'Intraday momentum',
    holding: 'same session',
    friendlyTab: 'Quick (Today)',
    friendlyTitle: 'Quick trades for today',
    friendlySummary:
      'You buy and sell on the same trading day. This is the fastest and riskiest choice — only use money you can afford to lose.',
  },
  WEEK: {
    label: 'Week',
    bookTitle: 'Top 10 weekly buys',
    mandate:
      'VCP and Stage-2 momentum holds of one to three weeks, sized for fee drag and board-lot granularity.',
    stance: 'Swing long',
    holding: '1–3 weeks',
    friendlyTab: 'Short-Term (1–3 Wks)',
    friendlyTitle: "Today's Best Short-Term Stock Picks (1–3 Weeks)",
    friendlySummary:
      'Hold for about one to three weeks. The desk looks for stocks in a steady uptrend that have paused briefly and are now turning back up.',
  },
  MONTH: {
    label: 'Month',
    bookTitle: 'Top 10 monthly books',
    mandate:
      'Quality-and-value compounders with improving estimates, rebalanced monthly around PH macro and sector rotation.',
    stance: 'Core accumulation',
    holding: '1–3 months',
    friendlyTab: 'Medium (1–3 Mos)',
    friendlyTitle: 'Best Medium-Term Stock Picks (1–3 Months)',
    friendlySummary:
      'Hold for one to three months. The desk favours solid, well-priced companies with improving prospects and a supportive Philippine backdrop.',
  },
  YEAR: {
    label: 'Year',
    bookTitle: 'Top 10 yearly compounders',
    mandate:
      'Multi-year compounders bought in Weinstein Stage 2 with dividend support, reviewed each quarter rather than each close.',
    stance: 'Compound',
    holding: '12+ months',
    friendlyTab: 'Long-Term (1+ Yr)',
    friendlyTitle: 'Best Long-Term Stocks to Grow Money (1+ Year)',
    friendlySummary:
      'Hold for a year or more. The desk looks for strong companies in long-term uptrends that also pay dividends, reviewed every quarter rather than every close.',
  },
};

