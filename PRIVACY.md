# Privacy Notice

PSE Signal Desk runs on your own machine, stores its data in a local SQLite file, and has no accounts,
no analytics, no cookies for tracking and no telemetry. This notice describes what is stored where,
and which requests leave your machine.

Last reviewed: 2026-11-26. The reader-facing summary is the first-run gate in
`frontend/src/lib/legalConsent.ts`.

## 1. What the desk stores locally

| Where | What | Why |
| --- | --- | --- |
| `backend/**/psedesk*.db` (SQLite) | Market snapshots, per-session metrics, disclosures, published signals, the forward-outcome ledger, the paper blotter and its settings, knowledge chunks and their embeddings, and model run metadata | The pipeline, the scorecard and the paper blotter read from it. Nothing is uploaded. |
| `backend/knowledge-base/*.md` | Hand-written PSE rules and playbooks shipped with the repo | Retrieval corpus for the model |
| Browser `localStorage` | `psedesk.legalAcceptedVersion` (the consent version you accepted) | So the first-run gate does not reappear on every reload |
| Process environment | `OllamaCloud__ApiKey`, `PSEDESK_OPERATOR_API_KEY` | Credentials. Never persisted by the app, never logged and never returned by an endpoint. |
| Logs | Run warnings, provider failures, endpoint timings | Debugging. Tickers and prices only, never credentials. |

There is no per-user profile, no email address, no phone number and no payment data anywhere in the
schema. The paper blotter records positions you type in yourself; it is a simulation, not a
connection to your brokerage account.

## 2. What leaves your machine

Four outbound flows, all of them necessary for the pipeline to produce a signal:

1. **Yahoo Finance chart endpoint** (`query1.finance.yahoo.com/v8/finance/chart/...`) — ticker symbols
   only, for daily OHLCV, dividends and the cross-asset series. No credentials, no identifiers.
2. **PSE Edge announcement pages** (`edge.pse.com.ph`) — ticker symbols and company ids, to read
   public disclosures. No credentials, no identifiers.
3. **PSE Daily Quotation Report** — only if an operator configures a URL or uploads a file. Otherwise
   no request is made.
4. **Ollama Cloud** (`OLLAMA_API_KEY`) — the reasoning model, the optional fast-extraction model and
   the embedding model. This is the flow that carries your own content:

   - the deterministic candidate table (tickers, prices, indicators, plan levels) and the market
     context block;
   - the retrieved knowledge-base passages, including passages from documents you uploaded;
   - disclosure headlines and categories for the shortlisted names;
   - your **prompt override**, if you set one in the research drawer.

   The paper blotter, the scorecard and the settings endpoints never call the model.

If you want zero outbound model traffic, do not configure `OllamaCloud:ApiKey`; retrieval then falls
back to BM25 + tag scoring, and the ranking step (which needs the model) fails loudly with `502`
rather than inventing a book.

## 3. Personal data and the Data Privacy Act

The desk is not designed to hold personal data and should not be fed any: not client names, account
numbers, TINs, ID scans or anything resembling an order book of real people. Every upload attestation
includes that undertaking, and the recommended deployment keeps the API on loopback only.

If you nevertheless process personal data through it, you become a personal information controller
under Republic Act No. 10173 (Data Privacy Act of 2012) and its IRR, with the obligations that follow
(lawful basis, purpose limitation, retention, security, breach notification and appointment of a Data
Protection Officer where required). Nothing in this project implements any of that for you.

## 4. Retention and deletion

Nothing expires on its own. To remove data:

- delete the SQLite file(s) under `backend/` to drop every stored session, signal, blotter row and
  knowledge chunk, then run the pipeline again; the schema is recreated on boot;
- delete the individual Markdown file plus `DELETE /api/knowledge/{id}` to drop an uploaded document
  from the corpus and its embeddings;
- clear the `psedesk.legalAcceptedVersion` key in the browser to see the gate again.

Clearing local data does not reach into Ollama Cloud's side. Their retention is governed by your
agreement with them, not by this project.

## 5. Cookies, analytics and third-party scripts

None. The frontend ships no analytics, no ad tech, no session cookies and no third-party script tags.
The only storage used is `localStorage` for the consent version, and every access is wrapped so a
browser that refuses storage still runs the desk (the gate simply shows once per session).

## 6. Security posture

- The API binds to loopback by default and refuses non-loopback requests in `PersonalLocal` mode
  (`ComplianceGuardMiddleware`).
- In `LicensedCommercial` mode every mutating request (POST/PUT/PATCH/DELETE) requires the operator
  key from `PSEDESK_OPERATOR_API_KEY`, compared in constant time. With no key configured, mutations
  fail closed with `503` instead of running unauthenticated.
- Credentials live in environment variables, not in the repo, and are redacted from logs.
- There is no multi-user authentication. Do not expose the port to the internet, and do not put the
  API behind a public reverse proxy without adding authentication and TLS yourself.

## 7. Changes

This notice is versioned with the consent gate. Material changes bump `CURRENT_LEGAL_VERSION` in
`frontend/src/lib/legalConsent.ts`, which re-prompts every browser before the desk renders again.

## 8. Contact

This is a personal project: there is no data-protection officer, no support desk and no data
processing agreement on offer. For questions about your own deployment, raise an issue in the
repository that hosts your copy.
