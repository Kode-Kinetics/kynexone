'use client';

import type { SkippedWelcomeCode } from '../../api/employeeAccess';
import { useLocale } from '../../contexts/LocaleContext';
import { GENERIC_SKIP_KEY, skipReasonKey } from '../../lib/employeeAccess';

/**
 * Who was skipped and why, one line per person, in the copy deck's words. Server text is shown only
 * in English and only for a reason this screen does not know; Arabic never shows raw server text.
 */
export function SkippedList({ skipped, names }: { skipped: SkippedWelcomeCode[]; names: Record<number, string> }) {
  const { t, locale } = useLocale();
  if (skipped.length === 0) return null;
  return (
    <ul className="max-h-32 space-y-0.5 overflow-y-auto rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900" data-testid="skipped-list">
      {skipped.map((s) => {
        const key = skipReasonKey(s.reasonCode);
        return (
          <li key={`${s.employeeId}-${s.reasonCode}`}>
            <span className="font-semibold">{names[s.employeeId] ?? t('Employee {id}', { id: s.employeeId })}</span>
            <span aria-hidden="true"> · </span>
            <span>{key ? t(key) : locale === 'en' && s.reason ? s.reason : t(GENERIC_SKIP_KEY)}</span>
          </li>
        );
      })}
    </ul>
  );
}
