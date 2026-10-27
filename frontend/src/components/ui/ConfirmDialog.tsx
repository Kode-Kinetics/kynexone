'use client';

import { Modal } from '@/src/components/Modal';
import { useEffect, useRef, useState, type ReactNode } from 'react';

export interface ConfirmState {
  message: ReactNode;
  /** 'danger' (default) for destructive confirmations, 'primary' for ordinary ones. */
  tone?: 'danger' | 'primary';
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

  const confirmClass = state?.tone === 'primary' ? 'btn-primary' : 'btn-danger';

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
          <button type="button" className="btn-secondary" onClick={onCancel} disabled={busy}>
            {state?.cancelLabel ?? 'Cancel'}
          </button>
          <button
            type="button"
            ref={confirmRef}
            className={confirmClass}
            onClick={() => void confirm()}
            disabled={busy}
          >
            {busy ? state?.busyLabel ?? 'Working…' : state?.confirmLabel ?? state?.fallbackConfirmLabel ?? 'Confirm'}
          </button>
        </>
      }
    >
      <div className="text-body text-fg-muted">{state?.message}</div>
      {busy && <span ref={busyRef} tabIndex={-1} role="status" aria-live="polite" className="sr-only">{state?.busyLabel ?? 'Working…'}</span>}
      {error && <p role="alert" aria-live="assertive" className="mt-3 text-body text-danger-fg">{error}</p>}
    </Modal>
  );
}
