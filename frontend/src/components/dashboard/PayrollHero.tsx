'use client';

/**
 * The hero band: this period's payroll, its trend, where the run stands, and headcount.
 * The one saturated surface on the page (.wg-hero); everything on it is real data.
 *
 * Honest fallbacks:
 *   - payroll module off or no run on record -> the band leads with workforce instead and says
 *     plainly that no run exists yet, with the action to start one;
 *   - fewer than two runs -> no trend line (one point is not a trend); the single run is stated;
 *   - no headcount history -> the side panel states joiners instead of drawing a flat line.
 */

import Link from 'next/link';
import { ArrowRight } from 'lucide-react';
import type { DashboardFull } from '../../api/dashboard';
import { GrossSplit, HeroSpark, HeroTrend, Stepper } from './charts/Visuals';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { msg } from '../../i18n/translations';
import { enumLabel } from '../EnumLabel';

/** PayrollRun.Status in run order, as the backend defines it (Draft -> Processed ->
 *  PendingFinanceReview -> Approved -> Locked). Payment itself is tracked on the WPS batch,
 *  not the run, so there is no "Paid" step here. Voided runs show their status instead. */
export const RUN_STEPS = [msg('Draft'), msg('Processed'), msg('Review'), msg('Approved'), msg('Locked')];
const STATUS_INDEX: Record<string, number> = { draft: 0, processed: 1, pendingfinancereview: 2, approved: 3, locked: 4 };

function stepIndex(status: string): number {
  return STATUS_INDEX[status.replace(/[\s_-]/g, '').toLowerCase()] ?? -1;
}


/**
 * `currency` is the latest run's company currency and `trendCurrency` the one currency every run on the
 * trend is paid in (lib/payrollCurrency); null when it is not confirmed or the runs are in different
 * currencies. Amounts are never labelled with a guessed currency, and runs in two currencies are never
 * drawn on one line or compared as a percentage.
 */
export function PayrollHero({ data, payrollEnabled, dense = false, currency = null, trendCurrency = null, currencyNote = null }: {
  data: DashboardFull; payrollEnabled: boolean; dense?: boolean;
  currency?: string | null; trendCurrency?: string | null; currencyNote?: string | null;
}) {
  const t = useT();
  const f = useFormat();
  const s = data.summary;
  const o = data.overview;
  const run = payrollEnabled ? o.payrollSummary : null;
  const series = data.payrollTrends.map((p) => ({ label: p.month, value: p.totalNet > 0 ? p.totalNet : null }));
  const ran = series.filter((p) => p.value != null);
  const prev = ran.length >= 2 ? ran[ran.length - 2] : null;
  const latest = ran.length ? ran[ran.length - 1] : null;
  const delta = trendCurrency && prev && latest && prev.value ? ((latest.value as number) - prev.value) / prev.value * 100 : null;
  const step = run ? stepIndex(run.status) : -1;
  const done = step === RUN_STEPS.length - 1;
  const trend = data.analytics?.headcountTrend ?? [];
  const money = (v: number) => f.moneyCompact(v, currency);
  const statusLabel = (status: string) => enumLabel(t, 'PayrollRunStatus', status);

  const side = (
    <div className="flex flex-col gap-2">
      <span className="text-[13px] font-medium text-white/80">{t('Active headcount')}</span>
      <div className="flex items-end justify-between gap-3">
        <span className="text-[34px] font-semibold leading-none tracking-tight tabular-nums">{f.integer(s.activeEmployees)}</span>
        {trend.length >= 3 && (
          <HeroSpark values={trend.map((p) => p.active)} label={t('Active headcount by month: {values}', { values: trend.map((p) => `${f.period(p.month, 'short')} ${f.integer(p.active)}`).join(', ') })} />
        )}
      </div>
      <span className="text-[12px] text-white/85">
        {o.newJoinersThisMonth > 0
          ? <span className="font-semibold text-emerald-200">{t('+{count} joined this month', { count: o.newJoinersThisMonth })}</span>
          : t('No one joined this month')}
        {' · '}{t('{count} on record', { count: s.totalEmployees })}
      </span>
    </div>
  );

  if (!run) {
    return (
      <section aria-labelledby="hero-heading" className="wg-hero relative overflow-hidden rounded-[22px] p-6 text-white sm:p-7">
        <div className="grid gap-7 lg:grid-cols-[minmax(0,1.6fr)_minmax(0,1fr)]">
          <div className="flex flex-col gap-3">
            <h2 id="hero-heading" className="text-[13px] font-medium text-white/80">{t('Payroll')}</h2>
            <p className="text-[34px] font-semibold leading-tight tracking-tight">{payrollEnabled ? t('No payroll run yet') : t('Payroll is switched off')}</p>
            <p className="max-w-prose text-[14px] text-white/85">
              {payrollEnabled
                ? t('Runs, their trend and their progress appear here once the first payroll is calculated.')
                : t('Turn on the payroll module in Tenant Admin to track runs here.')}
            </p>
            {payrollEnabled && (
              <Link href="/payroll" className="wg-press mt-1 inline-flex w-fit items-center gap-2 rounded-xl bg-white px-4 py-2.5 text-[13px] font-semibold text-blue-900 hover:bg-blue-50">
                {t('Start a payroll run')} <ArrowRight className="h-4 w-4" aria-hidden />
              </Link>
            )}
          </div>
          <div className="border-white/15 lg:border-s lg:ps-7">{side}</div>
        </div>
      </section>
    );
  }

  return (
    <section aria-labelledby="hero-heading" className={`wg-hero relative overflow-hidden rounded-[22px] text-white ${dense ? 'p-5' : 'p-6 sm:p-7'}`}>
      <div className={`grid lg:grid-cols-[minmax(0,1.7fr)_minmax(0,1fr)] ${dense ? 'gap-5' : 'gap-7'}`}>
        <div className={`flex min-w-0 flex-col ${dense ? 'gap-2' : 'gap-3'}`}>
          <div className="flex flex-wrap items-center gap-2.5">
            <h2 id="hero-heading" className="text-[13px] font-semibold text-white">{t('Payroll, {period}', { period: f.period(run.periodLabel) })}</h2>
            <span className={`rounded-full px-2.5 py-0.5 text-[12px] font-semibold ${done ? 'bg-emerald-300/20 text-emerald-100' : 'bg-amber-200/20 text-amber-100'}`}>
              {run.payDate
                ? t('{status}, pay date {date}', { status: statusLabel(run.status), date: f.date(run.payDate, 'dayMonth') })
                : statusLabel(run.status)}
            </span>
          </div>
          <div className="flex flex-wrap items-end gap-x-7 gap-y-3">
            <div className="flex flex-col gap-0.5">
              <span className={`${dense ? 'text-[36px]' : 'text-[44px]'} font-semibold leading-none tracking-[-0.03em] tabular-nums`}>{money(run.totalNet)}</span>
              <span className="text-[13px] text-white/90">
                {t('{count, plural, one {Net pay for # employee} other {Net pay for # employees}}', { count: run.employeeCount })}
                {delta != null && prev && (
                  <span className="ms-2 font-semibold text-white">
                    {t('{change} vs {period}', { change: `${delta >= 0 ? '+' : ''}${f.percent(delta, 1)}`, period: f.period(prev.label, 'short') })}
                  </span>
                )}
              </span>
              {currencyNote && <span className="text-[12px] text-white/80">{currencyNote}</span>}
            </div>
            <dl className="flex gap-6 pb-1">
              {[
                { k: msg('Gross'), v: money(run.totalGross) },
                { k: msg('Deductions'), v: money(run.totalDeductions) },
                ...(run.employerContributions ? [{ k: msg('Employer contributions'), v: money(run.employerContributions) }] : []),
              ].map((r) => (
                <div key={r.k} className="flex flex-col gap-0.5">
                  <dt className="text-[12px] text-white/90">{t(r.k)}</dt>
                  <dd className="text-[15px] font-semibold tabular-nums">{r.v}</dd>
                </div>
              ))}
            </dl>
          </div>
          {ran.length >= 2 && trendCurrency ? (
            <HeroTrend points={series.map((p) => ({ ...p, label: f.period(p.label, 'short') }))} format={(v) => f.moneyCompact(v, null)} unit={trendCurrency} label={t('Net payroll by month ({currency})', { currency: trendCurrency })} height={dense ? 172 : 190} />
          ) : (
            <div className="flex flex-col gap-2 pt-1">
              <GrossSplit net={run.totalNet} deductions={run.totalDeductions} employer={run.employerContributions ?? null} format={money} />
              <p className="text-[12px] text-white/80">
                {ran.length >= 2
                  ? t('The monthly trend is not drawn: its runs are not all in one confirmed currency.')
                  : t('First payroll run on record. The monthly trend appears here from the second run.')}
              </p>
            </div>
          )}
        </div>
        <div className={`flex flex-col border-white/15 lg:border-s ${dense ? 'gap-4 lg:ps-5' : 'gap-5 lg:ps-7'}`}>
          <div className="flex flex-col gap-3">
            <span className="text-[13px] font-medium text-white/85">{t('Run progress')}</span>
            {step >= 0 ? <Stepper steps={RUN_STEPS} current={done ? RUN_STEPS.length : step} /> : <span className="text-[13px] text-white/85">{t('Status: {status}', { status: statusLabel(run.status) })}</span>}
            <Link href="/payroll" className="wg-press inline-flex w-fit items-center gap-2 rounded-xl bg-white px-4 py-2.5 text-[13px] font-semibold text-blue-900 hover:bg-blue-50">
              {done ? t('Open payroll run') : step === 3 ? t('Review and lock run') : t('Review payroll run')} <ArrowRight className="h-4 w-4" aria-hidden />
            </Link>
          </div>
          <div className="h-px bg-white/15" />
          {dense ? (
            <p className="text-[13px] text-white/90">
              <span className="text-[22px] font-semibold tabular-nums text-white">{f.integer(s.activeEmployees)}</span> {t('Active')}
              {o.newJoinersThisMonth > 0 ? ` · ${t('+{count} joined this month', { count: o.newJoinersThisMonth })}` : ''}
              {' · '}{t('{count} on record', { count: s.totalEmployees })}
            </p>
          ) : side}
        </div>
      </div>
    </section>
  );
}
