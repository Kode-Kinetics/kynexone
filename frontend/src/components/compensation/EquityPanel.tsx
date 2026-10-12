'use client';

import { useCallback, useEffect, useState } from 'react';
import { ShieldAlert } from 'lucide-react';
import { compensationApi, type CycleSummary, type Equity, type EquityCohort } from '../../api/compensation';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { PanelState, serverMessage, theadClass, thClass } from '../gl/glUi';

/**
 * Pay equity within like-for-like cohorts (grade × designation in one company). Groups under the minimum cohort are
 * suppressed, and so is the cohort total whenever any group is, so a hidden group cannot be worked out by difference.
 * The formulas are stated on the page; nothing here is adjusted for tenure, performance or location.
 */
export function EquityPanel() {
  const t = useT();
  const [cycles, setCycles] = useState<CycleSummary[] | null>(null);
  const [cycleId, setCycleId] = useState<string>('');
  const [equity, setEquity] = useState<Equity | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    compensationApi.cycles().then((c) => { setCycles(c); setCycleId((x) => x || c.find((y) => y.status !== 'Cancelled')?.id || c[0]?.id || ''); }).catch((err) => setError(serverMessage(err, t)));
  }, [t]);
  const load = useCallback(async () => {
    if (!cycleId) return;
    setError(null); setEquity(null);
    try { setEquity(await compensationApi.equity(cycleId)); } catch (err) { setError(serverMessage(err, t)); }
  }, [cycleId, t]);
  useEffect(() => { void load(); }, [load]);

  return (
    <section className="space-y-4" aria-labelledby="equity-heading">
      <div className="surface space-y-2 p-4">
        <h3 id="equity-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Pay equity')}</h3>
        <div role="note" className="flex items-start gap-2 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-900 dark:bg-amber-500/10 dark:text-amber-200" data-testid="equity-notice">
          <ShieldAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
          <span>{t('Authorised use only. These figures describe pay positions within like-for-like cohorts for pay-equity review. They are not adjusted for tenure, performance or location and must not be used to make decisions about an individual.')}</span>
        </div>
        <details className="text-xs text-slate-600 dark:text-slate-300">
          <summary className="cursor-pointer font-medium">{t('How these figures are calculated')}</summary>
          <ul className="mt-1 list-disc space-y-0.5 ps-5">
            <li>{t('Cohort: the same company, grade and designation; active employees with a salary record and a grade midpoint.')}</li>
            <li>{t('Compa-ratio: monthly gross (basic plus fixed allowances) ÷ the grade midpoint, to 4 decimals.')}</li>
            <li>{t('Unadjusted gap: (mean compa-ratio of men − mean compa-ratio of women) ÷ mean compa-ratio of men × 100. A positive gap means women sit lower in the range.')}</li>
            <li>{t('Projected: the same, after the awards proposed on each manager’s submitted scenario.')}</li>
            <li>{t('Any group of fewer than {k} people is hidden, and so is its cohort total.', { k: equity?.minimumCohort ?? 5 })}</li>
          </ul>
        </details>
        {cycles && cycles.length > 0 && (
          <label className="flex flex-wrap items-center gap-2 text-xs">
            <span className="font-medium text-slate-600 dark:text-slate-300">{t('Cycle')}</span>
            <select className="input h-8 text-xs" value={cycleId} onChange={(e) => setCycleId(e.target.value)}>
              {cycles.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
        )}
      </div>
      <PanelState loading={!equity && !error && !!cycleId} error={error} onRetry={load} empty={!cycleId} emptyLabel={t('Create a merit cycle to see pay equity.')}>
        {equity && (
          <>
            <CohortTable title={t('By gender')} cohorts={equity.cohorts} currency={equity.currency} />
            {equity.nationalityCohorts.length > 0 && <CohortTable title={t('National and expatriate')} cohorts={equity.nationalityCohorts} currency={equity.currency} nationality />}
            <p className="text-xs text-slate-500 dark:text-slate-400">
              {t('Not included: {noGrade} without a grade midpoint, {noSalary} without a salary record.', { noGrade: equity.excludedNoGrade, noSalary: equity.excludedNoSalary })}
            </p>
          </>
        )}
      </PanelState>
    </section>
  );
}

function groupLabel(t: ReturnType<typeof useT>, g: string) {
  switch (g) {
    case 'Male': return t('Men');
    case 'Female': return t('Women');
    case 'National': return t('Nationals');
    case 'Expatriate': return t('Expatriates');
    default: return t('Not stated');
  }
}

function CohortTable({ title, cohorts, currency, nationality }: { title: string; cohorts: EquityCohort[]; currency: string; nationality?: boolean }) {
  const t = useT();
  const f = useFormat();
  const ratio = (v: number | null) => (v == null ? '—' : f.number(v, { minimumFractionDigits: 4, maximumFractionDigits: 4 }));
  return (
    <div className="surface overflow-x-auto p-4">
      <h4 className="mb-2 text-sm font-semibold text-slate-900 dark:text-white">{title}</h4>
      <table className="w-full min-w-[820px] text-sm" data-testid={nationality ? 'equity-nationality' : 'equity-gender'}>
        <thead className={theadClass}><tr>
          <th className={thClass}>{t('Cohort')}</th><th className={thClass}>{t('Group')}</th><th className={thClass}>{t('People')}</th>
          <th className={thClass}>{t('Mean compa-ratio')}</th><th className={thClass}>{t('Median compa-ratio')}</th><th className={thClass}>{t('Mean gross')}</th>
          <th className={thClass}>{t('Gap now')}</th><th className={thClass}>{t('Gap after proposals')}</th>
        </tr></thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/5">
          {cohorts.map((c) => {
            const key = `${c.gradeName}-${c.designationName}`;
            if (c.suppressed) {
              return (
                <tr key={key} data-cohort={key}>
                  <td className="px-3 py-2 font-medium">{`${c.gradeName} · ${c.designationName}`}</td>
                  <td className="px-3 py-2 text-xs text-slate-500 dark:text-slate-400" colSpan={7}>{t('Hidden: fewer than {k} people in this cohort.', { k: 5 })}</td>
                </tr>
              );
            }
            return c.groups.map((g, i) => (
              <tr key={`${key}-${g.group}`} data-cohort={key} data-group={g.group}>
                <td className="px-3 py-2 font-medium">{i === 0 ? `${c.gradeName} · ${c.designationName}` : ''}</td>
                <td className="px-3 py-2">{groupLabel(t, g.group)}</td>
                {g.suppressed ? (
                  <td className="px-3 py-2 text-xs text-slate-500 dark:text-slate-400" colSpan={4}>{t('Hidden: fewer than {k} people.', { k: 5 })}</td>
                ) : (
                  <>
                    <td className="px-3 py-2 tabular-nums">{f.integer(g.count)}</td>
                    <td className="px-3 py-2 tabular-nums">{ratio(g.meanCompaRatio)}</td>
                    <td className="px-3 py-2 tabular-nums">{ratio(g.medianCompaRatio)}</td>
                    <td className="px-3 py-2 tabular-nums">{f.money(g.meanGrossCycle, currency)}</td>
                  </>
                )}
                <td className="px-3 py-2 tabular-nums">{i === 0 ? f.percent(c.gapPercent, 2) : ''}</td>
                <td className="px-3 py-2 tabular-nums">{i === 0 ? f.percent(c.projectedGapPercent, 2) : ''}</td>
              </tr>
            ));
          })}
        </tbody>
      </table>
    </div>
  );
}
