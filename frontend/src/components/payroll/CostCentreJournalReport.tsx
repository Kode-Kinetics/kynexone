'use client';

import { Fragment, useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, ChevronDown, ChevronRight, Download, Eye, Info, Lock } from 'lucide-react';
import { payrollApi, type CostCenterAllocationReport, type PayrollCompany, type PayrollGLJournal, type PayrollRun } from '../../api/payroll';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { textDir } from '../../lib/glCostAccounting';
import { allocationDifference, allocationToCsv } from '../../lib/payrollCostReport';
import { EnumLabel } from '../EnumLabel';
import { Badge, LoadError, Spinner, serverMessage } from '../gl/glUi';
import { periodLabel } from './CostCentreLockPreview';

/**
 * Screen 7 (cost accounting v1): Payroll → Reports → "Payroll journal by cost centre". Totals by cost centre for one run, a
 * row opening to its accounts, whether the stamps reconcile to the journal, and the CSV of the table on screen. It never shows
 * an employee, a name, an IBAN or a per-employee amount: the server groups, this screen only lays out what it sends.
 */
export function CostCentreJournalReport() {
  const { hasPermission } = useAuth();
  if (!hasPermission('finance.gl.read')) return null;
  return <ReportBody />;
}

const periodKey = (r: PayrollRun) => `${r.year}-${String(r.month).padStart(2, '0')}`;

function ReportBody() {
  const t = useT();
  const { locale } = useLocale();
  const fmt = useFormat();
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [companies, setCompanies] = useState<PayrollCompany[]>([]);
  const [company, setCompany] = useState('');
  const [period, setPeriod] = useState('');
  const [runId, setRunId] = useState('');
  const [data, setData] = useState<CostCenterAllocationReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [setupError, setSetupError] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [journal, setJournal] = useState<PayrollGLJournal | null>(null);
  const [drillError, setDrillError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    Promise.all([payrollApi.listAllRuns(), payrollApi.listCompanies()])
      .then(([r, c]) => { if (active) { setRuns(r); setCompanies(c); } })
      .catch((e: unknown) => { if (active) setSetupError(serverMessage(e, t)); });
    return () => { active = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const inCompany = useMemo(() => runs.filter((r) => !company || r.companyId === company), [runs, company]);
  const periods = useMemo(() => [...new Set(inCompany.map(periodKey))].sort().reverse(), [inCompany]);
  const choices = useMemo(() => inCompany.filter((r) => !period || periodKey(r) === period), [inCompany, period]);
  const run = runs.find((r) => r.id === runId);

  const load = useCallback((id: string) => {
    if (!id) { setData(null); return; }
    setLoading(true);
    setError(null);
    setOpen(null);
    setJournal(null);
    payrollApi.costCenterAllocation(id)
      .then((d) => setData(d))
      .catch((e: unknown) => { setData(null); setError(serverMessage(e, t)); })
      .finally(() => setLoading(false));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => { load(runId); }, [runId, load]);

  const rows = data?.allocations ?? [];
  const basis = data?.basis ?? (run?.status === 'Locked' ? 'Stamped' : 'Preview');
  const notRecorded = basis === 'NotRecorded';
  const difference = basis === 'Stamped' ? allocationDifference(rows) : null;
  const reconciles = data?.reconciles ?? null;
  const currency = data?.currency ?? null;
  const money = (n: number) => fmt.money(n, null);
  const pickNote = locale === 'ar' ? data?.retroNoteAr || data?.retroNote : data?.retroNote || data?.retroNoteAr;

  const toggle = async (code: string) => {
    if (open === code) { setOpen(null); return; }
    setOpen(code);
    if (journal || !runId) return;
    setDrillError(null);
    try { setJournal(await payrollApi.glJournal(runId)); } catch (e) { setDrillError(serverMessage(e, t)); }
  };

  const exportCsv = () => {
    const csv = allocationToCsv(rows.map((r) => (r.costCenterId || notRecorded ? r : { ...r, costCenterName: t('Unassigned (no cost centre)') })), {
      headers: [t('Cost centre'), t('Headcount'), t('Gross'), t('Employer cost'), t('Net'), t('Journal net'), t('Cleared against accrual')],
      total: t('Grand total'),
    });
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = `payroll-journal-by-cost-centre-${data?.period ?? 'run'}.csv`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  };

  const th = 'px-3 py-2 font-medium';
  const rowLabel = (r: (typeof rows)[number]) => {
    if (notRecorded) return <span>{t('Not recorded (run locked before cost centres)')}</span>;
    if (!r.costCenterId) return <span>{t('Unassigned (no cost centre)')}</span>;
    return (
      <span>
        <bdi dir="ltr" className="font-mono">{r.costCenterCode}</bdi> — <bdi dir={textDir(r.costCenterName)}>{r.costCenterName}</bdi>
      </span>
    );
  };
  const sum = (pick: (r: (typeof rows)[number]) => number) => rows.reduce((s, r) => s + pick(r), 0);
  const stamped = rows.length > 0 && rows.every((r) => r.journalNet != null);
  const cols = 7;

  return (
    <section data-testid="cc-report" aria-labelledby="cc-report-title" className="surface space-y-4 p-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 id="cc-report-title" className="text-sm font-semibold text-slate-800 dark:text-white">{t('Payroll journal by cost centre')}</h2>
        <button type="button" data-testid="cc-report-export" onClick={exportCsv} disabled={rows.length === 0} className="btn-secondary h-8 px-3 text-xs">
          <Download className="h-3.5 w-3.5" aria-hidden /> {t('Export CSV')}
        </button>
      </div>

      {setupError && <LoadError message={setupError} onRetry={() => window.location.reload()} />}

      <div className="flex flex-wrap items-end gap-3">
        {companies.length > 1 && (
          <label className="text-xs font-medium text-slate-600 dark:text-slate-300">
            <span className="mb-1 block">{t('Company')}</span>
            <select data-testid="cc-report-company" className="select h-8 min-w-44 text-xs" value={company} onChange={(e) => { setCompany(e.target.value); setPeriod(''); setRunId(''); }}>
              <option value="">{t('All companies')}</option>
              {companies.map((c) => <option key={c.id} value={c.id}>{c.tradeName || c.name}</option>)}
            </select>
          </label>
        )}
        <label className="text-xs font-medium text-slate-600 dark:text-slate-300">
          <span className="mb-1 block">{t('Period')}</span>
          <select data-testid="cc-report-period" className="select h-8 min-w-36 text-xs" value={period} onChange={(e) => { setPeriod(e.target.value); setRunId(''); }}>
            <option value="">{t('All periods')}</option>
            {periods.map((p) => <option key={p} value={p}>{periodLabel(p, locale)}</option>)}
          </select>
        </label>
        <label className="text-xs font-medium text-slate-600 dark:text-slate-300">
          <span className="mb-1 block">{t('Run')}</span>
          <select data-testid="cc-report-run" className="select h-8 min-w-56 text-xs" value={runId} onChange={(e) => setRunId(e.target.value)}>
            <option value="">{t('Select a payroll run…')}</option>
            {choices.map((r) => <option key={r.id} value={r.id}>{periodLabel(periodKey(r), locale)} — <EnumLabel enum="PayrollRunStatus" value={r.status} /></option>)}
          </select>
        </label>
        {data && (
          <span data-testid="cc-report-chip">
            {basis === 'Preview'
              ? <Badge tone="amber"><Eye className="h-3 w-3" aria-hidden /> {t('Preview — this run is not locked')}</Badge>
              : <Badge tone="emerald"><Lock className="h-3 w-3" aria-hidden /> {t('Locked')}</Badge>}
          </span>
        )}
      </div>

      {loading && <Spinner />}
      {error && !loading && <LoadError message={error} onRetry={() => load(runId)} />}

      {data && !loading && !error && (
        <>
          {basis === 'Stamped' && reconciles === true && (
            <p data-testid="cc-report-reconciles" role="status" className="flex items-center gap-2 rounded-lg bg-emerald-50 px-3 py-2 text-sm font-medium text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300">
              <CheckCircle2 className="h-4 w-4" aria-hidden /> <span>{t('Reconciles with the journal.')}</span>
            </p>
          )}
          {basis === 'Stamped' && reconciles === false && (
            <p data-testid="cc-report-reconciles" role="status" className="flex flex-wrap items-center gap-2 rounded-lg bg-rose-50 px-3 py-2 text-sm font-medium text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">
              <AlertTriangle className="h-4 w-4" aria-hidden /> <span>{t('Does not reconcile with the journal.')}</span>
              {difference != null && <span data-testid="cc-report-diff">{t('Difference: {amount}', { amount: money(difference) })}</span>}
            </p>
          )}
          {basis !== 'Stamped' && (
            <p data-testid="cc-report-reconciles" role="status" className="flex items-center gap-2 rounded-lg bg-slate-50 px-3 py-2 text-sm text-slate-700 dark:bg-white/5 dark:text-slate-300">
              <Info className="h-4 w-4" aria-hidden /> <span>{t('Reconciliation with the journal is available once the run is locked.')}</span>
            </p>
          )}

          {rows.length === 0 ? (
            <p data-testid="cc-report-empty" role="status" className="text-sm text-slate-600 dark:text-slate-300">{t('No costed lines for this run.')}</p>
          ) : (
            <div className="overflow-x-auto" tabIndex={0} role="group" aria-labelledby="cc-report-title">
              <table className="w-full min-w-[760px] text-sm">
                <caption className="sr-only">{t('Payroll journal by cost centre')}</caption>
                <thead>
                  <tr className="border-b border-slate-100 text-xs text-slate-500 dark:border-white/5 dark:text-slate-400">
                    <th scope="col" className={`${th} text-start`}>{t('Cost centre')}</th>
                    <th scope="col" className={`${th} text-end`}>{t('Headcount')}</th>
                    <th scope="col" className={`${th} text-end`}>{t('Gross')}</th>
                    <th scope="col" className={`${th} text-end`}>{t('Employer cost')}</th>
                    <th scope="col" className={`${th} text-end`}>{t('Net')}</th>
                    <th scope="col" className={`${th} text-end`}>{t('Journal net')}</th>
                    <th scope="col" className={`${th} text-end`}>{t('Cleared against accrual')}</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => {
                    const code = r.costCenterCode;
                    const canDrill = !notRecorded && !!r.costCenterId && !!runId;
                    const isOpen = open === code;
                    const lines = (journal?.entries ?? []).filter((e) => e.costCentreCode === code);
                    return (
                      <Fragment key={r.costCenterId ?? `none-${code}`}>
                        <tr data-testid={notRecorded ? 'cc-report-not-recorded' : `cc-report-row-${code}`} className="border-t border-slate-100 dark:border-white/5">
                          <th scope="row" className="px-3 py-2 text-start font-medium">
                            {canDrill ? (
                              <button type="button" aria-expanded={isOpen} aria-label={t('Show accounts for {cc}', { cc: code })} onClick={() => void toggle(code)} className="inline-flex min-h-6 items-center gap-1.5 text-start focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire">
                                {isOpen ? <ChevronDown className="h-4 w-4" aria-hidden /> : <ChevronRight className="h-4 w-4" aria-hidden />}
                                {rowLabel(r)}
                              </button>
                            ) : rowLabel(r)}
                          </th>
                          <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{fmt.integer(r.employeeCount)}</bdi></td>
                          <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{money(r.grossSalary)}</bdi></td>
                          <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{money(r.employerCost)}</bdi></td>
                          <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{money(r.netSalary)}</bdi></td>
                          <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{r.journalNet == null ? '—' : money(r.journalNet)}</bdi></td>
                          <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{r.clearedAgainstAccrual == null ? '—' : money(r.clearedAgainstAccrual)}</bdi></td>
                        </tr>
                        {isOpen && (
                          <tr data-testid={`cc-report-drill-${code}`} className="bg-slate-50/70 dark:bg-white/[0.03]">
                            <td colSpan={cols} className="px-3 py-2">
                              {drillError ? <p role="alert" className="text-xs text-rose-700 dark:text-rose-300">{drillError}</p>
                                : !journal ? <Spinner />
                                : lines.length === 0 ? <p className="text-xs text-slate-600 dark:text-slate-300">{t('No journal lines match.')}</p>
                                : (
                                  <table className="w-full text-xs">
                                    <thead>
                                      <tr className="text-slate-500 dark:text-slate-400">
                                        <th scope="col" className="px-2 py-1 text-start font-medium">{t('Account')}</th>
                                        <th scope="col" className="px-2 py-1 text-end font-medium">{t('Debit')}</th>
                                        <th scope="col" className="px-2 py-1 text-end font-medium">{t('Credit')}</th>
                                      </tr>
                                    </thead>
                                    <tbody>
                                      {lines.map((l, i) => {
                                        const [acct, ...rest] = l.glAccount.split(' — ');
                                        return (
                                          <tr key={i} className="border-t border-slate-100 dark:border-white/5">
                                            <td className="px-2 py-1"><bdi dir="ltr" className="font-mono">{acct}</bdi>{rest.length ? <> <bdi dir={textDir(l.glAccountName)}>{l.glAccountName}</bdi></> : null}</td>
                                            <td className="px-2 py-1 text-end tabular-nums"><bdi dir="ltr">{l.entryType === 'DR' ? money(l.amount) : ''}</bdi></td>
                                            <td className="px-2 py-1 text-end tabular-nums"><bdi dir="ltr">{l.entryType === 'CR' ? money(l.amount) : ''}</bdi></td>
                                          </tr>
                                        );
                                      })}
                                    </tbody>
                                  </table>
                                )}
                            </td>
                          </tr>
                        )}
                      </Fragment>
                    );
                  })}
                  <tr data-testid="cc-report-total" className="border-t-2 border-slate-200 font-semibold dark:border-white/10">
                    <th scope="row" className="px-3 py-2 text-start">{t('Grand total')}</th>
                    <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{fmt.integer(sum((r) => r.employeeCount))}</bdi></td>
                    <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{money(sum((r) => r.grossSalary))}</bdi></td>
                    <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{money(sum((r) => r.employerCost))}</bdi></td>
                    <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{money(sum((r) => r.netSalary))}</bdi></td>
                    <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{stamped ? money(sum((r) => r.journalNet ?? 0)) : '—'}</bdi></td>
                    <td className="px-3 py-2 text-end tabular-nums"><bdi dir="ltr">{stamped ? money(sum((r) => r.clearedAgainstAccrual ?? 0)) : '—'}</bdi></td>
                  </tr>
                </tbody>
              </table>
            </div>
          )}

          {currency && <p className="text-xs text-slate-500 dark:text-slate-400">{t('Amounts in {currency}.', { currency })}</p>}
          {rows.some((r) => r.smallCell) && (
            <p data-testid="cc-report-rolled-note" role="note" className="flex items-center gap-2 text-xs text-slate-600 dark:text-slate-300">
              <Info className="h-3.5 w-3.5 shrink-0" aria-hidden /> {t('Includes cost centres rolled up to protect small groups.')}
            </p>
          )}
          {pickNote && <p data-testid="cc-report-retro-note" role="note" className="text-xs text-slate-600 dark:text-slate-300" dir="auto">{pickNote}</p>}
        </>
      )}
    </section>
  );
}
