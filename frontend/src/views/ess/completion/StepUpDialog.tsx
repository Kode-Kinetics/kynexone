'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { Loader2 } from 'lucide-react';
import { Modal } from '../../../components/Modal';
import { essCompletionApi, problemOf } from '../../../api/essCompletion';
import { useLocale } from '../../../contexts/LocaleContext';
import { essInput, essPrimaryButton } from '../../../components/ess/EssParts';

/**
 * Re-enter the password (and the authenticator code, when the account uses one) before an identity or bank detail is sent.
 * Focus moves into the dialog and returns to the button that opened it (Modal); the error is announced.
 */
export function StepUpDialog({ open, onClose, onVerified }: { open: boolean; onClose: () => void; onVerified: (token: string) => void }) {
  const { t } = useLocale();
  const passwordId = useId(); const codeId = useId(); const errorId = useId();
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [needsCode, setNeedsCode] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const passwordRef = useRef<HTMLInputElement>(null);
  useEffect(() => { if (open) { setPassword(''); setCode(''); setError(null); setNeedsCode(false); requestAnimationFrame(() => passwordRef.current?.focus()); } }, [open]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy(true); setError(null);
    try {
      const result = await essCompletionApi.stepUp(password, code);
      setPassword(''); setCode('');
      onVerified(result.stepUpToken);
    } catch (e) {
      const problem = problemOf(e);
      if (problem.code === 'mfa_required') setNeedsCode(true);
      setError(t(`ess.completion.error.${problem.code ?? 'invalid_credentials'}`));
    } finally { setBusy(false); }
  };

  return (
    <Modal isOpen={open} onClose={onClose} title={t('Confirm it is you')} size="sm" closeLabel={t('Close')} closeDisabled={busy}>
      <form onSubmit={submit} className="space-y-4" noValidate>
        <p className="text-sm text-slate-600 dark:text-slate-300">{t('Enter your password to continue. This protects your identity number and bank details.')}</p>
        <label htmlFor={passwordId} className="block space-y-1">
          <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Password')} <span className="font-normal text-slate-500">{t('(required)')}</span></span>
          <input id={passwordId} ref={passwordRef} type="password" autoComplete="current-password" dir="ltr" required className={essInput}
            value={password} onChange={e => setPassword(e.target.value)} aria-invalid={error ? true : undefined} aria-describedby={error ? errorId : undefined} />
        </label>
        {needsCode && (
          <label htmlFor={codeId} className="block space-y-1">
            <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Authenticator code')} <span className="font-normal text-slate-500">{t('(required)')}</span></span>
            <input id={codeId} inputMode="numeric" autoComplete="one-time-code" dir="ltr" maxLength={12} className={essInput}
              value={code} onChange={e => setCode(e.target.value)} aria-invalid={error ? true : undefined} aria-describedby={error ? errorId : undefined} />
          </label>
        )}
        <div id={errorId} role="alert" aria-live="assertive" className="min-h-[1rem] text-xs font-medium text-rose-700 dark:text-rose-300">{error}</div>
        <div className="flex flex-wrap justify-end gap-2">
          <button type="button" onClick={onClose} disabled={busy} className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-700 dark:border-white/10 dark:text-slate-200">{t('Cancel')}</button>
          <button type="submit" disabled={busy || !password} className={essPrimaryButton}>
            {busy && <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" />}{busy ? t('Checking…') : t('Confirm')}
          </button>
        </div>
      </form>
    </Modal>
  );
}
