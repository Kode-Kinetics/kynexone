'use client';

/**
 * Headcount by department as flat gradient bars, plus the employment-type split as a ring. Names and
 * exact values sit in aligned columns, so the bar never has to be read for the number. Nationality lives in the
 * Saudization tile, not repeated here.
 */

import type { DashboardFull } from '../../api/dashboard';
import { RankedBars, Ring } from './charts/Visuals';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { employmentTypeLabel } from '../../lib/employmentType';

export function Composition({ data }: { data: DashboardFull }) {
  const t = useT();
  const f = useFormat();
  const s = data.summary;
  const mix = [...data.overview.workforceMix].map((m) => ({ ...m, name: employmentTypeLabel(t, m.name) })).sort((a, b) => b.value - a.value);
  const mixTotal = mix.reduce((n, m) => n + m.value, 0);
  const depts = [...data.overview.headcountByDepartment].sort((a, b) => b.value - a.value).slice(0, 8).map((d) => ({ name: d.name, value: d.value }));

  return (
    <section aria-labelledby="comp-heading" className="wg-card flex min-w-0 flex-col gap-3 p-5">
      <header>
        <h2 id="comp-heading" className="text-body font-semibold text-slate-900 dark:text-white">{t('Headcount by department')}</h2>
        <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">{t('{count, plural, one {# active employee} other {# active employees}} across {departments, plural, one {# department} other {# departments}}, now', { count: s.activeEmployees, departments: depts.length })}</p>
      </header>

      {depts.length === 0 ? (
        <p className="text-body-sm text-slate-700 dark:text-slate-300">{t('No active employees yet.')}</p>
      ) : (
        <RankedBars items={depts} label={`${t('Headcount by department')}: ${depts.map((d) => `${d.name} ${d.value}`).join(', ')}`} />
      )}

      {/* Employment mix: the largest type against the rest, when there is a real split. */}
      {mix.length >= 2 ? (
        <div className="mt-auto flex flex-wrap items-center gap-4 rounded-xl border border-[color:var(--wg-line)] bg-[color:var(--wg-surface-2)] p-4">
          <Ring a={mix[0].value} b={mixTotal - mix[0].value} center={f.percent((mix[0].value / mixTotal) * 100)} sub={mix[0].name.toLowerCase()}
            label={`${t('Employment type')}: ${mix.map((m) => `${m.name} ${m.value}`).join(', ')}`} />
          <ul className="flex min-w-[140px] flex-1 flex-col gap-1.5 text-body-sm">
            {mix.slice(0, 4).map((m, i) => (
              <li key={m.name} className="flex items-center gap-2 text-slate-700 dark:text-slate-300">
                <span aria-hidden className={`h-2.5 w-2.5 rounded ${i === 0 ? 'bg-[color:var(--viz-ring-a)]' : 'bg-[color:var(--viz-ring-b-side)]'}`} />
                {m.name}<b className="ms-auto tabular-nums text-slate-900 dark:text-white">{m.value}</b>
              </li>
            ))}
          </ul>
        </div>
      ) : mix.length === 1 ? (
        <p className="mt-auto text-body-sm text-slate-700 dark:text-slate-300">{t('All {count} active employees are {type}.', { count: mixTotal, type: mix[0].name.toLowerCase() })}</p>
      ) : null}
    </section>
  );
}
