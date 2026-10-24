'use client';

import { useCallback, useEffect, useId, useRef, useState, type FormEvent, type ReactNode, type RefObject } from 'react';
import { useRouter } from 'next/navigation';
import { AlertTriangle, CheckCircle2, Info, Loader2, Monitor, RefreshCw, ShieldAlert, ShieldCheck, Smartphone, Tablet, type LucideIcon } from 'lucide-react';
import { authApi, MFA_ERRORS, type MfaStatus, type TrustedDevice } from '@/src/api/auth';
import { useAuth } from '@/src/contexts/AuthContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import { ConfirmDialog, type ConfirmState } from '@/src/components/ui/ConfirmDialog';
import { setSignInNotice } from '@/src/lib/signInNotice';
import { toAsciiDigits } from '@/src/lib/welcomeCode';
import { BackupCodesStep, primaryBtn, secondaryBtn } from './BackupCodesStep';

/**
 * Sign-in security: the signed-in person's own two-step sign-in and the browsers they trust.
 *
 * Endpoints (GET /api/auth/mfa/status, POST setup / verify-setup / disable / backup-codes,
 * GET and DELETE /api/auth/mfa/trusted-devices). Every endpoint needs only a session, so this card
 * is for every role, and nothing on it depends on a permission.
 *
 * Backup codes: verify-setup returns the first set once (an older API's 204 returns none, and the
 * card goes straight on); "Get new backup codes" replaces the set behind a current app code. Either
 * way the codes are shown in the "Save your backup codes" step and dropped from state on Done.
 *
 * Turning two-step on or off ends every session of the account today (the server rotates the
 * session stamp, and disable revokes the refresh tokens). After either change the card asks the
 * server whether this session survived: if it did, the card refreshes in place; if not, the person
 * is taken to the sign-in page with one sentence saying why.
 */

type Panel = 'none' | 'setup' | 'disable' | 'regenerate' | 'codes';

/** At this many unused backup codes or fewer, the card suggests getting new ones. */
const FEW_BACKUP_CODES = 3;

const ROLE_REQUIRES = 'Required for your role, because you can see or change payroll, banking or access.';
const WORKSPACE_REQUIRES = 'Required by your company for everyone.';
type RequiredBy = 'role' | 'workspace';

/** .btn-primary / .btn-secondary with a real disabled state live in ./BackupCodesStep, shared with it. */
const dangerBtn = 'btn-danger justify-center';
/** Headings that receive focus when a panel opens or a list changes: reachable, never in the tab order. */
const focusHeading = 'scroll-mt-24 focus:outline-none';
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
 * The device picture for a trusted browser, from the server's summary ("Chrome on Windows", "Safari on
 * iOS"; MfaTrustedDevices.DescribeBrowser). The server names the OS, not the hardware, so iOS and Android
 * are drawn as a phone, a tablet only if the words say so, and anything else (or nothing) as a monitor.
 */
export type DeviceKind = 'phone' | 'tablet' | 'computer';
export function deviceKindOf(browser: string | null | undefined): DeviceKind {
  const b = (browser ?? '').toLowerCase();
  if (/\b(ipad|tablet)\b/.test(b)) return 'tablet';
  if (/\b(ios|iphone|android)\b/.test(b)) return 'phone';
  return 'computer';
}
const DEVICE_ICON: Record<DeviceKind, LucideIcon> = { phone: Smartphone, tablet: Tablet, computer: Monitor };

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
  const { logout, user } = useAuth();
  const router = useRouter();
  /**
   * The codes on screen in the "Save your backup codes" step, and why they are there: the first set
   * right after turning two-step on (Done then carries on as a finished turn-on), or a replacement.
   * Held only while the step is open; Done sets this back to null.
   */
  const [freshCodes, setFreshCodes] = useState<{ codes: string[]; after: 'turn-on' | 'regenerate' } | null>(null);

  const [status, setStatus] = useState<MfaStatus | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [panel, setPanel] = useState<Panel>('none');
  const [notice, setNotice] = useState<Notice | null>(null);
  /** Set by a 409 ERR_MFA_REQUIRED_FOR_ROLE / _BY_WORKSPACE even when the status said the user could turn it off. */
  const [lockedBy, setLockedBy] = useState<RequiredBy | null>(null);
  /** Where focus goes once a panel has closed: back to the button that opened it (Cancel), or to the status. */
  const [focusAfterClose, setFocusAfterClose] = useState<'trigger' | 'status' | null>(null);
  const statusRef = useRef<HTMLHeadingElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  /** Which button opened the panel, so Cancel can hand focus back to that one. */
  const [trigger, setTrigger] = useState<'main' | 'regenerate'>('main');

  const closePanel = useCallback((focus: 'trigger' | 'status') => {
    setPanel('none');
    setFreshCodes(null);
    setFocusAfterClose(focus);
  }, []);

  useEffect(() => {
    if (panel !== 'none' || !focusAfterClose || !status) return;
    (focusAfterClose === 'trigger' && triggerRef.current ? triggerRef.current : statusRef.current)?.focus();
    setFocusAfterClose(null);
  }, [panel, focusAfterClose, status]);

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
    closePanel('status');
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
  }, [closePanel, load, logout, router, t]);

  /** Turned on: show the first backup codes before anything else happens, when the server sent some. */
  const afterTurnOn = useCallback((codes: string[] | null) => {
    if (!codes?.length) { void afterChange(true); return; }
    setFreshCodes({ codes, after: 'turn-on' });
    setPanel('codes');
  }, [afterChange]);

  /** Done on the codes step: the codes leave state (and the DOM) here, then the flow carries on. */
  const codesDone = useCallback(() => {
    const after = freshCodes?.after;
    setFreshCodes(null);
    if (after === 'turn-on') { void afterChange(true); return; }
    closePanel('status');
    setNotice({ tone: 'ok', text: t('Your new backup codes are ready. The old ones no longer work.') });
    void load();
  }, [afterChange, closePanel, freshCodes, load, t]);

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

  // Testing phase: the API has two-step switched off, so the controls (which would be refused or
  // pointless) give way to a notice. Enrolled factors are kept untouched on the server.
  if (status.disabledForTesting) {
    return (
      <Card testId="sign-in-security-card">
        <div className="flex items-start gap-3" data-testid="two-step-disabled-for-testing">
          <span className="mt-0.5 grid h-9 w-9 shrink-0 place-items-center rounded-xl bg-slate-100 text-slate-500 dark:bg-white/[0.06] dark:text-slate-400" aria-hidden>
            <Info className="h-5 w-5" />
          </span>
          <p role="status" className="text-sm text-slate-700 dark:text-slate-300">
            {t('Two-step sign-in is switched off while the product is in testing')}
          </p>
        </div>
      </Card>
    );
  }

  /*
   * Right after turning on, the status is not re-read until Done: turning on can end this session,
   * and a request that finds it ended navigates to sign-in, taking the unsaved codes with it. The
   * heading still says it is on.
   */
  const on = status.enabled || freshCodes?.after === 'turn-on';
  const remaining = status.enabled && Number.isInteger(status.backupCodesRemaining)
    ? Math.max(0, status.backupCodesRemaining as number) : null;
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
            <h2 ref={statusRef} tabIndex={-1} className={`text-base font-semibold text-slate-900 dark:text-white ${focusHeading}`} data-testid="two-step-status">
              {on ? t('Two-step sign-in is on') : t('Two-step sign-in is off')}
            </h2>
            <p className="mt-1 text-sm leading-6 text-slate-600 dark:text-slate-400">
              {on
                ? t('When you sign in, you enter your password and then a 6-digit code from the authenticator app on your phone.')
                : t('You sign in with your password only. Turn on two-step sign-in to also ask for a 6-digit code from an authenticator app on your phone, so a stolen password is not enough.')}
            </p>
          </div>
        </div>

        {remaining !== null && panel !== 'codes' && (
          <div className="mt-4 space-y-2" data-testid="backup-codes-summary">
            <p className="text-sm font-medium text-slate-800 dark:text-slate-200" data-testid="backup-codes-remaining">
              {t('Backup codes: {count} left', { count: remaining })}
            </p>
            {remaining <= FEW_BACKUP_CODES && (
              <p className="flex items-start gap-2 text-sm leading-6 text-slate-600 dark:text-slate-400" data-testid="backup-codes-low">
                <Info className="mt-1 h-4 w-4 shrink-0 text-slate-500 dark:text-slate-400" aria-hidden />
                <span>{remaining === 0
                  ? t('You have no backup codes left. Get new ones so you can still sign in without your phone.')
                  : t('Only a few backup codes are left. Get new ones before they run out.')}</span>
              </p>
            )}
          </div>
        )}

        {notice && <InlineNotice notice={notice} />}

        {/* A fact about the account, not a warning: neutral, with the shield in the brand colour. */}
        {requiredBy && (!on || !canDisable) && (
          <div className="mt-4 flex items-start gap-2.5 rounded-xl border border-slate-200 bg-slate-50 p-3 dark:border-white/[0.08] dark:bg-white/[0.03]" data-testid="two-step-required-reason">
            <ShieldCheck className="mt-0.5 h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" aria-hidden />
            <p className="text-sm leading-6 text-slate-700 dark:text-slate-300">
              {requiredBy === 'workspace' ? t(WORKSPACE_REQUIRES) : t(ROLE_REQUIRES)}
            </p>
          </div>
        )}

        {panel === 'none' && (
          <div className="mt-4 flex flex-wrap gap-3">
            {!on && (
              <button type="button" ref={trigger === 'main' ? triggerRef : undefined} className={primaryBtn} onClick={() => { setTrigger('main'); setNotice(null); setPanel('setup'); }} data-testid="two-step-turn-on">
                {t('Turn on two-step sign-in')}
              </button>
            )}
            {remaining !== null && (
              <button type="button" ref={trigger === 'regenerate' ? triggerRef : undefined} className={secondaryBtn}
                onClick={() => { setTrigger('regenerate'); setNotice(null); setPanel('regenerate'); }} data-testid="backup-codes-regenerate">
                {t('Get new backup codes')}
              </button>
            )}
            {on && canDisable && (
              <button type="button" ref={trigger === 'main' ? triggerRef : undefined} className={secondaryBtn}
                onClick={() => { setTrigger('main'); setNotice(null); setPanel('disable'); }} data-testid="two-step-turn-off">
                {t('Turn off two-step sign-in')}
              </button>
            )}
          </div>
        )}

        {panel === 'setup' && (
          <SetupPanel
            onCancel={() => closePanel('trigger')}
            onDone={afterTurnOn}
            onAlreadyOn={() => { closePanel('status'); setNotice({ tone: 'ok', text: t('Two-step sign-in is already on for your account.') }); void load(); }}
          />
        )}
        {panel === 'disable' && (
          <DisablePanel
            onCancel={() => closePanel('trigger')}
            onDone={() => void afterChange(false)}
            onRequired={(by) => { setLockedBy(by); closePanel('status'); }}
            onAlreadyOff={() => { closePanel('status'); setNotice({ tone: 'ok', text: t('Two-step sign-in is already off for your account.') }); void load(); }}
          />
        )}
        {panel === 'regenerate' && (
          <RegeneratePanel
            onCancel={() => closePanel('trigger')}
            onDone={(codes) => { setFreshCodes({ codes, after: 'regenerate' }); setPanel('codes'); }}
            onNotOn={() => { closePanel('status'); setNotice({ tone: 'ok', text: t('Two-step sign-in is off for your account, so there are no backup codes.') }); void load(); }}
          />
        )}
        {panel === 'codes' && freshCodes && (
          <BackupCodesStep codes={freshCodes.codes} account={user?.email} variant="app"
            replaced={freshCodes.after === 'regenerate'} onDone={codesDone} />
        )}
      </Card>

      {(status.enabled || (status.trustedDeviceCount ?? 0) > 0) && <TrustedBrowsers days={days} />}
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

/** A panel's heading takes focus when the panel opens, so a screen reader starts where the new content does. */
function useFocusOnMount<T extends HTMLElement>(): RefObject<T | null> {
  const ref = useRef<T>(null);
  useEffect(() => { ref.current?.focus(); }, []);
  return ref;
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

function SetupPanel({ onCancel, onDone, onAlreadyOn }: { onCancel: () => void; onDone: (codes: string[] | null) => void; onAlreadyOn: () => void }) {
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
  const headingRef = useFocusOnMount<HTMLHeadingElement>();
  const heading = (
    <h3 ref={headingRef} tabIndex={-1} className={`text-sm font-semibold text-slate-900 dark:text-white ${focusHeading}`} data-testid="two-step-setup-heading">
      {t('Set up your authenticator app')}
    </h3>
  );

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
      onDone(await authApi.mfaVerifySetup(secret, code));
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
      <div className="mt-5 space-y-3 border-t border-slate-100 pt-5 dark:border-white/[0.07]" data-testid="two-step-setup">
        {heading}
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
      {heading}
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
            <p className="mt-2 text-xs leading-5 text-slate-500 dark:text-slate-400" data-testid="two-step-lost-phone-note">
              {t('If you lose this phone, your administrator can reset it, or you can use one of your backup codes.')}
            </p>
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
  const headingRef = useFocusOnMount<HTMLHeadingElement>();

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
      <h3 ref={headingRef} tabIndex={-1} className={`text-sm font-semibold text-slate-900 dark:text-white ${focusHeading}`} data-testid="two-step-disable-heading">{t('Turn off two-step sign-in')}</h3>
      <CodeField id={`${id}-code`} label={t('Current 6-digit code from your authenticator app')} value={code}
        onChange={(v) => { setCode(v); setError(''); }} error={error} />
      <div className="flex items-start gap-2.5">
        <input id={`${id}-confirm`} type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)}
          className="mt-1 h-4 w-4 shrink-0 cursor-pointer accent-rose-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-rose-500 focus-visible:ring-offset-2 dark:focus-visible:ring-offset-[#0d1225]" />
        <label htmlFor={`${id}-confirm`} className="cursor-pointer text-sm leading-6 text-slate-700 dark:text-slate-300">
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

/** "Get new backup codes": a current app code, a plain warning, then the new set in the codes step. */
function RegeneratePanel({ onCancel, onDone, onNotOn }: {
  onCancel: () => void; onDone: (codes: string[]) => void; onNotOn: () => void;
}) {
  const { t } = useLocale();
  const id = useId();
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const headingRef = useFocusOnMount<HTMLHeadingElement>();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (code.length !== 6 || busy) return;
    setBusy(true); setError('');
    try {
      const codes = await authApi.mfaRegenerateBackupCodes(code);
      if (codes.length) { onDone(codes); return; }
      setError(t('New backup codes could not be made. Try again in a moment.'));
      setCode('');
    } catch (err) {
      const s = statusOf(err);
      const c = codeOf(err);
      if (c === MFA_ERRORS.notEnabled) { onNotOn(); return; }
      if (isNetwork(err)) setError(t('Cannot reach the server. Check your connection and try again.'));
      else if (c === MFA_ERRORS.codeInvalid || s === 400) setError(t("That code didn't match. Enter the newest code from your authenticator app. Each code works only once."));
      else setError(t('New backup codes could not be made. Try again in a moment.'));
      setCode('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} noValidate className="mt-5 space-y-4 border-t border-slate-100 pt-5 dark:border-white/[0.07]" data-testid="backup-codes-regenerate-panel">
      <h3 ref={headingRef} tabIndex={-1} className={`text-sm font-semibold text-slate-900 dark:text-white ${focusHeading}`} data-testid="backup-codes-regenerate-heading">
        {t('Get new backup codes')}
      </h3>
      <p className="flex items-start gap-2.5 rounded-xl border border-amber-200 bg-amber-50 p-3 text-sm font-medium leading-6 text-amber-900 dark:border-amber-500/20 dark:bg-amber-500/[0.06] dark:text-amber-200" data-testid="backup-codes-regenerate-warning">
        <AlertTriangle className="mt-1 h-4 w-4 shrink-0" aria-hidden />
        <span>{t('Your old codes stop working. Only the new ones will sign you in.')}</span>
      </p>
      <CodeField id={`${id}-code`} label={t('Current 6-digit code from your authenticator app')} value={code}
        onChange={(v) => { setCode(v); setError(''); }} error={error} />
      <div className="flex flex-wrap gap-3">
        <button type="submit" className={primaryBtn} disabled={busy || code.length !== 6} aria-busy={busy}>
          {busy && <Loader2 className="h-4 w-4 animate-spin" aria-hidden />}
          {busy ? t('Getting new codes…') : t('Get new codes')}
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
  /**
   * After a browser leaves the list, focus goes to the Forget button now in its place (the next row,
   * or the new last one), or to the "Trusted browsers" heading when none are left. Never to <body>.
   */
  const focusRow = useRef<number | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const forgetRefs = useRef<(HTMLButtonElement | null)[]>([]);

  const load = useCallback(async () => {
    setLoadError(false);
    try { setDevices(await authApi.mfaTrustedDevices()); } catch { setLoadError(true); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    const row = focusRow.current;
    if (row === null || devices === null) return;
    focusRow.current = null;
    // A frame later: closing the confirmation dialog first hands focus back to its (now removed) opener.
    requestAnimationFrame(() => {
      const target = devices.length === 0 ? headingRef.current : forgetRefs.current[Math.min(row, devices.length - 1)];
      target?.focus();
    });
  }, [devices]);

  const nameOf = (d: TrustedDevice) => d.browser?.trim() || t('Unknown browser');

  const forget = async (d: TrustedDevice, index: number) => {
    setBusyId(d.id); setNotice(null);
    try {
      await authApi.mfaForgetTrustedDevice(d.id);
      focusRow.current = index;
      setDevices((list) => (list ?? []).filter((x) => x.id !== d.id));
      setNotice({ tone: 'ok', text: d.current
        ? t('This browser is forgotten. It will ask for a code the next time you sign in.')
        : t('{browser} is forgotten. It will ask for a code the next time you sign in.', { browser: nameOf(d) }) });
    } catch (err) {
      if (statusOf(err) === 404) {
        setNotice({ tone: 'ok', text: t('That browser was already forgotten. The list is up to date.') });
        focusRow.current = index;
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
      focusRow.current = 0;
      setDevices([]);
      setNotice({ tone: 'ok', text: t('All browsers are forgotten. Each will ask for a code the next time you sign in.') });
    },
  });

  return (
    <Card testId="trusted-browsers">
      <div className="flex flex-col items-start gap-2 sm:flex-row sm:justify-between sm:gap-4">
        <div className="min-w-0">
          <h2 ref={headingRef} tabIndex={-1} className={`text-base font-semibold text-slate-900 dark:text-white ${focusHeading}`} data-testid="trusted-browsers-heading">
            {t('Trusted browsers')}
          </h2>
          <p className="mt-1 text-sm leading-6 text-slate-600 dark:text-slate-400">
            {days > 0
              ? t('{days, plural, one {A trusted browser skips the code for # day. Your company sets this period.} other {A trusted browser skips the code for # days. Your company sets this period.}}', { days })
              : t('Your company has switched off trusted browsers, so every sign-in asks for a code.')}
          </p>
        </div>
        {/* Quiet on purpose: a sweep for when several browsers are in doubt, never the page's main action. */}
        {devices && devices.length >= 2 && (
          <button type="button" onClick={forgetAll} data-testid="forget-all-browsers"
            className="-mx-2 inline-flex min-h-9 shrink-0 items-center rounded-lg px-2 text-sm font-semibold text-rose-700 underline-offset-4 hover:bg-rose-50 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-rose-500 dark:text-rose-300 dark:hover:bg-rose-500/10 [@media(pointer:coarse)]:min-h-11">
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
            {devices.map((d, i) => {
              const kind = deviceKindOf(d.browser);
              const Icon = DEVICE_ICON[kind];
              return (
                <li key={d.id} className="p-3 sm:p-4" data-testid="trusted-browser-row">
                  <div className="flex items-start gap-3">
                    <Icon className="mt-0.5 h-5 w-5 shrink-0 text-slate-500 dark:text-slate-400" aria-hidden data-testid="device-icon" data-kind={kind} />
                    <div className="min-w-0 flex-1">
                      {/* The name wraps inside its column, so Forget keeps its place at the row's end. */}
                      <div className="flex items-start justify-between gap-3 sm:gap-4">
                        <p className="flex min-w-0 flex-wrap items-center gap-2 pt-1.5 text-sm font-semibold text-slate-900 dark:text-white">
                          <bdi>{nameOf(d)}</bdi>
                          {d.current && (
                            <span className="rounded-full bg-blue-50 px-2 py-0.5 text-[11px] font-semibold text-blue-700 rtl:text-[13px] dark:bg-blue-500/15 dark:text-blue-300" data-testid="this-browser-badge">
                              {t('This browser')}
                            </span>
                          )}
                        </p>
                        <button type="button" ref={(el) => { forgetRefs.current[i] = el; }} className={`${secondaryBtn} shrink-0`}
                          onClick={() => void forget(d, i)} disabled={busyId === d.id}
                          aria-label={t('Forget {browser}', { browser: nameOf(d) })} data-testid="forget-browser">
                          {busyId === d.id ? t('Forgetting…') : t('Forget')}
                        </button>
                      </div>
                      <dl className="mt-2 grid grid-cols-[minmax(0,7.5rem)_minmax(0,1fr)] gap-x-4 gap-y-1 text-[13px] leading-5 sm:grid-cols-[13rem_minmax(0,1fr)]">
                        <dt className="text-slate-500 dark:text-slate-400">{t('Trusted since')}</dt>
                        <dd className="font-medium text-slate-800 dark:text-slate-200"><bdi>{fx.date(d.createdAtUtc)}</bdi></dd>
                        <dt className="text-slate-500 dark:text-slate-400">{t('Last used')}</dt>
                        {d.lastUsedAtUtc
                          ? <dd className="font-medium text-slate-800 dark:text-slate-200"><bdi>{fx.date(d.lastUsedAtUtc)}</bdi></dd>
                          : <dd className="text-slate-500 dark:text-slate-400">{t('Not used since you trusted it')}</dd>}
                        <dt className="text-slate-500 dark:text-slate-400">{t('Expires')}</dt>
                        <dd className="font-medium text-slate-800 dark:text-slate-200"><bdi>{fx.date(d.expiresAtUtc)}</bdi></dd>
                        {d.ipAddress && <>
                          <dt className="text-slate-500 dark:text-slate-400">{t('IP address')}</dt>
                          <dd className="font-medium text-slate-800 dark:text-slate-200"><bdi dir="ltr">{d.ipAddress}</bdi></dd>
                        </>}
                      </dl>
                    </div>
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </div>
      <ConfirmDialog state={confirm} onCancel={() => setConfirm(null)} />
    </Card>
  );
}
