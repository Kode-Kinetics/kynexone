'use client';

import { useId, useState, type FormEvent, type ReactNode } from 'react';
import { Eye, EyeOff, KeyRound, Loader2, Mail, RefreshCw } from 'lucide-react';
import { Modal } from '@/src/components/Modal';
import { usersApi, type PasswordResetLinkResult } from '@/src/api/identity';
import { localizedRefusal } from '@/src/lib/accessCeiling';
import { useLocale } from '@/src/contexts/LocaleContext';

/**
 * "Reset password" for another user (User Management → Users). Two ways, chosen in the dialog:
 *
 * - Email a reset link (the default): POST /api/access/users/{id}/password-reset-link. The person chooses their
 *   own password. With no mail transport the link comes back here, once, to be passed on by hand.
 * - Set a temporary password: POST /api/access/users/{id}/admin-reset-password { newPassword, reason }. For
 *   someone who cannot get the email. The administrator knows it, so the server only lets it be spent on choosing
 *   a new one at the next sign-in; every session ends and the person is notified. A reason is required (audited).
 *
 * The server is the gate for both (security.manage, the privilege ceiling, never yourself).
 */

export const TEMP_PASSWORD_REASON_MIN = 3;
export const TEMP_PASSWORD_REASON_MAX = 500;
const TEMP_PASSWORD_LENGTH = 16;

type Method = 'link' | 'temporary';

/** A random password that satisfies every workspace rule (upper, lower, digit, symbol), without look-alikes. */
export function generateTemporaryPassword(length = TEMP_PASSWORD_LENGTH): string {
  const sets = ['ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789', '!@#$%*-_+?'];
  const all = sets.join('');
  // Uniform in [0, n): rejection sampling, so no modulo bias.
  const randomInt = (n: number) => {
    const limit = 0x1_0000_0000 - (0x1_0000_0000 % n);
    const word = new Uint32Array(1);
    do crypto.getRandomValues(word); while (word[0] >= limit);
    return word[0] % n;
  };
  const pick = (alphabet: string) => alphabet[randomInt(alphabet.length)];
  // One of each class, the rest from all of them, then a Fisher–Yates shuffle so the classes are not in order.
  const chars = [...sets.map(pick), ...Array.from({ length: length - sets.length }, () => pick(all))];
  for (let i = chars.length - 1; i > 0; i--) {
    const j = randomInt(i + 1);
    [chars[i], chars[j]] = [chars[j], chars[i]];
  }
  return chars.join('');
}

function messageOf(err: unknown): string | undefined {
  const message = (err as { response?: { data?: { message?: unknown } } } | null)?.response?.data?.message;
  return typeof message === 'string' && message.trim() ? message : undefined;
}

export function ResetPasswordDialog({ user, onClose, onChanged }: {
  user: { id: string; fullName: string; email: string };
  onClose: () => void;
  /** Something changed on the server (a link was issued or a password set): the caller reloads the list. */
  onChanged: () => void;
}) {
  const { t, locale } = useLocale();
  const id = useId();
  const name = user.fullName?.trim() || user.email;
  const [method, setMethod] = useState<Method>('link');
  const [password, setPassword] = useState(() => generateTemporaryPassword());
  const [showPassword, setShowPassword] = useState(true);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [linkResult, setLinkResult] = useState<PasswordResetLinkResult | null>(null);
  const [passwordSet, setPasswordSet] = useState(false);
  const [copied, setCopied] = useState(false);
  const done = linkResult !== null || passwordSet;

  const copy = (value: string) => {
    navigator.clipboard?.writeText(value).then(() => setCopied(true)).catch(() => setCopied(false));
  };

  const refusal = (err: unknown, fallback: string) => {
    if (!(err as { response?: unknown } | null)?.response) return t('Cannot reach the server. Check your connection and try again.');
    if ((err as { response?: { status?: number } }).response?.status === 403)
      return localizedRefusal(err, locale) ?? t("You don't have permission to reset the password for {name}.", { name });
    return messageOf(err) ?? fallback;
  };

  const submit = async (e?: FormEvent) => {
    e?.preventDefault();
    if (busy || done) return;
    setError('');
    if (method === 'temporary') {
      const trimmed = reason.trim();
      if (trimmed.length < TEMP_PASSWORD_REASON_MIN || trimmed.length > TEMP_PASSWORD_REASON_MAX) {
        setError(t('Enter a reason of 3 to 500 characters.'));
        return;
      }
      if (password.length < 10) { setError(t('Use at least 10 characters.')); return; }
    }
    setBusy(true);
    try {
      if (method === 'link') setLinkResult(await usersApi.issuePasswordResetLink(user.id));
      else {
        await usersApi.setTemporaryPassword(user.id, password, reason.trim());
        setPasswordSet(true);
      }
      setCopied(false);
      onChanged();
    } catch (err) {
      setError(refusal(err, method === 'link'
        ? t('The reset link could not be sent. Try again in a moment.')
        : t('The temporary password could not be set. Try again in a moment.')));
    } finally {
      setBusy(false);
    }
  };

  const reasonId = `${id}-reason`;
  const passwordId = `${id}-password`;
  const choice = (value: Method, icon: ReactNode, title: string, body: string) => (
    <label className={`flex cursor-pointer gap-3 rounded-xl border p-3 text-sm ${method === value
      ? 'border-violet-500 bg-violet-50 dark:border-violet-400 dark:bg-violet-500/10'
      : 'border-slate-200 hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800/60'}`}>
      <input type="radio" name={`${id}-method`} value={value} checked={method === value}
        onChange={() => { setMethod(value); setError(''); }} className="mt-1" data-testid={`reset-password-method-${value}`} />
      <span className="flex-1">
        <span className="flex items-center gap-1.5 font-semibold text-slate-800 dark:text-slate-100">{icon}{title}</span>
        <span className="mt-0.5 block text-xs leading-5 text-slate-600 dark:text-slate-400">{body}</span>
      </span>
    </label>
  );

  return (
    <Modal
      isOpen
      title={t('Reset password for {name}', { name })}
      onClose={() => { if (!busy) onClose(); }}
      closeDisabled={busy}
      closeLabel={t('Close')}
      size="md"
      footer={
        <>
          <button type="button" className="btn-secondary" onClick={onClose} disabled={busy} data-testid="reset-password-close">
            {done ? t('Done') : t('Cancel')}
          </button>
          {!done && (
            <button type="button" className="btn-primary" onClick={() => void submit()} disabled={busy} aria-busy={busy} data-testid="reset-password-confirm">
              {busy && <Loader2 className="h-4 w-4 animate-spin" aria-hidden />}
              {method === 'link' ? t('Send reset link') : t('Set temporary password')}
            </button>
          )}
        </>
      }
    >
      <form onSubmit={(e) => void submit(e)} noValidate className="space-y-4" data-testid="reset-password-dialog">
        {!done && (
          <fieldset className="space-y-2">
            <legend className="sr-only">{t('How should {name} get a new password?', { name })}</legend>
            {choice('link', <Mail className="h-4 w-4" aria-hidden />, t('Email a reset link'),
              t('Recommended. {name} chooses their own password. The link works once and expires in 1 hour.', { name }))}
            {choice('temporary', <KeyRound className="h-4 w-4" aria-hidden />, t('Set a temporary password'),
              t('For someone who cannot get the email. Give it to them in person. They must choose their own password when they next sign in, and they are signed out everywhere now.'))}
          </fieldset>
        )}

        {!done && method === 'temporary' && (
          <div className="space-y-3">
            <div className="space-y-1.5">
              <label htmlFor={passwordId} className="block text-sm font-semibold text-slate-800 dark:text-slate-200">{t('Temporary password')}</label>
              <div className="flex gap-2">
                <input id={passwordId} dir="ltr" type={showPassword ? 'text' : 'password'} value={password} autoComplete="off" spellCheck={false}
                  onChange={(e) => { setPassword(e.target.value); setError(''); }} disabled={busy}
                  className="block w-full rounded-xl border border-slate-300 bg-white px-3 py-2 font-mono text-sm text-slate-900 focus:border-violet-500 focus:outline-none focus-visible:ring-2 focus-visible:ring-violet-500/40 dark:border-white/[0.16] dark:bg-white/[0.04] dark:text-white"
                  data-testid="reset-password-temporary" />
                <button type="button" onClick={() => setShowPassword(v => !v)} className="btn-secondary px-2"
                  aria-label={showPassword ? t('Hide password') : t('Show password')} aria-pressed={showPassword}>
                  {showPassword ? <EyeOff className="h-4 w-4" aria-hidden /> : <Eye className="h-4 w-4" aria-hidden />}
                </button>
                <button type="button" onClick={() => setPassword(generateTemporaryPassword())} className="btn-secondary whitespace-nowrap px-2" disabled={busy}>
                  <RefreshCw className="h-4 w-4" aria-hidden /> {t('Generate')}
                </button>
              </div>
              <p className="text-xs text-slate-500 dark:text-slate-400">{t('A strong one is generated for you. Your company\'s password rules apply if you type your own.')}</p>
            </div>
            <div className="space-y-1.5">
              <label htmlFor={reasonId} className="block text-sm font-semibold text-slate-800 dark:text-slate-200">
                {t('Reason')} <span aria-hidden className="text-rose-600 dark:text-rose-400">*</span>
              </label>
              <textarea id={reasonId} rows={2} value={reason} maxLength={TEMP_PASSWORD_REASON_MAX} required aria-required="true"
                placeholder={t('For example, locked out and confirmed in person. It is kept in the audit log.')}
                onChange={(e) => { setReason(e.target.value); setError(''); }} disabled={busy}
                className="block w-full rounded-xl border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-violet-500 focus:outline-none focus-visible:ring-2 focus-visible:ring-violet-500/40 dark:border-white/[0.16] dark:bg-white/[0.04] dark:text-white"
                data-testid="reset-password-reason" />
            </div>
          </div>
        )}

        {linkResult && (
          <div className="space-y-3" data-testid="reset-password-link-result">
            <p className={`text-sm ${linkResult.emailSent ? 'text-emerald-700 dark:text-emerald-400' : 'text-amber-700 dark:text-amber-400'}`}>{linkResult.message}</p>
            {linkResult.resetUrl && (
              <CopyOnce label={t('One-time reset link — copy it now, it is not shown again')} value={linkResult.resetUrl}
                copied={copied} onCopy={() => copy(linkResult.resetUrl!)} />
            )}
          </div>
        )}

        {passwordSet && (
          <div className="space-y-3" data-testid="reset-password-temporary-result">
            <p className="text-sm text-emerald-700 dark:text-emerald-400">
              {t('Temporary password set for {name}. They were signed out everywhere and must choose their own password when they next sign in.', { name })}
            </p>
            <CopyOnce label={t('Temporary password — give it to them in person, it is not shown again')} value={password}
              copied={copied} onCopy={() => copy(password)} />
          </div>
        )}

        {error && <p role="alert" className="rounded-xl bg-rose-50 p-3 text-sm font-medium text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300" data-testid="reset-password-error">{error}</p>}
      </form>
    </Modal>
  );
}

function CopyOnce({ label, value, copied, onCopy }: { label: string; value: string; copied: boolean; onCopy: () => void }) {
  const { t } = useLocale();
  return (
    <div>
      <p className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{label}</p>
      <div className="flex gap-2">
        <input readOnly dir="ltr" value={value} onFocus={e => e.currentTarget.select()} aria-label={label}
          className="block w-full rounded-xl border border-slate-300 bg-slate-50 px-3 py-2 font-mono text-xs text-slate-900 dark:border-white/[0.16] dark:bg-white/[0.04] dark:text-white" />
        <button type="button" onClick={onCopy} className="btn-secondary shrink-0 px-3">{copied ? t('Copied') : t('Copy')}</button>
      </div>
    </div>
  );
}
