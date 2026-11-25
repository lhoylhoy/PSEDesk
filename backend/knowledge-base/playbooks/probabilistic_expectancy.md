---
title: Probabilistic Expectancy — Break-Even Rates, Expected Value and Quarter-Kelly
slug: probabilistic_expectancy
mandatory: false
horizons: DAY, WEEK, MONTH, YEAR
categories: RISK, POSITION_SIZING, PROBABILITY
---

## A win rate without its break-even rate is decoration

Every PSE plan has three prices, and those three prices already imply the win rate the
trade needs just to break even. Friction is charged per leg and **per trade date**: 0.295%
on the buy leg, and on the sell leg 0.295% plus the stock transaction tax in force —
0.10% under RA 12214 (CMEPA) from 1 July 2025, 0.60% before that, i.e. 1.190% round trip
for any history dated before the cut. A fixed **PHP 20 broker commission plus 12% VAT**
sits on top of the percentage, so a clip whose gross is under roughly PHP 8,000 pays
proportionally more than the headline rate; the desk therefore sizes and grades in
**settled pesos**, never as `shares × per-share spread`.

`net R:R = (target - entry - fees) / (entry - stop + fees)` where the two legs are costed
separately, not at a single symmetric rate.

`break-even win rate = net risk / (net reward + net risk)`

The consequence is uncomfortable and must be stated honestly: a textbook 1:2.0 gross swing
with a 5% stop is only about 1:1.63 net, so it needs roughly a 38% hit rate, not the naive
33%. Any thesis that says "it only has to work one time in three" is arithmetic that
ignores the exchange — and any backtest that charged today's 0.10% tax on a 2023 trade
manufactured about 0.2R of phantom edge per trade. The desk publishes the break-even rate
beside every plan and treats a plan whose estimated probability does not clear break-even
as a watch-only idea.

## Where the probability comes from

The desk does not accept a model's opinion about odds. The probability is assembled from
three server-side inputs, and each one is bounded so no single input can dominate:

- **Empirical base rate.** Every ticker is replayed against its own OHLCV history: at each
  session where price closed above its 50D average, a stop at 1.5 ATR and a target at 3.0
  ATR are placed and the next ten sessions are walked forward. The resulting hit rate is
  shrunk toward a neutral 42% prior with 12 pseudo-observations, so five lucky trades
  cannot claim an 80% edge. Fewer than 60 bars of history means "no base rate", not "zero".
- **Barrier geometry.** A target three times further away than the stop is hit first far
  less often than a tight one. Geometry enters as `net risk / (net reward + net risk)`,
  which is why an unreachable target lowers the probability instead of being rewarded for
  a big gross number.
- **Bounded tilts.** Weinstein stage, 63-session relative strength versus the PSEi, a
  volatility-contraction ratio below 0.85, market breadth and the tape regime each move the
  estimate by a few points at most; soft trap warnings subtract up to 4.5 points; raw
  conviction contributes at most 4 points.

The estimate is clamped to 5-95%. A 1% or 99% probability is never a real edge, it is a
broken model.

## Expected value is the only reason to take a trade

`EV(R) = p x net R:R - (1 - p)`

Positive EV is the floor, not the goal. The desk's gates are deliberately horizon-specific:

- **DAY:** net R:R at least 2.0 and EV at least +0.20R.
- **WEEK:** net R:R at least 1.5 and EV at least +0.15R.
- **MONTH:** net R:R at least 1.6 and EV at least +0.20R.
- **YEAR:** net R:R at least 1.8 and EV at least +0.25R.

A plan that clears the ratio but not the EV gate is **SMALL_SIZE** at best. A plan with EV
at or below zero is **WATCH_ONLY** no matter how good the narrative is. TRADE additionally
requires a probability at least five points above break-even, a non-bearish tape and no
soft trap warning. This is why a quiet day can legitimately produce zero trades: the
correct output is a shorter list, never a weaker standard.

## Quarter-Kelly, because the probability is a guess

Kelly sizing assumes the probability is exact. Ours is a shrunk estimate built from a
finite sample, so the desk uses a quarter of the Kelly stake and caps the result at 20% of
equity:

`f = 0.25 x (p - (1 - p) / net R:R)`

A negative edge returns zero, not a short. When converting the stake into shares, the
position is additionally floored to the exchange board lot, capped at 20% of equity in
notional terms and capped at 2% of the name's 20-day average turnover so a thin PSE name
can never be oversized even when the risk budget would allow it.

## Cash is a position

The run-level recommended cash reserve starts from the tape: 70% in a bearish regime, 40%
neutral, 20% bullish. Weak breadth adds 10-20 points; participation above 65% of the
universe removes 10. Each Top-10 slot that did not earn a TRADE (with a half-sized
SMALL_SIZE counting as half a slot) rolls a further 4 points into cash. Holding 60% cash
while the breadth line is collapsing is a decision, and it should be visible on the
screen, not buried in prose.

## Grading the desk with real forward sessions

Every published pick enters a forward-outcome ledger and is graded only against the
sessions that actually followed it:

- The stop is evaluated before the target on every daily bar. If one session touched both,
  the loss is assumed to have filled first. Daily OHLC cannot order the two, and the
  conservative assumption is the honest one.
- A target touch resolves as a win, a stop touch as a loss, and the horizon window
  (3, 10, 42 or 126 sessions) forces an expiry mark at the closing price. Results are
  reported in **R**: the fee-adjusted per-share move divided by the fee-adjusted risk per
  share, so a clean stop is about -1.0R and a clean target equals the net R:R.
- The scorecard reports realized win rate, expectancy in R, profit factor, a Brier score
  for calibration and a breakdown by verdict. If TRADE picks do not beat WATCH_ONLY picks
  over a real sample, the verdict rule is wrong and must be revised — that is the point of
  keeping the ledger.

## Pre-mortems are mandatory

Before a plan can be published it must carry a `bear_case` of at most 30 words naming the
invalidation level and the most plausible way the setup fails. A pre-mortem that restates
the thesis is rejected, and the server substitutes its own deterministic version naming
the stop and the overhead supply. The purpose is not decoration: writing down the failure
mode in advance is what makes the stop executable when the tape turns.
