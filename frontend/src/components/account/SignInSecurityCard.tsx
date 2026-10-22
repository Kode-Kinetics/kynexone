'use client';

import { useCallback, useEffect, useId, useState, type FormEvent, type ReactNode } from 'react';
import { useRouter } from 'next/navigation';
import { CheckCircle2, Laptop, Loader2, RefreshCw, ShieldAlert, ShieldCheck } from 'lucide-react';
import { authApi, MFA_ERRORS, type MfaStatus, type TrustedDevice } from '@/src/api/auth';
import { useAuth } from '@/src/contexts/AuthContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import { ConfirmDialog, type ConfirmState } from '@/src/components/ui/ConfirmDialog';
import { setSignInNotice } from '@/src/lib/signInNotice';
import { toAsciiDigits } from '@/src/lib/welcomeCode';

/**
 * Sign-in security: the signed-in person's own two-step sign-in and the browsers they trust.
 *
 * Contract: scratchpad mfa-contract.md (GET /api/auth/mfa/status, POST setup / verify-setup / disable,
 * GET and DELETE /api/auth/mfa/trusted-devices). Every endpoint needs only a session, so this card
 * is for every role, and nothing on it depends on a permission.
 *
 * Turning two-step on or off ends every session of the account today (the server rotates the
 * session stamp, and disable revokes the refresh tokens). After either change the card asks the
 * server whether this session survived: if it did, the card refreshes in place; if not, the person
 * is taken to the sign-in page with one sentence saying why.
 */

type Panel = 'none' | 'setup' | 'disable';

const ROLE_REQUIRES =
  'Your role can see or change payroll, banking or access, so your company requires two-step sign-in.';
const WORKSPACE_REQUIRES = 'Your company requires two-step sign-in for everyone.';
type RequiredBy = 'role' | 'workspace';

const primaryBtn =
  'inline-flex min-h-[40px] items-center justify-center gap-2 rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-60 dark:bg-blue-500 dark:hover:bg-blue-400 dark:ring-offset-midnight';
const secondaryBtn =
  'inline-flex min-h-[40px] items-center justify-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-60 dark:border-white/[0.12] dark:bg-white/[0.04] dark:text-slate-200 dark:hover:bg-white/[0.08] dark:ring-offset-midnight';
const dangerBtn =
  'inline-flex min-h-[40px] items-center justify-center gap-2 rounded-xl bg-rose-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-rose-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-rose-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-60 dark:ring-offset-midnight';
const codeInput =
  'block w-full max-w-[14rem] rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-center font-mono text-lg tracking-[0.3em] text-slate-900 placeholder:text-slate-300 focus:border-blue-500 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500/40 dark:border-white/[0.16] dark:bg-white/[0.04] dark:text-white dark:placeholder:text-slate-600';

interface Notice { tone: 'ok' | 'error'; text: string }

function statusOf(err: unknown): number | undefined {
  return (err as { response?: { status?: number } } | null)?.response?.status;
}
function codeOf(err: unknown): string | undefined {
  const data = (err as { response?: { data?: { code?: unknown; error?: unknown } } } | null)?.response?.data;
  const code = data?.code ?? data?.error;
  return typeof code === 'string' ? code : undefined;
}
function isNetwork(err: unknown): boolean {
  return !!err && typeof err === 'object' && !(err as { response?: unknown }).response;
}

/** The base32 secret inside an otpauth:// URI, for people who cannot scan the QR. */
function secretFrom(uri: string): string {
  try { return new URL(uri).searchParams.get('secret') ?? ''; } catch { return ''; }
}

/** "JBSWY3DPEHPK3PXP" → "JBSW Y3DP EHPK 3PXP": easier to type into a phone. */
function groupKey(secret: string): string {
  return secret.replace(/(.{4})/g, '$1 ').trim();
}

/**
 * Whether this browser's session survived the change, asked without the API client: its 401
 * handling would sign out and navigate on its own, before this card can say anything.
 */
async function sessionStillValid(): Promise<boolean> {
  let token: string | null = null;
  try { token = localStorage.getItem('zayra_access_token'); } catch { /* storage unavailable */ }
  if (!token) return false;
  try {
    const res = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${token}` }, cache: 'no-store' });
    return res.ok;
  } catch {
    return false;
  }
}

export function SignInSecurityCard() {
  const { t } = useLocale();
  const { logout } = useAuth();
  const router = useRouter();

  const [status, setStatus] = useState<MfaStatus | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [panel, setPanel] = useState<Panel>('none');
  const [notice, setNotice] = useState<Notice | null>(null);
  /** Set by a 409 ERR_MFA_REQUIRED_FOR_ROLE / _BY_WORKSPACE even when the status said the user could turn it off. */
  const [lockedBy, setLockedBy] = useState<RequiredBy | null>(null);

  const load = useCallback(async () => {
    setLoadError(false);
    try {
      setStatus(await authApi.mfaStatus());
    } catch {
      setLoadError(true);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  /** After turning two-step on or off: carry on here if the session lived, otherwise sign in again. */
  const afterChange = useCallback(async (turnedOn: boolean) => {
    setPanel('none');
    if (await sessionStillValid()) {
      setNotice({ tone: 'ok', text: turnedOn
        ? t('Two-step sign-in is on. You were signed out everywhere else.')
        : t('Two-step sign-in is off. You were signed out everywhere else.') });
      await load();
      return;
    }
    setSignInNotice(turnedOn ? 'two-step-on' : 'two-step-off');
    await logout();
    router.replace('/login');
  }, [load, logout, router, t]);

  if (loadError) {
    return (
      <Card testId="sign-in-security-card">
        <div role="alert" className="flex flex-col items-start gap-3 rounded-xl border border-amber-200 bg-amber-50 p-4 dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <p className="text-sm font-medium text-amber-900 dark:text-amber-200">{t('Your sign-in settings could not be loaded.')}</p>
          <button type="button" onClick={() => void load()} className={secondaryBtn}>
            <RefreshCw className="h-4 w-4" aria-hidden /> {t('Try again')}
          </button>
        </div>
      </Card>
    );
  }

  if (!status) {
    return (
      <Card testId="sign-in-security-card">
        <div className="space-y-3" aria-busy="true">
          <div className="h-5 w-56 animate-pulse rounded bg-slate-100 dark:bg-white/[0.06]" />
          <div className="h-4 w-full max-w-md animate-pulse rounded bg-slate-100 dark:bg-white/[0.06]" />
          <span className="sr-only">{t('Loading…')}</span>
        </div>
      </Card>
    );
  }

  const on = status.enabled;
  const canDisable = !lockedBy && (status.canDisable ?? (status.enabled && !status.required));
  const requiredBy: RequiredBy | null = lockedBy
    ?? (status.required ? (status.requiredBecause === 'workspace_policy' ? 'workspace' : 'role') : null);
  const days = Number.isInteger(status.rememberDeviceDays) ? Math.max(0, status.rememberDeviceDays as number) : 0;

  return (
    <div className="space-y-5">
      <Card testId="sign-in-security-card">
        <div className="flex items-start gap-3">
          <span className={`mt-0.5 grid h-9 w-9 shrink-0 place-items-center rounded-xl ${on
            ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300'
            : 'bg-slate-100 text-slate-500 dark:bg-white/[0.06] dark:text-slate-400'}`} aria-hidden>
            {on ? <ShieldCheck className="h-5 w-5" /> : <ShieldAlert className="h-5 w-5" />}
          </span>
          <div className="min-w-0 flex-1">
            <h2 className="text-base font-semibold text-slate-900 dark:text-white" data-testid="two-step-status">
              {on ? t('Two-step sign-in is on') : t('Two-step sign-in is off')}
            </h2>
            <p className="mt-1 text-sm leading-6 text-slate-600 dark:text-slate-400">
              {on
                ? t('When you sign in, you enter your password and then a 6-digit code from the authenticator app on your phone.')
                : t('You sign in with your password only. Turn on two-step sign-in to also ask for a 6-digit code from an authenticator app on your phone, so a stolen password is not enough.')}
            </p>
          </div>
        </div>

        {notice && <InlineNotice notice={notice} />}

        {requiredBy && (!on || !canDisable) && (
          <p className="mt-4 rounded-xl border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-500/20 dark:bg-amber-500/[0.06] dark:text-amber-200" data-testid="two-step-required-reason">
            {requiredBy === 'workspace' ? t(WORKSPACE_REQUIRES) : t(ROLE_REQUIRES)}
          </p>
        )}

        {panel === 'none' && (
          <div className="mt-4 flex flex-wrap gap-3">
            {!on && (
              <button type="button" className={primaryBtn} onClick={() => { setNotice(null); setPanel('setup'); }} data-testid="two-step-turn-on">
                {t('Turn on two-step sign-in')}
              </button>
            )}
            {on && canDisable && (
              <button type="button" className={secondaryBtn} onClick={() => { setNotice(null); setPanel('disable'); }} data-testid="two-step-turn-off">
                {t('Turn off two-step sign-in')}
              </button>
            )}
          </div>
        )}

        {panel === 'setup' && (
          <SetupPanel
            onCancel={() => setPanel('none')}
            onDone={() => void afterChange(true)}
            onAlreadyOn={() => { setPanel('none'); setNotice({ tone: 'ok', text: t('Two-step sign-in is already on for your account.') }); void load(); }}
          />
        )}
        {panel === 'disable' && (
          <DisablePanel
            onCancel={() => setPanel('none')}
            onDone={() => void afterChange(false)}
            onRequired={(by) => { setLockedBy(by); setPanel('none'); }}
            onAlreadyOff={() => { setPanel('none'); setNotice({ tone: 'ok', text: t('Two-step sign-in is already off for your account.') }); void load(); }}
          />
        )}
      </Card>

      {(on || (status.trustedDeviceCount ?? 0) > 0) && <TrustedBrowsers days={days} />}
    </div>
  );
}

/** The step's number. The <ol> already gives a screen reader the order, so this is for the eye only. */
function StepNumber({ n }: { n: number }) {
  return (
    <span aria-hidden className="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded-full bg-blue-50 text-xs font-bold text-blue-700 dark:bg-blue-500/15 dark:text-blue-300">
      {n}
    </span>
  );
}

function Card({ children, testId }: { children: ReactNode; testId?: string }) {
  return (
    <section data-testid={testId} className="wg-card rounded-2xl p-5 sm:p-6">
      {children}
    </section>
  );
}

function InlineNotice({ notice }: { notice: Notice }) {
  return (
    <p role={notice.tone === 'error' ? 'alert' : 'status'}
      className={`mt-4 flex items-start gap-2 rounded-xl p-3 text-sm font-medium ${notice.tone === 'ok'
        ? 'bg-emerald-50 text-emerald-800 dark:bg-emerald-500/[0.08] dark:text-emerald-300'
        : 'bg-rose-50 text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300'}`}>
      {notice.tone === 'ok' && <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />}
      <span>{notice.text}</span>
    </p>
  );
}

/** The 6-digit code field: digits only (Arabic-Indic digits accepted), read by password managers and phones as a one-time code. */
function CodeField({ id, label, hint, value, onChange, error }: {
  id: string; label: string; hint?: string; value: string; onChange: (v: string) => void; error?: string;
}) {
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  const describedBy = [hint ? hintId : '', error ? errorId : ''].filter(Boolean).join(' ') || undefined;
  return (
    <div className="space-y-1.5">
      <label htmlFor={id} className="block text-sm font-semibold text-slate-800 dark:text-slate-200">{label}</label>
      {hint && <p id={hintId} className="text-xs text-slate-500 dark:text-slate-400">{hint}</p>}
      <input
        id={id} type="text" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6}
        dir="ltr" placeholder="000000" value={value}
        onChange={(e) => onChange(toAsciiDigits(e.target.value).replace(/\D/g, '').slice(0, 6))}
        aria-invalid={error ? true : undefined} aria-describedby={describedBy}
        className={codeInput}
      />
      {error && <p id={errorId} role="alert" className="text-sm font-medium text-rose-700 dark:text-rose-300">{error}</p>}
    </div>
  );
}

function SetupPanel({ onCancel, onDone, onAlreadyOn }: { onCancel: () => void; onDone: () => void; onAlreadyOn: () => void }) {
  const { t } = useLocale();
  const id = useId();
  const [uri, setUri] = useState('');
  const [qr, setQr] = useState('');
  const [qrFailed, setQrFailed] = useState(false);
  const [startError, setStartError] = useState('');
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  /** The setup can no longer be completed: only "Start again" helps. */
  const [stale, setStale] = useState(false);
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState<'' | 'ok' | 'failed'>('');
  const [attempt, setAttempt] = useState(0);
  const secret = secretFrom(uri);

  useEffect(() => {
    let cancelled = false;
    setUri(''); setQr(''); setQrFailed(false); setStartError(''); setCode(''); setError(''); setStale(false); setCopied('');
    authApi.mfaSetup()
      .then((res) => { if (!cancelled) setUri(res.provisioningUri); })
      .catch((err) => {
        if (cancelled) return;
        if (statusOf(err) === 409) { onAlreadyOn(); return; }
        setStartError(isNetwork(err)
          ? t('Cannot reach the server. Check your connection and try again.')
          : t('Setup could not start. Try again in a moment.'));
      });
    return () => { cancelled = true; };
    // onAlreadyOn is stable enough for this one request per attempt.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [attempt, t]);

  // The QR is drawn here, from the URI; the secret never leaves this browser for it.
  useEffect(() => {
    if (!uri) return;
    let cancelled = false;
    void import('qrcode')
      .then((QRCode) => QRCode.toDataURL(uri, { errorCorrectionLevel: 'M', margin: 1, width: 200, color: { dark: '#0f172a', light: '#ffffff' } }))
      .then((url) => { if (!cancelled) setQr(url); })
      .catch(() => { if (!cancelled) setQrFailed(true); });
    return () => { cancelled = true; };
  }, [uri]);

  const copy = async () => {
    try { await navigator.clipboard.writeText(secret); setCopied('ok'); } catch { setCopied('failed'); }
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (code.length !== 6 || !secret || busy) return;
    setBusy(true); setError('');
    try {
      await authApi.mfaVerifySetup(secret, code);
      onDone();
    } catch (err) {
      const s = statusOf(err);
      const c = codeOf(err);
      if (isNetwork(err)) setError(t('Cannot reach the server. Check your connection and try again.'));
      else if (c === MFA_ERRORS.alreadyEnabled || (s === 409 && !c)) { onAlreadyOn(); return; }
      else if (c === MFA_ERRORS.setupInvalid) { setStale(true); setError(t('This setup has expired. Start again to get a new QR code.')); }
      else if (c === MFA_ERRORS.codeInvalid || s === 400) setError(t("That code didn't match. Enter the newest code from the app, and check that your phone sets its time automatically."));
      else setError(t('Two-step sign-in could not be turned on. Try again in a moment.'));
      setCode('');
    } finally {
      setBusy(false);
    }
  };

  if (startError) {
    return (
      <div className="mt-5 space-y-3" data-testid="two-step-setup">
        <p role="alert" className="text-sm font-medium text-rose-700 dark:text-rose-300">{startError}</p>
        <div className="flex flex-wrap gap-3">
          <button type="button" className={primaryBtn} onClick={() => setAttempt((n) => n + 1)}>{t('Try again')}</button>
          <button type="button" className={secondaryBtn} onClick={onCancel}>{t('Cancel')}</button>
        </div>
      </div>
    );
  }

  return (
    <form onSubmit={submit} noValidate className="mt-5 space-y-5 border-t border-slate-100 pt-5 dark:border-white/[0.07]" data-testid="two-step-setup" aria-busy={!uri}>
      <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Set up your authenticator app')}</h3>
      <ol className="space-y-5">
        <li className="flex gap-3">
          <StepNumber n={1} />
          <div className="min-w-0 flex-1 space-y-3">
            <p className="text-sm text-slate-700 dark:text-slate-300">
              {t('Open your authenticator app (for example Google Authenticator or Microsoft Authenticator), add an account and scan this QR code.')}
            </p>
            <div className="grid h-[216px] w-[216px] place-items-center rounded-2xl border border-slate-200 bg-white p-2 dark:border-white/[0.12]">
              {qr
                ? <img src={qr} width={200} height={200} alt={t('QR code to add KynexOne to your authenticator app')} data-testid="two-step-qr" />
                : qrFailed
                  ? <p className="p-3 text-center text-xs text-slate-600">{t('The QR code could not be drawn. Enter the key below instead.')}</p>
                  : <Loader2 className="h-6 w-6 animate-spin text-slate-400" aria-label={t('Loading…')} />}
            </div>
          </div>
        </li>
        <li className="flex gap-3">
          <StepNumber n={2} />
          <div className="min-w-0 flex-1 space-y-2">
            <p className="text-sm text-slate-700 dark:text-slate-300">{t("Can't scan it? Enter this key in the app instead.")}</p>
            <div className="flex flex-wrap items-center gap-2">
              <code dir="ltr" className="rounded-lg bg-slate-100 px-3 py-2 font-mono text-sm tracking-wider text-slate-900 dark:bg-white/[0.06] dark:text-slate-100" data-testid="two-step-key">
                {secret ? groupKey(secret) : '…'}
              </code>
              <button type="button" className={secondaryBtn} onClick={() => void copy()} disabled={!secret}>
                {copied === 'ok' ? t('Copied') : t('Copy key')}
              </button>
            </div>
            {copied === 'failed' && <p role="alert" className="text-xs text-rose-700 dark:text-rose-300">{t('The key could not be copied. Select it and copy it yourself.')}</p>}
          </div>
        </li>
        <li className="flex gap-3">
          <StepNumber n={3} />
          <div className="min-w-0 flex-1">
            <CodeField id={`${id}-code`} label={t('Enter the 6-digit code the app shows')} value={code} onChange={(v) => { setCode(v); setError(''); }} error={error} />
          </div>
        </li>
      </ol>
      <p className="text-xs leading-5 text-slate-500 dark:text-slate-400">
        {t('Turning it on signs you out everywhere else, and you may need to sign in again here.')}
      </p>
      <div className="flex flex-wrap gap-3">
        {stale ? (
          <button type="button" className={primaryBtn} onClick={() => setAttempt((n) => n + 1)}>{t('Start again')}</button>
        ) : (
          <button type="submit" className={primaryBtn} disabled={busy || code.length !== 6 || !secret} aria-busy={busy}>
            {busy && <Loader2 className="h-4 w-4 animate-spin" aria-hidden />}
            {busy ? t('Turning on…') : t('Turn on')}
          </button>
        )}
        <button type="button" className={secondaryBtn} onClick={onCancel} disabled={busy}>{t('Cancel')}</button>
      </div>
    </form>
  );
}

function DisablePanel({ onCancel, onDone, onRequired, onAlreadyOff }: {
  onCancel: () => void; onDone: () => void; onRequired: (by: RequiredBy) => void; onAlreadyOff: () => void;
}) {
  const { t } = useLocale();
  const id = useId();
  const [code, setCode] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (code.length !== 6 || !confirmed || busy) return;
    setBusy(true); setError('');
    try {
      await authApi.mfaDisable(code);
      onDone();
    } catch (err) {
      const s = statusOf(err);
      const c = codeOf(err);
      if (c === MFA_ERRORS.requiredForRole) { onRequired('role'); return; }
      if (c === MFA_ERRORS.requiredByWorkspace) { onRequired('workspace'); return; }
      if (c === MFA_ERRORS.notEnabled) { onAlreadyOff(); return; }
      if (isNetwork(err)) setError(t('Cannot reach the server. Check your connection and try again.'));
      else if (c === MFA_ERRORS.codeInvalid || s === 400) setError(t("That code didn't match. Enter the newest code from your authenticator app. Each code works only once."));
      else setError(t('Two-step sign-in could not be turned off. Try again in a moment.'));
      setCode('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} noValidate className="mt-5 space-y-4 border-t border-slate-100 pt-5 dark:border-white/[0.07]" data-testid="two-step-disable">
      <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Turn off two-step sign-in')}</h3>
      <CodeField id={`${id}-code`} label={t('Current 6-digit code from your authenticator app')} value={code}
        onChange={(v) => { setCode(v); setError(''); }} error={error} />
      <div className="flex items-start gap-2.5">
        <input id={`${id}-confirm`} type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)}
          className="mt-0.5 h-5 w-5 shrink-0 rounded border-slate-300 text-rose-600 focus-visible:ring-2 focus-visible:ring-rose-500 dark:border-white/20 dark:bg-white/[0.04]" />
        <label htmlFor={`${id}-confirm`} className="text-sm text-slate-700 dark:text-slate-300">
          {t('I understand that only my password will protect this account, and every browser I trusted will be forgotten.')}
        </label>
      </div>
      <p className="text-xs leading-5 text-slate-500 dark:text-slate-400">
        {t('Turning it off signs you out everywhere else, and you may need to sign in again here.')}
      </p>
      <div className="flex flex-wrap gap-3">
        <button type="submit" className={dangerBtn} disabled={busy || code.length !== 6 || !confirmed} aria-busy={busy}>
          {busy && <Loader2 className="h-4 w-4 animate-spin" aria-hidden />}
          {busy ? t('Turning off…') : t('Turn off')}
        </button>
        <button type="button" className={secondaryBtn} onClick={onCancel} disabled={busy}>{t('Cancel')}</button>
      </div>
    </form>
  );
}

function TrustedBrowsers({ days }: { days: number }) {
  const { t } = useLocale();
  const fx = useFormat();
  const [devices, setDevices] = useState<TrustedDevice[] | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);

  const load = useCallback(async () => {
    setLoadError(false);
    try { setDevices(await authApi.mfaTrustedDevices()); } catch { setLoadError(true); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const nameOf = (d: TrustedDevice) => d.browser?.trim() || t('Unknown browser');

  const forget = async (d: TrustedDevice) => {
    setBusyId(d.id); setNotice(null);
    try {
      await authApi.mfaForgetTrustedDevice(d.id);
      setDevices((list) => (list ?? []).filter((x) => x.id !== d.id));
      setNotice({ tone: 'ok', text: d.current
        ? t('This browser is forgotten. It will ask for a code the next time you sign in.')
        : t('{browser} is forgotten. It will ask for a code the next time you sign in.', { browser: nameOf(d) }) });
    } catch (err) {
      if (statusOf(err) === 404) {
        setNotice({ tone: 'ok', text: t('That browser was already forgotten. The list is up to date.') });
        await load();
      } else {
        setNotice({ tone: 'error', text: isNetwork(err)
          ? t('Cannot reach the server. Check your connection and try again.')
          : t('The browser could not be forgotten. Try again in a moment.') });
      }
    } finally {
      setBusyId(null);
    }
  };

  const forgetAll = () => setConfirm({
    title: t('Forget all browsers?'),
    message: t('Every browser, this one included, will ask for a code the next time you sign in.'),
    confirmLabel: t('Forget all browsers'),
    cancelLabel: t('Cancel'),
    busyLabel: t('Forgetting…'),
    closeLabel: t('Close'),
    fallbackError: t('The browsers could not be forgotten. Try again in a moment.'),
    onConfirm: async () => {
      try {
        await authApi.mfaForgetAllTrustedDevices();
      } catch (err) {
        throw new Error(isNetwork(err)
          ? t('Cannot reach the server. Check your connection and try again.')
          : t('The browsers could not be forgotten. Try again in a moment.'));
      }
      setDevices([]);
      setNotice({ tone: 'ok', text: t('All browsers are forgotten. Each will ask for a code the next time you sign in.') });
    },
  });

  return (
    <Card testId="trusted-browsers">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0">
          <h2 className="text-base font-semibold text-slate-900 dark:text-white">{t('Trusted browsers')}</h2>
          <p className="mt-1 text-sm leading-6 text-slate-600 dark:text-slate-400">
            {days > 0
              ? t("{days, plural, one {A trusted browser doesn't ask for a code for # day after you trust it. Forget any browser you don't recognise or no longer use.} other {A trusted browser doesn't ask for a code for # days after you trust it. Forget any browser you don't recognise or no longer use.}}", { days })
              : t('Your company has switched off trusted browsers, so every sign-in asks for a code.')}
          </p>
        </div>
        {devices && devices.length > 0 && (
          <button type="button" className={`${secondaryBtn} shrink-0`} onClick={forgetAll} data-testid="forget-all-browsers">
            {t('Forget all browsers')}
          </button>
        )}
      </div>

      {notice && <InlineNotice notice={notice} />}

      <div className="mt-4">
        {loadError ? (
          <div role="alert" className="flex flex-wrap items-center gap-3 rounded-xl border border-amber-200 bg-amber-50 p-3 dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
            <p className="text-sm font-medium text-amber-900 dark:text-amber-200">{t('Your trusted browsers could not be loaded.')}</p>
            <button type="button" onClick={() => void load()} className={secondaryBtn}>
              <RefreshCw className="h-4 w-4" aria-hidden /> {t('Try again')}
            </button>
          </div>
        ) : devices === null ? (
          <div className="h-16 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
        ) : devices.length === 0 ? (
          <p className="rounded-xl bg-slate-50 p-4 text-center text-sm text-slate-600 dark:bg-white/[0.03] dark:text-slate-400" data-testid="trusted-browsers-empty">
            {t('No trusted browsers. Every sign-in asks for a code.')}
          </p>
        ) : (
          <ul className="divide-y divide-slate-100 rounded-xl border border-slate-100 dark:divide-white/[0.06] dark:border-white/[0.07]" data-testid="trusted-browsers-list">
            {devices.map((d) => (
              <li key={d.id} className="flex flex-col gap-3 p-3 sm:flex-row sm:items-center sm:justify-between sm:p-4" data-testid="trusted-browser-row">
                <div className="flex min-w-0 items-start gap-3">
                  <Laptop className="mt-0.5 h-5 w-5 shrink-0 text-slate-400" aria-hidden />
                  <div className="min-w-0">
                    <p className="flex flex-wrap items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
                      <bdi>{nameOf(d)}</bdi>
                      {d.current && (
                        <span className="rounded-full bg-blue-50 px-2 py-0.5 text-[11px] font-semibold text-blue-700 dark:bg-blue-500/15 dark:text-blue-300" data-testid="this-browser-badge">
                          {t('This browser')}
                        </span>
                      )}
                    </p>
                    <dl className="mt-1 grid gap-x-4 gap-y-0.5 text-xs text-slate-600 dark:text-slate-400 sm:grid-cols-2">
                      <div className="flex gap-1"><dt>{t('Trusted since')}</dt><dd><bdi>{fx.date(d.createdAtUtc)}</bdi></dd></div>
                      <div className="flex gap-1"><dt>{t('Last used')}</dt><dd><bdi>{d.lastUsedAtUtc ? fx.date(d.lastUsedAtUtc) : t('Not used yet')}</bdi></dd></div>
                      <div className="flex gap-1"><dt>{t('Expires')}</dt><dd><bdi>{fx.date(d.expiresAtUtc)}</bdi></dd></div>
                      {d.ipAddress && <div className="flex gap-1"><dt>{t('Network address')}</dt><dd><bdi dir="ltr">{d.ipAddress}</bdi></dd></div>}
                    </dl>
                  </div>
                </div>
                <button type="button" className={`${secondaryBtn} shrink-0 self-start sm:self-center`} onClick={() => void forget(d)}
                  disabled={busyId === d.id} aria-label={t('Forget {browser}', { browser: nameOf(d) })}>
                  {busyId === d.id ? t('Forgetting…') : t('Forget')}
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
      <ConfirmDialog state={confirm} onCancel={() => setConfirm(null)} />
    </Card>
  );
}
