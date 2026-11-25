---
title: Desk Engineering Contract and Deterministic Invariants
slug: engineering_references
mandatory: false
horizons: DAY, WEEK, MONTH, YEAR
categories: MECHANICS, ENGINEERING
---

## Server-owned invariants

The model proposes; the server decides. These properties are computed in code after the
response is parsed, and no prompt instruction can override them:

- Trade levels are snapped to the PSE tick grid and rounded to four decimals.
- DAY plans are clamped inside the +/- static band around the reference close before the
  tick grid is applied, and a net-of-fees reward-to-risk gate of 2.0 is enforced.
- Board-lot size is resolved from the official 15-tier table, so the UI can show a real
  order size rather than a fractional position.
- Drift status (NEW, HELD, UPGRADED, DOWNGRADED) is recomputed from yesterday's list.
- Drop categories are recomputed from price action: a stop that traded through the
  invalidation level is INVALIDATED, a reached target is TARGET_HIT, everything else is
  DISPLACED.
- Citations survive only if they match a section that retrieval actually supplied.

Keeping these deterministic is what makes the output auditable: the ranking is a model
opinion, the levels and the bookkeeping are arithmetic.

## Grounding and citations

Every prompt carries the mandatory `pse_rules` sections plus the horizon, sector and
trap-matched playbook sections. Each section has a stable reference of the form
`slug#section-anchor`, and the model is asked to return the ones it relied on
in `cited_rules`.

The server then intersects `cited_rules` with the retrieved references. An unknown,
invented or hallucinated citation is discarded silently, so the citation list on a card is
a factual record of what grounded that thesis — not decoration.

## Retrieval pipeline

The library stores documents and chunks in SQLite. Chunks are embedded as little-endian
`float32` blobs (the `sqlite-vec` layout), scored by BM25 and by cosine
similarity, and fused with reciprocal-rank fusion. Horizon, sector and structural-trap
boosts break ties, and the mandatory guardrails are always injected.

If no embedding model is configured — or the embeddings call fails — retrieval silently
falls back to lexical plus metadata ranking. Signals never fail because the brain is
unreachable; they only run with fewer citations.

## Live-only data contract

Candles come from Yahoo Finance and disclosures from PSE Edge. A transport failure, an
empty page or a stale session raises a service-unavailable error rather than falling back
to synthetic data. There is no demo seed and no offline ranking engine: the desk is
live-only by design, and an honest 503 is preferable to a plausible fiction.

## Trading calendar

Signals are produced after the end-of-day settlement window (15:35 PHT). Weekends and
Philippine regular holidays are skipped using an explicit, reviewable table of fixed and
movable observances, so the scheduler never creates a phantom trading day in the drift
chain.
