'use client';

import { useCallback, useState } from 'react';
import { Check, Loader2 } from 'lucide-react';
import { employeesApi } from '../../api/employees';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { requestFailureReason } from '../../lib/requestFailure';
import { transferConfirmMessage, transferHolderLine, transferTitle, type EmployeeTransferView } from '../../lib/transferSteps';
import { StatusChip } from '../StatusChip';
import { useConfirm } from '../ui/useConfirm';

export type TransferNotice = { tone: 'ok' | 'error'; text: string };

/**
 * Approving the step a transfer waits at. One tiered confirmation names who moves where and which step you are approving,
 * nothing is sent until it is confirmed, and the result (or the server's reason) stays on screen. Who may decide is the
 * server's call (`canDecide`); this never shows the button for a step the server would refuse.
 */
export function useTransferDecisions(reload: () => Promise<void> | void) {
  const { t } = useLocale();
  const { confirm, dialog } = useConfirm();
  const [busyId, setBusyId] = useState<string | null>(null);
  const [notice, setNotice] = useState<TransferNotice | null>(null);

  const decide = useCallback(async (view: EmployeeTransferView) => {
    if (!view.awaitingStep) return;
    const ok = await confirm({
      title: t('Approve this transfer?'),
      message: transferConfirmMessage(view, t),
      confirmLabel: t('Approve transfer'),
      tone: 'primary',
    });
    if (!ok) return;
    setBusyId(view.transfer.id);
    setNotice(null);
    try {
      await employeesApi.approveTransfer(view);
      setNotice({ tone: 'ok', text: view.awaitingStep === 'Hr' ? t('Approved and applied to the employee record.') : t('Approved. The transfer moved on to the next step.') });
    } catch (err) {
      setNotice({ tone: 'error', text: t(requestFailureReason(err)) });
      setBusyId(null);
      return;
    }
    setBusyId(null);
    try { await reload(); } catch { /* the approval already went through; the list refreshes on the next visit */ }
  }, [confirm, reload, t]);

  return { decide, busyId, notice, dialog };
}

export function TransferNoticeLine({ notice }: { notice: TransferNotice | null }) {
  if (!notice) return null;
  return notice.tone === 'ok'
    ? <p role="status" className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm font-medium text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-300">{notice.text}</p>
    : <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm font-medium text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">{notice.text}</p>;
}

export function TransferDecisionList({ rows, busyId, onDecide }: { rows: EmployeeTransferView[]; busyId: string | null; onDecide(view: EmployeeTransferView): void }) {
  const { t } = useLocale();
  const { date } = useFormat();
  return (
    <ul className="space-y-3">
      {rows.map(view => {
        const { transfer } = view;
        const waiting = view.awaitingStep != null;
        return (
          <li key={transfer.id} className="rounded-xl border border-slate-200 p-4 dark:border-white/10" data-testid="transfer-row">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0">
                <p className="font-semibold text-slate-900 dark:text-white"><bdi>{transferTitle(view, t)}</bdi></p>
                <p className="mt-0.5 text-sm text-slate-600 dark:text-slate-300">
                  {t('{from} to {to}, from {date}', {
                    from: [transfer.currentDepartment, transfer.currentBranch].filter(Boolean).join(' / ') || '-',
                    to: [transfer.newDepartment, transfer.newBranch, transfer.newDesignation].filter(Boolean).join(' / ') || '-',
                    date: date(transfer.effectiveDate),
                  })}
                </p>
                {transfer.reason && <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400"><bdi>{transfer.reason}</bdi></p>}
              </div>
              <StatusChip label={transferHolderLine(view, t)} tone={waiting ? 'amber' : 'emerald'} dot />
            </div>
            {view.canDecide && (
              <div className="mt-3">
                <button type="button" className="btn-primary inline-flex items-center gap-1.5" disabled={busyId === transfer.id} onClick={() => onDecide(view)}>
                  {busyId === transfer.id ? <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" /> : <Check className="h-4 w-4" aria-hidden="true" />}
                  {t('Approve')}
                </button>
              </div>
            )}
          </li>
        );
      })}
    </ul>
  );
}
