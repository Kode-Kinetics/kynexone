'use client';

import { useEffect, useRef, useState } from 'react';
import dynamic from 'next/dynamic';
import { useRouter, useSearchParams } from 'next/navigation';
import {
  AlertCircle, CheckCircle2, Eye, EyeOff, KeyRound, Lock, Mail,
  ShieldCheck, Smartphone,
} from 'lucide-react';
import { useAuth } from '../contexts/AuthContext';
import { authApi } from '../api/auth';
import { Logo } from '../components/Logo';
import { Brief, VendorFooter } from '../components/LoginMarketing';

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

type Mode = 'login' | 'forgot' | 'reset' | 'mfa' | 'mfa-enroll';

export function LoginPage() {
  const { login, verifyMfaChallenge, mfaPending, mfaEnrollmentPending } = useAuth();
  const router       = useRouter();
  const searchParams = useSearchParams();
  const from         = searchParams?.get('from') ?? '/dashboard';

  const [mode,         setMode]         = useState<Mode>('login');
  const [email,        setEmail]        = useState('');
  const [password,     setPassword]     = useState('');
  const [tenantSlug,   setTenantSlug]   = useState('');
  const [tenantLocked, setTenantLocked] = useState(false);
  const [error,        setError]        = useState('');
  const [info,         setInfo]         = useState('');
  const [loading,      setLoading]      = useState(false);
  const [showPw,       setShowPw]       = useState(false);
  const [forgotEmail,  setForgotEmail]  = useState('');
  const [resetToken,   setResetToken]   = useState('');
  const [newPw,        setNewPw]        = useState('');
  const [confirmPw,    setConfirmPw]    = useState('');
  const [totpCode,     setTotpCode]     = useState('');
  const [enrollmentUri, setEnrollmentUri] = useState('');
  const [enrollmentSecret, setEnrollmentSecret] = useState('');

  useEffect(() => {
    // Platform-admin impersonation: ?impersonate=<tenant-audience-jwt>
    // The backend already minted a scoped 1-hour token; just store it and redirect.
    // The token carries TenantAudience, so platform endpoints remain inaccessible.
    const impersonateToken = searchParams?.get('impersonate');
    if (impersonateToken) {
      localStorage.removeItem('zayra_refresh_token');
      localStorage.setItem('zayra_access_token', impersonateToken);
      router.replace('/dashboard');
      return;
    }
    const wsParam = searchParams?.get('workspace') ?? searchParams?.get('w');
    if (wsParam) { setTenantSlug(wsParam); setTenantLocked(true); return; }
    if (typeof window === 'undefined') return;
    const hostname = window.location.hostname.toLowerCase();
    if (hostname.endsWith('.vercel.app') || hostname.endsWith('.vercel.com')) return;
    const parts = hostname.split('.');
    const skip = new Set(['www', 'app', 'admin', 'mail', 'localhost']);
    const first = parts[0];
    const looksLikeSlug = /^[a-z][a-z0-9-]*$/i.test(first);
    if (parts.length >= 3 && !skip.has(first) && looksLikeSlug) setTenantSlug(first);
  }, [searchParams, router]);

  const handleLogin = async (e: React.FormEvent) => {
    e.preventDefault(); setError(''); setLoading(true);
    try {
      const outcome = await login(email, password, tenantSlug);
      if (outcome === 'mfa') { setMode('mfa'); return; }
      if (outcome === 'mfa-enroll') { setMode('mfa-enroll'); return; }
      router.replace(from);
    }
    // Only a 401 actually means the credentials were wrong. Reporting a server
    // outage or an unreachable API as "invalid credentials" sends everyone hunting
    // for a password problem while the real fault (e.g. a 500 from a schema that
    // lagged behind a deploy) stays invisible.
    catch (err: any) {
      const status = err?.response?.status;
      if (status === 401)      setError('Invalid credentials. Check your email, password and workspace.');
      else if (status === 400) setError(err.response?.data?.message ?? 'Please check the details you entered.');
      else if (status === 429) setError('Too many attempts. Please wait a moment and try again.');
      else if (!err?.response) setError('Cannot reach the server. Check your connection and try again.');
      else {
        const traceId = err.response?.data?.traceId;
        setError(
          `Sign-in is temporarily unavailable (server error ${status}). This is not a problem with your password.`
          + (traceId ? ` Reference: ${traceId}` : ''),
        );
      }
    }
    finally { setLoading(false); }
  };

  const handleMfa = async (e: React.FormEvent) => {
    e.preventDefault(); setError(''); setLoading(true);
    try {
      await verifyMfaChallenge(totpCode);
      router.replace(from);
    }
    catch { setError('Invalid or expired code. Please try again.'); }
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
      .catch(() => setError('MFA enrolment expired. Please sign in again.'));
  }, [mfaEnrollmentPending]);

  const handleMfaEnrollment = async (e: React.FormEvent) => {
    e.preventDefault(); setError(''); setLoading(true);
    try {
      if (!mfaEnrollmentPending || !enrollmentSecret) throw new Error('Missing enrollment challenge.');
      await authApi.mfaEnrollmentVerifySetup(mfaEnrollmentPending.enrollmentToken, enrollmentSecret, totpCode);
      setInfo('MFA is enabled. Sign in again to continue.');
      setTotpCode('');
      setMode('login');
    }
    catch { setError('Invalid or expired enrolment code. Please try again.'); }
    finally { setLoading(false); }
  };

  const handleForgot = async (e: React.FormEvent) => {
    e.preventDefault(); setError(''); setLoading(true);
    try {
      const res = await authApi.forgotPassword(forgotEmail || email, tenantSlug || undefined);
      if (res.resetToken) { setResetToken(res.resetToken); setMode('reset'); }
      else setInfo(res.message ?? 'Check your email for a reset link.');
    } catch (err: any) { setError(err.response?.data?.message ?? 'Request failed.'); }
    finally { setLoading(false); }
  };

  const handleReset = async (e: React.FormEvent) => {
    e.preventDefault(); setError('');
    if (newPw !== confirmPw) { setError('Passwords do not match.'); return; }
    if (newPw.length < 10)   { setError('Minimum 10 characters required.'); return; }
    setLoading(true);
    try {
      await authApi.resetPassword(forgotEmail || email, resetToken, newPw, tenantSlug || undefined);
      setInfo('Password updated. Sign in with your new password.');
      setMode('login');
    } catch (err: any) { setError(err.response?.data?.message ?? 'Reset failed. Token may have expired.'); }
    finally { setLoading(false); }
  };

  const go = (m: Mode) => { setError(''); setInfo(''); setMode(m); if (m === 'forgot' && email) setForgotEmail(email); };

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
              <span className="lx-descriptor">Payroll, HR and compliance</span>
            </div>
          </div>
        </header>

        {/* ── band 2: the stage. DOM order is form → brief, so the credential
             fields are the first thing reached by keyboard and by a screen
             reader; the grid places them left-to-right for the eye. */}
        <main className="lx-stage">
          <div className="lx-slot" ref={slotRef}>
            <div className="lx-pane" ref={paneRef}>
              <div className="lx-card">
                {mode === 'login' && (
                  <>
                    <Head title="Sign in" />
                    <form onSubmit={handleLogin} noValidate className="lx-form">
                      <Field legend="Work email" htmlFor="li-em">
                        <input id="li-em" type="email" value={email} onChange={e => setEmail(e.target.value)}
                          className="lx-in" placeholder="you@company.com" autoComplete="email" required />
                      </Field>

                      {/* dir="auto": the card's strings are still English while
                          LOCALE_BOOT flips the document to dir="rtl" for an
                          Arabic user, and the bidi algorithm then moves the
                          trailing "?" of an all-Latin run to the LEFT — the
                          Arabic sign-in screen rendered "?Forgot password".
                          "auto" takes the direction from the first strong
                          character, so this reads correctly as English today
                          and will still be right if the string is translated. */}
                      <Field legend="Password" htmlFor="li-pw" aside={
                        <button type="button" onClick={() => go('forgot')} className="lx-link"
                          dir="auto" data-testid="login-forgot">Forgot password?</button>
                      }>
                        <span className="lx-inwrap">
                          <input id="li-pw" type={showPw ? 'text' : 'password'} value={password}
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
                            aria-label={showPw ? 'Hide password' : 'Show password'} aria-pressed={showPw}>
                            {showPw ? <EyeOff /> : <Eye />}
                          </button>
                        </span>
                      </Field>

                      {/* The hint under this field ("The short name in your
                          KynexOne address") is gone: the placeholder already
                          shows the shape of the value, and a line of help text
                          under the last field before the button is exactly
                          where a form should be silent. */}
                      <Field legend="Workspace" htmlFor="li-ws" aside={
                        tenantLocked ? <span className="lx-tag"><Lock />Auto-detected</span> : null
                      }>
                        <input id="li-ws" type="text" value={tenantSlug} onChange={e => setTenantSlug(e.target.value)}
                          className="lx-in lx-in-mono" placeholder="your-workspace" autoComplete="organization" required />
                      </Field>

                      <Feedback error={error} info={info} />
                      <Submit busy={busy} label="Sign in" />
                    </form>
                  </>
                )}

                {mode === 'forgot' && (
                  <form onSubmit={handleForgot} noValidate className="lx-form">
                    <Back onClick={() => go('login')} />
                    <Head kicker="Account recovery" title="Reset password"
                      sub="We'll email you a reset code." icon={<Mail />} />
                    <Field legend="Work email" htmlFor="fg-em">
                      <input id="fg-em" type="email" value={forgotEmail || email}
                        onChange={e => setForgotEmail(e.target.value)}
                        className="lx-in" placeholder="you@company.com" autoComplete="email" required />
                    </Field>
                    <Field legend="Workspace" htmlFor="fg-ws" hint="Optional — helps locate your account">
                      <input id="fg-ws" type="text" value={tenantSlug} onChange={e => setTenantSlug(e.target.value)}
                        className="lx-in lx-in-mono" placeholder="your-workspace" />
                    </Field>
                    <Feedback error={error} info={info} />
                    <Submit busy={busy} label="Send reset code" />
                  </form>
                )}
                {mode === 'reset' && (
                  <form onSubmit={handleReset} noValidate className="lx-form">
                    <Back onClick={() => go('forgot')} label="Back" />
                    <Head kicker="Account recovery" title="New password"
                      sub="Enter the code from your email and set a new password." icon={<KeyRound />} />
                    <Field legend="Work email" htmlFor="rs-em">
                      <input id="rs-em" type="email" value={forgotEmail || email}
                        onChange={e => setForgotEmail(e.target.value)}
                        className="lx-in" placeholder="you@company.com" autoComplete="email" required />
                    </Field>
                    <Field legend="Reset code" htmlFor="rs-tk">
                      <input id="rs-tk" type="text" value={resetToken} onChange={e => setResetToken(e.target.value)}
                        className="lx-in lx-in-mono" placeholder="Paste code from email" required />
                    </Field>
                    <Field legend="New password" htmlFor="rs-pw" hint="Minimum 10 characters">
                      <input id="rs-pw" type="password" value={newPw} onChange={e => setNewPw(e.target.value)}
                        className="lx-in" placeholder="••••••••••" autoComplete="new-password" required />
                    </Field>
                    <Field legend="Confirm password" htmlFor="rs-cf">
                      <input id="rs-cf" type="password" value={confirmPw} onChange={e => setConfirmPw(e.target.value)}
                        className="lx-in" placeholder="••••••••••" autoComplete="new-password" required />
                    </Field>
                    <Feedback error={error} info={info} />
                    <Submit busy={busy} label="Update password" />
                  </form>
                )}

                {mode === 'mfa' && (
                  <form onSubmit={handleMfa} noValidate className="lx-form">
                    <Back onClick={() => { setMode('login'); setTotpCode(''); }} />
                    <Head kicker="Security check" title="Two-factor authentication"
                      sub="Enter the 6-digit code from your authenticator app." icon={<Smartphone />} />
                    <Field legend="Authentication code" htmlFor="mfa-code">
                      <input id="mfa-code" type="text" inputMode="numeric" pattern="[0-9]{6}" maxLength={6}
                        dir="ltr" value={totpCode} onChange={e => setTotpCode(e.target.value.replace(/\D/g, ''))}
                        className="lx-in lx-in-code" placeholder="000000" autoComplete="one-time-code" autoFocus required />
                    </Field>
                    <Feedback error={error} info={info} />
                    <Submit busy={busy} label="Verify" disabled={totpCode.length !== 6} />
                  </form>
                )}

                {mode === 'mfa-enroll' && (
                  <form onSubmit={handleMfaEnrollment} noValidate className="lx-form">
                    <Back onClick={() => { setMode('login'); setTotpCode(''); }} />
                    <Head kicker="Security check" title="Set up two-factor authentication"
                      sub="Add this account to your authenticator app, then enter the 6-digit code."
                      icon={<Smartphone />} />
                    {enrollmentSecret && (
                      <Field legend="Setup key" htmlFor="mfa-setup-key">
                        <input id="mfa-setup-key" className="lx-in lx-in-mono lx-in-sm"
                          value={enrollmentSecret} readOnly />
                      </Field>
                    )}
                    {enrollmentUri && <p className="lx-uri">{enrollmentUri}</p>}
                    <Field legend="Authentication code" htmlFor="mfa-enroll-code">
                      <input id="mfa-enroll-code" type="text" inputMode="numeric" pattern="[0-9]{6}" maxLength={6}
                        dir="ltr" value={totpCode} onChange={e => setTotpCode(e.target.value.replace(/\D/g, ''))}
                        className="lx-in lx-in-code" placeholder="000000" autoComplete="one-time-code" required />
                    </Field>
                    <Feedback error={error} info={info} />
                    <Submit busy={busy} label="Enable MFA" disabled={totpCode.length !== 6 || !enrollmentSecret} />
                  </form>
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
                  <a className="lx-secondary" href="/pricing">Price it for your headcount</a>
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

function Back({ onClick, label = 'Back to sign in' }: { onClick: () => void; label?: string }) {
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
function Submit({ busy, label, disabled }: { busy: boolean; label: string; disabled?: boolean }) {
  return (
    <button type="submit" disabled={busy || disabled} aria-busy={busy} className="lx-submit">
      {busy ? <span className="lx-spin" aria-hidden /> : label}
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
