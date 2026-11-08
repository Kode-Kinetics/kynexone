'use client';

import { Hourglass } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { fillTemplate } from '../../lib/gradeLoanLimits';
import type { WaitingAllowance } from '../../lib/cashAllowance';

/**
 * Benefits by grade, for someone who can confirm cash allowances (payroll.approve): the allowances somebody added that are still waiting for
 * Finance, each with the Confirm action. Nothing else told Finance they were there, and payroll then refused on pay day. It is the target of the
 * payroll readiness link (`#waiting-for-finance`) and of the notice sent when an allowance is added.
 */
export function WaitingForFinance({ items, nameOf, onConfirm }: {
  items: readonly WaitingAllowance[]; nameOf: (code: string) => string; onConfirm: (code: string) => void;
}) {
  const { t } = useLocale();
  if (items.length === 0) return null;
  return <section id="waiting-for-finance" aria-labelledby="waiting-for-finance-heading" data-testid="waiting-for-finance-list"
    className="scroll-mt-24 rounded-xl border border-amber-400 bg-amber-50 p-3 text-sm text-amber-950 dark:border-amber-500/50 dark:bg-amber-500/10 dark:text-amber-100">
    <h3 id="waiting-for-finance-heading" className="flex items-center gap-2 font-semibold">
      <Hourglass className="h-4 w-4 shrink-0" aria-hidden="true" />
      {t('{count, plural, one {# cash allowance is waiting for you to confirm it} other {# cash allowances are waiting for you to confirm them}}', { count: items.length })}
    </h3>
    <p className="mt-0.5 text-xs">{t('Payroll cannot be processed for anyone who has one of these on their salary until it is confirmed.')}</p>
    <ul className="mt-2 divide-y divide-amber-200 dark:divide-amber-500/20">
      {items.map(item => <li key={item.code} className="flex flex-wrap items-center gap-x-4 gap-y-1 py-1.5" data-testid="waiting-for-finance-item">
        <span className="min-w-0 flex-1 font-medium"><bdi>{nameOf(item.code)}</bdi></span>
        {item.canConfirm
          ? <button type="button" className="btn-secondary h-8 px-3 text-xs" aria-label={fillTemplate(t('Review {benefit}, then confirm it'), { benefit: nameOf(item.code) })} onClick={() => onConfirm(item.code)}>{t('Review and confirm')}</button>
          : <span className="text-xs">{t('You added this allowance, so someone else in Finance must confirm it.')}</span>}
      </li>)}
    </ul>
  </section>;
}
