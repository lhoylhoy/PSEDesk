import type {
  DriftStatus,
  EdgeVerdict,
  EntryTimingStatus,
  MarketCall,
  MarketRegime,
  PositionAdvice,
  SignalItem,
  SignalOutcomeStatus,
  TrustPulse,
} from '../types/signals';
import type { PositionAction, PortfolioSettingsUpdate } from '../types/portfolio';
import { formatPercent, formatPrice } from './formatters.ts';
import { computeRiskSizing } from './riskSizing.ts';

/**
 * Deterministic plain-English translation layer.
 *
 * The desk's numbers are never recomputed here — this module only renames server-owned
 * enums and warnings and reformats the ticket it was given. Everything a reader sees in
 * Simple mode is traceable to a field the API published, which is why the technical
 * drawer can always show the raw figure behind a friendly sentence.
 */

export interface PlainVerdict {
  /** Badge text: "Qualifies (Full)", "Qualifies (Half)", "Watch Only". */
  label: string;
  /** One-line reason to show under the badge. */
  explanation: string;
  /** Tailwind text colour for the verdict. */
  tone: string;
  /** True for TRADE / SMALL_SIZE — a name the model would actually size. */
  buyable: boolean;
}

const verdictCopy: Record<EdgeVerdict, PlainVerdict> = {
  TRADE: {
    label: 'Qualifies (Full)',
    explanation: 'The setup clears the fee-adjusted edge test at the model’s full size.',
    tone: 'text-mint-400',
    buyable: true,
  },
  SMALL_SIZE: {
    label: 'Qualifies (Half)',
    explanation: 'The setup clears the edge test at half size — the margin over fees is thinner.',
    tone: 'text-amber-400',
    buyable: true,
  },
  WATCH_ONLY: {
    label: 'Watch Only',
    explanation: 'The reward does not clear broker fees at this price, so the model sizes it at zero.',
    tone: 'text-slate-400',
    buyable: false,
  },
};

export function describePlainVerdict(verdict: EdgeVerdict | string): PlainVerdict {
  return verdictCopy[verdict as EdgeVerdict] ?? verdictCopy.WATCH_ONLY;
}

/** Badge styling shared by the friendly verdict pill. */
export function plainVerdictBadgeClass(verdict: EdgeVerdict | string): string {
  switch (verdict as EdgeVerdict) {
    case 'TRADE':
      return 'border-mint-400/40 bg-mint-400/10 text-mint-400';
    case 'SMALL_SIZE':
      return 'border-amber-400/40 bg-amber-400/10 text-amber-400';
    default:
      return 'border-slate-500/40 bg-slate-500/10 text-slate-400';
  }
}

export interface PlanPercentages {
  /** Entry → target move, as a positive percentage. */
  upsidePct: number | null;
  /** Entry → cut-loss move, as a negative percentage. */
  downsidePct: number | null;
}

/** Plain price-distance percentages for the 3-step Buy / Target / Cut-Loss strip. */
export function computePlanPercentages(
  entryPrice: number,
  targetPrice: number,
  stopLoss: number,
): PlanPercentages {
  const upsidePct =
    entryPrice > 0 && targetPrice > 0 ? ((targetPrice - entryPrice) / entryPrice) * 100 : null;
  const downsidePct =
    entryPrice > 0 && stopLoss > 0 ? ((stopLoss - entryPrice) / entryPrice) * 100 : null;

  return { upsidePct, downsidePct };
}

export interface PlainOrderGuide {
  shares: number;
  lots: number;
  boardLot: number;
  /** All-in cash the order needs, including the PSE buy-side fee when the ticket is server-sized. */
  cashNeededPhp: number;
  /** Net Peso gain if the target is hit after both fee legs; null when a legacy row has no ticket. */
  netGainAtTargetPhp: number | null;
  /** Net Peso loss if the cut-loss is hit after both fee legs. */
  netLossAtStopPhp: number;
  /** Stop price that makes the round trip exactly flat after fees; null without a ticket. */
  breakEvenStopPrice: number | null;
  /** Plain instruction for placing the order. */
  orderTip: string;
  /** Server warnings, translated out of desk jargon. */
  warnings: string[];
  /** True when the desk would fund at least one board lot. */
  actionable: boolean;
  /** True when the figures came from the server order ticket. */
  serverSized: boolean;
}

/** Turns the server order ticket into a Peso-sized, jargon-free buying guide. */
export function describeOrderGuide(item: SignalItem): PlainOrderGuide {
  const sizing = computeRiskSizing(item);
  const verdict = describePlainVerdict(item.edgeVerdict);

  return {
    shares: sizing.shares,
    lots: sizing.lots,
    boardLot: sizing.boardLot,
    cashNeededPhp: sizing.buyOutlayPhp,
    netGainAtTargetPhp: sizing.netRewardPhp,
    netLossAtStopPhp: sizing.netRiskPhp,
    breakEvenStopPrice: sizing.breakEvenStopPrice,
    orderTip: describeExecutionGuidance(sizing.executionGuidance, verdict.buyable),
    warnings: translateTicketWarnings(sizing.warnings),
    actionable: verdict.buyable && sizing.shares > 0,
    serverSized: sizing.serverSized,
  };
}

/** How to place the order, in the words a retail buyer would use. */
export function describeExecutionGuidance(
  guidance: string | null | undefined,
  buyable = true,
): string {
  switch (guidance) {
    case 'LIQUID_MARKETABLE':
      return 'Easy to buy near the current market price.';
    case 'LIMIT_ONLY':
    case 'THIN_LIMIT_ONLY':
      return 'Place a limit order at the buy price — avoid market orders.';
    case 'STAGED_ENTRY':
      return "Split your buy into smaller orders so you don't push the price up.";
    case 'WORK_THE_ORDER':
      return 'Work the order slowly in small pieces; one big order would move the price.';
    case 'NO_TRADE':
      return 'The desk would not trade this size today — wait for a better entry.';
    default:
      return buyable
        ? 'Use a limit order at the plan price if your broker supports it.'
        : 'Watch only for now; the model sizes no position at this price.';
  }
}

/**
 * Maps the server's order-ticket warnings onto plain sentences. The raw warning stays
 * available in the technical drawer, so this never hides a constraint.
 */
export function translateTicketWarnings(warnings: readonly string[]): string[] {
  return warnings.map((warning) => translateTicketWarning(warning));
}

function translateTicketWarning(warning: string): string {
  const text = warning.trim();

  if (/Portfolio heat is at/i.test(text)) {
    return 'You already have a lot of money at risk today, so this trade is smaller than usual.';
  }

  if (/SMALL_SIZE halves/i.test(text)) {
    return 'The desk deliberately halves the size because the edge is thinner.';
  }

  if (/WATCH_ONLY carries zero/i.test(text)) {
    return 'This is a watch-only idea, not a buy order.';
  }

  if (/sector cap is already fully used/i.test(text)) {
    return 'You already hold as much as the desk allows in this sector.';
  }

  if (/sector cap leaves/i.test(text)) {
    return 'The limit on how much you can hold in one sector caps this order.';
  }

  if (/positions are already open/i.test(text)) {
    return 'You already hold the maximum number of stocks the desk allows in this sector.';
  }

  if (/No averaging down/i.test(text)) {
    return 'You already own this stock at a higher price, and the desk will not average down.';
  }

  if (/Free cash of .* cannot fund/i.test(text)) {
    return 'Your available cash is not enough for one board lot at this price.';
  }

  if (/Free cash of .* caps/i.test(text)) {
    return 'Your available cash limits how many shares you can buy.';
  }

  if (/single-position cap caps/i.test(text)) {
    return 'The limit on how much one stock may take up caps this order.';
  }

  if (/Capped at .* of the .* 20-day turnover/i.test(text)) {
    return "The order is capped so you stay a small part of the stock's normal daily volume.";
  }

  if (/Equity of .* cannot fund/i.test(text)) {
    return 'Your account is not big enough to buy one board lot at this price.';
  }

  if (/hard limit: new risk is halted/i.test(text)) {
    return 'The paper account is in a deep drawdown, so new trades are paused.';
  }

  if (/soft limit .* per-trade risk is scaled/i.test(text)) {
    return 'The paper account is in a drawdown, so this trade is smaller while it recovers.';
  }

  if (/Realised PSEi volatility/i.test(text)) {
    return 'The market has been unusually jumpy, so the position is smaller.';
  }

  if (/risk budget is exhausted/i.test(text)) {
    return 'There is no risk budget left for a new position.';
  }

  return text;
}



export interface MarketMood {
  headline: string;
  explanation: string;
  /** Badge text: "Healthy", "Mixed", "Weak". */
  badge: string;
  badgeClass: string;
  tone: string;
}

const moodStyles: Record<MarketRegime, Pick<MarketMood, 'badge' | 'badgeClass' | 'tone'>> = {
  BULLISH: {
    badge: 'Healthy',
    badgeClass: 'border-mint-400/40 bg-mint-400/10 text-mint-400',
    tone: 'text-mint-400',
  },
  NEUTRAL: {
    badge: 'Mixed',
    badgeClass: 'border-slate-500/40 bg-slate-500/10 text-slate-300',
    tone: 'text-slate-300',
  },
  BEARISH: {
    badge: 'Weak',
    badgeClass: 'border-rose-400/40 bg-rose-400/10 text-rose-400',
    tone: 'text-rose-400',
  },
};

/** Plain-English reading of the PSEi regime plus what it means for a regular buyer. */
export function describeMarketMood(regime: MarketRegime | string): MarketMood {
  const style = moodStyles[regime as MarketRegime] ?? moodStyles.NEUTRAL;

  switch (regime as MarketRegime) {
    case 'BULLISH':
      return {
        ...style,
        headline: 'Healthy tape — long setups work more often',
        explanation:
          'The overall PSE index is trending up, which is the friendlier backdrop for long setups. The model still ranks every name and sizes each one on its own edge.',
      };
    case 'BEARISH':
      return {
        ...style,
        headline: 'Downtrend — the model stands in cash',
        explanation:
          'The overall PSE index is trending down. Fewer setups clear their fees in a falling market, so the model keeps more cash and sizes smaller.',
      };
    default:
      return {
        ...style,
        headline: 'Mixed market — only the clearest setups qualify',
        explanation:
          'The PSE index is not clearly trending either way, so only the strongest setups clear their fees and the model keeps cash aside.',
      };
  }
}

/** What the recommended cash level means in practice. */
export function describeCashAdvice(recommendedCashPct: number | null | undefined): string {
  if (recommendedCashPct == null || !Number.isFinite(recommendedCashPct)) {
    return 'Run today’s advice to see how much cash the desk suggests keeping.';
  }

  if (recommendedCashPct >= 75) {
    return 'The model stays mostly in cash at this level; only the very best setups qualify.';
  }

  if (recommendedCashPct >= 50) {
    return 'The model keeps more than half of the account in cash and accepts few setups.';
  }

  if (recommendedCashPct >= 25) {
    return 'The model keeps a cash buffer while the stronger setups qualify.';
  }

  return 'The model deploys most of the account into the top setups, each with its cut-loss set.';
}

/** Plain words for the conviction score. */
export function describeConviction(score: number): string {
  if (score >= 80) {
    return 'Very high confidence';
  }

  if (score >= 70) {
    return 'High confidence';
  }

  if (score >= 60) {
    return 'Moderate confidence';
  }

  return 'Low confidence';
}

/** Plain words for the win probability against the fee-adjusted break-even rate. */
export function describeWinChance(winProbability: number, breakEvenWinRate: number): string {
  if (!(winProbability > 0)) {
    return 'No estimate stored for this run — re-run the advice to price it.';
  }

  if (winProbability >= breakEvenWinRate + 0.1) {
    return 'Good odds — clearly better than break-even after fees.';
  }

  if (winProbability >= breakEvenWinRate) {
    return 'Slight edge over break-even after fees.';
  }

  return 'Below break-even after fees — not worth the risk right now.';
}

const driftCopy: Record<DriftStatus, string> = {
  NEW: 'New pick',
  HELD: 'Steady',
  UPGRADED: 'Moved up',
  DOWNGRADED: 'Moved down',
};

export function describeDriftStatus(status: DriftStatus | string): string {
  return driftCopy[status as DriftStatus] ?? 'Updated';
}

/** Friendly label for a disclosure/trap flag. */
export function describeTrapFlag(flag: string): string {
  switch (flag) {
    case 'SRO_DILUTION':
      return 'New shares from a rights offer may dilute the price';
    case 'TENDER_OFFER_PIN':
      return 'A tender offer may hold the price near the offer level';
    case 'TRADING_HALT_RISK':
      return 'Trading may be suspended';
    case 'FOO_PLACEMENT_OVERHANG':
      return 'New share offering may cap the price';
    case 'EX_DIVIDEND_DROP':
      return 'Recent cash dividend adjustment (a normal drop)';
    case 'GOVERNANCE_RISK':
      return 'A governance concern appeared in recent filings';
    case 'EARNINGS_REVERSAL':
      return 'Recent earnings turned weaker';
    case 'NONE':
      return 'No company-specific caution flagged';
    default:
      return flag.replaceAll('_', ' ').toLowerCase();
  }
}

/** Friendly label for a PSE Edge disclosure category. */
export function describeDisclosureCategory(category: string): string {
  switch (category) {
    case 'FORM_17Q':
      return 'Quarterly Financial Report';
    case 'FORM_17A':
      return 'Annual Financial Report';
    case 'FORM_17C':
      return 'Quarterly Report (17-C)';
    case 'FORM_23B':
      return 'Material Information';
    case 'DIVIDEND':
      return 'Dividend Announcement';
    case 'SRO':
      return 'Stock Rights Offering';
    case 'FOO':
      return 'Follow-On Offering';
    case 'TENDER_OFFER':
      return 'Tender Offer';
    case 'SHARE_BUYBACK':
      return 'Share Buyback';
    case 'TRADING_HALT':
      return 'Trading Halt';
    case 'INDEX_REBALANCE':
      return 'Index Rebalance';
    case 'GENERAL':
      return 'Company Announcement';
    default:
      return category.replaceAll('_', ' ').toLowerCase();
  }
}

export interface PlainPositionAction {
  label: string;
  description: string;
  tone: string;
  /** True when the desk says something must be done now. */
  urgent: boolean;
}

const positionActionCopy: Record<string, PlainPositionAction> = {
  HOLD: {
    label: 'Hold — plan on track',
    description: 'The plan is unchanged; no level has been touched.',
    tone: 'text-slate-300',
    urgent: false,
  },
  STOP_BREACHED: {
    label: 'Cut-loss level breached',
    description:
      'The price fell through the cut-loss. By the model’s exit rule the position is closed here, which caps the loss at the planned risk.',
    tone: 'text-rose-400',
    urgent: true,
  },
  TARGET_REACHED: {
    label: 'Target reached',
    description: 'The price reached the target, so the model’s exit rule banks the planned gain here.',
    tone: 'text-mint-400',
    urgent: true,
  },
  MOVE_STOP_TO_BREAKEVEN: {
    label: 'Break-even stop available',
    description: 'A stop at the break-even price takes the trade’s fee-adjusted loss to zero.',
    tone: 'text-amber-400',
    urgent: false,
  },
  TRAIL_STOP_EMA20: {
    label: 'Trail the stop',
    description: 'The model’s trail follows the rising 20-day average and protects the open gain.',
    tone: 'text-amber-400',
    urgent: false,
  },
  TIME_STOP_DUE: {
    label: 'Holding window ended — review',
    description: 'The planned holding period has passed, so the rule asks for a fresh look at the name.',
    tone: 'text-sky-400',
    urgent: false,
  },
};

export function describePositionAction(action: PositionAction | string): PlainPositionAction {
  return (
    positionActionCopy[action] ?? {
      label: action.replaceAll('_', ' ').toLowerCase(),
      description: 'The desk published this instruction for the open position.',
      tone: 'text-slate-400',
      urgent: false,
    }
  );
}

/** Friendly label for a graded pick's outcome. */
export function describeOutcomeStatus(status: SignalOutcomeStatus | string): string {
  switch (status) {
    case 'WIN_TARGET':
      return 'Hit target';
    case 'LOSS_STOP':
      return 'Hit cut-loss';
    case 'EXPIRED_WIN':
      return 'Finished higher';
    case 'EXPIRED_LOSS':
      return 'Finished lower';
    case 'OPEN':
      return 'Still open';
    case 'UNFILLED':
      return 'Entry never filled';
    default:
      return status.replaceAll('_', ' ').toLowerCase();
  }
}

/** Friendly label for the book's drawdown posture. */
export function describeRiskPosture(posture: string): string {
  switch (posture) {
    case 'NORMAL':
      return 'Normal — full risk budget available';
    case 'DEFENSIVE':
      return 'Defensive — new trades are smaller until the book recovers';
    case 'HALTED':
      return 'Paused — no new trades until the drawdown recovers';
    default:
      return 'Risk posture reported by the desk';
  }
}


// ---------------------------------------------------------------------------------------
// Money-plan presets. These are ordinary account settings the API already validates and
// clamps; picking one only fills the same numbers a user could type by hand. No sizing,
// odds or Peso figure is computed in the browser.
// ---------------------------------------------------------------------------------------

export type SafetyPresetId = 'careful' | 'balanced' | 'active';

export interface SafetyPreset {
  id: SafetyPresetId;
  /** Card title: "Careful", "Balanced", "Active". */
  label: string;
  /** One line under the title. */
  tagline: string;
  /** What it means in practice. */
  detail: string;
  settings: PortfolioSettingsUpdate;
}

export const SAFETY_PRESETS: SafetyPreset[] = [
  {
    id: 'careful',
    label: 'Careful',
    tagline: 'Smallest trades, slowest to lose',
    detail:
      'Risks 0.5% of your money per trade, keeps at most three trades open and pauses early after a losing streak. Best if you are new to stocks.',
    settings: {
      maxRiskPerTradePct: 0.5,
      smallSizeRiskMultiplier: 0.5,
      maxPositionPct: 10,
      maxPortfolioHeatPct: 3,
      maxSectorExposurePct: 20,
      maxPositionsPerSector: 2,
      softDrawdownThresholdPct: 5,
      hardDrawdownThresholdPct: 10,
      targetAnnualVolPct: 12,
    },
  },
  {
    id: 'balanced',
    label: 'Balanced',
    tagline: 'The desk default',
    detail:
      'Risks 1% of your money per trade with a 6% total risk limit. A middle ground between protecting your money and giving it room to grow.',
    settings: {
      maxRiskPerTradePct: 1,
      smallSizeRiskMultiplier: 0.5,
      maxPositionPct: 20,
      maxPortfolioHeatPct: 6,
      maxSectorExposurePct: 30,
      maxPositionsPerSector: 3,
      softDrawdownThresholdPct: 8,
      hardDrawdownThresholdPct: 15,
      targetAnnualVolPct: 15,
    },
  },
  {
    id: 'active',
    label: 'Active',
    tagline: 'Bigger trades, tighter limits',
    detail:
      'Risks 2% of your money per trade and allows more trades at once. Bigger swings both ways — only after you have practised with the paper account.',
    settings: {
      maxRiskPerTradePct: 2,
      smallSizeRiskMultiplier: 0.5,
      maxPositionPct: 25,
      maxPortfolioHeatPct: 10,
      maxSectorExposurePct: 40,
      maxPositionsPerSector: 4,
      softDrawdownThresholdPct: 10,
      hardDrawdownThresholdPct: 20,
      targetAnnualVolPct: 18,
    },
  },
];

/** Quick-fill amounts for the "how much play money" question, in pesos. */
export const BUDGET_PRESETS_PHP = [25_000, 50_000, 100_000, 250_000, 500_000];

/**
 * Which preset the account is currently on, or null when the numbers were customised. Used
 * only to highlight a card — the settings themselves stay server-owned.
 */
export function safetyPresetForSettings(
  settings: Partial<PortfolioSettingsUpdate>,
  tolerance = 0.01,
): SafetyPreset | null {
  return (
    SAFETY_PRESETS.find((preset) =>
      (Object.keys(preset.settings) as (keyof PortfolioSettingsUpdate)[]).every((key) => {
        const expected = preset.settings[key];
        const actual = settings[key];

        return (
          typeof expected === 'number' &&
          typeof actual === 'number' &&
          Math.abs(expected - actual) <= tolerance
        );
      }),
    ) ?? null
  );
}


// ---------------------------------------------------------------------------------------
// Friendly failures. The API already returns human problem details, but the two cases a
// regular reader hits most — an offline desk and a feed that has not published yet — need a
// sentence that says what to do instead of a problem title.
// ---------------------------------------------------------------------------------------

export interface FriendlyError {
  /** Short heading for the banner. */
  title: string;
  /** What happened and what to do next. */
  message: string;
  /** The raw API text, kept for the collapsible technical-details line. */
  technical: string | null;
  /** True when pressing Retry later could plausibly work. */
  retryable: boolean;
}

interface ErrorLike {
  message?: unknown;
  status?: unknown;
  title?: unknown;
}

function readError(caught: unknown): {
  status: number | null;
  title: string | null;
  message: string | null;
} {
  if (typeof caught === 'string') {
    return { status: null, title: null, message: caught };
  }

  if (typeof caught !== 'object' || caught === null) {
    return { status: null, title: null, message: null };
  }

  const error = caught as ErrorLike;
  const title = typeof error.title === 'string' && error.title.trim().length > 0 ? error.title : null;
  const message =
    typeof error.message === 'string' && error.message.trim().length > 0 ? error.message : null;

  return {
    status: typeof error.status === 'number' ? error.status : null,
    title,
    message,
  };
}

/** Turns any thrown value into a sentence a first-time investor can act on. */
export function describeFriendlyError(caught: unknown): FriendlyError {
  const { status, title, message } = readError(caught);
  const technical = message;

  if (status === 0) {
    return {
      title: 'The desk is offline',
      message:
        'We cannot reach the price server right now. Check your internet connection (or that the desk is running) and try again.',
      technical,
      retryable: true,
    };
  }

  if (status === 503) {
    return {
      title: 'Fresh prices are not available yet',
      message:
        'The desk could not download market prices. This usually means the trading day has not been processed yet or the price feed is briefly down — try again in a few minutes.',
      technical,
      retryable: true,
    };
  }

  if (status === 404) {
    return {
      title: 'We could not find that stock',
      message:
        message ??
        'That ticker is not listed on the PSE. Check the spelling, or search for the company name instead.',
      technical,
      retryable: false,
    };
  }

  if (status === 400) {
    return {
      title: 'That request was not accepted',
      message: message ?? 'Please check the details you entered and try again.',
      technical,
      retryable: false,
    };
  }

  if (status !== null) {
    return {
      title: title ?? 'Something went wrong',
      message: message ?? 'The desk could not complete that request. Please try again.',
      technical,
      retryable: status >= 500,
    };
  }

  return {
    title: 'Something went wrong',
    message: message ?? 'The desk could not complete that request. Please try again.',
    technical,
    retryable: true,
  };
}


// ---------------------------------------------------------------------------------------
// Plain readings of the desk's statistics.
// ---------------------------------------------------------------------------------------

/** What an average graded trade did, in R (one R = the money risked at the stop). */
export function describeExpectancyR(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return 'No graded trades yet, so there is no average to report.';
  }

  if (value > 0.05) {
    return 'On average each graded trade made money after broker fees.';
  }

  if (value >= -0.05) {
    return 'On average each graded trade ended roughly flat after broker fees.';
  }

  return 'On average each graded trade lost money after broker fees.';
}

/** Whether the winners covered the losers. */
export function describeProfitFactor(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) {
    return 'Not enough graded trades to compare winners with losers yet.';
  }

  if (value >= 1.5) {
    return 'Winners covered the losers comfortably.';
  }

  if (value >= 1) {
    return 'Winners only just covered the losers.';
  }

  return 'Winners did not cover the losers.';
}

/** Whether the desk's win forecasts have matched reality. */
export function describeBrierScore(
  value: number | null | undefined,
  sampleCount: number | null | undefined,
): string {
  if (typeof sampleCount !== 'number' || sampleCount <= 0) {
    return 'No forecasted pick has been graded yet.';
  }

  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return 'No forecasted pick has been graded yet.';
  }

  if (value < 0.2) {
    return 'The desk’s win forecasts have been accurate so far.';
  }

  if (value < 0.25) {
    return 'The desk’s win forecasts have been slightly better than a coin toss.';
  }

  return 'The desk’s win forecasts have been no better than a coin toss.';
}

/** What the hit rate has actually been. */
export function describeWinRate(
  winRate: number | null | undefined,
  sampleCount: number | null | undefined,
): string {
  if (
    typeof winRate !== 'number' ||
    !Number.isFinite(winRate) ||
    typeof sampleCount !== 'number' ||
    sampleCount <= 0
  ) {
    return 'Nothing has been graded yet.';
  }

  if (winRate >= 0.6) {
    return 'More than half of the graded picks worked out.';
  }

  if (winRate >= 0.45) {
    return 'About half of the graded picks worked out.';
  }

  return 'Fewer than half of the graded picks worked out.';
}

/** Explains a worst-case dip in the practice account. */
export function describeMaxDrawdownPct(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return 'No drawdown measured yet.';
  }

  const magnitude = Math.abs(value);
  return `At the worst point the practice account was ${magnitude.toFixed(1)}% below its best level.`;
}

/** Why a backtest trade closed, in words. */
export function describeBacktestExit(reason: string): string {
  switch (reason.trim().toUpperCase()) {
    case 'TARGET':
      return 'Reached the target — sold for a gain';
    case 'STOP':
      return 'Hit the cut-loss — sold to protect capital';
    case 'BREAKEVEN':
      return 'Stop was moved up to break-even, then hit — sold roughly flat';
    case 'TRAIL_EMA20':
      return 'Trailed the rising average, then hit — sold with most of the gain';
    case 'TIME':
      return 'Holding period ended — sold at the market';
    default:
      return reason.replaceAll('_', ' ').toLowerCase();
  }
}

/**
 * How the replay did against simply buying and holding the PSE index over the same window.
 * The desk pays fees and slippage while the index pays neither, so a small positive number is
 * worth more than it looks and a negative one is not automatically a bad rule.
 */
export function describeAlphaPct(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return 'No index prices were stored for this window, so there is no market to compare with yet.';
  }

  if (value > 1) {
    return 'The rules beat simply holding the PSE index over the same window — after fees and slippage.';
  }

  if (value >= -1) {
    return 'The rules finished roughly level with simply holding the PSE index — after fees and slippage.';
  }

  return 'Simply holding the PSE index would have done better over this window, even after the desk’s fees.';
}

/** Whether the average winner paid for the average loser, in R. */
export function describePayoffRatio(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) {
    return 'Not enough closed winners and losers to compare their sizes yet.';
  }

  if (value >= 2) {
    return 'Each winner is worth about twice what each loser costs.';
  }

  if (value >= 1) {
    return 'Winners are bigger than losers, so a coin-toss hit rate can still pay.';
  }

  return 'Losers are bigger than winners, so the desk needs a high hit rate to break even.';
}

/** How many legal adds a position has taken. Only a winner that reached break-even may add. */
export function describePyramidCount(count: number | null | undefined): string {
  if (typeof count !== 'number' || !Number.isFinite(count) || count <= 0) {
    return 'Bought once — no extra buys added yet.';
  }

  if (count === 1) {
    return 'One extra buy added above the first entry, with the stop already at break-even.';
  }

  return `${count} extra buys added; the desk allows no more, so the next step is banking part of it.`;
}

/** What the "Trim half" button will actually do, in shares and in plain words. */
export function describeTrimGuidance(shares: number | null | undefined, ticker?: string): string {
  const name = ticker?.trim().toUpperCase() || 'this stock';

  if (typeof shares !== 'number' || !Number.isFinite(shares) || shares <= 0) {
    return `One board lot is all you own of ${name}, so it is all-or-nothing: sell it or leave it.`;
  }

  return `Sell ${shares.toLocaleString('en-PH')} shares of ${name} to bank part of the gain — the rest keeps running with the stop moved up.`;
}

/**
 * Translates the book's alert strings. The ticker-prefixed ones come from the server's exit
 * instructions ("SM: STOP BREACHED — …"), so they are mapped back onto the same plain
 * position-action copy the Book tab uses.
 */
export function describePortfolioAlert(alert: string): string {
  const text = alert.trim();
  const position = /^([A-Z0-9]+): ([A-Z_ ]+?) — (.+)$/.exec(text);

  if (position) {
    const [, ticker, action, note] = position;
    const plain = describePositionAction(action.trim().replaceAll(' ', '_'));
    return `${ticker}: ${plain.label.toLowerCase()}. ${note}`;
  }

  if (/^Portfolio heat is/i.test(text)) {
    return 'You already have a lot of money at risk, so the desk will not open new trades until something is sold or trimmed.';
  }

  if (/hard limit: new risk is halted/i.test(text)) {
    return 'Your practice account is well below its best level, so new trades are paused until it recovers.';
  }

  if (/soft limit: per-trade risk is scaled down/i.test(text)) {
    return 'Your practice account is in a dip, so new trades are smaller until it recovers.';
  }

  if (/sector cap: no new name/i.test(text)) {
    return 'One part of the market already fills as much of your account as your rules allow, so no new stock from it will be added.';
  }

  return text;
}

/** One-line explanation of what a risk-per-trade setting means in pesos. */
export function describeRiskPerTrade(pct: number | null | undefined, equityPhp: number): string {
  if (typeof pct !== 'number' || !Number.isFinite(pct) || pct <= 0 || equityPhp <= 0) {
    return 'Set a risk limit so the desk can size a trade.';
  }

  const pesos = ((equityPhp * pct) / 100).toLocaleString('en-PH', {
    style: 'currency',
    currency: 'PHP',
    maximumFractionDigits: 0,
  });

  return `If a trade hits its cut-loss you would lose about ${pesos}.`;
}


/** The traffic light copy for the market hero. Never re-derives the call. */
export interface MarketCallCopy {
  /** "Green light" / "Amber light" / "Red light". */
  label: string;
  /** One-line instruction that matches the server's call. */
  instruction: string;
  tone: string;
  badgeClass: string;
  dotClass: string;
}

const marketCallCopy: Record<MarketCall, MarketCallCopy> = {
  RIDE_THE_WAVE: {
    label: 'Green light',
    instruction: 'The model favours the strongest names on the list, sized in small pieces.',
    tone: 'text-mint-400',
    badgeClass: 'border-mint-400/40 bg-mint-400/10 text-mint-400',
    dotClass: 'bg-mint-400',
  },
  BE_SELECTIVE: {
    label: 'Amber light',
    instruction: 'Only the clearest setups qualify; the model keeps a cash cushion.',
    tone: 'text-amber-400',
    badgeClass: 'border-amber-400/40 bg-amber-400/10 text-amber-400',
    dotClass: 'bg-amber-400',
  },
  STAY_IN_CASH: {
    label: 'Red light',
    instruction: 'The model stands in cash at this cash level.',
    tone: 'text-rose-400',
    badgeClass: 'border-rose-400/40 bg-rose-400/10 text-rose-400',
    dotClass: 'bg-rose-400',
  },
};

/** Plain traffic-light label for the server's market call. */
export function describeMarketCall(call: MarketCall | string): MarketCallCopy {
  return (
    marketCallCopy[call as MarketCall] ?? {
      label: 'Reading the market',
      instruction: 'Wait for the desk to finish today’s read.',
      tone: 'text-slate-400',
      badgeClass: 'border-slate-500/40 bg-slate-500/10 text-slate-300',
      dotClass: 'bg-slate-500',
    }
  );
}

/** Where the mark sits versus the plan's own entry band. */
export interface PlainEntryTiming {
  label: string;
  detail: string;
  tone: string;
  badgeClass: string;
  /** True when the honest answer is "wait", so the UI never nudges a chase. */
  wait: boolean;
}

/** Plain reading of the server's entry status, with the tick-valid band it came from. */
export function describeEntryTiming(
  status: EntryTimingStatus | string,
  idealBuyPrice?: number | null,
  maxChasePrice?: number | null,
): PlainEntryTiming {
  const band =
    idealBuyPrice != null && maxChasePrice != null
      ? `${formatPrice(idealBuyPrice)}–${formatPrice(maxChasePrice)}`
      : null;

  switch (status as EntryTimingStatus) {
    case 'READY_NOW':
      return {
        label: 'Inside the band',
        detail: band
          ? `The price is inside the model’s entry band (${band}).`
          : 'The price is inside the model’s entry band.',
        tone: 'text-mint-400',
        badgeClass: 'border-mint-400/40 bg-mint-400/10 text-mint-400',
        wait: false,
      };
    case 'NEAR_ENTRY':
      return {
        label: 'Wait for the entry',
        detail: idealBuyPrice != null
          ? `It is below the ideal buy of ${formatPrice(idealBuyPrice)}. Wait for it to settle into the zone.`
          : 'It is still below the ideal buy. Wait for it to settle into the zone.',
        tone: 'text-sky-400',
        badgeClass: 'border-sky-400/40 bg-sky-400/10 text-sky-400',
        wait: true,
      };
    case 'EXTENDED_WAIT_PULLBACK':
      return {
        label: 'Too late to chase',
        detail: maxChasePrice != null
          ? `It already ran past ${formatPrice(maxChasePrice)}. Wait for a pullback instead of chasing.`
          : 'It already ran past the safe chase price. Wait for a pullback instead of chasing.',
        tone: 'text-amber-400',
        badgeClass: 'border-amber-400/40 bg-amber-400/10 text-amber-400',
        wait: true,
      };
    case 'BROKEN_BELOW_STOP':
      return {
        label: 'Setup is broken',
        detail: 'The price fell through the cut-loss. Skip it and wait for a fresh setup.',
        tone: 'text-rose-400',
        badgeClass: 'border-rose-400/40 bg-rose-400/10 text-rose-400',
        wait: true,
      };
    case 'WAIT_WATCH_ONLY':
      return {
        label: 'Watch only',
        detail: 'The model sizes no position here today. The name stays on the watch list.',
        tone: 'text-slate-300',
        badgeClass: 'border-slate-500/40 bg-slate-500/10 text-slate-300',
        wait: true,
      };
    default:
      return {
        label: 'Check the plan',
        detail: 'Re-run the research to price today’s entry band.',
        tone: 'text-slate-400',
        badgeClass: 'border-slate-500/40 bg-slate-500/10 text-slate-300',
        wait: true,
      };
  }
}

/** The desk's graded record in one honest line. Never hides an empty ledger. */
export function describeTrustPulse(pulse: TrustPulse | null | undefined): string {
  if (!pulse) {
    return 'The desk’s own report card could not be read right now.';
  }

  if (pulse.resolvedCount <= 0) {
    return 'No graded picks yet for this horizon — every call is tracked automatically from here.';
  }

  const winRate = `${formatPercent(pulse.winRate * 100, 0, false)} reached target`;
  const expectancy = `${pulse.expectancyR > 0 ? '+' : ''}${pulse.expectancyR.toFixed(2)}R`;
  const sample = pulse.isCalibrated ? '' : ' (small sample so far)';

  return `${pulse.resolvedCount} of ${pulse.totalPicks} picks graded · ${winRate} · ${expectancy} average · profit factor ${pulse.profitFactor.toFixed(2)}${sample}.`;
}

/** The "reader already holds it" read: the server's instruction plus the fee-paid result. */
export interface PlainHoldingVerdict extends PlainPositionAction {
  /** The server's own sentence, with the peso result appended when it knows the size. */
  detail: string;
}

export function describeHoldingVerdict(advice: PositionAdvice): PlainHoldingVerdict {
  const base = describePositionAction(advice.action);
  const pnl = advice.unrealizedNetPnlPhp;
  const money =
    pnl == null
      ? ''
      : pnl >= 0
        ? ` That is about ${formatPrice(pnl)} in your pocket if you sold now.`
        : ` That is about ${formatPrice(Math.abs(pnl))} less than you paid, fees included.`;

  return { ...base, detail: `${advice.explanation}${money}` };
}

/**
 * The primary button's wording. The desk can only re-run a day it already stored, so while a
 * past session is on screen the button must never promise "latest" — it regenerates that day.
 */
export function describeRunLabel(historicalSession: boolean, simple: boolean): string {
  if (historicalSession) {
    return simple ? 'Re-run This Day' : 'Re-run session';
  }

  return simple ? 'Run Latest Screen' : 'Run screen';
}

/**
 * The amber strip shown above a stored session. It states plainly that the prices, targets and
 * cash advice below are history, and — in Pro mode — that re-running regenerates that day
 * rather than fetching a new one.
 */
export function describeHistoryBanner(selectedDate: string | null, simple: boolean): string {
  if (simple) {
    return 'You are reading an older session. The prices, targets and cash advice on this page are history, not today’s plan.';
  }

  return `${selectedDate ?? 'This session'} is a stored session · re-running it regenerates that day, it does not fetch a new one.`;
}

