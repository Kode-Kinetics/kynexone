'use client';

import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { Loader2 } from 'lucide-react';
import { Modal } from '@/src/components/Modal';
import { usersApi } from '@/src/api/identity';
import { MFA_ERRORS } from '@/src/api/auth';
import { localizedRefusal } from '@/src/lib/accessCeiling';
import { useLocale } from '@/src/contexts/LocaleContext';

/**
 * "Reset two-step sign-in" for another user (User Management → Users). POST
 * /api/access/users/{id}/mfa/reset { reason } → 204: the person's factor, backup codes and trusted
 * browsers are cleared at once and every session of theirs ends. The server is the gate (the same
 * security.manage permission and privilege ceiling as the other user actions, and never yourself);
 * the list only offers it where it would be accepted.
 *
 * The shared Modal traps focus and closes on Escape (not while the request is in flight). The reason
 * is required, 3 to 500 characters, and recorded in the audit log.
 */

export const RESET_REASON_MIN = 3;
export const RESET_REASON_MAX = 500;

function statusOf(err: unknown): number | undefined {
  return (err as { response?: { status?: number } } | null)?.response?.status;
}
function codeOf(err: unknown): string | undefined {
  const data = (err as { response?: { data?: { code?: unknown; error?: unknown } } } | null)?.response?.data;
  const code = data?.code ?? data?.error;
  return typeof code === 'string' ? code : undefined;
}

export function ResetTwoStepDialog({ user, onClose, onReset, onStale }: {
  user: { id: string; fullName: string; email: string };
  onClose: () => void;
  /** 204: the caller says so (toast) and reloads the list. */
  onReset: () => void;
  /** The list was out of date (404 or 409): the caller reloads it. The dialog stays open to say why. */
  onStale: () => void;
}) {
  const { t, locale } = useLocale();
  const id = useId();
  const reasonRef = useRef<HTMLTextAreaElement>(null);
  const [reason, setReason] = useState('');
  const [fieldError, setFieldError] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  /** After a 404 / 409 there is nothing left to reset: only Close remains. */
  const [finished, setFinished] = useState(false);
  const name = user.fullName?.trim() || user.email;

  // The reason is the one thing to do here, so the cursor starts in it. A frame after the Modal's
  // own first focus (its close button), which runs first.
  useEffect(() => {
    const frame = requestAnimationFrame(() => reasonRef.current?.focus());
    return () => cancelAnimationFrame(frame);
  }, []);

  const reasonId = `${id}-reason`;
  const hintId = `${id}-reason-hint`;
  const errorId = `${id}-reason-error`;

  const submit = async (e?: FormEvent) => {
    e?.preventDefault();
    if (busy || finished) return;
    const trimmed = reason.trim();
    if (trimmed.length < RESET_REASON_MIN || trimmed.length > RESET_REASON_MAX) {
      setFieldError(t('Enter a reason of 3 to 500 characters.'));
      reasonRef.current?.focus();
      return;
    }
    setBusy(true); setError(''); setFieldError('');
    try {
      await usersApi.resetMfa(user.id, trimmed);
      onReset();
    } catch (err) {
      const s = statusOf(err);
      const c = codeOf(err);
      if (!(err as { response?: unknown } | null)?.response) setError(t('Cannot reach the server. Check your connection and try again.'));
      else if (c === MFA_ERRORS.resetReason) { setFieldError(t('Enter a reason of 3 to 500 characters.')); reasonRef.current?.focus(); }
      else if (c === MFA_ERRORS.resetSelf) setError(t("You can't reset your own two-step sign-in. Ask another administrator to do it."));
      else if (c === MFA_ERRORS.notEnabled) {
        setFinished(true);
        setError(t('Two-step sign-in is already off for {name}. Nothing was changed.', { name }));
        onStale();
      }
      else if (s === 404) {
        setFinished(true);
        setError(t("{name} is no longer one of your company's users. The list has been refreshed.", { name }));
        onStale();
      }
      else if (s === 403) {
        // A privilege-ceiling or separation-of-duties refusal is coded and carries its own sentence in
        // both languages (AccessController.Refused). A bare 403 ("Forbidden") gets the plain one.
        const data = (err as { response?: { data?: { messageAr?: unknown } } }).response?.data;
        const domainRefusal = !!c?.startsWith('access_') || typeof data?.messageAr === 'string';
        setError((domainRefusal ? localizedRefusal(err, locale) : null)
          ?? t("You don't have permission to reset two-step sign-in for {name}.", { name }));
      }
      else setError(t('Two-step sign-in could not be reset. Try again in a moment.'));
    } finally {
      setBusy(false);
    }
  };

  const describedBy = [hintId, fieldError ? errorId : ''].filter(Boolean).join(' ');

  return (
    <Modal
      isOpen
      title={t('Reset two-step sign-in?')}
      onClose={() => { if (!busy) onClose(); }}
      closeDisabled={busy}
      closeLabel={t('Close')}
      size="md"
      footer={
        <>
          <button type="button" className="btn-secondary" onClick={onClose} disabled={busy} data-testid="reset-two-step-cancel">
            {finished ? t('Close') : t('Cancel')}
          </button>
          {!finished && (
            <button type="button" className="btn-danger" onClick={() => void submit()} disabled={busy} aria-busy={busy} data-testid="reset-two-step-confirm">
              {busy && <Loader2 className="h-4 w-4 animate-spin" aria-hidden />}
              {busy ? t('Resetting…') : t('Reset two-step sign-in')}
            </button>
          )}
        </>
      }
    >
      <form onSubmit={(e) => void submit(e)} noValidate className="space-y-4" data-testid="reset-two-step-dialog">
        <p className="text-sm leading-6 text-slate-700 dark:text-slate-300" data-testid="reset-two-step-explainer">
          {t('{name} will be signed out everywhere and set up two-step sign-in again the next time they sign in. Their trusted browsers and backup codes stop working.', { name })}
        </p>
        {!finished && (
          <div className="space-y-1.5">
            <label htmlFor={reasonId} className="block text-sm font-semibold text-slate-800 dark:text-slate-200">
              {t('Reason')} <span aria-hidden className="text-rose-600 dark:text-rose-400">*</span>
            </label>
            <p id={hintId} className="text-xs leading-5 text-slate-500 rtl:text-body-sm dark:text-slate-400">
              {t('For example, a lost or replaced phone. It is kept in the audit log.')}
            </p>
            <textarea
              id={reasonId}
              ref={reasonRef}
              rows={3}
              value={reason}
              maxLength={RESET_REASON_MAX}
              required
              aria-required="true"
              aria-invalid={fieldError ? true : undefined}
              aria-describedby={describedBy}
              onChange={(e) => { setReason(e.target.value); setFieldError(''); }}
              disabled={busy}
              className="block w-full rounded-xl border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-blue-500 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500/40 aria-[invalid=true]:border-rose-500 dark:border-white/[0.16] dark:bg-white/[0.04] dark:text-white"
              data-testid="reset-two-step-reason"
            />
            {fieldError && <p id={errorId} role="alert" className="text-sm font-medium text-rose-700 dark:text-rose-300">{fieldError}</p>}
          </div>
        )}
        {error && <p role="alert" className="rounded-xl bg-rose-50 p-3 text-sm font-medium text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300" data-testid="reset-two-step-error">{error}</p>}
      </form>
    </Modal>
  );
}
