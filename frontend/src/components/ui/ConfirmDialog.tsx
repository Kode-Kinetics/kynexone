'use client';

import { Modal } from '@/src/components/Modal';
import { useEffect, useState } from 'react';

export interface ConfirmState {
  message: string;
  onConfirm: () => void | Promise<void>;
  confirmLabel?: string;
  busy?: boolean;
  error?: string;
  closeOnConfirm?: boolean;
}

const btnGhost = 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5';
const btnDanger = 'inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50';

export function ConfirmDialog({
  state,
  onCancel,
}: {
  state: ConfirmState | null;
  onCancel: () => void;
}) {
  const [localBusy, setLocalBusy] = useState(false);
  const [localError, setLocalError] = useState('');
  const busy = Boolean(state?.busy || localBusy);
  const error = state?.error || localError;

  useEffect(() => {
    setLocalBusy(false);
    setLocalError('');
  }, [state?.message]);

  const confirm = async () => {
    if (!state || busy) return;
    setLocalBusy(true);
    setLocalError('');
    try {
      await state.onConfirm();
      if (state.closeOnConfirm !== false) onCancel();
    } catch (reason) {
      setLocalError(reason instanceof Error ? reason.message : 'The action could not be completed.');
    } finally {
      setLocalBusy(false);
    }
  };

  return (
    <Modal
      isOpen={!!state}
      title="Confirm"
      onClose={() => { if (!busy) onCancel(); }}
      closeDisabled={busy}
      size="sm"
      footer={
        <>
          <button type="button" className={btnGhost} onClick={onCancel} disabled={busy}>
            Cancel
          </button>
          <button
            type="button"
            className={btnDanger}
            onClick={() => void confirm()}
            disabled={busy}
          >
            {busy ? 'Working…' : state?.confirmLabel ?? 'Confirm'}
          </button>
        </>
      }
    >
      <p className="text-sm text-slate-700 dark:text-slate-300">{state?.message}</p>
      {error && <p role="alert" aria-live="assertive" className="mt-3 text-sm text-rose-700 dark:text-rose-300">{error}</p>}
    </Modal>
  );
}
