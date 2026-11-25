import type { SignalItem } from '../types/signals';

/**
 * Model bankroll the desk sizes against. Mirrors
 * `SignalPipelineService.DefaultPositionBudgetPhp` — the server already sizes board-lot
 * shares against this budget, so the terminal must not invent a different number.
 */
export const MODEL_POSITION_BUDGET_PHP = 100_000;

/**
 * Everything the terminal needs to render a risk-sized plan. When the API attaches an
 * order ticket (it sizes against the stored account equity), those shares, lots, outlay and
 * fee-paid risk are used verbatim; only a ticket-less legacy run falls back to the local
 * Quarter-Kelly × model-budget conversion.
 */
export interface RiskSizing {
  /** Fraction of the model bankroll the plan earns, already divided by four on the server. */
  kellyPct: number;
  /** Risk budget the Kelly fraction allocates, before board-lot rounding. */
  budgetPhp: number;
  /** Board-lot aligned shares the budget actually funds. */
  shares: number;
  lots: number;
  boardLot: number;
  /** Shares × entry price: the capital committed at the quoted price. */
  fundedPhp: number;
  /** Shares × stop distance, before the round-trip fee. */
  grossRiskPhp: number;
  /** winProbability ÷ breakEvenWinRate; above 1.00 means the edge is real. */
  edgeRatio: number;
  actionable: boolean;
  /** True when these numbers came from the API's order ticket, not local rounding. */
  serverSized: boolean;
  /** All-in cash the order consumes (ticket: includes the PSE buy-leg fee). */
  buyOutlayPhp: number;
  /** Peso risk after the fee legs (ticket: net of both legs). */
  netRiskPhp: number;
  /** Peso reward after fees; null when only the legacy derivation is available. */
  netRewardPhp: number | null;
  /** Stop tick that makes the round trip exactly flat after fees; null when unavailable. */
  breakEvenStopPrice: number | null;
  /** Server warnings (liquidity cap, heat cap, unfundable lot) worth surfacing. */
  warnings: string[];
  /** 'NORMAL' | 'DEFENSIVE' | 'HALTED' | 'BLOCKED'; null when the run carries no ticket. */
  riskGovernor: string | null;
  /** Risk percent the governor actually applied (already multiplied), null without a ticket. */
  effectiveRiskPct: number | null;
  /** Estimated round-trip slippage as a percentage of the entry, null without a ticket. */
  slippageRatePct: number | null;
  /** Peso slippage the clip is expected to pay, null without a ticket. */
  slippagePhp: number | null;
  /** Order size as a percentage of the name's 20D turnover, null without a ticket. */
  advParticipationPct: number | null;
  /** How the desk says to work the order, null without a ticket. */
  executionGuidance: string | null;
}

export function computeRiskSizing(item: SignalItem): RiskSizing {
  const actionable = item.edgeVerdict !== 'WATCH_ONLY';
  const kellyPct = actionable ? Math.max(0, item.quarterKellyFraction) * 100 : 0;
  const edgeRatio = item.breakEvenWinRate > 0 ? item.winProbability / item.breakEvenWinRate : 0;
  const ticket = item.orderTicket ?? null;

  if (ticket) {
    const boardLot = ticket.boardLotSize > 0 ? ticket.boardLotSize : 1;

    return {
      kellyPct,
      budgetPhp: ticket.riskBudgetPhp,
      shares: ticket.shares,
      lots: ticket.lots,
      boardLot,
      fundedPhp: ticket.shares * item.entryPrice,
      grossRiskPhp: ticket.shares * Math.max(0, item.entryPrice - item.stopLoss),
      edgeRatio,
      actionable,
      serverSized: true,
      buyOutlayPhp: ticket.buyOutlayPhp,
      netRiskPhp: ticket.netRiskPhp,
      netRewardPhp: ticket.netRewardPhp,
      breakEvenStopPrice: ticket.breakEvenStopPrice,
      warnings: ticket.warnings,
      riskGovernor: ticket.riskGovernor || null,
      effectiveRiskPct: ticket.effectiveRiskPct > 0 ? ticket.effectiveRiskPct : null,
      slippageRatePct: ticket.slippageRatePct > 0 ? ticket.slippageRatePct : null,
      slippagePhp: ticket.estimatedSlippagePhp > 0 ? ticket.estimatedSlippagePhp : null,
      advParticipationPct: ticket.advParticipationPct > 0 ? ticket.advParticipationPct : null,
      executionGuidance: ticket.executionGuidance || null,
    };
  }

  const budgetPhp = Math.floor((MODEL_POSITION_BUDGET_PHP * kellyPct) / 100);
  const boardLot = item.boardLotSize && item.boardLotSize > 0 ? item.boardLotSize : 1;
  const rawShares = item.entryPrice > 0 ? Math.floor(budgetPhp / item.entryPrice) : 0;
  const shares = actionable ? Math.floor(rawShares / boardLot) * boardLot : 0;
  const stopDistance = Math.max(0, item.entryPrice - item.stopLoss);
  const fundedPhp = shares * item.entryPrice;
  const grossRiskPhp = shares * stopDistance;

  return {
    kellyPct,
    budgetPhp,
    shares,
    lots: Math.floor(shares / boardLot),
    boardLot,
    fundedPhp,
    grossRiskPhp,
    edgeRatio,
    actionable,
    serverSized: false,
    buyOutlayPhp: fundedPhp,
    netRiskPhp: grossRiskPhp,
    netRewardPhp: null,
    breakEvenStopPrice: null,
    warnings: [],
    riskGovernor: null,
    effectiveRiskPct: null,
    slippageRatePct: null,
    slippagePhp: null,
    advParticipationPct: null,
    executionGuidance: null,
  };
}

/** A win probability as a whole percentage, e.g. 0.4242 → "42%". */
export function formatProbability(value: number | null | undefined, digits = 0): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return '—';
  }

  return `${(value * 100).toFixed(digits)}%`;
}

/** A result in R, always signed, e.g. 1.94 → "+1.94R". */
export function formatR(value: number | null | undefined, digits = 2): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return '—';
  }

  const sign = value > 0 ? '+' : '';
  return `${sign}${value.toFixed(digits)}R`;
}
