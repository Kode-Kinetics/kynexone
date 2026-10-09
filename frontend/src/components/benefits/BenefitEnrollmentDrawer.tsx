'use client';

import { useCallback, useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { Link2, Pencil, Plus, RefreshCw, X } from 'lucide-react';
import { benefitsApi, benefitsErrorMessage, type BenefitDeductionCandidate, type BenefitEnrollment, type BenefitEnrollmentDetail, type BenefitPlan, type EmployeeBenefitPackageItem } from '@/src/api/benefits';
import { useAppToast } from '@/src/components/ui/AppToast';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import { useAuth } from '@/src/contexts/AuthContext';
import { useReleaseA } from '@/src/lib/releaseA';
import { BenefitExceptionForm } from './BenefitExceptionForm';
import { BenefitPolicySummary } from './BenefitPaymentPolicy';
import { BenefitChecklist } from './BenefitChecklist';
import { AdditionalBenefitForm } from './AdditionalBenefitForm';
import { AdditionalBenefitRequestDetail } from './AdditionalBenefitRequestDetail';
import { INPUT, LABEL, PRIMARY, SECONDARY, FormError, StatusPill, today, enrollmentStatus, COVERAGE_TIERS } from './benefitUi';

export function AssignmentLabel({ enrollment }: { enrollment: Pick<BenefitEnrollment, 'assignmentSource' | 'hasException'> }) {
  const { t } = useLocale();
  return <span className="flex flex-wrap items-center gap-1.5">
    <span>{t(enrollment.assignmentSource === 'GradeDefault' ? 'Grade default' : ['IndividualException', 'IndividualAdditional'].includes(enrollment.assignmentSource) ? 'Additional benefit' : 'Manual enrolment')}</span>
    {enrollment.hasException && enrollment.assignmentSource !== 'IndividualAdditional' && <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-semibold text-amber-800 dark:bg-amber-500/10 dark:text-amber-300">{t("Individual exception")}</span>}
  </span>;
}

export function EnrollmentDrawer({ enrollmentId, plans, canRecord, canApplyException, startEditing = false, onChanged, onClose }: {
  enrollmentId: string; plans: BenefitPlan[]; canRecord: boolean; canApplyException: boolean; startEditing?: boolean; onChanged: (enrollmentId: string) => void; onClose: () => void;
}) {
  const { t } = useLocale();
  const format = useFormat();
  const money = (amount: number) => format.number(amount, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const range = (from: string, to: string | null) => `${format.date(from)} → ${to ? format.date(to) : t('Ongoing')}`;
  const toast = useAppToast();
  const { hasPermission } = useAuth();
  const releaseA = useReleaseA();
  const canPropose = hasPermission('employees.write') && !releaseA;
  const [amending, setAmending] = useState<EmployeeBenefitPackageItem | null>(null);
  const [adding, setAdding] = useState(false);
  const [additionalRequestId, setAdditionalRequestId] = useState<string | null>(null);
  const [loadingAmendment, setLoadingAmendment] = useState(false);
  const [detail, setDetail] = useState<BenefitEnrollmentDetail | null>(null);
  const [editingException, setEditingException] = useState(startEditing);
  const [error, setError] = useState<string | null>(null);
  const [candidates, setCandidates] = useState<BenefitDeductionCandidate[] | null>(null);
  const [contrib, setContrib] = useState({ employeeAmount: '', employerAmount: '', frequency: 'Monthly', payrollComponentCode: '', effectiveFrom: today() });
  const [link, setLink] = useState({ contributionId: '', deductionId: '', amount: '' });
  const [busy, setBusy] = useState<'contrib' | 'link' | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [d, c] = await Promise.all([benefitsApi.getEnrollment(enrollmentId), benefitsApi.deductionCandidates(enrollmentId)]);
      setDetail(d); setCandidates(c);
      setContrib(current => ({ ...current, effectiveFrom: d.enrollment.effectiveFrom > today() ? d.enrollment.effectiveFrom : today() }));
      setLink((l) => ({ ...l, contributionId: l.contributionId || d.contributions[0]?.id || '' }));
    } catch (e) { setError(benefitsErrorMessage(e, 'Could not load the enrolment.')); }
  }, [enrollmentId]);
  useEffect(() => { void load(); }, [load]);

  const plan = detail ? plans.find((p) => p.id === detail.enrollment.benefitPlanId) : null;
  const currency = plan?.currency ?? '';
  const superseded = detail?.enrollment.status === 'Superseded';
  const canRecordContribution = canRecord && detail?.enrollment.status === 'Active'
    && (!detail.enrollment.effectiveTo || detail.enrollment.effectiveTo >= today());

  const amendAdditional = async () => {
    if (!detail) return;
    setLoadingAmendment(true); setFormError(null);
    try {
      const employeePackage = await benefitsApi.employeePackage(detail.enrollment.employeeId);
      const enrollment = employeePackage.enrollments.find(item => item.id === enrollmentId);
      if (enrollment) setAmending(enrollment);
      else setFormError(t('This benefit has changed. Refresh the employee’s benefits before continuing.'));
    } catch (err) { setFormError(benefitsErrorMessage(err, t('Could not load employee benefits.'))); }
    finally { setLoadingAmendment(false); }
  };

  const addContribution = async (e: React.FormEvent) => {
    e.preventDefault();
    const ee = Number(contrib.employeeAmount || 0), er = Number(contrib.employerAmount || 0);
    if (ee < 0 || er < 0) { setFormError('Contribution amounts cannot be negative.'); return; }
    setBusy('contrib'); setFormError(null);
    try {
      await benefitsApi.addContribution(enrollmentId, {
        employeeAmount: ee, employerAmount: er, frequency: contrib.frequency, payrollComponentCode: contrib.payrollComponentCode || null,
        effectiveFrom: contrib.effectiveFrom, effectiveTo: null, isActive: true,
      });
      toast.success('Contribution recorded.');
      setContrib((c) => ({ ...c, employeeAmount: '', employerAmount: '' }));
      void load();
    } catch (err) { setFormError(benefitsErrorMessage(err, 'Could not record the contribution.')); }
    finally { setBusy(null); }
  };

  const linkDeduction = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!link.contributionId || !link.deductionId) return;
    setBusy('link'); setFormError(null);
    try {
      await benefitsApi.linkDeduction(enrollmentId, {
        benefitContributionId: link.contributionId, payrollDeductionId: link.deductionId,
        linkedAmount: link.amount === '' ? null : Number(link.amount),
      });
      toast.success('Payroll deduction linked.');
      setLink((l) => ({ ...l, deductionId: '', amount: '' }));
      void load();
    } catch (err) { setFormError(benefitsErrorMessage(err, 'Could not link the deduction.')); }
    finally { setBusy(null); }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex justify-end bg-black/40" role="dialog" aria-modal="true" aria-label={t("Enrolment detail")}>
      <div className="h-full w-full max-w-xl overflow-y-auto border-s border-slate-200 bg-white p-5 shadow-2xl dark:border-white/[0.08] dark:bg-[#0c1120]">
        <div className="mb-4 flex items-center justify-between">
          <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">{t("Enrolment")}</h2>
          <button type="button" onClick={onClose} aria-label={t("Close")} className="rounded-lg p-1 text-slate-400 hover:bg-slate-100 dark:hover:bg-white/[0.06]"><X className="h-4 w-4" /></button>
        </div>
        {error ? (
          <div className="space-y-2"><FormError message={error} /><button type="button" className={SECONDARY} onClick={() => void load()}><RefreshCw className="h-3.5 w-3.5" />{t("Retry")}</button></div>
        ) : !detail ? (
          <div className="space-y-3" aria-busy="true"><div className="h-16 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" /><div className="h-40 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" /></div>
        ) : (
          <div className="space-y-5">
            <div>
              <p className="text-base font-bold text-slate-800 dark:text-slate-100">{detail.enrollment.employeeName}</p>
              <p className="text-xs text-slate-500 dark:text-slate-400">{plan?.name ?? 'Plan'} · {detail.enrollment.coverageTier} · {range(detail.enrollment.effectiveFrom, detail.enrollment.effectiveTo)}</p>
              {canPropose && <div className="mt-3 space-y-2">
                <button type="button" className={PRIMARY} onClick={() => setAdding(true)}><Plus className="h-3.5 w-3.5" />{t('Manage benefits')}</button>
                <p className="text-xs text-slate-500 dark:text-slate-400">{t('Choose another benefit plan for this employee. Their current benefits stay unchanged.')}</p>
              </div>}
            </div>
            <section className="rounded-xl border border-slate-200 bg-slate-50 p-3 text-xs dark:border-white/[0.06] dark:bg-white/[0.03]" data-testid="enrollment-entitlement">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <AssignmentLabel enrollment={detail.enrollment} />
                {!superseded && (detail.enrollment.assignmentSource === 'IndividualAdditional'
                  ? canPropose && <button type="button" disabled={loadingAmendment} className={SECONDARY} onClick={() => void amendAdditional()}><Pencil className="h-3.5 w-3.5" />{t('Amend additional benefit')}</button>
                  : canApplyException && !editingException && <button type="button" className={SECONDARY} onClick={() => setEditingException(true)}><Pencil className="h-3.5 w-3.5" />{t('Adjust existing benefit')}</button>)}
              </div>
              <dl className="mt-3 grid grid-cols-2 gap-2">
                <div><dt className="text-slate-500 dark:text-slate-400">{t("Entitlement tier")}</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{detail.enrollment.entitlementTier || 'Standard tier'}</dd></div>
                <div><dt className="text-slate-500 dark:text-slate-400">{t("Status")}</dt><dd><StatusPill active={enrollmentStatus(detail.enrollment) === 'Active'} label={enrollmentStatus(detail.enrollment)} /></dd></div>
                <div><dt className="text-slate-500 dark:text-slate-400">{t("Benefit limit")}</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{detail.enrollment.maximumBenefitAmount === null ? 'No monetary cap' : `${money(detail.enrollment.maximumBenefitAmount)} ${currency}`} · {detail.enrollment.limitPeriod.replace(/([A-Z])/g, ' $1').trim()}</dd></div>
                <div><dt className="text-slate-500 dark:text-slate-400">{t("Enrolled value")}</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{detail.enrollment.requestedBenefitAmount === null ? 'Not specified' : `${money(detail.enrollment.requestedBenefitAmount)} ${currency}`}</dd></div>
              </dl>
              {detail.enrollment.exceptionReason && <p className="mt-3 border-t border-slate-200 pt-2 text-slate-600 dark:border-white/10 dark:text-slate-300"><span className="font-semibold">{t('Exception reason')}: </span>{detail.enrollment.exceptionReason}</p>}
              {superseded && <p className="mt-3 text-slate-500 dark:text-slate-400">{t('Replaced before activation after the employee’s draft details changed.')}</p>}
              {!superseded && editingException && canApplyException && detail.enrollment.assignmentSource !== 'IndividualAdditional' && <BenefitExceptionForm key={detail.enrollment.id} enrollment={detail.enrollment} mandatory={plan?.classification === 'Mandatory'} currency={currency}
                onCancel={() => setEditingException(false)} onSaved={(id) => { setEditingException(false); if (id === enrollmentId) void load(); onChanged(id); }} />}
            </section>
            <BenefitPolicySummary policy={detail.enrollment.paymentPolicy} currency={currency} />
            {(detail.exceptions ?? []).length > 0 && <section data-testid="benefit-exception-history">
              <h3 className="mb-2 text-sm font-semibold text-slate-800 dark:text-slate-100">{t("Exception history")}</h3>
              <ul className="space-y-2">{detail.exceptions.map(item => <li key={item.id} className="rounded-lg border border-slate-200 p-3 text-xs dark:border-white/[0.06]">
                <p className="font-medium text-slate-800 dark:text-slate-100">{item.reason}</p>
                <p className="mt-1 text-slate-500 dark:text-slate-400">{format.dateTime(item.createdAtUtc)} · {item.createdByName || t('Authorized HR user')}</p>
                <ExceptionChanges before={item.previousValuesJson} after={item.newValuesJson} currency={currency} />
              </li>)}</ul>
            </section>}
            <FormError message={formError} />

            <section>
              <h3 className="mb-2 text-sm font-semibold text-slate-800 dark:text-slate-100">{t("Contributions")}</h3>
              {detail.contributions.length === 0 ? (
                <p className="rounded-xl border border-dashed border-slate-200 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.08] dark:text-slate-400">{t("No contributions recorded.")}</p>
              ) : (
                <table className="w-full text-start text-xs" data-testid="contributions-table">
                  <thead><tr className="text-[10px] uppercase tracking-wide text-slate-500 dark:text-slate-400">
                    <th className="py-1 font-semibold">{t("Effective from")}</th><th className="py-1 text-end font-semibold">{t("Employee")}</th><th className="py-1 text-end font-semibold">{t("Employer")}</th><th className="py-1 font-semibold">{t("Frequency")}</th><th className="py-1 font-semibold">{t("Pay code")}</th>
                  </tr></thead>
                  <tbody>{detail.contributions.map((c) => (
                    <tr key={c.id} className="border-t border-slate-100 dark:border-white/[0.04]">
                      <td className="py-1.5 text-slate-600 dark:text-slate-300">{c.effectiveFrom}</td>
                      <td className="py-1.5 text-end font-mono text-slate-800 dark:text-slate-100">{money(c.employeeAmount)} {currency}</td>
                      <td className="py-1.5 text-end font-mono text-slate-800 dark:text-slate-100">{money(c.employerAmount)} {currency}</td>
                      <td className="py-1.5 text-slate-600 dark:text-slate-300">{c.frequency}</td>
                      <td className="py-1.5 font-mono text-slate-500 dark:text-slate-400">{c.payrollComponentCode || '—'}</td>
                    </tr>))}
                  </tbody>
                </table>
              )}
              {canRecordContribution && (
                <form onSubmit={addContribution} aria-label={t("Record contribution")} className="mt-3 grid grid-cols-2 gap-2 rounded-xl border border-slate-200 p-3 dark:border-white/[0.06]">
                  <label className={LABEL}>{t("Employee share")}<input type="number" min={0} step="0.01" required disabled={plan?.classification === 'Mandatory'} className={`${INPUT} disabled:opacity-60`} value={plan?.classification === 'Mandatory' ? '0' : contrib.employeeAmount} onChange={(e) => setContrib((c) => ({ ...c, employeeAmount: e.target.value }))} aria-label={t("Employee share")} />
                  </label>
                  <label className={LABEL}>{t("Employer share")}<input type="number" min={0} step="0.01" required className={INPUT} value={contrib.employerAmount} onChange={(e) => setContrib((c) => ({ ...c, employerAmount: e.target.value }))} aria-label={t("Employer share")} />
                  </label>
                  <label className={LABEL}>{t("Frequency")}<select className={INPUT} value={contrib.frequency} onChange={(e) => setContrib((c) => ({ ...c, frequency: e.target.value }))}>
                      {['Monthly', 'Quarterly', 'Annual', 'One-off'].map((f) => <option key={f} value={f}>{f}</option>)}
                    </select>
                  </label>
                  <label className={LABEL}>{t("Payroll component code")}<input className={INPUT} value={contrib.payrollComponentCode} placeholder={t('For example, MED-EE')} onChange={(e) => setContrib((c) => ({ ...c, payrollComponentCode: e.target.value }))} />
                  </label>
                  <label className={LABEL}>{t("Effective from")}<input type="date" required min={detail.enrollment.effectiveFrom > today() ? detail.enrollment.effectiveFrom : today()} max={detail.enrollment.effectiveTo ?? undefined} className={INPUT} value={contrib.effectiveFrom} onChange={(e) => setContrib((c) => ({ ...c, effectiveFrom: e.target.value }))} />
                  </label>
                  <div className="flex items-end justify-end"><button type="submit" disabled={busy !== null} className={PRIMARY}>{busy === 'contrib' ? 'Saving…' : 'Record contribution'}</button></div>
                </form>
              )}
            </section>

            <section>
              <h3 className="mb-2 text-sm font-semibold text-slate-800 dark:text-slate-100">{t("Payroll deductions")}</h3>
              {detail.deductions.length === 0 ? (
                <p className="rounded-xl border border-dashed border-slate-200 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.08] dark:text-slate-400">{t("No benefit payroll deductions are linked yet.")}</p>
              ) : (
                <ul className="space-y-1.5" data-testid="deductions-list">
                  {detail.deductions.map((d) => (
                    <li key={d.linkId} className="flex items-center justify-between gap-3 rounded-xl border border-slate-200 px-3 py-2 text-xs dark:border-white/[0.06]">
                      <span className="min-w-0 text-slate-600 dark:text-slate-300">
                        <span className="flex items-center gap-1.5 font-semibold text-slate-800 dark:text-slate-100"><Link2 className="h-3.5 w-3.5" /> {d.componentName || d.componentCode}</span>
                        <span className="mt-0.5 block">{d.year}-{String(d.month).padStart(2, '0')} · {t(d.runStatus)} · <span className="font-mono">{d.componentCode}</span></span>
                      </span>
                      <span className="shrink-0 text-end"><span className="block font-mono font-semibold text-slate-800 dark:text-slate-100">{money(d.linkedAmount)} {currency}</span>{d.linkedAmount !== d.deductionAmount && <span className="text-[10px] text-slate-400">{t('Of {amount}', { amount: money(d.deductionAmount) })}</span>}</span>
                    </li>
                  ))}
                </ul>
              )}
              {canRecord && !superseded && (
                detail.contributions.length === 0 ? (
                  <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400">{t("Record a contribution first; a deduction is linked to a contribution.")}</p>
                ) : candidates && candidates.length === 0 ? (
                  <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400">{t("This employee has no unlinked, non-statutory payroll deductions to link. Deductions appear here once a payroll run includes one.")}</p>
                ) : (
                  <form onSubmit={linkDeduction} aria-label={t("Link payroll deduction")} className="mt-3 grid grid-cols-2 gap-2 rounded-xl border border-slate-200 p-3 dark:border-white/[0.06]">
                    <label className={LABEL}>{t("Contribution")}<select className={INPUT} value={link.contributionId} onChange={(e) => setLink((l) => ({ ...l, contributionId: e.target.value }))}>
                        {detail.contributions.map((c) => <option key={c.id} value={c.id}>{t("Effective from")}{c.effectiveFrom} · {t('Employee share: {amount}', { amount: money(c.employeeAmount) })}</option>)}
                      </select>
                    </label>
                    <label className={LABEL}>{t("Payroll deduction")}<select className={INPUT} required value={link.deductionId} onChange={(e) => setLink((l) => ({ ...l, deductionId: e.target.value }))}>
                        <option value="">{t("Select…")}</option>
                        {(candidates ?? []).map((d) => <option key={d.id} value={d.id}>{d.year}-{String(d.month).padStart(2, '0')} · {d.componentName || d.componentCode} · {money(d.amount)}</option>)}
                      </select>
                    </label>
                    <label className={LABEL}>{t("Linked amount (optional)")}<input type="number" min={0} step="0.01" className={INPUT} placeholder={t("Defaults to deduction amount")} value={link.amount} onChange={(e) => setLink((l) => ({ ...l, amount: e.target.value }))} />
                    </label>
                    <div className="flex items-end justify-end"><button type="submit" disabled={busy !== null || !link.deductionId} className={PRIMARY}>{busy === 'link' ? 'Linking…' : 'Link deduction'}</button></div>
                  </form>
                )
              )}
            </section>
          </div>
        )}
        {amending && <AdditionalBenefitForm plans={plans} employee={{ id: amending.employeeId, fullName: amending.employeeName }} existing={amending} onClose={() => setAmending(null)} onRequested={() => { setAmending(null); onChanged(enrollmentId); }} />}
        {adding && detail && <BenefitChecklist plans={plans} employee={{ id: detail.enrollment.employeeId, fullName: detail.enrollment.employeeName }} onClose={() => setAdding(false)} onChanged={() => onChanged(enrollmentId)} />}
        {additionalRequestId && <AdditionalBenefitRequestDetail requestId={additionalRequestId} onClose={() => setAdditionalRequestId(null)} />}
      </div>
    </div>, document.body
  );
}




function ExceptionChanges({ before, after, currency }: { before: string; after: string; currency: string }) {
  const { t } = useLocale();
  const format = useFormat();
  let previous: Record<string, unknown>, next: Record<string, unknown>;
  try {
    previous = JSON.parse(before);
    next = JSON.parse(after);
    if (!previous || !next || typeof previous !== 'object' || typeof next !== 'object') return null;
  } catch { return null; }
  const fields = { coverageTier: 'Coverage', entitlementTier: 'Entitlement tier', maximumBenefitAmount: 'Benefit limit', requestedBenefitAmount: 'Enrolled value', limitPeriod: 'Limit period', status: 'Status', effectiveFrom: 'Effective from' };
  const value = (record: Record<string, unknown>, key: string) => record[key] ?? record[key[0].toUpperCase() + key.slice(1)] ?? null;
  const displayValue = (record: Record<string, unknown>, key: string) => {
    const raw = value(record, key);
    if (raw === null) return t('Not set');
    if ((key === 'maximumBenefitAmount' || key === 'requestedBenefitAmount') && typeof raw === 'number') return format.money(raw, currency);
    if (key === 'effectiveFrom' && typeof raw === 'string') return format.date(raw);
    if (key === 'coverageTier' || key === 'limitPeriod' || key === 'status') return t(raw === 'PerEnrollment' ? 'Per enrollment' : String(raw));
    return String(raw);
  };
  const changes = Object.entries(fields).filter(([key]) => value(previous, key) !== value(next, key));
  return changes.length > 0 ? <dl className="mt-2 space-y-1 text-slate-600 dark:text-slate-300">{changes.map(([key, label]) => <div key={key} className="flex flex-wrap gap-x-2"><dt>{t(label)}:</dt><dd>{displayValue(previous, key)} → {displayValue(next, key)}</dd></div>)}</dl> : null;
}
