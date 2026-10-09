'use client';

import { useEffect, useState } from 'react';
import client from '@/src/api/client';
import { benefitsErrorMessage, type BenefitPaymentPolicy as Policy } from '@/src/api/benefits';
import { useAuth } from '@/src/contexts/AuthContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import { FormError, INPUT, LABEL } from './benefitUi';

export const DEFAULT_PAYMENT_POLICY: Policy = { delivery: 'Coverage', amount: null, frequency: 'Monthly', paymentMonth: null, prorate: false, salaryComponentId: null, receiptRequired: false, receiptLabel: 'Receipt or invoice', claimWindowDays: null, instructions: '' };
export const DELIVERY_LABELS = { Coverage: 'Coverage only', SalaryAllowance: 'Add to salary', PayrollDeduction: 'Deduct from salary', Reimbursement: 'Reimburse approved claims' };
export const policyTreatment = (policy: Policy): 'Coverage' | 'CashAllowance' | 'Reimbursement' | 'OtherNonCash' => policy.delivery === 'SalaryAllowance' ? 'CashAllowance' : policy.delivery === 'Reimbursement' ? 'Reimbursement' : policy.delivery === 'Coverage' ? 'Coverage' : 'OtherNonCash';
type PaymentComponent = { id: string; code: string; name: string; componentType: string; isTaxable: boolean; isActive: boolean; salaryStructureId?: string | null };

export function BenefitPolicySummary({ policy, currency }: { policy?: Policy | null; currency: string }) {
  const { t } = useLocale(); const format = useFormat();
  if (!policy) return null;
  return <div className="mt-3 rounded-lg bg-blue-50 p-3 text-xs text-blue-900 dark:bg-blue-500/10 dark:text-blue-200" data-testid="benefit-payment-summary">
    <p className="font-semibold">{t(DELIVERY_LABELS[policy.delivery])}{policy.amount != null && policy.delivery !== 'Reimbursement' ? ` · ${format.money(policy.amount, currency)}` : ''}{['SalaryAllowance', 'PayrollDeduction'].includes(policy.delivery) ? ` · ${t(policy.frequency === 'OneTime' ? 'One time' : policy.frequency)}` : ''}</p>
    {policy.delivery === 'Coverage' && <p className="mt-1">{t('This benefit does not create a payroll payment.')}</p>}
    {policy.delivery === 'Reimbursement' && policy.amount != null && <p className="mt-1 font-semibold">{t('Policy claim limit: {amount}', { amount: format.money(policy.amount, currency) })}</p>}
    {policy.delivery === 'Reimbursement' && <p className="mt-1">{t('Submit a claim for approval. Approved claims are paid through payroll.')}{policy.receiptRequired ? ` ${t('A receipt is required.')}` : ''}</p>}
    {policy.frequency === 'Annual' && policy.paymentMonth && policy.delivery !== 'Reimbursement' && <p className="mt-1">{t('Payment month: {month}', { month: format.number(policy.paymentMonth) })}</p>}
    {policy.prorate && <p className="mt-1">{t('Prorated for covered days in the month.')}</p>}
    {policy.instructions && <p className="mt-2 whitespace-pre-wrap">{policy.instructions}</p>}
  </div>;
}

export function BenefitPaymentPolicyEditor({ value, onChange, currency, editing, paymentMethodLocked }: { value: Policy; onChange: (value: Policy) => void; currency: string; editing: boolean; paymentMethodLocked: boolean }) {
  const { t } = useLocale();
  const { hasPermission } = useAuth();
  const [creating, setCreating] = useState(false);
  const [savingComponent, setSavingComponent] = useState(false);
  const [componentName, setComponentName] = useState('');
  const [componentCode, setComponentCode] = useState('');
  const [taxable, setTaxable] = useState(false);
  const [components, setComponents] = useState<PaymentComponent[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const paid = value.delivery !== 'Coverage';
  const salary = value.delivery === 'SalaryAllowance' || value.delivery === 'PayrollDeduction';
  const kind = value.delivery === 'PayrollDeduction' ? 'Deduction' : 'Earning';
  useEffect(() => {
    if (!paid) return;
    let live = true; setLoading(true); setError(null);
    client.get<PaymentComponent[]>('/api/compensation/benefits/payment-components').then(response => { if (live) setComponents(response.data); })
      .catch(err => { if (live) setError(benefitsErrorMessage(err, t('Could not load payroll mappings.'))); }).finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [paid, t]);
  const set = <K extends keyof Policy>(key: K, next: Policy[K]) => onChange({ ...value, [key]: next });
  const createComponent = async () => {
    if (!componentCode.trim() || !componentName.trim()) { setError(t('Enter a payroll mapping name and code.')); return; }
    setSavingComponent(true); setError(null);
    try {
      const response = await client.post<PaymentComponent>('/api/compensation/benefits/payment-components', { code: componentCode.trim().toUpperCase(), name: componentName.trim(), componentType: kind, isTaxable: kind === 'Earning' && taxable });
      setComponents(current => [...current, response.data]); set('salaryComponentId', response.data.id); setCreating(false);
    } catch (err) { setError(benefitsErrorMessage(err, t('Could not create the payroll mapping.'))); }
    finally { setSavingComponent(false); }
  };
  const available = components.filter(item => item.isActive && !item.salaryStructureId && item.componentType === kind);
  return <fieldset disabled={savingComponent} className="space-y-3 rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="benefit-policy-editor">
    <label className={LABEL}>{t('How this benefit works')}<select disabled={paymentMethodLocked} aria-label={t('How this benefit works')} className={INPUT} value={value.delivery} onChange={event => {
      const delivery = event.target.value as Policy['delivery'];
      if (delivery === 'PayrollDeduction') setTaxable(false);
      onChange({ ...DEFAULT_PAYMENT_POLICY, delivery, instructions: value.instructions, receiptRequired: delivery === 'Reimbursement', receiptLabel: t('Receipt or invoice') });
    }}>{Object.entries(DELIVERY_LABELS).map(([key, label]) => <option key={key} value={key}>{t(label)}</option>)}</select></label>
    {paid && <>
      <div className="grid gap-3 sm:grid-cols-2">
        <label className={LABEL}>{salary ? t('Payment amount ({currency})', { currency }) : t('Claim limit ({currency})', { currency })}<input className={INPUT} type="number" min="0.01" max="999999999999.99" step="0.01" required={salary} value={value.amount ?? ''} onChange={event => set('amount', event.target.value ? Number(event.target.value) : null)} /></label>
        {salary && <label className={LABEL}>{t('Payment frequency')}<select aria-label={t('Payment frequency')} className={INPUT} value={value.frequency} onChange={event => onChange({ ...value, frequency: event.target.value as Policy['frequency'], paymentMonth: event.target.value === 'Annual' ? 1 : null, prorate: event.target.value === 'Monthly' && value.prorate })}><option value="Monthly">{t('Monthly')}</option><option value="Annual">{t('Annual')}</option><option value="OneTime">{t('One time')}</option></select></label>}
        {salary && value.frequency === 'Annual' && <label className={LABEL}>{t('Payment month')}<select aria-label={t('Payment month')} className={INPUT} required value={value.paymentMonth ?? ''} onChange={event => set('paymentMonth', Number(event.target.value))}>{Array.from({ length: 12 }, (_, i) => <option key={i + 1} value={i + 1}>{i + 1}</option>)}</select></label>}
      </div>
      {!salary && <p className="text-xs text-slate-500">{t('The lower of this limit and the employee’s assigned benefit limit applies. A limit is required before claiming.')}</p>}
      {salary && value.frequency === 'Monthly' && <label className="flex gap-2 text-xs text-slate-600 dark:text-slate-300"><input type="checkbox" checked={value.prorate} onChange={event => set('prorate', event.target.checked)} />{t('Prorate for covered days')}</label>}
      <FormError message={error} />
      <label className={LABEL}>{t('Payroll mapping')}<select aria-label={t('Payroll mapping')} className={INPUT} required value={value.salaryComponentId ?? ''} onChange={event => set('salaryComponentId', event.target.value || null)} disabled={loading}><option value="">{loading ? t('Loading…') : t('Select payroll mapping')}</option>{available.map(item => <option key={item.id} value={item.id}>{item.name} ({item.code}) · {t(item.isTaxable ? 'Taxable' : 'Non-taxable')}</option>)}</select></label>
      {hasPermission('payroll.write') && <div className="text-xs">
        {!creating ? <button type="button" className="font-semibold text-sapphire" onClick={() => setCreating(true)}>{t('Add payroll mapping')}</button> : <div className="space-y-3 rounded-lg bg-slate-50 p-3 dark:bg-white/5">
          <div className="grid gap-3 sm:grid-cols-2"><label className={LABEL}>{t('Mapping name')}<input className={INPUT} maxLength={200} value={componentName} onChange={event => setComponentName(event.target.value)} /></label><label className={LABEL}>{t('Mapping code')}<input className={INPUT} maxLength={50} value={componentCode} onChange={event => setComponentCode(event.target.value)} /></label></div>
          {kind === 'Earning' && <label className="flex items-center gap-2"><input type="checkbox" checked={taxable} onChange={event => setTaxable(event.target.checked)} />{t('Taxable')}</label>}
          <p className="text-slate-500">{t('Payroll uses this name and tax setting for the benefit. The benefit policy supplies the payment amount.')}</p>
          <div className="flex gap-3"><button type="button" disabled={savingComponent} className="font-semibold text-sapphire" onClick={() => void createComponent()}>{savingComponent ? t('Saving…') : t('Create payroll mapping')}</button><button type="button" disabled={savingComponent} onClick={() => setCreating(false)}>{t('Cancel')}</button></div>
        </div>}
      </div>}
      {!loading && !error && available.length === 0 && <p className="text-xs text-amber-700 dark:text-amber-300">{t('No dedicated payroll mapping is available. Ask payroll administration to create one before saving this policy.')}</p>}
      {value.delivery === 'Reimbursement' && <>
        <label className="flex gap-2 text-xs text-slate-600 dark:text-slate-300"><input type="checkbox" checked={value.receiptRequired} onChange={event => set('receiptRequired', event.target.checked)} />{t('Require receipt or invoice')}</label>
        <details><summary className="cursor-pointer text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Claim settings')}</summary><div className="mt-3 grid gap-3 sm:grid-cols-2"><label className={LABEL}>{t('Receipt label')}<input className={INPUT} required={value.receiptRequired} maxLength={120} value={value.receiptLabel} onChange={event => set('receiptLabel', event.target.value)} /></label><label className={LABEL}>{t('Submit within (days)')}<input type="number" className={INPUT} min="1" max="3650" step="1" value={value.claimWindowDays ?? ''} onChange={event => set('claimWindowDays', event.target.value ? Number(event.target.value) : null)} /></label></div></details>
      </>}
    </>}
    <label className={LABEL}>{t('Employee instructions (optional)')}<textarea className={INPUT} rows={2} maxLength={2000} value={value.instructions} onChange={event => set('instructions', event.target.value)} /></label>
    {editing && <div className="space-y-1 text-xs text-slate-500"><p>{t('Policy changes apply to future assignments. Existing employee benefits retain their agreed policy.')}</p><p>{t('Once saved, the payment method cannot change. Create a new plan for a different payment method.')}</p></div>}
  </fieldset>;
}
