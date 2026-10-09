'use client';

import type { AdditionalBenefitTerms, BenefitEnrollment } from '@/src/api/benefits';
import { useFormat } from '@/src/hooks/useFormat';
import { useLocale } from '@/src/contexts/LocaleContext';

export const TREATMENT_LABELS = { Coverage: 'Insurance or benefit coverage', CashAllowance: 'Cash allowance', Reimbursement: 'Reimbursement', LoanEligibility: 'Loan eligibility', OtherNonCash: 'Other non-cash benefit' };
export const PERIOD_LABELS = { PerEnrollment: 'Per enrollment', Monthly: 'Monthly', Annual: 'Annual', Lifetime: 'Lifetime' };
export const COST_FREQUENCY_LABELS = { OneTime: 'One-time', Monthly: 'Monthly', Annual: 'Annual' };

export function AdditionalBenefitSummary({ terms, currency, baseline, employeeName, planName }: {
  terms: AdditionalBenefitTerms; currency: string; baseline?: BenefitEnrollment | null; employeeName: string; planName: string;
}) {
  const { t } = useLocale();
  const format = useFormat();
  const cap = (amount: number | null) => amount === null ? t('No monetary cap') : format.money(amount, currency);
  return <div className="space-y-3 text-xs" data-testid="additional-benefit-summary">
    <div className="rounded-xl border border-sapphire/20 bg-sapphire/5 p-3 text-slate-700 dark:text-slate-200">
      <p className="font-semibold">{t('Grade defaults stay unchanged')}</p>
      <p className="mt-1">{t(baseline ? 'Only this employee’s additional benefit will change.' : 'This adds one individual benefit for {employee}.', { employee: employeeName })}</p>
    </div>
    <dl className="grid grid-cols-1 gap-3 sm:grid-cols-2">
      <div><dt className="text-slate-500 dark:text-slate-400">{t('Employee')}</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{employeeName}</dd></div>
      <div><dt className="text-slate-500 dark:text-slate-400">{t('Additional benefit')}</dt><dd className="font-semibold text-slate-800 dark:text-slate-100">{planName}</dd></div>
      <div><dt className="text-slate-500 dark:text-slate-400">{t('Benefit treatment')}</dt><dd>{t(TREATMENT_LABELS[terms.treatment])}</dd></div>
      <div><dt className="text-slate-500 dark:text-slate-400">{t('Coverage tier')}</dt><dd>{t(terms.coverageTier)} · {terms.entitlementTier}</dd></div>
      <div><dt className="text-slate-500 dark:text-slate-400">{t('Individual benefit limit')}</dt><dd className="font-semibold">{cap(terms.maximumBenefitAmount)} · {t(PERIOD_LABELS[terms.limitPeriod])}</dd></div>
      {terms.requestedBenefitAmount !== null && <div><dt className="text-slate-500 dark:text-slate-400">{t('Enrolled value')}</dt><dd>{format.money(terms.requestedBenefitAmount, currency)}</dd></div>}
      <div><dt className="text-slate-500 dark:text-slate-400">{t('Starts')}</dt><dd>{format.date(terms.effectiveFrom)}</dd></div>
      <div><dt className="text-slate-500 dark:text-slate-400">{t(terms.effectiveTo ? 'Ends' : 'Review due')}</dt><dd>{format.date(terms.effectiveTo || terms.reviewDate)}</dd></div>
      {terms.effectiveTo && terms.reviewDate && <div><dt className="text-slate-500 dark:text-slate-400">{t('Review due')}</dt><dd>{format.date(terms.reviewDate)}</dd></div>}
    </dl>
    {baseline && <div className="rounded-lg border border-slate-200 p-3 dark:border-white/10"><p className="font-semibold">{t('Current additional benefit')}</p><p className="mt-1">{t(baseline.coverageTier)} · {baseline.entitlementTier} · {cap(baseline.maximumBenefitAmount)} · {t(baseline.limitPeriod === 'PerEnrollment' ? 'Per enrollment' : baseline.limitPeriod)}</p></div>}
    <div className="rounded-xl bg-slate-50 p-3 dark:bg-white/[0.04]">
      <p className="font-semibold text-slate-800 dark:text-slate-100">{t('Financial review')}</p>
      <dl className="mt-2 grid grid-cols-2 gap-3">
        <div><dt className="text-slate-500 dark:text-slate-400">{t('Planned employer cost')}</dt><dd>{terms.plannedEmployerCost === null ? t('Not specified') : format.money(terms.plannedEmployerCost, currency)}</dd></div>
        <div><dt className="text-slate-500 dark:text-slate-400">{t('Planned employee cost')}</dt><dd>{terms.plannedEmployeeCost === null ? t('Not specified') : format.money(terms.plannedEmployeeCost, currency)}</dd></div>
      </dl>
      {terms.costFrequency && <p className="mt-1 text-slate-500 dark:text-slate-400">{t(COST_FREQUENCY_LABELS[terms.costFrequency])}</p>}
      <p className="mt-2 text-slate-600 dark:text-slate-300">{t('Approval assigns the benefit. Payments, contributions and payroll deductions must be recorded separately.')}</p>
    </div>
    <div><p className="font-semibold text-slate-700 dark:text-slate-200">{t('Reason shown to employee')}</p><p className="mt-1 whitespace-pre-wrap text-slate-600 dark:text-slate-300">{terms.reason}</p></div>
    <div><p className="font-semibold text-slate-700 dark:text-slate-200">{t('Internal justification')}</p><p className="mt-1 whitespace-pre-wrap text-slate-600 dark:text-slate-300">{terms.internalJustification}</p></div>
  </div>;
}
