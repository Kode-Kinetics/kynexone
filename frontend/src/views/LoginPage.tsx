'use client';

import { useEffect, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import {
  AlertCircle, CheckCircle2, Clock, Eye, EyeOff, KeyRound, Lock, Mail,
  ShieldCheck, Smartphone, TrendingUp, Users,
} from 'lucide-react';
import { useAuth } from '../contexts/AuthContext';
import { authApi } from '../api/auth';
import { Logo } from '../components/Logo';

// Feature-specific marketing ticker — a scrolling marquee of real modules/capabilities.
const FEATURE_TICKER = [
  'WPS / SIF export', 'GOSI & social insurance', 'EOSB gratuity', 'Qiwa & Mudad',
  'Shift & roster planning', 'Overtime & time-off', 'Loans & advances', 'Payslip designer',
  'Performance & calibration', 'Recruitment & onboarding', 'Org chart', 'Employee self-service',
  'Multi-company & multi-currency', 'Approval workflows', 'Saudization tracking', 'Hijri calendar',
  'Document & visa compliance', 'Bank file generation', 'Role-based access', 'Audit trails',
];

// Illustrative product-preview slides (a UI glimpse, like a product screenshot) — the
// carousel cycles these to show the platform's breadth at a glance.
const PREVIEW_SLIDES = [
  {
    tag: 'Payroll · June 2026', value: '$1.31M', delta: '+3.2%',
    bars: [40, 54, 46, 68, 60, 82, 74],
    stats: [
      { icon: Users, label: 'Employees', value: '1,248' },
      { icon: Clock, label: 'On-time', value: '99.9%' },
      { icon: CheckCircle2, label: 'Approved', value: '96%' },
    ],
  },
  {
    tag: 'Attendance · Today', value: '1,194 in', delta: '+96%',
    bars: [70, 82, 60, 88, 74, 90, 84],
    stats: [
      { icon: Users, label: 'Present', value: '96%' },
      { icon: Clock, label: 'Late', value: '18' },
      { icon: CheckCircle2, label: 'Synced', value: '42' },
    ],
  },
  {
    tag: 'Leave · This month', value: '84 requests', delta: '−12%',
    bars: [30, 44, 38, 52, 40, 58, 48],
    stats: [
      { icon: CheckCircle2, label: 'Approved', value: '71' },
      { icon: Clock, label: 'Pending', value: '9' },
      { icon: Users, label: 'Avg days', value: '1.2' },
    ],
  },
  {
    tag: 'Headcount · Q2', value: '1,248', delta: '+23',
    bars: [50, 58, 55, 66, 70, 76, 80],
    stats: [
      { icon: Users, label: 'New hires', value: '23' },
      { icon: TrendingUp, label: 'Attrition', value: '1.4%' },
      { icon: CheckCircle2, label: 'Open roles', value: '12' },
    ],
  },
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

  const [slide,        setSlide]        = useState(0);
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

  // Auto-advance the product-preview carousel.
  useEffect(() => {
    const id = setInterval(() => setSlide((s) => (s + 1) % PREVIEW_SLIDES.length), 3800);
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
        @keyframes aurora-a {
          0%, 100% { transform: translate3d(0,0,0) scale(1); }
          50% { transform: translate3d(6%, 8%, 0) scale(1.15); }
        }
        @keyframes aurora-b {
          0%, 100% { transform: translate3d(0,0,0) scale(1.1); }
          50% { transform: translate3d(-8%, -6%, 0) scale(1); }
        }
        @keyframes aurora-c {
          0%, 100% { transform: translate3d(0,0,0) scale(1); }
          50% { transform: translate3d(5%, -7%, 0) scale(1.2); }
        }
        .aurora-a { animation: aurora-a 19s ease-in-out infinite; }
        .aurora-b { animation: aurora-b 23s ease-in-out infinite; }
        .aurora-c { animation: aurora-c 27s ease-in-out infinite; }
        .brand-spot {
          background: radial-gradient(520px circle at var(--mx, 30%) var(--my, 22%), rgba(86,148,255,0.22), transparent 62%);
          transition: background 0.18s ease-out;
        }
        @keyframes bar-rise {
          0%, 100% { transform: scaleY(0.78); }
          50% { transform: scaleY(1); }
        }
        .pv-bar { transform-origin: bottom; animation: bar-rise 2.8s ease-in-out infinite; }
        @keyframes ticker-scroll { from { transform: translateX(0); } to { transform: translateX(-50%); } }
        .ticker-track { animation: ticker-scroll 38s linear infinite; }
        .ticker-mask:hover .ticker-track { animation-play-state: paused; }
        @media (prefers-reduced-motion: reduce) {
          .auth-fade, .aurora-a, .aurora-b, .aurora-c, .pv-bar, .ticker-track { animation: none !important; }
        }
      `}</style>

      <div className="grid min-h-[100svh] w-full lg:grid-cols-2">
        {/* ── Brand panel ───────────────────────────────────────────────── */}
        <section
          onMouseMove={(e) => {
            const r = e.currentTarget.getBoundingClientRect();
            e.currentTarget.style.setProperty('--mx', `${((e.clientX - r.left) / r.width) * 100}%`);
            e.currentTarget.style.setProperty('--my', `${((e.clientY - r.top) / r.height) * 100}%`);
          }}
          className="relative hidden flex-col overflow-hidden bg-[#060a17] px-12 py-14 text-white lg:flex"
        >
          {/* Aurora mesh */}
          <div className="pointer-events-none absolute -left-1/4 -top-1/4 h-[70%] w-[70%] rounded-full bg-[radial-gradient(circle,rgba(47,107,255,0.55),transparent_60%)] blur-3xl aurora-a" />
          <div className="pointer-events-none absolute bottom-[-20%] right-[-15%] h-[65%] w-[65%] rounded-full bg-[radial-gradient(circle,rgba(56,189,248,0.30),transparent_60%)] blur-3xl aurora-b" />
          <div className="pointer-events-none absolute left-[20%] top-[35%] h-[55%] w-[55%] rounded-full bg-[radial-gradient(circle,rgba(99,102,241,0.34),transparent_60%)] blur-3xl aurora-c" />
          {/* Iso dot-grid */}
          <div className="pointer-events-none absolute inset-0 opacity-[0.18] [background-image:radial-gradient(rgba(255,255,255,0.5)_1px,transparent_1px)] [background-size:26px_26px] [mask-image:radial-gradient(ellipse_at_center,black,transparent_75%)]" />
          {/* Cursor spotlight */}
          <div className="brand-spot pointer-events-none absolute inset-0" />
          {/* Film grain */}
          <div
            className="pointer-events-none absolute inset-0 opacity-[0.06] mix-blend-overlay"
            style={{ backgroundImage: "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='140' height='140'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.85' numOctaves='2' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)'/%3E%3C/svg%3E\")" }}
          />
          {/* Top edge fade for depth */}
          <div className="pointer-events-none absolute inset-x-0 top-0 h-24 bg-gradient-to-b from-white/[0.06] to-transparent" />

          {/* Brand */}
          <div className="relative z-10 mx-auto flex w-full max-w-[440px] items-center gap-3">
            <div className="rounded-2xl border border-white/15 bg-white/[0.07] p-2.5 shadow-[0_8px_30px_rgba(8,18,55,0.5)] backdrop-blur-md">
              <Logo size="md" collapsed theme="dark" />
            </div>
            <div>
              <p className="text-sm font-bold tracking-tight text-white">KynexOne</p>
              <p className="text-xs text-slate-400">Enterprise Workforce Platform</p>
            </div>
          </div>

          {/* Hero + product glimpse — grows to fill the panel and centers vertically */}
          <div className="relative z-10 mx-auto flex w-full max-w-[440px] flex-1 flex-col justify-center py-10">
            <div className="inline-flex w-fit items-center gap-2 rounded-full border border-white/10 bg-white/[0.05] px-3 py-1.5 text-[11px] font-medium tracking-wide text-slate-300 backdrop-blur">
              <span className="relative flex h-1.5 w-1.5">
                <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-75" />
                <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-emerald-400" />
              </span>
              Built for HR, payroll &amp; finance teams
            </div>

            <h1 className="mt-5 text-[2rem] font-bold leading-[1.1] tracking-tight xl:text-[2.4rem]">
              Run your entire workforce from{' '}
              <span className="bg-gradient-to-r from-sky-300 via-blue-300 to-indigo-300 bg-clip-text text-transparent">
                one trusted system.
              </span>
            </h1>
            <p className="mt-3.5 text-[14.5px] leading-relaxed text-slate-300/90">
              People, payroll, leave, attendance, and compliance — unified, tenant-isolated,
              and audit-ready from day one.
            </p>

            {/* Product preview carousel — cycles modules to show platform breadth */}
            {(() => {
              const s = PREVIEW_SLIDES[slide];
              const negative = s.delta.startsWith('−');
              return (
                <div className="mt-7 rounded-2xl border border-white/[0.08] bg-white/[0.04] p-4 shadow-[0_24px_70px_rgba(4,10,30,0.55)] backdrop-blur-xl">
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-1.5">
                      <span className="h-2 w-2 rounded-full bg-white/15" />
                      <span className="h-2 w-2 rounded-full bg-white/15" />
                      <span className="h-2 w-2 rounded-full bg-white/15" />
                    </div>
                    <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-400/10 px-2 py-0.5 text-[10px] font-semibold text-emerald-300">
                      <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" /> Live
                    </span>
                  </div>

                  {/* slide body — re-keyed so it cross-fades on change */}
                  <div key={slide} className="auth-fade">
                    <div className="mt-3 flex items-end justify-between">
                      <div>
                        <p className="text-[11px] font-medium uppercase tracking-wide text-slate-400">{s.tag}</p>
                        <p className="mt-0.5 text-2xl font-bold tracking-tight text-white">{s.value}</p>
                      </div>
                      <span className={`inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-[11px] font-semibold ${negative ? 'bg-rose-400/10 text-rose-300' : 'bg-emerald-400/10 text-emerald-300'}`}>
                        <TrendingUp className={`h-3.5 w-3.5 ${negative ? 'rotate-180' : ''}`} /> {s.delta.replace(/[+−]/, '')}
                      </span>
                    </div>

                    {/* animated sparkline bars */}
                    <div className="mt-3 flex h-12 items-end gap-1.5">
                      {s.bars.map((b, i) => (
                        <span
                          key={i}
                          className="pv-bar flex-1 rounded-sm bg-gradient-to-t from-blue-500/70 to-sky-300/80"
                          style={{ height: `${b}%`, animationDelay: `${i * 0.12}s` }}
                        />
                      ))}
                    </div>

                    {/* KPI row */}
                    <div className="mt-4 grid grid-cols-3 gap-2 border-t border-white/[0.07] pt-3">
                      {s.stats.map((st) => (
                        <div key={st.label} className="flex flex-col">
                          <span className="flex items-center gap-1 text-[10px] font-medium uppercase tracking-wide text-slate-400">
                            <st.icon className="h-3 w-3" /> {st.label}
                          </span>
                          <span className="mt-0.5 text-sm font-bold text-white">{st.value}</span>
                        </div>
                      ))}
                    </div>
                  </div>

                  {/* carousel dots */}
                  <div className="mt-4 flex items-center justify-center gap-1.5">
                    {PREVIEW_SLIDES.map((sl, i) => (
                      <button
                        key={sl.tag}
                        type="button"
                        aria-label={`Show ${sl.tag}`}
                        onClick={() => setSlide(i)}
                        className={`h-1.5 rounded-full transition-all ${i === slide ? 'w-5 bg-sky-300' : 'w-1.5 bg-white/20 hover:bg-white/40'}`}
                      />
                    ))}
                  </div>
                </div>
              );
            })()}

            {/* Feature ticker — scrolling marquee of platform capabilities */}
            <div className="ticker-mask relative mt-6 overflow-hidden [mask-image:linear-gradient(to_right,transparent,black_8%,black_92%,transparent)]">
              <div className="ticker-track flex w-max items-center gap-2.5">
                {[...FEATURE_TICKER, ...FEATURE_TICKER].map((f, i) => (
                  <span
                    key={i}
                    className="inline-flex shrink-0 items-center gap-1.5 rounded-full border border-white/[0.07] bg-white/[0.03] px-3 py-1 text-[11px] font-medium text-slate-300"
                  >
                    <span className="h-1 w-1 rounded-full bg-sky-400/80" />
                    {f}
                  </span>
                ))}
              </div>
            </div>
          </div>

          <p className="relative z-10 mx-auto w-full max-w-[440px] text-xs text-slate-500">
            A <span className="font-semibold text-slate-400">Kode Kinetics</span> product
          </p>
        </section>

        {/* ── Form panel ────────────────────────────────────────────────── */}
        <section className="flex items-center justify-center bg-slate-50 px-5 py-10 dark:bg-[#0a0f1e] sm:px-8">
          <div className="auth-fade w-full max-w-[420px]">
            {/* Mobile brand */}
            <div className="mb-8 flex items-center gap-3 lg:hidden">
              <div className="rounded-xl border border-slate-200 bg-white p-2.5 shadow-sm dark:border-white/10 dark:bg-white/5">
                <Logo size="md" collapsed />
              </div>
              <div>
                <p className="text-sm font-bold tracking-tight text-slate-900 dark:text-white">KynexOne</p>
                <p className="text-xs text-slate-500 dark:text-slate-400">Enterprise Workforce Platform</p>
              </div>
            </div>

            {mode === 'login' && (
              <>
                <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Sign in</h2>
                <p className="mt-1.5 text-sm text-slate-500 dark:text-slate-400">
                  Enter your work email, password, and workspace to continue.
                </p>

                <form onSubmit={handleLogin} noValidate className="mt-8 space-y-5">
                  <FormField label="Work email">
                    <input id="li-em" type="email" value={email} onChange={e => setEmail(e.target.value)}
                      className="auth-input" placeholder="you@company.com" autoComplete="email" required />
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
                      className="auth-input" placeholder="your-workspace" autoComplete="organization" required />
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
          border-radius: 10px;
          border: 1px solid rgb(203 213 225);
          background: #ffffff;
          padding: 11px 14px;
          font-size: 14px;
          color: #0f172a;
          transition: border-color 0.15s, box-shadow 0.15s;
          outline: none;
        }
        .auth-input::placeholder { color: #94a3b8; }
        .auth-input:focus {
          border-color: #2f6bff;
          box-shadow: 0 0 0 3px rgba(47, 107, 255, 0.14);
        }
        @media (prefers-color-scheme: dark) {
          .auth-input {
            background: rgba(255,255,255,0.04);
            border-color: rgba(255,255,255,0.12);
            color: #f1f5f9;
          }
          .auth-input::placeholder { color: rgba(255,255,255,0.28); }
          .auth-input:focus {
            border-color: #5eebff;
            box-shadow: 0 0 0 3px rgba(94, 235, 255, 0.16);
          }
        }
        .auth-btn {
          position: relative;
          display: flex; align-items: center; justify-content: center; gap: 8px;
          width: 100%;
          overflow: hidden;
          border-radius: 10px;
          background: linear-gradient(180deg, #3b78ff 0%, #2f6bff 55%, #1f54e6 100%);
          padding: 12px 20px;
          font-size: 14px;
          font-weight: 600;
          color: white;
          box-shadow: 0 1px 0 rgba(255,255,255,0.25) inset, 0 8px 20px rgba(31, 84, 230, 0.30);
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
          box-shadow: 0 1px 0 rgba(255,255,255,0.3) inset, 0 12px 26px rgba(31, 84, 230, 0.42);
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
