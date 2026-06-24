'use client';

import { useEffect, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import {
  AlertCircle, CheckCircle2, Eye, EyeOff, KeyRound, Lock, Mail, Smartphone,
} from 'lucide-react';
import { useAuth } from '../contexts/AuthContext';
import { authApi } from '../api/auth';
import { Logo } from '../components/Logo';

const SECURITY_POINTS = [
  'Tenant isolation',
  'Audit logging',
  'MFA-ready access',
];

const TRUST_POINTS = [
  { label: 'People ops', value: 'Attendance, leave, onboarding' },
  { label: 'Payroll', value: 'Runs, approvals, pay cycles' },
  { label: 'Control', value: 'MFA, audit trails, tenant isolation' },
];

const WORKFORCE_SIGNALS = [
  'Clock-ins',
  'Approvals',
  'Payroll',
  'Compliance',
];

const WORKFORCE_FLOW = [
  'Clock in',
  'Approve',
  'Run payroll',
  'Audit',
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
    const parts = window.location.hostname.split('.');
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
        @keyframes kx-float {
          0%, 100% { transform: translateY(0px); }
          50% { transform: translateY(-10px); }
        }
        @keyframes kx-drift {
          0%, 100% { transform: translate3d(0, 0, 0) rotate(0deg); }
          50% { transform: translate3d(10px, -8px, 0) rotate(5deg); }
        }
        @keyframes kx-panel-in {
          from { opacity: 0; transform: translateY(16px); }
          to { opacity: 1; transform: translateY(0); }
        }
        @keyframes kx-sheen {
          0% { transform: translateX(-40%); opacity: 0; }
          25% { opacity: 0.45; }
          100% { transform: translateX(40%); opacity: 0; }
        }
        @keyframes kx-orbit {
          from { transform: rotate(0deg) translateX(36px) rotate(0deg); }
          to { transform: rotate(360deg) translateX(36px) rotate(-360deg); }
        }
        @keyframes kx-rise {
          0%, 100% { transform: scaleY(0.84); opacity: 0.72; }
          50% { transform: scaleY(1); opacity: 1; }
        }
        @keyframes kx-glow {
          0%, 100% { opacity: 0.28; transform: scale(1); }
          50% { opacity: 0.52; transform: scale(1.06); }
        }
        @keyframes kx-line {
          0%, 100% { transform: scaleX(0.85); opacity: 0.55; }
          50% { transform: scaleX(1); opacity: 1; }
        }
        @keyframes kx-flow {
          0%, 100% { transform: translateX(-8px); opacity: 0.55; }
          50% { transform: translateX(8px); opacity: 1; }
        }
        @keyframes kx-node {
          0%, 100% { transform: scale(0.96); opacity: 0.72; }
          50% { transform: scale(1); opacity: 1; }
        }
        .kx-float { animation: kx-float 14s ease-in-out infinite; }
        .kx-drift { animation: kx-drift 18s ease-in-out infinite; }
        .kx-panel-in { animation: kx-panel-in 0.65s ease-out both; }
        .kx-sheen { animation: kx-sheen 8s ease-in-out infinite; }
        .kx-orbit { animation: kx-orbit 24s linear infinite; }
        .kx-rise { animation: kx-rise 2.8s ease-in-out infinite; transform-origin: bottom; }
        .kx-glow { animation: kx-glow 10s ease-in-out infinite; }
        .kx-line { animation: kx-line 10s ease-in-out infinite; transform-origin: center; }
        .kx-flow { animation: kx-flow 12s ease-in-out infinite; }
        .kx-node { animation: kx-node 3.6s ease-in-out infinite; }
      `}</style>

      <div className="relative min-h-[100svh] w-full overflow-hidden bg-[#eef2ff] text-slate-900 dark:bg-[#040814] dark:text-white">
        <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.16),transparent_28%),radial-gradient(circle_at_85%_20%,rgba(94,235,255,0.10),transparent_22%),linear-gradient(180deg,rgba(255,255,255,0.5),transparent_35%)] dark:bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.18),transparent_28%),radial-gradient(circle_at_85%_20%,rgba(94,235,255,0.08),transparent_22%),linear-gradient(180deg,rgba(6,11,24,0.9),rgba(4,8,20,0.98))]" />
        <div className="pointer-events-none absolute inset-0 opacity-[0.22] mix-blend-soft-light [background-image:linear-gradient(rgba(47,107,255,0.08)_1px,transparent_1px),linear-gradient(90deg,rgba(47,107,255,0.08)_1px,transparent_1px)] [background-size:72px_72px]" />
        <div className="pointer-events-none absolute inset-x-0 top-1/2 h-px bg-[linear-gradient(90deg,transparent,rgba(47,107,255,0.24),rgba(94,235,255,0.28),transparent)] opacity-45 kx-line" />
        <div className="pointer-events-none absolute left-[10%] top-[14%] h-60 w-60 rounded-full bg-white/28 blur-3xl kx-drift" />
        <div className="pointer-events-none absolute right-[12%] bottom-[16%] h-64 w-64 rounded-full bg-cyan-200/12 blur-3xl kx-float" />
        <div className="pointer-events-none absolute inset-y-0 left-1/3 w-px bg-gradient-to-b from-transparent via-blue-300/14 to-transparent opacity-45" />
        <div className="pointer-events-none absolute left-0 top-1/4 h-56 w-56 rounded-full bg-blue-300/14 blur-3xl dark:bg-blue-700/8 kx-float" />
        <div className="pointer-events-none absolute bottom-[-4rem] right-[-5rem] h-64 w-64 rounded-full bg-cyan-300/10 blur-3xl dark:bg-cyan-600/8 kx-float" />

        <div className="relative z-10 grid min-h-[100svh] w-full lg:grid-cols-[0.98fr_1.02fr]">
          <section className="relative flex min-h-[34svh] flex-col overflow-hidden bg-[linear-gradient(160deg,#fbfdff_0%,#f4f8ff_42%,#eaf2ff_100%)] px-5 py-4 text-slate-900 sm:px-8 sm:py-5 lg:min-h-[100svh] lg:px-10 lg:py-6">
            <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_18%_18%,rgba(47,107,255,0.18),transparent_26%),radial-gradient(circle_at_88%_14%,rgba(94,235,255,0.12),transparent_18%),radial-gradient(circle_at_50%_100%,rgba(56,189,248,0.10),transparent_22%)]" />
            <div className="pointer-events-none absolute inset-0 opacity-[0.16] [background-image:linear-gradient(rgba(47,107,255,0.07)_1px,transparent_1px),linear-gradient(90deg,rgba(47,107,255,0.07)_1px,transparent_1px)] [background-size:72px_72px]" />
            <div className="pointer-events-none absolute -right-10 top-10 h-28 w-28 rounded-full border border-cyan-300/20 bg-cyan-300/12 blur-2xl kx-orbit" />
            <div className="pointer-events-none absolute -left-12 bottom-16 h-36 w-36 rounded-full bg-blue-500/12 blur-3xl kx-glow" />

            <div className="relative z-10 flex items-center gap-3">
              <div className="rounded-[22px] border border-white/90 bg-white/88 px-5 py-4 shadow-[0_20px_46px_rgba(37,99,235,0.12)] backdrop-blur-xl">
                <Logo size="xl" />
              </div>
              <div className="h-9 w-px bg-slate-300/60" />
              <div>
                <p className="text-[10px] font-bold tracking-[0.28em] uppercase text-blue-500/70">
                  KynexOne
                </p>
                <p className="mt-1 text-[11px] font-semibold tracking-[0.22em] uppercase text-slate-500">
                  Enterprise workforce platform
                </p>
              </div>
            </div>

            <div className="flex flex-1 items-center justify-center">
              <div className="relative z-10 max-w-[560px] space-y-3">
                <div className="inline-flex items-center gap-2 rounded-full border border-blue-300/30 bg-white/78 px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.24em] text-blue-600 shadow-sm backdrop-blur">
                  <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
                  Secure workspace access
                </div>
                <div>
                  <h1 className="max-w-[540px] text-[38px] font-black leading-[1.03] tracking-tight text-slate-950 xl:text-[45px]">
                    Sign in to the<br />
                    <span className="bg-gradient-to-r from-blue-600 via-sky-500 to-cyan-400 bg-clip-text text-transparent">
                      workforce command center.
                    </span>
                  </h1>
                  <p className="mt-2 max-w-[500px] text-[14px] leading-relaxed text-slate-600">
                    Attendance, leave, payroll, approvals, and compliance in a tenant-isolated workspace with production-grade controls.
                  </p>
                </div>

                <div className="grid gap-2 sm:grid-cols-3">
                  {TRUST_POINTS.map((item) => (
                    <div key={item.label} className="rounded-2xl border border-white/80 bg-white/78 px-4 py-3 shadow-[0_10px_20px_rgba(37,99,235,0.05)] backdrop-blur-xl">
                      <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-slate-400">{item.label}</p>
                      <p className="mt-1.5 text-[12px] leading-snug text-slate-700">{item.value}</p>
                    </div>
                  ))}
                </div>

                <div className="rounded-[24px] border border-slate-200/80 bg-white/72 p-3 shadow-[0_14px_28px_rgba(37,99,235,0.06)] backdrop-blur-xl">
                  <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-slate-400">Core controls</p>
                  <div className="mt-3 flex flex-wrap gap-2">
                    {WORKFORCE_SIGNALS.map((signal) => (
                      <span
                        key={signal}
                        className="rounded-full border border-slate-200/80 bg-white px-3 py-1.5 text-[11px] font-medium text-slate-600"
                      >
                        {signal}
                      </span>
                    ))}
                  </div>
                </div>

                <div className="rounded-[24px] border border-slate-200/80 bg-[linear-gradient(180deg,rgba(255,255,255,0.84),rgba(244,248,255,0.72))] p-3 shadow-[0_14px_26px_rgba(37,99,235,0.05)] backdrop-blur-xl">
                  <div className="flex items-center justify-between gap-3">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-slate-400">Operational flow</p>
                    <span className="rounded-full border border-cyan-200/80 bg-cyan-50 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-[0.2em] text-cyan-600">
                      Live
                    </span>
                  </div>
                  <div className="kx-flow mt-3 flex items-center gap-2">
                    {WORKFORCE_FLOW.map((item, index) => (
                      <div key={item} className="flex min-w-0 flex-1 items-center gap-2">
                        <div className="kx-node flex h-8 w-8 shrink-0 items-center justify-center rounded-full border border-blue-200/80 bg-white text-[10px] font-bold text-blue-600 shadow-sm">
                          {index + 1}
                        </div>
                        <div className="min-w-0">
                          <p className="text-[11px] font-semibold text-slate-700">{item}</p>
                          <p className="truncate text-[10px] text-slate-400">
                            {index === 0 ? 'Capture' : index === 1 ? 'Route' : index === 2 ? 'Process' : 'Review'}
                          </p>
                        </div>
                        {index < WORKFORCE_FLOW.length - 1 && (
                          <div className="hidden h-px flex-1 bg-gradient-to-r from-blue-200 via-cyan-200 to-transparent sm:block" />
                        )}
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            </div>

            <p className="relative z-10 mt-auto pt-4 text-[11px] text-slate-500">
              A <span className="font-semibold text-slate-600">Kode Kinetics</span> product · secure access only
            </p>
          </section>

          <section className="relative flex min-h-[66svh] items-stretch justify-center px-3 py-3 sm:px-5 sm:py-5 lg:min-h-[100svh] lg:px-6 lg:py-4">
            <div className="pointer-events-none absolute -left-8 top-6 h-40 w-40 rounded-full border border-white/30 bg-white/40 blur-3xl dark:border-white/10 dark:bg-white/5" />
            <div className="pointer-events-none absolute right-2 top-2 h-16 w-16 rounded-full border border-cyan-300/20 bg-cyan-300/10 blur-2xl dark:bg-cyan-500/10" />

            <div className="relative z-10 flex w-full max-w-[560px] items-stretch">
              <div className="relative flex min-h-full w-full flex-col rounded-[30px] border border-white/85 bg-[rgba(250,252,255,0.56)] p-[1px] shadow-[0_24px_80px_rgba(37,99,235,0.12),0_0_0_1px_rgba(255,255,255,0.72)] backdrop-blur-3xl dark:border-white/[0.10] dark:bg-white/[0.04] dark:shadow-[0_24px_80px_rgba(0,0,0,0.52),0_0_0_1px_rgba(255,255,255,0.04)] kx-panel-in">
              <div className="relative flex min-h-full flex-1 flex-col overflow-hidden rounded-[29px] bg-[linear-gradient(180deg,rgba(255,255,255,0.72),rgba(247,249,255,0.48))] px-5 py-5 sm:px-8 sm:py-8 dark:bg-[linear-gradient(180deg,rgba(9,16,32,0.94),rgba(7,12,24,0.90))]">
                <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_20%_0%,rgba(255,255,255,0.88),transparent_34%),linear-gradient(135deg,rgba(47,107,255,0.08),transparent_42%,rgba(94,235,255,0.05))]" />
                <div className="pointer-events-none absolute inset-y-0 left-0 w-20 bg-gradient-to-r from-white/30 to-transparent dark:from-white/[0.04] kx-sheen" />

                <div className="relative flex flex-1 flex-col">
                  <div className="mb-5 flex items-center justify-between gap-3">
                    <div className="inline-flex items-center gap-2 rounded-full border border-blue-500/15 bg-blue-500/8 px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.24em] text-blue-600 dark:border-sapphire/25 dark:bg-sapphire/10 dark:text-cyanAccent">
                      <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
                      KynexOne secure access
                    </div>
                    <div className="hidden rounded-full border border-emerald-500/15 bg-emerald-500/10 px-3 py-1 text-[10px] font-semibold uppercase tracking-[0.22em] text-emerald-600 dark:text-emerald-400 sm:block">
                      Production
                    </div>
                  </div>

                  <h2 className="mb-2 text-[30px] font-black tracking-tight text-slate-950 dark:text-white sm:text-[34px]">
                    Access your workspace
                  </h2>
                  <p className="mb-6 max-w-[340px] text-[14px] leading-relaxed text-slate-500 dark:text-slate-400">
                    Use your work email, password, and workspace slug to enter the tenant-bound workspace.
                  </p>

                  {mode === 'login' && (
                    <form onSubmit={handleLogin} noValidate className="space-y-5">
                      <FormField label="Work Email">
                        <input id="li-em" type="email" value={email} onChange={e => setEmail(e.target.value)}
                          className="auth-input" placeholder="you@company.com" autoComplete="email" required />
                      </FormField>

                      <FormField label="Password" labelRight={
                        <button type="button" onClick={() => go('forgot')}
                          className="text-[11px] font-medium text-blue-500 hover:text-blue-600 dark:text-blue-400">
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
                            aria-label={showPw ? 'Hide' : 'Show'}>
                            {showPw ? <EyeOff className="h-[18px] w-[18px]" /> : <Eye className="h-[18px] w-[18px]" />}
                          </button>
                        </div>
                      </FormField>

                      <FormField
                        label="Workspace"
                        labelRight={tenantLocked
                          ? <span className="flex items-center gap-1 text-[11px] font-medium text-emerald-600 dark:text-emerald-400"><Lock className="h-3 w-3" />Auto-detected</span>
                          : tenantSlug ? <span className="text-[11px] text-slate-400">Pre-filled</span> : null}
                        hint="Your company or tenant workspace identifier"
                      >
                        <input id="li-ws" type="text" value={tenantSlug} onChange={e => setTenantSlug(e.target.value)}
                          className="auth-input" placeholder="your-workspace" autoComplete="organization" required />
                      </FormField>

                      <AuthFeedback error={error} info={info} />

                      <button type="submit" disabled={loading}
                        className="auth-btn mt-6 disabled:cursor-not-allowed disabled:opacity-60">
                        {loading
                          ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                          : 'Sign in'}
                      </button>

                      <div className="flex flex-wrap justify-center gap-2 pt-1">
                        {SECURITY_POINTS.map((point) => (
                          <span key={point} className="rounded-full border border-slate-200 bg-slate-50 px-3 py-1.5 text-[10px] font-medium text-slate-400 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-500">
                            {point}
                          </span>
                        ))}
                      </div>
                    </form>
                  )}

                  {mode === 'forgot' && (
                    <form onSubmit={handleForgot} noValidate className="space-y-5">
                      <button type="button" onClick={() => go('login')}
                        className="mb-2 flex items-center gap-1.5 text-[13px] font-medium text-slate-400 hover:text-slate-600 dark:hover:text-slate-300">
                        ← Back to sign in
                      </button>
                      <div className="mb-2 flex h-11 w-11 items-center justify-center rounded-2xl bg-blue-50 ring-1 ring-blue-200/60 dark:bg-blue-500/10 dark:ring-blue-500/20">
                        <Mail className="h-5 w-5 text-blue-600 dark:text-blue-400" />
                      </div>
                      <h2 className="mb-1 text-[28px] font-black tracking-tight text-slate-950 dark:text-white">Reset password</h2>
                      <p className="mb-4 text-[14px] text-slate-500 dark:text-slate-400">
                        We&apos;ll send a reset code to your email address.
                      </p>

                      <FormField label="Work Email">
                        <input id="fg-em" type="email" value={forgotEmail || email}
                          onChange={e => setForgotEmail(e.target.value)}
                          className="auth-input" placeholder="you@company.com" autoComplete="email" required />
                      </FormField>
                      <FormField label="Workspace" hint="Optional - helps locate your account">
                        <input id="fg-ws" type="text" value={tenantSlug}
                          onChange={e => setTenantSlug(e.target.value)}
                          className="auth-input" placeholder="your-workspace" />
                      </FormField>

                      <AuthFeedback error={error} info={info} />

                      <button type="submit" disabled={loading} className="auth-btn mt-6 disabled:cursor-not-allowed disabled:opacity-60">
                        {loading ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" /> : 'Send reset code'}
                      </button>
                    </form>
                  )}

                  {mode === 'reset' && (
                    <form onSubmit={handleReset} noValidate className="space-y-5">
                      <button type="button" onClick={() => go('forgot')}
                        className="mb-2 flex items-center gap-1.5 text-[13px] font-medium text-slate-400 hover:text-slate-600 dark:hover:text-slate-300">
                        ← Back
                      </button>
                      <div className="mb-2 flex h-11 w-11 items-center justify-center rounded-2xl bg-blue-50 ring-1 ring-blue-200/60 dark:bg-blue-500/10 dark:ring-blue-500/20">
                        <KeyRound className="h-5 w-5 text-blue-600 dark:text-blue-400" />
                      </div>
                      <h2 className="mb-1 text-[28px] font-black tracking-tight text-slate-950 dark:text-white">New password</h2>
                      <p className="mb-4 text-[14px] text-slate-500 dark:text-slate-400">Enter the code from your email and set a new password.</p>

                      <FormField label="Work Email">
                        <input id="rs-em" type="email" value={forgotEmail || email}
                          onChange={e => setForgotEmail(e.target.value)}
                          className="auth-input" placeholder="you@company.com" autoComplete="email" required />
                      </FormField>
                      <FormField label="Reset Code">
                        <input id="rs-tk" type="text" value={resetToken} onChange={e => setResetToken(e.target.value)}
                          className="auth-input font-mono tracking-wider" placeholder="Paste code from email" required />
                      </FormField>
                      <FormField label="New Password" hint="Min. 10 characters">
                        <input id="rs-pw" type="password" value={newPw} onChange={e => setNewPw(e.target.value)}
                          className="auth-input" placeholder="••••••••••" autoComplete="new-password" required />
                      </FormField>
                      <FormField label="Confirm Password">
                        <input id="rs-cf" type="password" value={confirmPw} onChange={e => setConfirmPw(e.target.value)}
                          className="auth-input" placeholder="••••••••••" autoComplete="new-password" required />
                      </FormField>

                      <AuthFeedback error={error} info={info} />

                      <button type="submit" disabled={loading} className="auth-btn mt-6 disabled:cursor-not-allowed disabled:opacity-60">
                        {loading ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" /> : 'Update password'}
                      </button>
                    </form>
                  )}

                  {mode === 'mfa' && (
                    <form onSubmit={handleMfa} noValidate className="space-y-5">
                      <button type="button" onClick={() => { setMode('login'); setTotpCode(''); }}
                        className="mb-2 flex items-center gap-1.5 text-[13px] font-medium text-slate-400 hover:text-slate-600 dark:hover:text-slate-300">
                        ← Back to sign in
                      </button>
                      <div className="mb-2 flex h-11 w-11 items-center justify-center rounded-2xl bg-blue-50 ring-1 ring-blue-200/60 dark:bg-blue-500/10 dark:ring-blue-500/20">
                        <Smartphone className="h-5 w-5 text-blue-600 dark:text-blue-400" />
                      </div>
                      <h2 className="mb-1 text-[28px] font-black tracking-tight text-slate-950 dark:text-white">Two-factor auth</h2>
                      <p className="mb-4 text-[14px] text-slate-500 dark:text-slate-400">
                        Enter the 6-digit code from your authenticator app.
                      </p>

                      <FormField label="Authentication Code">
                        <input
                          id="mfa-code"
                          type="text"
                          inputMode="numeric"
                          pattern="[0-9]{6}"
                          maxLength={6}
                          value={totpCode}
                          onChange={e => setTotpCode(e.target.value.replace(/\D/g, ''))}
                          className="auth-input font-mono tracking-[0.3em] text-center text-xl"
                          placeholder="000000"
                          autoComplete="one-time-code"
                          autoFocus
                          required
                        />
                      </FormField>

                      <AuthFeedback error={error} info={info} />

                      <button type="submit" disabled={loading || totpCode.length !== 6}
                        className="auth-btn mt-6 disabled:cursor-not-allowed disabled:opacity-60">
                        {loading
                          ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                          : 'Verify'}
                      </button>
                    </form>
                  )}

                  <div className="mt-auto pt-5">
                    <p className="text-center text-[11px] text-slate-500 dark:text-slate-500">
                      By signing in you agree to our{' '}
                      <a href="/privacy" target="_blank" rel="noopener noreferrer"
                        className="underline underline-offset-2 hover:text-slate-600 dark:hover:text-slate-400 transition-colors">
                        Privacy Policy
                      </a>
                      {' '}and{' '}
                      <a href="/terms" target="_blank" rel="noopener noreferrer"
                        className="underline underline-offset-2 hover:text-slate-600 dark:hover:text-slate-400 transition-colors">
                        Terms of Service
                      </a>
                    </p>
                  </div>
                </div>
              </div>
            </div>
            </div>

            <p className="mt-6 text-center text-[11px] text-slate-500 dark:text-slate-600 lg:hidden">
              A <span className="font-semibold text-slate-500">Kode Kinetics</span> product
            </p>
          </section>
        </div>
      </div>

      <style>{`
        .auth-input {
          display: block; width: 100%;
          border-radius: 16px;
          border: 1px solid rgba(148, 163, 184, 0.24);
          background: linear-gradient(180deg, rgba(255,255,255,0.74), rgba(247,249,255,0.96));
          padding: 14px 16px;
          font-size: 15px;
          color: #0f172a;
          box-shadow:
            inset 0 1px 0 rgba(255,255,255,0.95),
            0 1px 2px rgba(15, 23, 42, 0.04);
          transition: border-color 0.18s, box-shadow 0.18s, transform 0.18s, background 0.18s;
          outline: none;
        }
        .auth-input::placeholder { color: #94a3b8; }
        .auth-input:focus {
          border-color: rgba(47, 107, 255, 0.55);
          box-shadow:
            0 0 0 4px rgba(47, 107, 255, 0.12),
            0 10px 22px rgba(47, 107, 255, 0.08);
          transform: translateY(-1px);
        }
        @media (prefers-color-scheme: dark) {
          .auth-input {
            background:
              linear-gradient(180deg, rgba(255,255,255,0.06), rgba(255,255,255,0.03));
            border-color: rgba(255,255,255,0.09);
            color: #f1f5f9;
            box-shadow:
              inset 0 1px 0 rgba(255,255,255,0.05),
              0 1px 2px rgba(0,0,0,0.16);
          }
          .auth-input::placeholder { color: rgba(255,255,255,0.2); }
          .auth-input:focus {
            border-color: rgba(94, 235, 255, 0.55);
            box-shadow:
              0 0 0 4px rgba(47, 107, 255, 0.18),
              0 10px 22px rgba(0,0,0,0.24);
          }
        }
        .auth-btn {
          display: flex; align-items: center; justify-content: center; gap: 8px;
          width: 100%;
          border-radius: 16px;
          background:
            linear-gradient(135deg, #1d4ed8 0%, #2f6bff 50%, #5eebff 170%);
          padding: 15px 24px;
          font-size: 15px;
          font-weight: 800;
          color: white;
          letter-spacing: 0.01em;
          box-shadow:
            0 12px 30px rgba(47, 107, 255, 0.28),
            inset 0 1px 0 rgba(255,255,255,0.24);
          transition: transform 0.16s ease, box-shadow 0.16s ease, filter 0.16s ease;
        }
        .auth-btn:hover:not(:disabled) {
          filter: saturate(1.05) brightness(1.02);
          box-shadow:
            0 16px 36px rgba(47, 107, 255, 0.34),
            inset 0 1px 0 rgba(255,255,255,0.28);
          transform: translateY(-1px);
        }
        .auth-btn:active:not(:disabled) {
          transform: translateY(0) scale(0.995);
          box-shadow:
            0 8px 22px rgba(47, 107, 255, 0.24),
            inset 0 1px 0 rgba(255,255,255,0.20);
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
      <div className="mb-2 flex items-center justify-between">
        <p className="text-[10px] font-bold tracking-[0.2em] uppercase text-slate-400 dark:text-slate-500">{label}</p>
        {labelRight}
      </div>
      {children}
      {hint && <p className="mt-1.5 text-[11px] leading-relaxed text-slate-400 dark:text-slate-500">{hint}</p>}
    </div>
  );
}

function AuthFeedback({ error, info }: { error: string; info: string }) {
  if (error) return (
    <div className="mt-5 flex items-start gap-3 rounded-2xl border border-red-100 bg-red-50/80 px-4 py-3 shadow-[0_8px_24px_rgba(239,68,68,0.08)] dark:border-red-500/15 dark:bg-red-500/[0.07]">
      <AlertCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-500" />
      <p className="text-[13px] leading-relaxed text-red-700 dark:text-red-400">{error}</p>
    </div>
  );
  if (info) return (
    <div className="mt-5 flex items-start gap-3 rounded-2xl border border-emerald-100 bg-emerald-50/80 px-4 py-3 shadow-[0_8px_24px_rgba(16,185,129,0.08)] dark:border-emerald-500/15 dark:bg-emerald-500/[0.07]">
      <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-500" />
      <p className="text-[13px] leading-relaxed text-emerald-700 dark:text-emerald-400">{info}</p>
    </div>
  );
  return null;
}
