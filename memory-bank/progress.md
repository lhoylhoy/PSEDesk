# Progress — PSE Signal Desk

**Status snapshot:** 2026-11-28 — a four-horizon probabilistic PSE research desk: live-only ingestion behind a per-URL download cache with one fan-out per session (**PSE series now read from PSE Edge daily history**, because Yahoo stopped publishing PSE equities), deterministic point-in-time screening and tick-grid plans, server-owned odds / EV / Quarter-Kelly, one self-trained money-manager book per horizon (promoted only through a held-out gate, λ picked on a nested inner split), server-sized order tickets that settle real fees, a paper blotter with legal pyramiding and partial trims, a forward-outcome ledger, a benchmarked causal replay priced at its own dates, on-demand research for any ticker, an official-DQR foreign pillar, cross-asset dials, a SQLite RAG library (8 documents / 46 sections), and a Simple/Pro terminal that reflows from 320px to 2560px.

**Latest verification:** **561 backend tests green + 1 skipped (562 total) / 87 frontend tests green** (2026-11-28; the newest pass is the **empty Ollama answer recovery** — `done_reason`/`message.thinking` parsing, one no-thinking retry plus a per-model per-run latch, actionable `502` detail — on top of the 2026-11-28 **forecast accuracy Phases 1–3** pass, the 2026-11-26 **LLM measurement + structured-output** pass, the `More ▾` **stacking fix**, the frontend **wayfinding pass**, abuse-protection hardening, per-company PSE Edge disclosures, the feed-ledger, hand-cursor, row-opens-modal, typography/colour/whitespace, signal-list, typeface, compliance-hardening, responsive, brand-mark, header-organization, legibility, fast-model, Yahoo-delisting, feed-diagnostics, performance, accuracy and live-drift passes — each keeps its own section below).
 Backend behaviour changed only in the LLM seam: the request now carries a response schema and explicit sampling/context options, an empty reasoning-only answer is retried once without thinking, the stored run records how it was produced, and each pick records the quant rank it displaced. Everything past the LLM step is still unverified live here because `OLLAMA_API_KEY` is unset in the shell; the open security remediation is listed under *Remaining*.

## Latest verification (2026-11-28 — an empty Ollama answer no longer fails the run)

Reported as `Signal generation failed — Ollama Cloud returned an empty assistant message` with
`message.content: ""` and a `message.thinking` trace cut off mid-sentence. `num_predict` caps the
reasoning trace **and** the answer together, and `deepseek-v4.1-flash` thinks by default when
`think` is omitted, so a horizon's whole budget went into the trace. `OllamaSignalEngine` read only
`content`, ignored `done_reason`, and threw `SignalGenerationException` (`502`) — identically for
every horizon.

Backend only: `Infrastructure/AI/OllamaSignalEngine.cs`, `Configuration/OllamaCloudOptions.cs`
(doc comments), **3 new tests** in `Infrastructure/OllamaSignalEngineTests.cs`, `README.md` and this
memory bank. No DTO, schema, fee, indicator, golden snapshot or dependency change.

| Command | Result |
| --- | --- |
| `cd backend && NUGET_PACKAGES=$PWD/.nuget/packages dotnet build PseSignalDesk.slnx --nologo -v q` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `dotnet test … --filter 'FullyQualifiedName~OllamaSignalEngineTests'` | `Passed! - Failed: 0, Passed: 11` — 8 → 11, **+3** |
| `cd backend && dotnet test PseSignalDesk.slnx --no-build --nologo -v q` | `Passed! - Failed: 0, Passed: 561, Skipped: 1, Total: 562` (8 s) — 558 → 561, all Golden tests green |

**What changed**

- **The envelope explains the empty answer.** `OllamaChatEnvelope.DoneReason` (`stop`, `length`, …)
  and `OllamaChatMessage.Thinking` are deserialized, so `content: ""` is diagnosable.
- **One recovery call, then a latch.** On an empty `content` the engine logs the stop reason,
  completion tokens, reasoning length and body (the upstream body is no longer echoed in the `502`
  detail), retries **once** with `think: false`, and sets `_reasoningOverrunModel` so every later
  call on that engine instance sends `think: false` from the start — a corrective retry or an
  evaluation loop does not repeat the overrun. The typed client is resolved per run, so the latch is
  per run, not per process.
- **Provenance and the error stay honest.** The recovery is added to `ModelRetryCount` (on top of a
  corrective parse retry, if any). A second empty answer throws a `SignalGenerationException` naming
  `done_reason`, the token count and the two settings that prevent it; when thinking was already off,
  there is no retry because the identical request would only cost another call.
- **Docs.** `README.md` documents the `ReasoningEffort`/`num_predict` interaction and the recovery;
  `systemPatterns.md` and `techContext.md` carry the rule and the live observation.

**Not run:** a live `run-now` (still needs `OLLAMA_API_KEY`, unset in this shell), so the recovery is
proven against a stub response that reproduces the exact live envelope, not against the provider.


## Latest verification (2026-11-26 — LLM measurement + structured-output pass)

Triggered by "make the signal LLM more intelligent". The model's rank order is the product, yet its skill
was unmeasured and the request was a plain `format: "json"` chat with a hardcoded temperature, no seed and
no context window. Two slices landed: **A (measure)** and **B.1–B.3 (contract + controls)**. No new
dependency, no new layer, and every server-owned invariant is intact.

| Command | Result |
| --- | --- |
| `cd backend && dotnet build PseSignalDesk.slnx --nologo -v q` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo -v q` | `Passed! - Failed: 0, Passed: 548, Skipped: 1, Total: 549` (8 s) — 538 → 548, **+10** (`OllamaSignalEngineTests` 4 → 8, `OllamaPromptBuilderTests` 16 → 18, `SignalJsonParserTests` 9 → 10, `SignalOutcomeServiceTests` 19 → 22) |
| `dotnet ef migrations add AddSignalRankingProvenance --project src/PseSignalDesk.Infrastructure --startup-project src/PseSignalDesk.WebApi` | `20261126124346_AddSignalRankingProvenance` — 12 `SignalRuns` columns, 2 `SignalItems` columns, all defaulted |
| `cd frontend && npx tsc --noEmit` | clean (exit 0) |
| `cd frontend && npm test` | `ℹ tests 87 · ℹ pass 87 · ℹ fail 0` — unchanged |

**What it built:** `OllamaSignalResult` + `OllamaGenerationMetadata` on the engine seam →
`SignalPipelineService.ApplyModelProvenance` → 12 stored provenance columns (`EngineName`, `ModelName`,
`PromptHash`, `ModelTemperature`, `ModelSeed`, `ModelRetryCount`, `ModelLatencyMs`, `PromptTokens`,
`CompletionTokens`, `PromptCharacters`, `SchemaConstrained`, `FastExtractionApplied`) and
`SignalRunDto.ModelProvenance`; `SignalItem.QuantPreScore`/`QuantRank` (the ordinal in the candidate table
the prompt prints) → `SignalItemDto`; `RankingQualityDto` on the scorecard (mean Spearman rho between the
model order and the quant order re-ranked within the picks, plus each side's top-three win rate and mean R);
`format` now carries a JSON schema (`OllamaPromptBuilder.ResponseSchemaJson`, `drop_category` as `enum`);
`OllamaCloudOptions.Temperature/Seed/NumCtx/NumPredict/TopP/ReasoningEffort/UseStructuredOutput` with only
configured knobs serialised; and the model-facing `rank`/`drift_status`/`previous_rank` fields deleted from
the contract, the parser validation and the correction prompt. A `400` on schema-constrained output retries
once in plain JSON mode and latches the schema off for the process.

**Not run:** any live `run-now` (`OLLAMA_API_KEY` unset), so schema/seed/num_ctx/think support on Ollama
Cloud is unverified; and the prompt archive + `eval-prompt-replay` endpoint were deliberately deferred.

## Latest verification (2026-11-26 — `More ▾` stacking fix)

Reported as "it is hiding under". The cause was the shell's layering, not the menu's positioning:
`.panel` (`index.css`) applies `backdrop-blur`, so `<header>` is itself a stacking context; the menu's
`absolute right-0 z-40` therefore ranked only *inside* it, and the header — `position: static;
z-index: auto` — painted before the `.panel` cards beneath it (`HowToReadStrip`, `WelcomeSetupBanner`,
`#market-call`, `AdviceCardList`), which covered the open menu. Fixed in
`components/TerminalHeader.tsx` only: `<header>` is `panel relative z-30` (above in-flow panels, below
the `z-50` portals) and `MoreMenu` gained one `document` listener pair (outside `pointerdown` in the
capture phase, and `Escape` with focus returned to the summary). **No new dependency, no API or data
change, no new component.**

| Command / probe | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 87 · ℹ pass 87 · ℹ fail 0` (≈0.36 s, exit 0) |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 135 modules transformed`, `✓ built in 239ms`, exit 0 (`index-*.js` 93.14 kB / 26.24 kB gzip, CSS 61.85 kB / 11.55 kB gzip) |
| built-bundle audit | `grep -c "panel relative z-30" dist/assets/index-*.js` → `1` |
| real built app — `dist/` on `127.0.0.1:5399`, legal flag + theme seeded, 1280×940 iframe, headless Chrome `elementsFromPoint` at `header.bottom+40` | natural state `menuOnTop=true`; with a `.panel` card inserted as `main`'s first child `CARD+FIX => P.type-eyebrow > DIV.absolute z-40 > SECTION.panel, menuOnTop=true`; forcing the pre-fix `header{position:static;z-index:auto}` back gives `CARD+BEFORE => SECTION.panel > menu, menuOnTop=false` — the reported defect, reproduced and then removed |
| same page — dismissal | `pressInsideStaysOpen=true · pressOutsideClosed=true · escapeClosed=true · focusBackOnSummary=true · reopens=true` |

Not run: `dotnet test` (no backend file changed) and a Safari/Firefox pass. No unit test was added —
`npm test` is DOM-free `node:test` over `lib/`, so the layering contract is recorded in
`systemPatterns.md` (*Conventions*) and pinned by the built-app probe above instead.

## Latest verification (2026-11-26 — frontend wayfinding pass)

Frontend only: **6 new files** (`lib/shortcuts.ts`, `lib/howToRead.ts`,
`components/KeyboardShortcutsDialog.tsx`, `components/HowToReadStrip.tsx`,
`lib/__tests__/shortcuts.test.ts`, `lib/__tests__/howToRead.test.ts`), **13 edited** (`App.tsx`,
`index.css`, `components/{TerminalHeader,AdviceCardList,RegimeStrip,MarketDirectionHero,BeginnerGuideDialog,PortfolioDialog}.tsx`,
`components/ui/{Dialog,Tabs}.tsx`, `lib/{plainAdvice,preloaders,horizons}.ts`, `lib/__tests__/plainAdvice.test.ts`),
plus `README.md` and the memory bank. **No backend file, no API contract, no new dependency, no new
architectural layer**, and no server-owned number, verdict or label moved.

| Command | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 87 · ℹ pass 87 · ℹ fail 0` (≈0.30 s, exit 0) — 71 → 87, **+16** (9 shortcuts, 5 how-to-read, 2 `describeNextStep`) |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 135 modules transformed`, `✓ built in 232ms`, exit 0 (`index-*.js` 92.5 kB / 26.0 kB gzip, CSS 61.8 kB / 11.6 kB gzip) |
| build output audit | `KeyboardShortcutsDialog-*.js` **1.65 kB / 0.76 kB gzip** — the new sheet is its own chunk, lazily imported and warmed by `preloadKeyboardShortcutsDialog()` |
| built-CSS audit | `.kbd{…min-width:1.5rem;font-family:var(--font-mono);font-size:var(--text-2xs)…}` emitted once in `index.css`'s component layer; every `aria-keyshortcuts` value comes from `keysForAction()` |

Not run: a real-browser capture (headless Chrome still hangs on the seeded `dist/` page here — see
`techContext.md`) and `dotnet test` (no backend file changed), so the shortcut behaviour is pinned by
the pure resolver's `node:test` cases rather than by pressing keys in a browser.

**What changed**

- **`lib/shortcuts.ts` is the single map.** `SHORTCUT_GROUPS` (Move / Act / Change what you see / Help)
  feeds the shortcut sheet, the guide's one-line summary and every control's `aria-keyshortcuts`
  (`keysForAction('run')` → `['R']`), so a key cannot be bound but undocumented. `resolveShortcut` is
  pure: modifiers → `null`, a typing target → `null`, a sheet open → only `?`, non-printing keys →
  `null`. `isTypingTarget` reads `tagName`/`isContentEditable` structurally so the whole mapping is
  testable without a DOM. `Esc` is documented **without** an action because Radix owns it.
- **The keys.** `1`–`4` horizons, `R` run, `K` research, `T` trades, `D` scorecard, `S`/`P` view mode,
  `L` theme, `G` guide, `?` the sheet — wired once in `App.tsx` behind a computed `anyDialogOpen`
  (every sheet **plus** the legal gate), each branch calling the same setter its visible button calls.
- **Onboarding in place.** `HowToReadStrip` sits above the Simple list with three steps whose buttons
  *are* the actions; `psedesk.howToReadDismissed` remembers the dismissal through the `legalConsent`
  storage contract (silent on blocked quota / Safari private mode), and the footer's "How to read this
  screen" clears it and scrolls back to the strip.
- **Honest copy.** A stored session is titled `Trade signals · Wed, Nov 25` instead of "Today's"; `DAY`'s
  eyebrow lost its "Today's"; `Record` → **Practise** in the guide, the blotter button and the blotter's
  `take` tab in Simple mode; rows end in `View plan ▸` at every width with an `sr-only` "Opens the full
  plan in a detail sheet"; and `describeNextStep()` prints one sentence naming the next control — it says
  "nothing cleared the screen today", never "buy something".
- **Wayfinding chrome.** A skip link (`sr-only` until focused) lands on `<main id="main-content">`, the
  market call is focusable at `#market-call` for step 1, `#how-to-read` is the strip's anchor, and the
  footer now carries `How it works G` · `Keyboard shortcuts ?` · `How to read this screen`.


## Verification (2026-11-26 — Vite dev server port recovery)

Tooling only. `vite.config.ts` pins `strictPort: true`, so a leftover dev server makes `npm run dev` die with
`Error: Port 5173 is already in use` instead of moving to 5174. An orphaned `npm run dev` — PPID 1, 45 minutes
old, still answering `200` — held `[::1]:5173`. Freed, and made repeatable:

| Command | Result |
| --- | --- |
| `lsof -nP -iTCP:5173 -sTCP:LISTEN` + `ps -o pid,ppid,lstart,command -p <pid>` | `node 85742` on `[::1]:5173`, its parent `npm run dev` 85727 sitting at **PPID 1** (orphaned) |
| `kill -TERM 85742 85727` | both gone; a real `bind()` on `127.0.0.1:5173` **and** `[::1]:5173` → OK; `curl` → `000` |
| `frontend: free port 5173` — occupied / already free / SIGTERM-ignoring | `freeing :5173 — SIGTERM to 95119` → `:5173 is free now` · `:5173 is already free` · `after 6s — SIGKILL` → `:5173 is free now`; exit 0 in all three |
| `cd frontend && npm run dev` afterwards | `VITE v8.3.8  ready in 274 ms`, `Local: http://localhost:5173/`, `GET /` → **200** (server then stopped) |

New: the `frontend: free port 5173` task and a `Frontend: Vite dev server (frees :5173 first)` `pwa-node` launch
config (`preLaunchTask` + `serverReadyAction` → debuggable Chrome). No backend or frontend source touched, so the
test counts are unchanged (496 + 1 skipped / 71).

## Latest verification (2026-11-26 — per-company disclosures, data-truth slice 2)

Backend only. **1 new source file** (`Infrastructure/MarketData/PseEdgeDisclosureSource.cs`), **7 edited**
(`IMarketDataProvider` — one *defaulted* interface method, so no existing fake broke; `QuantitativeFilterService`
(`SelectDisclosureFanout`); `SignalPipelineService` (`EnrichCompanyDisclosuresAsync`, before
`MergeRollingDisclosuresAsync`); `MarketDataOptions` (`DisclosureCompanyFanoutLimit = 25`); `PseEdgeHistorySource`
(its `ReadCompanyId` now delegates to the shared `EdgeSymbolSearch`); `PseMarketDataProvider` (the fan-out plus
both Edge date formats and the identifier cells); `appsettings.json`), **1 new test file + 3 extended**. No
dependency, no DTO, no fee and no golden snapshot touched.

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo -v q` | `Passed! - Failed: 0, Passed: 496, Skipped: 1, Total: 497` (9 s) — 485 → 497, **+12** |
| `dotnet test --list-tests` | `PseEdgeCompanyDisclosuresTests` 8 (new), `QuantitativeFilterServiceTests` 38 → 40, `SignalPipelineServiceTests` 12 → 13, `PseMarketDataProviderTests` 33 → 34 |
| live harness (`/tmp/livecheck`, real exchange, outside the repo) | JFC/SM/BDO/TEL resolved to an id; `ZZZZ` has no page → **skip, not failure**; **JFC 47 / BDO 41 / TEL 40 / SM 22 filings** with real dates (`2026-11-25 10:16 Change in Shareholdings of Directors and Principal Officers`). Ledger line: `Ok — 4 of 5 screened name(s) filed through their own Edge page` |
| same harness, full market-wide tape (`FetchAsync`) | `session 2026-11-25, 60 names, **3** with attributable filings` (BDO, SM, BPI) — the fallback's true ceiling |
| live instance on `:5299` (real `pse_signals.db`, stopped after) | `GET /api/research/SM` → **200** (2 588 B) with `disclosures: []` — correct point-in-time: the only SM row on that page is dated `2026-11-26`, *after* the stored session (`2026-11-25`). `GET /api/health/feeds` → **200**, six feeds, `storedSessionCount: 1` |
| `lsof -ti :5299` | empty — instance stopped, the operator's `:5236` process untouched |

**The route was a guess and the guess was wrong.** The plan specified
`companyPage/companyDisclosures.do?cmpy_id=<id>` with an *empty* template as the safe default, so the fan-out
would have shipped inert. Probing first showed that route answers **HTTP 404**. The working call is
`GET companyDisclosures/search.ax?keyword={cmpy_id}` — `keyword` is the hidden field the page's own search form
posts — and it returns the company's newest-first 50-row table (`Total 424` for JFC), subject in the first cell,
`Nov 26, 2026 11:53 AM` in the second. Because the route is confirmed, the fan-out ships **on**
(`DisclosureCompanyFanoutLimit = 25`); setting `0` is the documented rollback (zero per-company requests).

**Why this was the whole pillar.** Attribution used to be a headline-to-company-name match over a market-wide
table that names the issuer only sometimes: the live run above attributed **3 of 60** names, so a rights offering
titled just "Rights Offering" reached no ticker and `SRO_DILUTION` could never fire. Keying the fetch on the
exchange's own `cmpy_id` makes attribution correct by construction — no name matching anywhere — and the
fan-out returns 22–47 filings per screened name. One page per company per run is enough because the pipeline
keeps a rolling 120-day merge of `RecentDisclosuresJson`.

**A deliberate, visible behaviour change.** A screened name that only its own company page reveals as
rights-offering / tender-offer / halted is now **dropped before the model sees it**
(`RunAsync_disqualifies_a_name_whose_rights_offering_only_the_company_page_names`). Failures stay soft: a
company the search does not know is a skip, a transport/JSON failure keeps the market-wide attribution, and the
ledger receives **one aggregated observation** per fan-out instead of 25.

**Two parser bugs surfaced by the same probe**, fixed in the market-wide fallback: the disclosure table prints
`Nov 26, 2026 05:20 PM`, which the numeric-only date regex missed — so the *timestamp* was kept as a subject and
every row was back-dated to the session — and the following PSE form/report numbers (`13-1`, `C08413-2026`)
became subjects too, turning one filing into three. `AnnounceDateRegex` + `BuildDate` read both formats;
`FilingIdentifierRegex` drops the identifier cells. Pinned by
`FetchAsync_reads_the_live_disclosure_table_date_and_ignores_its_identifier_columns`, which replays the captured
markup and asserts one filing dated `2026-11-21` whose headline carries no `10:05`, `LD-3` or `LN00207`.

**Not verified:** a full `run-now` (needs `OLLAMA_API_KEY`, and it would overwrite the stored WEEK run), so the
ledger has not been seen carrying a real fan-out line inside a production run — only from the harness.

## Latest verification (2026-11-26 — feed health ledger, data-truth slice 1)

Backend only. **4 new source files** (`Application/Models/FeedHealthModels.cs`,
`Application/Interfaces/IFeedHealthRegistry.cs`, `Infrastructure/Diagnostics/FeedHealthRegistry.cs`,
`WebApi/Controllers/FeedHealthController.cs`), **4 edited** (`PseMarketDataProvider`, `PseEdgeHistorySource`,
`OllamaEmbeddingClient` — each only gaining an optional registry parameter and the recordings — and
`Program.cs` for the singleton), **2 new test files + 1 extended**. No dependency, no DTO or status code
removed, no indicator/plan/fee/prompt/golden touched.

| Command | Result |
| --- | --- |
| `cd backend && dotnet build PseSignalDesk.slnx -c Debug --nologo -v q` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo -v q` | `Passed! - Failed: 0, Passed: 484, Skipped: 1, Total: 485` (8 s) — 464 → 484, +20, **no existing test edited** |
| live instance on `:5299` (`--no-launch-profile`, real `pse_signals.db`, stopped after) | `GET /api/health/feeds` → **200**: `overallStatus: Unknown`, `storedFactsAvailable: true`, `latestStoredSessionDate: 2026-11-25`, `storedSessionCount: 1`, `quotationReportTemplates: 0`, `embeddingsConfigured: false`, six feeds in enum order |
| `lsof -ti :5299` | empty — instance stopped, the operator's `:5236` process untouched |

Not run: a live `run-now` (needs `OLLAMA_API_KEY` and would overwrite the stored WEEK run), so the ledger has
not yet been observed carrying real PSE Edge counts. The disclosure degradation is pinned by a stubbed test
that replays the exact `200`/"no records found" page the live site serves.

**Why it exists.** `announcements/search.ax` answers `200` with a ~2.4 KB "no records found" page, so
`FetchDisclosuresAsync` returned an empty map, logged nothing, and the run looked healthy while the trap
pillar (`SRO_DILUTION`, `TENDER_OFFER_PIN`, `TRADING_HALT_RISK`) was inert. A transport failure raised; a
silent emptiness did not. This slice makes that state a reported fact instead of a missing log line.

**What it built** — a thread-safe, in-memory, singleton ledger with four recording verbs (`Success`,
`Degraded`, `Failure`, `Skipped`), run-scoped counters (`BeginRun()` at the top of `FetchAsync`) and
process-lifetime timestamps; status **derived from counters** rather than from the last write, because 58
names finish concurrently (`failures && !successes` → `Failed`, any failure/degraded → `Degraded`,
`successes` → `Ok`, only skips → `Degraded`, nothing → `Unknown`); recordings placed at the parse seam so a
cache hit still counts; hooks in Edge history, disclosures (one terminal verdict per scrape), Yahoo charts,
macro dials, the DQR and the embeddings client; and `GET /api/health/feeds` reporting the six feeds,
`overallStatus` over the **load-bearing three only**, the stored-session facts and the configuration that
explains an `Unknown` — `NoStore`, no credential reflected, and an unreadable store answering
`storedFactsAvailable: false` instead of `500` (with a test that serialises the report and asserts the key
string never appears).

**Next** — slice 2 shipped (per-company PSE Edge disclosures, section above), and the measurement half of slice 4
now ships too (`20261126124346_AddSignalRankingProvenance`, run provenance, model-vs-quant `rankingQuality`).
Remaining: slice 3 (surface "trap flags are unenforced this session" through `SignalDtos.Warnings`) and the rest
of slice 4 (opt-in prompt archive + `eval-prompt-replay`). Details in `activeContext.md`.


## Latest verification (2026-11-26 — hand cursor on every control)

Frontend only: `src/index.css` plus this memory bank. **No backend file, no API contract, no new dependency, no
new architectural layer**, and no server-owned number, label or verdict moved.

| Command | Result |
| --- | --- |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ built in 240ms`, exit 0 — `dist/assets/index-*.css` `59,339` bytes |
| `cd frontend && npm test` | `ℹ tests 71 · ℹ pass 71 · ℹ fail 0` (503 ms, exit 0) |
| built CSS audit | `button:not(:disabled),[role=button]:not([aria-disabled=true]),[role=tab]:not(…),[role=option]:not(…),[role=menuitem]:not(…),a[href],summary{cursor:pointer}` emitted once inside `@layer base` (layer starts at offset 3,708; `@layer utilities` at 10,009, so utilities win) |
| layer probe — headless Chrome `--dump-dom` over the built stylesheet | `button=pointer`, `button[disabled]=default`, utilities `cursor: default` opt-out → `default`, utilities `cursor: not-allowed` → `not-allowed`, `summary=pointer`, `[role=tab]=pointer`, `[role=tab][aria-disabled=true]=auto` |
| real built app — `dist/` copy served on `127.0.0.1:5311` with the legal gate + welcome seeded, 1280×940 | **19** rendered controls: `Open desk controls`, `Run Latest Screen`, `My Trades`, `More▾`, `How it works`, `Check a Stock`, `Past Results`, the four horizon tabs, `☀Light`, `☾Dark`, `Simple`, `Pro`, `Technical details`, `Review the notice` → all `cursor=pointer`; the **disabled** trading-day picker → `cursor=not-allowed` |

Not run: `dotnet test` (no backend file changed) and a VoiceOver/NVDA pass. The probe page and the served
`dist/` copy were throwaway files under `/tmp`; the server on `:5311` was stopped afterwards.

**What changed**

- **One base rule, not 100+ classes.** Tailwind v4's preflight no longer sets `cursor: pointer` on buttons (v3
  did), so every control showed an arrow. `index.css` now sets the hand for `button:not(:disabled)`,
  `[role="button"|"tab"|"option"|"menuitem"]:not([aria-disabled="true"])`, `a[href]` and `summary`, beside the
  existing `touch-action` block.
- **Dead controls keep the arrow.** `:not(:disabled)` / `:not([aria-disabled="true"])` exclude them, and their
  own `disabled:cursor-not-allowed` still applies inside the utilities layer.
- **Rows and cards were already right.** `SignalTable`'s clickable `<tr>` and `ProSignalCard`'s `<li>` carry
  `cursor-pointer` themselves; a tooltip-only `<span tabIndex={0}>` is deliberately left at `auto` because it
  explains a figure instead of acting on it.

## Latest verification (2026-11-26 — clicking a row opens the modal)

Frontend only: `AdviceCardList.tsx`, `TickerDetailDialog.tsx`, `SignalTable.tsx`, `App.tsx`, plus `README.md`
and the memory bank. **No backend file, no API contract, no new dependency, no new architectural layer**,
and no server-owned number, label or verdict moved.

| Command | Result |
| --- | --- |
| `cd frontend && ./node_modules/.bin/tsc --noEmit` | clean, exit 0 |
| `cd frontend && npm test` | `ℹ tests 71 · ℹ pass 71 · ℹ fail 0` (219 ms, exit 0) |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ built in 249ms`, exit 0; `TickerDetailDialog` still ships as its own 19.92 kB chunk |
| `grep -rn 'aria-haspopup' src` | one site — the Simple row button (`AdviceCardList.tsx:385`); `grep -c aria-haspopup dist/assets/index-*.js` → 1 |
| `grep -rn 'onTakeTrade' src` | `AdviceCardList` no longer takes it; `SignalTable`, `TickerDetailDialog`, `ResearchDialog` still do |
| headless Chrome over CDP against the running dev server (`localhost:5173`) + the running API (`:5236`, stored run 17 — WEEK, 10 items) | before the click **0** `[role="dialog"]`; **3** rows carry `aria-haspopup="dialog"`; clicking row 1 (Philippine National Bank) opened a dialog titled **“Philippine National Bank”** whose text carries the four-step plan, `Ticket: 50 shares (5 board lots of 10)`, `Chance it reaches the target 42%`, `Show the math behind this pick` and `Sizing & odds` (2,162 characters); screenshot `/tmp/psedesk-row-modal.png` |
| same session — close + reopen | the sheet's `Close` → **0** dialogs and the list intact; the next row opened immediately (“Aboitiz Equity”); a synthetic `keydown: Escape` (Radix `DismissableLayer`) → **0** dialogs |
| Pro at 500×757 (`psedesk.viewMode=pro`) | **10** phone cards; clicking a card opened the same ticker sheet; the card's `Take` opened **only** the portfolio blotter, so the stop-propagation keeps the two actions apart |
| same session — the drawer contents (re-run after the `MathRows` change) | *Show the math behind this pick* opened (`aria-expanded="true"`) showing the **“The pick”** and **“The ticket”** columns including `Quarter-Kelly` and `Risk governor`, with **no** `RSI14` / `Raw indicators` / `Volume vs 20D` / `Filings on file` / `Why it moved`; the deep drawer still holds `RSI 14`, `Weinstein stage`, `Rank trajectory` and `Recent disclosures`, so the indicators, history and filings are shown once each; screenshot `/tmp/psedesk-math-drawer.png` (130,713 bytes) |

Not run: `dotnet test` (no backend file changed) and a VoiceOver/NVDA pass. The Escape check is a synthetic
`KeyboardEvent` on the dialog because Chrome's CDP `Input.dispatchKeyEvent` does not reach Radix's document
listener in this headless setup — the same keydown a reader presses is what was exercised.

**What changed**

- **A Simple row is a button that opens the modal.** `SignalRow` is one `<button aria-haspopup="dialog">`
  calling `onSelect(ticker)`; the inline accordion, its `openTicker` state, `aria-expanded`/`aria-controls`,
  `useId`, the row-level `onTakeTrade` prop and the panel's own `Practise This Trade` / `Full Details`
  buttons are gone. The list header now reads “tap a row to open its details”.
- **The modal absorbed the panel, nothing was dropped.** `TickerDetailDialog`'s Simple branch renders
  `WavePlanSteps` (four-step roadmap; a payload without `wavePlan` still falls back to `PlanSteps`), the
  hold line, the `OrderBox`, the full-width win-chance `Meter` (now exported), the “Desk confidence
  N / 100 · …” sentence and a *Show the math behind this pick* drawer around `MathRows` (the pick's
  scores and the ticket's sizing maths), ahead of its existing *Sizing & odds* drawer, thesis, pre-mortem,
  the full indicator table (rank trajectory, disclosures) and the citations. `MathRows` replaces the old
  `TechnicalRows`, which also re-printed the raw indicators, the deviation reason and the filings the
  sheet already showed — one dialog, one copy of each figure.
- **Pro phone cards are clickable.** `ProSignalCard` opens the same modal from anywhere on the card; `Take`
  and the trap-flag badge call `stopPropagation` so they keep their own behaviour. The desktop `<tr>` was
  already clickable and is unchanged.
- **One detail sheet, not two.** The row and the Pro table now converge on `TickerDetailDialog` — one lazy
  chunk, one payload, no second endpoint.
- **Copy follows the behaviour.** The list header reads “tap a row to open its details”, the header's
  Simple/Pro hint now says the same (“tap a row to open its detail sheet”, was “for the why”), and the row
  keeps its `▸ Why?` affordance because the sheet is the why.

## Earlier verification (2026-11-26 — typography, colour and whitespace)

Frontend only: `src/index.css` plus every `.tsx` under `src/` (class lists; no logic, no props, no copy
meaning changed). **No backend file, no API contract, no new dependency, no new architectural layer**, and no
server-owned number, label or verdict moved.

| Command | Result |
| --- | --- |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 131 modules transformed`, `✓ built in 264ms`, exit 0, `dist/assets/index-*.css` 59.30 kB (11.0 kB gzip) |
| `cd frontend && npm test` | `ℹ tests 71 · ℹ pass 71 · ℹ fail 0` (411 ms, exit 0) |
| built-CSS audit | `.type-eyebrow` / `.type-badge` / `.type-caption` / `.type-body` / `.type-lead` / `.type-title` / `.type-figure` / `.measure` / `.measure-tight` / `.measure-wide` all emitted with their `62ch` / `46ch` / `78ch` caps; `--text-2xs:.6875rem`, `--tracking-label:.08em` present; `[data-view-mode=simple] .type-badge{letter-spacing:0;font-size:.75rem}` present; `text-slate-*` utility count **0** |
| `grep -rn -E 'text-\[(8|9|10|11|12)px\]\|text-slate-\|text-white' src` | 3 hits, all comments/`@apply` inside `index.css` — **0** in `.tsx` |
| `grep -rn 'uppercase' src` | only `.type-eyebrow` call sites plus the `text-transform` in its own rule; the surviving `uppercase` in `.tsx` is a code comment |
| `grep -rln 'type-badge' src` | 15 files: `index.css`, `VerdictBadge`, `DriftBadge`, `RegimeStrip`, `SignalTable`, `DropPanel`, `TerminalHeader`, `AdviceCardList`, `MarketDirectionHero`, `PortfolioDialog`, `TickerDetailDialog`, `ResearchDialog`, `ModelTrainerPanel`, `KnowledgeBaseDialog`, `WelcomeSetupBanner` |

Not run: a real-browser capture (headless Chrome hangs on the seeded app in this environment) and `dotnet test`
(no backend file changed).

**What changed**

- **A five-step ramp with an 11px floor** (`--text-2xs: 0.6875rem`) and role classes: `.type-eyebrow`,
  `.type-badge` (new), `.type-caption`, `.type-body` (13px/1.65, `text-wrap: pretty`), `.type-lead`,
  `.type-title`, `.type-figure`. Colour stays composable at the call site.
- **Measure in `ch`, not `rem`**: `.measure` 62ch / `.measure-tight` 46ch / `.measure-wide` 78ch, applied to
  every prose paragraph (thesis, pre-mortem, order guidance, warnings, empty states, dialog copy). The
  `max-w-3xl` cap it replaced let an 11px paragraph run ~130 characters.
- **A measured colour ramp.** `text-slate-*` → `text-fg-muted/subtle/faint`, `text-white` → `text-fg`; the light
  theme's `fg` ramp is re-measured rather than inverted, and light-theme `mint-400` moved from 4.02:1 to
  4.95:1 (`#0b8a5f` → `#087a53`) — the only accent that failed AA.
- **One whitespace rhythm**: inner cards `px-3.5 py-3`, panels `p-4 sm:p-5`, list rows `px-4 py-3.5`, warning
  stacks `space-y-1`, step grids `gap-2.5`, sections `gap-4`/`gap-5`.
- **Sentence-case controls.** `Button`, `TabsTrigger`, the segmented switches, `More`'s summary and menu rows
  dropped `uppercase tracking-label`; `.ui-button`/`.ui-tab` still lift them to 13px in Simple mode. Checked
  the flip side of that change too: no call site relied on CSS to capitalise its label — every `Button` /
  `TabsTrigger` / menu string in `src/` is already typed in sentence case (`Run`, `Check a Stock`,
  `Show the math …`), and no label starts lowercase.

Still open (deliberately): the two dense data tables (Pro `SignalTable`, the portfolio ledger) keep 12px cells
and tighter vertical padding — they are scanned column-wise, not read — and the light/dark visual check plus a
screen-reader pass remain under *Remaining*. The rhythm sweep was re-audited after the fact: the only surviving
`px-2.5 py-2` insets are the three entry/stop/target tiles inside `SignalTable`, the 13 `p-4`/`gap-4` hits are
section gaps and drawer bodies (not card insets), and the three `space-y-2`/`space-y-4` stacks are lists inside
`.type-body` prose — all three cases match the documented rhythm rather than escaping it.


## Earlier verification (2026-11-26 — Simple becomes a signal list)

Prompted by "as a common person I just expect a list of trade signals; expanding it tells me the why —
and Pro is for people who understand trades". Frontend only: `AdviceCardList.tsx`,
`MarketDirectionHero.tsx`, `App.tsx`, `TerminalHeader.tsx`, `lib/plainAdvice.ts`, its `node:test` suite,
`RidingNowStrip.tsx` (**deleted**), plus the docs and memory bank. **No backend file, no API contract, no
new dependency, no new architectural layer**; Pro, the ticker/research dialogs and every server-owned
number are untouched.

| Command | Result |
| --- | --- |
| `cd frontend && npm test` | `ℹ tests 71 · ℹ pass 71 · ℹ fail 0` (≈0.38 s, exit 0) — 66 → 71 with five `describeSignalRow` cases |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ 131 modules transformed`, `✓ built in 211ms`, exit 0 |

Not run: a real-browser capture (headless Chrome still hangs on the seeded app in this environment) and
`dotnet test` (no backend file changed).

**What it built** *(the accordion below was later replaced by the row-opens-modal pass at the top of this file; the row content and `describeSignalRow` did not change)*

- **One row per signal.** `AdviceCardList` no longer slices `buyable.slice(0, 3)` and no longer prints
  three fully expanded plan cards; it renders **every** buyable pick in rank order as one
  `<button aria-expanded>` row (rank, company name, ticker, verdict, entry chip, drift, the three
  tick-valid prices and the entry-timing line) and a single-open accordion panel with the thesis, the
  four-step plan, the hold line, the `OrderBox`, the win-chance meter, the desk-confidence line, the
  pre-mortem, `Practise This Trade` / `Full Details` and the *Show the math* drawer. The empty state, the
  collapsed `WATCH_ONLY` list and the legacy no-`wavePlan` fallback are unchanged.
- **`describeSignalRow` in `lib/plainAdvice.ts`.** A pure composer (`verdictLabel`, `headline`, `subline`,
  `entryChip`) over copy that already existed; it derives no probability, size or verdict. Five
  `node:test` cases pin it: price order, the `READY_NOW`-only chip, a wait status, `WATCH_ONLY`, the
  legacy fallback to the odds line, and `—` for an unreported price.
- **The hero became a call bar.** `MarketDirectionHero` keeps the traffic light, the instruction and one
  sentence; the 10–90 gauge, the reasons, the sector money flows, the trust line, the three summary cards
  and the institutional tiles moved into one *Why this call* drawer whose collapsed pills preview the
  PSEi, the recommended cash and the horizon.
- **`RidingNowStrip` deleted.** "Inside the band" is now an entry chip on the row it belongs to, sourced
  from the server's `EntryStatus` through `describeEntryTiming`.
- **The switch explains itself.** `Simple | Pro` keeps its labels and persisted value and gained a hint
  (`title` + `aria-description`): Simple lists the calls — tap a row for the why; Pro is the full quant
  table for readers who already think in odds, R:R and Kelly sizing.

## Latest verification (2026-11-26 — typeface: Outfit)

Frontend only: `src/index.css`, `index.html`, `scripts/fetch-fonts.mjs`, `public/fonts/` (the woff2 +
`OFL.txt`; the previous face's binary was deleted), plus `README.md`, `PRIVACY.md`,
`THIRD_PARTY_NOTICES.md` and the memory bank. **No backend file, no API contract, no new dependency,
no golden moved** — `package.json` only kept its existing `npm run fonts`.

| Command | Result |
| --- | --- |
| `node scripts/fetch-fonts.mjs` | `outfit-latin.woff2 32228 bytes` · `OFL.txt 4389 bytes` (Copyright 2021 The Outfit Project Authors), exit 0 |
| `node scripts/fetch-fonts.mjs --check` | prints the upstream `fonts.gstatic.com/s/outfit/v15/…` URL without writing |
| `cd frontend && npm run build` | `tsc --noEmit` clean, `✓ built in 285ms`; `grep -c @font-face dist/assets/index-*.css` → **1**; `dist/fonts/` = woff2 + `OFL.txt` only; built CSS carries `--font-sans:"Outfit", "Inter", …` |
| `cd frontend && npm test` | `ℹ tests 87 · ℹ pass 87 · ℹ fail 0` (≈0.37 s, exit 0) |
| `python3 -m http.server` over a copy of `dist/` + `curl` | `/` `200 text/html`; `/fonts/outfit-latin.woff2` `200 font/woff2` (32,228 bytes) |
| headless Chrome over `http://127.0.0.1:8732` (copy of `dist/`, legal gate seeded, dark Pro) | 1280px and 430px both render in the new face with no wrap/clip regression; screenshots `/tmp/outfit-desktop2.png`, `/tmp/outfit-mobile2.png` |
| `document.fonts` + advance-width probe | exactly one face, `Outfit loaded`; `document.fonts.check("40px 'Outfit'")` → `true`; `A` = **27.84 px** at 40 px vs **25.78** for `ui-sans-serif, system-ui`; computed `body` font-family starts with `Outfit` |
| upstream cmap parsed (`Outfit[wght].ttf`) | 360 glyphs; **no** U+20B1 `₱`, **no** U+25B2/U+25BC `▲ ▼`; U+2192 `→` present in the font but outside the served `latin` `unicode-range` |

Not run: `dotnet test` (no backend file changed) and a real-device Safari/Firefox pass.

**What changed**

- **Vendored, not linked.** `public/fonts/outfit-latin.woff2` (variable `100 900`, 32,228 bytes) is
  served by the app; `index.html` preloads it (`as="font"`, `crossorigin`) and `index.css` declares a
  single `@font-face` (`font-display: swap`, the API's own `unicode-range`) with
  `--font-sans: 'Outfit', 'Inter', ui-sans-serif, …`. No `fonts.googleapis.com` request exists, so
  `PRIVACY.md` §5 stays literally true and the terminal still renders with nothing but the API on
  loopback. `--font-mono` is untouched (tickers stay monospace).
- **One subset, on evidence.** Outfit's `latin-ext` sheet lists U+20B1 inside its `unicode-range`, but
  the upstream cmap carries no `₱`, `▲`/`▼` or (inside the served range) `→`; the subset is 14,760
  bytes for zero usable glyphs, so only `latin` ships (14.8 kB smaller than Google Sans Flex's 49,988).
  The system font supplies those few characters, exactly as it does behind the official CDN link.
- **`scripts/fetch-fonts.mjs`.** Same zero-dependency fetcher (desktop UA, `wOF2` signature check before
  overwriting, writes `OFL.txt`, supports `--check`), re-pointed at `family=Outfit` and `ofl/outfit`, and
  writing `outfit-<subset>.woff2`.
- **Licence + notices.** OFL 1.1 ships beside the font; `THIRD_PARTY_NOTICES.md` §4, `PRIVACY.md` §5 and
  `README.md` now name Outfit and the measured coverage. No reader-facing legal copy changed, so
  `CURRENT_LEGAL_VERSION` was not bumped.

## Latest verification (2026-11-26 — abuse-protection hardening)

Follow-up to the audit's F-02/F-03/F-13 gaps. **4 new source files**
(`Infrastructure/Configuration/FrontendCorsOptions.cs`, `WebApi/Services/OperatorKeyThrottle.cs`,
`WebApi/Services/DeskRequestLimits.cs`, `WebApi/Services/KnowledgeUploadPolicy.cs`), **3 new test
files** (`OperatorKeyThrottleTests`, `DeskRequestLimitsTests`, `KnowledgeUploadPolicyTests`), **8
edited source files** (`ComplianceOptions.cs`, `DeskRateLimitPolicies.cs`,
`ComplianceGuardMiddleware.cs`, `Program.cs`, `ResearchController.cs`, `KnowledgeController.cs`,
`SignalsController.cs`, `appsettings.json`), **2 edited test files**
(`ComplianceGuardMiddlewareTests.cs`, `SignalsControllerRateLimitTests.cs`) and **3 edited docs**
(`README.md`, `THIRD_PARTY_NOTICES.md`, this memory bank). No dependency, no architectural layer and
no existing status code moved; the new answers are `403` for a cross-site write, `400` for an
out-of-range lookback or an unsupported upload, and `429` for the two new budgets and the
operator-key lockout.

| Command | Result |
| --- | --- |
| `cd backend && dotnet build PseSignalDesk.slnx --nologo -v minimal` | `Build succeeded. 0 Warning(s) 0 Error(s)` (9.3 s) |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo --no-build -v minimal` | `Passed! - Failed: 0, Passed: 538, Skipped: 1, Total: 539` (9 s) — 497 → 539, **+42** |
| `cd frontend && npm test` | `ℹ tests 71 · ℹ pass 71 · ℹ fail 0` (≈0.25 s) — unchanged |
| live smoke `:5421`, throwaway DB, `ResearchRequestsPerHour=1`, `KnowledgeRequestsPerHour=1`, every upstream → `127.0.0.1:9` | cross-site and `Sec-Fetch-Site: cross-site` reindex → **403**; first allowed reindex → **200**; second → **429** + `Retry-After: 3600`; first research → **503**; second → **429** + `Retry-After: 3600`; `backtest?lookbackDays=36500` → **400** `{"title":"Lookback out of range", …}` |
| live smoke `:5422`, `LicensedCommercial`, `PSEDESK_OPERATOR_API_KEY=secret` | six wrong-key `POST /api/knowledge/reindex` → **401, 401, 401, 401, 429, 429** (the fifth carries `Retry-After: 300`); a correct key while locked → **429** |

Both smoke instances used throwaway SQLite files in `/tmp`, bound to `127.0.0.1`, and were stopped
before the command returned; no process was left on `:5421` / `:5422`.

### What the pass changed

1. **Three rate-limit policies, not one.** `desk-upstream` (`Compliance:ResearchRequestsPerHour`,
   default 120) covers `GET /api/research/{symbol}`, which fetches Yahoo + PSE Edge per call and was
   previously uncapped; `desk-model` (`Compliance:KnowledgeRequestsPerHour`, default 10) covers
   `knowledge/upload` and `knowledge/reindex`, which spend Ollama embedding credit. The existing
   `desk-expensive` policy is unchanged. Each is a fixed one-hour window, `QueueLimit = 0`, `429` +
   `Retry-After` + ProblemDetails; all three share the global desk budget on purpose.
2. **One request cannot ask for a century.** `DeskRequestLimits` caps `lookbackDays` at 1,825
   calendar days on `backtest`, `train` and `train-all` and answers `400` outside `1..1825`;
   previously the lower bound was lifted silently and the upper bound did not exist.
3. **Uploads are typed.** `KnowledgeUploadPolicy` allows `.md`, `.markdown`, `.txt` and `.pdf`; any
   other extension is a `400` instead of being decoded as UTF-8 text and indexed.
4. **Cross-site writes are refused.** `ComplianceGuardMiddleware` now checks the browser `Origin`
   against `Cors:AllowedOrigins` plus the API's own host (and falls back to
   `Sec-Fetch-Site: cross-site`) for `POST`/`PUT`/`PATCH`/`DELETE`; a mismatch is `403` before any
   work. `FrontendCorsOptions` is the single list the CORS policy and the middleware share, so they
   cannot drift.
5. **The operator key has a lockout.** `OperatorKeyThrottle` remembers wrong keys per source address:
   four failures answer `401`, the fifth answers `429` + `Retry-After`, and the window is five
   minutes. A correct key clears the record. The limiter still runs after the compliance guard, so a
   refused `403` never consumes an hourly budget.
6. **Proxy caveat documented.** There is no `ForwardedHeaders` trust: behind a same-machine reverse
   proxy every request would look loopback. The documented posture is loopback-only personal use or a
   real `LicensedCommercial` deployment with OIDC/TLS/security headers, not a personal desk behind
   nginx.

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
- [x] **Feed health ledger.** `IFeedHealthRegistry` / `FeedHealthRegistry` (singleton, in memory) records one observation per answered request across six upstreams — Edge history, Edge disclosures, Yahoo charts, macro dials, the DQR and embeddings — with status **derived from counters** (so 58 concurrent downloads cannot be summarised by whichever finished last), run-scoped counters reset by `BeginRun()`, process-lifetime timestamps, and a 240-char last message. `GET /api/health/feeds` reports them plus the stored-session facts and the configuration that explains an `Unknown`, with `overallStatus` covering only the load-bearing three. It exists because `announcements/search.ax` answers `200` with "no records found": the trap pillar was inert and nothing said so.

- [x] **Per-company disclosure attribution.** Every screened candidate gets its own PSE Edge page (`companyDisclosures/search.ax?keyword={cmpy_id}`, `DisclosureCompanyFanoutLimit = 25`, 8-way, 10-min cached) merged into its rolling disclosure set **before** the trap check, so the hard traps fire on filings the market-wide table never attributed. `0` disables the pass. Live: 22–47 filings per named company vs 3 of 60 names from the market-wide fallback, whose month-name dates and form/report identifier cells are now parsed correctly too.

### API, compliance and operations
- [x] Full REST surface: signals (`latest`, `by-date`, `dates`, `ticker/{symbol}/history`, `scorecard`, `backtest`, `model`, `train`, `train-all`, `desk-memory`, `run-now`, `quotation-report`), research (`catalog`, `{symbol}` with horizon/`averageCost`/`shares`), portfolio (summary, settings, order-ticket, trades, stop, close, trim), knowledge (list, reindex, upload, delete), `/health` and `GET /api/health/feeds` (per-upstream feed ledger).
- [x] RFC 7807 errors: `400` config/input/attestation/rule violation, `404` unknown session/document, `409` protected document, `409` a run or training job already in progress, `429` too many expensive requests (`Retry-After`), `499` client abort, `502` generation, `503` feed.
- [x] `ComplianceGuardMiddleware` (loopback-only in `PersonalLocal`, constant-time operator key on writes in `LicensedCommercial`, `/health` bypass) and `AllowedHosts` narrowed to loopback names.
- [x] **Bounded expensive work** — `DeskWorkGate` makes `run-now`/`train`/`train-all` mutually exclusive (`409`); three fixed one-hour policies answer `429` + `Retry-After`: `Expensive` (`Compliance:ExpensiveRequestsPerHour`, default 30, clamped 1–1000) on those three plus `backtest`, `Upstream` (`ResearchRequestsPerHour`, default 120, clamped 1–10000) on `research/{symbol}`, and `Model` (`KnowledgeRequestsPerHour`, default 10, clamped 1–1000) on `knowledge/upload|reindex`. `UseRateLimiter()` runs after the compliance guard so a refused caller never burns budget; stored reads are uncapped. Verified live on `:5411` (`503, 503, 429`), `:5412` (concurrent `409`), `:5421` (research `503 → 429`, knowledge `200 → 429`) and `:5422` (`401 ×4 → 429`).
- [x] **`LicensedCommercial` reachable from the UI** — `lib/operatorKey.ts` + `VITE_OPERATOR_API_KEY` put `X-PseDesk-Key` on mutating requests only, and `Compliance:AllowedHosts` widens the host allowlist only when the licensed mode is declared.
- [x] First-run legal gate (`lib/legalConsent.ts` v2, `LegalDisclaimerGate`) plus `LICENSE`, `DISCLAIMER.md` (§7 single-reader rule), `PRIVACY.md`, `THIRD_PARTY_NOTICES.md`; non-solicitation copy across the prompt and the UI.
- [x] `RequestCancellationMiddleware` (499), caller-cancellation rethrow in the provider, and a one-macrotask deferral in the terminal that removes the common StrictMode abort.
- [x] Scheduler: weekdays 15:35 Asia/Manila, four horizons per session, weekly refit, cold-boot refit.

### Frontend
- [x] React 19 + TypeScript 7 + Vite 8 + Tailwind 4 shell with a typed API client (`ApiError`, ProblemDetails, abort signals, FormData uploads) and centralized formatters.
- [x] **Outfit is the UI typeface, vendored rather than CDN-linked** (`public/fonts/outfit-latin.woff2`, variable 100–900, OFL 1.1 + `OFL.txt`, preloaded in `index.html`, `--font-sans` in `index.css`, refreshed by `npm run fonts`; it replaced Google Sans Flex on 2026-11-26 at the operator's request, 32,228 vs 49,988 bytes). A page load still talks to nothing but the API; `₱` and `▲ ▼` come from the system font because the upstream Outfit cmap has no such glyphs (measured), and `→` sits outside the served `latin` range. `--font-mono` stays for tickers.
- [x] `Simple | Pro` view mode and light/dark theme, both persisted; Simple is a **list of trade signals** — one compact `MarketDirectionHero` call bar (traffic light, instruction, one sentence; gauge/reasons/sectors/trust line/tiles behind *Why this call*), then one `AdviceCardList` row per buyable pick (rank, verdict, buy/target/cut prices, entry chip) that **opens `TickerDetailDialog`** with the four-step plan, the server ticket, the odds, the desk-confidence line, the pre-mortem and the *Show the math* drawer. Pro's phone card opens the same dialog. The old top-three cards, the “show more” toggle, `RidingNowStrip` and the inline accordion are gone.
- [x] Pro keeps the dense `SignalTable`/`ProSignalCard` views, regime tiles, rotation strip, governor/slippage tiles, `RiskSizerBar`, verdict/drift badges and the Knowledge Base action.
- [x] `TickerDetailDialog` (thesis, plan, odds, factors, technicals, dividends, disclosures with trap badges, server-verified citations, rank history), `ResearchDialog` (buy plan vs "already holding it" with `HoldingVerdictCard`), `ScorecardDialog` (forward scorecard, dual-line benchmarked replay, `ModelTrainerPanel` with learned edge / desk memory and refit buttons).
- [x] `PortfolioDialog` (Book / Record a trade / Ledger / Settings; trim, pyramid badges, expectancy tiles, exposure gauges, guardrails drawer), `KnowledgeBaseDialog`, `QuotationReportImportButton`, `WelcomeSetupBanner`, `BeginnerGuideDialog`, `LegalDisclaimerGate`, `DropPanel`.
- [x] Responsive foundations: Radix `Dialog` as a bottom sheet below `sm` with a sticky header, card fallbacks for every wide table below `md`, pinned identifier column, scrolling tab rail, touch-sized controls, `100dvh` / `viewport-fit=cover` / `.pb-safe`, pre-paint theme colour and 16px mobile inputs.
- [x] Zero-dependency SWR cache (`lib/queryCache.ts`, ref-counted abort, in-flight dedupe, prefix invalidation) plus idempotent preloaders; every reader paints cached data, then revalidates, and keeps the last payload behind an amber line when a refresh fails.
- [x] Lazy dialog chunks and pinned vendor chunks through a `manualChunks` **function** (Vite 8 / rolldown rejects the object form).
- [x] **Legibility and announcement layer.** Simple mode has a scoped type floor (`.simple-label` 12px, `.simple-note` 13px/1.55) applied across the advice card, order box, watch list and the plan-step grid; one `aria-live="polite"` region mounted from first render announces runs, results and failures; `Button` sets `aria-busy`; `run-now` renders an elapsed clock (`formatElapsedSeconds`) and a reduced-motion-aware indeterminate bar while it works.
- [x] **One type system, one colour ramp, one rhythm.** `index.css` defines a five-step scale with an **11px floor** (`--text-2xs`), role classes (`.type-eyebrow`, `.type-badge`, `.type-caption`, `.type-body`, `.type-lead`, `.type-title`, `.type-figure`), `ch`-based measure caps (`.measure` 62ch / `.measure-tight` 46ch / `.measure-wide` 78ch), a re-measured four-step `fg` ramp (17 / 8.4 / 5.9 / 4.8:1, AA in both themes) and one card/stack rhythm (`px-3.5 py-3` inner, `p-4 sm:p-5` panel, `gap-4`–`gap-5` sections). Wide tracking survives only on rubric labels; buttons, tabs and menu rows are sentence case.
- [x] **Header organization.** Two desktop rows at one 40px control height (identity + trading day + `My Trades` + the filled `Run` + `More ▾`; then the four horizon tabs + `Simple | Pro` + light/dark), grouped overflow under *Learn*/*Tools*, a single-line tagline, and Simple-mode type on the segmented switches. The phone keeps its two thumb rows and the "Desk controls" sheet.
- [x] **A real brand mark.** `DeskLogo` draws three ascending PSE candlesticks in `currentColor` on the same tinted badge (36px phone / 44px desktop) in place of the placeholder `PS` monogram, and the identical geometry — `lib/deskMarkGeometry.ts`, five pinned invariants — is rasterised by `npm run icons` into `favicon.svg`, `favicon.ico` (16/32/48) and a full-bleed 180px `apple-touch-icon.png`, all three declared in `index.html`.
- [x] **Dependency-free icon generation.** `frontend/scripts/generate-icons.mjs` imports that geometry straight from the `.ts` module, supersamples the rounded rects, deflates the PNG scanlines with `node:zlib` and writes the ICO container by hand — three committed binaries that are reproducible byte-for-byte from one command, with no image library added.

### Tooling and documentation
- [x] `npm test` harness: `node --test "src/lib/__tests__/*.test.ts"` over the React-free `lib/` layer (no DOM, no dependencies).
- [x] **Golden snapshot harness (backend).** `SyntheticMarketFixture` (seeded synthetic tape, no network, no clock), `GoldenSnapshot` (approval-style compare against `tests/…/Application/Golden/*.json`, `PSEDESK_UPDATE_GOLDEN=1` records) and a `Skip`ped Release-only benchmark that prints elapsed time and allocations. It is the guard that let the performance pass touch every indicator without moving a desk number.
- [x] `.vscode/launch.json` + `tasks.json` (API launch/attach, Chrome, `Frontend: Vite dev server` with port recovery, full-stack compound, build/test tasks, `backend: free port 5236` and `frontend: free port 5173` — each SIGTERM, poll 6 s, SIGKILL escalation, then a real probe) and `frontend/.env.example`.
- [x] `.gitignore` audited (build/test output, NuGet caches, local secrets, SQLite + sidecars + `.bak`, Node/Vite, `.env` overrides, editor/OS noise) with `git ls-files -i -c --exclude-standard` empty.
- [x] `README.md`, the three legal notices, `security_audit_report.md`, the six memory-bank files and `.clinerules/memory-bank.md`, with the sync rules recorded in `techContext.md`.

## Test inventory — 549 backend (548 executed + 1 skipped benchmark) + 87 frontend

Counts are the executed `dotnet test --list-tests` rows per class (parameterised rows included) and the `ℹ tests` figure per frontend file.

### Backend — `backend/tests/PseSignalDesk.Tests` (38 files)

| Layer | Suite | Cases |
| --- | --- | --- |
| Domain | `PseBoardLotServiceTests` — 15 tier/tick rows, bracket crossing, static band, plan repair, dated per-leg fees + commission floor, net R:R in pesos, break-even, EV, Quarter-Kelly, risk budget, gap fills, ADV, slippage | 78 |
| Domain | `LiveDriftPolicyTests` — standard errors, required gaps, multipliers, classification thresholds, hysteresis, `Describe` copy | 8 |
| Application | `QuantitativeFilterServiceTests` — indicators, stages, weighted score, mean+median liquidity floor, top-N cap, breadth, macro dials, rotation, disclosure trap wiring, the disclosure fan-out (equals the pre-trap candidate list; illiquid names and a zero limit yield none), point-in-time scoping, O(n) `ComputeEmaSeries` parity on every prefix, `IsAscendingByDate` | 40 |
| Application | `BacktestGoldenTests` — the four-horizon replay serialised against checked-in snapshots (plus a `Skip`ped benchmark that only prints time/allocations) | 5 |
| Application | `QuantitativeFilterGoldenTests` — `EvaluateStock`'s metrics against a checked-in snapshot | 1 |
| Application | `OrderTicketCalculatorTests` — sizing, verdicts, governor band, drift multiplier (incl. `HALTED` → 0), lot step-down and the risk-budget refusal, heat/sector/cash caps, slippage, execution guidance, all seven pyramid rules | 31 |
| Application | `PortfolioServiceTests` — open/close/stop rules, pyramiding, trims, drawdown posture, exposure, alert dedupe, drift-throttled tickets | 29 |
| Application | `ProbabilisticEdgeServiceTests` — base rate, barrier geometry, tilts, clamps, verdict gates, hit rates, learned edge, calibration | 22 |
| Application | `PseDisclosureClassifierTests` — categories, hard/soft traps, priority order, copy | 20 |
| Application | `TickerResearchServiceTests` — live/stored paths, plan validity, `DAY` clamp, ticket/edge parity, regime, holding advice | 17 |
| Application | `SignalOutcomeServiceTests` — entry gate, `UNFILLED`, stop tie, net R, scorecard, calibration, live-drift verdict + persistence, desk memory, and the model-vs-quant ranking read (rho = 1 when the model follows the screen, rho = 0 with the model's top three out-earning the screen's, null when no quant rank is stored) | 22 |
| Application | `BacktestServiceTests` — prefix causality, book limits, governor, slippage, exits, benchmark metrics, walk-forward | 13 |
| Application | `WaveTradePlanCalculatorTests` — plan levels, chase ceiling, entry status, ladder, trim size | 12 |
| Application | `SignalPipelineServiceTests` — discards, gates, drift, drops, persistence, `502`, a rights offering that only the candidate's own company page names dropping it before the model; the quant rank and the engine provenance are persisted with the run | 13 |
| Application | `SignalJsonParserTests` — fences, prose, brace-in-string, numeric strings, contract failure, an echoed `rank`/`drift_status` ignored | 10 |
| Application | `LearnedEdgeModelTests` — activation gate, ±6pp tilt, Kelly rescale, probability clamp | 7 |
| Application | `SectorRotationServiceTests` — mapping, leaders/laggards, strength | 7 |
| Application | `MarketDirectionServiceTests` — pillar blend, clamp, bearish override, unreported dials | 6 |
| Application | `DailyQuotationImportServiceTests` — live/upload paths, overlays, unmatched tickers, size guard | 4 |
| Application | `MoneyManagerTrainingServiceTests` — promotion gate, purged split, inactive fallback | 3 |
| Infrastructure | `PseMarketDataProviderTests` — symbol normalisation, candle sanitation, zero-volume echoes, proxy fallback, FX staleness, macro dials, chart-cache reuse, `InvalidateCache`, distinct-constituent counting, ordered failure reasons, the seven Edge-fallback cases (delisted Yahoo symbol, peso-`VALUE` volume, duplicate sessions, both-feed diagnostic, a full session from Edge, short-series diagnostic, `EnablePseEdgeHistory = false`), and the four feed-ledger cases (empty disclosure page → degraded, Edge ok + Yahoo ok-with-skips, a 500 Edge chart → failed, one observation per name per run on a cached body), and the captured live disclosure row (a month-name announce date read as a date, the form/report numbers never read as subjects) | 34 |
| Infrastructure | `PseEdgeCompanyDisclosuresTests` — `cmpy_id` resolved through `autoComplete` and the `?keyword=` route (never a bare `search.ax` query), rolling-window filter, an undatable row dropped rather than back-dated, no company page → skip not failure, `500` → failed, limit 0 → zero requests, the cap honoured with distinct ids, ten-minute cache reuse | 8 |
| Infrastructure | `MarketDataOptionsTests` — the binder's collection doubling reproduced and undone, order-preserving de-duplication, case-insensitive `DistinctNonBlank` | 4 |
| Infrastructure | `MarketDataCacheTests` — shared task under concurrency, no refetch inside the TTL, a failure is never served, TTL refetch, an abandoned caller cannot cancel, pre-cancelled does nothing, `Clear`, oldest-entry eviction, eviction safe against a concurrent `Clear()` | 9 |
| Infrastructure | `FeedHealthRegistryTests` — every source unknown on a fresh registry, success→ok, lone failure→failed, failure+success→degraded, explicit degraded observation, only-skips→degraded, skips don't degrade a producing feed, `BeginRun` clears run-scoped counters but keeps embeddings, timestamps survive a run, 500 concurrent records counted exactly, 240-char detail truncation | 10 |
| Infrastructure | `OllamaPromptBuilderTests` — horizon block, knowledge, feedback, cross-asset, desk memory, guardrails, the array-order ranking rule (no `rank`/`drift_status` in the contract) and both JSON schemas | 18 |
| Infrastructure | `OllamaSignalEngineTests` — stub `HttpMessageHandler` routed by the request body's `model`: a fast-model `404` keeps the ranking, one `404` per engine across two horizons (latch), a ranking `404` still surfaces as `SignalGenerationException`, a blank fast model makes zero fast calls; plus the schema `format` + sampling options, `think` forwarding, the schema-`400` → JSON-mode fallback (latched), and the prompt fingerprint / token usage | 8 |
| Infrastructure | `SqliteKnowledgeServiceTests` — reindex idempotency, guardrail retrieval, delete protection, search boosts | 8 |
| Infrastructure | `KnowledgeDocumentParserTests` — chunking, section refs, content hash | 6 |
| Infrastructure | `PseDailyQuotationParserTests` — header mapping, negatives, suspension, missing markers | 6 |
| Infrastructure | `DailySignalSchedulerRetrainTests` — weekday/hour trigger, per-horizon retrain, scope isolation | 5 |
| WebApi | `ComplianceGuardMiddlewareTests` — loopback gate, operator key (constant time), `/health` bypass, licensed passthrough | 18 |
| WebApi | `DeskWorkGateTests` — idle gate admits, second caller refused while held, release makes it reusable, exactly one of eight concurrent racers wins, unmatched `Exit()` throws | 4 |
| WebApi | `SignalsControllerRateLimitTests` — the four expensive actions carry `DeskRateLimitPolicies.Expensive` (reflection), five stored reads carry none | 9 |
| WebApi | `RequestCancellationMiddlewareTests` — 499 on abort, no write after cancellation | 4 |
| WebApi | `KnowledgeControllerTests` — upload attestation, protected-delete mapping | 2 |
| WebApi | `FeedHealthControllerTests` — overall status ignores the best-effort feeds, `Failed` on dead Edge history, `Unknown` before the first fetch, an empty store, an unreadable store (200 + no path leaked), configuration reported without the API key | 6 |

### Frontend — `frontend/src/lib/__tests__` (6 files, React-free)

| File | Covers | Cases |
| --- | --- | --- |
| `plainAdvice.test.ts` | `plainAdvice` / `plainStatus` / `describeSignalRow` / formatter copy for every plan, verdict, scorecard and backtest field, plus the "not advice" wording | 40 |
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
- [ ] **Frontend + docs** — mirror new server fields in `types/signals.ts` / `plainAdvice.ts`, extend `PseDisclosureClassifier` categories, and keep the README/memory-bank counts in sync (now 548 executed + 1 skipped / 87). `signalsApi.orderTicket` still has no caller and no `horizon` argument, so the manual sizing endpoint stays unthrottled by design.

### Data truth — the 2026-11-26 assessment, slices 3–4 (next up)

The audit behind slice 1 found seven data sources and one LLM, and the failure mode that mattered was **silence, not loudness**. Slices 1 (the feed ledger) and 2 (per-company disclosure attribution) are done — see their sections at the top; these are the rest, in the order they should ship.

- [x] **Slice 2 — restore the disclosure/trap pillar.** `PseEdgeDisclosureSource` resolves `ticker → cmpy_id` through the existing `PseEdgeSymbolSearchUrl` and fetches **one page per screened candidate** (`companyDisclosures/search.ax?keyword={cmpy_id}`, ≤25/run, 8-way, 10-min cached), so attribution is correct by construction instead of guessed from a market-wide headline table. `DisclosureCompanyFanoutLimit = 25` is the setting; `0` is the rollback. The market-wide scrape stays as the fallback, and the same probe fixed it too (month-name announce dates, form/report identifier cells). Live: 22–47 filings per named company vs **3 of 60** names from the fallback. The planned `companyPage/companyDisclosures.do?cmpy_id=` route was probed and is **404** — it was never shipped.
- [ ] **Slice 3 — surface the degradation in the run itself.** When disclosures answer but attribute nothing, emit the existing `SignalDtos.Warnings` line "trap flags are unenforced this session"; flag an index-proxy fallback the same way. The ledger reports it after the fact; this puts it on the screen the picks are read from.
- [~] **Slice 4 — measure the model.** **Shipped (2026-11-26, LLM pass):** `20261126124346_AddSignalRankingProvenance` (`SignalItems.QuantPreScore`/`QuantRank`, 12 run-provenance columns incl. `PromptHash`), `OllamaGenerationMetadata` on the engine seam, `SignalRunDto.ModelProvenance`, `SignalItemDto.QuantPreScore`/`QuantRank`, and `SignalScorecardDto.RankingQuality` (mean Spearman rho model-vs-screen + each side's top-three win rate / mean R). The ranking call is also no longer a bare JSON-mode chat: schema `format`, `seed`, `num_ctx`, `num_predict`, `top_p`, optional `think`, and the dead `rank`/`drift_status`/`previous_rank` fields are gone. **Still open:** opt-in prompt archive (`Signals:ArchivePrompts`, default false) + `POST /api/signals/eval-prompt-replay` behind the existing `Expensive` policy — deliberately deferred until a graded sample exists to replay against.
- [ ] **Deliberately deferred:** `CorporateActionAdjuster` (unadjusted prices mean a split distorts ATR, RS63 and every barrier), the dividends feed (`null` today), embedding access (`/api/embed` answers `401` for this key, so retrieval is lexical), the DQR staying manual-only, and the F-03 prompt-fencing patch (`security_audit_report.md:1561`, drafted but unapplied — keep it separate so a prompt change is never conflated with a data fix).

### UI/UX follow-ups (2026-11-26 legibility pass)

- [ ] Real browser **and screen-reader** pass (VoiceOver / NVDA) over Simple and Pro from 320px to 2560px; the repo has no DOM test harness, so announcement behaviour is reasoned, not observed.
- [ ] **Validate the up/down colour convention with 3–5 PH retail readers** before any further colour work (several Philippine broker apps use red-up/green-down). Record the outcome in `productContext.md`; the `▲/▼` glyph shipped as the interim redundancy.
- [x] ~~Put the remaining Simple surfaces on the same type floor~~ — the 2026-11-26 typography pass made the floor **global**: `--text-2xs` (11px) is the smallest size in the app and no `text-[8px]`/`[9px]`/`[10px]` survives, so `MarketDirectionHero`'s drawer copy, `DropPanel` and `WelcomeSetupBanner` inherit it without per-surface classes.
- [ ] A **pixel** check of that ramp in a real browser (dark + light, Simple + Pro, 320 → 2560) — the pass was verified through the emitted CSS and the grep inventory, not screenshots; headless Chrome hangs on the seeded app in this environment.
- [ ] Optional: a `GET /api/signals/run-status` read so a mid-run reload attaches to the in-flight run instead of starting a second one — needs a `systemPatterns.md` decision first because it adds a run-state resource.
- [ ] **Install surface is still missing** — no `site.webmanifest`, so there is no 192/512 install icon, no maskable icon and no `og:image` for link previews; the SVG + ICO + 180px touch trio covers browser tabs and iOS "Add to Home Screen" only.
- [~] **Remaining breakpoint + assistive pass** — the 2026-11-26 responsive pass now *covers* 320px and 2560px in CSS (new `xs`/`3xl`/`4xl` stops, `.shell` clamp gutters, container queries, `.tap`, `.scroll-x`), but only 390 (iframe), 768, 1024, 1280 and 1440-Pro were ever captured in a real browser. Still open: a visual capture at 320 and 2560, and the VoiceOver/NVDA run (never attempted — no DOM harness exists).
- [ ] **Phase 2 — page-body hierarchy** (decision deferred until the header is seen in a browser): a visual weight ladder so the market-call hero outranks the "Nothing changed" strip, and deleting the duplicated empty-state copy in `DropPanel`. The shared section-header shape is done — `AdviceCardList`, `SignalTable`, `DropPanel` and `RegimeStrip` all use `.type-eyebrow` + `.type-title` at `px-4 py-3` / `p-4 sm:p-5`.

### Performance pass — follow-ups

- [ ] Invalidate the download cache from the other manual entry points if a warm body ever proves stale there (`DailyQuotationImportService`, `POST /api/signals/train-all`); the daily run, research and replay deliberately reuse it.
- [ ] Confirm the Edge pagination split against a real outage — first page fatal, extra pages logged and skipped.
- [ ] Re-run the benchmark only in `Release`; `Debug` timings on this laptop are noise.
- [ ] Raise `MarketDataCache.DefaultMaxEntries` if the tracked universe grows far beyond ~58 names.

### Security remediation (`security_audit_report.md`, 2026-11-26 — 15 findings)

- **Done** — F-01/F-02 partly: `ComplianceGuardMiddleware` (loopback-only `PersonalLocal`, constant-time operator key on writes in `LicensedCommercial`), loopback `AllowedHosts`, `/health` bypass.
- **Done** — F-13: `DeskWorkGate` (`409` on a concurrent expensive job) plus three fixed one-hour policies (`429` + `Retry-After`): `Expensive` on `run-now`/`backtest`/`train`/`train-all`, `Upstream` on `research/{symbol}`, and `Model` on `knowledge/upload|reindex`; `UseRateLimiter()` is placed after the compliance guard. The cross-site write guard (`403`) and the five-wrong-key `OperatorKeyThrottle` (`429` + `Retry-After`) are also landed. Verified live (`503, 503, 429`; concurrent `409`; `401 ×4 → 429`; cross-site `403`).
- **P0/P1 open** — F-03 prompt fencing for retrieved text; F-06 `KnowledgeController` error detail; F-07 chunk bounds + batched embeddings; F-05 mostly closed (`IsTradablePrice` bounds every peso identity, `GetBracket` itself still unguarded). F-04 is closed at the HTTP edge (`DeskRequestLimits`, `400` outside `1..1825`).
- **P2 open** — F-08 security headers/HSTS, F-09 unbounded free text (`PortfolioTrades.Notes` is still unfiltered, which is the DPA/PII ingress), F-10 regex timeout/response caps, F-11 SQLite file mode still `0644`, F-12 committed lock files + `NuGetAudit` + `PdfPig` bump, F-14 `GET` mutating state, F-15 raw model response persistence (partially closed by the LLM pass: the parsed output is stored as `RawModelResponseJson` and each run now carries `PromptHash`/token/latency provenance, but the **raw prompt/thinking text archive** and the replay endpoint are still absent).
- **Auth** — still no user authentication; the origin allowlist and the per-caller operator-key lockout now cover drive-by writes and key guessing, but the operator key remains a single-operator convenience (a bundle-shipped key is readable by anyone who loads the page). Keep the desk on `127.0.0.1` and treat `LicensedCommercial` as "one trusted operator behind their own network boundary", not as multi-user identity.

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

