# Project Brief — PSE Signal Desk

> Foundation document. Everything else in the memory bank builds on this file.

## What this is

**PSE Signal Desk** (`PseSignalDesk`) is a full-stack research terminal for the Philippine Stock Exchange:

- A **.NET 10 Clean Architecture API** that screens the liquid PSE universe, ranks a **screened list for four horizons** (`DAY`, `WEEK`, `MONTH`, `YEAR`), grounds every thesis in a **local RAG knowledge base** of PSE rules and playbooks, reconciles **day-over-day drift**, and explains **what dropped out and why**.
- A **React 19 + TypeScript + Vite** terminal with four horizon tabs, a market-regime strip, the drop panel, a per-ticker detail dialog (board lot, SMA150/52-week high, Weinstein stage, trap flags, playbook citations) and a **Knowledge Base drawer** for curating the grounding library.

The product is research tooling, **not investment advice**, and it is **not a licensed market-data service**: `README.md`, `DISCLAIMER.md`, `PRIVACY.md` and `THIRD_PARTY_NOTICES.md` state the posture, the API refuses non-loopback callers by default, and the terminal opens behind a first-run legal gate. Redistributing PSE market data or operating for other people needs an exchange market-data agreement and SEC licensing that this repository does not have.

## Core requirements

1. **Live data only.** Every candle comes from the Yahoo Finance v8 chart endpoint (`PSEI.PS`, `PHP=X`, `{TICKER}.PS`) and every disclosure from the public PSE Edge announcement page. No synthetic series, no demo tape, no offline engine, no quant top-up: a missing feed is an error (`503`), not an excuse to invent data.
2. **Grounded, not free-form.** The reasoning model sees only retrieved `knowledge-base/` sections (mandatory PSE guardrails + horizon/sector/trap-matched playbooks) plus a deterministic candidate table. A citation the retrieval did not supply is dropped server-side.
3. **Quant filter first.** Compute a deterministic technical picture per ticker from the live daily OHLCV: Wilder RSI-14, ATR-14, EMA-20, SMA-50/150/200, 52-week high, Weinstein stage, 20-day turnover, volume ratio, support/resistance and a 0–100 pre-score. The 15-point foreign-flow pillar is scored only when the official PSE Daily Quotation Report was parsed for the session; otherwise it stays `null` and the verified 85 technical points are rescaled.
4. **Hard liquidity floor.** Exclude any ticker below **PHP 5,000,000 average 20-day turnover**. Send at most the **top 25** survivors to the signal engine.
5. **LLM ranking with guardrails.** Ollama Cloud returns strict JSON; parsing is defensive (fences, prose, numeric strings); unknown tickers, thesis-less picks and picks tripped by a **hard trap** (`SRO_DILUTION`, `TENDER_OFFER_PIN`, `TRADING_HALT_RISK`) are discarded; one corrective retry on malformed output; drift and drops are always recomputed server-side; zero valid picks fails the run (`502`).
6. **PSE-orderable plans only.** The plan is normalized through the **15-tier board-lot/tick table**, clamped inside the **static `+50% / -30%` band** for `DAY`, must clear `target > entry > stop`, and sized in **whole board lots** by the server (`OrderTicketCalculator`: risk budget, free cash, single-position cap, 2% of 20-day turnover) into an order ticket the UI renders verbatim. `DAY` plans must also clear a **net (fee-adjusted) R:R ≥ 2.0**.
7. **Exit accountability.** Every ticker that left the Top 10 gets exactly one category: `INVALIDATED`, `TARGET_HIT` or `DISPLACED`.
8. **Two-model Ollama setup.** A reasoning model ranks the book; an optional cheap model performs a compact per-session disclosure/trap pre-extraction pass (`FastExtractionModel`, blank disables it). `EmbeddingModel` vectorises the knowledge base and degrades gracefully to lexical BM25 when unavailable.
9. **Daily cadence.** A hosted scheduler recomputes the next weekday **15:35 Asia/Manila** run and produces all four horizons; `POST /api/signals/run-now` supports manual runs per horizon.
10. **Stable API contract.** Enums are serialized as strings so the UI can switch on them directly; failures use RFC 7807 `ProblemDetails` (`400` config/input, `409` protected built-in document, `502` generation, `503` feed, `404` unknown session, `499` client abort).
11. **The desk learns from its own ledger, inside published bounds.** One logistic money-manager model per horizon is refit weekly from the desk's own ATR-barrier history, scored on a purged/embargoed held-out slice and promoted only when it beats the neutral prior's Brier score. A promoted model may tilt the live win probability by at most ±6 points and rescale the Quarter-Kelly stake inside `[0.15, 0.35]`; a rejected fit stays visible as `PRIOR_FALLBACK` and cannot reach live pricing. Calibration, the desk memory (win rate, expectancy, sectors, stop-outs) and the learned factors are fed back into the prompt, so the model's advice is conditioned on what actually happened.

## Scope (current)

| In scope | Out of scope (reserved / future) |
| --- | --- |
| `DAY`, `WEEK`, `MONTH`, `YEAR` horizons (one run per horizon per session) | Shorts, leverage, live portfolio P&L tracking |
| Long-only Top 10 per horizon | Intraday streaming, live order routing |
| On-demand research for any `{TICKER}.PS` (`/api/research/{symbol}`) | Research caching per session |
| Causal walk-forward replay of each horizon's own rules (`/api/signals/backtest`) | Tick-level / intraday backtesting |
| Server-sized order tickets + a **paper** blotter (open, raise stop, close, ledger, heat) | Live broker integration, real fills, multi-account books |
| SQLite RAG knowledge base: 8 built-in Markdown documents (PSE rules + 6 playbooks + engineering references, 46 chunks), upload/reindex/delete behind a rights attestation | Multi-user editing, per-document ownership, versioned document history |
| PSE 15-tier board-lot/tick table, static price band, net-R:R gate, board-lot sizing | A licensed, redistributable market-data feed (the desk reads Yahoo/PSE Edge under a personal posture and refuses non-loopback traffic by default) |
| Official PSE Daily Quotation Report ingestion (live templates + operator upload) for net foreign flow | A stable, guaranteed exchange DQR URL (operator supplies or mirrors it) |
| Deterministic disclosure classification + structural trap flags | Fundamental database (the model reads uploaded books, the desk stores no financials) |
| PSEi regime strip (close, SMA50/200, foreign flow with provenance, USD/PHP, US 10Y / Brent / EPHE) | Full PSE universe (`PseTickerCatalog` is ~58 names; research reaches any ticker) |
| SQLite single-file persistence, single-user desk (one paper account) | Server database, multi-tenant hosting, auth |
| Yahoo Finance v8 charts + PSE Edge announcements | |


## Success criteria

- `dotnet test backend/PseSignalDesk.slnx` passes (**390 tests** as of 2026-11-26).
- `npm test` in `frontend/` passes (**49 `node:test` cases**: `lib/__tests__/plainAdvice.test.ts`, which also covers `lib/pseNicknames.ts`, `lib/sessionSelection.ts` and the `riskSizing.ts` formatters, plus `lib/__tests__/legalConsent.test.ts` and `lib/__tests__/queryCache.test.ts`).
- `npm run build` in `frontend/` passes (`tsc --noEmit` + Vite build), with each dialog and the Pro table in their own chunk.
- The terminal is usable one-thumbed on a 320px phone and dense on a 2560px desktop from the **same payload**: dialogs become bottom sheets, wide tables fall back to cards, and no mobile variant fetches a different endpoint.
- With network + `OLLAMA_API_KEY`, `run-now` for two consecutive sessions produces all four drift states and all three drop categories from real history, for every horizon.
- Without either prerequisite the API fails loudly (`503` feed, `400` key) and persists nothing.
- No run can persist a price that is not orderable on the PSE tick grid.
- Model output can never invent a ticker outside the liquid candidate set, cite a rule retrieval did not supply, nor override drift/drop classification.
- The Knowledge Base drawer lists the seeded library, re-indexes idempotently and refuses to delete built-in playbooks (`409`).
- Research and replay never invent history: both answer `503` when the feed lacks the bars the horizon needs.
- The blotter's cash ledger reconciles: every open debits the fee-paid buy leg, every close credits the net sell proceeds, and no rule breach is ever silently resized.


## Repository layout

```
.vscode/                           # launch.json (API launch/attach, Chrome) + tasks.json (build/test/dev)
backend/
  PseSignalDesk.slnx
  knowledge-base/                  # curated Markdown grounding library (copied to the API output; 8 docs / 46 chunks)
    pse_rules.md                   # mandatory guardrails: board lots, static band, fees, traps, Weinstein stages
    playbooks/                     # day_week_momentum_vcp, day_week_risk_sizing, month_year_quality_value,
                                   # month_year_ph_macro_sectors, probabilistic_expectancy, asymmetric_wealth_compounding
    docs/engineering-references.md # the deterministic invariants the model may never override
  src/
    PseSignalDesk.Domain/          # entities, enums, PSE board-lot/tick/static-band rules, expectancy + fee maths
    PseSignalDesk.Application/     # quant filter, horizons, edge/odds, tickets, portfolio, research, backtest,
                                   # training, market direction, wave plans, pipeline, DTOs, interfaces
    PseSignalDesk.Infrastructure/  # EF Core SQLite, live Yahoo/PSE Edge provider (+DQR), Ollama clients, RAG, scheduler
    PseSignalDesk.WebApi/          # controllers (signals, research, portfolio, knowledge), compliance + 499 middleware,
                                   # DI wiring, appsettings, EF migrations
  tests/PseSignalDesk.Tests/        # 390 cases: board lot, quant, edge, tickets, portfolio, research/backtest, outcome,
                                   # training, direction/wave, classifier, parser, pipeline, knowledge, provider, prompt,
                                   # compliance, cancellation middleware
frontend/
  src/
    api/        # typed fetch client (ApiError + ProblemDetails handling, FormData uploads)
    components/ # TerminalHeader (+ phone "Desk controls" sheet), RegimeStrip, MarketDirectionHero, RidingNowStrip,
                # AdviceCardList, SignalTable (+ ProSignalCard), DropPanel, TickerDetailDialog, RiskSizerBar,
                # ScorecardDialog, BacktestPanel, ModelTrainerPanel, ResearchDialog, PortfolioDialog,
                # KnowledgeBaseDialog, QuotationReportImportButton, VerdictBadge, DriftBadge,
                # WelcomeSetupBanner, BeginnerGuideDialog, LegalDisclaimerGate
    components/ui/ # Radix wrappers: Tabs (scrollable rail), Dialog (bottom sheet + DialogStickyHeader), Tooltip,
                   # Select, Button, InfoTip, TechnicalDisclosure
    lib/        # formatters (PHP, PSE prices, Asia/Manila dates), horizons metadata, class joiner, theme, viewMode,
                # legalConsent, plainAdvice, pseNicknames, sessionSelection, riskSizing, queryCache (SWR client cache),
                # preloaders (chunk + data warmers)
    lib/__tests__/ # node:test suites (legalConsent, plainAdvice, queryCache); excluded from the browser build
    types/      # API contract types
```


## Key documents

- `README.md` — user-facing run instructions, API table, limitations.
- `memory-bank/` — durable context for AI-assisted work (this file and its siblings).
