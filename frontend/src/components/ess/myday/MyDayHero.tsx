'use client';

/**
 * The employee home's hero: who and how today is going on the left, the one next step and pay on
 * the right. The gradient is the company home's (`wg-hero`); a quiet day (leave, nothing waiting)
 * uses the calm tint. Net pay is masked until the employee chooses to show it, per session.
 */

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { AlertCircle, ArrowRight, Eye, EyeOff, User } from 'lucide-react';
import type { EssDashboard } from '../../../api/ess';
import { BdiMessage } from '../../BdiMessage';
import { useFormat } from '../../../hooks/useFormat';
import { useT } from '../../../hooks/useT';
import { clockParts, daysBetween, liveWorkedSeconds, whenLabel, type DayView, type NextStep } from '../../../lib/myDay';

/** The masked stand-in for an amount; never the amount itself. */
const MASK = '••••••';
const SEPARATOR = '·';

function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(false);
  useEffect(() => {
    const q = window.matchMedia('(prefers-reduced-motion: reduce)');
    setReduced(q.matches);
    const on = () => setReduced(q.matches);
    q.addEventListener('change', on);
    return () => q.removeEventListener('change', on);
  }, []);
  return reduced;
}

/** Hours worked today, counting up. A local clock only: it never asks the server anything. */
function LiveHours({ attendance }: { attendance: NonNullable<EssDashboard['attendanceToday']> }) {
  const reduced = useReducedMotion();
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    setNow(Date.now());
    // Whole seconds are a lot of change for anyone who asked for less motion: a minute is enough.
    const id = window.setInterval(() => setNow(Date.now()), reduced ? 60_000 : 1000);
    return () => window.clearInterval(id);
  }, [reduced]);
  const secs = liveWorkedSeconds(attendance, now);
  if (secs === null) return null;
  const { hm, ss } = clockParts(secs);
  return (
    <p role="timer" aria-live="off" dir="ltr" className="text-start text-display font-semibold tabular-nums" data-testid="ess-live-hours">
      {hm}<span className="text-h2 font-semibold text-white/90">:{ss}</span>
    </p>
  );
}

export function MyDayHero({ dashboard, firstName, day, next, today, masked, onToggleMask, onRetry, stale }: {
  dashboard: EssDashboard; firstName: string; day: DayView; next: NextStep; today: string;
  masked: boolean; onToggleMask: () => void; onRetry: () => void; stale: boolean;
}) {
  const t = useT();
  const fx = useFormat();
  const p = dashboard.profile;
  const ps = dashboard.payrollSnapshot;
  const h = new Date().getHours();
  const greetingKey = h < 12 ? 'Good morning, {name}' : h < 17 ? 'Good afternoon, {name}' : 'Good evening, {name}';
  const tenure = dashboard.tenureMonths;
  const tenureText = tenure <= 0 ? null
    : tenure < 12 ? t('{months} months of service', { months: tenure })
      : tenure % 12 > 0 ? t('{years} years {months} months of service', { years: Math.floor(tenure / 12), months: tenure % 12 })
        : t('{years} years of service', { years: Math.floor(tenure / 12) });

  const payDate = ps?.nextPayrollDate ?? null;
  const payDays = payDate ? daysBetween(today, payDate.slice(0, 10)) : null;
  // The amount is only in the page while shown: masked, there is nothing to read or to copy.
  const net = ps ? fx.money(ps.netSalary, ps.currency) : '';
  const shownNet = masked && ps ? fx.money(0, ps.currency).replace(/\d[\d.,]*/, MASK) : net;

  const ctaCls = 'wg-press inline-flex h-11 w-fit items-center justify-center gap-2 rounded-xl bg-white px-5 text-body font-semibold text-blue-900 shadow-e2 outline-none hover:bg-blue-50 focus-visible:ring-2 focus-visible:ring-white focus-visible:ring-offset-2 focus-visible:ring-offset-blue-900';
  const ctaBody = next.cta ? <>{next.cta.label}<ArrowRight className="h-4 w-4 rtl:-scale-x-100" aria-hidden="true" /></> : null;

  return (
    <section
      aria-labelledby="myday-hero-title" data-testid="ess-hero" data-tone={day.calm || next.calm ? 'calm' : undefined}
      className={`wg-hero relative grid gap-5 overflow-hidden rounded-2xl p-5 text-white md:grid-cols-5 xl:p-6 ${stale ? 'saturate-50' : ''}`}
    >
      <div className="flex min-w-0 flex-col md:col-span-3">
        <div className="flex items-start gap-3">
          {p.profilePhotoUrl ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img src={p.profilePhotoUrl} alt="" width={48} height={48} decoding="async" className="hidden h-12 w-12 shrink-0 rounded-xl object-cover ring-1 ring-white/30 sm:block" />
          ) : (
            <span aria-hidden="true" className="hidden h-12 w-12 shrink-0 place-items-center rounded-xl bg-white/15 ring-1 ring-white/25 sm:grid"><User className="h-6 w-6" /></span>
          )}
          <div className="min-w-0">
            <p className="text-caption font-semibold uppercase tracking-[0.08em] text-white/90 rtl:text-meta rtl:normal-case rtl:tracking-normal">{fx.date(new Date(), 'full')}</p>
            <h2 id="myday-hero-title" className="mt-0.5 text-h2 font-semibold sm:text-h1"><BdiMessage message={greetingKey} values={{ name: firstName }} /></h2>
            {/* Each fact wraps as a whole, so a department name is never split across two lines. */}
            <ul className="mt-0.5 flex flex-wrap items-center gap-x-1.5 text-body-sm text-white/90">
              {[
                <bdi key="title">{p.jobTitle || t('Employee')}</bdi>,
                p.department ? <bdi key="dept">{p.department}</bdi> : null,
                p.employeeCode ? <bdi key="code">{p.employeeCode}</bdi> : null,
                tenureText ? <span key="tenure">{tenureText}</span> : null,
              ].filter(Boolean).map((fact, i) => (
                <li key={i} className="flex items-center gap-x-1.5">
                  {i > 0 && <span aria-hidden="true" className="text-white/60">{SEPARATOR}</span>}
                  <span>{fact}</span>
                </li>
              ))}
            </ul>
          </div>
        </div>

        <div className="mt-6 flex flex-col gap-1.5 md:mt-auto md:pt-6" data-testid="ess-today">
          <span className={`inline-flex h-7 w-fit items-center gap-2 rounded-full px-3 text-body-sm font-semibold ring-1 ${
            day.pill.tone === 'bad' ? 'bg-white text-rose-800 ring-white' : 'bg-white/15 text-white ring-white/25'}`}>
            {day.pill.tone === 'live' && <span aria-hidden="true" className="h-2 w-2 rounded-full bg-emerald-300 ring-4 ring-emerald-300/25" />}
            {day.pill.tone === 'bad' && <AlertCircle className="h-4 w-4" aria-hidden="true" />}
            {day.pill.text}
          </span>
          {day.figure.kind === 'live' && dashboard.attendanceToday
            ? <LiveHours attendance={dashboard.attendanceToday} />
            : <p className="text-kpi font-semibold tabular-nums">{day.figure.kind === 'text' ? day.figure.text : ''}</p>}
          <p className="text-body-sm text-white/90">{day.line}</p>
          {day.footnote && <p className="text-meta text-white/80">{day.footnote}</p>}
          {day.facts.length > 0 && (
            <dl className="mt-2 flex flex-wrap gap-x-6 gap-y-2">
              {day.facts.map((f) => (
                <div key={f.label} className="flex flex-col-reverse">
                  <dt className="text-meta text-white/85">{f.label}</dt>
                  <dd className="text-body font-semibold"><bdi>{f.value}</bdi></dd>
                </div>
              ))}
            </dl>
          )}
        </div>
      </div>

      <div className="flex min-w-0 flex-col md:col-span-2 md:border-s md:border-white/25 md:ps-6" data-testid="ess-attention">
        <p className="text-meta font-semibold text-white/90">{next.eyebrow}</p>
        <h3 className="mt-1 text-h3 font-semibold">{next.titleMessage ? <BdiMessage message={next.titleMessage.message} values={next.titleMessage.values} /> : next.title}</h3>
        <p className="mb-3 mt-1 text-body-sm text-white/90">{next.why}</p>
        {next.cta?.href && (
          <Link href={next.cta.href} data-primary-action className={`${ctaCls} max-sm:hidden`}>{ctaBody}</Link>
        )}
        {next.cta?.retry && (
          <button type="button" data-primary-action data-retry className={`${ctaCls} max-sm:hidden`} onClick={onRetry}>{ctaBody}</button>
        )}

        <dl className="mt-auto grid grid-cols-2 gap-4 border-t border-white/25 pt-4 max-md:mt-5">
          {ps ? (
            <>
              <div>
                <dt className="text-meta font-semibold text-white/90">{t('Next pay (estimate)')}</dt>
                <dd className="mt-0.5 text-body font-semibold tabular-nums">{payDate ? <bdi>{fx.date(payDate, 'dayMonth')}</bdi> : t('Not scheduled yet')}</dd>
                {payDays !== null && <dd className="text-meta text-white/85">{whenLabel(t, payDays)}</dd>}
              </div>
              <div>
                <dt className="text-meta font-semibold text-white/90">{ps.period ? t('Last net pay, {period}', { period: fx.period(ps.period) }) : t('Last net pay')}</dt>
                <dd className="mt-0.5 text-body font-semibold tabular-nums" data-testid="ess-net-pay"><bdi>{shownNet}</bdi></dd>
                <dd>
                  <button type="button" onClick={onToggleMask} data-testid="ess-net-toggle"
                    aria-label={masked ? t('Show net pay') : t('Hide net pay')}
                    className="wg-press mt-1 inline-flex h-8 items-center gap-1.5 rounded-lg border border-white/40 bg-white/10 px-2.5 text-meta font-semibold text-white outline-none hover:bg-white/20 focus-visible:ring-2 focus-visible:ring-white">
                    {masked ? <Eye className="h-3.5 w-3.5" aria-hidden="true" /> : <EyeOff className="h-3.5 w-3.5" aria-hidden="true" />}
                    {masked ? t('Show') : t('Hide')}
                  </button>
                </dd>
              </div>
            </>
          ) : (
            <div className="col-span-2">
              <dt className="text-meta font-semibold text-white/90">{t('Pay')}</dt>
              <dd className="mt-0.5 text-body font-semibold">{t('Your pay shows here after your first payroll run.')}</dd>
            </div>
          )}
        </dl>
      </div>
    </section>
  );
}
