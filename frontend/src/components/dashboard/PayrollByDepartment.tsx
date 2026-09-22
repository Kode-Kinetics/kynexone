'use client';

/**
 * Payroll by department: where this run's net pay goes. Sits under the payroll hero because it
 * answers the hero's next question ("which teams make up that number?"). Each bar carries the
 * exact amount and its share, so the chart never has to be read for the value.
 *
 * Fallback: with no department breakdown on the latest run, the card shows the monthly
 * attendance rate instead (when at least two months have data), labelled with its period.
 * With neither, it renders nothing rather than an empty frame.
 */

import Link from 'next/link';
import { ArrowRight } from 'lucide-react';
import type { DashboardFull } from '../../api/dashboard';
import { fmtMoney } from './dashboardModel';
import { useT } from '../../hooks/useT';

const MONTH_LONG: Record<string, string> = {
  Jan: 'January', Feb: 'February', Mar: 'March', Apr: 'April', May: 'May', Jun: 'June',
  Jul: 'July', Aug: 'August', Sep: 'September', Oct: 'October', Nov: 'November', Dec: 'December',
};

export function PayrollByDepartment({ data }: { data: DashboardFull }) {
  const t = useT();
  const run = data.overview.payrollSummary;
  const rows = [...data.overview.payrollByEntity].filter((r) => r.value > 0).sort((a, b) => b.value - a.value).slice(0, 8);
  const total = rows.reduce((n, r) => n + r.value, 0);
  const max = Math.max(1, ...rows.map((r) => r.value));

  if (run && rows.length > 0) {
    return (
      <section aria-labelledby="paydept-heading" className="wg-card flex min-w-0 flex-col gap-3 p-5">
        <header className="flex items-start justify-between gap-3">
          <div>
            <h2 id="paydept-heading" className="text-[15px] font-semibold text-slate-900 dark:text-white">{t('Payroll by department')}</h2>
            <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">
              {t('Net pay')}, {MONTH_LONG[run.periodLabel.split(' ')[0]] ?? run.periodLabel} {run.periodLabel.split(' ')[1]} {t('run')}. {rows.length} {rows.length === 1 ? t('department') : t('departments')}, {fmtMoney(total)}.
            </p>
          </div>
          <Link href="/payroll" className="inline-flex shrink-0 items-center gap-1 text-[13px] font-semibold text-sapphire hover:underline dark:text-blue-300">
            {t('Open payroll')} <ArrowRight className="h-3.5 w-3.5" aria-hidden />
          </Link>
        </header>
        <ul className="flex flex-col gap-2" role="list" aria-label={`${t('Net pay by department')}: ${rows.map((r) => `${r.name} ${fmtMoney(r.value)}`).join(', ')}`}>
          {rows.map((r) => (
            <li key={r.name} className="grid grid-cols-[minmax(0,9rem)_minmax(0,1fr)_auto] items-center gap-3">
              <span className="truncate text-[13px] text-slate-800 dark:text-slate-200">{r.name}</span>
              <span className="h-3 rounded-e-[4px] bg-[color:var(--viz-track)]">
                <span className="wg-hbar block h-3 rounded-e-[4px] bg-[color:var(--viz-1)]" ref={(n) => { if (n) n.style.width = `${Math.max(2, (r.value / max) * 100)}%`; }} />
              </span>
              <span className="whitespace-nowrap text-end text-[13px] tabular-nums">
                <b className="font-semibold text-slate-900 dark:text-white">{fmtMoney(r.value)}</b>
                <span className="ms-1.5 text-xs text-slate-600 dark:text-slate-400">{Math.round((r.value / total) * 100)}%</span>
              </span>
            </li>
          ))}
        </ul>
      </section>
    );
  }

  // Fallback: monthly attendance rate, when it has something to show.
  const months = data.trends.slice(-6);
  const pts = months.filter((m) => m.attendanceRate > 0);
  if (pts.length < 2) return null;
  const current = months[months.length - 1]?.month;
  return (
    <section aria-labelledby="attrate-heading" className="wg-card flex min-w-0 flex-col gap-3 p-5">
      <header>
        <h2 id="attrate-heading" className="text-[15px] font-semibold text-slate-900 dark:text-white">{t('Attendance rate by month')}</h2>
        <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">{t('Days attended as a share of days rostered.')} {current} {t('is month to date. Not today’s figure.')}</p>
      </header>
      <div className="flex items-end gap-3" role="img" aria-label={months.map((m) => `${m.month} ${m.attendanceRate > 0 ? `${Math.round(m.attendanceRate)}%` : 'no attendance captured'}`).join(', ')}>
        {months.map((m) => (
          <span key={m.month} className="flex flex-1 flex-col items-center gap-1">
            <span className="text-[11px] font-semibold tabular-nums text-slate-900 dark:text-white">{m.attendanceRate > 0 ? `${Math.round(m.attendanceRate)}%` : ''}</span>
            <span className="flex h-[88px] w-full items-end justify-center">
              {m.attendanceRate > 0
                ? <span className="wg-bar block w-[22px] rounded-t-[5px] rounded-b-[2px]" ref={(n) => { if (n) { n.style.height = `${(m.attendanceRate / 100) * 88}px`; n.style.background = m.month === current ? 'var(--viz-1)' : 'var(--viz-recede)'; } }} />
                : <span className="block h-[88px] w-[22px] rounded-[5px] border border-dashed border-slate-300 dark:border-white/15" />}
            </span>
            <span className={`text-[11px] ${m.month === current ? 'font-semibold text-slate-900 dark:text-white' : 'text-slate-600 dark:text-slate-400'}`}>{m.month}</span>
          </span>
        ))}
      </div>
    </section>
  );
}
