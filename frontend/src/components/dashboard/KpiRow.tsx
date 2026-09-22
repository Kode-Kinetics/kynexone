'use client';

/**
 * The four analytic tiles under the hero. Each names its period and has an honest state for
 * missing data: today's attendance says pre-shift / not captured rather than drawing 0%, and a
 * trend is drawn only with at least three months to draw.
 */

import Link from 'next/link';
import type { DashboardFull } from '../../api/dashboard';
import { todayAttendance } from './dashboardModel';
import { Gauge } from './charts/Visuals';
import { useT } from '../../hooks/useT';

const TILE = 'wg-card wg-press flex w-[84%] shrink-0 min-w-0 flex-col sm:w-auto gap-2 p-5 [.kx-dense_&]:gap-1.5 [.kx-dense_&]:p-4 outline-none hover:border-[color:var(--wg-line-strong)] focus-visible:ring-2 focus-visible:ring-sapphire';

function Legend({ items }: { items: Array<{ label: string; value: string; color: string }> }) {
  return (
    <ul className="flex min-w-0 flex-1 flex-col gap-1.5 text-[13px] [.kx-dense_&]:gap-0.5 [.kx-dense_&]:text-xs">
      {items.map((i) => (
        <li key={i.label} className="flex min-w-0 items-center gap-2 text-slate-700 dark:text-slate-300">
          <span aria-hidden className="h-2 w-2 shrink-0 rounded-sm" ref={(n) => { if (n) n.style.background = i.color; }} />
          <span className="min-w-0 truncate">{i.label}</span>
          <b className="ms-auto ps-3 font-semibold tabular-nums text-slate-900 dark:text-white">{i.value}</b>
        </li>
      ))}
    </ul>
  );
}

export function KpiRow({ data, hour, asOf, dense = false }: { data: DashboardFull; hour: number; asOf: string | null; dense?: boolean }) {
  const t = useT();
  const s = data.summary;
  const a = data.analytics;
  const today = todayAttendance(data, hour);

  // 1 · Attendance today
  const att = (
    <Link href="/attendance" className={TILE}>
      <span className="flex items-baseline justify-between gap-2">
        <span className="text-sm font-semibold text-slate-900 dark:text-white">{t('Attendance today')}</span>
        <span className="text-xs tabular-nums text-slate-600 dark:text-slate-400">{asOf ?? ''}</span>
      </span>
      <span className="flex items-center gap-3">
        {today.kind === 'counted' ? (
          <Gauge pct={today.rate / 100} center={`${today.rate}%`} sub={t('present')} label={`${today.rate}% ${t('of expected staff present today')}`} />
        ) : (
          <Gauge pct={null} center={today.kind === 'pre-shift' ? t('Pre-shift') : today.kind === 'not-captured' ? t('No punches') : t('Unknown')} sub={t('today')} label={t('No attendance recorded yet today')} />
        )}
        {today.kind === 'counted' ? (
          <Legend items={[
            { label: t('Present'), value: String(today.present), color: 'var(--viz-1)' },
            ...(today.expected - today.present - s.absent > 0 ? [{ label: t('Not in yet'), value: String(today.expected - today.present - s.absent), color: '#94A3B8' }] : []),
            { label: t('Absent'), value: String(s.absent), color: '#EF4444' },
            { label: t('On leave'), value: String(s.onLeave), color: '#0EA5E9' },
          ]} />
        ) : (
          <span className="text-[13px] leading-snug text-slate-700 dark:text-slate-300">
            {s.activeEmployees.toLocaleString()} {t('expected')}{s.onLeave > 0 ? `, ${s.onLeave} ${t('on approved leave')}` : ''}.
          </span>
        )}
      </span>
      <span className="text-xs text-slate-600 dark:text-slate-400 [.kx-dense_&]:line-clamp-1">
        {today.kind === 'counted' ? `${t('Of')} ${today.expected} ${t('expected today.')}`
          : today.kind === 'pre-shift' ? t('The working day has not started. Nothing is late yet.')
          : today.kind === 'not-captured' ? t('No punches recorded today. Check the attendance devices.')
          : t('Attendance could not be loaded.')}
      </span>
    </Link>
  );

  // 2 · Leave used this year
  const lu = a?.leaveUsage;
  const leaveColors = ['var(--viz-1)', '#0EA5E9', '#8B5CF6', '#94A3B8'];
  const top = (lu?.byType ?? []).slice(0, 3);
  const rest = (lu?.byType ?? []).slice(3).reduce((n, x) => n + x.days, 0);
  const parts = [...top.map((x) => ({ label: x.type, days: x.days })), ...(rest > 0 ? [{ label: t('Other'), days: rest }] : [])];
  const denom = lu?.entitlementDays && lu.entitlementDays > 0 ? lu.entitlementDays : lu?.takenDays || 1;
  const leave = (
    <Link href="/leave" className={TILE}>
      <span className="flex items-baseline justify-between gap-2"><span className="text-sm font-semibold text-slate-900 dark:text-white">{lu ? t('Leave used this year') : t('Leave requests')}</span>{lu && <span className="hidden text-xs text-slate-600 dark:text-slate-400 [.kx-dense_&]:inline">{lu.year}</span>}</span>
      {lu ? (
        <>
          <span className="text-[28px] [.kx-dense_&]:text-[24px] font-semibold leading-none tracking-tight tabular-nums text-slate-900 dark:text-white">
            {Math.round(lu.takenDays).toLocaleString()}
            <span className="ms-1.5 text-[13px] font-medium text-slate-600 dark:text-slate-400">
              {lu.entitlementDays ? `${t('of')} ${Math.round(lu.entitlementDays).toLocaleString()} ${t('days')}` : t('days taken')}
            </span>
          </span>
          <span className="flex h-3.5 gap-[2px] overflow-hidden rounded" role="img" aria-label={parts.map((p) => `${p.label} ${p.days} days`).join(', ')}>
            {lu.takenDays === 0 ? <span className="h-full w-full rounded bg-[color:var(--viz-track)]" /> : parts.map((p, i) => (
              <span key={p.label} className="wg-hbar h-full" ref={(n) => { if (n) { n.style.width = `${(p.days / denom) * 100}%`; n.style.background = leaveColors[i]; } }} />
            ))}
            {lu.entitlementDays ? <span className="h-full flex-1 bg-[color:var(--viz-track)]" /> : null}
          </span>
          <ul className="grid grid-cols-2 gap-x-3 gap-y-1 text-xs text-slate-700 dark:text-slate-300 [.kx-dense_&]:flex [.kx-dense_&]:flex-wrap [.kx-dense_&]:gap-x-3">
            {parts.map((p, i) => (
              <li key={p.label} className="flex items-center gap-1.5 truncate">
                <span aria-hidden className="h-2 w-2 shrink-0 rounded-sm" ref={(n) => { if (n) n.style.background = leaveColors[i]; }} />
                <span className="truncate">{p.label}</span><b className="ms-auto font-semibold tabular-nums text-slate-900 dark:text-white">{Math.round(p.days)}</b>
              </li>
            ))}
          </ul>
          <span className="text-xs text-slate-600 dark:text-slate-400 [.kx-dense_&]:hidden">{t('Approved leave,')} {lu.year}.</span>
        </>
      ) : (
        <span className="text-[13px] text-slate-700 dark:text-slate-300">
          <b className="block text-[28px] [.kx-dense_&]:text-[24px] font-semibold leading-none tabular-nums text-slate-900 dark:text-white">{data.overview.openLeaveRequests}</b> {t('leave requests waiting for a decision.')}
        </span>
      )}
    </Link>
  );

  // 3 · Overtime by month
  const current = data.trends[data.trends.length - 1]?.month;
  const ot = data.trends.map((d) => ({ m: d.month, v: d.attendanceRate > 0 ? d.overtimeHours : null }));
  const otPts = ot.filter((p) => p.v != null);
  const otMax = Math.max(1, ...otPts.map((p) => p.v as number));
  const overtime = (
    <Link href="/overtime" className={TILE}>
      <span className="flex items-baseline justify-between gap-2">
        <span className="text-sm font-semibold text-slate-900 dark:text-white">{t('Overtime hours')}</span>
      </span>
      <span className="text-[28px] [.kx-dense_&]:text-[24px] font-semibold leading-none tracking-tight tabular-nums text-slate-900 dark:text-white">{Math.round(s.overtimeHours)} h <span className="text-[13px] font-medium text-slate-600 dark:text-slate-400">{t('this month to date')}</span></span>
      <span className="flex items-end gap-1.5" role="img" aria-label={ot.map((p) => `${p.m} ${p.v == null ? 'no attendance captured' : `${Math.round(p.v)} hours`}`).join(', ')}>
        {ot.map((p) => (
          <span key={p.m} className="flex flex-1 flex-col items-center gap-1">
            <span className="flex h-[64px] items-end [.kx-dense_&]:h-[36px]">
              {p.v == null
                ? <span className="block h-[64px] w-[16px] [.kx-dense_&]:h-[36px] rounded-[5px] border border-dashed border-slate-300 dark:border-white/15" />
                : <span className="wg-bar wg-col3d block w-[16px] rounded-t-[4px] rounded-b-[2px]" ref={(n) => { if (n) { n.style.height = `${Math.max(3, ((p.v as number) / otMax) * (n.closest('.kx-dense') ? 36 : 64))}px`; n.style.background = p.m === current ? 'var(--viz-1)' : 'var(--viz-recede)'; } }} />}
            </span>
            <span className={`text-[11px] ${p.m === current ? 'font-semibold text-slate-900 dark:text-white' : 'text-slate-600 dark:text-slate-400'}`}>{p.m}</span>
          </span>
        ))}
      </span>
      <span className="text-xs text-slate-600 dark:text-slate-400 [.kx-dense_&]:hidden">{t('Recorded on attendance. Dashed months had nothing captured.')}</span>
    </Link>
  );

  // 4 · Saudization (or workforce mix when the module is off)
  const n = a?.nationality;
  const saudi = n ? (
    <Link href="/saudi-compliance" className={TILE}>
      <span className="flex items-baseline justify-between gap-2">
        <span className="text-sm font-semibold text-slate-900 dark:text-white">{t('Saudization')}</span>
        {n.nitaqatBand && <span title={t('Latest Nitaqat snapshot')} className="rounded-full bg-emerald-50 px-2.5 py-0.5 text-xs font-semibold text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-300">{n.nitaqatBand.replace(/([a-z])([A-Z])/g, '$1 $2')}</span>}
      </span>
      <span className="text-[28px] [.kx-dense_&]:text-[24px] font-semibold leading-none tracking-tight tabular-nums text-slate-900 dark:text-white">
        {n.saudizationPct != null ? `${n.saudizationPct.toFixed(1)}%` : t('Not set')}
        <span className="ms-1.5 text-[13px] font-medium text-slate-600 dark:text-slate-400">{n.saudi} {t('of')} {n.saudi + n.nonSaudi} {t('Saudi')}</span>
      </span>
      <span className="flex h-3 gap-[2px] overflow-hidden rounded-full" role="img" aria-label={`${t('Nationality mix')}: ${t('Saudi')} ${n.saudi}, ${t('non-Saudi')} ${n.nonSaudi}${n.unknown ? `, ${t('nationality not recorded')} ${n.unknown}` : ''}`}>
        {[{ v: n.saudi, c: '#15803D' }, { v: n.nonSaudi, c: '#BBF7D0' }, { v: n.unknown, c: '#E2E8F0' }].filter((x) => x.v > 0).map((x, i) => (
          <span key={i} className="wg-hbar h-full" ref={(el) => { if (el) { el.style.flexGrow = String(x.v); el.style.background = x.c; } }} />
        ))}
      </span>
      <span className="text-xs text-slate-600 dark:text-slate-400 [.kx-dense_&]:line-clamp-1">
        {n.unknown > 0 ? `${n.unknown} ${n.unknown === 1 ? t('employee has') : t('employees have')} ${t('no nationality recorded.')}` : t('Share of active employees who are Saudi nationals.')}
      </span>
    </Link>
  ) : (
    <Link href="/people" className={TILE}>
      <span className="text-sm font-semibold text-slate-900 dark:text-white">{t('On leave today')}</span>
      <span className="text-[28px] [.kx-dense_&]:text-[24px] font-semibold leading-none tabular-nums text-slate-900 dark:text-white">{s.onLeave}</span>
      <span className="text-xs text-slate-600 dark:text-slate-400 [.kx-dense_&]:line-clamp-1">{data.overview.openLeaveRequests} {t('open leave requests')}</span>
    </Link>
  );

  return (
    <section aria-label={t('Key metrics')} className={`wg-snap-x -mx-4 min-w-0 gap-3 px-4 pb-1 sm:mx-0 sm:grid sm:grid-cols-2 sm:gap-4 sm:overflow-visible sm:px-0 ${dense ? 'sm:auto-rows-fr' : 'min-[1380px]:grid-cols-4'} ${dense ? 'kx-dense' : ''}`}>
      {att}{leave}{overtime}{saudi}
    </section>
  );
}
