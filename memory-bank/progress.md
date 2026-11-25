# Progress — PSE Signal Desk

**Status snapshot:** 2026-11-26 — a four-horizon probabilistic PSE research desk: live-only ingestion, deterministic screening and tick-grid plans, server-owned odds / EV / Quarter-Kelly, one self-trained money-manager book per horizon (promoted only through a held-out gate), server-sized order tickets, a paper blotter with legal pyramiding and partial trims, a forward-outcome ledger, a benchmarked causal replay, on-demand research for any ticker, an official-DQR foreign pillar, cross-asset dials, a SQLite RAG library (8 documents / 46 sections), and a Simple/Pro terminal that reflows from 320px to 2560px.

**Latest verification:** **390 backend / 49 frontend tests green**, `tsc --noEmit` + `vite build` green (2026-11-26). Everything feed-dependent is unverified live in this environment (Yahoo/PSE answered 404/429 here); the open security remediation is listed under *Remaining*.

## Latest verification — documentation pass (2026-11-26)

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Failed: 0, Passed: 390, Skipped: 0, Total: 390` (~1 s) |
| `cd frontend && npm test` | `tests 49 · pass 49 · fail 0` |
| `cd frontend && npm run build` | `tsc --noEmit && vite build` exit 0, `✓ 129 modules transformed`, `✓ built in <350ms` |
| `sqlite3 …/pse_signals.db "select slug, chunkcount …"` | 8 built-in documents / 46 chunks |
| `git ls-files \| wc -l` | 230 tracked, 5531 ignored, clean tree before the doc edits |

No source, config, knowledge-base, dependency or migration changed in this pass — only `README.md` and `memory-bank/*.md`.

## What works

### Platform and invariants
- [x] Clean Architecture solution (`PseSignalDesk.slnx`) on .NET 10: Domain → Application → Infrastructure → WebApi, plus one xUnit test project with no network access.
- [x] Domain model (`DailyMarketSnapshot`, `StockDailyMetric`, `SignalRun`, `SignalItem`, `SignalDrop`, `KnowledgeDocument`, `KnowledgeChunk`, `PortfolioAccount`, `PortfolioTrade`, `TrainedHorizonModel`) and enums serialized as strings.
- [x] `PseBoardLotService`: the official 15-tier PSE board-lot/tick table, bracket-crossing re-rounding, the static `+50%/-30%` band, `target > entry > stop` repair, per-leg fees (`BuyLegFeeRate` 0.295%, `SellLegFeeRate` 0.395%, round trip 0.690%), the expectancy layer (net risk/reward, break-even rate, EV, Quarter-Kelly, risk-budget sizing), tick spread, ADV participation and estimated slippage.
- [x] `HorizonProfile` holds every per-horizon parameter (mission, `MinHistoryBars`, RSI band, ATR multiples, disclosure lookback, hysteresis, net-R:R requirement, EV minimum, preferred knowledge categories). All four horizons require net R:R ≥ 2.0; EV minima are 0.20 / 0.15 / 0.20 / 0.25.
- [x] EF Core + SQLite with nine migrations (`InitialCreate` → `AddPyramidCountToPortfolioTrades`), unique indexes, cascade deletes, replace-on-rerun semantics and no-tracking reads.

### Signal pipeline and pricing
- [x] Live market data: Yahoo v8 charts with a mirror-host retry, index resolution plus an equal-weight heavyweight proxy, USD/PHP staleness guard, 10-ticker minimum, tick-rounded candles, zero-volume echo removal, row-aware PSE Edge disclosure attribution, best-effort macro dials and an optional official DQR.
- [x] Quantitative filter: Wilder RSI-14, ATR-14, EMA-20, SMA-50/150/200, 52-week high, Weinstein stage, RS63d, ATR%, VCP ratio, up/down volume, Chaikin money flow, Kaufman efficiency, beta, max drawdown, realized volatility, 20-day turnover/volume, support/resistance, weighted pre-score (85-point technical total rescaled when foreign flow is unreported), PHP 5M floor and top-25 cap.
- [x] `PseDisclosureClassifier`: headline → `DisclosureCategory`, trap flags (hard `SRO_DILUTION`, `TENDER_OFFER_PIN`, `TRADING_HALT_RISK`; soft overhang, ex-dividend, static-band chases) and UI copy.
- [x] Defensive `SignalJsonParser`: outermost balanced JSON, fences/prose, braces in strings, numeric strings, contract validation and human-readable retry errors.
- [x] Pipeline: market-data reuse/fetch, horizon-aware candidate selection, knowledge retrieval, previous-run context, one Ollama call with a corrective retry, unknown-ticker/thesis-less/hard-trap discard, resolved-position exclusion, the `DAY` static-band clamp and net-R:R gate, board-lot sizing, citation verification, server-side drift and drop classification, atomic persist, and `502` when nothing tradeable remains.
- [x] `ProbabilisticEdgeService`: shrunk empirical base rate (prior 0.42, 12 pseudo-observations, null under 60 bars) blended with barrier geometry, bounded tilts (stage, RS, VCP, breadth, regime, traps, dividend yield, conviction), clamp `[0.05, 0.95]`, verdict gates (`TRADE` / `SMALL_SIZE` / `WATCH_ONLY`), run-level cash posture and per-horizon hit rates in one pass.
- [x] Mandatory pre-mortem: the model's `bear_case` is kept when usable, otherwise a deterministic stop/resistance/probability fallback is written.
- [x] `MarketDirectionService` (five weighted pillars onto a 50 base, clamped 10–90; bearish regime override; unreported dials omitted) and `WaveTradePlanCalculator` (ideal buy / cut-loss / fee-paid break-even / target, chase ceiling, entry status, holding ladder, legal trim size).
- [x] `OrderTicketCalculator`: one ticket per pick from `BookSizingSnapshot` (risk budget, cash, heat room, position cap, 2% ADV, sector room, governor multipliers, slippage, execution guidance), zero shares for `WATCH_ONLY`, and the **seven legal-pyramid rules** (above entry, break-even stop, ≥ +1R, under two adds, below target, 0.50x/0.25x risk, sector-name exemption).

### Learning loop
- [x] `LogisticModelTrainer`: L2 IRLS with a Gaussian-elimination solver over a per-trial **offset** (the structural prior), so the weights learn only the residual.
- [x] `MoneyManagerTrainingService`: trials replay the desk's own execution (tick-grid plan, effective fee, +1R break-even ratchet, +1.5R causal EMA20 trail, causal index regime) on liquid names only, with a chronological 70/30 split that is purged and embargoed, train-only standardisation, sample weights `1/√(signals that date) × clamp(0.5 + 0.5·|R|, 0.5, 2)`, a λ scan by held-out Brier (log-loss tie-break), a Kelly grid `[0.15, 0.35]`, and a promotion gate of ≥25 held-out trades, Brier **and** log-loss better than the prior, plus `policyExpectancyR > 0`.
- [x] `LearnedEdgeModel.FromActive` refuses any inactive row, so a `PRIOR_FALLBACK` can never price; a promoted fit adds at most ±6pp and rescales the Kelly stake.
- [x] Live calibration offset (≥20 graded picks, 20 pseudo-samples, ±10pp) and desk memory (realized record, sectors that paid/bled, recent stop-outs, ranked factors) computed from the stored ledger only.
- [x] Prompt feedback: `DESK_TRACK_RECORD_AND_LEARNED_EDGE` + `RECENT_STOP_OUTS`, with a rule that the record never overrides the server's levels, sizes, odds or verdicts.
- [x] Weekly refit in its own DI scope plus a background cold-boot refit; `NeedsBootstrap` is true only while a horizon is `UNTRAINED`, and a failed fetch only logs.
- [x] `GET /api/signals/model`, `POST /api/signals/train`, `POST /api/signals/train-all`, `GET /api/signals/desk-memory`.

### Paper book (blotter)
- [x] `PortfolioService` owns every cash transition: open (board-lot, tick, free-cash, position-cap and heat checks), raise stop, close at a fill, trim and pyramid merge. Each refusal is a `PortfolioRuleViolationException` → `400` naming the rule.
- [x] Mark-to-market from the last stored sessions with an honest `HasSession: false` fallback; barrier verdicts only after an observed session.
- [x] Exit ladder `STOP_BREACHED` → `TARGET_REACHED` → `MOVE_STOP_TO_BREAKEVEN` (+1R) → `TRAIL_STOP_EMA20` (+1.5R) → `TIME_STOP_DUE` → `HOLD`.
- [x] Legal pyramiding: one merged row, fee-paid average entry, summed cost, recomputed risk, `PyramidCount`, 0.50x/0.25x adds and five refusals.
- [x] Partial trims: whole board lots on both legs, proportional cost/risk into their own `CLOSED` slice, net cash credit, break-even ratchet on the remainder, and a server-computed `RecommendedTrimShares` that is always settlable.
- [x] Drawdown state (`PeakEquityPhp`, `RiskPosture`), sector exposure cards, alerts, and expectancy / profit factor / payoff ratio from the closed ledger.

### Research, replay and scorecard
- [x] `TickerResearchService`: live single-symbol chart or stored-session fallback, catalog membership, tick-valid plan (static-band clamped for `DAY`), the same edge assessment and ticket as the run, causal regime from the fetched PSEi bars, the same `WavePlan` and a held-position `PositionAdvice` (`averageCost`, optional `shares`).
- [x] `BacktestService`: prefix-only evaluation, multi-slot book (`MaxOpenPositions` 5, 6% heat, 2 names / 35% equity per sector), equity-based sizing with cash as a funding check, drawdown governor, board-lot slippage, break-even/EMA20 exit ladder, causal index regime gate, PSEi benchmark, alpha, CAGR, Sharpe, Calmar, total slippage and every trade.
- [x] `WalkForwardEdge`: the replay's own residual model, refitted every 60 sessions on already-resolved candidates, used only when it beats the neutral base rate on its own trades; it never writes `TrainedHorizonModels` and its acceptance is deliberately in-sample.
- [x] `SignalOutcomeService`: entry-fill gate, `UNFILLED` terminal rows, conservative same-bar tie to the stop, realized net R after per-leg fees, scorecard aggregation, live calibration offset and desk memory.
- [x] `GET /api/signals/scorecard`, `GET /api/signals/backtest`, `GET /api/research/catalog`, `GET /api/research/{query}`.

### Market data, DQR and knowledge
- [x] Official PSE Daily Quotation Report: deterministic parser (PDF/CSV/TXT/TSV, header mapping, accounting negatives, suspension flags, missing-marker columns) with two ingestion paths — live templates (`pse-dqr-live`) and operator upload (`pse-dqr-upload`, 25 MB, `400`/`404` on bad input).
- [x] Upload overlays foreign flow plus suspension disclosures only; breadth, macro dials, FX, realized vol and rotation survive, and unmatched tickers keep `null`.
- [x] Cross-asset dials (`^TNX` bps, `BZ=F`, `EPHE`) fetched concurrently, best-effort, `null` when stale — printed into the prompt's `CROSS-ASSET:` line.
- [x] SQLite RAG library: 8 built-in documents / 46 sections (front matter, `slug#section-n` references, content-hash re-indexing), mandatory-guardrail injection, BM25 + cosine RRF with horizon/sector/trap boosts, optional embeddings, Markdown/TXT/PDF upload behind the rights attestation, built-in delete protection (`409`).

### API, compliance and operations
- [x] Full REST surface: signals (`latest`, `by-date`, `dates`, `ticker/{symbol}/history`, `scorecard`, `backtest`, `model`, `train`, `train-all`, `desk-memory`, `run-now`, `quotation-report`), research (`catalog`, `{symbol}` with horizon/`averageCost`/`shares`), portfolio (summary, settings, order-ticket, trades, stop, close, trim), knowledge (list, reindex, upload, delete) and `/health`.
- [x] RFC 7807 errors: `400` config/input/attestation/rule violation, `404` unknown session/document, `409` protected document, `499` client abort, `502` generation, `503` feed.
- [x] `ComplianceGuardMiddleware` (loopback-only in `PersonalLocal`, constant-time operator key on writes in `LicensedCommercial`, `/health` bypass) and `AllowedHosts` narrowed to loopback names.
- [x] First-run legal gate (`lib/legalConsent.ts`, `LegalDisclaimerGate`) plus `DISCLAIMER.md`, `PRIVACY.md`, `THIRD_PARTY_NOTICES.md`; non-solicitation copy across the prompt and the UI.
- [x] `RequestCancellationMiddleware` (499), caller-cancellation rethrow in the provider, and a one-macrotask deferral in the terminal that removes the common StrictMode abort.
- [x] Scheduler: weekdays 15:35 Asia/Manila, four horizons per session, weekly refit, cold-boot refit.

### Frontend
- [x] React 19 + TypeScript 7 + Vite 8 + Tailwind 4 shell with a typed API client (`ApiError`, ProblemDetails, abort signals, FormData uploads) and centralized formatters.
- [x] `Simple | Pro` view mode and light/dark theme, both persisted; Simple leads with `MarketDirectionHero` (traffic light, 10–90 gauge, reasons, sector chips, trust line) and `RidingNowStrip`, then at most three `AdviceCardList` cards with the four-step plan grid and a technical drawer.
- [x] Pro keeps the dense `SignalTable`/`ProSignalCard` views, regime tiles, rotation strip, governor/slippage tiles, `RiskSizerBar`, verdict/drift badges and the Knowledge Base action.
- [x] `TickerDetailDialog` (thesis, plan, odds, factors, technicals, dividends, disclosures with trap badges, server-verified citations, rank history), `ResearchDialog` (buy plan vs "already holding it" with `HoldingVerdictCard`), `ScorecardDialog` (forward scorecard, dual-line benchmarked replay, `ModelTrainerPanel` with learned edge / desk memory and refit buttons).
- [x] `PortfolioDialog` (Book / Record a trade / Ledger / Settings; trim, pyramid badges, expectancy tiles, exposure gauges, guardrails drawer), `KnowledgeBaseDialog`, `QuotationReportImportButton`, `WelcomeSetupBanner`, `BeginnerGuideDialog`, `LegalDisclaimerGate`, `DropPanel`.
- [x] Responsive foundations: Radix `Dialog` as a bottom sheet below `sm` with a sticky header, card fallbacks for every wide table below `md`, pinned identifier column, scrolling tab rail, touch-sized controls, `100dvh` / `viewport-fit=cover` / `.pb-safe`, pre-paint theme colour and 16px mobile inputs.
- [x] Zero-dependency SWR cache (`lib/queryCache.ts`, ref-counted abort, in-flight dedupe, prefix invalidation) plus idempotent preloaders; every reader paints cached data, then revalidates, and keeps the last payload behind an amber line when a refresh fails.
- [x] Lazy dialog chunks and pinned vendor chunks through a `manualChunks` **function** (Vite 8 / rolldown rejects the object form).

### Tooling and documentation
- [x] `npm test` harness: `node --test "src/lib/__tests__/*.test.ts"` over the React-free `lib/` layer (no DOM, no dependencies).
- [x] `.vscode/launch.json` + `tasks.json` (API launch/attach, Chrome, full-stack compound, build/test tasks, `backend: free port 5236`) and `frontend/.env.example`.
- [x] `.gitignore` audited (build/test output, NuGet caches, local secrets, SQLite + sidecars + `.bak`, Node/Vite, `.env` overrides, editor/OS noise) with `git ls-files -i -c --exclude-standard` empty.
- [x] `README.md`, the three legal notices, `security_audit_report.md`, the six memory-bank files and `.clinerules/memory-bank.md`, with the sync rules recorded in `techContext.md`.

## Test inventory — 390 backend + 49 frontend

Counts are the executed `dotnet test --list-tests` rows per class (parameterised rows included) and the `ℹ tests` figure per frontend file.

### Backend — `backend/tests/PseSignalDesk.Tests` (26 files)

| Layer | Suite | Cases |
| --- | --- | --- |
| Domain | `PseBoardLotServiceTests` — 15 tier/tick rows, bracket crossing, static band, plan repair, per-leg fees, net R:R, break-even, EV, Quarter-Kelly, risk budget, ADV, slippage | 73 |
| Application | `QuantitativeFilterServiceTests` — indicators, stages, weighted score, liquidity floor, top-N cap, breadth, macro dials, rotation, disclosure trap wiring | 32 |
| Application | `OrderTicketCalculatorTests` — sizing, verdicts, governor band, heat/sector/cash caps, slippage, execution guidance, all seven pyramid rules | 27 |
| Application | `PortfolioServiceTests` — open/close/stop rules, pyramiding, trims, drawdown posture, exposure, alert dedupe | 27 |
| Application | `ProbabilisticEdgeServiceTests` — base rate, barrier geometry, tilts, clamps, verdict gates, hit rates, learned edge, calibration | 22 |
| Application | `PseDisclosureClassifierTests` — categories, hard/soft traps, priority order, copy | 20 |
| Application | `TickerResearchServiceTests` — live/stored paths, plan validity, `DAY` clamp, ticket/edge parity, regime, holding advice | 17 |
| Application | `SignalOutcomeServiceTests` — entry gate, `UNFILLED`, stop tie, net R, scorecard, calibration, desk memory | 16 |
| Application | `BacktestServiceTests` — prefix causality, book limits, governor, slippage, exits, benchmark metrics, walk-forward | 13 |
| Application | `WaveTradePlanCalculatorTests` — plan levels, chase ceiling, entry status, ladder, trim size | 12 |
| Application | `SignalPipelineServiceTests` — discards, gates, drift, drops, persistence, `502` | 12 |
| Application | `SignalJsonParserTests` — fences, prose, brace-in-string, numeric strings, contract failure | 9 |
| Application | `LearnedEdgeModelTests` — activation gate, ±6pp tilt, Kelly rescale, probability clamp | 7 |
| Application | `SectorRotationServiceTests` — mapping, leaders/laggards, strength | 7 |
| Application | `MarketDirectionServiceTests` — pillar blend, clamp, bearish override, unreported dials | 6 |
| Application | `DailyQuotationImportServiceTests` — live/upload paths, overlays, unmatched tickers, size guard | 4 |
| Application | `MoneyManagerTrainingServiceTests` — promotion gate, purged split, inactive fallback | 3 |
| Infrastructure | `PseMarketDataProviderTests` — symbol normalisation, candle sanitation, zero-volume echoes, proxy fallback, FX staleness, macro dials | 18 |
| Infrastructure | `OllamaPromptBuilderTests` — horizon block, knowledge, feedback, cross-asset, desk memory, guardrails | 16 |
| Infrastructure | `SqliteKnowledgeServiceTests` — reindex idempotency, guardrail retrieval, delete protection, search boosts | 8 |
| Infrastructure | `KnowledgeDocumentParserTests` — chunking, section refs, content hash | 6 |
| Infrastructure | `PseDailyQuotationParserTests` — header mapping, negatives, suspension, missing markers | 6 |
| Infrastructure | `DailySignalSchedulerRetrainTests` — weekday/hour trigger, per-horizon retrain, scope isolation | 5 |
| WebApi | `ComplianceGuardMiddlewareTests` — loopback gate, operator key (constant time), `/health` bypass, licensed passthrough | 18 |
| WebApi | `RequestCancellationMiddlewareTests` — 499 on abort, no write after cancellation | 4 |
| WebApi | `KnowledgeControllerTests` — upload attestation, protected-delete mapping | 2 |

### Frontend — `frontend/src/lib/__tests__` (3 files, React-free)

| File | Covers | Cases |
| --- | --- | --- |
| `plainAdvice.test.ts` | `plainAdvice` / `plainStatus` / formatter copy for every plan, verdict, scorecard and backtest field, plus the "not advice" wording | 35 |
| `queryCache.test.ts` | cache read/write, ref-counted abort, in-flight dedupe, prefix invalidation, failed-refresh retention | 8 |
| `legalConsent.test.ts` | first-run gate, versioned acceptance, stale notice, review clears flag, throwing storage, notice content | 6 |

## Remaining

### Security remediation (`security_audit_report.md`, 2026-11-26 — 15 findings)

- **Done** — F-01/F-02 partly: `ComplianceGuardMiddleware` (loopback-only `PersonalLocal`, constant-time operator key on writes in `LicensedCommercial`), loopback `AllowedHosts`, `/health` bypass.
- **P0/P1 open** — F-03 prompt fencing for retrieved text; F-04 lookback ceiling; F-05 price bounds in `PseBoardLotService`; F-06 `KnowledgeController` error detail; F-07 chunk bounds + batched embeddings.
- **P2 open** — F-08 security headers/HSTS, F-09 unbounded free text, F-10 regex timeout/response caps, F-11 SQLite file mode, F-12 committed lock files + `NuGetAudit` + `PdfPig` bump, F-13 rate limiting / run gate, F-14 `GET` mutating state, F-15 raw model response persistence.
- **Auth** — no user authentication, no origin check on unsafe methods, no rate limiter: keep the desk on `127.0.0.1`.

### Live verification (needs network + `OLLAMA_API_KEY`)

- [ ] `POST /api/signals/run-now` × four horizons, twice, to exercise drift and drop categories.
- [ ] `POST /api/signals/quotation-report` against a stored session (`foreignFlowSource`, suspension trap).
- [ ] `POST /api/signals/train-all` → four books `LEARNED`, then a second call → `skipped: every horizon already carries a fitted model`.
- [ ] `GET /api/signals/backtest?horizon=WEEK` once `PSEI.PS` returns ≥200 bars.
- [ ] Ticket parity: `SignalItemDto.orderTicket` = `/api/research/{ticker}` = `/api/portfolio/order-ticket`.
- [ ] Real browser pass, Simple and Pro, 320 → 2560px (no component suite exists).

### Product tuning (policy defaults, not fitted)

- [ ] Direction pillars 20/14/8/6/6, 60/45 thresholds, 1.25 chase floor (2.0 on `DAY`).
- [ ] `+1R` break-even / `+1.5R` trail and the 5%/10% ladder — currently duplicated in training, replay and wave plans.
- [ ] Governor `[0.50, 1.50]`, 2% Kelly reference, 6% heat, 2 names / 35% sector, λ = 25, `EV > 0` on a thin tape.
- [ ] Calibration prior 0.42 / 12 pseudo-observations / ±10pp clamp after ≥30 graded picks.
- [ ] Slippage model (`2 × tick / price` + √impact, ≤ 1.5%) against real fills.
- [ ] Optional: horizon-specific net-R:R and EV minima beyond the current uniform 2.0 floor.

### UI / device

- [ ] Manual iOS Safari + Android Chrome pass (sheet dismissal, safe areas, 16px inputs, chart legibility).
- [ ] Decide whether a component/DOM harness (jsdom + Testing Library) is worth the dependency; today `lib/` is the only tested surface.
- [ ] A lint/format step is still absent (no ESLint/Prettier config).

### Docs

- [ ] Keep the sync rules in `techContext.md` current; refresh `README.md` counts only from real command output.

## Known issues and limitations

- **Unverified live feeds.** Yahoo/PSE answered `404`/`429` from this environment, so ingestion, the model calls, DQR live templates and the replay's PSEi benchmark have never been observed end-to-end here. By design a failure is a `503`, never synthetic data.
- **No UI automation.** Responsiveness and the Simple/Pro switch are verified by reading code and by `tsc`, not by a test.
- **Deliberately in-sample walk-forward.** `WalkForwardEdge` accepts its residual model by comparing it to the base rate on its own trades; it is a replay aid, not a validated out-of-sample result — and it never writes `TrainedHorizonModels`.
- **Bootstrap success path unobserved.** Only the failure branch (`cold-boot refit skipped`) has been seen; the `LEARNED` path still needs a network run.
- **SQLite decimals.** Amounts are `decimal` and read back through SQLite's `TEXT` provider; the desk treats values as advisory and rounds at the board lot.
- **Calendar, not exchange calendar.** The scheduler fires on weekdays 15:35 Asia/Manila and does not know PSE holidays.
- **Thin books start on the prior.** Below 25 held-out trades (training) or 20 graded picks (calibration) the desk prices off the structural prior with small tilts, so a fresh database reports `PRIOR_FALLBACK`.
- **Retrieval degrades silently by design.** No embeddings key, an embeddings failure or a BM25-only fallback all still retrieve through guardrails rather than erroring.
- **Ladder duplication.** The break-even/trail ladder lives in three services; a change must be applied in all three (training, replay, wave plans).
- **Replay/training cost.** Both refetch long lookbacks for the liquid universe per call; refits are bounded and cached, but a cold `train-all` is the slowest operation in the app.

## Verification log (newest first, observed output)

**2026-11-26 — documentation pass (this pass; docs only).**

```
cd backend && dotnet test PseSignalDesk.slnx --nologo
  -> Passed! - Failed: 0, Passed: 390, Skipped: 0, Total: 390 (net10.0)
cd frontend && npm test            -> ℹ tests 49 · ℹ pass 49 · ℹ fail 0
cd frontend && npm run build       -> tsc --noEmit exit 0; vite ✓ 129 modules, ✓ built in <350ms
sqlite3 …/pse_signals.db "select slug, chunkcount …" -> 8 built-in documents, 46 chunks
git ls-files | wc -l               -> 230 tracked; 5531 ignored; tree clean before edits
```

**2026-11-26 — compliance hardening** (`ComplianceGuardMiddleware`, legal notices, first-run
gate, non-solicitation copy): 390 / 49 / `tsc` / build green; no behaviour-critical number changed.
**2026-11-26 — security audit** (`security_audit_report.md`, read-only): 15 findings, no code changed.
**2026-11-26 — money-manager pass** (training, learned edge, calibration, desk memory): backend went
369 → 390, frontend 42 → 49. **2026-11-25 — data-accuracy pass**: 363 backend / 42 frontend at the
time; per-leg fees, single slippage debit, `UNFILLED`, proxy regime and row-attributed filings landed
with regression tests.

Earlier smoke tests against the real SQLite database (each on a throwaway port, then stopped):

```
POST startup migration          -> PRAGMA table_info(PortfolioTrades) ends with PyramidCount INTEGER NOT NULL DEFAULT 0
GET  /api/portfolio             -> 200, expectancyR / profitFactor / payoffRatio / riskPosture / drawdownPct
POST /api/portfolio/trades/999999/trim -> 400 {"title":"Order rejected","detail":"Position 999999 does not exist."}
GET  /health                    -> 200 {"status":"ok","service":"PseSignalDesk.WebApi",…}
POST /api/signals/quotation-report (date=1999-01-01) -> 404 "No stored session for that trading day"
POST /api/signals/quotation-report (notes.xlsx)      -> 400 "'.xlsx' is not a quotation-report format…"
GET  /api/knowledge             -> 200 (built-in docs, source: builtin); reindex 0 indexed / N skipped
DELETE /api/knowledge/{builtin} -> 409 (built-ins are protected); Markdown upload 201 -> delete 204
GET  /api/signals/backtest?horizon=WEEK -> 503 "PSEi (PSEI.PS) returned only 1 live daily bars…"
```

The `503` is the sandbox feed, not the code: `FetchIndexBarsAsync` refuses an index series shorter
than `MinimumIndexBars`, and Yahoo answered `429 Too Many Requests` on both `query1` and `query2`
from this network, so `run-now` and `/api/research/{ticker}` correctly returned `503` too.

Environment notes: use `./node_modules/.bin/tsc` (a bare `npx tsc` only prints npm notices and looks
like a silent failure); if NuGet resolves a relative global-packages folder, prefix the build/test with
`NUGET_PACKAGES=$HOME/.nuget/packages`.

