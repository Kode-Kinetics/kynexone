'use client';

/**
 * Headcount by department as extruded bars, plus the employment-type split as a 3D ring. The depth face is
 * part of each bar's length (front + depth = value), and names and exact values sit in aligned
 * columns, so the depth never has to be read for the number. Nationality lives in the
 * Saudization tile, not repeated here.
 */

import type { DashboardFull } from '../../api/dashboard';
import { Bars3D, Ring3D } from './charts/Visuals';
import { useT } from '../../hooks/useT';

export function Composition({ data }: { data: DashboardFull }) {
  const t = useT();
  const s = data.summary;
  const mix = [...data.overview.workforceMix].sort((a, b) => b.value - a.value);
  const mixTotal = mix.reduce((n, m) => n + m.value, 0);
  const depts = [...data.overview.headcountByDepartment].sort((a, b) => b.value - a.value).slice(0, 8).map((d) => ({ name: d.name, value: d.value }));

  return (
    <section aria-labelledby="comp-heading" className="wg-card flex min-w-0 flex-col gap-3 p-5">
      <header>
        <h2 id="comp-heading" className="text-[15px] font-semibold text-slate-900 dark:text-white">{t('Headcount by department')}</h2>
        <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">{s.activeEmployees.toLocaleString()} {t('active employees across')} {depts.length} {depts.length === 1 ? t('department') : t('departments')}, {t('now')}</p>
      </header>

      {depts.length === 0 ? (
        <p className="text-[13px] text-slate-700 dark:text-slate-300">{t('No active employees yet.')}</p>
      ) : (
        <div className="grid grid-cols-[minmax(0,8rem)_minmax(0,1fr)_2.25rem] items-start gap-x-3">
          <ul className="flex flex-col">{depts.map((d) => <li key={d.name} className="flex h-[22px] items-center truncate text-xs text-slate-800 dark:text-slate-200">{d.name}</li>)}</ul>
          <div className="pt-[2px]"><Bars3D items={depts} label={`${t('Headcount by department')}: ${depts.map((d) => `${d.name} ${d.value}`).join(', ')}`} /></div>
          <ul className="flex flex-col">{depts.map((d) => <li key={d.name} className="flex h-[22px] items-center justify-end text-xs font-semibold tabular-nums text-slate-900 dark:text-white">{d.value}</li>)}</ul>
        </div>
      )}

      {/* Employment mix: the largest type against the rest, when there is a real split. */}
      {mix.length >= 2 ? (
        <div className="mt-auto flex flex-wrap items-center gap-4 rounded-xl border border-[color:var(--wg-line)] bg-[color:var(--wg-surface-2)] p-4">
          <Ring3D a={mix[0].value} b={mixTotal - mix[0].value} center={`${Math.round((mix[0].value / mixTotal) * 100)}%`} sub={mix[0].name.toLowerCase()}
            label={`${t('Employment type')}: ${mix.map((m) => `${m.name} ${m.value}`).join(', ')}`} />
          <ul className="flex min-w-[140px] flex-1 flex-col gap-1.5 text-[13px]">
            {mix.slice(0, 4).map((m, i) => (
              <li key={m.name} className="flex items-center gap-2 text-slate-700 dark:text-slate-300">
                <span aria-hidden className={`h-2.5 w-2.5 rounded-sm ${i === 0 ? 'bg-[color:var(--viz-ring-a)]' : 'bg-[color:var(--viz-ring-b-side)]'}`} />
                {m.name}<b className="ms-auto tabular-nums text-slate-900 dark:text-white">{m.value}</b>
              </li>
            ))}
          </ul>
        </div>
      ) : mix.length === 1 ? (
        <p className="mt-auto text-[13px] text-slate-700 dark:text-slate-300">{t('All')} {mixTotal} {t('active employees are')} <b className="font-semibold text-slate-900 dark:text-white">{mix[0].name.toLowerCase()}</b>.</p>
      ) : null}
    </section>
  );
}
