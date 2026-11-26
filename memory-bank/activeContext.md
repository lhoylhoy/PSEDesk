# Active Context — PSE Signal Desk

**Last updated:** 2026-11-26 (performance pass — per-URL chart cache, one session fan-out, pooled HTTP and the O(B²) indicator work removed, all gated on byte-identical golden snapshots; **433 backend tests green + 1 skipped benchmark / 49 frontend tests green**)
**Last feature pass:** 2026-11-26 (accuracy pass, five pillars) + live-drift wiring + this performance pass (same day)
**Current task:** none in flight. The performance pass is landed and verified but **not committed** — the operator asked for no commits unless explicitly requested.

## Where the desk stands

A single-operator research desk for the Philippine Stock Exchange, with all four horizons live:

- **Ingestion.** Yahoo Finance v8 charts (`PSEI.PS` or an equal-weight heavyweight proxy, `PHP=X`, `{TICKER}.PS`), PSE Edge filings (row-attributed, word-boundary matched), an optional official PSE Daily Quotation Report (live templates or operator upload) and three best-effort macro series (`^TNX`, `BZ=F`, `EPHE`). A session fetches everything in one fan-out and each download is reused for ten minutes (charts) / five (Edge pages) per request URL, so the four horizons of one run share one network call. No synthetic series and no demo tape exist.
- **Screening.** A deterministic quant pass (Wilder RSI-14, ATR-14, EMA-20, SMA-50/150/200, 52-week high, Weinstein stage, RS63d, ATR%, VCP, turnover/volume), a hard PHP 5M floor on **both** the average and the 20-session median turnover plus ≥16 active sessions, a top-25 cap, market breadth and sector rotation. History is read point-in-time: no live foreign-flow print and no filing dated or timestamped after the session's close.
- **Ranking.** One strict-JSON Ollama call per horizon, grounded in retrieved knowledge sections, then server-owned post-processing: unknown ticker / thesis-less / hard-trap discard, tick-grid normalisation, the `DAY` static band and net-R:R gate, drift recomputation, drop classification and citation verification.
- **Pricing.** `ProbabilisticEdgeService` owns the win probability, break-even rate, EV, Quarter-Kelly stake and `TRADE` / `SMALL_SIZE` / `WATCH_ONLY` verdict. An optional **promoted** per-horizon logistic model (a residual over the prior, ±6pp tilt, Kelly rescale) and a live calibration offset feed in; the desk memory (realized record, sectors that paid/bled, stop-outs, ranked factors) is printed into the prompt.
- **Sizing.** `OrderTicketCalculator` builds one ticket per pick from a `BookSizingSnapshot` (risk budget, free cash, heat, sector room, 2% of 20-day turnover, governor multipliers, the live-drift throttle, slippage/ADV participation, execution guidance), settles the fees including the PHP 20 commission floor, and routes a same-ticker order into a legal pyramid add.
- **Grading and replay.** The forward-outcome ledger grades only plans the tape actually filled; `BacktestService` replays the same rules causally (point-in-time filings, median liquidity, fees dated to each trade, gap-open stop fills) into a multi-slot, benchmarked equity curve with a rolling walk-forward refit.
- **Paper book.** A cash ledger with raise-stop, close, trim and pyramid merge, plus expectancy / profit factor / payoff telemetry, drawdown posture and alerts.
- **Terminal.** **Simple** (market call, then at most three plan cards) and **Pro** (dense table and tiles) read one payload; persisted light/dark, bottom-sheet dialogs, card fallbacks from 320px up, a zero-dependency SWR cache with intent preloaders, and a first-run legal gate.
- **Knowledge base.** 8 built-in documents / 46 sections in SQLite, BM25 + cosine RRF retrieval with mandatory guardrails and horizon/sector/trap boosts, optional embeddings, and uploads behind a rights attestation.

## Verified in the performance pass (2026-11-26)

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 433, Skipped: 1, Total: 434` (7 s, exit 0); the skip is the manual replay benchmark |
| `dotnet test … -c Release --filter FullyQualifiedName~Benchmark_reports --logger "console;verbosity=detailed"` | **before** (3 runs) 6,554 / 6,327 / 6,224 ms, 1,708.4 MB allocated on every run; **after** (3 runs) 4,741 / 5,649 / 6,247 ms, 1,401–1,404 MB |
| same benchmark, published numbers | identical on both sides: DAY 48 trades / 89,975.20 / −10.02 %, WEEK 36 / 89,709.45 / −10.29 %, MONTH 30 / 88,919.05 / −11.08 %, YEAR 27 / 89,081.12 / −10.92 % |
| `git stash push -u -- backend/src`, then the golden suite against the **pre-optimisation** source | `Passed! 5/5` — the four replay goldens and the filter golden match the optimised tree byte-for-byte, i.e. the refactor moved no number |
| `dotnet test … --list-tests` | 434 rows over 31 test files (`MarketDataCacheTests` 8, `PseMarketDataProviderTests` 20, `QuantitativeFilterServiceTests` 38, `BacktestGoldenTests` 5, `QuantitativeFilterGoldenTests` 1) |
| `git status --porcelain` | the intended 9 modified + 7 new paths only; all three comparison stashes popped (`git stash list` empty) |

The wall clock is noisy on this laptop — the three "after" runs span 4.7–6.2 s and one baseline run was as slow as the old median — so the **allocation** figure (1,708.4 MB → ~1,402 MB on every run, all four horizons) is the reliable signal; wall-clock is ~11 % faster on the medians and ~24 % on the best pair. Run the benchmark in `Release` only; the earlier `Debug` numbers (7.3 s vs 9.6 s) were noise, not a regression.

## Verified in the accuracy pass (2026-11-26 — counts as of that pass)

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

## What the performance pass changed

Four layers, each gated on the snapshot suite added first:

- **Output equivalence first (Phase 0).** `SyntheticMarketFixture` (seeded, clock-free synthetic tape), `GoldenSnapshot` (approval-style file compare, `PSEDESK_UPDATE_GOLDEN=1` records), `BacktestGoldenTests` (four horizons, plus a `Skip`ped benchmark `[Fact]` that prints elapsed time and allocations) and `QuantitativeFilterGoldenTests`, with `Application/Golden/backtest_{DAY,WEEK,MONTH,YEAR}.json` + `quantitative_filter_metrics.json` checked in. Every later step had to leave these byte-identical.
- **A per-URL download cache (Phase 1a).** `MarketDataCache` (singleton, `TimeProvider`-injectable, bounded at 200 entries, oldest-`CreatedAt` eviction) caches the **task**, not the value, so concurrent callers share one HTTP GET; a failure is evicted before the exception escapes (never remembered); the shared fetch runs on `CancellationToken.None` while each caller waits with `Task.WaitAsync(token)`, and the download keeps its own `RequestTimeoutSeconds` timeout — an abandoned tab can neither cancel nor poison a shared download. Keys are the absolute URL (`chart:` 10 min, `edge:` 5 min), so `(symbol, window)` variants separate naturally and parsing (tick rounding, volume filter, date clamp) still runs per call. `IMarketDataProvider.InvalidateCache()` landed as a default no-op; `PseMarketDataProvider.InvalidateCache()` clears the map and `POST /api/signals/run-now` calls it.
- **One fan-out per session (Phase 1b/1c).** After the index fixes the session date, `FetchAsync` starts the FX series, the Edge pages, the DQR, the macro dials and the ticker charts together and `Task.WhenAll`s them; `FetchStocksAsync`/`FetchStockAsync` now take the disclosure `Task<…>` and await it only after their own chart lands. The disclosure path was restructured (`throwOnFailure` deleted: `DownloadDisclosureHtmlAsync` always throws, `FetchDisclosureHtmlAsync` caches, `TryFetchDisclosureHtmlAsync` is the best-effort wrapper). The typed client got a pooled `SocketsHttpHandler` (`AutomaticDecompression = All`, `UseCookies = false`, `PooledConnectionLifetime = 5 min`) and the catalog endpoint a one-hour client cache.
- **The O(B²) indicator work is gone (Phase 2).** `ComputeEmaSeries` is the single O(n) EMA (seed = `period`-bar SMA, `null` before it), used by `ComputeEma`, `BacktestService.BuildCausalEma20` and the replay's new `BuildEma20Series` (one series per ticker instead of one per bar); `EvaluateStock` calls `IsAscendingByDate` to skip its defensive sort and fills every trailing-20-session statistic (mean, median, active days, up/down volume, CMF, turnover) in one pass; the Weinstein prior average, the short ATR and Kaufman's efficiency window are summed by index; `ComputeMedianTurnover` sorts a reused `decimal[]`; `BacktestService` memoises the index regime per date in `regimeByDate`.

New tests: `MarketDataCacheTests` (8 — concurrency, no-refetch, failure-not-cached, TTL, abandoned caller, pre-cancelled, `Clear`, oldest eviction), two `PseMarketDataProviderTests` cache cases (reuse + `InvalidateCache`), two `QuantitativeFilterServiceTests` (`ComputeEmaSeries` matches `ComputeEma` on every prefix, `IsAscendingByDate`), five golden rows and the skipped benchmark.

## What the accuracy pass changed

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
- **A cache may serve an older real body, never a synthetic one.** Charts live ten minutes and Edge pages five, in memory only, and a manual run clears them first. The cached object is the downloaded body, so tick rounding, the date clamp and the volume filter are still applied per caller.
- **Output equivalence beats a speed claim.** Any change to an indicator, a plan level or a fee must leave the checked-in goldens byte-identical, or explain the diff.
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

### 2. Performance pass — follow-ups (small)

1. **Invalidate from the other manual entry points** if a warm body ever proves stale there: `DailyQuotationImportService` (a DQR upload re-saves a session) and `POST /api/signals/train-all` currently reuse whatever the cache holds; the daily run, research and replay deliberately want the warm cache.
2. **Confirm the disclosure pagination split against a live outage.** A failed *first* page still fails the run (disclosures are part of the live contract) while a failed *extra* page is logged and skipped — that split is now explicit in code (`DownloadDisclosureHtmlAsync` throws, `TryFetchDisclosureHtmlAsync` swallows) instead of being a boolean parameter, but only a real PSE Edge failure can confirm the behaviour end-to-end.
3. **Keep the benchmark honest.** It is `Skip`ped and Release-only; the `Debug` figures collected mid-pass (7.3 s → 9.6 s) were scheduler noise, not a regression.
4. **Watch the cache bound.** 200 entries covers ~58 tracked names plus index/FX/macro/mirror-host variants; a much larger universe would need the bound raised (or keyed per session date).

### 3. Security remediation still open

`security_audit_report.md` (2026-11-26, read-only) records 15 findings. `ComplianceGuardMiddleware` now covers most of F-01/F-02 (loopback-only in `PersonalLocal`, constant-time operator key on writes in `LicensedCommercial`, `AllowedHosts` narrowed). Still open and confirmed in the code:

- **F-03 — prompt fencing:** retrieved playbook text still reaches the prompt without untrusted-content delimiters (`OllamaPromptBuilder.AppendPlaybooks`).
- **F-04 — lookback clamp:** `PseMarketDataProvider.NormalizeLookback` has a floor but no ceiling, so a huge `lookbackDays` still reaches the upstream fetch.
- **F-05 — price bounds:** partially closed. `PseBoardLotService.IsTradablePrice` (`> 0`, `<= MaximumTradablePrice = 1_000_000`) now guards `ComputeLegFeePhp`, `ResolveStopFillPrice`, every net-risk/reward/P&L identity and `ProbabilisticEdgeService`; what is still missing is a guard inside `GetBracket`/`RoundToValidTick` themselves and a rejection path in the WebApi for an absurd `entry`/`stop`/`target` payload.
- **F-06 — error detail:** `KnowledgeController` still returns `ex.Message` to the client.
- **F-07 — chunk/embed bounds:** `KnowledgeDocumentParser.ChunkPlainText` still bypasses `SplitLong`, and `OllamaEmbeddingClient.EmbedAsync` still sends every chunk in one request.
- **F-08 – F-15** — security headers/HSTS, unbounded free text (`title`, tags, notes, file name), regex timeouts/response caps, SQLite file mode, lock files + `NuGetAudit` + a `PdfPig` bump, rate limiting/run gate, `GET` mutating state, unbounded raw model response persistence.
- **F-01/F-02 remainder** — there is still no user authentication, no origin check on unsafe methods and no rate limiter; keep the desk on `127.0.0.1` until those land.

### 4. Live verification backlog (needs network + `OLLAMA_API_KEY`)

- `POST /api/signals/run-now` for all four horizons; a second session for real drift and drop categories.
- `POST /api/signals/quotation-report` against a stored session; confirm `foreignFlowSource` and the suspension trap.
- `POST /api/signals/train-all`, then read the four books: `optimalKellyFractionMultiplier`, the Brier/log-loss pair, `validationExpectancyR` and the status (`LEARNED` vs `PRIOR_FALLBACK`).
- `GET /api/signals/backtest?horizon=WEEK` once `PSEI.PS` returns ≥200 bars; compare `ExcessReturnPct` with a prior-pinned baseline, and read the dated-friction fields (`feesPaidPhp` per trade) against a hand-computed pre-CMEPA and post-CMEPA round trip.
- The bootstrap's success path (`Bootstrap money-manager refit finished … LEARNED`, then `skipped: every horizon already carries a fitted model`).
- Ticket parity: `SignalItemDto.orderTicket` vs `GET /api/research/{ticker}` vs `GET /api/portfolio/order-ticket` on the same plan.
- A real browser/device pass (Simple and Pro) at 320–2560px; no component/DOM suite exists, so this is the only UI verification.

### 5. Tuning questions (policy defaults, not fitted)

- Direction pillars (20/14/8/6/6), the 60/45 call thresholds and the 1.25 net-R:R chase floor (2.0 on `DAY`).
- The `+1R` break-even / `+1.5R` EMA20-trail ladder, and the 5%/10% drawdown ladder — they exist in three places (training, replay, wave plans) until `TradeExitLadderPolicy` lands.
- The `[0.50, 1.50]` governor band, the 2% Kelly reference, the 6% heat ceiling, two names / 35% notional per sector, fixed λ = 25 in the replay refit, and whether `EV > 0` is too strict on a thin tape.
- The per-horizon volume ratios (1.2 / 1.2 / 1.1 / 1.0) and the `MinimumActiveTradingDays20d = 16` median-liquidity gate — both were relaxed once already to keep the replay and training suites populated; a thinner tape may need them lower still.
- The drift thresholds (15 graded picks, 20pp / 0.60R minimum gaps, 1.00 / 0.65 / 0.35 / 0.00 multipliers, half-gap release) — policy numbers, not fitted.
- The calibration prior (0.42), 12 pseudo-observations and the ±10pp offset clamp once ≥30 picks are graded.
- The slippage model (`2 × tick / price` + √impact, capped at 1.5%) against real fills.

### 6. Documentation upkeep

Follow the sync rules in `techContext.md` (*Documentation map*): behaviour → README + memory bank; compliance/copy → the three notices + `legalConsent.ts` (bump `CURRENT_LEGAL_VERSION`) + README; a fee/rule change → `PseBoardLotService` + `backend/knowledge-base/` + README + memory bank. Record counts only after running the commands.

## Session checklist for contributors

1. Read all six `memory-bank/*.md` files before starting.
2. Confirm the baseline: backend `dotnet test PseSignalDesk.slnx` (**433 passed + 1 skipped benchmark = 434**), `cd frontend && npm test` (**49**), `npm run build`.
3. Update `activeContext.md` when starting/finishing a task and `progress.md` when status changes.
4. Keep `systemPatterns.md` in sync with architectural changes and `techContext.md` with tool/config changes.
5. Never persist secrets (`OLLAMA_API_KEY`, `OllamaCloud:ApiKey`, `PSEDESK_OPERATOR_API_KEY`) — env or user-secrets only.
6. Never add a fallback that fabricates market data, never let a test hit the network, never compute a risk number in the browser.
7. Keep the compliance posture intact: loopback-only by default, no soliciting copy, simulated results labelled simulated, uploads behind the rights attestation, and the legal notices in sync with `lib/legalConsent.ts`.

