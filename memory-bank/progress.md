# Progress — PSE Signal Desk

**Status snapshot:** 2026-11-26 — a four-horizon probabilistic PSE research desk: live-only ingestion behind a per-URL download cache with one fan-out per session (**PSE series now read from PSE Edge daily history**, because Yahoo stopped publishing PSE equities), deterministic point-in-time screening and tick-grid plans, server-owned odds / EV / Quarter-Kelly, one self-trained money-manager book per horizon (promoted only through a held-out gate, λ picked on a nested inner split), server-sized order tickets that settle real fees, a paper blotter with legal pyramiding and partial trims, a forward-outcome ledger, a benchmarked causal replay priced at its own dates, on-demand research for any ticker, an official-DQR foreign pillar, cross-asset dials, a SQLite RAG library (8 documents / 46 sections), and a Simple/Pro terminal that reflows from 320px to 2560px.

**Latest verification:** **464 backend tests green + 1 skipped manual benchmark (465 total) / 66 frontend tests green** (2026-11-26, typeface pass on top of the compliance-hardening, responsive, brand-mark, header-organization, legibility, fast-model, Yahoo-delisting, feed-diagnostics, performance, accuracy and live-drift passes — each of those keeps its own section below). This pass changed only the frontend's typeface: Google Sans Flex is **vendored** (`public/fonts/google-sans-flex-latin.woff2`, OFL 1.1) instead of CDN-linked, so the privacy notice stays literal and the terminal still renders with nothing but the API on loopback; only the `latin` subset ships, because the API's `latin-ext`/`symbols` subsets were measured and do not carry the desk's `₱`/`▲ ▼` glyphs. Everything past the LLM step remains unverified live here because `OLLAMA_API_KEY` is unset in the shell; the open security remediation is listed under *Remaining*.

## Latest verification (2026-11-26 — typeface: Google Sans Flex)

Frontend only: `src/index.css`, `index.html`, `package.json`, `scripts/fetch-fonts.mjs` (new),
`public/fonts/` (new: the woff2 + `OFL.txt`), plus `README.md`, `PRIVACY.md`, `THIRD_PARTY_NOTICES.md`
and the memory bank. **No backend file, no API contract, no new dependency, no golden moved.**

| Command | Result |
| --- | --- |
| `node scripts/fetch-fonts.mjs` | `google-sans-flex-latin.woff2 49988 bytes` · `OFL.txt 4488 bytes`, exit 0; a second run reproduced the identical MD5 (`306cc19…`) |
| `node scripts/fetch-fonts.mjs --check` | prints the upstream `fonts.gstatic.com` subsets URL without writing; the URL matches the committed binary |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ built in 231ms`; `grep -c @font-face dist/assets/index-*.css` → **1**; `dist/fonts/` = woff2 + `OFL.txt` only |
| `cd frontend && npm test` | `ℹ tests 66 · ℹ pass 66 · ℹ fail 0` (≈0.25 s, exit 0) |
| `npm run preview -- --port 5199` + `curl` | `/` `200 text/html`; `/fonts/google-sans-flex-latin.woff2` `200 font/woff2` (49,988 bytes) and `/fonts/OFL.txt` `200 text/plain` |
| headless Chrome over `http://127.0.0.1:5290` (copy of `dist/`, legal gate seeded) | 1280px light Simple, 1280px dark Pro, 500px dark Pro — all render in the new face, header rows unwrapped, tabs intact; screenshots `/tmp/app-gsf.png`, `/tmp/app-gsf-dark.png`, `/tmp/app-gsf-phone.png` |
| `document.fonts` + advance-width probe | one face, `loaded`, `U+0-FF …`; `A` = 26.81 px at 40 px vs 24.09 px for the Menlo fallback → the bundled face is applied |

Not run: `dotnet test` (no backend file changed) and a real-device Safari/Firefox pass — the glyph-coverage
finding below is Chrome-measured, and the official Google Fonts CSS behaves identically there.

**What changed**

- **Vendored, not linked.** `public/fonts/google-sans-flex-latin.woff2` (variable `100 900`, 49,988 bytes)
  is served by the app; `index.html` preloads it (`as="font"`, `crossorigin`) and `index.css` declares a
  single `@font-face` (`font-display: swap`, the API's own `unicode-range`) with
  `--font-sans: 'Google Sans Flex', 'Inter', ui-sans-serif, …`. No `fonts.googleapis.com` request exists,
  so `PRIVACY.md` §5 ("no third-party script tags") stays literally true and the terminal still renders
  with nothing but the API on loopback. `--font-mono` is untouched.
- **One subset, on evidence.** The API's `latin-ext` and `symbols` subsets were fetched, rendered and
  compared against the Menlo fallback: `₱` (U+20B1), `▲`/`▼` (U+25B2/U+25BC) and `→` came out
  **pixel-identical to the fallback**, and the official CDN CSS did the same in the same browser. They were
  dropped (~51 kB saved, zero glyphs lost); the system font supplies those few characters.
- **`scripts/fetch-fonts.mjs` (new, zero dependencies).** Reads the Google Fonts CSS with a desktop UA,
  extracts the subset URL, verifies the `wOF2` signature before overwriting (an HTML error page can never
  replace a good binary), writes `OFL.txt`, and supports `--check`. `package.json` gained `npm run fonts`.
- **Licence + notices.** OFL 1.1 ships beside the font (OFL §1 requires it); `THIRD_PARTY_NOTICES.md` §4
  gained a *Bundled assets* table, `PRIVACY.md` §5 and `README.md` document the font and the command. No
  reader-facing legal copy changed, so `CURRENT_LEGAL_VERSION` was not bumped.

## Latest verification (2026-11-26 — compliance hardening)

Cross-stack but small: **4 new files** (`LICENSE`, `lib/operatorKey.ts`, `WebApi/Services/DeskWorkGate.cs`,
`WebApi/Services/DeskRateLimitPolicies.cs`) plus **12 edited** (`DISCLAIMER.md`, `PRIVACY.md`,
`THIRD_PARTY_NOTICES.md`, `README.md`, `legalConsent.ts`, `signalsApi.ts`, `vite-env.d.ts`, `.env.example`,
`ComplianceOptions.cs`, `Program.cs`, `SignalsController.cs`, `appsettings.json`), **1 edited test**
(`legalConsent.test.ts`) and **3 new test files**. No indicator, plan level, fee, prompt, golden snapshot or
existing API status code moved; the four expensive endpoints gained `409` and `429`.

| Command | Result |
| --- | --- |
| `cd backend && NUGET_PACKAGES=$HOME/.nuget/packages dotnet build PseSignalDesk.slnx --nologo` | `Build succeeded. 0 Warning(s) 0 Error(s)` (12.6 s) |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 464, Skipped: 1, Total: 465` (8 s) — 452 → 465, +13 |
| `cd frontend && npm test` | `ℹ tests 66 · ℹ pass 66 · ℹ fail 0` (≈0.42 s) — 60 → 66, +6 |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 129 modules, ✓ built in 189ms` |
| `dotnet PseSignalDesk.WebApi.dll` on `:5411`, throwaway DB, every upstream → `127.0.0.1:9`, `Compliance__ExpensiveRequestsPerHour=2` | `GET /api/signals/dates` → **200**; `POST /api/signals/run-now` three times → **503, 503, 429**; the `429` carries `Content-Type: application/problem+json` and `Retry-After: 3600` |
| `dotnet PseSignalDesk.WebApi.dll` on `:5412`, Python listener that accepts and never answers, `RequestTimeoutSeconds=30` | first `run-now` blocked inside the fetch; a **concurrent** second `run-now` → **409** `application/problem+json` `{"title":"The desk is already running a job", …}` while the first later answered `503` |

Both smoke instances ran under `nohup`, were stopped with SIGTERM + SIGKILL, and used throwaway SQLite
files in `/tmp` — the real `pse_signals.db` was never opened, and no process was left on `:5411` / `:5412`.

### What the pass changed

1. **`LICENSE` — the repo had no licence at all**, so `DISCLAIMER.md`'s warranty cap and liability limit
   bound nobody who copied the code. The new file is explicitly **not** open source: one personal,
   non-commercial copy on a machine you control; no redistribution, no hosting for others, no selling, no
   derivative distribution; "as is" + liability cap; termination on breach; §3 states that market data is
   not licensed by it (the PSE agreement is not the licensor's to give).
2. **`DISCLAIMER.md` §7 (new) — "Instruction-shaped copy is for one self-user only"**, with §7/§8
   renumbered to §8/§9 (the only inbound cross-reference, `THIRD_PARTY_NOTICES.md` §6, points at §6 and is
   unaffected). This is the gap no code can close: Simple mode says "buy in this band", "cut here", "lock
   break-even at +1R" because that is clarity for one informed reader — and it is precisely the shape of
   personalised advice when a third party is reading it. The section forbids passing the output on as
   advice, forbids running anyone else's money through it (names RA 8799 / the SRC and the SEC rules),
   and says the portfolio-controls layer makes the *simulation* realistic, not the software a
   portfolio-management system.
3. **`legalConsent.ts` — new "Written for one reader" card** in the same voice as the other five, and
   `CURRENT_LEGAL_VERSION` bumped `2026-11-v1` → **`2026-11-v2`**, so `hasAcceptedCurrentLegalVersion`
   rejects the stored flag and the gate re-prompts. `legalConsent.test.ts` now asserts the card exists
   and contains "not a recommendation", "anyone else's money" and "Securities Regulation Code" — a future
   softening of that copy fails the suite.
4. **The `LicensedCommercial` path is operable end to end.** `lib/operatorKey.ts` (pure, React-free,
   `SAFE_METHODS`-aware) decides the header; `signalsApi`'s single `request()` spreads it in, so every
   POST/PUT/PATCH/DELETE from the terminal carries `X-PseDesk-Key` while reads never do — the key is not
   attached to a GET, and a blank `VITE_OPERATOR_API_KEY` (the personal default) adds nothing at all.
   Server-side, `Compliance:AllowedHosts` replaces the loopback-only `AllowedHosts` **only** when
   `DeploymentMode` is `LicensedCommercial`, applied before `builder.Build()`; previously a licensed
   deployment behind a domain answered `400` to every request (host filter) and the SPA answered `401` to
   every write (no key), so the mode was documented but unreachable.
5. **Bounded expensive work.** `DeskWorkGate` is a singleton `SemaphoreSlim(1,1)` with a non-blocking
   `TryEnter()`; `run-now`, `train` and `train-all` return `409` (`WorkAlreadyRunning()`, a ProblemDetails
   that names the cause) when another job holds it, and release it in a `finally` on every path. The
   `DeskRateLimitPolicies.Expensive` policy — one fixed one-hour window per process, `PermitLimit` from
   `Compliance:ExpensiveRequestsPerHour` (default 30, `Math.Clamp(…, 1, 1000)`), `QueueLimit = 0` —
   is attached with `[EnableRateLimiting]` to those three **plus** `backtest`, and `OnRejected` writes
   `429` + `Retry-After` + ProblemDetails. `app.UseRateLimiter()` sits **after**
   `ComplianceGuardMiddleware`, so a `403` never burns budget. Stored-session reads stay uncapped.

Two tripwires keep the wiring from rotting: `DeskWorkGateTests` (idle→enter, refusal while held, release
makes it reusable, exactly one of eight concurrent racers wins, an unmatched `Exit()` throws) and
`SignalsControllerRateLimitTests`, which reflects over the controller and fails if any of the four
expensive actions loses its attribute — or if a stored read grows one.

## Latest verification (2026-11-26 — responsive pass, 320px → 2560px)

A frontend-only pass: `index.css` plus 19 components (`App.tsx`, `ui/Button`, `ui/Dialog`, `ui/Tabs`,
`ui/TechnicalDisclosure`, `TerminalHeader`, `SignalTable`, `AdviceCardList`, `DropPanel`, `RegimeStrip`,
`MarketDirectionHero`, `BacktestPanel`, `ModelTrainerPanel`, `RiskSizerBar`, `PortfolioDialog`,
`ResearchDialog`, `TickerDetailDialog`, `ScorecardDialog`, `BeginnerGuideDialog`). **No backend file, no
API contract, no new dependency, no new architectural layer.**

| Command | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 60 · ℹ pass 60 · ℹ fail 0` (≈0.15–0.19 s, exit 0) — unchanged, run before and after the pass |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 131 modules transformed`, `✓ built in ~190ms`, exit 0 (run after the `App.tsx` JSX restructure, after the dialog/sheet edits and again after the final `xs:` sweep) |
| built-CSS breakpoint audit | `@media (width>=30rem)`, `(width>=40rem)`, `(width>=48rem)`, `(width>=64rem)`, `(width>=80rem)`, `(width>=96rem)`, `(width>=120rem)`, `(width>=160rem)` all emitted — `xs`, `3xl` and `4xl` are real |
| built-CSS utility audit | `.tap{` (inside `@media (pointer: coarse)`), `.scroll-x{`, `.shell{`, `.text-fluid-hero`, `pb-safe`, `2.75rem`, `container-type` all present |
| container-variant audit | `@container` root plus `(width>=24rem / 28rem / 32rem / 36rem / 42rem / 56rem / 64rem / 80rem)` rules emitted for `@sm/@md/@lg/@xl/@2xl/@4xl/@5xl/@7xl` |
| `git status --porcelain` | the intended 20 modified paths only; nothing deleted, no new file |

Not run: a real-browser capture at 320px and 2560px. Headless Chrome still hangs on the seeded `dist/`
page in this environment (see `techContext.md`), so the 320px/2560px behaviour is derived from the emitted
CSS (column counts, clamp ranges, media conditions) rather than observed pixels. `dotnet test` was not
re-run because no backend file changed.

What changed, in the order it matters:

1. **Three new breakpoints and one gutter/type ladder.** `@theme` gains `--breakpoint-xs` (30rem),
   `--breakpoint-3xl` (120rem) and `--breakpoint-4xl` (160rem). `.shell` replaces the
   `px-3 py-4 sm:px-5 sm:py-6` ladder with `clamp()` gutters; `App` widens to `128rem` at `3xl` and turns
   the Pro book into `minmax(0,1fr) 22rem` (a 26rem sidebar at `4xl`) instead of stacking the dropped
   names under the table forever.
2. **Container queries where the component, not the window, decides.** `DialogContent`, the advice card
   and `DropPanel` are `@container` roots; the card grids, the four-step `WavePlanGrid`, the
   entry/stop/target `PlanSteps`, the technical rows, the watch list and the drop list all switched from
   `sm:`/`lg:`/`xl:` viewport prefixes to `@sm:`/`@md:`/`@lg:`/`@xl:`/`@4xl:`/`@5xl:`/`@7xl:` — a card in
   the 3-column layout now reflows to its own width, and the same `DropPanel` is a full-width section in
   Simple mode and a 22rem sidebar in Pro without a second code path.
3. **Phone band (`xs`) on every tile ladder.** `grid-cols-2 xs:grid-cols-3 …` on `IndicatorTiles`,
   the portfolio KPIs and guardrails, the risk sizer, the detail/research/backtest/scorecard/model tiles:
   a 480–639px phone gets three metric tiles per row instead of two, which is most of a screen of height
   saved in every sheet.
4. **Sheets.** `dvh` instead of `vh` for both sheet heights (`92dvh` phone, `88dvh` desktop),
   `overscroll-contain`, the `3xl` width steps, a readable prose measure for `BeginnerGuideDialog`,
   `TickerDetailDialog`'s conviction + action block now wraps (a `Record in blotter · 1,240 sh` label
   could push `Close` off a 320px sheet), the scorecard calibration table got a `.scroll-x` wrapper, and
   the calibration/verdict sections stack from one column.
5. **Touch and scrolling.** `.tap` (44px floor, `pointer: coarse` only) on `Button`'s base, the technical
   drawer, the signal-card `Details`/`Take`, the table's info/Take cells, the guide's `See the full
   record`, the advice card's `Show N more` and the portfolio refresh. `.scroll-x` replaces every bare
   `overflow-x-auto` (SignalTable, ledger, replay, scorecard, the tab rail), with the `.no-scrollbar`
   interaction handled by `:not()` so the two utilities cannot fight over `scrollbar-width`.
6. **Header on a tablet.** Between `md` and `lg` the phone command row now carries the trading-day picker
   inline (`auto 1fr auto` grid), the run label ellipsizes instead of overflowing at 320px while the
   `Book`/`My Trades` control keeps its place, and the picker's visibility lives on a wrapper (`hidden
   md:block`) because `cn()` cannot arbitrate `hidden` against the trigger's own `sm:inline-flex`.
7. **Fluid type where fixed sizes were wrong at both ends.** `.text-fluid-hero` on the market-call
   headline (replacing `text-lg sm:text-xl`), `.text-fluid-metric` on the Simple summary figures
   (replacing `text-lg`), a `clamp()` loading skeleton instead of `h-40`, and a `xs:grid-cols-3` /
   `3xl:grid-cols-9` tape that is one row of dials on a 2560px monitor.

## Latest verification (2026-11-26 — `:5236` port recovery)

| Command | Result |
| --- | --- |
| `lsof -nP -iTCP:5236 -sTCP:LISTEN` | PID `59738` on `127.0.0.1:5236` **and** `[::1]:5236` |
| `ps -o pid,ppid,stat,lstart,command -p 59738` | `PPID 1`, `STAT S`, 3h55m old, `bin/Debug/net10.0/PseSignalDesk.WebApi` (orphaned debuggee apphost, no `dotnet run` parent) |
| `curl -sv --max-time 3 http://127.0.0.1:5236/health` | TCP connected, request sent, **0 bytes received**, timed out — hung host |
| `kill -TERM 59738` | still alive after 3 s, still holding both sockets (graceful shutdown could not complete) |
| `kill -9 59738` | process gone; `lsof` empty; `curl` exit `7`; real `bind(('127.0.0.1', 5236))` OK |
| `ASPNETCORE_URLS=http://localhost:5236 dotnet run --no-launch-profile --no-build` | `Now listening on: http://localhost:5236`; `GET /health` → `200`; instance stopped, port left free |
| hardened `backend: free port 5236`, free port | `:5236 is already free` · `:5236 refuses connections — ready for F5`, exit 0 |
| hardened `backend: free port 5236`, SIGTERM-ignoring listener | SIGTERM → 6 s poll → `SIGKILL` → `:5236 is free now`, exit 0 |

Not run: `dotnet test` / `npm test` — no application or test file changed. Tooling only; the app is
left **stopped** with `:5236` bindable so the next F5 succeeds.

## Latest verification (2026-11-26 — brand mark + icon set)

| Command | Result |
| --- | --- |
| `cd frontend && npm run icons` | `favicon.svg 704 bytes` · `favicon.ico 1466 bytes (16/32/48 px)` · `apple-touch-icon.png 1319 bytes (180 px)`; exit 0, no dependency installed |
| `node scripts/generate-icons.mjs --preview /tmp/desk-mark-512.png` | 512px PNG written (6.3 kB) — the only way the mark was judged at large size |
| `file public/favicon.ico` + entry extraction | `3 icons, 16x16 / 32x32 / 48x48 … PNG image data, 32 bits/pixel`; each entry a valid PNG blob (272 / 417 / 630 bytes) |
| `sips -g pixelWidth -g pixelHeight` | `apple-touch-icon.png` `180 x 180`, `hasAlpha: yes` |
| `cd frontend && npm test` | `ℹ tests 60 · ℹ pass 60 · ℹ fail 0` (≈0.2 s, exit 0) — 55 → 60 |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ built in 304ms`, exit 0; `dist/` carries the three icon files and `index.html` references them |
| `vite preview --port 5199` + `curl` | `index 200 text/html`, `favicon.svg 200 image/svg+xml`, `favicon.ico 200 image/x-icon`, `apple-touch-icon.png 200 image/png` |
| headless Chrome, static `/tmp` preview | 36px and 44px badges on both palettes beside the wordmark, plus the real 16/32px bitmaps in a light and a dark tab strip |

Not run: a screenshot of the running app (headless Chrome hangs on the seeded `dist/` page here and only
produced files for static `file://` pages), and `dotnet test` (no backend file changed).

## Latest verification (2026-11-26 — header visual organization)

| Command | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 55 · ℹ pass 55 · ℹ fail 0` (≈0.34 s, exit 0) |
| `cd frontend && npm run build` | `tsc --noEmit` clean; `✓ 129 modules transformed`, `✓ built in 231ms`, exit 0 |
| `npm run preview` + headless Chrome (static copy of `dist/` in `/tmp`, seeded `psedesk.legalAcceptedVersion`) | screenshots at **390** (390px iframe), **768**, **1024**, **1280** and **1440 Pro**: every header control measures 40px, row 1 never wraps, all four horizon tabs are visible at 768+, and the light-theme CTA samples `#0a6f4d` on `#f3f6fb` |
| built CSS inspection | `sm:h-10`, `sm:h-full`, `bg-mint-500`, `text-ink-950` emitted; the switch groups no longer depend on `sm:min-h-0` for their height |
| `git status --porcelain` | `M TerminalHeader.tsx`, `M ui/Button.tsx`, `M ui/Tabs.tsx` on top of the still-uncommitted legibility pass; no backend file, API contract or server number touched |

Not run: a screen-reader pass (VoiceOver/NVDA) and a 2560px wide-monitor check. Headless Chrome on macOS
clamps `--window-size` to **500px** wide, so the 390px phone layout was captured inside a 390px iframe
(`frame-390.html`) and the 320px case was reasoned from the same classes.

## What the header pass changed

Three frontend files. No new dependency, no new layer, no server change.

- **One control height.** `SegmentedControl` (new, shared by `ThemeSwitch` and `ViewModeSwitch`) uses a `sm:h-10` frame with `sm:h-full` inner pills; `More` moved `h-9` → `h-10`; `TabsList`/`TabsTrigger` use the same pair. The desktop bar previously mixed 25px (both switch groups, which collapsed under `sm:min-h-0`), 36px (`More`) and 40px (buttons, select) in one row.
- **Two rows with one job each.** Row 1: identity + trading day + `My Trades` + `Run` + `More`. Row 2: horizon tabs + `ThemeSwitch`/`ViewModeSwitch` + a quiet `N days saved`. The tagline is a single `truncate` line (`title` keeps the full sentence) and the 10px uppercase sessions/RAG sentence is gone — the footer already states fees and Manila time.
- **A real primary action.** `Button` gains `variant="cta"`: filled `bg-mint-500` on `text-ink-950` (≈11:1 dark, ≈6.5:1 light). Used only by the header Run on phone and desktop; `primary` keeps its previous outline look everywhere else.
- **Grouped overflow.** `More` lists *Learn* (How it works / Guide, Knowledge base in Pro) and *Tools* (Check a Stock, Past Results) instead of four flat rows.
- **One horizon strip below `lg`.** The tablet-only `Check a Stock` / `Past Results` pills squeezed the rail until `Long-Term (1+ Yr)` was clipped and wrapped their own labels onto two lines. Both actions stay in the ☰ *Desk controls* sheet, so the rail keeps the full row at 768px.
- **Simple-mode type floor reaches the switches.** Both segmented controls and the `More` summary carry `.ui-button`, so Simple renders them at 13px normal-case rather than 10px uppercase.

## Latest verification (2026-11-26 — UI/UX legibility pass)

| Command | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 55 · ℹ pass 55 · ℹ fail 0` (≈0.3 s, exit 0) |
| `cd frontend && npm run build` | `tsc --noEmit` clean; `✓ 129 modules transformed`, `✓ built in 235ms`, exit 0 |
| `npm run dev -- --port 5199 --host 127.0.0.1` | `index:200`; `src/App.tsx` and `src/components/AdviceCardList.tsx` both transform (`simple-note` present 16×), then the server was stopped |
| built CSS inspection | `[data-view-mode=simple] .simple-label` → `0.75rem` (the old `0.6875rem` is gone), `.simple-note` emitted, `.run-progress` keyframes emitted, reduced-motion override present, `sr-only` emitted |
| `git status --porcelain` | 7 modified files + 1 new test file, all under `frontend/src`; no backend file touched |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 451, Skipped: 1, Total: 452` (8 s, exit 0) — regression check only, no backend file changed |

Not run in this pass: a real browser/DOM pass (the project has no jsdom or component-test harness, and adding one would be a new dependency).

## What the UI/UX legibility pass changed

Six frontend files plus one new suite. No API contract, no server number, no backend file changed.

- **Legibility floor for Simple mode.** `[data-view-mode='simple'] .simple-label` moved 11px → 12px with a line height, and a new `.simple-note` (13px / 1.55) now carries every explanatory second line that used to be 9–10px: plan-step notes, order-box disclaimers, warnings, watch-list copy, the thesis and the pre-mortem. Pro mode is untouched because the rule is scoped to the attribute.
- **A shell-wide live region.** `App.tsx` mounts one `aria-live="polite" aria-atomic` node from the first render and feeds it the error, the run notice and the "running" state; `Button` now sets `aria-busy`, the Pro "Refreshing…" line is a `role="status"`, and the header run button reads `Running…` while it works.
- **`run-now` is narrated, not frozen.** A new strip under the header counts elapsed seconds (`formatElapsedSeconds`) beside an indeterminate bar (`.run-progress`, reduced-motion aware) and says the previous session stays on screen. It reports elapsed time only — never a stage or percentage the server did not confirm.
- **One fewer bordered block per card.** The promised win-chance meter is now full width; the confidence score became a single line ("Desk confidence 82 / 100 · …") with its plain sentence, and the raw value still lives in the technical drawer.
- **Direction is not colour-only.** `directionGlyph` / `formatDeltaPercent` / `formatDeltaBasisPoints` prefix every toned 20D delta (USD/PHP, US 10Y bps, Brent, EPHE, rotation RS) with `▲`/`▼`, so a reader whose broker app uses the opposite palette (or who is colour-blind) is not misled.
- **`formatters.test.ts` (6 cases)** pins the two invariants: a value always carries its unit, and an unreported value renders `—` rather than `0`.

## Verification (2026-11-26 — fast-model fix)

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 451, Skipped: 1, Total: 452` (8 s, exit 0) |
| `dotnet build PseSignalDesk.slnx --nologo` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `dotnet test … --filter FullyQualifiedName~OllamaSignalEngineTests` | `Passed! 4/4` (86 ms) |
| `curl -H 'Authorization: Bearer <user-secret>' https://ollama.com/api/tags` | `200`, **25 models** listed; no `qwen3.5:4b` |
| `curl POST /api/chat` per model | `qwen3.5:4b` → `404 {"error": "model 'qwen3.5:4b' not found. Try glm-5.3-flash:cloud."}`; `deepseek-v4.1-flash`, `glm-5.3-flash`, `mimo-v2.6-flash` → `200` |
| `curl POST /api/chat` with the real fast-extraction prompt on `glm-5.3-flash` | `200`, `{"summary": "In a BULLISH regime, ICT shows the cleanest structure …", "trap_flags": [...]}` — parses as a JSON object |
| `curl POST /api/embed` (`nomic-embed-text`, `nomic-embed-text:latest`, `glm-5.3-flash`) | `401 {"error": "unauthorized"}` for all three; legacy `/api/embeddings` → `404 path not found` → retrieval stays lexical |

Not run: a full `run-now` past the LLM step (the fast prompt was exercised directly against the live endpoint) and the frontend suites (no frontend file changed).


| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 447, Skipped: 1, Total: 448` (8 s, exit 0) |
| `dotnet build PseSignalDesk.slnx --nologo` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `dotnet test … --list-tests` (delta) | `PseMarketDataProviderTests` 22 → 28 (6 new Edge-fallback cases), `MarketDataCacheTests` 8 → 9 (the eviction/`Clear()` race) |
| `curl` probes against the live feed | every `{TICKER}.PS` chart → `404`; `PSEI.PS` → `200` with a live `regularMarketPrice` and a sparse legacy series; Edge search / company page / `DisclosureCht.ax` → `200` |
| fresh-DB smoke run (`/tmp/psedesk-fresh.db`, port 5299), `POST /api/signals/run-now?date=2026-11-25` | **280 Edge chart POSTs / 284 Yahoo `.PS` requests / 0 failures**, 283 daily bars per ticker, proxy note *"reading the 11/25/2026 trend from an equal-weight proxy of 12 PSE heavyweights"*, 4 honest skips (`VLL`, `RRHI`, `VREIT` stale on Edge itself, `RCB` one session behind), then `400 Ollama Cloud is not configured` |
| `GET /api/research/JFC` | `200` — close `147.20`, turnover `₱72,828,984`, `rsi14 49.67`, `sma200 156.84`, `asOfDate 2026-11-25` |
| `GET /api/research/{RCB,VLL,RRHI,VREIT}` | `RCB` `200` after the cache fix (was a `500 NullReferenceException`); the other three `503` "no live chart with 200 sessions" |
| `git status --porcelain` | this pass touched `MarketDataOptions.cs`, `PseMarketDataProvider.cs`, `MarketDataCache.cs`, `appsettings.json`, the new `PseEdgeHistorySource.cs` and the two test files; `README.md`, `Program.cs`, `MarketDataOptionsTests.cs` and the memory bank still carry the earlier uncommitted passes |

Not run: anything past the LLM step, and the frontend suites (no frontend file changed).

## Verification (2026-11-26 — performance pass)

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 433, Skipped: 1, Total: 434` (7 s, exit 0) |
| `dotnet test … -c Release --filter FullyQualifiedName~Benchmark_reports --logger "console;verbosity=detailed"` | before: 6,554 / 6,327 / 6,224 ms and **1,708.4 MB** every run; after: 4,741 / 5,649 / 6,247 ms and **1,401–1,404 MB** |
| benchmark trade lines, both sides | DAY 48 trades 89,975.20 (−10.02 %), WEEK 36 · 89,709.45 (−10.29 %), MONTH 30 · 88,919.05 (−11.08 %), YEAR 27 · 89,081.12 (−10.92 %) — identical |
| goldens run against the pre-optimisation source (`git stash push -u -- backend/src`) | `Passed! 5/5`, i.e. byte-identical output |
| `dotnet test … --list-tests` | 434 rows over 31 test files |
| `git status --porcelain` | 9 modified + 7 new paths, no strays; `git stash list` empty afterwards |

Wall clock is noisy here (±30 % between identical runs), so the allocation number is the signal that is actually repeatable. Not run: anything needing network, and `dotnet ef` (no schema change in this pass).

## Verification (2026-11-26 — accuracy pass + live-drift wiring; counts as of that pass)

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 416, Skipped: 0, Total: 416` (~1 s, exit 0) |
| `cd frontend && npm test` | `tests 49 · pass 49 · fail 0` (≈0.2 s) |
| `cd frontend && npm run build` | `✓ built in 248ms` (`tsc --noEmit` + Vite, exit 0) |
| `ls backend/src/PseSignalDesk.Infrastructure/Migrations` | eleven migrations; `20261125212733_AddRobustLiquidityColumns` and `20261125214041_AddLiveDriftState` both present with the expected `Up`/`Down` |
| `dotnet ef migrations script 20261125212733_AddRobustLiquidityColumns` | `ALTER TABLE "TrainedHorizonModels" ADD "LiveDrift" TEXT NOT NULL DEFAULT 'STABLE'` + `LiveDriftGradedPicks INTEGER NOT NULL DEFAULT 0` |
| `sqlite3` on a **copy** of `pse_signals.db` | both columns added cleanly; `PRAGMA table_info` ends `LiveDrift TEXT 1 'STABLE'`, `LiveDriftGradedPicks INTEGER 1 0` |
| `dotnet run` against a throwaway `Data Source=/tmp/psedesk-smoke.db` | all eleven migrations applied on a fresh database; `/health`, `/api/portfolio`, `/api/signals/latest?horizon=WEEK` and `/api/portfolio/order-ticket` each answered `200`; the only logged failure is the expected offline bootstrap refit (`HTTP 404` on every `*.PS` chart) |
| `dotnet test … --list-tests` | 416 rows over 27 test files |

Not run: `dotnet ef database update` (startup applies migrations), any network call.

## What works

### Platform and invariants
- [x] Clean Architecture solution (`PseSignalDesk.slnx`) on .NET 10: Domain → Application → Infrastructure → WebApi, plus one xUnit test project with no network access.
- [x] Domain model (`DailyMarketSnapshot`, `StockDailyMetric`, `SignalRun`, `SignalItem`, `SignalDrop`, `KnowledgeDocument`, `KnowledgeChunk`, `PortfolioAccount`, `PortfolioTrade`, `TrainedHorizonModel`) and enums serialized as strings.
- [x] `PseBoardLotService`: the official 15-tier PSE board-lot/tick table, bracket-crossing re-rounding, the static `+50%/-30%` band, `target > entry > stop` repair, **dated** per-leg fees (`BuyLegFeeRate` 0.295%, `StockTransactionTaxRate(date)` 0.60% pre-CMEPA / 0.10% from 2025-07-01, `RoundTripFeeRateOn(date)`), the PHP 20 + 12% VAT commission floor through `ComputeLegFeePhp`, settled-peso risk/reward (`ComputeNetRiskPhp`, `ComputeNetRewardPhp`, `ComputeNetRiskRewardPeso`), the expectancy layer (break-even rate, EV, Quarter-Kelly, risk-budget sizing), gap-aware `ResolveStopFillPrice`, tick spread, ADV participation and estimated slippage.
- [x] `PseTradingSessionRules.TradingSessionsBetween` — the exchange calendar (weekends **and** PSE holidays) behind `WeekdaySessionsBetween`, embargo lengths and forward windows.
- [x] `HorizonProfile` holds every per-horizon parameter (mission, `MinHistoryBars`, RSI band, ATR multiples, disclosure lookback, hysteresis, net-R:R requirement, EV minimum, `MinimumVolumeRatio` 1.2/1.2/1.1/1.0, preferred knowledge categories). All four horizons require net R:R ≥ 2.0; EV minima are 0.20 / 0.15 / 0.20 / 0.25.
- [x] EF Core + SQLite with eleven migrations (`InitialCreate` → `AddLiveDriftState`), unique indexes, cascade deletes, replace-on-rerun semantics and no-tracking reads.
- [x] Point-in-time discipline: `EvaluateStock(..., includeLiveForeignFlow: false)` for every replay/training call, `ScopeToSession` / `IsAvailableBy` / `ScopeDisclosures` honouring the filing date and the 15:00 PHT `MarketClose` cutoff (`StockDisclosure.FiledAtPht`).

### Signal pipeline and pricing
- [x] Live market data: Yahoo v8 charts with a mirror-host retry, index resolution plus an equal-weight heavyweight proxy, USD/PHP staleness guard, 10-ticker minimum, tick-rounded candles, zero-volume echo removal, row-aware PSE Edge disclosure attribution, best-effort macro dials and an optional official DQR.
- [x] **PSE Edge daily history is the live PSE series source.** Yahoo stopped publishing PSE equities (every `{TICKER}.PS` answers `404`), so `PseEdgeHistorySource` resolves each symbol on Edge (search → `cmpy_id`, company page → `security_id`), POSTs `common/DisclosureCht.ax` for three to five years of `chartData`, and `WithEdgeHistoryAsync` substitutes it whenever the Yahoo chart is missing or shorter than the caller needs — for research, the stock fan-out and the proxy constituents alike. Sessions are de-duplicated (last print wins), clamped to the requested window and never carried forward, and Edge's peso `VALUE` becomes volume through `VALUE / ((HIGH+LOW+CLOSE)/3)` so `Close × Volume` still equals the official turnover the PHP 5M floor uses. A short Edge series names its own size and newest session in the diagnostic; `MarketData:EnablePseEdgeHistory = false` restores the Yahoo-only contract.
- [x] **One download per upstream URL.** `MarketDataCache` (singleton, bounded at 200 entries, `TimeProvider`-injectable) caches the *task* of a raw body under the absolute URL — 10 minutes for a Yahoo chart, 5 for an Edge page — so the four horizons of one session, a replay and an ad-hoc research call share a single GET. Concurrent callers share one request, a failure is evicted rather than remembered, and an abandoned caller never cancels the download another request is waiting for (shared fetch on `CancellationToken.None`, per-caller `WaitAsync`, the download keeps its own timeout). `InvalidateCache()` (default no-op on the interface) clears it, and `POST /api/signals/run-now` calls it.
- [x] **One fan-out per session.** With the session date fixed by the index, the FX series, the Edge pages, the DQR, the macro dials and the ticker charts start together; each ticker awaits the disclosure map only after its own chart lands. The typed client runs on a pooled `SocketsHttpHandler` (`AutomaticDecompression = All`, `UseCookies = false`, `PooledConnectionLifetime = 5 min`) and `GET /api/research/catalog` is client-cached for an hour.
- [x] Quantitative filter: Wilder RSI-14, ATR-14, EMA-20, SMA-50/150/200, 52-week high, Weinstein stage, RS63d, ATR%, VCP ratio, up/down volume, Chaikin money flow, Kaufman efficiency, beta, max drawdown, realized volatility, 20-day turnover/volume (mean **and** median + active-session count), support/resistance, weighted pre-score (85-point technical total rescaled when foreign flow is unreported), the PHP 5M floor plus `MinimumActiveTradingDays20d = 16`, and the top-25 cap.
- [x] `PseDisclosureClassifier`: headline → `DisclosureCategory`, trap flags (hard `SRO_DILUTION`, `TENDER_OFFER_PIN`, `TRADING_HALT_RISK`; soft overhang, ex-dividend, static-band chases) and UI copy.
- [x] Defensive `SignalJsonParser`: outermost balanced JSON, fences/prose, braces in strings, numeric strings, contract validation and human-readable retry errors.
- [x] Pipeline: market-data reuse/fetch, horizon-aware candidate selection, knowledge retrieval, previous-run context, one Ollama call with a corrective retry, unknown-ticker/thesis-less/hard-trap discard, resolved-position exclusion, the `DAY` static-band clamp and net-R:R gate, board-lot sizing, citation verification, server-side drift and drop classification, atomic persist, and `502` when nothing tradeable remains.
- [x] `ProbabilisticEdgeService`: shrunk empirical base rate (prior 0.42, 12 pseudo-observations, null under 60 bars) blended with barrier geometry, bounded tilts (stage, RS, VCP, breadth, regime, traps, dividend yield, conviction), clamp `[0.05, 0.95]`, verdict gates (`TRADE` / `SMALL_SIZE` / `WATCH_ONLY`), run-level cash posture and per-horizon hit rates in one pass.
- [x] Mandatory pre-mortem: the model's `bear_case` is kept when usable, otherwise a deterministic stop/resistance/probability fallback is written.
- [x] `MarketDirectionService` (five weighted pillars onto a 50 base, clamped 10–90; bearish regime override; unreported dials omitted) and `WaveTradePlanCalculator` (ideal buy / cut-loss / fee-paid break-even / target, chase ceiling, entry status, holding ladder, legal trim size).
- [x] `OrderTicketCalculator`: one ticket per pick from `BookSizingSnapshot` (risk budget, cash, heat room, position cap, 2% ADV, sector room, governor multipliers, slippage, execution guidance), settled-peso `NetRiskPhp`/`NetRewardPhp` with a whole-lot step-down loop that fits the risk budget (refusing `"cannot carry a {lot}-share lot"` when one lot is already too big), zero shares for `WATCH_ONLY`, and the **seven legal-pyramid rules** (above entry, break-even stop, ≥ +1R, under two adds, below target, 0.50x/0.25x risk, sector-name exemption). The `LiveDriftPolicy` multiplier is applied **after** the `[0.50, 1.50]` governor clamp, fed by the book's own `LiveDrift` snapshot.

### Learning loop
- [x] `LogisticModelTrainer`: L2 IRLS with a Gaussian-elimination solver over a per-trial **offset** (the structural prior), so the weights learn only the residual.
- [x] `MoneyManagerTrainingService`: trials replay the desk's own execution (tick-grid plan, the trade's own dating for fees, the +1R break-even ratchet, the +1.5R causal EMA20 trail, the causal index regime, gap-aware stop fills) on liquid names only, with a chronological 70/30 split that is purged and embargoed in **trading sessions**, train-only standardisation, sample weights `1/√(signals that date) × clamp(0.5 + 0.5·|R|, 0.5, 2)`, a λ scan on a **nested inner 80/20 split** (`SelectLambda`, no peeking at the held-out slice), a Kelly grid `[0.15, 0.35]`, and a promotion gate of ≥25 held-out trades, Brier **and** log-loss better than the prior, plus `policyExpectancyR > 0`. Lookback default is now five years (1820 calendar days).
- [x] `LiveDriftPolicy` (Domain, pure): `Classify(gradedPicks, realizedWinRate, expectedWinRate, rollingExpectancyR, baselineExpectancyR, previous)` → `STABLE` / `WARNING` / `DEGRADED` / `HALTED` with `MinimumGradedPicks = 15`, gaps of `max(20pp, 2·SE)` and `max(0.60R, 2·SE/√n)`, hysteresis on release, `MultiplierFor` 1.00 / 0.65 / 0.35 / 0.00 and a `Describe` warning printed into the ticket.
- [x] **The drift throttle is wired.** `SignalOutcomeService.EvaluateLiveDriftAsync(horizon)` compares the rolling window of the last 30 graded picks against the promoted record (`ValidationWinRate` / `ValidationExpectancyR`), persists `TrainedHorizonModel.LiveDrift` + `LiveDriftGradedPicks` when the verdict changes, and returns a `LiveDriftSnapshot`. `PortfolioService.GetBookSizingSnapshotAsync(ct, horizon)` evaluates it once per book and every `BookSizingSnapshot.For(ticker)` ticket carries it; the daily run, the terminal envelope and the research endpoint pass their horizon. A prior-priced book (no active model) and the horizon-less portfolio ticket stay `STABLE`.
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
- [x] `BacktestService`: prefix-only evaluation with `includeLiveForeignFlow: false` and session-scoped disclosures, the median turnover/active-day liquidity gate, multi-slot book (`MaxOpenPositions` 5, 6% heat, 2 names / 35% equity per sector), equity-based sizing with cash as a funding check, drawdown governor, board-lot slippage, break-even/EMA20 exit ladder, gap-open stop fills, **dated fees per trade**, the PSEi benchmark, alpha, CAGR, Sharpe, Calmar, total slippage and every trade.
- [x] `WalkForwardEdge`: the replay's own residual model, refitted every 60 sessions on already-resolved candidates, used only when it beats the neutral base rate on its own trades; it never writes `TrainedHorizonModels` and its acceptance is deliberately in-sample.
- [x] `SignalOutcomeService`: entry-fill gate, `UNFILLED` terminal rows, conservative same-bar tie to the stop, realized net R after per-leg fees, scorecard aggregation, live calibration offset and desk memory.
- [x] **Replay cost is linear in bars, not quadratic.** One causal EMA-20 series per ticker (`BuildEma20Series`, the same `ComputeEmaSeries` the live filter uses) instead of one EMA per bar, the index regime memoised per date, and the filter's trailing-20-session statistics computed in a single pass — the four-horizon benchmark went from 1,708.4 MB to ~1,402 MB allocated with identical output.
- [x] `GET /api/signals/scorecard`, `GET /api/signals/backtest`, `GET /api/research/catalog`, `GET /api/research/{query}`.

### Market data, DQR and knowledge
- [x] Official PSE Daily Quotation Report: deterministic parser (PDF/CSV/TXT/TSV, header mapping, accounting negatives, suspension flags, missing-marker columns) with two ingestion paths — live templates (`pse-dqr-live`) and operator upload (`pse-dqr-upload`, 25 MB, `400`/`404` on bad input).
- [x] Upload overlays foreign flow plus suspension disclosures only; breadth, macro dials, FX, realized vol and rotation survive, and unmatched tickers keep `null`.
- [x] Cross-asset dials (`^TNX` bps, `BZ=F`, `EPHE`) fetched concurrently, best-effort, `null` when stale — printed into the prompt's `CROSS-ASSET:` line.
- [x] SQLite RAG library: 8 built-in documents / 46 sections (front matter, `slug#section-n` references, content-hash re-indexing), mandatory-guardrail injection, BM25 + cosine RRF with horizon/sector/trap boosts, optional embeddings, Markdown/TXT/PDF upload behind the rights attestation, built-in delete protection (`409`).

### API, compliance and operations
- [x] Full REST surface: signals (`latest`, `by-date`, `dates`, `ticker/{symbol}/history`, `scorecard`, `backtest`, `model`, `train`, `train-all`, `desk-memory`, `run-now`, `quotation-report`), research (`catalog`, `{symbol}` with horizon/`averageCost`/`shares`), portfolio (summary, settings, order-ticket, trades, stop, close, trim), knowledge (list, reindex, upload, delete) and `/health`.
- [x] RFC 7807 errors: `400` config/input/attestation/rule violation, `404` unknown session/document, `409` protected document, `409` a run or training job already in progress, `429` too many expensive requests (`Retry-After`), `499` client abort, `502` generation, `503` feed.
- [x] `ComplianceGuardMiddleware` (loopback-only in `PersonalLocal`, constant-time operator key on writes in `LicensedCommercial`, `/health` bypass) and `AllowedHosts` narrowed to loopback names.
- [x] **Bounded expensive work** — `DeskWorkGate` makes `run-now`/`train`/`train-all` mutually exclusive (`409`), and `DeskRateLimitPolicies.Expensive` (`Compliance:ExpensiveRequestsPerHour`, default 30, clamped 1–1000) applies one fixed one-hour window to those three plus `backtest` (`429` + `Retry-After`). `UseRateLimiter()` runs after the compliance guard so a refused caller never burns budget; stored reads are uncapped. Verified live on `:5411` (`503, 503, 429`) and `:5412` (concurrent `409`).
- [x] **`LicensedCommercial` reachable from the UI** — `lib/operatorKey.ts` + `VITE_OPERATOR_API_KEY` put `X-PseDesk-Key` on mutating requests only, and `Compliance:AllowedHosts` widens the host allowlist only when the licensed mode is declared.
- [x] First-run legal gate (`lib/legalConsent.ts` v2, `LegalDisclaimerGate`) plus `LICENSE`, `DISCLAIMER.md` (§7 single-reader rule), `PRIVACY.md`, `THIRD_PARTY_NOTICES.md`; non-solicitation copy across the prompt and the UI.
- [x] `RequestCancellationMiddleware` (499), caller-cancellation rethrow in the provider, and a one-macrotask deferral in the terminal that removes the common StrictMode abort.
- [x] Scheduler: weekdays 15:35 Asia/Manila, four horizons per session, weekly refit, cold-boot refit.

### Frontend
- [x] React 19 + TypeScript 7 + Vite 8 + Tailwind 4 shell with a typed API client (`ApiError`, ProblemDetails, abort signals, FormData uploads) and centralized formatters.
- [x] **Google Sans Flex is the UI typeface, vendored rather than CDN-linked** (`public/fonts/…-latin.woff2`, variable 100–900, OFL 1.1 + `OFL.txt`, preloaded in `index.html`, `--font-sans` in `index.css`, refreshed by `npm run fonts`). A page load still talks to nothing but the API; `₱` and `▲ ▼` come from the system font because no Google-served subset carries them (measured, same as the official CDN link). `--font-mono` stays for tickers.
- [x] `Simple | Pro` view mode and light/dark theme, both persisted; Simple leads with `MarketDirectionHero` (traffic light, 10–90 gauge, reasons, sector chips, trust line) and `RidingNowStrip`, then at most three `AdviceCardList` cards with the four-step plan grid and a technical drawer.
- [x] Pro keeps the dense `SignalTable`/`ProSignalCard` views, regime tiles, rotation strip, governor/slippage tiles, `RiskSizerBar`, verdict/drift badges and the Knowledge Base action.
- [x] `TickerDetailDialog` (thesis, plan, odds, factors, technicals, dividends, disclosures with trap badges, server-verified citations, rank history), `ResearchDialog` (buy plan vs "already holding it" with `HoldingVerdictCard`), `ScorecardDialog` (forward scorecard, dual-line benchmarked replay, `ModelTrainerPanel` with learned edge / desk memory and refit buttons).
- [x] `PortfolioDialog` (Book / Record a trade / Ledger / Settings; trim, pyramid badges, expectancy tiles, exposure gauges, guardrails drawer), `KnowledgeBaseDialog`, `QuotationReportImportButton`, `WelcomeSetupBanner`, `BeginnerGuideDialog`, `LegalDisclaimerGate`, `DropPanel`.
- [x] Responsive foundations: Radix `Dialog` as a bottom sheet below `sm` with a sticky header, card fallbacks for every wide table below `md`, pinned identifier column, scrolling tab rail, touch-sized controls, `100dvh` / `viewport-fit=cover` / `.pb-safe`, pre-paint theme colour and 16px mobile inputs.
- [x] Zero-dependency SWR cache (`lib/queryCache.ts`, ref-counted abort, in-flight dedupe, prefix invalidation) plus idempotent preloaders; every reader paints cached data, then revalidates, and keeps the last payload behind an amber line when a refresh fails.
- [x] Lazy dialog chunks and pinned vendor chunks through a `manualChunks` **function** (Vite 8 / rolldown rejects the object form).
- [x] **Legibility and announcement layer.** Simple mode has a scoped type floor (`.simple-label` 12px, `.simple-note` 13px/1.55) applied across the advice card, order box, watch list and the plan-step grid; one `aria-live="polite"` region mounted from first render announces runs, results and failures; `Button` sets `aria-busy`; `run-now` renders an elapsed clock (`formatElapsedSeconds`) and a reduced-motion-aware indeterminate bar while it works.
- [x] **Header organization.** Two desktop rows at one 40px control height (identity + trading day + `My Trades` + the filled `Run` + `More ▾`; then the four horizon tabs + `Simple | Pro` + light/dark), grouped overflow under *Learn*/*Tools*, a single-line tagline, and Simple-mode type on the segmented switches. The phone keeps its two thumb rows and the "Desk controls" sheet.
- [x] **A real brand mark.** `DeskLogo` draws three ascending PSE candlesticks in `currentColor` on the same tinted badge (36px phone / 44px desktop) in place of the placeholder `PS` monogram, and the identical geometry — `lib/deskMarkGeometry.ts`, five pinned invariants — is rasterised by `npm run icons` into `favicon.svg`, `favicon.ico` (16/32/48) and a full-bleed 180px `apple-touch-icon.png`, all three declared in `index.html`.
- [x] **Dependency-free icon generation.** `frontend/scripts/generate-icons.mjs` imports that geometry straight from the `.ts` module, supersamples the rounded rects, deflates the PNG scanlines with `node:zlib` and writes the ICO container by hand — three committed binaries that are reproducible byte-for-byte from one command, with no image library added.

### Tooling and documentation
- [x] `npm test` harness: `node --test "src/lib/__tests__/*.test.ts"` over the React-free `lib/` layer (no DOM, no dependencies).
- [x] **Golden snapshot harness (backend).** `SyntheticMarketFixture` (seeded synthetic tape, no network, no clock), `GoldenSnapshot` (approval-style compare against `tests/…/Application/Golden/*.json`, `PSEDESK_UPDATE_GOLDEN=1` records) and a `Skip`ped Release-only benchmark that prints elapsed time and allocations. It is the guard that let the performance pass touch every indicator without moving a desk number.
- [x] `.vscode/launch.json` + `tasks.json` (API launch/attach, Chrome, full-stack compound, build/test tasks, `backend: free port 5236` — SIGTERM, poll 6 s, SIGKILL escalation, `curl /health` proof) and `frontend/.env.example`.
- [x] `.gitignore` audited (build/test output, NuGet caches, local secrets, SQLite + sidecars + `.bak`, Node/Vite, `.env` overrides, editor/OS noise) with `git ls-files -i -c --exclude-standard` empty.
- [x] `README.md`, the three legal notices, `security_audit_report.md`, the six memory-bank files and `.clinerules/memory-bank.md`, with the sync rules recorded in `techContext.md`.

## Test inventory — 465 backend (464 executed + 1 skipped benchmark) + 66 frontend

Counts are the executed `dotnet test --list-tests` rows per class (parameterised rows included) and the `ℹ tests` figure per frontend file.

### Backend — `backend/tests/PseSignalDesk.Tests` (35 files)

| Layer | Suite | Cases |
| --- | --- | --- |
| Domain | `PseBoardLotServiceTests` — 15 tier/tick rows, bracket crossing, static band, plan repair, dated per-leg fees + commission floor, net R:R in pesos, break-even, EV, Quarter-Kelly, risk budget, gap fills, ADV, slippage | 78 |
| Domain | `LiveDriftPolicyTests` — standard errors, required gaps, multipliers, classification thresholds, hysteresis, `Describe` copy | 8 |
| Application | `QuantitativeFilterServiceTests` — indicators, stages, weighted score, mean+median liquidity floor, top-N cap, breadth, macro dials, rotation, disclosure trap wiring, point-in-time scoping, O(n) `ComputeEmaSeries` parity on every prefix, `IsAscendingByDate` | 38 |
| Application | `BacktestGoldenTests` — the four-horizon replay serialised against checked-in snapshots (plus a `Skip`ped benchmark that only prints time/allocations) | 5 |
| Application | `QuantitativeFilterGoldenTests` — `EvaluateStock`'s metrics against a checked-in snapshot | 1 |
| Application | `OrderTicketCalculatorTests` — sizing, verdicts, governor band, drift multiplier (incl. `HALTED` → 0), lot step-down and the risk-budget refusal, heat/sector/cash caps, slippage, execution guidance, all seven pyramid rules | 31 |
| Application | `PortfolioServiceTests` — open/close/stop rules, pyramiding, trims, drawdown posture, exposure, alert dedupe, drift-throttled tickets | 29 |
| Application | `ProbabilisticEdgeServiceTests` — base rate, barrier geometry, tilts, clamps, verdict gates, hit rates, learned edge, calibration | 22 |
| Application | `PseDisclosureClassifierTests` — categories, hard/soft traps, priority order, copy | 20 |
| Application | `TickerResearchServiceTests` — live/stored paths, plan validity, `DAY` clamp, ticket/edge parity, regime, holding advice | 17 |
| Application | `SignalOutcomeServiceTests` — entry gate, `UNFILLED`, stop tie, net R, scorecard, calibration, live-drift verdict + persistence, desk memory | 19 |
| Application | `BacktestServiceTests` — prefix causality, book limits, governor, slippage, exits, benchmark metrics, walk-forward | 13 |
| Application | `WaveTradePlanCalculatorTests` — plan levels, chase ceiling, entry status, ladder, trim size | 12 |
| Application | `SignalPipelineServiceTests` — discards, gates, drift, drops, persistence, `502` | 12 |
| Application | `SignalJsonParserTests` — fences, prose, brace-in-string, numeric strings, contract failure | 9 |
| Application | `LearnedEdgeModelTests` — activation gate, ±6pp tilt, Kelly rescale, probability clamp | 7 |
| Application | `SectorRotationServiceTests` — mapping, leaders/laggards, strength | 7 |
| Application | `MarketDirectionServiceTests` — pillar blend, clamp, bearish override, unreported dials | 6 |
| Application | `DailyQuotationImportServiceTests` — live/upload paths, overlays, unmatched tickers, size guard | 4 |
| Application | `MoneyManagerTrainingServiceTests` — promotion gate, purged split, inactive fallback | 3 |
| Infrastructure | `PseMarketDataProviderTests` — symbol normalisation, candle sanitation, zero-volume echoes, proxy fallback, FX staleness, macro dials, chart-cache reuse, `InvalidateCache`, distinct-constituent counting, ordered failure reasons, and the seven Edge-fallback cases (delisted Yahoo symbol, peso-`VALUE` volume, duplicate sessions, both-feed diagnostic, a full session from Edge, short-series diagnostic, `EnablePseEdgeHistory = false`) | 29 |
| Infrastructure | `MarketDataOptionsTests` — the binder's collection doubling reproduced and undone, order-preserving de-duplication, case-insensitive `DistinctNonBlank` | 4 |
| Infrastructure | `MarketDataCacheTests` — shared task under concurrency, no refetch inside the TTL, a failure is never served, TTL refetch, an abandoned caller cannot cancel, pre-cancelled does nothing, `Clear`, oldest-entry eviction, eviction safe against a concurrent `Clear()` | 9 |
| Infrastructure | `OllamaPromptBuilderTests` — horizon block, knowledge, feedback, cross-asset, desk memory, guardrails | 16 |
| Infrastructure | `OllamaSignalEngineTests` — stub `HttpMessageHandler` routed by the request body's `model`: a fast-model `404` keeps the ranking, one `404` per engine across two horizons (latch), a ranking `404` still surfaces as `SignalGenerationException`, a blank fast model makes zero fast calls | 4 |
| Infrastructure | `SqliteKnowledgeServiceTests` — reindex idempotency, guardrail retrieval, delete protection, search boosts | 8 |
| Infrastructure | `KnowledgeDocumentParserTests` — chunking, section refs, content hash | 6 |
| Infrastructure | `PseDailyQuotationParserTests` — header mapping, negatives, suspension, missing markers | 6 |
| Infrastructure | `DailySignalSchedulerRetrainTests` — weekday/hour trigger, per-horizon retrain, scope isolation | 5 |
| WebApi | `ComplianceGuardMiddlewareTests` — loopback gate, operator key (constant time), `/health` bypass, licensed passthrough | 18 |
| WebApi | `DeskWorkGateTests` — idle gate admits, second caller refused while held, release makes it reusable, exactly one of eight concurrent racers wins, unmatched `Exit()` throws | 4 |
| WebApi | `SignalsControllerRateLimitTests` — the four expensive actions carry `DeskRateLimitPolicies.Expensive` (reflection), five stored reads carry none | 9 |
| WebApi | `RequestCancellationMiddlewareTests` — 499 on abort, no write after cancellation | 4 |
| WebApi | `KnowledgeControllerTests` — upload attestation, protected-delete mapping | 2 |

### Frontend — `frontend/src/lib/__tests__` (6 files, React-free)

| File | Covers | Cases |
| --- | --- | --- |
| `plainAdvice.test.ts` | `plainAdvice` / `plainStatus` / formatter copy for every plan, verdict, scorecard and backtest field, plus the "not advice" wording | 35 |
| `queryCache.test.ts` | cache read/write, ref-counted abort, in-flight dedupe, prefix invalidation, failed-refresh retention | 8 |
| `legalConsent.test.ts` | first-run gate, versioned acceptance, stale notice, review clears flag, throwing storage, notice content (incl. the single-reader card) | 6 |
| `formatters.test.ts` | unit is never dropped (₱ / % / `: 1`), unreported renders `—` and never `0`, direction glyph for toned deltas, elapsed-clock formatting | 6 |
| `deskMark.test.ts` | the brand mark as geometry: three candles, strictly ascending bodies and wicks, every shape inside the 32-unit tile with the trio centred, neighbouring bodies never closer than 2 units, exactly one full-strength candle | 5 |
| `operatorKey.test.ts` | the `X-PseDesk-Key` fragment: every mutating verb carries it, no safe verb ever does, method case/padding normalised, blank key adds nothing, the value is trimmed, the header name matches `ComplianceOptions.OperatorKeyHeaderName` | 6 |

## Remaining

### Accuracy pillars — remainder (next up)

- [ ] **Show the drift verdict in the UI.** The throttle changes sizing and prints `LiveDriftPolicy.Describe` into the ticket warnings, but no DTO surfaces `LiveDriftStatus` itself, so the Signals/Portfolio pages cannot show a badge or a "halved until the book recovers" explanation. An additive `liveDrift` field on the ticket/book payloads plus a `types/signals.ts` union is the cheap next step.
- [ ] **Pillar 1 remainder** — `CorporateActionAdjuster` (`events=div,split` + `adjclose`; adjusted history for indicators and barriers, raw prices for levels), listing/delisting dates in `PseTickerInfo` for a point-in-time universe, and retention of the macro series (`^TNX`, `PHP=X`) instead of overwrite.
- [ ] **Pillar 2 remainder** — `TrainedHorizonModel.FeatureKeysJson` (+ loader refusal on schema mismatch) so a book cannot price a feature vector it was not trained on.
- [ ] **Pillar 5 remainder** — `InvalidationFirstPlanBuilder`, pipeline inversion (invalidation before target), `TradeExitLadderPolicy` as the single ladder, `OllamaTopPick` audit-only fields.
- [ ] **Pillar 4 remainder** — real walk-forward folds (disjoint rolling windows + fold report); today the replay refits one rolling model.
- [ ] **Frontend + docs** — mirror new server fields in `types/signals.ts` / `plainAdvice.ts`, extend `PseDisclosureClassifier` categories, and keep the README/memory-bank counts in sync (now 451 executed + 1 skipped / 55). `signalsApi.orderTicket` still has no caller and no `horizon` argument, so the manual sizing endpoint stays unthrottled by design.

### UI/UX follow-ups (2026-11-26 legibility pass)

- [ ] Real browser **and screen-reader** pass (VoiceOver / NVDA) over Simple and Pro from 320px to 2560px; the repo has no DOM test harness, so announcement behaviour is reasoned, not observed.
- [ ] **Validate the up/down colour convention with 3–5 PH retail readers** before any further colour work (several Philippine broker apps use red-up/green-down). Record the outcome in `productContext.md`; the `▲/▼` glyph shipped as the interim redundancy.
- [ ] Put the remaining Simple surfaces on the same type floor (`MarketDirectionHero` reason lines, `DropPanel` copy, `WelcomeSetupBanner`, `RidingNowStrip` chips); the advice-card path is done.
- [ ] Optional: a `GET /api/signals/run-status` read so a mid-run reload attaches to the in-flight run instead of starting a second one — needs a `systemPatterns.md` decision first because it adds a run-state resource.
- [ ] **Install surface is still missing** — no `site.webmanifest`, so there is no 192/512 install icon, no maskable icon and no `og:image` for link previews; the SVG + ICO + 180px touch trio covers browser tabs and iOS "Add to Home Screen" only.
- [~] **Remaining breakpoint + assistive pass** — the 2026-11-26 responsive pass now *covers* 320px and 2560px in CSS (new `xs`/`3xl`/`4xl` stops, `.shell` clamp gutters, container queries, `.tap`, `.scroll-x`), but only 390 (iframe), 768, 1024, 1280 and 1440-Pro were ever captured in a real browser. Still open: a visual capture at 320 and 2560, and the VoiceOver/NVDA run (never attempted — no DOM harness exists).
- [ ] **Phase 2 — page-body hierarchy** (decision deferred until the header is seen in a browser): one shared section-header shape for `AdviceCardList` / `SignalTable` / `DropPanel` (they hand-write the same `h2` + `text-[11px]` block), a visual weight ladder so the market-call hero outranks the "Nothing changed" strip, and deleting the duplicated empty-state copy in `DropPanel`.

### Performance pass — follow-ups

- [ ] Invalidate the download cache from the other manual entry points if a warm body ever proves stale there (`DailyQuotationImportService`, `POST /api/signals/train-all`); the daily run, research and replay deliberately reuse it.
- [ ] Confirm the Edge pagination split against a real outage — first page fatal, extra pages logged and skipped.
- [ ] Re-run the benchmark only in `Release`; `Debug` timings on this laptop are noise.
- [ ] Raise `MarketDataCache.DefaultMaxEntries` if the tracked universe grows far beyond ~58 names.

### Security remediation (`security_audit_report.md`, 2026-11-26 — 15 findings)

- **Done** — F-01/F-02 partly: `ComplianceGuardMiddleware` (loopback-only `PersonalLocal`, constant-time operator key on writes in `LicensedCommercial`), loopback `AllowedHosts`, `/health` bypass.
- **Done** — F-13: `DeskWorkGate` (`409` on a concurrent expensive job) plus `DeskRateLimitPolicies.Expensive` (`429` + `Retry-After` on `run-now`/`backtest`/`train`/`train-all`); `UseRateLimiter()` is placed after the compliance guard. Verified live (`503, 503, 429` and a concurrent `409`).
- **P0/P1 open** — F-03 prompt fencing for retrieved text; F-04 lookback ceiling (`lookbackDays` still reaches the provider unbounded); F-06 `KnowledgeController` error detail; F-07 chunk bounds + batched embeddings; F-05 mostly closed (`IsTradablePrice` bounds every peso identity, `GetBracket` itself still unguarded).
- **P2 open** — F-08 security headers/HSTS, F-09 unbounded free text (`PortfolioTrades.Notes` is still unfiltered, which is the DPA/PII ingress), F-10 regex timeout/response caps, F-11 SQLite file mode still `0644`, F-12 committed lock files + `NuGetAudit` + `PdfPig` bump, F-14 `GET` mutating state, F-15 raw model response persistence.
- **Auth** — still no user authentication and no origin check on unsafe methods; the operator key is a single-operator convenience (and a bundle-shipped key is readable by anyone who loads the page), so keep the desk on `127.0.0.1` and treat `LicensedCommercial` as "one trusted operator behind their own network boundary", not as multi-user identity.

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

- **The drift verdict is invisible in the UI.** Sizing changes and the ticket carries the `Describe` warning, but no DTO exposes `LiveDriftStatus`, so the pages cannot badge it.
- **Trailing dividend yield is unreported on the live tape.** Yields are parsed from the Yahoo chart `events.dividends` block, and Yahoo no longer publishes PSE charts; the Edge history carries prices and turnover only. The desk reports `null` (UI `—`) rather than assuming a yield, so the dividend tilt is dormant until a dividend source lands.
- **The live disclosure pillar currently yields nothing.** `edge.pse.com.ph/announcements/search.ax` answers `200` with a "no records found" page (2.4 KB) and `companyDisclosures/search.ax` returns rows with **no company column** (filing type, timestamp, form, reference only), so `AttributeFilings` cannot attach a headline to a ticker. The code path and its tests are intact; the upstream layout changed. Fixing it means reading a per-company disclosure listing (or the filing's own company id) rather than the market-wide table.
- **Edge history stops where the exchange stops printing.** `VLL` (newest bar 2026-06-01), `RRHI` (2026-07-10) and `VREIT` (2026-06-01) have no recent Edge sessions and are skipped honestly (`no live daily series … PSE Edge: 160 bar(s), newest 2026-06-01.`); `RCB` lags one session. Expect those names missing from a session until the exchange resumes them.
- **The drift read costs one full ledger pass.** `EvaluateLiveDriftAsync` loads the horizon's runs *with* their items on every book build (so `GET /api/signals/latest` pays for it too) and writes only when the verdict or the graded count changed. That is one query per book, not per ticket, but it is the expensive part of a list render; a `LIMIT 30` projection over graded rows is the obvious next optimisation.
- **Prices are unadjusted.** History is raw OHLC; a split inside a lookback still distorts ATR/RS/barriers until `CorporateActionAdjuster` lands.
- **Unverified live feeds.** Yahoo no longer serves PSE equities at all (every `{TICKER}.PS` → `404`, `PSEI.PS` → a sparse legacy series plus a live price), so the PSE tape now comes from PSE Edge daily history, which **is** verified end-to-end here (283 real bars per ticker, proxy index, `GET /api/research/JFC` `200`). Still unobserved live: anything past the LLM step (`OLLAMA_API_KEY` unset), the DQR live templates and the replay's PSEi benchmark (the index has no Edge series either). By design a failure is a `503`, never synthetic data.
- **The `MarketDataOptions` lists are additive.** `ConfigurationBinder` appends a bound section to the collection the class already initialized, so `appsettings.json` entries *extend* the code defaults and cannot remove one. `MarketDataOptions.Normalize()` (applied in `Program.cs` and again in the provider constructor) makes that harmless by de-duplicating, which is why the shipped file can keep listing the same 12 heavyweights the class already declares.
- **No UI automation.** Responsiveness and the Simple/Pro switch are verified by reading code and by `tsc`, not by a test.
- **Deliberately in-sample walk-forward.** `WalkForwardEdge` accepts its residual model by comparing it to the base rate on its own trades; it is a replay aid, not a validated out-of-sample result — and it never writes `TrainedHorizonModels`.
- **Bootstrap success path unobserved.** Only the failure branch (`cold-boot refit skipped`) has been seen; the `LEARNED` path still needs a network run.
- **SQLite decimals.** Amounts are `decimal` and read back through SQLite's `TEXT` provider; the desk treats values as advisory and rounds at the board lot.
- **Calendar, not exchange calendar.** The scheduler fires on weekdays 15:35 Asia/Manila and does not know PSE holidays.
- **Thin books start on the prior.** Below 25 held-out trades (training) or 20 graded picks (calibration) the desk prices off the structural prior with small tilts, so a fresh database reports `PRIOR_FALLBACK`.
- **Retrieval degrades silently by design.** No embeddings key, an embeddings failure or a BM25-only fallback all still retrieve through guardrails rather than erroring. As of 2026-11-26 `POST /api/embed` answers `401 unauthorized` for every model name with the project's key, so this deployment is lexical-only until the account has embed access.
- **Ollama Cloud model names are account-scoped, not free text.** `GET /api/tags` listed 25 chat models on 2026-11-26; anything else answers `404 "model … not found"`. Check the catalogue before trusting a name: the shipped fast model was `qwen3.5:4b`, which had never existed there. The engine now switches an unknown fast model off after one warning, but a wrong **ranking** model still fails the run with `502` (correctly — that run has no book).
- **Ladder duplication.** The break-even/trail ladder lives in three services; a change must be applied in all three (training, replay, wave plans).
- **Replay/training cost.** Both refetch long lookbacks for the liquid universe per call; the chart cache now absorbs the *repeat* requests (the four horizons of a session, research, a second refit within ten minutes), but a cold `train-all` on a fresh process is still the slowest operation in the app. The indicator work is linear in bars now, so what remains is upstream latency, not arithmetic.
- **The download cache is per process.** It lives in memory, so a restart is cold and two API processes would each download their own copies; there is no on-disk cache and no cross-process invalidation.
- **A failed extra Edge page is now silent.** `FetchDisclosuresAsync` still fails the run when the announcements page itself fails, but a failed second/third page is logged and skipped (the old `throwOnFailure` flag is gone). The suite pins the split; a real Edge outage has never been observed here.

- **An orphaned API host can hang while holding both loopback sockets.** A debuggee apphost whose debug session died can sit at PPID 1 accepting TCP but answering nothing (`GET /health` times out with 0 bytes), and it does **not** exit on SIGTERM — graceful shutdown waits on work that never completes. `kill -9` is the only reliable release; `backend: free port 5236` now does that automatically after a 6 s grace. The cause of the hang itself has never been captured (no dump taken), so treat it as "recover and relaunch", not "fixed".

## Verification log (newest first, observed output)

**2026-11-26 — fast-model fix (448 → 452; +4 executed rows).**

```
cd backend && dotnet build PseSignalDesk.slnx --nologo
  -> Build succeeded. 0 Warning(s) 0 Error(s)
cd backend && dotnet test PseSignalDesk.slnx --nologo
  -> Passed! - Failed: 0, Passed: 451, Skipped: 1, Total: 452 (net10.0), exit 0
dotnet test --filter FullyQualifiedName~OllamaSignalEngineTests
  -> Passed! 4/4 (86 ms)
curl -H "Authorization: Bearer <user-secret>" https://ollama.com/api/tags
  -> 200, 25 models (deepseek-v4.1-flash, glm-5.3-flash, mimo-v2.6-flash, ... no qwen3.5:4b)
curl POST https://ollama.com/api/chat  model=qwen3.5:4b
  -> 404 {"error": "model 'qwen3.5:4b' not found. Try glm-5.3-flash:cloud."}
curl POST https://ollama.com/api/chat  model=glm-5.3-flash   (the real fast-extraction prompt)
  -> 200 {"message":{"content":"{\"summary\": \"In a BULLISH regime, ICT shows the cleanest
      structure: STAGE_2 uptrend, +1.25% on 1.40x volume ...", \"trap_flags\": [...]}"}}
curl POST https://ollama.com/api/embed  model=nomic-embed-text
  -> 401 {"error": "unauthorized"}   (same for nomic-embed-text:latest and glm-5.3-flash)
```

**2026-11-26 — Yahoo-delisting pass (440 → 448; Edge history fallback + a cache race, +8 executed rows).**

```
cd backend && dotnet build PseSignalDesk.slnx --nologo
  -> Build succeeded. 0 Warning(s) 0 Error(s)
cd backend && dotnet test PseSignalDesk.slnx --nologo
  -> Passed! - Failed: 0, Passed: 447, Skipped: 1, Total: 448 (net10.0), exit 0
dotnet test --list-tests -> 448 rows (PseMarketDataProviderTests 29, MarketDataCacheTests 9, ...)
curl .../v8/finance/chart/JFC.PS  -> 404 {"chart":{"result":null,"error":{...,"description":"No data found, symbol may be delisted"}}}
curl '.../autoComplete/searchCompanyNameSymbol.ax?term=VLL' -> [{"cmpyId":"607",...,"symbol":"VLL"}]
curl '.../autoComplete/searchCompanyNameSymbol.ax?term=PSEI' -> []            (no index security exists)
curl '.../psei/form.do' -> 200, but /psei/search.ax is a company-news list, not a chart
fresh DB + POST /api/signals/run-now?date=2026-11-25
  -> 280 Edge chart POSTs, 284 Yahoo .PS GETs, 0 failures
  -> "Reading TEL from PSE Edge (283 daily bars)" ... for the whole universe
  -> "The PSEi index has no live history; reading the 11/25/2026 trend from an
      equal-weight proxy of 12 PSE heavyweights"
  -> "Skipping VLL: no live daily series for VLL.PS (HTTP 404 for VLL.PS. PSE Edge: 160 bar(s), newest 2026-06-01)"
  -> 400 Ollama Cloud is not configured   (ingestion and scoring completed)
GET /api/research/JFC -> 200 close 147.20, turnover 72828984, rsi14 49.67, asOfDate 2026-11-25
GET /api/research/RCB -> 200 (before the cache fix: 500 NullReferenceException in MarketDataCache.EvictOldest)
```


**2026-11-26 — feed-diagnostics pass (434 → 440; two failure-path defects, +6 executed rows).**

```
cd backend && dotnet build PseSignalDesk.slnx --nologo
  -> Build succeeded. 0 Warning(s) 0 Error(s)
cd backend && dotnet test PseSignalDesk.slnx --nologo
  -> Passed! - Failed: 0, Passed: 439, Skipped: 1, Total: 440 (net10.0), exit 0
     (skip: BacktestGoldenTests.Benchmark_reports_elapsed_time_and_allocations)
dotnet test --list-tests -> 440 rows over 32 test files
  (PseMarketDataProviderTests 22, MarketDataOptionsTests 4, MarketDataCacheTests 8, ...)
curl .../v8/finance/chart/PSEI.PS?range=1y -> 200, regularMarketPrice 5465.79, 1 timestamp
curl .../v8/finance/chart/{SM,TEL,BDO,JFC}.PS -> 404 each; then {AAPL,EPHE} -> 429
smoke: dotnet bin/Debug/net10.0/PseSignalDesk.WebApi.dll on :5299, /tmp/psedesk-verify.db
  POST /api/signals/run-now?horizon=WEEK -> 503
  "No PSEi index series is published by the live feed and the equal-weight heavyweight proxy
   could only use 0 of 12 constituents (6 required): PSEI.PS: 0 bar(s), 200 required;
   TEL: HTTP 404 for TEL.PS; SM: ...; GLO: HTTP 404 for GLO.PS. The live market feed is unavailable."
  (before the pass the same run said "0 of 24" and listed PSEI.PS twice)
```

The upstream probe is the important half of this pass: the `503` was **not** a desk bug. `PSEI.PS` still publishes a live level but no daily history, and no `.PS` stock resolves from this network, so the index/proxy resolution cannot be satisfied without fabricating bars — which the desk refuses to do by invariant.

**2026-11-26 — performance pass (cache, fan-out, linear indicators; 416 → 434, +17 executed rows and 1 skipped benchmark).**

```
cd backend && dotnet test PseSignalDesk.slnx --nologo
  -> Passed! - Failed: 0, Passed: 433, Skipped: 1, Total: 434 (net10.0), exit 0
     (skip: BacktestGoldenTests.Benchmark_reports_elapsed_time_and_allocations)
dotnet test -c Release --filter 'FullyQualifiedName~Benchmark_reports' --logger 'console;verbosity=detailed'
  before (stashed src, three runs): 6,554 / 6,327 / 6,224 ms, 1,708.4 MB allocated
  after  (three runs):              4,741 / 5,649 / 6,247 ms, 1,401-1,404 MB allocated
  both: DAY 48 trades 89,975.20 (-10.02%), WEEK 36 89,709.45 (-10.29%),
        MONTH 30 88,919.05 (-11.08%), YEAR 27 89,081.12 (-10.92%)
dotnet test -c Release --filter 'FullyQualifiedName~BacktestGoldenTests' with backend/src stashed
  -> Passed! - Failed: 0, Passed: 5, Total: 5   (goldens match the pre-optimisation source)
dotnet test --list-tests -> 434 rows over 31 test files
  (MarketDataCacheTests 8, PseMarketDataProviderTests 20, QuantitativeFilterServiceTests 38,
   BacktestGoldenTests 5, QuantitativeFilterGoldenTests 1)
git status --porcelain   -> 9 modified + 7 new paths; git stash list empty
```

The `Debug` figures taken mid-pass (7,274 ms → 9,633 ms) were re-measured properly before being believed: on this machine the wall clock swings ±30 % run to run, so the claim rests on allocations (a steady −18 %) and on the goldens proving the numbers did not move.

**2026-11-26 — live-drift throttle wiring (five new tests, 411 → 416).**

```
cd backend && dotnet test PseSignalDesk.slnx --nologo
  -> Passed! - Failed: 0, Passed: 416, Skipped: 0, Total: 416 (net10.0), exit 0
cd frontend && npm test            -> tests 49 · pass 49 · fail 0
cd frontend && npm run build       -> ✓ built in 248ms (exit 0)
dotnet ef migrations script 20261125212733_AddRobustLiquidityColumns
  -> ALTER TABLE "TrainedHorizonModels" ADD "LiveDrift" TEXT NOT NULL DEFAULT 'STABLE';
     ALTER TABLE "TrainedHorizonModels" ADD "LiveDriftGradedPicks" INTEGER NOT NULL DEFAULT 0;
cp pse_signals.db /tmp/... && sqlite3 (apply both ALTERs)
  -> PRAGMA table_info ends 22|LiveDrift|TEXT|1|'STABLE'|0, 23|LiveDriftGradedPicks|INTEGER|1|0|0
dotnet run with ConnectionStrings__DefaultConnection='Data Source=/tmp/psedesk-smoke.db'
  -> fresh DB: 11 migrations applied, ... 'AddLiveDriftState' applied
     GET /health 200, /api/portfolio 200, /api/signals/latest?horizon=WEEK 200,
     /api/portfolio/order-ticket 200 (unthrottled, no warnings)
     only failure: Bootstrap money-manager refit -> MarketDataUnavailableException (offline, by design)
```

The smoke run is what proves the real container binds the new constructor: `PortfolioService` gained an optional `SignalOutcomeService`, and `Program.cs` `AddScoped<PortfolioService>()` resolves it because `SignalOutcomeService` is registered too, so the throttle is live in production rather than only in tests.

Suite growth: 411 → 416 (+3 `SignalOutcomeServiceTests`: `HALTED` with exact gaps and a persisted verdict, prior-priced book stays `STABLE` without writing, 30-pick window cap; +2 `PortfolioServiceTests`: a halted book sizes zero shares with the `HALTED` warning, and a horizon-less ticket stays unthrottled). The `PortfolioServiceTests` fake `ISignalRepository` had to grow `GetTrainedModelAsync`/`SaveTrainedModelAsync`/`GetAllRunsAsync` backing lists — the previous stub answered `null`/empty, which silently made every drift check a no-op.

**2026-11-26 — accuracy pass (five pillars, 21 new tests).**

```
cd backend && dotnet test PseSignalDesk.slnx --nologo
  -> Passed! - Failed: 0, Passed: 411, Skipped: 0, Total: 411 (net10.0), exit 0
ls backend/src/PseSignalDesk.Infrastructure/Migrations
  -> 20261125212733_AddRobustLiquidityColumns.cs + .Designer.cs present
     Up: AddColumn MedianTurnover20dPhp TEXT(18,2) default 0, ActiveTradingDays20d INTEGER default 0
     Down: drops both
cd frontend && npm test            -> ℹ tests 49 · ℹ pass 49 · ℹ fail 0
```

Suite growth: 390 → 411 (+5 `PseBoardLotServiceTests`, +8 new `LiveDriftPolicyTests`, +4 `QuantitativeFilterServiceTests`, +4 `OrderTicketCalculatorTests`). Two mid-pass suites failed on the first attempt and drove real changes rather than test edits: the per-horizon `MinimumVolumeRatio` relaxations (MONTH/YEAR left too few eligible names for the replay and training fixtures) and the `NetRiskPhp` re-pin (9,979.73 → 9,979.64 once the commission floor was charged).

**2026-11-26 — documentation pass.**

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

