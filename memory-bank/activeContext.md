# Active Context — PSE Signal Desk

**Last updated:** 2026-11-28 (empty Ollama answer recovery: `done_reason`/`thinking` parsing, one no-thinking retry + per-run model latch, actionable `502` detail — backend **561 + 1 skipped**, frontend **87**)
**Last feature pass:** 2026-11-28 (empty Ollama answer recovery, following the forecast-accuracy Phases 1–3 pass)
**Current task:** The empty-answer recovery is implemented and verified (uncommitted per repo convention). Phase 4 of the forecast-accuracy plan (corporate-action split adjuster, dividend feed audit, and live DB migration/retrain + live `eval-llm` run once `OLLAMA_API_KEY` is set) remains open.

## Session note (2026-11-28) — an empty Ollama answer no longer fails the run

Reported as `Signal generation failed — Ollama Cloud returned an empty assistant message` with a
payload whose `message.content` was `""` and whose `message.thinking` stopped mid-sentence. Cause:
`num_predict` (4,096) caps the reasoning trace **and** the answer together, and
`deepseek-v4.1-flash` thinks by default when `think` is omitted, so a whole horizon's budget went
into the trace and no JSON was ever emitted. `OllamaSignalEngine` read only `content`, ignored
`done_reason`, and threw `SignalGenerationException` (`502`) — so every horizon failed the same way.

Backend only: `OllamaSignalEngine` + `OllamaCloudOptions` doc comments, three new tests, `README.md`
and this memory bank. No DTO, schema, fee, indicator, golden snapshot or dependency change.

| Command | Result |
| --- | --- |
| `cd backend && NUGET_PACKAGES=$PWD/.nuget/packages dotnet build PseSignalDesk.slnx --nologo -v q` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `dotnet test … --filter 'FullyQualifiedName~OllamaSignalEngineTests'` | `Passed! - Failed: 0, Passed: 11` — 8 → 11, **+3** |
| `dotnet test PseSignalDesk.slnx --no-build --nologo -v q` | `Passed! - Failed: 0, Passed: 561, Skipped: 1, Total: 562` (8 s) — 558 → 561, all Golden tests green |

**What changed**

1. **The envelope now says why.** `OllamaChatEnvelope` reads `done_reason` (`stop`, `length`, …) and
   `OllamaChatMessage` reads `thinking`, so an empty `content` is diagnosable instead of opaque.
2. **Recovery, not a hard failure.** On an empty answer the engine logs the stop reason, completion
   tokens, reasoning length and the upstream body (the body is no longer copied into the `502`
   detail), then retries **once** with `think: false` so the whole budget is available for the JSON
   contract. The model is remembered (`_reasoningOverrunModel`) and every later call on that engine
   instance sends `think: false` from the first request, so a corrective retry or an evaluation loop
   does not pay for the same overrun. (The typed client is resolved per run, so the latch is per
   run, not per process.)
3. **Provenance stays honest.** The recovery increments `ModelRetryCount` (it is added to the
   corrective-retry count, not substituted for it). A second empty answer throws an actionable
   `SignalGenerationException` naming `done_reason`, the token count and the two settings that
   prevent it. When thinking was already off, an empty answer is not retried — repeating the same
   request would only cost another call.

**Next**

- With `OLLAMA_API_KEY` set, one `run-now` per horizon to confirm the live account now returns a
  book (`modelProvenance.modelRetryCount` reports `1` for a run whose thinking call overran and was
  retried, `0` when `ReasoningEffort=off` avoided it), then Phase 4.
- If the reasoning trace regularly overruns, prefer `OllamaCloud:ReasoningEffort=off` (deterministic,
  no extra call) or raise `OllamaCloud:NumPredict`; the retry is a safety net, not a budget.

## Session note (2026-11-28) — Forecast accuracy remediation & LLM benchmark harness (Phases 1–3)

Prompted by "is our LLM properly trained to be accurate on its forecasts?". Audited the codebase and `pse_signals.db`: the Ollama Cloud LLM is an untrained prompt/RAG ranker with 0 graded picks in the store, while the 12-feature L2 logistic trainer had 6 mathematical/parity bugs (including `MONTH` promoted at `0.2502` Brier, worse than a 0.25 coin flip, because it compared only against a degraded constant train-slice mean).

| Command | Result |
| --- | --- |
| `dotnet build backend/PseSignalDesk.slnx --no-restore --nologo -v q` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `dotnet test backend/PseSignalDesk.slnx --no-build --nologo -v q` | `Passed! - Failed: 0, Passed: 558, Skipped: 1, Total: 559` (8 s) — 548 → 558, **+10**, all Golden tests green |
| `cd frontend && npx tsc --noEmit && npm test && npm run build` | `tsc` clean, `ℹ tests 87 · ℹ pass 87 · ℹ fail 0`, Vite build clean (`✓ 135 modules transformed`) |

**What changed**

1. **Statistical trainer & probability engine (Phase 1).** `MoneyManagerTrainingService` now charges date-correct CMEPA tax (`tradeDate`) and fills gap-down stops at `bar.Open` (`ResolveStopFillPrice`), enforces `MeetsLiquidityFloor` in `IsEligible`, supplies O(n) causal prefix `EmpiricalWinRate` to `ComputeStructuralPriorProbability` to remove negative intercept skew, scores inner/outer validation with the deployed `BlendWeight`/`MaximumTilt` residual formula, and requires `trainedBrier < min(baselineBrier, priorBrier)`, `trainedBrier < 0.25`, `trainedLogLoss <= min(baselineLogLoss, priorLogLoss)`, and `policyExpectancyR > 0`. `TrainedModelPresenter` reports the exact failing gate. `ProbabilisticEdgeService` suppresses bull/neutral learned tilts and Kelly scaling during `MarketRegime.BEARISH`. `SignalOutcomeService` uses `ResolveStopFillPrice` on post-entry gap-downs and date-correct fees. `SignalPipelineService` (fresh fetch) and `TickerResearchService` wire `BarrierStatistics` (and `LearnedFeatures` in research) without altering `StockDailyMetric` or breaking golden snapshots.
2. **LLM prompt, RAG grounding & F-03 safety (Phase 2).** `MarketDataMapper.ToCandidate` attaches `HorizonScore` and tick-valid ATR reference plans; `SignalPipelineService` and `OllamaPromptBuilder` preserve horizon-adjusted `ComputeHorizonScore` ordering (fixing the raw `QuantPreScore` mismatch), emit `DATA_QUALITY_WARNINGS`, fence untrusted disclosures/playbooks/notes/prior theses in `<untrusted_data>` tags (F-03), and align `MONTH`/`YEAR` missions with available inputs. `KnowledgeBase:MaxSnippets` increased `6 → 10` (`MandatorySnippetCount` `2 → 4`), `IndexBuiltInAsync` re-embeds unchanged built-in docs with null vectors once embeddings are configured, and `OllamaSignalEngine` supports opt-in `OllamaCloud:ArchivePrompts`.
3. **Offline LLM benchmark & UI transparency (Phase 3).** Added `PromptEvalService` + `POST /api/signals/eval-llm` (`DeskRateLimitPolicies.Expensive`, `DeskWorkGate`, 1..20 sessions) to grade LLM Top-10/Top-3 vs. Pure Quant Top-10/Top-3 causally on real forward OHLC bars. Updated `ScorecardDialog` to surface `rankingQuality` (LLM vs. Quant Top-3 + Spearman ρ) and `ModelTrainerPanel` to distinguish the local statistical odds model from the Ollama Cloud LLM ranker.

## Session note (2026-11-26) — the LLM pass: measure the ranking, then clean the contract

Prompted by "make the signal LLM more intelligent". The diagnosis was that **the model's rank order *is*
the product** (`SignalPipelineService` keeps the model's array order and answers `502` when no pick
survives), yet its skill had never been measured: no quant rank was persisted, no prompt fingerprint, no
model-vs-quant read, and the request was a plain chat completion with `format: "json"`, a hardcoded
`temperature: 0.1`, no `seed`/`num_ctx`, no `think`, and a prompt that asked for `rank`, `drift_status`
and `previous_rank` — fields the server validates and then overwrites.

**Slice A (measure) + Slice B.1–B.3 (contract and controls), backend + two frontend type files.** No new
dependency, no new architectural layer, and every server-owned invariant is intact (prices, ticks, band,
sizing, probability, EV, verdict, drift, drops still server-computed; `502` when no pick survives;
citations still verified against the retrieved set). The raw-response archive and the replay endpoint were
deliberately **not** built: they need the archive this pass only prepares.

| Command | Result |
| --- | --- |
| `cd backend && dotnet build PseSignalDesk.slnx --nologo -v q` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo -v q` | `Passed! - Failed: 0, Passed: 548, Skipped: 1, Total: 549` (8 s) — 538 → 548, **+10** |
| `dotnet ef migrations add AddSignalRankingProvenance …` | `20261126124346_AddSignalRankingProvenance` — 12 `SignalRuns` columns + 2 `SignalItems` columns, all defaulted so legacy rows read "unmeasured" |
| `cd frontend && npx tsc --noEmit` | clean (exit 0) |
| `cd frontend && npm test` | `ℹ tests 87 · ℹ pass 87 · ℹ fail 0` — unchanged, no test touched |

Not run: a live `run-now` (still needs `OLLAMA_API_KEY`, unset in this shell), so whether Ollama Cloud
honours the schema, `seed`, `num_ctx` and `think` is **unverified against the real endpoint**. The schema
path is therefore defended in code: a `400` on schema-constrained output is retried once in plain JSON mode
and the schema is latched off for the process.

**What it built**

1. **Provenance travels with the book.** `SignalRun` gained `EngineName`, `ModelName`, `PromptHash`
   (SHA-256 of system + user prompt), `ModelTemperature`, `ModelSeed`, `ModelRetryCount`,
   `ModelLatencyMs`, `PromptTokens`, `CompletionTokens`, `PromptCharacters`, `SchemaConstrained` and
   `FastExtractionApplied`; `IOllamaSignalEngine.GenerateSignalAsync` now returns
   `OllamaSignalResult(Output, OllamaGenerationMetadata)` and `ApplyModelProvenance` stamps the run.
   `SignalRunDto.ModelProvenance` exposes it (null on pre-pass rows).
2. **Quant rank is stored per pick.** `SignalItem.QuantPreScore` + `QuantRank` (1-based position in the
   candidate table the prompt prints: score, then turnover, then ticker). `SignalItemDto` carries both.
3. **The scorecard can now answer "did the model add anything?".** `RankingQualityDto` on
   `SignalScorecardDto`: runs measured, picks with a quant rank, mean Spearman rho (model order vs the
   quant order *re-ranked within the picks it made*, so the coefficient stays in `[-1, 1]`), and the
   realized win rate / mean R of each side's top three. Null until a graded run stores a quant rank.
4. **The response contract is a JSON schema, not just JSON mode.** `OllamaPromptBuilder.ResponseSchemaJson`
   / `FastExtractionSchemaJson`; `format` carries the schema, `drop_category` is an `enum`, and only
   `ticker`/`conviction_score`/`entry_price`/`stop_loss`/`target_price`/`thesis`/`bear_case` are required.
5. **The dead fields are gone.** `OllamaTopPick.Rank`/`DriftStatus`/`PreviousRank` were deleted with their
   parser validation, the system-prompt contract and the correction-prompt reminder; rule 8 and the TASK
   line now say *the array order IS the ranking* and ask the model to compare the whole candidate set
   before choosing. A model that still echoes `rank`/`drift_status` no longer burns the corrective retry.
6. **Sampling and context are controlled.** `OllamaCloudOptions` gained `Temperature` (0.1), `Seed` (42),
   `NumCtx` (16 384 — the old ~8k-token prompt had no explicit window), `NumPredict` (4 096), `TopP`
   (null = provider default), `ReasoningEffort` (blank = no `think`; `off`/`on`/`low`/`medium`/`high`/`max`
   mapped, unknown values omitted rather than sent) and `UseStructuredOutput` (true). Only configured knobs
   are serialised, and `RequestTimeoutSeconds` moved 120 → 180 because a thinking model needs headroom.

**Decision.** Measurement first, and measurement must cost nothing at run time: provenance and quant rank
are written from data the pipeline already has, and the scorecard reads the stored ledger. The prompt
archive and the replay endpoint stay out until there is a graded sample to replay against — an eval harness
with no ground truth is just latency.

**Next**

- Set `OLLAMA_API_KEY`, probe `GET /api/tags` for the strongest reasoning model, then run one session per
  horizon and compare `GET /api/signals/scorecard` → `rankingQuality` before/after, with
  `modelProvenance.promptHash` proving the inputs are comparable.
- Then: prompt archive behind `Signals:ArchivePrompts` (default off) + `eval-prompt-replay` under the
  `Expensive` policy; slice 3's "trap flags are unenforced this session" warning; the F-03 fencing patch for
  retrieved/untrusted text; `KnowledgeBase:MaxSnippets` 6 → 8–10 so more `pse_rules` sections are guaranteed.

## Session note (2026-11-26) — `More ▾` no longer hides under the desk (frontend only)

Prompted by "it is hiding under" on the header's `More ▾` panel. The cause was **layering, not
positioning**: `index.css`'s `.panel` applies `backdrop-blur`, so every panel — including `<header>`
— is a stacking context. The menu's `absolute right-0 z-40` therefore ranked only *inside the header*,
and because the header was `position: static; z-index: auto` it painted before the `.panel` cards that
follow it in `<main>` (`HowToReadStrip`, `WelcomeSetupBanner`, `#market-call`, `AdviceCardList`), so the
open menu was swallowed by whichever card sat underneath it. One file (`components/TerminalHeader.tsx`),
**no new dependency, no API or data change, no second component tree**:

1. **The header owns a rung.** `<header>` is now `panel relative z-30` — above the in-flow panels
   (`z-auto` = 0) it overlaps and below every portalled overlay (`Dialog` / `Select` / `Tooltip` are all
   `z-50`). The menu keeps `z-40`, so it still clears the header's own tab row.
2. **The menu closes like a menu.** One `document` listener pair (inside `MoreMenu`) closes it on the
   next outside `pointerdown` — capture phase, so it lands before whatever is underneath acts — and on
   `Escape`, which also returns focus to the summary. A press *inside* the menu is left to the item
   handler. No React state, so no extra renders and no fight with `closeThen`.

| Command / probe | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 87 · ℹ pass 87 · ℹ fail 0` (≈0.36 s, exit 0) — unchanged, no test touched |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 135 modules transformed`, `✓ built in 239ms`, exit 0 |
| built-bundle audit | `grep -c "panel relative z-30" dist/assets/index-*.js` → `1`; CSS unchanged at `61.85 kB / 11.55 kB gzip` |
| real built app — `dist/` on `127.0.0.1:5399`, legal flag seeded, 1280×940 iframe, headless Chrome `elementsFromPoint` | **fix kept:** `NATURAL menuOnTop=true`; with a `.panel` card inserted under the header `CARD+FIX => menu P/DIV.absolute z-40 > SECTION.panel, menuOnTop=true`. **Defect reproduced** by forcing the old `static/z-auto` back: `CARD+BEFORE => SECTION.panel > menu, menuOnTop=false` |
| same page — dismissal | `pressInsideStaysOpen=true · pressOutsideClosed=true · escapeClosed=true · focusBackOnSummary=true · reopens=true` |

Not run: `dotnet test` (nothing in `backend/` changed) and a Safari/Firefox pass. No automated
regression test was added: the suite is DOM-free `node:test` over `lib/`, and a class-string assertion
would only restate the JSX. The layering rule now lives in `systemPatterns.md` under *Conventions*.

## Session note (2026-11-26) — wayfinding pass (frontend only)

Prompted by "make the frontend the most intuitive, easy-to-understand UI/UX possible". Six defects were
diagnosed from the code, not from taste: a first-time reader landed on a call bar and a list with no
in-place explanation (the guide was buried in `More ▾` / the ☰ sheet); there was **no** keyboard
navigation and no skip link; Simple mode said "Today's trade signals" over a *stored past* session;
the guide said "Press **Record This Trade**" while the sheet said "Practise This Trade" (contradicting
the rule that Simple mode always says *Practise* — the account is paper); rows ended in a bare `▸ Why?`
that was **hidden below `sm`**, so a phone showed only a chevron; and the list-header eyebrow did three
jobs in one 100-character line.

Frontend only: **6 new files**, **13 edited**, plus `README.md` and this memory bank. **No backend file,
no API contract, no new dependency, no new architectural layer**, and every server-owned number, verdict
and label is unchanged.

| Command | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 87 · ℹ pass 87 · ℹ fail 0` (≈0.30 s, exit 0) — 71 → 87, **+16** |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 135 modules transformed`, `✓ built in 232ms`, exit 0; `KeyboardShortcutsDialog` ships as its own **1.65 kB** chunk |
| built-CSS audit | `.kbd{border,background,min-width:1.5rem,font-size:var(--text-2xs),…}` emitted once — the keycap is a real rule, not a utility pile at each call site |

Not run: a real-browser capture (headless Chrome still hangs on the seeded app here) and `dotnet test`
(nothing in `backend/` changed). Every shortcut is verified by the pure resolver's `node:test` cases
rather than by pressing keys in a browser.

**What it built**

1. **`lib/shortcuts.ts` — one source of truth.** `SHORTCUT_GROUPS` (Move / Act / Change what you see /
   Help) is rendered by the shortcut sheet, quoted by the guide and reflected into each control's
   `aria-keyshortcuts` via `keysForAction(action)`, so a key cannot be documented-but-unbound or
   bound-but-invisible. `resolveShortcut(input, context)` is pure and DOM-free with ordered rules:
   any modifier → `null` (⌘R stays the browser's); a typing target → `null`; a sheet open → only `?`;
   a non-printing key → `null`. `isTypingTarget` reads `tagName`/`isContentEditable`/`role` structurally
   (an open picker `listbox` owns its letters; a *closed* combobox trigger does not), so all of it is
   testable without a DOM. `Esc` is documented in the Help group **without** an action —
   Radix already owns it, and wiring it would double-close.
2. **`components/KeyboardShortcutsDialog.tsx`** renders that list as keycaps, reachable from `?`, the
   `More ▾` menu and the phone ☰ sheet, and preloaded by `preloadKeyboardShortcutsDialog()`.
3. **`lib/howToRead.ts` + `components/HowToReadStrip.tsx`** — three steps above the list whose buttons
   *are* the actions (jump to the call, open the best-ranked name, open My Trades). Dismissal is one
   remembered flag (`psedesk.howToReadDismissed`) with the `legalConsent` storage contract (injected
   storage, silent on blocked quota/private mode), and the footer's "How to read this screen" restores it.
4. **`App.tsx`** wires one global `keydown` listener through `resolveShortcut` + `isTypingTarget`, with a
   computed `anyDialogOpen` (every sheet, plus the legal gate) so a keystroke never reaches the page
   behind a sheet; `runShortcut` calls the same setter each visible button calls. Added a skip link and
   a `<main id="main-content">` wrapper, and a footer row with `How it works G`, `Keyboard shortcuts ?`
   and `How to read this screen`.
5. **Honest copy.** The Simple list heading names the stored date (`Trade signals · Wed, Nov 25`) instead
   of "Today's" whenever a past session is on screen; `DAY`'s friendly eyebrow lost its "Today's";
   `Record` → **Practise** in the guide, the blotter button and the blotter's `take` tab in Simple mode;
   rows now end in `View plan ▸` at every width with an `sr-only` "Opens the full plan in a detail sheet";
   and `describeNextStep()` prints one sentence under the header naming the control a reader should press
   (it never turns an empty session into a nudge to trade).

**Decision.** Shortcuts accelerate a visible control; they never replace one. Every key in the map has a
button on screen, the sheet is reachable by clicking, and the handler refuses to fire while the reader is
typing or while a sheet is open — so nothing in the desk becomes keyboard-only knowledge.

## Session note (2026-11-26) — abuse-protection hardening

Triggered by *"does it have abuse protection?"* after the security audit: the desk had one hourly
budget (the expensive run/train endpoints) and no origin check, so `GET /api/research/{symbol}`,
knowledge ingestion and any CORS-simple cross-site write were uncapped. This pass closes that
without adding a dependency or an architectural layer. **4 new source files, 3 new test files, 8
edited source files, 2 edited tests; no API contract change outside the new `400`/`403`/`429`
answers.** `dotnet build` → 0 warnings/0 errors; `dotnet test` → **538 passed + 1 skipped (539
total, +42)**; `npm test` → **71 passed**. Live smoke on `:5421` proved the origin guard, the two new
budgets and the lookback `400`; `:5422` proved `401 ×4 → 429 + Retry-After: 300 → correct key still
429`.

**What changed**

1. `ComplianceOptions` gained `ResearchRequestsPerHour` (120, clamp 1–10 000) and
   `KnowledgeRequestsPerHour` (10, clamp 1–1 000); `DeskRateLimitPolicies` gained `Upstream` and
   `Model`; `Program.cs` registers all three fixed windows and one `OnRejected`. The research action
   and the two knowledge writes opt in with `[EnableRateLimiting]`; `SignalsControllerRateLimitTests`
   reflects over all three controllers.
2. `DeskRequestLimits` caps `lookbackDays` at 1 825 days and answers `400` outside `1..1825` on
   `backtest`, `train` and `train-all`; the helper is unit-tested independently of the controller.
3. `KnowledgeUploadPolicy` allows only `.md`/`.markdown`/`.txt`/`.pdf`; the controller rejects anything
   else with `400` before reading the body.
4. `FrontendCorsOptions` is the single origin list shared by `AddCors` and
   `ComplianceGuardMiddleware`; unsafe methods from an unknown `Origin` (or a browser
   `Sec-Fetch-Site: cross-site` with no Origin) get `403`. Same-origin writes are recognised by
   authority, so an API-served terminal still works.
5. `OperatorKeyThrottle` (singleton, in memory, five failures / five minutes per source address)
   makes the constant-time key comparison rate-limited; a correct key resets the record. The
   middleware still sits before `UseRateLimiter`, so a refused `403` burns no hourly budget.
6. Docs: README, THIRD_PARTY_NOTICES, `systemPatterns.md`, `techContext.md`, `progress.md` and this
   file. The no-`ForwardedHeaders` caveat is now explicit: do not put a personal desk behind a
   same-machine reverse proxy.

**Next:** the remaining data-truth slices 3–4 are unchanged. If the desk is ever deployed as
multi-user, the next abuse work is per-caller (not global) partitioning behind a trusted proxy and a
real identity provider — neither is needed for the loopback personal posture.

## Session note (2026-11-26) — port 5173 held by an orphaned dev server

Prompted by `error when starting dev server: Error: Port 5173 is already in use` (followed by *"Waiting for the
debugger to disconnect"*) when launching the Vite dev server. `vite.config.ts` pins `strictPort: true`, so Vite
never falls back to 5174 — a leftover server is a hard failure, not an inconvenience. Tooling only
(`.vscode/tasks.json`, `.vscode/launch.json`) plus this memory bank: **no application code, no API contract, no
dependency, no test count moved**.

| Command | Result |
| --- | --- |
| `lsof -nP -iTCP:5173 -sTCP:LISTEN` | `node 85742` on `[::1]:5173`, with an `ESTABLISHED` peer from the VS Code process |
| `ps -o pid,ppid,stat,lstart,etime,command -p 85742` | `PPID 85727`, started `17:22:34`, 45 min old; parent 85727 is `npm run dev` with **PPID 1** — orphaned, and it still answered `curl / → 200` |
| `kill -TERM 85742 85727` | both gone; `lsof` empty; a real `bind()` probe on `127.0.0.1:5173` **and** `[::1]:5173` → OK; `curl` → `000` |
| `frontend: free port 5173` against an occupied port | `freeing :5173 — SIGTERM to 95119` · `:5173 is free now` · `:5173 refuses connections`, exit 0 |
| same task, port already free | `:5173 is already free` · `:5173 refuses connections`, exit 0 |
| same task, SIGTERM-ignoring listener | SIGTERM → 6 s poll → `:5173 still held by 95767 after 6s — SIGKILL` → `:5173 is free now`, exit 0 |
| `cd frontend && npm run dev` after the fix | `VITE v8.3.8 ready in 274 ms`, `Local: http://localhost:5173/`, `GET /` → **200**; server then stopped and the port left free |
| config checks | `tasks.json` parses (6 tasks); `launch.json` parses with 4 configurations + the compound |

**What changed**

1. **`frontend: free port 5173` (new task)** — the mirror of `backend: free port 5236`: SIGTERM the listener,
   poll for 6 s, escalate to SIGKILL, re-check, then finish with a real `curl` probe. Killing the *node child*
   is sufficient — the orphaned `npm` parent exits with it.
2. **`Frontend: Vite dev server (frees :5173 first)` (new launch config, `pwa-node`)** — runs `npm run dev`
   with that task as `preLaunchTask`, so an F5 on the frontend cannot hit the error again, and a
   `serverReadyAction` opens a debuggable Chrome once Vite prints `Local: …`.

The port is **free now**, so the operator's own launch can bind immediately; nothing is committed.

## Session note (2026-11-26) — per-company PSE Edge disclosures (data-truth slice 2)

The trap pillar (`SRO_DILUTION`, `TENDER_OFFER_PIN`, `TRADING_HALT_RISK`) was inert because disclosure
**attribution** was a headline-to-company-name match over a market-wide table that names the issuer only
sometimes — a live run attributed filings to **3 of 60** names, so a rights offering titled just "Rights
Offering" reached no ticker. This slice fetches each screened candidate's own Edge page instead. Backend only:
**1 new source file** (`Infrastructure/MarketData/PseEdgeDisclosureSource.cs`), **7 edited**
(`IMarketDataProvider` — one *defaulted* method so no existing fake broke; `QuantitativeFilterService`
(`SelectDisclosureFanout`); `SignalPipelineService` (`EnrichCompanyDisclosuresAsync`); `MarketDataOptions`
(`DisclosureCompanyFanoutLimit = 25`); `PseEdgeHistorySource` (`ReadCompanyId` delegates to the shared
`EdgeSymbolSearch`); `PseMarketDataProvider`; `appsettings.json`), **1 new test file + 3 extended**. No
dependency, no DTO, no fee, no golden snapshot, and nothing in `frontend/`.

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo -v q` | `Passed! - Failed: 0, Passed: 496, Skipped: 1, Total: 497` (9 s) — 485 → 497, **+12**, no existing test edited |
| `dotnet test --list-tests` | `PseEdgeCompanyDisclosuresTests` 8 (new), `QuantitativeFilterServiceTests` 38 → 40, `SignalPipelineServiceTests` 12 → 13, `PseMarketDataProviderTests` 33 → 34 |
| live harness (`/tmp/livecheck`, real exchange, outside the repo) | JFC/SM/BDO/TEL resolved to an id, `ZZZZ` → **skip not failure**; **JFC 47 / BDO 41 / TEL 40 / SM 22 filings** with real dates; ledger `Ok — 4 of 5 screened name(s) filed through their own Edge page` |
| same harness, full market-wide tape (`FetchAsync`) | `session 2026-11-25, 60 names, **3** with attributable filings` (BDO, SM, BPI) — the fallback's true ceiling |
| live instance on `:5299` (`--no-launch-profile`, real `pse_signals.db`, stopped after) | `GET /api/research/SM` → **200** (2 588 B), `disclosures: []` — correct point-in-time: the only SM row is dated `2026-11-26`, after the stored session; `GET /api/health/feeds` → **200**, six feeds, `storedSessionCount: 1` |
| `lsof -ti :5299` | empty — the operator's `:5236` instance was not touched |


**The planned route was dead.** The plan said `companyPage/companyDisclosures.do?cmpy_id=<id>` and defaulted
the template to *empty* so nothing could regress — which would have shipped an inert fan-out. The probe showed
**HTTP 404**. The working call is `GET companyDisclosures/search.ax?keyword={cmpy_id}` (`keyword` is the hidden
field the page's own search form posts): the company's newest-first 50-row table, `Total 424` for JFC. Because
the route is now confirmed, the fan-out ships **on**; `DisclosureCompanyFanoutLimit = 0` is the documented
rollback (zero per-company requests).

**What it built**

1. **`PseEdgeDisclosureSource`** — `internal sealed record EdgeFiling(DateOnly, string Headline, TimeOnly?
   FiledAtPht)`, `EdgeSymbolSearch.ReadCompanyId(json, ticker)` (shared with the history source) and
   `FetchAsync(ticker, sessionDate, ct)`: resolve the id, read the company table, filter to the rolling window
   (`MaxRollingDisclosureLookbackDays`), newest-first, cache both responses 10 min
   (`edge-symbol:{ticker}`, `edge-company-disclosures:{id}`), record success/skip/failure on
   `FeedSource.PseEdgeDisclosures`. Every failure is soft (`MarketDataUnavailableException`/`JsonException` →
   empty) because the market-wide attribution still stands.
2. **`IMarketDataProvider.FetchCompanyDisclosuresAsync(tickers, sessionDate, ct)`** — a *defaulted* interface
   method returning an empty map, so no existing fake broke; `PseMarketDataProvider` implements it with
   `Parallel.ForEachAsync` at 8-way concurrency into a `lock`-guarded dictionary, then records **one
   aggregated** ledger line rather than 25.
3. **`QuantitativeFilterService.SelectDisclosureFanout(metrics, horizon, limit)`** — the liquidity-floor
   candidates in pre-trap score order (`ComputeHorizonScore`, then turnover, then ticker), `Take(limit)`,
   `limit <= 0` → empty. Tested against `SelectCandidates` so the two lists cannot drift.

4. **`SignalPipelineService.EnrichCompanyDisclosuresAsync`** — runs **before** `MergeRollingDisclosuresAsync`
   because attribution must precede disqualification: a rights offering only the company page names has to be in
   the candidate's disclosures before the traps are read. Merges through
   `MarketDataMapper.Deserialize/SerializeDisclosures` + `MergeRollingDisclosures`; any failure logs a warning
   and keeps the market-wide attribution, so a disclosure outage never fails a run (proven by
   `RunAsync_disqualifies_a_name_whose_rights_offering_only_the_company_page_names`).
5. **Two parser bugs the probe exposed in the fallback, fixed.** The disclosure table prints
   `Nov 26, 2026 05:20 PM`, which the numeric-only date regex missed — so the *timestamp* survived as a subject
   and every row was back-dated to the session — and the PSE form/report numbers after the subject (`13-1`,
   `C08413-2026`) became subjects too, turning one filing into three. `AnnounceDateRegex` + `BuildDate` read both
   formats and `FilingIdentifierRegex` drops the identifier cells; pinned by
   `FetchAsync_reads_the_live_disclosure_table_date_and_ignores_its_identifier_columns` against the captured
   markup.

**Next (the rest of the plan — not started)**

- **Slice 3 — surface it.** When disclosures answer but attribute nothing, emit the existing
  `SignalDtos.Warnings` line "trap flags are unenforced this session", and flag an index-proxy fallback the same
  way. The ledger reports it after the fact; this puts it on the screen the picks are read from.
- **Slice 4 — measure the model.** Migration `AddQuantRankToSignalItems` (`QuantPreScore`, `QuantRank`); a
  model-vs-quant scorecard block (hit rate, Brier, mean realized R, model Top 10 vs quant Top 10 over the same
  sessions and barriers); opt-in prompt archive (`Signals:ArchivePrompts`, default off) +
  `POST /api/signals/eval-prompt-replay` behind the existing `Expensive` policy.
- **Not observed yet:** a full live `run-now` (needs `OLLAMA_API_KEY`, unset here, and it would overwrite the
  stored WEEK run), so the fan-out's ledger line has only been seen from the harness.
- **Still out of scope:** `CorporateActionAdjuster` (splits still distort ATR/RS63/barriers), the dividends
  feed, embedding access, and the F-03 prompt-fencing patch (`security_audit_report.md:1561`, drafted but
  unapplied — keep it separate so a prompt change is never conflated with a data fix).

## Session note (2026-11-26) — feed health ledger (data-truth slice 1)

Prompted by the assessment "are our data sources reliable and is the model properly trained/measured?" — the short answer was *the model is untrained and its skill unmeasured, and the worst data defect is silent rather than loud*. This pass is the observability half: nothing about the pipeline's behaviour changed, it can now **say** what it read.

Frontend untouched. **4 new source files** (`Application/Models/FeedHealthModels.cs`, `Application/Interfaces/IFeedHealthRegistry.cs`, `Infrastructure/Diagnostics/FeedHealthRegistry.cs`, `WebApi/Controllers/FeedHealthController.cs`) plus **4 edited** (`PseMarketDataProvider`, `PseEdgeHistorySource`, `OllamaEmbeddingClient`, `Program.cs` — the three services only to accept an optional registry) and **2 new + 1 extended test files**. No indicator, plan level, fee, prompt, DTO or existing status code moved; no dependency added.

| Command | Result |
| --- | --- |
| `cd backend && dotnet build PseSignalDesk.slnx -c Debug --nologo -v q` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo -v q` | `Passed! - Failed: 0, Passed: 484, Skipped: 1, Total: 485` (8 s) — **464 → 484**, +20, **zero existing tests edited** |
| live instance (`ASPNETCORE_URLS=http://127.0.0.1:5299 dotnet run --no-launch-profile --no-build`, real `pse_signals.db`, stopped afterwards) | `GET /api/health/feeds` → **200** `application/json`: `overallStatus: Unknown` (no run yet), `storedFactsAvailable: true`, `latestStoredSessionDate: 2026-11-25`, `storedSessionCount: 1`, `configuration {pseEdgeHistoryEnabled: true, quotationReportTemplates: 0, embeddingsConfigured: false, embeddingModel: nomic-embed-text}`, all six feeds in enum order with `status: Unknown` |
| `lsof -ti :5299` after shutdown | empty — the test instance was stopped; the operator's own `:5236` instance was **not** touched (the first attempt died on `address already in use` because `dotnet run` ignores `ASPNETCORE_URLS` without `--no-launch-profile`) |

Not run here: a **full live `run-now`**, so the ledger has not yet been observed carrying real PSE Edge numbers (that needs `OLLAMA_API_KEY`, unset in this shell, and would overwrite the stored WEEK run). The disclosure-degraded state is proven by a stubbed test instead — `FetchAsync_reports_the_disclosure_feed_degraded_when_edge_answers_with_no_filings` drives the exact `200`/"no records found" page the live site serves.


**What it built**

1. **`IFeedHealthRegistry`** (Application) with four recording verbs — `RecordSuccess`, `RecordDegraded`, `RecordFailure`, `RecordSkipped` — plus `BeginRun()` and `Snapshot()`. `FeedHealthRegistry` (Infrastructure/Diagnostics, **singleton**, in memory) is thread-safe under one lock and keeps counters, last item count, a 240-char last message and the last success/failure timestamps per `FeedSource`.
2. **Status is derived from counters, never from the last write.** 58 symbols are fetched concurrently, so `failures > 0 && successes == 0` → `Failed`; `failures > 0 || degraded > 0` → `Degraded`; `successes > 0` → `Ok`; only skips → `Degraded`; nothing observed → `Unknown`. A **skip** is not a fault (Edge listing no security, a Yahoo `404`, an unpublished DQR template) — but a feed that only ever skips is degraded.
3. **Hooked at every upstream, one terminal observation per run.** Edge history records success/skip/failure at the parse seam; `FetchDisclosuresAsync` records one verdict for the whole scrape (`no filing rows` / `N filings, none attributable` → degraded, attributed → ok); charts record `404` as skip and non-2xx/timeout/transport as failure; the macro dials report `0/3` → failed, `1–2/3` → degraded; the DQR records not-published/skip, non-2xx/failure and unparseable/degraded; `OllamaEmbeddingClient` records the `401` that forces lexical retrieval.
4. **`GET /api/health/feeds`** returns the six feeds, `overallStatus` (computed from the **load-bearing three** only, so a dark EPHE quote cannot paint the desk red), the stored-session facts and the configuration that explains an `Unknown`. `NoStore`; an unreadable store yields `storedFactsAvailable: false` with a fixed sentence instead of a `500` or a file path; the API key is never reflected (a test serialises the report and asserts the key string is absent).
5. **Run-scoped vs process-scoped counters.** `BeginRun()` is the first line of `FetchAsync`: the five tape feeds start blank for the new session, while `Embeddings` (exercised at indexing time) and the timestamps survive. Cache hits still count, because recording happens after the body is parsed rather than at the HTTP layer — otherwise the second of four horizon runs would report the tape as unobserved.

**Next** — slice 2 has since shipped (see the note at the top of this file); slices 3 and 4 remain as described
there.

## Session note (2026-11-26) — "clickable items should show hand cursor"

Tailwind v4's preflight dropped the `button, [role="button"] { cursor: pointer }` rule that v3 shipped, so every
control on the desk — the filled Run button, the four horizon tabs, a Simple signal row, a drawer toggle, a `?`
pill, the trading-day picker, the `More` summary — computed `cursor: default` under a mouse and signalled
clickability only through its hover tint. Frontend only: `src/index.css` plus this memory bank. **No backend
file, no API contract, no new dependency, no new architectural layer**, and no server-owned number, label or
verdict moved.

| Command | Result |
| --- | --- |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ built in 240ms`, exit 0 (CSS `59,339` bytes) |
| `cd frontend && npm test` | `ℹ tests 71 · ℹ pass 71 · ℹ fail 0` (503 ms, exit 0) |
| built CSS audit | one rule emitted in `@layer base`: `button:not(:disabled),[role=button]:not([aria-disabled=true]),[role=tab]:not(…),[role=option]:not(…),[role=menuitem]:not(…),a[href],summary{cursor:pointer}`; `.cursor-pointer` utility still present for the Pro row/card |
| layer probe (headless Chrome `--dump-dom` over the built stylesheet) | `button=pointer` · `button[disabled]=default` · utilities-layer `cursor: default` opt-out = `default` · utilities `cursor: not-allowed` = `not-allowed` · `summary=pointer` · `[role=tab]=pointer` · `[role=tab][aria-disabled=true]=auto` → **utilities still beat the base rule**, so a call site can override and a dead control never shows a hand |
| real built app (`dist/` copy served on `127.0.0.1:5311`, legal gate + welcome seeded, 1280×940) | 19 rendered controls measured: `Open desk controls`, `Run Latest Screen`, `My Trades`, `More▾`, `How it works` / `Check a Stock` / `Past Results`, all four horizon tabs, `☀Light` / `☾Dark`, `Simple` / `Pro`, `Technical details` and `Review the notice` all compute `cursor=pointer`; the **disabled** trading-day picker computes `cursor=not-allowed` |

**What changed**

1. **`src/index.css` — one `@layer base` rule** beside the existing interactive-surface block: the hand on
   `button:not(:disabled)`, `[role="button"|"tab"|"option"|"menuitem"]:not([aria-disabled="true"])`, `a[href]`
   and `summary`. Disabled and `aria-disabled` controls keep the arrow (and their own
   `disabled:cursor-not-allowed`), and a focusable tooltip badge (`<span tabIndex={0}>`) is deliberately left
   as `auto` — it explains, it does not act.
2. **Nothing else needed the class.** Every interactive surface in `src/` is already a `<button>`, a Radix
   trigger that renders one, a `<summary>`, `<a>`, or a row/card that carries its own `cursor-pointer`
   (`SignalTable`'s `<tr>`, `ProSignalCard`'s `<li>`), so the rule is the single source of truth instead of 100+
   call-site classes. The two `<summary>` elements keep their explicit `cursor-pointer` as intent documentation.

**Decision.** The cursor is a base-layer default, not a per-component prop. It matches how `.type-*`, `.shell`,
`.scroll-x` and `.tap` already work here: one rule with a doc comment in `index.css`, utilities free to win.

## Session note (2026-11-26) — "clicking a row should open a modal"

The Simple list expanded a row **in place** (a single-open accordion) while every Pro row already opened
`TickerDetailDialog`. Two ways to reach the same numbers, and the default surface disagreed with the
reader's expectation. Frontend only: `AdviceCardList.tsx`, `TickerDetailDialog.tsx`, `SignalTable.tsx`,
`TerminalHeader.tsx` (one hint string), `App.tsx`, plus `README.md` and this memory bank. **No backend
file, no API contract, no new dependency, no new architectural layer**; every figure is the same server
field, only laid out in the sheet.

| Command | Result |
| --- | --- |
| `cd frontend && ./node_modules/.bin/tsc --noEmit` | clean (exit 0) |
| `cd frontend && npm test` | `ℹ tests 71 · ℹ pass 71 · ℹ fail 0` (219 ms) |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ built in 249ms`, `TickerDetailDialog` still its own 19.92 kB chunk |
| `grep -rn 'aria-haspopup' src` | one site, `AdviceCardList.tsx:385` (the row button) |
| headless Chrome (CDP) on `localhost:5173` with the running API (`:5236`, run 17 / WEEK / 10 items) | **0** dialogs before the click; **3** rows carry `aria-haspopup="dialog"`; clicking row 1 opened `[role="dialog"]` titled **“Philippine National Bank”** containing the wave plan, `Ticket: 50 shares`, the win-chance meter (42%), the *Show the math* drawer and *Sizing & odds*; screenshot `/tmp/psedesk-row-modal.png` |
| same session, Close path | the sheet's `Close` → **0** dialogs, list intact, the next row opened (“Aboitiz Equity”) immediately; a synthetic `keydown: Escape` (Radix `DismissableLayer`) → **0** dialogs |
| Pro at 500px (`psedesk.viewMode=pro`) | **10** phone cards; clicking a card opened the same ticker sheet; its `Take` button opened **only** the portfolio blotter (no ticker sheet), i.e. the stop-propagation works |
| same session, the math drawer after the `MathRows` narrowing | *Show the math behind this pick* opened (`aria-expanded="true"`) with **“The pick”** and **“The ticket”** columns (`Quarter-Kelly`, `Risk governor` present) and **no** `RSI14` / `Raw indicators` / `Volume vs 20D` / `Filings on file` / `Why it moved`; the deep drawer still carries `RSI 14`, `Weinstein stage`, `Rank trajectory`, `Recent disclosures` — each figure once; `/tmp/psedesk-math-drawer.png` |

**What changed**

1. **`AdviceCardList`** — `SignalRow` is now a single `<button aria-haspopup="dialog">` that calls
   `onSelect(ticker)`; the accordion panel, `openTicker` state, `useId`/`aria-expanded`/`aria-controls`
   and the row's `onTakeTrade` prop are gone (so `App` no longer passes it). The component keeps
   exporting the pieces the modal reuses: `PlanSteps`, `WavePlanGrid`, `WavePlanSteps`, `Meter`
   (now exported), `OrderBox`, and `MathRows` (was `TechnicalRows`; narrowed to the sizing maths so the
   sheet never prints the indicator table twice).
2. **`TickerDetailDialog`** (the modal every row opens) — its Simple branch now carries everything the
   accordion used to: `WavePlanSteps` (four-step roadmap, legacy fallback intact) instead of the three
   price tiles, the hold line, the `OrderBox`, the full-width win-chance `Meter`, the
   “Desk confidence N / 100 · …” sentence, and a *Show the math behind this pick* drawer holding
   `MathRows` — the sizing maths only (“The pick” / “The ticket”). The sheet's existing *Sizing & odds*
   drawer, thesis, pre-mortem, the full indicator table with rank history and filings, and the citations
   follow unchanged, so nothing was dropped and nothing is printed twice.
3. **`SignalTable`** — the Pro phone card (`ProSignalCard`) is clickable anywhere and opens the same
   modal; its `Take` button and trap-flag badge call `stopPropagation` so they keep their own actions.
4. **Copy** — the list header reads “tap a row to open its details” (was “for the why”), the header's
   Simple/Pro hint now says “tap a row to open its detail sheet”, and the row keeps its `▸ Why?`
   affordance because the sheet *is* the why.

**Decision.** One detail sheet, not two. The product copy used to promise “row = the why, Full Details =
the sheet”, but the sheet already held the same thesis, plan, order box and odds — a second modal would
have been a second code path over one payload. The row now opens that sheet, and the accordion's unique
content moved into it.

## Session note (2026-11-26) — typography, colour theory and whitespace

Prompted by a straight design critique of the built UI: 9–11px type on the reading path, prose lines running
well past 100 characters, 0.12–0.28em tracking used as decoration, sub-AA grey, and card padding that changed
at every call site. Frontend only — `src/index.css` plus every `.tsx` under `src/` (28 files, mostly class
lists). **No backend file, no API contract, no new dependency, no new architectural layer**, and no
server-owned number, label or verdict moved.

| Command | Result |
| --- | --- |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 131 modules transformed`, `✓ built in 264ms`, exit 0, `dist/assets/index-*.css` 59.30 kB (11.0 kB gzip) |
| `cd frontend && npm test` | `ℹ tests 71 · ℹ pass 71 · ℹ fail 0` (411 ms, exit 0) |
| built-CSS audit | `.type-eyebrow`, `.type-badge`, `.type-caption`, `.type-body`, `.type-lead`, `.type-title`, `.type-figure`, `.measure`, `.measure-tight`, `.measure-wide` all emitted with their `62ch` / `46ch` / `78ch` caps; `--text-2xs:.6875rem`, `--tracking-label:.08em` present; `[data-view-mode=simple] .type-badge{letter-spacing:0;font-size:.75rem}` present; `text-slate-*` utility count **0**; `.tracking-label` **utility** gone (nothing uses it — the variable it fed is still referenced by `.type-eyebrow`) |
| `grep -rn -E 'text-\[(8\|9\|10\|11\|12)px\]\|text-slate-\|text-white' src` | 3 hits, all comments / one `@apply` inside `index.css` — **0** in `.tsx` |
| `grep -rn 'uppercase' src` | only `.type-eyebrow`'s own `text-transform` plus one code comment; no `uppercase` utility on any element |
| `grep -rln 'type-badge' src` | 15 files: `index.css`, `VerdictBadge`, `DriftBadge`, `RegimeStrip`, `SignalTable`, `DropPanel`, `TerminalHeader`, `AdviceCardList`, `MarketDirectionHero`, `PortfolioDialog`, `TickerDetailDialog`, `ResearchDialog`, `ModelTrainerPanel`, `KnowledgeBaseDialog`, `WelcomeSetupBanner` |

Not run: a real-browser capture (headless Chrome still hangs on the seeded app here) and `dotnet test` (no
backend file changed), so the visual result is verified through the emitted CSS and the type roles, not pixels.

**What it built**

1. **The ramp.** Five sizes with an 11px floor (`--text-2xs`), and role classes instead of repeated class
   strings: `.type-eyebrow` (the only legal uppercase + 0.08em), `.type-badge` (**new** — 11px/600/0.02em and
   deliberately *not* uppercase, for chips that carry words like “Qualifies (Full)”), `.type-caption`,
   `.type-body` (13px/1.65 + `text-wrap: pretty`), `.type-lead`, `.type-title`, `.type-figure`. Colour stays
   composable at the call site.
2. **Measure.** `.measure` / `.measure-tight` / `.measure-wide` in `ch` (62/46/78) replace `max-w-3xl`
   (48rem); the old cap let an 11px paragraph run ~130 characters per line. `text-wrap: balance` on h1–h3.
3. **Colour.** `text-slate-400/500/600` → `text-fg-muted/subtle/faint`, `text-slate-100…300` → `text-fg(-muted)`,
   `text-white` → `text-fg` (21 sites). The `fg` ramp is re-measured for light theme rather than inverted, and
   light-theme `mint-400` moved `#0b8a5f` → `#087a53` (4.02:1 → 4.95:1). `--color-slate-*` stays for badge tints.
4. **Whitespace.** Inner cards `px-3.5 py-3`, panels `p-4 sm:p-5`, rows `px-4 py-3.5`, stacks `space-y-1`
   (warnings) / `gap-2.5` (steps) / `gap-4`–`gap-5` (sections). The table header, footer and both dialog
   surfaces moved to the same rhythm.
5. **Controls are sentence case.** `Button`, `TabsTrigger`, the segmented controls, the `More` summary and the
   menu items dropped `uppercase tracking-label` and sit at 12px (13px in Simple via `.ui-button`/`.ui-tab`).

**Decision.** `.type-eyebrow` keeps uppercase because a rubric label (“PLAN & SIZE”, “TAPE · WEEK HORIZON”) is
scanned, not read; anything that carries a *sentence* — a friendly verdict chip, a table field label, a form
label, a menu row — uses `.type-badge`, `.type-caption` or `.type-body` instead, so no sentence is shouted at 11px.

## Session note (2026-11-26) — "keep it ultra simple: a list of trade signals"

Prompted by "as a common person I just expect to see a list of trade signals, expanding it will tell me
the details of why — if the user understands trades then we offer pro views right?". The answer is yes,
and `Simple | Pro` already existed; the *Simple* screen was what disagreed with it: a full market-call
hero (traffic light, 10–90 gauge, reasons, sector chips, trust line, three summary cards) followed by
three **already-expanded** plan cards and a "show more" toggle. A first-time reader had to read three
trade plans before choosing one.

Frontend only: `AdviceCardList.tsx`, `MarketDirectionHero.tsx`, `App.tsx`, `TerminalHeader.tsx`,
`lib/plainAdvice.ts`, `lib/__tests__/plainAdvice.test.ts`, `RidingNowStrip.tsx` (**deleted**), plus the
docs and memory bank. **No backend file, no API contract, no new dependency, no new layer**; Pro, the
ticker/research dialogs and every server-owned number are untouched.

| Command | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 71 · ℹ pass 71 · ℹ fail 0` (≈0.38 s, exit 0) — 66 → 71 with five `describeSignalRow` cases |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 131 modules transformed`, `✓ built in 211ms`, exit 0 |

Not run: a real-browser capture (headless Chrome still hangs on the seeded app here) and `dotnet test`
(nothing in `backend/` changed).

**What it built** *(the accordion described below was replaced by the row-opens-modal pass at the top of this file — the row content and `describeSignalRow` are unchanged, the panel now lives in `TickerDetailDialog`)*. `AdviceCardList` now renders **every** buyable pick in rank order as one
`<button aria-expanded>` row — rank, company name, ticker, verdict, entry chip, drift, `Buy … · Target …
· Cut-loss …` and the entry-timing line — with a single-open accordion panel holding the thesis, the
four-step `WavePlanGrid` (or the legacy `PlanSteps`), the hold line, the `OrderBox`, the win-chance meter,
the confidence line, the pre-mortem, `Practise This Trade` / `Full Details` and the *Show the math*
drawer. The top-three slice, the “Show N more” button and `RidingNowStrip` are gone; “inside the band”
became an `entryChip` on the row, sourced from the server's `EntryStatus`. `MarketDirectionHero` is a
**call bar** (traffic light + instruction + one sentence) with the gauge, reasons, sector flows, trust
line, summary cards and institutional tiles in one *Why this call* drawer whose collapsed pills preview
PSEi, cash and horizon. `describeSignalRow` in `plainAdvice.ts` composes only existing copy. The
`Simple | Pro` switch keeps its labels/persisted value and gained a `title` + `aria-description` hint.

**Decision.** The market call stays in Simple, but as one bar: dropping it leaves a list context-free on
a risk-off day, and keeping the hero re-creates the wall the list replaces.

## Session note (2026-11-26) — typeface is Outfit ("use Outfit font from google font instead")

Frontend only: `src/index.css`, `index.html`, `scripts/fetch-fonts.mjs`, `public/fonts/` (the new woff2 +
`OFL.txt`; the Google Sans Flex binary was deleted), and the three notices. **No backend file, no API
contract, no new dependency** (the fetcher uses Node's global `fetch`), no indicator/plan/fee/golden
moved. The vendoring contract is unchanged: the face is committed and preloaded, never CDN-linked.

| Command | Result |
| --- | --- |
| `node scripts/fetch-fonts.mjs` | `outfit-latin.woff2 32228 bytes` · `OFL.txt 4389 bytes`, exit 0; MD5 `98ccc2b…` |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ built in 285ms`; `grep -c @font-face dist/assets/index-*.css` → **1**; `dist/fonts/` carries only the woff2 + `OFL.txt` |
| `cd frontend && npm test` | `ℹ tests 87 · ℹ pass 87 · ℹ fail 0` (≈0.37 s) |
| `python3 -m http.server` + `curl` | `/` `200 text/html`; `/fonts/outfit-latin.woff2` `200 font/woff2` (32,228 bytes) |
| headless Chrome, built app over `http://127.0.0.1:8732` (legal gate seeded, dark Pro) | 1280px and 430px both render in the new face with **no wrap/clip regression**; screenshots `/tmp/outfit-desktop2.png`, `/tmp/outfit-mobile2.png` |
| `document.fonts` probe | 1 face, `Outfit loaded`, `document.fonts.check("40px 'Outfit'")` → `true`; advance `A` = **27.84 px** at 40 px vs **25.78** for `ui-sans-serif, system-ui`; computed `body` family starts with `Outfit` |

**The measurement that shaped the change.** The upstream `Outfit[wght].ttf` cmap was parsed (throwaway
Node script, no new dependency): 360 glyphs, and **no** `₱` (U+20B1), **no** `▲`/`▼` (U+25B2/U+25BC).
`→` (U+2192) *is* in the font, but the served `latin` `unicode-range` omits it, so it still falls back.
Outfit's `latin-ext` sheet is the trap: its `unicode-range` lists `U+20B1` (inside `U+20AD-20C4`) yet the
font carries no such glyph — shipping it would add 14,760 bytes for zero usable glyphs. So `SUBSETS`
stays `latin` and `₱`/`▲▼`/`→` come from the system font, exactly as they did with Google Sans Flex.

**What it built**

1. **`public/fonts/outfit-latin.woff2` + `OFL.txt` (committed).** Outfit, variable `100 900`, `latin`
   subset, 32,228 bytes (14.8 kB lighter than Google Sans Flex), OFL 1.1 with the licence beside it
   (OFL §1 requires the licence to travel with a redistribution).
2. **`src/index.css`.** One `@font-face` (`font-display: swap`, the API's own `unicode-range`) and
   `--font-sans: 'Outfit', 'Inter', ui-sans-serif, …` — Inter kept as a local-install fallback,
   `--font-mono` untouched (tickers stay monospace).
3. **`index.html`.** `<link rel="preload" as="font" type="font/woff2" crossorigin>` for the latin file
   (`crossorigin` is required on a font preload even same-origin).
4. **`scripts/fetch-fonts.mjs`.** Re-pointed at `family=Outfit` / `ofl/outfit` and `outfit-<subset>.woff2`;
   it still reads the CSS with a desktop UA, **verifies the `wOF2` signature** before overwriting (an HTML
   error page can never replace a good binary), writes the licence, and `--check` prints the upstream URLs.
5. **The notices stay true.** `PRIVACY.md` §5 states the font is bundled rather than CDN-linked (the only
   page-load requests are to the desk's own API); `THIRD_PARTY_NOTICES.md` §4's *Bundled assets* table names
   Outfit and the measured coverage; `README.md` documents the asset and the command. No copy in the legal
   gate changed, so `CURRENT_LEGAL_VERSION` did **not** need a bump.

Out of scope (not done): a wordmark typeface for the logo, per-weight `.woff2` instancing, and any
change to `--font-mono` (tickers keep their monospace). Google Sans Flex was the face from earlier the
same day until this switch; no other CSS token or type-scale step moved.

## Session note (2026-11-26) — compliance hardening (is this government compliant?)

Prompted by "is this government compliant? am I not violating any laws as a developer?". Answered with a
technical compliance review rather than an opinion, then closed the three gaps that review called *one
action away* from mattering. **No indicator, plan level, fee, prompt, golden or API contract changed** —
the four endpoints kept every status code they had and gained two.

| Command | Result |
| --- | --- |
| `cd backend && NUGET_PACKAGES=$HOME/.nuget/packages dotnet build PseSignalDesk.slnx --nologo` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Passed! - Failed: 0, Passed: 464, Skipped: 1, Total: 465` (8 s) — 451 → 464 with 13 new cases |
| `cd frontend && npm test` | `ℹ tests 66 · ℹ pass 66 · ℹ fail 0` (≈0.42 s) — 60 → 66 with `operatorKey.test.ts` |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ built in 189ms` |
| live smoke (`:5411`, throwaway DB, every upstream pointed at `127.0.0.1:9`, `ExpensiveRequestsPerHour=2`) | `GET /api/signals/dates` → **200** (uncapped); `POST run-now` → **503, 503, 429**; the 429 body is `application/problem+json` with `Retry-After: 3600` |
| live gate proof (`:5412`, Python listener that accepts and never answers, 30 s timeout) | first `run-now` hangs inside the fetch, second concurrent `run-now` → **409** `application/problem+json` "The desk is already running a job" / `"first -> 503"` after the slow upstream timed out |

**What changed, in the order it matters**

1. **`LICENSE` (new).** Not open source: one personal non-commercial copy on a machine you control; no
   redistribution, hosting for others, selling or derivative distribution; "as is" + liability cap;
   termination on breach; explicit that market data is not licensed. The repo previously had only
   `THIRD_PARTY_NOTICES.md`, so `DISCLAIMER.md`'s warranty cap bound nobody.
2. **`DISCLAIMER.md` §7 (new) + §7/§8 renumbered to §8/§9**, and **`legalConsent.ts`** gained the matching
   "Written for one reader" card with `CURRENT_LEGAL_VERSION` bumped `2026-11-v1` → **`2026-11-v2`**, so
   every browser re-reads the gate. This is the one compliance point no code can enforce: Simple mode is
   deliberately *instruction-shaped* ("buy in this band", "cut here", "lock break-even at +1R"), which is
   clarity for one self-user and the exact shape of personalised advice for a third party. The text now
   says so, names RA 8799 / the SRC, and forbids running anyone else's money through the paper book.
3. **The licensed path is now operable.** `lib/operatorKey.ts` (new, React-free) decides the header; the
   `signalsApi` client spreads it in on `POST`/`PUT`/`PATCH`/`DELETE` from `VITE_OPERATOR_API_KEY`, and
   reads never carry it. `Compliance:AllowedHosts` replaces the loopback-only `AllowedHosts` **only** when
   `DeploymentMode` is `LicensedCommercial`, applied in `Program.cs` before `Build()` — previously a
   licensed deployment behind a domain answered `400` to everything, and the SPA answered `401` to every
   write.
4. **The expensive endpoints are bounded.** `DeskWorkGate` (singleton semaphore, `WebApi/Services`) makes
   `run-now`/`train`/`train-all` mutually exclusive (`409`), and `AddRateLimiter` gives those three plus
   `backtest` a fixed one-hour window (`Compliance:ExpensiveRequestsPerHour`, default 30, clamped 1–1000)
   answering `429` + `Retry-After` through an `OnRejected` ProblemDetails writer. `UseRateLimiter()` sits
   **after** `ComplianceGuardMiddleware`, so a refused caller never consumes the operator's budget.
   `DeskRateLimitPolicies.Expensive` is one constant shared by `Program.cs` and the attributes.

**Deliberately not done** (the audit's P0/P1 perimeter work, still open under *Remaining* in
`progress.md`): authentication on every endpoint (F-01), the origin/`Sec-Fetch-Site` guard (F-02), prompt
fencing for uploaded text (F-03), security headers + TLS enforcement (F-08), the `lookbackDays`/price
clamps (F-04/F-05), `0600` on the SQLite file (F-11). None of them is needed to keep the personal posture
honest; all of them are pre-hosting gates.


## Session note (2026-11-26) — responsive pass, 320px → 2560px

Prompted by "make the terminal fully responsive from 320px phones to ultra-wide monitors". Frontend only:
`index.css` + 19 components (`App.tsx`, `ui/Button`, `ui/Dialog`, `ui/Tabs`, `ui/TechnicalDisclosure`,
`TerminalHeader`, `SignalTable`, `AdviceCardList`, `DropPanel`, `RegimeStrip`, `MarketDirectionHero`,
`BacktestPanel`, `ModelTrainerPanel`, `RiskSizerBar`, `PortfolioDialog`, `ResearchDialog`,
`TickerDetailDialog`, `ScorecardDialog`, `BeginnerGuideDialog`). **No backend file, no API contract, no new
dependency, no new architectural layer** — every rule is addendum CSS or a class list.

| Command | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 60 · ℹ pass 60 · ℹ fail 0` (0.15–0.19 s, exit 0) — run before, during and after |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 131 modules transformed`, `✓ built in ~190ms`, exit 0 — re-run after the `App.tsx` grid/Suspense restructure, after the sheet edits and after the final `xs:` sweep |
| built-CSS breakpoint audit | `@media (width>=30/40/48/64/80/96/120/160rem)` all emitted → `xs`, `3xl`, `4xl` are real |
| built-CSS utility audit | `.tap{` inside `@media (pointer: coarse)`, `.scroll-x{`, `.shell{`, `.text-fluid-hero`, `pb-safe`, `2.75rem`, `container-type` all present |
| container audit | `@container` root + `(width>=24/28/32/36/42/56/64/80rem)` rules for `@sm/@md/@lg/@xl/@2xl/@4xl/@5xl/@7xl` |
| `git status --porcelain` | the 20 intended modified paths; nothing deleted, no new file |

Not run: a real-browser capture at 320px or 2560px (headless Chrome still hangs on the seeded `dist/` page
here — see `techContext.md`), so the two extremes are reasoned from the emitted CSS, not observed pixels.
`dotnet test` was not re-run because no backend file changed.

**What it built.** Three new stops in `@theme` (`xs` 30rem, `3xl` 120rem, `4xl` 160rem); `.shell` as the
single `clamp()` gutter (replacing `px-3 py-4 sm:px-5 sm:py-6`) plus `env(safe-area-inset-left/right)` on
`body`; `.text-fluid-hero` / `.text-fluid-metric` for the two sizes that were wrong at both ends; `.scroll-x`
(momentum, `overscroll-behavior-x: contain`, 6px bar, `:not(.no-scrollbar)` so the two utilities cannot fight
over `scrollbar-width`); `.tap` (44px floor, `pointer: coarse` only) on `Button`'s base and the compact row
actions; `@container` roots on `DialogContent`, the advice card and `DropPanel`, with the inner grids moved
from viewport prefixes to `@sm:`/`@md:`/`@lg:`/`@xl:`/`@4xl:`/`@5xl:`/`@7xl:`; `dvh` sheet heights, the
`3xl` width steps, a prose-measure override for the guide sheet, `xs:grid-cols-3` on every 2→6 tile ladder,
and a `3xl:grid-cols-9` tape / `3xl` Pro-sidebar (`22rem`, `26rem` at `4xl`) whose panels then reflow by
container rather than by window.

**Two traps found and recorded** (`systemPatterns.md` conventions + `techContext.md` gotchas): `cn()` cannot
arbitrate `hidden` against a breakpoint display utility (the later media query wins, so a control meant to
stay hidden until `md` reappears at `sm` — put the visibility on a wrapper), and a container variant silently
falls back to its base column count when no ancestor is a container root.

Next: the visual capture at 320 and 2560 plus the VoiceOver/NVDA pass (both still open under *Remaining* in
`progress.md`); nothing is committed — the operator asked for no commits unless explicitly requested.

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

Rendering: Pro → `frontend/src/components/SignalTable.tsx`; Simple → `AdviceCardList.tsx` (one row per signal under `MarketDirectionHero`'s call bar; the row opens the detail sheet); detail sheet → `TickerDetailDialog.tsx`; catalog search → `ResearchDialog.tsx` + `lib/pseNicknames.ts`.

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
- **Terminal.** **Simple** (a compact call bar, then one row per trade signal; the row opens the ticker's detail sheet) and **Pro** (dense table and tiles) read one payload; persisted light/dark, bottom-sheet dialogs, card fallbacks from 320px up, a zero-dependency SWR cache with intent preloaders, and a first-run legal gate.
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
- **F-04 — lookback clamp:** closed at the HTTP edge. `DeskRequestLimits` rejects `lookbackDays` outside `1..1825` on `backtest`, `train` and `train-all` with a `400`, so one request cannot ask for a century of bars; `PseMarketDataProvider.NormalizeLookback` itself still has a floor but no ceiling, so a non-HTTP caller could pass a huge value.
- **F-05 — price bounds:** partially closed. `PseBoardLotService.IsTradablePrice` (`> 0`, `<= MaximumTradablePrice = 1_000_000`) now guards `ComputeLegFeePhp`, `ResolveStopFillPrice`, every net-risk/reward/P&L identity and `ProbabilisticEdgeService`; what is still missing is a guard inside `GetBracket`/`RoundToValidTick` themselves and a rejection path in the WebApi for an absurd `entry`/`stop`/`target` payload.
- **F-06 — error detail:** `KnowledgeController` still returns `ex.Message` to the client.
- **F-07 — chunk/embed bounds:** `KnowledgeDocumentParser.ChunkPlainText` still bypasses `SplitLong`, and `OllamaEmbeddingClient.EmbedAsync` still sends every chunk in one request.
- **F-08 – F-15** — security headers/HSTS, unbounded free text (`title`, tags, notes, file name), regex timeouts/response caps, SQLite file mode, lock files + `NuGetAudit` + a `PdfPig` bump, `GET` mutating state, unbounded raw model response persistence.
- **F-01/F-02 remainder** — there is still no user authentication and no per-user throttling; the cross-site write guard and the three hourly budgets now cover drive-by writes and loop abuse, so keep the desk on `127.0.0.1` until a real identity provider lands.

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

