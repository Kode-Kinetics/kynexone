'use client';

import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { AlertCircle, Check, CheckCircle2, Circle, Eye, EyeOff, Info, KeyRound } from 'lucide-react';
import { useAuth } from '../contexts/AuthContext';
import { LocaleProvider, useLocale } from '../contexts/LocaleContext';
import { authApi } from '../api/auth';
import { Logo } from '../components/Logo';
import { VendorFooter } from '../components/LoginMarketing';
import { SignInLanguageToggle } from '../components/SignInLanguageToggle';
import { normalizeWorkspace, resolveWorkspaceAlias } from '../lib/publicAuth';
import {
  consumeWelcomeFragment, DEFAULT_MIN_PASSWORD_LENGTH, formatWelcomeCode, hasArabicLetters, isWelcomeCode,
  normalizeWelcomeCode, passwordChecks,
} from '../lib/welcomeCode';
import { takeBootFragment, takeWelcomeHandoff } from '../lib/welcomeHandoff';
import { homePathFor } from '../lib/homePath';

/** Shapes, not words: nothing here to translate. */
const EMAIL_PLACEHOLDER = 'name@company.com';
const CODE_PLACEHOLDER = '1234 5678';
const WORKSPACE_PLACEHOLDER = 'your-company';
/** The brand, never translated (the same lockup as /login). */
const WORDMARK = { head: 'Kynex', tail: 'One' } as const;

type Step = 'code' | 'password';

/**
 * /welcome — an employee's first sign-in, with the welcome code HR gave them.
 *
 * Three ways in:
 *  - the QR on the sign-in slip opens `/welcome#e=<email>&c=<code>&w=<company id>`. An inline boot
 *    script (app/welcome/page.tsx) moves the fragment into memory and out of the address bar before
 *    any app script runs; this page reads it, then asks for ONE thing: a password. The code is never
 *    shown, stored or put in a query string.
 *  - from /login, in memory (lib/welcomeHandoff.ts): the "First time?" button, or a code typed into
 *    the Password box.
 *  - typed: work email + the 8-digit code, then the same password step.
 *
 * Saving calls `POST /api/auth/welcome/redeem` (no session), then signs in with the new password and
 * lands on the employee home. Every refusal is mapped from its code to a sentence in the reader's
 * language; server text is never shown.
 */
export function WelcomePage() {
  return (
    <LocaleProvider>
      <WelcomeCard />
    </LocaleProvider>
  );
}

function WelcomeCard() {
  const { t, dir, locale, setLocale } = useLocale();
  const { user, login, logout } = useAuth();
  const router = useRouter();
  const searchParams = useSearchParams();
  /* Signed in: land where a normal sign-in would (lib/homePath.ts) — Self-Service for an employee,
     the usual home for HR or an admin redeeming a reset code. Waits for the user to be set. */
  const [signedIn, setSignedIn] = useState(false);
  useEffect(() => { if (signedIn && user) router.replace(homePathFor(user)); }, [signedIn, user, router]);

  const [step, setStep] = useState<Step>('code');
  const [email, setEmail] = useState('');
  const [code, setCode] = useState('');
  const [password, setPassword] = useState('');
  const [showPw, setShowPw] = useState(true);
  const [workspace, setWorkspace] = useState('');
  const [showWorkspace, setShowWorkspace] = useState(false);
  const [error, setError] = useState('');
  const [info, setInfo] = useState('');
  const [busy, setBusy] = useState(false);
  const [passwordSet, setPasswordSet] = useState(false);
  /** The server refused because this browser still holds someone else's KynexOne session. */
  const [needsSignOut, setNeedsSignOut] = useState(false);
  const consumed = useRef(false);
  const inFlight = useRef(false);
  const workspaceRef = useRef<HTMLInputElement>(null);
  /* Bumped to put the cursor in the company-ID field once it has rendered (it may only just have
     appeared, so a focus() in the same tick would find nothing). */
  const [focusWorkspace, setFocusWorkspace] = useState(0);
  useEffect(() => { if (focusWorkspace) workspaceRef.current?.focus(); }, [focusWorkspace]);
  const [minLength, setMinLength] = useState(DEFAULT_MIN_PASSWORD_LENGTH);

  useLayoutEffect(() => {
    // Strict Mode runs layout effects twice in development; the second pass would find the
    // fragment already scrubbed and overwrite what the first one captured.
    if (consumed.current) return;
    consumed.current = true;
    const fromFragment = consumeWelcomeFragment(window.location, window.history, takeBootFragment());
    const handoff = takeWelcomeHandoff();
    const captured = {
      email: fromFragment.email || handoff?.email || '',
      code: fromFragment.code || normalizeWelcomeCode(handoff?.code),
      workspace: fromFragment.workspace || normalizeWorkspace(handoff?.workspace),
    };
    const slug = captured.workspace || (searchParams ? resolveWorkspaceAlias(searchParams) : '');
    if (slug) setWorkspace(slug);
    if (captured.email) setEmail(captured.email);
    if (captured.code) setCode(captured.code);
    if (captured.email && isWelcomeCode(captured.code)) {
      setStep('password');
      if (!fromFragment.code && handoff?.source === 'login') setInfo(t("That looks like a welcome code. Let's set your password."));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // The company's minimum length for the live tick. The server enforces the whole policy at save.
  const policySlug = normalizeWorkspace(workspace);
  useEffect(() => {
    let live = true;
    authApi.passwordPolicy(policySlug || undefined).then((n) => { if (live) setMinLength(n); });
    return () => { live = false; };
  }, [policySlug]);

  const checks = passwordChecks(password, email, minLength);
  const arabicLetters = hasArabicLetters(password);
  const workspaceArg = () => normalizeWorkspace(workspace) || undefined;

  const notMyEmail = () => {
    setEmail(''); setCode(''); setPassword('');
    setError(''); setInfo('');
    setStep('code');
  };

  const handleContinue = (e: React.FormEvent) => {
    e.preventDefault(); setError(''); setInfo('');
    if (!email.trim() || !email.includes('@')) { setError(t('Enter your work email.')); return; }
    if (!isWelcomeCode(code)) { setError(t('The welcome code is 8 digits. Check your sign-in slip.')); return; }
    if (showWorkspace && !workspace.trim()) { setError(t('Enter your company ID.')); return; }
    setCode(normalizeWelcomeCode(code));
    setStep('password');
  };

  /** Sign in with the password just saved, and go to the employee home. */
  const signIn = async (companyId?: string): Promise<boolean> => {
    try {
      const outcome = await login(email, password, companyId ?? workspaceArg());
      // A privileged login may need its second factor; the sign-in page owns that step and reads
      // the pending challenge from the same AuthProvider.
      if (outcome === 'authenticated') setSignedIn(true);
      else router.replace('/login');
      return true;
    } catch {
      return false;
    }
  };

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault();
    if (inFlight.current) return;
    setError(''); setInfo('');
    if (!checks.longEnough || !checks.notPersonal) { setError(t("Your password doesn't meet the rules above.")); return; }
    if (showWorkspace && !workspace.trim()) { setError(t('Enter your company ID.')); return; }

    inFlight.current = true;
    setBusy(true);
    try {
      let companyId = workspaceArg();
      if (!passwordSet) {
        const redeemed = await authApi.welcomeRedeem(email, code, password, workspaceArg());
        // The code is spent: drop it from memory now, whatever happens next.
        setCode('');
        setPasswordSet(true);
        // The language they set their password in is their choice from now on, so the first
        // app page (and every later visit) matches it.
        setLocale(locale);
        // The server says which company the code belonged to; sign in to exactly that one.
        if (redeemed.tenantSlug) { companyId = redeemed.tenantSlug; setWorkspace(redeemed.tenantSlug); }
      }
      if (!(await signIn(companyId))) {
        setInfo(t('Your password is saved. Sign in with your email and new password.'));
      }
    } catch (err: any) {
      const status = err?.response?.status;
      const reason = err?.response?.data?.code;
      if (status === 400 && reason === 'code_invalid') {
        // Show both values again so they can be checked against the slip.
        setStep('code');
        setCode(formatWelcomeCode(code));
        setError(t("Email and code don't match. Check both on your sign-in slip."));
      } else if (status === 400 && (reason === 'code_expired' || reason === 'code_used')) {
        setError(t('This code no longer works. It may have expired or already been used. Ask HR for a new one.'));
      } else if (status === 400 && reason === 'password_policy') {
        setError(t("Your password doesn't meet the rules above."));
      } else if (status === 400 && reason === 'workspace_required') {
        setShowWorkspace(true);
        setError(t("We couldn't find your company from your email. Enter your company ID. HR can tell you what it is."));
        setFocusWorkspace((n) => n + 1);
      } else if (status === 429 || (status === 400 && reason === 'try_later')) {
        setError(t('Too many tries. Wait a few minutes and try again.'));
      } else if (status === 400 && reason === 'sign_out_first') {
        setError(t('Sign out of KynexOne on this device first.'));
        setNeedsSignOut(true);
      } else if (status === 400 && reason === 'seat_limit') {
        setError(t("Your company's KynexOne plan is full. Ask HR."));
      } else if (!err?.response || status === 408 || status >= 500) {
        // The code is single-use and its answer was lost: it may have been saved. Signing in with
        // the new password settles it either way, without replaying the code into a "used" error.
        if (!(await signIn())) setError(t('Something went wrong. Please try again.'));
      } else {
        setError(t('Something went wrong. Please try again.'));
      }
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  };

  const loginHref = workspaceArg() ? `/login?workspace=${encodeURIComponent(workspaceArg()!)}` : '/login';

  return (
    <div className="tenant-login-shell lx-shell">
      <div className="lx-field-static" aria-hidden="true" />
      <div className="lx-page">
        <header className="lx-rail-top">
          <div className="lx-lockup">
            <div className="lx-lockup-mark"><Logo size="xl" collapsed theme="dark" /></div>
            <div className="lx-lockup-type">
              <span className="lx-wordmark">{WORDMARK.head}<em>{WORDMARK.tail}</em></span>
              <span className="lx-descriptor">{t('Payroll, HR and compliance')}</span>
            </div>
          </div>
        </header>

        {/* One column: this page has one job, so there is nothing beside the card. */}
        <main className="lx-stage lx-stage-solo">
          <div className="lx-slot">
            <div className="lx-pane">
              <div className="lx-card" dir={dir} data-testid="welcome-card">
                <SignInLanguageToggle />
                <div className="lx-head-block" aria-live="polite">
                  <p className="lx-kicker"><KeyRound aria-hidden />{t('First sign-in')}</p>
                  <h1 className="lx-title">{t('Welcome')}</h1>
                  {step === 'code' && <p className="lx-sub">{t('Enter your work email and the welcome code from your sign-in slip.')}</p>}
                </div>

                {step === 'code' && (
                  <form onSubmit={handleContinue} noValidate className="lx-form">
                    <Field label={t('Work email')} htmlFor="wc-email">
                      <input id="wc-email" type="email" dir="ltr" inputMode="email" value={email}
                        onChange={(event) => setEmail(event.target.value)}
                        className="lx-in" placeholder={EMAIL_PLACEHOLDER} autoComplete="username" autoCapitalize="none"
                        spellCheck={false} required />
                    </Field>
                    <Field label={t('Welcome code')} htmlFor="wc-code" hint={t('The 8-digit code on your sign-in slip.')}>
                      <input id="wc-code" type="text" dir="ltr" inputMode="numeric" autoComplete="one-time-code"
                        value={code} onChange={(event) => setCode(event.target.value)} maxLength={16}
                        className="lx-in lx-in-code" placeholder={CODE_PLACEHOLDER} required />
                    </Field>
                    {showWorkspace && <WorkspaceField t={t} value={workspace} onChange={setWorkspace} inputRef={workspaceRef} />}
                    <Feedback error={error} info={info} />
                    <button type="submit" className="lx-submit">{t('Continue')}</button>
                  </form>
                )}

                {step === 'password' && (
                  <form onSubmit={handleSave} noValidate className="lx-form">
                    {/* Who this is for. The email is the username they will sign in with from now
                        on; it reads left-to-right inside Arabic text. */}
                    <div className="lx-who">
                      <bdi dir="ltr" className="lx-who-email" data-testid="welcome-email">{email}</bdi>
                      <button type="button" className="lx-link" dir="auto" onClick={notMyEmail} data-testid="welcome-not-me">
                        {t('Not your email?')}
                      </button>
                    </div>
                    {/* For password managers: the username that goes with the new password. */}
                    <input type="email" className="lx-sr" value={email} autoComplete="username" readOnly
                      tabIndex={-1} aria-hidden="true" />

                    <Field label={t('Choose a password')} htmlFor="wc-pw">
                      {/* The whole box is LTR (not only the input), so the eye sits on the same side
                          as the padding that keeps the text clear of it, in Arabic too. */}
                      <span className="lx-inwrap" dir="ltr">
                        <input id="wc-pw" dir="ltr" type={showPw ? 'text' : 'password'} value={password}
                          onChange={(event) => setPassword(event.target.value)}
                          className="lx-in lx-in-pw" autoComplete="new-password" autoCapitalize="none"
                          spellCheck={false} autoFocus required aria-describedby="wc-pw-rules" />
                        <button type="button" onClick={() => setShowPw((shown) => !shown)}
                          className="lx-reveal" data-testid="welcome-password-toggle"
                          aria-label={showPw ? t('Hide password') : t('Show password')} aria-pressed={showPw}>
                          {showPw ? <EyeOff /> : <Eye />}
                        </button>
                      </span>
                    </Field>
                    <ul className="lx-pwchecks" id="wc-pw-rules" aria-live="polite">
                      <Tick ok={checks.longEnough} label={t('At least {n} characters', { n: minLength })} testId="tick-length" />
                      <Tick ok={checks.notPersonal} label={t("Doesn't contain your name or email")} testId="tick-personal" />
                    </ul>
                    {arabicLetters && (
                      <p className="lx-hint lx-hint-warn" data-testid="welcome-arabic-hint">
                        <Info aria-hidden />{t('Use English letters so you can sign in from any keyboard.')}
                      </p>
                    )}
                    {showWorkspace && <WorkspaceField t={t} value={workspace} onChange={setWorkspace} inputRef={workspaceRef} />}
                    <Feedback error={error} info={info} />
                    {needsSignOut && (
                      <button type="button" className="lx-alt" data-testid="welcome-sign-out"
                        onClick={async () => { await logout(); setNeedsSignOut(false); setError(''); }}>
                        {t('Sign out on this device')}
                      </button>
                    )}
                    {passwordSet && info ? (
                      <a className="lx-submit" href={loginHref}>{t('Sign in')}</a>
                    ) : (
                      <button type="submit" disabled={busy || !checks.longEnough || !checks.notPersonal}
                        aria-busy={busy} className="lx-submit">
                        {busy && <span className="lx-spin" aria-hidden />}
                        {busy ? t('Signing in…') : t('Save password and sign in')}
                      </button>
                    )}
                  </form>
                )}

                <div className="lx-card-foot lx-card-foot-stack">
                  <p className="lx-note">{t('Forgot your password? Ask HR for a new welcome code.')}</p>
                  <a className="lx-secondary" href={loginHref}>{t('I already have a password')}</a>
                </div>
              </div>
            </div>
          </div>
        </main>

        <footer className="lx-rail-bottom"><VendorFooter /></footer>
      </div>
    </div>
  );
}

function Field({ label, htmlFor, hint, children }: {
  label: string; htmlFor: string; hint?: string; children: React.ReactNode;
}) {
  return (
    <div className="lx-field">
      <div className="lx-legend"><label htmlFor={htmlFor}>{label}</label></div>
      {children}
      {hint && <p className="lx-hint">{hint}</p>}
    </div>
  );
}

function WorkspaceField({ t, value, onChange, inputRef }: {
  t: (key: string) => string; value: string; onChange: (value: string) => void;
  inputRef: React.RefObject<HTMLInputElement | null>;
}) {
  return (
    <Field label={t('Company ID')} htmlFor="wc-ws">
      <input id="wc-ws" ref={inputRef} type="text" dir="ltr" value={value} onChange={(event) => onChange(event.target.value)}
        className="lx-in lx-in-mono" placeholder={WORKSPACE_PLACEHOLDER} autoComplete="organization" autoCapitalize="none" required />
    </Field>
  );
}

/** A live check: a tick when met, an empty ring when not. The words carry the meaning, not the colour. */
function Tick({ ok, label, testId }: { ok: boolean; label: string; testId: string }) {
  return (
    <li className="lx-pwcheck" data-ok={ok ? 'true' : 'false'} data-testid={testId}>
      {ok ? <Check aria-hidden /> : <Circle aria-hidden />}
      <span>{label}</span>
    </li>
  );
}

function Feedback({ error, info }: { error: string; info: string }) {
  if (error) return (
    <div className="lx-fault" role="alert">
      <AlertCircle aria-hidden /><p>{error}</p>
    </div>
  );
  if (info) return (
    <div className="lx-ok" role="status">
      <CheckCircle2 aria-hidden /><p>{info}</p>
    </div>
  );
  return null;
}
