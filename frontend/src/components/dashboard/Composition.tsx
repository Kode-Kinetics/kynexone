'use client';

/**
 * Headcount by department as extruded bars: the one 3D touch on the page. The depth face is
 * part of each bar's length (front + depth = value), and names and exact values sit in aligned
 * columns, so the depth never has to be read for the number. Nationality lives in the
 * Saudization tile, not repeated here.
 */

import type { DashboardFull } from '../../api/dashboard';
import { Bars3D } from './charts/Visuals';
import { useT } from '../../hooks/useT';

export function Composition({ data }: { data: DashboardFull }) {
  const t = useT();
  const s = data.summary;
  const depts = [...data.overview.headcountByDepartment].sort((a, b) => b.value - a.value).slice(0, 8).map((d) => ({ name: d.name, value: d.value }));

  return (
    <section aria-labelledby="comp-heading" className="wg-card flex min-w-0 flex-col gap-4 p-5">
      <header>
        <h2 id="comp-heading" className="text-[15px] font-semibold text-slate-900 dark:text-white">{t('Headcount by department')}</h2>
        <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">{s.activeEmployees.toLocaleString()} {t('active employees across')} {depts.length} {depts.length === 1 ? t('department') : t('departments')}, {t('now')}</p>
      </header>

      {depts.length === 0 ? (
        <p className="text-[13px] text-slate-700 dark:text-slate-300">{t('No active employees yet.')}</p>
      ) : (
        <div className="grid grid-cols-[minmax(0,8rem)_minmax(0,1fr)_2.25rem] items-start gap-x-3">
          <ul className="flex flex-col">{depts.map((d) => <li key={d.name} className="flex h-[26px] items-center truncate text-xs text-slate-800 dark:text-slate-200">{d.name}</li>)}</ul>
          <div className="pt-[2px]"><Bars3D items={depts} label={`${t('Headcount by department')}: ${depts.map((d) => `${d.name} ${d.value}`).join(', ')}`} /></div>
          <ul className="flex flex-col">{depts.map((d) => <li key={d.name} className="flex h-[26px] items-center justify-end text-xs font-semibold tabular-nums text-slate-900 dark:text-white">{d.value}</li>)}</ul>
        </div>
      )}
    </section>
  );
}
