'use client';

import { Fragment, type ReactNode } from 'react';
import { useLocale } from '../contexts/LocaleContext';
import { formatCalendarDate } from '../lib/calendarDate';
import { STATUTORY_KIND_LABEL } from '../lib/ksaStatutoryLeave';
import type { StatutoryLeaveContext } from '../api/leave';

const APPROVED_STATUSES = new Set(['Approved', 'CancellationRequested']);

/**
 * Renders a translated sentence whose placeholders are filled with React nodes, so a value such as
 * the leave kind can sit in its own <bdi> and keep its direction inside an Arabic sentence.
 */
function fill(sentence: string, values: Record<string, ReactNode>): ReactNode[] {
  return sentence.split(/(\{[a-z]+\})/g).map((part, i) => {
    const m = /^\{([a-z]+)\}$/.exec(part);
    return <Fragment key={i}>{m && m[1] in values ? values[m[1]] : part}</Fragment>;
  });
}

/**
 * Beside a Saudi statutory leave request an approver is deciding: a prominent flag when the requester
 * declared it a separate event, and the employee's earlier leave of the same kind in plain language.
 * Renders nothing when there is neither.
 */
export function StatutoryLeaveHistory({ context }: { context?: StatutoryLeaveContext | null }) {
  const { t, locale } = useLocale();
  if (!context) return null;
  const date = (d: string | null) => formatCalendarDate(d, locale);
  const kind = <bdi>{t(STATUTORY_KIND_LABEL[context.statutoryKind] ?? context.statutoryKind)}</bdi>;
  const items = context.history;
  if (!context.separateEventReason && items.length === 0) return null;
  return (
    <div className="mt-2 space-y-2">
      {context.separateEventReason && (
        <div className="rounded-lg border border-rose-300 bg-rose-50 px-3 py-2 text-xs font-semibold text-rose-800 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
          {fill(t('Declared separate event: {date} — {reason}'), {
            date: <bdi>{date(context.eventDate)}</bdi>,
            reason: <bdi>{context.separateEventReason}</bdi>,
          })}
        </div>
      )}
      {items.length > 0 && (
        <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-300">
          <p className="font-semibold">{t('Earlier statutory leave of this kind')}</p>
          <p>{fill(t('This request: event date {event}.'), {
            event: <bdi>{context.eventDate ? date(context.eventDate) : t('no event date')}</bdi>,
          })}</p>
          <ul className="mt-1 space-y-0.5">
            {items.map((h) => {
              const approved = APPROVED_STATUSES.has(h.status);
              const key = h.sameEvent
                ? approved
                  ? 'Earlier {kind}: {days} day(s), {from} – {to}, approved — same event. Event date: {event}.'
                  : 'Earlier {kind}: {days} day(s), {from} – {to}, pending approval — same event. Event date: {event}.'
                : approved
                  ? 'Earlier {kind}: {days} day(s), {from} – {to}, approved. Event date: {event}.'
                  : 'Earlier {kind}: {days} day(s), {from} – {to}, pending approval. Event date: {event}.';
              return (
                <li key={h.requestId} className={h.sameEvent ? 'font-semibold' : undefined}>
                  {fill(t(key), {
                    kind,
                    days: <bdi>{h.totalDays}</bdi>,
                    from: <bdi>{date(h.startDate)}</bdi>,
                    to: <bdi>{date(h.endDate)}</bdi>,
                    event: <bdi>{h.eventDate ? date(h.eventDate) : t('no event date')}</bdi>,
                  })}
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </div>
  );
}
