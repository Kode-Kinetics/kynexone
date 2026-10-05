'use client';

import type { LoanEligibility } from '../../api/loanGovernance';
import { useLocale } from '../../contexts/LocaleContext';
import { useTenantSettings } from '../../contexts/TenantSettingsContext';
import {
  bindingBreakdown, bindingLimitKeys, breakdownExplanation, fillTemplate, gradeReasonFallbackKey, gradeReasonKeys,
} from '../../lib/gradeLoanLimits';

/**
 * Explains the applicant's limit before they submit: the grade limit (per loan, outstanding,
 * available), which limit actually caps this request and how it was worked out. Never shows codes.
 */
export function LoanLimitCard({ eligibility, self }: { eligibility: LoanEligibility; self: boolean }) {
  const { t } = useLocale();
  const { currencyCode } = useTenantSettings();
  const grade = eligibility.gradeLimit;
  const currency = grade?.currency || currencyCode;
  const money = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency, maximumFractionDigits: 2 });
  const breakdown = bindingBreakdown(eligibility);
  const explanation = breakdown ? breakdownExplanation(breakdown, money) : null;
  if (!grade?.applies && !eligibility.bindingLimit) return null;

  const heading = grade?.gradeName
    ? fillTemplate(t(self ? 'Your limit ({grade})' : "This employee's limit ({grade})"), { grade: grade.gradeName })
    : t(self ? 'Your limit' : "This employee's limit");
  const parts: string[] = [];
  if (grade?.applies) {
    parts.push(grade.perLoanCap == null ? t('no per-loan grade limit') : fillTemplate(t('up to {amount} per loan'), { amount: money(grade.perLoanCap) }));
    parts.push(fillTemplate(t('Outstanding {amount}'), { amount: money(grade.outstandingNow) }));
    if (grade.available != null) parts.push(fillTemplate(t('Available {amount}'), { amount: money(grade.available) }));
  }
  const reason = grade?.applies && !grade.eligible
    ? fillTemplate(t(grade.reasonCode ? gradeReasonKeys[grade.reasonCode] ?? gradeReasonFallbackKey : gradeReasonFallbackKey), {
      perLoanCap: grade.perLoanCap == null ? '' : money(grade.perLoanCap),
      outstandingCap: grade.outstandingCap == null ? '' : money(grade.outstandingCap),
    })
    : '';

  return <div className="space-y-1 rounded-lg border border-sapphire/30 bg-sapphire/5 p-3 text-sm" aria-live="polite">
    {grade?.applies && <p><span className="font-semibold">{heading}:</span> {parts.join(' · ')}</p>}
    {reason && <p className="font-semibold text-amber-700 dark:text-amber-300">{reason}</p>}
    {eligibility.bindingLimit && <p className="text-slate-600 dark:text-slate-300">{fillTemplate(t('The limit that applies: {limit}.'), { limit: t(bindingLimitKeys[eligibility.bindingLimit] ?? 'the company loan policy') })}</p>}
    {explanation && <p className="font-medium" dir="auto">{fillTemplate(t(explanation.key), explanation.values)}</p>}
  </div>;
}
