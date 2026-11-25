# PSE Signal Desk

A full-stack research notebook for the Philippine Stock Exchange: a .NET 10 Clean Architecture API that screens the liquid PSE universe, ranks a **screened list for four horizons** (`DAY`, `WEEK`, `MONTH`, `YEAR`) grounded in a **local RAG knowledge base** of PSE rules and playbooks, prices each name with **horizon-matched, fee-adjusted odds**, a **dividend-aware yield tilt** and **server-computed order tickets**, forces a **bear-case pre-mortem**, reconciles **day-over-day drift** and explains what **dropped out** — then **grades itself** against the sessions that follow and **replays its own rules causally** on the real tape. The React/TypeScript terminal renders it, with a Knowledge Base drawer, a scorecard with a walk-forward replay tab, on-demand research for any PSE ticker and a paper blotter that sizes positions server-side.

> **Research tooling only — not investment advice, and not a licensed market-data service.**
> The desk is built for one person's own research on their own machine. Read
> [`DISCLAIMER.md`](DISCLAIMER.md), [`PRIVACY.md`](PRIVACY.md) and
> [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) before you run it.

## What it does

- **Live-only data.** Every candle comes from Yahoo Finance's v8 chart endpoint (`PSEI.PS`, `PHP=X`, `{TICKER}.PS`) and every disclosure from the public PSE Edge announcement page. There is **no synthetic series, no demo tape and no offline engine**: if the tape or the disclosure page fails the run fails (`503`) instead of inventing prices. The optional enrichments (the official Daily Quotation Report and the `^TNX`/`BZ=F`/`EPHE` macro series) are best-effort and degrade to `null`, never to a fabricated number.
- **Grounded, not free-form.** The reasoning model only sees retrieved sections from `backend/knowledge-base/` (mandatory PSE guardrails first, then horizon/sector/trap-matched playbooks) plus a deterministic candidate table. Citations the retrieval did not supply are dropped server-side, so a thesis can only point at rules that were actually loaded.
- **Quant filter first.** Live daily OHLCV per ticker → Wilder RSI-14, ATR-14, EMA-20, SMA-50/150/200, 52-week high, Weinstein stage, 20-day turnover, volume ratio, support/resistance, **relative strength vs the PSEi (63D)**, **ATR as a share of price** and a **volatility-contraction ratio (VCP)**, plus a 0–100 pre-score. Anything below **PHP 5M average 20-day turnover** is excluded; the best 25 names go to the model. **Universe breadth** (% above 50D/200D, Stage-2 count) is measured across the screen and fed to both the prompt and the probability model. The 15-point foreign-flow pillar is scored only when an **official PSE Daily Quotation Report** was parsed for the session; otherwise it stays `null` (rendered `—` / `unreported`) and the 85-point technical score is rescaled to 0–100 instead of awarding phantom points.
- **Official foreign flow, two ways in.** Net foreign flow comes only from the exchange's Daily Quotation Report (DQR). The provider tries date-addressed templates from `MarketData:PseDailyQuotationUrlTemplates` after the tape is fetched, and an operator can push a downloaded report through `POST /api/signals/quotation-report` (PDF, CSV, TXT, TSV). Both paths store per-ticker flow for the names the report states (an unmatched ticker keeps `null`), add a `TRADING_HALT_RISK` disclosure for suspended rows, and stamp the provenance (`pse-dqr-live` / `pse-dqr-upload`) so the prompt and the regime strip can say where the number came from. A missing or unreadable report never becomes a zero.
- **Cross-asset backdrop.** Three extra Yahoo series ride along with the session: **US 10Y yield (`^TNX`)** with its 20-session change in basis points, **Brent (`BZ=F`)** and the **MSCI Philippines ETF (`EPHE`)** with 20-session percentage changes. They are best-effort — one failing series leaves its tile `—` — and they appear in the regime strip, the persisted snapshot and the prompt's `CROSS-ASSET:` line.
- **Four mandates, one desk.** `HorizonProfile` gives each horizon its own mission, history requirement, RSI band, ATR stop/target multiples, disclosure lookback, hysteresis and playbook shelf. `DAY` additionally clamps plans inside the PSE static band (`+50%/-30%`) and must clear a **net (fee-adjusted) R:R ≥ 2.0**.
- **Disclosure traps.** PSE Edge headlines are classified (`DisclosureCategory`) and turned into `PseTrapFlag`s. Hard traps — `SRO_DILUTION`, `TENDER_OFFER_PIN`, `TRADING_HALT_RISK` — disqualify a pick; the softer ones (`FOO_PLACEMENT_OVERHANG`, `EX_DIVIDEND_DROP`, static-band chases) surface as badges.
- **LLM ranking with guardrails.** Ollama Cloud returns strict JSON (`top_10`, `dropped_from_yesterday`, `bear_case`). The pipeline parses defensively (fences, prose, numeric strings), discards tickers outside the liquid candidate set, discards picks without a thesis or carrying a hard trap, retries once on malformed JSON, and **never trusts model drift/ranks or model probabilities** — drift, ranks, odds, sizing and the actionability verdict are all recomputed server-side. No quant top-up: a response with zero valid picks fails the run (`502`).
- **Probabilistic, not narrative.** Every pick is priced server-side: fee-adjusted reward and risk per share (0.690% round trip) → **break-even win rate** → **calibrated win probability** (shrunk historical ATR-barrier hit rate blended with the barrier geometry, then bounded tilts for stage, relative strength, volatility contraction, breadth, regime, trap warnings, **trailing dividend yield** and conviction) → **expected value in R** → **Quarter-Kelly** size. The model never supplies a probability. The horizon shapes the edge: `DAY` charges an ATR-risk premium, while `MONTH`/`YEAR` let a genuine trailing cash yield tilt the estimate.
- **Dividends are part of the odds, not decoration.** Cash dividends parsed from the Yahoo chart `events.dividends` block feed a trailing-365-day yield per name (future ex-dates dropped, duplicates by ex-date collapsed to the largest). The yield tilts the probability for the longer horizons and is disclosed with its last ex-date in the ticker dialog.
- **A verdict, not a maybe.** The desk labels each name `TRADE`, `SMALL_SIZE` or `WATCH_ONLY`. `WATCH_ONLY` means zero or negative expected value after fees — a real no-bet kept in the list for research context and sized at zero shares. The run also reports a **recommended cash percentage** from the regime, breadth and how many names actually cleared the gate.
- **Order tickets, not guesses.** Each pick carries a server-computed **order ticket** against the stored account equity: whole board lots, the all-in buy outlay including the buy leg, fee-paid peso risk and reward, and the break-even stop tick. The terminal renders those numbers; `lib/riskSizing.ts` only falls back to a local budget conversion for legacy runs with no ticket.
- **Mandatory pre-mortem.** Each pick carries a `bear_case`: the specific tape action that invalidates the thesis, written before the trade. If the model omits it, the server writes a deterministic fallback naming the stop, the overhead resistance and the shrunk probability.
- **Self-grading, and self-replaying.** A **forward-outcome ledger** re-reads the real OHLC sessions after each run date, grades every open pick (`WIN_TARGET` / `LOSS_STOP` / `EXPIRED_WIN` / `EXPIRED_LOSS`, conservative on same-session stop+target ties), stores realized R after fees and exposes `GET /api/signals/scorecard`: win rate, expectancy in R, profit factor, **Brier calibration**, per-verdict breakdown and the recent resolved picks. `GET /api/signals/backtest` replays the horizon's **own** entry/stop/time-stop rules causally on the real tape — one position at a time, 1% risk, board lots and the full round-trip fee, prefix-only evaluation with no look-ahead — and returns the equity curve, drawdown, expectancy and every simulated round trip.
- **Research on demand.** `GET /api/research/{symbol}` prices any PSE ticker, in the catalog or not: live OHLCV, ATR plan snapped to the tick grid, calibrated odds, the server order ticket, trap flags, liquidity-floor verdict, recent disclosures and the pre-mortem. A ticker with no usable history answers `503` instead of a placeholder.
- **A paper blotter the server owns.** `POST /api/portfolio/trades` logs a position against stored capital and the API debits the cash with the buy leg; `GET /api/portfolio` marks every open position to the latest stored session and derives the exit instruction (`STOP_BREACHED`, `TARGET_REACHED`, `MOVE_STOP_TO_BREAKEVEN` at +1R, `TRAIL_STOP_EMA20` at +1.5R, `TIME_STOP_DUE`). Stops can be raised, positions closed at a fill, and the settings (`1%` risk per trade, `SMALL_SIZE` factor, position cap, portfolio heat cap) are validated server-side — a rule breach is a `400`, never a silent resize.
- **Pod-shop risk governance.** Order tickets no longer scale only on the verdict: `OrderTicketCalculator` applies an **edge (Quarter-Kelly) multiplier**, a **volatility-target multiplier** (realised PSEi 20D vol against `TargetAnnualVolPct`, cutting only), and a **drawdown multiplier** from the book's own equity high-water mark (`NORMAL` → `DEFENSIVE` between the soft and hard thresholds → `HALTED` past the hard one). Each multiplier is clamped to `[0.50, 1.50]`, so a weak edge is halved, never deleted, and a strong one can never double the base risk. Sector caps (`MaxSectorExposurePct`, `MaxPositionsPerSector`), a **no-averaging-down** rule and the remaining sector room are enforced before any share is funded, and the summary publishes the drawdown, peak equity, `riskPosture` and per-sector exposure gauges.
- **PSE transaction-cost analysis.** Every ticket prices the round trip beyond the 0.690% fee: the **round-trip tick spread** (`2 × tick / price`), a square-root **market-impact** term on ADV participation (capped at 1.5%), the resulting **slippage rate and peso cost**, fee-plus-slippage net risk/reward, the order's **share of 20D turnover** and an **execution guidance** label (`LIQUID_MARKETABLE`, `STAGED_ENTRY`, `LIMIT_ONLY`, `WORK_THE_ORDER`, `NO_TRADE`). Slippage also feeds the edge engine when it is supplied, so a coarse-tick name is priced against what it really costs to cross.
- **Sector rotation and the factor sleeve.** `SectorRotationService` scores each sector 0–100 from breadth, Weinstein Stage 2 share, 63D relative strength and up/down volume, labels it `LEADING` / `IMPROVING` / `WEAKENING` / `LAGGING`, renormalises the weights over whatever a sector actually reports, and publishes the ranking to the prompt, the candidates and the regime strip (persisted as `SectorRotationJson`). The quant filter also computes an accumulation/quality sleeve — **up/down volume ratio 20D**, **Chaikin money flow 20D**, **Kaufman trend efficiency 63D**, **beta 252D vs the PSEi**, **max drawdown 252D**, **tick spread** — plus a barrier hit rate for **each of the four horizons** in one pass.
- **Deterministic post-processing.** Every plan is rounded to the official **15-tier** PSE board-lot/tick table with `target > entry > stop`, and sized in **whole board lots** against a `PHP 100,000` position budget. A pick whose **prior stop** or **prior target** was already touched is treated as a closed trade and removed from today's list, so exits are always classified:
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
    playbooks/                     # day_week_momentum_vcp, day_week_risk_sizing, month_position, year_compounding, probabilistic_expectancy
    docs/engineering-references.md
  src/
    PseSignalDesk.Domain/          # entities, enums, PSE board-lot/tick/static-band rules + expectancy maths
    PseSignalDesk.Application/     # horizons, quant filter, probabilistic edge, order tickets, portfolio, research, backtest, scorecard, parser, pipeline, DTOs
    PseSignalDesk.Infrastructure/   # EF Core SQLite, live Yahoo/PSE Edge provider (charts + dividends), Ollama clients, RAG store, scheduler
    PseSignalDesk.WebApi/           # controllers (signals, research, portfolio, knowledge), DI wiring, appsettings, EF migrations
  tests/PseSignalDesk.Tests/        # board lot, quant, edge, tickets, portfolio, research/backtest, outcome, classifier, parser, pipeline, knowledge, provider, prompt tests
frontend/
  src/
    api/       # typed fetch client (ApiError + ProblemDetails handling, JSON + FormData bodies)
    components/# TerminalHeader, RegimeStrip, SignalTable, DropPanel, TickerDetailDialog, RiskSizerBar, ScorecardDialog, BacktestPanel, ResearchDialog, PortfolioDialog, KnowledgeBaseDialog, QuotationReportImportButton, VerdictBadge
    components/ui/ # Radix wrappers: Tabs, Dialog, Tooltip, Select, Button
    lib/       # formatters, horizon metadata, risk sizing (Kelly → pesos), class joiner
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
export OLLAMA_FAST_MODEL="qwen3.5:4b"             # optional cheap disclosure/trap pre-pass (blank = off)
export OLLAMA_EMBEDDING_MODEL="nomic-embed-text"  # optional knowledge embeddings (blank = lexical only)
```

Without a key every `run-now` answers `400` with a configuration problem; the desk never fabricates a ranking.

Legal and deployment posture (see `DISCLAIMER.md`, `PRIVACY.md`, `THIRD_PARTY_NOTICES.md`):

```bash
export Compliance__DeploymentMode="PersonalLocal"   # default; refuses non-loopback callers with 403
export PSEDESK_OPERATOR_API_KEY="..."                # required for mutating calls in LicensedCommercial
```

`PersonalLocal` is the shipping default: any request whose remote address is not loopback is answered
`403` before it reaches an endpoint (a caller with no remote address, e.g. an in-process test host,
counts as loopback). `LicensedCommercial` only flips that check off — it assumes **you** hold the PSE
market-data/redistribution agreement and an SEC licence for whatever you are offering. In that mode
every mutating verb (`POST`, `PUT`, `PATCH`, `DELETE`) must present the operator key in
`X-PseDesk-Key`; a missing or wrong key is `401`, and a blank key falls closed with `503`. `GET /health`
bypasses both checks. The outbound `User-Agent` is `MarketData:UserAgent`, so the desk identifies itself
honestly to Yahoo and PSE Edge instead of impersonating a browser.

Live feed tuning lives in the `MarketData` section (`YahooFinanceBaseUrl`, `PseEdgeAnnouncementsUrl`, `PseEdgeCompanyDisclosuresUrl`, `DisclosurePagesToScan` = 2, `PseDailyQuotationUrlTemplates` = `[]`, `Us10YahooSymbol` = `^TNX`, `BrentYahooSymbol` = `BZ=F`, `EpheYahooSymbol` = `EPHE`, `LookbackCalendarDays` = 420, `MaxConcurrency` = 8, `RequestTimeoutSeconds` = 15).

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

`VITE_API_BASE_URL` overrides the API base (see `.env.example`); CORS is pre-configured for `http://localhost:5173`.

### 3. First live run

```bash
curl -X POST "http://localhost:5236/api/signals/run-now"
curl -X POST "http://localhost:5236/api/signals/run-now?date=2026-11-24&horizon=DAY"
```

`run-now` without a date uses the latest stored session, otherwise today in Asia/Manila; `horizon` defaults to `WEEK` and accepts `DAY`, `WEEK`, `MONTH`, `YEAR`. The provider resolves the real session date from the newest `PSEI.PS` bar, requires USD/PHP within 7 days of it, needs at least 10 tickers with enough bars on that same session, and then persists candles + metrics + the model's ranked list with server-computed odds, verdicts, a bear case and an open ledger entry. Re-running the next trading day is what produces `NEW`/`HELD`/`UPGRADED`/`DOWNGRADED`, the three drop categories and the first graded outcomes — the drift and the scorecard are real history, not scripted data.

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
| `GET` | `/api/signals/backtest?horizon=WEEK` | Causal walk-forward replay of the horizon's own rules (equity curve, drawdown, expectancy, trades) |
| `GET` | `/api/research/{symbol}?horizon=WEEK` | On-demand deep dive: live plan, odds, order ticket, flags, disclosures, pre-mortem |
| `GET` | `/api/portfolio` | Book summary: settings, mark-to-market positions, heat, closed ledger, exit alerts |
| `PUT` | `/api/portfolio/settings` | Update capital and the risk caps (validated) |
| `GET` | `/api/portfolio/order-ticket?entry=…&stop=…&target=…` | Server-sized ticket for an arbitrary plan |
| `POST` | `/api/portfolio/trades` | Log a position ("Take trade"); `201` with the marked position |
| `PATCH` | `/api/portfolio/trades/{id}/stop` | Raise the live stop (e.g. to the break-even tick) |
| `POST` | `/api/portfolio/trades/{id}/close` | Close at a fill price and settle the ledger |
| `POST` | `/api/signals/run-now?date=...&horizon=WEEK` | Fetch live data, run the pipeline, grade open picks, persist the run |
| `POST` | `/api/signals/quotation-report` | Multipart upload of the official PSE Daily Quotation Report (PDF/CSV/TXT/TSV, ≤ 25 MB, optional `date`) to fill in net foreign flow |
| `GET` | `/api/knowledge` | Indexed knowledge documents with horizons/categories/chunk counts |
| `POST` | `/api/knowledge/reindex` | Re-scan `knowledge-base/` (content-hash aware) |
| `POST` | `/api/knowledge/upload` | Multipart upload (Markdown/TXT/PDF, ≤ 20 MB) |
| `DELETE` | `/api/knowledge/{id}` | Remove an upload; `409` for built-in documents |
| `GET` | `/health` | Liveness probe |

Failures use RFC 7807 `ProblemDetails`: `400` missing Ollama key, bad input, empty or oversized upload, unsupported quotation-report type or an unreadable report, **portfolio rule violation** (heat cap, position cap, non-lot shares, off-grid price, stop above the last close), `502` signal generation (model returned no tradeable pick), `503` market data, disclosure feed, research or backtest history unavailable, `404` unknown session/document (including a quotation report for a session that was never stored), `409` protected built-in document. A client-aborted request is rethrown as cancellation and answered `499`, never converted into a fake batch.

## Tests

```bash
cd backend && dotnet test PseSignalDesk.slnx    # 282 passing
cd frontend && npm run build                   # tsc --noEmit + vite build
```

The suite is offline (HTTP is stubbed, never opened) and covers the 15-tier PSE board-lot table and plan-normalisation invariants (incl. static band and net R:R), board-lot share sizing, the expectancy layer (net reward/risk per share, break-even rate, expected value, Quarter-Kelly, risk-budget sizing, order tickets), the pod-shop ticket governor (edge/volatility/drawdown multipliers, sector caps and room, no averaging down, TCA slippage and execution guidance), the sector rotation service (composite ranking, weight renormalisation, acceleration footprint, quadrant bands and lookup), the probabilistic edge service (base-rate shrinkage, barrier-geometry blend, bounded tilts, dividend tilt, `DAY` ATR penalty, per-horizon base rates, sector and accumulation tilts, tick-spread and slippage fees, walk-forward calibration offset, verdict gates, cash posture), the paper blotter (sizing against equity, cash ledger, stop/close bookkeeping, drawdown peak and posture, sector exposure, rule violations, no phantom stop-out before a session prints), the research service (live chart, stored-session fallback, an under-200-session live series never padded, catalog membership, tick-valid plan, clamped DAY band, PSEi-relative factors, the stored sector dial, both 503 shapes) and the causal-backtest service (prefix-only evaluation, no look-ahead, stop wins ties), the forward-outcome ledger (barrier walk, conservative same-bar tie, expiry, realized R, scorecard aggregation), RSI/ATR/EMA/SMA math plus relative strength, volatility contraction and market breadth, the liquidity floor and pre-score bands, trailing dividend parsing (future ex-dates dropped, duplicates collapsed), disclosure classification and trap flags, JSON-fence/prose parsing plus contract validation, pipeline behaviour (knowledge retrieval, drift recomputation, deterministic drop classification, resolved-position exclusion, hard-trap/unknown-ticker/thesis-less discarding, `DAY` clamping and citation intersection, server-owned odds and bear-case fallback, failure when no valid pick remains, market-data-missing errors), the knowledge layer (front matter, chunking, hash stability, mandatory-guardrail injection, horizon/trap boosts, delete protection, upload scoping, re-index idempotence), the live provider (chart parsing, dividends, tick rounding, stale-session/404/non-2xx/empty-PSE-Edge failures, caller cancellation, macro dials that stay `null` on failure and a live DQR overlay tagged `pse-dqr-live`), the Daily Quotation Report parser (CSV/TSV/whitespace layouts, accounting negatives, suspension blocks, garbage/scanned-PDF degrades to empty) with the upload service against real in-memory SQLite (foreign overlay, macro dials preserved on re-save, pre-score recompute for changed rows, 404/400 paths), and the prompt builder's per-horizon, breadth, cross-asset, probability-input, provenance and unreported-dial handling.


## Debugging in VS Code

`.vscode/launch.json` ships three configurations and `.vscode/tasks.json` the matching build tasks:

- **API: PseSignalDesk.WebApi** — builds the solution, then launches the API on `http://localhost:5236` with `ASPNETCORE_ENVIRONMENT=Development`.
- **API: attach to running process** — attach to a `dotnet run` instance.
- **Terminal: Chrome (run `npm run dev` first)** — attaches the browser debugger to the Vite dev server (`webRoot` = `frontend/`).

**"TaskCanceledException … was not handled in user code" while debugging the API is expected.** It comes from `RequestCancellationMiddleware`: when a client abandons a request (React StrictMode's development remount, closing the tab, switching date/horizon mid-load), the token passed to EF Core is cancelled and SQLite reports it as `TaskCanceledException` from `RelationalConnection.OpenInternalAsync`. The middleware answers `499 ClientClosedRequest` and logs at Debug — nothing fails. The terminal avoids the common case by deferring its first fetch by one macrotask, so StrictMode's simulated remount cancels the timer instead of a live request. If a deliberate mid-flight abort still pauses the debugger, set `"justMyCode": false` in that launch configuration (it then stops only on truly unhandled exceptions).

## Notes and limitations

- **All four horizons are live.** `DAY`, `WEEK`, `MONTH` and `YEAR` each get their own run per session, their own mandate (`HorizonProfile`) and their own drift chain. `DAY` plans are additionally clamped inside the PSE static band and must clear a fee-adjusted R:R of at least 2.0.
- **Market data is live and fail-fast.** `PseMarketDataProvider` reads Yahoo Finance charts and scrapes PSE Edge. Both are unofficial and may change shape or rate-limit; when they do, the run answers `503` rather than falling back. Tickers with a 404, too few bars or no bar on the PSE session date are skipped with a warning; fewer than 10 surviving names fails the run. The DQR fetch, the second Edge listing and the three macro series are exempt from that rule by design: they log a warning, leave the affected figure `null` and let the live tape through.
- **The odds are a calibrated prior, not a promise.** History shorter than 60 bars leaves the empirical hit rate at `null`, so the probability is the 0.42 prior blended 50/50 with the barrier geometry and then nudged by bounded factor tilts (clamped to `[0.05, 0.95]`). Only the scorecard's Brier score and realized expectancy can tell you whether it is any good — that is why the ledger exists.
- **Picks stored before the probabilistic migration are inert.** They carry `winProbability = 0`, `edgeVerdict = WATCH_ONLY` and an empty bear case; the UI labels them "re-run the pipeline" instead of showing a fake forecast. The verdict breakdown counts them separately until a fresh run replaces them.
- **Net foreign flow needs the official report.** The exchange does not publish a stable, date-addressed DQR URL, so `MarketData:PseDailyQuotationUrlTemplates` ships empty and the columns stay `null` (UI `—` / `unreported`, quant score normalised over the 85 verified technical points) until an operator uploads the report or configures an endpoint they are entitled to read (a third-party mirror is not permitted — see `THIRD_PARTY_NOTICES.md`). The upload only overlays foreign flow and suspension flags; breadth keeps the pipeline's own measurement. A missing or unreadable report is never stored as a zero.
- **The macro dials are context, not signals.** `^TNX`, `BZ=F` and `EPHE` are best-effort Yahoo series: a stale (older than 7 calendar days), missing or renamed symbol leaves that tile `—`, and nothing in the verdict or the sizing depends on them.
- **Grounding degrades, invariants do not.** If the knowledge base is empty, the embedding model is unavailable or the fast-extraction pass fails, the run continues (logged, lexical or ungrounded prompt) — but tick rounding, the static band, net R:R, board-lot sizing, odds, verdicts, the bear-case fallback, citation filtering, drift and drop classification are always enforced.
- **Knowledge uploads are append-only and unprotected.** A re-upload creates a new slug; delete the old one. There is no auth on `/api/knowledge`, so anyone who can reach the API can upload or delete non-built-in documents.
- **The blotter is paper-only.** No broker, no order routing, no live fills: the entry is priced at the planned level, the close uses the price you supply, and everything between is marked to the latest stored session. It is a sizing and discipline harness, not an execution venue, and it shares the same SQLite file as the runs.
- **Research and replay need history.** `GET /api/research/{symbol}` and `GET /api/signals/backtest` answer `503` with the upstream reason when the feed has fewer bars than the horizon (or the 200-day regime) requires; they never substitute a synthetic series or a partially filled curve.
- **SQLite** keeps the MVP self-contained (signal runs, the knowledge base and the blotter share `pse_signals.db`); the repository boundary and EF Core model are ready for a server database, and embeddings are scanned in memory rather than through an ANN index.
- **Ollama Cloud** is a paid API and the only ranking engine — there is no offline substitute.

