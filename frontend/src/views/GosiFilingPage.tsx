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

// ── helpers ─────────────────────────────────────────────────────────────────────

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const HALALA = 0.01;

const money = (n: number | null | undefined) =>
  n === null || n === undefined
    ? '—'
    : n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

const signed = (n: number | null | undefined) =>
  n === null || n === undefined ? '—' : `${n > 0 ? '+' : ''}${money(n)}`;

const isZero = (n: number | null | undefined) => n !== null && n !== undefined && Math.abs(n) < HALALA;

const errorText = (e: unknown, what: string) => {
  const status = (e as { response?: { status?: number } })?.response?.status;
  if (status === 403) return `Your role cannot read ${what}. GOSI filing needs Admin, HR Manager, Payroll Manager or Payroll Officer.`;
  if (status === 404) return `The ${what} was not found.`;
  return `Could not load the ${what}.`;
};

type Scope = { kind: 'period' } | { kind: 'run'; runId: string };

// ── page ────────────────────────────────────────────────────────────────────────

/**
 * GOSI filing & variance: read-only view over the three reconciliation endpoints.
 * Period mode is the filing source (every non-voided run unioned, ceiling applied once);
 * run mode drills into one run's tie-out and per-employee variance with expected lines.
 */
export function GosiFilingPage() {
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
      setError(errorText(e, 'GOSI period summary'));
    } finally {
      if (seq === requestSeq.current) setLoading(false);
    }
  }, [year, month, companyId]);

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
      setError(errorText(e, 'payroll run reconciliation'));
    } finally {
      if (seq === requestSeq.current) setLoading(false);
    }
  }, []);

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

  const reload = () => (scope.kind === 'run' ? void loadRun(scope.runId) : void loadPeriod());
  const tie = scope.kind === 'run' ? runSummary : period;

  return (
    <div className="space-y-4">
      {/* Header + picker */}
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">GOSI Filing &amp; Variance</h1>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            Deducted vs recomputed vs posted to the ledger, per employee, per period. Read-only.
          </p>
        </div>
        <div className="flex flex-wrap items-end gap-2">
          <PickerSelect label="Month" value={String(month)} onChange={(v) => pickMonth(Number(v))} ariaLabel="Month">
            {MONTHS.map((m, i) => <option key={m} value={i + 1}>{m}</option>)}
          </PickerSelect>
          <PickerSelect label="Year" value={String(year)} onChange={(v) => pickYear(Number(v))} ariaLabel="Year">
            {years.map((y) => <option key={y} value={y}>{y}</option>)}
          </PickerSelect>
          <PickerSelect label="Company" value={companyId} onChange={setCompanyId} ariaLabel="Company">
            {companies.length !== 1 && <option value="">All companies</option>}
            {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </PickerSelect>
          <button type="button" onClick={reload}
            className="flex h-[34px] items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 text-xs font-semibold text-slate-600 hover:bg-slate-50 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-300 dark:hover:bg-white/[0.08]">
            <RefreshCw className="h-3.5 w-3.5" /> Refresh
          </button>
        </div>
      </div>

      {periodChips.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5 text-[11px]">
          <span className="font-medium text-slate-500 dark:text-slate-400">Periods with payroll runs:</span>
          {periodChips.map(([key, p]) => {
            const active = p.year === year && p.month === month;
            return (
              <button key={key} type="button" onClick={() => { pickYear(p.year); pickMonth(p.month); }}
                className={`rounded-full px-2.5 py-0.5 font-semibold transition ${active
                  ? 'bg-sapphire text-white'
                  : 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-white/[0.06] dark:text-slate-300 dark:hover:bg-white/[0.1]'}`}>
                {MONTHS[p.month - 1].slice(0, 3)} {p.year}
              </button>
            );
          })}
        </div>
      )}

      <div className="flex items-start gap-2 rounded-xl border border-blue-200 bg-blue-50 px-4 py-2.5 text-xs font-medium text-blue-800 dark:border-blue-500/20 dark:bg-blue-500/[0.06] dark:text-blue-300">
        <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" />
        <span>
          Employee GOSI readiness (missing IDs, classification, blocked employees) lives on{' '}
          <Link href="/saudi-compliance" className="font-semibold underline underline-offset-2">Saudi Compliance</Link>.
          This screen reconciles what payroll actually filed.
        </span>
      </div>

      {loading ? (
        <div className="space-y-3" aria-busy="true" aria-label="Loading GOSI reconciliation">
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
            <RefreshCw className="h-3.5 w-3.5" /> Retry
          </button>
        </div>
      ) : period && !period.hasStatutoryData && scope.kind === 'period' ? (
        <NoStatutoryData period={`${MONTHS[month - 1]} ${year}`} runCount={period.runCount} packStatusNote={period.packStatusNote} />
      ) : tie ? (
        <>
          {period && period.runs.length > 0 && (
            <ScopeBar period={period} scope={scope} onChange={setScope} />
          )}

          {scope.kind === 'run' && runSummary && !runSummary.hasStatutoryData ? (
            <NoStatutoryData period={`this run (${runSummary.period})`} runCount={1} packStatusNote={runSummary.packStatusNote} />
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
    <label className="flex flex-col text-[10px] font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
      {label}
      <select aria-label={ariaLabel} value={value} onChange={(e) => onChange(e.target.value)}
        className="mt-0.5 h-[34px] rounded-lg border border-slate-200 bg-white px-2.5 text-xs font-semibold normal-case tracking-normal text-slate-700 dark:border-white/[0.08] dark:bg-[#0c1120] dark:text-slate-200">
        {children}
      </select>
    </label>
  );
}

function NoStatutoryData({ period, runCount, packStatusNote }: { period: string; runCount: number; packStatusNote: string | null }) {
  return (
    <div data-testid="gosi-no-statutory" className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 bg-white p-10 text-center dark:border-white/[0.06] dark:bg-white/[0.03]">
      <FileSpreadsheet className="h-10 w-10 text-slate-300 dark:text-slate-600" />
      <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">No statutory lines recorded for {period}</p>
      <p className="max-w-lg text-xs text-slate-500 dark:text-slate-400">
        {runCount === 0
          ? 'There is no processed payroll run for this period and company, so there is nothing to file. Pick a period from the quick-picks above, or process payroll for this month first.'
          : `${runCount} payroll run(s) exist for this period, but none carries statutory GOSI deduction lines. Reprocess the run, or check the employees' GOSI classification on Saudi Compliance.`}
      </p>
      {packStatusNote && <p className="max-w-lg text-[11px] text-slate-400">{packStatusNote}</p>}
      <Link href="/saudi-compliance" className="text-xs font-semibold text-sapphire hover:underline">Open Saudi Compliance</Link>
    </div>
  );
}

function ScopeBar({ period, scope, onChange }: { period: GosiPeriodSummary; scope: Scope; onChange: (s: Scope) => void }) {
  const base = 'rounded-lg px-3 py-1.5 text-xs font-semibold transition';
  const on = 'bg-slate-800 text-white dark:bg-white dark:text-slate-900';
  const off = 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/[0.06]';
  return (
    <div className="flex flex-wrap items-center gap-1 rounded-xl border border-slate-200 bg-white p-1 dark:border-white/[0.06] dark:bg-white/[0.03]" role="tablist" aria-label="Reconciliation scope">
      <button type="button" role="tab" aria-selected={scope.kind === 'period'} onClick={() => onChange({ kind: 'period' })}
        className={`${base} ${scope.kind === 'period' ? on : off}`}>
        Whole period · filing view ({period.runCount} run{period.runCount === 1 ? '' : 's'})
      </button>
      {period.runs.map((r) => (
        <button key={r.runId} type="button" role="tab" aria-selected={scope.kind === 'run' && scope.runId === r.runId}
          onClick={() => onChange({ kind: 'run', runId: r.runId })}
          className={`${base} ${scope.kind === 'run' && scope.runId === r.runId ? on : off}`}>
          {r.runType} run · {r.status} · EE {money(r.employeeTotal)} / ER {money(r.employerTotal)}
        </button>
      ))}
    </div>
  );
}

function Notes({ packResolved, packStatusNote, periodScopeNote, expectedIsPeriodPartial, runCount }: {
  packResolved: boolean; packStatusNote: string | null; periodScopeNote: string | null; expectedIsPeriodPartial: boolean; runCount: number;
}) {
  const items: { tone: 'amber' | 'blue'; title: string; body: string; testId: string }[] = [];
  if (packStatusNote) items.push({
    tone: packResolved ? 'blue' : 'amber',
    title: packResolved ? 'Country pack note' : 'Country pack not resolved: expected figures are indicative',
    body: packStatusNote, testId: 'gosi-pack-note',
  });
  if (expectedIsPeriodPartial) items.push({
    tone: 'amber',
    title: 'This run is part of a multi-run period. A variance here is arithmetic, not a defect.',
    body: periodScopeNote ?? 'Other runs share this period. File from the whole-period view.', testId: 'gosi-period-scope-note',
  });
  else if (runCount > 1) items.push({
    tone: 'blue',
    title: `${runCount} runs are unioned for this period`,
    body: 'Expected is recomputed once on the period-aggregated covered wage, so the statutory ceiling is applied exactly once. This is the figure to file.',
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
  const expDelta = tie.expectedVsActualEmployeeDelta + tie.expectedVsActualEmployerDelta;
  const glDelta = tie.glPosted ? (tie.glEmployeeDelta ?? 0) + (tie.glEmployerDelta ?? 0) : null;
  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
      <Tile label="Employee contributions" value={money(tie.totalEmployeeContrib)} sub="Deducted from pay" testId="tile-employee" />
      <Tile label="Employer contributions" value={money(tie.totalEmployerContrib)} sub="Employer cost" testId="tile-employer" />
      <Tile label="Expected vs actual" value={signed(expDelta)} testId="tile-expected-delta"
        tone={isZero(expDelta) ? 'ok' : 'warn'}
        sub={isZero(expDelta) ? 'Recomputation matches to the halala' : 'Actual minus recomputed'} />
      <Tile label="Ledger vs actual" value={glDelta === null ? 'Not posted' : signed(glDelta)} testId="tile-gl-delta"
        tone={glDelta === null ? 'muted' : isZero(glDelta) ? 'ok' : 'warn'}
        sub={glDelta === null ? 'No GL posting yet (run not locked)' : isZero(glDelta) ? 'GL matches deductions to the halala' : 'Actual minus GL liability'} />
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
      <p className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</p>
      <p className={`mt-1 font-mono text-xl font-bold tabular-nums ${valueCls}`}>{value}</p>
      <p className="mt-0.5 text-[11px] text-slate-500 dark:text-slate-400">{sub}</p>
    </div>
  );
}

/** The differentiator: Deducted = Recomputed = Slip = GL, per side, with the delta at every hop. */
function TieOut({ tie, run }: { tie: Tie; run: GosiRunSummary | null }) {
  const sides = [
    {
      side: 'Employee', actual: tie.totalEmployeeContrib, expected: tie.expectedEmployeeContrib,
      slip: run ? run.slipEmployeeStatutoryTotal : null, gl: tie.glEmployeeLiability,
      account: run?.glEmployeeAccount ?? '2101', glDelta: tie.glEmployeeDelta,
    },
    {
      side: 'Employer', actual: tie.totalEmployerContrib, expected: tie.expectedEmployerContrib,
      slip: run ? run.slipEmployerStatutoryTotal : null, gl: tie.glEmployerLiability,
      account: run?.glEmployerAccount ?? '2106', glDelta: tie.glEmployerDelta,
    },
  ];
  const allTie = sides.every((s) => isZero(s.actual - s.expected) && (s.slip === null || isZero(s.actual - s.slip)) && tie.glPosted && isZero(s.glDelta));
  return (
    <section data-testid="gosi-tieout" className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">Tie-out</h2>
        <span data-testid="gosi-tieout-verdict"
          className={`flex items-center gap-1 rounded-full px-2.5 py-0.5 text-[11px] font-bold ${allTie
            ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300'
            : 'bg-amber-100 text-amber-800 dark:bg-amber-500/[0.12] dark:text-amber-300'}`}>
          {allTie ? <><CheckCircle2 className="h-3.5 w-3.5" /> Ties out to the halala</> : <><AlertTriangle className="h-3.5 w-3.5" /> Differences to review</>}
        </span>
      </div>
      <div className="space-y-2">
        {sides.map((s) => (
          <div key={s.side} className="flex flex-wrap items-stretch gap-1.5 text-xs">
            <span className="flex w-20 items-center font-semibold text-slate-600 dark:text-slate-300">{s.side}</span>
            <Hop label="Deducted" value={s.actual} />
            <Eq ok={isZero(s.actual - s.expected)} />
            <Hop label="Recomputed" value={s.expected} />
            {s.slip !== null && (<><Eq ok={isZero(s.actual - s.slip)} /><Hop label="Payslips" value={s.slip} /></>)}
            <Eq ok={tie.glPosted && isZero(s.glDelta)} muted={!tie.glPosted} />
            <Hop label={`GL ${s.account}`} value={s.gl} muted={!tie.glPosted} />
          </div>
        ))}
      </div>
    </section>
  );
}

function Hop({ label, value, muted = false }: { label: string; value: number | null; muted?: boolean }) {
  return (
    <div className={`min-w-[112px] rounded-lg border px-2.5 py-1.5 ${muted
      ? 'border-dashed border-slate-200 text-slate-400 dark:border-white/[0.08]'
      : 'border-slate-200 bg-slate-50 dark:border-white/[0.06] dark:bg-white/[0.03]'}`}>
      <p className="text-[10px] font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</p>
      <p className="font-mono font-bold tabular-nums text-slate-800 dark:text-slate-100">{value === null ? 'Not posted' : money(value)}</p>
    </div>
  );
}

function Eq({ ok, muted = false }: { ok: boolean; muted?: boolean }) {
  return (
    <span className={`flex items-center px-0.5 text-base font-bold ${muted ? 'text-slate-300 dark:text-slate-600' : ok ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}`}
      aria-label={muted ? 'not posted' : ok ? 'equal' : 'not equal'}>
      {muted ? '·' : ok ? '=' : '≠'}
    </span>
  );
}

function ComponentBreakdown({ rows }: { rows: GosiComponentBreakdown[] }) {
  return (
    <section className="overflow-x-auto rounded-2xl border border-slate-200/80 bg-white dark:border-white/[0.06] dark:bg-white/[0.03]">
      <h2 className="px-4 pt-3 text-sm font-bold text-slate-800 dark:text-slate-100">Component breakdown</h2>
      <table className="mt-2 w-full min-w-[560px] text-start text-sm">
        <thead>
          <tr className="border-b border-slate-100 text-[11px] uppercase tracking-wide text-slate-500 dark:border-white/[0.06] dark:text-slate-400">
            <th className="px-4 py-2 font-semibold">Component</th>
            <th className="px-4 py-2 font-semibold">Paid by</th>
            <th className="px-4 py-2 text-end font-semibold">Employees</th>
            <th className="px-4 py-2 text-end font-semibold">Amount</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.componentCode} className="border-b border-slate-50 last:border-0 dark:border-white/[0.03]">
              <td className="px-4 py-2">
                <span className="font-medium text-slate-800 dark:text-slate-100">{r.componentName || r.componentCode}</span>
                <span className="ms-2 font-mono text-[11px] text-slate-400">{r.componentCode}</span>
              </td>
              <td className="px-4 py-2">
                <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${r.isEmployerContribution
                  ? 'bg-violet-100 text-violet-700 dark:bg-violet-500/[0.12] dark:text-violet-300'
                  : 'bg-sky-100 text-sky-700 dark:bg-sky-500/[0.12] dark:text-sky-300'}`}>
                  {r.isEmployerContribution ? 'Employer' : 'Employee'}
                </span>
              </td>
              <td className="px-4 py-2 text-end tabular-nums text-slate-600 dark:text-slate-300">{r.employeeCount}</td>
              <td className="px-4 py-2 text-end font-mono font-semibold tabular-nums text-slate-800 dark:text-slate-100">{money(r.totalAmount)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function VarianceCell({ value }: { value: number }) {
  const ok = isZero(value);
  return (
    <td className={`px-3 py-2 text-end font-mono tabular-nums ${ok ? 'text-slate-400 dark:text-slate-500' : 'font-bold text-rose-700 dark:text-rose-300'}`}>
      {ok ? '0.00' : signed(value)}
    </td>
  );
}

function VarianceHeader({ title, count, total }: { title: string; count: number; total: number }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-2 px-4 pt-3">
      <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">{title}</h2>
      <span data-testid="gosi-variance-count"
        className={`flex items-center gap-1 rounded-full px-2.5 py-0.5 text-[11px] font-bold ${count === 0
          ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300'
          : 'bg-rose-100 text-rose-700 dark:bg-rose-500/[0.12] dark:text-rose-300'}`}>
        {count === 0 ? <ShieldCheck className="h-3.5 w-3.5" /> : <XCircle className="h-3.5 w-3.5" />}
        {count === 0 ? `All ${total} employees reconcile` : `${count} of ${total} employees with a variance`}
      </span>
    </div>
  );
}

const TH = 'px-3 py-2 font-semibold';
const THR = 'px-3 py-2 text-end font-semibold';

function PeriodEmployeeTable({ period }: { period: GosiPeriodSummary }) {
  const rows = [...period.employees].sort((a, b) => Number(b.hasVariance) - Number(a.hasVariance) || a.employeeCode.localeCompare(b.employeeCode));
  return (
    <section className="overflow-x-auto rounded-2xl border border-slate-200/80 bg-white dark:border-white/[0.06] dark:bg-white/[0.03]">
      <VarianceHeader title="Per-employee reconciliation" count={period.varianceCount} total={rows.length} />
      <table data-testid="gosi-employee-table" className="mt-2 w-full min-w-[900px] text-start text-xs">
        <thead>
          <tr className="border-b border-slate-100 text-[10px] uppercase tracking-wide text-slate-500 dark:border-white/[0.06] dark:text-slate-400">
            <th className={TH}>Employee</th><th className={TH}>Class</th><th className={THR}>Covered wage</th>
            <th className={THR}>EE expected</th><th className={THR}>EE actual</th><th className={THR}>EE var.</th>
            <th className={THR}>ER expected</th><th className={THR}>ER actual</th><th className={THR}>ER var.</th>
            <th className={THR}>Runs</th>
          </tr>
        </thead>
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
              <td className="px-3 py-2 text-end tabular-nums text-slate-500 dark:text-slate-400">{r.runCount}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function RunVarianceTable({ report }: { report: GosiVarianceReport }) {
  const [open, setOpen] = useState<number | null>(null);
  const rows = [...report.rows].sort((a, b) => Number(b.hasVariance) - Number(a.hasVariance));
  return (
    <section className="overflow-x-auto rounded-2xl border border-slate-200/80 bg-white dark:border-white/[0.06] dark:bg-white/[0.03]">
      <VarianceHeader title="Per-employee variance (this run)" count={report.withVariance} total={report.totalEmployees} />
      <table data-testid="gosi-employee-table" className="mt-2 w-full min-w-[900px] text-start text-xs">
        <thead>
          <tr className="border-b border-slate-100 text-[10px] uppercase tracking-wide text-slate-500 dark:border-white/[0.06] dark:text-slate-400">
            <th className={TH}>Employee</th><th className={TH}>Class</th><th className={THR}>Covered wage</th>
            <th className={THR}>EE expected</th><th className={THR}>EE actual</th><th className={THR}>EE var.</th>
            <th className={THR}>ER expected</th><th className={THR}>ER actual</th><th className={THR}>ER var.</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <Fragment key={r.employeeId}>
              <tr data-variance={r.hasVariance ? 'true' : 'false'}
                className={`cursor-pointer border-b border-slate-50 dark:border-white/[0.03] ${r.hasVariance ? 'bg-rose-50/70 dark:bg-rose-500/[0.06]' : 'hover:bg-slate-50 dark:hover:bg-white/[0.02]'}`}
                onClick={() => setOpen(open === r.employeeId ? null : r.employeeId)}
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
                    <p className="mb-1 text-[10px] font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Expected lines (recomputed from the run&apos;s pack)</p>
                    {r.expectedLines.length === 0 ? (
                      <p className="text-slate-500 dark:text-slate-400">No statutory lines expected for this classification.</p>
                    ) : (
                      <ul className="space-y-0.5">
                        {r.expectedLines.map((l) => (
                          <li key={l.code} className="flex gap-4 font-mono text-slate-600 dark:text-slate-300">
                            <span className="w-56 truncate font-sans">{l.label} <span className="text-slate-400">({l.code})</span></span>
                            <span>EE {money(l.employeeAmount)}</span><span>ER {money(l.employerAmount)}</span>
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
  return (
    <div className="flex items-center gap-1.5">
      {flagged && <AlertTriangle className="h-3.5 w-3.5 shrink-0 text-rose-500" aria-label="Variance" />}
      <div>
        <p className="font-medium text-slate-800 dark:text-slate-100">{name}</p>
        <p className="font-mono text-[10px] text-slate-400">{code}</p>
      </div>
    </div>
  );
}
