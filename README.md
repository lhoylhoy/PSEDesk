# PSE Signal Desk

A full-stack research notebook for the Philippine Stock Exchange: a .NET 10 Clean Architecture API that screens the liquid PSE universe, ranks a **screened list for four horizons** (`DAY`, `WEEK`, `MONTH`, `YEAR`) grounded in a **local RAG knowledge base** of PSE rules and playbooks, prices each name with **horizon-matched, fee-adjusted odds**, a **dividend-aware yield tilt** and **server-computed order tickets**, forces a **bear-case pre-mortem**, reconciles **day-over-day drift** and explains what **dropped out** — then **grades itself** against the sessions that follow, **replays its own rules causally** on the real tape and **refits one money-manager model per horizon** from that ledger, promoting a fit to live pricing only when it beats the neutral prior on trades it never saw. The React/TypeScript terminal renders it in a **Simple** reading mode (one compact market call, then one row per signal — tap a row to open its detail sheet) or the dense **Pro** terminal, with a Knowledge Base drawer, a scorecard with a walk-forward replay tab, on-demand research for any PSE ticker and a paper blotter that sizes positions server-side.

> **Research tooling only — not investment advice, and not a licensed market-data service.**
> The desk is built for one person's own research on their own machine. Read
> [`LICENSE`](LICENSE), [`DISCLAIMER.md`](DISCLAIMER.md), [`PRIVACY.md`](PRIVACY.md) and
> [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) before you run it. The licence grants one
> personal, non-commercial copy and nothing else: no hosting for other people, no selling.

## What it does

- **Live-only data.** Every candle comes from a public exchange feed — Yahoo Finance's v8 chart endpoint (`PHP=X` and the `^TNX`/`BZ=F`/`EPHE` dials) or, for every PSE equity and the equal-weight index proxy, **PSE Edge's own daily history** (`common/DisclosureCht.ax`), because Yahoo stopped publishing `{TICKER}.PS` charts (every one now answers `404`) — and every disclosure from PSE Edge: each screened candidate's **own company page**, keyed on the exchange's `cmpy_id` (`companyDisclosures/search.ax?keyword=…`), with the market-wide announcement page as the fallback. There is **no synthetic series, no demo tape and no offline engine**: if the tape or the disclosure page fails the run fails (`503`) instead of inventing prices. The optional enrichments (the official Daily Quotation Report and the three macro series) are best-effort and degrade to `null`, never to a fabricated number. Each download is cached in memory per request URL (a chart for ten minutes, an Edge page for five) so the four horizons of one session, a replay and an ad-hoc research call share one fetch; `POST /api/signals/run-now` clears that cache first, and a failed download is never cached.
- **Grounded, not free-form.** The reasoning model only sees retrieved sections from `backend/knowledge-base/` (mandatory PSE guardrails first, then horizon/sector/trap-matched playbooks) plus a deterministic candidate table. Citations the retrieval did not supply are dropped server-side, so a thesis can only point at rules that were actually loaded.
- **Quant filter first.** Live daily OHLCV per ticker → Wilder RSI-14, ATR-14, EMA-20, SMA-50/150/200, 52-week high, Weinstein stage, 20-day turnover, volume ratio, support/resistance, **relative strength vs the PSEi (63D)**, **ATR as a share of price** and a **volatility-contraction ratio (VCP)**, plus a 0–100 pre-score. Anything below **PHP 5M average 20-day turnover** is excluded, and a name must also clear **PHP 5M on the median** of its last 20 sessions with at least **16 sessions that actually traded**; the best 25 names go to the model. **Universe breadth** (% above 50D/200D, Stage-2 count) is measured across the screen and fed to both the prompt and the probability model. The 15-point foreign-flow pillar is scored only when an **official PSE Daily Quotation Report** was parsed for the session; otherwise it stays `null` (rendered `—` / `unreported`) and the 85-point technical score is rescaled to 0–100 instead of awarding phantom points.
- **Official foreign flow, two ways in.** Net foreign flow comes only from the exchange's Daily Quotation Report (DQR). The provider tries date-addressed templates from `MarketData:PseDailyQuotationUrlTemplates` after the tape is fetched, and an operator can push a downloaded report through `POST /api/signals/quotation-report` (PDF, CSV, TXT, TSV). Both paths store per-ticker flow for the names the report states (an unmatched ticker keeps `null`), add a `TRADING_HALT_RISK` disclosure for suspended rows, and stamp the provenance (`pse-dqr-live` / `pse-dqr-upload`) so the prompt and the regime strip can say where the number came from. A missing or unreadable report never becomes a zero.
- **Cross-asset backdrop.** Three extra Yahoo series ride along with the session: **US 10Y yield (`^TNX`)** with its 20-session change in basis points, **Brent (`BZ=F`)** and the **MSCI Philippines ETF (`EPHE`)** with 20-session percentage changes. They are best-effort — one failing series leaves its tile `—` — and they appear in the regime strip, the persisted snapshot and the prompt's `CROSS-ASSET:` line.
- **Four mandates, one desk.** `HorizonProfile` gives each horizon its own mission, history requirement, RSI band, ATR stop/target multiples, disclosure lookback, hysteresis and playbook shelf. `DAY` additionally clamps plans inside the PSE static band (`+50%/-30%`) and must clear a **net (fee-adjusted) R:R ≥ 2.0**.
- **Disclosure traps.** PSE Edge headlines are classified (`DisclosureCategory`) and turned into `PseTrapFlag`s. Hard traps — `SRO_DILUTION`, `TENDER_OFFER_PIN`, `TRADING_HALT_RISK` — disqualify a pick; the softer ones (`FOO_PLACEMENT_OVERHANG`, `EX_DIVIDEND_DROP`, static-band chases) surface as badges. Attribution is the exchange's own company id, not a headline match: before the trap check, each screened candidate's company page is merged into its rolling disclosure set, so a filing titled just "Rights Offering" still reaches the one company it belongs to.
- **LLM ranking with guardrails.** Ollama Cloud returns strict JSON (`top_10`, `dropped_from_yesterday`, `bear_case`). The pipeline parses defensively (fences, prose, numeric strings), discards tickers outside the liquid candidate set, discards picks without a thesis or carrying a hard trap, retries once on malformed JSON, and **never trusts model drift/ranks or model probabilities** — drift, ranks, odds, sizing and the actionability verdict are all recomputed server-side. No quant top-up: a response with zero valid picks fails the run (`502`).
- **Probabilistic, not narrative.** Every pick is priced server-side: **settled-peso** fee-adjusted reward and risk (0.690% round trip today; 1.190% before the 2025-07-01 CMEPA cut, when the sale tax was 0.60% — and the PHP 20 + 12% VAT broker minimum is charged on every clip, not a flat percentage) → **break-even win rate** → **calibrated win probability** (shrunk historical ATR-barrier hit rate blended with the barrier geometry, then bounded tilts for stage, relative strength, volatility contraction, breadth, regime, trap warnings, **trailing dividend yield** and conviction) → **expected value in R** → **Quarter-Kelly** size. The model never supplies a probability. The horizon shapes the edge: `DAY` charges an ATR-risk premium, while `MONTH`/`YEAR` let a genuine trailing cash yield tilt the estimate.
- **History is replayed honestly.** The backtest and the trainer only see what had printed: no live foreign-flow figure, no disclosure dated after the session or filed after the 15:00 close (an unreported time on a same-day filing still counts), a stop that gapped through fills at the **open**, and every fee charged at the trade's **own date**. Liquidity is judged on the **median** 20-session turnover with at least 16 sessions that actually traded, not an average one block trade can buy. Hyper-parameters are picked on a nested inner split, so the promotion slice never influences the fit that is judged on it.
- **Dividends are part of the odds, not decoration.** Cash dividends are read from the exchange's own per-company dividends table (`POST companyPage/dividends_and_rights_list.ax?DividendsOrRights=Dividends` with the numeric `cmpy_id` — the route settled by live probe on 2026-11-28) and summed over the trailing 365 calendar days per name. Only `COMMON` rows count: the same table prints preferred series beside the common (`JFCPB` at Php10.60 next to `COMMON` at Php2.11) whose rate cell is a coupon sentence, so reading every row would value the common on a preferred's coupon. A cell that is not readable as pesos (`N/A`, a percentage, prose) is dropped rather than guessed, a declared ex-date that has not gone ex yet is excluded, a re-published circular is counted once, and two declarations sharing an ex-date are summed. A name with no cash dividend on record (`SCC`, `PXP`) reports `null` (UI `—`), never a 0% payout. The yield tilts the probability for the longer horizons, feeds the cash-yield sleeve and the dividend-versus-US10Y dial, and is disclosed with its last ex-date in the ticker dialog. Yahoo's chart `events.dividends` block is still preferred when it answers — it no longer does for PSE names, which is what left every trailing yield unreported.
- **A verdict, not a maybe.** The desk labels each name `TRADE`, `SMALL_SIZE` or `WATCH_ONLY`. `WATCH_ONLY` means zero or negative expected value after fees — a real no-bet kept in the list for research context and sized at zero shares. The run also reports a **recommended cash percentage** from the regime, breadth and how many names actually cleared the gate.
- **Order tickets, not guesses.** Each pick carries a server-computed **order ticket** against the stored account equity: whole board lots, the all-in buy outlay including the buy leg, fee-paid peso risk and reward, and the break-even stop tick. The terminal renders those numbers; `lib/riskSizing.ts` only falls back to a local budget conversion for legacy runs with no ticket.
- **Mandatory pre-mortem.** Each pick carries a `bear_case`: the specific tape action that invalidates the thesis, written before the trade. If the model omits it, the server writes a deterministic fallback naming the stop, the overhead resistance and the shrunk probability.
- **Self-grading, and self-replaying.** A **forward-outcome ledger** re-reads the real OHLC sessions after each run date, grades every open pick (`WIN_TARGET` / `LOSS_STOP` / `EXPIRED_WIN` / `EXPIRED_LOSS` / `UNFILLED`, conservative on same-session stop+target ties and only grading a plan the tape actually filled), stores realized R after fees and exposes `GET /api/signals/scorecard`: win rate, expectancy in R, profit factor, **Brier calibration**, per-verdict breakdown and the recent resolved picks. `GET /api/signals/backtest` replays the horizon's **own** entry/stop/time-stop rules causally on the real tape with the live book's money rules — up to five concurrent slots, 6% portfolio heat, two names and 35% of equity per sector, fractional Kelly, a 5%/10% drawdown governor, board-lot slippage on every round trip and a `+1R` break-even → `+1.5R` EMA20 exit ladder — prefix-only with no look-ahead, and returns the equity curve against a **PSEi buy-and-hold benchmark** plus alpha, drawdown, expectancy, Sharpe/Calmar and every simulated round trip.
- **Learns from its own ledger, inside published bounds.** One logistic money-manager model per horizon is refit weekly from the desk's own ATR-barrier history. It learns a **residual over the structural prior** (`eta = logit(prior) + β₀ + w·z`) on a purged, embargoed 70/30 split, and is promoted only when the held-out slice holds ≥25 trades, it beats the neutral prior's **Brier and log-loss**, and its policy expectancy is positive. Anything else is stored and shown as `PRIOR_FALLBACK` and is structurally unable to price (`LearnedEdgeModel.FromActive` returns `null`). A promoted model may tilt the live win probability by at most ±6 points and rescale the Quarter-Kelly stake — a bound the 2026-11-28 refit re-tested (it improved the held-out Brier on all four books but left funded expectancy negative on three) and left in place. A factor the **training slice itself never observed** carries no weight at all: a column seen in fewer than 25 training trades is stored with a neutral mean, unit scale and a zero coefficient, so live odds cannot be priced off a factor the fit never faced — which is exactly what keeps the trailing dividend yield honest today, because the exchange's dividends table only lists about a year of ex-dates and the fit's own slice ends before them. `GET /api/signals/model`, `POST /api/signals/train`, `POST /api/signals/train-all` and `GET /api/signals/desk-memory` publish the books, the refit and the realized record — win rate, expectancy, sectors that paid/bled, recent stop-outs, ranked factor weights — which the prompt prints as `DESK_TRACK_RECORD_AND_LEARNED_EDGE` and `RECENT_STOP_OUTS`. A rejected fit is never hidden.
- **Two ways to read the same payload.** **Simple** (the default) is a **list of trade signals**: one compact call bar (traffic light, one instruction, one sentence — the score, reasons and sector money flows behind *Why this call*), then one row per pick with the company name, the verdict, the buy/target/cut-loss prices and where the price sits against the model's entry band. Clicking a row opens its detail sheet with the whole why: the thesis, the four-step plan (buy band, cut-loss, break-even lock, trail trigger), the server order ticket, the chance it reaches the target, what would prove it wrong and the raw math. **Pro** restores the dense 14-column table, nine regime tiles, rotation strip, governor/slippage tiles and the Knowledge Base action for readers who already work in odds, R:R and Kelly sizing. Both read the same run and the same endpoint; the choice persists per browser, the palette has a persisted **light / dark** switch, and one layout reflows from a 320px phone (bottom sheets, stacked rows, scrolling tab rail) to a 2560px desktop. A first-run legal gate must be accepted before anything renders.
- **Research on demand.** `GET /api/research/{symbol}` prices any PSE ticker, in the catalog or not: live OHLCV, ATR plan snapped to the tick grid, calibrated odds, the server order ticket, trap flags, liquidity-floor verdict, recent disclosures and the pre-mortem. A ticker with no usable history answers `503` instead of a placeholder.
- **A paper blotter the server owns.** `POST /api/portfolio/trades` logs a position against stored capital and the API debits the cash with the buy leg; `GET /api/portfolio` marks every open position to the latest stored session and derives the exit instruction (`STOP_BREACHED`, `TARGET_REACHED`, `MOVE_STOP_TO_BREAKEVEN` at +1R, `TRAIL_STOP_EMA20` at +1.5R, `TIME_STOP_DUE`). Stops can be raised, positions closed at a fill and partly banked (`/trim`, whole board lots on both legs), and a second order on an open ticker is routed into a **legal pyramid add** — only above the existing entry, only at the break-even tick, only at ≥ +1R, at most twice, at 0.50x then 0.25x risk — or refused. The summary publishes the drawdown, peak equity, `riskPosture`, per-sector exposure and the **expectancy / profit factor / payoff ratio** of the closed ledger; the settings (`1%` risk per trade, `SMALL_SIZE` factor, position cap, portfolio heat cap, sector caps, drawdown ladder, volatility target) are validated server-side — a rule breach is a `400`, never a silent resize.
- **Pod-shop risk governance.** Order tickets no longer scale only on the verdict: `OrderTicketCalculator` applies an **edge (Quarter-Kelly) multiplier**, a **volatility-target multiplier** (realised PSEi 20D vol against `TargetAnnualVolPct`, cutting only), a **drawdown multiplier** from the book's own equity high-water mark (`NORMAL` → `DEFENSIVE` between the soft and hard thresholds → `HALTED` past the hard one) and a **live-drift multiplier** that compares the last 30 graded picks with the record the promoted model was accepted on (1.00 / 0.65 / 0.35 / 0.00, released only once both gaps halve). Each governor multiplier is clamped to `[0.50, 1.50]`, so a weak edge is halved, never deleted, and a strong one can never double the base risk; the drift multiplier is applied **after** that clamp so a book that has stopped earning its record can be halted outright without ever re-pricing a published signal. Sector caps (`MaxSectorExposurePct`, `MaxPositionsPerSector`), a **no-averaging-down** rule and the remaining sector room are enforced before any share is funded, and the summary publishes the drawdown, peak equity, `riskPosture` and per-sector exposure gauges.
- **PSE transaction-cost analysis.** Every ticket prices the round trip beyond the 0.690% fee: the **round-trip tick spread** (`2 × tick / price`), a square-root **market-impact** term on ADV participation (capped at 1.5%), the resulting **slippage rate and peso cost**, fee-plus-slippage net risk/reward, the order's **share of 20D turnover** and an **execution guidance** label (`LIQUID_MARKETABLE`, `STAGED_ENTRY`, `LIMIT_ONLY`, `WORK_THE_ORDER`, `NO_TRADE`). Slippage also feeds the edge engine when it is supplied, so a coarse-tick name is priced against what it really costs to cross.
- **Sector rotation and the factor sleeve.** `SectorRotationService` scores each sector 0–100 from breadth, Weinstein Stage 2 share, 63D relative strength and up/down volume, labels it `LEADING` / `IMPROVING` / `WEAKENING` / `LAGGING`, renormalises the weights over whatever a sector actually reports, and publishes the ranking to the prompt, the candidates and the regime strip (persisted as `SectorRotationJson`). The quant filter also computes an accumulation/quality sleeve — **up/down volume ratio 20D**, **Chaikin money flow 20D**, **Kaufman trend efficiency 63D**, **beta 252D vs the PSEi**, **max drawdown 252D**, **tick spread** — plus a barrier hit rate for **each of the four horizons** in one pass, and an **entry-geometry** sleeve: a close **2.5 ATRs above EMA20** costs 6 points on `DAY`/`WEEK`/`MONTH` (a `YEAR` position is not entered for next month's range), while a close inside **−0.5 … 1.0 ATRs of EMA20** and still above a rising `SMA200` earns 5 on `WEEK`/`MONTH`/`YEAR`.
- **Deterministic post-processing.** Every plan is rounded to the official **15-tier** PSE board-lot/tick table with `target > entry > stop`, and sized in **whole board lots** from the desk's real book — the risk budget, free cash, the single-position cap, the sector room and 2% of the name's 20-day turnover — into a server order ticket the terminal renders verbatim. A pick whose **prior stop** or **prior target** was already touched is treated as a closed trade and removed from today's list, so exits are always classified:
  - `INVALIDATED` — close at/below the prior stop
  - `TARGET_HIT` — close at/above the prior target
  - `DISPLACED` — otherwise (or when the model explains it)
- **Dual-model Ollama setup.** A reasoning model ranks the book; an optional cheap model (`OllamaCloud:FastExtractionModel`) runs a compact disclosure/trap pre-pass first, and `OllamaCloud:EmbeddingModel` vectorises knowledge chunks. Both optional passes degrade gracefully — retrieval falls back to BM25 + tag scoring.
- **Daily cadence.** A hosted scheduler recomputes the next 15:35 Asia/Manila weekday run and produces all four horizons; `POST /api/signals/run-now` is available for manual runs per horizon.


## Repository layout

```
backend/
  PseSignalDesk.slnx
  knowledge-base/                  # curated Markdown grounding library (copied to the API output)
    pse_rules.md                   # board lots, static band, fees, disclosure traps, Weinstein stages
    playbooks/                     # day_week_momentum_vcp, day_week_risk_sizing, month_year_quality_value,
                                   # month_year_ph_macro_sectors, probabilistic_expectancy, asymmetric_wealth_compounding
    docs/engineering-references.md # the deterministic invariants the model may never override
  src/
    PseSignalDesk.Domain/          # entities, enums, PSE board-lot/tick/static-band rules + expectancy maths
    PseSignalDesk.Application/     # horizons, quant filter, probabilistic edge, order tickets, portfolio, research,
                                   # backtest, scorecard, training, market direction, wave plans, DTOs
    PseSignalDesk.Infrastructure/   # EF Core SQLite, live Yahoo/PSE Edge provider (charts, dividends, DQR),
                                   # Ollama clients, RAG store, scheduler
    PseSignalDesk.WebApi/           # controllers (signals, research, portfolio, knowledge), compliance + 499
                                   # middleware, DI wiring, appsettings, EF migrations
  tests/PseSignalDesk.Tests/        # 562 xUnit cases (561 run + 1 skipped benchmark): board lot, quant,
                                   # edge, tickets, portfolio, research/backtest, golden snapshots, outcome/training,
                                   # drift policy, market direction/wave, classifier, parser, pipeline, knowledge,
                                   # provider + download cache, prompt, compliance, cancellation middleware
frontend/
  public/          # favicon.svg / favicon.ico (16+32+48) / apple-touch-icon.png — generated, see `npm run icons`;
                   # fonts/ = Outfit (latin subset, OFL 1.1) — vendored, see `npm run fonts`
  scripts/         # generate-icons.mjs: zero-dependency SVG/PNG/ICO writer driven by src/lib/deskMarkGeometry.ts;
                   # fetch-fonts.mjs: re-download the bundled woff2 + its OFL licence from the Google Fonts API
  src/
    api/       # typed fetch client (ApiError + ProblemDetails handling, JSON + FormData bodies)
    components/# TerminalHeader, DeskLogo (the desk mark + its header badge), RegimeStrip, MarketDirectionHero,
               # AdviceCardList (Simple's signal list), SignalTable (+ ProSignalCard),
               # DropPanel, TickerDetailDialog, RiskSizerBar, ScorecardDialog, BacktestPanel, ModelTrainerPanel,
               # ResearchDialog, PortfolioDialog, KnowledgeBaseDialog, QuotationReportImportButton, VerdictBadge,
               # DriftBadge, WelcomeSetupBanner, BeginnerGuideDialog, LegalDisclaimerGate
    components/ui/ # Radix wrappers: Tabs, Dialog (+ phone bottom sheet / sticky header), Tooltip, Select, Button,
                   # InfoTip, TechnicalDisclosure
    lib/       # formatters, horizons, viewMode, theme, legalConsent, plainAdvice, pseNicknames, sessionSelection,
               # riskSizing, queryCache (SWR client cache), preloaders, cn,
               # deskMarkGeometry (the one candlestick drawing the badge and both icons are rendered from)
    lib/__tests__/ # node:test suites (plainAdvice, legalConsent, queryCache, formatters, deskMark,
                   # shortcuts, howToRead, operatorKey); excluded from the browser build
    types/     # API contract types
```

## Run it

### 1. API

```bash
cd backend
dotnet build PseSignalDesk.slnx
dotnet run --project src/PseSignalDesk.WebApi
```

The API listens on `http://localhost:5236` (see `Properties/launchSettings.json`), applies EF migrations at startup, and creates `pse_signals.db` next to the WebApi project.

Optional Ollama Cloud configuration (environment variables override `appsettings.json`):

```bash
export OLLAMA_API_KEY="..."          # required: there is no offline engine
export OLLAMA_BASE_URL="https://ollama.com"
export OLLAMA_MODEL="deepseek-v4.1-flash"        # reasoning model that ranks the book
export OLLAMA_FAST_MODEL="glm-5.3-flash"         # optional cheap disclosure/trap pre-pass (blank = off)
export OLLAMA_EMBEDDING_MODEL="nomic-embed-text"  # optional knowledge embeddings (blank = lexical only)
```

Every model name must exist in your account's catalogue (`GET https://ollama.com/api/tags`, or
`ollama ls` against the cloud). An unknown **fast** model is logged once and the optional pass is
switched off, so the book still ranks; an unknown **reasoning** model fails the run with `502`.

`OllamaCloud:ReasoningEffort` controls the `think` field (blank leaves the provider default,
`off` disables it, `low`/`medium`/`high`/`max` forward the level). `num_predict` caps the reasoning
trace *and* the answer together, so a thinking model can spend the whole completion budget before it
writes a single JSON character. When that happens the engine logs the stop reason
(`done_reason: length`) and retries that call once with thinking disabled, then keeps that model on
the no-thinking setting for the rest of the run — the book still ranks, and the extra call is
recorded in the run's `modelRetryCount`. Set `OllamaCloud:ReasoningEffort=off` (or raise
`OllamaCloud:NumPredict`) to avoid the retry altogether.

Without a key every `run-now` answers `400` with a configuration problem; the desk never fabricates a ranking.

Legal and deployment posture (see `LICENSE`, `DISCLAIMER.md`, `PRIVACY.md`, `THIRD_PARTY_NOTICES.md`):

```bash
export Compliance__DeploymentMode="PersonalLocal"   # default; refuses non-loopback callers with 403
export PSEDESK_OPERATOR_API_KEY="..."               # required for mutating calls in LicensedCommercial
export Compliance__AllowedHosts="desk.example.ph"   # licensed mode only; overrides the loopback allowlist
export Compliance__ExpensiveRequestsPerHour=30      # hourly ceiling on run-now / backtest / train
export Compliance__ResearchRequestsPerHour=120      # hourly ceiling on single-ticker research lookups
export Compliance__KnowledgeRequestsPerHour=10      # hourly ceiling on knowledge uploads / reindex (embeddings)
```

**This is not open source.** `LICENSE` grants one personal, non-commercial copy on a machine you
control and nothing else: no redistribution, no hosting for other people, no selling, no derivative
distribution. It also carries the "as is" warranty disclaimer and the liability cap, so the terms
travel with a copy instead of living only in the README.

`PersonalLocal` is the shipping default: any request whose remote address is not loopback is answered
`403` before it reaches an endpoint (a caller with no remote address, e.g. an in-process test host,
counts as loopback). `LicensedCommercial` only flips that check off — it assumes **you** hold the PSE
market-data/redistribution agreement and an SEC licence for whatever you are offering. In that mode
every mutating verb (`POST`, `PUT`, `PATCH`, `DELETE`) must present the operator key in
`X-PseDesk-Key`; a missing or wrong key is `401`, and a blank key falls closed with `503`. The terminal
sends that header itself once `VITE_OPERATOR_API_KEY` is set at build time (see `frontend/.env.example`),
so the documented mode is reachable from the UI and not only from `curl`. `Compliance:AllowedHosts`
replaces the loopback-only `AllowedHosts` **only** when the licensed mode is declared, so a licensed
deployment can answer to its own host name without a leftover value ever widening the personal desk.
`GET /health` bypasses all of it. The outbound `User-Agent` is `MarketData:UserAgent`, so the desk
identifies itself honestly to Yahoo and PSE Edge instead of impersonating a browser.

Three guards keep the "polite, personal, bounded" claim true rather than aspirational. Only **one**
expensive job runs at a time — a second concurrent `run-now`, `train` or `train-all` is answered
`409` immediately instead of queueing behind minutes of work and re-fetching the same session — and
those four endpoints share an hourly ceiling (`Compliance:ExpensiveRequestsPerHour`, default `30`,
clamped 1–1000) that answers `429` with `Retry-After` and a ProblemDetails body. A single-ticker
research lookup has its own ceiling (`Compliance:ResearchRequestsPerHour`, default `120`, clamped
1–10000) because walking the catalog would otherwise hammer Yahoo Finance and PSE Edge without ever
touching the run budget, and the knowledge-base writes that spend embedding credit share
`Compliance:KnowledgeRequestsPerHour` (default `10`, clamped 1–1000). `lookbackDays` on `backtest`,
`train` and `train-all` is capped at **1,825 calendar days** (five years) and answers `400` outside
`1..1825`, so one request cannot ask for a century of bars. A human driving four horizons never
notices any of it; a loop cannot hammer the exchange or spend unlimited model credit. Stored-session
reads stay uncapped because they are free.

Two more guards close the drive-by paths. Every mutating request must come from the desk's own
browser origin (the `Cors:AllowedOrigins` list, or the API's own host): CORS only hides a response,
so without this check a page on another site could still press the desk's buttons on loopback. And
the operator key is rate-limited per caller — five wrong `X-PseDesk-Key` values in five minutes
answer `429` with `Retry-After` for the rest of the window, so a constant-time comparison is not an
unlimited guessing oracle. Knowledge uploads additionally accept only `.md`, `.markdown`, `.txt` and
`.pdf`; anything else is a `400` instead of being decoded as text.

One rule is not enforceable in code, so `DISCLAIMER.md` §7 states it plainly: the plain-language cards
are written **as instructions for one self-user** ("buy in this band", "cut here", "lock break-even at
+1R"). Do not pass that output to another person as advice and do not run anyone else's money through
it — that is what turns research tooling into unregistered investment advice under the Securities
Regulation Code. The first-run gate repeats it, and bumping `CURRENT_LEGAL_VERSION` re-prompts every
browser.


Live feed tuning lives in the `MarketData` section (`YahooFinanceBaseUrl`, `PseEdgeAnnouncementsUrl`, `PseEdgeCompanyDisclosuresUrl`, `DisclosurePagesToScan` = 2, `DisclosureCompanyFanoutLimit` = 25, `EnablePseEdgeHistory` = true, `PseEdgeChartUrl`, `PseEdgeSymbolSearchUrl`, `PseEdgeCompanyPageUrl`, `PseDailyQuotationUrlTemplates` = `[]`, `Us10YahooSymbol` = `^TNX`, `BrentYahooSymbol` = `BZ=F`, `EpheYahooSymbol` = `EPHE`, `LookbackCalendarDays` = 420, `MaxConcurrency` = 8, `RequestTimeoutSeconds` = 15). `DisclosureCompanyFanoutLimit` is how many screened candidates get their own Edge company page each run; `0` disables the per-company pass and leaves only the market-wide scrape. The typed client runs on one pooled, decompressing `SocketsHttpHandler`, the session's charts, FX series, Edge pages, DQR and macro dials are fetched in a single fan-out once the index has fixed the session date, and repeat requests within ten minutes (charts, Edge history and the company disclosure pages) / five minutes (the market-wide Edge pages) are served from the in-process `MarketDataCache` instead of the network.

The `OllamaCloud:ApiKey` in `appsettings.json` is deliberately blank; put yours in user-secrets instead of the repository:

```bash
cd backend/src/PseSignalDesk.WebApi
dotnet user-secrets set "OllamaCloud:ApiKey" "..."
```

Knowledge-base tuning lives in the `KnowledgeBase` section (`RootPath` = `knowledge-base`, `MaxSnippets` = 6, `MaxChunkCharacters` = 1600, `EnableEmbeddings` = true, `IndexOnStartup` = true). The built-in Markdown library is parsed, hash-compared and indexed into SQLite at startup; set `KnowledgeBase__EnableEmbeddings=false` to index and retrieve without calling the embeddings endpoint.

### 2. Terminal

```bash
cd frontend
npm install
npm run dev     # http://localhost:5173
```

`VITE_API_BASE_URL` overrides the API base (see `.env.example`); CORS is pre-configured for `http://localhost:5173`. `VITE_OPERATOR_API_KEY` adds the `X-PseDesk-Key` header on writes and is only needed against a `LicensedCommercial` API — the personal loopback desk needs no key, and reads never carry one.

The terminal opens behind a first-run **legal gate**, then in **Simple** mode. `Simple | Pro` (`psedesk.viewMode`) and **light / dark** (`psedesk.theme`) persist per browser, dialogs become bottom sheets below `sm`, and the same run reflows from a 320px phone to a 2560px desktop without a second endpoint or component tree. A first-visit *three steps to read this screen* strip sits above the list (dismissed once, remembered in `psedesk.howToReadDismissed`, and restorable from the footer), and every action has a keyboard accelerator — `1`–`4` horizons, `R` run, `K` research, `T` trades, `D` scorecard, `S`/`P` view, `L` theme, `G` guide, `?` the shortcut sheet — documented by `src/lib/shortcuts.ts`, which the help sheet, the guide and each control's `aria-keyshortcuts` all read, so a key can never be bound but undocumented. `npm test` runs the dependency-free `node:test` suites over the React-free `lib/` layer (advice/labels, shortcuts, the "how to read" preference, legal consent, the SWR cache, formatters and the desk mark); `npm run build` is the UI gate.

The tab icon is not a hand-drawn asset: `src/lib/deskMarkGeometry.ts` describes the mark once (three ascending candlesticks on a 32-unit grid), `DeskLogo` renders it in the header in `currentColor`, and `npm run icons` rasterises the same geometry into `public/favicon.svg`, `public/favicon.ico` (16/32/48, PNG-compressed entries) and `public/apple-touch-icon.png` (180px, full-bleed because iOS masks it and paints transparent corners black). Re-run `npm run icons` after touching the geometry — the command needs no dependency, it writes PNGs through `node:zlib` and the ICO container by hand.

The typeface is **Outfit** (variable 100–900), served from `public/fonts/` rather than from a font CDN: the woff2 is bundled, `index.html` preloads it, and `npm run fonts` re-downloads the `latin` subset plus its OFL licence from the Google Fonts API if you ever want to move the font forward. Bundling is what keeps the privacy notice literal — a page load talks to nothing but the API — and the font file ships under the SIL Open Font License with `public/fonts/OFL.txt` beside it. The peso sign, the `▲ ▼` direction glyphs and `→` are not covered by any Google-served Outfit subset (measured against the upstream cmap: 360 glyphs, none of those), so those few characters render from the reader's system font beside Outfit digits; the rest of the UI — all copy, tickers and the digits of every price — is the bundled face.

### 3. First live run

```bash
curl -X POST "http://localhost:5236/api/signals/run-now"
curl -X POST "http://localhost:5236/api/signals/run-now?date=2026-11-24&horizon=DAY"
```

`run-now` without a date means **today in Asia/Manila** (the provider then re-dates the run to the newest real session in the fetched batch, so a weekend or holiday run cannot invent a session); pass `date=YYYY-MM-DD` to re-run a stored day. `horizon` defaults to `WEEK` and accepts `DAY`, `WEEK`, `MONTH`, `YEAR`. The provider resolves the real session date from the newest `PSEI.PS` bar, requires USD/PHP within 7 days of it, needs at least 10 tickers with enough bars on that same session, and then persists candles + metrics + the model's ranked list with server-computed odds, verdicts, a bear case and an open ledger entry. Re-running the next trading day is what produces `NEW`/`HELD`/`UPGRADED`/`DOWNGRADED`, the three drop categories and the first graded outcomes — the drift and the scorecard are real history, not scripted data.

```bash
curl "http://localhost:5236/api/signals/scorecard?horizon=WEEK"
```

The scorecard reads the stored ledger only (no network): win rate, expectancy in R, profit factor, Brier score, calibration buckets, the TRADE / SMALL_SIZE / WATCH_ONLY breakdown and the recent resolved picks. It stays empty until picks actually resolve.

### 4. Curate the knowledge base

```bash
curl http://localhost:5236/api/knowledge                       # list indexed documents
curl -X POST http://localhost:5236/api/knowledge/reindex       # re-scan knowledge-base/ (hash-aware)
curl -F "file=@notes.md" -F "title=My research note" \
     -F "horizons=week,month" -F "categories=risk,technical" \
     -F "confirmRightsAndCloudProcessing=true" \
     http://localhost:5236/api/knowledge/upload                # Markdown, TXT or PDF (<= 20 MB)
curl -X DELETE http://localhost:5236/api/knowledge/7           # 204 for uploads, 409 for built-ins
```

Every upload must set `confirmRightsAndCloudProcessing=true`. Unset, the endpoint answers `400`:
indexing a document and retrieving it into a prompt both copy its text, and the passages are sent to
the configured Ollama Cloud model. Upload only material you have the rights to index — your own
notes, a document you licenced, or public-domain text — and never a book, report or feed you merely
have access to. Built-in documents are immutable from the API — edit the Markdown file and re-index
instead. Uploads are append-only today (a re-upload gets a new slug), so delete the superseded one.

### 5. Import the official Daily Quotation Report (foreign flow)

```bash
# After the close: the exchange's DQR is the only public source of net foreign flow.
curl -F "file=@dqr-20261124.pdf" -F "date=2026-11-24" \
     http://localhost:5236/api/signals/quotation-report
```

The session must already exist (run the pipeline for that day first), otherwise the endpoint answers `404`; an unreadable file, a missing file and anything that is not `.pdf/.csv/.txt/.tsv` answer `400`, and the 25 MB limit is enforced before parsing. On success the response reports the session date, `source: "pse-dqr-upload"`, the market-level net foreign flow, how many stored tickers matched, how many metrics changed and which tickers were suspended. The stored snapshot keeps its macro dials, breadth, FX and rotation — the upload only owns foreign flow (plus a synthetic `TRADING_HALT_RISK` disclosure for suspended rows). The terminal's **Import DQR** button posts the same request and refreshes the dashboard.

To let the provider fetch the report by itself, configure one or more date-addressed URLs (`{yyyy}`,
`{MMddyyyy}` or `{MMddyy}` tokens). The exchange publishes no stable date-addressed URL, so only point
this at an endpoint you are entitled to read — your own copy on a local file server, or a licensed
PSE market-data feed. A third-party mirror of the exchange's report is not permitted here: serving it
would redistribute PSE market data without a redistribution agreement.

```bash
export MarketData__PseDailyQuotationUrlTemplates__0="http://localhost:8080/pse/dqr-{MMddyyyy}.pdf"
```

### 6. Research, replay and the paper blotter

```bash
# On-demand deep dive for any PSE ticker (catalog or not)
curl "http://localhost:5236/api/research/SM?horizon=WEEK"

# Causal walk-forward replay of the horizon's own rules
curl "http://localhost:5236/api/signals/backtest?horizon=WEEK"

# The book: settings, open positions marked to the last stored session, closed ledger
curl http://localhost:5236/api/portfolio
curl -X PUT http://localhost:5236/api/portfolio/settings -H 'Content-Type: application/json' \
     -d '{"maxRiskPerTradePct":1.0,"maxPortfolioHeatPct":6.0}'

# Log a trade the server already sized (shares must be whole board lots on the tick grid)
curl -X POST http://localhost:5236/api/portfolio/trades -H 'Content-Type: application/json' \
     -d '{"ticker":"SM","shares":10,"entryPrice":800,"stopLoss":760,"targetPrice":880,"horizon":"WEEK","edgeVerdict":"TRADE"}'

curl -X PATCH http://localhost:5236/api/portfolio/trades/1/stop -H 'Content-Type: application/json' -d '{"stopLoss":807.5}'
curl -X POST  http://localhost:5236/api/portfolio/trades/1/close -H 'Content-Type: application/json' -d '{"exitPrice":880}'
```

The blotter is paper-only: no broker connection, no order routing. It exists so the desk's sizing rules can be exercised and audited — cash is debited with the buy leg, credited with the net sell proceeds, and every refusal (heat cap, position cap, non-lot shares, off-grid price, stop above the last close) comes back as a `400` `ProblemDetails` naming the rule.

## API

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/signals/latest?horizon=WEEK` | Latest run + regime strip + available dates for a horizon |
| `GET` | `/api/signals/by-date?date=YYYY-MM-DD&horizon=WEEK` | Run/snapshot for a specific trading day |
| `GET` | `/api/signals/dates?horizon=WEEK` | Stored sessions (merged snapshots + runs) |
| `GET` | `/api/signals/ticker/{symbol}/history?horizon=WEEK` | Rank/conviction/level history for one ticker (incl. odds and realized R) |
| `GET` | `/api/signals/scorecard?horizon=WEEK` | Forward-outcome scorecard: win rate, expectancy, profit factor, Brier calibration, verdict breakdown, recent resolved picks |
| `GET` | `/api/signals/backtest?horizon=WEEK` | Causal walk-forward replay of the horizon's own rules (multi-slot equity curve, benchmark/alpha, drawdown, expectancy, trades) |
| `GET` | `/api/signals/model?horizon=WEEK` | Learned money-manager book(s): status, held-out scores, factor weights |
| `POST` | `/api/signals/train?horizon=WEEK` | Refit one horizon's model from the live tape (`503` on an empty feed) |
| `POST` | `/api/signals/train-all` | Refit all four books |
| `GET` | `/api/signals/desk-memory?horizon=WEEK` | Realized record, sector memory, recent stop-outs and the learned factors |
| `GET` | `/api/research/catalog` | Static PSE catalog (ticker, company, sector) used by the terminal's search |
| `GET` | `/api/research/{symbol}?horizon=WEEK&averageCost=…&shares=…` | On-demand deep dive: live plan, odds, order ticket, flags, disclosures, pre-mortem; optional held-position verdict |
| `GET` | `/api/portfolio` | Book summary: settings, mark-to-market positions, heat, closed ledger, exit alerts |
| `PUT` | `/api/portfolio/settings` | Update capital and the risk caps (validated) |
| `GET` | `/api/portfolio/order-ticket?entry=…&stop=…&target=…` | Server-sized ticket for an arbitrary plan |
| `POST` | `/api/portfolio/trades` | Log a position ("Take trade"); `201` with the marked position |
| `PATCH` | `/api/portfolio/trades/{id}/stop` | Raise the live stop (e.g. to the break-even tick) |
| `POST` | `/api/portfolio/trades/{id}/close` | Close at a fill price and settle the ledger |
| `POST` | `/api/portfolio/trades/{id}/trim` | Bank part of a position: whole board lots on both legs, own `CLOSED` slice |
| `POST` | `/api/signals/run-now?date=...&horizon=WEEK` | Fetch live data, run the pipeline, grade open picks, persist the run |
| `POST` | `/api/signals/quotation-report` | Multipart upload of the official PSE Daily Quotation Report (PDF/CSV/TXT/TSV, ≤ 25 MB, optional `date`) to fill in net foreign flow |
| `GET` | `/api/knowledge` | Indexed knowledge documents with horizons/categories/chunk counts |
| `POST` | `/api/knowledge/reindex` | Re-scan `knowledge-base/` (content-hash aware) |
| `POST` | `/api/knowledge/upload` | Multipart upload (Markdown/TXT/PDF, ≤ 20 MB) |
| `DELETE` | `/api/knowledge/{id}` | Remove an upload; `409` for built-in documents |
| `GET` | `/health` | Liveness probe |
| `GET` | `/api/health/feeds` | Per-upstream feed ledger: what each feed answered in the last run (`Ok` / `Degraded` / `Failed` / `Unknown`), the latest stored session, and the configuration that explains an unobserved feed |

Failures use RFC 7807 `ProblemDetails`: `400` missing Ollama key, bad input, empty or oversized upload, unsupported quotation-report type or an unreadable report, **portfolio rule violation** (heat cap, position cap, non-lot shares, off-grid price, stop above the last close), `502` signal generation (model returned no tradeable pick), `503` market data, disclosure feed, research or backtest history unavailable, `404` unknown session/document (including a quotation report for a session that was never stored), `409` protected built-in document, `409` a run or training job already in progress, `429` too many expensive requests (with `Retry-After`). A client-aborted request is rethrown as cancellation and answered `499`, never converted into a fake batch.

## Tests

```bash
cd backend && dotnet test PseSignalDesk.slnx    # 561 passing + 1 skipped (manual replay benchmark)
cd frontend && npm test                       # 87 passing (node:test, no DOM)
cd frontend && npm run build                  # tsc --noEmit + vite build
```

The suite is offline (HTTP is stubbed, never opened) and covers the 15-tier PSE board-lot table and plan-normalisation invariants (incl. static band and net R:R), board-lot share sizing, the expectancy layer (net reward/risk per share, break-even rate, expected value, Quarter-Kelly, risk-budget sizing, order tickets), the pod-shop ticket governor (edge/volatility/drawdown multipliers, sector caps and room, no averaging down, TCA slippage and execution guidance), the sector rotation service (composite ranking, weight renormalisation, acceleration footprint, quadrant bands and lookup), the probabilistic edge service (base-rate shrinkage, barrier-geometry blend, bounded tilts, dividend tilt, `DAY` ATR penalty, per-horizon base rates, sector and accumulation tilts, tick-spread and slippage fees, walk-forward calibration offset, verdict gates, cash posture), the paper blotter (sizing against equity, cash ledger, stop/close bookkeeping, drawdown peak and posture, sector exposure, rule violations, no phantom stop-out before a session prints), the research service (live chart, stored-session fallback, an under-200-session live series never padded, catalog membership, tick-valid plan, clamped DAY band, PSEi-relative factors, the stored sector dial, both 503 shapes) and the causal-backtest service (prefix-only evaluation, no look-ahead, stop wins ties), the forward-outcome ledger (barrier walk, conservative same-bar tie, expiry, realized R, scorecard aggregation), RSI/ATR/EMA/SMA math plus relative strength, volatility contraction and market breadth, the liquidity floor and pre-score bands, disclosure classification and trap flags, JSON-fence/prose parsing plus contract validation, pipeline behaviour (knowledge retrieval, drift recomputation, deterministic drop classification, resolved-position exclusion, hard-trap/unknown-ticker/thesis-less discarding, `DAY` clamping and citation intersection, server-owned odds and bear-case fallback, failure when no valid pick remains, market-data-missing errors), the knowledge layer (front matter, chunking, hash stability, mandatory-guardrail injection, horizon/trap boosts, delete protection, upload scoping, re-index idempotence), the live provider (chart parsing, dividends, tick rounding, stale-session/404/non-2xx/empty-PSE-Edge failures, caller cancellation, macro dials that stay `null` on failure and a live DQR overlay tagged `pse-dqr-live`), the Daily Quotation Report parser (CSV/TSV/whitespace layouts, accounting negatives, suspension blocks, garbage/scanned-PDF degrades to empty) with the upload service against real in-memory SQLite (foreign overlay, macro dials preserved on re-save, pre-score recompute for changed rows, 404/400 paths), and the prompt builder's per-horizon, breadth, cross-asset, probability-input, provenance and unreported-dial handling. Newer suites cover the **learned money manager** (residual fit, purged/embargoed split, Brier/log-loss/expectancy promotion gate, ±6pp tilt, Kelly rebasing, `NeedsBootstrap`, and the observable-coverage floor that keeps a factor the training slice barely reported at a zero weight with a neutral mean and unit scale), the **live drift policy** (standard errors, gap thresholds, 1.00/0.65/0.35/0.00 multipliers, hysteresis) and its **production wiring** (a halted book persists its verdict and sizes zero shares, a prior-priced book stays stable without writing, the window is capped at the last 30 graded picks, and a horizon-less ticket is never throttled), the **point-in-time scoping** and **median-liquidity** gates, the **market call and wave plans** (pillar summing, bearish override, chase ceiling, holding ladder, trim legality), the **compliance guard** (loopback refusal, constant-time operator key, health bypass) and the **rights attestation** on uploads. The **output-equivalence harness and the download cache** layer: `SyntheticMarketFixture` + `GoldenSnapshot` replay a seeded tape through all four horizons and through `EvaluateStock` and compare the result byte-for-byte with checked-in JSON (`PSEDESK_UPDATE_GOLDEN=1` records a new baseline), while `MarketDataCacheTests` pins the shared-task guarantee, the never-served failure, the per-caller abort that cannot cancel a shared download, the TTL refetch, `Clear` and the oldest-entry eviction. The **per-company disclosure fan-out** suite is `PseEdgeCompanyDisclosuresTests`, which pins the `cmpy_id` resolution, the `?keyword=` route (never a bare `search.ax` query), the rolling window, a row the parser cannot date being dropped rather than back-dated, a company with no page counting as a skip rather than a failure, the `0` rollback, the fan-out cap with distinct ids and the ten-minute cache, while `QuantitativeFilterServiceTests`, `SignalPipelineServiceTests` and `PseMarketDataProviderTests` add the pre-trap selection order, a rights offering that only the candidate's own company page names dropping it before the model, and the captured live disclosure row. The newest suite is the **PSE Edge dividends table**: `PseEdgeDividendsTests` pins the `COMMON`-only filter (the preferred `JFCPB` row beside it is ignored), every rate spelling the exchange prints (`Php2.11`, `Php 0.11`, `P0.133/Share`, `Php0.11733697`, a bare `0.63`), `N/A`/percentage/prose rejection, an ex-date that has not gone ex yet, one-circular-one-declaration with same-ex-date summation, `no data.` as a skip rather than a zero yield, `500` as a soft failure, the `EnablePseEdgeDividends` rollback and the six-hour cache; `LearnedEdgeModelTests` pins the 12 → 14 feature append (a stored 12-key fit still prices identically) and `QuantitativeFilterServiceTests` pins the entry-geometry sleeve (chase penalty, YEAR exemption, pullback bonus and the rising-200D requirement). The frontend suite is plain `node:test` over the React-free `lib/` layer — advice and label copy, legal consent and the SWR cache contracts — so there is no component/DOM harness: `tsc --noEmit` + `npm run build` are the UI gate.


## Debugging in VS Code

`.vscode/launch.json` ships three configurations and `.vscode/tasks.json` the matching build tasks:

- **API: PseSignalDesk.WebApi** — builds the solution, then launches the API on `http://localhost:5236` with `ASPNETCORE_ENVIRONMENT=Development`.
- **API: attach to running process** — attach to a `dotnet run` instance.
- **Terminal: Chrome (run `npm run dev` first)** — attaches the browser debugger to the Vite dev server (`webRoot` = `frontend/`).

**"TaskCanceledException … was not handled in user code" while debugging the API is expected.** It comes from `RequestCancellationMiddleware`: when a client abandons a request (React StrictMode's development remount, closing the tab, switching date/horizon mid-load), the token passed to EF Core is cancelled and SQLite reports it as `TaskCanceledException` from `RelationalConnection.OpenInternalAsync`. The middleware answers `499 ClientClosedRequest` and logs at Debug — nothing fails. The terminal avoids the common case by deferring its first fetch by one macrotask, so StrictMode's simulated remount cancels the timer instead of a live request. If a deliberate mid-flight abort still pauses the debugger, set `"justMyCode": false` in that launch configuration (it then stops only on truly unhandled exceptions).

## Notes and limitations

- **All four horizons are live.** `DAY`, `WEEK`, `MONTH` and `YEAR` each get their own run per session, their own mandate (`HorizonProfile`) and their own drift chain. `DAY` plans are additionally clamped inside the PSE static band and must clear a fee-adjusted R:R of at least 2.0.
- **Market data is live and fail-fast.** `PseMarketDataProvider` reads Yahoo Finance charts for FX and the macro dials, and **PSE Edge daily history for every PSE series** (Yahoo no longer publishes `{TICKER}.PS`, so a run that trusted it alone could not build a session; `EnablePseEdgeHistory` = false restores the Yahoo-only contract). Both hosts are unofficial and may change shape or rate-limit; when they do, the run answers `503` rather than falling back to anything invented. Edge's peso value traded becomes share volume through `VALUE / ((HIGH+LOW+CLOSE)/3)`, so `Close × Volume` still equals the official turnover the PHP 5M floor is measured on. Tickers with a 404, too few bars or no bar on the PSE session date are skipped with a warning that names both feeds; fewer than 10 surviving names fails the run. The DQR fetch, the second Edge listing and the three macro series are exempt from that rule by design: they log a warning, leave the affected figure `null` and let the live tape through.
- **The odds are a calibrated prior, not a promise.** History shorter than 60 bars leaves the empirical hit rate at `null`, so the probability is the 0.42 prior blended 50/50 with the barrier geometry and then nudged by bounded factor tilts (clamped to `[0.05, 0.95]`). Only the scorecard's Brier score and realized expectancy can tell you whether it is any good — that is why the ledger exists.
- **Picks stored before the probabilistic migration are inert.** They carry `winProbability = 0`, `edgeVerdict = WATCH_ONLY` and an empty bear case; the UI labels them "re-run the pipeline" instead of showing a fake forecast. The verdict breakdown counts them separately until a fresh run replaces them.
- **Net foreign flow needs the official report.** The exchange does not publish a stable, date-addressed DQR URL, so `MarketData:PseDailyQuotationUrlTemplates` ships empty and the columns stay `null` (UI `—` / `unreported`, quant score normalised over the 85 verified technical points) until an operator uploads the report or configures an endpoint they are entitled to read (a third-party mirror is not permitted — see `THIRD_PARTY_NOTICES.md`). The upload only overlays foreign flow and suspension flags; breadth keeps the pipeline's own measurement. A missing or unreadable report is never stored as a zero.
- **The macro dials are context, not signals.** `^TNX`, `BZ=F` and `EPHE` are best-effort Yahoo series: a stale (older than 7 calendar days), missing or renamed symbol leaves that tile `—`, and nothing in the verdict or the sizing depends on them.
- **Grounding degrades, invariants do not.** If the knowledge base is empty, the embedding model is unavailable or the fast-extraction pass fails, the run continues (logged, lexical or ungrounded prompt) — but tick rounding, the static band, net R:R, board-lot sizing, odds, verdicts, the bear-case fallback, citation filtering, drift and drop classification are always enforced.
- **Knowledge uploads are append-only and gated, not multi-user.** A re-upload creates a new slug, so delete the superseded one. The API refuses non-loopback callers in the default `PersonalLocal` mode, every upload must carry the rights/cloud-processing attestation, and in `LicensedCommercial` mode writes require the operator key — but there are still no user accounts, roles or per-document ownership.
- **The blotter is paper-only.** No broker, no order routing, no live fills: the entry is priced at the planned level, the close uses the price you supply, and everything between is marked to the latest stored session. It is a sizing and discipline harness, not an execution venue, and it shares the same SQLite file as the runs.
- **Research and replay need history.** `GET /api/research/{symbol}` and `GET /api/signals/backtest` answer `503` with the upstream reason when the feed has fewer bars than the horizon (or the 200-day regime) requires; they never substitute a synthetic series or a partially filled curve.
- **SQLite** keeps the MVP self-contained (signal runs, the knowledge base and the blotter share `pse_signals.db`); the repository boundary and EF Core model are ready for a server database, and embeddings are scanned in memory rather than through an ANN index.
- **Ollama Cloud** is a paid API and the only ranking engine — there is no offline substitute.

