'use client';

import { useCallback, useEffect, useState } from 'react';
import { leaveRequestsApi, type LeaveAwaitingDecision } from '../../api/leave';
import { notifyApiError } from '../../api/client';
import { useLocale } from '../../contexts/LocaleContext';
import { formatCalendarDate } from '../../lib/calendarDate';
import { useAppToast } from '../ui/AppToast';
import { useConfirm } from '../ui/useConfirm';

/**
 * Pending cancellations of APPROVED leave, for whoever may decide them. An employee asking to cancel approved
 * leave used to land in a queue nobody could open: the endpoints existed, no screen called them. Approve
 * cancellation releases the days; Keep leave refuses the request and the leave stays approved. Both tell the
 * employee, and both go through a confirm because the first one moves a balance.
 */
export function useLeaveCancellationDecision(onDecided: () => void) {
  const { t, locale } = useLocale();
  const toast = useAppToast();
  const { confirm, dialog } = useConfirm();
  const [busyId, setBusyId] = useState<string | null>(null);

  const span = (i: LeaveAwaitingDecision) => ({
    name: i.employeeName,
    type: i.leaveTypeName,
    start: formatCalendarDate(i.startDate, locale),
    end: formatCalendarDate(i.endDate, locale),
    days: i.totalDays,
  });

  const approve = async (i: LeaveAwaitingDecision) => {
    const ok = await confirm({
      title: t('Approve this cancellation?'),
      message: t("{name}'s {type} ({start} to {end}, {days} days) will be cancelled and the days returned to their balance. They will be told.", span(i)),
      confirmLabel: t('Approve cancellation'),
      tone: 'danger',
    });
    if (!ok) return;
    setBusyId(i.id);
    try {
      await leaveRequestsApi.approveCancellation(i.id);
      toast.success(t('Cancellation approved. The leave was cancelled and {name} was told.', { name: i.employeeName }));
      onDecided();
    } catch (e) { notifyApiError(e, t('The cancellation could not be approved.')); }
    finally { setBusyId(null); }
  };

  const keep = async (i: LeaveAwaitingDecision) => {
    const ok = await confirm({
      title: t('Keep this leave?'),
      message: t("The request to cancel {name}'s {type} ({start} to {end}) will be declined and the leave stays approved. They will be told.", span(i)),
      confirmLabel: t('Keep leave'),
      tone: 'primary',
    });
    if (!ok) return;
    setBusyId(i.id);
    try {
      await leaveRequestsApi.rejectCancellation(i.id);
      toast.success(t('Leave kept. {name} was told the cancellation was declined.', { name: i.employeeName }));
      onDecided();
    } catch (e) { notifyApiError(e, t('The cancellation could not be declined.')); }
    finally { setBusyId(null); }
  };

  return { approve, keep, busyId, dialog };
}

/** The cancellation actions for one queue row. */
export function LeaveCancellationActions({ item, busy, onApprove, onKeep }: {
  item: LeaveAwaitingDecision; busy: boolean; onApprove: (i: LeaveAwaitingDecision) => void; onKeep: (i: LeaveAwaitingDecision) => void;
}) {
  const { t } = useLocale();
  return (
    <div className="flex gap-2">
      <button type="button" disabled={busy} onClick={() => onKeep(item)} data-testid="leave-cancellation-keep"
        className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 disabled:opacity-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5">
        {t('Keep leave')}
      </button>
      <button type="button" disabled={busy} onClick={() => onApprove(item)} data-testid="leave-cancellation-approve"
        className="inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50">
        {t('Approve cancellation')}
      </button>
    </div>
  );
}

/** What the employee asked, shown on the row: the dates and the reason they gave. */
export function LeaveCancellationDetail({ item }: { item: LeaveAwaitingDecision }) {
  const { t } = useLocale();
  return (
    <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">
      {item.cancellationReason
        ? t('Asked to cancel approved leave. Reason: {reason}', { reason: item.cancellationReason })
        : t('Asked to cancel approved leave.')}
    </p>
  );
}

/**
 * The Approval Center's strip of pending leave cancellations (they have no row in the shared approvals table, so
 * the table cannot list them). Renders nothing when there are none, or when the list cannot be read: it must
 * never get in the way of the queue under it.
 */
export function LeaveCancellationQueue() {
  const { t, locale } = useLocale();
  const [items, setItems] = useState<LeaveAwaitingDecision[]>([]);
  const load = useCallback(() => {
    leaveRequestsApi.awaitingDecision()
      .then(all => setItems((Array.isArray(all) ? all : []).filter(i => i.kind === 'Cancellation')))
      .catch(() => setItems([]));
  }, []);
  useEffect(() => { load(); }, [load]);
  const { approve, keep, busyId, dialog } = useLeaveCancellationDecision(load);

  if (items.length === 0) return <>{dialog}</>;
  return (
    <section aria-label={t('Leave cancellations awaiting your decision')} data-testid="leave-cancellation-queue"
      className="rounded-lg border border-amber-200 bg-amber-50/60 p-4 dark:border-amber-500/30 dark:bg-amber-500/10">
      <h2 className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
        {t('Leave cancellations awaiting your decision')}
        <span className="rounded-full bg-amber-200 px-2 py-0.5 text-xs font-semibold text-amber-900 dark:bg-amber-500/30 dark:text-amber-100">{items.length}</span>
      </h2>
      <ul className="mt-3 space-y-3">
        {items.map(i => (
          <li key={i.id} data-id={i.id} className="flex flex-wrap items-start justify-between gap-3 rounded-lg bg-white/80 p-3 dark:bg-white/[0.04]">
            <div className="min-w-0">
              <p className="text-sm font-semibold text-slate-900 dark:text-white">{i.employeeName}</p>
              <p className="text-xs text-slate-500 dark:text-slate-400">
                {i.leaveTypeName} · {formatCalendarDate(i.startDate, locale)} – {formatCalendarDate(i.endDate, locale)} · {t('{count} days', { count: i.totalDays })}
              </p>
              <LeaveCancellationDetail item={i} />
            </div>
            <LeaveCancellationActions item={i} busy={busyId === i.id} onApprove={approve} onKeep={keep} />
          </li>
        ))}
      </ul>
      {dialog}
    </section>
  );
}
