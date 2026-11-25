import type { EdgeVerdict, Horizon } from './signals';

/** Capital configuration and cash ledger of the single desk account. */
export interface PortfolioAccount {
  startingCapitalPhp: number;
  cashBalancePhp: number;
  maxRiskPerTradePct: number;
  smallSizeRiskMultiplier: number;
  maxPositionPct: number;
  maxPortfolioHeatPct: number;
  updatedAtUtc: string;
  // Pod-shop guardrails, all server-owned and clamped on write.
  /** High-water mark of the book, null until the first mark-to-market. */
  peakEquityPhp: number | null;
  /** Cap on a single sector's share of equity. */
  maxSectorExposurePct: number;
  /** Cap on the number of open names in one sector. */
  maxPositionsPerSector: number;
  /** Drawdown at which new risk is halved. */
  softDrawdownThresholdPct: number;
  /** Drawdown at which the desk stops funding new risk. */
  hardDrawdownThresholdPct: number;
  /** Portfolio volatility the risk budget is scaled against. */
  targetAnnualVolPct: number;
}

/** Exposure of one PSE sector against its caps, for the Book tab gauges. */
export interface SectorExposure {
  sector: string;
  exposurePct: number;
  positionCount: number;
  maxSectorExposurePct: number;
  maxPositionsPerSector: number;
}

/** Deterministic exit instruction the server derives from the session's OHLC. */
export type PositionAction =
  | 'HOLD'
  | 'STOP_BREACHED'
  | 'TARGET_REACHED'
  | 'MOVE_STOP_TO_BREAKEVEN'
  | 'TRAIL_STOP_EMA20'
  | 'TIME_STOP_DUE';

/** One open blotter position marked to the latest stored session, every peso fee-paid. */
export interface OpenPosition {
  id: number;
  ticker: string;
  companyName: string;
  sector: string;
  horizon: Horizon | string;
  edgeVerdict: EdgeVerdict | string;
  openedDate: string;
  shares: number;
  entryPrice: number;
  initialStopLoss: number;
  currentStopLoss: number;
  targetPrice: number;
  buyCostPhp: number;
  initialNetRiskPhp: number;
  lastPrice: number;
  ema20: number | null;
  unrealizedNetPnlPhp: number;
  unrealizedNetR: number;
  breakEvenStopPrice: number;
  action: PositionAction | string;
  actionNote: string;
  sessionsHeld: number;
  /** Legal pyramid adds already taken into this position (0, 1 or 2). */
  pyramidCount: number;
  /** Largest slice at or below half that is a whole board lot on both legs at the marked price; 0 when no legal trim exists (the buttons then hide). */
  recommendedTrimShares: number;
}

/** One settled trade in the closed ledger. */
export interface ClosedTrade {
  id: number;
  ticker: string;
  companyName: string;
  horizon: Horizon | string;
  openedDate: string;
  closedDate: string | null;
  shares: number;
  entryPrice: number;
  exitPrice: number;
  sellProceedsPhp: number;
  realizedNetPnlPhp: number;
  realizedNetR: number | null;
  notes: string;
}

export interface PortfolioEquityPoint {
  date: string;
  equityPhp: number;
  drawdownPct: number;
}

/** The whole book: settings, mark-to-market, heat and exit alerts. */
export interface PortfolioSummary {
  account: PortfolioAccount;
  totalEquityPhp: number;
  investedCapitalPhp: number;
  unrealizedNetPnlPhp: number;
  realizedNetPnlPhp: number;
  openRiskPhp: number;
  openHeatPct: number;
  openPositions: number;
  closedTrades: number;
  winRate: number;
  positions: OpenPosition[];
  closed: ClosedTrade[];
  realizedEquityCurve: PortfolioEquityPoint[];
  alerts: string[];
  // Drawdown governor and sector heat, rendered as the Book tab header.
  /** Current drawdown from the equity high-water mark, as a positive percentage. */
  drawdownPct: number;
  /** Equity high-water mark the drawdown is measured against. */
  peakEquityPhp: number;
  /** 'NORMAL' | 'DEFENSIVE' | 'HALTED' — the desk's own risk posture from the drawdown. */
  riskPosture: string;
  /** Per-sector exposure gauges, null on a book with no open positions. */
  sectorExposure: SectorExposure[] | null;
  /** Average realized R per settled trade — the edge's per-trade pulse. */
  expectancyR: number;
  /** Gross winning R divided by gross losing R; 0 without closed trades. */
  profitFactor: number;
  /** Average winner R divided by average loser R; null until both sides exist. */
  payoffRatio: number | null;
}

/** The reduced position plus the settled slice that landed in the closed ledger. */
export interface TrimTradeResult {
  remainingPosition: OpenPosition;
  settledSlice: ClosedTrade;
}

/** Partial account update; omitted fields keep their stored value. */
export interface PortfolioSettingsUpdate {
  startingCapitalPhp?: number;
  cashBalancePhp?: number;
  maxRiskPerTradePct?: number;
  smallSizeRiskMultiplier?: number;
  maxPositionPct?: number;
  maxPortfolioHeatPct?: number;
  maxSectorExposurePct?: number;
  maxPositionsPerSector?: number;
  softDrawdownThresholdPct?: number;
  hardDrawdownThresholdPct?: number;
  targetAnnualVolPct?: number;
}

/** Payload for logging a position ("Take Trade"). */
export interface OpenTradeInput {
  ticker: string;
  shares: number;
  entryPrice: number;
  stopLoss: number;
  targetPrice: number;
  horizon: Horizon;
  edgeVerdict: EdgeVerdict;
  companyName?: string;
  sector?: string;
  signalItemId?: number;
  openedDate?: string;
  notes?: string;
}

/** Prefill handed to the blotter when a pick is taken from the terminal. */
export interface TradeDraft {
  ticker: string;
  companyName?: string;
  sector?: string;
  horizon: Horizon;
  edgeVerdict: EdgeVerdict;
  entryPrice: number;
  stopLoss: number;
  targetPrice: number;
  signalItemId?: number;
  shares: number;
}
