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
| Tailwind CSS | `^4.3.5` | CSS-first via `@tailwindcss/vite`; no `tailwind.config`. Breakpoint tokens live in `@theme` in `index.css` (`--breakpoint-xs: 30rem`, `--breakpoint-3xl: 120rem`, `--breakpoint-4xl: 160rem`, on top of the defaults); container queries (`@container`, `@md:` …) come built in |
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
dotnet test PseSignalDesk.slnx                        # 439 passing, 1 skipped (the manual benchmark)

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
npm test                                             # node --test "src/lib/__tests__/*.test.ts" (60 passing)
npm run icons                                        # regenerate public/favicon.{svg,ico} + apple-touch-icon.png
npm run fonts                                        # re-download the bundled Outfit woff2 + OFL.txt
node scripts/fetch-fonts.mjs --check                 # print the upstream subset URLs without writing (offline → error)
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

# Feed health (what each upstream answered in the last run; no network of its own)
curl "http://localhost:5236/api/health/feeds"

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
| `OllamaCloud:FastExtractionModel` | `glm-5.3-flash` | Cheap disclosure/trap pre-pass; blank disables it. It must be a model the account's `/api/tags` actually lists — an unknown name answers HTTP `404`, which switches the pass off for the process (one warning, ranking unaffected) |
| `OllamaCloud:EmbeddingModel` | `nomic-embed-text` | Knowledge chunk embeddings; blank → lexical retrieval |
| `OllamaCloud:ApiKey` | empty | Unset → `400` on every run (no offline engine); kept in **user-secrets**, never committed |
| `OllamaCloud:RequestTimeoutSeconds` | `180` | HttpClient timeout (min 30 s). Raised from 120 when `think` became available: a reasoning call with a thinking model needs the headroom |
| `OllamaCloud:Temperature` | `0.1` | Ranking-call sampling temperature (decimal) |
| `OllamaCloud:Seed` | `42` | Fixed sampling seed where the provider honours it; null leaves provider seeding |
| `OllamaCloud:NumCtx` | `16384` | Context window requested. The 25-candidate prompt is ~8k tokens, so too small a window silently truncates the tail (yesterday's list, the TASK line) |
| `OllamaCloud:NumPredict` | `4096` | Completion cap; null = provider default |
| `OllamaCloud:TopP` | null | Nucleus cut-off; null = provider default (1.0) |
| `OllamaCloud:ReasoningEffort` | empty | `think` field: blank = omit, `off`/`false`/`none`/`disabled` → `false`, `on`/`true` → `true`, `low`/`medium`/`high`/`max` → forwarded as-is. **Unknown values are omitted rather than sent** so a typo cannot fail a live run |
| `OllamaCloud:UseStructuredOutput` | `true` | `format` carries the response JSON schema instead of `"json"`; an endpoint that answers `400` to the schema is retried once in JSON mode and the schema is latched off for the process |
| `KnowledgeBase:RootPath` | `knowledge-base` | Markdown library folder (content root, then `AppContext.BaseDirectory`) |
| `KnowledgeBase:MaxSnippets` | `6` | Sections injected per prompt (mandatory guardrails included) |
| `KnowledgeBase:MaxChunkCharacters` | `1600` | Section size cap; longer sections are split |
| `KnowledgeBase:EnableEmbeddings` | `true` | `false` = lexical-only indexing/retrieval (offline switch) |
| `KnowledgeBase:IndexOnStartup` | `true` | Seeds/skips the built-in library at boot |
| `MarketData:YahooFinanceBaseUrl` | `https://query1.finance.yahoo.com` | v8 chart origin |
| `MarketData:YahooFinanceFallbackBaseUrl` | `https://query2.finance.yahoo.com` | Mirror host retried once on a transport error/5xx (a `404` is host-independent and is not retried) |
| `MarketData:IndexYahooSymbols` | `["PSEI.PS"]` | Index candidates in preference order; the first with a usable multi-bar chart wins. Read through `MarketDataOptions.DistinctNonBlank`, so a repeated entry costs one request |
| `MarketData:IndexProxyConstituents` | 12 heavyweights (`TEL`, `SM`, `SMPH`, `ALI`, `BDO`, `BPI`, `ICT`, `JFC`, `MER`, `AC`, `URC`, `GLO`) | When no index symbol has history, the regime/breadth/vol is read from an equal-weight proxy of these closes; the proxy never prints an index level or a PSEi moving average. The binder **appends** configured entries to this default list (it does not replace it); `MarketDataOptions.Normalize()` de-duplicates, so listing the same names in `appsettings.json` is harmless and extra names extend the proxy |
| `MarketData:PseEdgeAnnouncementsUrl` | `https://edge.pse.com.ph/announcements/search.ax` | Disclosure page (fail-fast contract) |
| `MarketData:PseEdgeCompanyDisclosuresUrl` | `https://edge.pse.com.ph/companyDisclosures/search.ax` | Per-company disclosure table. Called with `?keyword={cmpy_id}` (the page's own search form field); without that query it answers a company-less market-wide listing |
| `MarketData:EnablePseEdgeHistory` | `true` | When a Yahoo chart carries no usable PSE series, read the daily bars from PSE Edge instead. `false` restores the strict Yahoo-only contract (and the old `503`) |
| `MarketData:PseEdgeChartUrl` | `https://edge.pse.com.ph/common/DisclosureCht.ax` | Edge daily-history POST (`{cmpy_id, security_id, startDate, endDate}` → `chartData[]`) |
| `MarketData:PseEdgeSymbolSearchUrl` | `https://edge.pse.com.ph/autoComplete/searchCompanyNameSymbol.ax` | `?term=TICKER` → `cmpyId` for the history source |
| `MarketData:PseEdgeCompanyPageUrl` | `https://edge.pse.com.ph/companyPage/stockData.do` | `?cmpy_id=N` → the common security's `security_id` |
| `MarketData:DisclosurePagesToScan` | `2` | Edge result pages scanned per listing (covers long weekends); extra pages best-effort |
| `MarketData:DisclosureCompanyFanoutLimit` | `25` | How many candidates get their own per-company disclosure page fetched each run, in pre-trap score order. `0` is the rollback: no per-company requests, only the market-wide attribution |
| `MarketData:PseDailyQuotationUrlTemplates` | `[]` | Date-addressed DQR sources (`{yyyy}`, `{MMddyyyy}`, `{MMddyy}`); empty = rely on the upload endpoint, foreign flow stays `null` |
| `MarketData:Us10YahooSymbol` / `BrentYahooSymbol` / `EpheYahooSymbol` | `^TNX` / `BZ=F` / `EPHE` | Cross-asset macro dials (best-effort; a missing series leaves the tile `—`) |
| `MarketData:LookbackCalendarDays` | `420` | Calendar lookback per chart request (~280 sessions) |
| `MarketData:MaxConcurrency` | `8` | Parallel ticker requests |
| `MarketData:RequestTimeoutSeconds` | `15` | Per-request timeout; a timeout is a 503 |
| `Cors:AllowedOrigins` | `http://localhost:5173` | Frontend origin for CORS **and** the write-origin allowlist (`ComplianceGuardMiddleware` refuses a mutating request from any other origin with `403`) |
| `AllowedHosts` | `localhost;127.0.0.1;[::1]` | Host-header allowlist (no wildcard, so a forged `Host:` cannot resolve) |
| `Compliance:DeploymentMode` | `PersonalLocal` | `PersonalLocal` refuses non-loopback callers with `403`; `LicensedCommercial` opens that check and requires the operator key on writes. Unrecognised values fall back to personal |
| `Compliance:EnforceLoopbackInPersonalMode` | `true` | The loopback refusal switch; only a deliberate `false` disables it |
| `Compliance:OperatorApiKey` | empty | Shared key required on `POST`/`PUT`/`PATCH`/`DELETE` when set; blank is fine in personal mode and a `503` fail-closed in licensed mode |
| `Compliance:AllowedHosts` | empty | Licensed mode only: replaces the loopback `AllowedHosts` so a deployment under a real host name is reachable; ignored in `PersonalLocal`, so it can never widen the personal desk |
| `Compliance:ExpensiveRequestsPerHour` | `30` | One fixed one-hour window over `run-now` / `backtest` / `train` / `train-all`; `429` + `Retry-After` beyond it. Clamped 1–1000 |
| `Compliance:ResearchRequestsPerHour` | `120` | One fixed one-hour window over `GET /api/research/{symbol}` (each call fans out to Yahoo / PSE Edge); `429` + `Retry-After` beyond it. Clamped 1–10000 |
| `Compliance:KnowledgeRequestsPerHour` | `10` | One fixed one-hour window over `POST /api/knowledge/upload` and `/reindex` (each accepted upload spends Ollama embedding credit); `429` + `Retry-After` beyond it. Clamped 1–1000 |
| `MarketData:UserAgent` | `PseSignalDesk-PersonalResearch/0.1 (local-noncommercial)` | Outbound identity for Yahoo and PSE Edge — never a spoofed browser string |
| `PSEDESK_OPERATOR_API_KEY` | — | Environment override for `Compliance:OperatorApiKey`, so the key never sits in `appsettings.json` |
| `OLLAMA_API_KEY` / `OLLAMA_BASE_URL` / `OLLAMA_MODEL` / `OLLAMA_FAST_MODEL` / `OLLAMA_EMBEDDING_MODEL` | — | Environment variables override settings |


### Frontend

| Variable | Default | Purpose |
| --- | --- | --- |
| `VITE_API_BASE_URL` | `http://localhost:5236` | API base URL (trailing slashes stripped) |
| `VITE_OPERATOR_API_KEY` | empty | Shared `X-PseDesk-Key` for a `LicensedCommercial` API, sent on `POST`/`PUT`/`PATCH`/`DELETE` only (`lib/operatorKey.ts`). Blank in personal mode, where writes need no key. Not per-user identity — a bundle-shipped key is readable by anyone who loads the page |

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
- Migrations: `20261124150948_InitialCreate`, `20261124231758_MakeNetForeignNullable`, `20261124233850_AddKnowledgeBaseAndMultiHorizon` (adds `KnowledgeDocuments`/`KnowledgeChunks`, `StockDailyMetrics.Sma150`/`Week52High`/`WeinsteinStage` and `SignalItems.BoardLotShares`/`CitedReferencesJson`) `20261125010058_AddProbabilisticEdgeAndScorecard` (adds the odds/verdict/bear-case/outcome columns, run-level cash posture + breadth, snapshot breadth, metric OHLC/RS/VCP/ATR%/hit-rate columns and the `SignalItems (Horizon, OutcomeStatus)` index; defaults keep legacy rows at `WATCH_ONLY` / `OPEN` / `0`), `20261125025142_AddPortfolioResearchAndDividends` (adds `PortfolioAccounts` / `PortfolioTrades` with their indexes and `StockDailyMetrics.TrailingDividendPerShare` / `TrailingDividendYieldPct` / `LastExDividendDate`), `20261125035931_AddMoneyManagerEngine` (adds the factor columns `UpDownVolumeRatio20d` / `ChaikinMoneyFlow20d` / `TrendEfficiency63d` / `Beta252d` / `MaxDrawdown252dPct` / `EmpiricalWinRateDay` / `EmpiricalWinRateMonth` / `EmpiricalWinRateYear` onto `StockDailyMetrics` (realised volatility is **not** stored per name — `QuantitativeFilterService.ComputeRealizedVolatilityPct` computes it on demand), the governance columns `PeakEquityPhp` / `MaxSectorExposurePct` / `MaxPositionsPerSector` / `SoftDrawdownThresholdPct` / `HardDrawdownThresholdPct` / `TargetAnnualVolPct` onto `PortfolioAccounts`, and `UsdPhpSma50` / `UsdPhpChange20dPct` / `PseiRealizedVol20dPct` / `SectorRotationJson` onto `DailyMarketSnapshots`; existing rows read `null` = unreported), and `20261125045357_AddCrossAssetMacroAndForeignFlowDqr` (adds `Us10YClosePct` / `Us10YChange20dBps` / `BrentUsd` / `BrentChange20dPct` / `EpheCloseUsd` / `EpheChange20dPct` / `ForeignFlowSource` onto `DailyMarketSnapshots`; nullable = unreported, `NetForeignTodayPhp` already existed on `StockDailyMetrics`), and `20261125060730_AddTrainedMoneyManagerModels` (adds `TrainedHorizonModels` with a unique `Horizon` index; the JSON weight/stat blobs are `TEXT` and the scores are `TEXT` decimals, exactly like the rest of the SQLite schema), and `20261125092954_AddPyramidCountToPortfolioTrades` (adds `PortfolioTrades.PyramidCount` as `INTEGER` default 0, so a book written before the pyramid layer reads "no adds taken"), and `20261125212733_AddRobustLiquidityColumns` (adds `StockDailyMetrics.MedianTurnover20dPhp` `TEXT` precision 18,2 default `0` and `ActiveTradingDays20d` `INTEGER` default `0`; a legacy row reads `0`/`0`, which `MeetsLiquidityFloor` interprets as "median not measured" and falls back to the mean), and `20261125214041_AddLiveDriftState` (adds `TrainedHorizonModels.LiveDrift` `TEXT` max 16 default `'STABLE'` and `LiveDriftGradedPicks` `INTEGER` default `0`; the `'STABLE'` default is deliberate — an empty-string default would make EF's string-to-enum conversion throw on every pre-existing row), and `20261126124346_AddSignalRankingProvenance` (adds the 12 run-provenance columns `EngineName`/`ModelName` `TEXT` 128, `PromptHash` `TEXT` 64, `ModelTemperature` `TEXT` decimal, `ModelSeed`/`PromptTokens`/`CompletionTokens` nullable `INTEGER`, `ModelRetryCount`/`ModelLatencyMs`/`PromptCharacters` `INTEGER` default 0, `SchemaConstrained`/`FastExtractionApplied` `INTEGER` default false, and `SignalItems.QuantPreScore` `INTEGER` default 0 + `QuantRank` nullable `INTEGER`; every default means "unmeasured", so a legacy row never claims provenance or a quant rank it never had).
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

`LICENSE`, `DISCLAIMER.md`, `PRIVACY.md` and `THIRD_PARTY_NOTICES.md` live at the repository root (and are linked from `README.md`). `frontend/src/lib/legalConsent.ts` mirrors their reader-facing summary; bumping `CURRENT_LEGAL_VERSION` there re-prompts every browser through `LegalDisclaimerGate`. Keep the four documents, that summary and the README's compliance paragraph in sync when any of them changes.

`LICENSE` is deliberately **not** open source: one personal, non-commercial copy on a machine you control, no redistribution or third-party hosting, "as is" with a liability cap. It is the only place the warranty/liability terms bind someone who merely copies the code, so do not replace it with an SPDX identifier (MIT/Apache) without also reworking `DISCLAIMER.md` §8 and the README.

## Documentation map

| Document | Audience / purpose |
| --- | --- |
| `README.md` | The user-facing manual: what the desk does, how to run it, the API table, tests and limitations |
| `LICENSE` | The grant: one personal non-commercial copy, no redistribution or third-party hosting, "as is" + liability cap. Not open source |
| `DISCLAIMER.md` / `PRIVACY.md` / `THIRD_PARTY_NOTICES.md` | Legal posture, data handling and licences (linked from the README and mirrored by the first-run gate) |
| `security_audit_report.md` | The 2026-11-26 read-only audit (OWASP ASVS / API Top 10 / LLM Top 10) with the still-open remediation list |
| `memory-bank/*.md` | Durable engineering context: brief, product, patterns, toolchain, active state, progress |
| `.clinerules/memory-bank.md` | The protocol that keeps the memory bank current |
| `backend/knowledge-base/` | The grounding library the model is allowed to cite (also documented product content) |

Sync rules: a behaviour change lands in `README.md` and the memory bank; a compliance or copy change lands in `LICENSE` + the three notices **and** `legalConsent.ts` (bump the version) **and** the README; a fee/rule change lands in `PseBoardLotService`, `backend/knowledge-base/`, the README and the memory bank.

## Debugging (VS Code)

Committed in `.vscode/` (not gitignored):

| File | Contents |
| --- | --- |
| `launch.json` | `API: PseSignalDesk.WebApi` (`coreclr`, `preLaunchTask: backend: build`, `ASPNETCORE_URLS=http://localhost:5236`, `justMyCode: false`), `API: attach to running process`, `Frontend: Vite dev server (frees :5173 first)` (`pwa-node`, `runtimeExecutable: npm`, `runtimeArgs: ["run","dev"]`, `preLaunchTask: frontend: free port 5173`, `serverReadyAction` → `debugWithChrome` on `Local: http://localhost:5173`), `Terminal: Chrome (run npm run dev first)` (`type: chrome`, `webRoot` = `frontend/`), compound `Full stack (API + Chrome, Vite already running)` |
| `tasks.json` | `backend: build` (default build task), `backend: test`, **`backend: free port 5236`** (SIGTERM any listener on 5236, poll up to 6s, then **SIGKILL** and re-check, ending with a `curl /health` proof — the one-click version of the stale-`dotnet run` remedy below), `frontend: build`, `frontend: dev` (background, Vite problem matcher waiting for `Local: http://localhost:5173`), **`frontend: free port 5173`** (same SIGTERM → 6s poll → SIGKILL escalation, ending with a `curl http://localhost:5173/` probe — needed because `vite.config.ts` pins `strictPort: true`) |

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

- **Yahoo Finance v8 chart** (`{base}/v8/finance/chart/{symbol}?interval=1d&includePrePost=false&period1&period2`): `PSEI.PS` (regime), `PHP=X` (USD/PHP), `{TICKER}.PS` for the ~58-name `PseTickerCatalog` and the three macro dials. 15s per-request timeout; non-2xx/timeout/429 → 503; 404 → symbol skipped. **Yahoo no longer publishes PSE equities** (2026-11-26: every `{TICKER}.PS` answers `404`, `PSEI.PS` keeps one legacy quarterly series plus a live `regularMarketPrice`), so in practice every PSE series comes from the PSE Edge history source below and `PHP=X` / `^TNX` / `BZ=F` / `EPHE` are the only live Yahoo series left.
- **PSE Edge daily history** (`PseEdgeHistorySource`, three calls per symbol): `autoComplete/searchCompanyNameSymbol.ax?term=TICKER` → `cmpyId`; `companyPage/stockData.do?cmpy_id=N` → the common security's `security_id` (exact symbol match wins over the preselected option, else the page's own `sendData.security_id`); `POST common/DisclosureCht.ax` with `{cmpy_id, security_id, startDate, endDate}` → `chartData[{OPEN,HIGH,LOW,CLOSE,VALUE,CHART_DATE}]` back three to five years. Ten-minute `MarketDataCache` TTL per call, per-request timeout, failures returned as a reason string (never thrown). Rows are de-duplicated per session (last print wins), clamped to the requested window, and a row without a usable OHLC print is skipped. **Edge reports peso `VALUE`, not a share count**, so volume is `VALUE / ((HIGH+LOW+CLOSE)/3)` — within ~0.01% of the exchange's own print, and it keeps `Close × Volume` equal to the official turnover the PHP 5M floor is measured on.
- **PSE Edge disclosures** — two reads, one attribution rule (`cmpy_id`, never a headline match):
  - **Market-wide** (`https://edge.pse.com.ph/announcements/search.ax`, plus the company-less `companyDisclosures/search.ax` listing, `DisclosurePagesToScan` pages each): scraped rows matched to tracked companies. Non-2xx, empty page or timeout on the announcements page → 503 (disclosures are part of the live contract). As of 2026-11-26 the announcements listing answers `200` with a "no records found" body, and this market-wide read attributed filings to **3 of 60** names in a live run — a subject line usually does not repeat the issuer's name, so this is coverage-by-luck and is kept only as the fallback.
  - **Per company** (`PseEdgeDisclosureSource`, route settled by live probe on 2026-11-26): `GET companyDisclosures/search.ax?keyword={cmpy_id}` → 50-row table of *that* company's filings (`Total 424` for JFC). The planned `companyPage/companyDisclosures.do?cmpy_id=86` route is **HTTP 404**; `?keyword=` is the field the page's own search form posts. Ticker → id via `autoComplete/searchCompanyNameSymbol.ax?term=TICKER` (`EdgeSymbolSearch.ReadCompanyId`, shared with the history source). Announce cells print `Nov 26, 2026 11:53 AM` — the parser accepts that *and* `11/26/2026`, and a row the parser cannot date is dropped rather than back-dated to the session. A company with no search hit is a **skip**, never a failure. Both responses cached ten minutes (`edge-symbol:{ticker}`, `edge-company-disclosures:{id}`); failures are soft (`MarketDataUnavailableException`/`JsonException` → empty map) because the market-wide read still stands.
  - Live comparison, same run (2026-11-25 session): fan-out over four names returned 41 (BDO) / 47 (JFC) / 22 (SM) / 40 (TEL) filings with real dates; the market-wide page attributed 3 filings across 60 names. That gap is the trap pillar.

- **Official PSE Daily Quotation Report**: the only source of net foreign flow. Fetched from `MarketData:PseDailyQuotationUrlTemplates` when configured (`{yyyy}` / `{MMddyyyy}` / `{MMddyy}` tokens) or supplied by an operator through `POST /api/signals/quotation-report`. Parsed by `PseDailyQuotationParser` (PDF text via PdfPig, CSV, TXT, TSV). Every failure is best-effort and leaves foreign flow `null`; provenance is stored in `ForeignFlowSource` (`pse-dqr-live` / `pse-dqr-upload`).
- **Yahoo Finance macro symbols** (`^TNX`, `BZ=F`, `EPHE`): fetched concurrently with the same chart client (`roundToTick: false`), best-effort. `^TNX` is a percent yield (20-day change reported in bps), `BZ=F` is USD per barrel, `EPHE` is the MSCI Philippines ETF in USD.
- **One download per URL per TTL.** `MarketDataCache` (singleton, in-memory, bounded at 200 entries) serves a chart body for 10 minutes and an Edge page for 5, keyed by the absolute URL, so the four horizons of one session, a replay and an on-demand research request share a single download (parsing still runs per call, so tick rounding and the volume filter stay per-caller). Failures are never cached. `POST /api/signals/run-now` calls `IMarketDataProvider.InvalidateCache()` first, so an operator-triggered run always sees the newest tape. The typed client uses a pooled `SocketsHttpHandler` with `AutomaticDecompression = All`.
- **Ollama Cloud**: paid chat completion API; required — no offline substitute. Three roles from one endpoint: `Model` ranks the book, `FastExtractionModel` produces the compact disclosure/trap note (optional, failures swallowed; a `404` for the model name switches the pass off for the process), `EmbeddingModel` feeds `POST /api/embed` for knowledge vectors (optional, falls back to lexical). The endpoint serves only the models the account's `GET /api/tags` lists (25 on 2026-11-26: `deepseek-v4.1-flash`, `glm-5.3-flash`, `mimo-v2.6-flash`, `minimax-m3.1-flash`, …); `qwen3.5:4b` was never among them and answered `404`, and the `:cloud` suffix in the provider's error hint is a CLI alias — `/api/chat` takes the bare name.
- Net foreign flow is only present for sessions with a parsed DQR; otherwise both columns are `null` and the UI renders `—` / `unreported`.
- All scraping is best-effort by nature; when the markup changes the desk fails loudly rather than degrading to synthetic data.

## Constraints and gotchas

- **All four horizons are live.** `DAY`/`WEEK`/`MONTH`/`YEAR` each get their own run, mandate (`HorizonProfile`) and drift chain.
- **Live-only, fail-fast.** No synthetic series or demo seed exists. `POST /api/signals/run-now` needs network access to Yahoo Finance + PSE Edge and `OLLAMA_API_KEY`; otherwise it returns `503`/`400` and persists nothing.
- **Foreign flow needs a DQR.** No public feed exposes per-ticker net foreign flow; the desk reads the official Daily Quotation Report only when a template is configured or an operator uploads one. Without it the columns stay `null`, the UI shows `—` / `unreported` and `ComputePreScore` rescales its 85 technical points.
- **Macro dials are context, not inputs to a verdict.** `^TNX` / `BZ=F` / `EPHE` can be missing or stale (older than `MaxSessionLagDays`) and report `null`; nothing gates on them.
- **The DQR upload never fabricates.** An unmatched ticker keeps its stored value, a file with no recognizable rows is a `400`, an unstored session is a `404`, and a successful re-save preserves the macro dials, FX, realized vol, rotation and breadth.
- **Configuration collections are additive, not replacing.** `ConfigurationBinder` binds an `IList<T>` property by **adding** the section's entries to the instance the class already initialized, so `MarketDataOptions` would end up with the code defaults *plus* the `appsettings.json` list (12 → 24 constituents, `PSEI.PS` twice) — which is exactly the "0 of 24" a failed run used to print. `MarketDataOptions.Normalize()` trims, drops blanks and de-duplicates those lists (case-insensitive, first spelling wins, order preserved); `Program.cs` runs it in a `PostConfigure` on the `MarketData` options, and `PseMarketDataProvider` normalises again in its constructor so a hand-built `Options.Create(...)` behaves the same. Never read `IndexYahooSymbols` / `IndexProxyConstituents` raw.
- **A failing proxy names every constituent once, in configured order.** `FetchProxyConstituentBarsAsync` returns a `ProxyConstituentFetch(bars, reason)` per name and appends the reasons after `Task.WhenAll` — the old code had 24 tasks writing one `List<string>`, which duplicated and shuffled the `503` detail. `JoinReasons` trims each reason's own period before the message adds one.
- **SQLite stores decimals as TEXT**; do not assume numeric ordering/SQL aggregation on decimal columns.
- **The market-data cache is a freshness window, not a source of truth.** Ten minutes for a chart body, five for an Edge page, in memory only (a restart is cold), and `POST /api/signals/run-now` clears it first. Nothing is fabricated and nothing is persisted, so a stale-cache bug can only serve a *real* older body, never a synthetic one.
- **Cache eviction must tolerate a concurrent `Clear()`.** Enumerating the bounded map for oldest-`CreatedAt` eviction while another thread runs `Clear()` (the run-now path) can hand the snapshot an entry whose value is already gone on .NET 10, and dereferencing it faulted the request with a `NullReferenceException` mid-session. `EvictOldest` filters a missing value out before sorting, and `MarketDataCacheTests.GetOrAddAsync_evicts_safely_while_another_caller_clears_the_map` reproduces the race (it failed in ~130 ms before the fix).
- **The Edge history is a fallback, never an invention.** `WithEdgeHistoryAsync` only replaces a Yahoo chart that is unavailable or has fewer than the required bars, and only with real Edge sessions inside the requested window; a bar without a usable OHLC print is dropped rather than carried forward. When Edge also falls short, the reason says which feed failed and how short the series was (`… PSE Edge: 160 bar(s), newest 2026-06-01.`) instead of a blank. `MarketData:EnablePseEdgeHistory = false` restores the strict Yahoo-only contract.
- **Derived volume is the one computed field on an Edge bar.** `VALUE / ((HIGH+LOW+CLOSE)/3)` — no share count exists upstream. Everything downstream (turnover, volume ratios, `Close × Volume`) is consistent because it is derived from the same official peso value.
- **Feed health is per-process, run-scoped and never persisted.** `FeedHealthRegistry` (singleton, in memory) backs `GET /api/health/feeds`; `BeginRun()` in `FetchAsync` clears the five tape feeds' counters so the report describes the last session, while its timestamps and the embeddings counters survive. A restart therefore reports `Unknown` (never a borrowed `Ok`) until the first fetch. Recordings happen inside the parse seam, so a cache hit still counts as an observation; and because 58 symbols are fetched concurrently the status is derived from counters (`failures && !successes` → `Failed`, any failure or degraded observation → `Degraded`), never from whichever download finished last. `Skip` deliberately covers "Edge lists no security" and a Yahoo `404`; a feed that only ever skips is `Degraded`.
- **Output equivalence is enforced by goldens.** `backtest_{DAY,WEEK,MONTH,YEAR}.json` and `quantitative_filter_metrics.json` are reviewed files, not generated artefacts to be regenerated casually: any indicator, plan-level or fee change must leave them byte-identical, or the diff must be explained in the commit. Regenerate only with `PSEDESK_UPDATE_GOLDEN=1`, then read the diff.
- **Tick rounding is bracket-sensitive.** Rounding can cross into a coarser tick bracket; always go through `PseBoardLotService` (15 tiers) rather than rounding ad hoc.
- **The static band only clamps `DAY`.** `NormalizeTradePlanWithinStaticLimits` keeps day plans inside `+50%/-30%` of the reference close; other horizons keep the plain tick/`target > entry > stop` normalization.
- **An unknown Ollama model name is a configuration error, not a run failure.** `OllamaSignalEngine.SendChatAsync` now raises an internal `ChatResponseException` carrying the status code: the ranking path re-wraps it as `SignalGenerationException` (the `502` contract is unchanged), while the optional fast-extraction pass reads `404` as "this endpoint does not serve that model" and, after one warning naming the setting and the reasoning model, latches the pass off for the life of the engine so the four horizons stop repeating the call. Verified 2026-11-26 against the live account: `GET /api/tags` lists 25 models, `qwen3.5:4b` → `404 "model … not found"`, `deepseek-v4.1-flash` / `glm-5.3-flash` / `mimo-v2.6-flash` → `200`, and `glm-5.3-flash` returned a valid `{summary, trap_flags}` object for the fast prompt. `POST /api/embed` answered `401 unauthorized` for `nomic-embed-text` *and* for a chat model with the same key, so embeddings stay unavailable (retrieval is lexical) until the account has embed access.
- **Retrieval is an enhancement, never a failure mode.** A missing embedding model, a dead Ollama embeddings call or an unreadable folder degrades to lexical + tag retrieval (or an ungrounded prompt) instead of failing the run.
- **Built-in knowledge documents are immutable from the API.** Edit the Markdown file and re-index; `DELETE /api/knowledge/{id}` returns `409` for `Source = "builtin"`.
- **Drift and drop classification are server-owned.** Model-supplied `drift_status` / `previous_rank` are persisted only inside the raw audit JSON, never used for the final item.
- **Citations are server-verified.** Only references present in the retrieved set survive into `CitedReferencesJson`.
- **Re-running a day replaces that day's run** for the same horizon. Previous-run lookup uses strictly earlier `RunDate` values.
- **Ticker matching is case-insensitive** everywhere (`OrdinalIgnoreCase`).
- **The scheduler is calendar-weekday based**, not a PSE holiday calendar.
- **No multi-user auth, and no HTTPS enforcement.** There is no per-user login; the loopback
  refusal (`PersonalLocal`), the shared operator key (`LicensedCommercial`) and the write-origin
  allowlist are the whole access story. A mutating request from an origin outside
  `Cors:AllowedOrigins` (and the API's own host) is `403`; five wrong keys in five minutes lock a
  caller out with `429` + `Retry-After`; and the costly routes carry three hourly budgets
  (`Expensive`, `Upstream`, `Model`), with `lookbackDays` capped at 1,825 days and knowledge
  uploads restricted to `.md`/`.markdown`/`.txt`/`.pdf`. Keep the desk on `127.0.0.1`: there is no
  `ForwardedHeaders` trust, so fronting a `PersonalLocal` desk with a reverse proxy on the same
  machine would make every request look loopback. A real multi-user deployment needs OIDC, TLS and
  security headers first (`security_audit_report.md` F-01/F-02/F-08).
- **A leftover Vite server steals port 5173 and kills the debug launch.** `vite.config.ts` sets
  `strictPort: true`, so a second `npm run dev` (or the `Frontend: Vite dev server` launch) does not move to
  5174 — it dies with `error when starting dev server: Error: Port 5173 is already in use` from
  `httpServerStart`, and a debug session then hangs on *"Waiting for the debugger to disconnect"*. The
  leftover is usually an `npm run dev` **orphaned to PPID 1** when its terminal or debug session closed: in
  the 2026-11-26 case a `node` child (PPID = a PPID-1 `npm run dev`, 45 min old) held `[::1]:5173` and still
  answered `200` on `/`. Confirm with `lsof -nP -iTCP:5173 -sTCP:LISTEN` plus
  `ps -o pid,ppid,stat,lstart,command -p <pid>` (PPID 1 is the tell), then run the
  **`frontend: free port 5173`** task. Killing the *listener* is enough — the orphaned `npm` parent exits with
  its child. Verify with `curl http://localhost:5173/` (refused = free) rather than `lsof`, which can list a
  dead socket for a few seconds.
- **A leftover API host steals port 5236 and kills the debug session.** Launching
  `API: PseSignalDesk.WebApi` while a previous host is still up fails with `System.IO.IOException:
  Failed to bind to address http://127.0.0.1:5236: address already in use.` (inner
  `SocketException`). The debugger instance is the victim, not the culprit — the leftover is either a
  terminal `dotnet run --project src/PseSignalDesk.WebApi --no-build` or a **debuggee apphost**
  (`bin/Debug/net10.0/PseSignalDesk.WebApi`, no `dotnet run` parent) orphaned to PPID 1 after a
  window reload or a killed debug session. Confirm with `ps -o pid,ppid,stat,lstart,command -p <pid>`
  (PPID 1 is the tell), then free it with
  `lsof -nP -iTCP:5236 -sTCP:LISTEN` + `kill -TERM <pid>` — or just run the `backend: free port 5236`
  task. **A hung host may ignore SIGTERM**: observed 2026-11-26 with a 3h55m-old orphan that accepted
  TCP connections but never answered `GET /health` (curl timed out with 0 bytes) and was still
  holding both `127.0.0.1:5236` and `[::1]:5236` three seconds after SIGTERM, so graceful shutdown
  could not complete. Escalate to `kill -9` and re-check; the task now does that automatically.
  `lsof` can list the dead socket for a few seconds after the process is gone, so confirm with
  `curl http://127.0.0.1:5236/health` (connection refused = free) or a real `bind()` before
  relaunching. The paused debuggee cannot recover from the bind failure: stop that debug session
  (Shift+F5), free the port, then press F5 again.
- **`--no-launch-profile` does not mean "use port 5236".** `launch.json` supplies the address through
  `ASPNETCORE_URLS`; without the launch profile *and* without that variable the host falls back to
  `http://localhost:5000`. Reproducing a debug launch from a terminal means
  `ASPNETCORE_URLS=http://localhost:5236 ASPNETCORE_ENVIRONMENT=Development dotnet run --project
  src/PseSignalDesk.WebApi --no-launch-profile --no-build` (verified 2026-11-26: `Now listening on:
  http://localhost:5236`, `GET /health` → `200`).
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
- **Tailwind v4 emits range syntax, and container queries need a named ancestor.** A built stylesheet
  contains `@media (width>=30rem)`, not `min-width: 30rem`, so grepping the dist CSS for the old form
  reports a false negative. Container variants (`@md:grid-cols-2`) resolve against the nearest
  `@container` ancestor: `DialogContent`, the Simple signal-row panel and the drop panel are deliberately
  declared as
  container roots, so a tile grid inside them measures the sheet, not the window. A grid using an `@`
  variant with no container ancestor silently falls back to its base column count.
- **Tailwind v4 ships no `cursor: pointer` on buttons.** v3's preflight set it on `button, [role="button"]`; v4 removed the rule and lets the browser default stand, so without a global rule every `<button>`, Radix trigger and `<summary>` renders an arrow and only hints at being clickable through its hover tint. `src/index.css` carries one `@layer base` rule — `button:not(:disabled)`, `[role="button"|"tab"|"option"|"menuitem"]:not([aria-disabled="true"])`, `a[href]`, `summary` — so the hand has a single source of truth instead of 100+ call-site classes. Because it lives in `base`, a utilities-layer class still wins (`disabled:cursor-not-allowed`, `data-disabled:cursor-not-allowed`, or a deliberate `cursor-default`), which is also why a clickable non-button (`SignalTable`'s `<tr>`, `ProSignalCard`'s `<li>`) keeps its own `cursor-pointer`. Verified 2026-11-26 by computed style in headless Chrome against the built app: 19 rendered controls → `pointer`, the disabled trading-day picker → `not-allowed`.
- **A `hidden` utility loses to a breakpoint display utility on the same element.** `cn()` is a plain
  joiner, so `'flex … sm:inline-flex'` + `'hidden md:flex'` is three competing display rules and the
  later media query wins — which is how a control that must stay hidden below `md` reappears at `sm`.
  Put the visibility class on a wrapper (`<div className="hidden md:block">`) instead.
- **Server owns the odds and the verdict.** `WinProbability`, `BreakEvenWinRate`, `ExpectedValueR`, `QuarterKellyFraction`, `EdgeVerdict` and the fallback `BearCase` are computed in `ProbabilisticEdgeService`/`PseBoardLotService`; the frontend only formats them and converts Kelly to a peso budget. Do not recompute fees in TypeScript — `lib/riskSizing.ts` deliberately avoids duplicating `RoundTripFeeRate`.
- **Legacy runs look empty by design.** Rows written before `AddProbabilisticEdgeAndScorecard` carry zero odds, `WATCH_ONLY` and an empty bear case. They still resolve once their forward window fills (and are visible in the scorecard's realized stats + recent list) but are excluded from Brier and the calibration buckets. Re-run the pipeline (or a past date) to populate them.
- **Frontend `npm test` is `node:test` over pure modules, not a DOM suite.** It runs `node --test "src/lib/__tests__/*.test.ts"` on Node's native type stripping, so nothing there may import React or touch the DOM; `lib/queryCache.ts` is deliberately React-free for this reason. There is still no component/browser harness (`tsc --noEmit` + `npm run build` are the gate for UI).
- **The icon files are generated, never hand-edited.** `npm run icons` runs `frontend/scripts/generate-icons.mjs`, which imports `src/lib/deskMarkGeometry.ts` directly (Node 24 strips the types; no build step, no ts-node), supersamples the same rounded rects into RGBA, deflates the scanlines with `node:zlib` and writes the PNG, the ICO container and the SVG by hand — **no image library, no new dependency**. Editing `public/favicon.*` by hand is a bug: the next `npm run icons` overwrites it. `--preview <path>` dumps a 512px PNG for eyeballing. ICO entries are PNG-compressed (fine for Vista+), and the 180px touch icon is deliberately a full-bleed opaque square because iOS applies its own squircle mask and paints transparent corners black.
- **The bundled typeface is vendored, and its coverage was measured.** `frontend/public/fonts/outfit-latin.woff2` (variable `100 900`, 32,228 bytes) is referenced by one `@font-face` in `src/index.css` and preloaded by `index.html`; `npm run fonts` re-downloads it plus `OFL.txt` from the Google Fonts API (it verifies the `wOF2` signature, so an HTML error page can never overwrite the binary). **No `fonts.googleapis.com` link may be added** — the desk must load with nothing but the API on loopback, and a CDN request would leak the reader's address (`PRIVACY.md` §5). Measured 2026-11-26 in headless Chrome: the app's Latin copy, digits and punctuation come from the bundled face (advance width 27.84 px for `A` at 40 px vs 25.78 for the system stack; `document.fonts` reports exactly one face, `Outfit loaded`), while `₱` (U+20B1), `▲`/`▼` (U+25B2/U+25BC) and `→` resolve through the **system** font. The upstream `Outfit[wght].ttf` cmap was parsed for that: 360 glyphs, no U+20B1, no U+25B2/U+25BC; `→` exists in the font but the served `latin` `unicode-range` omits it, and Outfit's `latin-ext` sheet *claims* U+20B1 inside U+20AD-20C4 without carrying the glyph (14,760 bytes for nothing). That is why only `latin` ships; do not "fix" the peso sign by bundling more subsets without re-running that measurement.
- **Headless Chrome can capture the built app — over `http://`, with the legal gate seeded.** The reliable recipe (used 2026-11-26 to check the typeface at 1280px and 500px): `cp -R dist/. /tmp/preview/`, insert an inline script in the copied `index.html` that sets `localStorage['psedesk.legalAcceptedVersion']` (the current `CURRENT_LEGAL_VERSION`), `psedesk.welcomeDismissed`, `psedesk.theme` and `psedesk.viewMode`, serve it with `python3 -m http.server <port> --bind 127.0.0.1`, then
  `"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --disable-gpu --no-sandbox --hide-scrollbars --window-size=1280,940 --virtual-time-budget=9000 --screenshot=/tmp/app.png http://127.0.0.1:<port>/`.
  It exits `0` in ~3 s and writes the PNG; the `--virtual-time-budget` is what stops it waiting forever, and a `subprocess.run(..., timeout=90)` wrapper keeps a hung run from blocking the session. Loading the same page over `file://` (or pointing Chrome at the seeded `dist/index.html` directly) is what used to hang — the SPA needs an origin. The API being offline is fine: the shell, header, dialogs and the offline card all render and are exactly what a font/layout check needs.
- **Driving the dev server over CDP needs `localhost`, and the browser must be driven at the browser endpoint.** The 2026-11-26 row-opens-modal pass added these rules the hard way:
  - Vite 8 in this checkout binds **`[::1]:5173` only** (`lsof -nP -iTCP:5173` shows `TCP [::1]:5173`), so `curl`/Chrome hitting `http://127.0.0.1:5173/` get `ERR_CONNECTION_REFUSED`. Always use `http://localhost:5173/`.
  - A page-level DevTools WebSocket (`/json/list` → `webSocketDebuggerUrl`) can attach and then **never answer** `Page.navigate` / `Runtime.evaluate` (observed after a `kill -9` left the profile dir behind). The reliable shape is the **browser** endpoint from `/json/version` plus `Target.createTarget { url }` → `Target.attachToTarget { flatten: true }`, then send commands with that `sessionId`. `Page.navigate`'s response carries `errorText` (e.g. `net::ERR_CONNECTION_REFUSED`) — read it instead of trusting a blank `location.href`.
  - Kill the old browser and `rm -rf` its `--user-data-dir` before each run; reuse the same profile and the new process forwards to the wedged one.
  - `npm run dev` and `dotnet run` started with a bare `&` may not outlive the tool command that started them: start the server, run the CDP script and stop the server **inside one shell command**.
  - `innerText` returns the *rendered* text, so `.type-eyebrow` headings come back **upper-cased** (`"THE PICK"`, `"RANK TRAJECTORY"`). Assert case-insensitively (`text.toLowerCase().includes(...)`) or the check silently fails.
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
- **Sandbox network notes (2026-11-25, re-measured 2026-11-26):** the first `curl` to
  `query1.finance.yahoo.com/v8/finance/chart/PSEI.PS` returned **1** bar for every
  `range`/`period1..period2` request, and a `date=2026-11-25` run answered `503` "PSEi returned only
  1 live daily bars". The 2026-11-26 re-measurement found the reason: **Yahoo has stopped publishing
  PSE equities.** `PSEI.PS?range=1y` answers `200` with `regularMarketPrice 5465.79` plus a sparse
  legacy series (roughly quarterly) and **no** bar on the session date; `SM.PS` / `TEL.PS` / `BDO.PS`
  / `JFC.PS` (and every other `{TICKER}.PS`) answer `404` "No data found, symbol may be delisted".
  A crumb/cookie handshake and the Yahoo search endpoint do not change it, and after roughly six
  chart calls the host throttles everything — including `AAPL` and `EPHE` — with
  `429 Too Many Requests`. PSE Edge still serves the exchange's own daily bars, so with
  `MarketData:EnablePseEdgeHistory` on, a session now fetches end-to-end from Edge: a fresh-DB
  `POST /api/signals/run-now?date=2026-11-25` logged **280 Edge chart POSTs / 284 Yahoo `.PS`
  requests / 0 failures**, read **283 daily bars** per ticker, engaged the 12-name equal-weight proxy
  for the index (`The PSEi index has no live history; reading the 11/25/2026 trend from an
  equal-weight proxy of 12 PSE heavyweights`) and stopped at `400 Ollama Cloud is not configured`.
  `GET /api/research/JFC` answers `200` with real Edge numbers (close 147.20, turnover
  ₱72.8M, RSI 49.67 on 2026-11-25). Names Edge itself stopped printing are skipped honestly:
  `VLL` (newest 2026-06-01), `RRHI` (2026-07-10), `VREIT` (2026-06-01), and `RCB` (one session
  behind). `OLLAMA_API_KEY` remains unset here, so no live Top-10 run has been observed.
