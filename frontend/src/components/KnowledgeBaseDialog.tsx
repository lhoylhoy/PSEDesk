import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ApiError, knowledgeApi } from '../api/signalsApi';
import type { Horizon, KnowledgeDocument, KnowledgeIngestionResult } from '../types/signals';
import { cn } from '../lib/cn';
import { formatInteger, formatTimestamp } from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import {
  fetchQuery,
  getSnapshot,
  queryKeys,
  queryStaleTime,
} from '../lib/queryCache';
import { Button } from './ui/Button';
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogStickyHeader,
  DialogTitle,
} from './ui/Dialog';

const HORIZONS: Horizon[] = ['DAY', 'WEEK', 'MONTH', 'YEAR'];

interface KnowledgeBaseDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

function describeError(caught: unknown): string {
  if (caught instanceof ApiError) {
    return caught.title ? `${caught.title}: ${caught.message}` : caught.message;
  }

  return caught instanceof Error
    ? caught.message
    : 'Something went wrong while talking to the knowledge base.';
}

export function KnowledgeBaseDialog({ open, onOpenChange }: KnowledgeBaseDialogProps) {
  const [documents, setDocuments] = useState<KnowledgeDocument[]>([]);
  const [status, setStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<'reindex' | 'upload' | 'delete' | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [title, setTitle] = useState('');
  const [categories, setCategories] = useState('');
  const [targets, setTargets] = useState<Horizon[]>(['WEEK', 'MONTH']);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  const load = useCallback(async (signal?: AbortSignal, force = false) => {
    const cached = getSnapshot<KnowledgeDocument[]>(queryKeys.knowledgeList);

    // The sheet lists a slow-moving catalog; a fresh copy paints immediately and the
    // revalidation below only runs once its window has expired or a mutation forced it.
    if (cached && !force) {
      setDocuments(cached);
      setStatus('ready');
    } else if (!cached) {
      setStatus('loading');
    }

    setError(null);

    try {
      const result = await fetchQuery({
        key: queryKeys.knowledgeList,
        staleTimeMs: queryStaleTime.knowledge,
        force,
        signal,
        fetcher: (next) => knowledgeApi.list(next),
      });
      setDocuments(result);
      setStatus('ready');
    } catch (caught) {
      if (signal?.aborted) {
        return;
      }

      setError(describeError(caught));
      // A failed revalidation keeps the documents already on screen readable.
      setStatus(cached ? 'ready' : 'error');
    }
  }, []);

  useEffect(() => {
    if (!open) {
      return;
    }

    const controller = new AbortController();
    // StrictMode double-mounts in development; defer so the cleanup clears the timer
    // instead of aborting an in-flight request.
    const timer = window.setTimeout(() => void load(controller.signal), 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [open, load]);

  const totals = useMemo(
    () =>
      documents.reduce(
        (acc, document) => ({
          chunks: acc.chunks + document.chunkCount,
          embedded: acc.embedded + document.embeddedChunkCount,
        }),
        { chunks: 0, embedded: 0 },
      ),
    [documents],
  );


  const handleReindex = useCallback(async () => {
    setBusy('reindex');
    setNotice(null);
    setError(null);

    try {
      const result: KnowledgeIngestionResult = await knowledgeApi.reindex();
      setNotice(
        `${result.documentsIndexed} document${result.documentsIndexed === 1 ? '' : 's'} indexed · ` +
          `${result.documentsUpdated} updated · ${result.documentsSkipped} unchanged · ` +
          `${formatInteger(result.chunksIndexed)} chunks (${formatInteger(result.chunksEmbedded)} embedded with ${result.embeddingModel}).`,
      );
      await load(undefined, true);
    } catch (caught) {
      setError(describeError(caught));
    } finally {
      setBusy(null);
    }
  }, [load]);

  const handleUpload = useCallback(async () => {
    if (!file) {
      setError('Pick a .md, .txt or .pdf trading book first.');
      return;
    }

    setBusy('upload');
    setNotice(null);
    setError(null);

    try {
      const uploaded = await knowledgeApi.upload({
        file,
        title,
        horizons: targets,
        categories: categories
          .split(',')
          .map((value) => value.trim())
          .filter((value) => value.length > 0),
      });

      setNotice(
        `${uploaded.title} indexed into ${uploaded.chunkCount} chunk${uploaded.chunkCount === 1 ? '' : 's'} ` +
          `(${formatInteger(uploaded.embeddedChunkCount)} embedded).`,
      );
      setFile(null);
      setTitle('');
      setCategories('');

      if (fileInputRef.current) {
        fileInputRef.current.value = '';
      }

      await load(undefined, true);
    } catch (caught) {
      setError(describeError(caught));
    } finally {
      setBusy(null);
    }
  }, [categories, file, load, targets, title]);

  const handleDelete = useCallback(
    async (document: KnowledgeDocument) => {
      setBusy('delete');
      setNotice(null);
      setError(null);

      try {
        await knowledgeApi.remove(document.id);
        setNotice(`${document.title} removed from the knowledge base.`);
        await load(undefined, true);
      } catch (caught) {
        setError(describeError(caught));
      } finally {
        setBusy(null);
      }
    },
    [load],
  );

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="lg">
        <DialogStickyHeader>
          <div className="min-w-0">
            <DialogTitle className="text-base font-semibold text-white">Knowledge base</DialogTitle>
            <DialogDescription className="mt-1 max-w-2xl text-xs leading-relaxed text-slate-400">
              The desk answers from these documents only. Built-in playbooks ship with the repo; uploads are
              scoped to the horizons you tick so a trading book cannot outrank the horizon playbook.
            </DialogDescription>
          </div>
          <div className="flex items-center gap-2">
            <Button
              variant="secondary"
              busy={busy === 'reindex'}
              onClick={() => void handleReindex()}
              disabled={busy !== null}
            >
              Re-index
            </Button>
            <DialogClose asChild>
              <Button variant="ghost">Close</Button>
            </DialogClose>
          </div>
        </DialogStickyHeader>

        {notice ? (
          <p className="mt-3 rounded-xl border border-mint-400/30 bg-mint-400/5 px-3 py-2 text-[11px] text-mint-400">
            {notice}
          </p>
        ) : null}

        {error ? (
          <p className="mt-3 rounded-xl border border-rose-400/40 bg-rose-400/5 px-3 py-2 text-[11px] text-rose-400">
            {error}
          </p>
        ) : null}

        <p className="mt-3 text-[10px] uppercase tracking-[0.18em] text-slate-600">
          {documents.length} document{documents.length === 1 ? '' : 's'} · {formatInteger(totals.chunks)} chunks ·{' '}
          {formatInteger(totals.embedded)} embedded
        </p>
        <section className="mt-4 rounded-xl border border-line/70 bg-ink-850/60 p-3">
          <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">Upload a trading book</h3>
          <div className="mt-2 grid gap-2.5 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-[10px] uppercase tracking-[0.18em] text-slate-500">
              File (.md, .txt or .pdf)
              <input
                ref={fileInputRef}
                type="file"
                accept=".md,.markdown,.txt,.pdf"
                onChange={(event) => {
                  setFile(event.target.files?.[0] ?? null);
                  setNotice(null);
                }}
                className="rounded-lg border border-line/80 bg-ink-900/80 px-2.5 py-1.5 text-[11px] text-slate-300 outline-none file:mr-2 file:rounded file:border-0 file:bg-ink-700 file:px-2 file:py-1 file:text-[10px] file:uppercase file:tracking-wider file:text-slate-300 focus:border-mint-400/50"
              />
            </label>
            <label className="flex flex-col gap-1 text-[10px] uppercase tracking-[0.18em] text-slate-500">
              Title (optional)
              <input
                type="text"
                value={title}
                onChange={(event) => setTitle(event.target.value)}
                placeholder="Defaults to the file name"
                className="rounded-lg border border-line/80 bg-ink-900/80 px-2.5 py-1.5 text-[11px] normal-case tracking-normal text-slate-200 outline-none placeholder:text-slate-600 focus:border-mint-400/50"
              />
            </label>
            <label className="flex flex-col gap-1 text-[10px] uppercase tracking-[0.18em] text-slate-500">
              Categories (comma separated)
              <input
                type="text"
                value={categories}
                onChange={(event) => setCategories(event.target.value)}
                placeholder="risk, sizing, ph-macro"
                className="rounded-lg border border-line/80 bg-ink-900/80 px-2.5 py-1.5 text-[11px] normal-case tracking-normal text-slate-200 outline-none placeholder:text-slate-600 focus:border-mint-400/50"
              />
            </label>
            <div className="flex flex-col gap-1 text-[10px] uppercase tracking-[0.18em] text-slate-500">
              Scoped horizons
              <div className="flex flex-wrap gap-1.5">
                {HORIZONS.map((option) => {
                  const selected = targets.includes(option);

                  return (
                    <button
                      key={option}
                      type="button"
                      aria-pressed={selected}
                      onClick={() =>
                        setTargets((current) =>
                          current.includes(option)
                            ? current.filter((value) => value !== option)
                            : [...current, option],
                        )
                      }
                      className={cn(
                        'rounded-lg border px-2 py-1 text-[10px] font-semibold uppercase tracking-[0.14em] transition',
                        selected
                          ? 'border-mint-400/40 bg-mint-400/10 text-mint-400'
                          : 'border-line/80 bg-ink-900/80 text-slate-500 hover:text-slate-300',
                      )}
                    >
                      {horizonMeta[option].label}
                    </button>
                  );
                })}
              </div>
            </div>
          </div>
          <div className="mt-3 flex flex-wrap items-center justify-between gap-2">
            <p className="max-w-lg text-[10px] leading-relaxed text-slate-600">
              Markdown keeps its structure; PDFs are text-extracted and chunked on headings. Leave the horizons
              empty to let the book apply to every book.
            </p>
            <Button
              variant="primary"
              busy={busy === 'upload'}
              onClick={() => void handleUpload()}
              disabled={busy !== null || !file}
              className="w-full sm:w-auto"
            >
              Upload and index
            </Button>
          </div>
        </section>
        <section className="mt-4">
          <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">Indexed documents</h3>
          {status === 'loading' && documents.length === 0 ? (
            <p className="mt-2 text-xs text-slate-500">Loading the knowledge index…</p>
          ) : null}
          {status === 'ready' && documents.length === 0 ? (
            <p className="mt-2 text-xs text-slate-500">
              Nothing indexed yet. Re-index to import the shipped playbooks.
            </p>
          ) : null}
          <ul className="mt-2 space-y-1.5">
            {documents.map((document) => (
              <li key={document.id} className="rounded-xl border border-line/60 bg-ink-850/60 px-3 py-2">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-xs font-semibold text-slate-100">{document.title}</span>
                    <span className="rounded border border-line/70 px-1 py-0.5 text-[9px] uppercase tracking-wider text-slate-500">
                      {document.source === 'builtin' ? 'built-in' : 'upload'}
                    </span>
                    {document.isMandatory ? (
                      <span className="rounded border border-mint-400/30 bg-mint-400/10 px-1 py-0.5 text-[9px] uppercase tracking-wider text-mint-400">
                        mandatory
                      </span>
                    ) : null}
                  </div>
                  {document.source === 'builtin' ? null : (
                    <Button
                      variant="ghost"
                      className="h-7 px-2 text-[10px]"
                      onClick={() => void handleDelete(document)}
                      disabled={busy !== null}
                    >
                      Delete
                    </Button>
                  )}
                </div>
                <p className="mt-1 font-mono text-[10px] text-slate-500">
                  {document.slug} · {document.sourcePath}
                </p>
                <div className="mt-1 flex flex-wrap items-center gap-1">
                  {document.horizons.map((value) => (
                    <span
                      key={`${document.id}-h-${value}`}
                      className="rounded border border-line/70 px-1 py-0.5 text-[9px] uppercase tracking-wider text-slate-400"
                    >
                      {horizonMeta[value as Horizon]?.label ?? value}
                    </span>
                  ))}
                  {document.categories.map((value) => (
                    <span
                      key={`${document.id}-c-${value}`}
                      className="rounded border border-line/50 px-1 py-0.5 text-[9px] tracking-wider text-slate-500"
                    >
                      {value}
                    </span>
                  ))}
                </div>
                <p className="mt-1 text-[10px] text-slate-600">
                  {formatInteger(document.chunkCount)} chunks · {formatInteger(document.embeddedChunkCount)}{' '}
                  embedded · updated {formatTimestamp(document.updatedAtUtc)}
                </p>
              </li>
            ))}
          </ul>
        </section>
      </DialogContent>


    </Dialog>
  );
}

