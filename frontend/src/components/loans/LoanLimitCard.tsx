'use client';

import type { LoanEligibility } from '../../api/loanGovernance';
import { useLocale } from '../../contexts/LocaleContext';
import { limitCardText, moneyFormatter } from '../../lib/gradeLoanLimits';

/**
 * Explains the applicant's limit before they submit: the grade limit (per loan, outstanding,
 * available), which limit actually caps this request and how it was worked out. Never shows codes.
 * `self` = the employee applying for themselves; false = HR applying on their behalf (third person).
 * Amounts are shown in the currency the server states for the employee's company — never a tenant default.
 */
export function LoanLimitCard({ eligibility, self }: { eligibility: LoanEligibility; self: boolean }) {
  const { t, locale } = useLocale();
  const money = moneyFormatter(eligibility.currency || eligibility.gradeLimit?.currency);
  const text = limitCardText(eligibility, self, locale, t, money);
  if (!text) return null;
  return <div className="space-y-1 rounded-lg border border-sapphire/30 bg-sapphire/5 p-3 text-sm" aria-live="polite">
    {text.heading && <p><span className="font-semibold">{text.heading}:</span> {text.parts.join(' · ')}</p>}
    {text.reason && <p className="font-semibold text-amber-700 dark:text-amber-300">{text.reason}</p>}
    {text.binding && <p className="text-slate-600 dark:text-slate-300">{text.binding}</p>}
    {text.explanation && <p className="font-medium" dir="auto">{text.explanation}</p>}
  </div>;
}
