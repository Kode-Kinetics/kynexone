'use client';

import { useState } from 'react';
import { benefitsApi, benefitsErrorMessage, type BenefitEnrollment } from '@/src/api/benefits';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useAppToast } from '@/src/components/ui/AppToast';
import { COVERAGE_TIERS, FormError, INPUT, LABEL, PRIMARY, SECONDARY, today } from './benefitUi';

export function BenefitExceptionForm({ enrollment, mandatory, currency, onCancel, onSaved }: {
  enrollment: BenefitEnrollment; mandatory: boolean; currency: string; onCancel: () => void; onSaved: (enrollmentId: string) => void;
}) {
  const { t } = useLocale();
  const toast = useAppToast();
  const [form, setForm] = useState({
    effectiveFrom: enrollment.effectiveFrom > today() ? enrollment.effectiveFrom : today(),
    coverageTier: enrollment.coverageTier,
    entitlementTier: enrollment.entitlementTier || 'Standard tier',
    maximumBenefitAmount: enrollment.maximumBenefitAmount?.toString() ?? '',
    requestedBenefitAmount: enrollment.requestedBenefitAmount?.toString() ?? '',
    limitPeriod: enrollment.limitPeriod || 'PerEnrollment',
    status: enrollment.status === 'Waived' ? 'Waived' as const : 'Active' as const,
    reason: '',
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = (key: keyof typeof form, value: string) => setForm(current => ({ ...current, [key]: value }));
  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!form.reason.trim()) { setError('Enter a reason for this individual exception.'); return; }
    const maximumBenefitAmount = form.maximumBenefitAmount.trim() ? Number(form.maximumBenefitAmount) : null;
    const requestedBenefitAmount = form.requestedBenefitAmount.trim() ? Number(form.requestedBenefitAmount) : null;
    if ((maximumBenefitAmount !== null && (!Number.isFinite(maximumBenefitAmount) || maximumBenefitAmount <= 0))
      || (requestedBenefitAmount !== null && (!Number.isFinite(requestedBenefitAmount) || requestedBenefitAmount <= 0))) {
      setError('Enter valid benefit amounts. The enrolled value must be greater than zero.'); return;
    }
    if (maximumBenefitAmount !== null && requestedBenefitAmount !== null && requestedBenefitAmount > maximumBenefitAmount) {
      setError('The enrolled value cannot exceed the individual benefit limit.'); return;
    }
    setSaving(true); setError(null);
    try {
      const saved = await benefitsApi.applyException(enrollment.id, { ...form, expectedUpdatedAtUtc: enrollment.updatedAtUtc, reason: form.reason.trim(), maximumBenefitAmount, requestedBenefitAmount });
      toast.success('Individual benefit exception recorded.');
      onSaved(saved.id);
    } catch (err) { setError(benefitsErrorMessage(err, 'Could not apply the benefit exception.')); }
    finally { setSaving(false); }
  };
  return <form onSubmit={submit} aria-label={t("Apply benefit exception")} className="mt-3 space-y-3 border-t border-slate-200 pt-3 dark:border-white/10">
    <p className="text-slate-600 dark:text-slate-300">{t("These changes apply to this employee only. The grade defaults remain unchanged.")}</p>
    <FormError message={error} />
    <label className={LABEL}>{t("Exception effective from")}<input className={INPUT} type="date" required min={enrollment.effectiveFrom > today() ? enrollment.effectiveFrom : today()} max={enrollment.effectiveTo ?? undefined} value={form.effectiveFrom} onChange={event => set('effectiveFrom', event.target.value)} /></label>
    <div className="grid gap-3 sm:grid-cols-2">
      <label className={LABEL}>{t("Coverage tier")}<select aria-label={t("Coverage tier")} className={INPUT} value={form.coverageTier} onChange={event => set('coverageTier', event.target.value)}>
        {[...new Set([...COVERAGE_TIERS, enrollment.coverageTier])].map(tier => <option key={tier} value={tier}>{tier}</option>)}
      </select></label>
      <label className={LABEL}>{t("Entitlement tier")}<input className={INPUT} required maxLength={100} value={form.entitlementTier} onChange={event => set('entitlementTier', event.target.value)} /></label>
      <label className={LABEL}>{t('Individual benefit limit ({currency})', { currency })}<input className={INPUT} type="number" min="0.01" step="0.01" placeholder={t("No monetary cap")} value={form.maximumBenefitAmount} onChange={event => set('maximumBenefitAmount', event.target.value)} /></label>
      <label className={LABEL}>{t('Enrolled value ({currency})', { currency })}<input className={INPUT} type="number" min="0.01" step="0.01" placeholder={t("Not specified")} value={form.requestedBenefitAmount} onChange={event => set('requestedBenefitAmount', event.target.value)} /></label>
      <label className={LABEL}>{t("Limit period")}<select aria-label={t("Limit period")} aria-describedby={mandatory ? 'mandatory-benefit-limit-period-help' : undefined} disabled={mandatory} className={`${INPUT} disabled:cursor-not-allowed disabled:bg-slate-100 disabled:text-slate-500 dark:disabled:bg-slate-800`} value={form.limitPeriod} onChange={event => set('limitPeriod', event.target.value)}>
        {['PerEnrollment', 'Monthly', 'Annual', 'Lifetime'].map(period => <option key={period} value={period}>{period.replace(/([A-Z])/g, ' $1').trim()}</option>)}
      </select>{mandatory && <span id="mandatory-benefit-limit-period-help" className="mt-1 block text-xs font-normal text-slate-500 dark:text-slate-400">{t('Mandatory benefits keep their configured limit period.')}</span>}</label>
      <label className={LABEL}>{t("Benefit status")}<select aria-label={t("Benefit status")} className={INPUT} value={form.status} onChange={event => set('status', event.target.value)}>
        <option value="Active">{t("Active")}</option>{!mandatory && <option value="Waived">{t("Waived for this employee")}</option>}
      </select></label>
    </div>
    <label className={LABEL}>{t("Exception reason")} <span className="text-rose-500">*</span><textarea className={INPUT} required maxLength={1000} rows={3} value={form.reason} onChange={event => set('reason', event.target.value)} /></label>
    <div className="flex justify-end gap-2"><button type="button" disabled={saving} onClick={onCancel} className={SECONDARY}>{t("Cancel")}</button><button type="submit" disabled={saving || !form.reason.trim()} className={PRIMARY}>{t(saving ? 'Saving…' : 'Save exception')}</button></div>
  </form>;
}
