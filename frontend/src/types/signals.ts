export type DriftStatus = 'NEW' | 'HELD' | 'UPGRADED' | 'DOWNGRADED';

export type DropCategory = 'INVALIDATED' | 'TARGET_HIT' | 'DISPLACED';

export type MarketRegime = 'BULLISH' | 'NEUTRAL' | 'BEARISH';

export type Horizon = 'DAY' | 'WEEK' | 'MONTH' | 'YEAR';

/**
 * Weinstein stage analysis on the 30-week (150D) average. STAGE_2 is the only
 * long-only zone; the desk uses the label to weight the YEAR horizon.
 */
export type WeinsteinStage = 'STAGE_1' | 'STAGE_2' | 'STAGE_3' | 'STAGE_4';

/**
 * Sector rotation quadrant computed from breadth, Weinstein stage, relative strength and
 * up/down volume. The desk only sizes long trades in LEADING / IMPROVING sectors.
 */
export type SectorQuadrant = 'LEADING' | 'IMPROVING' | 'WEAKENING' | 'LAGGING';

/** One sector's rotation card: the inputs behind the composite score, published per run. */
export interface SectorRotation {
  sector: string;
  stockCount: number;
  /** Mean 63D relative strength versus the PSEi, in percentage points. */
  avgRelativeStrength63d: number | null;
  /** Share of the sector trading above its 50D average. */
  aboveSma50Pct: number | null;
  /** Share of the sector in a Weinstein Stage 2 uptrend. */
  stage2Pct: number | null;
  /** Median up/down volume ratio; above 1.0 is accumulation. */
  avgUpDownVolumeRatio: number | null;
  /** 0–100 composite, renormalised over the inputs the sector actually reports. */
  compositeScore: number;
  quadrant: SectorQuadrant;
}

/** Trap taxonomy produced by the PSE disclosure classifier. */
export type PseTrapFlag =
  | 'NONE'
  | 'SRO_DILUTION'
  | 'TENDER_OFFER_PIN'
  | 'TRADING_HALT_RISK'
  | 'FOO_PLACEMENT_OVERHANG'
  | 'EX_DIVIDEND_DROP'
  | 'GOVERNANCE_RISK'
  | 'EARNINGS_REVERSAL';

/**
 * Server-owned actionability verdict. The desk — never the model — decides this from the
 * fee-adjusted net R:R, the calibrated win probability and the tape. WATCH_ONLY picks are
 * NO-BET research context and carry no risk-sized shares.
 */
export type EdgeVerdict = 'TRADE' | 'SMALL_SIZE' | 'WATCH_ONLY';

/** Forward-outcome ledger state graded against the OHLC sessions after a pick's run date. */
export type SignalOutcomeStatus =
  | 'OPEN'
  | 'WIN_TARGET'
  | 'LOSS_STOP'
  | 'EXPIRED_WIN'
  | 'EXPIRED_LOSS'
  /** The entry price never traded inside the horizon window: nothing was bought or sold. */
  | 'UNFILLED';

/** Participation breadth computed across the screened universe, not the Top 10. */
export interface MarketBreadth {
  /** Null when the breadth pass had no eligible names to measure. */
  aboveSma50Pct: number | null;
  aboveSma200Pct: number | null;
  stage2Count: number;
  evaluatedStockCount: number;
}

export interface TechnicalMetrics {
  closePrice: number;
  changePct: number;
  avgTurnover20dPhp: number;
  volumeRatioVs20d: number;
  rsi14: number;
  ema20: number;
  sma50: number;
  sma200: number;
  atr14: number;
  supportLevel: number;
  resistanceLevel: number;
  /** Null when the live feed does not publish foreign-flow figures for the session. */
  netForeignTodayPhp: number | null;
  quantPreScore: number;
  /** 150D (30-week) average — Weinstein's Stage Analysis baseline. */
  sma150?: number | null;
  /** Highest close of the trailing 52 weeks. */
  week52High?: number | null;
  weinsteinStage?: WeinsteinStage | null;
  /** Session OHLC of the signal day, stored for the forward-outcome resolver. */
  openPrice?: number | null;
  highPrice?: number | null;
  lowPrice?: number | null;
  /** 63D return minus the PSEi's, in percentage points. Null without an index series. */
  relativeStrength63d?: number | null;
  /** ATR14 as a percentage of the close — the barrier geometry input. */
  atrPct?: number | null;
  /** Recent six-bar range over the 30-bar base range; below ~0.85 is a volatility coil. */
  volatilityContractionRatio?: number | null;
  /** Shrunk historical hit rate of the ATR-shaped barriers; null under 60 bars. */
  empiricalWinRate?: number | null;
  /** Cash dividends whose ex-date fell inside the trailing 365 days, per share. */
  trailingDividendPerShare?: number | null;
  /** Trailing cash yield on the close, e.g. 4.12 for 4.12%. Null when unreported. */
  trailingDividendYieldPct?: number | null;
  /** Latest ex-dividend date inside the trailing window. */
  lastExDividendDate?: string | null;
  // Institutional factor sleeve. Null means "unreported" — never zero — so the client
  // can render an em dash instead of a fake figure on older runs.
  /** Shrunk DAY-horizon hit rate of the ATR barriers. */
  empiricalWinRateDay?: number | null;
  /** Shrunk WEEK-horizon hit rate of the ATR barriers. */
  empiricalWinRateMonth?: number | null;
  /** Shrunk MONTH-horizon hit rate of the ATR barriers. */
  empiricalWinRateYear?: number | null;
  /** 20D up-volume over down-volume; above 1.0 is accumulation. */
  upDownVolumeRatio20d?: number | null;
  /** Chaikin money flow, −100…100. Positive is accumulation. */
  chaikinMoneyFlow20d?: number | null;
  /** Net 63D move over the summed absolute daily moves; 1.0 is a perfect trend. */
  trendEfficiency63d?: number | null;
  /** Beta against the PSEi over 252 sessions; > 1.2 draws a volatility budget cut. */
  beta252d?: number | null;
  /** Peak-to-trough close drawdown over 252 sessions, as a positive percentage. */
  maxDrawdown252dPct?: number | null;
  /** Half-spread as a percentage of the close, from the PSE tick table. */
  tickSpreadRatePct?: number | null;
}

/**
 * Server-computed order ticket: whole board lots the desk's own risk budget funds against
 * the current account equity, the all-in buy outlay including the PSE buy-leg fees, the
 * fee-paid peso risk/reward and the break-even stop tick. The browser only renders these.
 */
export interface OrderTicket {
  boardLotSize: number;
  shares: number;
  lots: number;
  buyOutlayPhp: number;
  netRiskPhp: number;
  netRewardPhp: number;
  breakEvenStopPrice: number;
  riskBudgetPhp: number;
  liquidityCapped: boolean;
  warnings: string[];
  // Pod-shop governor and transaction-cost analysis. Server-owned: the browser renders
  // the numbers, the desk decides them.
  /** Risk percentage actually used after the governor's multipliers. */
  effectiveRiskPct: number;
  /** 'NORMAL' | 'DEFENSIVE' | 'HALTED' | 'BLOCKED'. HALTED funds no new risk. */
  riskGovernor: string;
  /** Book drawdown at sizing time; soft/hard thresholds cut or halt the budget. */
  drawdownPct: number;
  /** Edge-tier multiplier applied to the base risk (e.g. SMALL_SIZE < 1). */
  edgeMultiplier: number;
  /** Beta/volatility multiplier applied to the base risk. */
  volatilityMultiplier: number;
  /** Drawdown multiplier applied to the base risk (soft threshold only). */
  drawdownMultiplier: number;
  /** Round-trip slippage assumption as a percentage of the price. */
  slippageRatePct: number;
  estimatedSlippagePhp: number;
  /** Net risk with both entry and exit slippage paid. */
  netRiskAfterSlippagePhp: number;
  /** Net reward with both entry and exit slippage paid. */
  netRewardAfterSlippagePhp: number;
  /** Order size as a percentage of the 20D average daily value traded. */
  advParticipationPct: number;
  /** 'LIQUID_MARKETABLE' | 'THIN_LIMIT_ONLY' | ... — how to work the order. */
  executionGuidance: string;
}

export interface StockDisclosure {
  date: string;
  headline: string;
  category: string;
  source: string;
  /** Disclosure/trap classification; hard traps disqualify a name server-side. */
  trapFlag?: PseTrapFlag | string;
}

/** Traffic-light call the desk computes from five deterministic pillars. */
export type MarketCall = 'RIDE_THE_WAVE' | 'BE_SELECTIVE' | 'STAY_IN_CASH';

/** Direction read behind the call: the 10–90 score's own label. */
export type MarketDirection = 'UPTREND' | 'SIDEWAYS' | 'DOWNTREND';

/** How many pillars reported and agreed. Never a model's self-assessment. */
export type MarketConfidence = 'HIGH' | 'MODERATE' | 'CAUTIOUS';

/** The plain-language market hero: call, score, reasons and the rotation behind it. */
export interface MarketDirectionForecast {
  call: MarketCall | string;
  direction: MarketDirection | string;
  /** 10 (deeply bearish) to 90 (strongly bullish); 50 is neutral. */
  bullishScore: number;
  confidence: MarketConfidence | string;
  headline: string;
  actionSummary: string;
  /** Plain reasons, at most four. Unreported dials are omitted, never shown as 0. */
  reasons: string[];
  leadingSectors: string[];
  laggingSectors: string[];
}

/** Where the mark sits versus the plan's own entry band. */
export type EntryTimingStatus =
  | 'READY_NOW'
  | 'NEAR_ENTRY'
  | 'EXTENDED_WAIT_PULLBACK'
  | 'BROKEN_BELOW_STOP'
  | 'WAIT_WATCH_ONLY';

/** The four-step exit roadmap for one pick, every price a tradeable PSE tick. */
export interface WaveTradePlan {
  idealBuyPrice: number;
  cutLossPrice: number;
  breakEvenStopPrice: number;
  targetPrice: number;
  maxChasePrice: number;
  lockBreakEvenTriggerPrice: number;
  trailWaveTriggerPrice: number;
  /** Tick-floored EMA20 when it protects a gain; null when no trail applies. */
  trailStopPrice: number | null;
  entryStatus: EntryTimingStatus | string;
  maxHoldTradingDays: number;
}

/** The desk's own graded record for this horizon. */
export interface TrustPulse {
  horizon: Horizon | string;
  totalPicks: number;
  resolvedCount: number;
  openCount: number;
  winRate: number;
  expectancyR: number;
  profitFactor: number;
  isCalibrated: boolean;
}

export interface SignalItem {
  rank: number;
  ticker: string;
  companyName: string;
  sector: string;
  driftStatus: DriftStatus;
  previousRank: number | null;
  convictionScore: number;
  entryPrice: number;
  stopLoss: number;
  targetPrice: number;
  riskRewardRatio: number;
  thesis: string;
  deviationReason: string;
  metrics: TechnicalMetrics | null;
  disclosures: StockDisclosure[];
  /** PSE board lot for the entry price band (15-tier table). */
  boardLotSize?: number;
  /** Board lots the position budget actually funds, rounded down. */
  boardLotShares?: number;
  /** Knowledge-base references the model cited, verified server-side. */
  citations?: string[];
  /** Deduped, non-NONE trap flags across this session's disclosures. */
  trapFlags?: string[];
  /** Fee-adjusted reward-to-risk: a gross 2.00 with a 5% stop is about 1.63 net after the 0.690% round trip. */
  netRiskRewardRatio: number;
  /** Calibrated probability of touching the target before the stop, clamped [0.05, 0.95]. */
  winProbability: number;
  /** Win rate at which this plan's expected value is zero after the 0.690% round trip. */
  breakEvenWinRate: number;
  /** Expected value in R: winProbability × net R:R − (1 − winProbability). */
  expectedValueR: number;
  /** Quarter-Kelly fraction of the model bankroll the plan earns, 0 when EV ≤ 0. */
  quarterKellyFraction: number;
  edgeVerdict: EdgeVerdict;
  /** Pre-mortem: the specific tape action that invalidates the thesis. */
  bearCase: string;
  /** Forward-outcome ledger state; OPEN until the resolver grades the pick. */
  outcomeStatus: SignalOutcomeStatus;
  outcomeDate: string | null;
  outcomeExitPrice: number | null;
  /** Realized result in R after the round-trip fee, null while the pick is open. */
  realizedNetR: number | null;
  barsHeld: number | null;
  /** Server-sized order ticket for this pick, null when the account has no equity. */
  orderTicket?: OrderTicket | null;
  /** The four-step exit roadmap; null on runs persisted before it existed. */
  wavePlan?: WaveTradePlan | null;
}

export interface SignalDrop {
  ticker: string;
  companyName: string;
  previousRank: number | null;
  dropCategory: DropCategory;
  reason: string;
}

export interface SignalRun {
  id: number;
  runDate: string;
  horizon: Horizon;
  createdAtUtc: string;
  items: SignalItem[];
  drops: SignalDrop[];
  /** Cash the desk wants held after funding the actionable picks, 0–100. */
  recommendedCashPct: number;
  tradeCount: number;
  smallSizeCount: number;
  watchOnlyCount: number;
  breadth: MarketBreadth | null;
}

export interface RegimeSnapshot {
  tradeDate: string;
  pseiClose: number;
  pseiSma50: number;
  pseiSma200: number;
  sma50Status: 'ABOVE' | 'BELOW';
  sma200Status: 'ABOVE' | 'BELOW';
  /** Null when the live feed does not publish a net foreign figure for the session. */
  netForeignFlowPhp: number | null;
  usdPhp: number;
  regime: MarketRegime;
  regimeNote: string;
  /** Universe breadth measured on the same session; null on snapshots predating it. */
  breadthAboveSma50Pct: number | null;
  breadthAboveSma200Pct: number | null;
  stage2Count: number | null;
  evaluatedStockCount: number | null;
  // Macro / FX / rotation dials. Null on snapshots stored before the money-manager layer.
  /** 50D average of USD/PHP — the FX trend line for foreign-flow context. */
  usdPhpSma50: number | null;
  /** 20D change in USD/PHP, in percent. Positive is peso weakness. */
  usdPhpChange20dPct: number | null;
  /** 20D realized volatility of the PSEi close, annualised percent. */
  pseiRealizedVol20dPct: number | null;
  /** Sector rotation cards, ordered best composite first. */
  sectorRotation: SectorRotation[] | null;
  // Cross-asset macro dials (Yahoo Finance ^TNX, BZ=F, EPHE). Null means the feed did not
  // publish a usable print, so the tile must render an em dash rather than a zero.
  /** US 10-year Treasury yield, in percent (e.g. 4.185 for 4.185%). */
  us10YClosePct: number | null;
  /** 20-session change in the US 10-year yield, in basis points. */
  us10YChange20dBps: number | null;
  /** Brent crude front-month close, USD per barrel. */
  brentUsd: number | null;
  /** 20-session change in Brent, percent. */
  brentChange20dPct: number | null;
  /** EPHE (MSCI Philippines ETF) close, USD. */
  epheCloseUsd: number | null;
  /** 20-session change in EPHE, percent. */
  epheChange20dPct: number | null;
  /** Where net foreign flow came from: pse-dqr-live, pse-dqr-upload, or null if unreported. */
  foreignFlowSource: string | null;
  /** The traffic light derived from this session plus today's run. */
  directionForecast?: MarketDirectionForecast | null;
}

/** Result of applying an uploaded official PSE Daily Quotation Report onto a stored session. */
export interface QuotationReportImportResult {
  tradeDate: string;
  source: string;
  marketNetForeignFlowPhp: number | null;
  matchedTickerCount: number;
  updatedMetricCount: number;
  suspendedTickers: string[];
}

export interface DateOption {
  date: string;
  hasMarketSnapshot: boolean;
  hasSignalRun: boolean;
}

export interface LatestSignalsResponse {
  run: SignalRun | null;
  regime: RegimeSnapshot | null;
  dates: DateOption[];
  droppedFromYesterday: SignalDrop[];
  /** The desk's own report card for this horizon; null when the ledger is unreadable. */
  trustPulse?: TrustPulse | null;
}

/** One tracked PSE name from `GET /api/research/catalog`. Metadata only — never a price. */
export interface CatalogStock {
  ticker: string;
  companyName: string;
  sector: string;
}

export interface TickerHistoryPoint {
  runDate: string;
  rank: number;
  driftStatus: DriftStatus;
  previousRank: number | null;
  convictionScore: number;
  entryPrice: number;
  stopLoss: number;
  targetPrice: number;
  riskRewardRatio: number;
  netRiskRewardRatio: number;
  winProbability: number;
  expectedValueR: number;
  edgeVerdict: EdgeVerdict;
  outcomeStatus: SignalOutcomeStatus;
  realizedNetR: number | null;
}

export interface TickerHistory {
  ticker: string;
  companyName: string;
  sector: string;
  points: TickerHistoryPoint[];
}

/** One indexed knowledge-base document (built-in playbook or uploaded trading book). */
export interface KnowledgeDocument {
  id: number;
  slug: string;
  title: string;
  /** 'builtin' for the shipped Markdown playbooks, 'upload' for user files. */
  source: string;
  sourcePath: string;
  horizons: string[];
  categories: string[];
  isMandatory: boolean;
  chunkCount: number;
  embeddedChunkCount: number;
  contentHash: string;
  updatedAtUtc: string;
}

/** Telemetry returned by a knowledge-base (re)index pass. */
export interface KnowledgeIngestionResult {
  documentsIndexed: number;
  documentsUpdated: number;
  documentsSkipped: number;
  chunksIndexed: number;
  chunksEmbedded: number;
  embeddingModel: string;
}

/** Multipart payload for a trading-book upload. */
export interface KnowledgeUploadInput {
  file: File;
  title?: string;
  horizons: string[];
  categories: string[];
}

/** One probability bucket of the calibration table: what the desk predicted vs what happened. */
export interface CalibrationBucket {
  /** Human label for the bucket's predicted-probability range, e.g. "40–50%". */
  label: string;
  predictedWinPct: number;
  actualWinPct: number;
  sampleCount: number;
}

/** Realized performance split by the server-owned actionability verdict. */
export interface VerdictBreakdown {
  verdict: EdgeVerdict | string;
  count: number;
  resolvedCount: number;
  winRate: number;
  averageRealizedNetR: number;
  totalRealizedNetR: number;
}

/** One graded pick from the forward-outcome ledger. */
export interface ResolvedSignal {
  ticker: string;
  runDate: string;
  horizon: Horizon;
  edgeVerdict: EdgeVerdict | string;
  winProbability: number;
  expectedValueR: number;
  outcomeStatus: SignalOutcomeStatus | string;
  outcomeDate: string | null;
  entryPrice: number;
  stopLoss: number;
  targetPrice: number;
  outcomeExitPrice: number | null;
  realizedNetR: number | null;
  barsHeld: number | null;
}

/** Forward-outcome scorecard for one horizon — the desk grading itself. */
export interface SignalScorecard {
  horizon: Horizon | string;
  totalPicks: number;
  resolvedCount: number;
  openCount: number;
  winRate: number;
  averageRealizedNetR: number;
  expectancyR: number;
  profitFactor: number;
  brierScore: number;
  calibration: CalibrationBucket[];
  verdicts: VerdictBreakdown[];
  recentResolved: ResolvedSignal[];
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
}

/** ATR-anchored, tick-valid plan for an on-demand research name. */
export interface ResearchPlan {
  entryPrice: number;
  stopLoss: number;
  targetPrice: number;
  riskRewardRatio: number;
  netRiskRewardRatio: number;
  atrStopDistance: number;
  atrTargetDistance: number;
}

/**
 * On-demand single-ticker research. Every level, probability, verdict and size is computed
 * server-side from the live OHLCV series; only the optional AI thesis is prose.
 */
export interface TickerResearch {
  ticker: string;
  companyName: string;
  sector: string;
  horizon: Horizon | string;
  asOfDate: string;
  inCatalog: boolean;
  passesLiquidityFloor: boolean;
  avgTurnover20dPhp: number;
  metrics: TechnicalMetrics | null;
  trapFlags: string[];
  disclosures: StockDisclosure[];
  plan: ResearchPlan;
  winProbability: number;
  breakEvenWinRate: number;
  expectedValueR: number;
  quarterKellyFraction: number;
  edgeVerdict: EdgeVerdict | string;
  bearCase: string;
  orderTicket: OrderTicket | null;
  dataSource: string;
  aiThesis: string | null;
  /** The same four-step roadmap the daily list prints for this name. */
  wavePlan?: WaveTradePlan | null;
  /** The "I already own it" answer; null unless the reader supplied an average cost. */
  positionAdvice?: PositionAdvice | null;
}

/** The exit instruction for a position the reader already owns. */
export type PositionAdviceAction =
  | 'HOLD'
  | 'STOP_BREACHED'
  | 'TARGET_REACHED'
  | 'MOVE_STOP_TO_BREAKEVEN'
  | 'TRAIL_STOP_EMA20';

/** Server-computed answer to "what do I do with the shares I already hold?". */
export interface PositionAdvice {
  ticker: string;
  averageCost: number;
  currentPrice: number;
  shares: number;
  breakEvenPrice: number;
  suggestedStopPrice: number;
  suggestedTargetPrice: number;
  /** Fee-paid return from average cost to the current price, in percent. */
  netReturnPct: number;
  /** Fee-paid peso result; null when no share count was supplied. */
  unrealizedNetPnlPhp: number | null;
  unrealizedR: number;
  action: PositionAdviceAction | string;
  headline: string;
  explanation: string;
  /** Whole board lots on both legs; 0 when no legal half-trim exists. */
  recommendedTrimShares: number;
}

/** One simulated round trip from the causal walk-forward replayer. */
export interface BacktestTrade {
  ticker: string;
  entryDate: string;
  entryPrice: number;
  exitDate: string;
  exitPrice: number;
  shares: number;
  netPnlPhp: number;
  netR: number;
  /** `BREAKEVEN` and `TRAIL_EMA20` are the ladder exits: the ratcheted stop filled, not the first one. */
  exitReason: 'STOP' | 'TARGET' | 'TIME' | 'BREAKEVEN' | 'TRAIL_EMA20' | string;
  barsHeld: number;
  /** Tick-spread and impact this clip paid on both legs, in pesos; already inside `netPnlPhp`. */
  slippagePhp?: number;
}

export interface BacktestEquityPoint {
  date: string;
  equityPhp: number;
  drawdownPct: number;
  /** Same starting capital held in PSEI.PS from the first replayed session; null without index bars. */
  benchmarkEquityPhp?: number | null;
}

/** What the horizon's own rules actually compounded on the real tape, fees paid. */
export interface BacktestResult {
  horizon: Horizon | string;
  startDate: string;
  endDate: string;
  startingCapitalPhp: number;
  endingEquityPhp: number;
  netReturnPct: number;
  maxDrawdownPct: number;
  totalTrades: number;
  winRate: number;
  expectancyR: number;
  profitFactor: number;
  totalFeesPaidPhp: number;
  sessionsEvaluated: number;
  equityCurve: BacktestEquityPoint[];
  trades: BacktestTrade[];
  // Benchmark and risk-adjusted return, all server-owned. Optional because the API appends
  // them: a client that predates the alpha proof still reads a replay.
  /** Return of buying and holding the PSE index over the same window, in percent. */
  benchmarkReturnPct?: number | null;
  /** Slippage-adjusted net return minus the index's — the desk pays friction, the index does not. */
  excessReturnPct?: number | null;
  /** Compound annual growth rate of the replayed curve, in percent. */
  cagrPct?: number;
  /** Annualised Sharpe of the daily curve returns (risk-free 0); null under 20 sessions. */
  annualizedSharpeRatio?: number | null;
  /** CAGR divided by max drawdown; null when the replay never drew down. */
  calmarRatio?: number | null;
  /** Tick-spread and market-impact estimate charged across every round trip, in pesos. */
  totalSlippagePhp?: number;
  /** The net return with slippage deducted as well as the fees, in percent. */
  netReturnAfterSlippagePct?: number;
}

/**
 * How the desk's own self-trained money manager stands for one horizon. `UNTRAINED` means no
 * fit exists yet; `PRIOR_FALLBACK` means a fit was rejected out of sample, so the deterministic
 * prior still prices every pick; only `LEARNED` may tilt the odds.
 */
export type TrainedModelStatus = 'UNTRAINED' | 'LEARNED' | 'PRIOR_FALLBACK';

/** One learned factor, ranked by how much one standard deviation of it moves the odds. */
export interface LearnedFactorWeight {
  feature: string;
  label: string;
  weight: number;
  normalizedImpactPct: number;
  direction: string;
}

/** The held-out report card on the horizon's logistic model. */
export interface TrainedModelSummary {
  horizon: Horizon | string;
  status: TrainedModelStatus | string;
  isActive: boolean;
  trainedAtUtc: string | null;
  trainStartDate: string | null;
  trainEndDate: string | null;
  validationStartDate: string | null;
  validationEndDate: string | null;
  trainSampleCount: number;
  validationSampleCount: number;
  embargoBars: number;
  baselineBrierScore: number;
  trainedBrierScore: number;
  brierImprovementPct: number;
  baselineLogLoss: number;
  trainedLogLoss: number;
  validationWinRate: number;
  validationExpectancyR: number;
  validationProfitFactor: number;
  /** Learned fraction of full Kelly in [0.15, 0.35]; 0.25 leaves Quarter-Kelly untouched. */
  optimalKellyFractionMultiplier: number;
  weights: LearnedFactorWeight[];
  summary: string;
}

/** One sector's realized record in the desk's own forward ledger. */
export interface SectorMemory {
  sector: string;
  samples: number;
  winRate: number;
  averageRealizedNetR: number;
}

/** One resolved stop-out, kept as a post-mortem line for the prompt and the terminal. */
export interface LossPostMortem {
  ticker: string;
  runDate: string;
  entryPrice: number;
  stopLoss: number;
  realizedNetR: number;
  barsHeld: number;
}

/** The desk's memory of its own results, computed from the stored ledger only. */
export interface DeskMemorySummary {
  horizon: Horizon | string;
  resolvedCount: number;
  winRate: number;
  expectancyR: number;
  brierScore: number;
  calibrationBiasPct: number;
  bestSectors: SectorMemory[];
  worstSectors: SectorMemory[];
  topFactors: LearnedFactorWeight[];
  recentStopOuts: LossPostMortem[];
}

