'use client';

import { useLocale } from '../contexts/LocaleContext';
import { formatCalendarDate } from '../lib/calendarDate';
import type { StatutoryLeaveHistoryItem } from '../api/leave';

const APPROVED_STATUSES = new Set(['Approved', 'CancellationRequested']);

/**
 * The employee's earlier leave of the same Saudi statutory kind (maternity, Hajj, bereavement…),
 * in plain language, beside a request an approver is deciding. Renders nothing when there is none.
 */
export function StatutoryLeaveHistory({ items }: { items?: StatutoryLeaveHistoryItem[] | null }) {
  const { t } = useLocale();
  if (!items || items.length === 0) return null;
  return (
    <div className="mt-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-300">
      <p className="font-semibold">{t('Earlier statutory leave of this kind')}</p>
      <ul className="mt-1 space-y-0.5">
        {items.map((h) => {
          const approved = APPROVED_STATUSES.has(h.status);
          const key = h.sameEvent
            ? approved
              ? 'Earlier {kind}: {days} day(s), {from} – {to}, approved — same event window.'
              : 'Earlier {kind}: {days} day(s), {from} – {to}, pending approval — same event window.'
            : approved
              ? 'Earlier {kind}: {days} day(s), {from} – {to}, approved.'
              : 'Earlier {kind}: {days} day(s), {from} – {to}, pending approval.';
          const sentence = t(key)
            .replace('{kind}', h.statutoryKind)
            .replace('{days}', String(h.totalDays))
            .replace('{from}', formatCalendarDate(h.startDate, 'en-GB'))
            .replace('{to}', formatCalendarDate(h.endDate, 'en-GB'));
          return <li key={h.requestId} className={h.sameEvent ? 'font-semibold' : undefined}>{sentence}</li>;
        })}
      </ul>
    </div>
  );
}
