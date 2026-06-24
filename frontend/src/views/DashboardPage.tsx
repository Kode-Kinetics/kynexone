'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import dynamic from 'next/dynamic';
import {
  Activity,
  AlertTriangle,
  ArrowRight,
  ArrowUpRight,
  BadgeDollarSign,
  MessageSquareText,
  Building2,
  CalendarCheck,
  CalendarPlus,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Clock,
  FileCheck2,
  FileWarning,
  RefreshCw,
  ShieldAlert,
  Lightbulb,
  TrendingDown,
  TrendingUp,
  UserPlus,
  Users,
  Zap,
} from 'lucide-react';
import { dashboardApi } from '../api/dashboard';
import type { DashboardFull, ActivityFeedItem } from '../api/dashboard';
import { aiAssistantApi } from '../api/intelligence';
import type { AIInsight } from '../api/intelligence';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';

// ── Chart imports (SSR-safe) ──────────────────────────────────────────────────

const DashboardAttendanceTrendChart = dynamic(
  () => import('../components/charts/dashboard/DashboardAttendanceTrendChart').then((m) => m.DashboardAttendanceTrendChart),
  { ssr: false },
);
const DashboardPayrollByEntityChart = dynamic(
  () => import('../components/charts/dashboard/DashboardPayrollByEntityChart').then((m) => m.DashboardPayrollByEntityChart),
  { ssr: false },
);
const DashboardWorkforceMixChart = dynamic(
  () => import('../components/charts/dashboard/DashboardWorkforceMixChart').then((m) => m.DashboardWorkforceMixChart),
  { ssr: false },
);

// ── Constants ─────────────────────────────────────────────────────────────────

const MIX_BAR = ['bg-[#2F6BFF]', 'bg-[#00C896]', 'bg-[#5EEBFF]', 'bg-slate-400', 'bg-violet-400', 'bg-amber-500'];
const MIX_DOT = ['bg-[#2F6BFF]', 'bg-[#00C896]', 'bg-[#5EEBFF]', 'bg-slate-400', 'bg-violet-400', 'bg-amber-500'];

const MODULE_COLOR: Record<string, string> = {
  Payroll:    'bg-blue-500/10 text-blue-600 dark:text-blue-400',
  Leave:      'bg-emerald-500/10 text-emerald-700 dark:text-emerald-400',
  Attendance: 'bg-amber-500/10 text-amber-700 dark:text-amber-400',
  HR:         'bg-violet-500/10 text-violet-700 dark:text-violet-400',
};

// ── Helpers ───────────────────────────────────────────────────────────────────

function fmtMoney(n: number, currency = 'SAR'): string {
  if (Math.abs(n) >= 1_000_000) return `${currency} ${(n / 1_000_000).toFixed(2)}M`;
  if (Math.abs(n) >= 1_000) return `${currency} ${(n / 1_000).toFixed(1)}K`;
  return `${currency} ${Math.round(n).toLocaleString()}`;
}

function timeAgo(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 2) return 'Just now';
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.floor(hrs / 24);
  if (days === 1) return 'Yesterday';
  if (days < 7) return `${days}d ago`;
  return new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
}

function humaniseAction(action: string): string {
  return action
    .replace(/\./g, ' ')
    .replace(/([A-Z])/g, ' $1')
    .replace(/^./, (c) => c.toUpperCase())
    .trim();
}

// ── Skeleton ──────────────────────────────────────────────────────────────────

function Skel({ className }: { className: string }) {
  return <div className={`animate-pulse rounded-lg bg-slate-100 dark:bg-white/[0.06] ${className}`} />;
}

// ── Primitive components ──────────────────────────────────────────────────────

/** Panel card shell */
function Card({
  children,
  className = '',
  onClick,
  title,
  titleRight,
}: {
  children: React.ReactNode;
  className?: string;
  onClick?: () => void;
  title?: string;
  titleRight?: React.ReactNode;
}) {
  const base = 'rounded-2xl border border-slate-200/80 bg-white dark:border-white/[0.07] dark:bg-[#0e1729]/80';
  if (onClick) {
    return (
      <button
        type="button"
        onClick={onClick}
        className={`${base} text-left transition-shadow hover:shadow-md ${className}`}
      >
        {title && <CardHead title={title} right={titleRight} />}
        {children}
      </button>
    );
  }
  return (
    <div className={`${base} ${className}`}>
      {title && <CardHead title={title} right={titleRight} />}
      {children}
    </div>
  );
}

function CardHead({ title, right }: { title: string; right?: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-3 border-b border-slate-100 px-5 py-3 dark:border-white/[0.06]">
      <h3 className="text-[13px] font-bold text-slate-900 dark:text-white">{title}</h3>
      {right && <div className="shrink-0">{right}</div>}
    </div>
  );
}

// Sets bar width imperatively to avoid a `style=` JSX attribute (linter: no inline styles).
function DeptBar({ pct, colorClass }: { pct: number; colorClass: string }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (ref.current) ref.current.style.width = `${pct}%`;
  }, [pct]);
  return (
    <div className="h-1.5 overflow-hidden rounded-full bg-slate-100 dark:bg-white/[0.06]">
      <div ref={ref} className={`h-full rounded-full transition-all duration-700 ${colorClass}`} />
    </div>
  );
}

function Empty({ msg }: { msg: string }) {
  return (
    <div className="flex min-h-[72px] items-center justify-center rounded-xl border border-dashed border-slate-200 px-4 py-5 text-center text-xs text-slate-400 dark:border-white/[0.07] dark:text-slate-600">
      {msg}
    </div>
  );
}

// ── Attention bar ─────────────────────────────────────────────────────────────

interface AttentionItem {
  id: string;
  label: string;
  severity: 'critical' | 'warning';
  to: string;
  ctaLabel: string;
}

function AttentionBar({ items, router }: { items: AttentionItem[]; router: ReturnType<typeof useRouter> }) {
  if (items.length === 0) return null;
  const hasCritical = items.some((i) => i.severity === 'critical');
  return (
    <div
      role="alert"
      className={`flex flex-wrap items-center gap-x-4 gap-y-2 rounded-xl px-4 py-3 ${
        hasCritical
          ? 'border border-rose-300/50 bg-rose-50 dark:border-rose-500/20 dark:bg-rose-500/[0.06]'
          : 'border border-amber-300/50 bg-amber-50 dark:border-amber-500/20 dark:bg-amber-500/[0.06]'
      }`}
    >
      <span className="relative flex h-2 w-2 shrink-0">
        <span
          className={`absolute inline-flex h-full w-full animate-ping rounded-full opacity-75 ${
            hasCritical ? 'bg-rose-400' : 'bg-amber-400'
          }`}
        />
        <span
          className={`relative inline-flex h-2 w-2 rounded-full ${hasCritical ? 'bg-rose-500' : 'bg-amber-500'}`}
        />
      </span>
      <p className={`flex-1 text-sm font-semibold ${hasCritical ? 'text-rose-700 dark:text-rose-300' : 'text-amber-700 dark:text-amber-300'}`}>
        Action required
      </p>
      <div className="flex flex-wrap gap-2">
        {items.map((item) => (
          <button
            key={item.id}
            type="button"
            onClick={() => router.push(item.to)}
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-semibold transition ${
              item.severity === 'critical'
                ? 'bg-rose-600 text-white hover:bg-rose-700'
                : 'border border-amber-300 bg-white text-amber-700 hover:bg-amber-50 dark:border-amber-500/30 dark:bg-transparent dark:text-amber-400'
            }`}
          >
            {item.label}
            <ChevronRight className="h-3 w-3" />
          </button>
        ))}
      </div>
    </div>
  );
}

// ── KPI strip ─────────────────────────────────────────────────────────────────

type KpiTone = 'blue' | 'green' | 'cyan' | 'amber' | 'rose' | 'neutral';

interface KpiDef {
  label: string;
  value: string;
  sub: string;
  tone: KpiTone;
  trend?: 'up' | 'down' | 'flat';
  icon: React.ElementType;
  to: string;
  primary?: boolean;
}

function KpiCard({ kpi, loading, router }: { kpi: KpiDef; loading: boolean; router: ReturnType<typeof useRouter> }) {
  const ACCENT: Record<KpiTone, string> = {
    blue:    'from-blue-500/[0.08] border-blue-500/20',
    green:   'from-emerald-500/[0.08] border-emerald-500/20',
    cyan:    'from-cyan-500/[0.08] border-cyan-500/20',
    amber:   'from-amber-500/[0.08] border-amber-500/20',
    rose:    'from-rose-500/[0.08] border-rose-500/20',
    neutral: 'from-slate-500/[0.05] border-slate-200 dark:border-white/[0.08]',
  };
  const VAL_CLR: Record<KpiTone, string> = {
    blue:    'text-blue-600 dark:text-blue-400',
    green:   'text-emerald-600 dark:text-emerald-400',
    cyan:    'text-cyan-600 dark:text-cyan-400',
    amber:   'text-amber-600 dark:text-amber-400',
    rose:    'text-rose-600 dark:text-rose-400',
    neutral: 'text-slate-900 dark:text-white',
  };
  const ICON_CLR: Record<KpiTone, string> = {
    blue:    'bg-blue-500/10 text-blue-600',
    green:   'bg-emerald-500/10 text-emerald-600',
    cyan:    'bg-cyan-500/10 text-cyan-600',
    amber:   'bg-amber-500/10 text-amber-600',
    rose:    'bg-rose-500/10 text-rose-600',
    neutral: 'bg-slate-100 text-slate-400 dark:bg-white/[0.07] dark:text-slate-400',
  };

  const Icon = kpi.icon;
  const valueSize = kpi.primary ? 'text-3xl sm:text-4xl' : 'text-2xl';

  if (loading) {
    return (
      <div className={`flex flex-col gap-3 overflow-hidden rounded-2xl border bg-gradient-to-b to-transparent p-5 dark:bg-[#0e1729]/80 ${ACCENT[kpi.tone]} ${kpi.primary ? 'col-span-2' : ''}`}>
        <div className="flex items-center justify-between">
          <Skel className="h-3 w-24" />
          <Skel className="h-7 w-7 rounded-lg" />
        </div>
        <Skel className={`h-10 ${kpi.primary ? 'w-48' : 'w-28'}`} />
        <Skel className="h-3 w-32" />
      </div>
    );
  }

  return (
    <button
      type="button"
      onClick={() => router.push(kpi.to)}
      className={`group relative flex flex-col gap-3 overflow-hidden rounded-2xl border bg-gradient-to-b to-transparent p-5 text-left transition hover:-translate-y-0.5 hover:shadow-lg dark:bg-[#0e1729]/80 dark:hover:shadow-black/40 ${ACCENT[kpi.tone]} ${kpi.primary ? 'col-span-2' : ''}`}
      aria-label={`${kpi.label}: ${kpi.value}`}
    >
      <div className="flex items-center justify-between">
        <p className="text-[10px] font-bold uppercase tracking-[0.15em] text-slate-500 dark:text-slate-400">{kpi.label}</p>
        <span className={`flex h-7 w-7 items-center justify-center rounded-lg ${ICON_CLR[kpi.tone]}`}>
          <Icon className="h-3.5 w-3.5" />
        </span>
      </div>
      <div className="flex items-end justify-between gap-2">
        <p className={`font-mono font-extrabold leading-none tracking-tight ${valueSize} ${VAL_CLR[kpi.tone]}`}>
          {kpi.value}
        </p>
        {kpi.trend === 'up' && <TrendingUp className="mb-1 h-4 w-4 shrink-0 text-emerald-500" />}
        {kpi.trend === 'down' && <TrendingDown className="mb-1 h-4 w-4 shrink-0 text-rose-500" />}
      </div>
      <p className="text-[11px] font-medium leading-tight text-slate-500 dark:text-slate-400">{kpi.sub}</p>
      <ArrowUpRight className="absolute right-3 top-3 h-3.5 w-3.5 text-slate-300 opacity-0 transition-opacity group-hover:opacity-100 dark:text-slate-600" />
    </button>
  );
}

// ── Action queue row ──────────────────────────────────────────────────────────

function QueueRow({
  title,
  module,
  age,
  onAction,
  actionLabel,
}: {
  title: string;
  module: string;
  age: string;
  onAction: () => void;
  actionLabel: string;
}) {
  return (
    <div className="group flex items-center gap-3 rounded-xl border border-slate-100 bg-white px-4 py-3 dark:border-white/[0.06] dark:bg-white/[0.02]">
      <span className={`shrink-0 rounded-md px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide ${MODULE_COLOR[module] ?? 'bg-slate-100 text-slate-600'}`}>
        {module}
      </span>
      <span className="min-w-0 flex-1 truncate text-sm font-medium text-slate-800 dark:text-slate-200">{title}</span>
      <span className="shrink-0 text-[11px] text-slate-400 dark:text-slate-500">{age}</span>
      <button
        type="button"
        onClick={onAction}
        className="shrink-0 rounded-lg bg-blue-600 px-3 py-1.5 text-[11px] font-semibold text-white transition hover:bg-blue-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
      >
        {actionLabel}
      </button>
    </div>
  );
}

// ── Activity feed ─────────────────────────────────────────────────────────────

function FeedItem({ item }: { item: ActivityFeedItem }) {
  const mod = item.module;
  const dotCls: Record<string, string> = {
    Payroll:    'bg-blue-500',
    Leave:      'bg-emerald-500',
    Attendance: 'bg-amber-500',
    HR:         'bg-violet-500',
  };
  return (
    <div className="flex items-start gap-3 py-2.5">
      <span className={`mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full ${dotCls[mod] ?? 'bg-slate-400'}`} />
      <div className="min-w-0 flex-1">
        <p className="text-[12px] font-medium text-slate-800 dark:text-slate-200 leading-snug">
          {humaniseAction(item.action)}
        </p>
        <p className="mt-0.5 flex items-center gap-1.5 text-[11px] text-slate-400 dark:text-slate-500">
          <span className={`rounded px-1 py-px text-[9px] font-bold uppercase tracking-wide ${MODULE_COLOR[mod] ?? ''}`}>
            {mod}
          </span>
          · {item.actor !== 'System' ? item.actor : 'System'} · {timeAgo(item.occurredAt)}
        </p>
      </div>
    </div>
  );
}

// ── Section label ─────────────────────────────────────────────────────────────

function SLabel({ label, icon: Icon }: { label: string; icon?: React.ElementType }) {
  return (
    <div className="flex items-center gap-2 py-1">
      {Icon && <Icon className="h-3.5 w-3.5 text-slate-400 dark:text-slate-600" aria-hidden />}
      <span className="text-[10px] font-bold uppercase tracking-[0.16em] text-slate-400 dark:text-slate-600">{label}</span>
      <div className="flex-1 border-t border-slate-100 dark:border-white/[0.06]" />
    </div>
  );
}

// ── Main page ─────────────────────────────────────────────────────────────────

export function DashboardPage() {
  const router = useRouter();
  const { isFeatureEnabled } = useFeatureFlags();
  const [data, setData] = useState<DashboardFull | null>(null);
  const [insights, setInsights] = useState<AIInsight[]>([]);
  const [loading, setLoading] = useState(true);
  const [analyticsOpen, setAnalyticsOpen] = useState(true);
  const loadRef = useRef(false);

  const load = useCallback(async () => {
    if (loadRef.current) return;
    loadRef.current = true;
    setLoading(true);
    const tasks: Promise<void>[] = [
      dashboardApi.full(6).then(setData).catch(() => {}),
    ];
    if (isFeatureEnabled('ai_assistant')) {
      tasks.push(
        aiAssistantApi.listInsights({ acknowledged: false })
          .then((r) => setInsights(r.items))
          .catch(() => {}),
      );
    }
    await Promise.allSettled(tasks);
    setLoading(false);
    loadRef.current = false;
  }, [isFeatureEnabled]);

  useEffect(() => { load(); }, [load]);

  // ── Derived values ────────────────────────────────────────────────────────

  const s = data?.summary;
  const o = data?.overview;
  const kpis = data?.kpis;
  const payroll = o?.payrollSummary ?? null;

  // Time-aware attendance: before 08:00 local, don't colour-code 0 present as danger.
  const localHour = new Date().getHours();
  const isEarlyMorning = localHour < 8;

  const attendanceRate = s && s.activeEmployees > 0
    ? Math.round((s.presentToday / s.activeEmployees) * 100) : 0;
  const attendanceTone: KpiTone = isEarlyMorning
    ? 'neutral'
    : attendanceRate >= 80 ? 'green' : attendanceRate >= 60 ? 'amber' : 'rose';

  const workforceMix    = o?.workforceMix ?? [];
  const headcountByDept = o?.headcountByDepartment ?? [];
  const payrollByEntity = o?.payrollByEntity ?? [];
  const alerts          = o?.alerts ?? [];
  const criticalAlerts  = alerts.filter((a) => a.severity === 'Critical');
  const deptMax         = Math.max(1, ...headcountByDept.map((d) => d.value));
  const workforceTotal  = workforceMix.reduce((s, x) => s + x.value, 0);

  // ── Attention bar items ────────────────────────────────────────────────────

  const attentionItems: AttentionItem[] = [];
  if (criticalAlerts.length > 0) attentionItems.push({
    id: 'compliance',
    label: `${criticalAlerts.length} critical compliance alert${criticalAlerts.length !== 1 ? 's' : ''}`,
    severity: 'critical',
    to: '/compliance',
    ctaLabel: 'View Compliance',
  });
  if ((o?.pendingApprovals ?? 0) >= 5) attentionItems.push({
    id: 'approvals',
    label: `${o!.pendingApprovals} approvals waiting`,
    severity: 'warning',
    to: '/approvals',
    ctaLabel: 'Review Approvals',
  });
  if ((kpis?.expiredDocuments ?? 0) > 0) attentionItems.push({
    id: 'docs',
    label: `${kpis!.expiredDocuments} expired document${kpis!.expiredDocuments !== 1 ? 's' : ''}`,
    severity: 'critical',
    to: '/compliance',
    ctaLabel: 'Fix Documents',
  });

  // ── KPI definitions ────────────────────────────────────────────────────────

  const kpiDefs: KpiDef[] = [
    {
      label: 'Net Payroll',
      icon: BadgeDollarSign,
      value: payroll ? fmtMoney(payroll.totalNet) : (loading ? '—' : 'No run'),
      sub: payroll
        ? `${payroll.periodLabel} · ${payroll.employeeCount} employees · ${payroll.status}`
        : 'No processed payroll run yet',
      tone: 'neutral',
      trend: payroll ? 'up' : 'flat',
      to: '/payroll',
      primary: true,
    },
    {
      label: 'Active Headcount',
      icon: Users,
      value: loading ? '—' : (s?.activeEmployees ?? 0).toLocaleString(),
      sub: loading ? 'Loading…' : `${s?.totalEmployees ?? 0} total · ${o?.newJoinersThisMonth ?? 0} joined this month`,
      tone: 'blue',
      trend: 'up',
      to: '/people',
    },
    {
      label: 'Present Today',
      icon: CalendarCheck,
      value: loading ? '—' : (s?.presentToday ?? 0).toLocaleString(),
      sub: loading ? 'Loading…' : (isEarlyMorning ? 'Pre-shift window' : `${attendanceRate}% attendance rate`),
      tone: attendanceTone,
      trend: attendanceTone === 'green' ? 'up' : 'flat',
      to: '/attendance',
    },
    {
      label: 'On Leave',
      icon: CalendarPlus,
      value: loading ? '—' : (s?.onLeave ?? 0).toLocaleString(),
      sub: loading ? 'Loading…' : `${s?.absent ?? 0} absent · ${(s?.overtimeHours ?? 0).toFixed(0)}h OT this month`,
      tone: 'cyan',
      to: '/leave',
    },
    {
      label: 'Pending Approvals',
      icon: Clock,
      value: loading ? '—' : (o?.pendingApprovals ?? 0).toLocaleString(),
      sub: loading ? 'Loading…' : `${o?.openLeaveRequests ?? 0} open leave requests`,
      tone: !loading && (o?.pendingApprovals ?? 0) > 0 ? 'amber' : 'green',
      trend: 'flat',
      to: '/approvals',
    },
    {
      label: 'Compliance',
      icon: ShieldAlert,
      value: loading ? '—' : alerts.length.toLocaleString(),
      sub: loading ? 'Loading…' : (
        criticalAlerts.length > 0
          ? `${criticalAlerts.length} critical need action`
          : kpis?.expiringDocuments ?? 0 > 0
          ? `${kpis?.expiringDocuments} expiring soon`
          : 'All clear'
      ),
      tone: !loading && criticalAlerts.length > 0 ? 'rose' : !loading && alerts.length > 0 ? 'amber' : 'green',
      trend: criticalAlerts.length > 0 ? 'down' : 'flat',
      to: '/compliance',
    },
  ];

  // ── Quick actions (role-aware) ─────────────────────────────────────────────

  const quickActions = [
    { label: 'Add Employee',      desc: 'Start onboarding', icon: UserPlus,        to: '/people',     cls: 'bg-blue-500/10 text-blue-600 border-blue-500/20' },
    { label: 'Run Payroll',       desc: 'Process payroll',  icon: BadgeDollarSign, to: '/payroll',    cls: 'bg-emerald-500/10 text-emerald-600 border-emerald-500/20' },
    { label: 'Create Roster',     desc: 'Plan shifts',      icon: CalendarPlus,    to: '/shifts',     cls: 'bg-violet-500/10 text-violet-600 border-violet-500/20' },
    { label: 'Review Compliance', desc: 'Expiring docs',    icon: FileCheck2,      to: '/compliance', cls: 'bg-amber-500/10 text-amber-600 border-amber-500/20' },
  ];

  // ── Render ────────────────────────────────────────────────────────────────

  return (
    <div className="mx-auto flex max-w-[1600px] flex-col gap-5 px-1" aria-label="Workforce Command Center">

      {/* ── Command header ─────────────────────────────────────────────────── */}
      <header className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="relative flex h-2 w-2">
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-75" />
              <span className="relative inline-flex h-2 w-2 rounded-full bg-emerald-500" />
            </span>
            <span className="text-[10px] font-bold uppercase tracking-[0.18em] text-emerald-600 dark:text-emerald-400">Live</span>
          </div>
          <h1 className="mt-1 text-xl font-extrabold tracking-tight text-slate-900 dark:text-white sm:text-2xl">
            Workforce Command Center
          </h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
            {loading
              ? 'Loading live data…'
              : `${(s?.activeEmployees ?? 0).toLocaleString()} active · ${attendanceRate}% present${payroll ? ` · ${payroll.periodLabel} net ${fmtMoney(payroll.totalNet)}` : ''}`}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <button
            type="button"
            onClick={load}
            disabled={loading}
            aria-label="Refresh dashboard"
            className="grid h-8 w-8 place-items-center rounded-lg border border-slate-200 bg-white text-slate-500 transition hover:border-slate-300 hover:text-slate-800 disabled:opacity-40 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-400 dark:hover:text-white"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
          <button
            type="button"
            onClick={() => router.push('/reports')}
            className="h-8 rounded-lg border border-slate-200 bg-white px-3 text-xs font-semibold text-slate-700 transition hover:border-slate-300 hover:bg-slate-50 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-300 dark:hover:bg-white/[0.08]"
          >
            Reports
          </button>
          <button
            type="button"
            onClick={() => router.push('/approvals')}
            className="flex h-8 items-center gap-1.5 rounded-lg bg-blue-600 px-3 text-xs font-semibold text-white transition hover:bg-blue-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          >
            Approvals
            {!loading && (o?.pendingApprovals ?? 0) > 0 && (
              <span className="rounded-full bg-white/25 px-1.5 py-0.5 text-[10px] font-bold leading-none">
                {o!.pendingApprovals}
              </span>
            )}
            <ArrowRight className="h-3 w-3" />
          </button>
        </div>
      </header>

      {/* ── Attention bar (conditional — only shown when items exist) ─────── */}
      {!loading && <AttentionBar items={attentionItems} router={router} />}

      {/* ── KPI strip (12-col fluid grid) ─────────────────────────────────── */}
      {/* Primary KPI (Net Payroll) spans 2 cols; 5 secondary KPIs span 1 col each.
          At ≥1280px this makes a 7-col strip; at 768-1279 it wraps gracefully.
          CSS grid auto-fit fills full width with no dead gaps. */}
      <section aria-label="Key metrics">
        <SLabel label="Key Metrics" icon={Activity} />
        <div className="mt-3 grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-[repeat(7,1fr)]">
          {kpiDefs.map((kpi) => (
            <KpiCard key={kpi.label} kpi={kpi} loading={loading} router={router} />
          ))}
        </div>
      </section>

      {/* ── Ops queue strip ───────────────────────────────────────────────── */}
      {(!loading && kpis) && (
        <section aria-label="Operations queue">
          <SLabel label="Operations Queue" icon={Zap} />
          <div className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-3 xl:grid-cols-6">
            {([
              { label: 'Pending Leave',    value: kpis.pendingLeaveRequests,        tone: 'amber' as const, to: '/leave'      },
              { label: 'Att. Corrections', value: kpis.pendingAttendanceCorrections, tone: 'amber' as const, to: '/attendance' },
              { label: 'Att. Exceptions',  value: kpis.attendanceExceptions,         tone: 'rose'  as const, to: '/attendance' },
              { label: 'Expiring Docs',    value: kpis.expiringDocuments,            tone: 'amber' as const, to: '/compliance' },
              { label: 'Expired Docs',     value: kpis.expiredDocuments,             tone: 'rose'  as const, to: '/compliance' },
              { label: 'Missing Docs',     value: kpis.missingDocuments,             tone: kpis.missingDocuments > 0 ? 'rose' as const : 'green' as const, to: '/compliance' },
            ] satisfies { label: string; value: number; tone: KpiTone; to: string }[]).map(({ label, value, tone, to }) => {
              const DOT: Record<KpiTone, string> = { rose: 'bg-rose-500', amber: 'bg-amber-500', green: 'bg-emerald-500', blue: 'bg-blue-500', cyan: 'bg-cyan-500', neutral: 'bg-slate-400' };
              const NUM: Record<KpiTone, string> = {
                rose:    value > 0 ? 'text-rose-600 dark:text-rose-400' : 'text-slate-900 dark:text-white',
                amber:   value > 0 ? 'text-amber-600 dark:text-amber-400' : 'text-slate-900 dark:text-white',
                green:   'text-emerald-600 dark:text-emerald-400',
                blue:    'text-blue-600', cyan: 'text-cyan-600', neutral: 'text-slate-900 dark:text-white',
              };
              const BDR: Record<KpiTone, string> = {
                rose:    value > 0 ? 'border-rose-500/30'   : 'border-slate-200 dark:border-white/[0.08]',
                amber:   value > 0 ? 'border-amber-500/30'  : 'border-slate-200 dark:border-white/[0.08]',
                green:   'border-emerald-500/30',
                blue:    'border-blue-500/20', cyan: 'border-cyan-500/20', neutral: 'border-slate-200 dark:border-white/[0.08]',
              };
              return (
                <button
                  key={label}
                  type="button"
                  onClick={() => router.push(to)}
                  className={`group flex flex-col gap-1.5 rounded-xl border bg-white px-3 py-3 text-left transition hover:shadow-md dark:bg-white/[0.03] ${BDR[tone]}`}
                >
                  <div className="flex items-center gap-1.5">
                    <span className={`h-1.5 w-1.5 rounded-full ${value > 0 ? DOT[tone] : 'bg-slate-300 dark:bg-slate-700'}`} />
                    <span className="text-[10px] font-bold uppercase tracking-[0.10em] text-slate-500">{label}</span>
                  </div>
                  <span className={`font-mono text-2xl font-extrabold leading-none ${NUM[tone]}`}>
                    {value.toLocaleString()}
                  </span>
                  <span className="flex items-center gap-1 text-[10px] font-medium text-slate-400 opacity-0 transition-opacity group-hover:opacity-100">
                    View <ChevronRight className="h-3 w-3" />
                  </span>
                </button>
              );
            })}
          </div>
        </section>
      )}
      {loading && (
        <section aria-hidden>
          <SLabel label="Operations Queue" icon={Zap} />
          <div className="mt-3 grid grid-cols-3 gap-2 xl:grid-cols-6">
            {Array.from({ length: 6 }).map((_, i) => <Skel key={i} className="h-[88px] rounded-xl" />)}
          </div>
        </section>
      )}

      {/* ── Main canvas: 8-col + 4-col right rail ─────────────────────────── */}
      <div className="grid grid-cols-1 gap-5 xl:grid-cols-12">

        {/* ── Left: main canvas (8 cols) ─────────────────────────────────── */}
        <div className="flex flex-col gap-5 xl:col-span-8">

          {/* Action Queue */}
          <section aria-label="Approval queue">
            <SLabel label="Action Queue" icon={Activity} />
            <div className="mt-3 flex flex-col gap-2">
              {loading && Array.from({ length: 4 }).map((_, i) => (
                <Skel key={i} className="h-14 rounded-xl" />
              ))}
              {!loading && (o?.approvalQueue ?? []).length === 0 && (
                <Card className="p-5">
                  <div className="flex items-center gap-3 text-sm text-slate-500 dark:text-slate-400">
                    <CheckCircle2 className="h-5 w-5 text-emerald-500" />
                    No pending approvals — all caught up.
                  </div>
                </Card>
              )}
              {!loading && (o?.approvalQueue ?? []).map((item) => (
                <QueueRow
                  key={item.id}
                  title={item.title}
                  module={item.module}
                  age={timeAgo(item.createdAtUtc)}
                  onAction={() => router.push('/approvals')}
                  actionLabel="Review"
                />
              ))}
              {!loading && (o?.pendingApprovals ?? 0) > (o?.approvalQueue ?? []).length && (
                <button
                  type="button"
                  onClick={() => router.push('/approvals')}
                  className="flex items-center justify-center gap-1.5 rounded-xl border border-dashed border-slate-200 py-2.5 text-xs font-semibold text-slate-500 transition hover:border-blue-500/30 hover:text-blue-600 dark:border-white/[0.07]"
                >
                  View {(o!.pendingApprovals - o!.approvalQueue.length)} more <ArrowRight className="h-3.5 w-3.5" />
                </button>
              )}
            </div>
          </section>

          {/* Analytics (collapsible on mobile) */}
          <section aria-label="Analytics">
            <button
              type="button"
              onClick={() => setAnalyticsOpen((v) => !v)}
              className="flex w-full items-center gap-2 py-1"
              aria-expanded={analyticsOpen ? 'true' : 'false'}
            >
              <Activity className="h-3.5 w-3.5 text-slate-400 dark:text-slate-600" aria-hidden />
              <span className="text-[10px] font-bold uppercase tracking-[0.16em] text-slate-400 dark:text-slate-600">Analytics</span>
              <div className="flex-1 border-t border-slate-100 dark:border-white/[0.06]" />
              <ChevronDown className={`h-3.5 w-3.5 text-slate-400 transition-transform ${analyticsOpen ? '' : '-rotate-90'}`} />
            </button>

            {analyticsOpen && (
              <div className="mt-3 flex flex-col gap-4">
                {/* Attendance + OT trend */}
                <Card title="Attendance & Overtime" titleRight={
                  <span className="text-[11px] text-slate-400">6 months</span>
                }>
                  <div className="p-5 pt-3">
                    {loading && <Skel className="h-[200px] w-full" />}
                    {!loading && (data?.trends ?? []).length === 0 && (
                      <Empty msg="No attendance history yet." />
                    )}
                    {!loading && (data?.trends ?? []).length > 0 && (
                      <div className="h-[200px]">
                        <DashboardAttendanceTrendChart data={data!.trends} />
                      </div>
                    )}
                  </div>
                </Card>

                {/* Payroll cost trend + dept breakdown */}
                {payroll && (
                  <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                    <Card title="Payroll Trend" titleRight={
                      <button type="button" onClick={() => router.push('/payroll')} className="flex items-center gap-1 text-xs font-semibold text-blue-600 hover:underline dark:text-blue-400">
                        View <ArrowRight className="h-3 w-3" />
                      </button>
                    }>
                      <div className="p-4 pt-2">
                        <div className="grid grid-cols-2 gap-2">
                          {[
                            { label: 'Gross',      value: fmtMoney(payroll.totalGross) },
                            { label: 'Net',        value: fmtMoney(payroll.totalNet) },
                            { label: 'Deductions', value: fmtMoney(payroll.totalDeductions) },
                            { label: 'Headcount',  value: payroll.employeeCount.toLocaleString() },
                          ].map((t) => (
                            <div key={t.label} className="rounded-xl border border-slate-100 bg-slate-50/80 p-3 dark:border-white/[0.06] dark:bg-white/[0.03]">
                              <p className="text-[10px] font-bold uppercase tracking-[0.10em] text-slate-400">{t.label}</p>
                              <p className="mt-0.5 font-mono text-sm font-bold text-slate-900 dark:text-white">{t.value}</p>
                            </div>
                          ))}
                        </div>
                      </div>
                    </Card>

                    <Card title="Net by Department">
                      <div className="p-4 pt-2">
                        {payrollByEntity.length === 0
                          ? <Empty msg="No payslip department breakdown." />
                          : <div className="h-[130px]"><DashboardPayrollByEntityChart data={payrollByEntity} /></div>}
                      </div>
                    </Card>
                  </div>
                )}

                {/* Workforce mix + headcount by dept */}
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  <Card title="Headcount by Department">
                    <div className="p-4 pt-3">
                      {loading && Array.from({ length: 4 }).map((_, i) => <Skel key={i} className="mb-3 h-6 rounded" />)}
                      {!loading && headcountByDept.length === 0
                        ? <Empty msg="No active employees yet." />
                        : headcountByDept.map((d, i) => (
                          <div key={d.name} className="mb-3 last:mb-0">
                            <div className="mb-1 flex items-center justify-between">
                              <span className="flex items-center gap-1.5 text-xs font-medium text-slate-600 dark:text-slate-300">
                                <Building2 className="h-3 w-3 text-slate-400" aria-hidden />
                                {d.name}
                              </span>
                              <span className="font-mono text-xs font-bold text-slate-900 dark:text-white">{d.value}</span>
                            </div>
                            <DeptBar
                              pct={(d.value / deptMax) * 100}
                              colorClass={MIX_BAR[i % MIX_BAR.length]}
                            />
                          </div>
                        ))}
                    </div>
                  </Card>

                  <Card title="Workforce Mix">
                    <div className="p-4 pt-3">
                      {loading && <Skel className="h-[120px] w-full rounded-xl" />}
                      {!loading && workforceMix.length === 0
                        ? <Empty msg="No active employees to chart." />
                        : (
                          <div className="flex items-center gap-4">
                            <div className="h-[120px] w-[120px] shrink-0">
                              <DashboardWorkforceMixChart data={workforceMix} />
                            </div>
                            <div className="flex-1 space-y-2">
                              {workforceMix.slice(0, 5).map((item, i) => (
                                <div key={item.name} className="flex items-center justify-between text-xs">
                                  <span className="flex items-center gap-2 font-medium text-slate-600 dark:text-slate-300">
                                    <span className={`h-2 w-2 rounded-full ${MIX_DOT[i % MIX_DOT.length]}`} />
                                    {item.name}
                                  </span>
                                  <span className="font-mono font-bold text-slate-900 dark:text-white">
                                    {item.value}
                                    {workforceTotal > 0 && (
                                      <span className="ml-1 text-[10px] font-medium text-slate-400">
                                        {Math.round((item.value / workforceTotal) * 100)}%
                                      </span>
                                    )}
                                  </span>
                                </div>
                              ))}
                            </div>
                          </div>
                        )}
                    </div>
                  </Card>
                </div>
              </div>
            )}
          </section>
        </div>

        {/* ── Right rail (4 cols, persistent on xl) ─────────────────────── */}
        <aside className="flex flex-col gap-4 xl:col-span-4" aria-label="Right panel">

          {/* Activity feed */}
          <Card title="Live Activity" titleRight={
            <span className="flex items-center gap-1 text-[10px] font-bold text-emerald-600 dark:text-emerald-400 uppercase tracking-wider">
              <span className="h-1.5 w-1.5 rounded-full bg-emerald-500 animate-pulse" />
              Audit-backed
            </span>
          }>
            <div className="px-4 pb-3 divide-y divide-slate-50 dark:divide-white/[0.04]">
              {loading && Array.from({ length: 5 }).map((_, i) => (
                <div key={i} className="py-2.5 flex items-start gap-3">
                  <Skel className="mt-1.5 h-1.5 w-1.5 rounded-full" />
                  <div className="flex-1 space-y-1.5">
                    <Skel className="h-3 w-full" />
                    <Skel className="h-2.5 w-3/5" />
                  </div>
                </div>
              ))}
              {!loading && (data?.activityFeed ?? []).length === 0 && (
                <div className="py-6">
                  <Empty msg="No recent audit activity." />
                </div>
              )}
              {!loading && (data?.activityFeed ?? []).map((item, i) => (
                <FeedItem key={i} item={item} />
              ))}
            </div>
          </Card>

          {/* Quick actions */}
          <Card title="Quick Actions">
            <div className="grid grid-cols-2 gap-2 p-4 pt-3">
              {quickActions.map((a) => {
                const Icon = a.icon;
                return (
                  <button
                    key={a.label}
                    type="button"
                    onClick={() => router.push(a.to)}
                    className="group flex flex-col gap-2 rounded-xl border border-slate-100 bg-slate-50/80 p-3 text-left transition hover:-translate-y-0.5 hover:shadow-md dark:border-white/[0.07] dark:bg-white/[0.03] dark:hover:bg-white/[0.06]"
                  >
                    <span className={`flex h-7 w-7 items-center justify-center rounded-lg border ${a.cls}`}>
                      <Icon className="h-3.5 w-3.5" aria-hidden />
                    </span>
                    <span>
                      <span className="block text-[12px] font-bold text-slate-900 transition-colors group-hover:text-blue-600 dark:text-white dark:group-hover:text-blue-400">
                        {a.label}
                      </span>
                      <span className="block text-[10px] text-slate-500 dark:text-slate-400">{a.desc}</span>
                    </span>
                  </button>
                );
              })}
            </div>
          </Card>

          {/* Compliance watchlist — real gaps only */}
          <Card
            title="Compliance Watchlist"
            titleRight={
              <button type="button" onClick={() => router.push('/compliance')} className="flex items-center gap-1 text-xs font-semibold text-blue-600 hover:underline dark:text-blue-400">
                All <ArrowRight className="h-3 w-3" />
              </button>
            }
          >
            <div className="p-4 pt-3">
              {loading && Array.from({ length: 3 }).map((_, i) => <Skel key={i} className="mb-2 h-9 rounded-xl" />)}
              {!loading && alerts.length === 0 && (
                <div className="flex items-center gap-2 rounded-xl bg-emerald-50 px-3 py-2.5 text-xs font-medium text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400">
                  <CheckCircle2 className="h-4 w-4 shrink-0" />
                  All documents in order
                </div>
              )}
              {!loading && alerts.slice(0, 5).map((item) => {
                const cfg = {
                  Critical: { icon: ShieldAlert,   cls: 'text-rose-500',  bg: 'bg-rose-500/10',  bdr: 'border-rose-200 dark:border-rose-500/20' },
                  Warning:  { icon: AlertTriangle, cls: 'text-amber-500', bg: 'bg-amber-500/10', bdr: 'border-amber-200 dark:border-amber-500/20' },
                  Info:     { icon: FileWarning,   cls: 'text-blue-500',  bg: 'bg-blue-500/10',  bdr: 'border-blue-200 dark:border-blue-500/20' },
                } as const;
                const c = cfg[item.severity as keyof typeof cfg] ?? cfg.Info;
                const SevIcon = c.icon;
                return (
                  <button
                    key={item.title}
                    type="button"
                    onClick={() => router.push('/compliance')}
                    className={`mb-2 flex w-full items-center gap-2.5 rounded-xl border p-2.5 text-left transition hover:shadow-sm ${c.bdr}`}
                  >
                    <span className={`flex h-6 w-6 shrink-0 items-center justify-center rounded-lg ${c.bg}`}>
                      <SevIcon className={`h-3.5 w-3.5 ${c.cls}`} aria-hidden />
                    </span>
                    <span className="truncate text-[12px] font-medium text-slate-700 dark:text-slate-200">{item.title}</span>
                  </button>
                );
              })}

              {/* Doc mini-summary */}
              {!loading && kpis && (
                <div className="mt-3 grid grid-cols-3 gap-1.5 border-t border-slate-100 pt-3 dark:border-white/[0.06]">
                  {[
                    { label: 'Missing',  value: kpis.missingDocuments,  icon: FileWarning, cls: 'text-rose-500'  },
                    { label: 'Expiring', value: kpis.expiringDocuments, icon: AlertTriangle, cls: 'text-amber-500' },
                    { label: 'Expired',  value: kpis.expiredDocuments,  icon: ShieldAlert,   cls: 'text-rose-600'  },
                  ].map(({ label, value, icon: Ic, cls }) => (
                    <button
                      key={label}
                      type="button"
                      onClick={() => router.push('/compliance')}
                      className="flex flex-col items-center gap-0.5 rounded-lg py-2 text-center transition hover:bg-slate-50 dark:hover:bg-white/[0.04]"
                    >
                      <Ic className={`h-3.5 w-3.5 ${value > 0 ? cls : 'text-slate-300 dark:text-slate-700'}`} aria-hidden />
                      <span className={`font-mono text-base font-extrabold leading-none ${value > 0 ? cls : 'text-slate-300 dark:text-slate-700'}`}>
                        {value}
                      </span>
                      <span className="text-[9px] font-medium text-slate-400 dark:text-slate-600">{label}</span>
                    </button>
                  ))}
                </div>
              )}
            </div>
          </Card>

          {/* Insights — only when feature enabled */}
          {isFeatureEnabled('ai_assistant') && (
            <Card
              title="Insights"
            >
              <div className="p-4 pt-3">
                {insights.length === 0
                  ? <Empty msg="No active insights." />
                  : (
                    <div className="space-y-2">
                      {insights.slice(0, 3).map((ins) => {
                        const sev = ins.severity?.toLowerCase() ?? 'info';
                        const cfg = {
                          critical: { icon: ShieldAlert,   cls: 'text-rose-500',  bg: 'bg-rose-500/10' },
                          warning:  { icon: AlertTriangle, cls: 'text-amber-500', bg: 'bg-amber-500/10' },
                          info:     { icon: Lightbulb,     cls: 'text-blue-500',  bg: 'bg-blue-500/10' },
                        } as const;
                        const c = cfg[sev as keyof typeof cfg] ?? cfg.info;
                        const CIcon = c.icon;
                        return (
                          <div key={ins.id ?? ins.title} className="flex items-start gap-2.5 rounded-xl border border-slate-100 p-3 dark:border-white/[0.06]">
                            <span className={`mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-lg ${c.bg}`}>
                              <CIcon className={`h-3.5 w-3.5 ${c.cls}`} aria-hidden />
                            </span>
                            <div className="min-w-0">
                              <p className="text-[12px] font-semibold text-slate-900 dark:text-white">{ins.title}</p>
                              <p className="mt-0.5 text-[11px] leading-relaxed text-slate-500 dark:text-slate-400">{ins.summary}</p>
                            </div>
                          </div>
                        );
                      })}
                    </div>
                  )}
                <button
                  type="button"
                  onClick={() => router.push('/ai-assistant')}
                  className="mt-3 flex w-full items-center justify-center gap-1.5 rounded-xl border border-slate-200 bg-slate-50 py-2 text-xs font-semibold text-slate-700 transition hover:border-blue-500/30 hover:bg-blue-500/[0.04] hover:text-blue-600 dark:border-white/[0.07] dark:bg-white/[0.03] dark:text-slate-300 dark:hover:text-blue-400"
                >
                  <MessageSquareText className="h-3.5 w-3.5" aria-hidden />
                  Open Assistant
                </button>
              </div>
            </Card>
          )}
        </aside>
      </div>

    </div>
  );
}
