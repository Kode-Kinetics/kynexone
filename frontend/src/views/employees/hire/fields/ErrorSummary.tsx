'use client';

import { forwardRef } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';
import type { FieldIssue } from '@/src/lib/hire/hireValidation';
import { fieldId } from './FieldShell';
import { issueText } from '../issueText';

/** Focus target after a failed Next / Create. Each link opens the right step and focuses its field. */
export const ErrorSummary = forwardRef<HTMLDivElement, { issues: FieldIssue[]; onGo: (issue: FieldIssue) => void }>(function ErrorSummary({ issues, onGo }, ref) {
  const { t, locale } = useLocale();
  if (!issues.length) return null;
  return (
    <div ref={ref} tabIndex={-1} data-testid="error-summary" role="group" aria-labelledby="hire-error-summary-title" className="mb-4 rounded-xl border border-red-300 bg-red-50 p-4 text-sm text-red-900 outline-none focus-visible:ring-2 focus-visible:ring-red-500 dark:border-red-400/40 dark:bg-red-500/10 dark:text-red-100">
      <h2 id="hire-error-summary-title" className="font-semibold">{t('There is a problem')}</h2>
      <ul className="mt-2 list-disc space-y-1 ps-5">
        {issues.map(issue => (
          <li key={issue.field}>
            <a href={`#${fieldId(issue.field)}`} onClick={e => { e.preventDefault(); onGo(issue); }} className="underline">{issueText(issue, t, locale)}</a>
          </li>
        ))}
      </ul>
    </div>
  );
});
