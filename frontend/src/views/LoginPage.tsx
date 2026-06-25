'use client';

import { useEffect, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import {
  AlertCircle, CheckCircle2, Eye, EyeOff, KeyRound, Lock, Mail,
  ShieldCheck, Smartphone,
} from 'lucide-react';
import { useAuth } from '../contexts/AuthContext';
import { authApi } from '../api/auth';
import { Logo } from '../components/Logo';

const OPERATING_LANES = [
  'Workforce',
  'Payroll',
  'Compliance',
  'Dispatch',
  'Last mile',
  'Fleet',
];

const TRUST_MESSAGES = [
  'One operating layer across HR, payroll, compliance, dispatch, and delivery.',
  'Move from isolated approvals and trackers into one governed workspace.',
  'Give every operator, manager, and decision maker the same source of truth.',
];

const SECURITY_POINTS = [
  'Tenant isolation',
  'Audit logging',
  'MFA-ready access',
];

type Mode = 'login' | 'forgot' | 'reset' | 'mfa';

export function LoginPage() {
  const { login, verifyMfaChallenge, mfaPending } = useAuth();
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
  const [messageIndex, setMessageIndex] = useState(0);

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
      await login(email, password, tenantSlug);
      // If MFA is required, mfaPending is set in context; switch to MFA mode.
      if (mfaPending) { setMode('mfa'); return; }
      router.replace(from);
    }
    catch { setError('Invalid credentials. Check your email, password, and workspace.'); }
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
    const id = setInterval(() => setMessageIndex((s) => (s + 1) % TRUST_MESSAGES.length), 3400);
    return () => clearInterval(id);
  }, []);

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
      setInfo('Password updated. Sign in below.');
      setMode('login');
    } catch (err: any) { setError(err.response?.data?.message ?? 'Reset failed. Token may have expired.'); }
    finally { setLoading(false); }
  };

  const go = (m: Mode) => { setError(''); setInfo(''); setMode(m); if (m === 'forgot' && email) setForgotEmail(email); };

  return (
    <>
      <style>{`
        @keyframes auth-fade {
          from { opacity: 0; transform: translateY(8px); }
          to   { opacity: 1; transform: translateY(0); }
        }
        .auth-fade { animation: auth-fade 0.4s ease-out both; }
        @keyframes ambient-a {
          0%, 100% { transform: translate3d(0,0,0) scale(1); }
          50% { transform: translate3d(4%, 7%, 0) scale(1.12); }
        }
        @keyframes ambient-b {
          0%, 100% { transform: translate3d(0,0,0) scale(1.1); }
          50% { transform: translate3d(-7%, -6%, 0) scale(1); }
        }
        @keyframes ambient-c {
          0%, 100% { transform: translate3d(0,0,0) scale(1); }
          50% { transform: translate3d(5%, -7%, 0) scale(1.18); }
        }
        .ambient-a { animation: ambient-a 18s ease-in-out infinite; }
        .ambient-b { animation: ambient-b 24s ease-in-out infinite; }
        .ambient-c { animation: ambient-c 28s ease-in-out infinite; }
        .brand-spot {
          background: radial-gradient(540px circle at var(--mx, 36%) var(--my, 34%), rgba(90,164,255,0.18), transparent 60%);
          transition: background 0.18s ease-out;
        }
        @keyframes orbit-slow {
          0% { transform: rotate(0deg) translateY(-8px) rotate(0deg); }
          100% { transform: rotate(360deg) translateY(-8px) rotate(-360deg); }
        }
        @keyframes orbit-reverse {
          0% { transform: rotate(360deg) translateY(-10px) rotate(-360deg); }
          100% { transform: rotate(0deg) translateY(-10px) rotate(0deg); }
        }
        @keyframes pulse-ring {
          0%, 100% { transform: scale(1); opacity: 0.6; }
          50% { transform: scale(1.06); opacity: 1; }
        }
        @keyframes chip-float {
          0%, 100% { transform: translateY(0px); }
          50% { transform: translateY(-8px); }
        }
        .orbit-slow { animation: orbit-slow 22s linear infinite; }
        .orbit-reverse { animation: orbit-reverse 28s linear infinite; }
        .pulse-ring { animation: pulse-ring 4.6s ease-in-out infinite; }
        .chip-float { animation: chip-float 5.4s ease-in-out infinite; }
        @media (prefers-reduced-motion: reduce) {
          .auth-fade, .ambient-a, .ambient-b, .ambient-c, .orbit-slow, .orbit-reverse, .pulse-ring, .chip-float { animation: none !important; }
        }
      `}</style>

      <div className="grid min-h-[100svh] w-full overflow-hidden bg-[linear-gradient(135deg,#f7fbff_0%,#eef5ff_38%,#f9fbff_100%)] lg:grid-cols-[minmax(0,1.06fr)_minmax(420px,0.94fr)]">
        {/* ── Brand panel ───────────────────────────────────────────────── */}
        <section
          onMouseMove={(e) => {
            const r = e.currentTarget.getBoundingClientRect();
            e.currentTarget.style.setProperty('--mx', `${((e.clientX - r.left) / r.width) * 100}%`);
            e.currentTarget.style.setProperty('--my', `${((e.clientY - r.top) / r.height) * 100}%`);
          }}
          className="relative hidden items-center overflow-hidden px-10 py-12 lg:flex"
        >
          <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_top_left,rgba(66,153,255,0.28),transparent_34%),radial-gradient(circle_at_bottom_right,rgba(34,211,238,0.16),transparent_26%),linear-gradient(180deg,rgba(255,255,255,0.84),rgba(242,247,255,0.72))]" />
          <div className="pointer-events-none absolute -left-24 top-10 h-80 w-80 rounded-full bg-[radial-gradient(circle,rgba(54,112,255,0.24),transparent_64%)] blur-3xl ambient-a" />
          <div className="pointer-events-none absolute bottom-0 right-0 h-96 w-96 rounded-full bg-[radial-gradient(circle,rgba(76,201,240,0.20),transparent_66%)] blur-3xl ambient-b" />
          <div className="pointer-events-none absolute left-[22%] top-[18%] h-72 w-72 rounded-full bg-[radial-gradient(circle,rgba(15,23,42,0.06),transparent_70%)] blur-3xl ambient-c" />
          <div className="pointer-events-none absolute inset-0 opacity-[0.42] [background-image:linear-gradient(rgba(71,108,171,0.08)_1px,transparent_1px),linear-gradient(90deg,rgba(71,108,171,0.08)_1px,transparent_1px)] [background-size:28px_28px] [mask-image:radial-gradient(circle_at_center,black,transparent_84%)]" />
          {/* Cursor spotlight */}
          <div className="brand-spot pointer-events-none absolute inset-0" />

          <div className="relative z-10 flex w-full max-w-[560px] flex-1 flex-col justify-center">
            <div className="flex items-center gap-4">
              <div className="relative">
                <div className="pulse-ring absolute inset-[-12px] rounded-[24px] border border-blue-300/45 bg-white/35 blur-[0.4px]" />
                <div className="absolute inset-[-22px] rounded-[30px] bg-[radial-gradient(circle,rgba(59,130,246,0.22),transparent_70%)] blur-xl" />
                <div className="relative rounded-[22px] border border-white/85 bg-white/68 p-3 shadow-[0_28px_70px_rgba(37,99,235,0.18),inset_0_1px_0_rgba(255,255,255,0.95)] backdrop-blur-2xl">
                  <Logo size="xl" collapsed />
                </div>
              </div>
              <div>
                <p className="text-[29px] font-black tracking-[-0.04em] text-slate-950">
                  Kynex<span className="bg-gradient-to-r from-blue-600 via-sky-500 to-cyan-500 bg-clip-text text-transparent">One</span>
                </p>
                <p className="text-[13px] font-medium tracking-[0.22em] text-slate-500">ENTERPRISE WORKFORCE PLATFORM</p>
              </div>
            </div>

            <div className="mt-8 inline-flex w-fit items-center gap-2 rounded-full border border-white/75 bg-white/55 px-4 py-2 text-[11px] font-semibold uppercase tracking-[0.22em] text-blue-700 shadow-[0_18px_40px_rgba(15,23,42,0.08)] backdrop-blur-xl">
              <span className="relative flex h-2 w-2">
                <span className="absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-75 animate-ping" />
                <span className="relative inline-flex h-2 w-2 rounded-full bg-emerald-400" />
              </span>
              Unified operating access
            </div>

            <div className="mt-7 max-w-[520px]">
              <h1 className="text-[3rem] font-black leading-[0.96] tracking-[-0.05em] text-slate-950 xl:text-[3.55rem]">
                One secure entry point for
                <span className="mt-2 block bg-gradient-to-r from-blue-700 via-sky-500 to-cyan-500 bg-clip-text text-transparent">
                  workforce and field operations.
                </span>
              </h1>
              <p className="mt-5 max-w-[470px] text-[18px] leading-[1.65] text-slate-600">
                Stop switching between disconnected HR tools, payroll files, compliance trackers,
                dispatch boards, and delivery views. KynexOne brings the operating surface together
                in one controlled workspace.
              </p>
            </div>

            <div className="auth-fade mt-5 min-h-[52px] max-w-[500px] rounded-2xl border border-white/70 bg-white/52 px-5 py-4 text-[15px] leading-7 text-slate-600 shadow-[0_18px_50px_rgba(15,23,42,0.07)] backdrop-blur-xl">
              {TRUST_MESSAGES[messageIndex]}
            </div>

            <div className="relative mt-8 h-[265px] max-w-[540px] overflow-hidden rounded-[28px] border border-white/75 bg-[linear-gradient(145deg,rgba(255,255,255,0.78),rgba(240,247,255,0.56))] shadow-[0_28px_80px_rgba(37,99,235,0.12)] backdrop-blur-2xl">
              <div className="absolute inset-5 rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(245,249,255,0.92),rgba(231,240,255,0.74))]" />
              <div className="absolute left-1/2 top-1/2 h-[184px] w-[184px] -translate-x-1/2 -translate-y-1/2 rounded-full border border-blue-200/80 bg-[radial-gradient(circle,rgba(255,255,255,0.96),rgba(231,241,255,0.78))] shadow-[inset_0_1px_0_rgba(255,255,255,1),0_24px_60px_rgba(37,99,235,0.14)]" />
              <div className="orbit-slow absolute left-1/2 top-1/2 h-[244px] w-[244px] -translate-x-1/2 -translate-y-1/2 rounded-full border border-blue-200/60" />
              <div className="orbit-reverse absolute left-1/2 top-1/2 h-[194px] w-[194px] -translate-x-1/2 -translate-y-1/2 rounded-full border border-cyan-200/70" />
              <div className="absolute left-1/2 top-1/2 z-10 flex h-[184px] w-[184px] -translate-x-1/2 -translate-y-1/2 flex-col items-center justify-center">
                <div className="rounded-[26px] border border-white/90 bg-white/88 p-4 shadow-[0_20px_50px_rgba(37,99,235,0.16)]">
                  <Logo size="xl" collapsed />
                </div>
                <p className="mt-3 text-[12px] font-semibold uppercase tracking-[0.26em] text-slate-500">Control Layer</p>
              </div>

              {OPERATING_LANES.map((lane, index) => {
                const positions = [
                  'left-[11%] top-[18%]',
                  'right-[12%] top-[15%]',
                  'right-[9%] top-[47%]',
                  'right-[18%] bottom-[12%]',
                  'left-[17%] bottom-[10%]',
                  'left-[9%] top-[50%]',
                ];
                const delays = ['0s', '0.8s', '1.5s', '2.1s', '2.8s', '3.4s'];
                return (
                  <div
                    key={lane}
                    className={`chip-float absolute ${positions[index]} rounded-full border border-white/90 bg-white/78 px-4 py-2 text-[12px] font-semibold text-slate-700 shadow-[0_16px_40px_rgba(15,23,42,0.10)] backdrop-blur-xl`}
                    style={{ animationDelay: delays[index] }}
                  >
                    <span className="mr-2 inline-block h-2 w-2 rounded-full bg-gradient-to-r from-blue-500 to-cyan-400" />
                    {lane}
                  </div>
                );
              })}

              <div className="absolute left-[20%] top-[24%] h-px w-[24%] rotate-[11deg] bg-gradient-to-r from-blue-300/0 via-blue-300/55 to-blue-300/0" />
              <div className="absolute right-[19%] top-[24%] h-px w-[21%] -rotate-[10deg] bg-gradient-to-r from-cyan-300/0 via-cyan-300/55 to-cyan-300/0" />
              <div className="absolute right-[18%] top-[53%] h-px w-[18%] bg-gradient-to-r from-blue-300/0 via-blue-300/55 to-blue-300/0" />
              <div className="absolute right-[24%] bottom-[24%] h-px w-[16%] rotate-[18deg] bg-gradient-to-r from-cyan-300/0 via-cyan-300/55 to-cyan-300/0" />
              <div className="absolute left-[24%] bottom-[22%] h-px w-[18%] -rotate-[18deg] bg-gradient-to-r from-blue-300/0 via-blue-300/55 to-blue-300/0" />
              <div className="absolute left-[18%] top-[57%] h-px w-[16%] bg-gradient-to-r from-cyan-300/0 via-cyan-300/55 to-cyan-300/0" />
            </div>

            <div className="mt-7 flex max-w-[560px] flex-wrap gap-3">
              {[
                'Tenant-scoped access',
                'Governed approvals',
                'Audit-ready operations',
                'Shared data backbone',
              ].map((item) => (
                <span
                  key={item}
                  className="rounded-full border border-white/85 bg-white/60 px-4 py-2 text-[12px] font-semibold text-slate-600 shadow-[0_10px_30px_rgba(15,23,42,0.06)] backdrop-blur-lg"
                >
                  {item}
                </span>
              ))}
            </div>

            <p className="mt-8 text-sm text-slate-500">
              A <span className="font-semibold text-slate-700">Kode Kinetics</span> product
            </p>
          </div>
        </section>

        {/* ── Form panel ────────────────────────────────────────────────── */}
        <section className="relative flex items-center justify-center px-5 py-10 sm:px-8">
          <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_top,rgba(255,255,255,0.92),rgba(248,251,255,0.88)_35%,rgba(236,244,255,0.72))]" />
          <div className="auth-fade relative w-full max-w-[470px] rounded-[32px] border border-white/80 bg-white/62 p-8 shadow-[0_32px_90px_rgba(15,23,42,0.10)] backdrop-blur-2xl sm:p-10">
            {/* Mobile brand */}
            <div className="mb-8 flex items-center gap-3 lg:hidden">
              <div className="rounded-2xl border border-white/90 bg-white/80 p-3 shadow-[0_16px_40px_rgba(37,99,235,0.14)]">
                <Logo size="lg" collapsed />
              </div>
              <div>
                <p className="text-sm font-bold tracking-tight text-slate-900">KynexOne</p>
                <p className="text-xs text-slate-500">Enterprise Workforce Platform</p>
              </div>
            </div>

            {mode === 'login' && (
              <>
                <div className="mb-7 flex items-center justify-between gap-3">
                  <div className="rounded-full border border-emerald-200 bg-emerald-50/90 px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.2em] text-emerald-700">
                    Secure access
                  </div>
                  <div className="rounded-full border border-blue-100 bg-blue-50/90 px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.2em] text-blue-700">
                    Production workspace
                  </div>
                </div>
                <h2 className="text-[2.2rem] font-black tracking-[-0.04em] text-slate-950">Sign in</h2>
                <p className="mt-2 text-[15px] leading-7 text-slate-500">
                  Enter your work email, password, and workspace identifier to continue into your operational environment.
                </p>

                <form onSubmit={handleLogin} noValidate className="mt-8 space-y-5">
                  <FormField label="Work email">
                    <input id="li-em" type="email" value={email} onChange={e => setEmail(e.target.value)}
                      className="auth-input" placeholder="you@company.com" autoComplete="username" required />
                  </FormField>

                  <FormField label="Password" labelRight={
                    <button type="button" onClick={() => go('forgot')}
                      className="text-xs font-medium text-sapphire hover:text-blue-700 dark:text-sky-400">
                      Forgot password?
                    </button>
                  }>
                    <div className="relative">
                      <input id="li-pw" type={showPw ? 'text' : 'password'} value={password}
                        onChange={e => setPassword(e.target.value)}
                        className="auth-input pr-11" placeholder="••••••••••"
                        autoComplete="current-password" required />
                      <button type="button" onClick={() => setShowPw(v => !v)} tabIndex={-1}
                        className="absolute right-3 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-600 dark:hover:text-slate-300"
                        aria-label={showPw ? 'Hide password' : 'Show password'}>
                        {showPw ? <EyeOff className="h-[18px] w-[18px]" /> : <Eye className="h-[18px] w-[18px]" />}
                      </button>
                    </div>
                  </FormField>

                  <FormField
                    label="Workspace"
                    labelRight={tenantLocked
                      ? <span className="flex items-center gap-1 text-xs font-medium text-emerald-600 dark:text-emerald-400"><Lock className="h-3 w-3" />Auto-detected</span>
                      : tenantSlug ? <span className="text-xs text-slate-400">Pre-filled</span> : null}
                    hint="Your company or tenant workspace identifier"
                  >
                    <input id="li-ws" type="text" value={tenantSlug} onChange={e => setTenantSlug(e.target.value)}
                      className="auth-input" placeholder="your-workspace" autoComplete="organization" spellCheck={false} required />
                  </FormField>

                  <AuthFeedback error={error} info={info} />

                  <button type="submit" disabled={loading}
                    className="auth-btn disabled:cursor-not-allowed disabled:opacity-60">
                    {loading
                      ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                      : 'Sign in'}
                  </button>
                </form>
              </>
            )}

            {mode === 'forgot' && (
              <form onSubmit={handleForgot} noValidate className="space-y-5">
                <button type="button" onClick={() => go('login')}
                  className="flex items-center gap-1.5 text-sm font-medium text-slate-400 hover:text-slate-600 dark:hover:text-slate-300">
                  ← Back to sign in
                </button>
                <div className="flex h-11 w-11 items-center justify-center rounded-xl bg-sapphire/10 ring-1 ring-sapphire/20">
                  <Mail className="h-5 w-5 text-sapphire dark:text-sky-400" />
                </div>
                <div>
                  <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Reset password</h2>
                  <p className="mt-1.5 text-sm text-slate-500 dark:text-slate-400">
                    We&apos;ll send a reset code to your email address.
                  </p>
                </div>

                <FormField label="Work email">
                  <input id="fg-em" type="email" value={forgotEmail || email}
                    onChange={e => setForgotEmail(e.target.value)}
                    className="auth-input" placeholder="you@company.com" autoComplete="email" required />
                </FormField>
                <FormField label="Workspace" hint="Optional — helps locate your account">
                  <input id="fg-ws" type="text" value={tenantSlug}
                    onChange={e => setTenantSlug(e.target.value)}
                    className="auth-input" placeholder="your-workspace" />
                </FormField>

                <AuthFeedback error={error} info={info} />

                <button type="submit" disabled={loading} className="auth-btn disabled:cursor-not-allowed disabled:opacity-60">
                  {loading ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" /> : 'Send reset code'}
                </button>
              </form>
            )}

            {mode === 'reset' && (
              <form onSubmit={handleReset} noValidate className="space-y-5">
                <button type="button" onClick={() => go('forgot')}
                  className="flex items-center gap-1.5 text-sm font-medium text-slate-400 hover:text-slate-600 dark:hover:text-slate-300">
                  ← Back
                </button>
                <div className="flex h-11 w-11 items-center justify-center rounded-xl bg-sapphire/10 ring-1 ring-sapphire/20">
                  <KeyRound className="h-5 w-5 text-sapphire dark:text-sky-400" />
                </div>
                <div>
                  <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">New password</h2>
                  <p className="mt-1.5 text-sm text-slate-500 dark:text-slate-400">Enter the code from your email and set a new password.</p>
                </div>

                <FormField label="Work email">
                  <input id="rs-em" type="email" value={forgotEmail || email}
                    onChange={e => setForgotEmail(e.target.value)}
                    className="auth-input" placeholder="you@company.com" autoComplete="email" required />
                </FormField>
                <FormField label="Reset code">
                  <input id="rs-tk" type="text" value={resetToken} onChange={e => setResetToken(e.target.value)}
                    className="auth-input font-mono tracking-wider" placeholder="Paste code from email" required />
                </FormField>
                <FormField label="New password" hint="Minimum 10 characters">
                  <input id="rs-pw" type="password" value={newPw} onChange={e => setNewPw(e.target.value)}
                    className="auth-input" placeholder="••••••••••" autoComplete="new-password" required />
                </FormField>
                <FormField label="Confirm password">
                  <input id="rs-cf" type="password" value={confirmPw} onChange={e => setConfirmPw(e.target.value)}
                    className="auth-input" placeholder="••••••••••" autoComplete="new-password" required />
                </FormField>

                <AuthFeedback error={error} info={info} />

                <button type="submit" disabled={loading} className="auth-btn disabled:cursor-not-allowed disabled:opacity-60">
                  {loading ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" /> : 'Update password'}
                </button>
              </form>
            )}

            {mode === 'mfa' && (
              <form onSubmit={handleMfa} noValidate className="space-y-5">
                <button type="button" onClick={() => { setMode('login'); setTotpCode(''); }}
                  className="flex items-center gap-1.5 text-sm font-medium text-slate-400 hover:text-slate-600 dark:hover:text-slate-300">
                  ← Back to sign in
                </button>
                <div className="flex h-11 w-11 items-center justify-center rounded-xl bg-sapphire/10 ring-1 ring-sapphire/20">
                  <Smartphone className="h-5 w-5 text-sapphire dark:text-sky-400" />
                </div>
                <div>
                  <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Two-factor authentication</h2>
                  <p className="mt-1.5 text-sm text-slate-500 dark:text-slate-400">
                    Enter the 6-digit code from your authenticator app.
                  </p>
                </div>

                <FormField label="Authentication code">
                  <input
                    id="mfa-code"
                    type="text"
                    inputMode="numeric"
                    pattern="[0-9]{6}"
                    maxLength={6}
                    value={totpCode}
                    onChange={e => setTotpCode(e.target.value.replace(/\D/g, ''))}
                    className="auth-input text-center font-mono text-xl tracking-[0.3em]"
                    placeholder="000000"
                    autoComplete="one-time-code"
                    autoFocus
                    required
                  />
                </FormField>

                <AuthFeedback error={error} info={info} />

                <button type="submit" disabled={loading || totpCode.length !== 6}
                  className="auth-btn disabled:cursor-not-allowed disabled:opacity-60">
                  {loading
                    ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                    : 'Verify'}
                </button>
              </form>
            )}

            {/* Trust row */}
            <div className="mt-8 flex flex-wrap items-center gap-2 border-t border-slate-200 pt-6 dark:border-white/10">
              <ShieldCheck className="h-4 w-4 text-slate-400" aria-hidden />
              {SECURITY_POINTS.map((point) => (
                <span key={point} className="text-xs font-medium text-slate-400 after:mx-1.5 after:text-slate-300 after:content-['·'] last:after:content-['']">
                  {point}
                </span>
              ))}
            </div>

            <p className="mt-6 text-center text-xs text-slate-400 dark:text-slate-500">
              By signing in you agree to our{' '}
              <a href="/privacy" target="_blank" rel="noopener noreferrer"
                className="underline underline-offset-2 hover:text-slate-600 dark:hover:text-slate-300">
                Privacy Policy
              </a>
              {' '}and{' '}
              <a href="/terms" target="_blank" rel="noopener noreferrer"
                className="underline underline-offset-2 hover:text-slate-600 dark:hover:text-slate-300">
                Terms of Service
              </a>
            </p>
          </div>
        </section>
      </div>

      <style>{`
        .auth-input {
          display: block; width: 100%;
          border-radius: 16px;
          border: 1px solid rgba(186, 200, 228, 0.92);
          background: rgba(255,255,255,0.82);
          padding: 13px 16px;
          font-size: 15px;
          color: #0f172a;
          box-shadow: inset 0 1px 0 rgba(255,255,255,0.88);
          transition: border-color 0.15s, box-shadow 0.15s, background 0.15s;
          outline: none;
        }
        .auth-input::placeholder { color: #94a3b8; }
        .auth-input:focus {
          border-color: #2f6bff;
          background: rgba(255,255,255,0.95);
          box-shadow: 0 0 0 4px rgba(47, 107, 255, 0.12), inset 0 1px 0 rgba(255,255,255,0.95);
        }
        .auth-input:-webkit-autofill,
        .auth-input:-webkit-autofill:hover,
        .auth-input:-webkit-autofill:focus {
          -webkit-text-fill-color: #0f172a;
          -webkit-box-shadow: 0 0 0px 1000px rgba(255, 252, 214, 0.12) inset;
          box-shadow: 0 0 0px 1000px rgba(255, 255, 255, 0.84) inset;
          transition: background-color 99999s ease-in-out 0s;
        }
        .auth-btn {
          position: relative;
          display: flex; align-items: center; justify-content: center; gap: 8px;
          width: 100%;
          overflow: hidden;
          border-radius: 18px;
          background: linear-gradient(135deg, #1f5eff 0%, #3b82f6 52%, #4cc9f0 100%);
          padding: 14px 20px;
          font-size: 15px;
          font-weight: 700;
          color: white;
          letter-spacing: -0.01em;
          box-shadow: 0 1px 0 rgba(255,255,255,0.25) inset, 0 18px 34px rgba(31, 84, 230, 0.28);
          transition: box-shadow 0.18s ease, transform 0.18s ease, filter 0.18s ease;
        }
        /* hover sheen sweep */
        .auth-btn::after {
          content: '';
          position: absolute; top: 0; bottom: 0; left: -60%;
          width: 45%;
          background: linear-gradient(100deg, transparent, rgba(255,255,255,0.45), transparent);
          transform: skewX(-18deg);
          transition: left 0.6s cubic-bezier(.2,.7,.2,1);
        }
        .auth-btn:hover:not(:disabled) {
          filter: brightness(1.04);
          box-shadow: 0 1px 0 rgba(255,255,255,0.3) inset, 0 22px 42px rgba(31, 84, 230, 0.34);
          transform: translateY(-1px);
        }
        .auth-btn:hover:not(:disabled)::after { left: 120%; }
        .auth-btn:active:not(:disabled) { transform: translateY(0); box-shadow: 0 1px 0 rgba(255,255,255,0.2) inset, 0 6px 16px rgba(31, 84, 230, 0.32); }
        @media (prefers-reduced-motion: reduce) {
          .auth-btn::after { display: none; }
          .auth-btn:hover:not(:disabled) { transform: none; }
        }
      `}</style>
    </>
  );
}

// ── File-local sub-components ─────────────────────────────────────────────────

function FormField({ label, labelRight, hint, children }: {
  label: string;
  labelRight?: React.ReactNode;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <div className="mb-1.5 flex items-center justify-between">
        <p className="text-sm font-medium text-slate-700 dark:text-slate-300">{label}</p>
        {labelRight}
      </div>
      {children}
      {hint && <p className="mt-1.5 text-xs leading-relaxed text-slate-400 dark:text-slate-500">{hint}</p>}
    </div>
  );
}

function AuthFeedback({ error, info }: { error: string; info: string }) {
  if (error) return (
    <div className="flex items-start gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 dark:border-red-500/20 dark:bg-red-500/[0.08]">
      <AlertCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-500" />
      <p className="text-sm leading-relaxed text-red-700 dark:text-red-400">{error}</p>
    </div>
  );
  if (info) return (
    <div className="flex items-start gap-3 rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 dark:border-emerald-500/20 dark:bg-emerald-500/[0.08]">
      <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-500" />
      <p className="text-sm leading-relaxed text-emerald-700 dark:text-emerald-400">{info}</p>
    </div>
  );
  return null;
}
