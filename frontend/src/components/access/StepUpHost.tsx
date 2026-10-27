'use client';

import { useCallback, useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { Loader2, ShieldCheck } from 'lucide-react';
import { Modal } from '@/src/components/Modal';
import client from '@/src/api/client';
import { registerStepUpHandler, setStepUpToken } from '@/src/api/stepUp';
import { useLocale } from '@/src/contexts/LocaleContext';

/**
 * The "Confirm it's you" dialog, mounted once for the signed-in app. The API client opens it when an action against an
 * Admin or the workspace owner is refused with 403 step_up_required; on success the request is repeated (src/api/stepUp.ts).
 * POST /api/auth/step-up { password, totpCode? } → { stepUpToken, expiresInSeconds }; 400 ERR_STEP_UP_CODE_REQUIRED asks
 * for the two-step code, 400 ERR_STEP_UP_WRONG and 423 ERR_STEP_UP_LOCKED are said in plain words.
 */
export function StepUpHost() {
  const { t } = useLocale();
  const id = useId();
  const [open, setOpen] = useState(false);
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [needsCode, setNeedsCode] = useState(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const resolver = useRef<((confirmed: boolean) => void) | null>(null);
  const passwordRef = useRef<HTMLInputElement>(null);

  const finish = useCallback((confirmed: boolean) => {
    resolver.current?.(confirmed);
    resolver.current = null;
    setOpen(false);
    setPassword(''); setCode(''); setNeedsCode(false); setError(''); setBusy(false);
  }, []);

  useEffect(() => registerStepUpHandler(() => new Promise<boolean>((resolve) => {
    resolver.current = resolve;
    setOpen(true);
  })), []);

  useEffect(() => {
    if (!open) return;
    const frame = requestAnimationFrame(() => passwordRef.current?.focus());
    return () => cancelAnimationFrame(frame);
  }, [open]);

  const submit = async (e?: FormEvent) => {
    e?.preventDefault();
    if (busy || !password) return;
    setBusy(true); setError('');
    try {
      const { data } = await client.post<{ stepUpToken: string; expiresInSeconds: number }>(
        '/api/auth/step-up', { password, totpCode: needsCode ? code.trim() : undefined });
      setStepUpToken(data.stepUpToken, data.expiresInSeconds);
      finish(true);
    } catch (err) {
      const response = (err as { response?: { status?: number; data?: { code?: string } } }).response;
      if (!response) setError(t('Cannot reach the server. Check your connection and try again.'));
      else if (response.data?.code === 'ERR_STEP_UP_CODE_REQUIRED') { setNeedsCode(true); setError(''); }
      else if (response.data?.code === 'ERR_STEP_UP_WRONG') setError(needsCode
        ? t("That didn't match. Check your password and the newest code from your authenticator app.")
        : t("That password didn't match. Try again."));
      else if (response.status === 423) setError(t('Your account is locked after too many wrong attempts. Try again later.'));
      else setError(t('Could not confirm it is you. Try again in a moment.'));
      setBusy(false);
    }
  };

  if (!open) return null;
  return (
    <Modal
      isOpen
      title={t("Confirm it's you")}
      onClose={() => { if (!busy) finish(false); }}
      closeDisabled={busy}
      closeLabel={t('Close')}
      size="sm"
      footer={
        <>
          <button type="button" className="btn-secondary" onClick={() => finish(false)} disabled={busy} data-testid="step-up-cancel">{t('Cancel')}</button>
          <button type="button" className="btn-primary" onClick={() => void submit()} disabled={busy || !password || (needsCode && code.trim().length < 6)}
            aria-busy={busy} data-testid="step-up-confirm">
            {busy && <Loader2 className="h-4 w-4 animate-spin" aria-hidden />}
            {t('Confirm')}
          </button>
        </>
      }
    >
      <form onSubmit={(e) => void submit(e)} noValidate className="space-y-4" data-testid="step-up-dialog">
        <p className="flex gap-2 text-sm leading-6 text-slate-700 dark:text-slate-300">
          <ShieldCheck className="mt-0.5 h-4 w-4 shrink-0 text-violet-600" aria-hidden />
          {t('This changes an administrator account. Enter your password to continue.')}
        </p>
        <div className="space-y-1.5">
          <label htmlFor={`${id}-password`} className="block text-sm font-semibold text-slate-800 dark:text-slate-200">{t('Your password')}</label>
          <input id={`${id}-password`} ref={passwordRef} type="password" dir="ltr" autoComplete="current-password" value={password}
            onChange={(e) => { setPassword(e.target.value); setError(''); }} disabled={busy}
            className="block w-full rounded-xl border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-violet-500 focus:outline-none focus-visible:ring-2 focus-visible:ring-violet-500/40 dark:border-white/[0.16] dark:bg-white/[0.04] dark:text-white"
            data-testid="step-up-password" />
        </div>
        {needsCode && (
          <div className="space-y-1.5">
            <label htmlFor={`${id}-code`} className="block text-sm font-semibold text-slate-800 dark:text-slate-200">{t('6-digit code')}</label>
            <input id={`${id}-code`} inputMode="numeric" autoComplete="one-time-code" dir="ltr" maxLength={8} value={code}
              onChange={(e) => { setCode(e.target.value); setError(''); }} disabled={busy}
              className="block w-full rounded-xl border border-slate-300 bg-white px-3 py-2 font-mono text-sm tracking-widest text-slate-900 focus:border-violet-500 focus:outline-none focus-visible:ring-2 focus-visible:ring-violet-500/40 dark:border-white/[0.16] dark:bg-white/[0.04] dark:text-white"
              data-testid="step-up-code" />
            <p className="text-xs text-slate-500 dark:text-slate-400">{t('From your authenticator app.')}</p>
          </div>
        )}
        {error && <p role="alert" className="rounded-xl bg-rose-50 p-3 text-sm font-medium text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300" data-testid="step-up-error">{error}</p>}
      </form>
    </Modal>
  );
}
