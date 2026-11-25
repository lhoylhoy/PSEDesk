# Third-Party Notices

PSE Signal Desk is personal research tooling. It depends on other people's software, and it reads
market data and public filings published by others. This file records what those dependencies are and
the posture the project takes towards them. It is not legal advice and it is not a substitute for the
licence text it points at.

Last reviewed: 2026-11-26.

## 1. Data sources — read this first

### PSE market data is proprietary

The Philippine Stock Exchange owns its market data (last sale, best bid/offer, volume, indices, the
Daily Quotation Report). Using it for **personal, non-commercial research** on your own machine is a
different posture from **redistributing** it. Redistribution — publishing prices, running a service
for other people, relaying a feed, or selling anything derived from it — requires a
market-data vendor / redistribution agreement with the PSE.

This project does not have one, and that is why it ships locked down:

- `Compliance:DeploymentMode` defaults to `PersonalLocal`; `ComplianceGuardMiddleware` refuses every
  non-loopback request with `403`.
- `LicensedCommercial` mode exists for an operator who *does* hold the agreement. It requires
  `PSEDESK_OPERATOR_API_KEY` and fails closed (`503`) when the key is missing.
- The desk does not mirror, cache for others, or re-serve exchange data. It stores what it needs to
  score its own paper record.

**Do not** point this at a public mirror of the PSE Daily Quotation Report, and do not host it for an
audience without the agreement.

### Sources the pipeline reads

| Source | Endpoint | Used for | Posture |
| --- | --- | --- | --- |
| Yahoo Finance chart API | `query1.finance.yahoo.com/v8/finance/chart/{symbol}` | Daily OHLCV for `.PS` names, dividends, `PSEI.PS`, `PHP=X`, `^TNX`, `BZ=F`, `EPHE` | Unofficial public endpoint, no API key. Yahoo's Terms of Service restrict commercial use and redistribution. Read them before deploying beyond personal use. No warranty of accuracy; the endpoint can change shape or rate-limit without notice. |
| PSE Edge | `edge.pse.com.ph` | Company disclosures, classification, trap flags | Public announcement pages published by the exchange for investor information. Scraped politely (identifying `MarketData:UserAgent`, small batch, no hammering). Redistribution of the scraped content is not intended or permitted. |
| PSE Daily Quotation Report | operator-supplied URL, or `POST /api/signals/quotation-report` | Net foreign flow, suspension flags | Only consulted when an operator configures a URL they are entitled to read or uploads a file they have. Not bundled, not mirrored, never substituted with synthetic values. |
| Ollama Cloud | `ollama.com` (per `OllamaCloud:BaseUrl`) | Reasoning, fast extraction and embedding models | Commercial API under your own account and key. Its terms govern the prompts and passages you send; see `PRIVACY.md` §2 for exactly what that is. |

Nothing here is presented as an official PSE product, and none of these providers sponsor or endorse
the desk. If a provider's terms conflict with how you intend to run it, the provider's terms win and
you must stop using that source.

## 2. Knowledge base content

`backend/knowledge-base/` holds original, hand-written summaries of PSE trading rules and playbooks
(board lots, tick sizes, the static price band, fee structure, risk process). They are the author's own
words, not excerpts of any book, broker report or paid publication. Where a rule is prescribed by the
exchange, the summary cites the rule rather than reproducing its text.

You may add documents only through the upload endpoint, which now requires an explicit attestation
(`confirmRightsAndCloudProcessing=true`) that you hold the rights to index the material, that it
contains no personal data and no material non-public information, and that its passages will be sent
to the configured cloud model. See `DISCLAIMER.md` §6.

## 3. Backend dependencies (NuGet)

All are permissively licensed (MIT or Apache-2.0) and are used unmodified.

| Package | Version | Licence | Used for |
| --- | --- | --- | --- |
| `Microsoft.EntityFrameworkCore.Sqlite` | 10.0.12 | MIT | Local store for sessions, signals, blotter and knowledge chunks |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.12 | MIT | Migrations tooling (design-time only) |
| `Microsoft.AspNetCore.OpenApi` | 10.0.12 | MIT | OpenAPI document |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | MIT | Logging seams in the Application layer |
| `Microsoft.Extensions.Hosting.Abstractions` | 10.0.0 | MIT | The scheduled-run host abstraction |
| `Microsoft.Extensions.Http` | 10.0.0 | MIT | Typed `HttpClient` registration for the market-data providers |
| `PdfPig` (UglyToad) | 0.1.16 | Apache-2.0 | Text extraction for uploaded PDFs and the DQR parser |
| `xunit`, `xunit.runner.visualstudio` | 2.9.3 / 3.1.4 | Apache-2.0 | Test framework |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | Test host |
| `coverlet.collector` | 6.0.4 | MIT | Coverage collector |

## 4. Frontend dependencies (npm)

| Package | Version | Licence | Used for |
| --- | --- | --- | --- |
| `react`, `react-dom` | 19.3.x | MIT | UI runtime |
| `@radix-ui/react-dialog` | 1.3.x | MIT | Accessible dialogs, including the legal consent gate |
| `@radix-ui/react-select` | 2.4.x | MIT | Horizon / filter selectors |
| `@radix-ui/react-tabs` | 1.2.x | MIT | Tabs inside the research and scorecard drawers |
| `@radix-ui/react-tooltip` | 1.4.x | MIT | Field hints and provenance tooltips |
| `vite`, `@vitejs/plugin-react` | 8.3.x / 6.3.x | MIT | Build and dev server |
| `tailwindcss`, `@tailwindcss/vite` | 4.3.x | MIT | Styling |
| `typescript` | 7.0.x | Apache-2.0 | Type checking (`tsc --noEmit`) |
| `@types/react`, `@types/react-dom` | 19.3.x | MIT | Type definitions |

Frontend tests run on the Node.js built-in test runner (`node --test`) against the `.ts` files in
`src/lib/__tests__/`; no extra test framework is bundled.

## 5. Trade marks

"PSE", "Philippine Stock Exchange", "PSEi" and "PSE Edge" are the marks of The Philippine Stock
Exchange, Inc. "Yahoo Finance" is a mark of Yahoo. "Ollama" is a mark of its owner. They appear here
only to name the sources the desk reads, with no affiliation, sponsorship or endorsement implied.

## 6. Reporting a problem

If you believe this repository infringes a right you hold, open an issue describing the file and the
claim. Infringing material — a mirrored report, a copied book chapter, a scraped feed — will be
removed rather than argued about.
