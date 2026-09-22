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
import { fmtMoney } from './dashboardModel';
import { GrossSplit, HeroSpark, HeroTrend, Stepper } from './charts/Visuals';
import { useT } from '../../hooks/useT';

/** PayrollRun.Status in run order, as the backend defines it (Draft -> Processed ->
 *  PendingFinanceReview -> Approved -> Locked). Payment itself is tracked on the WPS batch,
 *  not the run, so there is no "Paid" step here. Voided runs show their status instead. */
export const RUN_STEPS = ['Draft', 'Processed', 'Review', 'Approved', 'Locked'];
const STATUS_INDEX: Record<string, number> = { draft: 0, processed: 1, pendingfinancereview: 2, approved: 3, locked: 4 };

function stepIndex(status: string): number {
  return STATUS_INDEX[status.replace(/[\s_-]/g, '').toLowerCase()] ?? -1;
}

/** "PendingFinanceReview" -> "Pending finance review". */
function statusLabel(status: string): string {
  const w = status.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  return w.charAt(0).toUpperCase() + w.slice(1);
}

const MONTH_LONG: Record<string, string> = {
  Jan: 'January', Feb: 'February', Mar: 'March', Apr: 'April', May: 'May', Jun: 'June',
  Jul: 'July', Aug: 'August', Sep: 'September', Oct: 'October', Nov: 'November', Dec: 'December',
};

export function PayrollHero({ data, payrollEnabled, dense = false }: { data: DashboardFull; payrollEnabled: boolean; dense?: boolean }) {
  const t = useT();
  const s = data.summary;
  const o = data.overview;
  const run = payrollEnabled ? o.payrollSummary : null;
  const series = data.payrollTrends.map((p) => ({ label: p.month, value: p.totalNet > 0 ? p.totalNet : null }));
  const ran = series.filter((p) => p.value != null);
  const prev = ran.length >= 2 ? ran[ran.length - 2] : null;
  const latest = ran.length ? ran[ran.length - 1] : null;
  const delta = prev && latest && prev.value ? ((latest.value as number) - prev.value) / prev.value * 100 : null;
  const step = run ? stepIndex(run.status) : -1;
  const done = step === RUN_STEPS.length - 1;
  const trend = data.analytics?.headcountTrend ?? [];
  const money = (v: number) => fmtMoney(v);

  const side = (
    <div className="flex flex-col gap-2">
      <span className="text-[13px] font-medium text-white/80">{t('Active headcount')}</span>
      <div className="flex items-end justify-between gap-3">
        <span className="text-[34px] font-semibold leading-none tracking-tight tabular-nums">{s.activeEmployees.toLocaleString()}</span>
        {trend.length >= 3 && (
          <HeroSpark values={trend.map((p) => p.active)} label={`${t('Active headcount by month')}: ${trend.map((p) => `${p.month} ${p.active}`).join(', ')}`} />
        )}
      </div>
      <span className="text-[12px] text-white/85">
        {o.newJoinersThisMonth > 0
          ? <><span className="font-semibold text-emerald-200">+{o.newJoinersThisMonth} {t('joined')}</span> {t('this month')}. </>
          : `${t('No one joined this month')}. `}
        {s.totalEmployees.toLocaleString()} {t('on record')}.
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

  const periodMonth = run.periodLabel.split(' ')[0];
  return (
    <section aria-labelledby="hero-heading" className={`wg-hero relative overflow-hidden rounded-[22px] text-white ${dense ? 'p-5' : 'p-6 sm:p-7'}`}>
      <div className={`grid lg:grid-cols-[minmax(0,1.7fr)_minmax(0,1fr)] ${dense ? 'gap-5' : 'gap-7'}`}>
        <div className={`flex min-w-0 flex-col ${dense ? 'gap-2' : 'gap-3'}`}>
          <div className="flex flex-wrap items-center gap-2.5">
            <h2 id="hero-heading" className="text-[13px] font-semibold text-white">{t('Payroll')}, {MONTH_LONG[periodMonth] ?? periodMonth} {run.periodLabel.split(' ')[1]}</h2>
            <span className={`rounded-full px-2.5 py-0.5 text-[12px] font-semibold ${done ? 'bg-emerald-300/20 text-emerald-100' : 'bg-amber-200/20 text-amber-100'}`}>
              {statusLabel(run.status)}{run.payDate ? `, ${t('pay date')} ${new Date(run.payDate).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })}` : ''}
            </span>
          </div>
          <div className="flex flex-wrap items-end gap-x-7 gap-y-3">
            <div className="flex flex-col gap-0.5">
              <span className={`${dense ? 'text-[36px]' : 'text-[44px]'} font-semibold leading-none tracking-[-0.03em] tabular-nums`}>{money(run.totalNet)}</span>
              <span className="text-[13px] text-white/90">
                {t('Net pay for')} {run.employeeCount.toLocaleString()} {run.employeeCount === 1 ? t('employee') : t('employees')}
                {delta != null && prev && (
                  <span className="ms-2 font-semibold text-white">
                    {delta >= 0 ? '+' : ''}{delta.toFixed(1)}% {t('vs')} {prev.label}
                  </span>
                )}
              </span>
            </div>
            <dl className="flex gap-6 pb-1">
              {[
                { k: 'Gross', v: money(run.totalGross) },
                { k: 'Deductions', v: money(run.totalDeductions) },
                ...(run.employerContributions ? [{ k: 'Employer contributions', v: money(run.employerContributions) }] : []),
              ].map((r) => (
                <div key={r.k} className="flex flex-col gap-0.5">
                  <dt className="text-[12px] text-white/90">{t(r.k)}</dt>
                  <dd className="text-[15px] font-semibold tabular-nums">{r.v}</dd>
                </div>
              ))}
            </dl>
          </div>
          {ran.length >= 2 ? (
            <HeroTrend points={series} format={(v) => fmtMoney(v).replace('SAR ', '')} label={t('Net payroll by month, SAR')} height={dense ? 172 : 190} />
          ) : (
            <div className="flex flex-col gap-2 pt-1">
              <GrossSplit net={run.totalNet} deductions={run.totalDeductions} employer={run.employerContributions ?? null} format={money} />
              <p className="text-[12px] text-white/80">{t('First payroll run on record. The monthly trend appears here from the second run.')}</p>
            </div>
          )}
        </div>
        <div className={`flex flex-col border-white/15 lg:border-s ${dense ? 'gap-4 lg:ps-5' : 'gap-5 lg:ps-7'}`}>
          <div className="flex flex-col gap-3">
            <span className="text-[13px] font-medium text-white/85">{t('Run progress')}</span>
            {step >= 0 ? <Stepper steps={RUN_STEPS} current={done ? RUN_STEPS.length : step} /> : <span className="text-[13px] text-white/85">{t('Status')}: {statusLabel(run.status)}</span>}
            <Link href="/payroll" className="wg-press inline-flex w-fit items-center gap-2 rounded-xl bg-white px-4 py-2.5 text-[13px] font-semibold text-blue-900 hover:bg-blue-50">
              {done ? t('Open payroll run') : step === 3 ? t('Review and lock run') : t('Review payroll run')} <ArrowRight className="h-4 w-4" aria-hidden />
            </Link>
          </div>
          <div className="h-px bg-white/15" />
          {dense ? (
            <p className="text-[13px] text-white/90">
              <span className="text-[22px] font-semibold tabular-nums text-white">{s.activeEmployees.toLocaleString()}</span> {t('active')}
              {o.newJoinersThisMonth > 0 ? `, +${o.newJoinersThisMonth} ${t('joined this month')}` : ''}. {s.totalEmployees.toLocaleString()} {t('on record')}.
            </p>
          ) : side}
        </div>
      </div>
    </section>
  );
}
