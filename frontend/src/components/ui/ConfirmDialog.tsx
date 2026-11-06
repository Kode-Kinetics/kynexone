'use client';

import { Modal } from '@/src/components/Modal';
import { useT } from '@/src/hooks/useT';
import { confirmMissing, type ConfirmTier } from '@/src/lib/confirmTier';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';

export interface ConfirmState {
  message: ReactNode;
  /** 'danger' (default) for destructive confirmations, 'primary' for ordinary ones. */
  tone?: 'danger' | 'primary';
  /** Receives the reason when the dialog asked for one; existing zero-argument handlers are unaffected. */
  onConfirm: (input: { reason: string }) => void | Promise<void>;
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
  /** 2 (default): focus starts on Cancel. 3: also an impact preview, a typed confirmation and a required reason. */
  tier?: ConfirmTier;
  /** Tier 3: what will change, shown above the confirmation. */
  impact?: ReactNode;
  /** Tier 3: the exact name or number to type back. */
  typeToConfirm?: string;
  /** Ask for a reason (always asked for tier 3; this adds it to tier 2, replacing a native prompt()). */
  /** `maxLength` is the server's own limit for this reason, when it has one; there is no default cap. */
  reason?: { label?: string; required?: boolean; placeholder?: string; minLength?: number; maxLength?: number };
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
  const [typed, setTyped] = useState('');
  const [reason, setReason] = useState('');
  const t = useT();
  const typedId = useId();
  const reasonId = useId();
  const cancelRef = useRef<HTMLButtonElement>(null);
  const confirmRef = useRef<HTMLButtonElement>(null);
  const busyRef = useRef<HTMLSpanElement>(null);
  const wasBusy = useRef(false);
  const busy = Boolean(state?.busy || localBusy);
  const error = state?.error || localError;

  useEffect(() => {
    setLocalBusy(false);
    setLocalError('');
    setTyped('');
    setReason('');
  }, [state?.message, state?.resetKey]);

  // Focus starts on Cancel, so Enter cannot confirm a destructive action by reflex. Modal focuses its close
  // button on the next frame; this runs after it.
  const isOpen = !!state;
  useEffect(() => {
    if (!isOpen) return;
    const frame = requestAnimationFrame(() => cancelRef.current?.focus());
    return () => cancelAnimationFrame(frame);
  }, [isOpen]);

  const tier: ConfirmTier = state?.tier ?? 2;
  const reasonRequired = tier === 3 || state?.reason?.required === true;
  const askReason = tier === 3 || !!state?.reason;
  const missing = confirmMissing({ tier, typeToConfirm: state?.typeToConfirm, reasonRequired, reasonMinLength: state?.reason?.minLength }, { typed, reason });

  const confirmClass = state?.tone === 'primary' ? 'btn-primary' : 'btn-danger';

  useEffect(() => {
    if (busy) busyRef.current?.focus();
    else if (wasBusy.current && error) confirmRef.current?.focus();
    wasBusy.current = busy;
  }, [busy, error]);

  const confirm = async () => {
    if (!state || busy || missing.length > 0) return;
    setLocalBusy(true);
    setLocalError('');
    try {
      await state.onConfirm({ reason: reason.trim() });
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
          <button type="button" ref={cancelRef} className="btn-secondary" onClick={onCancel} disabled={busy}>
            {state?.cancelLabel ?? 'Cancel'}
          </button>
          <button
            type="button"
            ref={confirmRef}
            className={confirmClass}
            onClick={() => void confirm()}
            disabled={busy || missing.length > 0}
            aria-describedby={missing.length > 0 ? `${typedId}-needs` : undefined}
          >
            {busy ? state?.busyLabel ?? 'Working…' : state?.confirmLabel ?? state?.fallbackConfirmLabel ?? 'Confirm'}
          </button>
        </>
      }
    >
      <div className="text-body text-fg-muted">{state?.message}</div>
      {tier === 3 && state?.impact && (
        <div className="mt-3 rounded-lg border border-line bg-surface-sunken p-3 text-body" data-testid="confirm-impact">
          <p className="text-meta font-semibold uppercase tracking-wide text-fg-subtle">{t('What will change')}</p>
          <div className="mt-1 text-fg">{state.impact}</div>
        </div>
      )}
      {askReason && (
        <div className="mt-3">
          <label htmlFor={reasonId} className="block text-body font-medium text-fg">
            {state?.reason?.label ?? t('Reason')} <span className="font-normal text-fg-muted">{reasonRequired ? t('(required)') : t('(optional)')}</span>
          </label>
          <textarea id={reasonId} className="textarea mt-1.5 w-full" rows={3} maxLength={state?.reason?.maxLength} value={reason} disabled={busy}
            placeholder={state?.reason?.placeholder} onChange={(e) => setReason(e.target.value)} data-testid="confirm-reason" />
        </div>
      )}
      {tier === 3 && state?.typeToConfirm !== undefined && (
        <div className="mt-3">
          <label htmlFor={typedId} className="block text-body font-medium text-fg">
            {t('Type {text} to confirm', { text: state.typeToConfirm })}
          </label>
          <input id={typedId} className="input mt-1.5 w-full" dir="auto" autoComplete="off" value={typed} disabled={busy}
            onChange={(e) => setTyped(e.target.value)} data-testid="confirm-typed" />
        </div>
      )}
      {missing.length > 0 && (
        <p id={`${typedId}-needs`} className="mt-2 text-meta text-fg-muted" data-testid="confirm-needs">
          {missing.includes('typed') ? t('Type {text} exactly to continue.', { text: state?.typeToConfirm ?? '' }) : (state?.reason?.minLength ?? 1) > 1 ? t('Give a reason of at least {count} characters to continue.', { count: state?.reason?.minLength }) : t('Give a reason to continue.')}
        </p>
      )}
      {busy && <span ref={busyRef} tabIndex={-1} role="status" aria-live="polite" className="sr-only">{state?.busyLabel ?? 'Working…'}</span>}
      {error && <p role="alert" aria-live="assertive" className="mt-3 text-body text-danger-fg">{error}</p>}
    </Modal>
  );
}
