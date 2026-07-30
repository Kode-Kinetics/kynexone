'use client';

import { useRef, useState } from 'react';
import { AlertTriangle, ArrowRight, CheckCircle2, Download, FileUp, Upload } from 'lucide-react';
import { Modal } from './Modal';

export interface ImportResult {
  received: number;
  created: number;
  skipped: number;
  errors: string[];
  // Non-fatal notices — the row imported, but an optional reference (e.g. cost center,
  // branch) could not be resolved. Optional for back-compat with entities that omit it.
  warnings?: string[];
  // ── Opt-in richer fields (the employee importer) for the persistent results view ──
  importBatchId?: string;
  hierarchyLinked?: number;
  payrollProfilesCreated?: number;
  /** Rows created but not activatable (imported as Draft, need completion before going Active). */
  createdIncomplete?: Array<{ employeeId: number; employeeCode: string; name: string; blockingCount: number }>;
}

/** One projected row from a pre-commit dry-run. */
export interface ImportPreviewRow {
  row: number;
  fullName: string;
  employeeCode?: string;
  status: string; // "WillCreate" | "Error"
  projectedStatus?: string; // "Active" | "Draft" | ""
  blocking?: string[];
  recommended?: string[];
  errors?: string[];
  warnings?: string[];
}

/** Dry-run projection returned before commit — persists nothing server-side. */
export interface ImportPreview {
  received: number;
  wouldCreate: number;
  wouldSkip: number;
  wouldCreateActive?: number;
  wouldCreateDraft?: number;
  rows: ImportPreviewRow[];
}

export interface ImportExportToolbarProps {
  entityName: string;
  onExport: () => Promise<void>;
  onDownloadTemplate: () => Promise<void>;
  onImport: (csvContent: string) => Promise<ImportResult>;
  /**
   * Opt-in: when provided, importing runs a pre-commit dry-run and a persistent results view
   * instead of the 5-second toast. Importers that omit this keep the original toast flow.
   */
  onPreview?: (csvContent: string) => Promise<ImportPreview>;
  /** Opt-in: deep-link from the results view to the "needs info" worklist. */
  onViewIncomplete?: () => void;
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

function csvCell(value: string | number | undefined): string {
  const s = String(value ?? '');
  return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

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

function LandingPill({ status }: { status?: string }) {
  const active = status?.toLowerCase() === 'active';
  const draft = status?.toLowerCase() === 'draft';
  const cls = active
    ? 'bg-emeraldZ/10 text-emerald-700 ring-emeraldZ/20 dark:text-emerald-300'
    : draft
      ? 'bg-amber-400/15 text-amber-700 ring-amber-400/25 dark:text-amber-300'
      : 'bg-slate-100 text-slate-500 ring-slate-200 dark:bg-white/10 dark:text-slate-300';
  return <span className={`inline-flex rounded-full px-2 py-0.5 text-[11px] font-semibold ring-1 ${cls}`}>{status || '—'}</span>;
}

export function ImportExportToolbar({
  entityName,
  onExport,
  onDownloadTemplate,
  onImport,
  onPreview,
  onViewIncomplete,
}: ImportExportToolbarProps) {
  const [exporting, setExporting] = useState(false);
  const [templating, setTemplating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const [toast, setToast] = useState<Toast | null>(null);
  // Dry-run + persistent-results state (only used when onPreview is provided).
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [pendingCsv, setPendingCsv] = useState<string | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
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

  const runToastImport = async (csvContent: string) => {
    setImporting(true);
    try {
      const res = await onImport(csvContent);
      const warnings = res.warnings ?? [];
      const warningTail =
        warnings.length > 0
          ? ` ${warnings.length} warning${warnings.length > 1 ? 's' : ''}: ${warnings.slice(0, 2).join('; ')}${warnings.length > 2 ? ' …' : ''}`
          : '';
      if (res.errors.length > 0) {
        showToast({
          type: 'error',
          message: `Imported: Created ${res.created}, Skipped ${res.skipped}. Errors: ${res.errors.slice(0, 3).join('; ')}${res.errors.length > 3 ? ' …' : ''}`,
        });
      } else {
        showToast({
          type: 'success',
          message: `Import complete — Created ${res.created}, Skipped ${res.skipped} of ${res.received} rows.${warningTail}`,
        });
      }
    } catch (err) {
      const data = (err as { response?: { data?: unknown } })?.response?.data;
      showToast({ type: 'error', message: extractImportError(data, entityName) });
    } finally {
      setImporting(false);
    }
  };

  const handleFileChange = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    const csvContent = await file.text();

    if (!onPreview) {
      await runToastImport(csvContent);
      return;
    }

    // Dry-run first, then confirm.
    setPreviewing(true);
    setPreview(null);
    setResult(null);
    setPendingCsv(csvContent);
    try {
      setPreview(await onPreview(csvContent));
    } catch (err) {
      setPendingCsv(null);
      const data = (err as { response?: { data?: unknown } })?.response?.data;
      showToast({ type: 'error', message: extractImportError(data, entityName) });
    } finally {
      setPreviewing(false);
    }
  };

  const confirmImport = async () => {
    if (!pendingCsv) return;
    setImporting(true);
    try {
      const res = await onImport(pendingCsv);
      setResult(res);
      setPreview(null);
      setPendingCsv(null);
    } catch (err) {
      const data = (err as { response?: { data?: unknown } })?.response?.data;
      showToast({ type: 'error', message: extractImportError(data, entityName) });
      setPreview(null);
      setPendingCsv(null);
    } finally {
      setImporting(false);
    }
  };

  const cancelPreview = () => {
    setPreview(null);
    setPendingCsv(null);
  };

  const downloadPreviewReport = () => {
    if (!preview) return;
    const header = ['Row', 'Name', 'Code', 'Outcome', 'Reasons'];
    const lines = preview.rows.map((r) => [
      r.row,
      r.fullName,
      r.employeeCode ?? '',
      r.status === 'Error' ? 'Rejected' : r.projectedStatus || 'Draft',
      [...(r.errors ?? []), ...(r.blocking ?? [])].join('; '),
    ].map(csvCell).join(','));
    downloadCsv([header.join(','), ...lines].join('\n'), 'import-preview.csv');
  };

  const downloadResultReport = () => {
    if (!result) return;
    const rows: string[] = [['Type', 'Detail'].join(',')];
    (result.errors ?? []).forEach((m) => rows.push([csvCell('Error'), csvCell(m)].join(',')));
    (result.warnings ?? []).forEach((m) => rows.push([csvCell('Warning'), csvCell(m)].join(',')));
    (result.createdIncomplete ?? []).forEach((c) =>
      rows.push([csvCell('Created (incomplete)'), csvCell(`${c.name} (${c.employeeCode}) — ${c.blockingCount} to activate`)].join(',')),
    );
    downloadCsv(rows.join('\n'), 'import-results.csv');
  };

  const btnBase =
    'inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-xs font-medium transition-colors disabled:opacity-50';
  const btnOutline =
    `${btnBase} border-slate-200 text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5`;

  const previewOpen = pendingCsv !== null || previewing;
  const incompleteCount = result?.createdIncomplete?.length ?? 0;

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
        disabled={importing || previewing}
        onClick={() => fileInputRef.current?.click()}
        title={`Import ${entityName} from CSV`}
      >
        <Upload className="h-3.5 w-3.5" />
        {importing ? 'Importing…' : previewing ? 'Checking…' : 'Import CSV'}
      </button>

      {/* Toast (kept for the toast-flow importers and for import errors) */}
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

      {/* Pre-commit dry-run preview */}
      {onPreview && (
        <Modal
          isOpen={previewOpen}
          title={`Review import — ${entityName}`}
          size="xl"
          onClose={cancelPreview}
          footer={
            <>
              <button type="button" className="btn-secondary" onClick={cancelPreview} disabled={importing}>Cancel</button>
              <button type="button" className="btn-secondary" onClick={downloadPreviewReport} disabled={!preview}>
                <Download className="h-3.5 w-3.5" />
                Download preview
              </button>
              <button type="button" className="btn-primary disabled:opacity-60" onClick={confirmImport} disabled={importing || !preview}>
                {importing ? 'Importing…' : `Confirm import`}
              </button>
            </>
          }
        >
          {previewing || !preview ? (
            <p className="p-4 text-sm text-slate-500">Checking rows…</p>
          ) : (
            <div className="space-y-3">
              <div className="flex flex-wrap gap-2 text-xs">
                <span className="rounded-md bg-slate-100 px-2 py-1 font-semibold text-slate-600 dark:bg-white/10 dark:text-slate-300">{preview.received} rows</span>
                <span className="rounded-md bg-emeraldZ/10 px-2 py-1 font-semibold text-emerald-700 ring-1 ring-emeraldZ/20 dark:text-emerald-300">{preview.wouldCreateActive ?? 0} Active</span>
                <span className="rounded-md bg-amber-400/15 px-2 py-1 font-semibold text-amber-700 ring-1 ring-amber-400/25 dark:text-amber-300">{preview.wouldCreateDraft ?? 0} Draft</span>
                {preview.wouldSkip > 0 && <span className="rounded-md bg-rose-500/10 px-2 py-1 font-semibold text-rose-700 ring-1 ring-rose-500/20 dark:text-rose-300">{preview.wouldSkip} rejected</span>}
              </div>
              <p className="text-xs text-slate-500 dark:text-slate-400">
                Nothing is saved yet. Rows with missing required details are created as <strong>Draft</strong> — they can be completed and activated afterwards.
              </p>
              <div className="max-h-[50vh] overflow-auto rounded-lg border border-slate-200 dark:border-white/10">
                <table className="w-full min-w-[560px] text-sm">
                  <thead className="sticky top-0 bg-slate-50 dark:bg-white/[0.04]">
                    <tr className="text-left text-[11px] font-bold uppercase text-slate-400">
                      <th className="px-3 py-2">#</th>
                      <th className="px-3 py-2">Name</th>
                      <th className="px-3 py-2">Lands as</th>
                      <th className="px-3 py-2">Why</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                    {preview.rows.map((r) => (
                      <tr key={r.row}>
                        <td className="px-3 py-2 text-slate-400">{r.row}</td>
                        <td className="px-3 py-2 text-slate-700 dark:text-slate-200">{r.fullName || <span className="text-slate-400">(no name)</span>}</td>
                        <td className="px-3 py-2">{r.status === 'Error' ? <span className="text-xs font-semibold text-rose-600">Rejected</span> : <LandingPill status={r.projectedStatus} />}</td>
                        <td className="px-3 py-2 text-xs text-slate-500 dark:text-slate-400">
                          {[...(r.errors ?? []), ...(r.blocking ?? [])].join(', ') || (r.status === 'Error' ? '' : 'Complete')}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </Modal>
      )}

      {/* Persistent results view — stays until dismissed (replaces the 5s toast) */}
      {onPreview && result && (
        <Modal
          isOpen
          title={`Import results — ${entityName}`}
          size="lg"
          onClose={() => setResult(null)}
          footer={
            <>
              <button type="button" className="btn-secondary" onClick={downloadResultReport}>
                <Download className="h-3.5 w-3.5" />
                Download report
              </button>
              {onViewIncomplete && incompleteCount > 0 && (
                <button type="button" className="btn-primary" onClick={() => { onViewIncomplete(); setResult(null); }}>
                  View the {incompleteCount} incomplete
                  <ArrowRight className="h-3.5 w-3.5" />
                </button>
              )}
              <button type="button" className="btn-secondary" onClick={() => setResult(null)}>Done</button>
            </>
          }
        >
          <div className="space-y-3">
            <div className="flex flex-wrap gap-2 text-xs">
              <span className="inline-flex items-center gap-1 rounded-md bg-emeraldZ/10 px-2 py-1 font-semibold text-emerald-700 ring-1 ring-emeraldZ/20 dark:text-emerald-300">
                <CheckCircle2 className="h-3.5 w-3.5" /> {result.created} created
              </span>
              {result.skipped > 0 && <span className="rounded-md bg-rose-500/10 px-2 py-1 font-semibold text-rose-700 ring-1 ring-rose-500/20 dark:text-rose-300">{result.skipped} skipped</span>}
              {incompleteCount > 0 && <span className="rounded-md bg-amber-400/15 px-2 py-1 font-semibold text-amber-700 ring-1 ring-amber-400/25 dark:text-amber-300">{incompleteCount} need info</span>}
            </div>

            {result.errors.length > 0 && (
              <ResultTier tone="rose" icon={<AlertTriangle className="h-3.5 w-3.5" />} title={`Rejected · ${result.errors.length}`} items={result.errors} />
            )}
            {(result.warnings?.length ?? 0) > 0 && (
              <ResultTier tone="amber" icon={<AlertTriangle className="h-3.5 w-3.5" />} title={`Warnings · ${result.warnings!.length}`} items={result.warnings!} />
            )}
            {incompleteCount > 0 && (
              <div>
                <p className="mb-1.5 flex items-center gap-1.5 text-xs font-bold uppercase tracking-wide text-amber-600 dark:text-amber-400">
                  <AlertTriangle className="h-3.5 w-3.5" /> Created — needs info · {incompleteCount}
                </p>
                <ul className="max-h-48 space-y-1 overflow-auto">
                  {result.createdIncomplete!.map((c) => (
                    <li key={c.employeeId} className="flex items-center justify-between gap-2 rounded-lg border border-slate-200 px-2.5 py-1.5 text-sm dark:border-white/10">
                      <span className="truncate text-slate-700 dark:text-slate-200">{c.name} <span className="text-xs text-slate-400">{c.employeeCode}</span></span>
                      <span className="shrink-0 text-[11px] font-semibold text-amber-700 dark:text-amber-400">{c.blockingCount} to activate</span>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {result.errors.length === 0 && (result.warnings?.length ?? 0) === 0 && incompleteCount === 0 && (
              <p className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-300">
                All {result.created} rows imported cleanly.
              </p>
            )}
          </div>
        </Modal>
      )}
    </div>
  );
}

function ResultTier({ tone, icon, title, items }: { tone: 'rose' | 'amber'; icon: React.ReactNode; title: string; items: string[] }) {
  const head = tone === 'rose' ? 'text-rose-600 dark:text-rose-400' : 'text-amber-600 dark:text-amber-400';
  return (
    <div>
      <p className={`mb-1.5 flex items-center gap-1.5 text-xs font-bold uppercase tracking-wide ${head}`}>{icon}{title}</p>
      <ul className="max-h-40 space-y-1 overflow-auto rounded-lg border border-slate-200 p-2 dark:border-white/10">
        {items.map((m, i) => (
          <li key={i} className="text-xs text-slate-600 dark:text-slate-300">{m}</li>
        ))}
      </ul>
    </div>
  );
}
