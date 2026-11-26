# Disclaimer

**PSE Signal Desk is a personal research notebook. It is not an investment adviser, a broker, a
dealer, a fund manager or a source of personalised investment advice, and it is not registered or
licensed by the Philippine Securities and Exchange Commission.** Nothing it prints is a
recommendation, an offer, or a solicitation to buy or sell any security.

Last reviewed: 2026-11-26. The reader-facing summary of this document is the first-run gate in
`frontend/src/lib/legalConsent.ts`; when this file changes in a way a reader should re-read, bump
`CURRENT_LEGAL_VERSION` there so every browser sees the gate again.

## 1. No advice, no fiduciary relationship

Every number in the desk — the screened list, the verdicts (`TRADE` / `SMALL_SIZE` /
`WATCH_ONLY`), the entry, target and cut-loss levels, the odds, the cash percentage, the paper
blotter's exit instructions and the model's prose — is the output of deterministic rules and a
language model working on public prices. It is research material for one person's own analysis.

Using the desk creates no advisory, fiduciary, broker-client or agency relationship with anyone. The
model never gives personal recommendations and cannot know your income, debts, taxes, time horizon,
existing holdings or risk tolerance — the parts that actually decide whether a trade is suitable for
you.

## 2. Your money, your decision

You are responsible for every order you place. Before acting on anything here:

- verify the price, the board lot and the tick against your broker, and the filing against
  [PSE Edge](https://edge.pse.com.ph/);
- check that the company's current disclosures have not changed the picture the desk modelled;
- size the position so that hitting the cut-loss is survivable, and never trade money you cannot
  afford to lose;
- consider your own tax position. The desk models statutory fees and a slippage estimate, **not**
  capital-gains or income tax, and not your broker's own pricing.

Trading PSE-listed shares can lose money, including more than the fees modelled here. Illiquid names
can be impossible to exit at the quoted price.

## 3. Hypothetical, simulated and paper results

The backtest, the "Simulated CAGR" tile, the model scorecards, the desk-memory statistics and the
paper portfolio are **hypothetical or simulated**, not the record of a real account:

- the replay walks a stored tape forward with a book of up to five concurrent positions, 1% of
  equity risked per trade inside a 6% portfolio heat cap, at most two names and 35% of equity per
  sector, fractional-Kelly sizing, board-lot rounding, the 0.690% statutory round trip plus a
  board-lot slippage estimate, and money-manager weights refitted every 60 sessions;
- a simulated fill is an assumption. Real fills depend on the order book, queue position and
  liquidity that a daily bar cannot show;
- the forward scorecard grades the desk's own published picks against the tape. It is the model's
  paper record, not a client account, and it is not audited;
- results exclude taxes, and they exclude anything the operator changed in the configuration.

**Past performance and simulated performance are not reliable indicators of future results.** A
favourable path in one window is one sample from a wide distribution, and the parameters were chosen
by someone looking at the same history.

## 4. Market data is personal, non-commercial research data

Prices come from Yahoo Finance's public chart endpoint and filings from the public PSE Edge
announcement pages. Both are consumed under a personal, non-commercial research posture. The
exchange's market data is proprietary, and redistributing it — to other people, over a LAN, as a
service, or as a data feed — requires a PSE market-data vendor/redistribution agreement that this
project does not have.

That is why the API defaults to `Compliance:DeploymentMode = "PersonalLocal"` and refuses every
non-loopback request with `403`. Serving this desk to other people without the licence is not
permitted, and calling it a licensed or commercial product without one would be a misrepresentation.
See `THIRD_PARTY_NOTICES.md` for the sources and their terms.

## 5. The model can be wrong, and confidently so

The reasoning model is told not to invent probabilities and its citations are checked server-side,
but it can still misread a filing, mis-describe a chart, or write a fluent sentence that is simply
wrong. A thesis you cannot verify against a primary source should be treated as unverified. The
technical drawer exposes the raw fields behind every friendly sentence for exactly this reason.

## 6. Uploads are your responsibility

The knowledge base accepts Markdown, text and PDF documents so the model can quote them. Every upload
requires an explicit attestation that you hold the rights to index the document, that it contains no
personal information and no material non-public information, and that its retrieved passages will be
sent to the configured cloud model. Do not upload a book, report, research note or data feed you
merely have access to — copyright, database rights and confidentiality obligations all still apply
inside a personal tool. Material non-public information must never enter this system.

## 7. Instruction-shaped copy is for one self-user only

The plain-language surface is deliberately written as an instruction: the cards say "buy in this
band", "cut here", "lock break-even at +1R" and "ride the wave or trail to the target", and the
market call says "ride it", "be selective" or "stay in cash". That wording exists so a single
informed reader does not have to translate a table into a decision. It is **not** a
recommendation, and it is not written for anybody else.

Two consequences follow, and both are conditions of using this software:

- **Do not pass the output to another person as advice**, and do not present it as research you
  produced for them. Read it yourself, then make your own decision.
- **Do not use it to manage anyone else's money** — not a relative's, not a friend's, not a
  client's, not a fund's. Doing so can make you an unregistered investment adviser or an
  unlicensed fund manager under Republic Act No. 8799 (the Securities Regulation Code) and the
  rules of the Securities and Exchange Commission, and it brings the Data Privacy Act duties in
  `PRIVACY.md` §3 into play for the data you then hold.

The same rule applies to the paper blotter: it is a simulation of *your* book, not an account
you may run for someone else. The codebase carries portfolio-management controls (sector caps, a
drawdown ladder, a heat budget, Kelly-fraction sizing); they make the simulation realistic, and
they do not make this a portfolio-management system.

If you are not the only reader of this screen, stop and take counsel first.

## 8. No warranty, and the limits of liability

The software is provided "as is", without warranty of any kind, express or implied, including
fitness for a particular purpose. The authors are not liable for any loss or damage arising from its
use, including trading losses, data loss or service interruption. Nothing in this document limits
any liability that Philippine law does not allow to be limited.

## 9. Not legal, tax or accounting advice

This document explains how the project is built and what it does not do. It is not legal, tax or
accounting advice. If you intend to operate the desk for anyone other than yourself, take Philippine
counsel on SEC licensing (including the investment-adviser and broker/dealer regimes), PSE market-data
licensing and the Data Privacy Act before you do.
