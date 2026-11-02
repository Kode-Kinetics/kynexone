'use client';

import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import {
  AlertTriangle, ArrowRight, CheckCircle2, Download, FileWarning, History,
  Loader2, Lock, PlayCircle, RotateCcw, Upload, X,
} from 'lucide-react';
import { SectionHeader } from '@/src/components/SectionHeader';
import { DataPanel } from '@/src/components/DataPanel';
import { ErrorBanner } from '@/src/components/ui/ErrorBanner';
import {
  MIGRATION_SECTIONS, migrationsApi,
  type LockedPeriodRefusal, type MigrationPackageRequest, type MigrationReconciliation, type SectionMeta,
} from '@/src/api/migrations';
import { useT } from '@/src/hooks/useT';
import { useFormat } from '@/src/hooks/useFormat';
import { EnumLabel } from '@/src/components/EnumLabel';
import { enumLabel } from '@/src/i18n/enumLabel';

type Translate = (key: string, params?: Record<string, string | number | null | undefined>) => string;

/** Splices React nodes into a translated sentence: translate with `mark('name')` as the value, then pass the nodes. */
const mark = (name: string) => `\u0001${name}\u0001`;
function rich(text: string, nodes: Record<string, React.ReactNode>): React.ReactNode[] {
  return text.split(/\u0001(\w+)\u0001/).map((part, i) => (i % 2 === 1 ? <span key={i}>{nodes[part]}</span> : part));
}

/** The section names and descriptions live in api/migrations.ts as English; this is their translation by key. */
function sectionText(t: Translate): Record<string, { label: string; description: string }> {
  return {
    companyCutover: { label: t('Cutover dates'), description: t('One row per legal entity, naming the date this product takes over its payroll. Import this first — nothing else is accepted without it.') },
    roles: { label: t('Roles'), description: t('Security roles carried across from the previous system.') },
    users: { label: t('Users'), description: t('Login accounts, invited rather than activated.') },
    leaveBalances: { label: t('Leave balances'), description: t('Entitled, accrued, used, carried forward and encashed, per leave type.') },
    attendanceDaily: { label: t('Attendance history'), description: t('Daily records including late, early-exit and overtime minutes.') },
    employeeHistory: { label: t('Employment history'), description: t('Job and status changes, as an audit trail.') },
    payrollOpeningBalances: { label: t('Year-to-date payroll'), description: t('YTD gross, deductions and net, plus the statutory and tax split for the annual reconciliation.') },
    loans: { label: t('Loans'), description: t('Outstanding balances with their remaining instalment schedules, imported mid-life so payroll keeps deducting correctly.') },
    advances: { label: t('Salary advances'), description: t('Outstanding advance balances and their remaining repayments.') },
    eosbOpeningProvision: { label: t('End-of-service provision'), description: t('The accrued liability already on the customer’s balance sheet, per employee.') },
    benefitsEnrollments: { label: t('Benefits enrolments'), description: t('Plan enrolments and contributions.') },
    documentManifests: { label: t('Document manifest'), description: t('Pointers to documents in the legacy store. No files are moved.') },
    contracts: { label: t('Contracts'), description: t('Employment contracts and their basic salary.') },
    reconciliationSignoffs: { label: t('Reconciliation sign-off'), description: t('Who prepared and who approved the reconciliation, with the variance count and evidence.') },
  };
}
function sectionOf(t: Translate, s: SectionMeta) {
  return sectionText(t)[s.key] ?? { label: s.label, description: s.description };
}

// ─────────────────────────────────────────────────────────────────────────────
//  Opening-balance import wizard
//
//  The engine this drives has existed for a while and has had no caller: a consultant
//  ran it from Postman against a JSON envelope of CSV strings. Everything below exists
//  so the customer's own payroll lead can re-run a corrected file at 11pm during parallel
//  without a consultant on the phone.
//
//  The five steps are the five things that go wrong, in the order they go wrong:
//  choose what you are loading, get the template so the columns are right, attach the
//  file, LOOK AT THE RECONCILIATION before committing anything, and — when a 3,000-row
//  import dies on row 2,847 — resume it rather than restoring a database.
// ─────────────────────────────────────────────────────────────────────────────

type Step = 'choose' | 'upload' | 'preview' | 'done';

interface StoredRun {
  batchId: string;
  status: string;
  at: string;
  sections: string[];
  receivedRows: number;
  errorRows: number;
  /** The package is kept so Resume can be offered: the engine checksums it and refuses a different file. */
  package: MigrationPackageRequest;
}

const HISTORY_KEY = 'kynexone_migration_history';
const MAX_HISTORY = 10;

function readHistory(): StoredRun[] {
  if (typeof window === 'undefined') return [];
  try {
    const raw = window.localStorage.getItem(HISTORY_KEY);
    return raw ? (JSON.parse(raw) as StoredRun[]) : [];
  } catch {
    return [];
  }
}

function writeHistory(runs: StoredRun[]) {
  try {
    window.localStorage.setItem(HISTORY_KEY, JSON.stringify(runs.slice(0, MAX_HISTORY)));
  } catch {
    /* history is a convenience — never block an import on it */
  }
}

function errorMessage(err: unknown, fallback: string): string {
  const anyErr = err as { response?: { data?: { message?: string; errors?: string[] } }; message?: string };
  const data = anyErr?.response?.data;
  if (data?.message) return data.message;
  if (Array.isArray(data?.errors) && data.errors.length > 0) return data.errors.join(' · ');
  if (Array.isArray(data)) return (data as string[]).join(' · ');
  return anyErr?.message ?? fallback;
}

function lockedRefusals(err: unknown): LockedPeriodRefusal[] {
  const data = (err as { response?: { data?: { code?: string; refusals?: LockedPeriodRefusal[] } } })?.response?.data;
  return data?.code === 'cutover_period_locked' ? data.refusals ?? [] : [];
}


/**
 * Opening balances. `embedded` is its Setup section (Setup → Get started): the section header already names it, so only
 * its description and the "Start over" action are shown, without a page title.
 */
export function MigrationImportPage({ embedded = false, onDirtyChange }: { embedded?: boolean; onDirtyChange?: (dirty: boolean) => void } = {}) {
  const t = useT();
  const OPENING_BALANCES_DESCRIPTION = t('Carry a customer in from their previous system, at any point in the year. Import the cutover date for each legal entity first, then the balances that sit under it — year-to-date payroll, leave, loans, advances and the end-of-service provision. Nothing is written until you have seen the reconciliation.');
  const failMessage = (e: unknown) => errorMessage(e, t('Something went wrong. Please try again.'));
  const [step, setStep] = useState<Step>('choose');
  const [selected, setSelected] = useState<string[]>([]);
  const [files, setFiles] = useState<Record<string, string>>({});
  const [fileNames, setFileNames] = useState<Record<string, string>>({});
  const [batchRef, setBatchRef] = useState('');

  const [templates, setTemplates] = useState<Record<string, string> | null>(null);
  const [templatesError, setTemplatesError] = useState<string | null>(null);

  const [preview, setPreview] = useState<MigrationReconciliation | null>(null);
  const [result, setResult] = useState<MigrationReconciliation | null>(null);
  const [refusals, setRefusals] = useState<LockedPeriodRefusal[]>([]);
  const [busy, setBusy] = useState<null | 'preview' | 'commit' | 'resume' | 'status'>(null);
  const [error, setError] = useState<string | null>(null);
  const [history, setHistory] = useState<StoredRun[]>([]);

  const fileInputs = useRef<Record<string, HTMLInputElement | null>>({});

  // Files attached, or a reconciliation waiting for its commit: leaving Setup's section would throw that work away.
  const inProgress = step === 'upload' || step === 'preview';
  useLayoutEffect(() => { onDirtyChange?.(inProgress); return () => onDirtyChange?.(false); }, [inProgress, onDirtyChange]);

  useEffect(() => {
    setHistory(readHistory());
  }, []);

  useEffect(() => {
    let cancelled = false;
    migrationsApi
      .templates()
      .then((tpl) => {
        if (!cancelled) setTemplates(tpl);
      })
      .catch((e) => {
        if (!cancelled) setTemplatesError(failMessage(e));
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const chosen: SectionMeta[] = useMemo(
    () => MIGRATION_SECTIONS.filter((s) => selected.includes(s.key)),
    [selected],
  );

  const attached = chosen.filter((s) => (files[s.key] ?? '').trim().length > 0);
  const canPreview = attached.length > 0;

  const buildPackage = useCallback(
    (dryRun: boolean): MigrationPackageRequest => ({
      externalBatchId: batchRef.trim() || null,
      sections: Object.fromEntries(attached.map((s) => [s.key, files[s.key]])),
      dryRun,
    }),
    [attached, files, batchRef],
  );

  const remember = useCallback((dto: MigrationReconciliation, pkg: MigrationPackageRequest) => {
    setHistory((prev) => {
      const next: StoredRun[] = [
        {
          batchId: dto.batchId,
          status: dto.status,
          at: new Date().toISOString(),
          sections: Object.keys(pkg.sections),
          receivedRows: dto.receivedRows,
          errorRows: dto.errorRows,
          package: pkg,
        },
        ...prev.filter((r) => r.batchId !== dto.batchId),
      ].slice(0, MAX_HISTORY);
      writeHistory(next);
      return next;
    });
  }, []);

  function toggleSection(key: string) {
    setSelected((prev) => (prev.includes(key) ? prev.filter((k) => k !== key) : [...prev, key]));
  }

  function downloadTemplate(key: string) {
    const csv = templates?.[key];
    if (!csv) return;
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `kynexone-${key}-template.csv`;
    a.click();
    URL.revokeObjectURL(url);
  }

  async function attachFile(key: string, file: File) {
    const text = await file.text();
    setFiles((prev) => ({ ...prev, [key]: text }));
    setFileNames((prev) => ({ ...prev, [key]: file.name }));
  }

  function detachFile(key: string) {
    setFiles((prev) => {
      const next = { ...prev };
      delete next[key];
      return next;
    });
    setFileNames((prev) => {
      const next = { ...prev };
      delete next[key];
      return next;
    });
    const input = fileInputs.current[key];
    if (input) input.value = '';
  }

  async function runPreview() {
    setBusy('preview');
    setError(null);
    setRefusals([]);
    try {
      const dto = await migrationsApi.preview(buildPackage(true));
      setPreview(dto);
      setRefusals(dto.lockedPeriodRefusals ?? []);
      setStep('preview');
    } catch (e) {
      setError(failMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function runCommit() {
    setBusy('commit');
    setError(null);
    setRefusals([]);
    const pkg = buildPackage(false);
    try {
      const dto = await migrationsApi.commit(pkg);
      setResult(dto);
      remember(dto, pkg);
      setStep('done');
    } catch (e) {
      const locked = lockedRefusals(e);
      if (locked.length > 0) setRefusals(locked);
      setError(failMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function runResume(run: StoredRun) {
    setBusy('resume');
    setError(null);
    try {
      const dto = await migrationsApi.resume(run.batchId, run.package);
      setResult(dto);
      remember(dto, run.package);
      setStep('done');
    } catch (e) {
      setError(failMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function refreshStatus(batchId: string) {
    setBusy('status');
    setError(null);
    try {
      const dto = await migrationsApi.status(batchId);
      setResult(dto);
      setStep('done');
    } catch (e) {
      setError(failMessage(e));
    } finally {
      setBusy(null);
    }
  }

  function startOver() {
    setStep('choose');
    setSelected([]);
    setFiles({});
    setFileNames({});
    setBatchRef('');
    setPreview(null);
    setResult(null);
    setRefusals([]);
    setError(null);
  }

  const startOverButton = step !== 'choose' ? (
    <button
      type="button"
      onClick={startOver}
      className="inline-flex items-center gap-2 rounded-lg border border-slate-300 px-3 py-2 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
    >
      <RotateCcw className="h-4 w-4" aria-hidden="true" />
      {t('Start over')}
    </button>
  ) : null;

  return (
    <div className="space-y-6">
      {embedded ? (
        <div className="flex flex-wrap items-start justify-between gap-3">
          <p className="max-w-3xl text-sm leading-6 text-slate-600 dark:text-slate-400">{OPENING_BALANCES_DESCRIPTION}</p>
          {startOverButton}
        </div>
      ) : (
        <SectionHeader
          eyebrow={t('Implementation')}
          title={t('Opening balances')}
          description={OPENING_BALANCES_DESCRIPTION}
          action={startOverButton}
        />
      )}

      <Stepper step={step} />

      {error ? <ErrorBanner message={error} onDismiss={() => setError(null)} /> : null}

      {refusals.length > 0 ? <LockedPeriodPanel refusals={refusals} /> : null}

      {step === 'choose' ? (
        <ChooseStep
          selected={selected}
          templates={templates}
          templatesError={templatesError}
          onToggle={toggleSection}
          onDownload={downloadTemplate}
          onNext={() => setStep('upload')}
        />
      ) : null}

      {step === 'upload' ? (
        <UploadStep
          chosen={chosen}
          fileNames={fileNames}
          batchRef={batchRef}
          busy={busy === 'preview'}
          canPreview={canPreview}
          fileInputs={fileInputs}
          templatesAvailable={Boolean(templates)}
          onBatchRef={setBatchRef}
          onAttach={attachFile}
          onDetach={detachFile}
          onDownload={downloadTemplate}
          onBack={() => setStep('choose')}
          onPreview={runPreview}
        />
      ) : null}

      {step === 'preview' && preview ? (
        <PreviewStep
          preview={preview}
          busy={busy === 'commit'}
          blocked={refusals.length > 0}
          onBack={() => setStep('upload')}
          onCommit={runCommit}
        />
      ) : null}

      {step === 'done' && result ? (
        <DoneStep result={result} onStartOver={startOver} onRefresh={() => refreshStatus(result.batchId)} busy={busy === 'status'} />
      ) : null}

      <HistoryPanel
        history={history}
        busy={busy}
        onResume={runResume}
        onStatus={refreshStatus}
      />
    </div>
  );
}

// ── Stepper ──────────────────────────────────────────────────────────────────

const STEP_KEYS: Step[] = ['choose', 'upload', 'preview', 'done'];

function Stepper({ step }: { step: Step }) {
  const t = useT();
  const labels: Record<Step, string> = { choose: t('Choose sections'), upload: t('Attach files'), preview: t('Reconcile'), done: t('Result') };
  const STEPS = STEP_KEYS.map((key) => ({ key, label: labels[key] }));
  const activeIndex = STEPS.findIndex((s) => s.key === step);
  return (
    <ol className="flex flex-wrap items-center gap-x-2 gap-y-2 text-xs font-semibold" aria-label={t('Import progress')}>
      {STEPS.map((s, i) => {
        const state = i < activeIndex ? 'done' : i === activeIndex ? 'current' : 'todo';
        return (
          <li key={s.key} className="flex items-center gap-2">
            <span
              aria-current={state === 'current' ? 'step' : undefined}
              className={[
                'inline-flex items-center gap-2 rounded-full px-3 py-1.5',
                state === 'current'
                  ? 'bg-sapphire text-white dark:bg-cyanAccent dark:text-slate-950'
                  : state === 'done'
                    ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300'
                    : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400',
              ].join(' ')}
            >
              {state === 'done' ? <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" /> : <span aria-hidden="true">{i + 1}</span>}
              {s.label}
            </span>
            {i < STEPS.length - 1 ? (
              <ArrowRight className="h-3.5 w-3.5 text-slate-300 dark:text-slate-600" aria-hidden="true" />
            ) : null}
          </li>
        );
      })}
    </ol>
  );
}

// ── Step 1: choose ───────────────────────────────────────────────────────────

function ChooseStep({
  selected, templates, templatesError, onToggle, onDownload, onNext,
}: {
  selected: string[];
  templates: Record<string, string> | null;
  templatesError: string | null;
  onToggle: (key: string) => void;
  onDownload: (key: string) => void;
  onNext: () => void;
}) {
  const t = useT();
  const cutoverChosen = selected.includes('companyCutover');
  const balanceChosen = selected.some((k) => MIGRATION_SECTIONS.find((s) => s.key === k)?.openingBalance);

  return (
    <DataPanel
      title={t('What are you loading?')}
      description={t('Pick one or more sections. They are applied in the order shown.')}
    >
      {templatesError ? (
        <p className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-800/60 dark:bg-amber-900/20 dark:text-amber-300">
          {t('Templates could not be loaded ({error}). You can still attach files, but check your column headings against the documentation.', { error: templatesError })}
        </p>
      ) : null}

      {balanceChosen && !cutoverChosen ? (
        <p className="mb-4 flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-800/60 dark:bg-amber-900/20 dark:text-amber-300">
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
          <span>
            {rich(t('You have chosen an opening-balance section without {section}. Loans, advances and end-of-service provisions are refused unless the employee’s legal entity has an active cutover — either add that section here, or import it on its own first.', { section: mark('section') }),
              { section: <strong>{t('Cutover dates')}</strong> })}
          </span>
        </p>
      ) : null}

      <ul className="space-y-2">
        {MIGRATION_SECTIONS.map((s) => {
          const isSelected = selected.includes(s.key);
          return (
            <li
              key={s.key}
              className={[
                'rounded-lg border p-3 transition-colors',
                isSelected
                  ? 'border-sapphire bg-sapphire/5 dark:border-cyanAccent dark:bg-cyanAccent/10'
                  : 'border-slate-200 hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800/60',
              ].join(' ')}
            >
              <div className="flex items-start gap-3">
                <input
                  id={`section-${s.key}`}
                  type="checkbox"
                  checked={isSelected}
                  onChange={() => onToggle(s.key)}
                  className="mt-1 h-4 w-4 shrink-0 rounded border-slate-300 text-sapphire focus:ring-sapphire dark:border-slate-600 dark:bg-slate-800"
                />
                <div className="min-w-0 flex-1">
                  <label htmlFor={`section-${s.key}`} className="flex flex-wrap items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                    {sectionOf(t, s).label}
                    {s.openingBalance ? (
                      <span className="rounded-full bg-indigo-50 px-2 py-0.5 text-caption font-semibold uppercase tracking-wide text-indigo-700 dark:bg-indigo-900/40 dark:text-indigo-300">
                        {t('Opening balance')}
                      </span>
                    ) : null}
                  </label>
                  <p className="mt-0.5 text-xs leading-5 text-slate-600 dark:text-slate-400">{sectionOf(t, s).description}</p>
                </div>
                <button
                  type="button"
                  onClick={() => onDownload(s.key)}
                  disabled={!templates?.[s.key]}
                  className="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-40 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                >
                  <Download className="h-3.5 w-3.5" aria-hidden="true" />
                  {t('Template')}
                </button>
              </div>
            </li>
          );
        })}
      </ul>

      <div className="mt-5 flex justify-end">
        <button
          type="button"
          onClick={onNext}
          disabled={selected.length === 0}
          className="inline-flex items-center gap-2 rounded-lg bg-sapphire px-4 py-2 text-sm font-semibold text-white transition-colors hover:bg-sapphire/90 disabled:cursor-not-allowed disabled:opacity-40 dark:bg-cyanAccent dark:text-slate-950 dark:hover:bg-cyanAccent/90"
        >
          {t('Attach files')}
          <ArrowRight className="h-4 w-4" aria-hidden="true" />
        </button>
      </div>
    </DataPanel>
  );
}

// ── Step 2: upload ───────────────────────────────────────────────────────────

function UploadStep({
  chosen, fileNames, batchRef, busy, canPreview, fileInputs, templatesAvailable,
  onBatchRef, onAttach, onDetach, onDownload, onBack, onPreview,
}: {
  chosen: SectionMeta[];
  fileNames: Record<string, string>;
  batchRef: string;
  busy: boolean;
  canPreview: boolean;
  fileInputs: React.MutableRefObject<Record<string, HTMLInputElement | null>>;
  templatesAvailable: boolean;
  onBatchRef: (v: string) => void;
  onAttach: (key: string, file: File) => void;
  onDetach: (key: string) => void;
  onDownload: (key: string) => void;
  onBack: () => void;
  onPreview: () => void;
}) {
  const t = useT();
  return (
    <DataPanel
      title={t('Attach a CSV for each section')}
      description={t('Files are read in the browser and sent as one package. Nothing is written yet.')}
    >
      <div className="mb-5">
        <label htmlFor="batch-ref" className="block text-xs font-semibold text-slate-700 dark:text-slate-300">
          {t('Batch reference')} <span className="font-normal text-slate-500 dark:text-slate-400">{t('(optional)')}</span>
        </label>
        <input
          id="batch-ref"
          type="text"
          value={batchRef}
          onChange={(e) => onBatchRef(e.target.value)}
          placeholder={t('Example: acme-wave1-september')}
          className="mt-1 w-full max-w-md rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-sapphire focus:outline-none focus:ring-1 focus:ring-sapphire dark:border-slate-700 dark:bg-slate-900 dark:text-white dark:placeholder:text-slate-500"
        />
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
          {t('Give the batch a name and a re-run under the same name resumes it rather than starting a second import. The package is checksummed, so the same name with a different file is refused.')}
        </p>
      </div>

      <ul className="space-y-2">
        {chosen.map((s) => {
          const name = fileNames[s.key];
          return (
            <li key={s.key} className="rounded-lg border border-slate-200 p-3 dark:border-slate-700">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="text-sm font-semibold text-slate-900 dark:text-white">{sectionOf(t, s).label}</p>
                  {name ? (
                    <p className="mt-0.5 flex items-center gap-1.5 text-xs text-emerald-700 dark:text-emerald-400">
                      <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
                      {name}
                    </p>
                  ) : (
                    <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{t('No file attached — this section will be skipped.')}</p>
                  )}
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  {templatesAvailable ? (
                    <button
                      type="button"
                      onClick={() => onDownload(s.key)}
                      className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition-colors hover:bg-slate-100 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                    >
                      <Download className="h-3.5 w-3.5" aria-hidden="true" />
                      {t('Template')}
                    </button>
                  ) : null}
                  <label className="inline-flex cursor-pointer items-center gap-1.5 rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition-colors hover:bg-slate-100 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800">
                    <Upload className="h-3.5 w-3.5" aria-hidden="true" />
                    {name ? t('Replace') : t('Attach CSV')}
                    <input
                      ref={(el) => {
                        fileInputs.current[s.key] = el;
                      }}
                      type="file"
                      accept=".csv,text/csv"
                      className="sr-only"
                      onChange={(e) => {
                        const f = e.target.files?.[0];
                        if (f) onAttach(s.key, f);
                      }}
                    />
                  </label>
                  {name ? (
                    <button
                      type="button"
                      onClick={() => onDetach(s.key)}
                      aria-label={t('Remove the file attached to {section}', { section: sectionOf(t, s).label })}
                      className="rounded-lg p-1.5 text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-800 dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-slate-200"
                    >
                      <X className="h-4 w-4" aria-hidden="true" />
                    </button>
                  ) : null}
                </div>
              </div>
            </li>
          );
        })}
      </ul>

      <div className="mt-5 flex flex-wrap justify-between gap-3">
        <button
          type="button"
          onClick={onBack}
          className="rounded-lg border border-slate-300 px-4 py-2 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
        >
          {t('Back')}
        </button>
        <button
          type="button"
          onClick={onPreview}
          disabled={!canPreview || busy}
          className="inline-flex items-center gap-2 rounded-lg bg-sapphire px-4 py-2 text-sm font-semibold text-white transition-colors hover:bg-sapphire/90 disabled:cursor-not-allowed disabled:opacity-40 dark:bg-cyanAccent dark:text-slate-950 dark:hover:bg-cyanAccent/90"
        >
          {busy ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <PlayCircle className="h-4 w-4" aria-hidden="true" />}
          {busy ? t('Reconciling…') : t('Preview and reconcile')}
        </button>
      </div>
    </DataPanel>
  );
}

// ── Step 3: preview / reconciliation ─────────────────────────────────────────

function PreviewStep({
  preview, busy, blocked, onBack, onCommit,
}: {
  preview: MigrationReconciliation;
  busy: boolean;
  blocked: boolean;
  onBack: () => void;
  onCommit: () => void;
}) {
  const t = useT();
  const fx = useFormat();
  const sectionNames = sectionText(t);
  const sections = Object.keys(preview.sectionCounts ?? {});
  const totals = preview.sectionTotals ?? {};
  const willReject = preview.errors?.length ?? 0;

  return (
    <div className="space-y-4">
      <DataPanel
        title={t('Reconciliation')}
        description={t('Tie the control total for each section to the bottom line of the report from the outgoing system before you commit. The total is what the FILE claims, over every row — including the rows listed below as rejections.')}
      >
        {sections.length === 0 ? (
          <p className="py-6 text-center text-sm text-slate-500 dark:text-slate-400">
            {t('The package contained no rows to reconcile.')}
          </p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[32rem] text-sm">
              <thead>
                <tr className="border-b border-slate-200 text-start text-xs uppercase tracking-wide text-slate-500 dark:border-slate-700 dark:text-slate-400">
                  <th scope="col" className="py-2 pe-4 font-semibold">{t('Section')}</th>
                  <th scope="col" className="py-2 pe-4 text-end font-semibold">{t('Rows')}</th>
                  <th scope="col" className="py-2 text-end font-semibold">{t('Control total')}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                {sections.map((key) => {
                  const meta = MIGRATION_SECTIONS.find((s) => s.key === key);
                  const total = totals[key] ?? 0;
                  return (
                    <tr key={key}>
                      <td className="py-2 pe-4 text-slate-800 dark:text-slate-200">{sectionNames[key]?.label ?? meta?.label ?? key}</td>
                      <td className="py-2 pe-4 text-end tabular-nums text-slate-800 dark:text-slate-200">{fx.integer(preview.sectionCounts[key])}</td>
                      <td className="py-2 text-end tabular-nums text-slate-800 dark:text-slate-200">
                        {total === 0 ? <span className="text-slate-400 dark:text-slate-500">—</span> : fx.number(total, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
              <tfoot>
                <tr className="border-t border-slate-200 font-semibold dark:border-slate-700">
                  <td className="py-2 pe-4 text-slate-900 dark:text-white">{t('Total received')}</td>
                  <td className="py-2 pe-4 text-end tabular-nums text-slate-900 dark:text-white">{fx.integer(preview.receivedRows)}</td>
                  <td className="py-2" />
                </tr>
              </tfoot>
            </table>
          </div>
        )}
      </DataPanel>

      <DataPanel
        title={willReject === 0 ? t('Nothing will be rejected') : t('{count, plural, one {# row will be rejected} other {# rows will be rejected}}', { count: willReject })}
        description={willReject === 0 ? undefined : t('Each is named with its file, line number and the reason. A rejected row leaves nothing behind — fix the file and re-import.')}
      >
        {willReject === 0 ? (
          <p className="flex items-center gap-2 py-2 text-sm text-emerald-700 dark:text-emerald-400">
            <CheckCircle2 className="h-4 w-4" aria-hidden="true" />
            {t('Every row validated cleanly.')}
          </p>
        ) : (
          <ul className="max-h-72 space-y-1.5 overflow-y-auto">
            {preview.errors.map((e, i) => (
              <li
                key={`${i}-${e.slice(0, 24)}`}
                className="flex items-start gap-2 rounded-lg bg-red-50 px-3 py-2 text-xs leading-5 text-red-800 dark:bg-red-900/20 dark:text-red-300"
              >
                <FileWarning className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                <span>{e}</span>
              </li>
            ))}
          </ul>
        )}
      </DataPanel>

      <div className="flex flex-wrap justify-between gap-3">
        <button
          type="button"
          onClick={onBack}
          className="rounded-lg border border-slate-300 px-4 py-2 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
        >
          {t('Back')}
        </button>
        <button
          type="button"
          onClick={onCommit}
          disabled={busy || blocked}
          title={blocked ? t('A locked payroll period is blocking this import.') : undefined}
          className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white transition-colors hover:bg-emerald-700 disabled:cursor-not-allowed disabled:opacity-40"
        >
          {busy ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <CheckCircle2 className="h-4 w-4" aria-hidden="true" />}
          {busy ? t('Importing…') : t('Commit import')}
        </button>
      </div>
    </div>
  );
}

// ── Step 4: result ───────────────────────────────────────────────────────────

function DoneStep({
  result, onStartOver, onRefresh, busy,
}: {
  result: MigrationReconciliation;
  onStartOver: () => void;
  onRefresh: () => void;
  busy: boolean;
}) {
  const t = useT();
  const fx = useFormat();
  const failed = result.status === 'Failed';
  return (
    <DataPanel
      title={failed ? t('The import did not finish') : t('Import complete')}
      description={
        failed
          ? t('Sections that had already been applied are committed and safe. Resume it from the history below — every row is an idempotent upsert, so a resume re-runs the package without applying anything twice.')
          : undefined
      }
      action={
        <button
          type="button"
          onClick={onRefresh}
          disabled={busy}
          className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition-colors hover:bg-slate-100 disabled:opacity-40 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
        >
          {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <RotateCcw className="h-3.5 w-3.5" aria-hidden="true" />}
          {t('Refresh')}
        </button>
      }
    >
      <dl className="grid grid-cols-2 gap-3 sm:grid-cols-5">
        <Stat label={t('Status')} value={enumLabel(t, 'Status', result.status)} tone={failed ? 'bad' : 'good'} />
        <Stat label={t('Received')} value={fx.integer(result.receivedRows)} />
        <Stat label={t('Created')} value={fx.integer(result.createdRows)} tone="good" />
        <Stat label={t('Updated')} value={fx.integer(result.updatedRows)} />
        <Stat label={t('Rejected')} value={fx.integer(result.errorRows)} tone={result.errorRows > 0 ? 'bad' : undefined} />
      </dl>

      <p className="mt-4 break-all text-xs text-slate-500 dark:text-slate-400">
        {rich(t('Batch {batch} · checksum {checksum}', { batch: mark('batch'), checksum: mark('checksum') }), {
          batch: <span className="font-mono">{result.batchId}</span>,
          checksum: <span className="font-mono">{result.packageChecksum.slice(0, 16)}…</span>,
        })}
        {result.currentSection ? <> · {rich(t('Stopped in {section}', { section: mark('section') }), { section: <strong>{sectionText(t)[result.currentSection]?.label ?? result.currentSection}</strong> })}</> : null}
      </p>

      {result.errors?.length > 0 ? (
        <ul className="mt-4 max-h-60 space-y-1.5 overflow-y-auto">
          {result.errors.map((e, i) => (
            <li
              key={`${i}-${e.slice(0, 24)}`}
              className="rounded-lg bg-red-50 px-3 py-2 text-xs leading-5 text-red-800 dark:bg-red-900/20 dark:text-red-300"
            >
              {e}
            </li>
          ))}
        </ul>
      ) : null}

      <div className="mt-5">
        <button
          type="button"
          onClick={onStartOver}
          className="rounded-lg bg-sapphire px-4 py-2 text-sm font-semibold text-white transition-colors hover:bg-sapphire/90 dark:bg-cyanAccent dark:text-slate-950 dark:hover:bg-cyanAccent/90"
        >
          {t('Import another section')}
        </button>
      </div>
    </DataPanel>
  );
}

function Stat({ label, value, tone }: { label: string; value: string; tone?: 'good' | 'bad' }) {
  const toneClass =
    tone === 'good'
      ? 'text-emerald-700 dark:text-emerald-400'
      : tone === 'bad'
        ? 'text-red-700 dark:text-red-400'
        : 'text-slate-900 dark:text-white';
  return (
    <div className="rounded-lg bg-slate-50 px-3 py-2 dark:bg-slate-800/60">
      <dt className="text-caption font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</dt>
      <dd className={`mt-0.5 text-lg font-semibold tabular-nums ${toneClass}`}>{value}</dd>
    </div>
  );
}

// ── Locked-period refusal ────────────────────────────────────────────────────

function LockedPeriodPanel({ refusals }: { refusals: LockedPeriodRefusal[] }) {
  const t = useT();
  const fx = useFormat();
  return (
    <div
      role="alert"
      className="rounded-lg border border-amber-300 bg-amber-50 p-4 dark:border-amber-800/60 dark:bg-amber-900/20"
    >
      <p className="flex items-center gap-2 text-sm font-semibold text-amber-900 dark:text-amber-300">
        <Lock className="h-4 w-4 shrink-0" aria-hidden="true" />
        {t('A locked payroll period is blocking this import')}
      </p>
      <p className="mt-1 text-xs leading-5 text-amber-800 dark:text-amber-300/90">
        {t('Opening balances may not restate a period that has already been paid and journalled.')}
      </p>
      <ul className="mt-3 space-y-2">
        {refusals.map((r) => (
          <li key={r.payrollRunId} className="rounded-lg bg-white/70 px-3 py-2 text-xs leading-5 text-amber-900 dark:bg-slate-900/40 dark:text-amber-200">
            <p className="font-semibold">
              {t('{company} · {period} · cutover {date}', { company: r.companyName, period: r.period, date: fx.date(r.cutoverDate) })}
            </p>
            <p className="mt-0.5">{r.reason}</p>
          </li>
        ))}
      </ul>
    </div>
  );
}

// ── History ──────────────────────────────────────────────────────────────────

function HistoryPanel({
  history, busy, onResume, onStatus,
}: {
  history: StoredRun[];
  busy: null | string;
  onResume: (run: StoredRun) => void;
  onStatus: (batchId: string) => void;
}) {
  const t = useT();
  const fx = useFormat();
  return (
    <DataPanel
      title={t('Recent imports')}
      description={t('Kept in this browser so a failed import can be resumed after a reload. The package travels with it, because the engine checksums it and refuses a different file.')}
    >
      {history.length === 0 ? (
        <p className="flex items-center gap-2 py-6 text-center text-sm text-slate-500 dark:text-slate-400">
          <History className="h-4 w-4 shrink-0" aria-hidden="true" />
          {t('No imports have been run from this browser yet.')}
        </p>
      ) : (
        <ul className="divide-y divide-slate-100 dark:divide-slate-800">
          {history.map((run) => {
            const failed = run.status === 'Failed' || run.errorRows > 0;
            return (
              <li key={run.batchId} className="flex flex-wrap items-center justify-between gap-3 py-3">
                <div className="min-w-0">
                  <p className="flex flex-wrap items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                    <span
                      className={[
                        'rounded-full px-2 py-0.5 text-caption font-semibold uppercase tracking-wide',
                        failed
                          ? 'bg-red-50 text-red-700 dark:bg-red-900/30 dark:text-red-300'
                          : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300',
                      ].join(' ')}
                    >
                      <EnumLabel enum="Status" value={run.status} />
                    </span>
                    {t('{sections, plural, one {# section} other {# sections}} · {rows, plural, one {# row} other {# rows}}', { sections: run.sections.length, rows: run.receivedRows })}
                  </p>
                  <p className="mt-0.5 break-all font-mono text-xs text-slate-500 dark:text-slate-400">
                    {run.batchId} · {fx.dateTime(run.at)}
                  </p>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <button
                    type="button"
                    onClick={() => onStatus(run.batchId)}
                    disabled={busy !== null}
                    className="rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition-colors hover:bg-slate-100 disabled:opacity-40 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                  >
                    {t('Status')}
                  </button>
                  {failed ? (
                    <button
                      type="button"
                      onClick={() => onResume(run)}
                      disabled={busy !== null}
                      className="inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-2.5 py-1.5 text-xs font-semibold text-white transition-colors hover:bg-sapphire/90 disabled:opacity-40 dark:bg-cyanAccent dark:text-slate-950 dark:hover:bg-cyanAccent/90"
                    >
                      {busy === 'resume' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <RotateCcw className="h-3.5 w-3.5" aria-hidden="true" />}
                      {t('Resume')}
                    </button>
                  ) : null}
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </DataPanel>
  );
}
