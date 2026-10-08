'use client';

import type { SkippedWelcomeCode } from '../../api/employeeAccess';
import { useLocale } from '../../contexts/LocaleContext';
import { GENERIC_SKIP_KEY, skipReasonKey } from '../../lib/employeeAccess';

/** Who was skipped and why, one line per person, in the copy deck's words. Server text is never shown. */
export function SkippedList({ skipped, names }: { skipped: SkippedWelcomeCode[]; names: Record<number, string> }) {
  const { t } = useLocale();
  if (skipped.length === 0) return null;
  return (
    <ul className="max-h-32 space-y-0.5 overflow-y-auto rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900" data-testid="skipped-list">
      {skipped.map((s) => {
        const name = names[s.employeeId] ?? t('Employee {id}', { id: s.employeeId });
        return (
          <li key={`${s.employeeId}-${s.reasonCode}`}>
            <span className="font-semibold">{name}</span>
            <span aria-hidden="true"> · </span>
            <span>{t(skipReasonKey(s.reasonCode) ?? GENERIC_SKIP_KEY, { name })}</span>
          </li>
        );
      })}
    </ul>
  );
}
