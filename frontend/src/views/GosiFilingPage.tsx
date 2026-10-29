'use client';

import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import {
  AlertTriangle, CheckCircle2, ChevronDown, ChevronRight, FileSpreadsheet, Info, RefreshCw, ShieldCheck, XCircle,
} from 'lucide-react';
import {
  gosiApi, type GosiComponentBreakdown, type GosiPeriodSummary, type GosiRunSummary, type GosiVarianceReport,
} from '@/src/api/gosi';
import { payrollApi, type PayrollRun } from '@/src/api/payroll';
import { useCompany } from '@/src/contexts/CompanyContext';
import { useT } from '@/src/hooks/useT';
import { useFormat } from '@/src/hooks/useFormat';
import { enumLabel } from '@/src/i18n/enumLabel';
import type { Formatter } from '@/src/lib/format';

// ── helpers ─────────────────────────────────────────────────────────────────────

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const HALALA = 0.01;

/** Two-decimal amounts in the viewer's locale: the filing view always shows halalas. */
function makeMoney(fx: Formatter) {
  const money = (n: number | null | undefined) =>
    n === null || n === undefined ? '—' : fx.number(n, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const signed = (n: number | null | undefined) =>
    n === null || n === undefined ? '—' : `${n > 0 ? '+' : ''}${money(n)}`;
  return { money, signed };
}

const isZero = (n: number | null | undefined) => n !== null && n !== undefined && Math.abs(n) < HALALA;

const errorText = (e: unknown, what: 'period' | 'run', t: (k: string) => string) => {
  const status = (e as { response?: { status?: number } })?.response?.status;
  if (what === 'period') {
    if (status === 403) return t('Your role cannot read GOSI period summary. GOSI filing needs Admin, HR Manager, Payroll Manager or Payroll Officer.');
    if (status === 404) return t('The GOSI period summary was not found.');
    return t('Could not load the GOSI period summary.');
  }
  if (status === 403) return t('Your role cannot read payroll run reconciliation. GOSI filing needs Admin, HR Manager, Payroll Manager or Payroll Officer.');
  if (status === 404) return t('The payroll run reconciliation was not found.');
  return t('Could not load the payroll run reconciliation.');
};

type Scope = { kind: 'period' } | { kind: 'run'; runId: string };

// ── page ────────────────────────────────────────────────────────────────────────

/**
 * GOSI filing & variance: read-only view over the three reconciliation endpoints.
 * Period mode is the filing source (every non-voided run unioned, ceiling applied once);
 * run mode drills into one run's tie-out and per-employee variance with expected lines.
 */
export function GosiFilingPage() {
  const t = useT();
  const fx = useFormat();
  const { companies, selectedCompanyId, companyVersion } = useCompany();
  const now = new Date();
  const [year, setYear] = useState(now.getFullYear());
  const [month, setMonth] = useState(now.getMonth() + 1);
  const [companyId, setCompanyId] = useState<string>('');
  const [scope, setScope] = useState<Scope>({ kind: 'period' });

  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runsLoaded, setRunsLoaded] = useState(false);

  const [period, setPeriod] = useState<GosiPeriodSummary | null>(null);
  const [runSummary, setRunSummary] = useState<GosiRunSummary | null>(null);
  const [variance, setVariance] = useState<GosiVarianceReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // Once the user picks a period, the "open on the latest run" default must never override it.
  const userPicked = useRef(false);
  // Only the newest request may write state; an older, slower response must not overwrite it.
  const requestSeq = useRef(0);
  const pickYear = (y: number) => { userPicked.current = true; setYear(y); };
  const pickMonth = (m: number) => { userPicked.current = true; setMonth(m); };

  // Company default: the switcher's selection, else the first accessible company. '' = all companies.
  useEffect(() => {
    if (!companyId && selectedCompanyId) setCompanyId(selectedCompanyId);
  }, [selectedCompanyId, companyId]);

  // Recent payroll periods: used to open on the latest period that actually has a run, and as quick-picks.
  useEffect(() => {
    let cancelled = false;
    payrollApi.listRuns({ page: 1, pageSize: 50 })
      .then((res) => {
        if (cancelled) return;
        const list = (res.items ?? []).filter((r) => r.status !== 'Voided');
        setRuns(list);
        const latest = [...list].sort((a, b) => b.year - a.year || b.month - a.month)[0];
        if (latest && !userPicked.current) { setYear(latest.year); setMonth(latest.month); }
      })
      .catch(() => { /* quick-picks are optional; the period picker still works */ })
      .finally(() => { if (!cancelled) setRunsLoaded(true); });
    return () => { cancelled = true; };
  }, [companyVersion]);

  const loadPeriod = useCallback(async () => {
    const seq = ++requestSeq.current;
    setLoading(true);
    setError(null);
    try {
      const data = await gosiApi.periodSummary(year, month, companyId || null);
      if (seq === requestSeq.current) setPeriod(data);
    } catch (e) {
      if (seq !== requestSeq.current) return;
      setPeriod(null);
      setError(errorText(e, 'period', t));
    } finally {
      if (seq === requestSeq.current) setLoading(false);
    }
  }, [year, month, companyId, t]);

  const loadRun = useCallback(async (runId: string) => {
    const seq = ++requestSeq.current;
    setLoading(true);
    setError(null);
    try {
      const [s, v] = await Promise.all([gosiApi.runSummary(runId), gosiApi.runVariance(runId)]);
      if (seq !== requestSeq.current) return;
      setRunSummary(s);
      setVariance(v);
    } catch (e) {
      if (seq !== requestSeq.current) return;
      setRunSummary(null);
      setVariance(null);
      setError(errorText(e, 'run', t));
    } finally {
      if (seq === requestSeq.current) setLoading(false);
    }
  }, [t]);

  // Wait for the run list so the first request is for the latest real period, not "this month".
  useEffect(() => {
    if (!runsLoaded) return;
    void loadPeriod();
  }, [runsLoaded, loadPeriod, companyVersion]);

  useEffect(() => { setScope({ kind: 'period' }); }, [year, month, companyId]);
  useEffect(() => { if (scope.kind === 'run') void loadRun(scope.runId); }, [scope, loadRun]);

  const periodChips = useMemo(() => {
    const seen = new Map<string, { year: number; month: number }>();
    for (const r of runs) {
      if (companyId && r.companyId && r.companyId !== companyId) continue;
      const key = `${r.year}-${String(r.month).padStart(2, '0')}`;
      if (!seen.has(key)) seen.set(key, { year: r.year, month: r.month });
    }
    return [...seen.entries()].sort((a, b) => b[0].localeCompare(a[0])).slice(0, 6);
  }, [runs, companyId]);

  const years = useMemo(() => {
    const ys = new Set<number>([now.getFullYear(), now.getFullYear() - 1, now.getFullYear() - 2, year]);
    runs.forEach((r) => ys.add(r.year));
    return [...ys].sort((a, b) => b - a);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runs, year]);

  const monthName = (m: number) => fx.period(MONTHS[m - 1], 'long');
  const periodLabel = (m: number, y: number) => fx.period(`${MONTHS[m - 1]} ${y}`, 'long');
  const reload = () => (scope.kind === 'run' ? void loadRun(scope.runId) : void loadPeriod());
  const tie = scope.kind === 'run' ? runSummary : period;

  return (
    <div className="space-y-4">
      {/* Header + picker */}
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-lg font-semibold text-slate-800 dark:text-slate-100">{t('GOSI Filing & Variance')}</h1>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('Deducted vs recomputed vs posted to the ledger, per employee, per period. Read-only.')}
          </p>
        </div>
        <div className="flex flex-wrap items-end gap-2">
          <PickerSelect label={t('Month')} value={String(month)} onChange={(v) => pickMonth(Number(v))} ariaLabel={t('Month')}>
            {MONTHS.map((m, i) => <option key={m} value={i + 1}>{monthName(i + 1)}</option>)}
          </PickerSelect>
          <PickerSelect label={t('Year')} value={String(year)} onChange={(v) => pickYear(Number(v))} ariaLabel={t('Year')}>
            {years.map((y) => <option key={y} value={y}>{fx.number(y, { useGrouping: false })}</option>)}
          </PickerSelect>
          <PickerSelect label={t('Company')} value={companyId} onChange={setCompanyId} ariaLabel={t('Company')}>
            {companies.length !== 1 && <option value="">{t('All companies')}</option>}
            {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </PickerSelect>
          <button type="button" onClick={reload}
            className="flex h-[34px] items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 text-xs font-semibold text-slate-600 hover:bg-slate-50 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-300 dark:hover:bg-white/[0.08]">
            <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" /> {t('Refresh')}
          </button>
        </div>
      </div>

      {periodChips.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5 text-caption">
          <span className="font-medium text-slate-500 dark:text-slate-400">{t('Periods with payroll runs')}</span>
          {periodChips.map(([key, p]) => {
            const active = p.year === year && p.month === month;
            return (
              <button key={key} type="button" onClick={() => { pickYear(p.year); pickMonth(p.month); }}
                className={`rounded-full px-2.5 py-0.5 font-semibold transition ${active
                  ? 'bg-sapphire text-white'
                  : 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-white/[0.06] dark:text-slate-300 dark:hover:bg-white/[0.1]'}`}>
                {fx.period(`${MONTHS[p.month - 1]} ${p.year}`, 'short')}
              </button>
            );
          })}
        </div>
      )}

      <div className="flex items-start gap-2 rounded-xl border border-blue-200 bg-blue-50 px-4 py-2.5 text-xs font-medium text-blue-800 dark:border-blue-500/20 dark:bg-blue-500/[0.06] dark:text-blue-300">
        <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" />
        <span>
          {t('Employee GOSI readiness (missing IDs, classification, blocked employees) lives on {link}. This screen reconciles what payroll actually filed.', {
            link: '\u0000',
          }).split('\u0000').flatMap((part, i, all) => i < all.length - 1
            ? [part, <Link key={i} href="/saudi-compliance" className="font-semibold underline underline-offset-2">{t('Saudi Compliance')}</Link>]
            : [part])}
        </span>
      </div>

      {loading ? (
        <div className="space-y-3" aria-busy="true" aria-label={t('Loading GOSI reconciliation')}>
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            {[0, 1, 2, 3].map((i) => <div key={i} className="h-24 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" />)}
          </div>
          <div className="h-64 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" />
        </div>
      ) : error ? (
        <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <AlertTriangle className="h-8 w-8 text-amber-500" />
          <p className="text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={reload} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" /> {t('Retry')}
          </button>
        </div>
      ) : period && !period.hasStatutoryData && scope.kind === 'period' ? (
        <NoStatutoryData periodLabel={t('No statutory lines recorded for {period}', { period: periodLabel(month, year) })} runCount={period.runCount} packStatusNote={period.packStatusNote} />
      ) : tie ? (
        <>
          {period && period.runs.length > 0 && (
            <ScopeBar period={period} scope={scope} onChange={setScope} />
          )}

          {scope.kind === 'run' && runSummary && !runSummary.hasStatutoryData ? (
            <NoStatutoryData periodLabel={t('No statutory lines recorded for this run ({period})', { period: runSummary.period })} runCount={1} packStatusNote={runSummary.packStatusNote} />
          ) : (
            <>
              <Notes
                packResolved={tie.packResolved}
                packStatusNote={tie.packStatusNote}
                periodScopeNote={scope.kind === 'run' ? runSummary?.periodScopeNote ?? null : null}
                expectedIsPeriodPartial={scope.kind === 'run' ? runSummary?.expectedIsPeriodPartial ?? false : false}
                runCount={scope.kind === 'period' ? period?.runCount ?? 0 : 1}
              />
              <HeaderTiles tie={tie} />
              <TieOut tie={tie} run={scope.kind === 'run' ? runSummary : null} />
              <ComponentBreakdown rows={tie.branchBreakdown} />
              {scope.kind === 'period' && period ? (
                <PeriodEmployeeTable period={period} />
              ) : variance ? (
                <RunVarianceTable report={variance} />
              ) : null}
            </>
          )}
        </>
      ) : null}
    </div>
  );
}

// ── pieces ──────────────────────────────────────────────────────────────────────

function PickerSelect({ label, value, onChange, ariaLabel, children }: {
  label: string; value: string; onChange: (v: string) => void; ariaLabel: string; children: React.ReactNode;
}) {
  return (
    <label className="flex flex-col text-caption font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
      {label}
      <select aria-label={ariaLabel} value={value} onChange={(e) => onChange(e.target.value)}
        className="mt-0.5 h-[34px] rounded-lg border border-slate-200 bg-white px-2.5 text-xs font-semibold normal-case tracking-normal text-slate-700 dark:border-white/[0.08] dark:bg-surface dark:text-slate-200">
        {children}
      </select>
    </label>
  );
}

function NoStatutoryData({ periodLabel, runCount, packStatusNote }: { periodLabel: string; runCount: number; packStatusNote: string | null }) {
  const t = useT();
  return (
    <div data-testid="gosi-no-statutory" className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 bg-white p-10 text-center dark:border-white/[0.06] dark:bg-white/[0.03]">
      <FileSpreadsheet className="h-10 w-10 text-slate-300 dark:text-slate-600" />
      <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">{periodLabel}</p>
      <p className="max-w-lg text-xs text-slate-500 dark:text-slate-400">
        {runCount === 0
          ? t('There is no processed payroll run for this period and company, so there is nothing to file. Pick a period from the quick-picks above, or process payroll for this month first.')
          : t('{count, plural, one {# payroll run exists} other {# payroll runs exist}} for this period, but none carries statutory GOSI deduction lines. Reprocess the run, or check the employees\' GOSI classification on Saudi Compliance.', { count: runCount })}
      </p>
      {packStatusNote && <p className="max-w-lg text-caption text-slate-400">{packStatusNote}</p>}
      <Link href="/saudi-compliance" className="text-xs font-semibold text-sapphire hover:underline">{t('Open Saudi Compliance')}</Link>
    </div>
  );
}

function ScopeBar({ period, scope, onChange }: { period: GosiPeriodSummary; scope: Scope; onChange: (s: Scope) => void }) {
  const t = useT();
  const fx = useFormat();
  const { money } = makeMoney(fx);
  const base = 'rounded-lg px-3 py-1.5 text-xs font-semibold transition';
  const on = 'bg-slate-800 text-white dark:bg-white dark:text-slate-900';
  const off = 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/[0.06]';
  return (
    <div className="flex flex-wrap items-center gap-1 rounded-xl border border-slate-200 bg-white p-1 dark:border-white/[0.06] dark:bg-white/[0.03]" role="tablist" aria-label={t('Reconciliation scope')}>
      <button type="button" role="tab" aria-selected={scope.kind === 'period'} onClick={() => onChange({ kind: 'period' })}
        className={`${base} ${scope.kind === 'period' ? on : off}`}>
        {t('Whole period · filing view ({count, plural, one {# run} other {# runs}})', { count: period.runCount })}
      </button>
      {period.runs.map((r) => (
        <button key={r.runId} type="button" role="tab" aria-selected={scope.kind === 'run' && scope.runId === r.runId}
          onClick={() => onChange({ kind: 'run', runId: r.runId })}
          className={`${base} ${scope.kind === 'run' && scope.runId === r.runId ? on : off}`}>
          {t('{type} run · {status} · EE {ee} / ER {er}', {
            type: enumLabel(t, 'Status', r.runType), status: enumLabel(t, 'PayrollRunStatus', r.status),
            ee: money(r.employeeTotal), er: money(r.employerTotal),
          })}
        </button>
      ))}
    </div>
  );
}

function Notes({ packResolved, packStatusNote, periodScopeNote, expectedIsPeriodPartial, runCount }: {
  packResolved: boolean; packStatusNote: string | null; periodScopeNote: string | null; expectedIsPeriodPartial: boolean; runCount: number;
}) {
  const t = useT();
  const items: { tone: 'amber' | 'blue'; title: string; body: string; testId: string }[] = [];
  if (packStatusNote) items.push({
    tone: packResolved ? 'blue' : 'amber',
    title: packResolved ? t('Country pack note') : t('Country pack not resolved: expected figures are indicative'),
    body: packStatusNote, testId: 'gosi-pack-note',
  });
  if (expectedIsPeriodPartial) items.push({
    tone: 'amber',
    title: t('This run is part of a multi-run period. A variance here is arithmetic, not a defect.'),
    body: periodScopeNote ?? t('Other runs share this period. File from the whole-period view.'), testId: 'gosi-period-scope-note',
  });
  else if (runCount > 1) items.push({
    tone: 'blue',
    title: t('{count} runs are unioned for this period', { count: runCount }),
    body: t('Expected is recomputed once on the period-aggregated covered wage, so the statutory ceiling is applied exactly once. This is the figure to file.'),
    testId: 'gosi-multi-run-note',
  });
  if (items.length === 0) return null;
  return (
    <div className="space-y-2">
      {items.map((n) => (
        <div key={n.testId} data-testid={n.testId}
          className={`flex items-start gap-2 rounded-xl border px-4 py-2.5 text-xs ${n.tone === 'amber'
            ? 'border-amber-200 bg-amber-50 text-amber-900 dark:border-amber-500/20 dark:bg-amber-500/[0.06] dark:text-amber-200'
            : 'border-blue-200 bg-blue-50 text-blue-900 dark:border-blue-500/20 dark:bg-blue-500/[0.06] dark:text-blue-200'}`}>
          <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" />
          <div><p className="font-semibold">{n.title}</p><p className="mt-0.5 opacity-90">{n.body}</p></div>
        </div>
      ))}
    </div>
  );
}

type Tie = GosiPeriodSummary | GosiRunSummary;

function HeaderTiles({ tie }: { tie: Tie }) {
  const t = useT();
  const fx = useFormat();
  const { money, signed } = makeMoney(fx);
  const expDelta = tie.expectedVsActualEmployeeDelta + tie.expectedVsActualEmployerDelta;
  const glDelta = tie.glPosted ? (tie.glEmployeeDelta ?? 0) + (tie.glEmployerDelta ?? 0) : null;
  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
      <Tile label={t('Employee contributions')} value={money(tie.totalEmployeeContrib)} sub={t('Deducted from pay')} testId="tile-employee" />
      <Tile label={t('Employer contributions')} value={money(tie.totalEmployerContrib)} sub={t('Employer cost')} testId="tile-employer" />
      <Tile label={t('Expected vs actual')} value={signed(expDelta)} testId="tile-expected-delta"
        tone={isZero(expDelta) ? 'ok' : 'warn'}
        sub={isZero(expDelta) ? t('Recomputation matches to the halala') : t('Actual minus recomputed')} />
      <Tile label={t('Ledger vs actual')} value={glDelta === null ? t('Not posted') : signed(glDelta)} testId="tile-gl-delta"
        tone={glDelta === null ? 'muted' : isZero(glDelta) ? 'ok' : 'warn'}
        sub={glDelta === null ? t('No GL posting yet (run not locked)') : isZero(glDelta) ? t('GL matches deductions to the halala') : t('Actual minus GL liability')} />
    </div>
  );
}

function Tile({ label, value, sub, tone = 'neutral', testId }: {
  label: string; value: string; sub: string; tone?: 'neutral' | 'ok' | 'warn' | 'muted'; testId: string;
}) {
  const toneCls = {
    neutral: 'border-slate-200 bg-white dark:border-white/[0.06] dark:bg-white/[0.03]',
    ok: 'border-emerald-200 bg-emerald-50 dark:border-emerald-500/20 dark:bg-emerald-500/[0.06]',
    warn: 'border-rose-200 bg-rose-50 dark:border-rose-500/20 dark:bg-rose-500/[0.06]',
    muted: 'border-slate-200 bg-slate-50 dark:border-white/[0.06] dark:bg-white/[0.02]',
  }[tone];
  const valueCls = {
    neutral: 'text-slate-800 dark:text-slate-100',
    ok: 'text-emerald-700 dark:text-emerald-300',
    warn: 'text-rose-700 dark:text-rose-300',
    muted: 'text-slate-500 dark:text-slate-400',
  }[tone];
  return (
    <div data-testid={testId} className={`rounded-2xl border p-4 ${toneCls}`}>
      <p className="text-caption font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</p>
      <p className={`mt-1 font-mono text-xl font-semibold tabular-nums ${valueCls}`}>{value}</p>
      <p className="mt-0.5 text-caption text-slate-500 dark:text-slate-400">{sub}</p>
    </div>
  );
}

/** The differentiator: Deducted = Recomputed = Slip = GL, per side, with the delta at every hop. */
function TieOut({ tie, run }: { tie: Tie; run: GosiRunSummary | null }) {
  const t = useT();
  const sides = [
    {
      side: t('Employee'), actual: tie.totalEmployeeContrib, expected: tie.expectedEmployeeContrib,
      slip: run ? run.slipEmployeeStatutoryTotal : null, gl: tie.glEmployeeLiability,
      account: run?.glEmployeeAccount ?? '2101', glDelta: tie.glEmployeeDelta,
    },
    {
      side: t('Employer'), actual: tie.totalEmployerContrib, expected: tie.expectedEmployerContrib,
      slip: run ? run.slipEmployerStatutoryTotal : null, gl: tie.glEmployerLiability,
      account: run?.glEmployerAccount ?? '2106', glDelta: tie.glEmployerDelta,
    },
  ];
  const allTie = sides.every((s) => isZero(s.actual - s.expected) && (s.slip === null || isZero(s.actual - s.slip)) && tie.glPosted && isZero(s.glDelta));
  return (
    <section data-testid="gosi-tieout" className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Tie-out')}</h2>
        <span data-testid="gosi-tieout-verdict"
          className={`flex items-center gap-1 rounded-full px-2.5 py-0.5 text-caption font-semibold ${allTie
            ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300'
            : 'bg-amber-100 text-amber-800 dark:bg-amber-500/[0.12] dark:text-amber-300'}`}>
          {allTie ? <><CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Ties out to the halala')}</> : <><AlertTriangle className="h-3.5 w-3.5" aria-hidden="true" /> {t('Differences to review')}</>}
        </span>
      </div>
      <div className="space-y-2">
        {sides.map((s) => (
          <div key={s.side} className="flex flex-wrap items-stretch gap-1.5 text-xs">
            <span className="flex w-20 items-center font-semibold text-slate-600 dark:text-slate-300">{s.side}</span>
            <Hop label={t('Deducted')} value={s.actual} />
            <Eq ok={isZero(s.actual - s.expected)} />
            <Hop label={t('Recomputed')} value={s.expected} />
            {s.slip !== null && (<><Eq ok={isZero(s.actual - s.slip)} /><Hop label={t('Payslips')} value={s.slip} /></>)}
            <Eq ok={tie.glPosted && isZero(s.glDelta)} muted={!tie.glPosted} />
            <Hop label={t('GL {account}', { account: s.account })} value={s.gl} muted={!tie.glPosted} />
          </div>
        ))}
      </div>
    </section>
  );
}

function Hop({ label, value, muted = false }: { label: string; value: number | null; muted?: boolean }) {
  const t = useT();
  const { money } = makeMoney(useFormat());
  return (
    <div className={`min-w-[112px] rounded-lg border px-2.5 py-1.5 ${muted
      ? 'border-dashed border-slate-200 text-slate-400 dark:border-white/[0.08]'
      : 'border-slate-200 bg-slate-50 dark:border-white/[0.06] dark:bg-white/[0.03]'}`}>
      <p className="text-caption font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</p>
      <p className="font-mono font-semibold tabular-nums text-slate-800 dark:text-slate-100">{value === null ? t('Not posted') : money(value)}</p>
    </div>
  );
}

function Eq({ ok, muted = false }: { ok: boolean; muted?: boolean }) {
  const t = useT();
  return (
    <span className={`flex items-center px-0.5 text-base font-semibold ${muted ? 'text-slate-300 dark:text-slate-600' : ok ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}`}
      aria-label={muted ? t('Not posted') : ok ? t('Equal') : t('Not equal')}>
      {muted ? '·' : ok ? '=' : '≠'}
    </span>
  );
}

function ComponentBreakdown({ rows }: { rows: GosiComponentBreakdown[] }) {
  const t = useT();
  const fx = useFormat();
  const { money } = makeMoney(fx);
  return (
    <section className="overflow-x-auto rounded-2xl border border-slate-200/80 bg-white dark:border-white/[0.06] dark:bg-white/[0.03]">
      <h2 className="px-4 pt-3 text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Component breakdown')}</h2>
      <table className="mt-2 w-full min-w-[560px] text-start text-sm">
        <thead>
          <tr className="border-b border-slate-100 text-caption uppercase tracking-wide text-slate-500 dark:border-white/[0.06] dark:text-slate-400">
            <th className="px-4 py-2 font-semibold">{t('Component')}</th>
            <th className="px-4 py-2 font-semibold">{t('Paid by')}</th>
            <th className="px-4 py-2 text-end font-semibold">{t('Employees')}</th>
            <th className="px-4 py-2 text-end font-semibold">{t('Amount')}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.componentCode} className="border-b border-slate-50 last:border-0 dark:border-white/[0.03]">
              <td className="px-4 py-2">
                <span className="font-medium text-slate-800 dark:text-slate-100">{r.componentName || r.componentCode}</span>
                <span className="ms-2 font-mono text-caption text-slate-400">{r.componentCode}</span>
              </td>
              <td className="px-4 py-2">
                <span className={`rounded-full px-2 py-0.5 text-caption font-semibold ${r.isEmployerContribution
                  ? 'bg-violet-100 text-violet-700 dark:bg-violet-500/[0.12] dark:text-violet-300'
                  : 'bg-sky-100 text-sky-700 dark:bg-sky-500/[0.12] dark:text-sky-300'}`}>
                  {r.isEmployerContribution ? t('Employer') : t('Employee')}
                </span>
              </td>
              <td className="px-4 py-2 text-end tabular-nums text-slate-600 dark:text-slate-300">{fx.integer(r.employeeCount)}</td>
              <td className="px-4 py-2 text-end font-mono font-semibold tabular-nums text-slate-800 dark:text-slate-100">{money(r.totalAmount)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function VarianceCell({ value }: { value: number }) {
  const { money, signed } = makeMoney(useFormat());
  const ok = isZero(value);
  return (
    <td className={`px-3 py-2 text-end font-mono tabular-nums ${ok ? 'text-slate-400 dark:text-slate-500' : 'font-semibold text-rose-700 dark:text-rose-300'}`}>
      {ok ? money(0) : signed(value)}
    </td>
  );
}

function VarianceHeader({ title, count, total }: { title: string; count: number; total: number }) {
  const t = useT();
  return (
    <div className="flex flex-wrap items-center justify-between gap-2 px-4 pt-3">
      <h2 className="text-sm font-semibold text-slate-800 dark:text-slate-100">{title}</h2>
      <span data-testid="gosi-variance-count"
        className={`flex items-center gap-1 rounded-full px-2.5 py-0.5 text-caption font-semibold ${count === 0
          ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300'
          : 'bg-rose-100 text-rose-700 dark:bg-rose-500/[0.12] dark:text-rose-300'}`}>
        {count === 0 ? <ShieldCheck className="h-3.5 w-3.5" /> : <XCircle className="h-3.5 w-3.5" />}
        {count === 0 ? t('All {total} employees reconcile', { total }) : t('{count} of {total} employees with a variance', { count, total })}
      </span>
    </div>
  );
}

const TH = 'px-3 py-2 font-semibold';
const THR = 'px-3 py-2 text-end font-semibold';

function EmployeeTableHead({ withRuns }: { withRuns: boolean }) {
  const t = useT();
  return (
    <thead>
      <tr className="border-b border-slate-100 text-caption uppercase tracking-wide text-slate-500 dark:border-white/[0.06] dark:text-slate-400">
        <th className={TH}>{t('Employee')}</th><th className={TH}>{t('Class')}</th><th className={THR}>{t('Covered wage')}</th>
        <th className={THR}>{t('EE expected')}</th><th className={THR}>{t('EE actual')}</th><th className={THR}>{t('EE var.')}</th>
        <th className={THR}>{t('ER expected')}</th><th className={THR}>{t('ER actual')}</th><th className={THR}>{t('ER var.')}</th>
        {withRuns && <th className={THR}>{t('Runs')}</th>}
      </tr>
    </thead>
  );
}

function PeriodEmployeeTable({ period }: { period: GosiPeriodSummary }) {
  const t = useT();
  const fx = useFormat();
  const { money } = makeMoney(fx);
  const rows = [...period.employees].sort((a, b) => Number(b.hasVariance) - Number(a.hasVariance) || a.employeeCode.localeCompare(b.employeeCode));
  return (
    <section className="overflow-x-auto rounded-2xl border border-slate-200/80 bg-white dark:border-white/[0.06] dark:bg-white/[0.03]">
      <VarianceHeader title={t('Per-employee reconciliation')} count={period.varianceCount} total={rows.length} />
      <table data-testid="gosi-employee-table" className="mt-2 w-full min-w-[900px] text-start text-xs">
        <EmployeeTableHead withRuns />
        <tbody>
          {rows.map((r) => (
            <tr key={r.employeeId} data-variance={r.hasVariance ? 'true' : 'false'}
              className={`border-b border-slate-50 last:border-0 dark:border-white/[0.03] ${r.hasVariance ? 'bg-rose-50/70 dark:bg-rose-500/[0.06]' : ''}`}>
              <td className="px-3 py-2"><EmployeeCell code={r.employeeCode} name={r.employeeName} flagged={r.hasVariance} /></td>
              <td className="px-3 py-2 text-slate-600 dark:text-slate-300">{r.classification}</td>
              <td className="px-3 py-2 text-end font-mono tabular-nums text-slate-700 dark:text-slate-200">{money(r.coveredWageBase)}</td>
              <td className="px-3 py-2 text-end font-mono tabular-nums text-slate-600 dark:text-slate-300">{money(r.expectedEmployee)}</td>
              <td className="px-3 py-2 text-end font-mono tabular-nums text-slate-800 dark:text-slate-100">{money(r.actualEmployee)}</td>
              <VarianceCell value={r.employeeVariance} />
              <td className="px-3 py-2 text-end font-mono tabular-nums text-slate-600 dark:text-slate-300">{money(r.expectedEmployer)}</td>
              <td className="px-3 py-2 text-end font-mono tabular-nums text-slate-800 dark:text-slate-100">{money(r.actualEmployer)}</td>
              <VarianceCell value={r.employerVariance} />
              <td className="px-3 py-2 text-end tabular-nums text-slate-500 dark:text-slate-400">{fx.integer(r.runCount)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function RunVarianceTable({ report }: { report: GosiVarianceReport }) {
  const t = useT();
  const { money } = makeMoney(useFormat());
  const [open, setOpen] = useState<number | null>(null);
  const rows = [...report.rows].sort((a, b) => Number(b.hasVariance) - Number(a.hasVariance));
  return (
    <section className="overflow-x-auto rounded-2xl border border-slate-200/80 bg-white dark:border-white/[0.06] dark:bg-white/[0.03]">
      <VarianceHeader title={t('Per-employee variance (this run)')} count={report.withVariance} total={report.totalEmployees} />
      <table data-testid="gosi-employee-table" className="mt-2 w-full min-w-[900px] text-start text-xs">
        <EmployeeTableHead withRuns={false} />
        <tbody>
          {rows.map((r) => (
            <Fragment key={r.employeeId}>
              <tr data-variance={r.hasVariance ? 'true' : 'false'}
                className={`cursor-pointer border-b border-slate-50 dark:border-white/[0.03] ${r.hasVariance ? 'bg-rose-50/70 dark:bg-rose-500/[0.06]' : 'hover:bg-slate-50 dark:hover:bg-white/[0.02]'}`}
                onClick={() => setOpen(open === r.employeeId ? null : r.employeeId)}
                onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setOpen(open === r.employeeId ? null : r.employeeId); } }}
                tabIndex={0}
                aria-expanded={open === r.employeeId}>
                <td className="px-3 py-2">
                  <div className="flex items-center gap-1">
                    {open === r.employeeId ? <ChevronDown className="h-3 w-3 text-slate-400" /> : <ChevronRight className="h-3 w-3 text-slate-400" />}
                    <EmployeeCell code={r.employeeCode} name={r.employeeName} flagged={r.hasVariance} />
                  </div>
                </td>
                <td className="px-3 py-2 text-slate-600 dark:text-slate-300">{r.classification}</td>
                <td className="px-3 py-2 text-end font-mono tabular-nums text-slate-700 dark:text-slate-200">{money(r.coveredWageBase)}</td>
                <td className="px-3 py-2 text-end font-mono tabular-nums text-slate-600 dark:text-slate-300">{money(r.expectedEmployeeContrib)}</td>
                <td className="px-3 py-2 text-end font-mono tabular-nums text-slate-800 dark:text-slate-100">{money(r.actualEmployeeContrib)}</td>
                <VarianceCell value={r.employeeVariance} />
                <td className="px-3 py-2 text-end font-mono tabular-nums text-slate-600 dark:text-slate-300">{money(r.expectedEmployerContrib)}</td>
                <td className="px-3 py-2 text-end font-mono tabular-nums text-slate-800 dark:text-slate-100">{money(r.actualEmployerContrib)}</td>
                <VarianceCell value={r.employerVariance} />
              </tr>
              {open === r.employeeId && (
                <tr className="border-b border-slate-100 bg-slate-50/60 dark:border-white/[0.04] dark:bg-white/[0.02]">
                  <td colSpan={9} className="px-8 py-2">
                    <p className="mb-1 text-caption font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{t('Expected lines (recomputed from the run\'s pack)')}</p>
                    {r.expectedLines.length === 0 ? (
                      <p className="text-slate-500 dark:text-slate-400">{t('No statutory lines expected for this classification.')}</p>
                    ) : (
                      <ul className="space-y-0.5">
                        {r.expectedLines.map((l) => (
                          <li key={l.code} className="flex gap-4 font-mono text-slate-600 dark:text-slate-300">
                            <span className="w-56 truncate font-sans">{l.label} <span className="text-slate-400">({l.code})</span></span>
                            <span>{t('EE {amount}', { amount: money(l.employeeAmount) })}</span><span>{t('ER {amount}', { amount: money(l.employerAmount) })}</span>
                          </li>
                        ))}
                      </ul>
                    )}
                  </td>
                </tr>
              )}
            </Fragment>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function EmployeeCell({ code, name, flagged }: { code: string; name: string; flagged: boolean }) {
  const t = useT();
  return (
    <div className="flex items-center gap-1.5">
      {flagged && <AlertTriangle className="h-3.5 w-3.5 shrink-0 text-rose-500" aria-label={t('Variance')} />}
      <div>
        <p className="font-medium text-slate-800 dark:text-slate-100">{name}</p>
        <p className="font-mono text-caption text-slate-400">{code}</p>
      </div>
    </div>
  );
}
