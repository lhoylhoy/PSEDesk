# Tech Context — PSE Signal Desk

## Toolchain (verified in this workspace)

| Tool | Version | Notes |
| --- | --- | --- |
| .NET SDK | `10.0.401` | All backend projects target `net10.0` |
| Node.js | `v24.21.0` | Frontend tooling; `npm test` relies on its native TypeScript type stripping (no ts-node/tsx) |
| C# | `net10.0`, nullable + implicit usings enabled | C# 14 language defaults |
| EF Core | `10.0.12` | SQLite provider + Design package |
| xUnit | `2.9.3` | `xunit.runner.visualstudio` 3.1.4, `Microsoft.NET.Test.Sdk` 17.14.1, coverlet 6.0.4 |
| React | `^19.3.1` | `react-dom` same minor |
| TypeScript | `^7.0.2` | `strict`, `noEmit`, `noUnusedLocals/Parameters` |
| Vite | `^8.3.8` | `@vitejs/plugin-react` 6.3.0; bundles with **rolldown**, so `build.rollupOptions.output.manualChunks` must be a **function** (the Rollup object/array form fails with `TS2769: Object literal may only specify known properties`) |
| Tailwind CSS | `^4.3.5` | CSS-first via `@tailwindcss/vite`; no `tailwind.config` |
| Radix UI | dialog 1.3.1, select 2.4.1, tabs 1.2.1, tooltip 1.4.1 | Wrapped in `src/components/ui` |
| PdfPig | `0.1.16` | `UglyToad.PdfPig`; offline PDF text extraction for knowledge uploads |

No mocking, mapping, validation or HTTP client libraries beyond the framework (`HttpClient`, `System.Text.Json`, ASP.NET ProblemDetails). PdfPig is the only third-party runtime package added for the RAG layer.


## Commands

```bash
# Backend build + run
cd backend
dotnet build PseSignalDesk.slnx
dotnet run --project src/PseSignalDesk.WebApi        # http://localhost:5236

# Backend tests
cd backend
dotnet test PseSignalDesk.slnx                        # 433 passing, 1 skipped (the manual benchmark)

# Golden snapshots (output-equivalence guard: replay + quantitative filter)
dotnet test PseSignalDesk.slnx --filter "FullyQualifiedName~Golden"
PSEDESK_UPDATE_GOLDEN=1 dotnet test PseSignalDesk.slnx --filter "FullyQualifiedName~Golden"   # rewrite them

# Manual replay benchmark (prints elapsed time + allocations; Release only, ~5s)
dotnet test PseSignalDesk.slnx -c Release --filter "FullyQualifiedName~Benchmark_reports" \
  --logger "console;verbosity=detailed"

# Frontend
cd frontend
npm install
npm run dev                                          # http://localhost:5173 (strict port)
npm run build                                        # tsc --noEmit && vite build
npm test                                             # node --test "src/lib/__tests__/*.test.ts" (49 passing)
./node_modules/.bin/tsc --noEmit                     # type gate alone (npx tsc only prints npm notices here)
npm run preview

# EF migrations (design-time factory avoids booting WebApi)
cd backend
dotnet ef migrations add <Name> --project src/PseSignalDesk.Infrastructure --startup-project src/PseSignalDesk.WebApi
dotnet ef database update --project src/PseSignalDesk.Infrastructure --startup-project src/PseSignalDesk.WebApi
dotnet ef migrations list --project src/PseSignalDesk.Infrastructure --startup-project src/PseSignalDesk.WebApi

# Live run (needs network + OLLAMA_API_KEY; no seed step exists)
curl -X POST http://localhost:5236/api/signals/run-now
curl -X POST "http://localhost:5236/api/signals/run-now?date=2026-11-24&horizon=DAY"

# On-demand research (plain buy plan, then the same name as an owned position)
curl "http://localhost:5236/api/research/JFC"
curl "http://localhost:5236/api/research/JFC?horizon=WEEK&averageCost=12.40&shares=1200"

# Forward-outcome scorecard (no network needed; reads the stored ledger)
curl "http://localhost:5236/api/signals/scorecard?horizon=WEEK"

# Official PSE Daily Quotation Report upload (PDF/CSV/TXT/TSV, optional session date)
curl -F "file=@dqr-20261124.pdf" -F "date=2026-11-24" \
     http://localhost:5236/api/signals/quotation-report

# Knowledge base
curl http://localhost:5236/api/knowledge
curl -X POST http://localhost:5236/api/knowledge/reindex
curl -F "file=@book.md" -F "title=Trading book" -F "horizons=week" -F "categories=risk" \
     http://localhost:5236/api/knowledge/upload
curl -X DELETE http://localhost:5236/api/knowledge/7        # upload → 204, builtin → 409
```


## Runtime configuration

### Backend (`appsettings.json`, env overrides)

| Setting | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings:DefaultConnection` | `Data Source=pse_signals.db` | SQLite; relative paths resolved against the WebApi content root |
| `OllamaCloud:BaseUrl` | `https://ollama.com` | Ollama Cloud endpoint (chat + `/api/embed`) |
| `OllamaCloud:Model` | `deepseek-v4.1-flash` | Reasoning model that ranks the Top 10 |
| `OllamaCloud:FastExtractionModel` | `qwen3.5:4b` | Cheap disclosure/trap pre-pass; blank disables it |
| `OllamaCloud:EmbeddingModel` | `nomic-embed-text` | Knowledge chunk embeddings; blank → lexical retrieval |
| `OllamaCloud:ApiKey` | empty | Unset → `400` on every run (no offline engine); kept in **user-secrets**, never committed |
| `OllamaCloud:RequestTimeoutSeconds` | `120` | HttpClient timeout (min 30s) |
| `KnowledgeBase:RootPath` | `knowledge-base` | Markdown library folder (content root, then `AppContext.BaseDirectory`) |
| `KnowledgeBase:MaxSnippets` | `6` | Sections injected per prompt (mandatory guardrails included) |
| `KnowledgeBase:MaxChunkCharacters` | `1600` | Section size cap; longer sections are split |
| `KnowledgeBase:EnableEmbeddings` | `true` | `false` = lexical-only indexing/retrieval (offline switch) |
| `KnowledgeBase:IndexOnStartup` | `true` | Seeds/skips the built-in library at boot |
| `MarketData:YahooFinanceBaseUrl` | `https://query1.finance.yahoo.com` | v8 chart origin |
| `MarketData:YahooFinanceFallbackBaseUrl` | `https://query2.finance.yahoo.com` | Mirror host retried once on a transport error/5xx (a `404` is host-independent and is not retried) |
| `MarketData:IndexYahooSymbols` | `["PSEI.PS"]` | Index candidates in preference order; the first with a usable multi-bar chart wins |
| `MarketData:IndexProxyConstituents` | 12 heavyweights (`TEL`, `SM`, `SMPH`, `ALI`, `BDO`, `BPI`, `ICT`, `JFC`, `MER`, `AC`, `URC`, `GLO`) | When no index symbol has history, the regime/breadth/vol is read from an equal-weight proxy of these closes; the proxy never prints an index level or a PSEi moving average |
| `MarketData:PseEdgeAnnouncementsUrl` | `https://edge.pse.com.ph/announcements/search.ax` | Disclosure page (fail-fast contract) |
| `MarketData:PseEdgeCompanyDisclosuresUrl` | `https://edge.pse.com.ph/companyDisclosures/search.ax` | Second Edge listing, polled best-effort alongside the announcements |
| `MarketData:DisclosurePagesToScan` | `2` | Edge result pages scanned per listing (covers long weekends); extra pages best-effort |
| `MarketData:PseDailyQuotationUrlTemplates` | `[]` | Date-addressed DQR sources (`{yyyy}`, `{MMddyyyy}`, `{MMddyy}`); empty = rely on the upload endpoint, foreign flow stays `null` |
| `MarketData:Us10YahooSymbol` / `BrentYahooSymbol` / `EpheYahooSymbol` | `^TNX` / `BZ=F` / `EPHE` | Cross-asset macro dials (best-effort; a missing series leaves the tile `—`) |
| `MarketData:LookbackCalendarDays` | `420` | Calendar lookback per chart request (~280 sessions) |
| `MarketData:MaxConcurrency` | `8` | Parallel ticker requests |
| `MarketData:RequestTimeoutSeconds` | `15` | Per-request timeout; a timeout is a 503 |
| `Cors:AllowedOrigins` | `http://localhost:5173` | Frontend origin |
| `AllowedHosts` | `localhost;127.0.0.1;[::1]` | Host-header allowlist (no wildcard, so a forged `Host:` cannot resolve) |
| `Compliance:DeploymentMode` | `PersonalLocal` | `PersonalLocal` refuses non-loopback callers with `403`; `LicensedCommercial` opens that check and requires the operator key on writes. Unrecognised values fall back to personal |
| `Compliance:EnforceLoopbackInPersonalMode` | `true` | The loopback refusal switch; only a deliberate `false` disables it |
| `Compliance:OperatorApiKey` | empty | Shared key required on `POST`/`PUT`/`PATCH`/`DELETE` when set; blank is fine in personal mode and a `503` fail-closed in licensed mode |
| `MarketData:UserAgent` | `PseSignalDesk-PersonalResearch/0.1 (local-noncommercial)` | Outbound identity for Yahoo and PSE Edge — never a spoofed browser string |
| `PSEDESK_OPERATOR_API_KEY` | — | Environment override for `Compliance:OperatorApiKey`, so the key never sits in `appsettings.json` |
| `OLLAMA_API_KEY` / `OLLAMA_BASE_URL` / `OLLAMA_MODEL` / `OLLAMA_FAST_MODEL` / `OLLAMA_EMBEDDING_MODEL` | — | Environment variables override settings |


### Frontend

| Variable | Default | Purpose |
| --- | --- | --- |
| `VITE_API_BASE_URL` | `http://localhost:5236` | API base URL (trailing slashes stripped) |

See `frontend/.env.example`.

### Frontend browser storage

| Key | Values | Owner |
| --- | --- | --- |
| `psedesk.viewMode` | `simple` (default) / `pro` | `lib/viewMode.ts` — sets `data-view-mode` on `<html>` |
| `psedesk.theme` | `light` / `dark` — unset means system `prefers-color-scheme`, light when the browser cannot say | `lib/theme.ts` — sets `data-theme` on `<html>`; also read by the inline pre-paint script in `index.html` |
| `psedesk.welcomeDismissed` | `1` | `WelcomeSetupBanner` — the only way to re-show the first-run presets is clearing it |
| `psedesk.legalAcceptedVersion` | the `CURRENT_LEGAL_VERSION` string currently `2026-11-v1` | `lib/legalConsent.ts` — the first-run legal gate; a stale or missing value re-shows the dialog |

Storage access is wrapped: a blocked/quota-full `localStorage` falls back to in-memory state instead of throwing.

## Runtime topology

- API: `http://localhost:5236` (HTTP profile; HTTPS profile also exposes 7207). OpenAPI mapped in Development only; `/health` always available.
- Frontend dev server: `http://localhost:5173`, strict port; CORS pre-configured.
- SQLite file: `backend/src/PseSignalDesk.WebApi/pse_signals.db` (gitignored; created at startup via `Database.MigrateAsync`). Signal runs, the knowledge base and the paper blotter share this file.
- Knowledge library: `backend/knowledge-base/` is copied to the API output (`bin/<cfg>/net10.0/knowledge-base/…`) by MSBuild, so the packaged app can seed without the source tree. The seeded library is **8 documents / 46 sections** — `pse_rules.md` (mandatory), six playbooks (`day_week_momentum_vcp`, `day_week_risk_sizing`, `month_year_quality_value`, `month_year_ph_macro_sectors`, `probabilistic_expectancy`, `asymmetric_wealth_compounding`) and `docs/engineering-references.md`.
- Migrations: `20261124150948_InitialCreate`, `20261124231758_MakeNetForeignNullable`, `20261124233850_AddKnowledgeBaseAndMultiHorizon` (adds `KnowledgeDocuments`/`KnowledgeChunks`, `StockDailyMetrics.Sma150`/`Week52High`/`WeinsteinStage` and `SignalItems.BoardLotShares`/`CitedReferencesJson`) `20261125010058_AddProbabilisticEdgeAndScorecard` (adds the odds/verdict/bear-case/outcome columns, run-level cash posture + breadth, snapshot breadth, metric OHLC/RS/VCP/ATR%/hit-rate columns and the `SignalItems (Horizon, OutcomeStatus)` index; defaults keep legacy rows at `WATCH_ONLY` / `OPEN` / `0`), `20261125025142_AddPortfolioResearchAndDividends` (adds `PortfolioAccounts` / `PortfolioTrades` with their indexes and `StockDailyMetrics.TrailingDividendPerShare` / `TrailingDividendYieldPct` / `LastExDividendDate`), `20261125035931_AddMoneyManagerEngine` (adds the factor columns `UpDownVolumeRatio20d` / `ChaikinMoneyFlow20d` / `TrendEfficiency63d` / `Beta252d` / `MaxDrawdown252dPct` / `EmpiricalWinRateDay` / `EmpiricalWinRateMonth` / `EmpiricalWinRateYear` onto `StockDailyMetrics` (realised volatility is **not** stored per name — `QuantitativeFilterService.ComputeRealizedVolatilityPct` computes it on demand), the governance columns `PeakEquityPhp` / `MaxSectorExposurePct` / `MaxPositionsPerSector` / `SoftDrawdownThresholdPct` / `HardDrawdownThresholdPct` / `TargetAnnualVolPct` onto `PortfolioAccounts`, and `UsdPhpSma50` / `UsdPhpChange20dPct` / `PseiRealizedVol20dPct` / `SectorRotationJson` onto `DailyMarketSnapshots`; existing rows read `null` = unreported), and `20261125045357_AddCrossAssetMacroAndForeignFlowDqr` (adds `Us10YClosePct` / `Us10YChange20dBps` / `BrentUsd` / `BrentChange20dPct` / `EpheCloseUsd` / `EpheChange20dPct` / `ForeignFlowSource` onto `DailyMarketSnapshots`; nullable = unreported, `NetForeignTodayPhp` already existed on `StockDailyMetrics`), and `20261125060730_AddTrainedMoneyManagerModels` (adds `TrainedHorizonModels` with a unique `Horizon` index; the JSON weight/stat blobs are `TEXT` and the scores are `TEXT` decimals, exactly like the rest of the SQLite schema), and `20261125092954_AddPyramidCountToPortfolioTrades` (adds `PortfolioTrades.PyramidCount` as `INTEGER` default 0, so a book written before the pyramid layer reads "no adds taken"), and `20261125212733_AddRobustLiquidityColumns` (adds `StockDailyMetrics.MedianTurnover20dPhp` `TEXT` precision 18,2 default `0` and `ActiveTradingDays20d` `INTEGER` default `0`; a legacy row reads `0`/`0`, which `MeetsLiquidityFloor` interprets as "median not measured" and falls back to the mean), and `20261125214041_AddLiveDriftState` (adds `TrainedHorizonModels.LiveDrift` `TEXT` max 16 default `'STABLE'` and `LiveDriftGradedPicks` `INTEGER` default `0`; the `'STABLE'` default is deliberate — an empty-string default would make EF's string-to-enum conversion throw on every pre-existing row).
- **Fee schedule is dated, and the calendar is the exchange's.** `PseBoardLotService.StockTransactionTaxRate(tradeDate)` is `0.60%` before `CmepaEffectiveDate = 2025-07-01` and `0.10%` after, so any historical call must pass its own date or it will over-charge 2023 trades and under-charge 2026 ones. `PseTradingSessionRules.TradingSessionsBetween` excludes weekends **and** PSE holidays and is the only way to count sessions between two dates (embargo length, forward windows, `WeekdaySessionsBetween`). `DefaultLookbackCalendarDays` is `900` in `BacktestService` and `1820` (five years) in `MoneyManagerTrainingService`; `MarketData:LookbackCalendarDays` (420) only governs the ingestion chart request.
- Risk-governance settings are **data, not configuration**: the sector caps, drawdown ladder and `TargetAnnualVolPct` live on the `PortfolioAccounts` row and are edited through `PUT /api/portfolio/settings` (clamped server-side, hard > soft enforced). There is no appsettings knob for them, so a restart can never silently widen the pod's risk budget.
- Hosted services (in order): `KnowledgeBaseInitializer` (seed library, failures logged) then `DailySignalSchedulerService`, weekdays 15:35 PHT (07:35 UTC), one run per horizon `DAY`/`WEEK`/`MONTH`/`YEAR`.


## Secret handling

`appsettings.json` ships `OllamaCloud:ApiKey` blank. Set it per machine without touching the repo:

```bash
cd backend/src/PseSignalDesk.WebApi
dotnet user-secrets set "OllamaCloud:ApiKey" "..."   # UserSecretsId already in the csproj
```

Environment variables (`OllamaCloud__ApiKey`, `OLLAMA_API_KEY`) still win, so CI and the sandbox can inject the key without user-secrets. The compliance operator key has the same shape: `PSEDESK_OPERATOR_API_KEY` (bound over `Compliance:OperatorApiKey` in `Program.cs`) and never committed — `appsettings.json` ships it blank.

## Legal notices

`DISCLAIMER.md`, `PRIVACY.md` and `THIRD_PARTY_NOTICES.md` live at the repository root (and are linked from `README.md`). `frontend/src/lib/legalConsent.ts` mirrors their reader-facing summary; bumping `CURRENT_LEGAL_VERSION` there re-prompts every browser through `LegalDisclaimerGate`. Keep the three documents, that summary and the README's compliance paragraph in sync when any of them changes.

## Documentation map

| Document | Audience / purpose |
| --- | --- |
| `README.md` | The user-facing manual: what the desk does, how to run it, the API table, tests and limitations |
| `DISCLAIMER.md` / `PRIVACY.md` / `THIRD_PARTY_NOTICES.md` | Legal posture, data handling and licences (linked from the README and mirrored by the first-run gate) |
| `security_audit_report.md` | The 2026-11-26 read-only audit (OWASP ASVS / API Top 10 / LLM Top 10) with the still-open remediation list |
| `memory-bank/*.md` | Durable engineering context: brief, product, patterns, toolchain, active state, progress |
| `.clinerules/memory-bank.md` | The protocol that keeps the memory bank current |
| `backend/knowledge-base/` | The grounding library the model is allowed to cite (also documented product content) |

Sync rules: a behaviour change lands in `README.md` and the memory bank; a compliance or copy change lands in the three legal documents **and** `legalConsent.ts` (bump the version) **and** the README; a fee/rule change lands in `PseBoardLotService`, `backend/knowledge-base/`, the README and the memory bank.

## Debugging (VS Code)

Committed in `.vscode/` (not gitignored):

| File | Contents |
| --- | --- |
| `launch.json` | `API: PseSignalDesk.WebApi` (`coreclr`, `preLaunchTask: backend: build`, `ASPNETCORE_URLS=http://localhost:5236`, `justMyCode: false`), `API: attach to running process`, `Terminal: Chrome (run npm run dev first)` (`type: chrome`, `webRoot` = `frontend/`), compound `Full stack (API + Chrome, Vite already running)` |
| `tasks.json` | `backend: build` (default build task), `backend: test`, **`backend: free port 5236`** (SIGTERM any listener on 5236 and re-check — the one-click version of the stale-`dotnet run` remedy below), `frontend: build`, `frontend: dev` (background, Vite problem matcher waiting for `Local: http://localhost:5173`) |

`justMyCode` is **false** on purpose. A client abort is normal control flow here: React
StrictMode remounts every screen once in development, switching horizon/date cancels the previous
load, and closing a dialog or tab cancels its fetch. Kestrel reports it through
`HttpContext.RequestAborted`, EF Core raises a `TaskCanceledException` while opening SQLite, and
`RequestCancellationMiddleware` answers a quiet `499`. Just My Code's "user-unhandled" stop fires
*before* that catch, so it would pause the debugger on every deliberate abort. With it off, the
debugger stops only on a truly unhandled exception. `App.tsx` additionally defers **both**
mount-time fetches (signals *and* the portfolio summary) by one macrotask, so StrictMode's
remount clears a timer instead of aborting a request — the common page-load case never reaches
the API at all.

## External data / services

- **Yahoo Finance v8 chart** (`{base}/v8/finance/chart/{symbol}?interval=1d&includePrePost=false&period1&period2`): `PSEI.PS` (regime), `PHP=X` (USD/PHP) and `{TICKER}.PS` for the ~58-name `PseTickerCatalog`. 15s per-request timeout; non-2xx/timeout/429 → 503; 404 → symbol skipped.
- **PSE Edge** (`https://edge.pse.com.ph/announcements/search.ax`, plus the best-effort `companyDisclosures/search.ax` listing, `DisclosurePagesToScan` pages each): scraped announcement titles matched to tracked companies. Non-2xx, empty page or timeout on the announcements page → 503 (disclosures are part of the live contract); the second listing only adds coverage.
- **Official PSE Daily Quotation Report**: the only source of net foreign flow. Fetched from `MarketData:PseDailyQuotationUrlTemplates` when configured (`{yyyy}` / `{MMddyyyy}` / `{MMddyy}` tokens) or supplied by an operator through `POST /api/signals/quotation-report`. Parsed by `PseDailyQuotationParser` (PDF text via PdfPig, CSV, TXT, TSV). Every failure is best-effort and leaves foreign flow `null`; provenance is stored in `ForeignFlowSource` (`pse-dqr-live` / `pse-dqr-upload`).
- **Yahoo Finance macro symbols** (`^TNX`, `BZ=F`, `EPHE`): fetched concurrently with the same chart client (`roundToTick: false`), best-effort. `^TNX` is a percent yield (20-day change reported in bps), `BZ=F` is USD per barrel, `EPHE` is the MSCI Philippines ETF in USD.
- **One download per URL per TTL.** `MarketDataCache` (singleton, in-memory, bounded at 200 entries) serves a chart body for 10 minutes and an Edge page for 5, keyed by the absolute URL, so the four horizons of one session, a replay and an on-demand research request share a single download (parsing still runs per call, so tick rounding and the volume filter stay per-caller). Failures are never cached. `POST /api/signals/run-now` calls `IMarketDataProvider.InvalidateCache()` first, so an operator-triggered run always sees the newest tape. The typed client uses a pooled `SocketsHttpHandler` with `AutomaticDecompression = All`.
- **Ollama Cloud**: paid chat completion API; required — no offline substitute. Three roles from one endpoint: `Model` ranks the book, `FastExtractionModel` produces the compact disclosure/trap note (optional, failures swallowed), `EmbeddingModel` feeds `POST /api/embed` for knowledge vectors (optional, falls back to lexical).
- Net foreign flow is only present for sessions with a parsed DQR; otherwise both columns are `null` and the UI renders `—` / `unreported`.
- All scraping is best-effort by nature; when the markup changes the desk fails loudly rather than degrading to synthetic data.

## Constraints and gotchas

- **All four horizons are live.** `DAY`/`WEEK`/`MONTH`/`YEAR` each get their own run, mandate (`HorizonProfile`) and drift chain.
- **Live-only, fail-fast.** No synthetic series or demo seed exists. `POST /api/signals/run-now` needs network access to Yahoo Finance + PSE Edge and `OLLAMA_API_KEY`; otherwise it returns `503`/`400` and persists nothing.
- **Foreign flow needs a DQR.** No public feed exposes per-ticker net foreign flow; the desk reads the official Daily Quotation Report only when a template is configured or an operator uploads one. Without it the columns stay `null`, the UI shows `—` / `unreported` and `ComputePreScore` rescales its 85 technical points.
- **Macro dials are context, not inputs to a verdict.** `^TNX` / `BZ=F` / `EPHE` can be missing or stale (older than `MaxSessionLagDays`) and report `null`; nothing gates on them.
- **The DQR upload never fabricates.** An unmatched ticker keeps its stored value, a file with no recognizable rows is a `400`, an unstored session is a `404`, and a successful re-save preserves the macro dials, FX, realized vol, rotation and breadth.
- **SQLite stores decimals as TEXT**; do not assume numeric ordering/SQL aggregation on decimal columns.
- **The market-data cache is a freshness window, not a source of truth.** Ten minutes for a chart body, five for an Edge page, in memory only (a restart is cold), and `POST /api/signals/run-now` clears it first. Nothing is fabricated and nothing is persisted, so a stale-cache bug can only serve a *real* older body, never a synthetic one.
- **Output equivalence is enforced by goldens.** `backtest_{DAY,WEEK,MONTH,YEAR}.json` and `quantitative_filter_metrics.json` are reviewed files, not generated artefacts to be regenerated casually: any indicator, plan-level or fee change must leave them byte-identical, or the diff must be explained in the commit. Regenerate only with `PSEDESK_UPDATE_GOLDEN=1`, then read the diff.
- **Tick rounding is bracket-sensitive.** Rounding can cross into a coarser tick bracket; always go through `PseBoardLotService` (15 tiers) rather than rounding ad hoc.
- **The static band only clamps `DAY`.** `NormalizeTradePlanWithinStaticLimits` keeps day plans inside `+50%/-30%` of the reference close; other horizons keep the plain tick/`target > entry > stop` normalization.
- **Retrieval is an enhancement, never a failure mode.** A missing embedding model, a dead Ollama embeddings call or an unreadable folder degrades to lexical + tag retrieval (or an ungrounded prompt) instead of failing the run.
- **Built-in knowledge documents are immutable from the API.** Edit the Markdown file and re-index; `DELETE /api/knowledge/{id}` returns `409` for `Source = "builtin"`.
- **Drift and drop classification are server-owned.** Model-supplied `drift_status` / `previous_rank` are persisted only inside the raw audit JSON, never used for the final item.
- **Citations are server-verified.** Only references present in the retrieved set survive into `CitedReferencesJson`.
- **Re-running a day replaces that day's run** for the same horizon. Previous-run lookup uses strictly earlier `RunDate` values.
- **Ticker matching is case-insensitive** everywhere (`OrdinalIgnoreCase`).
- **The scheduler is calendar-weekday based**, not a PSE holiday calendar.
- **No auth, no rate limiting, no HTTPS enforcement** in the MVP.
- **A leftover `dotnet run` steals port 5236 and kills the debug session.** Launching
  `API: PseSignalDesk.WebApi` while a terminal `dotnet run --project src/PseSignalDesk.WebApi
  --no-build` is still up fails with `System.IO.IOException: Failed to bind to address
  http://127.0.0.1:5236: address already in use.` (inner `SocketException`). The debugger
  instance is the victim, not the culprit — the stale terminal server (orphaned to PPID 1 after
  a window reload) keeps the port. Confirm with
  `ps -o pid,ppid,lstart,command -p <pid>` (PPID 1 + `dotnet run --launch-profile http --no-build`
  is the tell), then free it with `lsof -nP -iTCP:5236 -sTCP:LISTEN` and `kill <pid>` (SIGTERM to
  the app host; it exits within a few seconds after the hosted-service shutdown) — or run the
  `backend: free port 5236` task. `lsof` can list the dead socket for a few seconds after the
  process is gone, so confirm with `curl http://127.0.0.1:5236/health` (connection refused = free)
  or a real `bind()` before relaunching. The paused debuggee cannot recover from the bind failure:
  stop that debug session (Shift+F5), then press F5 again.
- **Client aborts are routine, not failures.** React StrictMode remounts (dev) and date/horizon
  switches cancel in-flight fetches; the API answers `499 ClientClosedRequest` through
  `RequestCancellationMiddleware`. The terminal defers its fetch by one macrotask so StrictMode
  cancels a timer rather than a live request, removing the usual trigger. A remaining deliberate
  mid-flight abort can still pause a debugger as Just My Code's "user-unhandled" stop — set
  `"justMyCode": false` in the API launch configuration to silence it.
- **One malformed brace fails the whole frontend build.** `@tailwindcss/vite` parses `src/index.css`
  itself and reports `CssSyntaxError: Missing opening {` (no line number) when the `@layer utilities`
  block is unbalanced — the fix is to check brace balance rather than hunt the reported file. Global
  `@keyframes` belong **outside** `@layer utilities`; the sheet animations live at the end of the file.
- **Server owns the odds and the verdict.** `WinProbability`, `BreakEvenWinRate`, `ExpectedValueR`, `QuarterKellyFraction`, `EdgeVerdict` and the fallback `BearCase` are computed in `ProbabilisticEdgeService`/`PseBoardLotService`; the frontend only formats them and converts Kelly to a peso budget. Do not recompute fees in TypeScript — `lib/riskSizing.ts` deliberately avoids duplicating `RoundTripFeeRate`.
- **Legacy runs look empty by design.** Rows written before `AddProbabilisticEdgeAndScorecard` carry zero odds, `WATCH_ONLY` and an empty bear case. They still resolve once their forward window fills (and are visible in the scorecard's realized stats + recent list) but are excluded from Brier and the calibration buckets. Re-run the pipeline (or a past date) to populate them.
- **Frontend `npm test` is `node:test` over pure modules, not a DOM suite.** It runs `node --test "src/lib/__tests__/*.test.ts"` on Node's native type stripping, so nothing there may import React or touch the DOM; `lib/queryCache.ts` is deliberately React-free for this reason. There is still no component/browser harness (`tsc --noEmit` + `npm run build` are the gate for UI).
- **Git is initialized on branch `main` and currently has three commits** (2026-11-25 initial, a portfolio/book-sizing refactor and a readability pass on 2026-11-26; HEAD `3e2aea4`), so everything under `backend/`, `frontend/`, `memory-bank/`, `docs`-level notices and `.clinerules/` is tracked.
- `.gitignore` covers build output (`**/bin/`, `**/obj/`, `.vs/`, `*.user`, `*.suo`,
  `*.sln.docstates` / `*.slnx.docstates`), test/coverage artefacts (`**/TestResults/`,
  `**/coverage/`, `*.trx`, `*.coverage`, `*.coveragexml`, `coverage*.xml`, `coverage*.json`,
  `coverage*.info`, `*.lcov`, `*.opencover.xml`, `*.binlog`), restored NuGet caches (`**/.nuget/`,
  `*.nupkg`, `*.snupkg`, `project.lock.json`), local secret overrides (`appsettings.*.local.json`,
  `*.pfx`, `*.snk`, `.npmrc`), local SDK/tool installs (`.dotnet/`), SQLite `*.db` + its
  `-shm`/`-wal`/`-journal` siblings, `*.sqlite`/`*.sqlite3` + their sidecars (`*.sqlite-*` /
  `*.sqlite3-*`) and **any** `*.db.bak*` safety copy, Node/Vite (`node_modules/`, `**/dist/`,
  `**/.vite/`, `vite.config.*.timestamp-*`, `*.tsbuildinfo`, `.eslintcache`, npm/yarn/pnpm/lerna
  debug logs), `.env` overrides (`.env`, `.env.local`, `.env.*.local`; `frontend/.env.example` stays
  tracked through the `!.env.example` negation), editor/OS noise (`.DS_Store`, `._*`, `.localized`,
  `__MACOSX/`, `.Spotlight-V100/`, `.Trashes/`, `.fseventsd/`, `.VolumeIcon.icns`, `Thumbs.db`,
  `Desktop.ini`, `$RECYCLE.BIN/`, `.idea/`, `*.iml`, `.fleet/`, `.history/`, `.vshistory/`,
  `_ReSharper*/`, `*.DotSettings.user`, swap/backup files), the stray per-project `.nuget/` package
  caches, the `/Library/` folder the .NET tools create when `HOME` resolves to the workspace root,
  and `*.log`/`*.pid`. `.vscode/` and `frontend/.env.example` stay tracked on purpose.
- Deliberately **not** copied from the github/gitignore `VisualStudio` / `Node` templates: VS
  designer/legacy C++/SharePoint/SQL-Server artefacts (this is a pure ASP.NET Core CLI solution),
  `[Dd]ebug/` + `[Rr]elease/` (they would also swallow a legitimately named folder; `bin/`+`obj/`
  already hold the output), `publish/` (default `dotnet publish` lands inside the ignored `bin/`),
  legacy `**/[Pp]ackages/*` (PackageReference only), blanket `*.pdb`/`*.dll`/`*.exe`, and the blanket
  `.env.*` (this repo keeps `.env.local` / `.env.*.local` local but would allow a deliberate
  `.env.production` to be committed).
- Two ways to verify: `git check-ignore -v <path>` shows which rule wins (2026-11-26: **5531 ignored entries reported, 230 tracked files**, none of them ignored); `git ls-files -ci --exclude-standard` must print nothing — it is the check that a new rule has not swallowed source files. This macOS checkout has `core.ignorecase=true`, so `Obj/`/`TestResults/` are matched by the lowercase rules too.


## Environment notes

- macOS (Darwin); `ManilaTimeZone` resolves cross-platform and falls back to fixed UTC+8.
- Relative SQLite paths are combined with `builder.Environment.ContentRootPath`, so launching from a different working directory still finds the DB next to the WebApi project.
- If `dotnet build` fails with `NETSDK1064: Package Microsoft.Extensions.Logging.Abstractions, version 10.0.0 was not found` while the package exists in `~/.nuget/packages`, the shell resolved a **relative** global-packages folder. Re-run with `NUGET_PACKAGES=$HOME/.nuget/packages dotnet build …` (this happened once in this workspace; `dotnet restore` alone did not fix it). That incident left ~389 MB of restored packages in `backend/.nuget/packages` plus a `.nuget/` folder in every project — both gitignored and safe to delete (NuGet re-restores from `~/.nuget/packages`).
- **Sandbox network notes (2026-11-25):** an early `curl` to
  `query1.finance.yahoo.com/v8/finance/chart/PSEI.PS` returned exactly **1** bar for every
  `range`/`period1..period2` request, and a `date=2026-11-25` run answered `503` "PSEi returned only
  1 live daily bars". A later run in the same sandbox fetched the full session (quant pass, breadth,
  knowledge retrieval all logged) and then stopped at `400 Ollama Cloud is not configured`, so the
  truncation is environment-dependent — check the log order before blaming the provider. No live
  Top-10 run has been observed here yet because `OLLAMA_API_KEY` is unset.
