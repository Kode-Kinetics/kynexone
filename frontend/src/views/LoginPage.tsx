'use client';

import { useEffect, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import {
  AlertCircle, BarChart3, CheckCircle2, Clock,
  Eye, EyeOff, FileText, KeyRound, Lock, Mail,
  Settings, ShieldCheck, TrendingUp, Users, Zap,
} from 'lucide-react';
import { useAuth } from '../contexts/AuthContext';
import { authApi } from '../api/auth';
import { Smartphone } from 'lucide-react';
import { Logo } from '../components/Logo';

function useCountUp(target: number, duration = 800, delay = 0): number {
  const [val, setVal] = useState(0);
  useEffect(() => {
    let cancelled = false;
    const tid = setTimeout(() => {
      const start = performance.now();
      const step = (now: number) => {
        if (cancelled) return;
        const t = Math.min((now - start) / duration, 1);
        const eased = 1 - Math.pow(1 - t, 3);
        setVal(eased * target);
        if (t < 1) requestAnimationFrame(step);
        else setVal(target);
      };
      requestAnimationFrame(step);
    }, delay);
    return () => { cancelled = true; clearTimeout(tid); };
  }, [target, duration, delay]);
  return val;
}

const WEEK = [
  { d: 'M', v: 96 }, { d: 'T', v: 99 }, { d: 'W', v: 97 },
  { d: 'T', v: 98 }, { d: 'F', v: 94 }, { d: 'S', v: 88 }, { d: 'S', v: 91 },
];

const ACTIVITY = [
  { name: 'Ahmad K.', action: 'Clocked in',       time: '8:02 AM', dot: 'bg-emerald-400' },
  { name: 'Sarah M.', action: 'Leave approved',   time: '8:15 AM', dot: 'bg-blue-400'    },
  { name: 'Payroll',  action: 'Run completed',    time: '7:00 AM', dot: 'bg-amber-400'   },
];

const PILLS = ['Multi-country Payroll', 'Labour Law Compliance', 'RBAC + Audit Logs', 'TOTP MFA', 'Multi-branch', 'AI-powered'];

const TRUST_POINTS = [
  { label: 'Audit trail', value: 'Every sign-in logged' },
  { label: 'Security posture', value: 'Tenant-scoped sessions' },
  { label: 'Access control', value: 'MFA + lockout aware' },
];

const SURFACE_POINTS = [
  'Workspace-aware sign-in',
  'Deterministic MFA handoff',
  'Built for enterprise auditability',
];

const NAV_ICONS = [
  { icon: BarChart3,  active: true  },
  { icon: Users,      active: false },
  { icon: Clock,      active: false },
  { icon: FileText,   active: false },
  { icon: ShieldCheck,active: false },
  { icon: Settings,   active: false },
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

  const kpi0 = useCountUp(347,  800, 300);
  const kpi1 = useCountUp(98.2, 800, 420);
  const kpi2 = useCountUp(2.4,  800, 540);

  return (
    <>
      <style>{`
        @keyframes kx-sheen {
          0% { transform: translateX(-35%); opacity: 0; }
          30% { opacity: 0.55; }
          100% { transform: translateX(35%); opacity: 0; }
        }
        @keyframes kx-grid-shift { from{background-position:0 0} to{background-position:44px 44px} }
        @keyframes kx-window-in  { from{opacity:0;transform:translateY(20px)} to{opacity:1;transform:translateY(0)} }
        @keyframes kx-bar-in     { from{transform:scaleY(0)} to{transform:scaleY(1)} }
        @keyframes kx-pulse-dot  { 0%,100%{opacity:1} 50%{opacity:0.3} }
        .kx-grid {
          background-image:
            linear-gradient(rgba(100,150,255,0.04) 1px, transparent 1px),
            linear-gradient(90deg, rgba(100,150,255,0.04) 1px, transparent 1px);
          background-size: 44px 44px;
        }
        @keyframes kx-count  { from{opacity:0;transform:translateY(6px)} to{opacity:1;transform:translateY(0)} }
        @keyframes kx-row-in { from{opacity:0;transform:translateX(-8px)} to{opacity:1;transform:translateX(0)} }
        .kx-win   { animation: kx-window-in 0.7s ease-out both }
        .kx-bar   { animation: kx-bar-in 0.4s ease-out both; transform-origin: bottom }
        .kx-dot   { animation: kx-pulse-dot 2s ease-in-out infinite }
        .kx-count { animation: kx-count 0.5s ease-out both }
        .kx-row   { animation: kx-row-in 0.4s ease-out both }
      `}</style>

      <div className="relative flex min-h-screen overflow-hidden font-sans bg-[#eef2ff] text-slate-900 dark:bg-[#040814] dark:text-white">
        <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.18),transparent_28%),radial-gradient(circle_at_85%_20%,rgba(94,235,255,0.12),transparent_22%),linear-gradient(180deg,rgba(255,255,255,0.4),transparent_35%)] dark:bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.22),transparent_28%),radial-gradient(circle_at_85%_20%,rgba(94,235,255,0.10),transparent_22%),linear-gradient(180deg,rgba(6,11,24,0.85),rgba(4,8,20,0.98))]" />
        <div className="pointer-events-none absolute inset-0 opacity-[0.28] mix-blend-soft-light [background-image:linear-gradient(rgba(47,107,255,0.08)_1px,transparent_1px),linear-gradient(90deg,rgba(47,107,255,0.08)_1px,transparent_1px)] [background-size:72px_72px]" />

        {/* ════════════════ LEFT — product showcase ══════════════════════════ */}
        <div className="relative hidden overflow-hidden lg:flex lg:w-[58%] flex-col bg-[#050915]">

          {/* Subtle grid pattern */}
          <div className="kx-grid absolute inset-0" />

          {/* Single deep-blue gradient radial — no blobs, no spinning rings */}
          <div className="absolute inset-0 bg-[radial-gradient(ellipse_80%_60%_at_30%_50%,rgba(37,99,235,0.12),transparent)]" />

          {/* Faint KYNEXONE stamp — bottom right */}
          <p className="pointer-events-none absolute bottom-6 right-8 select-none text-[72px] font-black tracking-tighter text-white opacity-[0.015] -rotate-3">KYNEXONE</p>

          {/* ── Content ────────────────────────────────────────────────────── */}
          <div className="relative z-10 flex h-full flex-col justify-between gap-6 px-12 py-10">

            {/* Logo row — forced dark context so text renders white on dark bg */}
            <div className="flex items-center justify-between gap-4 dark">
              <div className="flex items-center gap-3">
                <Logo size="lg" />
                <div className="h-5 w-px bg-white/[0.1]" />
                <span className="text-[10px] font-bold tracking-[0.22em] uppercase text-blue-300/60">
                  Enterprise Workforce Platform
                </span>
              </div>
              <div className="rounded-full border border-white/10 bg-white/5 px-3 py-1 text-[10px] font-semibold uppercase tracking-[0.22em] text-white/45">
                KynexOne Secure Entry
              </div>
            </div>

            {/* Headline */}
            <div className="shrink-0">
              <h1 className="max-w-[640px] text-[50px] font-black leading-[1.02] tracking-tight xl:text-[60px]">
                <span className="text-white">The complete workforce</span><br />
                <span className="bg-gradient-to-r from-blue-400 via-cyan-300 to-sky-300 bg-clip-text text-transparent">
                  platform for every team.
                </span>
              </h1>
              <p className="mt-4 max-w-[460px] text-[15px] leading-relaxed text-slate-300/80">
                HR, payroll, attendance and compliance in one control plane. Built for teams that need calm, traceable operations across every country, every branch, and every shift.
              </p>
              <div className="mt-6 flex flex-wrap gap-2">
                {SURFACE_POINTS.map((point) => (
                  <span
                    key={point}
                    className="rounded-full border border-white/10 bg-white/[0.05] px-3 py-1.5 text-[11px] font-medium text-white/65 backdrop-blur-sm"
                  >
                    {point}
                  </span>
                ))}
              </div>
            </div>

            {/* ── App window mockup — the product speaks for itself ─────── */}
            <div className="kx-win flex-1 overflow-hidden rounded-[28px] border border-white/[0.08] bg-[#08111f] shadow-[0_36px_100px_rgba(0,0,0,0.58),0_0_0_1px_rgba(255,255,255,0.04)]">

              {/* Browser-style chrome bar */}
              <div className="flex items-center gap-3 border-b border-white/[0.06] bg-[#070d19] px-4 py-2.5">
                <div className="flex gap-1.5">
                  <div className="h-2.5 w-2.5 rounded-full bg-[#ff5f57]" />
                  <div className="h-2.5 w-2.5 rounded-full bg-[#febc2e]" />
                  <div className="h-2.5 w-2.5 rounded-full bg-[#28c840]" />
                </div>
                <div className="flex flex-1 justify-center">
                    <div className="flex items-center gap-2 rounded-full bg-white/[0.05] px-3 py-1.5">
                      <div className="kx-dot h-1.5 w-1.5 rounded-full bg-emerald-400" />
                      <span className="text-[11px] text-white/25">app.kynexone.com / dashboard</span>
                    </div>
                  </div>
                  <div className="h-6 w-6 overflow-hidden rounded-full bg-gradient-to-br from-blue-500 to-indigo-600 ring-1 ring-white/[0.15]" />
              </div>

              {/* App layout */}
              <div className="flex h-full">

                {/* Narrow sidebar */}
                <div className="flex flex-col items-center gap-1 border-r border-white/[0.05] bg-[#070d19] px-2 py-3">
                  {NAV_ICONS.map(({ icon: Icon, active }, i) => (
                    <div key={i} className={`flex h-8 w-8 items-center justify-center rounded-lg transition-colors ${
                      active
                        ? 'bg-blue-500/20 text-blue-400 ring-1 ring-blue-500/20'
                        : 'text-white/[0.18] hover:bg-white/[0.04] hover:text-white/30'
                    }`}>
                      <Icon className="h-3.5 w-3.5" />
                    </div>
                  ))}
                </div>

                {/* Main dashboard content */}
                <div className="relative flex-1 overflow-hidden p-4">
                  <div className="pointer-events-none absolute right-4 top-4 h-24 w-24 rounded-full border border-cyan-300/15 bg-cyan-300/5 blur-2xl" />

                  {/* Page header */}
                  <div className="mb-4 flex items-start justify-between">
                    <div>
                      <p className="text-[11px] font-bold uppercase tracking-widest text-white/30">Dashboard</p>
                      <p className="mt-0.5 text-sm font-bold text-white/80">Workforce Overview</p>
                    </div>
                    <div className="flex items-center gap-1.5 rounded-full bg-emerald-500/[0.08] px-2.5 py-1 ring-1 ring-emerald-500/20">
                      <div className="kx-dot h-1.5 w-1.5 rounded-full bg-emerald-400" />
                      <span className="text-[10px] font-semibold text-emerald-400">Live</span>
                    </div>
                  </div>

                  {/* KPI tiles */}
                  <div className="mb-3 grid grid-cols-3 gap-2">
                    {[
                      { label: 'Active Employees', value: String(Math.round(kpi0)),       sub: '↑ 12 today',   color: 'text-emerald-300', ring: 'ring-emerald-500/15', bg: 'bg-emerald-500/[0.06]', Icon: Users,       delay: 300 },
                      { label: 'Attendance Rate',  value: kpi1.toFixed(1) + '%',          sub: 'Monthly avg',   color: 'text-cyan-300',    ring: 'ring-cyan-500/15',    bg: 'bg-cyan-500/[0.06]',    Icon: TrendingUp,  delay: 420 },
                      { label: 'Payroll',          value: '$' + kpi2.toFixed(1) + 'M',    sub: 'On time · Jun', color: 'text-amber-300',   ring: 'ring-amber-500/15',   bg: 'bg-amber-500/[0.06]',   Icon: CheckCircle2,delay: 540 },
                    ].map(({ label, value, sub, color, ring, bg, Icon, delay }) => (
                      <div key={label} className={`${bg} rounded-2xl p-3 ring-1 ${ring} backdrop-blur-sm`}>
                        <Icon className={`mb-2 h-3.5 w-3.5 ${color} opacity-60`} />
                        <p className={`kx-count text-[21px] font-black leading-none ${color}`} style={{ animationDelay: `${delay}ms` }}>{value}</p>
                        <p className="mt-1.5 text-[9px] font-medium uppercase tracking-wide text-white/25">{label}</p>
                        <p className="text-[9px] text-white/15">{sub}</p>
                      </div>
                    ))}
                  </div>

                  {/* Attendance sparkline */}
                  <div className="mb-3 rounded-2xl bg-white/[0.025] p-3 ring-1 ring-white/[0.04]">
                    <div className="mb-2 flex items-center justify-between">
                      <p className="text-[9px] font-bold uppercase tracking-widest text-white/20">Attendance — This Week</p>
                      <p className="text-[9px] text-white/15">Mon–Sun</p>
                    </div>
                    <div className="flex items-end gap-1.5 h-[36px]">
                      {WEEK.map(({ d, v }, i) => (
                        <div key={i} className="flex flex-1 flex-col items-center gap-1">
                          <div
                            className="kx-bar w-full rounded-t-[2px] bg-gradient-to-t from-blue-500/50 to-cyan-400/70"
                            style={{ height: `${Math.round((v / 100) * 28)}px`, animationDelay: `${i * 55}ms` }}
                          />
                          <p className="text-[7px] text-white/15">{d}</p>
                        </div>
                      ))}
                    </div>
                  </div>

                  {/* Recent activity */}
                  <div className="space-y-1">
                    {ACTIVITY.map(({ name, action, time, dot }, i) => (
                      <div key={name} className="kx-row flex items-center gap-2.5 rounded-xl bg-white/[0.02] px-2.5 py-1.5 ring-1 ring-white/[0.03]" style={{ animationDelay: `${700 + i * 60}ms` }}>
                        <div className={`h-1.5 w-1.5 rounded-full ${dot}`} />
                        <p className="flex-1 text-[10px] text-white/40">
                          <span className="font-semibold text-white/60">{name}</span> · {action}
                        </p>
                        <p className="text-[9px] text-white/15">{time}</p>
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            </div>

            {/* Compliance pill strip */}
            <div className="flex flex-wrap justify-center gap-2 shrink-0">
              {PILLS.map(p => (
                <span key={p}
                  className="rounded-full border border-white/[0.12] bg-white/[0.07] px-4 py-1.5 text-[12px] font-medium text-white/55 backdrop-blur-sm shadow-[inset_0_1px_0_rgba(255,255,255,0.08)]">
                  {p}
                </span>
              ))}
            </div>

            {/* Footer */}
            <p className="shrink-0 text-[11px] text-white/20">
              A <span className="font-semibold text-white/30">Kode Kinetics</span> product · Trusted by growing enterprises worldwide
            </p>

          </div>
        </div>

        {/* ════════════════ RIGHT — auth forms ════════════════════════════════ */}
        <div className="relative flex flex-1 flex-col items-center justify-center overflow-hidden px-6 py-10 sm:px-10 sm:py-14">

          {/* Glass background blobs — subtle, behind the card */}
          <div className="pointer-events-none absolute -top-24 -right-24 h-72 w-72 rounded-full bg-blue-300/20 blur-3xl dark:bg-blue-700/10" />
          <div className="pointer-events-none absolute -bottom-16 -left-16 h-64 w-64 rounded-full bg-indigo-300/20 blur-3xl dark:bg-indigo-700/10" />
          <div className="pointer-events-none absolute inset-0 bg-[linear-gradient(180deg,rgba(255,255,255,0.55),rgba(255,255,255,0.12))] dark:bg-[linear-gradient(180deg,rgba(4,8,20,0.18),rgba(4,8,20,0.04))]" />

          {/* Mobile logo */}
          <div className="relative z-10 mb-8 lg:hidden">
            <Logo />
          </div>

          {/* Glass card wrapper */}
          <div className="relative z-10 w-full max-w-[460px] rounded-[32px] border border-white/75 bg-white/72 p-[1px] shadow-[0_30px_120px_rgba(37,99,235,0.12),0_0_0_1px_rgba(255,255,255,0.6)] backdrop-blur-2xl dark:border-white/[0.08] dark:bg-white/[0.04] dark:shadow-[0_30px_120px_rgba(0,0,0,0.52),0_0_0_1px_rgba(255,255,255,0.04)]">
            <div className="rounded-[31px] bg-[linear-gradient(180deg,rgba(255,255,255,0.88),rgba(255,255,255,0.72))] px-6 py-6 sm:px-10 sm:py-10 dark:bg-[linear-gradient(180deg,rgba(9,16,32,0.90),rgba(7,12,24,0.86))]">

          <div className="w-full max-w-[380px]">

            {/* ── SIGN IN ─────────────────────────────────────────────────── */}
            {mode === 'login' && (
              <form onSubmit={handleLogin} noValidate>
                <div className="mb-6 inline-flex items-center gap-2 rounded-full border border-sapphire/15 bg-sapphire/5 px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.24em] text-sapphire dark:border-sapphire/25 dark:bg-sapphire/10 dark:text-cyanAccent">
                  <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
                  Enterprise access
                </div>

                <h2 className="mb-2 text-[34px] font-black tracking-tight text-slate-950 dark:text-white sm:text-[38px]">
                  Sign in
                </h2>
                <p className="mb-8 max-w-[340px] text-[15px] leading-relaxed text-slate-500 dark:text-slate-400">
                  Access your workforce workspace with the credentials and tenant slug your organization uses every day.
                </p>

                <div className="mb-6 grid gap-3 sm:grid-cols-3">
                  {TRUST_POINTS.map((item) => (
                    <div key={item.label} className="rounded-2xl border border-slate-200/80 bg-white/70 px-3 py-3 shadow-[0_8px_24px_rgba(15,23,42,0.05)] dark:border-white/10 dark:bg-white/[0.03]">
                      <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-slate-400 dark:text-slate-500">{item.label}</p>
                      <p className="mt-2 text-[12px] leading-snug text-slate-700 dark:text-slate-300">{item.value}</p>
                    </div>
                  ))}
                </div>

                <div className="space-y-5">
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
                    hint="Your company's unique workspace identifier"
                  >
                    <input id="li-ws" type="text" value={tenantSlug} onChange={e => setTenantSlug(e.target.value)}
                      className="auth-input" placeholder="your-workspace" autoComplete="organization" required />
                  </FormField>
                </div>

                <AuthFeedback error={error} info={info} />

                <button type="submit" disabled={loading}
                  className="auth-btn mt-6 disabled:cursor-not-allowed disabled:opacity-60">
                  {loading
                    ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                    : 'Sign in'}
                </button>

                <div className="mt-8 flex flex-wrap items-center justify-center gap-3 border-t border-slate-100 pt-6 dark:border-white/[0.06]">
                  {['AES-256 at rest · TLS in transit', 'Tenant data isolation', 'Audit-logged'].map(t => (
                    <span key={t} className="rounded-full border border-slate-200 bg-slate-50 px-3 py-1.5 text-[10px] font-medium text-slate-400 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-500">
                      {t}
                    </span>
                  ))}
                </div>
              </form>
            )}

            {/* ── FORGOT PASSWORD ──────────────────────────────────────────── */}
            {mode === 'forgot' && (
              <form onSubmit={handleForgot} noValidate>
                <button type="button" onClick={() => go('login')}
                  className="mb-6 flex items-center gap-1.5 text-[13px] font-medium text-slate-400 hover:text-slate-600 dark:hover:text-slate-300">
                  ← Back to sign in
                </button>
                <div className="mb-5 flex h-11 w-11 items-center justify-center rounded-2xl bg-blue-50 ring-1 ring-blue-200/60 dark:bg-blue-500/10 dark:ring-blue-500/20">
                  <Mail className="h-5 w-5 text-blue-600 dark:text-blue-400" />
                </div>
                <h2 className="mb-1 text-[28px] font-black tracking-tight text-slate-950 dark:text-white">Reset password</h2>
                <p className="mb-8 text-[14px] text-slate-500 dark:text-slate-400">
                  We'll send a reset code to your email address.
                </p>

                <div className="space-y-5">
                  <FormField label="Work Email">
                    <input id="fg-em" type="email" value={forgotEmail || email}
                      onChange={e => setForgotEmail(e.target.value)}
                      className="auth-input" placeholder="you@company.com" autoComplete="email" required />
                  </FormField>
                  <FormField label="Workspace" hint="Optional — helps locate your account">
                    <input id="fg-ws" type="text" value={tenantSlug}
                      onChange={e => setTenantSlug(e.target.value)}
                      className="auth-input" placeholder="your-workspace" />
                  </FormField>
                </div>

                <AuthFeedback error={error} info={info} />

                <button type="submit" disabled={loading} className="auth-btn mt-6 disabled:cursor-not-allowed disabled:opacity-60">
                  {loading ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" /> : 'Send reset code'}
                </button>
                <p className="mt-4 text-center text-[12px] text-slate-400">
                  Have a code already?{' '}
                  <button type="button" onClick={() => go('reset')} className="font-medium text-blue-500 hover:text-blue-600 dark:text-blue-400">Enter it here</button>
                </p>
              </form>
            )}

            {/* ── SET NEW PASSWORD ─────────────────────────────────────────── */}
            {mode === 'reset' && (
              <form onSubmit={handleReset} noValidate>
                <button type="button" onClick={() => go('forgot')}
                  className="mb-6 flex items-center gap-1.5 text-[13px] font-medium text-slate-400 hover:text-slate-600 dark:hover:text-slate-300">
                  ← Back
                </button>
                <div className="mb-5 flex h-11 w-11 items-center justify-center rounded-2xl bg-blue-50 ring-1 ring-blue-200/60 dark:bg-blue-500/10 dark:ring-blue-500/20">
                  <KeyRound className="h-5 w-5 text-blue-600 dark:text-blue-400" />
                </div>
                <h2 className="mb-1 text-[28px] font-black tracking-tight text-slate-950 dark:text-white">New password</h2>
                <p className="mb-8 text-[14px] text-slate-500 dark:text-slate-400">Enter the code from your email and set a new password.</p>

                <div className="space-y-5">
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
                </div>

                <AuthFeedback error={error} info={info} />

                <button type="submit" disabled={loading} className="auth-btn mt-6 disabled:cursor-not-allowed disabled:opacity-60">
                  {loading ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" /> : 'Update password'}
                </button>
              </form>
            )}

            {/* ── MFA CHALLENGE ────────────────────────────────────────────── */}
            {mode === 'mfa' && (
              <form onSubmit={handleMfa} noValidate>
                <button type="button" onClick={() => { setMode('login'); setTotpCode(''); }}
                  className="mb-6 flex items-center gap-1.5 text-[13px] font-medium text-slate-400 hover:text-slate-600 dark:hover:text-slate-300">
                  ← Back to sign in
                </button>
                <div className="mb-5 flex h-11 w-11 items-center justify-center rounded-2xl bg-blue-50 ring-1 ring-blue-200/60 dark:bg-blue-500/10 dark:ring-blue-500/20">
                  <Smartphone className="h-5 w-5 text-blue-600 dark:text-blue-400" />
                </div>
                <h2 className="mb-1 text-[28px] font-black tracking-tight text-slate-950 dark:text-white">Two-factor auth</h2>
                <p className="mb-8 text-[14px] text-slate-500 dark:text-slate-400">
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

          </div>
            </div>
          </div>{/* /glass card wrapper */}

          <p className="relative z-10 mt-8 max-w-[440px] text-center text-[11px] text-slate-500 dark:text-slate-500">
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

      {/* Global scoped styles for auth inputs and button */}
      <style>{`
        .auth-input {
          display: block; width: 100%;
          border-radius: 16px;
          border: 1px solid rgba(148, 163, 184, 0.28);
          background:
            linear-gradient(180deg, rgba(255,255,255,0.88), rgba(248,250,252,0.98));
          padding: 14px 16px;
          font-size: 15px;
          color: #0f172a;
          box-shadow:
            inset 0 1px 0 rgba(255,255,255,0.9),
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
