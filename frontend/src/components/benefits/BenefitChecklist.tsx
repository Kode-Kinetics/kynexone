'use client';

import { useEffect, useRef, useState } from 'react';
import { benefitsApi, benefitsErrorMessage, type AdditionalBenefitInput, type AdditionalBenefitRequest, type BenefitEligibilityCheck, type BenefitPlan, type EmployeeBenefitPackage } from '@/src/api/benefits';
import { employeesApi, type EmployeeListItem } from '@/src/api/employees';
import { useAuth } from '@/src/contexts/AuthContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import { AdditionalBenefitForm, type BenefitEmployee } from './AdditionalBenefitForm';
import { BenefitExceptionForm } from './BenefitExceptionForm';
import { AdditionalBenefitRequestDetail } from './AdditionalBenefitRequestDetail';
import { COST_FREQUENCY_LABELS, PERIOD_LABELS, TREATMENT_LABELS } from './AdditionalBenefitSummary';
import { BenefitPolicySummary, policyTreatment } from './BenefitPaymentPolicy';
import { COVERAGE_TIERS, FormError, INPUT, LABEL, Modal, PRIMARY, SECONDARY, today } from './benefitUi';

type Selection = {
  selected: boolean; effectiveFrom: string; effectiveTo: string; reviewDate: string; limit: string;
  coverageTier: string; entitlementTier: string; limitPeriod: AdditionalBenefitInput['limitPeriod'];
  treatment: AdditionalBenefitInput['treatment']; requestedBenefitAmount: string;
  plannedEmployerCost: string; plannedEmployeeCost: string; costFrequency: NonNullable<AdditionalBenefitInput['costFrequency']>;
  request?: AdditionalBenefitRequest; error?: string; defaultsApplied?: boolean;
};
const initialTerms = (plan: BenefitPlan): Selection => ({
  selected: false, effectiveFrom: plan.effectiveFrom > today() ? plan.effectiveFrom : today(),
  effectiveTo: plan.effectiveTo ?? '', reviewDate: '', limit: '', coverageTier: 'Employee', entitlementTier: plan.name,
  limitPeriod: 'Annual', treatment: plan.paymentPolicy && (plan.policyVersion ?? 0) > 0 ? policyTreatment(plan.paymentPolicy) : ['Medical', 'Dental', 'Life', 'Vision'].includes(plan.planType) ? 'Coverage'
    : ['Education', 'Reimbursement'].includes(plan.planType) ? 'Reimbursement' : 'OtherNonCash',
  requestedBenefitAmount: '', plannedEmployerCost: '', plannedEmployeeCost: '', costFrequency: 'Monthly',
});
const numberOrNull = (value: string) => value.trim() ? Number(value) : null;
const dayBefore = (date: string) => {
  const value = new Date(`${date}T00:00:00Z`);
  value.setUTCDate(value.getUTCDate() - 1);
  return value.toISOString().slice(0, 10);
};

/** One request per selected plan. Successful rows stay locked while failed rows can be corrected. */
export function BenefitChecklist({ plans, employee: fixedEmployee, initialPlanId, onClose, onChanged }: {
  plans: BenefitPlan[]; employee?: BenefitEmployee; initialPlanId?: string; onClose: () => void; onChanged: () => void;
}) {
  const { t } = useLocale();
  const plansRef = useRef(plans);
  plansRef.current = plans;
  const format = useFormat();
  const { hasPermission, hasRole } = useAuth();
  const canConfigure = hasPermission('approvals.manage') && (hasRole('Admin') || hasRole('HR Manager'));
  const [employee, setEmployee] = useState<BenefitEmployee | null>(fixedEmployee ?? null);
  const [search, setSearch] = useState('');
  const [results, setResults] = useState<EmployeeListItem[]>([]);
  const [searching, setSearching] = useState(false);
  const [employeePackage, setEmployeePackage] = useState<EmployeeBenefitPackage | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [endings, setEndings] = useState<Record<string, { endDate: string; request?: AdditionalBenefitRequest; error?: string }>>({});
  const [selections, setSelections] = useState<Record<string, Selection>>({});
  const [checks, setChecks] = useState<Record<string, BenefitEligibilityCheck>>({});
  const [checkErrors, setCheckErrors] = useState<Record<string, string>>({});
  const [checking, setChecking] = useState(false);
  const [reason, setReason] = useState(t('Individual benefit allocation.'));
  const [justification, setJustification] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [setupUrl, setSetupUrl] = useState<string | null>(null);
  const [requestId, setRequestId] = useState<string | null>(null);
  const [submitted, setSubmitted] = useState(false);
  const [adjustingId, setAdjustingId] = useState<string | null>(null);
  const [amendingId, setAmendingId] = useState<string | null>(null);

  useEffect(() => {
    if (employee || search.trim().length < 2) { setResults([]); return; }
    let live = true;
    const timer = setTimeout(() => {
      setSearching(true);
      employeesApi.list({ search: search.trim(), pageSize: 8 }).then(response => {
        if (live) setResults(response.items.filter(item => !['Offboarded', 'Terminated', 'Deleted', 'Exited'].includes(item.status)));
      }).catch(() => { if (live) setResults([]); }).finally(() => { if (live) setSearching(false); });
    }, 250);
    return () => { live = false; clearTimeout(timer); };
  }, [employee, search]);

  useEffect(() => {
    setEmployeePackage(null); setSelections({}); setEndings({}); setChecks({}); setCheckErrors({}); setLoadError(null); setSubmitted(false);
    if (!employee) return;
    let live = true;
    setLoading(true);
    benefitsApi.employeePackage(employee.id).then(data => {
      if (!live) return;
      setEmployeePackage(data);
      const plan = plansRef.current.find(item => item.id === initialPlanId);
      if (plan && !data.enrollments.some(item => item.benefitPlanId === plan.id && ['Current', 'Scheduled'].includes(item.effectiveStatus))
        && !data.additionalRequests.some(item => item.benefitPlanId === plan.id && item.status === 'Pending')) {
        setSelections({ [plan.id]: { ...initialTerms(plan), selected: true } });
      }
    }).catch(err => { if (live) setLoadError(benefitsErrorMessage(err, t('Could not load employee benefits.'))); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [employee, initialPlanId, t]);

  const active = employeePackage?.enrollments.filter(item => ['Current', 'Scheduled'].includes(item.effectiveStatus)) ?? [];
  const pending = employeePackage?.additionalRequests.filter(item => item.status === 'Pending') ?? [];
  const assignedIds = new Set(active.map(item => item.benefitPlanId));
  const pendingIds = new Set(pending.map(item => item.benefitPlanId));
  const available = plans.filter(plan => plan.isActive && (!plan.effectiveTo || plan.effectiveTo >= today())
    && (!plan.companyId || plan.companyId === employeePackage?.companyId) && !assignedIds.has(plan.id) && !pendingIds.has(plan.id));
  const chosen = available.filter(plan => selections[plan.id]?.selected && !selections[plan.id]?.request);
  const completed = [...Object.values(selections), ...Object.values(endings)].filter(row => row.request);
  const chosenEnds = active.filter(item => endings[item.id] && !endings[item.id].request);
  const checkKey = chosen.map(plan => `${plan.id}:${selections[plan.id].effectiveFrom}`).join('|');
  useEffect(() => {
    if (!employee || !checkKey) { setChecking(false); return; }
    let live = true;
    setChecking(true); setChecks({}); setCheckErrors({});
    void Promise.all(checkKey.split('|').map(async value => {
      const [planId, date] = value.split(':');
      if (!date) return;
      try {
        const check = await benefitsApi.checkEligibility(planId, employee.id, date);
        if (live) {
          setChecks(current => ({ ...current, [planId]: check }));
          setSelections(current => {
            const row = current[planId];
            if (!row || row.defaultsApplied) return current;
            return { ...current, [planId]: { ...row, defaultsApplied: true, entitlementTier: check.tierName || row.entitlementTier, limit: row.limit || check.maximumBenefitAmount?.toString() || '', limitPeriod: check.limitPeriod && check.limitPeriod in PERIOD_LABELS ? check.limitPeriod as Selection['limitPeriod'] : row.limitPeriod } };
          });
        }
      } catch (err) { if (live) setCheckErrors(current => ({ ...current, [planId]: benefitsErrorMessage(err, t('Could not check eligibility.')) })); }
    })).finally(() => { if (live) setChecking(false); });
    return () => { live = false; };
  }, [employee, checkKey, t]);

  const update = (plan: BenefitPlan, change: Partial<Selection>) => setSelections(current => ({ ...current, [plan.id]: { ...(current[plan.id] ?? initialTerms(plan)), ...change, error: undefined } }));
  const blocked = (planId: string) => checks[planId]?.checks.filter(check => ['plan_active', 'company_scope', 'plan_window'].includes(check.key) && !check.passed) ?? [];
  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!employee || saving || (!chosen.length && !chosenEnds.length)) return;
    setError(null); setSetupUrl(null);
    if (!reason.trim() || !justification.trim()) { setError(t('Enter a reason for these changes.')); return; }
    if (chosen.some(plan => !checks[plan.id] || checkErrors[plan.id] || blocked(plan.id).length || checks[plan.id].alreadyEnrolled)) return;
    setSaving(true); setSubmitted(true);
    for (const plan of chosen) {
      const row = selections[plan.id];
      const input: AdditionalBenefitInput = {
        employeeId: employee.id, benefitPlanId: plan.id, coverageTier: row.coverageTier, entitlementTier: row.entitlementTier.trim(),
        effectiveFrom: row.effectiveFrom, effectiveTo: row.effectiveTo || null, reviewDate: row.effectiveTo ? null : row.reviewDate || null,
        maximumBenefitAmount: numberOrNull(row.limit), requestedBenefitAmount: numberOrNull(row.requestedBenefitAmount), limitPeriod: row.limitPeriod,
        reason: reason.trim(), internalJustification: justification.trim(), treatment: row.treatment,
        plannedEmployerCost: numberOrNull(row.plannedEmployerCost), plannedEmployeeCost: numberOrNull(row.plannedEmployeeCost),
        costFrequency: row.plannedEmployerCost || row.plannedEmployeeCost ? row.costFrequency : null,
      };
      try {
        const request = await benefitsApi.requestAdditional(input);
        setSelections(current => ({ ...current, [plan.id]: { ...current[plan.id], request, error: undefined } }));
        onChanged();
      } catch (err) {
        const data = (err as { response?: { data?: { code?: string; setupUrl?: string } } }).response?.data;
        if (data?.code === 'approval_route_not_configured' && data.setupUrl?.startsWith('/') && !data.setupUrl.startsWith('//')) setSetupUrl(data.setupUrl);
        setSelections(current => ({ ...current, [plan.id]: { ...current[plan.id], error: benefitsErrorMessage(err, t('Could not submit the additional benefit.')) } }));
      }
    }
    for (const enrollment of chosenEnds) {
      try {
        const request = await benefitsApi.requestBenefitEnd(enrollment.id, { endDate: endings[enrollment.id].endDate, reason: reason.trim(), internalJustification: justification.trim(), expectedUpdatedAtUtc: enrollment.updatedAtUtc });
        setEndings(current => ({ ...current, [enrollment.id]: { ...current[enrollment.id], request, error: undefined } }));
        onChanged();
      } catch (err) {
        const data = (err as { response?: { data?: { code?: string; setupUrl?: string } } }).response?.data;
        if (data?.code === 'approval_route_not_configured' && data.setupUrl?.startsWith('/') && !data.setupUrl.startsWith('//')) setSetupUrl(data.setupUrl);
        setEndings(current => ({ ...current, [enrollment.id]: { ...current[enrollment.id], error: benefitsErrorMessage(err, t('Could not submit the benefit removal.')) } }));
      }
    }
    setSaving(false);
  };

  return <Modal title={t('Manage benefits')} onClose={saving ? () => {} : onClose} wide>
    <form onSubmit={submit} className="space-y-4" aria-label={t('Benefit checklist')}>
      <p className="text-sm text-slate-600 dark:text-slate-300">{t('Check benefits to add or uncheck an additional benefit to remove it. Changes take effect after approval.')}</p>
      {employee ? <div className="flex flex-wrap items-center justify-between gap-2 rounded-lg bg-slate-50 p-3 text-sm dark:bg-white/5"><span className="font-semibold text-slate-800 dark:text-slate-100">{employee.fullName}</span>{!fixedEmployee && !completed.length && !saving && <button type="button" className={SECONDARY} onClick={() => { setEmployee(null); setSearch(''); }}>{t('Change employee')}</button>}</div>
        : <div><label className={LABEL}>{t('Search employee')}<input className={INPUT} aria-label={t('Search employee')} value={search} onChange={event => setSearch(event.target.value)} /></label>{searching && <p className="mt-1 text-xs text-slate-500">{t('Searching…')}</p>}<ul role="listbox" aria-label={t('Employees')} className="mt-1 space-y-1">{results.map(item => <li key={item.id}><button type="button" role="option" aria-selected={false} className="w-full rounded-lg border border-slate-200 p-2 text-start text-xs dark:border-white/10 dark:text-slate-200" onClick={() => setEmployee({ id: item.id, fullName: item.fullName })}>{item.fullName} · {item.employeeCode} · {t(item.status)}</button></li>)}</ul></div>}
      {loading && <p role="status" className="text-sm text-slate-500">{t('Loading benefits')}</p>}
      <FormError message={loadError || error} />
      {employeePackage && <>
        {active.length > 0 && <section className="space-y-2"><h3 className="text-xs font-semibold text-slate-500">{t('Assigned benefits')}</h3>{active.map(item => {
          const ending = endings[item.id];
          const pendingEnd = pending.find(request => request.terms.enrollmentId === item.id && ['End', 'Cancel'].includes(request.operation ?? ''));
          const canEdit = item.assignmentSource === 'IndividualAdditional' && !pendingIds.has(item.benefitPlanId);
          const canRemove = canEdit && item.classification !== 'Mandatory' && (!item.effectiveTo || item.effectiveTo > today());
          return <section key={item.id} aria-label={item.planName} className="rounded-xl border border-slate-200 p-3 dark:border-white/10">
            <label className="flex items-center gap-3 text-sm font-semibold text-slate-800 dark:text-slate-100"><input type="checkbox" aria-label={t('Keep {benefit}', { benefit: item.planName })} checked={!ending || !!ending.request} disabled={!canRemove || saving || !!ending?.request} className="h-4 w-4 accent-sapphire" onChange={event => setEndings(current => { const next = { ...current }; if (event.target.checked) delete next[item.id]; else next[item.id] = { endDate: today() }; return next; })} /><span>{item.planName}</span></label>
            <p className="mt-1 ps-7 text-xs text-slate-500">{t(item.assignmentSource === 'GradeDefault' ? 'Grade default' : 'Additional benefit')} · {t(item.effectiveStatus === 'Scheduled' ? 'Scheduled' : 'Active')} · {item.maximumBenefitAmount === null ? t('No monetary cap') : format.money(item.maximumBenefitAmount, item.currency)} · {t((PERIOD_LABELS as Record<string, string>)[item.limitPeriod] ?? item.limitPeriod)}</p>
            <p className="mt-1 ps-7 text-xs text-slate-500">{t('Starts {date}', { date: format.date(item.effectiveFrom) })}{item.effectiveTo ? ` · ${t('Ends {date}', { date: format.date(item.effectiveTo) })}` : ''}</p>
            {item.assignmentSource === 'GradeDefault' && <p className="mt-1 ps-7 text-xs text-slate-500">{t('Grade defaults remain assigned. Use Adjust existing benefit for an exception.')}</p>}
            {item.assignmentSource !== 'IndividualAdditional' && hasPermission('employees.approve') && <button type="button" className={`${SECONDARY} mt-2`} disabled={saving} onClick={() => setAdjustingId(item.id)}>{t('Adjust existing benefit')}</button>}
            {canEdit && !ending && <button type="button" className={`${SECONDARY} mt-2`} disabled={saving} onClick={() => setAmendingId(item.id)}>{t('Edit terms')}</button>}
            {(ending?.request || pendingEnd) && <div className="mt-2 space-y-2"><p className="text-xs font-semibold text-amber-700">{t('Removal pending approval. Coverage remains unchanged until approved.')}</p><button type="button" className={SECONDARY} onClick={() => setRequestId((ending?.request || pendingEnd)!.id)}>{t('View request')}</button></div>}
            {ending && !ending.request && <div className="mt-3 space-y-2"><label className={LABEL}>{t('Last covered day')}<input type="date" required min={today()} max={item.effectiveTo ? dayBefore(item.effectiveTo) : undefined} className={INPUT} disabled={saving} value={ending.endDate} onChange={event => setEndings(current => ({ ...current, [item.id]: { endDate: event.target.value } }))} /></label><p className="text-xs text-slate-500">{t(item.effectiveFrom > ending.endDate ? 'This cancels the scheduled benefit before coverage starts.' : 'Coverage ends after this date once approved.')}</p><FormError message={ending.error ?? null} /></div>}
          </section>;
        })}</section>}
        {pending.map(item => <div key={item.id} className="flex items-center justify-between gap-2 rounded-xl border border-amber-200 bg-amber-50 p-3 text-xs dark:border-amber-500/20 dark:bg-amber-500/10"><span>{item.planName} · {t(['End', 'Cancel'].includes(item.operation ?? '') ? 'Removal pending approval' : 'Addition pending approval')}</span><button type="button" className={SECONDARY} onClick={() => setRequestId(item.id)}>{t('View request')}</button></div>)}
        <h3 className="text-xs font-semibold text-slate-500">{t('Available benefits')}</h3>
        <div className="space-y-3" data-testid="benefit-checklist-plans">{available.map(plan => {
          const row = selections[plan.id] ?? initialTerms(plan);
          return <section key={plan.id} aria-label={plan.name} className={`rounded-xl border p-3 ${row.selected ? 'border-sapphire/40 bg-sapphire/[0.03]' : 'border-slate-200 dark:border-white/10'}`}>
            <label className="flex cursor-pointer items-center gap-3 text-sm font-semibold text-slate-800 dark:text-slate-100"><input type="checkbox" aria-label={plan.name} checked={row.selected} disabled={saving || !!row.request} className="h-4 w-4 shrink-0 accent-sapphire" onChange={event => update(plan, { selected: event.target.checked })} /><span className="min-w-0 flex-1">{plan.name}</span><span className="text-xs font-normal text-slate-500">{plan.currency}</span></label>
            {row.request ? <div className="mt-3 flex flex-wrap items-center justify-between gap-2 text-xs text-emerald-700 dark:text-emerald-300"><span>{t('Pending approval')}</span><button type="button" className={SECONDARY} onClick={() => setRequestId(row.request!.id)}>{t('View request')}</button></div> : row.selected && <div className="mt-3 space-y-3">
              <BenefitPolicySummary policy={plan.paymentPolicy} currency={plan.currency} />
              <fieldset disabled={saving} className="grid gap-3 sm:grid-cols-3">
                <label className={LABEL}>{t('Starts')}<input type="date" className={INPUT} required min={plan.effectiveFrom > today() ? plan.effectiveFrom : today()} max={plan.effectiveTo ?? undefined} value={row.effectiveFrom} onChange={event => update(plan, { effectiveFrom: event.target.value })} /></label>
                <label className={LABEL}>{t('Ends')}<input type="date" className={INPUT} required={!!plan.effectiveTo} min={row.effectiveFrom} max={plan.effectiveTo ?? undefined} value={row.effectiveTo} onChange={event => update(plan, { effectiveTo: event.target.value })} /></label>
                <label className={LABEL}>{t('Limit ({currency})', { currency: plan.currency })}<input type="number" className={INPUT} min="0.01" step="0.01" placeholder={t('No monetary cap')} value={row.limit} onChange={event => update(plan, { limit: event.target.value })} /><span className="mt-1 block text-xs font-normal">{t(PERIOD_LABELS[row.limitPeriod])}</span></label>
                {!row.effectiveTo && <label className={LABEL}>{t('Review due')}<input type="date" className={INPUT} required min={row.effectiveFrom} value={row.reviewDate} onChange={event => update(plan, { reviewDate: event.target.value })} /><span className="mt-1 block text-xs font-normal">{t('Required when there is no end date.')}</span></label>}
              </fieldset>
              <details className="text-xs"><summary className="cursor-pointer font-semibold text-slate-500 dark:text-slate-400">{t('Advanced terms (optional)')}</summary><fieldset disabled={saving} className="mt-3 grid gap-3 sm:grid-cols-2">
                <label className={LABEL}>{t('Coverage tier')}<select aria-label={t('Coverage tier')} className={INPUT} value={row.coverageTier} onChange={event => update(plan, { coverageTier: event.target.value })}>{COVERAGE_TIERS.map(value => <option key={value} value={value}>{t(value)}</option>)}</select></label>
                <label className={LABEL}>{t('Entitlement tier')}<input className={INPUT} required maxLength={100} value={row.entitlementTier} onChange={event => update(plan, { entitlementTier: event.target.value })} /></label>
                <label className={LABEL}>{t('Limit period')}<select aria-label={t('Limit period')} className={INPUT} value={row.limitPeriod} onChange={event => update(plan, { limitPeriod: event.target.value as Selection['limitPeriod'] })}>{Object.entries(PERIOD_LABELS).map(([value, label]) => <option key={value} value={value}>{t(label)}</option>)}</select></label>
                {!(plan.policyVersion ?? 0) && <label className={LABEL}>{t('Benefit treatment')}<select aria-label={t('Benefit treatment')} className={INPUT} value={row.treatment} onChange={event => update(plan, { treatment: event.target.value as Selection['treatment'] })}>{Object.entries(TREATMENT_LABELS).map(([value, label]) => <option key={value} value={value}>{t(label)}</option>)}</select></label>}
                <label className={LABEL}>{t('Enrolled value')}<input type="number" className={INPUT} min="0.01" max={row.limit || undefined} step="0.01" value={row.requestedBenefitAmount} onChange={event => update(plan, { requestedBenefitAmount: event.target.value })} /></label>
                <label className={LABEL}>{t('Planned employer cost')}<input type="number" className={INPUT} min="0" step="0.01" value={row.plannedEmployerCost} onChange={event => update(plan, { plannedEmployerCost: event.target.value })} /></label>
                <label className={LABEL}>{t('Planned employee cost')}<input type="number" className={INPUT} min="0" max={plan.classification === 'Mandatory' ? 0 : undefined} step="0.01" value={row.plannedEmployeeCost} onChange={event => update(plan, { plannedEmployeeCost: event.target.value })} /></label>
                <label className={LABEL}>{t('Cost frequency')}<select aria-label={t('Cost frequency')} className={INPUT} value={row.costFrequency} onChange={event => update(plan, { costFrequency: event.target.value as Selection['costFrequency'] })}>{Object.entries(COST_FREQUENCY_LABELS).map(([value, label]) => <option key={value} value={value}>{t(label)}</option>)}</select></label>
              </fieldset><p className="mt-2 text-slate-500">{t((plan.policyVersion ?? 0) > 0 ? 'The plan’s payment policy determines payroll and claims. Planned costs do not change the payment amount.' : 'Approval assigns the benefit. Payments, contributions and payroll deductions must be recorded separately.')}</p></details>
              <FormError message={row.error || checkErrors[plan.id] || blocked(plan.id).map(item => item.detail).join(' ') || (checks[plan.id]?.alreadyEnrolled ? t('This employee already has this benefit. Adjust the existing benefit instead.') : null)} />
            </div>}
          </section>;
        })}</div>
        {!available.length && !loading && <p className="text-sm text-slate-500">{t('No other benefit plans are available for this employee.')}</p>}
        {(chosen.length > 0 || chosenEnds.length > 0) && <fieldset disabled={saving} className="space-y-3"><label className={LABEL}>{t('Reason for changes')}<textarea className={INPUT} required rows={2} maxLength={2000} value={justification} onChange={event => setJustification(event.target.value)} /><span className="mt-1 block text-xs font-normal">{t('Visible to HR and approvers only.')}</span></label><details><summary className="cursor-pointer text-xs font-semibold text-slate-500">{t('Employee message (optional)')}</summary><label className={`${LABEL} mt-2`}>{t('Reason shown to employee')}<textarea className={INPUT} required rows={2} maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></label></details></fieldset>}
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('Changes are sent for approval separately. Current coverage stays unchanged until approval.')}</p>
      </>}
      {submitted && !saving && <p role="status" className="rounded-lg bg-slate-50 p-3 text-sm text-slate-700 dark:bg-white/5 dark:text-slate-200">{t('{count} benefit requests sent for approval.', { count: completed.length })}{(chosen.some(plan => selections[plan.id].error) || chosenEnds.some(item => endings[item.id].error)) ? ` ${t('Correct the failed rows and submit them again. Successful requests will not be sent again.')}` : ''}</p>}
      {setupUrl && <p className="text-xs text-slate-600 dark:text-slate-300">{canConfigure ? <a href={setupUrl} className="font-semibold text-sapphire underline">{t('Configure approval workflow')}</a> : t('Contact an administrator to configure additional benefit approvals.')}</p>}
      <div className="flex flex-wrap justify-end gap-2"><button type="button" className={SECONDARY} disabled={saving} onClick={onClose}>{t(completed.length ? 'Done' : 'Cancel')}</button><button type="submit" className={PRIMARY} disabled={saving || loading || checking || (!chosen.length && !chosenEnds.length) || chosen.some(plan => !checks[plan.id] || checkErrors[plan.id] || blocked(plan.id).length || checks[plan.id]?.alreadyEnrolled)}>{t(saving ? 'Saving…' : 'Save changes')}</button></div>
    </form>
    {adjustingId && active.find(item => item.id === adjustingId) && <Modal title={t('Adjust existing benefit')} onClose={() => setAdjustingId(null)} wide><BenefitExceptionForm enrollment={active.find(item => item.id === adjustingId)!} mandatory={active.find(item => item.id === adjustingId)!.classification === 'Mandatory'} currency={active.find(item => item.id === adjustingId)!.currency} onCancel={() => setAdjustingId(null)} onSaved={() => { setAdjustingId(null); if (employee) void benefitsApi.employeePackage(employee.id).then(setEmployeePackage); onChanged(); }} /></Modal>}
    {requestId && <AdditionalBenefitRequestDetail requestId={requestId} onClose={() => setRequestId(null)} />}
    {amendingId && employee && <AdditionalBenefitForm plans={plans} employee={employee} existing={active.find(item => item.id === amendingId)} onClose={() => setAmendingId(null)} onRequested={request => { setAmendingId(null); setEmployeePackage(current => current ? { ...current, additionalRequests: [request, ...current.additionalRequests] } : current); onChanged(); }} />}
  </Modal>;
}
