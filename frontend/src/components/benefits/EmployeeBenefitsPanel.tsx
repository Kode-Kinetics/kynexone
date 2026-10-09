'use client';

import { useCallback, useEffect, useState } from 'react';
import { HeartPulse, Plus, RefreshCw } from 'lucide-react';
import { benefitsApi, benefitsErrorMessage, type EmployeeBenefitPackage, type EmployeeBenefitPackageItem, type BenefitPlan } from '@/src/api/benefits';
import { useAuth } from '@/src/contexts/AuthContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import { useReleaseA } from '@/src/lib/releaseA';
import { AssignmentLabel, EnrollmentDrawer } from './BenefitEnrollmentDrawer';
import { BenefitClaimsPanel } from './BenefitClaims';
import { BenefitPolicySummary } from './BenefitPaymentPolicy';
import { BenefitChecklist } from './BenefitChecklist';
import { AdditionalBenefitForm } from './AdditionalBenefitForm';
import { AdditionalBenefitRequestDetail } from './AdditionalBenefitRequestDetail';
import { CARD, PRIMARY, SECONDARY, FormError, StatusPill } from './benefitUi';

export interface EmployeeBenefitsPanelProps {
  employeeId: number;
  employeeName: string;
  companyId: string | null;
  gradeId?: string | null;
}

/** The employee-scoped HR view. Loads only when its parent renders the Benefits tab. */
export function EmployeeBenefitsPanel({ employeeId, employeeName, companyId }: EmployeeBenefitsPanelProps) {
  const { t } = useLocale();
  const { hasPermission } = useAuth();
  const format = useFormat();
  const releaseA = useReleaseA();
  const canRecord = hasPermission('employees.approve');
  const canPropose = hasPermission('employees.write') && !releaseA;
  const [plans, setPlans] = useState<BenefitPlan[]>([]);
  const [data, setData] = useState<EmployeeBenefitPackage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [openEnrollment, setOpenEnrollment] = useState<{ id: string; adjust?: boolean } | null>(null);
  const [additionalForm, setAdditionalForm] = useState<{ existing?: EmployeeBenefitPackageItem } | null>(null);
  const [requestId, setRequestId] = useState<string | null>(null);
  const load = useCallback(async () => {
    setLoading(true); setError(null);
    try {
      const [availablePlans, employeeEnrollments] = await Promise.all([
        benefitsApi.listPlans(companyId ?? undefined), benefitsApi.employeePackage(employeeId),
      ]);
      setPlans(availablePlans); setData(employeeEnrollments);
    } catch (err) { setError(benefitsErrorMessage(err, t('Could not load employee benefits.'))); }
    finally { setLoading(false); }
  }, [companyId, employeeId, t]);
  useEffect(() => { void load(); }, [load]);

  const current = data?.enrollments.filter(item => ['Current', 'Scheduled'].includes(item.effectiveStatus)) ?? [];
  const previous = data?.enrollments.filter(item => !['Current', 'Scheduled'].includes(item.effectiveStatus)) ?? [];
  const pending = data?.additionalRequests.filter(item => item.status === 'Pending') ?? [];
  const requestHistory = data?.additionalRequests.filter(item => item.status !== 'Pending') ?? [];
  const benefitList = (items: EmployeeBenefitPackageItem[]) => <ul className="grid grid-cols-[repeat(auto-fit,minmax(min(100%,240px),1fr))] gap-3">{items.map(enrollment => {
    const status = enrollment.effectiveStatus === 'Current' ? 'Active' : enrollment.effectiveStatus === 'Expired' ? 'Ended' : enrollment.effectiveStatus;
    const ongoing = ['Active', 'Scheduled'].includes(status);
    return <li key={enrollment.id} className={`${CARD} min-w-0 p-4`}>
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0"><p className="font-semibold text-slate-800 dark:text-slate-100">{enrollment.planName}</p><p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{enrollment.entitlementTier || enrollment.coverageTier}</p></div>
        <StatusPill active={status === 'Active'} label={status} />
      </div>
      <div className="mt-3 text-xs text-slate-600 dark:text-slate-300"><AssignmentLabel enrollment={enrollment} /></div>
      {status === 'Superseded' && <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">{t('Replaced before activation after the employee’s draft details changed.')}</p>}
      <p className="mt-3 text-sm font-semibold text-slate-800 dark:text-slate-100">{enrollment.maximumBenefitAmount === null ? t('No monetary cap') : format.money(enrollment.maximumBenefitAmount, enrollment.currency)}<span className="text-xs font-normal text-slate-500 dark:text-slate-400"> · {t(enrollment.limitPeriod === 'PerEnrollment' ? 'Per enrollment' : enrollment.limitPeriod)}</span></p>
      <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('Starts {date}', { date: format.date(enrollment.effectiveFrom) })}{enrollment.effectiveTo ? ` · ${t('Ends {date}', { date: format.date(enrollment.effectiveTo) })}` : ''}</p>
      <BenefitPolicySummary policy={enrollment.paymentPolicy} currency={enrollment.currency} />
      {enrollment.reviewRequired && <p className="mt-2 rounded-lg bg-amber-50 p-2 text-xs font-semibold text-amber-800 dark:bg-amber-500/10 dark:text-amber-200">{t('Review required')}{enrollment.reviewDate ? ` · ${format.date(enrollment.reviewDate)}` : ''}</p>}
      {!enrollment.reviewRequired && enrollment.reviewDate && <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('Review on {date}', { date: format.date(enrollment.reviewDate) })}</p>}
      <div className="mt-3 flex flex-wrap gap-2"><button type="button" className={SECONDARY} onClick={() => setOpenEnrollment({ id: enrollment.id })}>{t('View benefit')}</button>
        {ongoing && enrollment.assignmentSource === 'IndividualAdditional' && canPropose && <button type="button" className={SECONDARY} onClick={() => setAdditionalForm({ existing: enrollment })}>{t('Amend additional benefit')}</button>}
        {ongoing && enrollment.assignmentSource !== 'IndividualAdditional' && canRecord && !releaseA && <button type="button" className={SECONDARY} onClick={() => setOpenEnrollment({ id: enrollment.id, adjust: true })}>{t('Adjust existing benefit')}</button>}
      </div>
    </li>;
  })}</ul>;

  return <section className="space-y-4" data-testid="employee-benefits-panel" aria-label={t('Employee benefits')}>
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div><h3 className="font-semibold text-slate-800 dark:text-slate-100">{t('Benefits')}</h3><p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('Benefits for {employee}', { employee: employeeName })}</p></div>
      <div className="flex flex-wrap gap-2"><button type="button" className={SECONDARY} onClick={() => void load()} disabled={loading}><RefreshCw className="h-3.5 w-3.5" />{t('Refresh')}</button>
        {canPropose && <button type="button" className={PRIMARY} disabled={loading} onClick={() => setAdditionalForm({})}><Plus className="h-3.5 w-3.5" />{t('Manage benefits')}</button>}
      </div>
    </div>
    {loading ? <div aria-busy="true" aria-label={t('Loading benefits')} className="h-36 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" />
      : error ? <FormError message={error} />
        : <>
          {pending.length > 0 && <section className="space-y-2"><h4 className="text-sm font-semibold text-slate-700 dark:text-slate-200">{t('Pending benefit requests')}</h4>{pending.map(request => <button type="button" key={request.id} className={`${CARD} flex w-full items-center justify-between gap-3 p-3 text-start text-xs`} onClick={() => setRequestId(request.id)}><span className="font-semibold text-slate-800 dark:text-slate-100">{request.planName}</span><StatusPill active={false} label={t(['End', 'Cancel'].includes(request.operation ?? '') ? 'Removal pending approval' : 'Addition pending approval')} /></button>)}<p className="text-xs text-slate-500 dark:text-slate-400">{t('Pending requests do not change the employee’s benefits until approved.')}</p></section>}
          {current.length ? benefitList(current) : <div className={`${CARD} flex flex-col items-center gap-2 p-7 text-center`}><HeartPulse className="h-7 w-7 text-slate-300" /><p className="text-sm text-slate-600 dark:text-slate-300">{t('No current or upcoming benefits.')}</p></div>}
          {data?.enrollments.some(item => item.paymentPolicy?.delivery === 'Reimbursement') && <BenefitClaimsPanel employeeId={employeeId} benefits={data.enrollments} canWrite={canPropose} onChanged={() => void load()} />}
          {requestHistory.length > 0 && <details className={`${CARD} p-4`}><summary className="cursor-pointer text-sm font-semibold text-slate-600 dark:text-slate-300">{t('Benefit request history')}</summary><div className="mt-3 space-y-2">{requestHistory.map(request => <button type="button" key={request.id} onClick={() => setRequestId(request.id)} className="flex w-full items-center justify-between gap-3 rounded-lg border border-slate-200 p-3 text-start text-xs dark:border-white/10"><span className="text-slate-700 dark:text-slate-200">{request.planName}</span><StatusPill active={request.status === 'Approved'} label={request.status} /></button>)}</div></details>}
          {previous.length > 0 && <details className={`${CARD} p-4`}><summary className="cursor-pointer text-sm font-semibold text-slate-600 dark:text-slate-300">{t('Past benefits ({count})', { count: previous.length })}</summary><div className="mt-3">{benefitList(previous)}</div></details>}
        </>}
    {openEnrollment && <EnrollmentDrawer key={openEnrollment.id} enrollmentId={openEnrollment.id} startEditing={openEnrollment.adjust} plans={plans} canRecord={canRecord} canApplyException={canRecord && !releaseA} onChanged={id => { setOpenEnrollment({ id }); void load(); }} onClose={() => setOpenEnrollment(null)} />}
    {additionalForm && !additionalForm.existing && <BenefitChecklist plans={plans} employee={{ id: employeeId, fullName: employeeName }} onClose={() => setAdditionalForm(null)} onChanged={() => void load()} />}
    {additionalForm?.existing && <AdditionalBenefitForm plans={plans} employee={{ id: employeeId, fullName: employeeName }} existing={additionalForm.existing} onClose={() => setAdditionalForm(null)} onRequested={request => { setAdditionalForm(null); setRequestId(request.id); void load(); }} />}
    {requestId && <AdditionalBenefitRequestDetail requestId={requestId} onClose={() => setRequestId(null)} />}
  </section>;
}
