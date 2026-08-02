'use client';

import { useRef, useState } from 'react';
import { AlertTriangle, ArrowRight, CheckCircle2, Download, FileUp, Upload, Users } from 'lucide-react';
import { Modal } from './Modal';

/**
 * One typed org-skeleton / linkage gap on an imported (Draft) row. `type` is the deep-link key
 * (e.g. "link:manager", "org:department", "pay:salaryHeld"); each gap is a People-list filter.
 */
export interface ImportGap {
  type: string;
  category?: string;
  detail: string;
}

/** Filter payload the results view hands to the "needs info" worklist deep-link. */
export interface ImportResultFilter {
  readiness?: string;
  gapType?: string;
  importBatchId?: string;
}

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
  createdIncomplete?: Array<{ employeeId: number; employeeCode: string; name: string; blockingCount: number; gaps?: ImportGap[] }>;
  // ── Countable org-skeleton summary (accept-never-block). All optional; the summary line
  //    degrades to created/skipped when the server omits them. ──
  imported?: number;
  receivedTotal?: number;
  skippedNoName?: number;
  skippedDupCode?: number;
  incompleteDraft?: number;
  managersUnresolved?: number;
  supervisorsUnresolved?: number;
  newDepartments?: number;
  newBranches?: number;
  salariesHeld?: number;
  salariesForReview?: number;
  /** Rows imported but flagged as a possible existing person (dup:strong / dup:possible). */
  possibleDuplicates?: number;
}

/** Display metadata + singular/plural label for each typed gap deep-link. */
const GAP_META: Record<string, { one: string; many: string }> = {
  'link:manager': { one: 'manager unresolved', many: 'managers unresolved' },
  'link:supervisor': { one: 'supervisor unresolved', many: 'supervisors unresolved' },
  'pay:salaryHeld': { one: 'salary held', many: 'salaries held' },
  'pay:salaryReview': { one: 'salary for review', many: 'salaries for review' },
  'org:company': { one: 'company unassigned', many: 'companies unassigned' },
  'org:department': { one: 'new department', many: 'new departments' },
  'org:branch': { one: 'new branch', many: 'new branches' },
  'org:grade': { one: 'grade unresolved', many: 'grades unresolved' },
  'org:position': { one: 'position unresolved', many: 'positions unresolved' },
  'org:establishment': { one: 'over budget', many: 'over budget' },
  // Duplicate-person flags (accept-never-block): the row imported, but may be the same human as an
  // existing record. Resolve each from the People list (confirm distinct / merge) — never auto-merged.
  'dup:strong': { one: 'possible duplicate (ID match)', many: 'possible duplicates (ID match)' },
  'dup:possible': { one: 'possible duplicate (name + DOB)', many: 'possible duplicates (name + DOB)' },
  // Work-email derivation flags (accept-never-block): the row imported, the work email just needs a
  // human touch — a provided address whose domain differs from the company's, or none derivable.
  'email:domain-mismatch': { one: 'work email domain mismatch', many: 'work email domain mismatches' },
  'email:needs-info': { one: 'work email needs manual entry', many: 'work emails need manual entry' },
  'email:duplicate': { one: 'work email already in use', many: 'work emails already in use' },
};

/** A typed gap is a possible-duplicate flag needing a human decision (distinct/merge). */
function isDuplicateGap(type: string): boolean {
  return type === 'dup:strong' || type === 'dup:possible';
}

function gapLabel(type: string, count: number): string {
  const meta = GAP_META[type];
  if (meta) return count === 1 ? meta.one : meta.many;
  return type; // unknown type — show the raw key rather than dropping it
}

/**
 * Roll every created-incomplete row's typed gaps up into per-type occurrence counts and the
 * distinct raw values seen (used to derive "N new departments" when the server omits the field).
 */
function aggregateGaps(result: ImportResult): { counts: Map<string, number>; distinct: Map<string, Set<string>> } {
  const counts = new Map<string, number>();
  const distinct = new Map<string, Set<string>>();
  for (const row of result.createdIncomplete ?? []) {
    for (const gap of row.gaps ?? []) {
      counts.set(gap.type, (counts.get(gap.type) ?? 0) + 1);
      if (!distinct.has(gap.type)) distinct.set(gap.type, new Set());
      distinct.get(gap.type)!.add(gap.detail);
    }
  }
  return { counts, distinct };
}

/**
 * Build the countable one-line summary, e.g.
 * "Imported 214/220 · 6 skipped (2 no-name, 4 dup-code) · 41 incomplete(Draft) · 12 managers
 *  unresolved · 3 new departments · 2 new branches · 5 salaries held".
 * Prefers the server's counters, falls back to created/skipped + gap aggregation so it stays
 * accurate before the richer server fields land.
 */
function buildSummaryLine(result: ImportResult, agg: { counts: Map<string, number>; distinct: Map<string, Set<string>> }): string {
  const imported = result.imported ?? result.created;
  const receivedTotal = result.receivedTotal ?? result.received;
  const incompleteDraft = result.incompleteDraft ?? (result.createdIncomplete?.length ?? 0);
  const managersUnresolved = result.managersUnresolved ?? (agg.counts.get('link:manager') ?? 0);
  const newDepartments = result.newDepartments ?? (agg.distinct.get('org:department')?.size ?? 0);
  const newBranches = result.newBranches ?? (agg.distinct.get('org:branch')?.size ?? 0);
  const salariesHeld = result.salariesHeld ?? (agg.counts.get('pay:salaryHeld') ?? 0);
  const possibleDuplicates = result.possibleDuplicates
    ?? ((agg.counts.get('dup:strong') ?? 0) + (agg.counts.get('dup:possible') ?? 0));

  const parts: string[] = [`Imported ${imported}/${receivedTotal}`];
  if (result.skipped > 0) {
    const noName = result.skippedNoName;
    const dup = result.skippedDupCode;
    const breakdown = noName !== undefined || dup !== undefined
      ? ` (${noName ?? 0} no-name, ${dup ?? 0} dup-code)`
      : '';
    parts.push(`${result.skipped} skipped${breakdown}`);
  }
  if (incompleteDraft > 0) parts.push(`${incompleteDraft} incomplete(Draft)`);
  if (managersUnresolved > 0) parts.push(`${managersUnresolved} managers unresolved`);
  if (newDepartments > 0) parts.push(`${newDepartments} new ${newDepartments === 1 ? 'department' : 'departments'}`);
  if (newBranches > 0) parts.push(`${newBranches} new ${newBranches === 1 ? 'branch' : 'branches'}`);
  if (salariesHeld > 0) parts.push(`${salariesHeld} ${salariesHeld === 1 ? 'salary held' : 'salaries held'}`);
  if (possibleDuplicates > 0) parts.push(`${possibleDuplicates} possible ${possibleDuplicates === 1 ? 'duplicate' : 'duplicates'}`);
  return parts.join(' · ');
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

/** One aggregated missing/incorrect-field summary across all previewed rows. */
export interface ImportFieldGap {
  field?: string;
  label: string;
  gate?: string;
  rowCount: number;
}

/** Dry-run projection returned before commit — persists nothing server-side. */
export interface ImportPreview {
  received: number;
  wouldCreate: number;
  wouldSkip: number;
  wouldCreateActive?: number;
  wouldCreateDraft?: number;
  rows: ImportPreviewRow[];
  /** Optional per-field roll-up; when absent it is derived from each row's blocking/recommended. */
  fieldGaps?: ImportFieldGap[];
}

/**
 * Roll every row's missing/incorrect detail up into a per-field count, so the popup can show the
 * owner's "which fields" summary even when the server doesn't pre-aggregate it. Blocking gaps
 * (activation floor) are surfaced ahead of recommended ones, then by frequency.
 */
function deriveFieldGaps(preview: ImportPreview): ImportFieldGap[] {
  if (preview.fieldGaps && preview.fieldGaps.length > 0) return preview.fieldGaps;
  const counts = new Map<string, ImportFieldGap>();
  const tally = (labels: string[] | undefined, gate: string) => {
    for (const raw of labels ?? []) {
      const label = raw.trim();
      if (!label) continue;
      const existing = counts.get(label);
      if (existing) existing.rowCount += 1;
      else counts.set(label, { label, gate, rowCount: 1 });
    }
  };
  for (const row of preview.rows) {
    tally(row.blocking, 'activate');
    tally(row.recommended, 'recommended');
  }
  return [...counts.values()].sort((a, b) => {
    if (a.gate !== b.gate) return a.gate === 'activate' ? -1 : 1;
    return b.rowCount - a.rowCount;
  });
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
  /**
   * Opt-in: deep-link from the results view to the "needs info" worklist. An optional filter
   * narrows the worklist to a specific typed gap (e.g. link:manager) within this import batch.
   */
  onViewIncomplete?: (filter?: ImportResultFilter) => void;
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
  // Draft rows land as inactive-until-completed — say so in plain words rather than the raw status.
  const label = active ? 'Active' : draft ? 'Inactive · needs info' : status || '—';
  return <span className={`inline-flex rounded-full px-2 py-0.5 text-[11px] font-semibold ring-1 ${cls}`}>{label}</span>;
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

  // Full audit CSV: every error, every warning, every created-incomplete row, and one row per
  // typed gap (so the download is the complete accept-never-block ledger, not just the toasts).
  const downloadResultReport = () => {
    if (!result) return;
    const rows: string[] = [['Type', 'EmployeeCode', 'Detail'].join(',')];
    (result.errors ?? []).forEach((m) => rows.push([csvCell('Error'), csvCell(''), csvCell(m)].join(',')));
    (result.warnings ?? []).forEach((m) => rows.push([csvCell('Warning'), csvCell(''), csvCell(m)].join(',')));
    (result.createdIncomplete ?? []).forEach((c) => {
      rows.push([csvCell('Created (incomplete)'), csvCell(c.employeeCode), csvCell(`${c.name} — ${c.blockingCount} to activate`)].join(','));
      (c.gaps ?? []).forEach((g) =>
        rows.push([csvCell(`Gap ${g.type}`), csvCell(c.employeeCode), csvCell(g.detail)].join(',')),
      );
    });
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
                <span className="rounded-md bg-amber-400/15 px-2 py-1 font-semibold text-amber-700 ring-1 ring-amber-400/25 dark:text-amber-300">{preview.wouldCreateDraft ?? 0} inactive · needs info</span>
                {preview.wouldSkip > 0 && <span className="rounded-md bg-rose-500/10 px-2 py-1 font-semibold text-rose-700 ring-1 ring-rose-500/20 dark:text-rose-300">{preview.wouldSkip} rejected</span>}
              </div>
              {(preview.wouldCreateDraft ?? 0) > 0 ? (
                <div className="flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300" role="status">
                  <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
                  <span>
                    Nothing is saved yet. <strong>{preview.wouldCreateDraft} {preview.wouldCreateDraft === 1 ? 'employee' : 'employees'} will be imported as inactive until {preview.wouldCreateDraft === 1 ? 'its' : 'their'} details are completed.</strong> You can finish them one by one in the People list afterwards, or cancel and fix the sheet offline first.
                  </span>
                </div>
              ) : (
                <p className="text-xs text-slate-500 dark:text-slate-400">Nothing is saved yet. All rows have the details required to activate.</p>
              )}
              {(() => {
                const gaps = deriveFieldGaps(preview);
                if (gaps.length === 0) return null;
                return (
                  <div className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
                    <p className="mb-1.5 text-[11px] font-bold uppercase tracking-wide text-slate-400">Most common missing details</p>
                    <div className="flex flex-wrap gap-1.5">
                      {gaps.slice(0, 8).map((gap) => (
                        <span
                          key={gap.label}
                          className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-medium ring-1 ${
                            gap.gate === 'activate'
                              ? 'bg-amber-400/15 text-amber-700 ring-amber-400/25 dark:text-amber-300'
                              : 'bg-slate-100 text-slate-500 ring-slate-200 dark:bg-white/10 dark:text-slate-300'
                          }`}
                          title={gap.gate === 'activate' ? 'Required before activation' : 'Recommended detail'}
                        >
                          {gap.label}
                          <span className="font-bold">{gap.rowCount}</span>
                        </span>
                      ))}
                    </div>
                  </div>
                );
              })()}
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
                <button
                  type="button"
                  className="btn-primary"
                  onClick={() => { onViewIncomplete({ readiness: 'NotReady', importBatchId: result.importBatchId }); setResult(null); }}
                >
                  View the {incompleteCount} incomplete
                  <ArrowRight className="h-3.5 w-3.5" />
                </button>
              )}
              <button type="button" className="btn-secondary" onClick={() => setResult(null)}>Done</button>
            </>
          }
        >
          <div className="space-y-3">
            {/* Countable one-line summary — the accept-never-block ledger at a glance. */}
            <p className="rounded-lg bg-slate-50 px-3 py-2 text-sm font-medium text-slate-700 dark:bg-white/[0.04] dark:text-slate-200">
              {buildSummaryLine(result, aggregateGaps(result))}
            </p>

            <div className="flex flex-wrap gap-2 text-xs">
              <span className="inline-flex items-center gap-1 rounded-md bg-emeraldZ/10 px-2 py-1 font-semibold text-emerald-700 ring-1 ring-emeraldZ/20 dark:text-emerald-300">
                <CheckCircle2 className="h-3.5 w-3.5" /> {result.imported ?? result.created} created
              </span>
              {result.skipped > 0 && <span className="rounded-md bg-rose-500/10 px-2 py-1 font-semibold text-rose-700 ring-1 ring-rose-500/20 dark:text-rose-300">{result.skipped} skipped</span>}
              {incompleteCount > 0 && <span className="rounded-md bg-amber-400/15 px-2 py-1 font-semibold text-amber-700 ring-1 ring-amber-400/25 dark:text-amber-300">{incompleteCount} inactive · needs info</span>}
              {(result.possibleDuplicates ?? 0) > 0 && <span className="inline-flex items-center gap-1 rounded-md bg-fuchsia-500/10 px-2 py-1 font-semibold text-fuchsia-700 ring-1 ring-fuchsia-500/20 dark:text-fuchsia-300"><Users className="h-3.5 w-3.5" /> {result.possibleDuplicates} possible {result.possibleDuplicates === 1 ? 'duplicate' : 'duplicates'}</span>}
            </div>

            {/* Possible-duplicate resolution deep-links — the important new never-silent-dup signal.
                Each chip filters the People list to the flagged rows so the operator can confirm the
                person is distinct or merge into the existing record. The dup TOTAL is server-
                authoritative (`possibleDuplicates`); per-type counts come from the flagged rows, and
                when a dup landed Active (not in the incomplete set) both filters are still offered so
                every flagged row stays reachable. */}
            {(() => {
              const { counts } = aggregateGaps(result);
              const strong = counts.get('dup:strong') ?? 0;
              const possible = counts.get('dup:possible') ?? 0;
              const dupTotal = result.possibleDuplicates ?? (strong + possible);
              if (dupTotal <= 0 || !onViewIncomplete) return null;
              const showBoth = strong === 0 && possible === 0; // total known only from the server counter
              const chips: Array<{ type: string; label: string; count: number }> = [];
              if (strong > 0 || showBoth) chips.push({ type: 'dup:strong', label: 'ID match', count: strong });
              if (possible > 0 || showBoth) chips.push({ type: 'dup:possible', label: 'name + DOB', count: possible });
              return (
                <div className="rounded-lg border border-fuchsia-200 bg-fuchsia-50/60 p-3 dark:border-fuchsia-500/30 dark:bg-fuchsia-500/[0.06]">
                  <p className="mb-1.5 flex items-center gap-1.5 text-[11px] font-bold uppercase tracking-wide text-fuchsia-700 dark:text-fuchsia-300">
                    <Users className="h-3.5 w-3.5" /> Possible duplicates · {dupTotal}
                  </p>
                  <div className="flex flex-wrap gap-1.5">
                    {chips.map(({ type, label, count }) => (
                      <button
                        key={type}
                        type="button"
                        onClick={() => { onViewIncomplete({ gapType: type, importBatchId: result.importBatchId }); setResult(null); }}
                        className="inline-flex items-center gap-1 rounded-full bg-fuchsia-500/10 px-2 py-0.5 text-[11px] font-medium text-fuchsia-700 ring-1 ring-fuchsia-500/25 transition-colors hover:bg-fuchsia-500/20 dark:text-fuchsia-300"
                        title={`Filter the People list to these possible ${label} duplicates`}
                      >
                        {label}
                        {count > 0 && <span className="font-bold">{count}</span>}
                      </button>
                    ))}
                  </div>
                  <p className="mt-1.5 text-[11px] text-fuchsia-700/80 dark:text-fuchsia-300/80">Imported as-is — open each to confirm it&apos;s a distinct person or merge it into the existing record. Nothing was auto-merged.</p>
                </div>
              );
            })()}

            {/* Per-gap deep-link chips: each jumps straight to the filtered "needs info" worklist.
                Duplicate flags are surfaced in their own block above, so they are excluded here. */}
            {(() => {
              const { counts } = aggregateGaps(result);
              const entries = [...counts.entries()].filter(([type]) => !isDuplicateGap(type)).sort((a, b) => b[1] - a[1]);
              if (entries.length === 0 || !onViewIncomplete) return null;
              return (
                <div>
                  <p className="mb-1.5 text-[11px] font-bold uppercase tracking-wide text-slate-400">Jump to a gap</p>
                  <div className="flex flex-wrap gap-1.5">
                    {entries.map(([type, count]) => (
                      <button
                        key={type}
                        type="button"
                        onClick={() => { onViewIncomplete({ gapType: type, importBatchId: result.importBatchId }); setResult(null); }}
                        className="inline-flex items-center gap-1 rounded-full bg-amber-400/15 px-2 py-0.5 text-[11px] font-medium text-amber-700 ring-1 ring-amber-400/25 transition-colors hover:bg-amber-400/25 dark:text-amber-300"
                        title={`Filter the People list to these ${gapLabel(type, count)}`}
                      >
                        {gapLabel(type, count)}
                        <span className="font-bold">{count}</span>
                      </button>
                    ))}
                  </div>
                </div>
              );
            })()}

            {incompleteCount > 0 && (
              <p className="text-xs text-slate-500 dark:text-slate-400">
                {incompleteCount} {incompleteCount === 1 ? 'employee was' : 'employees were'} imported as <strong>inactive</strong> because required details are missing — {incompleteCount === 1 ? 'it' : 'they'} cannot be set Active until completed. Open each from the list (they show a red <em>Incomplete</em> badge) to finish them.
              </p>
            )}

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
