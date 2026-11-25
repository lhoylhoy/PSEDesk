---
title: Asymmetric Wealth Compounding — Streak Sizing, Pyramiding and the Exit Ladder
slug: asymmetric_wealth_compounding
mandatory: false
horizons: DAY, WEEK, MONTH, YEAR
categories: RISK, POSITION_SIZING, EXITS, COMPOUNDING
---

## The desk compounds one asymmetric bet at a time

A concentrated PSE book does not compound by being right often. It compounds by losing a
small, fixed fraction of equity when wrong and letting the winners pay several times that
fraction when right. Three rules keep that asymmetry intact:

1. **Risk is a fraction of equity, never of conviction.** A 1% risk unit means the distance
   from entry to stop, plus the 0.690% round trip, costs 1% of equity. After a stop-out the
   next unit is smaller in peso terms because equity is smaller — that is the compounding.
2. **Streaks do not change the unit.** Two losses in a row are not a reason to double the
   next bet, and two wins are not a reason to halve it. The desk's governor already scales
   risk by the measured edge, the tape's volatility and the book's drawdown; adding a
   discretionary streak multiplier on top would double-count the same evidence and turn a
   normal losing run into a fatal one.
3. **Only a funded slot earns a new entry.** With a fixed number of open slots, a new
   candidate must beat the weakest *open* position on expected value per unit of risk, not
   merely beat the bench. Otherwise the book silently drifts into its worst ideas.

## Pyramiding has one legal shape

Adding to a winner is allowed only in the direction of confirmation and only at a smaller
size:

- The position must already be at least **+1R** in net terms and the stop must already have
  moved to break-even, so the add cannot turn a protected trade into a loss.
- Each add is smaller than the previous clip (0.5x, then 0.25x of the original unit). The
  average cost rises, never falls, because averaging down is prohibited outright.
- Every add must still respect the single-position cap, the sector cap and the 2% of 20-day
  turnover participation limit. A pyramid that cannot be executed inside those caps is not
  executed.

## The exit ladder is mechanical

Most of the compounding comes from exits, not entries. Once a position is open the desk
walks this ladder in order and acts on the first rung that triggers:

1. **Stop breached** → close, no discussion.
2. **Target reached** → take at least half the position off; the remainder rides only if the
   stop has already moved and the trend structure is intact.
3. **+1R reached** → move the stop to break-even so the trade can no longer lose money.
4. **+1.5R with a rising 20-day average** → trail the stop behind that average instead of a
   fixed level, letting a trend pay for more than its original target.
5. **Forward window expired** → close the remainder. A thesis with no time stop becomes a
   hope, and a hope occupies a slot that a fresh setup could use.

## What this playbook is not

It is not a promise of returns, and it does not override the deterministic risk rules in
`pse_rules` or the expectancy maths in `probabilistic_expectancy`. Sizing, the round-trip
friction, the probability estimate and the TRADE / SMALL_SIZE / WATCH_ONLY verdict stay
server-owned. This page explains **why** the server sizes, adds and exits the way it does.
