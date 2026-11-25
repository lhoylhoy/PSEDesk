# Active Context — PSE Signal Desk

**Last updated:** 2026-11-26 (documentation sync pass — README and memory bank reconciled with the code; **390 backend / 49 frontend tests green**, `npm run build` green)
**Last feature pass:** 2026-11-26 (compliance hardening — loopback guard, operator key, legal notices, first-run gate, non-solicitation copy)
**Current task:** none in flight. This pass changed **documentation only**: no source, config, knowledge-base, dependency or migration was touched.

## Where the desk stands

A single-operator research desk for the Philippine Stock Exchange, with all four horizons live:

- **Ingestion.** Yahoo Finance v8 charts (`PSEI.PS` or an equal-weight heavyweight proxy, `PHP=X`, `{TICKER}.PS`), PSE Edge filings (row-attributed, word-boundary matched), an optional official PSE Daily Quotation Report (live templates or operator upload) and three best-effort macro series (`^TNX`, `BZ=F`, `EPHE`). No synthetic series and no demo tape exist.
- **Screening.** A deterministic quant pass (Wilder RSI-14, ATR-14, EMA-20, SMA-50/150/200, 52-week high, Weinstein stage, RS63d, ATR%, VCP, turnover/volume), a hard PHP 5M average 20-day turnover floor, a top-25 cap, market breadth and sector rotation.
- **Ranking.** One strict-JSON Ollama call per horizon, grounded in retrieved knowledge sections, then server-owned post-processing: unknown ticker / thesis-less / hard-trap discard, tick-grid normalisation, the `DAY` static band and net-R:R gate, drift recomputation, drop classification and citation verification.
- **Pricing.** `ProbabilisticEdgeService` owns the win probability, break-even rate, EV, Quarter-Kelly stake and `TRADE` / `SMALL_SIZE` / `WATCH_ONLY` verdict. An optional **promoted** per-horizon logistic model (a residual over the prior, ±6pp tilt, Kelly rescale) and a live calibration offset feed in; the desk memory (realized record, sectors that paid/bled, stop-outs, ranked factors) is printed into the prompt.
- **Sizing.** `OrderTicketCalculator` builds one ticket per pick from a `BookSizingSnapshot` (risk budget, free cash, heat, sector room, 2% of 20-day turnover, governor multipliers, slippage/ADV participation, execution guidance) and routes a same-ticker order into a legal pyramid add.
- **Grading and replay.** The forward-outcome ledger grades only plans the tape actually filled; `BacktestService` replays the same rules causally into a multi-slot, benchmarked equity curve with a nested walk-forward refit.
- **Paper book.** A cash ledger with raise-stop, close, trim and pyramid merge, plus expectancy / profit factor / payoff telemetry, drawdown posture and alerts.
- **Terminal.** **Simple** (market call, then at most three plan cards) and **Pro** (dense table and tiles) read one payload; persisted light/dark, bottom-sheet dialogs, card fallbacks from 320px up, a zero-dependency SWR cache with intent preloaders, and a first-run legal gate.
- **Knowledge base.** 8 built-in documents / 46 sections in SQLite, BM25 + cosine RRF retrieval with mandatory guardrails and horizon/sector/trap boosts, optional embeddings, and uploads behind a rights attestation.

## Verified in this documentation pass (2026-11-26)

| Command | Result |
| --- | --- |
| `cd backend && dotnet test PseSignalDesk.slnx --nologo` | `Failed: 0, Passed: 390, Skipped: 0, Total: 390` (~1 s, exit 0) |
| `cd frontend && npm test` | `tests 49 · pass 49 · fail 0` (≈0.2–0.6 s) |
| `cd frontend && npm run build` | `tsc --noEmit && vite build` exit 0, `✓ 129 modules transformed`, `✓ built in ≈0.15–0.35 s` (`index 86.76 kB`, `ScorecardDialog 39.19`, `PortfolioDialog 35.22`, `ResearchDialog 21.43`, `TickerDetailDialog 19.67`, `vendor-react 206.75`, `vendor-radix 118.02`) |
| `sqlite3 backend/src/PseSignalDesk.WebApi/pse_signals.db "select slug, chunkcount …"` | 8 built-in documents / 46 chunks — matches the `##` heading count in `backend/knowledge-base/` |
| `git ls-files \| wc -l`, `git ls-files --others --ignored --exclude-standard \| wc -l` | 230 tracked, 5531 ignored, tree clean before the documentation edits |

Cross-checked against the code before writing: the 26 backend test files, every controller route attribute (including `catalog`, `model`, `train`, `train-all`, `desk-memory` and `trades/{id}/trim`), `appsettings.json`, the nine EF migrations, the per-leg fee constants, `HorizonProfile` (all four horizons require a net R:R ≥ 2.0; EV minima 0.20 / 0.15 / 0.20 / 0.25), `ComplianceGuardMiddleware`, the `legalConsent` storage keys, and the knowledge-base file list.

## What this pass changed in the docs

- **`README.md`** — added the learned-model and Simple/Pro feature bullets, corrected the replay (multi-slot, benchmarked) and sizing (real book, not a fixed PHP 100k budget) descriptions, listed the six real playbooks, refreshed the repository layout and the component/lib inventory, added the missing API routes (`catalog`, `model`, `train`, `train-all`, `desk-memory`, `trim`, research parameters), replaced `282 passing` with `390 passing` + `npm test` (49), and rewrote the upload/compliance limitation.
- **`projectbrief.md`** — 390 backend / 49 frontend tests, 8 built-in documents / 46 chunks, refreshed layout and test summary.
- **`productContext.md`** — the Knowledge Base flow now names 8 documents (46 chunks) and the rights attestation.
- **`systemPatterns.md`** — library names and count, the two new migrations, the upload attestation, a new *Compliance posture and the first-run legal gate* section, the compliance middleware in the WebApi seam, and one duplicated per-horizon hit-rate paragraph collapsed.
- **`techContext.md`** — library count, current git state (3 commits, HEAD `3e2aea4`, 230 tracked / 5531 ignored) and a new *Documentation map* section with the sync rules.
- **`activeContext.md` / `progress.md`** — rewritten as present-state documents; superseded per-pass narratives and duplicated facts were removed rather than appended.

## Invariants that must not be relaxed

- **The server owns every invariant.** Prices, tick rounding, board lots, the static band, the net-R:R gate, sizing, odds, verdicts, drift, drop category, citations and the bear-case fallback are computed in code after the model answers. The model never supplies a probability, a size or a drift status.
- **Fees are per leg, because the tax is.** `BuyLegFeeRate` 0.295% and `SellLegFeeRate` 0.395% (the sell leg carries the 0.10% stock transaction tax) sum to `RoundTripFeeRate` 0.690%; the same constants drive the odds, tickets, blotter, training trials, `NetRealizedR` and the knowledge base.
- **Only an executed entry can be graded.** No fill inside the horizon window is `UNFILLED` — null exit, null R, excluded from the win rate, profit factor, Brier score and `openCount`.
- **Slippage is one cash debit.** The replay charges it at entry, stores it per trade and subtracts it from realized P&L, so `NetReturnAfterSlippagePct` equals `NetReturnPct` by construction.
- **A proxy index may drive context, never a level.** When the PSEi series is unusable, `PseiClose` / `PseiSma50` / `PseiSma200` stay unreported (`0`) and the regime note says "proxy".
- **A missing cell is a null, never a neighbour.** A `-`/`N/A` DQR marker keeps its own column, and a row with no net-foreign figure is dropped.
- **The browser never sizes.** `lib/riskSizing.ts` renders the server ticket; `lib/plainAdvice.ts` only re-words server values.
- **Live-or-fail.** A missing feed is a `503`, a missing key a `400`, an empty candidate set a `502`; nothing is persisted in any of those cases.

## Open work, in priority order

### 1. Security remediation still open

`security_audit_report.md` (2026-11-26, read-only) records 15 findings. `ComplianceGuardMiddleware` now covers most of F-01/F-02 (loopback-only in `PersonalLocal`, constant-time operator key on writes in `LicensedCommercial`, `AllowedHosts` narrowed). Still open and confirmed in the code:

- **F-03 — prompt fencing:** retrieved playbook text still reaches the prompt without untrusted-content delimiters (`OllamaPromptBuilder.AppendPlaybooks`).
- **F-04 — lookback clamp:** `PseMarketDataProvider.NormalizeLookback` has a floor but no ceiling, so a huge `lookbackDays` still reaches the upstream fetch.
- **F-05 — price bounds:** `PseBoardLotService` has no `ValidatePrice`; extreme decimals can still overflow the sizing maths.
- **F-06 — error detail:** `KnowledgeController` still returns `ex.Message` to the client.
- **F-07 — chunk/embed bounds:** `KnowledgeDocumentParser.ChunkPlainText` still bypasses `SplitLong`, and `OllamaEmbeddingClient.EmbedAsync` still sends every chunk in one request.
- **F-08 – F-15** — security headers/HSTS, unbounded free text (`title`, tags, notes, file name), regex timeouts/response caps, SQLite file mode, lock files + `NuGetAudit` + a `PdfPig` bump, rate limiting/run gate, `GET` mutating state, unbounded raw model response persistence.
- **F-01/F-02 remainder** — there is still no user authentication, no origin check on unsafe methods and no rate limiter; keep the desk on `127.0.0.1` until those land.

### 2. Live verification backlog (needs network + `OLLAMA_API_KEY`)

- `POST /api/signals/run-now` for all four horizons; a second session for real drift and drop categories.
- `POST /api/signals/quotation-report` against a stored session; confirm `foreignFlowSource` and the suspension trap.
- `POST /api/signals/train-all`, then read the four books: `optimalKellyFractionMultiplier`, the Brier/log-loss pair, `validationExpectancyR` and the status (`LEARNED` vs `PRIOR_FALLBACK`).
- `GET /api/signals/backtest?horizon=WEEK` once `PSEI.PS` returns ≥200 bars; compare `ExcessReturnPct` with a prior-pinned baseline.
- The bootstrap's success path (`Bootstrap money-manager refit finished … LEARNED`, then `skipped: every horizon already carries a fitted model`).
- Ticket parity: `SignalItemDto.orderTicket` vs `GET /api/research/{ticker}` vs `GET /api/portfolio/order-ticket` on the same plan.
- A real browser/device pass (Simple and Pro) at 320–2560px; no component/DOM suite exists, so this is the only UI verification.

### 3. Tuning questions (policy defaults, not fitted)

- Direction pillars (20/14/8/6/6), the 60/45 call thresholds and the 1.25 net-R:R chase floor (2.0 on `DAY`).
- The `+1R` break-even / `+1.5R` EMA20-trail ladder, and the 5%/10% drawdown ladder — they exist in three places (training, replay, wave plans).
- The `[0.50, 1.50]` governor band, the 2% Kelly reference, the 6% heat ceiling, two names / 35% notional per sector, fixed λ = 25 in the replay refit, and whether `EV > 0` is too strict on a thin tape.
- The calibration prior (0.42), 12 pseudo-observations and the ±10pp offset clamp once ≥30 picks are graded.
- The slippage model (`2 × tick / price` + √impact, capped at 1.5%) against real fills.

### 4. Documentation upkeep

Follow the sync rules in `techContext.md` (*Documentation map*): behaviour → README + memory bank; compliance/copy → the three notices + `legalConsent.ts` (bump `CURRENT_LEGAL_VERSION`) + README; a fee/rule change → `PseBoardLotService` + `backend/knowledge-base/` + README + memory bank. Record counts only after running the commands.

## Session checklist for contributors

1. Read all six `memory-bank/*.md` files before starting.
2. Confirm the baseline: backend `dotnet test PseSignalDesk.slnx` (**390**), `cd frontend && npm test` (**49**), `npm run build`.
3. Update `activeContext.md` when starting/finishing a task and `progress.md` when status changes.
4. Keep `systemPatterns.md` in sync with architectural changes and `techContext.md` with tool/config changes.
5. Never persist secrets (`OLLAMA_API_KEY`, `OllamaCloud:ApiKey`, `PSEDESK_OPERATOR_API_KEY`) — env or user-secrets only.
6. Never add a fallback that fabricates market data, never let a test hit the network, never compute a risk number in the browser.
7. Keep the compliance posture intact: loopback-only by default, no soliciting copy, simulated results labelled simulated, uploads behind the rights attestation, and the legal notices in sync with `lib/legalConsent.ts`.

