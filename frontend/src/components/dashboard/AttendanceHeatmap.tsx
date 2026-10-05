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
import { useFormat } from '../../hooks/useFormat';
import type { Formatter } from '../../lib/format';

function dayLabel(iso: string, f: Formatter) {
  const d = new Date(`${iso}T00:00:00`);
  return {
    dow: f.gregorian(d, 'weekdayShort'),
    day: d.getDate(),
    full: f.gregorian(d, 'full'),
  };
}

export function AttendanceHeatmap({ data }: { data: DashboardFull }) {
  const t = useT();
  const f = useFormat();
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
    <section aria-labelledby="heat-heading" className="wg-card flex min-w-0 flex-col gap-3 p-5">
      <header className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 id="heat-heading" className="text-[15px] font-semibold text-slate-900 dark:text-white">{t('Attendance by department')}</h2>
          <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">
            {t('Share of rostered staff present each day over the last {days}.', { days: t('{count} days', { count: days.length || 15 }) })}
            {worst && worst.c.rate != null && worst.c.rate < 90 && <> {t('Lowest: {department} on {date} ({rate}).', { department: worst.d, date: dayLabel(worst.c.date, f).full, rate: f.percent(worst.c.rate) })}</>}
          </p>
        </div>
        <button type="button" onClick={() => setAsTable((v) => !v)} aria-pressed={asTable} aria-label={asTable ? t('Show heatmap') : t('Show as table')}
          className="wg-press grid h-8 w-8 shrink-0 place-items-center rounded-lg text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/[0.06]">
          {asTable ? <LayoutGrid className="h-4 w-4" aria-hidden /> : <Table2 className="h-4 w-4" aria-hidden />}
        </button>
      </header>

      {!anyData ? (
        <p className="rounded-xl border border-dashed border-[color:var(--wg-line-strong)] px-4 py-8 text-center text-[13px] text-slate-700 dark:text-slate-300">
          {t('No attendance has been captured for any department in the last {days}. Once punches arrive from devices or the app, each department fills in here day by day.', { days: t('{count} days', { count: days.length }) })}
        </p>
      ) : asTable ? (
        <div className="max-h-[320px] overflow-auto">
          <table className="w-full text-xs">
            <thead><tr className="text-slate-600 dark:text-slate-400"><th scope="col" className="py-1.5 text-start font-medium">{t('Department')}</th>{days.map((d) => <th key={d} scope="col" className="px-1 py-1.5 text-end font-medium">{dayLabel(d, f).day}</th>)}</tr></thead>
            <tbody>
              {depts.map((d) => (
                <tr key={d.name} className="border-t border-[color:var(--wg-line)]">
                  <th scope="row" className="py-1.5 text-start font-medium text-slate-800 dark:text-slate-200">{d.name}</th>
                  {d.cells.map((c) => <td key={c.date} className="px-1 py-1.5 text-end tabular-nums text-slate-900 dark:text-white">{c.rate == null ? '·' : f.percent(c.rate)}</td>)}
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
              const l = dayLabel(d, f);
              return <span key={d} className="text-center text-[10px] leading-tight text-slate-600 dark:text-slate-400">{d === today ? t('Today') : l.dow}<br /><b className="font-semibold text-slate-800 dark:text-slate-200">{l.day}</b></span>;
            })}
            {depts.map((d) => (
              <div key={d.name} className="contents">
                <span className="flex min-w-0 items-center truncate pe-2 text-[13px] text-slate-800 dark:text-slate-200" title={`${d.name}, ${d.headcount}`}>{d.name}</span>
                {d.cells.map((c) => {
                  const h = heatStyle(c.rate);
                  const l = dayLabel(c.date, f);
                  const when = c.date === today ? t('{date}, so far', { date: l.full }) : l.full;
                  const cellLabel = c.rate == null
                    ? t('{department}, {date}: nobody rostered', { department: d.name, date: when })
                    : t('{department}, {date}: {rate}, {attended} of {rostered} attended', { department: d.name, date: when, rate: f.percent(c.rate), attended: c.attended, rostered: c.rostered });
                  return (
                    <span key={c.date} role="img"
                      aria-label={cellLabel}
                      title={cellLabel}
                      className={`grid h-[25px] place-items-center rounded-[5px] text-[11px] font-semibold tabular-nums ${c.date === today ? 'wg-heat-today bg-transparent text-slate-800 outline-dashed outline-1 outline-slate-400 dark:text-slate-200' : h ? '' : 'bg-slate-100 dark:bg-white/[0.05]'}`}
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
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
          <HeatLegend />
          <span className="flex items-center gap-1.5 text-[11px] text-slate-700 dark:text-slate-300" title={t('Today fills in as people punch in, so it is not comparable with full days yet.')}>
            <span aria-hidden className="h-3.5 w-3.5 rounded outline-dashed outline-1 outline-slate-400" />{t('Today, so far')}
          </span>
        </div>
      )}
    </section>
  );
}
