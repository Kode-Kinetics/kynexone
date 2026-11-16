'use client';

/**
 * The one-screen stage's payroll hero, composed in three bands: who/where (period, status, a
 * one-row run stepper), the figure (net pay, change on last month, the one primary action) beside
 * the 12-month trend, and an inset ledger of the run's own numbers plus headcount. Same data and
 * the same honest fallbacks as PayrollHero, which it defers to when there is no run to show.
 */

import Link from 'next/link';
import { useState } from 'react';
import { ArrowRight } from 'lucide-react';
import type { DashboardFull } from '../../api/dashboard';
import { FillChart, PayrollHero, RUN_STEPS, stepIndex } from './PayrollHero';
import { GrossSplit, HeroTrend, Stepper } from './charts/Visuals';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { enumLabel } from '../EnumLabel';

export function PayrollHeroStage({ data, payrollEnabled, currency = null, trendCurrency = null, currencyNote = null, compact = false }: {
  compact?: boolean; data: DashboardFull; payrollEnabled: boolean; currency?: string | null; trendCurrency?: string | null; currencyNote?: string | null;
}) {
  const t = useT();
  const f = useFormat();
  const [scrub, setScrub] = useState<number | null>(null);
  const o = data.overview;
  const s = data.summary;
  const run = payrollEnabled ? o.payrollSummary : null;
  if (!run) return <PayrollHero data={data} payrollEnabled={payrollEnabled} dense fill currency={currency} trendCurrency={trendCurrency} currencyNote={currencyNote} />;

  const series = data.payrollTrends.map((p) => ({ label: p.month, value: p.totalNet > 0 ? p.totalNet : null }));
  const ran = series.filter((p) => p.value != null);
  const prev = ran.length >= 2 ? ran[ran.length - 2] : null;
  const latest = ran.length ? ran[ran.length - 1] : null;
  const delta = trendCurrency && prev && latest && prev.value ? ((latest.value as number) - prev.value) / prev.value * 100 : null;
  // Reading an earlier month on the chart swaps the figure to that month's net (and its change on the month before).
  // Only net exists per month, so the run's own ledger below dims while another month is shown.
  const latestIdx = series.reduce((m, p, i) => (p.value != null ? i : m), -1);
  const viewing = (() => {
    if (scrub == null || scrub === latestIdx || series[scrub]?.value == null) return null;
    let j = scrub - 1;
    while (j >= 0 && series[j].value == null) j--;
    const pv = j >= 0 ? (series[j].value as number) : null;
    const v = series[scrub].value as number;
    return { value: v, label: series[scrub].label, prevLabel: j >= 0 ? series[j].label : null, delta: trendCurrency && pv ? (v - pv) / pv * 100 : null };
  })();
  const step = stepIndex(run.status);
  const done = step === RUN_STEPS.length - 1;
  const money = (v: number) => f.moneyCompact(v, currency);
  const statusLabel = enumLabel(t, 'PayrollRunStatus', run.status);
  const cta = done ? t('Open payroll run') : step === 3 ? t('Review and lock run') : t('Review payroll run');

  const ledger = [
    { k: t('Gross'), v: money(run.totalGross), sub: null as string | null },
    { k: t('Deductions'), v: money(run.totalDeductions), sub: null },
    ...(run.employerContributions ? [{ k: t('Employer contributions'), v: money(run.employerContributions), sub: null }] : []),
    { k: t('Active headcount'), v: f.integer(s.activeEmployees), sub: o.newJoinersThisMonth > 0 ? t('+{count} joined this month', { count: o.newJoinersThisMonth }) : t('No one joined this month') },
  ];

  return (
    <section aria-labelledby="hero-heading" className="wg-hero relative flex h-full flex-col gap-3 overflow-hidden rounded-2xl p-4 text-white xl:p-5">
      {/* Band 1: where this run stands */}
      <div className="flex flex-wrap items-start justify-between gap-x-6 gap-y-2">
        <div className="flex flex-wrap items-center gap-2.5">
          <h2 id="hero-heading" className="text-caption font-semibold uppercase tracking-[0.08em] text-white rtl:text-meta rtl:normal-case rtl:tracking-normal">{t('Payroll, {period}', { period: f.period(run.periodLabel) })}</h2>
          <span className={`rounded-full px-2.5 py-0.5 text-meta font-semibold ${done ? 'bg-emerald-300/20 text-emerald-100' : 'bg-amber-200/20 text-amber-100'}`}>
            {run.payDate ? t('{status}, pay date {date}', { status: statusLabel, date: f.date(run.payDate, 'dayMonth') }) : statusLabel}
          </span>
        </div>
        <div className="w-[290px] max-w-full">
          {step >= 0
            ? <Stepper steps={RUN_STEPS} current={done ? RUN_STEPS.length : step} labels="current" />
            : <span className="text-body-sm text-white/90">{t('Status: {status}', { status: statusLabel })}</span>}
        </div>
      </div>

      {/* Band 2: the figure beside the trend */}
      <div className={`flex min-h-0 flex-1 items-stretch gap-x-6 gap-y-3 ${compact ? 'items-center' : 'flex-wrap'}`}>
        <div className={`flex flex-col justify-center gap-4 ${compact ? 'flex-1 flex-row flex-wrap items-center justify-between gap-x-4 gap-y-2' : 'min-w-[250px]'}`}>
          <div className="flex flex-col gap-1.5">
            <span className="text-display font-semibold tabular-nums">{viewing ? money(viewing.value) : money(run.totalNet)}</span>
            <span className="flex flex-wrap items-center gap-x-2 gap-y-1 text-body-sm text-white/90">
              {viewing ? (
                <>
                  {viewing.delta != null && (
                    <span className="rounded-full bg-white/15 px-2 py-0.5 text-meta font-semibold text-white ring-1 ring-white/20">
                      {t('{change} vs {period}', { change: `${viewing.delta >= 0 ? '+' : ''}${f.percent(viewing.delta, 1)}`, period: f.period(viewing.prevLabel as string, 'short') })}
                    </span>
                  )}
                  {t('Net pay, {period}', { period: f.period(viewing.label, 'long') })}
                </>
              ) : (<>
              {delta != null && prev && (
                <span className="rounded-full bg-white/15 px-2 py-0.5 text-meta font-semibold text-white ring-1 ring-white/20">
                  {t('{change} vs {period}', { change: `${delta >= 0 ? '+' : ''}${f.percent(delta, 1)}`, period: f.period(prev.label, 'short') })}
                </span>
              )}
              {t('{count, plural, one {Net pay for # employee} other {Net pay for # employees}}', { count: run.employeeCount })}
              </>)}
            </span>
            {currencyNote && <span className="text-meta text-white/80">{currencyNote}</span>}
          </div>
          <Link href="/payroll" className="wg-press group inline-flex h-10 w-fit items-center gap-2 rounded-full bg-white pe-1.5 ps-4 text-body-sm font-semibold text-blue-900 shadow-[0_8px_20px_-10px_rgba(8,20,80,0.6)] hover:bg-blue-50">
            {cta}
            <span aria-hidden className="grid h-7 w-7 place-items-center rounded-full bg-blue-900/10 transition-transform duration-base ease-standard group-hover:translate-x-0.5 rtl:group-hover:-translate-x-0.5">
              <ArrowRight className="h-4 w-4 rtl:-scale-x-100" />
            </span>
          </Link>
        </div>
        <div className={`min-w-[260px] flex-1 flex-col justify-end ${compact ? 'hidden' : 'flex'}`}>
          {ran.length >= 2 && trendCurrency ? (
            <FillChart>{(h) => (
              <HeroTrend points={series.map((p) => ({ ...p, label: f.period(p.label, 'short') }))} format={(v) => f.moneyCompact(v, null)} unit={trendCurrency} onScrub={setScrub}
                label={t('Net payroll by month, {currency}', { currency: trendCurrency })} height={h} />
            )}</FillChart>
          ) : (
            <div className="flex flex-col gap-2">
              <GrossSplit net={run.totalNet} deductions={run.totalDeductions} employer={run.employerContributions ?? null} format={money} />
              <p className="text-meta text-white/80">
                {ran.length >= 2
                  ? t('The monthly trend is not drawn: its runs are not all in one confirmed currency.')
                  : t('First payroll run on record. The monthly trend appears here from the second run.')}
              </p>
            </div>
          )}
        </div>
      </div>

      {/* Band 3: the run's ledger, inset */}
      <dl className={`grid shrink-0 grid-cols-2 gap-px overflow-hidden rounded-2xl bg-white/15 ring-1 ring-white/15 transition-opacity duration-fast sm:grid-cols-4 ${viewing ? 'opacity-50' : ''}`}>
        {ledger.map((c) => (
          <div key={c.k} className="flex flex-col gap-0.5 bg-blue-950/30 px-3.5 py-2">
            <dt className="text-meta text-white/85">{c.k}</dt>
            <dd className="flex items-baseline gap-2 text-body font-semibold tabular-nums">
              {c.v}
              {c.sub && <span className="text-meta font-medium text-white/80">{c.sub}</span>}
            </dd>
          </div>
        ))}
      </dl>
    </section>
  );
}
