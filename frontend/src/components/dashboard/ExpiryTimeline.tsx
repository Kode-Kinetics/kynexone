'use client';

/**
 * Document expiries on a time axis: overdue (left of today), due within 30 days, and later up
 * to 90 days. Each pin is the employee's initials plus the document and the days. Pins are
 * placed in lanes so labels never overlap; the same items are listed as text below the axis
 * for screen readers and for anyone who prefers a list. Uploaded-document gaps (employees
 * missing a required document) are summarised underneath, with their own count and action.
 */

import { useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { ArrowRight, CheckCircle2 } from 'lucide-react';
import { Avatar } from '../Avatar';
import type { DashboardFull } from '../../api/dashboard';
import { complianceDeadlines } from './dashboardModel';
import { Ring3D } from './charts/Visuals';
import { useT } from '../../hooks/useT';

const MIN_D = -45;
const MAX_D = 90;

export function ExpiryTimeline({ data }: { data: DashboardFull }) {
  const t = useT();
  const k = data.kpis;
  const items = complianceDeadlines(data.overview.alerts).filter((d) => d.daysRemaining != null);
  const legacy = complianceDeadlines(data.overview.alerts).filter((d) => d.daysRemaining == null);
  const pos = (d: number) => ((Math.max(MIN_D, Math.min(MAX_D, d)) - MIN_D) / (MAX_D - MIN_D)) * 100;

  // Lanes by the space each label really takes: a pin's label runs to its end side, or back
  // toward the axis when the pin is past 62%. Intervals are in % of the measured axis width.
  const axisRef = useRef<HTMLDivElement>(null);
  const [axisW, setAxisW] = useState(600);
  useEffect(() => {
    const el = axisRef.current;
    if (!el) return;
    const ro = new ResizeObserver(([e]) => setAxisW(Math.max(240, e.contentRect.width)));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const lanes: Array<Array<[number, number]>> = [];
  const placed = [...items].sort((a, b) => (a.daysRemaining as number) - (b.daysRemaining as number)).map((it) => {
    const x = pos(it.daysRemaining as number);
    const labelPx = 44 + Math.max(it.kind.length * 6.6, 78);
    const w = (labelPx / axisW) * 100;
    const span: [number, number] = x > 62 ? [x - w, x + 3] : [x - 3, x + w];
    let lane = lanes.findIndex((l) => l.every(([a, b]) => span[1] + 1.5 < a || span[0] - 1.5 > b));
    if (lane < 0) { lanes.push([]); lane = lanes.length - 1; }
    lanes[lane].push(span);
    return { it, x, lane };
  });
  const laneCount = Math.max(1, lanes.length);
  const overdue = items.filter((i) => (i.daysRemaining as number) < 0).length;
  const soon = items.filter((i) => (i.daysRemaining as number) >= 0 && (i.daysRemaining as number) <= 30).length;

  return (
    <section aria-labelledby="expiry-heading" className="wg-card flex min-w-0 flex-col gap-3 p-5">
      <header className="flex items-start justify-between gap-3">
        <div>
          <h2 id="expiry-heading" className="text-[15px] font-semibold text-slate-900 dark:text-white">{t('Document expiries, next 90 days')}</h2>
          <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400">
            {items.length === 0 ? t('Nothing expires in the next 90 days and nothing is overdue.') : `${overdue} ${t('overdue')}, ${soon} ${t('within 30 days')}, ${items.length - overdue - soon} ${t('later')}.`}
          </p>
        </div>
        <Link href="/compliance" className="inline-flex shrink-0 items-center gap-1 text-[13px] font-semibold text-sapphire hover:underline dark:text-blue-300">
          {t('Open compliance')} <ArrowRight className="h-3.5 w-3.5" aria-hidden />
        </Link>
      </header>

      {items.length > 0 && (
        <div style={{ height: `${22 + laneCount * 40}px` }} ref={axisRef} className="relative min-w-0" aria-hidden >
          <span className="absolute inset-y-0 start-0 rounded-s-xl bg-rose-50 dark:bg-rose-500/[0.08]" ref={(n) => { if (n) n.style.width = `${pos(0)}%`; }} />
          <span className="absolute inset-y-0 bg-amber-50 dark:bg-amber-500/[0.07]" ref={(n) => { if (n) { n.style.insetInlineStart = `${pos(0)}%`; n.style.width = `${pos(30) - pos(0)}%`; } }} />
          {[-30, 0, 30, 60, 90].map((d) => (
            <span key={d} className="absolute inset-y-0 w-px" ref={(n) => { if (n) { n.style.insetInlineStart = `${pos(d)}%`; n.style.background = d === 0 ? 'var(--wg-line-strong)' : 'var(--wg-line)'; } }}>
              <span className={`absolute -bottom-5 -translate-x-1/2 whitespace-nowrap text-[11px] rtl:translate-x-1/2 ${d === 0 ? 'font-semibold text-slate-900 dark:text-white' : 'text-slate-600 dark:text-slate-400'}`}>
                {d === 0 ? t('Today') : `${d > 0 ? '+' : ''}${d} ${t('d')}`}
              </span>
            </span>
          ))}
          {placed.map(({ it, x, lane }) => {
            const d = it.daysRemaining as number;
            const tone = d < 0 ? 'text-rose-700 dark:text-rose-300' : d <= 30 ? 'text-amber-800 dark:text-amber-300' : 'text-blue-800 dark:text-blue-300';
            return (
              <span key={it.key} className={`absolute flex items-center gap-2 ${x > 62 ? 'flex-row-reverse' : ''}`} ref={(n) => {
                if (!n) return;
                n.style.top = `${4 + lane * 40}px`;
                // Pins past ~60% anchor from their end so the label grows back into the axis.
                if (x > 62) { n.style.insetInlineEnd = `calc(${100 - x}% - 14px)`; n.style.insetInlineStart = 'auto'; }
                else { n.style.insetInlineStart = `calc(${x}% - 14px)`; n.style.insetInlineEnd = 'auto'; }
              }}>
                {it.employeeName ? <Avatar name={it.employeeName} size="sm" /> : <span className="h-7 w-7 rounded-full bg-slate-200" />}
                <span className="flex flex-col whitespace-nowrap rounded-lg bg-[color:var(--wg-surface)] px-2 py-0.5 shadow-[0_2px_8px_-4px_rgba(16,24,40,0.3)]">
                  <span className="text-xs font-semibold text-slate-900 dark:text-white">{it.kind}</span>
                  <span className={`text-[11px] font-semibold ${tone}`}>{d < 0 ? `${t('expired')} ${-d} ${t('d ago')}` : d === 0 ? t('expires today') : `${t('in')} ${d} ${t('days')}`}</span>
                </span>
              </span>
            );
          })}
        </div>
      )}

      {/* Text equivalent of the axis (and the only view for the legacy API shape). */}
      <ul className={items.length > 0 ? 'sr-only' : 'flex flex-col gap-1.5'}>
        {[...items, ...legacy].map((it) => (
          <li key={it.key} className="text-[13px] text-slate-800 dark:text-slate-200">
            {it.kind}{it.employeeName ? `, ${it.employeeName}` : ''}: {it.daysRemaining == null ? it.status : it.daysRemaining < 0 ? `${t('expired')} ${-it.daysRemaining} ${t('days ago')}` : `${t('expires in')} ${it.daysRemaining} ${t('days')}`}{it.dateLabel ? ` (${it.dateLabel})` : ''}
          </li>
        ))}
      </ul>
      {items.length === 0 && legacy.length === 0 && (
        <p className="flex items-center gap-2 text-[13px] text-slate-700 dark:text-slate-300"><CheckCircle2 className="h-4 w-4 text-emerald-600" aria-hidden />{t('No iqama, passport, visa or permit expires in the next 90 days.')}</p>
      )}

      {/* Document coverage: employees with every required document on file. */}
      <div className="mt-auto flex flex-wrap items-center gap-5 rounded-xl border border-[color:var(--wg-line)] bg-[color:var(--wg-surface-2)] p-4">
        <Ring3D
          a={Math.max(0, data.summary.activeEmployees - k.missingDocuments)}
          b={Math.min(k.missingDocuments, data.summary.activeEmployees)}
          center={`${Math.max(0, data.summary.activeEmployees - k.missingDocuments)}/${data.summary.activeEmployees}`}
          sub={t('complete')}
          label={`${t('Document coverage')}: ${Math.max(0, data.summary.activeEmployees - k.missingDocuments)} ${t('of')} ${data.summary.activeEmployees} ${t('employees have every required document')}`}
        />
        <div className="flex min-w-[180px] flex-1 flex-col gap-1.5">
          <span className="text-[14px] font-semibold text-slate-900 dark:text-white">{t('Document coverage')}</span>
          <span className="text-[13px] leading-snug text-slate-700 dark:text-slate-300">
            <b className={`tabular-nums ${k.missingDocuments > 0 ? 'text-rose-700 dark:text-rose-300' : 'text-emerald-700 dark:text-emerald-300'}`}>{k.missingDocuments}</b>{' '}
            {k.missingDocuments === 1 ? t('employee is missing a required document') : t('employees are missing a required document')}
            {k.expiredDocuments + k.expiringDocuments > 0 ? `. ${k.expiredDocuments} ${t('uploaded documents expired')}, ${k.expiringDocuments} ${t('expiring')}.` : '.'}
          </span>
          {k.missingDocuments + k.expiredDocuments + k.expiringDocuments > 0 && (
            <Link href="/compliance?tab=employee-documents" className="inline-flex w-fit items-center gap-1 text-[13px] font-semibold text-sapphire hover:underline dark:text-blue-300">
              {t('Review missing documents')} <ArrowRight className="h-3.5 w-3.5" aria-hidden />
            </Link>
          )}
        </div>
      </div>
    </section>
  );
}
