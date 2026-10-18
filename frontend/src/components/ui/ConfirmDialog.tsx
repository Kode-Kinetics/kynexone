'use client';

import { Modal } from '@/src/components/Modal';
import { useEffect, useRef, useState } from 'react';

export interface ConfirmState {
  message: string;
  onConfirm: () => void | Promise<void>;
  confirmLabel?: string;
  title?: string;
  cancelLabel?: string;
  busyLabel?: string;
  fallbackConfirmLabel?: string;
  fallbackError?: string;
  closeLabel?: string;
  busy?: boolean;
  error?: string;
  closeOnConfirm?: boolean;
  resetKey?: string | number;
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
  const confirmRef = useRef<HTMLButtonElement>(null);
  const busyRef = useRef<HTMLSpanElement>(null);
  const wasBusy = useRef(false);
  const busy = Boolean(state?.busy || localBusy);
  const error = state?.error || localError;

  useEffect(() => {
    setLocalBusy(false);
    setLocalError('');
  }, [state?.message, state?.resetKey]);

  useEffect(() => {
    if (busy) busyRef.current?.focus();
    else if (wasBusy.current && error) confirmRef.current?.focus();
    wasBusy.current = busy;
  }, [busy, error]);

  const confirm = async () => {
    if (!state || busy) return;
    setLocalBusy(true);
    setLocalError('');
    try {
      await state.onConfirm();
      if (state.closeOnConfirm !== false) onCancel();
    } catch (reason) {
      setLocalError(reason instanceof Error && reason.message ? reason.message : state.fallbackError ?? 'The action could not be completed.');
    } finally {
      setLocalBusy(false);
    }
  };

  return (
    <Modal
      isOpen={!!state}
      title={state?.title ?? 'Confirm'}
      onClose={() => { if (!busy) onCancel(); }}
      closeDisabled={busy}
      closeLabel={state?.closeLabel}
      size="sm"
      footer={
        <>
          <button type="button" className={btnGhost} onClick={onCancel} disabled={busy}>
            {state?.cancelLabel ?? 'Cancel'}
          </button>
          <button
            type="button"
            ref={confirmRef}
            className={btnDanger}
            onClick={() => void confirm()}
            disabled={busy}
          >
            {busy ? state?.busyLabel ?? 'Working…' : state?.confirmLabel ?? state?.fallbackConfirmLabel ?? 'Confirm'}
          </button>
        </>
      }
    >
      <p className="text-sm text-slate-700 dark:text-slate-300">{state?.message}</p>
      {busy && <span ref={busyRef} tabIndex={-1} role="status" aria-live="polite" className="sr-only">{state?.busyLabel ?? 'Working…'}</span>}
      {error && <p role="alert" aria-live="assertive" className="mt-3 text-sm text-rose-700 dark:text-rose-300">{error}</p>}
    </Modal>
  );
}
