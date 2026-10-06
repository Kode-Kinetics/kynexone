'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { Scale } from 'lucide-react';
import { deductionsApi, type DeductionStatement, type RunDeductionRow } from '../../api/deductions';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { useReleaseA } from '../../lib/releaseA';
import { CapStatusChip, DeductionStatementView } from './DeductionStatementView';
import { DeductionsDrawer } from './DeductionsDrawer';

type Filter = 'attention' | 'all';

/**
 * The payroll run's deductions check (Release A slice R3), mounted once under the run's slips. Exception-first: it opens
 * on the employees near or over the Art. 93 half-wage limit; "Everyone" lists the whole run. Each row's "Deductions"
 * button opens the drawer with every line behind the numbers. Renders nothing unless the tenant has release_a on.
 */
export function RunDeductionsReview({ runId }: { runId: string }) {
  const enabled = useReleaseA();
  if (!enabled) return null;
  return <RunDeductionsReviewBody key={runId} runId={runId} />;
}

function RunDeductionsReviewBody({ runId }: { runId: string }) {
  const { t } = useLocale();
  const fx = useFormat();
  const [rows, setRows] = useState<RunDeductionRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter>('attention');
  const [open, setOpen] = useState<RunDeductionRow | null>(null);
  const [statement, setStatement] = useState<DeductionStatement | null>(null);
  const [statementError, setStatementError] = useState<string | null>(null);

  const load = useCallback(() => {
    setError(null);
    deductionsApi.run(runId)
      .then(setRows)
      .catch(() => { setRows(null); setError(t('The deductions check could not be loaded.')); });
  }, [runId, t]);
  useEffect(() => { load(); }, [load]);

  useEffect(() => {
    if (!open) { setStatement(null); return; }
    let cancelled = false;
    setStatement(null); setStatementError(null);
    deductionsApi.slip(open.slipId)
      .then((s) => { if (!cancelled) setStatement(s); })
      .catch(() => { if (!cancelled) setStatementError(t('These deductions could not be loaded.')); });
    return () => { cancelled = true; };
  }, [open, t]);

  const over = useMemo(() => rows?.filter((r) => r.capStatus === 'Over').length ?? 0, [rows]);
  const near = useMemo(() => rows?.filter((r) => r.capStatus === 'Near').length ?? 0, [rows]);
  const shown = useMemo(() => (rows ?? []).filter((r) => filter === 'all' || r.capStatus !== 'Within' || r.flags.length > 0), [rows, filter]);

  return (
    <section className="border-t border-slate-100 px-4 py-4 dark:border-white/[0.07]" data-testid="run-deductions-review" aria-labelledby={`deductions-check-${runId}`}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h3 id={`deductions-check-${runId}`} className="flex items-center gap-1.5 text-sm font-bold text-slate-900 dark:text-white">
            <Scale className="h-4 w-4 text-sapphire dark:text-cyanAccent" aria-hidden /> {t('Deductions check (Article 93)')}
          </h3>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('Loan, advance, penalty and court-ordered deductions may not exceed half of the wage due. Near the limit means more than 80% of it is used.')}
          </p>
        </div>
        <div role="group" aria-label={t('Show')} className="inline-flex rounded-lg border border-slate-200 p-0.5 text-xs dark:border-white/10">
          {(['attention', 'all'] as const).map((f) => (
            <button key={f} type="button" aria-pressed={filter === f} onClick={() => setFilter(f)}
              className={`rounded-md px-2.5 py-1 font-semibold ${filter === f ? 'bg-sapphire text-white dark:bg-cyanAccent dark:text-slate-900' : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/[0.06]'}`}>
              {f === 'attention' ? t('Near or over the limit') : t('Everyone')}
            </button>
          ))}
        </div>
      </div>

      {error ? (
        <p role="alert" className="mt-3 text-xs text-rose-600 dark:text-rose-400">{error}</p>
      ) : rows === null ? (
        <div className="mt-3 h-16 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
      ) : (
        <>
          <p className="mt-3 text-xs font-medium text-slate-700 dark:text-slate-200" data-testid="run-deductions-summary">
            {t('Over the limit: {over}. Near the limit: {near}. Employees in this run: {total}.', { over, near, total: rows.length })}
          </p>
          {shown.length === 0 ? (
            <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">
              {filter === 'attention' ? t('No employee in this run is near or over the limit.') : t('No payslips in this run yet.')}
            </p>
          ) : (
            <div className="mt-2 overflow-x-auto">
              <table className="w-full min-w-[620px] text-sm">
                <thead>
                  <tr className="border-b border-slate-100 text-start text-xs font-bold uppercase text-slate-400 dark:border-white/[0.07]">
                    <th className="px-2 py-2 text-start">{t('Employee')}</th>
                    <th className="px-2 py-2 text-end">{t('Wage due')}</th>
                    <th className="px-2 py-2 text-end">{t('Counted deductions')}</th>
                    <th className="px-2 py-2 text-end">{t('Share of wage')}</th>
                    <th className="px-2 py-2 text-end">{t('Room left')}</th>
                    <th className="px-2 py-2 text-start">{t('Status')}</th>
                    <th className="px-2 py-2"><span className="sr-only">{t('Actions')}</span></th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                  {shown.map((r) => (
                    <tr key={r.slipId} data-testid="run-deduction-row">
                      <td className="px-2 py-2">
                        <p className="font-medium text-slate-900 dark:text-white">{r.employeeName}</p>
                        <p className="text-xs text-slate-400">{r.employeeCode}</p>
                      </td>
                      <td className="px-2 py-2 text-end font-mono tabular-nums">{fx.money(r.wageDue, r.currency)}</td>
                      <td className="px-2 py-2 text-end font-mono tabular-nums">{fx.money(r.debtTotal, r.currency)}</td>
                      <td className="px-2 py-2 text-end font-mono tabular-nums">{r.debtPercentOfWage == null ? '—' : fx.percent(r.debtPercentOfWage, 2)}</td>
                      <td className={`px-2 py-2 text-end font-mono tabular-nums ${r.headroom < 0 ? 'text-rose-700 dark:text-rose-300' : ''}`}>{fx.money(r.headroom, r.currency)}</td>
                      <td className="px-2 py-2">
                        <CapStatusChip status={r.capStatus} />
                        {r.flags.some((f) => f !== 'DEDUCTIONS_OVER_HALF_WAGE') && (
                          <p className="mt-0.5 text-[10px] font-semibold text-amber-700 dark:text-amber-300">{t('Needs a look')}</p>
                        )}
                      </td>
                      <td className="px-2 py-2 text-end">
                        <button type="button" onClick={() => setOpen(r)}
                          aria-label={t('Open the deductions for {name}', { name: r.employeeName })}
                          className="rounded-lg border border-slate-200 px-2.5 py-1 text-xs font-semibold text-sapphire hover:bg-slate-50 dark:border-white/10 dark:text-cyanAccent dark:hover:bg-white/[0.04]">
                          {t('Deductions')}
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}

      <DeductionsDrawer open={!!open} onClose={() => setOpen(null)}
        title={open ? t('Deductions for {name}', { name: open.employeeName }) : ''}
        subtitle={open?.employeeCode}>
        {statementError ? (
          <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{statementError}</p>
        ) : statement ? (
          <DeductionStatementView statement={statement} audience="hr" />
        ) : (
          <div className="h-48 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
        )}
      </DeductionsDrawer>
    </section>
  );
}
