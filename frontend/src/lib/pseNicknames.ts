import type { CatalogStock } from '../types/signals';

/**
 * The names Filipinos actually use for the stocks they own. The desk's catalog stores the
 * legal company name ("Manila Electric Company"), but a reader searches "Meralco" and expects
 * MER. This is presentation-only metadata: the ticker and every number still come from the
 * server.
 */
const NICKNAMES: Record<string, string> = {
  AC: 'Ayala Corp',
  ACEN: 'ACEN',
  AEV: 'Aboitiz Equity',
  AGI: 'Alliance Global',
  ALLHC: 'AyalaLand Logistics',
  ALI: 'Ayala Land',
  AP: 'Aboitiz Power',
  AREIT: 'AREIT',
  BDO: 'BDO',
  BLOOM: 'Bloomberry',
  BPI: 'BPI',
  CBC: 'China Bank',
  CNPF: 'Century Pacific',
  CNVRG: 'Converge',
  CREIT: 'Citicore REIT',
  DMC: 'DMCI',
  EMI: 'Emperador',
  FB: 'SM Food & Bev',
  GLO: 'Globe',
  GSMI: 'Ginebra San Miguel',
  GTCAP: 'GT Capital',
  ICT: 'ICTSI',
  JFC: 'Jollibee',
  JGS: 'JG Summit',
  LTG: 'LT Group',
  MBT: 'Metrobank',
  MEG: 'Megaworld',
  MER: 'Meralco',
  MONDE: 'Monde Nissin',
  MREIT: 'MREIT',
  PGOLD: 'Puregold',
  PLUS: 'DigiPlus',
  RCB: 'RCBC',
  RCR: 'RL Commercial REIT',
  RLC: 'Robinsons Land',
  RRHI: 'Robinsons Retail',
  SCC: 'Semirara',
  SM: 'SM Investments',
  SMPH: 'SM Prime',
  TEL: 'PLDT',
  URC: 'Universal Robina',
  VREIT: 'VistaREIT',
  WLCON: 'Wilcon Depot',
};

/** The everyday name for a ticker, or null when the desk has no nickname on file. */
export function nicknameFor(ticker: string | null | undefined): string | null {
  if (!ticker) {
    return null;
  }

  return NICKNAMES[ticker.trim().toUpperCase()] ?? null;
}

/**
 * Best human label for a stock: the familiar nickname first, then the legal name, then the
 * ticker. Cards lead with this so the reader recognises the company before the symbol.
 */
export function companyLabel(
  ticker: string | null | undefined,
  companyName?: string | null,
): string {
  const nickname = nicknameFor(ticker);
  if (nickname) {
    return nickname;
  }

  const name = (companyName ?? '').trim();
  return name.length > 0 ? name : (ticker ?? '').trim().toUpperCase();
}

export interface PopularPseStock {
  ticker: string;
  label: string;
}

/** The one-tap starting points shown under the research box. */
export const POPULAR_PSE_STOCKS: PopularPseStock[] = [
  { ticker: 'JFC', label: 'Jollibee' },
  { ticker: 'MER', label: 'Meralco' },
  { ticker: 'TEL', label: 'PLDT' },
  { ticker: 'BDO', label: 'BDO' },
  { ticker: 'SMPH', label: 'SM Prime' },
  { ticker: 'ALI', label: 'Ayala Land' },
  { ticker: 'ICT', label: 'ICTSI' },
  { ticker: 'URC', label: 'Universal Robina' },
];

function normalize(value: string): string {
  return value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();
}

function matchScore(stock: CatalogStock, query: string): number {
  const ticker = stock.ticker.toLowerCase();
  const company = normalize(stock.companyName);
  const nickname = normalize(nicknameFor(stock.ticker) ?? '');

  if (ticker === query) {
    return 0;
  }

  if (ticker.startsWith(query)) {
    return 1;
  }

  if (nickname.startsWith(query)) {
    return 2;
  }

  if (company.startsWith(query)) {
    return 3;
  }

  if (company.includes(query)) {
    return 4;
  }

  if (nickname.includes(query)) {
    return 5;
  }

  return ticker.includes(query) ? 6 : Number.POSITIVE_INFINITY;
}

/**
 * Company-first search over the server's catalog: "jollibee", "JFC" and "jollibee foods" all
 * find the same name. Purely a local filter over metadata the API already returned.
 */
export function searchCatalog(
  stocks: readonly CatalogStock[],
  query: string,
  limit = 8,
): CatalogStock[] {
  const normalized = normalize(query);
  if (normalized.length === 0) {
    return [];
  }

  return stocks
    .map((stock) => ({ stock, score: matchScore(stock, normalized) }))
    .filter((entry) => Number.isFinite(entry.score))
    .sort((left, right) =>
      left.score === right.score
        ? left.stock.ticker.localeCompare(right.stock.ticker)
        : left.score - right.score,
    )
    .slice(0, limit)
    .map((entry) => entry.stock);
}

/** The catalog row for a ticker or familiar name, if the server tracks it. */
export function findCatalogStock(
  stocks: readonly CatalogStock[],
  query: string,
): CatalogStock | null {
  return searchCatalog(stocks, query, 1)[0] ?? null;
}
