---
title: PSE Market Mechanics and Structural Traps
slug: pse_rules
mandatory: true
horizons: DAY, WEEK, MONTH, YEAR
categories: MECHANICS, DISCLOSURE, RISK
---

## Orderable prices: tick sizes and board lots

The Philippine Stock Exchange trades on a discrete tick grid, and every order must be a
multiple of the board lot for its price bracket. The desk enforces the full 15-tier
Revised Trading Rules table, from the ₱0.0001 tick used by sub-centavo mining shells up
to the ₱5.00 tick for ₱5,000-and-above names.

| Price band (₱) | Tick | Board lot |
| --- | --- | --- |
| 0.0001 – 0.0099 | 0.0001 | 1,000,000 |
| 0.0100 – 0.0490 | 0.0010 | 100,000 |
| 0.0500 – 0.2490 | 0.0010 | 10,000 |
| 0.2500 – 0.4950 | 0.0050 | 10,000 |
| 0.5000 – 4.9900 | 0.0100 | 1,000 |
| 5.0000 – 9.9900 | 0.0100 | 100 |
| 10.0000 – 19.9800 | 0.0200 | 100 |
| 20.0000 – 49.9500 | 0.0500 | 100 |
| 50.0000 – 99.9500 | 0.0500 | 10 |
| 100.0000 – 199.9000 | 0.1000 | 10 |
| 200.0000 – 499.8000 | 0.2000 | 10 |
| 500.0000 – 999.5000 | 0.5000 | 10 |
| 1,000.0000 – 1,999.0000 | 1.0000 | 5 |
| 2,000.0000 – 4,998.0000 | 2.0000 | 5 |
| 5,000.0000 and above | 5.0000 | 5 |

A proposal that ignores the grid cannot be executed. A ₱1.13 target on a ₱1.1150 stock is
fictional; the orderable target is ₱1.1300 and the orderable stop is the nearest tick on
the other side of the entry. Board lots also decide whether a plan is affordable at all,
so sizing is expressed in whole lots, never in fractional shares.

## The static price band

The exchange publishes a static band around the previous close: no valid order may print
above roughly +50% or below roughly -30% of that reference price, and dynamic thresholds
inside the band trigger temporary trading halts on violent moves.

Two consequences for signal generation:

- Any target or stop that sits outside the band on the same session is unreachable and
  must be clamped back inside it before the plan reaches a user.
- A name that is already pinned near its ceiling cannot offer reward: the remaining
  upside to the static ceiling has to be worth the risk, otherwise the trade is a
  lottery ticket with the stop still open below.

For DAY and WEEK ideas the model must state levels that a broker screen would accept
today. For MONTH and YEAR ideas multi-quarter targets may sit outside the daily band —
they are reached over many sessions, not in one print — but they still have to be
expressible on the tick grid.

## Round-trip friction

Every PSE round trip carries commission and VAT on both legs plus the exchange trading fee
and the clearing/SCCP fee, and — on the sell leg only — the stock transaction tax. Under
Republic Act No. 12214 (the Capital Markets Efficiency Promotion Act, effective 1 July
2025) that tax is **0.10%** of the sale, down from the pre-CMEPA 0.60%. The legs are
therefore asymmetric:

- Buy leg: 0.25% commission + 12% VAT on that commission + 0.005% PSE fee + 0.01% SCCP fee
  = **0.295%**.
- Sell leg: the same charges plus 0.10% stock transaction tax = **0.395%**.
- Round trip: **0.690%** of notional.

At the top tick band the spread adds more on top; the desk prices the tick at the plan's
entry so a ₱100.00 name carries about 0.89% all-in. On a 3% intraday scalp that is still a
fifth of the gross edge, and on a 1.5% move the trade is already a loser before it starts.

Therefore:

- A DAY plan must clear its fee-adjusted reward-to-risk gate, not the gross one.
- A stop 2% below entry on a 0.69% round trip needs a target far enough above entry that
  the net reward still pays for the risk taken.
- The tax asymmetry matters for partial exits: every sale pays the 0.10% again, so a plan
  that dribbles out in five tranches pays it five times.
- Fees are a fixed cost of the venue, not a variable to optimise away. If the net
  number does not work, the correct action is to skip the trade, not to widen the stop.

## Stock rights offerings and follow-on offers (dilution)

A stock rights offering (SRO) lets existing holders buy new shares at a discount to the
market. The ex-rights price is a weighted average of the old shares and the discounted
new ones, so the screen price mechanically drops even though nothing is wrong with the
business. On thin names the entitlement is also the only way to avoid being diluted.

Treat an SRO announcement as a structural event, not a bullish catalyst:

- The theoretical ex-rights price is the reference, so any plan built on the pre-offer
  close is wrong until the adjustment is paid through the tape.
- Offering sizes above roughly a quarter of the existing share count are hard
  disqualified: the overhang is larger than normal order flow can absorb.
- Management history matters. A company that returns to shareholders for cash every
  cycle is funding operations, not growth.
- A follow-on offering (FOO) or a private placement at a discount creates the same
  overhang without the entitlement. It is a warning, not a disqualifier: the stock can
  still work, but only after the placed shares have been distributed.

## Tender offers and price pins

A tender offer fixes a price at which a large holder will buy the float. Once the offer
is live, the stock trades at or just below the offer price and stops responding to
ordinary supply and demand — the plan's reward is capped by a corporate action, not by
technical structure.

If a tender offer is pending, or the stock has been pinned within a tick or two of a
disclosed offer price for several sessions, do not publish a directional target above
that price. Any breakout entry above the pin is a liquidity trap.

## Trading halts and suspension risk

The exchange halts a name when a material disclosure needs clarification, when required
reports are overdue, or when price movement hits a dynamic threshold. A halt is not a
dip; you cannot exit during it, and the reopening print can gap through any stop.

Names with a pending suspension risk — late annual or quarterly reports, a qualified
audit opinion, a disclosure that trading is suspended pending clarification — are hard
disqualified for every horizon. Capital that cannot be retrieved is not an investment.

## Ex-dividend and special dividend drops

On the ex-date the exchange adjusts the reference price downward by the declared cash
dividend. A 4% dividend payer therefore opens about 4% lower and the chart prints a gap
that is not selling pressure. Signal logic must not read that gap as deterioration.

The same adjustment is why a "high dividend" screen is not free money: cash dividends
are taxed at 10% for residents, and the price you pay before the ex-date already embeds
the distribution. Judge dividend sustainability from payout ratio and free cash flow,
not from the trailing yield alone.

## Reading the disclosure feed correctly

Disclosures arrive before the numbers do, and the wording is designed to be neutral.
Sort them by what they change:

- **Ownership and capital structure**: SRO, FOO, placement, buyback, tender offer,
  change in control, foreign ownership limit breaches.
- **Earnings mechanics**: profit warnings, one-off gains, impairments, related-party
  transactions, auditor changes.
- **Operations**: plant commissioning, contract awards, franchise or rate decisions,
  mine or vessel suspensions, force majeure.
- **Governance**: board changes, delisting notices, trading suspensions, report delays.

A single disclosure rarely decides a trade. A cluster of them does: an SRO plus a profit
warning plus an auditor resignation is a very different company from the one in last
quarter's chart. When the disclosure trail contradicts the chart, believe neither the
chart nor the headline — stand aside.

