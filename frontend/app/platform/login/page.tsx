'use client';

import { useState, useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { AlertCircle, Eye, EyeOff, Lock, ShieldCheck, Activity, Users } from 'lucide-react';
import { platformApi, PLATFORM_PENDING_ENROLLMENT_KEY } from '@/src/api/platform';
import { Logo } from '@/src/components/Logo';
import { waitPhrase } from '@/src/lib/retryAfter';

type ErrorKind = 'invalid_credentials' | 'not_configured' | 'network' | 'busy' | 'rate_limited' | 'invalid_code' | 'enrollment_expired' | null;

/** credentials → (mfa | enroll). `enroll` is reached from sign-in once two-step sign-in is mandatory,
 *  or from the console's "set up now" prompt via sessionStorage. */
type Step = 'credentials' | 'mfa' | 'enroll' | 'codes';

function errorMessage(kind: ErrorKind, when = 'in a few seconds'): string {
  switch (kind) {
    case 'invalid_credentials': return 'Invalid platform admin credentials. Please check your email and password.';
    case 'not_configured': return 'Platform admin access is not configured on this server. Set PLATFORM_ADMIN_EMAIL and PLATFORM_ADMIN_PASSWORD environment variables.';
    case 'network': return 'Cannot reach the server. Check that the backend is running and reachable.';
    case 'busy': return `The sign-in service is busy — try again ${when}.`;
    case 'rate_limited': return `Too many sign-in attempts. Please try again ${when}.`;
    case 'invalid_code': return 'That code was not accepted. Check your authenticator app and try again.';
    case 'enrollment_expired': return 'This setup session has expired. Sign in again to restart setup.';
    default: return '';
  }
}

const CAPABILITIES = [
  { icon: Users, title: 'Tenant oversight', body: 'Provision, monitor, and manage every tenant from one control plane.' },
  { icon: Activity, title: 'Security & activity', body: 'Review login activity, sessions, and security events as they happen.' },
  { icon: ShieldCheck, title: 'Scoped operator access', body: 'Internal-only, role-based, and audit-logged — never crossing into tenant data.' },
];

const TRUST_BADGES = ['Audit logs', 'Tenant isolation', 'RBAC controls', 'MFA required'];

export default function PlatformLoginPage() {
  const router = useRouter();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [errorKind, setErrorKind] = useState<ErrorKind>(null);
  const [loading, setLoading] = useState(false);
  const [step, setStep] = useState<Step>('credentials');
  const [challengeToken, setChallengeToken] = useState('');
  const [enrollmentToken, setEnrollmentToken] = useState('');
  const [enrollmentSecret, setEnrollmentSecret] = useState('');
  const [enrollmentUri, setEnrollmentUri] = useState('');
  const [totpCode, setTotpCode] = useState('');
  const [info, setInfo] = useState('');
  const [retryWhen, setRetryWhen] = useState('in a few seconds');
  const [useRecoveryCode, setUseRecoveryCode] = useState(false);
  const [recoveryCode, setRecoveryCode] = useState('');
  const [recoveryCodes, setRecoveryCodes] = useState<string[]>([]);

  useEffect(() => {
    if (typeof window === 'undefined') return;
    let pending: string | null = null;
    try {
      pending = sessionStorage.getItem(PLATFORM_PENDING_ENROLLMENT_KEY);
      sessionStorage.removeItem(PLATFORM_PENDING_ENROLLMENT_KEY);
    } catch { /* storage unavailable */ }
    if (pending) { void beginEnrollment(pending); return; }
    if (localStorage.getItem('platform_access_token')) {
      router.replace('/platform/dashboard');
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [router]);

  async function beginEnrollment(token: string) {
    setStep('enroll');
    setEnrollmentToken(token);
    setEnrollmentSecret('');
    setEnrollmentUri('');
    setTotpCode('');
    setErrorKind(null);
    try {
      const { provisioningUri } = await platformApi.mfaEnrollmentSetup(token);
      setEnrollmentUri(provisioningUri);
      setEnrollmentSecret(new URL(provisioningUri).searchParams.get('secret') ?? '');
    } catch {
      setErrorKind('enrollment_expired');
    }
  }

  function backToCredentials() {
    setStep('credentials');
    setTotpCode('');
    setChallengeToken('');
    setEnrollmentToken('');
    setUseRecoveryCode(false);
    setRecoveryCode('');
  }

  async function handleMfa(e: React.FormEvent) {
    e.preventDefault();
    setErrorKind(null);
    setLoading(true);
    try {
      const { token } = useRecoveryCode
        ? await platformApi.mfaRecoveryVerify(challengeToken, recoveryCode)
        : await platformApi.mfaChallengeVerify(challengeToken, totpCode);
      localStorage.setItem('platform_access_token', token);
      router.replace('/platform/dashboard');
    } catch {
      setErrorKind('invalid_code');
    } finally {
      setLoading(false);
    }
  }

  async function handleEnroll(e: React.FormEvent) {
    e.preventDefault();
    setErrorKind(null);
    setLoading(true);
    try {
      const { recoveryCodes: codes } = await platformApi.mfaEnrollmentVerifySetup(enrollmentToken, enrollmentSecret, totpCode);
      setRecoveryCodes(codes ?? []);
      setStep('codes');
    } catch {
      setErrorKind('invalid_code');
    } finally {
      setLoading(false);
    }
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setErrorKind(null);
    setLoading(true);
    try {
      const res = await platformApi.login(email, password);
      if ('mfaRequired' in res && res.mfaRequired) {
        setChallengeToken(res.challengeToken);
        setTotpCode('');
        setStep('mfa');
        return;
      }
      if ('mfaEnrollmentRequired' in res && res.mfaEnrollmentRequired) {
        await beginEnrollment(res.enrollmentToken);
        return;
      }
      if (!('token' in res)) throw new Error('Unexpected sign-in response.');
      localStorage.setItem('platform_access_token', res.token);
      router.replace('/platform/dashboard');
    } catch (err: unknown) {
      const status = (err as { response?: { status?: number } })?.response?.status;
      if (status === 401) setErrorKind('invalid_credentials');
      else if (status === 503) setErrorKind('not_configured');
      else if (status === 429) {
        const res = (err as { response?: { data?: { error?: string }; headers?: Record<string, unknown> } })?.response;
        setRetryWhen(waitPhrase(res?.headers?.['retry-after']));
        setErrorKind(res?.data?.error === 'account_rate_limited' || res?.data?.error === 'ip_failure_budget' ? 'rate_limited' : 'busy');
      }
      else if (!(err as { response?: unknown })?.response) setErrorKind('network');
      else setErrorKind('invalid_credentials');
    } finally {
      setLoading(false);
    }
  }

  return (
    <>
      <style>{`
        @keyframes pa-fade { from { opacity: 0; transform: translateY(8px); } to { opacity: 1; transform: translateY(0); } }
        .pa-fade { animation: pa-fade 0.4s ease-out both; }
        @media (prefers-reduced-motion: reduce) { .pa-fade { animation: none !important; } }
      `}</style>

      <div className="grid min-h-[100svh] w-full lg:grid-cols-2">
        {/* ── Brand panel ─────────────────────────────────────────────── */}
        <section className="relative hidden flex-col justify-between overflow-hidden bg-[#060a17] px-12 py-12 text-white lg:flex">
          {/* Aurora + grid */}
          <div className="pointer-events-none absolute -left-1/4 -top-1/4 h-[70%] w-[70%] rounded-full bg-[radial-gradient(circle,rgba(47,107,255,0.45),transparent_60%)] blur-3xl" />
          <div className="pointer-events-none absolute bottom-[-20%] right-[-15%] h-[60%] w-[60%] rounded-full bg-[radial-gradient(circle,rgba(56,189,248,0.22),transparent_60%)] blur-3xl" />
          <div className="pointer-events-none absolute inset-0 opacity-[0.16] [background-image:radial-gradient(rgba(255,255,255,0.5)_1px,transparent_1px)] [background-size:26px_26px] [mask-image:radial-gradient(ellipse_at_center,black,transparent_75%)]" />
          <div className="pointer-events-none absolute inset-x-0 top-0 h-24 bg-gradient-to-b from-white/[0.06] to-transparent" />

          {/* Brand */}
          <div className="relative z-10 mx-auto flex w-full max-w-[440px] items-center gap-3">
            <div className="rounded-2xl border border-white/15 bg-white/[0.07] p-2.5 shadow-[0_8px_30px_rgba(8,18,55,0.5)] backdrop-blur-md">
              <Logo size="md" collapsed theme="dark" />
            </div>
            <div>
              <p className="text-sm font-bold tracking-tight text-white">KynexOne</p>
              <p className="text-xs text-slate-400">Platform Command Center</p>
            </div>
          </div>

          {/* Hero */}
          <div className="relative z-10 mx-auto w-full max-w-[440px]">
            <div className="inline-flex items-center gap-2 rounded-full border border-white/10 bg-white/[0.05] px-3 py-1.5 text-[11px] font-medium tracking-wide text-slate-300 backdrop-blur">
              <Lock className="h-3 w-3 text-sky-300" />
              Internal operators only
            </div>

            <h1 className="mt-5 text-[2rem] font-bold leading-[1.1] tracking-tight xl:text-[2.4rem]">
              Operate the platform with{' '}
              <span className="bg-gradient-to-r from-sky-300 via-blue-300 to-indigo-300 bg-clip-text text-transparent">
                calm precision.
              </span>
            </h1>
            <p className="mt-3.5 text-[14.5px] leading-relaxed text-slate-300/90">
              Monitor tenants, review login activity, and manage the operational surface area
              from one restrained, audit-first control plane.
            </p>

            <div className="mt-8 space-y-2.5">
              {CAPABILITIES.map((c) => (
                <div key={c.title} className="group flex gap-3 rounded-xl border border-transparent p-2.5 transition-colors hover:border-white/10 hover:bg-white/[0.04]">
                  <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-white/10 bg-white/[0.06] text-sky-300 transition-colors group-hover:bg-sky-400/15">
                    <c.icon className="h-4 w-4" aria-hidden />
                  </span>
                  <div>
                    <p className="text-sm font-semibold text-white">{c.title}</p>
                    <p className="mt-0.5 text-sm leading-relaxed text-slate-400">{c.body}</p>
                  </div>
                </div>
              ))}
            </div>

            <div className="mt-7 flex flex-wrap items-center gap-2">
              <ShieldCheck className="h-4 w-4 text-slate-400" aria-hidden />
              {TRUST_BADGES.map((b) => (
                <span key={b} className="rounded-full border border-white/10 bg-white/[0.04] px-2.5 py-1 text-[11px] font-medium text-slate-300">
                  {b}
                </span>
              ))}
            </div>
          </div>

          <p className="relative z-10 mx-auto w-full max-w-[440px] text-xs text-slate-500">
            A <span className="font-semibold text-slate-400">Kode Kinetics</span> product · internal platform operations
          </p>
        </section>

        {/* ── Form panel ──────────────────────────────────────────────── */}
        <section className="flex items-center justify-center bg-slate-50 px-5 py-10 dark:bg-[#0a0f1e] sm:px-8">
          <div className="pa-fade w-full max-w-[420px]">
            {/* Mobile brand */}
            <div className="mb-8 flex items-center gap-3 lg:hidden">
              <div className="rounded-xl border border-slate-200 bg-white p-2.5 shadow-sm dark:border-white/10 dark:bg-white/5">
                <Logo size="md" collapsed />
              </div>
              <div>
                <p className="text-sm font-bold tracking-tight text-slate-900 dark:text-white">KynexOne</p>
                <p className="text-xs text-slate-500 dark:text-slate-400">Platform Command Center</p>
              </div>
            </div>

            <div className="mb-2 inline-flex items-center gap-1.5 rounded-full border border-slate-200 bg-white px-2.5 py-1 text-[11px] font-semibold text-slate-500 dark:border-white/10 dark:bg-white/5 dark:text-slate-400">
              <Lock className="h-3 w-3" /> Platform admin
            </div>
            <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Sign in</h2>
            <p className="mt-1.5 text-sm text-slate-500 dark:text-slate-400">
              Enter your internal operator credentials to continue.
            </p>

            {step === 'mfa' && (
              <form onSubmit={handleMfa} className="mt-8 space-y-5">
                {useRecoveryCode ? (
                  <div>
                    <label htmlFor="platform-mfa-recovery" className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
                      Recovery code
                    </label>
                    <input id="platform-mfa-recovery" type="text" dir="ltr" autoComplete="off" autoFocus required
                      value={recoveryCode} onChange={e => setRecoveryCode(e.target.value)}
                      placeholder="XXXX-XXXX-XXXX-XXXX-XXXX" className="pa-input font-mono" />
                    <p className="mt-1.5 text-xs text-slate-500 dark:text-slate-400">Each recovery code works once.</p>
                  </div>
                ) : (
                  <div>
                    <label htmlFor="platform-mfa-code" className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
                      Authentication code
                    </label>
                    <input id="platform-mfa-code" type="text" inputMode="numeric" pattern="[0-9]{6}" maxLength={6} dir="ltr"
                      value={totpCode} onChange={e => setTotpCode(e.target.value.replace(/\D/g, ''))}
                      autoComplete="one-time-code" autoFocus required placeholder="000000" className="pa-input" />
                    <p className="mt-1.5 text-xs text-slate-500 dark:text-slate-400">Enter the 6-digit code from your authenticator app.</p>
                  </div>
                )}
                {errorKind && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{errorMessage(errorKind, retryWhen)}</p>}
                <button type="submit"
                  disabled={loading || (useRecoveryCode ? recoveryCode.replace(/[^A-Za-z0-9]/g, '').length !== 20 : totpCode.length !== 6)}
                  className="pa-btn disabled:cursor-not-allowed disabled:opacity-60">
                  {loading ? 'Verifying…' : 'Verify and sign in'}
                </button>
                <button type="button" onClick={() => { setUseRecoveryCode(v => !v); setErrorKind(null); }}
                  className="w-full text-sm text-slate-500 underline-offset-2 hover:underline dark:text-slate-400">
                  {useRecoveryCode ? 'Use my authenticator app instead' : 'Lost your device? Use a recovery code'}
                </button>
                <button type="button" onClick={backToCredentials} className="w-full text-sm text-slate-500 underline-offset-2 hover:underline dark:text-slate-400">
                  Back to sign in
                </button>
              </form>
            )}

            {step === 'enroll' && (
              <form onSubmit={handleEnroll} className="mt-8 space-y-5">
                <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
                  Two-step sign-in is required for platform operators. Add this account to your authenticator app, then enter the 6-digit code it shows.
                </div>
                {enrollmentSecret && (
                  <div>
                    <label htmlFor="platform-mfa-setup-key" className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
                      Setup key
                    </label>
                    <input id="platform-mfa-setup-key" readOnly value={enrollmentSecret} dir="ltr" className="pa-input font-mono text-xs" />
                  </div>
                )}
                {enrollmentUri && <p dir="ltr" className="break-all text-xs text-slate-500 dark:text-slate-400">{enrollmentUri}</p>}
                <div>
                  <label htmlFor="platform-mfa-enroll-code" className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
                    Authentication code
                  </label>
                  <input id="platform-mfa-enroll-code" type="text" inputMode="numeric" pattern="[0-9]{6}" maxLength={6} dir="ltr"
                    value={totpCode} onChange={e => setTotpCode(e.target.value.replace(/\D/g, ''))}
                    autoComplete="one-time-code" required placeholder="000000" className="pa-input" />
                </div>
                {errorKind && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{errorMessage(errorKind, retryWhen)}</p>}
                <button type="submit" disabled={loading || totpCode.length !== 6 || !enrollmentSecret} className="pa-btn disabled:cursor-not-allowed disabled:opacity-60">
                  {loading ? 'Enabling…' : 'Turn on two-step sign-in'}
                </button>
                <button type="button" onClick={backToCredentials} className="w-full text-sm text-slate-500 underline-offset-2 hover:underline dark:text-slate-400">
                  Back to sign in
                </button>
              </form>
            )}

            {step === 'codes' && (
              <div className="mt-8 space-y-5">
                <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-300">
                  Two-step sign-in is on. Save these recovery codes somewhere safe. Each one signs you in once if you lose your
                  authenticator. They are shown only now.
                </div>
                <ul dir="ltr" aria-label="Recovery codes" className="grid grid-cols-1 gap-2 sm:grid-cols-2 rounded-lg border border-slate-200 bg-white p-4 font-mono text-sm text-slate-800 dark:border-white/10 dark:bg-white/5 dark:text-slate-100">
                  {recoveryCodes.map(code => <li key={code}>{code}</li>)}
                </ul>
                <button type="button" className="pa-btn"
                  onClick={() => {
                    setRecoveryCodes([]);
                    backToCredentials();
                    setInfo('Sign in again with your password and a code from your app.');
                  }}>
                  I have saved my recovery codes
                </button>
              </div>
            )}

            {step === 'credentials' && info && (
              <p role="status" className="mt-6 rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-300">{info}</p>
            )}

            {step === 'credentials' && (
            <form onSubmit={handleSubmit} className="mt-8 space-y-5">
              <div>
                <label htmlFor="platform-email" className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
                  Email address
                </label>
                <input
                  id="platform-email"
                  type="email"
                  value={email}
                  onChange={e => setEmail(e.target.value)}
                  required
                  autoComplete="email"
                  placeholder="admin@example.com"
                  className="pa-input"
                />
              </div>

              <div>
                <div className="mb-1.5 flex items-center justify-between">
                  <label htmlFor="platform-password" className="block text-sm font-medium text-slate-700 dark:text-slate-300">
                    Password
                  </label>
                  <span className="text-xs text-slate-500 dark:text-slate-400">Not for tenant users</span>
                </div>
                <div className="relative">
                  <input
                    id="platform-password"
                    type={showPassword ? 'text' : 'password'}
                    value={password}
                    onChange={e => setPassword(e.target.value)}
                    required
                    autoComplete="current-password"
                    placeholder="••••••••"
                    className="pa-input pe-11"
                  />
                  <button
                    type="button"
                    onClick={() => setShowPassword(v => !v)}
                    tabIndex={-1}
                    aria-label={showPassword ? 'Hide password' : 'Show password'}
                    className="absolute end-3 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-600 dark:hover:text-slate-300"
                  >
                    {showPassword ? <EyeOff className="h-[18px] w-[18px]" /> : <Eye className="h-[18px] w-[18px]" />}
                  </button>
                </div>
              </div>

              {errorKind && (
                <div className="flex items-start gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 dark:border-red-500/20 dark:bg-red-500/[0.08]">
                  <AlertCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-500" />
                  <p className="text-sm leading-relaxed text-red-700 dark:text-red-400">{errorMessage(errorKind, retryWhen)}</p>
                </div>
              )}

              <button type="submit" disabled={loading} className="pa-btn disabled:cursor-not-allowed disabled:opacity-60">
                {loading
                  ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                  : 'Sign in'}
              </button>
            </form>
            )}

            <div className="mt-8 flex flex-wrap items-center gap-2 border-t border-slate-200 pt-6 dark:border-white/10">
              <ShieldCheck className="h-4 w-4 text-slate-400" aria-hidden />
              {['Audit-logged', 'MFA-aware', 'Tenant-safe'].map((pill) => (
                <span key={pill} className="text-xs font-medium text-slate-500 after:mx-1.5 after:text-slate-300 after:content-['·'] last:after:content-[''] dark:text-slate-400">
                  {pill}
                </span>
              ))}
            </div>

            <p className="mt-6 text-center text-sm text-slate-500 dark:text-slate-400">
              Tenant workspace login?{' '}
              <a href="/login" className="font-medium text-blue-700 underline underline-offset-2 hover:text-blue-800 dark:text-sky-400 dark:hover:text-sky-300">
                Sign in here
              </a>
            </p>
          </div>
        </section>
      </div>

      <style>{`
        .pa-input {
          display: block; width: 100%;
          border-radius: 10px;
          border: 1px solid rgb(203 213 225);
          background: #ffffff;
          padding: 11px 14px;
          font-size: 14px;
          color: #0f172a;
          transition: border-color 0.15s, box-shadow 0.15s;
          outline: none;
        }
        .pa-input::placeholder { color: #94a3b8; }
        .pa-input:focus { border-color: #2f6bff; box-shadow: 0 0 0 3px rgba(47,107,255,0.14); }
        /* Dark follows the .dark class like every dark: utility on this page (tailwind darkMode:
           'class'). A prefers-color-scheme query here painted light text on the still-light form
           for anyone whose OS is in dark mode: 1.04:1, the typed email/code all but invisible. */
        .dark .pa-input { background: rgba(255,255,255,0.04); border-color: rgba(255,255,255,0.12); color: #f1f5f9; }
        .dark .pa-input::placeholder { color: rgba(255,255,255,0.28); }
        .dark .pa-input:focus { border-color: #5eebff; box-shadow: 0 0 0 3px rgba(94,235,255,0.16); }
        .pa-btn {
          position: relative; display: flex; align-items: center; justify-content: center; gap: 8px;
          width: 100%; overflow: hidden; border-radius: 10px;
          background: linear-gradient(180deg, #3b78ff 0%, #2f6bff 55%, #1f54e6 100%);
          padding: 12px 20px; font-size: 14px; font-weight: 600; color: white;
          box-shadow: 0 1px 0 rgba(255,255,255,0.25) inset, 0 8px 20px rgba(31,84,230,0.30);
          transition: box-shadow 0.18s ease, transform 0.18s ease, filter 0.18s ease;
        }
        .pa-btn:hover:not(:disabled) { filter: brightness(1.04); transform: translateY(-1px); box-shadow: 0 1px 0 rgba(255,255,255,0.3) inset, 0 12px 26px rgba(31,84,230,0.42); }
        .pa-btn:active:not(:disabled) { transform: translateY(0); }
      `}</style>
    </>
  );
}
