# Active Context — PSE Signal Desk

**Last updated:** 2026-11-26 (live-drift throttle wired into sizing on top of the accuracy pass — point-in-time replay, dated friction, robust liquidity, nested λ selection; **416 backend / 49 frontend tests green**)
**Last feature pass:** 2026-11-26 (accuracy pass, five pillars — see below) + live-drift wiring (same day)
**Current task:** none in flight. Pillars 1 (core), 3, 4 (friction) and 5a/5b (logic **and** wiring) landed; the remainder is listed under *Open work*.

## Where the desk stands

A single-operator research desk for the Philippine Stock Exchange, with all four horizons live:

- **Ingestion.** Yahoo Finance v8 charts (`PSEI.PS` or an equal-weight heavyweight proxy, `PHP=X`, `{TICKER}.PS`), PSE Edge filings (row-attributed, word-boundary matched), an optional official PSE Daily Quotation Report (live templates or operator upload) and three best-effort macro series (`^TNX`, `BZ=F`, `EPHE`). No synthetic series and no demo tape exist.
- **Screening.** A deterministic quant pass (Wilder RSI-14, ATR-14, EMA-20, SMA-50/150/200, 52-week high, Weinstein stage, RS63d, ATR%, VCP, turnover/volume), a hard PHP 5M floor on **both** the average and the 20-session median turnover plus ≥16 active sessions, a top-25 cap, market breadth and sector rotation. History is read point-in-time: no live foreign-flow print and no filing dated or timestamped after the session's close.
- **Ranking.** One strict-JSON Ollama call per horizon, grounded in retrieved knowledge sections, then server-owned post-processing: unknown ticker / thesis-less / hard-trap discard, tick-grid normalisation, the `DAY` static band and net-R:R gate, drift recomputation, drop classification and citation verification.
- **Pricing.** `ProbabilisticEdgeService` owns the win probability, break-even rate, EV, Quarter-Kelly stake and `TRADE` / `SMALL_SIZE` / `WATCH_ONLY` verdict. An optional **promoted** per-horizon logistic model (a residual over the prior, ±6pp tilt, Kelly rescale) and a live calibration offset feed in; the desk memory (realized record, sectors that paid/bled, stop-outs, ranked factors) is printed into the prompt.
- **Sizing.** `OrderTicketCalculator` builds one ticket per pick from a `BookSizingSnapshot` (risk budget, free cash, heat, sector room, 2% of 20-day turnover, governor multipliers, the live-drift throttle, slippage/ADV participation, execution guidance), settles the fees including the PHP 20 commission floor, and routes a same-ticker order into a legal pyramid add.
- **Grading and replay.** The forward-outcome ledger grades only plans the tape actually filled; `BacktestService` replays the same rules causally (point-in-time filings, median liquidity, fees dated to each trade, gap-open stop fills) into a multi-slot, benchmarked equity curve with a rolling walk-forward refit.
- **Paper book.** A cash ledger with raise-stop, close, trim and pyramid merge, plus expectancy / profit factor / payoff telemetry, drawdown posture and alerts.
- **Terminal.** **Simple** (market call, then at most three plan cards) and **Pro** (dense table and tiles) read one payload; persisted light/dark, bottom-sheet dialogs, card fallbacks from 320px up, a zero-dependency SWR cache with intent preloaders, and a first-run legal gate.
- **Knowledge base.** 8 built-in documents / 46 sections in SQLite, BM25 + cosine RRF retrieval with mandatory guardrails and horizon/sector/trap boosts, optional embeddings, and uploads behind a rights attestation.

## Verified in this pass (2026-11-26)

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 416, Skipped: 0, Total: 416` (~1 s, exit 0) |
| `cd frontend && npm test` | `tests 49 · pass 49 · fail 0` (≈0.2 s) |
| `cd frontend && npm run build` | `✓ built in 248ms`, exit 0 |
| `ls backend/src/PseSignalDesk.Infrastructure/Migrations` | eleven migrations; `20261125212733_AddRobustLiquidityColumns` and `20261125214041_AddLiveDriftState` both present, `Up`/`Down` in place |
| `dotnet ef migrations script 20261125212733_AddRobustLiquidityColumns` | renders `LiveDrift TEXT NOT NULL DEFAULT 'STABLE'` and `LiveDriftGradedPicks INTEGER NOT NULL DEFAULT 0` |
| `sqlite3` against a **copy** of `pse_signals.db` | both `ALTER`s apply; `PRAGMA table_info` ends at `LiveDrift` / `LiveDriftGradedPicks` with the intended defaults |
| `dotnet run` on a throwaway DB (`Data Source=/tmp/psedesk-smoke.db`) | eleven migrations applied; `/health`, `/api/portfolio`, `/api/signals/latest?horizon=WEEK`, `/api/portfolio/order-ticket` all `200`; the real container resolves the new optional `SignalOutcomeService` dependency |
| `dotnet test … --list-tests` | 416 rows over 27 backend test files |

Not run in this pass: `dotnet ef database update` against the real SQLite file (the migrations apply at next startup through `Database.MigrateAsync`; the `AddLiveDriftState` SQL was instead rendered with `migrations script` and applied to a throwaway copy), and anything needing network.

## What this pass changed

Eleven accuracy/leakage gaps, five pillars:

- **Pillar 1 — point-in-time (core done).** `QuantitativeFilterService.EvaluateStock` takes `includeLiveForeignFlow` (replay/training pass `false`, so `NetForeignTodayPhp` is null in history) and scopes its own `stock.Disclosures` through `ScopeToSession`; `IsAvailableBy` drops filings dated after the session or filed after `PseTradingSessionRules.MarketClose` on the same day; `StockDisclosure.FiledAtPht` (`TimeOnly?`) carries the timestamp. **Remaining:** `CorporateActionAdjuster` (dividend/split via `events=div,split` + `adjclose`), listing dates in `PseTickerInfo` for survivorship, macro-series retention (`^TNX`, `PHP=X`).
- **Robust liquidity.** `StockDailyMetric.MedianTurnover20dPhp` + `ActiveTradingDays20d`, `ComputeMedianTurnover20d`, `MeetsLiquidityFloor(avg, median, active)` (median ≥ floor **and** ≥16 active sessions, mean-only fallback for legacy `0`/`0` rows), wired into `SelectCandidates`, both `IsEligible` gates and `TickerResearchService`. Migration `AddRobustLiquidityColumns`.
- **Pillar 2 — volume/lookback gates.** `MoneyManagerTrainingService.DefaultLookbackCalendarDays` 1080 → 1820 (five years); horizon `MinimumVolumeRatio` now DAY 1.2 / WEEK 1.2 / MONTH 1.1 / YEAR 1.0 (relaxed from an intended 1.0/0.8 after the replay and training suites failed); `WeekdaySessionsBetween` delegates to the holiday-aware `PseTradingSessionRules.TradingSessionsBetween`.
- **Pillar 4 — walk-forward friction.** `BuildCandidate` and `LabelOutcome` are date-aware, stops fill at the gap open (`ResolveStopFillPrice`), and every fee call in `Simulate` passes the session date (`ComputeRealizedNetPnlPhp(shares, entry, exit, entryDate, exitDate)`, `ComputeInitialNetRiskPhp(..., tradeDate)`). Pre-CMEPA history pays the 0.60% sale tax it really paid. Walk-forward *folds* (multiple disjoint train/test windows) are **not** done — the replay still refits a single rolling model.
- **Pillar 5a — sizing.** The commission floor (PHP 20 + 12% VAT, ~PHP 8,000 gross/leg) makes per-share identities wrong on small clips, so `OrderTicketCalculator` publishes settled `NetRiskPhp`/`NetRewardPhp` and steps whole lots down until the risk fits, refusing with `"cannot carry a {lot}-share lot"` when one lot already exceeds the budget.
- **Pillar 5b — live drift (logic + wiring).** `LiveDriftPolicy` (pure, `MinimumGradedPicks` 15, win-rate gap in percentage points, expectancy gap in R, hysteresis) plus `OrderTicketContext.LiveDrift*`; the multiplier is applied **after** the `[0.50, 1.50]` governor clamp so `HALTED` reaches 0.00x. The `Classify` unit bug (fraction-vs-percentage-points) is fixed. **Now wired:** `SignalOutcomeService.EvaluateLiveDriftAsync(horizon)` grades the last 30 realized picks against the promoted model's own `ValidationWinRate` / `ValidationExpectancyR`, persists the verdict on `TrainedHorizonModel.LiveDrift` / `LiveDriftGradedPicks` (so the release hysteresis survives a restart) and returns a `LiveDriftSnapshot`; `PortfolioService.GetBookSizingSnapshotAsync(ct, horizon)` evaluates it once per book and `BookSizingSnapshot.For(ticker)` stamps every ticket. The pipeline, the terminal envelope and the research endpoint pass their horizon; a prior-priced book and the horizon-less portfolio ticket stay `STABLE`. A ledger read failure is logged and leaves the ticket unthrottled rather than failing the page.
- **Pillar 3 — calibration.** `SelectLambda` scans the L2 grid on a nested chronological inner split (`InnerTrainFraction = 0.80`, same purge) and only then reads the held-out slice for the promotion gate; thin slices fall back to the strongest regularisation instead of peeking.

Still open from the wider plan: `FeatureKeysJson` per-horizon schema persistence, `OllamaTopPick` audit-only fields, `PseDisclosureClassifier` category extensions, frontend unions (`types/signals.ts`, `plainAdvice.ts`), `InvalidationFirstPlanBuilder` / pipeline inversion, `TradeExitLadderPolicy` unification.

## Invariants that must not be relaxed

- **The server owns every invariant.** Prices, tick rounding, board lots, the static band, the net-R:R gate, sizing, odds, verdicts, drift, drop category, citations and the bear-case fallback are computed in code after the model answers. The model never supplies a probability, a size or a drift status.
- **Fees are per leg *and* dated, because the tax changed.** `BuyLegFeeRate` 0.295% and `SellLegFeeRate` 0.395% (the sell leg carries the 0.10% stock transaction tax) sum to `RoundTripFeeRate` 0.690% — but only on or after `CmepaEffectiveDate = 2025-07-01`. `StockTransactionTaxRate(date)` returns 0.60% before it, so historical calls take their own date. The commission floor (PHP 20, +12% VAT) means risk is settled pesos, never `shares × spread`.
- **A replay sees only what had printed.** No live foreign-flow print, no disclosure dated after the session, and nothing filed after the close on the session's own day; a stop fills at the gap open, never at the stop it gapped through.
- **Liquidity is a median.** The floor requires the mean **and** the 20-session median to clear PHP 5M with ≥16 active sessions; a legacy row with `0`/`0` falls back to the mean rather than being rejected.
- **Only an executed entry can be graded.** No fill inside the horizon window is `UNFILLED` — null exit, null R, excluded from the win rate, profit factor, Brier score and `openCount`.
- **Slippage is one cash debit.** The replay charges it at entry, stores it per trade and subtracts it from realized P&L, so `NetReturnAfterSlippagePct` equals `NetReturnPct` by construction.
- **A proxy index may drive context, never a level.** When the PSEi series is unusable, `PseiClose` / `PseiSma50` / `PseiSma200` stay unreported (`0`) and the regime note says "proxy".
- **A missing cell is a null, never a neighbour.** A `-`/`N/A` DQR marker keeps its own column, and a row with no net-foreign figure is dropped.
- **The browser never sizes.** `lib/riskSizing.ts` renders the server ticket; `lib/plainAdvice.ts` only re-words server values.
- **Hyper-parameters never see the promotion slice.** λ is chosen on a nested inner split; the held-out slice decides only promotion.
- **Live-or-fail.** A missing feed is a `503`, a missing key a `400`, an empty candidate set a `502`; nothing is persisted in any of those cases.

## Open work, in priority order

### 1. Accuracy pillars — remainder (next up)

1. **Surface the drift verdict in the UI.** Sizing already throttles and the ticket carries the `Describe` warning, but no DTO exposes `LiveDriftStatus`, so the Signals/Portfolio pages cannot badge "throttled to 65%" or explain the recovery rule. Additive `liveDrift` field on the ticket/book payloads + a `types/signals.ts` union.
2. **Pillar 1 remainder.** `CorporateActionAdjuster`: fetch `{TICKER}.PS` with `events=div,split` plus `adjclose`, build split/dividend factors and use adjusted prices for indicator and barrier history (raw prices for order levels). `PseTickerInfo` needs listing/delisting dates so the universe is point-in-time and delisted names stop appearing. Retain the macro series (`^TNX`, `PHP=X`) alongside each snapshot instead of overwriting.
3. **Pillar 2 remainder.** `FeatureKeysJson` on `TrainedHorizonModel` so a model's feature vector is versioned, and the loader refuses a book trained on a different key set instead of silently zero-filling.
4. **Pillar 5 remainder.** `InvalidationFirstPlanBuilder` (stop/invalidation before target, with the pipeline refusing a plan whose invalidation is not level-based), `TradeExitLadderPolicy` as the single implementation the trainer, replay and wave plans all call, and `OllamaTopPick` audit-only fields so the raw model ranking can be compared with the server's without affecting the run.
5. **Pillar 4 remainder.** True walk-forward folds (disjoint rolling train/test windows with a fold report) rather than the current single rolling refit.
6. **Frontend + docs.** Mirror new server fields in `types/signals.ts` / `plainAdvice.ts` unions, extend `PseDisclosureClassifier` categories, and refresh the memory bank.

### 2. Security remediation still open

`security_audit_report.md` (2026-11-26, read-only) records 15 findings. `ComplianceGuardMiddleware` now covers most of F-01/F-02 (loopback-only in `PersonalLocal`, constant-time operator key on writes in `LicensedCommercial`, `AllowedHosts` narrowed). Still open and confirmed in the code:

- **F-03 — prompt fencing:** retrieved playbook text still reaches the prompt without untrusted-content delimiters (`OllamaPromptBuilder.AppendPlaybooks`).
- **F-04 — lookback clamp:** `PseMarketDataProvider.NormalizeLookback` has a floor but no ceiling, so a huge `lookbackDays` still reaches the upstream fetch.
- **F-05 — price bounds:** partially closed. `PseBoardLotService.IsTradablePrice` (`> 0`, `<= MaximumTradablePrice = 1_000_000`) now guards `ComputeLegFeePhp`, `ResolveStopFillPrice`, every net-risk/reward/P&L identity and `ProbabilisticEdgeService`; what is still missing is a guard inside `GetBracket`/`RoundToValidTick` themselves and a rejection path in the WebApi for an absurd `entry`/`stop`/`target` payload.
- **F-06 — error detail:** `KnowledgeController` still returns `ex.Message` to the client.
- **F-07 — chunk/embed bounds:** `KnowledgeDocumentParser.ChunkPlainText` still bypasses `SplitLong`, and `OllamaEmbeddingClient.EmbedAsync` still sends every chunk in one request.
- **F-08 – F-15** — security headers/HSTS, unbounded free text (`title`, tags, notes, file name), regex timeouts/response caps, SQLite file mode, lock files + `NuGetAudit` + a `PdfPig` bump, rate limiting/run gate, `GET` mutating state, unbounded raw model response persistence.
- **F-01/F-02 remainder** — there is still no user authentication, no origin check on unsafe methods and no rate limiter; keep the desk on `127.0.0.1` until those land.

### 3. Live verification backlog (needs network + `OLLAMA_API_KEY`)

- `POST /api/signals/run-now` for all four horizons; a second session for real drift and drop categories.
- `POST /api/signals/quotation-report` against a stored session; confirm `foreignFlowSource` and the suspension trap.
- `POST /api/signals/train-all`, then read the four books: `optimalKellyFractionMultiplier`, the Brier/log-loss pair, `validationExpectancyR` and the status (`LEARNED` vs `PRIOR_FALLBACK`).
- `GET /api/signals/backtest?horizon=WEEK` once `PSEI.PS` returns ≥200 bars; compare `ExcessReturnPct` with a prior-pinned baseline, and read the dated-friction fields (`feesPaidPhp` per trade) against a hand-computed pre-CMEPA and post-CMEPA round trip.
- The bootstrap's success path (`Bootstrap money-manager refit finished … LEARNED`, then `skipped: every horizon already carries a fitted model`).
- Ticket parity: `SignalItemDto.orderTicket` vs `GET /api/research/{ticker}` vs `GET /api/portfolio/order-ticket` on the same plan.
- A real browser/device pass (Simple and Pro) at 320–2560px; no component/DOM suite exists, so this is the only UI verification.

### 4. Tuning questions (policy defaults, not fitted)

- Direction pillars (20/14/8/6/6), the 60/45 call thresholds and the 1.25 net-R:R chase floor (2.0 on `DAY`).
- The `+1R` break-even / `+1.5R` EMA20-trail ladder, and the 5%/10% drawdown ladder — they exist in three places (training, replay, wave plans) until `TradeExitLadderPolicy` lands.
- The `[0.50, 1.50]` governor band, the 2% Kelly reference, the 6% heat ceiling, two names / 35% notional per sector, fixed λ = 25 in the replay refit, and whether `EV > 0` is too strict on a thin tape.
- The per-horizon volume ratios (1.2 / 1.2 / 1.1 / 1.0) and the `MinimumActiveTradingDays20d = 16` median-liquidity gate — both were relaxed once already to keep the replay and training suites populated; a thinner tape may need them lower still.
- The drift thresholds (15 graded picks, 20pp / 0.60R minimum gaps, 1.00 / 0.65 / 0.35 / 0.00 multipliers, half-gap release) — policy numbers, not fitted.
- The calibration prior (0.42), 12 pseudo-observations and the ±10pp offset clamp once ≥30 picks are graded.
- The slippage model (`2 × tick / price` + √impact, capped at 1.5%) against real fills.

### 5. Documentation upkeep

Follow the sync rules in `techContext.md` (*Documentation map*): behaviour → README + memory bank; compliance/copy → the three notices + `legalConsent.ts` (bump `CURRENT_LEGAL_VERSION`) + README; a fee/rule change → `PseBoardLotService` + `backend/knowledge-base/` + README + memory bank. Record counts only after running the commands.

## Session checklist for contributors

1. Read all six `memory-bank/*.md` files before starting.
2. Confirm the baseline: backend `dotnet test PseSignalDesk.slnx` (**416**), `cd frontend && npm test` (**49**), `npm run build`.
3. Update `activeContext.md` when starting/finishing a task and `progress.md` when status changes.
4. Keep `systemPatterns.md` in sync with architectural changes and `techContext.md` with tool/config changes.
5. Never persist secrets (`OLLAMA_API_KEY`, `OllamaCloud:ApiKey`, `PSEDESK_OPERATOR_API_KEY`) — env or user-secrets only.
6. Never add a fallback that fabricates market data, never let a test hit the network, never compute a risk number in the browser.
7. Keep the compliance posture intact: loopback-only by default, no soliciting copy, simulated results labelled simulated, uploads behind the rights attestation, and the legal notices in sync with `lib/legalConsent.ts`.

