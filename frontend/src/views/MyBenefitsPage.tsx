'use client';

import { useCallback, useEffect, useState } from 'react';
import Link from 'next/link';
import { AlertTriangle, HeartPulse, RefreshCw, UserX } from 'lucide-react';
import { benefitsApi, type EssBenefits } from '@/src/api/benefits';
import { useLocale } from '../contexts/LocaleContext';
import { useAuth } from '../contexts/AuthContext';
import { useFormat } from '../hooks/useFormat';
import { BenefitClaimsPanel } from '../components/benefits/BenefitClaims';
import { BenefitPolicySummary } from '../components/benefits/BenefitPaymentPolicy';
import { useCanWriteEss } from '../components/ess/EssParts';
import { useReleaseA } from '../lib/releaseA';
import { COST_FREQUENCY_LABELS } from '../components/benefits/AdditionalBenefitSummary';


/** Employee self-service: the caller's own benefit enrolments. Read-only. */
export function MyBenefitsPage() {
  const { t } = useLocale();
  const { hasPermission } = useAuth();
  const format = useFormat();
  const canWrite = useCanWriteEss();
  const releaseA = useReleaseA();
  const money = (amount: number) => format.number(amount, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const [showPast, setShowPast] = useState(false);
  const [data, setData] = useState<EssBenefits | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // 404 = the login has no employee record (e.g. a tenant admin account). Retrying cannot fix
  // that, so it gets its own explanatory state instead of the error card.
  const [unlinked, setUnlinked] = useState<string | null>(null);
  const canManageUsers = hasPermission('users.manage');

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    setUnlinked(null);
    try {
      setData(await benefitsApi.mine());
    } catch (e) {
      const res = (e as { response?: { status?: number; data?: { message?: string } } })?.response;
      if (res?.status === 404) setUnlinked(res.data?.message ?? 'Your user account is not linked to an employee record.');
      else setError(res?.data?.message ?? (res?.status === 403 ? 'Self-service access is not enabled for your account.' : 'Could not load your benefits.'));
      setData(null);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);
  const past = (enrollment: EssBenefits['enrollments'][number]) => ['Expired', 'Waived'].includes(enrollment.effectiveStatus ?? '') || enrollment.status !== 'Active' || !!enrollment.effectiveTo && enrollment.effectiveTo < new Date().toISOString().slice(0, 10);
  const pastCount = data?.enrollments.filter(past).length ?? 0;

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">{t("My Benefits")}</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t("Your enrolled benefit plans, employer contributions, and payroll deductions")}</p>
      </div>

      {loading ? (
        <div className="grid gap-3 sm:grid-cols-2" aria-busy="true" aria-label={t("Loading your benefits")}>
          {[0, 1].map((i) => <div key={i} className="h-36 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" />)}
        </div>
      ) : unlinked ? (
        <div data-testid="my-benefits-unlinked" className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 bg-white p-10 text-center dark:border-white/[0.06] dark:bg-white/[0.03]">
          <UserX className="h-10 w-10 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">{t("No employee profile for this login")}</p>
          <p className="max-w-md text-xs text-slate-500 dark:text-slate-400">{t("My Benefits shows the plans of the employee your login belongs to.")} {unlinked}
          </p>
          {canManageUsers && (
            <div className="flex flex-wrap justify-center gap-2">
              <Link href="/user-management" className="rounded-lg bg-slate-800 px-3 py-1.5 text-xs font-semibold text-white hover:bg-slate-700 dark:bg-white/[0.1] dark:hover:bg-white/[0.16]">{t("Link an employee in User Management")}</Link>
              <Link href="/benefits" className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 dark:border-white/[0.1] dark:text-slate-200 dark:hover:bg-white/[0.06]">{t("Manage company benefits")}</Link>
            </div>
          )}
        </div>
      ) : error ? (
        <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <AlertTriangle className="h-8 w-8 text-amber-500" />
          <p className="max-w-lg text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" />{t("Retry")}</button>
        </div>
      ) : !data || data.enrollments.length === 0 ? (
        <div data-testid="my-benefits-empty" className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 bg-white p-10 text-center dark:border-white/[0.06] dark:bg-white/[0.03]">
          <HeartPulse className="h-10 w-10 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">{t("You are not enrolled in any benefit plans")}</p>
          <p className="max-w-md text-xs text-slate-500 dark:text-slate-400">{t("Your grade benefits and any individual benefits assigned by HR appear here with your share of the cost. Contact HR if a benefit is missing.")}</p>
        </div>
      ) : (
        <div className="space-y-3" data-testid="my-benefits-list">
          <div className="grid gap-3 sm:grid-cols-2">
          {data.enrollments.filter(item => showPast || !past(item)).map((e) => {
            const date = new Date().toISOString().slice(0, 10);
            const status = e.effectiveStatus === 'Current' ? 'Active' : e.effectiveStatus === 'Expired' ? 'Ended' : e.effectiveStatus ?? (e.status !== 'Active' ? e.status : e.effectiveFrom > date ? 'Scheduled' : e.effectiveTo && e.effectiveTo < date ? 'Ended' : 'Active');
            const active = status === 'Active';
            return (
              <article key={e.id} className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
                <div className="flex items-start justify-between gap-2">
                  <div>
                    <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">{e.planName}</h2>
                    <p className="text-[11px] text-slate-500 dark:text-slate-400">{t(e.planType)} · {t(e.coverageTier)}</p>
                  </div>
                  <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${active
                    ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300'
                    : 'bg-slate-200 text-slate-600 dark:bg-white/[0.08] dark:text-slate-300'}`}>{t(status)}</span>
                </div>
                <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">{t(e.assignmentSource === 'GradeDefault' ? 'Assigned from your grade' : ['IndividualAdditional', 'IndividualException'].includes(e.assignmentSource) ? 'Additional benefit' : 'Assigned by HR')}{e.hasException && e.assignmentSource === 'GradeDefault' ? ` · ${t('Individual terms')}` : ''}</p>
                <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">{status === 'Superseded' ? t('Replaced before activation after the employee’s draft details changed.') : <>{t('Covered from {date}', { date: format.date(e.effectiveFrom) })}{e.effectiveTo ? ` · ${t('Ends {date}', { date: format.date(e.effectiveTo) })}` : ` · ${t('Ongoing')}`}</>}</p>
                <BenefitPolicySummary policy={e.paymentPolicy} currency={e.currency} />
                {e.grantReason && <p className="mt-2 text-xs text-slate-600 dark:text-slate-300">{e.grantReason}</p>}
                {e.reviewRequired && <p className="mt-2 text-xs font-semibold text-amber-700 dark:text-amber-300">{t('Pending HR review')}</p>}
                {e.reviewDate && <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('Review on {date}', { date: format.date(e.reviewDate) })}</p>}
                {(e.entitlementTier || e.maximumBenefitAmount !== null) && (
                  <div className="mt-3 rounded-xl border border-slate-200 bg-slate-50 p-3 text-xs dark:border-white/[0.06] dark:bg-white/[0.03]">
                    <p className="font-semibold text-slate-800 dark:text-slate-100">{t('Your entitlement: {tier}', { tier: e.entitlementTier || t('Standard tier') })}</p>
                    <p className="mt-1 text-slate-500 dark:text-slate-400">{e.maximumBenefitAmount === null ? t('No monetary cap') : t('Maximum {amount}', { amount: format.money(e.maximumBenefitAmount, e.currency) })} {e.limitPeriod ? `· ${t(e.limitPeriod === 'PerEnrollment' ? 'Per enrollment' : e.limitPeriod)}` : ''}</p>
                    {e.requestedBenefitAmount !== null && <p className="mt-1 text-slate-600 dark:text-slate-300">{t('Enrolled value')}: {format.money(e.requestedBenefitAmount, e.currency)}</p>}
                  </div>
                )}
                {(e.currentEmployeeAmount !== null || !e.paymentPolicy) && (e.currentEmployeeAmount !== null ? (
                  <dl className="mt-3 grid grid-cols-2 gap-2 rounded-xl bg-slate-50 p-3 text-xs dark:bg-white/[0.03]">
                    <div><dt className="text-slate-500 dark:text-slate-400">{t('You pay ({frequency})', { frequency: t(e.contributionFrequency || 'Monthly') })}</dt><dd className="font-mono text-sm font-bold text-slate-800 dark:text-slate-100">{money(e.currentEmployeeAmount)} {e.currency}</dd></div>
                    <div><dt className="text-slate-500 dark:text-slate-400">{t("Employer pays")}</dt><dd className="font-mono text-sm font-bold text-slate-800 dark:text-slate-100">{money(e.currentEmployerAmount ?? 0)} {e.currency}</dd></div>
                  </dl>
                ) : (
                  <p className="mt-3 rounded-xl bg-slate-50 p-3 text-xs text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">{t("No contribution is currently recorded for this plan.")}</p>
                ))}
                {e.assignmentSource === 'IndividualAdditional' && (e.plannedEmployeeCost != null || e.plannedEmployerCost != null) && <details className="mt-3 rounded-lg border border-slate-200 p-3 text-xs dark:border-white/10"><summary className="cursor-pointer font-semibold text-slate-600 dark:text-slate-300">{t('Approved planned costs')}</summary><dl className="mt-2 grid grid-cols-2 gap-2"><div><dt>{t('Planned employer cost')}</dt><dd>{e.plannedEmployerCost == null ? t('Not specified') : format.money(e.plannedEmployerCost, e.currency)}</dd></div><div><dt>{t('Planned employee cost')}</dt><dd>{e.plannedEmployeeCost == null ? t('Not specified') : format.money(e.plannedEmployeeCost, e.currency)}</dd></div></dl>{e.costFrequency && <p className="mt-1">{t(COST_FREQUENCY_LABELS[e.costFrequency])}</p>}<p className="mt-2 text-slate-500 dark:text-slate-400">{t('These are planned costs. Actual contributions and deductions are shown separately.')}</p></details>}
                {(!e.paymentPolicy || e.deductions.length > 0) && <div className="mt-3 border-t border-slate-100 pt-3 dark:border-white/[0.06]">
                  <p className="text-[10px] font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{t("Payroll deductions")}</p>
                  {e.deductions.length === 0 ? (
                    <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t("No benefit deduction has been recorded on payroll yet.")}</p>
                  ) : (
                    <ul className="mt-1.5 space-y-1.5" data-testid={`benefit-deductions-${e.id}`}>
                      {e.deductions.map((d) => (
                        <li key={d.linkId} className="flex items-center justify-between gap-3 rounded-lg bg-slate-50 px-2.5 py-2 text-xs dark:bg-white/[0.03]">
                          <span className="min-w-0 text-slate-600 dark:text-slate-300">
                            <span className="block truncate font-medium text-slate-800 dark:text-slate-100">{d.componentName || d.componentCode}</span>
                            <span>{d.year}-{String(d.month).padStart(2, '0')} · {t(d.runStatus)}</span>
                          </span>
                          <span className="shrink-0 font-mono font-semibold text-slate-800 dark:text-slate-100">{money(d.linkedAmount)} {e.currency}</span>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>}
              </article>
            );
          })}
          </div>
          {!releaseA && data.enrollments.some(item => item.paymentPolicy?.delivery === 'Reimbursement') && <BenefitClaimsPanel benefits={data.enrollments} canWrite={canWrite} onChanged={() => void load()} />}
          {pastCount > 0 && <button type="button" aria-expanded={showPast} onClick={() => setShowPast(value => !value)} className="rounded-lg border border-slate-200 px-3 py-2 text-xs font-semibold text-slate-600 dark:border-white/10 dark:text-slate-300">{showPast ? t('Hide past benefits') : t('Past benefits ({count})', { count: pastCount })}</button>}
        </div>
      )}
    </div>
  );
}
