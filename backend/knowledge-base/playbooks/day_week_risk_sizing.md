---
title: Day and Week Risk Playbook — Sizing, Stops and Fee-Aware R:R
slug: day_week_risk_sizing
mandatory: false
horizons: DAY, WEEK
categories: RISK, POSITION_SIZING
---

## Risk is defined before the entry, not after

The only decision that is fully under your control is how much you can lose. Every DAY
and WEEK plan must state three numbers before it can be published: entry, invalidation
(stop) and objective (target). A plan without a stop is not a plan.

The desk's convention:

- Risk per idea is a fixed fraction of the book — 0.5% to 1.0% for a swing idea, 0.25% to
  0.5% for an intraday idea. The percentage is small enough that being wrong five times
  in a row is survivable.
- Position size is derived, not guessed: `shares = risk_budget / (entry - stop)`, then
  rounded down to the exchange board lot. The board lot can make a position smaller than
  the formula wants, and that is fine.
- Correlation is concentration. Three bank ideas are one idea about rates. Cap total
  exposure per sector in a swing book.

## ATR-based stops that survive normal noise

A stop placed at an arbitrary 2% will be hit by ordinary daily range in a volatile PSE
mid-cap. The desk scales the stop to the instrument:

- Stop distance = 1.2 to 1.5 ATR(14) for DAY and WEEK setups, anchored to structure (the
  contraction low, the breakout pivot, the swing low) and then rounded to a valid tick.
- Target distance = 2.5 to 3.0 ATR, or the next obvious resistance shelf — whichever is
  closer, because the market has to actually reach it.
- If the structure stop is further away than two ATR, the setup is too extended; wait for
  a tighter base rather than widening the risk.
- Never widen a stop to "give it room". Widening converts a small loss into the loss
  that ends the quarter.

## Fee-adjusted reward-to-risk

The gross reward-to-risk ratio is a vanity metric on the PSE because the round trip costs
about 0.69% of notional (0.295% buy leg + 0.395% sell leg, the extra being the 0.10% stock
transaction tax charged on sales only). Both the risk and the reward shrink by the fee
drag, and the ratio that matters is:

`net R:R = (target - entry - fees) / (entry - stop + fees)` where `fees` is the two-leg
round-trip cost in pesos per share.

Consequences the desk enforces:

- A DAY plan must clear a net reward-to-risk gate of at least 2.0 after fees. Anything
  below that is not worth the screen time.
- Tight scalps on high-priced names are hit hardest: a ₱0.10 stop on a ₱100 stock is
  0.1%, less than the fee drag, so the trade cannot be profitable even when it works.
- The minimum viable gross move on a DAY idea is about 0.9% just to cover friction and the
  tick. If the expected edge is smaller, the correct output is "no trade today", not a
  lower-quality list.

## Intraday guardrails specific to the PSE

- The static band and the dynamic halts mean a violent move can pause the tape. Size so
  that a halt-and-gap does not breach the risk budget.
- Liquidity is lumpy: a name trading ₱10 million a day will not absorb a position that
  needs ₱2 million without moving 2-3%. Cap the position at a small fraction of average
  daily turnover.
- Opening prints are not prices. The first minutes carry the widest spreads of the
  session; wait for the auction imbalance to clear before treating a level as valid.
- Closing auction behaviour is its own game. Do not treat the 15:20 print as
  representative of the next session's open.

## The trade-management rules that actually protect capital

- Do not average down on a broken plan. Adding at a lower price moves the average, not
  the thesis; if the invalidation level was right, the position should be gone.
- Move the stop to break-even only after the trade has proven itself — typically after a
  close beyond the first target tranche — not on the first green candle.
- Take partial profit at the first objective when the tape is choppy; leave a runner only
  when the index and the sector are both supporting.
- After two consecutive stop-outs in the same theme, stop trading that theme for the
  week. The pattern, not the individual ticker, is what is broken.

## What the desk refuses to publish

- Long ideas on names pending a trading halt or a suspension-related disclosure, at any
  horizon. You cannot manage risk in a stock you cannot sell.
- Targets above a live tender-offer price, or above the static ceiling for a same-session
  plan.
- Entries that are more than one ATR above the pivot the plan is supposedly buying. That
  is chasing, with the stop already too far away.
- Plans whose net reward-to-risk is below the horizon gate. Fewer, better ideas beat a
  full page of mediocre ones.
