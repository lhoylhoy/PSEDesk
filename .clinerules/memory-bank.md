# Memory Bank Protocol (PSE Signal Desk)

This project uses a **memory bank** in `memory-bank/` to preserve context across sessions. Treat it as the source of truth for *why* the code looks the way it does.

## At the start of every task

Read **all** files in `memory-bank/` before planning or editing:

1. `projectbrief.md` — what the product is, scope, success criteria, layout.
2. `productContext.md` — why it exists, users, flows, UX/tone rules.
3. `systemPatterns.md` — architecture, pipeline, decisions, data model, conventions.
4. `techContext.md` — toolchain, commands, config, external services, gotchas.
5. `activeContext.md` — current focus, recent changes, open questions.
6. `progress.md` — what works, test inventory, known issues, next candidates.

If a file is missing or clearly stale, say so and fix it before proceeding.

## During work

- Prefer the documented patterns (Clean Architecture seams, deterministic server-owned invariants, typed API client, Radix wrappers, centralized formatters).
- Do not introduce new dependencies or architectural layers without recording the decision in `systemPatterns.md`.
- Keep the offline-first and test-without-network guarantees intact.

## At the end of every task

Update the memory bank before reporting completion:

- `activeContext.md` — what changed, what is in flight, new decisions, next steps.
- `progress.md` — move items between "works" and "remaining", refresh known issues and verification results.
- `systemPatterns.md` — only when architecture, flow, data model or conventions change.
- `techContext.md` — only when dependencies, versions, commands, config or external services change.
- `projectbrief.md` / `productContext.md` — only when scope, goals, users or product behavior change.

Also refresh the **Last updated** date in `activeContext.md`.

## Rules

- Keep entries factual and current; delete or rewrite stale claims instead of appending contradictions.
- Record commands actually run and their real results (e.g. "55 tests passed"), not expectations.
- Never store secrets (`OLLAMA_API_KEY`, tokens) in the memory bank.
- Memory bank files are documentation, not a changelog: summarize the present state, with a short "recent changes" note where useful.
