'use client';

import { useCallback, useId, useState } from 'react';
import { Check, Loader2, Undo2, X } from 'lucide-react';
import { employeesApi } from '../../api/employees';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { requestFailureReason } from '../../lib/requestFailure';
import { transferConfirmMessage, transferHolderLine, transferOutcomeLine, transferTitle, type EmployeeTransferView } from '../../lib/transferSteps';
import { StatusChip } from '../StatusChip';
import { ConfirmDialog, type ConfirmState } from '../ui/ConfirmDialog';
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
  const [closing, setClosing] = useState<{ kind: 'reject' | 'withdraw'; view: EmployeeTransferView } | null>(null);
  const [reason, setReason] = useState('');

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

  const startClose = useCallback((kind: 'reject' | 'withdraw', view: EmployeeTransferView) => { setReason(''); setClosing({ kind, view }); }, []);

  // Reject needs a reason (tier 2: the decider writes why); withdraw takes one if the requester wants to give it.
  const closeState: ConfirmState | null = closing ? {
    title: closing.kind === 'reject' ? t('Reject this transfer?') : t('Withdraw this transfer?'),
    message: <ReasonField kind={closing.kind} view={closing.view} value={reason} onChange={setReason} />,
    confirmLabel: closing.kind === 'reject' ? t('Reject transfer') : t('Withdraw transfer'),
    tone: 'danger',
    resetKey: `${closing.kind}:${closing.view.transfer.id}`,
    onConfirm: async () => {
      const text = reason.trim();
      if (closing.kind === 'reject' && !text) throw new Error(t('Give a reason for rejecting this transfer.'));
      try {
        await employeesApi.closeTransfer(closing.view, closing.kind, text);
      } catch (err) {
        throw new Error(t(requestFailureReason(err)));
      }
      setNotice({ tone: 'ok', text: closing.kind === 'reject' ? t('Rejected. The transfer will not go ahead.') : t('Withdrawn. The transfer will not go ahead.') });
      try { await reload(); } catch { /* the decision already went through; the list refreshes on the next visit */ }
    },
  } : null;
  const closeDialog = <ConfirmDialog state={closeState} onCancel={() => setClosing(null)} />;

  return { decide, reject: (view: EmployeeTransferView) => startClose('reject', view), withdraw: (view: EmployeeTransferView) => startClose('withdraw', view), busyId, notice, dialog: <>{dialog}{closeDialog}</> };
}

function ReasonField({ kind, view, value, onChange }: { kind: 'reject' | 'withdraw'; view: EmployeeTransferView; value: string; onChange(v: string): void }) {
  const { t } = useLocale();
  const id = useId();
  return (
    <div className="space-y-2">
      <p>{kind === 'reject'
        ? t('{employee} will not move to {target}. The requester and the employee are told.', { employee: view.employeeName, target: view.transfer.newDepartment || view.transfer.newBranch || view.transfer.newDesignation || t('Transfer request') })
        : t('The transfer of {employee} is taken back. Whoever was holding it is told.', { employee: view.employeeName })}</p>
      <label htmlFor={id} className="block text-sm font-semibold text-slate-700 dark:text-slate-200">{kind === 'reject' ? t('Reason') : t('Reason (optional)')}</label>
      <textarea id={id} rows={3} maxLength={1000} value={value} onChange={e => onChange(e.target.value)} className="w-full rounded-lg border border-slate-300 bg-white p-2 text-sm dark:border-white/20 dark:bg-white/[0.04]" />
    </div>
  );
}

export function TransferNoticeLine({ notice }: { notice: TransferNotice | null }) {
  if (!notice) return null;
  return notice.tone === 'ok'
    ? <p role="status" className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm font-medium text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-300">{notice.text}</p>
    : <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm font-medium text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">{notice.text}</p>;
}

export function TransferDecisionList({ rows, busyId, onDecide, onReject, onWithdraw }: {
  rows: EmployeeTransferView[]; busyId: string | null; onDecide(view: EmployeeTransferView): void; onReject(view: EmployeeTransferView): void; onWithdraw(view: EmployeeTransferView): void;
}) {
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
              <StatusChip label={transferHolderLine(view, t)} tone={waiting ? 'amber' : view.transfer.status === 'ApprovedApplied' ? 'emerald' : 'slate'} dot />
            </div>
            {transferOutcomeLine(view) && <p className="mt-2 text-sm text-slate-600 dark:text-slate-300">{t('Reason')}: <bdi>{transferOutcomeLine(view)}</bdi></p>}
            {(view.canDecide || view.canReject || view.canWithdraw) && (
              <div className="mt-3 flex flex-wrap gap-2">
                {view.canDecide && (
                  <button type="button" className="btn-primary inline-flex items-center gap-1.5" disabled={busyId === transfer.id} onClick={() => onDecide(view)}>
                    {busyId === transfer.id ? <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" /> : <Check className="h-4 w-4" aria-hidden="true" />}
                    {t('Approve')}
                  </button>
                )}
                {view.canReject && (
                  <button type="button" className="btn-secondary inline-flex items-center gap-1.5" onClick={() => onReject(view)}>
                    <X className="h-4 w-4" aria-hidden="true" />{t('Reject')}
                  </button>
                )}
                {view.canWithdraw && (
                  <button type="button" className="btn-secondary inline-flex items-center gap-1.5" onClick={() => onWithdraw(view)}>
                    <Undo2 className="h-4 w-4" aria-hidden="true" />{t('Withdraw')}
                  </button>
                )}
              </div>
            )}
          </li>
        );
      })}
    </ul>
  );
}
