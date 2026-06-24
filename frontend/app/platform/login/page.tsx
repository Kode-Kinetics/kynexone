'use client';

import { useState, useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { BarChart3, CheckCircle2, Eye, EyeOff, ShieldCheck, Sparkles, Users } from 'lucide-react';
import { platformApi } from '@/src/api/platform';
import { Logo } from '@/src/components/Logo';

type ErrorKind = 'invalid_credentials' | 'not_configured' | 'network' | null;

function errorMessage(kind: ErrorKind): string {
  switch (kind) {
    case 'invalid_credentials': return 'Invalid platform admin credentials. Please check your email and password.';
    case 'not_configured': return 'Platform admin access is not configured on this server. Set PLATFORM_ADMIN_EMAIL and PLATFORM_ADMIN_PASSWORD environment variables.';
    case 'network': return 'Cannot reach the server. Check that the backend is running and reachable.';
    default: return '';
  }
}

const INSIGHT_PILLS = [
  'Tenant health at a glance',
  'Security events and login activity',
  'Scoped access for internal operators',
];

const TRUST_POINTS = [
  { label: 'Admin scope', value: 'Internal only' },
  { label: 'Session model', value: 'Token-backed access' },
  { label: 'Audit posture', value: 'Traceable actions' },
];

const PLATFORM_METRICS = [
  { label: 'Tenants monitored', value: '482', sub: '+18 this week', color: 'text-cyan-300', icon: Users },
  { label: 'Login success', value: '99.4%', sub: '7-day average', color: 'text-emerald-300', icon: CheckCircle2 },
  { label: 'Security alerts', value: '12', sub: 'Under review', color: 'text-amber-300', icon: ShieldCheck },
];

export default function PlatformLoginPage() {
  const router = useRouter();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [errorKind, setErrorKind] = useState<ErrorKind>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (typeof window !== 'undefined' && localStorage.getItem('platform_access_token')) {
      router.replace('/platform/dashboard');
    }
  }, [router]);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setErrorKind(null);
    setLoading(true);
    try {
      const { token } = await platformApi.login(email, password);
      localStorage.setItem('platform_access_token', token);
      router.replace('/platform/dashboard');
    } catch (err: unknown) {
      const status = (err as { response?: { status?: number } })?.response?.status;
      if (status === 401) {
        setErrorKind('invalid_credentials');
      } else if (status === 503) {
        setErrorKind('not_configured');
      } else if (!(err as { response?: unknown })?.response) {
        setErrorKind('network');
      } else {
        setErrorKind('invalid_credentials');
      }
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="relative flex min-h-screen overflow-hidden bg-[#f2f5ff] text-slate-900 dark:bg-[#040814] dark:text-white">
      <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.18),transparent_28%),radial-gradient(circle_at_80%_20%,rgba(94,235,255,0.10),transparent_22%),linear-gradient(180deg,rgba(255,255,255,0.42),transparent_35%)] dark:bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.22),transparent_28%),radial-gradient(circle_at_80%_20%,rgba(94,235,255,0.10),transparent_22%),linear-gradient(180deg,rgba(6,11,24,0.86),rgba(4,8,20,0.98))]" />
      <div className="pointer-events-none absolute inset-0 opacity-[0.22] mix-blend-soft-light [background-image:linear-gradient(rgba(47,107,255,0.08)_1px,transparent_1px),linear-gradient(90deg,rgba(47,107,255,0.08)_1px,transparent_1px)] [background-size:72px_72px]" />

      <div className="relative hidden overflow-hidden lg:flex lg:w-[58%] flex-col bg-[#050915]">
        <div className="absolute inset-0 bg-[radial-gradient(ellipse_80%_60%_at_30%_45%,rgba(37,99,235,0.14),transparent)]" />
        <div className="kx-grid absolute inset-0 opacity-90" />
        <p className="pointer-events-none absolute bottom-6 right-8 select-none text-[72px] font-black tracking-tighter text-white opacity-[0.015] -rotate-3">
          PLATFORM
        </p>

        <div className="relative z-10 flex h-full flex-col justify-between gap-6 px-12 py-10">
          <div className="flex items-center justify-between gap-4 dark">
            <div className="flex items-center gap-3">
              <Logo size="lg" />
              <div className="h-5 w-px bg-white/[0.1]" />
              <span className="text-[10px] font-bold tracking-[0.22em] uppercase text-blue-300/60">
                Platform Command Center
              </span>
            </div>
            <div className="rounded-full border border-white/10 bg-white/5 px-3 py-1 text-[10px] font-semibold uppercase tracking-[0.22em] text-white/45">
              Internal Access
            </div>
          </div>

          <div className="shrink-0">
            <h1 className="max-w-[640px] text-[50px] font-black leading-[1.02] tracking-tight xl:text-[60px]">
              <span className="text-white">Operate the platform</span><br />
              <span className="bg-gradient-to-r from-blue-400 via-cyan-300 to-sky-300 bg-clip-text text-transparent">
                with calm precision.
              </span>
            </h1>
            <p className="mt-4 max-w-[470px] text-[15px] leading-relaxed text-slate-300/80">
              Monitor tenants, review login activity, and manage the operational surface area from one restrained, audit-friendly control plane.
            </p>
            <div className="mt-6 flex flex-wrap gap-2">
              {INSIGHT_PILLS.map((pill) => (
                <span
                  key={pill}
                  className="rounded-full border border-white/10 bg-white/[0.05] px-3 py-1.5 text-[11px] font-medium text-white/65 backdrop-blur-sm"
                >
                  {pill}
                </span>
              ))}
            </div>
          </div>

          <div className="kx-win flex-1 overflow-hidden rounded-[28px] border border-white/[0.08] bg-[#08111f] shadow-[0_36px_100px_rgba(0,0,0,0.58),0_0_0_1px_rgba(255,255,255,0.04)]">
            <div className="flex items-center gap-3 border-b border-white/[0.06] bg-[#070d19] px-4 py-2.5">
              <div className="flex gap-1.5">
                <div className="h-2.5 w-2.5 rounded-full bg-[#ff5f57]" />
                <div className="h-2.5 w-2.5 rounded-full bg-[#febc2e]" />
                <div className="h-2.5 w-2.5 rounded-full bg-[#28c840]" />
              </div>
              <div className="flex flex-1 justify-center">
                <div className="flex items-center gap-2 rounded-full bg-white/[0.05] px-3 py-1.5">
                  <div className="kx-dot h-1.5 w-1.5 rounded-full bg-emerald-400" />
                  <span className="text-[11px] text-white/25">platform.kynexone.com / security</span>
                </div>
              </div>
              <div className="h-6 w-6 overflow-hidden rounded-full bg-gradient-to-br from-blue-500 to-indigo-600 ring-1 ring-white/[0.15]" />
            </div>

            <div className="flex h-full">
              <div className="flex flex-col items-center gap-1 border-r border-white/[0.05] bg-[#070d19] px-2 py-3">
                {[BarChart3, ShieldCheck, Users].map((Icon, i) => (
                  <div
                    key={i}
                    className={`flex h-8 w-8 items-center justify-center rounded-lg transition-colors ${
                      i === 0
                        ? 'bg-blue-500/20 text-blue-400 ring-1 ring-blue-500/20'
                        : 'text-white/[0.18] hover:bg-white/[0.04] hover:text-white/30'
                    }`}
                  >
                    <Icon className="h-3.5 w-3.5" />
                  </div>
                ))}
              </div>

              <div className="relative flex-1 overflow-hidden p-4">
                <div className="pointer-events-none absolute right-4 top-4 h-24 w-24 rounded-full border border-cyan-300/15 bg-cyan-300/5 blur-2xl" />
                <div className="mb-4 flex items-start justify-between">
                  <div>
                    <p className="text-[11px] font-bold uppercase tracking-widest text-white/30">Platform overview</p>
                    <p className="mt-0.5 text-sm font-bold text-white/80">Security and tenancy pulse</p>
                  </div>
                  <div className="flex items-center gap-1.5 rounded-full bg-emerald-500/[0.08] px-2.5 py-1 ring-1 ring-emerald-500/20">
                    <div className="kx-dot h-1.5 w-1.5 rounded-full bg-emerald-400" />
                    <span className="text-[10px] font-semibold text-emerald-400">Live</span>
                  </div>
                </div>

                <div className="mb-3 grid grid-cols-3 gap-2">
                  {PLATFORM_METRICS.map(({ label, value, sub, color, icon: Icon }) => (
                    <div key={label} className="rounded-2xl bg-white/[0.04] p-3 ring-1 ring-white/[0.06] backdrop-blur-sm">
                      <Icon className={`mb-2 h-3.5 w-3.5 ${color} opacity-70`} />
                      <p className={`text-[21px] font-black leading-none ${color}`}>{value}</p>
                      <p className="mt-1.5 text-[9px] font-medium uppercase tracking-wide text-white/25">{label}</p>
                      <p className="text-[9px] text-white/15">{sub}</p>
                    </div>
                  ))}
                </div>

                <div className="mb-3 rounded-2xl bg-white/[0.025] p-3 ring-1 ring-white/[0.04]">
                  <div className="mb-2 flex items-center justify-between">
                    <p className="text-[9px] font-bold uppercase tracking-widest text-white/20">Recent activity</p>
                    <p className="text-[9px] text-white/15">Last 60 min</p>
                  </div>
                  <div className="space-y-1">
                    {[
                      ['Tenant suspended', 'Finance East', '2m ago', 'bg-amber-400'],
                      ['Login reviewed', 'Platform admin', '12m ago', 'bg-cyan-400'],
                      ['Security note', 'SAML policy updated', '28m ago', 'bg-emerald-400'],
                    ].map(([name, action, time, dot]) => (
                      <div key={name} className="flex items-center gap-2.5 rounded-xl bg-white/[0.02] px-2.5 py-1.5 ring-1 ring-white/[0.03]">
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
          </div>

          <div className="flex flex-wrap justify-center gap-2 shrink-0">
            {['Audit logs', 'Tenant isolation', 'RBAC controls', 'MFA required'].map((pill) => (
              <span
                key={pill}
                className="rounded-full border border-white/[0.12] bg-white/[0.07] px-4 py-1.5 text-[12px] font-medium text-white/55 backdrop-blur-sm shadow-[inset_0_1px_0_rgba(255,255,255,0.08)]"
              >
                {pill}
              </span>
            ))}
          </div>

          <p className="shrink-0 text-[11px] text-white/20">
            A <span className="font-semibold text-white/30">Kode Kinetics</span> product · internal platform operations
          </p>
        </div>
      </div>

      <div className="relative flex flex-1 flex-col items-center justify-center overflow-hidden px-6 py-10 sm:px-10 sm:py-14">
        <div className="pointer-events-none absolute -top-24 -right-24 h-72 w-72 rounded-full bg-blue-300/20 blur-3xl dark:bg-blue-700/10" />
        <div className="pointer-events-none absolute -bottom-16 -left-16 h-64 w-64 rounded-full bg-indigo-300/20 blur-3xl dark:bg-indigo-700/10" />
        <div className="pointer-events-none absolute inset-0 bg-[linear-gradient(180deg,rgba(255,255,255,0.55),rgba(255,255,255,0.12))] dark:bg-[linear-gradient(180deg,rgba(4,8,20,0.18),rgba(4,8,20,0.04))]" />

        <div className="relative z-10 w-full max-w-[980px]">
          <div className="grid gap-8 lg:grid-cols-[0.92fr_1.08fr] lg:items-center">
            <div className="space-y-6">
              <div className="inline-flex items-center gap-2 rounded-full border border-sapphire/15 bg-sapphire/5 px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.24em] text-sapphire dark:border-sapphire/25 dark:bg-sapphire/10 dark:text-cyanAccent">
                <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
                Internal access
              </div>

              <div>
                <h1 className="max-w-[560px] text-[34px] font-black tracking-tight text-slate-950 dark:text-white sm:text-[40px] lg:text-[50px]">
                  Platform Admin
                </h1>
                <p className="mt-4 max-w-[460px] text-[15px] leading-relaxed text-slate-500 dark:text-slate-400">
                  Restricted to internal operators. Sign in to manage tenants, monitor platform health, and review security events without crossing into tenant space.
                </p>
              </div>

              <div className="grid gap-3 sm:grid-cols-3 lg:grid-cols-1">
                {TRUST_POINTS.map((item) => (
                  <div key={item.label} className="rounded-2xl border border-slate-200/80 bg-white/72 px-4 py-4 shadow-[0_10px_28px_rgba(15,23,42,0.05)] backdrop-blur-sm dark:border-white/10 dark:bg-white/[0.03]">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-slate-400 dark:text-slate-500">{item.label}</p>
                    <p className="mt-2 text-[12px] leading-snug text-slate-700 dark:text-slate-300">{item.value}</p>
                  </div>
                ))}
              </div>

              <div className="rounded-[28px] border border-white/70 bg-white/70 p-4 shadow-[0_18px_60px_rgba(37,99,235,0.08)] backdrop-blur-xl dark:border-white/[0.08] dark:bg-white/[0.03]">
                <div className="mb-3 flex items-center justify-between">
                  <div>
                    <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-slate-400 dark:text-slate-500">Operating focus</p>
                    <p className="mt-1 text-sm font-semibold text-slate-900 dark:text-white">Session-aware, audit-first, low-friction</p>
                  </div>
                  <span className="rounded-full border border-emerald-500/20 bg-emerald-500/10 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-[0.18em] text-emerald-600 dark:text-emerald-400">
                    Always on
                  </span>
                </div>
                <div className="space-y-2">
                  {[
                    ['Tenant health', '482 monitored', 'text-cyan-600 dark:text-cyan-300'],
                    ['Security events', '12 under review', 'text-amber-600 dark:text-amber-300'],
                    ['Operator scope', 'RBAC + MFA enforced', 'text-emerald-600 dark:text-emerald-300'],
                  ].map(([label, value, tone]) => (
                    <div key={label} className="flex items-center justify-between rounded-2xl border border-slate-200/70 bg-white/60 px-3 py-3 dark:border-white/10 dark:bg-white/[0.03]">
                      <div>
                        <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-slate-400 dark:text-slate-500">{label}</p>
                        <p className={`mt-1 text-[13px] font-semibold ${tone}`}>{value}</p>
                      </div>
                      <div className="h-2.5 w-2.5 rounded-full bg-sapphire/70" />
                    </div>
                  ))}
                </div>
              </div>
            </div>

            <div className="relative">
              <div className="absolute -inset-4 rounded-[38px] bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.24),transparent_40%),linear-gradient(135deg,rgba(47,107,255,0.18),rgba(94,235,255,0.06))] blur-2xl opacity-80" />
              <div className="relative z-10 rounded-[34px] border border-white/75 bg-white/78 p-[1px] shadow-[0_30px_120px_rgba(37,99,235,0.12),0_0_0_1px_rgba(255,255,255,0.6)] backdrop-blur-2xl dark:border-white/[0.08] dark:bg-white/[0.04] dark:shadow-[0_30px_120px_rgba(0,0,0,0.52),0_0_0_1px_rgba(255,255,255,0.04)]">
                <div className="rounded-[33px] bg-[linear-gradient(180deg,rgba(255,255,255,0.94),rgba(255,255,255,0.78))] px-6 py-6 sm:px-10 sm:py-10 dark:bg-[linear-gradient(180deg,rgba(9,16,32,0.92),rgba(7,12,24,0.88))]">
                  <div className="mb-6 flex items-start justify-between gap-4">
                    <div>
                      <div className="mb-3 inline-flex items-center gap-2 rounded-full border border-sapphire/15 bg-sapphire/5 px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.24em] text-sapphire dark:border-sapphire/25 dark:bg-sapphire/10 dark:text-cyanAccent">
                        <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
                        Platform login
                      </div>
                      <h2 className="text-[34px] font-black tracking-tight text-slate-950 dark:text-white sm:text-[38px]">
                        Sign in
                      </h2>
                      <p className="mt-2 max-w-[340px] text-[15px] leading-relaxed text-slate-500 dark:text-slate-400">
                        Enter your internal operator credentials to continue.
                      </p>
                    </div>
                    <div className="hidden rounded-2xl border border-emerald-500/15 bg-emerald-500/10 px-3 py-2 text-right sm:block">
                      <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-emerald-600 dark:text-emerald-400">Live</p>
                      <p className="mt-1 text-[12px] font-semibold text-slate-700 dark:text-slate-200">Command channel</p>
                    </div>
                  </div>

                  <form onSubmit={handleSubmit} className="space-y-5">
                    <div>
                      <label htmlFor="platform-email" className="mb-2 block text-[10px] font-bold tracking-[0.2em] uppercase text-slate-400 dark:text-slate-500">
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
                        className="auth-input"
                      />
                    </div>

                    <div>
                      <div className="mb-2 flex items-center justify-between">
                        <label htmlFor="platform-password" className="block text-[10px] font-bold tracking-[0.2em] uppercase text-slate-400 dark:text-slate-500">
                          Password
                        </label>
                        <span className="text-[11px] font-medium text-slate-400 dark:text-slate-500">Not for tenant users</span>
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
                          className="auth-input pr-11"
                        />
                        <button
                          type="button"
                          onClick={() => setShowPassword(v => !v)}
                          tabIndex={-1}
                          aria-label={showPassword ? 'Hide password' : 'Show password'}
                          className="absolute right-3 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-600 dark:hover:text-slate-300"
                        >
                          {showPassword ? <EyeOff className="h-[18px] w-[18px]" /> : <Eye className="h-[18px] w-[18px]" />}
                        </button>
                      </div>
                    </div>

                    {errorKind && (
                      <div className="mt-5 flex items-start gap-3 rounded-2xl border border-red-100 bg-red-50/80 px-4 py-3 shadow-[0_8px_24px_rgba(239,68,68,0.08)] dark:border-red-500/15 dark:bg-red-500/[0.07]">
                        <Sparkles className="mt-0.5 h-4 w-4 shrink-0 text-red-500" />
                        <p className="text-[13px] leading-relaxed text-red-700 dark:text-red-400">{errorMessage(errorKind)}</p>
                      </div>
                    )}

                    <button
                      type="submit"
                      disabled={loading}
                      className="auth-btn mt-6 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {loading ? (
                        <>
                          <span className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent" />
                          Signing in…
                        </>
                      ) : (
                        'Sign in'
                      )}
                    </button>
                  </form>

                  <div className="mt-8 flex flex-wrap items-center justify-center gap-3 border-t border-slate-100 pt-6 dark:border-white/[0.06]">
                    {['Audit-logged', 'MFA-aware', 'Tenant-safe'].map((pill) => (
                      <span key={pill} className="rounded-full border border-slate-200 bg-slate-50 px-3 py-1.5 text-[10px] font-medium text-slate-400 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-500">
                        {pill}
                      </span>
                    ))}
                  </div>

                  <p className="mt-6 text-center text-[12px] text-slate-500 dark:text-slate-500">
                    Tenant workspace login?{' '}
                    <a href="/login" className="font-medium text-blue-500 underline underline-offset-2 hover:text-blue-600 dark:text-blue-400">
                      Sign in here
                    </a>
                  </p>
                </div>
              </div>
            </div>
          </div>

          <p className="mt-8 text-center text-[11px] text-slate-500 dark:text-slate-600">
            A <span className="font-semibold text-slate-500">Kode Kinetics</span> product
          </p>
        </div>
      </div>

      <style>{`
        @keyframes kx-grid-shift { from{background-position:0 0} to{background-position:44px 44px} }
        @keyframes kx-window-in  { from{opacity:0;transform:translateY(20px)} to{opacity:1;transform:translateY(0)} }
        @keyframes kx-bar-in     { from{transform:scaleY(0)} to{transform:scaleY(1)} }
        @keyframes kx-pulse-dot  { 0%,100%{opacity:1} 50%{opacity:0.3} }
        @keyframes kx-count      { from{opacity:0;transform:translateY(6px)} to{opacity:1;transform:translateY(0)} }
        .kx-grid {
          background-image:
            linear-gradient(rgba(100,150,255,0.04) 1px, transparent 1px),
            linear-gradient(90deg, rgba(100,150,255,0.04) 1px, transparent 1px);
          background-size: 44px 44px;
        }
        .kx-win   { animation: kx-window-in 0.7s ease-out both }
        .kx-bar   { animation: kx-bar-in 0.4s ease-out both; transform-origin: bottom }
        .kx-dot   { animation: kx-pulse-dot 2s ease-in-out infinite }
        .kx-count { animation: kx-count 0.5s ease-out both }
        .auth-input {
          display: block;
          width: 100%;
          border-radius: 16px;
          border: 1px solid rgba(148, 163, 184, 0.28);
          background: linear-gradient(180deg, rgba(255,255,255,0.88), rgba(248,250,252,0.98));
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
            background: linear-gradient(180deg, rgba(255,255,255,0.06), rgba(255,255,255,0.03));
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
          display: flex;
          align-items: center;
          justify-content: center;
          gap: 8px;
          width: 100%;
          border-radius: 16px;
          background: linear-gradient(135deg, #1d4ed8 0%, #2f6bff 50%, #5eebff 170%);
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
    </div>
  );
}
