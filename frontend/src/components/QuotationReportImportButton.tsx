import { useRef, useState } from 'react';
import { signalsApi } from '../api/signalsApi';
import type { QuotationReportImportResult } from '../types/signals';
import { Button } from './ui/Button';

interface QuotationReportImportButtonProps {
  /** Session the report belongs to. When null the server reads the date from the file itself. */
  date: string | null;
  disabled?: boolean;
  onImported: (result: QuotationReportImportResult) => void;
  onError: (caught: unknown) => void;
}

/** The official PSE Daily Quotation Report is the only public source of net foreign flow. */
const ACCEPTED_TYPES = '.pdf,.csv,.txt,.tsv';

/**
 * Uploads the official PSE Daily Quotation Report for the visible session. The server keeps the
 * provenance honest (pse-dqr-upload) and leaves every ticker the file does not mention at null,
 * so this button can never invent a foreign flow.
 */
export function QuotationReportImportButton({
  date,
  disabled,
  onImported,
  onError,
}: QuotationReportImportButtonProps) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);

  const handleFile = async (file: File | undefined) => {
    if (!file) {
      return;
    }

    setBusy(true);

    try {
      onImported(await signalsApi.importQuotationReport(file, date));
    } catch (caught) {
      onError(caught);
    } finally {
      setBusy(false);

      // Reset so re-uploading the same file (a corrected export, for example) still fires.
      if (inputRef.current) {
        inputRef.current.value = '';
      }
    }
  };

  return (
    <>
      <input
        ref={inputRef}
        type="file"
        accept={ACCEPTED_TYPES}
        className="hidden"
        aria-label="PSE Daily Quotation Report file"
        onChange={(event) => {
          void handleFile(event.target.files?.[0]);
        }}
      />
      <Button
        variant="secondary"
        busy={busy}
        disabled={disabled || busy}
        onClick={() => inputRef.current?.click()}
      >
        Import DQR
      </Button>
    </>
  );
}
