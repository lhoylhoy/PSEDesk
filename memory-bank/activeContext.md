# Active Context — PSE Signal Desk

**Last updated:** 2026-11-26 (port-5236 recovery — a hung orphaned API host freed, `backend: free port 5236` hardened with SIGKILL escalation, API relaunch on :5236 verified `200`; **451 backend tests + 1 skipped benchmark / 60 frontend tests unchanged**)
**Last feature pass:** 2026-11-26 (brand mark + icon set, on top of the header visual organization, the UI/UX legibility pass, the fast-model fix, the Yahoo-delisting pass, the accuracy pass, live-drift wiring, the performance pass and the feed-diagnostics pass — same day)
**Current task:** none in flight. Every pass above is landed and verified but **not committed** — the operator asked for no commits unless explicitly requested.

## Session note (2026-11-26) — `address already in use` on :5236

Prompted by the debugger aborting with `System.IO.IOException: Failed to bind to address
http://127.0.0.1:5236: address already in use.` (inner `SocketException`). One `.vscode` task edited
plus the memory bank — **no application code, no API contract, no dependency**.

| Command | Result |
| --- | --- |
| `lsof -nP -iTCP:5236 -sTCP:LISTEN` | PID `59738` on **both** `127.0.0.1:5236` and `[::1]:5236` |
| `ps -o pid,ppid,stat,lstart,command -p 59738` | `PPID 1`, `STAT S`, started 11:58:37, `bin/Debug/net10.0/PseSignalDesk.WebApi` — a **debuggee apphost** orphaned 3h55m earlier (no `dotnet run` parent) |
| `curl -sv --max-time 3 http://127.0.0.1:5236/health` | connected, request sent, **0 bytes received**, timed out — the host was hung, not serving |
| `kill -TERM 59738` | **still alive after 3 s and still holding both sockets** — graceful shutdown could not complete |
| `kill -9 59738` | process gone, `lsof` empty, `curl` exit **7** (connection refused), real `socket.bind(('127.0.0.1', 5236))` **OK** |
| `ASPNETCORE_URLS=http://localhost:5236 … dotnet run --no-launch-profile --no-build` | `Now listening on: http://localhost:5236`, `Application started`; `GET /health` → **`200`**; test instance then stopped, port left free for F5 |
| hardened task, path 1 (port free) | `:5236 is already free` · `:5236 refuses connections — ready for F5`, exit 0 |
| hardened task, path 2 (SIGTERM-ignoring listener on :5236) | `freeing :5236 — SIGTERM to 42311` → `:5236 still held by 42311 after 6s (hung host ignores SIGTERM) — SIGKILL` → `:5236 is free now`, exit 0 |
| `python3 -c "json.load(open('.vscode/tasks.json'))"` | parses, command 764 chars |

**What changed**

1. **`.vscode/tasks.json` → `backend: free port 5236`.** The old command sent one SIGTERM, slept 2 s
   and printed `lsof` output — which, on the hung host above, would have reported a *failure* with no
   remedy. It now: returns early when nothing listens; SIGTERMs and polls `lsof` for up to 6 s;
   **falls back to `kill -9`** and re-checks; exits `1` only if the port is still held (that means a
   second *live* debug session, which the operator must stop); and closes with a `curl /health`
   probe so the task's last line states whether the port is bindable.
2. **`techContext.md`** — the leftover-host gotcha now names both culprits (`dotnet run` *and* an
   orphaned debuggee apphost), records the observed SIGTERM-ignoring hang, and gains the
   `--no-launch-profile` trap (`ASPNETCORE_URLS` unset → the host lands on **`http://localhost:5000`**,
   which is how the first verification attempt silently "failed" to listen on 5236).

Verified and left free: nothing is listening on `:5236` now, so F5 binds immediately. The port is not
owned by this session — the app is not running.


## Session note (2026-11-26) — brand mark and icon set

Prompted by "implement a fitting favicon + logo". Five frontend files plus `index.html` and `package.json`;
**no backend file, no API contract, no new dependency** (the generator writes PNG/ICO through `node:zlib`).

| Command | Result |
| --- | --- |
| `cd frontend && npm run icons` | `favicon.svg 704 bytes` · `favicon.ico 1466 bytes (16/32/48 px)` · `apple-touch-icon.png 1319 bytes (180 px)` · exit 0 |
| `file public/favicon.ico` | `MS Windows icon resource - 3 icons, 16x16 … 32x32 … with PNG image data, 32 bits/pixel` |
| ICO entry extraction (`node` + the header directory) | 3 entries, each a valid `PNG` blob at offset 6/…; `sips` reads the 180px touch icon as `180 x 180` |
| `cd frontend && npm test` | `ℹ tests 60 · ℹ pass 60 · ℹ fail 0` (≈0.2 s, exit 0) — 55 → 60 with `deskMark.test.ts` |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ built in 304ms`, exit 0; `dist/` carries `favicon.ico`, `favicon.svg`, `apple-touch-icon.png` |
| `vite preview --port 5199 --host 127.0.0.1` + `curl` | `index 200 text/html` · `favicon.svg 200 image/svg+xml` · `favicon.ico 200 image/x-icon` · `apple-touch-icon.png 200 image/png` |
| headless Chrome screenshot of a static `/tmp` preview | 36px and 44px badges in **both** palettes next to the wordmark, plus the real 16/32px bitmaps in a light and a dark tab strip |

Not run: a screenshot of the **running app**. Headless Chrome hangs on the seeded `dist/` page in this
environment (it produced files only for static `file://` pages), so the header was checked through a static
preview of the same markup and palette rather than the live React tree. `dotnet test` was not re-run because
no backend file changed.

**What it built**

1. **`src/lib/deskMarkGeometry.ts` (new, zero imports).** The one description of the mark — "the desk tick":
   three ascending PSE candlesticks, oldest on the left — as data: 32-unit viewBox, per-candle `center` /
   body / wick / `opacity`, body 6 units, wick 1.6 units overhanging 3.5, tile radius 7, and the three baked
   colours (`#3ce6a8` mint, `#0a1e1c` = mint-400 at 10% over ink-950, i.e. the header badge's own tint).
2. **`src/components/DeskLogo.tsx` (new).** `DeskMark` renders the candles in `currentColor` over a
   `mint-400/10` tile with a `mint-400/30` ring — the exact badge classes from the old `PS` div — at `sm`
   (`size-9 rounded-lg`, phone) and `md` (`size-11 rounded-xl`, desktop). `aria-hidden` and no props beyond
   `size`, because the `<h1>` already names the desk.
3. **`scripts/generate-icons.mjs` (new, zero dependencies).** Imports the geometry straight from the `.ts`
   file (Node 24 type stripping), supersamples the rounded rects 8×8 (≤48px) / 4×4, composites the mint over
   the opaque tile, deflates the scanlines with `node:zlib`, and writes the PNG, a hand-built ICO container
   (16/32/48, PNG entries) and the SVG. `--preview <path>` dumps a 512px PNG.
4. **`public/favicon.svg`, `public/favicon.ico`, `public/apple-touch-icon.png` (new, committed).** One drawing,
   three files. The 180px touch icon is a **full-bleed opaque square** because iOS masks it itself and paints
   transparent corners black.
5. **`index.html`.** `link rel=icon` (ico → svg) + `apple-touch-icon` + `application-name` /
   `apple-mobile-web-app-title`; the `.ico` stays because Safari before 26 and iOS Safari ignore SVG favicons.
6. **`TerminalHeader.tsx`.** Both `PS` divs (`:329` phone, `:383` desktop) are now `<DeskLogo size="sm" />`
   and `<DeskLogo />`; nothing else in the header moved, so the two-row layout is untouched.
7. **`lib/__tests__/deskMark.test.ts` (new, 5 cases).** Three candles, strictly ascending bodies/wicks, every
   shape inside the tile and the trio centred, neighbouring bodies never closer than 2 units (at 16px a merge
   reads as one blob), and exactly one full-strength candle.

**Design decisions worth keeping.** Body radius is deliberately small (a pill body reads as a map pin); the wick
is thin with a long overhang (a thick nub turns the trio into three batteries at 512px); emphasis is group
`opacity`, never `fill-opacity`, so a wick crossing its own body does not double-darken; and the tile is baked
dark rather than `prefers-color-scheme`-adaptive, so the tab, the home screen and the header badge are the same
object in both palettes.

Out of scope (not built): a wordmark lockup, `site.webmanifest` + 192/512 install icons, `og:image`, an
adaptive favicon, and the mark on the legal gate or the guide sheet.


## Session note (2026-11-26) — header visual organization

Prompted by "content has no proper visual organization" on the desktop command bar. Three frontend files
plus the memory bank; no backend file, no API contract, no new dependency and no new architectural layer.

| Command | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 55 · ℹ pass 55 · ℹ fail 0` (0.34 s, exit 0) |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 129 modules transformed`, `✓ built in 231ms`, exit 0 |
| `npm run preview -- --port 5199 --host 127.0.0.1` | `index:200`, serves `assets/index-BTiMI5v1.js`; server stopped afterwards |
| built CSS inspection | `sm:h-10`, `sm:h-full`, `bg-mint-500`, `text-ink-950` all emitted |

Diagnosis: the desktop bar mixed **25px** (both switch groups, collapsed under `sm:min-h-0`),
**36px** (`More`) and **40px** (buttons, select) in one row; six near-identical tiles carried no grouping;
and identity (≈504px) + controls (≈827px) could not fit between 1024 and ~1439px, so the whole cluster
wrapped under the logo. The switch groups were also the last 10px uppercase labels left in Simple mode.

What changed:

1. `SegmentedControl` — one component behind `ThemeSwitch` and `ViewModeSwitch`, with a `sm:h-10` frame and
   `sm:h-full` pills that carry `.ui-button` (so the Simple type floor reaches them). `More` is `h-10`;
   `TabsList`/`TabsTrigger` use the same frame pair, so dialog rails match their 40px buttons too.
2. Desktop row 1 = identity + trading day + divider + `My Trades` + the filled `Run` + `More ▾`; row 2 =
   the four horizon tabs (helper label only from `xl`) + the two appearance switches + a quiet `N days saved`.
3. `Button` gains an opt-in `variant="cta"` (filled `mint-500` / `ink-950`, AA in both palettes), used only
   by the header Run on phone and desktop; `primary` keeps its previous look everywhere else.
4. `More` groups *Learn* / *Tools*; the tagline is one `truncate` line with the full sentence on `title`;
   the 10px uppercase sessions/RAG sentence was deleted (the footer already states fees + Manila time).
5. The tablet-only `Check a Stock` / `Past Results` pills were removed — they squeezed the horizon rail
   until `Long-Term (1+ Yr)` was clipped and wrapped their own labels; both actions live in the ☰ sheet.

Verified in a real browser for the first time: a static copy of `dist/` was served from `/tmp` with
`psedesk.legalAcceptedVersion` seeded, then captured with headless Chrome at 390 (inside a 390px iframe,
because headless Chrome clamps `--window-size` to 500px), 768, 1024, 1280 and 1440 Pro. Every header
control measured 40px, row 1 never wrapped, all four horizon tabs were visible at 768+, and the light CTA
sampled `#0a6f4d` on `#f3f6fb`.

Deliberately not done: the page-body hierarchy pass (tracked as **Phase 2** under *UI/UX follow-ups* in
`progress.md`), an icon-only theme toggle (this audience needs the visible state), and any restyle of
`primary` outside the header. Still not run: 320px, 2560px and a VoiceOver/NVDA pass.

## Session note (2026-11-26) — UI/UX legibility pass

A frontend-only pass, prompted by "what makes a good UI/UX for this type of app?". Seven files under
`frontend/src` changed plus one new suite; **no backend file, no API contract and no server number moved**.

| Command | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 55 · ℹ pass 55 · ℹ fail 0` (≈0.3 s, exit 0) — 49 → 55 with `formatters.test.ts` |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 129 modules transformed`, `✓ built in 235ms`, exit 0 |
| `npm run dev -- --port 5199 --host 127.0.0.1` | `index:200`; `App.tsx` and `AdviceCardList.tsx` transform; server stopped afterwards |
| built CSS inspection | `.simple-label` 0.75rem (old 0.6875rem gone), `.simple-note`, `.run-progress` + keyframes + reduced-motion override, `sr-only` all emitted |
| `git status --porcelain` | `M App.tsx`, `M AdviceCardList.tsx`, `M RegimeStrip.tsx`, `M TerminalHeader.tsx`, `M ui/Button.tsx`, `M index.css`, `M lib/formatters.ts`, `?? lib/__tests__/formatters.test.ts` |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 451, Skipped: 1, Total: 452` (8 s, exit 0) — regression check, no backend file changed |

Not run: a real browser/screen-reader pass (no jsdom or component-test harness exists; adding one would be
a new dependency, which the memory-bank rules forbid without a recorded decision).

**What it fixed, in priority order** (from the audit in the same conversation):

1. **Nothing was announced.** `grep -rn 'aria-live\|role="status"\|aria-busy' src` returned **zero** hits, so a run result, an amber stale-data line or a failure was silent for screen readers. `App.tsx` now mounts one `aria-live="polite" aria-atomic` node from the first render; `Button` sets `aria-busy`; the Pro "Refreshing…" line is a `role="status"`.
2. **The smallest text explained the biggest number.** 262 occurrences of `text-[9px]`/`text-[10px]` (46 at 9px) sat on the Simple reading path. `.simple-label` is now 12px and a new `.simple-note` (13px / 1.55) replaced every 9–10px explanatory line in the advice card, order box, watch list and card headers. Both rules are scoped to `[data-view-mode='simple']`, so Pro density is unchanged.
3. **`run-now` looked hung.** It is a minutes-scale operation (a few hundred PSE Edge POSTs + four model calls) behind a disabled button. A strip under the header now counts elapsed seconds (`formatElapsedSeconds`) beside an indeterminate bar and states that the previous session stays on screen until the new one lands. It narrates elapsed time only — never a stage or percentage the server did not confirm.
4. **Simple cards carried two bordered meter blocks.** The promised win-chance meter is now full width; confidence folded into one line ("Desk confidence 82 / 100 · …") with its plain sentence, and the raw score remains a `TechRow` in the drawer.
5. **Colour could be the only channel.** `directionGlyph` / `formatDeltaPercent` / `formatDeltaBasisPoints` now prefix every toned 20D delta (USD/PHP, US 10Y bps, Brent, EPHE, rotation RS) with `▲`/`▼` — insurance against the reversed red-up/green-down palette common in PH broker apps and against colour-vision deficiency.
6. **Unit and absence discipline is now tested.** `formatters.test.ts` pins "a value always carries its unit" and "unreported renders `—`, never `0`" for `formatPrice`, `formatCompactPeso`, `formatPercent`, `formatRatio`, the delta formatters and the elapsed clock.

Deliberately **not** done: a stage-by-stage progress estimate (would fabricate what the server has not
reported), any new dependency, and any colour-semantics change (needs the 3–5 reader check recorded
under *Remaining* in `progress.md`).


## Session note (2026-11-26) — "where are the tickers?"

Read-only inventory; no source file changed. Two different lists exist and they are easy to confuse:

| List | Lives in | Reaches the browser via | Size / state |
| --- | --- | --- | --- |
| **Tracked universe** (static metadata, no prices) | `backend/src/PseSignalDesk.Infrastructure/MarketData/PseTickerCatalog.cs` → `PseTickerCatalog.All` | `GET /api/research/catalog` → `ResearchController.GetCatalog()` → `TickerResearchService.GetCatalog()` → `IMarketDataProvider.GetCatalog()` | **64 names** (verified live: ICT … RCR) |
| **Tickers on the desk** (the ranked book) | SQLite `SignalItems.Ticker` rows joined to `SignalRuns` | `GET /api/signals/latest?horizon=…` → `run.items[].ticker` | whatever the last stored run for that horizon produced |

Rendering: Pro → `frontend/src/components/SignalTable.tsx`; Simple → `AdviceCardList.tsx` + `RidingNowStrip.tsx`; detail sheet → `TickerDetailDialog.tsx`; catalog search → `ResearchDialog.tsx` + `lib/pseNicknames.ts`.

Local `backend/src/PseSignalDesk.WebApi/pse_signals.db` at the time of the check: runs **14 (WEEK, 9 names)** and **15 (MONTH, 10 names)**, both `RunDate 2026-11-25`; **no DAY or YEAR run**, so those two horizons legitimately render the "Market data loaded, no signal run yet" empty state. `DailyMarketSnapshots` 1 row / `StockDailyMetrics` 60 rows, all `2026-11-25`.

Verified commands:
- API on a throwaway DB (`Data Source=/tmp/psedesk-tickers.db`, port 5399, `KnowledgeBase__IndexOnStartup=false`): `/health` `200`; `GET /api/research/catalog` `200` with **64** entries; `GET /api/signals/latest?horizon=WEEK` `200` with `run: null`, `dates: []` (fresh DB → no book, catalog still full). Throwaway DB removed afterwards; the real `pse_signals.db` was only opened read-only via `sqlite3 -readonly`.

## Verified in the fast-model fix (2026-11-26)

The operator's debugger showed two first-chance lines: `SignalGenerationException` in
`PseSignalDesk.Infrastructure.dll` and the same exception surfaced from `System.Private.CoreLib.dll`,
with `Ollama Cloud returned HTTP 404 (Not Found). {"error": "model 'qwen3.5:4b' not found. Try
glm-5.3-flash:cloud."}`.

| Finding | Evidence |
| --- | --- |
| **The fast-extraction default was never a cloud model.** | `GET https://ollama.com/api/tags` (Bearer user-secret key) → `200`, **25 models**: `deepseek-v4.1-flash`, `deepseek-v4.1-flash:1017`, `deepseek-v4-pro:0813`, `glm-5.2`, `glm-5.3`, `glm-5.3-flash`, `gpt-oss:20b/120b`, `kimi-k2.6/2.7-code/3/3.1-pro`, `mimo-v2.6-flash/pro`, `minimax-m3/3.1-flash`, `mistral-large-4`, `gemma4:31b`, `clef:27b`, `nemotron-3-super/ultra`, … — no `qwen3.5:4b`. |
| **The 404 is the *optional* pass, not the ranking.** | `POST /api/chat` with `qwen3.5:4b` → `404`; `deepseek-v4.1-flash` → `200`; `glm-5.3-flash` → `200`; `mimo-v2.6-flash` → `200`. The exception was caught by `EnrichWithFastExtractionAsync` (warning, run continues), which is why the run still produced a book — the debugger was reporting first-chance throw sites. |
| **`glm-5.3-flash` answers the real fast prompt.** | The exact fast system/user prompt (two candidates, `format: json`, `temperature 0.1`) → `200` and a parseable object: `{"summary": "In a BULLISH regime, ICT shows the cleanest structure: STAGE_2 uptrend … Avoid MER on dilution", "trap_flags": [...]}`. |
| **Embeddings are unavailable for this key, by design harmless.** | `POST /api/embed` with `nomic-embed-text`, `nomic-embed-text:latest` and even `glm-5.3-flash` → `401 {"error": "unauthorized"}`; legacy `/api/embeddings` → `404 path not found`. `OllamaEmbeddingClient` logs and returns `null`, so retrieval stays lexical. |

| Command | Result |
| --- | --- |
| `cd backend && dotnet build PseSignalDesk.slnx --nologo` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 451, Skipped: 1, Total: 452` (8 s, exit 0); the skip is the manual replay benchmark |
| `dotnet test … --filter FullyQualifiedName~OllamaSignalEngineTests` | `Passed! 4/4` — fast 404 keeps the ranking, one 404 per engine (latch), ranking 404 still `SignalGenerationException`, blank fast model makes no call |
| live `curl` probes (see the table above) | `/api/tags` `200` (25 models); `qwen3.5:4b` `404`; `glm-5.3-flash` / `deepseek-v4.1-flash` / `mimo-v2.6-flash` `200`; `/api/embed` `401` |

Not run in this pass: a full `run-now` past the LLM step (the fast prompt was exercised directly against
the live endpoint instead), and the frontend suites (no frontend file changed).

## What the fast-model fix changed

Four files plus the memory bank; no indicator, plan level, fee, prompt or golden moved.

- **`OllamaCloudOptions.FastExtractionModel`** default and **`appsettings.json`** → `glm-5.3-flash` (the model the provider itself pointed at, validated `200`); `README.md`'s env example follows.
- **`OllamaSignalEngine`** now separates "the endpoint answered non-success" from "the ranking failed": `SendChatAsync` throws a private `ChatResponseException(status, message)`, `SendRankingChatAsync` re-wraps it as `SignalGenerationException` with the identical text (the API's `502` contract is untouched), and `EnrichWithFastExtractionAsync` treats `404` as a configuration error — one warning naming the setting and the reasoning model, then `_fastExtractionUnavailable` (volatile) latches the pass off for the life of the engine, so the four horizons stop repeating the call.
- **New `OllamaSignalEngineTests`** (4 cases, stub `HttpMessageHandler` routed by the request body's `model`, no sockets): fast 404 keeps the book, one 404 per engine across two horizons, ranking 404 still `SignalGenerationException` with the upstream detail, blank fast model makes zero fast calls.

## Where the desk stands

A single-operator research desk for the Philippine Stock Exchange, with all four horizons live:

- **Ingestion.** Yahoo Finance v8 charts (`PSEI.PS` or an equal-weight heavyweight proxy, `PHP=X`, `{TICKER}.PS`) **plus the PSE Edge daily-history fallback that now supplies every PSE series** (Yahoo answers `404` for all `{TICKER}.PS`), PSE Edge filings (row-attributed, word-boundary matched), an optional official PSE Daily Quotation Report (live templates or operator upload) and three best-effort macro series (`^TNX`, `BZ=F`, `EPHE`). A session fetches everything in one fan-out and each download is reused for ten minutes (charts) / five (Edge pages) per request URL, so the four horizons of one run share one network call. No synthetic series and no demo tape exist.
- **Screening.** A deterministic quant pass (Wilder RSI-14, ATR-14, EMA-20, SMA-50/150/200, 52-week high, Weinstein stage, RS63d, ATR%, VCP, turnover/volume), a hard PHP 5M floor on **both** the average and the 20-session median turnover plus ≥16 active sessions, a top-25 cap, market breadth and sector rotation. History is read point-in-time: no live foreign-flow print and no filing dated or timestamped after the session's close.
- **Ranking.** One strict-JSON Ollama call per horizon, grounded in retrieved knowledge sections, then server-owned post-processing: unknown ticker / thesis-less / hard-trap discard, tick-grid normalisation, the `DAY` static band and net-R:R gate, drift recomputation, drop classification and citation verification.
- **Pricing.** `ProbabilisticEdgeService` owns the win probability, break-even rate, EV, Quarter-Kelly stake and `TRADE` / `SMALL_SIZE` / `WATCH_ONLY` verdict. An optional **promoted** per-horizon logistic model (a residual over the prior, ±6pp tilt, Kelly rescale) and a live calibration offset feed in; the desk memory (realized record, sectors that paid/bled, stop-outs, ranked factors) is printed into the prompt.
- **Sizing.** `OrderTicketCalculator` builds one ticket per pick from a `BookSizingSnapshot` (risk budget, free cash, heat, sector room, 2% of 20-day turnover, governor multipliers, the live-drift throttle, slippage/ADV participation, execution guidance), settles the fees including the PHP 20 commission floor, and routes a same-ticker order into a legal pyramid add.
- **Grading and replay.** The forward-outcome ledger grades only plans the tape actually filled; `BacktestService` replays the same rules causally (point-in-time filings, median liquidity, fees dated to each trade, gap-open stop fills) into a multi-slot, benchmarked equity curve with a rolling walk-forward refit.
- **Paper book.** A cash ledger with raise-stop, close, trim and pyramid merge, plus expectancy / profit factor / payoff telemetry, drawdown posture and alerts.
- **Terminal.** **Simple** (market call, then at most three plan cards) and **Pro** (dense table and tiles) read one payload; persisted light/dark, bottom-sheet dialogs, card fallbacks from 320px up, a zero-dependency SWR cache with intent preloaders, and a first-run legal gate.
- **Knowledge base.** 8 built-in documents / 46 sections in SQLite, BM25 + cosine RRF retrieval with mandatory guardrails and horizon/sector/trap boosts, optional embeddings, and uploads behind a rights attestation.

## Verified in the Yahoo-delisting pass (2026-11-26)

The operator hit `MarketDataUnavailableException` — "No PSEi index series … 0 of 12 constituents" — on a healthy network. The cause is upstream: **Yahoo stopped publishing PSE equities.**

| Finding | Evidence |
| --- | --- |
| **Every PSE equity chart is gone from Yahoo.** | `query1.finance.yahoo.com/v8/finance/chart/JFC.PS` (and `SM.PS`, `TEL.PS`, `BDO.PS`, …) → `404` "No data found, symbol may be delisted"; `PSEI.PS?range=1y` → `200` with a sparse legacy series (~quarterly) and a live `regularMarketPrice`, but no bar on the session date. |
| **The Yahoo workarounds do not exist.** | A crumb/cookie handshake, the mirror host `query2` and the Yahoo search endpoint all return the same `404`; search only resolves the symbol, never a chart. |
| **PSE Edge still publishes the exchange's own daily bars.** | `autoComplete/searchCompanyNameSymbol.ax?term=JFC` → `cmpyId 86`; `companyPage/stockData.do?cmpy_id=86` → `security_id 158`; `POST common/DisclosureCht.ax` with `{cmpy_id, security_id, startDate, endDate}` → `chartData[{OPEN,HIGH,LOW,CLOSE,VALUE,CHART_DATE}]` back 3–5 years (283 sessions for `date=2026-11-25`). |
| **Edge reports peso value, not share volume.** | `VALUE` is the exact turnover; `VALUE / ((HIGH+LOW+CLOSE)/3)` reproduces the exchange's own share count within ~0.01% on a real JFC print, which keeps `Close × Volume` equal to the turnover the PHP 5M floor is measured on. |
| **Edge has no index series at all.** | `psei/form.do` is a company-news search (`/psei/search.ax`), `index/form.do` a company lookup, and `?term=PSEI` answers `[]` — so the equal-weight proxy of 12 heavyweights stays the only live path for regime, breadth and realised volatility. |
| **The extra cache traffic exposed a real cache race.** | `MarketDataCache.EvictOldest` dereferenced an entry whose value a concurrent `Clear()` had already removed → `NullReferenceException` → `GET /api/research/RCB` answered `500` mid-session. Reproduced in a stress probe in 133 ms, then fixed and pinned by a test. |

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 447, Skipped: 1, Total: 448` (8 s, exit 0); the skip is the manual replay benchmark |
| `dotnet build PseSignalDesk.slnx --nologo` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `curl` probes against the live feed | `JFC.PS` / `SM.PS` / `TEL.PS` / `BDO.PS` → `404`; `PSEI.PS` → `200` with `regularMarketPrice` and a sparse legacy series; all three Edge endpoints → `200` |
| fresh-DB smoke run (`Data Source=/tmp/psedesk-fresh.db`, port 5299), `POST /api/signals/run-now?date=2026-11-25` | reached the LLM step (`400 Ollama Cloud is not configured`) with **280 Edge chart POSTs / 284 Yahoo `.PS` requests / 0 failures**, **283 daily bars** per ticker, the proxy note *"The PSEi index has no live history; reading the 11/25/2026 trend from an equal-weight proxy of 12 PSE heavyweights"*, and 4 honest skips (`VLL` 2026-06-01, `RRHI` 2026-07-10, `VREIT` 2026-06-01, `RCB` one session behind — all stale on Edge itself) |
| `GET /api/research/JFC` on the same build | `200` — close `147.20`, turnover `₱72,828,984`, `rsi14 49.67`, `sma200 156.84`, `maxDrawdown252dPct 44.39`, `asOfDate 2026-11-25`, `dataSource live` |
| `GET /api/research/{RCB,VLL,RRHI,VREIT}` | `RCB` `200` after the cache fix (was `500`); `VLL`/`RRHI`/`VREIT` `503` **"no live chart with 200 sessions"** — correct, Edge stopped printing them |
| `dotnet test … --list-tests` (delta) | +8 provider tests (`PseMarketDataProviderTests` 22 → 28) and +1 cache race test (`MarketDataCacheTests` 8 → 9) |

Not run in this pass: any run past the LLM step (`OLLAMA_API_KEY` unset), and the frontend suites (no frontend file changed).

## What the Yahoo-delisting pass changed

Four files, all in the *feed* layer; no indicator, plan level, fee or golden moved.

- **`PseEdgeHistorySource`** (new, `internal`) + `EdgeBarFetch(Bars, Failure)`: resolve (symbol search → `cmpyId`, company page → `security_id`) then one chart POST, parsed into one bar per session, 10-minute `MarketDataCache` TTL per call, per-request timeout, and every failure returned as a reason instead of thrown.
- **`PseMarketDataProvider.WithEdgeHistoryAsync`** is the seam, called at exactly three points (single-symbol research, the stock fan-out, the proxy constituent fetch) and only when the Yahoo chart is unavailable or shorter than the caller's requirement. A replacement logs `Reading {TICKER} from PSE Edge ({n} daily bars) because …`; a short Edge series now reports `PSE Edge: {count} bar(s), newest {date}.` instead of a blank reason (the live run printed `PSE Edge: .` before this fix).
- **`MarketDataOptions`** gained `EnablePseEdgeHistory` (default `true`), `PseEdgeChartUrl`, `PseEdgeSymbolSearchUrl`, `PseEdgeCompanyPageUrl`, mirrored in `appsettings.json`; `EnablePseEdgeHistory = false` keeps the strict Yahoo-only contract.
- **`MarketDataCache.EvictOldest`** filters a value that a concurrent `Clear()` already removed before sorting, so eviction can no longer fault a request.


The operator pasted the debugger output of a failing `run-now`: `MarketDataUnavailableException`, "equal-weight heavyweight proxy could only use **0 of 24** constituents (6 required)" with `PSEI.PS: 0 bar(s), 200 required` listed **twice** and duplicated/scrambled constituent names.

| Finding | Evidence |
| --- | --- |
| **The upstream really is degraded here** (so the `503` was correct *at that time*, not a code fault — the Yahoo-delisting pass above then found the cause and the fix). | `curl` to `query1.finance.yahoo.com/v8/finance/chart/PSEI.PS?interval=1d&range=1y` → `200` with `regularMarketPrice: 5465.79` and **exactly 1 timestamp/close**; `SM.PS`, `TEL.PS`, `BDO.PS`, `JFC.PS` → **404**; after ~6 chart calls every symbol (incl. `AAPL`, `EPHE`) → **429 Too Many Requests**. |
| **`ConfigurationBinder` appends instead of replacing.** | `MarketDataOptions` initializes `IndexYahooSymbols`/`IndexProxyConstituents`; the shipped `appsettings.json` repeats both lists, so binding produced **2** index symbols and **24** constituents. Pinned by `MarketDataOptionsTests.Normalize_undoes_the_doubling_a_real_configuration_section_causes` (asserts 2/24 raw → 1/12 normalised). |
| **The failure message raced.** | `FetchProxyConstituentBarsAsync` had 24 tasks appending to one shared `List<string>`; that is why the reasons were duplicated *and* out of order. |

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 439, Skipped: 1, Total: 440` (7 s, exit 0); the skip is the manual replay benchmark |
| `dotnet test … --list-tests` | 440 rows over 32 test files (`PseMarketDataProviderTests` 22, `MarketDataOptionsTests` 4) |
| `dotnet build PseSignalDesk.slnx --nologo` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| smoke run: `ASPNETCORE_URLS=http://127.0.0.1:5299 … Data Source=/tmp/psedesk-verify.db KnowledgeBase__IndexOnStartup=false dotnet bin/Debug/net10.0/PseSignalDesk.WebApi.dll`, then `POST /api/signals/run-now?horizon=WEEK` | `/health` `200`; the run answered `503 Market data unavailable` with **"could only use 0 of 12 constituents (6 required): PSEI.PS: 0 bar(s), 200 required; TEL: HTTP 404 for TEL.PS; SM: …; GLO: HTTP 404 for GLO.PS. The live market feed is unavailable."** — one entry per distinct constituent, configured order, no double period. Throwaway DB and process removed afterwards; the real `pse_signals.db` was never opened. |

Not run in this pass: anything that needs a healthy Yahoo feed (a real Top-10 run, DQR live templates, the replay benchmark), and the frontend (`npm test` / `npm run build` — no frontend file changed).

## What the feed-diagnostics pass changed

Three defects, all in the *failure* path (the success path is byte-identical — no golden moved, no plan level or fee changed):

- **Normalised symbol lists (`MarketDataOptions.Normalize()`, `DistinctNonBlank`).** `ConfigurationBinder` adds a bound section to a collection the class already initialized, so a section that repeats the defaults doubles the list. `MarketDataOptions.Normalize()` trims, drops blanks and de-duplicates (`IndexYahooSymbols`, `IndexProxyConstituents`, `PseDailyQuotationUrlTemplates`, case-insensitive, first spelling wins), and `Program.cs` applies it in a `PostConfigure` on the `MarketData` options registration. An operator can still *extend* a list from configuration — a repeated entry is simply read once.
- **The provider normalises again in its constructor** into `_indexSymbols` / `_proxyConstituents`, so a hand-built `Options.Create(new MarketDataOptions())` (every test, any DI-free caller) fetches one download per distinct symbol and reports an honest `of N constituents`.
- **Deterministic, thread-safe failure reasons.** `FetchProxyConstituentBarsAsync` now returns a `ProxyConstituentFetch(bars, reason)` per constituent and collects the reasons **after** `Task.WhenAll`, in configured order — no shared `List<T>` written by parallel tasks, no duplicated or shuffled entries. `JoinReasons` trims the period each reason already ends with so the `503` detail does not print `GLO.PS..`.
- **Behaviour is unchanged where it matters:** the index still needs 200 bars, the proxy still needs 6 of the configured heavyweights, and nothing is fabricated — the upstream outage still answers `503`.

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
- **A delisted feed is replaced by the exchange's own tape, never by an invented one.** When Yahoo has no PSE series, `PseEdgeHistorySource` supplies real Edge sessions inside the requested window; a row without a usable OHLC print is dropped rather than carried forward, and Edge's peso `VALUE` becomes volume through `VALUE / ((HIGH+LOW+CLOSE)/3)`. `MarketData:EnablePseEdgeHistory = false` restores the strict Yahoo-only contract.
- **Eviction never trusts an entry it just enumerated.** A value a concurrent `Clear()` removed is skipped, not dereferenced.
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
- `GET /api/signals/backtest?horizon=WEEK` and a real Top-10 run now that the tape resolves from PSE Edge; compare `ExcessReturnPct` with a prior-pinned baseline, and read the dated-friction fields (`feesPaidPhp` per trade) against a hand-computed pre-CMEPA and post-CMEPA round trip.
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
2. Confirm the baseline: backend `dotnet test PseSignalDesk.slnx` (**447 passed + 1 skipped benchmark = 448**), `cd frontend && npm test` (**49**), `npm run build`.
3. Update `activeContext.md` when starting/finishing a task and `progress.md` when status changes.
4. Keep `systemPatterns.md` in sync with architectural changes and `techContext.md` with tool/config changes.
5. Never persist secrets (`OLLAMA_API_KEY`, `OllamaCloud:ApiKey`, `PSEDESK_OPERATOR_API_KEY`) — env or user-secrets only.
6. Never add a fallback that fabricates market data, never let a test hit the network, never compute a risk number in the browser.
7. Keep the compliance posture intact: loopback-only by default, no soliciting copy, simulated results labelled simulated, uploads behind the rights attestation, and the legal notices in sync with `lib/legalConsent.ts`.

