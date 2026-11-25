import type {
  BacktestResult,
  CatalogStock,
  DeskMemorySummary,
  Horizon,
  KnowledgeDocument,
  KnowledgeIngestionResult,
  KnowledgeUploadInput,
  LatestSignalsResponse,
  OrderTicket,
  ProblemDetails,
  QuotationReportImportResult,
  SignalRun,
  SignalScorecard,
  TickerHistory,
  TickerResearch,
  TrainedModelSummary,
} from '../types/signals';
import type {
  ClosedTrade,
  OpenPosition,
  OpenTradeInput,
  PortfolioAccount,
  PortfolioSettingsUpdate,
  PortfolioSummary,
  TrimTradeResult,
} from '../types/portfolio';

const DEFAULT_API_BASE_URL = 'http://localhost:5236';

export const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL ?? DEFAULT_API_BASE_URL).replace(/\/+$/, '');

export class ApiError extends Error {
  readonly status: number;
  readonly title: string | null;

  constructor(message: string, status: number, title: string | null = null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
  }
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  signal?: AbortSignal;
  /** Multipart payload; the browser sets the boundary, so no Content-Type header. */
  body?: FormData;
  /** JSON payload serialized with camelCase names, matching the API's default policy. */
  json?: unknown;
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  let response: Response;

  const headers: Record<string, string> = { Accept: 'application/json' };
  if (options.json !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  try {
    response = await fetch(`${API_BASE_URL}${path}`, {
      method: options.method ?? 'GET',
      headers,
      body: options.json !== undefined ? JSON.stringify(options.json) : options.body,
      signal: options.signal,
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error;
    }

    throw new ApiError(
      `Cannot reach the signal desk API at ${API_BASE_URL}. Start the .NET API with "dotnet run --project backend/src/PseSignalDesk.WebApi".`,
      0,
      'API offline',
    );
  }

  if (!response.ok) {
    let problem: ProblemDetails | null = null;

    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      problem = null;
    }

    throw new ApiError(
      problem?.detail ?? problem?.title ?? `Request failed with status ${response.status}.`,
      response.status,
      problem?.title ?? null,
    );
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

function horizonQuery(horizon: Horizon): string {
  return `horizon=${encodeURIComponent(horizon)}`;
}

export const signalsApi = {
  latest(horizon: Horizon, signal?: AbortSignal): Promise<LatestSignalsResponse> {
    return request<LatestSignalsResponse>(`/api/signals/latest?${horizonQuery(horizon)}`, { signal });
  },

  byDate(date: string, horizon: Horizon, signal?: AbortSignal): Promise<LatestSignalsResponse> {
    return request<LatestSignalsResponse>(
      `/api/signals/by-date?date=${encodeURIComponent(date)}&${horizonQuery(horizon)}`,
      { signal },
    );
  },

  tickerHistory(ticker: string, horizon: Horizon, signal?: AbortSignal): Promise<TickerHistory> {
    return request<TickerHistory>(
      `/api/signals/ticker/${encodeURIComponent(ticker)}/history?${horizonQuery(horizon)}`,
      { signal },
    );
  },

  runNow(horizon: Horizon, date?: string | null): Promise<SignalRun> {
    const datePart = date ? `&date=${encodeURIComponent(date)}` : '';
    return request<SignalRun>(`/api/signals/run-now?${horizonQuery(horizon)}${datePart}`, { method: 'POST' });
  },

  /**
   * Forward-outcome scorecard: realized win rate, expectancy in R, profit factor, Brier
   * calibration and the verdict breakdown. Graded server-side from stored picks only.
   */
  scorecard(horizon: Horizon, signal?: AbortSignal): Promise<SignalScorecard> {
    return request<SignalScorecard>(`/api/signals/scorecard?${horizonQuery(horizon)}`, { signal });
  },

  /**
   * Uploads the official PSE Daily Quotation Report (PDF/CSV/TXT/TSV) for a stored session.
   * The server overlays per-ticker net foreign flow and suspension disclosures, recording the
   * provenance as pse-dqr-upload; an unmatched file answers 400 and a missing session 404.
   */
  importQuotationReport(file: File, date?: string | null): Promise<QuotationReportImportResult> {
    const form = new FormData();
    form.append('file', file);

    if (date) {
      form.append('date', date);
    }

    return request<QuotationReportImportResult>('/api/signals/quotation-report', {
      method: 'POST',
      body: form,
    });
  },

  /**
   * Causal walk-forward replay of the horizon's own rules over the real tape. One position
   * at a time, 1% risk, board lots and the full round-trip fee; 503 when the feed is empty.
   */
  backtest(horizon: Horizon, lookbackDays?: number, signal?: AbortSignal): Promise<BacktestResult> {
    const lookback = lookbackDays ? `&lookbackDays=${lookbackDays}` : '';
    return request<BacktestResult>(`/api/signals/backtest?${horizonQuery(horizon)}${lookback}`, {
      signal,
    });
  },

  /**
   * The report card on the horizon's self-trained money manager: held-out Brier versus the
   * neutral prior, the promotion status and the ranked factor weights. A rejected fit is
   * still returned — with `isActive` false — so the terminal can explain why the prior prices.
   */
  model(horizon: Horizon, signal?: AbortSignal): Promise<TrainedModelSummary[]> {
    return request<TrainedModelSummary[]>(`/api/signals/model?${horizonQuery(horizon)}`, { signal });
  },

  /** All four report cards in one call, for the trainer panel's overview. */
  models(signal?: AbortSignal): Promise<TrainedModelSummary[]> {
    return request<TrainedModelSummary[]>('/api/signals/model', { signal });
  },

  /**
   * Re-fits the horizon's weights from the real PSE tape. Answers 503 when the feed has no
   * history: the desk never trains on synthetic data.
   */
  train(horizon: Horizon, lookbackDays?: number): Promise<TrainedModelSummary> {
    const lookback = lookbackDays ? `&lookbackDays=${lookbackDays}` : '';
    return request<TrainedModelSummary>(`/api/signals/train?${horizonQuery(horizon)}${lookback}`, {
      method: 'POST',
    });
  },

  /** Fits all four books from a single fetch and returns their report cards. */
  trainAll(lookbackDays?: number): Promise<TrainedModelSummary[]> {
    const lookback = lookbackDays ? `?lookbackDays=${lookbackDays}` : '';
    return request<TrainedModelSummary[]>(`/api/signals/train-all${lookback}`, { method: 'POST' });
  },

  /**
   * The desk's own graded history: realized win rate, expectancy, calibration bias, the
   * sectors that paid, the promoted model's factors and the latest stop-outs.
   */
  deskMemory(horizon: Horizon, signal?: AbortSignal): Promise<DeskMemorySummary> {
    return request<DeskMemorySummary>(`/api/signals/desk-memory?${horizonQuery(horizon)}`, { signal });
  },
};

export const researchApi = {
  /** The tracked catalog (ticker, company name, sector), used for company-name search. */
  catalog(signal?: AbortSignal): Promise<CatalogStock[]> {
    return request<CatalogStock[]>('/api/research/catalog', { signal });
  },

  /**
   * On-demand deep dive for one ticker *or company name*: the server resolves an
   * unambiguous name ("Jollibee" → JFC) before pricing the plan from the live series.
   * Passing an `averageCost` asks the desk to answer "I already own it" against the
   * reader's own cost; `shares` is optional but turns the peso result into real money.
   */
  get(
    query: string,
    horizon: Horizon,
    signal?: AbortSignal,
    holding?: ResearchHolding,
  ): Promise<TickerResearch> {
    const params = new URLSearchParams({ horizon });
    if (holding?.averageCost !== undefined) {
      params.set('averageCost', String(holding.averageCost));
    }
    if (holding?.shares !== undefined) {
      params.set('shares', String(holding.shares));
    }

    return request<TickerResearch>(
      `/api/research/${encodeURIComponent(query.trim())}?${params.toString()}`,
      { signal },
    );
  },
};

/** The reader's own position, when they ask about a stock they already hold. */
export interface ResearchHolding {
  averageCost?: number;
  shares?: number;
}

export const portfolioApi = {
  summary(signal?: AbortSignal): Promise<PortfolioSummary> {
    return request<PortfolioSummary>('/api/portfolio', { signal });
  },

  updateSettings(update: PortfolioSettingsUpdate): Promise<PortfolioAccount> {
    return request<PortfolioAccount>('/api/portfolio/settings', { method: 'PUT', json: update });
  },

  /** Server-computed ticket for an arbitrary plan against the stored account equity. */
  orderTicket(
    plan: { ticker?: string; entry: number; stop: number; target: number; verdict?: string },
    signal?: AbortSignal,
  ): Promise<OrderTicket> {
    const query = new URLSearchParams({
      entry: String(plan.entry),
      stop: String(plan.stop),
      target: String(plan.target),
      verdict: plan.verdict ?? 'TRADE',
    });

    if (plan.ticker) {
      query.set('ticker', plan.ticker);
    }

    return request<OrderTicket>(`/api/portfolio/order-ticket?${query.toString()}`, { signal });
  },

  openTrade(input: OpenTradeInput): Promise<OpenPosition> {
    return request<OpenPosition>('/api/portfolio/trades', { method: 'POST', json: input });
  },

  updateStop(id: number, stopLoss: number): Promise<OpenPosition> {
    return request<OpenPosition>(`/api/portfolio/trades/${id}/stop`, {
      method: 'PATCH',
      json: { stopLoss },
    });
  },

  closeTrade(id: number, exitPrice: number, closedDate?: string, notes?: string): Promise<ClosedTrade> {
    return request<ClosedTrade>(`/api/portfolio/trades/${id}/close`, {
      method: 'POST',
      json: { exitPrice, closedDate: closedDate ?? null, notes: notes ?? null },
    });
  },

  /**
   * Banks part of an open position at a real fill. Both the slice and the shares left behind
   * must be whole board lots at their own prices; the server splits the fee-paid cost, credits
   * the cash and ratchets the remainder's stop to break-even.
   */
  trimTrade(
    id: number,
    shares: number,
    exitPrice: number,
    closedDate?: string,
    notes?: string,
  ): Promise<TrimTradeResult> {
    return request<TrimTradeResult>(`/api/portfolio/trades/${id}/trim`, {
      method: 'POST',
      json: { shares, exitPrice, closedDate: closedDate ?? null, notes: notes ?? null },
    });
  },
};

export const knowledgeApi = {
  list(signal?: AbortSignal): Promise<KnowledgeDocument[]> {
    return request<KnowledgeDocument[]>('/api/knowledge', { signal });
  },

  reindex(): Promise<KnowledgeIngestionResult> {
    return request<KnowledgeIngestionResult>('/api/knowledge/reindex', { method: 'POST' });
  },

  upload(input: KnowledgeUploadInput): Promise<KnowledgeDocument> {
    const form = new FormData();
    form.append('file', input.file);

    if (input.title && input.title.trim().length > 0) {
      form.append('title', input.title.trim());
    }

    if (input.horizons.length > 0) {
      form.append('horizons', input.horizons.join(','));
    }

    if (input.categories.length > 0) {
      form.append('categories', input.categories.join(','));
    }

    return request<KnowledgeDocument>('/api/knowledge/upload', { method: 'POST', body: form });
  },

  remove(id: number): Promise<void> {
    return request<void>(`/api/knowledge/${id}`, { method: 'DELETE' });
  },
};
