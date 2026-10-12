'use client';

import { useEffect, useState } from 'react';
import { benefitsApi, benefitsErrorMessage, type AdditionalBenefitInput, type AdditionalBenefitRequest, type AdditionalBenefitTerms, type BenefitEligibilityCheck, type BenefitPlan, type EmployeeBenefitPackageItem } from '@/src/api/benefits';
import { employeesApi, type EmployeeListItem } from '@/src/api/employees';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useAppToast } from '@/src/components/ui/AppToast';
import { useAuth } from '@/src/contexts/AuthContext';
import { BenefitPolicySummary, policyTreatment } from './BenefitPaymentPolicy';
import { COVERAGE_TIERS, FormError, INPUT, LABEL, Modal, PRIMARY, SECONDARY, today } from './benefitUi';
import { AdditionalBenefitSummary, COST_FREQUENCY_LABELS, PERIOD_LABELS, TREATMENT_LABELS } from './AdditionalBenefitSummary';

export interface BenefitEmployee { id: number; fullName: string; }

export function AdditionalBenefitForm({ plans, employee: fixedEmployee, existing, initialPlanId, onClose, onRequested }: {
  plans: BenefitPlan[]; employee?: BenefitEmployee; existing?: EmployeeBenefitPackageItem; initialPlanId?: string;
  onClose: () => void; onRequested: (request: AdditionalBenefitRequest) => void;
}) {
  const { t } = useLocale();
  const { hasPermission, hasRole } = useAuth();
  const canConfigure = hasPermission('approvals.manage') && (hasRole('Admin') || hasRole('HR Manager'));
  const toast = useAppToast();
  const [employee, setEmployee] = useState<BenefitEmployee | null>(fixedEmployee ?? null);
  const [search, setSearch] = useState('');
  const [results, setResults] = useState<EmployeeListItem[]>([]);
  const [searching, setSearching] = useState(false);
  const [planId, setPlanId] = useState(existing?.benefitPlanId ?? initialPlanId ?? '');
  const [form, setForm] = useState({
    coverageTier: existing?.coverageTier ?? 'Employee', entitlementTier: existing?.entitlementTier ?? '',
    maximumBenefitAmount: existing?.maximumBenefitAmount?.toString() ?? '', requestedBenefitAmount: existing?.requestedBenefitAmount?.toString() ?? '',
    limitPeriod: existing?.limitPeriod ?? 'Annual', effectiveFrom: today() > (existing?.effectiveFrom ?? '') ? today() : existing!.effectiveFrom,
    effectiveTo: existing?.effectiveTo ?? '', reviewDate: existing?.reviewDate ?? '',
    reason: existing?.grantReason ?? '', internalJustification: '',
    treatment: existing?.treatment ?? 'OtherNonCash', plannedEmployerCost: existing?.plannedEmployerCost?.toString() ?? '',
    plannedEmployeeCost: existing?.plannedEmployeeCost?.toString() ?? '', costFrequency: existing?.costFrequency ?? 'Monthly',
  });
  const [duration, setDuration] = useState(existing?.effectiveTo ? 'end' : 'review');
  const [check, setCheck] = useState<BenefitEligibilityCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [checkError, setCheckError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [setupUrl, setSetupUrl] = useState<string | null>(null);
  const [reviewing, setReviewing] = useState(false);
  const [saving, setSaving] = useState(false);
  const plan = plans.find(item => item.id === planId);
  const paymentPolicy = existing?.paymentPolicy ?? plan?.paymentPolicy;
  const configuredPolicy = !!existing?.paymentPolicy || (plan?.policyVersion ?? 0) > 0;
  const set = (key: keyof typeof form, value: string) => setForm(current => ({ ...current, [key]: value }));
  useEffect(() => {
    if (!plan?.effectiveTo) return;
    setDuration('end');
    setForm(current => ({ ...current, effectiveTo: existing?.effectiveTo || plan.effectiveTo || '' }));
  }, [plan?.id, plan?.effectiveTo, existing?.effectiveTo]);
  const amount = (value: string) => value.trim() ? Number(value) : null;
  const terms: AdditionalBenefitTerms = {
    coverageTier: form.coverageTier, entitlementTier: form.entitlementTier.trim(), maximumBenefitAmount: amount(form.maximumBenefitAmount), requestedBenefitAmount: amount(form.requestedBenefitAmount),
    limitPeriod: form.limitPeriod as AdditionalBenefitTerms['limitPeriod'], effectiveFrom: form.effectiveFrom,
    effectiveTo: duration === 'end' ? form.effectiveTo || null : null, reviewDate: duration === 'review' ? form.reviewDate || null : null,
    reason: form.reason.trim(), internalJustification: form.internalJustification.trim(), treatment: configuredPolicy && paymentPolicy ? policyTreatment(paymentPolicy) : form.treatment,
    plannedEmployerCost: amount(form.plannedEmployerCost), plannedEmployeeCost: amount(form.plannedEmployeeCost),
    costFrequency: form.plannedEmployerCost.trim() || form.plannedEmployeeCost.trim() ? form.costFrequency : null,
  };

  useEffect(() => {
    if (employee || search.trim().length < 2) { setResults([]); return; }
    let active = true;
    const timer = setTimeout(() => {
      setSearching(true);
      employeesApi.list({ search: search.trim(), pageSize: 8 }).then(response => {
        if (active) setResults(response.items.filter(item => !['Offboarded', 'Terminated', 'Deleted', 'Exited'].includes(item.status)));
      }).catch(() => { if (active) setResults([]); }).finally(() => { if (active) setSearching(false); });
    }, 250);
    return () => { active = false; clearTimeout(timer); };
  }, [employee, search]);

  useEffect(() => {
    setCheck(null); setCheckError(null); setChecking(false);
    if (!employee || !planId || !form.effectiveFrom) return;
    let active = true;
    setChecking(true);
    benefitsApi.checkEligibility(planId, employee.id, form.effectiveFrom).then(result => { if (active) setCheck(result); })
      .catch(err => { if (active) setCheckError(benefitsErrorMessage(err, t('Could not check eligibility.'))); })
      .finally(() => { if (active) setChecking(false); });
    return () => { active = false; };
  }, [employee, planId, form.effectiveFrom, t]);

  const blockedChecks = check?.checks.filter(item => ['plan_active', 'company_scope', 'plan_window'].includes(item.key) && !item.passed) ?? [];
  const planAllowed = !!check && blockedChecks.length === 0 && (existing || !check.alreadyEnrolled);
  const review = (event: React.FormEvent) => {
    event.preventDefault(); setError(null);
    if (!employee || !plan || !planAllowed) return;
    if (!terms.entitlementTier || !terms.reason || !terms.internalJustification) { setError(t('Enter the entitlement, employee reason and internal justification.')); return; }
    if ([terms.maximumBenefitAmount, terms.requestedBenefitAmount].some(value => value !== null && (!Number.isFinite(value) || value <= 0))) { setError(t('Benefit amounts must be greater than zero.')); return; }
    if ([terms.plannedEmployeeCost, terms.plannedEmployerCost].some(value => value !== null && (!Number.isFinite(value) || value < 0))) { setError(t('Planned costs cannot be negative.')); return; }
    if (terms.maximumBenefitAmount !== null && terms.requestedBenefitAmount !== null && terms.requestedBenefitAmount > terms.maximumBenefitAmount) { setError(t('The enrolled value cannot exceed the individual benefit limit.')); return; }
    if (!(terms.effectiveTo || terms.reviewDate)) { setError(t('Set an end date or a review date.')); return; }
    setReviewing(true);
  };
  const submit = async () => {
    if (!employee || !plan || saving) return;
    setSaving(true); setError(null); setSetupUrl(null);
    try {
      const input: AdditionalBenefitInput = { ...terms, employeeId: employee.id, benefitPlanId: plan.id,
        ...(existing ? { enrollmentId: existing.id, expectedUpdatedAtUtc: existing.updatedAtUtc } : {}) };
      const request = await benefitsApi.requestAdditional(input);
      toast.success(t('Additional benefit sent for approval.'));
      onRequested(request);
    } catch (err) {
      const data = (err as { response?: { data?: { code?: string; setupUrl?: string } } }).response?.data;
      setSetupUrl(data?.code === 'approval_route_not_configured' && data.setupUrl?.startsWith('/') && !data.setupUrl.startsWith('//') ? data.setupUrl : null);
      setError(benefitsErrorMessage(err, t('Could not submit the additional benefit.')));
    } finally { setSaving(false); }
  };

  return <Modal title={t(existing ? 'Amend additional benefit' : 'Add additional benefit')} onClose={saving ? () => {} : onClose} wide>
    <FormError message={error} />
    {setupUrl && <p className="my-3 text-xs text-slate-600 dark:text-slate-300">{t('An approval route is required before this request can be submitted.')} {canConfigure ? <a href={setupUrl} className="font-semibold text-sapphire underline">{t('Configure approval workflow')}</a> : t('Contact an administrator to configure additional benefit approvals.')}</p>}
    {reviewing && employee && plan ? <div className="space-y-4">
      <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Review additional benefit')}</h3>
      <AdditionalBenefitSummary terms={terms} currency={plan.currency} baseline={existing} employeeName={employee.fullName} planName={plan.name} paymentPolicy={paymentPolicy} />
      <p className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-900 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-200">{t('This request needs independent approval. The benefit is assigned only after approval.')}</p>
      <div className="flex justify-end gap-2"><button type="button" disabled={saving} className={SECONDARY} onClick={() => setReviewing(false)}>{t('Back')}</button><button type="button" disabled={saving} className={PRIMARY} onClick={() => void submit()}>{t(saving ? 'Submitting…' : 'Submit for approval')}</button></div>
    </div> : <form className="space-y-4" onSubmit={review} aria-label={t('Additional benefit details')}>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('Add terms for one employee. Their grade defaults stay unchanged.')}</p>
      {employee ? <div className="flex items-center justify-between gap-2 rounded-lg bg-slate-50 p-3 text-sm dark:bg-white/5"><span className="font-semibold text-slate-700 dark:text-slate-200">{employee.fullName}</span>{!fixedEmployee && <button type="button" className={SECONDARY} onClick={() => { setEmployee(null); setSearch(''); }}>{t('Change employee')}</button>}</div>
        : <div><label className={LABEL}>{t('Search employee')}<input className={INPUT} aria-label={t('Search employee')} value={search} onChange={event => setSearch(event.target.value)} /></label>{searching && <p className="mt-1 text-xs text-slate-500">{t('Searching…')}</p>}<ul role="listbox" aria-label={t('Employees')} className="mt-1 space-y-1">{results.map(item => <li key={item.id}><button type="button" role="option" aria-selected={false} className="w-full rounded-lg border border-slate-200 p-2 text-start text-xs hover:bg-slate-50 dark:border-white/10 dark:text-slate-200" onClick={() => setEmployee({ id: item.id, fullName: item.fullName })}>{item.fullName} · {item.employeeCode} · {t(item.status)}</button></li>)}</ul></div>}
      <label className={LABEL}>{t('Benefit plan')}<select aria-label={t('Benefit plan')} className={INPUT} required value={planId} disabled={!!existing} onChange={event => setPlanId(event.target.value)}><option value="">{t('Select benefit')}</option>{plans.filter(item => item.isActive).map(item => <option key={item.id} value={item.id}>{item.name} · {item.currency}</option>)}</select></label>
      <div className="grid gap-3 sm:grid-cols-2">
        <label className={LABEL}>{t('Starts')}<input type="date" className={INPUT} required min={[today(), plan?.effectiveFrom ?? '', existing?.effectiveFrom ?? ''].sort().at(-1)} max={plan?.effectiveTo ?? undefined} value={form.effectiveFrom} onChange={event => set('effectiveFrom', event.target.value)} /></label>
        <label className={LABEL}>{t('Duration')}<select aria-label={t('Duration')} className={INPUT} value={duration} onChange={event => setDuration(event.target.value)}><option value="review" disabled={!!plan?.effectiveTo}>{t('Ongoing with a review date')}</option><option value="end">{t('Ends on a date')}</option></select></label>
        <label className={LABEL}>{t(duration === 'end' ? 'Ends' : 'Review due')}<input type="date" className={INPUT} required min={form.effectiveFrom} max={duration === 'end' ? plan?.effectiveTo ?? undefined : undefined} value={duration === 'end' ? form.effectiveTo : form.reviewDate} onChange={event => set(duration === 'end' ? 'effectiveTo' : 'reviewDate', event.target.value)} /></label>
        {!configuredPolicy && <label className={LABEL}>{t('Benefit treatment')}<select aria-label={t('Benefit treatment')} className={INPUT} value={form.treatment} onChange={event => set('treatment', event.target.value)}>{Object.entries(TREATMENT_LABELS).map(([value, label]) => <option key={value} value={value}>{t(label)}</option>)}</select></label>}
        <label className={LABEL}>{t('Coverage tier')}<select aria-label={t('Coverage tier')} className={INPUT} value={form.coverageTier} onChange={event => set('coverageTier', event.target.value)}>{COVERAGE_TIERS.map(item => <option key={item} value={item}>{t(item)}</option>)}</select></label>
        <label className={LABEL}>{t('Entitlement tier')}<input className={INPUT} required maxLength={100} value={form.entitlementTier} onChange={event => set('entitlementTier', event.target.value)} /></label>
        <label className={LABEL}>{t('Individual benefit limit ({currency})', { currency: plan?.currency ?? '' })}<input className={INPUT} type="number" min="0.01" step="0.01" placeholder={t('No monetary cap')} value={form.maximumBenefitAmount} onChange={event => set('maximumBenefitAmount', event.target.value)} /></label>
        <label className={LABEL}>{t('Enrolled value ({currency})', { currency: plan?.currency ?? '' })}<input className={INPUT} type="number" min="0.01" step="0.01" max={form.maximumBenefitAmount || undefined} placeholder={t('Not specified')} value={form.requestedBenefitAmount} onChange={event => set('requestedBenefitAmount', event.target.value)} /></label>
        <label className={LABEL}>{t('Limit period')}<select aria-label={t('Limit period')} className={INPUT} value={form.limitPeriod} onChange={event => set('limitPeriod', event.target.value)}>{Object.entries(PERIOD_LABELS).map(([value, label]) => <option key={value} value={value}>{t(label)}</option>)}</select></label>
      </div>
      {plan && <BenefitPolicySummary policy={paymentPolicy} currency={plan.currency} />}
      {duration === 'review' && <p className="text-xs text-slate-500 dark:text-slate-400">{t('The review date prompts HR to review this benefit. It does not end coverage.')}</p>}
      {plan?.effectiveTo && <p className="text-xs text-slate-500 dark:text-slate-400">{t('This plan has a fixed end date. The additional benefit must end within that period.')}</p>}
      {checking ? <p className="text-xs text-slate-500">{t('Checking plan availability…')}</p> : checkError ? <FormError message={checkError} /> : check && <div className="rounded-lg bg-slate-50 p-3 text-xs dark:bg-white/5" data-testid="additional-benefit-eligibility">
        {blockedChecks.length > 0 ? blockedChecks.map(item => <p key={item.key} className="text-rose-700 dark:text-rose-300">{item.detail}</p>)
          : check.alreadyEnrolled && !existing ? <p className="text-amber-800 dark:text-amber-300">{t('This employee already has this benefit. Adjust the existing benefit instead.')}</p>
            : <p className="text-slate-600 dark:text-slate-300">{t(check.eligible ? 'The plan is available for this employee.' : 'This benefit is outside the employee’s grade or service eligibility and requires approval.')}</p>}
      </div>}
      <details className="rounded-xl border border-slate-200 p-3 dark:border-white/10"><summary className="cursor-pointer text-xs font-semibold text-slate-700 dark:text-slate-200">{t('Planned costs (optional)')}</summary><div className="mt-3 grid gap-3 sm:grid-cols-2">
        <label className={LABEL}>{t('Planned employer cost')}<input className={INPUT} type="number" min="0" step="0.01" value={form.plannedEmployerCost} onChange={event => set('plannedEmployerCost', event.target.value)} /></label>
        <label className={LABEL}>{t('Planned employee cost')}<input className={INPUT} type="number" min="0" step="0.01" value={form.plannedEmployeeCost} onChange={event => set('plannedEmployeeCost', event.target.value)} /></label>
        <label className={LABEL}>{t('Cost frequency')}<select aria-label={t('Cost frequency')} className={INPUT} value={form.costFrequency} onChange={event => set('costFrequency', event.target.value)}>{Object.entries(COST_FREQUENCY_LABELS).map(([value, label]) => <option key={value} value={value}>{t(label)}</option>)}</select></label>
      </div><p className="mt-2 text-xs text-slate-500 dark:text-slate-400">{t(configuredPolicy ? 'The plan’s payment policy determines payroll and claims. Planned costs do not change the payment amount.' : 'Approval assigns the benefit. Payments, contributions and payroll deductions must be recorded separately.')}</p></details>
      <label className={LABEL}>{t('Reason shown to employee')}<textarea className={INPUT} required rows={2} maxLength={1000} value={form.reason} onChange={event => set('reason', event.target.value)} /></label>
      <label className={LABEL}>{t('Internal justification')}<textarea className={INPUT} required rows={2} maxLength={2000} value={form.internalJustification} onChange={event => set('internalJustification', event.target.value)} /><span className="mt-1 block text-xs font-normal">{t('Visible to HR and approvers only.')}</span></label>
      {plan && <p className="text-xs text-slate-500 dark:text-slate-400">{t('All amounts are in {currency}.', { currency: plan.currency })}</p>}
      <div className="flex justify-end gap-2"><button type="button" className={SECONDARY} onClick={onClose}>{t('Cancel')}</button><button type="submit" className={PRIMARY} disabled={!employee || !planAllowed || checking}>{t('Review request')}</button></div>
    </form>}
  </Modal>;
}
