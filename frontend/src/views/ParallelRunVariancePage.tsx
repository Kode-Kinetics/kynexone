'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  AlertTriangle, CheckCircle2, Download, FileWarning, Loader2, RotateCcw, Scale, Upload, X,
} from 'lucide-react';
import { SectionHeader } from '@/src/components/SectionHeader';
import { DataPanel } from '@/src/components/DataPanel';
import { ErrorBanner } from '@/src/components/ui/ErrorBanner';
import { useTenantSettings } from '@/src/contexts/TenantSettingsContext';
import { payrollApi, type PayrollRun } from '@/src/api/payroll';
import {
  parallelRunApi, VARIANCE_REGISTER_TEMPLATE,
  type VarianceLine, type VarianceReport, type VarianceToleranceType,
} from '@/src/api/migrations';

// ─────────────────────────────────────────────────────────────────────────────
//  Parallel-run variance — the tie-out against the system the customer is leaving
//
//  `POST /api/payroll/parallel-run/{runId}/variance` has been built and unit-proven for a
//  while with ZERO callers. Every serious payroll go-live runs both systems side by side for
//  one to three months and reconciles to the fils; without this screen the only way to do
//  that was to export both registers and reconcile in Excel with VLOOKUP.
//
//  The shape below is what a finance reviewer reads, not what the JSON happens to contain:
//  the net tie-out first, then employee coverage, then the lines — filtered to the ones that
//  differ, because on a thousand-employee register the matched rows are not the artefact.
// ─────────────────────────────────────────────────────────────────────────────

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** A run with no slips cannot be compared — the comparison would report every employee missing. */
const COMPARABLE_STATUSES = ['Processed', 'PendingFinanceReview', 'Approved', 'Locked', 'Paid'];

type LineFilter = 'outside' | 'differing' | 'matched' | 'onlyMine' | 'onlyTheirs' | 'all';

const FILTERS: { key: LineFilter; label: string; hint: string }[] = [
  { key: 'outside', label: 'Outside tolerance', hint: 'Differences larger than the tolerance you set. Start here.' },
  { key: 'differing', label: 'All differences', hint: 'Every line where the two systems disagree by any amount.' },
  { key: 'matched', label: 'Matched', hint: 'Present on both sides and identical to the fils.' },
  { key: 'onlyMine', label: 'Only in KynexOne', hint: 'This product paid it; the legacy register has no such line.' },
  { key: 'onlyTheirs', label: 'Only in the register', hint: 'The legacy register has it; this run does not.' },
  { key: 'all', label: 'All lines', hint: 'Everything compared, matched and differing alike.' },
];

const PAGE_SIZE = 200;

function errorMessage(err: unknown): string {
  const anyErr = err as { response?: { data?: { message?: string; errors?: string[] } }; message?: string };
  const data = anyErr?.response?.data;
  if (data?.message && Array.isArray(data.errors) && data.errors.length > 0) {
    return `${data.message} ${data.errors.slice(0, 4).join(' · ')}${data.errors.length > 4 ? ` (+${data.errors.length - 4} more)` : ''}`;
  }
  if (data?.message) return data.message;
  if (Array.isArray(data?.errors) && data.errors.length > 0) return data.errors.join(' · ');
  return anyErr?.message ?? 'The comparison could not be run. Please try again.';
}

function money(n: number, currency: string) {
  return `${n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${currency}`;
}

function plain(n: number) {
  return n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function matchesFilter(line: VarianceLine, filter: LineFilter): boolean {
  switch (filter) {
    case 'outside': return line.isOutsideTolerance;
    case 'differing': return line.variance !== 0;
    case 'matched': return line.variance === 0 && line.presence === 'Both';
    case 'onlyMine': return line.presence === 'OnlyInKynexOne';
    case 'onlyTheirs': return line.presence === 'OnlyInRegister';
    case 'all': return true;
  }
}

function downloadCsv(filename: string, csv: string) {
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

export function ParallelRunVariancePage() {
  const { currencyCode } = useTenantSettings();

  const [runs, setRuns] = useState<PayrollRun[] | null>(null);
  const [runsError, setRunsError] = useState<string | null>(null);
  const [runId, setRunId] = useState('');

  const [registerCsv, setRegisterCsv] = useState('');
  const [fileName, setFileName] = useState('');
  const [tolerance, setTolerance] = useState('0.01');
  const [toleranceType, setToleranceType] = useState<VarianceToleranceType>('Absolute');

  const [report, setReport] = useState<VarianceReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<LineFilter>('outside');
  const [visible, setVisible] = useState(PAGE_SIZE);

  const fileInput = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    let cancelled = false;
    payrollApi
      .listRuns({ pageSize: 100 })
      .then((r) => { if (!cancelled) setRuns(r.items); })
      .catch((e) => { if (!cancelled) setRunsError(errorMessage(e)); });
    return () => { cancelled = true; };
  }, []);

  const selectedRun = runs?.find((r) => r.id === runId) ?? null;
  const runNotComparable = selectedRun !== null && !COMPARABLE_STATUSES.includes(selectedRun.status);

  const toleranceNumber = Number(tolerance);
  const toleranceValid = tolerance.trim().length > 0 && Number.isFinite(toleranceNumber) && toleranceNumber >= 0;
  const canCompare = runId.length > 0 && registerCsv.trim().length > 0 && toleranceValid && !busy;

  const attachFile = useCallback(async (file: File) => {
    const text = await file.text();
    setRegisterCsv(text);
    setFileName(file.name);
    setReport(null);
  }, []);

  function detachFile() {
    setRegisterCsv('');
    setFileName('');
    setReport(null);
    if (fileInput.current) fileInput.current.value = '';
  }

  async function runComparison() {
    if (!canCompare) return;
    setBusy(true);
    setError(null);
    try {
      const dto = await parallelRunApi.variance(runId, {
        registerCsv,
        tolerance: toleranceNumber,
        toleranceType,
      });
      setReport(dto);
      setFilter(dto.linesOutsideTolerance > 0 ? 'outside' : 'differing');
      setVisible(PAGE_SIZE);
    } catch (e: unknown) {
      setReport(null);
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  function startOver() {
    setReport(null);
    setError(null);
    detachFile();
  }

  const filtered = useMemo(
    () => (report ? report.lines.filter((l) => matchesFilter(l, filter)) : []),
    [report, filter],
  );

  const counts = useMemo(() => {
    const empty = { outside: 0, differing: 0, matched: 0, onlyMine: 0, onlyTheirs: 0, all: 0 };
    if (!report) return empty;
    for (const l of report.lines) {
      empty.all += 1;
      if (l.isOutsideTolerance) empty.outside += 1;
      if (l.variance !== 0) empty.differing += 1;
      if (l.variance === 0 && l.presence === 'Both') empty.matched += 1;
      if (l.presence === 'OnlyInKynexOne') empty.onlyMine += 1;
      if (l.presence === 'OnlyInRegister') empty.onlyTheirs += 1;
    }
    return empty;
  }, [report]);

  function exportReport() {
    if (!report) return;
    const header = 'EmployeeCode,EmployeeName,ComponentCode,KynexOneAmount,ExternalAmount,Variance,VariancePct,OutsideTolerance,Presence';
    const esc = (s: string) => (/[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
    const rows = filtered.map((l) =>
      [
        esc(l.employeeCode), esc(l.employeeName), esc(l.componentCode),
        l.kynexOneAmount, l.externalAmount, l.variance, l.variancePct,
        l.isOutsideTolerance ? 'Yes' : 'No', l.presence,
      ].join(','),
    );
    downloadCsv(`kynexone-variance-${report.period}-${filter}.csv`, [header, ...rows].join('\n'));
  }

  const netDelta = report ? Math.round((report.kynexOneNetTotal - report.externalNetTotal) * 100) / 100 : 0;

  return (
    <div className="space-y-6">
      <SectionHeader
        eyebrow="Parallel run"
        title="Variance against the legacy register"
        description="Upload the payroll register your outgoing system produced for the same month. Every employee and every component is reconciled to the fils, so the figure the customer signs off is reproducible rather than a spreadsheet nobody can re-run."
        action={
          report ? (
            <button
              type="button"
              onClick={startOver}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-300 px-3 py-2 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-100 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
            >
              <RotateCcw className="h-4 w-4" aria-hidden="true" />
              New comparison
            </button>
          ) : null
        }
      />

      {error ? <ErrorBanner message={error} onDismiss={() => setError(null)} /> : null}

      <DataPanel
        title="What are you comparing?"
        description="A processed run on this side, and the legacy register — long form, one row per employee per component — on the other."
        action={
          <button
            type="button"
            onClick={() => downloadCsv('kynexone-legacy-register-template.csv', VARIANCE_REGISTER_TEMPLATE)}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition-colors hover:bg-slate-100 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
          >
            <Download className="h-3.5 w-3.5" aria-hidden="true" />
            Template
          </button>
        }
      >
        <div className="space-y-4">
          <div>
            <label htmlFor="variance-run" className="mb-1.5 block text-xs font-semibold text-slate-700 dark:text-slate-300">
              KynexOne payroll run
            </label>
            {runs === null && runsError === null ? (
              <p className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
                <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
                Loading runs…
              </p>
            ) : runs === null ? (
              // Reachable only with runsError set — the loading case is the branch above.
              <p className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-800/60 dark:bg-amber-900/20 dark:text-amber-300">
                Payroll runs could not be loaded ({runsError}).
              </p>
            ) : runs.length === 0 ? (
              <p className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-xs text-slate-600 dark:border-slate-700 dark:bg-slate-800/60 dark:text-slate-300">
                There are no payroll runs yet. Process a run for the parallel month first — the comparison reads its
                payslips, earnings and deductions.
              </p>
            ) : (
              <select
                id="variance-run"
                value={runId}
                onChange={(e) => { setRunId(e.target.value); setReport(null); }}
                className="w-full max-w-md rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none focus:ring-1 focus:ring-sapphire dark:border-slate-700 dark:bg-slate-900 dark:text-white dark:focus:border-cyanAccent dark:focus:ring-cyanAccent"
              >
                <option value="">Select a run…</option>
                {runs.map((r) => (
                  <option key={r.id} value={r.id}>
                    {MONTHS[r.month - 1]} {r.year} — {r.status} ({r.employeeCount} employees)
                  </option>
                ))}
              </select>
            )}
            {runNotComparable ? (
              <p className="mt-2 flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-800/60 dark:bg-amber-900/20 dark:text-amber-300">
                <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                <span>
                  This run is <strong>{selectedRun?.status}</strong> and has no payslips yet, so every employee in the
                  register will report as missing. Process the run first — a processed-but-unlocked run writes no GL and
                  publishes no payslip, so it is safe to compare and re-process as many times as you need.
                </span>
              </p>
            ) : null}
          </div>

          <div>
            <span className="mb-1.5 block text-xs font-semibold text-slate-700 dark:text-slate-300">
              Legacy payroll register (CSV)
            </span>
            <p className="mb-2 text-xs leading-5 text-slate-600 dark:text-slate-400">
              Columns <code className="rounded bg-slate-100 px-1 py-0.5 font-mono text-[11px] dark:bg-slate-800">EmployeeCode</code>,{' '}
              <code className="rounded bg-slate-100 px-1 py-0.5 font-mono text-[11px] dark:bg-slate-800">ComponentCode</code>,{' '}
              <code className="rounded bg-slate-100 px-1 py-0.5 font-mono text-[11px] dark:bg-slate-800">Amount</code>. Use the
              reserved codes <strong>GROSS</strong>, <strong>DEDUCTIONS</strong> and <strong>NET</strong> for the totals —
              every source system has those three even when its component names match nothing here.
            </p>
            {fileName ? (
              <div className="flex flex-wrap items-center gap-2 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-xs text-emerald-800 dark:border-emerald-800/60 dark:bg-emerald-900/20 dark:text-emerald-300">
                <CheckCircle2 className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                <span className="min-w-0 flex-1 truncate font-semibold">{fileName}</span>
                <span className="shrink-0 opacity-75">{registerCsv.split('\n').filter((l) => l.trim().length > 0).length - 1} data row(s)</span>
                <button
                  type="button"
                  onClick={detachFile}
                  aria-label={`Remove ${fileName}`}
                  className="shrink-0 rounded p-0.5 transition-colors hover:bg-emerald-100 dark:hover:bg-emerald-800/40"
                >
                  <X className="h-3.5 w-3.5" aria-hidden="true" />
                </button>
              </div>
            ) : (
              <label className="inline-flex cursor-pointer items-center gap-2 rounded-lg border border-dashed border-slate-300 px-3 py-2 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-50 dark:border-slate-600 dark:text-slate-200 dark:hover:bg-slate-800/60">
                <Upload className="h-4 w-4" aria-hidden="true" />
                Attach register
                <input
                  ref={fileInput}
                  type="file"
                  accept=".csv,text/csv"
                  className="sr-only"
                  onChange={(e) => {
                    const f = e.target.files?.[0];
                    if (f) void attachFile(f);
                  }}
                />
              </label>
            )}
          </div>

          <div className="flex flex-wrap items-end gap-3">
            <div>
              <label htmlFor="variance-tolerance" className="mb-1.5 block text-xs font-semibold text-slate-700 dark:text-slate-300">
                Tolerance
              </label>
              <input
                id="variance-tolerance"
                type="number"
                min="0"
                step="0.01"
                value={tolerance}
                onChange={(e) => setTolerance(e.target.value)}
                aria-invalid={!toleranceValid}
                className="w-32 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none focus:ring-1 focus:ring-sapphire dark:border-slate-700 dark:bg-slate-900 dark:text-white dark:focus:border-cyanAccent dark:focus:ring-cyanAccent"
              />
            </div>
            <div>
              <label htmlFor="variance-tolerance-type" className="mb-1.5 block text-xs font-semibold text-slate-700 dark:text-slate-300">
                Measured as
              </label>
              <select
                id="variance-tolerance-type"
                value={toleranceType}
                onChange={(e) => setToleranceType(e.target.value as VarianceToleranceType)}
                className="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none focus:ring-1 focus:ring-sapphire dark:border-slate-700 dark:bg-slate-900 dark:text-white dark:focus:border-cyanAccent dark:focus:ring-cyanAccent"
              >
                <option value="Absolute">Absolute amount</option>
                <option value="Percentage">Percentage of the legacy figure</option>
              </select>
            </div>
            <button
              type="button"
              onClick={runComparison}
              disabled={!canCompare}
              className="inline-flex items-center gap-2 rounded-lg bg-sapphire px-4 py-2 text-sm font-semibold text-white transition-colors hover:bg-sapphire/90 disabled:cursor-not-allowed disabled:opacity-40 dark:bg-cyanAccent dark:text-slate-950 dark:hover:bg-cyanAccent/90"
            >
              {busy ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <Scale className="h-4 w-4" aria-hidden="true" />}
              {busy ? 'Comparing…' : 'Compare'}
            </button>
          </div>
          {!toleranceValid ? (
            <p className="text-xs text-rose-600 dark:text-rose-400">Tolerance must be a number of zero or more.</p>
          ) : null}
        </div>
      </DataPanel>

      {report ? (
        <VarianceResult
          report={report}
          currency={currencyCode}
          netDelta={netDelta}
          filter={filter}
          counts={counts}
          filtered={filtered}
          visible={visible}
          onFilter={(f) => { setFilter(f); setVisible(PAGE_SIZE); }}
          onShowMore={() => setVisible((v) => v + PAGE_SIZE)}
          onExport={exportReport}
        />
      ) : null}
    </div>
  );
}

// ── The report ───────────────────────────────────────────────────────────────

function VarianceResult({
  report, currency, netDelta, filter, counts, filtered, visible, onFilter, onShowMore, onExport,
}: {
  report: VarianceReport;
  currency: string;
  netDelta: number;
  filter: LineFilter;
  counts: Record<LineFilter, number>;
  filtered: VarianceLine[];
  visible: number;
  onFilter: (f: LineFilter) => void;
  onShowMore: () => void;
  onExport: () => void;
}) {
  const clean = report.linesOutsideTolerance === 0
    && report.employeesOnlyInRun === 0
    && report.employeesOnlyInRegister === 0;

  return (
    <div className="space-y-6">
      {/* The one number a CFO looks at first. */}
      <section
        className={[
          'rounded-xl border p-5',
          clean
            ? 'border-emerald-300 bg-emerald-50 dark:border-emerald-700/60 dark:bg-emerald-900/20'
            : 'border-amber-300 bg-amber-50 dark:border-amber-700/60 dark:bg-amber-900/20',
        ].join(' ')}
      >
        <div className="flex items-start gap-3">
          {clean
            ? <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />
            : <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-600 dark:text-amber-400" aria-hidden="true" />}
          <div className="min-w-0">
            <h2 className="text-sm font-bold text-slate-900 dark:text-white">
              {clean
                ? `Period ${report.period} reconciles within tolerance.`
                : `Period ${report.period} — ${report.linesOutsideTolerance} line(s) outside tolerance.`}
            </h2>
            <p className="mt-1 text-xs leading-5 text-slate-700 dark:text-slate-300">
              Tolerance {report.toleranceType === 'Percentage' ? `${plain(report.tolerance)}%` : money(report.tolerance, currency)},
              measured {report.toleranceType === 'Percentage' ? 'against the legacy figure' : 'as an absolute amount'}.
            </p>
          </div>
        </div>

        <dl className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-3">
          <Figure label="Net — KynexOne" value={money(report.kynexOneNetTotal, currency)} />
          <Figure label="Net — legacy register" value={money(report.externalNetTotal, currency)} />
          <Figure
            label="Difference"
            value={money(netDelta, currency)}
            tone={netDelta === 0 ? 'good' : 'bad'}
          />
        </dl>
      </section>

      <DataPanel title="Coverage" description="Who each system thinks it paid this month. An employee on one side only is the first thing to explain.">
        <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
          <Figure label="In this run" value={String(report.employeesInRun)} />
          <Figure label="In the register" value={String(report.employeesInRegister)} />
          <Figure label="Matched" value={String(report.matchedEmployees)} tone={report.matchedEmployees > 0 ? 'good' : undefined} />
          <Figure label="Only in KynexOne" value={String(report.employeesOnlyInRun)} tone={report.employeesOnlyInRun > 0 ? 'bad' : undefined} />
          <Figure label="Only in the register" value={String(report.employeesOnlyInRegister)} tone={report.employeesOnlyInRegister > 0 ? 'bad' : undefined} />
          <Figure label="Total absolute variance" value={money(report.totalAbsoluteVariance, currency)} tone={report.totalAbsoluteVariance > 0 ? 'bad' : 'good'} />
        </dl>
      </DataPanel>

      {report.registerErrors.length > 0 ? (
        <section className="rounded-xl border border-rose-200 bg-rose-50 p-5 dark:border-rose-800/60 dark:bg-rose-900/20">
          <div className="flex items-start gap-3">
            <FileWarning className="mt-0.5 h-5 w-5 shrink-0 text-rose-600 dark:text-rose-400" aria-hidden="true" />
            <div className="min-w-0">
              <h2 className="text-sm font-bold text-rose-900 dark:text-rose-200">
                {report.registerErrors.length} row(s) of the register could not be read
              </h2>
              <p className="mt-1 text-xs leading-5 text-rose-800 dark:text-rose-300">
                These rows were <strong>not</strong> compared, so the totals above exclude them. Correct the file and run
                the comparison again before showing anybody this reconciliation.
              </p>
              <ul className="mt-3 max-h-56 space-y-1 overflow-y-auto text-xs text-rose-800 dark:text-rose-300">
                {report.registerErrors.map((e, i) => (
                  <li key={i} className="font-mono">{e}</li>
                ))}
              </ul>
            </div>
          </div>
        </section>
      ) : null}

      <DataPanel
        title="Variance lines"
        description={FILTERS.find((f) => f.key === filter)?.hint}
        action={
          <button
            type="button"
            onClick={onExport}
            disabled={filtered.length === 0}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-40 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
          >
            <Download className="h-3.5 w-3.5" aria-hidden="true" />
            Export view
          </button>
        }
      >
        <div className="mb-4 flex flex-wrap gap-2" role="tablist" aria-label="Variance line filter">
          {FILTERS.map((f) => (
            <button
              key={f.key}
              type="button"
              role="tab"
              aria-selected={filter === f.key}
              onClick={() => onFilter(f.key)}
              className={[
                'rounded-full px-3 py-1.5 text-xs font-semibold transition-colors',
                filter === f.key
                  ? 'bg-sapphire text-white dark:bg-cyanAccent dark:text-slate-950'
                  : 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700',
              ].join(' ')}
            >
              {f.label}
              <span className="ms-1.5 opacity-70">{counts[f.key]}</span>
            </button>
          ))}
        </div>

        {filtered.length === 0 ? (
          <p className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-6 text-center text-sm text-slate-600 dark:border-slate-700 dark:bg-slate-800/60 dark:text-slate-300">
            {filter === 'outside'
              ? 'Nothing is outside tolerance. Every line the two systems share agrees within the limit you set.'
              : 'No lines in this view.'}
          </p>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full min-w-[46rem] text-sm">
                <thead>
                  <tr className="border-b border-slate-200 text-xs uppercase tracking-wide text-slate-500 dark:border-slate-700 dark:text-slate-400">
                    <th scope="col" className="py-2 pe-3 text-start font-semibold">Employee</th>
                    <th scope="col" className="py-2 pe-3 text-start font-semibold">Component</th>
                    <th scope="col" className="py-2 pe-3 text-end font-semibold">KynexOne</th>
                    <th scope="col" className="py-2 pe-3 text-end font-semibold">Legacy</th>
                    <th scope="col" className="py-2 pe-3 text-end font-semibold">Variance</th>
                    <th scope="col" className="py-2 pe-3 text-end font-semibold">%</th>
                    <th scope="col" className="py-2 text-start font-semibold">Present</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                  {filtered.slice(0, visible).map((l, i) => (
                    <tr
                      key={`${l.employeeCode}-${l.componentCode}-${i}`}
                      className={l.isOutsideTolerance ? 'bg-rose-50/60 dark:bg-rose-900/10' : undefined}
                    >
                      <td className="py-2 pe-3 align-top">
                        <span className="block font-mono text-xs font-semibold text-slate-900 dark:text-white">{l.employeeCode}</span>
                        {l.employeeName ? <span className="block text-xs text-slate-500 dark:text-slate-400">{l.employeeName}</span> : null}
                      </td>
                      <td className="py-2 pe-3 align-top font-mono text-xs text-slate-700 dark:text-slate-300">{l.componentCode}</td>
                      <td className="py-2 pe-3 text-end align-top tabular-nums text-slate-900 dark:text-white">{plain(l.kynexOneAmount)}</td>
                      <td className="py-2 pe-3 text-end align-top tabular-nums text-slate-900 dark:text-white">{plain(l.externalAmount)}</td>
                      <td
                        className={[
                          'py-2 pe-3 text-end align-top tabular-nums font-semibold',
                          l.variance === 0
                            ? 'text-slate-400 dark:text-slate-500'
                            : l.isOutsideTolerance
                              ? 'text-rose-600 dark:text-rose-400'
                              : 'text-amber-600 dark:text-amber-400',
                        ].join(' ')}
                      >
                        {l.variance > 0 ? '+' : ''}{plain(l.variance)}
                      </td>
                      <td className="py-2 pe-3 text-end align-top tabular-nums text-xs text-slate-500 dark:text-slate-400">
                        {l.variancePct === 0 ? '—' : `${l.variancePct > 0 ? '+' : ''}${l.variancePct.toFixed(2)}%`}
                      </td>
                      <td className="py-2 align-top">
                        <PresenceBadge presence={l.presence} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {filtered.length > visible ? (
              <div className="mt-4 text-center">
                <button
                  type="button"
                  onClick={onShowMore}
                  className="rounded-lg border border-slate-300 px-3 py-2 text-xs font-semibold text-slate-700 transition-colors hover:bg-slate-100 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                >
                  Show more — {filtered.length - visible} remaining
                </button>
              </div>
            ) : null}
          </>
        )}
      </DataPanel>
    </div>
  );
}

function Figure({ label, value, tone }: { label: string; value: string; tone?: 'good' | 'bad' }) {
  return (
    <div className="rounded-lg border border-slate-200 bg-white px-3 py-2.5 dark:border-slate-700 dark:bg-slate-900/60">
      <dt className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</dt>
      <dd
        className={[
          'mt-0.5 text-sm font-bold tabular-nums',
          tone === 'good' ? 'text-emerald-700 dark:text-emerald-400'
            : tone === 'bad' ? 'text-rose-700 dark:text-rose-400'
              : 'text-slate-900 dark:text-white',
        ].join(' ')}
      >
        {value}
      </dd>
    </div>
  );
}

const PRESENCE_LABEL: Record<string, { label: string; className: string }> = {
  Both: { label: 'Both', className: 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300' },
  OnlyInKynexOne: { label: 'KynexOne only', className: 'bg-indigo-50 text-indigo-700 dark:bg-indigo-900/40 dark:text-indigo-300' },
  OnlyInRegister: { label: 'Register only', className: 'bg-orange-50 text-orange-700 dark:bg-orange-900/40 dark:text-orange-300' },
  Neither: { label: 'Neither', className: 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400' },
};

function PresenceBadge({ presence }: { presence: string }) {
  const meta = PRESENCE_LABEL[presence] ?? PRESENCE_LABEL.Neither;
  return (
    <span className={`inline-block whitespace-nowrap rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide ${meta.className}`}>
      {meta.label}
    </span>
  );
}
