# Product Context — PSE Signal Desk

## Why it exists

Philippine equity research is noisy: thin names can look technically perfect until you try to trade them, and a daily buy list is only credible if it accounts for what happened to **yesterday's** list. PSE Signal Desk addresses four concrete problems:

1. **Liquidity is a prerequisite, not a score.** A hard PHP 5M average 20-day turnover floor removes names that cannot absorb a real position before any model sees them.
2. **Recommendations need memory.** A buy list that silently forgets yesterday's failures is marketing, not research. The desk persists each run, recomputes drift server-side and forces every exit into an auditable category.
3. **Models must not be trusted with invariants.** The LLM ranks and explains; the server owns liquidity, tick rounding, the static band, board-lot sizing, the net-R:R gate, drift, trap flags and drop classification.
4. **A money manager needs a mandate per horizon.** A `DAY` trade and a `YEAR` compounding position cannot share one prompt. Each horizon has its own mission, RSI band, ATR multiples, disclosure lookback, hysteresis and playbook shelf — grounded in a curated, local knowledge base instead of the model's memory.
5. **The desk must learn from its own results, not just re-rank.** Every pick is graded against what the price did afterwards; that ledger trains one money-manager model per horizon, corrects the claimed odds, and is fed back into the prompt as a track record and as `RECENT_STOP_OUTS`. A learned model only touches live sizing when it beats a neutral prior on trades it never saw — otherwise the desk says so and keeps the prior in charge.

## Target users

- A single research desk / analyst running a **long-only PSE book across four horizons** (intraday, swing, position, compounding).
- Reviewers who want to see *why* a name entered, held, moved or left the Top 10 — including the exact playbook section the model cited.
- Anyone who values **live-or-fail honesty**: when the feed is down the desk says so instead of showing invented prices.

## Product experience goals

- **Plain English first, depth on demand — and one call before the list.** The default surface is a **Simple** view a non-technical investor can act on: a **Ride the Wave** market call at the top (one traffic light and one instruction — ride it / be selective / stay in cash — with a 10–90 direction score, the plain reasons and which sectors money is moving into or out of), the picks already in the buy zone, then at most **three** advice cards (“buy in this band / cut here / lock break-even at +1R / ride or trail to the target”, how many shares, what you keep or lose after fees, the one thing that would prove it wrong) with a per-card *Technical details* drawer. A persisted `Simple | Pro` pill in the header switches to the dense quant terminal; nothing about the data changes, only how much of it is on screen at once.
- **Terminal-like where it counts.** Pro mode keeps the dense, readable terminal: tabular numbers, monospace tickers, mint/rose/amber/sky semantic accents. Both modes paint on one CSS-variable palette, so the header's **light / dark** switch flips the whole app (stored per browser, system preference on a first visit) without changing a single number.
- **Company names before ticker codes.** Search accepts “jollibee”, “meralco” or “JFC” and resolves through the server's static PSE catalog; cards, dialogs and the blotter lead with the familiar company name and keep the ticker as a small mono badge. An ambiguous query (“bank”) is asked again, never silently priced as one company.
- **A beginner is set up in three answers.** On a fresh book the welcome banner asks how much play money to practise with (₱25k–₱500k presets) and how careful to be (Careful 0.5% / Balanced 1% / Active 2% risk per trade, with matching heat, position, sector and drawdown caps); the `How it works` guide lists what the desk does, a glossary of buy price / sell price / cut-loss / board lot / fees, and what it will *not* do. Both write only settings the server clamps, and every action says “Practise” because the blotter is paper-only.
- **Actionable means copy-able into a broker app.** Every order box carries a checklist (search the ticker → Buy → Limit order → shares → limit price → cash needed → exit reminders) built from the server ticket, so the reader never has to translate the card into a broker screen themselves.
- **One screen tells the story.** Header (view-mode pill, four horizon tabs + session + Run, secondary sheets under `More ▾`) → the Simple **market-call hero** (traffic light, score gauge, reasons, sector chips, the desk's trust line, institutional tiles in one drawer) or, in Pro, the nine institutional tiles + rotation strip → the names already in the buy zone → the top-three advice cards (Simple) or the Top 10 table (Pro) → dropped-from-yesterday panel. Clicking a card opens thesis, the four-step roadmap, the **server order ticket**, technicals, dividends, classified disclosures with trap badges, playbook citations and rank history; the header adds **Check a Stock** (research, with a second tab for a position the reader **already owns**) and **My Trades** (the paper blotter, with an alert count).
- **Grounded theses.** Every thesis can point at the playbook sections that were actually retrieved; the citations list is server-filtered, never model-invented.
- **Honest empty states.** "No live session stored yet" vs "Market data loaded, no signal run yet for this horizon" are distinct; the first explains that feeds + `OLLAMA_API_KEY` are required.
- **No dead ends.** API-offline errors explain how to start the API and offer Retry; missing key returns `400`, dead feeds return `503` with the upstream reason, and unreported figures render `—` / `unreported` rather than `0`.
- **Manila time everywhere.** Trading days, timestamps and the scheduled run use Asia/Manila (UTC+8, no DST).
- **Any screen, one layout.** The same run reflows from a 320px phone to a 2560px desktop: dialogs become bottom sheets with a pinned title, wide tables hand over to cards with the ticker pinned, and cached data paints before a refresh lands — so opening a panel or switching horizon feels instant rather than like a page load. No mobile route, no second component tree, no extra request.

## Core user flows

1. **First run (Simple):** open the terminal → the legal gate blocks the desk with an un-dismissable *Before you use the desk* dialog (not advice, your decisions, hypothetical results, what leaves the machine, your data) that must be accepted before anything renders → the welcome banner asks for a practice budget (₱25k / ₱50k / ₱100k / ₱250k / ₱500k presets or a custom amount) and a safety level (Careful 0.5% · Balanced 1% · Active 2% risk per trade, each with its own heat, sector and drawdown limits) → `How it works` explains the desk and its limits → **Run Latest Screen** (no date) pulls the live session from Yahoo Finance + PSE Edge and ranks it → the market-call hero says whether to ride, be selective or sit in cash and why, then at most three cards report which names cleared the fee-adjusted test, the band the model would pay, where the cut-loss sits, when the stop moves to break-even and where the wave has to reach before the target; every raw desk figure sits one click away. Next trading day's run shows updates and drop-outs from real history.
2. **Switch depth or horizon:** toggle `Simple | Pro` in the header — the choice persists per browser. Simple keeps the advice cards, summary regime cards and friendly dialogs; Pro restores the 14-column table, the nine regime tiles, the rotation strip, ticket governor/slippage tiles and the Knowledge Base action. Inside either mode the four horizon tabs (`DAY`/`WEEK`/`MONTH`/`YEAR`) re-read a different mandate/stance over the same stored session, each with its own run and drift chain.
3. **Daily review:** pick a stored session from the date selector → read the regime strip → scan the Top 10 → read the drop panel → open a ticker for technicals, disclosures, trap badges, citations and rank history.
4. **Feed the foreign pillar:** after the close, download the official PSE Daily Quotation Report → **Import DQR** in the header → pick the file and the session date → the stored session gains per-ticker foreign flow, the market total, the `pse-dqr-upload` provenance and a `TRADING_HALT_RISK` disclosure for any suspended name; the terminal refreshes. A wrong date answers `404` and an unreadable file `400`, and nothing is written in either case.
5. **Curate the brain:** open **Knowledge base** → review the 6 indexed documents and chunk counts → upload a Markdown/PDF book (scoped to horizons/categories) → re-index → delete an upload (built-ins are protected).
6. **Manual refresh:** **Run pipeline** re-runs the selected trading day and horizon; the previous run for that `(date, horizon)` is replaced so drift history never double-counts a day.
7. **Take a trade:** click **Take** on a row (or in the ticker dialog/research view) → the `Take trade` tab opens pre-filled with that ticket → the log is refused (`400`) unless the shares are whole board lots and the heat/position/cash caps hold.
8. **Work the book:** open **Book** → read marked positions and the desk's exit instruction → raise the stop (break-even tick) or close at a fill; the ledger and the realized equity curve update from the server.
9. **Audit the rules:** open the **Forward scorecard** for graded outcomes and its **Walk-forward replay** tab for the causal equity curve of the same horizon, or **Research** a name the pipeline never ranked.

## Domain semantics the UI depends on

| Concept | Values | Meaning |
| --- | --- | --- |
| Drift status | `NEW`, `HELD`, `UPGRADED`, `DOWNGRADED` | Rank movement vs the previous run for the same horizon |
| Drop category | `INVALIDATED`, `TARGET_HIT`, `DISPLACED` | Why a ticker left the Top 10 |
| Market regime | `BULLISH`, `NEUTRAL`, `BEARISH` | Tape character from the PSEi close vs its 50D/200D averages |
| Horizon | `DAY`, `WEEK`, `MONTH`, `YEAR` | All four active; each has a run, mandate and drift chain |
| Disclosure category | `FORM_17Q`, `FORM_17A`, `FORM_17C`, `FORM_23B`, `DIVIDEND`, `SRO`, `FOO`, `TENDER_OFFER`, `SHARE_BUYBACK`, `TRADING_HALT`, `INDEX_REBALANCE`, `GENERAL` | Deterministic classification of a PSE Edge headline |
| Trap flag | `SRO_DILUTION`, `TENDER_OFFER_PIN`, `TRADING_HALT_RISK` (hard), `FOO_PLACEMENT_OVERHANG`, `EX_DIVIDEND_DROP`, `STATIC_CEILING_CHASE`, `STATIC_FLOOR_BREAKDOWN` | Structural hazards; hard traps disqualify a pick |
| Weinstein stage | `STAGE_1`…`STAGE_4`, `null` | Trend stage from the close vs SMA150; `YEAR` prefers `STAGE_2` |
| Net foreign flow | `decimal?` | Reported only when an official PSE Daily Quotation Report was parsed (live template or operator upload); otherwise `null` → UI shows `—` / `unreported`, with `foreignFlowSource` naming the provenance |
| Cross-asset dials | `decimal?` | US 10Y close (%) + 20D change (bps), Brent (USD, 20D %), EPHE (USD, 20D %); each is independently `null` when its feed is missing or stale |
| Position action | `HOLD`, `STOP_BREACHED`, `TARGET_REACHED`, `MOVE_STOP_TO_BREAKEVEN`, `TRAIL_STOP_EMA20`, `TIME_STOP_DUE` | The blotter's deterministic exit instruction for an open position (barrier verdicts only after a real session) |
| Research source | `live`, `stored` | Whether the deep dive was priced from a fresh single-symbol chart or the latest stored session (with that session's own date) |

## UX copy / tone guidelines (as implemented)

- **Simple copy is written for a first-time investor, in the third person.** The hero says “money is moving into banks”, “ride the wave” or “stay in cash today — nothing cleared its fees”; cards report “the plan cleared the fee-adjusted test”, the entry band, the cut-loss level, the break-even lock and the trail trigger, “chance it reaches the target”, “what would prove this wrong”, and the holding tab reports where a position the reader already owns sits on the ladder; the blotter becomes *My Trades (Paper Tracker)* with the server-derived exit posture instead of “exit signal”, and the settings tab becomes *My Rules*. Failures are phrased as a title plus an action (“the desk could not reach the price feed — check your internet and try again”), while the raw API text stays available under *Technical details*.
- **Explain guardrails in plain language** (Simple: “the desk blocks any order that breaks your cash or risk limits”; Pro: “board-lot rounded to the PSE tick table”, “drift recomputed server-side”).
- **Technical detail is never removed, only folded.** Every drawer prints the raw server fields (verdict codes, R:R, P(win), EV, Quarter-Kelly, indicators, governor multipliers, calibration, filings) so a curious reader can audit the sentence above it.
- **The model writes the plain sentence.** `thesis` and `bear_case` are required in everyday English (≤45 / ≤30 words, a Peso invalidation level, no indicator shorthand), so Simple mode does not have to launder jargon.
- State limitations in the footer: Simple reads “four holding periods · prices already include broker fees · Manila time (UTC+8)”, Pro keeps “four horizons · board-lot rounded plans · Manila time (UTC+8)”, and both end with “Research tooling only, not investment advice. Prices from Yahoo Finance and PSE Edge under a personal, non-commercial posture.” plus a *Review the notice* button that reopens the legal gate
- Drop reasons are full sentences with the actual prices that triggered the classification.
- **The phone is a first-class reading surface, not a degraded one.** The same run is read one-thumbed at 320px: dialogs slide up as sheets with the title pinned, the Pro table hands over to cards, the tab rail scrolls instead of wrapping, and nothing important is dropped (only re-laid-out) below `md`. Copy stays identical across breakpoints — a smaller screen gets less width, never a simplified or hedged version of the advice.
- **Waiting is never blank.** The last payload stays on screen while a revalidation runs, and a failed refresh says what failed in an amber line above the data it could not update instead of replacing it with an error page.

## What "good" looks like

- A day-2 live run shows `NEW`, `HELD`, `UPGRADED`, `DOWNGRADED` plus `INVALIDATED`, `TARGET_HIT` and `DISPLACED` without hand-editing data — per horizon.
- Every displayed price is a valid PSE tick, clears `target > entry > stop`, and the share count is a whole multiple of the board lot for that price.
- The same stored inputs always produce the same candidate list and scores, and the same model response always produces the same list, drift and drops.
- Switching horizon visibly changes the mandate, the stance line and the retrieved playbooks, not just the label.
- Every citation shown in the ticker dialog exists in the retrieved set; no thesis can cite a rule the desk did not supply.
- Unreported upstream figures are visibly unreported rather than silently zeroed.
- A reviewer can explain every exit from the previous list using persisted data alone.

