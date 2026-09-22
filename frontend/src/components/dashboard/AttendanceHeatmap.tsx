'use client';

/**
 * Attendance by department x day: the dashboard's signature view. Each cell is the share of
 * rostered staff who attended that day; a day with nobody rostered (weekend, holiday) is empty,
 * never 0%. The exact value is in every cell and in a table view.
 */

import { useState } from 'react';
import { Table2, LayoutGrid } from 'lucide-react';
import type { DashboardFull } from '../../api/dashboard';
import { HeatLegend, heatStyle } from './charts/Visuals';
import { useT } from '../../hooks/useT';

function dayLabel(iso: string) {
  const d = new Date(`${iso}T00:00:00`);
  return {
    dow: d.toLocaleDateString('en-GB', { weekday: 'short' }),
    day: d.getDate(),
    full: d.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' }),
  };
}

export function AttendanceHeatmap({ data }: { data: DashboardFull }) {
  const t = useT();
  const [asTable, setAsTable] = useState(false);
  const hm = data.analytics?.attendanceHeatmap;
  const days = hm?.days ?? [];
  const depts = hm?.departments ?? [];
  const anyData = depts.some((d) => d.cells.some((c) => c.rostered > 0));
  // The last day is today and still filling in: marked "so far" and left out of the low point.
  const today = days[days.length - 1];
  const worst = depts.flatMap((d) => d.cells.filter((c) => c.rate != null && c.date !== today).map((c) => ({ d: d.name, c })))
    .sort((a, b) => (a.c.rate as number) - (b.c.rate as number))[0];

  if (!hm) return null;
  return (
    <section aria-labelledby="heat-heading" className="wg-card flex min-w-0 flex-col gap-4 p-5">
      <header className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 id="heat-heading" className="text-[15px] font-semibold text-slate-900 dark:text-white">{t('Attendance by department')}</h2>
          <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">
            {t('Share of rostered staff present each day, last')} {days.length || 15} {t('days.')}
            {worst && worst.c.rate != null && worst.c.rate < 90 && <> {t('Lowest:')} {worst.d}, {dayLabel(worst.c.date).full} ({Math.round(worst.c.rate)}%).</>}
          </p>
        </div>
        <button type="button" onClick={() => setAsTable((v) => !v)} aria-pressed={asTable} aria-label={asTable ? t('Show heatmap') : t('Show as table')}
          className="wg-press grid h-8 w-8 shrink-0 place-items-center rounded-lg text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/[0.06]">
          {asTable ? <LayoutGrid className="h-4 w-4" aria-hidden /> : <Table2 className="h-4 w-4" aria-hidden />}
        </button>
      </header>

      {!anyData ? (
        <p className="rounded-xl border border-dashed border-[color:var(--wg-line-strong)] px-4 py-8 text-center text-[13px] text-slate-700 dark:text-slate-300">
          {t('No attendance has been captured for any department in the last')} {days.length} {t('days. Once punches arrive from devices or the app, each department fills in here day by day.')}
        </p>
      ) : asTable ? (
        <div className="max-h-[320px] overflow-auto">
          <table className="w-full text-xs">
            <thead><tr className="text-slate-600 dark:text-slate-400"><th scope="col" className="py-1.5 text-start font-medium">{t('Department')}</th>{days.map((d) => <th key={d} scope="col" className="px-1 py-1.5 text-end font-medium">{dayLabel(d).day}</th>)}</tr></thead>
            <tbody>
              {depts.map((d) => (
                <tr key={d.name} className="border-t border-[color:var(--wg-line)]">
                  <th scope="row" className="py-1.5 text-start font-medium text-slate-800 dark:text-slate-200">{d.name}</th>
                  {d.cells.map((c) => <td key={c.date} className="px-1 py-1.5 text-end tabular-nums text-slate-900 dark:text-white">{c.rate == null ? '·' : `${Math.round(c.rate)}%`}</td>)}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="-mx-1 overflow-x-auto px-1 pb-1 outline-none focus-visible:ring-2 focus-visible:ring-sapphire" tabIndex={0} role="group" aria-label={t('Attendance heatmap, scrolls sideways on small screens')}>
          <div className="grid min-w-[500px] gap-[3px]" ref={(n) => { if (n) n.style.gridTemplateColumns = `minmax(92px, 132px) repeat(${days.length}, minmax(0, 1fr))`; }}>
            <span />
            {days.map((d) => {
              const l = dayLabel(d);
              return <span key={d} className="text-center text-[10px] leading-tight text-slate-600 dark:text-slate-400">{d === today ? t('Today') : l.dow}<br /><b className="font-semibold text-slate-800 dark:text-slate-200">{l.day}</b></span>;
            })}
            {depts.map((d) => (
              <div key={d.name} className="contents">
                <span className="flex min-w-0 items-center truncate pe-2 text-[13px] text-slate-800 dark:text-slate-200" title={`${d.name}, ${d.headcount}`}>{d.name}</span>
                {d.cells.map((c) => {
                  const h = heatStyle(c.rate);
                  const l = dayLabel(c.date);
                  return (
                    <span key={c.date} role="img"
                      aria-label={`${d.name}, ${l.full}${c.date === today ? ` (${t('so far today')})` : ''}: ${c.rate == null ? t('nobody rostered') : `${Math.round(c.rate)}%, ${c.attended} ${t('of')} ${c.rostered}`}`}
                      title={`${d.name}, ${l.full}: ${c.rate == null ? t('nobody rostered') : `${Math.round(c.rate)}% (${c.attended} of ${c.rostered})`}`}
                      className={`grid h-[32px] place-items-center rounded-[6px] text-[11px] font-semibold tabular-nums ${c.date === today ? 'wg-heat-today bg-transparent text-slate-800 outline-dashed outline-1 outline-slate-400 dark:text-slate-200' : h ? '' : 'bg-slate-100 dark:bg-white/[0.05]'}`}
                      ref={(n) => { if (n && h && c.date !== today) { n.style.background = h.bg; n.style.color = h.fg; } }}>
                      {c.rate == null ? '' : Math.round(c.rate)}
                    </span>
                  );
                })}
              </div>
            ))}
          </div>
        </div>
      )}
      {anyData && !asTable && (
        <div className="flex flex-col gap-1.5">
          <HeatLegend />
          <p className="text-[11px] text-slate-600 dark:text-slate-400">{t('Today’s column is outlined: it fills in as people punch in, so it is not comparable with full days yet.')}</p>
        </div>
      )}
    </section>
  );
}
