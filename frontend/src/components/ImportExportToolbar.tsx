'use client';

import { useRef, useState } from 'react';
import { Download, FileUp, Upload } from 'lucide-react';

export interface ImportResult {
  received: number;
  created: number;
  skipped: number;
  errors: string[];
  // Non-fatal notices — the row imported, but an optional reference (e.g. cost center,
  // branch) could not be resolved. Optional for back-compat with entities that omit it.
  warnings?: string[];
}

export interface ImportExportToolbarProps {
  entityName: string;
  onExport: () => Promise<void>;
  onDownloadTemplate: () => Promise<void>;
  onImport: (csvContent: string) => Promise<ImportResult>;
}

interface Toast {
  type: 'success' | 'error';
  message: string;
}

function downloadCsv(content: string, filename: string) {
  const blob = new Blob([content], { type: 'text/csv' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

export { downloadCsv };

type ServerErrorPayload = {
  message?: string;
  error?: string;
  title?: string;
  detail?: string;
  errors?: Record<string, string[]> | string[];
};

/**
 * Turn whatever the server returned on a failed import into a specific,
 * human-readable message. Handles a plain { message }, an ASP.NET
 * ValidationProblemDetails ({ errors: { field: string[] } }), and an import
 * result carrying a flat row-level errors[]. Falls back to the generic string.
 */
function extractImportError(data: unknown, entityName: string): string {
  if (typeof data === 'string' && data.trim()) return data.trim();
  const payload = (data ?? {}) as ServerErrorPayload;

  // Import result / 422 carrying a flat list of row errors.
  if (Array.isArray(payload.errors) && payload.errors.length > 0) {
    const shown = payload.errors.slice(0, 3).join('; ');
    return `Import failed — ${shown}${payload.errors.length > 3 ? ' …' : ''}`;
  }

  // ASP.NET model-validation problem details: { errors: { field: [msgs] } }.
  if (payload.errors && typeof payload.errors === 'object') {
    const flat = Object.entries(payload.errors as Record<string, string[]>)
      .map(([field, msgs]) => `${field}: ${(msgs ?? []).join(' ')}`);
    if (flat.length > 0) return `Import failed — ${flat.slice(0, 3).join(' · ')}`;
  }

  const message = payload.message ?? payload.detail ?? payload.title;
  if (message) return `Import failed — ${message}`;

  return `Failed to import ${entityName}.`;
}

export function ImportExportToolbar({
  entityName,
  onExport,
  onDownloadTemplate,
  onImport,
}: ImportExportToolbarProps) {
  const [exporting, setExporting] = useState(false);
  const [templating, setTemplating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [toast, setToast] = useState<Toast | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const showToast = (t: Toast) => {
    setToast(t);
    setTimeout(() => setToast(null), 5000);
  };

  const handleExport = async () => {
    setExporting(true);
    try {
      await onExport();
    } catch {
      showToast({ type: 'error', message: `Failed to export ${entityName}.` });
    } finally {
      setExporting(false);
    }
  };

  const handleTemplate = async () => {
    setTemplating(true);
    try {
      await onDownloadTemplate();
    } catch {
      showToast({ type: 'error', message: 'Failed to download template.' });
    } finally {
      setTemplating(false);
    }
  };

  const handleFileChange = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;

    setImporting(true);
    try {
      const csvContent = await file.text();
      const result = await onImport(csvContent);

      const warnings = result.warnings ?? [];
      const warningTail =
        warnings.length > 0
          ? ` ${warnings.length} warning${warnings.length > 1 ? 's' : ''}: ${warnings.slice(0, 2).join('; ')}${warnings.length > 2 ? ' …' : ''}`
          : '';
      if (result.errors.length > 0) {
        showToast({
          type: 'error',
          message: `Imported: Created ${result.created}, Skipped ${result.skipped}. Errors: ${result.errors.slice(0, 3).join('; ')}${result.errors.length > 3 ? ' …' : ''}`,
        });
      } else {
        showToast({
          type: 'success',
          message: `Import complete — Created ${result.created}, Skipped ${result.skipped} of ${result.received} rows.${warningTail}`,
        });
      }
    } catch (err) {
      const data = (err as { response?: { data?: unknown } })?.response?.data;
      showToast({ type: 'error', message: extractImportError(data, entityName) });
    } finally {
      setImporting(false);
    }
  };

  const btnBase =
    'inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-xs font-medium transition-colors disabled:opacity-50';
  const btnOutline =
    `${btnBase} border-slate-200 text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5`;

  return (
    <div className="relative flex items-center gap-2">
      <input
        ref={fileInputRef}
        type="file"
        accept=".csv,text/csv"
        className="hidden"
        onChange={handleFileChange}
      />

      <button
        type="button"
        className={btnOutline}
        disabled={exporting}
        onClick={handleExport}
        title={`Export ${entityName} as CSV`}
      >
        <Download className="h-3.5 w-3.5" />
        {exporting ? 'Exporting…' : 'Export'}
      </button>

      <button
        type="button"
        className={btnOutline}
        disabled={templating}
        onClick={handleTemplate}
        title="Download blank CSV import template"
      >
        <FileUp className="h-3.5 w-3.5" />
        {templating ? 'Downloading…' : 'Template'}
      </button>

      <button
        type="button"
        className={btnOutline}
        disabled={importing}
        onClick={() => fileInputRef.current?.click()}
        title={`Import ${entityName} from CSV`}
      >
        <Upload className="h-3.5 w-3.5" />
        {importing ? 'Importing…' : 'Import CSV'}
      </button>

      {/* Toast */}
      {toast && (
        <div
          className={`absolute right-0 top-10 z-50 max-w-sm rounded-lg border px-4 py-3 text-xs shadow-lg ${
            toast.type === 'success'
              ? 'border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-300'
              : 'border-rose-200 bg-rose-50 text-rose-800 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300'
          }`}
        >
          {toast.message}
        </div>
      )}
    </div>
  );
}
