#!/usr/bin/env node
// Plain-language layer: the copy a first-time investor reads must stay tied to the server's
// numbers and never drift when a threshold changes. Run with `npm test` (node:test, no network).
import assert from 'node:assert/strict';
import test from 'node:test';
import {
  BUDGET_PRESETS_PHP,
  SAFETY_PRESETS,
  describeAlphaPct,
  describeBacktestExit,
  describeBrierScore,
  describeEntryTiming,
  describeExpectancyR,
  describeFriendlyError,
  describeHistoryBanner,
  describeHoldingVerdict,
  describeMarketCall,
  describeMaxDrawdownPct,
  describeNextStep,
  describeOutcomeStatus,
  describePayoffRatio,
  describePlainVerdict,
  describePortfolioAlert,
  describePositionAction,
  describeProfitFactor,
  describePyramidCount,
  describeRiskPerTrade,
  describeRunLabel,
  describeSignalRow,
  describeTrimGuidance,
  describeTrustPulse,
  describeWinRate,
  safetyPresetForSettings,
} from '../plainAdvice.ts';
import {
  POPULAR_PSE_STOCKS,
  companyLabel,
  findCatalogStock,
  nicknameFor,
  searchCatalog,
} from '../pseNicknames.ts';
import type { CatalogStock, PositionAdvice, TrustPulse } from '../../types/signals';
import { formatR, formatProbability } from '../riskSizing.ts';
import { isViewingHistory, newestSessionDate } from '../sessionSelection.ts';

const CATALOG: CatalogStock[] = [
  { ticker: 'JFC', companyName: 'Jollibee Foods Corporation', sector: 'Consumer' },
  { ticker: 'MER', companyName: 'Manila Electric Company', sector: 'Utilities' },
  { ticker: 'SM', companyName: 'SM Investments Corporation', sector: 'Holding Firms' },
  { ticker: 'SMPH', companyName: 'SM Prime Holdings, Inc.', sector: 'Property' },
];

test('every safety preset round-trips through safetyPresetForSettings', () => {
  for (const preset of SAFETY_PRESETS) {
    assert.equal(safetyPresetForSettings(preset.settings)?.id, preset.id, preset.id);
  }
});

test('safety presets get progressively riskier', () => {
  const risks = SAFETY_PRESETS.map((preset) => preset.settings.maxRiskPerTradePct ?? 0);
  assert.deepEqual(
    risks,
    [...risks].sort((left, right) => left - right),
    'Careful must risk less per trade than Balanced, and Balanced less than Active',
  );
});

test('customised settings are not mistaken for a preset', () => {
  const balanced = SAFETY_PRESETS.find((preset) => preset.id === 'balanced');
  assert.ok(balanced);

  assert.equal(safetyPresetForSettings({ ...balanced.settings, maxRiskPerTradePct: 1.75 }), null);
});

test('budget presets are positive and ascending', () => {
  assert.ok(BUDGET_PRESETS_PHP.length >= 3);
  for (const amount of BUDGET_PRESETS_PHP) {
    assert.ok(amount > 0);
  }
  assert.deepEqual(BUDGET_PRESETS_PHP, [...BUDGET_PRESETS_PHP].sort((left, right) => left - right));
});

test('an unreachable desk becomes an offline message with the raw text kept aside', () => {
  const friendly = describeFriendlyError({ status: 0, message: 'Failed to fetch' });

  assert.equal(friendly.title, 'The desk is offline');
  assert.equal(friendly.retryable, true);
  assert.equal(friendly.technical, 'Failed to fetch');
  assert.match(friendly.message, /internet connection/i);
});

test('a 503 explains that prices are not published yet, a 404 does not invite a retry', () => {
  const notReady = describeFriendlyError({ status: 503, message: 'No OHLCV snapshot' });
  assert.equal(notReady.retryable, true);
  assert.match(notReady.title, /prices/i);

  const missing = describeFriendlyError({ status: 404, message: 'Unknown ticker NOPE' });
  assert.equal(missing.retryable, false);
  assert.equal(missing.technical, 'Unknown ticker NOPE');
});

test('a rejected request keeps the server sentence but not the retry promise', () => {
  const rejected = describeFriendlyError({ status: 400, message: 'Stop loss must sit below entry.' });

  assert.equal(rejected.message, 'Stop loss must sit below entry.');
  assert.equal(rejected.retryable, false);
});

test('plain errors and unknown throws never leak an empty banner', () => {
  const fromError = describeFriendlyError(new Error('boom'));
  assert.equal(fromError.title, 'Something went wrong');
  assert.equal(fromError.technical, 'boom');

  const fromNothing = describeFriendlyError(null);
  assert.equal(fromNothing.title, 'Something went wrong');
  assert.ok(fromNothing.message.length > 0);
});

test('expectancy copy follows the sign of the average result', () => {
  assert.match(describeExpectancyR(0.4), /made money/);
  assert.match(describeExpectancyR(0.0), /roughly flat/);
  assert.match(describeExpectancyR(-0.4), /lost money/);
  assert.match(describeExpectancyR(null), /no average/i);
});

test('profit factor copy says whether winners covered losers', () => {
  assert.match(describeProfitFactor(2), /comfortably/);
  assert.match(describeProfitFactor(1.1), /only just/);
  assert.match(describeProfitFactor(0.6), /did not cover/);
  assert.match(describeProfitFactor(0), /Not enough graded trades/i);
});

test('brier copy needs a graded forecast before it judges accuracy', () => {
  assert.match(describeBrierScore(0.15, 12), /accurate/);
  assert.match(describeBrierScore(0.22, 12), /slightly better than a coin toss/);
  assert.match(describeBrierScore(0.3, 12), /no better than a coin toss/);
  assert.match(describeBrierScore(undefined, 0), /No forecasted pick/i);
});

test('win rate copy always states the sample it is describing', () => {
  assert.match(describeWinRate(0.7, 10), /More than half/);
  assert.match(describeWinRate(0.5, 10), /About half/);
  assert.match(describeWinRate(0.2, 10), /Fewer than half/);
  assert.match(describeWinRate(0.2, 0), /Nothing has been graded/);
});

test('drawdown and risk-per-trade copy stay in pesos and percent', () => {
  assert.match(describeMaxDrawdownPct(-12.34), /12\.3% below its best level/);
  assert.match(describeMaxDrawdownPct(null), /No drawdown/i);

  const risk = describeRiskPerTrade(1, 100_000);
  assert.match(risk, /lose about/);
  assert.match(risk, /1,000/);
  assert.match(describeRiskPerTrade(0, 100_000), /Set a risk limit/);
});

test('backtest exits translate every server reason and pass anything else through', () => {
  assert.match(describeBacktestExit('TARGET'), /Reached the target/);
  assert.match(describeBacktestExit('stop'), /cut-loss/);
  assert.match(describeBacktestExit('TIME'), /Holding period ended/);
  // The exit ladder's two ratcheted stops must not read like the original cut-loss.
  assert.match(describeBacktestExit('BREAKEVEN'), /break-even/);
  assert.match(describeBacktestExit('TRAIL_EMA20'), /rising average/);
  assert.equal(describeBacktestExit('SOMETHING_NEW'), 'something new');
});

test('alpha copy always says the desk paid friction and the index did not', () => {
  assert.match(describeAlphaPct(4.2), /beat simply holding the PSE index/);
  assert.match(describeAlphaPct(0.3), /roughly level/);
  assert.match(describeAlphaPct(-6), /would have done better/);
  assert.match(describeAlphaPct(null), /No index prices/i);
  assert.match(describeAlphaPct(undefined), /No index prices/i);
});

test('payoff ratio copy compares the average winner with the average loser', () => {
  assert.match(describePayoffRatio(2.4), /worth about twice/);
  assert.match(describePayoffRatio(1.2), /Winners are bigger than losers/);
  assert.match(describePayoffRatio(0.7), /Losers are bigger than winners/);
  assert.match(describePayoffRatio(null), /Not enough closed winners/);
});

test('pyramid copy names the number of legal adds and the ceiling', () => {
  assert.match(describePyramidCount(0), /no extra buys/);
  assert.match(describePyramidCount(1), /One extra buy/);
  assert.match(describePyramidCount(2), /allows no more/);
  assert.match(describePyramidCount(null), /no extra buys/);
});

test('trim guidance names the exact share count the button will send', () => {
  assert.match(describeTrimGuidance(500, 'ict'), /Sell 500 shares of ICT/);
  assert.match(describeTrimGuidance(1_500, 'SM'), /Sell 1,500 shares of SM/);
  // A single board lot cannot be halved, so the copy must not promise a partial exit.
  assert.match(describeTrimGuidance(0, 'JFC'), /all-or-nothing/);
  assert.match(describeTrimGuidance(undefined), /all-or-nothing/);
});

test('portfolio alerts keep the ticker and lose the shouting', () => {
  const plain = describePortfolioAlert('SM: STOP BREACHED — price closed below 800.00');

  assert.match(plain, /^SM: /);
  assert.doesNotMatch(plain, /STOP BREACHED/);

  assert.match(
    describePortfolioAlert('Portfolio heat is 6.2% of equity, above the 5.0% cap.'),
    /lot of money at risk/,
  );
});

test('company-name search finds the household name and the ticker', () => {
  assert.equal(searchCatalog(CATALOG, 'jollibee')[0]?.ticker, 'JFC');
  assert.equal(searchCatalog(CATALOG, 'meralco')[0]?.ticker, 'MER');
  assert.equal(searchCatalog(CATALOG, 'jfc')[0]?.ticker, 'JFC');
  assert.equal(searchCatalog(CATALOG, 'manila electric')[0]?.ticker, 'MER');
  assert.equal(searchCatalog(CATALOG, '').length, 0);
  assert.equal(searchCatalog(CATALOG, 'zzzz').length, 0);
});

test('an exact ticker outranks a company prefix', () => {
  const results = searchCatalog(CATALOG, 'sm');

  assert.equal(results[0]?.ticker, 'SM');
  assert.ok(results.some((stock) => stock.ticker === 'SMPH'));
});

test('labels lead with the familiar name but fall back to the legal name', () => {
  assert.equal(nicknameFor('jfc'), 'Jollibee');
  assert.equal(nicknameFor(null), null);
  assert.equal(companyLabel('JFC', 'Jollibee Foods Corporation'), 'Jollibee');
  assert.equal(companyLabel('XYZ', 'XYZ Holdings Corporation'), 'XYZ Holdings Corporation');
  assert.equal(companyLabel('xyz', ''), 'XYZ');
  assert.equal(companyLabel(undefined, undefined), '');
});

test('catalog lookup accepts a ticker or a company name', () => {
  assert.equal(findCatalogStock(CATALOG, 'JFC')?.ticker, 'JFC');
  assert.equal(findCatalogStock(CATALOG, 'Manila Electric')?.ticker, 'MER');
  assert.equal(findCatalogStock(CATALOG, 'nothing here'), null);
});

test('popular chips are unique, labelled starting points', () => {
  const tickers = POPULAR_PSE_STOCKS.map((stock) => stock.ticker);

  assert.equal(new Set(tickers).size, tickers.length);
  for (const stock of POPULAR_PSE_STOCKS) {
    assert.ok(stock.label.trim().length > 0);
    assert.equal(stock.ticker, stock.ticker.toUpperCase());
  }
});

// ── Ride the wave: market call, entry timing, trust pulse and the "I own it" verdict ────────

const CALIBRATED_PULSE: TrustPulse = {
  horizon: 'WEEK',
  totalPicks: 30,
  resolvedCount: 24,
  openCount: 6,
  winRate: 0.5,
  expectancyR: 0.42,
  profitFactor: 1.83,
  isCalibrated: true,
};

function holdingFixture(overrides: Partial<PositionAdvice> = {}): PositionAdvice {
  return {
    ticker: 'JFC',
    averageCost: 100,
    currentPrice: 103,
    shares: 100,
    breakEvenPrice: 101,
    suggestedStopPrice: 92.5,
    suggestedTargetPrice: 120,
    netReturnPct: 2.08,
    unrealizedNetPnlPhp: 2087,
    unrealizedR: 0.4,
    action: 'HOLD',
    headline: 'Still on track',
    explanation: 'Nothing to do — the plan is still on track.',
    recommendedTrimShares: 0,
    ...overrides,
  };
}

// ── Non-solicitation copy: the desk describes its model, it does not tell anyone to trade ─────

test('verdict badges report model qualification instead of soliciting a trade', () => {
  assert.equal(describePlainVerdict('TRADE').label, 'Qualifies (Full)');
  assert.equal(describePlainVerdict('SMALL_SIZE').label, 'Qualifies (Half)');
  assert.equal(describePlainVerdict('WATCH_ONLY').label, 'Watch Only');

  // An unknown verdict is never sold as a buy.
  assert.equal(describePlainVerdict('SOMETHING_NEW').buyable, false);

  for (const verdict of ['TRADE', 'SMALL_SIZE', 'WATCH_ONLY'] as const) {
    assert.doesNotMatch(describePlainVerdict(verdict).label, /buy now|strong buy/i);
  }
});

test('the market call is a traffic light with one instruction attached', () => {
  assert.equal(describeMarketCall('RIDE_THE_WAVE').label, 'Green light');
  assert.equal(describeMarketCall('BE_SELECTIVE').label, 'Amber light');
  assert.equal(describeMarketCall('STAY_IN_CASH').label, 'Red light');
  assert.match(describeMarketCall('STAY_IN_CASH').instruction, /cash/i);

  // An unknown call never invents a colour: it asks the reader to wait for the read.
  const unknown = describeMarketCall('SOMETHING_NEW');
  assert.equal(unknown.label, 'Reading the market');
  assert.match(unknown.instruction, /wait/i);
});

test('entry timing says wait unless the price is already inside the band', () => {
  const ready = describeEntryTiming('READY_NOW', 10, 10.5);
  assert.equal(ready.wait, false);
  assert.match(ready.detail, /10\.00/);
  assert.match(ready.detail, /10\.50/);

  assert.equal(describeEntryTiming('NEAR_ENTRY', 10, 10.5).wait, true);
  assert.equal(describeEntryTiming('EXTENDED_WAIT_PULLBACK', 10, 10.5).wait, true);
  assert.equal(describeEntryTiming('BROKEN_BELOW_STOP', 10, 10.5).wait, true);
  assert.equal(describeEntryTiming('WAIT_WATCH_ONLY').wait, true);
  assert.equal(describeEntryTiming('UNKNOWN_STATUS').wait, true);
});

test('too-late entry names the exact price the reader must not pay', () => {
  const late = describeEntryTiming('EXTENDED_WAIT_PULLBACK', 10, 10.5);

  assert.equal(late.label, 'Too late to chase');
  assert.match(late.detail, /10\.50/);
  assert.match(late.detail, /pullback/);
});

test('the trust pulse reads the ledger verbatim and admits an empty one', () => {
  assert.equal(
    describeTrustPulse(CALIBRATED_PULSE),
    '24 of 30 picks graded · 50% reached target · +0.42R average · profit factor 1.83.',
  );

  const thin = describeTrustPulse({ ...CALIBRATED_PULSE, resolvedCount: 4, isCalibrated: false });
  assert.match(thin, /4 of 30 picks graded/);
  assert.match(thin, /small sample so far/);

  assert.match(describeTrustPulse({ ...CALIBRATED_PULSE, resolvedCount: 0 }), /No graded picks yet/);
  assert.match(describeTrustPulse(null), /could not be read/);
});

test('the own-it verdict keeps the server action and adds the fee-paid pesos', () => {
  const holding = describeHoldingVerdict(holdingFixture());
  assert.equal(holding.label, 'Hold — plan on track');
  assert.equal(holding.urgent, false);
  assert.match(holding.detail, /2,087\.00 in your pocket/);

  const losing = describeHoldingVerdict(
    holdingFixture({ action: 'STOP_BREACHED', unrealizedNetPnlPhp: -1250.5, netReturnPct: -1.25 }),
  );
  assert.equal(losing.urgent, true);
  assert.match(losing.detail, /1,250\.50 less than you paid/);

  // No share count: the verdict stands, the peso sentence is simply omitted.
  const sizeless = describeHoldingVerdict(holdingFixture({ unrealizedNetPnlPhp: null, shares: 0 }));
  assert.equal(sizeless.label, describePositionAction('HOLD').label);
  assert.doesNotMatch(sizeless.detail, /pocket|less than you paid/);
});

test('a pick the tape never filled reads as a non-event, never as a loss', () => {
  assert.equal(describeOutcomeStatus('UNFILLED'), 'Entry never filled');
  assert.equal(describeOutcomeStatus('OPEN'), 'Still open');
  assert.equal(describeOutcomeStatus('EXPIRED_WIN'), 'Finished higher');

  // An unknown status degrades to readable words instead of leaking the enum casing.
  assert.equal(describeOutcomeStatus('SOMETHING_NEW'), 'something new');
});

test('a missing realized R prints a dash instead of a fabricated zero', () => {
  assert.equal(formatR(null), '—');
  assert.equal(formatR(undefined), '—');
  assert.equal(formatR(Number.NaN), '—');
  assert.equal(formatR(1.6349), '+1.63R');
  assert.equal(formatR(-1), '-1.00R');
  assert.equal(formatR(0), '0.00R');
  assert.equal(formatProbability(0.4242), '42%');
  assert.equal(formatProbability(null), '—');
});

test('the newest stored session is what "latest" means, not today', () => {
  assert.equal(newestSessionDate([]), null);
  assert.equal(newestSessionDate([{ date: '2026-11-20' }]), '2026-11-20');
  assert.equal(
    newestSessionDate([{ date: '2026-11-20' }, { date: '2026-11-24' }, { date: '2026-11-24' }]),
    '2026-11-24',
  );

  // Default view and the newest day itself are both "live"; only an older day is history.
  assert.equal(isViewingHistory(null, '2026-11-24'), false);
  assert.equal(isViewingHistory('2026-11-24', '2026-11-24'), false);
  assert.equal(isViewingHistory('2026-11-20', '2026-11-24'), true);

  // An empty store has no history to warn about.
  assert.equal(isViewingHistory('2026-11-20', null), false);
});

test('the primary button re-runs a stored day instead of promising the latest one', () => {
  assert.equal(describeRunLabel(false, true), 'Run Latest Screen');
  assert.equal(describeRunLabel(false, false), 'Run screen');
  assert.equal(describeRunLabel(true, true), 'Re-run This Day');
  assert.equal(describeRunLabel(true, false), 'Re-run session');
});

test('the history strip says the page is history and names the day in Pro mode', () => {
  assert.match(describeHistoryBanner('2026-11-20', true), /history, not today’s plan/);
  assert.match(describeHistoryBanner('2026-11-20', false), /^2026-11-20 is a stored session/);
  assert.match(describeHistoryBanner('2026-11-20', false), /does not fetch a new one/);
  assert.match(describeHistoryBanner(null, false), /^This session is a stored session/);
});

// The Simple home is a list of signals: one collapsed line per pick, the "why" one tap down.
// These pin the four facts that line is allowed to state, so the list can never drift from the
// server figures the expanded panel prints.
const ROW_BASE = {
  edgeVerdict: 'TRADE' as const,
  entryPrice: 245,
  targetPrice: 268,
  stopLoss: 231,
  winProbability: 0.61,
  breakEvenWinRate: 0.45,
};

const ROW_WAVE = {
  idealBuyPrice: 244,
  cutLossPrice: 231,
  breakEvenStopPrice: 246,
  targetPrice: 268,
  maxChasePrice: 246,
  lockBreakEvenTriggerPrice: 258,
  trailWaveTriggerPrice: 265,
  trailStopPrice: 252,
  entryStatus: 'READY_NOW',
  maxHoldTradingDays: 10,
};

test('a signal row states the verdict and the three prices in the order they are used', () => {
  const row = describeSignalRow({ ...ROW_BASE, wavePlan: ROW_WAVE });

  assert.equal(row.verdictLabel, 'Qualifies (Full)');
  assert.equal(row.headline, 'Buy ₱245.00 · Target ₱268.00 · Cut-loss ₱231.00');
  // Buy first, then the target, then the cut-loss — the order a reader acts on them.
  assert.ok(row.headline.indexOf('Buy') < row.headline.indexOf('Target'));
  assert.ok(row.headline.indexOf('Target') < row.headline.indexOf('Cut-loss'));
});

test('only a price already inside the server band earns the entry chip', () => {
  const ready = describeSignalRow({ ...ROW_BASE, wavePlan: ROW_WAVE });
  assert.equal(ready.entryChip, 'Inside the band');
  assert.match(ready.subline, /inside the model’s entry band/);

  // NEAR_ENTRY and EXTENDED_WAIT_PULLBACK both mean "wait" — no chip, and the line says why.
  const waiting = describeSignalRow({ ...ROW_BASE, wavePlan: { ...ROW_WAVE, entryStatus: 'NEAR_ENTRY' } });
  assert.equal(waiting.entryChip, null);
  assert.match(waiting.subline, /below the ideal buy/i);
});

test('a watch-only name says why it cannot be funded and never asks to be bought', () => {
  const row = describeSignalRow({ ...ROW_BASE, edgeVerdict: 'WATCH_ONLY', wavePlan: ROW_WAVE });

  assert.equal(row.verdictLabel, 'Watch Only');
  assert.equal(row.entryChip, null);
  assert.match(row.subline, /does not clear broker fees/);
});

test('a run without a wave plan falls back to the odds instead of inventing an entry status', () => {
  const row = describeSignalRow(ROW_BASE);

  assert.equal(row.entryChip, null);
  assert.match(row.subline, /Good odds/);
});

test('an unreported price renders a dash on the row, never a zero', () => {
  const row = describeSignalRow({ ...ROW_BASE, entryPrice: null as unknown as number });

  assert.match(row.headline, /^Buy —/);
  assert.doesNotMatch(row.headline, /₱0/);
});

test('the next step names the control a reader can actually press', () => {
  const withIdeas = describeNextStep({ buyableCount: 3, watchOnlyCount: 4 });
  assert.match(withIdeas, /open a name below/i);
  assert.match(withIdeas, /My Trades/);
  assert.match(withIdeas, /^Next:/);

  const waiting = describeNextStep({ buyableCount: 0, watchOnlyCount: 2 });
  assert.match(waiting, /nothing is buyable today/i);
  assert.match(waiting, /watch list below/i);
  // A waiting session must not tell the reader to open a row that does not exist.
  assert.doesNotMatch(waiting, /open a name below/i);

  const empty = describeNextStep({ buyableCount: 0, watchOnlyCount: 0 });
  assert.match(empty, /nothing cleared the screen today/i);
  assert.match(empty, /nothing to open/i);
});

test('the next step never promises a profit or orders a trade', () => {
  const copies = [
    describeNextStep({ buyableCount: 5, watchOnlyCount: 0 }),
    describeNextStep({ buyableCount: 0, watchOnlyCount: 5 }),
    describeNextStep({ buyableCount: 0, watchOnlyCount: 0 }),
  ].join(' ');

  assert.doesNotMatch(copies, /guarantee|sure profit|risk-free|you must buy/i);
  assert.match(copies, /practise/);
});

