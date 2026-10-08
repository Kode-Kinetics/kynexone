'use client';

import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import dynamic from 'next/dynamic';
import { useRouter, useSearchParams } from 'next/navigation';
import {
  AlertCircle, CheckCircle2, Eye, EyeOff, Lock, Mail,
  ShieldCheck, Smartphone,
} from 'lucide-react';
import { useAuth } from '../contexts/AuthContext';
import { LocaleProvider, useLocale } from '../contexts/LocaleContext';
import { authApi } from '../api/auth';
import { Logo } from '../components/Logo';
import { Brief, VendorFooter } from '../components/LoginMarketing';
import { SignInLanguageToggle } from '../components/SignInLanguageToggle';
import { isWorkspaceRequired, normalizeWorkspace, resolveWorkspaceAlias, safeLocalReturnPath } from '../lib/publicAuth';
import { isWelcomeCode, normalizeWelcomeCode } from '../lib/welcomeCode';
import { setWelcomeHandoff } from '../lib/welcomeHandoff';
import { dateLocale } from '../lib/format';
import { markResetNoticeSeen } from '../components/ResetCodeNotice';
import { homePathFor } from '../lib/homePath';

/** Shapes, not words: nothing here to translate. */
const EMAIL_PLACEHOLDER = 'name@company.com';
const WORKSPACE_PLACEHOLDER = 'your-workspace';

/** Whole minutes for a 429's Retry-After (the API's LoginAbuseGuard thresholds round the same way). */
function waitMinutes(retryAfterHeader: unknown): number {
  const seconds = Number.parseInt(String(retryAfterHeader ?? ''), 10);
  return Number.isFinite(seconds) && seconds > 60 ? Math.ceil(seconds / 60) : 1;
}

/**
 * The aurora is CODE-SPLIT and never server-rendered.
 *
 * It was a static import, which put the 823-line scene, the 679-line render
 * harness and the whole fragment shader source into the chunk the browser has
 * to parse before the sign-in form exists. None of it can draw anything until
 * hydration anyway — the canvas has no SSR output — so it was pure latency in
 * front of the one thing on this page anybody came for. Split out, the form
 * paints on the static field (see `.lx-field-static` in login-aurora.css) and
 * the shader takes over when it is ready.
 */
const LoginAuroraScene = dynamic(
  () => import('../components/LoginAuroraScene').then(m => m.LoginAuroraScene),
  { ssr: false },
);

/**
 * Below this width the scene is not mounted at all and the static CSS field is
 * the whole background.
 *
 * This is a cost decision, not an art one. The shader is fill-rate bound —
 * per-channel refraction plus a 12-tap shaft march over every pixel of a
 * full-bleed canvas at dpr 1.5 — and a mid-range phone pays that on its GPU
 * and its battery for a picture that a phone-width layout mostly covers with
 * the card anyway. The static field keeps the same two lights in the same
 * places, so the composition is the one that was designed; it just is not
 * lit per-frame.
 */
const SCENE_MIN_WIDTH = 768;

type Mode = 'login' | 'forgot' | 'mfa' | 'mfa-enroll' | 'reset-notice';

/**
 * The sign-in surface runs outside the tenant shell, so it mounts its own LocaleProvider: the card is
 * translated with the same dictionary and the same stored choice as the app, and the language picked
 * here is the one the person lands in.
 */
export function LoginPage() {
  return (
    <LocaleProvider preferDeviceLanguage>
      <LoginCard />
    </LocaleProvider>
  );
}

function LoginCard() {
  const { t, dir, locale } = useLocale();
  const { user, login, verifyMfaChallenge, mfaPending, mfaEnrollmentPending } = useAuth();
  const router       = useRouter();
  const searchParams = useSearchParams();
  /* An explicit, safe ?from= wins. Otherwise the landing page follows who signed in:
     self-service-only employees go to /ess, everyone else to /dashboard (lib/homePath.ts). */
  const fromParam    = searchParams?.get('from') ?? '';
  const askedFor     = fromParam && safeLocalReturnPath(fromParam) === fromParam ? fromParam : null;
  const from         = askedFor ?? homePathFor(user);

  const [mode,         setMode]         = useState<Mode>('login');
  const [email,        setEmail]        = useState('');
  const [password,     setPassword]     = useState('');
  const [tenantSlug,   setTenantSlug]   = useState('');
  const [tenantLocked, setTenantLocked] = useState(false);
  /* Workspace is asked for only when it is needed: a ?workspace= link, a tenant subdomain, or the
     server answering `workspace_required` because the email's domain belongs to more than one
     company. Everyone else signs in with the two things they actually know. */
  const [showWorkspace, setShowWorkspace] = useState(false);
  const workspaceRef = useRef<HTMLInputElement>(null);
  /* Bumped to put the cursor in the company-ID field once it has rendered (it may only just have
     appeared, so a focus() in the same tick would find nothing). */
  const [focusWorkspace, setFocusWorkspace] = useState(0);
  useEffect(() => { if (focusWorkspace) workspaceRef.current?.focus(); }, [focusWorkspace]);
  const [error,        setError]        = useState('');
  const [info,         setInfo]         = useState('');
  const [loading,      setLoading]      = useState(false);
  const [showPw,       setShowPw]       = useState(false);
  const [forgotEmail,  setForgotEmail]  = useState('');
  const [totpCode,     setTotpCode]     = useState('');
  const [enrollmentUri, setEnrollmentUri] = useState('');
  const [enrollmentSecret, setEnrollmentSecret] = useState('');

  useLayoutEffect(() => {
    // Never retain a query-string bearer credential in browser history, logs, or
    // copied URLs. Issuance is contained server-side; this is a second boundary.
    const url = new URL(window.location.href);
    if (!url.searchParams.has('impersonate')) return;
    url.searchParams.delete('impersonate');
    window.history.replaceState(window.history.state, '', `${url.pathname}${url.search}${url.hash}`);
  }, []);

  useEffect(() => {
    // Query-string bearer credentials are deliberately rejected. Support and
    // impersonation are contained until their revocation ledger is proven.
    const wsParam = searchParams ? resolveWorkspaceAlias(searchParams) : '';
    if (wsParam) { setTenantSlug(wsParam); setTenantLocked(true); setShowWorkspace(true); return; }
    if (typeof window === 'undefined') return;
    const hostname = window.location.hostname.toLowerCase();
    if (hostname.endsWith('.vercel.app') || hostname.endsWith('.vercel.com')) return;
    const parts = hostname.split('.');
    const skip = new Set(['www', 'app', 'admin', 'mail', 'localhost']);
    const first = parts[0];
    const looksLikeSlug = /^[a-z][a-z0-9-]*$/i.test(first);
    if (parts.length >= 3 && !skip.has(first) && looksLikeSlug) {
      setTenantSlug(normalizeWorkspace(first));
      setTenantLocked(true);
      setShowWorkspace(true);
    }
  }, [searchParams]);

  /** The server could not tell the company from the email alone: ask, and put the cursor there. */
  const askForWorkspace = () => {
    setShowWorkspace(true);
    setTenantLocked(false);
    setError(t("We couldn't find your company from your email. Enter your company ID. HR can tell you what it is."));
    setFocusWorkspace((n) => n + 1);
  };

  /** Asked for by the person: their email's domain belongs to another company, or they just know it. */
  const signInWithWorkspace = () => {
    setShowWorkspace(true);
    setTenantLocked(false);
    setError('');
    setFocusWorkspace((n) => n + 1);
  };

  const workspaceArg = () => (showWorkspace ? normalizeWorkspace(tenantSlug) || undefined : undefined);

  const handleLogin = async (e: React.FormEvent) => {
    e.preventDefault(); setError(''); setInfo('');
    if (!email.trim()) { setError(t('Enter your work email.')); return; }
    if (showWorkspace && !tenantSlug.trim()) {
      setError(t('Enter your company ID.'));
      workspaceRef.current?.focus();
      return;
    }
    setLoading(true);
    try {
      const outcome = await login(email, password, workspaceArg());
      if (outcome === 'mfa') { setMode('mfa'); return; }
      if (outcome === 'mfa-enroll') { setMode('mfa-enroll'); return; }
      setSignedIn(true);
    }
    // Only a 401 actually means the credentials were wrong. Reporting a server
    // outage or an unreachable API as "invalid credentials" sends everyone hunting
    // for a password problem while the real fault (e.g. a 500 from a schema that
    // lagged behind a deploy) stays invisible.
    catch (err: any) {
      const status = err?.response?.status;
      // This API's `workspace_required`, or the previous API's "TenantSlug is required" validation problem.
      if (isWorkspaceRequired(err)) { askForWorkspace(); return; }
      // Someone typed the 8-digit welcome code from their slip into the Password box. That is the
      // most natural mistake on a first sign-in, so take them where the code works. Only AFTER the
      // sign-in failed (an 8-digit password is still a password), and the email and code travel in
      // memory (lib/welcomeHandoff.ts), never in a URL or storage.
      if ((status === 401 || status === 400) && email.trim() && isWelcomeCode(password)) {
        setWelcomeHandoff({ email, code: normalizeWelcomeCode(password), workspace: workspaceArg(), source: 'login' });
        setPassword('');
        router.push('/welcome');
        return;
      }
      if (status === 401) {
        setError(showWorkspace
          ? t('Email, password or company ID is incorrect. Check them and try again.')
          : t('Email or password is incorrect. Check both and try again.'));
      }
      else if (status === 400) setError(t('Please check the details you entered.'));
      else if (status === 429) {
        // Distinct codes from the API (LoginAbuseGuard): only the account limit is "too many attempts".
        const limit = err?.response?.data?.error;
        const count = waitMinutes(err?.response?.headers?.['retry-after']);
        if (limit === 'account_rate_limited') {
          setError(t('Too many attempts for this account. Try again in {count, plural, one {# minute} other {# minutes}}.', { count }));
        } else if (limit === 'ip_failure_budget') {
          setError(t('Too many failed sign-ins from your network. Try again in {count, plural, one {# minute} other {# minutes}}.', { count }));
        } else {
          setError(t('The sign-in service is busy. Try again in {count, plural, one {# minute} other {# minutes}}.', { count }));
        }
      }
      else if (!err?.response) setError(t('Cannot reach the server. Check your connection and try again.'));
      else {
        const traceId = err.response?.data?.traceId;
        setError(traceId
          ? t('Sign-in is unavailable right now (server error {status}). This is not a problem with your password. Reference: {traceId}', { status, traceId })
          : t('Sign-in is unavailable right now (server error {status}). This is not a problem with your password.', { status }));
      }
    }
    finally { setLoading(false); }
  };

  const handleMfa = async (e: React.FormEvent) => {
    e.preventDefault(); setError(''); setLoading(true);
    try {
      await verifyMfaChallenge(totpCode);
      setSignedIn(true);
    }
    catch { setError(t('Invalid or expired code. Please try again.')); }
    finally { setLoading(false); }
  };

  // Switch to MFA mode as soon as context signals a pending challenge.
  useEffect(() => {
    if (mfaPending && mode !== 'mfa') setMode('mfa');
  }, [mfaPending, mode]);

  useEffect(() => {
    if (!mfaEnrollmentPending) return;
    setMode('mfa-enroll');
    setError('');
    setInfo('');
    setEnrollmentUri('');
    setEnrollmentSecret('');
    authApi.mfaEnrollmentSetup(mfaEnrollmentPending.enrollmentToken)
      .then(res => {
        setEnrollmentUri(res.provisioningUri);
        const parsed = new URL(res.provisioningUri);
        setEnrollmentSecret(parsed.searchParams.get('secret') ?? '');
      })
      .catch(() => setError(t('Two-factor setup expired. Please sign in again.')));
  }, [mfaEnrollmentPending, t]);

  const handleMfaEnrollment = async (e: React.FormEvent) => {
    e.preventDefault(); setError(''); setLoading(true);
    try {
      if (!mfaEnrollmentPending || !enrollmentSecret) throw new Error('Missing enrollment challenge.');
      await authApi.mfaEnrollmentVerifySetup(mfaEnrollmentPending.enrollmentToken, enrollmentSecret, totpCode);
      setInfo(t('Two-factor authentication is on. Sign in again to continue.'));
      setTotpCode('');
      setMode('login');
    }
    catch { setError(t('Invalid or expired code. Please try again.')); }
    finally { setLoading(false); }
  };

  /** The company cannot send email, so a reset link will never arrive: point to HR's welcome code. */
  const [noEmailDelivery, setNoEmailDelivery] = useState(false);

  const handleForgot = async (e: React.FormEvent) => {
    e.preventDefault(); setError(''); setInfo(''); setNoEmailDelivery(false);
    if (!(forgotEmail || email).trim()) { setError(t('Enter your work email.')); return; }
    if (showWorkspace && !tenantSlug.trim()) { setError(t('Enter your company ID.')); return; }
    setLoading(true);
    try {
      // The server's own sentence is English; the page says the same thing in the reader's language.
      const res = await authApi.forgotPassword(forgotEmail || email, workspaceArg());
      if (res?.emailDeliveryConfigured === false) setNoEmailDelivery(true);
      else setInfo(t('If this email has an account, a reset link is on its way to it.'));
    } catch (err: any) {
      if (isWorkspaceRequired(err)) askForWorkspace();
      else if (!err?.response) setError(t('Cannot reach the server. Check your connection and try again.'));
      else setError(t('The reset link could not be sent. Try again in a moment.'));
    }
    finally { setLoading(false); }
  };

  /** First sign-in: carry whatever is already typed (email, company ID) to /welcome, in memory. */
  const goWelcome = () => {
    setWelcomeHandoff({ email: email.trim() || undefined, workspace: workspaceArg() });
    router.push('/welcome');
  };

  /* Signed in. If HR has issued a reset code for this login that is still unused, say so before
     going on (contract Amendment 3, F1): the old password still works until the code is redeemed,
     so this is the one moment the person can tell HR "that wasn't me". */
  const [signedIn, setSignedIn] = useState(false);
  useEffect(() => {
    if (!signedIn || !user) return;
    if (user.pendingResetNotice?.date) setMode('reset-notice');
    else router.replace(from);
  }, [signedIn, user, from, router]);
  const noticeDate = (() => {
    const raw = user?.pendingResetNotice?.date;
    const when = raw ? new Date(raw) : null;
    if (!when || Number.isNaN(when.getTime())) return raw ?? '';
    // Before sign-in completes there is no tenant setting to read; the copy deck's default zone applies.
    return new Intl.DateTimeFormat(dateLocale(locale),
      { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'Asia/Riyadh' }).format(when);
  })();

  const go = (m: Mode) => { setError(''); setInfo(''); setNoEmailDelivery(false); setMode(m); if (m === 'forgot' && email) setForgotEmail(email); };

  const slotRef = useRef<HTMLDivElement>(null);
  const paneRef = useRef<HTMLDivElement>(null);
  /** The mark. The shader reads its live centre as the scene's key light. */
  const markRef = useRef<HTMLDivElement>(null);
  const busy = loading;

  /* Starts false so the first client render matches the server's (no canvas),
     then turns on for wide viewports after mount. Tracked live, so rotating a
     tablet or dragging a window across the breakpoint mounts/unmounts the
     scene — and unmounting runs useRenderCanvas's teardown, which cancels the
     rAF loop and hands the GL context back. */
  const [sceneOn, setSceneOn] = useState(false);
  /* Latched: once the scene has proved it cannot hold a frame rate on this
     machine, widening the window must not bring it back. */
  const [sceneRetired, setSceneRetired] = useState(false);
  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return;
    const mq = window.matchMedia(`(min-width: ${SCENE_MIN_WIDTH}px)`);
    const sync = () => setSceneOn(mq.matches);
    sync();
    mq.addEventListener('change', sync);
    return () => mq.removeEventListener('change', sync);
  }, []);

  return (
    <div className="tenant-login-shell lx-shell">
      {/* The static field. Always present, painted from CSS on first paint,
          and the ONLY background below 768px, with no WebGL, and before the
          scene chunk arrives. Purely decorative; never announced. */}
      <div className="lx-field-static" aria-hidden="true" />

      {/* The aurora: a shader field whose key light is the KynexOne mark, the
          roster matrix that light passes through, and the glass the form sits
          behind. Purely decorative; never announced. */}
      {sceneOn && !sceneRetired && (
        <LoginAuroraScene
          slotRef={slotRef}
          paneRef={paneRef}
          markRef={markRef}
          stateKey={`${mode}|${error ? 'e' : ''}${info ? 'i' : ''}`}
          onTooSlow={() => setSceneRetired(true)}
        />
      )}

      <div className="lx-page">
        {/* ── band 1: identity, and NOTHING ELSE. ──────────────────────
             This band used to carry a capability marquee under the lockup and
             a filled pill on the right holding two links and a dual-calendar
             clock. Both are deleted. The marquee duplicated, in a moving strip
             that clipped its own words, breadth the register already implies;
             the pill was a lit object competing with the sign-in card for the
             eye, and a clock is not a reason to buy a payroll system. What the
             emptiness on the right buys is the one thing the top band actually
             needs: a clear field for the rays leaving the mark.
             LOCKED: the mark, its rays and the wordmark are approved and are
             not restyled, resized or moved. */}
        <header className="lx-rail-top">
          <div className="lx-lockup">
            <div className="lx-lockup-mark" ref={markRef}>
              <Logo size="xl" collapsed theme="dark" />            </div>
            <div className="lx-lockup-type">
              <span className="lx-wordmark">Kynex<em>One</em></span>
              <span className="lx-descriptor">{t('Payroll, HR and compliance')}</span>
            </div>
          </div>
        </header>

        {/* ── band 2: the stage. DOM order is form → brief, so the credential
             fields are the first thing reached by keyboard and by a screen
             reader; the grid places them left-to-right for the eye. */}
        <main className="lx-stage">
          <div className="lx-slot" ref={slotRef}>
            <div className="lx-pane" ref={paneRef}>
              <div className="lx-card" dir={dir}>
                <SignInLanguageToggle />
                {mode === 'login' && (
                  <>
                    <Head title={t('Sign in')} />
                    <form onSubmit={handleLogin} noValidate className="lx-form">
                      <Field legend={t('Email')} htmlFor="li-em">
                        {/* Emails and passwords are left-to-right in every language. */}
                        <input id="li-em" type="email" dir="ltr" value={email} onChange={e => setEmail(e.target.value)}
                          className="lx-in" placeholder={EMAIL_PLACEHOLDER} autoComplete="email" required />
                      </Field>

                      {/* dir="auto": the card's strings are still English while
                          LOCALE_BOOT flips the document to dir="rtl" for an
                          Arabic user, and the bidi algorithm then moves the
                          trailing "?" of an all-Latin run to the LEFT — the
                          Arabic sign-in screen rendered "?Forgot password".
                          "auto" takes the direction from the first strong
                          character, so this reads correctly as English today
                          and will still be right if the string is translated. */}
                      <Field legend={t('Password')} htmlFor="li-pw" aside={
                        <button type="button" onClick={() => go('forgot')} className="lx-link"
                          dir="auto" data-testid="login-forgot">{t('Forgot password?')}</button>
                      }>
                        <span className="lx-inwrap" dir="ltr">
                          <input id="li-pw" dir="ltr" type={showPw ? 'text' : 'password'} value={password}
                            onChange={e => setPassword(e.target.value)}
                            className="lx-in lx-in-pw" placeholder="••••••••••" autoComplete="current-password" required />
                          {/* No tabIndex={-1}. Revealing the password is
                              functionality, so WCAG 2.1.1 requires it from the
                              keyboard; taking the control out of the tab order
                              left a keyboard-only user unable to check what
                              they had typed before submitting. It sits between
                              the password field and Workspace, which is where
                              it visually is. */}
                          <button type="button" onClick={() => setShowPw(v => !v)}
                            className="lx-reveal" data-testid="login-password-toggle"
                            aria-label={showPw ? t('Hide password') : t('Show password')} aria-pressed={showPw}>
                            {showPw ? <EyeOff /> : <Eye />}
                          </button>
                        </span>
                      </Field>

                      {/* The hint under this field ("The short name in your
                          KynexOne address") is gone: the placeholder already
                          shows the shape of the value, and a line of help text
                          under the last field before the button is exactly
                          where a form should be silent. */}
                      {showWorkspace && (
                        <Field legend={t('Company ID')} htmlFor="li-ws" aside={
                          tenantLocked ? <span className="lx-tag"><Lock />{t('Auto-detected')}</span> : null
                        }>
                          <input id="li-ws" ref={workspaceRef} type="text" dir="ltr" value={tenantSlug}
                            onChange={e => setTenantSlug(e.target.value)}
                            className="lx-in lx-in-mono" placeholder={WORKSPACE_PLACEHOLDER} autoComplete="organization" required />
                        </Field>
                      )}
                      {/* The way in when the email alone cannot find the company (its domain is another
                          company's, or a shared one): in words, and only while the field is hidden. */}
                      {!showWorkspace && (
                        <button type="button" className="lx-link lx-link-row" dir="auto" onClick={signInWithWorkspace}
                          data-testid="login-use-company-id">{t('Sign in with Company ID')}</button>
                      )}

                      <Feedback error={error} info={info} />
                      <Submit busy={busy} label={t('Sign in')} busyLabel={t('Signing in…')} />
                      {/* The other way in, for someone holding a welcome slip and no password yet.
                          Full width and labelled in words: it is the first thing most employees
                          will ever press here. */}
                      <button type="button" className="lx-alt" onClick={goWelcome} data-testid="login-welcome-code">
                        {t('First time? Use your welcome code')}
                      </button>
                    </form>
                  </>
                )}

                {mode === 'forgot' && (
                  <form onSubmit={handleForgot} noValidate className="lx-form">
                    <Back onClick={() => go('login')} label={t('Back to sign in')} />
                    <Head kicker={t('Account recovery')} title={t('Reset password')}
                      sub={t('We will email you a secure reset link.')} icon={<Mail />} />
                    <Field legend={t('Work email')} htmlFor="fg-em">
                      <input id="fg-em" type="email" dir="ltr" value={forgotEmail || email}
                        onChange={e => setForgotEmail(e.target.value)}
                        className="lx-in" placeholder={EMAIL_PLACEHOLDER} autoComplete="email" required />
                    </Field>
                    {showWorkspace && (
                      <Field legend={t('Company ID')} htmlFor="fg-ws">
                        <input id="fg-ws" type="text" dir="ltr" value={tenantSlug} onChange={e => setTenantSlug(e.target.value)}
                          className="lx-in lx-in-mono" placeholder={WORKSPACE_PLACEHOLDER} autoComplete="organization" required />
                      </Field>
                    )}
                    <Feedback error={error} info={info} />
                    <Submit busy={busy} label={t('Send reset link')} busyLabel={t('Sending…')} />
                    {noEmailDelivery && (
                      <div className="lx-ok" role="status" data-testid="forgot-ask-hr">
                        <CheckCircle2 aria-hidden />
                        <p>{t('Forgot your password? Ask HR for a new welcome code.')}</p>
                      </div>
                    )}
                  </form>
                )}

                {mode === 'mfa' && (
                  <form onSubmit={handleMfa} noValidate className="lx-form">
                    <Back onClick={() => { setMode('login'); setTotpCode(''); }} label={t('Back to sign in')} />
                    <Head kicker={t('Security check')} title={t('Two-factor authentication')}
                      sub={t('Enter the 6-digit code from your authenticator app.')} icon={<Smartphone />} />
                    <Field legend={t('Authentication code')} htmlFor="mfa-code">
                      <input id="mfa-code" type="text" inputMode="numeric" pattern="[0-9]{6}" maxLength={6}
                        dir="ltr" value={totpCode} onChange={e => setTotpCode(e.target.value.replace(/\D/g, ''))}
                        className="lx-in lx-in-code" placeholder="000000" autoComplete="one-time-code" autoFocus required />
                    </Field>
                    <Feedback error={error} info={info} />
                    <Submit busy={busy} label={t('Verify')} busyLabel={t('Verifying…')} disabled={totpCode.length !== 6} />
                  </form>
                )}

                {mode === 'mfa-enroll' && (
                  <form onSubmit={handleMfaEnrollment} noValidate className="lx-form">
                    <Back onClick={() => { setMode('login'); setTotpCode(''); }} label={t('Back to sign in')} />
                    <Head kicker={t('Security check')} title={t('Set up two-factor authentication')}
                      sub={t('Add this account to your authenticator app, then enter the 6-digit code.')}
                      icon={<Smartphone />} />
                    {enrollmentSecret && (
                      <Field legend={t('Setup key')} htmlFor="mfa-setup-key">
                        <input id="mfa-setup-key" className="lx-in lx-in-mono lx-in-sm"
                          value={enrollmentSecret} readOnly />
                      </Field>
                    )}
                    {enrollmentUri && <p className="lx-uri" dir="ltr">{enrollmentUri}</p>}
                    <Field legend={t('Authentication code')} htmlFor="mfa-enroll-code">
                      <input id="mfa-enroll-code" type="text" inputMode="numeric" pattern="[0-9]{6}" maxLength={6}
                        dir="ltr" value={totpCode} onChange={e => setTotpCode(e.target.value.replace(/\D/g, ''))}
                        className="lx-in lx-in-code" placeholder="000000" autoComplete="one-time-code" required />
                    </Field>
                    <Feedback error={error} info={info} />
                    <Submit busy={busy} label={t('Turn on two-factor authentication')} busyLabel={t('Turning on…')} disabled={totpCode.length !== 6 || !enrollmentSecret} />
                  </form>
                )}

                {mode === 'reset-notice' && (
                  <div className="lx-form" data-testid="login-reset-notice">
                    <Head kicker={t('Security check')} title={t('Signed in')} icon={<ShieldCheck aria-hidden />} />
                    <div className="lx-fault" role="alert">
                      <AlertCircle aria-hidden />
                      <p>{t("HR gave you a new sign-in code on {date}. If you didn't ask for it, tell HR.", { date: noticeDate })}</p>
                    </div>
                    <button type="button" className="lx-submit" onClick={() => {
                      // Seen here: the app shell's banner need not repeat it this session.
                      if (user?.pendingResetNotice?.date) markResetNoticeSeen(user.pendingResetNotice.date);
                      router.replace(from);
                    }}>{t('Continue')}</button>
                  </div>
                )}

                {/* ONE line under the button. It used to be three: a
                    tenant-isolation / audit-trails / MFA strip, this link, and
                    a sentence explaining the pricing flow. The security strip
                    went because /security says it properly and this is not
                    where a returning user is reading; the explanatory sentence
                    went because it described a page instead of getting anyone
                    to it. What is left is the only line here that changes what
                    someone does next. */}
                <div className="lx-card-foot">
                  <a className="lx-secondary" href="/pricing">{t('Get a proposal for your headcount')}</a>
                </div>
              </div>
            </div>
          </div>

          {/* ── the brief. No card, and now only two objects: the claim and
               the register that proves it. Both sit directly on the aurora
               field on the SAME left edge as the brand mark above, so the
               page has one spine from the logo to the last figure. See
               src/components/LoginMarketing.tsx for what was cut and why. */}
          <Brief />
        </main>

        {/* ── band 3: the vendor ────────────────────────────────────── */}
        <footer className="lx-rail-bottom">
          <VendorFooter />
        </footer>
      </div>
    </div>
  );
}

/* ── file-local parts ─────────────────────────────────────────────────── */

function Head({ kicker, title, sub, icon }: {
  kicker?: string; title: string; sub?: string; icon?: React.ReactNode;
}) {
  /* aria-live sits on the WRAPPER, not on the <h1>.
   *
   * The h1 previously carried role="status" to announce the new step when the
   * card swaps between sign-in / recovery / MFA. An ARIA role replaces the
   * element's native semantics, so that turned the page's only <h1> into a
   * live region with no heading role at all: nothing in the accessibility
   * tree was a level-1 heading, and heading navigation (the first thing a
   * screen-reader user does on an unfamiliar page) had nowhere to land.
   * Announcing from the wrapper keeps both — the region still speaks the new
   * title on a mode change, and the h1 stays a heading. */
  return (
    <div className="lx-head-block" aria-live="polite">
      {kicker && (
        <p className="lx-kicker">
          {icon ?? <ShieldCheck aria-hidden />}
          {kicker}
        </p>
      )}
      <h1 className="lx-title">{title}</h1>
      {sub && <p className="lx-sub">{sub}</p>}
    </div>
  );
}

function Back({ onClick, label }: { onClick: () => void; label: string }) {
  /* The arrow is a separate span marked aria-hidden rather than a character in
     the label, and the button is dir="auto" for the same bidi reason as the
     forgot link. The glyph itself is flipped for RTL in CSS, so "back" always
     points away from the reading direction instead of into it. */
  return (
    <button type="button" onClick={onClick} className="lx-back" dir="auto" data-testid="login-back">
      <span className="lx-back-arrow" aria-hidden>←</span>{label}
    </button>
  );
}

/** One input, with a real <label for> and an optional aside on the legend row. */
function Field({ legend, htmlFor, aside, hint, children }: {
  legend: string; htmlFor?: string; aside?: React.ReactNode; hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="lx-field">
      <div className="lx-legend">
        {htmlFor ? <label htmlFor={htmlFor}>{legend}</label> : <span>{legend}</span>}
        {aside}      </div>
      {children}
      {hint && <p className="lx-hint">{hint}</p>}
    </div>
  );
}

/** The key. It is the brightest object on the page, because you press it. */
/*  The label STAYS while the request is in flight. Swapping it out for a bare
 *  spinner stripped the button of its accessible name at exactly the moment a
 *  screen-reader user needs it — the control went from "Sign in, button" to an
 *  unnamed disabled button, and `aria-busy` alone does not say what is busy.
 *  It also left `.lx-submit`'s `gap: 9px` dead, which is the tell that
 *  spinner-beside-label was the original intent.
 *
 *  `busyLabel` is also the non-motion alternative the reduced-motion path
 *  leans on: there the ring is hidden outright, so the word is the only thing
 *  left to carry the state. */
function Submit({ busy, label, busyLabel, disabled }: {
  busy: boolean; label: string; busyLabel: string; disabled?: boolean;
}) {
  return (
    <button type="submit" disabled={busy || disabled} aria-busy={busy} className="lx-submit">
      {busy && <span className="lx-spin" aria-hidden />}
      {busy ? busyLabel : label}
    </button>
  );
}

/* The dual Gregorian / Hijri service stamp that used to sit in the top-right
   pill is deleted. The two calendars still appear on this page, once, in the
   run bar above the register — where a pay period labels figures and therefore
   means something. A clock in a header does not. */

function Feedback({ error, info }: { error: string; info: string }) {
  if (error) return (
    <div className="lx-fault" role="alert">
      <AlertCircle aria-hidden />
      <p>{error}</p>
    </div>
  );
  if (info) return (
    <div className="lx-ok" role="status">
      <CheckCircle2 aria-hidden />
      <p>{info}</p>    </div>
  );
  return null;
}
