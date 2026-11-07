'use client';

import Link from 'next/link';
import { AlertTriangle } from 'lucide-react';
import type { CashAllowanceIssue } from '../../api/payroll';
import { useLocale } from '../../contexts/LocaleContext';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';

/** How many employees are listed before "and more": the list is a to-do, not a report. */
const SHOWN = 25;

/** The sentence for each problem (i18n keys, whole sentences so Arabic orders its own words). An unknown one falls back to the server's words. */
const SENTENCES: Record<string, string> = {
  no_definition: 'The allowance {code} on this salary is no longer set up in Benefits by grade. Remove it from the salary, or set it up again.',
  not_confirmed: 'The allowance {code} is waiting for Finance to confirm it in Benefits by grade.',
  no_gl_driver: 'The allowance {code} has no GL account yet. Finance sets it in Benefits by grade.',
};

function employeeHref(id: number): string | null {
  try { return employeeWorkspaceHref(id, { section: 'pay-benefits', panel: 'salary' }); } catch { return null; }
}

/**
 * Payroll readiness: employees whose monthly allowance line the run could not use. Any of them stops the run from being ready, so they are
 * named here, with the way to fix each: the employee's salary, or Benefits by grade when the allowance itself needs Finance.
 */
export function CashAllowanceIssuesPanel({ issues }: { issues: readonly CashAllowanceIssue[] }) {
  const { t } = useLocale();
  if (issues.length === 0) return null;
  const listed = issues.slice(0, SHOWN);
  return (
    <section aria-labelledby="cash-allowance-issues-heading" className="surface overflow-hidden border border-rose-200 dark:border-rose-500/30" data-testid="cash-allowance-issues">
      <div className="flex items-start gap-3 border-b border-slate-100 px-5 py-4 dark:border-white/10">
        <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-rose-600 dark:text-rose-400" aria-hidden="true" />
        <div className="min-w-0">
          <h3 id="cash-allowance-issues-heading" className="text-sm font-semibold text-slate-900 dark:text-white">
            {t('{count, plural, one {# monthly allowance line can’t be used yet} other {# monthly allowance lines can’t be used yet}}', { count: issues.length })}
          </h3>
          <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-300">{t('Payroll is not ready to process until each one is fixed.')}</p>
        </div>
      </div>
      <ul className="divide-y divide-slate-100 dark:divide-white/5">
        {listed.map(issue => {
          const href = employeeHref(issue.employeeId);
          const sentence = SENTENCES[issue.problem];
          const needsBenefits = issue.problem === 'not_confirmed' || issue.problem === 'no_gl_driver';
          return (
            <li key={`${issue.employeeId}-${issue.code}-${issue.problem}`} className="flex flex-wrap items-center gap-x-4 gap-y-1 px-5 py-2.5 text-sm" data-testid="cash-allowance-issue">
              <span className="min-w-0 flex-1 text-slate-800 dark:text-slate-100">
                <span className="font-medium">{href ? <Link href={href} className="text-sapphire underline"><bdi>{issue.employeeName}</bdi></Link> : <bdi>{issue.employeeName}</bdi>}</span>
                {' '}<span className="text-xs text-slate-500" dir="ltr"><bdi>{issue.employeeCode}</bdi></span>
                <span className="block text-xs text-slate-600 dark:text-slate-300">
                  {sentence ? t(sentence, { code: issue.code }) : issue.message}
                </span>
              </span>
              {needsBenefits && <Link href="/benefits/by-grade" className="text-xs font-semibold text-sapphire underline">{t('Open Benefits by grade')}</Link>}
            </li>
          );
        })}
      </ul>
      {issues.length > listed.length && <p className="border-t border-slate-100 px-5 py-2 text-xs text-slate-600 dark:border-white/10 dark:text-slate-300">
        {t('{count} more are not listed. Fix these and refresh to see them.', { count: issues.length - listed.length })}
      </p>}
    </section>
  );
}
