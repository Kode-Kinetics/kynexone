'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import {
  TrendingUp, Users, Building2, CreditCard, Brain,
  Activity, AlertTriangle, RefreshCw, ChevronRight,
  Zap, XCircle, CheckCircle, Clock, ArrowUpRight, ArrowDownRight,
} from 'lucide-react';
import {
  platformApi,
  type PlatformStats,
  type PlatformTenantSummary,
  type PlatformAuditLog,
  type PlatformHealthStatus,
} from '@/src/api/platform';

// ── Skeleton ──────────────────────────────────────────────────────────────────

function Skeleton({ className }: { className: string }) {
  return (
    <div
      className={`rounded bg-white/[0.05] animate-pulse ${className}`}
      aria-hidden="true"
    />
  );
}

// ── KPI Metric Card ───────────────────────────────────────────────────────────

function KpiCard({
  label, value, sub, icon: Icon, loading,
  trend, accent = 'blue',
}: {
  label: string;
  value: string | number;
  sub?: string;
  icon: React.ElementType;
  loading: boolean;
  trend?: { dir: 'up' | 'down' | 'flat'; label: string };
  accent?: 'blue' | 'emerald' | 'amber' | 'rose' | 'purple' | 'cyan';
}) {
  const accentMap: Record<string, { bg: string; icon: string; val: string; border: string }> = {
    blue:    { bg: 'bg-blue-500/10',    icon: 'text-blue-400',    val: 'text-blue-300',    border: 'border-blue-500/15' },
    emerald: { bg: 'bg-emerald-500/10', icon: 'text-emerald-400', val: 'text-emerald-300', border: 'border-emerald-500/15' },
    amber:   { bg: 'bg-amber-500/10',   icon: 'text-amber-400',   val: 'text-amber-300',   border: 'border-amber-500/15' },
    rose:    { bg: 'bg-rose-500/10',    icon: 'text-rose-400',    val: 'text-rose-300',    border: 'border-rose-500/15' },
    purple:  { bg: 'bg-purple-500/10',  icon: 'text-purple-400',  val: 'text-purple-300',  border: 'border-purple-500/15' },
    cyan:    { bg: 'bg-cyan-500/10',    icon: 'text-cyan-400',    val: 'text-cyan-300',    border: 'border-cyan-500/15' },
  };
  const a = accentMap[accent];

  return (
    <div className={`relative bg-[#161b22] border ${a.border} rounded-xl p-5 overflow-hidden hover:border-white/10 transition-all duration-200 group`}>
      {/* Subtle top gradient line */}
      <div className={`absolute inset-x-0 top-0 h-px ${a.bg.replace('/10', '/40')}`} />

      <div className="flex items-start justify-between mb-3">
        <div className={`h-8 w-8 rounded-lg ${a.bg} border ${a.border} flex items-center justify-center`}>
          <Icon className={`h-4 w-4 ${a.icon}`} />
        </div>
        {trend && !loading && (
          <div className={`flex items-center gap-0.5 text-[11px] font-medium ${
            trend.dir === 'up' ? 'text-emerald-400' : trend.dir === 'down' ? 'text-rose-400' : 'text-slate-500'
          }`}>
            {trend.dir === 'up' && <ArrowUpRight className="h-3 w-3" />}
            {trend.dir === 'down' && <ArrowDownRight className="h-3 w-3" />}
            {trend.label}
          </div>
        )}
      </div>

      {loading ? (
        <div className="space-y-2">
          <Skeleton className="h-7 w-24" />
          <Skeleton className="h-3.5 w-32" />
        </div>
      ) : (
        <>
          <p className="text-2xl font-bold text-white tabular-nums leading-none mb-1">{value}</p>
          {sub && <p className="text-[12px] text-slate-500 leading-tight">{sub}</p>}
        </>
      )}

      <p className="text-[11px] font-semibold text-slate-600 uppercase tracking-widest mt-3">{label}</p>
    </div>
  );
}

// ── Plan Distribution Bar ─────────────────────────────────────────────────────

function PlanBar({ plans, total, loading }: {
  plans: PlatformStats['tenantsByPlan'] | undefined;
  total: number;
  loading: boolean;
}) {
  if (loading || !plans || total === 0) {
    return (
      <div className="space-y-3">
        {['Enterprise', 'Growth', 'Starter', 'Trial'].map(p => (
          <div key={p} className="flex items-center gap-3">
            <span className="text-xs text-slate-600 w-20 shrink-0">{p}</span>
            <Skeleton className="flex-1 h-2" />
            <Skeleton className="h-3 w-4" />
          </div>
        ))}
      </div>
    );
  }

  const rows: { label: string; value: number; cls: string }[] = [
    { label: 'Enterprise', value: plans.enterprise, cls: 'bg-amber-500' },
    { label: 'Growth',     value: plans.growth,     cls: 'bg-purple-500' },
    { label: 'Starter',    value: plans.starter,    cls: 'bg-blue-500' },
    { label: 'Trial',      value: plans.trial,      cls: 'bg-slate-500' },
  ];

  // Maps a percentage to the closest Tailwind width fraction class (no inline styles)
  function pctClass(pct: number) {
    if (pct <= 0)   return 'w-0';
    if (pct <= 8)   return 'w-[8%]';
    if (pct <= 17)  return 'w-1/6';
    if (pct <= 25)  return 'w-1/4';
    if (pct <= 33)  return 'w-1/3';
    if (pct <= 42)  return 'w-5/12';
    if (pct <= 50)  return 'w-1/2';
    if (pct <= 58)  return 'w-7/12';
    if (pct <= 67)  return 'w-2/3';
    if (pct <= 75)  return 'w-3/4';
    if (pct <= 83)  return 'w-5/6';
    if (pct <= 92)  return 'w-11/12';
    return 'w-full';
  }

  return (
    <div className="space-y-3">
      {rows.map(r => {
        const pct = total > 0 ? (r.value / total) * 100 : 0;
        return (
          <div key={r.label} className="flex items-center gap-3">
            <span className="text-xs text-slate-400 w-20 shrink-0">{r.label}</span>
            <div className="flex-1 h-1.5 bg-white/[0.06] rounded-full overflow-hidden">
              <div className={`h-full rounded-full transition-all duration-700 ${r.cls} ${pctClass(pct)}`} />
            </div>
            <span className="text-xs text-slate-500 tabular-nums w-4 text-right">{r.value}</span>
          </div>
        );
      })}
    </div>
  );
}

// ── Health Status Component ───────────────────────────────────────────────────

function HealthRow({ label, status, loading }: {
  label: string;
  status: 'ok' | 'error' | 'unknown' | 'configured' | 'not_configured' | 'disconnected';
  loading: boolean;
}) {
  if (loading) return (
    <div className="flex items-center justify-between py-2.5 border-b border-white/[0.04] last:border-0">
      <Skeleton className="h-3.5 w-24" />
      <Skeleton className="h-3.5 w-12" />
    </div>
  );

  const isOk   = status === 'ok' || status === 'configured';
  const isWarn  = status === 'not_configured' || status === 'unknown';
  const isError = status === 'error' || status === 'disconnected';

  const label2Map: Record<string, string> = {
    ok:             'Healthy',
    configured:     'Configured',
    not_configured: 'Not set',
    unknown:        'Unknown',
    error:          'Error',
    disconnected:   'Disconnected',
  };

  return (
    <div className="flex items-center justify-between py-2.5 border-b border-white/[0.04] last:border-0">
      <span className="text-sm text-slate-300">{label}</span>
      <div className="flex items-center gap-1.5">
        {isOk
          ? <CheckCircle className="h-3.5 w-3.5 text-emerald-400" />
          : isWarn
          ? <AlertTriangle className="h-3.5 w-3.5 text-amber-400" />
          : isError
          ? <XCircle className="h-3.5 w-3.5 text-rose-400" />
          : <AlertTriangle className="h-3.5 w-3.5 text-slate-600" />
        }
        <span className={`text-[11px] font-medium ${isOk ? 'text-emerald-400' : isError ? 'text-rose-400' : 'text-amber-400'}`}>
          {label2Map[status] ?? status}
        </span>
      </div>
    </div>
  );
}

// ── At-Risk Tenant Row ────────────────────────────────────────────────────────

function RiskRow({ t }: { t: PlatformTenantSummary }) {
  const status = t.subscription?.status ?? 'Unknown';
  const exp = t.subscription?.expiresAtUtc
    ? Math.ceil((new Date(t.subscription.expiresAtUtc).getTime() - Date.now()) / 86400000)
    : null;
  const isSuspended = status === 'Suspended';
  const isPastDue   = status === 'PastDue';
  const isExpiring  = !isSuspended && !isPastDue && exp !== null && exp <= 7;

  const riskLabel = isSuspended ? 'Suspended' : isPastDue ? 'Past Due' : isExpiring ? (exp! <= 0 ? 'Expired' : `${exp}d left`) : status;
  const riskCls   = isSuspended ? 'text-rose-400' : isPastDue ? 'text-amber-400' : isExpiring ? 'text-amber-400' : 'text-slate-500';
  const dotCls    = isSuspended ? 'bg-rose-500' : 'bg-amber-500';

  return (
    <Link
      href={`/platform/tenants/${t.id}`}
      className="flex items-center gap-3 px-4 py-3 border-b border-white/[0.04] last:border-0 hover:bg-white/[0.03] transition-colors group"
    >
      <span className={`h-1.5 w-1.5 rounded-full shrink-0 ${dotCls}`} />
      <div className="flex-1 min-w-0">
        <p className="text-sm text-white font-medium truncate group-hover:text-blue-300 transition-colors">{t.name}</p>
        <p className="text-[11px] text-slate-600 font-mono">/{t.slug}</p>
      </div>
      <div className="text-right shrink-0">
        <p className={`text-xs font-semibold ${riskCls}`}>{riskLabel}</p>
        {t.subscription?.plan && (
          <p className="text-[11px] text-slate-600 capitalize">{t.subscription.plan}</p>
        )}
      </div>
      <ChevronRight className="h-3 w-3 text-slate-700 group-hover:text-slate-400 transition-colors" />
    </Link>
  );
}

// ── Audit Log Row ─────────────────────────────────────────────────────────────

// Maps exact backend Action values → badge style
const AUDIT_BADGE: Record<string, string> = {
  TenantCreated:           'text-emerald-400 bg-emerald-500/10 border-emerald-500/20',
  AdminCreated:            'text-emerald-400 bg-emerald-500/10 border-emerald-500/20',
  Suspended:               'text-rose-400 bg-rose-500/10 border-rose-500/20',
  Reactivated:             'text-blue-400 bg-blue-500/10 border-blue-500/20',
  FeatureEnabled:          'text-cyan-400 bg-cyan-500/10 border-cyan-500/20',
  FeatureDisabled:         'text-slate-400 bg-white/[0.05] border-white/[0.08]',
  SupportAccessStarted:    'text-orange-400 bg-orange-500/10 border-orange-500/20',
  SupportAccessEnded:      'text-slate-400 bg-white/[0.05] border-white/[0.08]',
  PasswordResetRequested:  'text-amber-400 bg-amber-500/10 border-amber-500/20',
  ForcePasswordReset:      'text-amber-400 bg-amber-500/10 border-amber-500/20',
  SubscriptionUpdated:     'text-purple-400 bg-purple-500/10 border-purple-500/20',
  PlanPriceUpdated:        'text-purple-400 bg-purple-500/10 border-purple-500/20',
  InvoiceCreated:          'text-blue-400 bg-blue-500/10 border-blue-500/20',
  InvoiceSent:             'text-blue-400 bg-blue-500/10 border-blue-500/20',
  Updated:                 'text-slate-400 bg-white/[0.05] border-white/[0.08]',
};

const AUDIT_LABEL: Record<string, string> = {
  TenantCreated:          'Tenant Created',
  AdminCreated:           'Admin Added',
  Suspended:              'Suspended',
  Reactivated:            'Reactivated',
  FeatureEnabled:         'Feature On',
  FeatureDisabled:        'Feature Off',
  SupportAccessStarted:   'Support Started',
  SupportAccessEnded:     'Support Ended',
  PasswordResetRequested: 'Password Reset',
  ForcePasswordReset:     'Force Reset',
  SubscriptionUpdated:    'Subscription',
  PlanPriceUpdated:       'Plan Price',
  InvoiceCreated:         'Invoice',
  InvoiceSent:            'Invoice Sent',
  Updated:                'Updated',
};

function AuditRow({ log }: { log: PlatformAuditLog }) {
  const cls   = AUDIT_BADGE[log.action] ?? 'text-slate-400 bg-white/[0.04] border-white/[0.06]';
  const label = AUDIT_LABEL[log.action] ?? log.action.replace(/([A-Z])/g, ' $1').trim();

  // Show: who performed it, or which tenant it affected
  const actor = log.performedByName && log.performedByName !== 'platform_admin'
    ? log.performedByName
    : log.tenantName ?? 'Platform Admin';

  function relative(d: string) {
    const s = Math.floor((Date.now() - new Date(d).getTime()) / 1000);
    if (s < 60) return `${s}s ago`;
    if (s < 3600) return `${Math.floor(s / 60)}m ago`;
    if (s < 86400) return `${Math.floor(s / 3600)}h ago`;
    return new Date(d).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
  }

  return (
    <div className="flex items-center gap-3 px-4 py-2.5 border-b border-white/[0.04] last:border-0 hover:bg-white/[0.02] transition-colors">
      <span className={`text-[10px] font-bold px-1.5 py-0.5 rounded border shrink-0 whitespace-nowrap ${cls}`}>
        {label}
      </span>
      <p className="flex-1 text-xs text-slate-400 truncate min-w-0">{actor}</p>
      <span className="text-[11px] text-slate-600 shrink-0 tabular-nums">{relative(log.createdAtUtc)}</span>
    </div>
  );
}

// ── Quick Action Button ───────────────────────────────────────────────────────

function QuickAction({ icon: Icon, label, href, accent = 'default' }: {
  icon: React.ElementType; label: string; href: string; accent?: string;
}) {
  const accentCls: Record<string, string> = {
    default: 'bg-white/[0.04] border-white/[0.08] text-slate-300 hover:bg-white/[0.07] hover:border-white/[0.14]',
    blue:    'bg-blue-500/10 border-blue-500/20 text-blue-300 hover:bg-blue-500/15 hover:border-blue-500/30',
    amber:   'bg-amber-500/10 border-amber-500/20 text-amber-300 hover:bg-amber-500/15 hover:border-amber-500/30',
    emerald: 'bg-emerald-500/10 border-emerald-500/20 text-emerald-300 hover:bg-emerald-500/15 hover:border-emerald-500/30',
  };
  return (
    <Link
      href={href}
      className={`flex flex-col items-center justify-center gap-2 p-4 rounded-xl border text-center transition-all duration-150 ${accentCls[accent] ?? accentCls.default}`}
    >
      <Icon className="h-5 w-5" />
      <span className="text-xs font-medium leading-tight">{label}</span>
    </Link>
  );
}

// ── Main Page ─────────────────────────────────────────────────────────────────

export default function CommandCenter() {
  const router = useRouter();
  const [stats,   setStats]   = useState<PlatformStats | null>(null);
  const [tenants, setTenants] = useState<PlatformTenantSummary[]>([]);
  const [logs,    setLogs]    = useState<PlatformAuditLog[]>([]);
  const [health,  setHealth]  = useState<PlatformHealthStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshed, setRefreshed] = useState<Date | null>(null);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    const [s, t, a, h] = await Promise.allSettled([
      platformApi.getStats(),
      platformApi.listTenants(),
      platformApi.getAuditLogs(undefined, 1, 15),
      platformApi.getHealth(),
    ]);
    if (s.status === 'fulfilled') setStats(s.value);
    if (t.status === 'fulfilled') setTenants(t.value);
    if (a.status === 'fulfilled') setLogs(a.value.logs);
    if (h.status === 'fulfilled') setHealth(h.value);
    setRefreshed(new Date());
    setLoading(false);
  }, []);

  const atRisk = tenants.filter(t => {
    const s = (t.subscription?.status ?? '').toLowerCase();
    if (s === 'suspended' || s === 'pastdue') return true;
    const exp = t.subscription?.expiresAtUtc
      ? Math.ceil((new Date(t.subscription.expiresAtUtc).getTime() - Date.now()) / 86400000)
      : null;
    return exp !== null && exp <= 7;
  }).slice(0, 6);

  const mrr = stats?.estimatedMrr ?? 0;
  const arr = mrr * 12;

  function fmt(n: number) {
    if (n >= 1000000) return `$${(n / 1000000).toFixed(1)}M`;
    if (n >= 1000)    return `$${(n / 1000).toFixed(1)}K`;
    return `$${n.toLocaleString()}`;
  }

  return (
    <div className="space-y-6">

      {/* ── Header ─────────────────────────────────────────────────────────── */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white tracking-tight">Command Center</h1>
          <p className="text-xs text-slate-500 mt-0.5">
            {refreshed
              ? <>Last updated <span className="text-slate-400">{refreshed.toLocaleTimeString()}</span></>
              : 'Loading…'}
          </p>
        </div>
        <button
          type="button"
          onClick={load}
          disabled={loading}
          aria-label="Refresh dashboard"
          className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/[0.08] hover:border-white/20 rounded-lg transition-all disabled:opacity-40"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
        </button>
      </div>

      {/* ── KPI Grid ───────────────────────────────────────────────────────── */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4 xl:grid-cols-5">
        <KpiCard
          label="Monthly ARR" value={loading ? '—' : fmt(arr)}
          sub={`MRR ${fmt(mrr)}`}
          icon={CreditCard} loading={loading} accent="blue"
          trend={mrr > 0 ? { dir: 'up', label: 'Active subs' } : undefined}
        />
        <KpiCard
          label="Active Tenants" value={loading ? '—' : stats?.activeTenants ?? 0}
          sub={`of ${stats?.totalTenants ?? 0} total`}
          icon={Building2} loading={loading} accent="emerald"
        />
        <KpiCard
          label="Platform Users" value={loading ? '—' : stats?.totalUsers ?? 0}
          sub="across all tenants"
          icon={Users} loading={loading} accent="purple"
        />
        <KpiCard
          label="Needs Attention" value={loading ? '—' : (stats?.overdueCount ?? 0) + (stats?.suspendedCount ?? 0) + (stats?.expiringCount ?? 0)}
          sub={loading ? '' : `${stats?.overdueCount ?? 0} overdue · ${stats?.expiringCount ?? 0} expiring`}
          icon={AlertTriangle} loading={loading}
          accent={(((stats?.overdueCount ?? 0) + (stats?.suspendedCount ?? 0)) > 0) ? 'rose' : 'amber'}
        />
        <KpiCard
          label="Employees" value={loading ? '—' : stats?.totalEmployees ?? 0}
          sub="workforce managed"
          icon={Activity} loading={loading} accent="cyan"
        />
      </div>

      {/* ── Middle Row ─────────────────────────────────────────────────────── */}
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">

        {/* Plan Distribution */}
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          <div className="flex items-center justify-between px-5 py-3 border-b border-white/[0.06]">
            <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Plan Distribution</p>
            <Link href="/platform/plans" className="text-[11px] text-blue-400 hover:text-blue-300 transition-colors">Plans →</Link>
          </div>
          <div className="px-5 py-4">
            <PlanBar plans={stats?.tenantsByPlan} total={stats?.totalTenants ?? 0} loading={loading} />
          </div>
        </div>

        {/* At-Risk Tenants */}
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          <div className="flex items-center justify-between px-5 py-3 border-b border-white/[0.06]">
            <div className="flex items-center gap-2">
              <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">At Risk</p>
              {!loading && atRisk.length > 0 && (
                <span className="text-[10px] font-bold text-rose-400 bg-rose-500/10 border border-rose-500/20 px-1.5 py-0.5 rounded-full">
                  {atRisk.length}
                </span>
              )}
            </div>
            <Link href="/platform/tenants" className="text-[11px] text-blue-400 hover:text-blue-300 transition-colors">All →</Link>
          </div>
          {loading ? (
            <div className="px-4 py-3 space-y-3">
              {[1,2,3].map(i => (
                <div key={i} className="flex items-center gap-3">
                  <Skeleton className="h-1.5 w-1.5 rounded-full" />
                  <div className="flex-1 space-y-1">
                    <Skeleton className="h-3.5 w-28" />
                    <Skeleton className="h-3 w-16" />
                  </div>
                  <Skeleton className="h-3.5 w-16" />
                </div>
              ))}
            </div>
          ) : atRisk.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-8 gap-2">
              <CheckCircle className="h-5 w-5 text-emerald-500" />
              <p className="text-xs text-slate-600">All tenants healthy</p>
            </div>
          ) : (
            atRisk.map(t => <RiskRow key={t.id} t={t} />)
          )}
        </div>

        {/* System Health */}
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          <div className="flex items-center justify-between px-5 py-3 border-b border-white/[0.06]">
            <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">System Health</p>
            <Link href="/platform/system-health" className="text-[11px] text-blue-400 hover:text-blue-300 transition-colors">Details →</Link>
          </div>
          <div className="px-4 py-1">
            <HealthRow label="Database" status={health?.components.database.status ?? 'unknown'} loading={loading} />
            <HealthRow label="SMTP"     status={health?.components.smtp.status ?? 'unknown'}     loading={loading} />
            <HealthRow label="Redis"    status={health?.components.redis.status ?? 'unknown'}    loading={loading} />
          </div>
          {!loading && health && (
            <div className="px-4 pt-2 pb-3">
              <div className={`flex items-center gap-2 px-3 py-2 rounded-lg ${
                health.status === 'healthy' ? 'bg-emerald-500/5 border border-emerald-500/15' : 'bg-amber-500/5 border border-amber-500/15'
              }`}>
                <span className="relative flex h-1.5 w-1.5 shrink-0">
                  <span className={`animate-ping absolute inline-flex h-full w-full rounded-full opacity-50 ${health.status === 'healthy' ? 'bg-emerald-400' : 'bg-amber-400'}`} />
                  <span className={`relative inline-flex h-1.5 w-1.5 rounded-full ${health.status === 'healthy' ? 'bg-emerald-500' : 'bg-amber-500'}`} />
                </span>
                <span className={`text-[11px] font-medium ${health.status === 'healthy' ? 'text-emerald-400' : 'text-amber-400'}`}>
                  {health.status === 'healthy' ? 'All systems operational' : 'Degraded'}
                </span>
                <span className="ml-auto text-[10px] text-slate-700 font-mono">{health.version}</span>
              </div>
            </div>
          )}
        </div>
      </div>

      {/* ── Bottom Row ─────────────────────────────────────────────────────── */}
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">

        {/* Quick Actions */}
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          <div className="px-5 py-3 border-b border-white/[0.06]">
            <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Quick Actions</p>
          </div>
          <div className="p-4 grid grid-cols-2 gap-2">
            <QuickAction icon={Building2} label="New Tenant"       href="/platform/tenants"        accent="blue" />
            <QuickAction icon={CreditCard} label="View Billing"    href="/platform/billing"         accent="emerald" />
            <QuickAction icon={Zap}        label="Feature Flags"   href="/platform/plans"           accent="amber" />
            <QuickAction icon={TrendingUp} label="AI Usage"        href="/platform/ai-usage"        accent="default" />
          </div>
        </div>

        {/* Recent Activity */}
        <div className="lg:col-span-2 bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          <div className="flex items-center justify-between px-5 py-3 border-b border-white/[0.06]">
            <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Recent Activity</p>
            <Link href="/platform/audit-logs" className="text-[11px] text-blue-400 hover:text-blue-300 transition-colors">Full log →</Link>
          </div>
          {loading ? (
            <div className="px-4 py-3 space-y-3">
              {[1,2,3,4,5].map(i => (
                <div key={i} className="flex items-center gap-3">
                  <Skeleton className="h-5 w-24 rounded" />
                  <Skeleton className="flex-1 h-3.5" />
                  <Skeleton className="h-3 w-10" />
                </div>
              ))}
            </div>
          ) : logs.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-8 gap-2">
              <Clock className="h-5 w-5 text-slate-700" />
              <p className="text-xs text-slate-600">No activity recorded yet</p>
            </div>
          ) : (
            <div>
              {logs.map(log => <AuditRow key={log.id} log={log} />)}
            </div>
          )}
        </div>
      </div>

    </div>
  );
}
