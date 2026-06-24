'use client';

import { useEffect, useState } from 'react';
import {
  MessageSquareText, Loader2, CalendarOff, Send, FileText, Clock,
  ChevronRight, Megaphone, CheckCircle2, AlertCircle,
  Zap, ClipboardList, TrendingUp, CreditCard, Banknote,
  Star, Target, Calendar, BadgeCheck, User, X,
} from 'lucide-react';
import { useRouter } from 'next/navigation';
import { essApi, type EssDashboard, type EssHrRequest, type EssHrRequestDetail } from '../api/ess';
import { useAuth } from '../contexts/AuthContext';
import { StatusChip } from '../components/StatusChip';

// ── helpers ───────────────────────────────────────────────────────────────────

function greeting(): string {
  const h = new Date().getHours();
  if (h < 12) return 'Good morning';
  if (h < 17) return 'Good afternoon';
  return 'Good evening';
}

function todayLabel(): string {
  return new Date().toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
}

function minutes(value?: number) {
  if (!value) return '0h 0m';
  const h = Math.floor(value / 60);
  const m = value % 60;
  return `${h}h ${m}m`;
}

function formatCurrency(amount: number, currency: string) {
  return `${currency} ${amount.toLocaleString('en', { minimumFractionDigits: 0, maximumFractionDigits: 0 })}`;
}

function tenureLabel(months: number) {
  if (months < 12) return `${months}mo`;
  const y = Math.floor(months / 12);
  const m = months % 12;
  return m > 0 ? `${y}y ${m}mo` : `${y}yr${y > 1 ? 's' : ''}`;
}

function formatDate(dateStr: string) {
  try {
    return new Date(dateStr).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
  } catch {
    return dateStr;
  }
}

// ── Leave balance bar ────────────────────────────────────────────────────────

function LeaveBar({ name, available, entitled }: { name: string; available: number; entitled: number }) {
  const pct = entitled > 0 ? Math.round((available / entitled) * 100) : 0;
  const color = pct >= 60 ? 'bg-emerald-500' : pct >= 30 ? 'bg-amber-500' : 'bg-rose-500';
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between text-xs">
        <span className="font-medium text-slate-700 dark:text-slate-200">{name}</span>
        <span className="tabular-nums text-slate-500 dark:text-slate-400">
          <span className="font-semibold text-slate-800 dark:text-white">{available.toFixed(1)}</span>
          {entitled > 0 && <span> / {entitled.toFixed(1)} days</span>}
        </span>
      </div>
      <div className="h-1.5 w-full overflow-hidden rounded-full bg-slate-100 dark:bg-white/[0.07]">
        {/* eslint-disable-next-line react/forbid-dom-props */}
        <div className={`h-full rounded-full transition-all duration-700 ${color}`} style={{ width: `${Math.min(100, pct)}%` }} />
      </div>
    </div>
  );
}

// ── KPI Card ──────────────────────────────────────────────────────────────────

function KpiCard({
  icon: Icon, iconBg, label, value, sub, sub2, onClick, emptyText,
}: {
  icon: React.ElementType;
  iconBg: string;
  label: string;
  value: React.ReactNode;
  sub?: string;
  sub2?: string;
  onClick?: () => void;
  emptyText?: string;
}) {
  const inner = (
    <div className="flex flex-col gap-3 h-full">
      <div className={`flex h-9 w-9 items-center justify-center rounded-xl ${iconBg}`}>
        <Icon className="h-4 w-4" />
      </div>
      <div className="flex-1">
        {emptyText ? (
          <p className="text-sm text-slate-400 dark:text-slate-500">{emptyText}</p>
        ) : (
          <>
            <div className="text-xl font-extrabold leading-tight text-slate-900 dark:text-white tabular-nums">{value}</div>
            {sub && <p className="mt-1 text-xs text-slate-500 dark:text-slate-400 leading-snug">{sub}</p>}
            {sub2 && <p className="text-xs text-slate-400 dark:text-slate-500 leading-snug">{sub2}</p>}
          </>
        )}
      </div>
      <p className="text-[11px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-600">{label}</p>
    </div>
  );

  const cls = 'rounded-xl border border-slate-100 bg-white p-4 dark:border-white/[0.07] dark:bg-white/[0.03]';

  if (onClick) {
    return (
      <button type="button" onClick={onClick} className={`${cls} text-left group hover:shadow-md hover:-translate-y-0.5 transition-all w-full`}>
        {inner}
      </button>
    );
  }
  return <div className={cls}>{inner}</div>;
}

// ── Star rating display ───────────────────────────────────────────────────────

function StarRating({ rating }: { rating: number }) {
  const full = Math.floor(rating);
  const half = rating % 1 >= 0.4;
  return (
    <span className="inline-flex items-center gap-0.5">
      {[1, 2, 3, 4, 5].map((i) => (
        <Star
          key={i}
          className={`h-3.5 w-3.5 ${i <= full ? 'fill-amber-400 text-amber-400' : i === full + 1 && half ? 'fill-amber-200 text-amber-400' : 'text-slate-200 dark:text-slate-700'}`}
        />
      ))}
      <span className="ml-1 text-xs font-semibold text-slate-700 dark:text-slate-200">{rating.toFixed(1)}</span>
    </span>
  );
}

// ── Goals progress bar ────────────────────────────────────────────────────────

function GoalsBar({ done, total }: { done: number; total: number }) {
  const pct = total > 0 ? Math.round((done / total) * 100) : 0;
  return (
    <div className="space-y-1">
      <div className="flex items-center justify-between text-xs">
        <span className="text-slate-500 dark:text-slate-400">Goals complete</span>
        <span className="font-semibold text-slate-800 dark:text-white">{done}/{total}</span>
      </div>
      <div className="h-2 w-full overflow-hidden rounded-full bg-slate-100 dark:bg-white/[0.07]">
        {/* eslint-disable-next-line react/forbid-dom-props */}
        <div className="h-full rounded-full bg-sapphire dark:bg-cyanAccent transition-all duration-700" style={{ width: `${Math.min(100, pct)}%` }} />
      </div>
    </div>
  );
}

// ── Upcoming item row ─────────────────────────────────────────────────────────

function UpcomingRow({
  dot, label, sub, badge, badgeColor,
}: {
  dot: string; label: string; sub: string; badge?: string; badgeColor?: string;
}) {
  return (
    <div className="flex items-start gap-3">
      <div className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${dot}`} />
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-slate-900 dark:text-white truncate">{label}</p>
        <p className="text-xs text-slate-500 dark:text-slate-400">{sub}</p>
      </div>
      {badge && (
        <span className={`shrink-0 rounded-full px-2 py-0.5 text-[10px] font-bold ${badgeColor ?? 'bg-slate-100 text-slate-600 dark:bg-white/[0.07] dark:text-slate-400'}`}>
          {badge}
        </span>
      )}
    </div>
  );
}

// ── Announcement card ─────────────────────────────────────────────────────────

function AnnouncementCard({ title, body }: { title: string; body: string }) {
  return (
    <div className="flex gap-3 rounded-xl border border-slate-100 bg-slate-50/60 p-3.5 dark:border-white/[0.07] dark:bg-white/[0.02]">
      <div className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-blue-500/10">
        <Megaphone className="h-3.5 w-3.5 text-blue-500 dark:text-blue-400" />
      </div>
      <div className="min-w-0">
        <p className="text-sm font-semibold text-slate-900 dark:text-white">{title}</p>
        <p className="mt-0.5 line-clamp-2 text-xs leading-relaxed text-slate-500 dark:text-slate-400">{body}</p>
      </div>
    </div>
  );
}

// ── Attendance status pill ────────────────────────────────────────────────────

function AttendancePill({ status, worked, missing }: { status?: string; worked?: number; missing?: boolean }) {
  if (!status) return <span className="rounded-full bg-slate-100 px-3 py-1 text-xs text-slate-500 dark:bg-white/[0.07] dark:text-slate-400">No record today</span>;
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-semibold ${
      missing ? 'bg-rose-500/10 text-rose-600 dark:text-rose-400' : 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400'
    }`}>
      {missing ? <AlertCircle className="h-3.5 w-3.5" /> : <CheckCircle2 className="h-3.5 w-3.5" />}
      {status}{worked ? ` · ${minutes(worked)}` : ''}
    </span>
  );
}

// ── Profile completeness bar ──────────────────────────────────────────────────

function CompletenessBar({ score }: { score: number }) {
  const pct = Math.round(score);
  const color = pct >= 80 ? 'bg-emerald-500' : pct >= 50 ? 'bg-amber-500' : 'bg-rose-500';
  return (
    <div className="mt-2 flex items-center gap-2">
      <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-slate-200/60 dark:bg-white/[0.08]">
        {/* eslint-disable-next-line react/forbid-dom-props */}
        <div className={`h-full rounded-full transition-all duration-700 ${color}`} style={{ width: `${Math.min(100, pct)}%` }} />
      </div>
      <span className="text-[10px] font-semibold text-slate-500 dark:text-slate-400">{pct}% profile</span>
    </div>
  );
}

// ── Loading skeleton ──────────────────────────────────────────────────────────

function Skeleton({ className }: { className?: string }) {
  return <div className={`animate-pulse rounded-lg bg-slate-200/70 dark:bg-white/[0.06] ${className}`} />;
}

function DashboardSkeleton() {
  return (
    <div className="mx-auto max-w-[1400px] space-y-6">
      <Skeleton className="h-44 w-full rounded-2xl" />
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        {[1, 2, 3, 4].map((i) => <Skeleton key={i} className="h-28" />)}
      </div>
      <div className="grid gap-5 lg:grid-cols-2">
        <Skeleton className="h-40" />
        <Skeleton className="h-40" />
      </div>
      <Skeleton className="h-48" />
      <div className="grid gap-5 lg:grid-cols-2">
        <Skeleton className="h-60" />
        <Skeleton className="h-60" />
      </div>
    </div>
  );
}

// ── Main page ─────────────────────────────────────────────────────────────────

export function EmployeeSelfServicePage() {
  const { user } = useAuth();
  const router = useRouter();
  const [dashboard, setDashboard] = useState<EssDashboard | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  // AI assistant
  const [question, setQuestion] = useState('');
  const [answer, setAnswer] = useState('');
  const [asking, setAsking] = useState(false);

  // HR request
  const [ticketSubject, setTicketSubject] = useState('');
  const [ticketMessage, setTicketMessage] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const [myRequests, setMyRequests] = useState<EssHrRequest[]>([]);
  const [openThread, setOpenThread] = useState<EssHrRequestDetail | null>(null);
  const [threadLoading, setThreadLoading] = useState(false);
  const [replyText, setReplyText] = useState('');
  const [replying, setReplying] = useState(false);

  const loadRequests = async () => {
    try { setMyRequests(await essApi.hrRequests()); } catch { /* non-blocking */ }
  };

  const openTicket = async (id: string) => {
    setThreadLoading(true); setOpenThread(null); setReplyText('');
    try { setOpenThread(await essApi.hrRequestDetail(id)); } catch { /* ignore */ }
    finally { setThreadLoading(false); }
  };

  const sendReply = async () => {
    if (!openThread || !replyText.trim()) return;
    setReplying(true);
    try {
      await essApi.addHrRequestComment(openThread.request.id, replyText.trim());
      setReplyText('');
      setOpenThread(await essApi.hrRequestDetail(openThread.request.id));
      await loadRequests();
    } finally { setReplying(false); }
  };

  const load = async () => {
    setLoading(true); setError('');
    try { setDashboard(await essApi.dashboard()); await loadRequests(); }
    catch (err: unknown) {
      const e = err as { response?: { data?: { message?: string } } };
      setError(e.response?.data?.message ?? 'Unable to load your workspace.');
    } finally { setLoading(false); }
  };

  useEffect(() => { load(); }, []);

  const askAi = async () => {
    if (!question.trim()) return;
    setAsking(true); setAnswer('');
    try { const res = await essApi.askAi(question); setAnswer(res.answer); }
    catch (err: unknown) {
      const e = err as { response?: { data?: { message?: string } } };
      setAnswer(e.response?.data?.message ?? 'The assistant could not answer right now.');
    } finally { setAsking(false); }
  };

  const createTicket = async () => {
    if (!ticketSubject.trim() || !ticketMessage.trim()) return;
    setSubmitting(true);
    try {
      await essApi.createHrRequest({ categoryName: 'General HR', subject: ticketSubject, description: ticketMessage, priority: 'Normal' });
      setTicketSubject(''); setTicketMessage(''); setSubmitted(true);
      setTimeout(() => setSubmitted(false), 4000);
      await loadRequests();
    } finally { setSubmitting(false); }
  };

  if (loading) return <DashboardSkeleton />;

  if (error || !dashboard) {
    return (
      <div className="rounded-xl border border-rose-200 bg-rose-50 p-6 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200">
        {error || 'ESS dashboard was empty.'}
      </div>
    );
  }

  const attendance = dashboard.attendanceToday;
  const primaryLeave = dashboard.leaveBalances.find((b) =>
    b.leaveTypeName.toLowerCase().includes('annual') || b.leaveTypeName.toLowerCase().includes('casual')
  ) ?? dashboard.leaveBalances[0];
  const firstName = (dashboard.profile.fullName ?? user?.fullName ?? 'there').split(' ')[0];
  const ps = dashboard.payrollSnapshot;
  const perf = dashboard.performanceSnapshot;
  const loans = dashboard.loansSummary;
  const nextLeave = dashboard.nextApprovedLeave;
  const totalUpcoming = (nextLeave ? 1 : 0) + dashboard.documentAlerts.length + dashboard.actionItems.length;

  return (
    <div className="mx-auto max-w-[1400px] space-y-6">

      {/* ═══ HERO: My Portrait ══════════════════════════════════════════════ */}
      <div className="relative overflow-hidden rounded-2xl border border-slate-100 bg-gradient-to-br from-white via-blue-50/40 to-indigo-50/30 p-6 dark:border-white/[0.07] dark:from-[#0f1729] dark:via-[#101e36] dark:to-[#0d1525]">
        <div className="pointer-events-none absolute right-0 top-0 h-64 w-64 translate-x-16 -translate-y-16 rounded-full bg-sapphire/[0.06] blur-3xl dark:bg-blue-500/[0.12]" />
        <div className="pointer-events-none absolute bottom-0 left-1/3 h-40 w-72 -translate-y-4 rounded-full bg-indigo-400/[0.04] blur-2xl dark:bg-indigo-500/[0.07]" />

        <div className="relative flex flex-col gap-5 lg:flex-row lg:items-start lg:justify-between">

          {/* Avatar + identity */}
          <div className="flex items-start gap-5">
            <div className="relative shrink-0">
              {dashboard.profile.profilePhotoUrl ? (
                <img
                  src={dashboard.profile.profilePhotoUrl}
                  alt={dashboard.profile.fullName}
                  className="h-16 w-16 rounded-2xl object-cover ring-2 ring-white/60 dark:ring-white/10"
                />
              ) : (
                <div className="flex h-16 w-16 items-center justify-center rounded-2xl bg-sapphire/10 dark:bg-cyanAccent/10 ring-2 ring-white/60 dark:ring-white/10">
                  <User className="h-7 w-7 text-sapphire dark:text-cyanAccent" />
                </div>
              )}
              {/* Online indicator */}
              <span className="absolute -bottom-0.5 -right-0.5 h-3.5 w-3.5 rounded-full border-2 border-white bg-emerald-400 dark:border-[#0f1729]" />
            </div>

            <div className="min-w-0">
              <p className="text-[11px] font-bold uppercase tracking-widest text-sapphire dark:text-cyanAccent">{todayLabel()}</p>
              <h1 className="mt-0.5 text-2xl font-extrabold tracking-tight text-slate-900 dark:text-white">
                {greeting()}, {firstName}
              </h1>
              <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
                {dashboard.profile.jobTitle || 'Employee'}
                {dashboard.profile.department ? ` · ${dashboard.profile.department}` : ''}
                {dashboard.profile.employeeCode ? ` · ${dashboard.profile.employeeCode}` : ''}
              </p>

              {/* Tenure badge */}
              {dashboard.tenureMonths > 0 && (
                <span className="mt-2 inline-flex items-center gap-1 rounded-full bg-blue-500/10 px-2.5 py-0.5 text-[11px] font-semibold text-blue-600 dark:text-blue-400">
                  <BadgeCheck className="h-3 w-3" />
                  {tenureLabel(dashboard.tenureMonths)} tenure
                </span>
              )}

              {/* Profile completeness */}
              <CompletenessBar score={dashboard.profile.profileCompletenessScore} />
            </div>
          </div>

          {/* Hero action buttons + today's status */}
          <div className="flex flex-col gap-3 lg:items-end">
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                onClick={() => router.push('/leave')}
                className="inline-flex items-center gap-1.5 rounded-xl bg-sapphire px-4 py-2 text-sm font-semibold text-white hover:bg-sapphire/90 transition dark:bg-cyanAccent dark:text-slate-900"
              >
                <CalendarOff className="h-4 w-4" /> Apply Leave
              </button>
              <button
                type="button"
                onClick={() => router.push('/payroll')}
                className="inline-flex items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 transition dark:border-white/[0.12] dark:bg-white/[0.04] dark:text-slate-200 dark:hover:bg-white/[0.08]"
              >
                <FileText className="h-4 w-4" /> View Payslip
              </button>
              <button
                type="button"
                onClick={() => router.push('/overtime')}
                className="inline-flex items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 transition dark:border-white/[0.12] dark:bg-white/[0.04] dark:text-slate-200 dark:hover:bg-white/[0.08]"
              >
                <Zap className="h-4 w-4" /> OT Request
              </button>
              <button
                type="button"
                onClick={() => router.push('/hr-requests')}
                className="inline-flex items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 transition dark:border-white/[0.12] dark:bg-white/[0.04] dark:text-slate-200 dark:hover:bg-white/[0.08]"
              >
                <ClipboardList className="h-4 w-4" /> My Requests
              </button>
            </div>

            {/* Today attendance pill */}
            <AttendancePill
              status={attendance?.status}
              worked={attendance?.totalWorkedMinutes}
              missing={attendance?.missingPunch}
            />
          </div>
        </div>
      </div>

      {/* ═══ ROW 1: 4 KPI Cards ══════════════════════════════════════════════ */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">

        {/* Leave KPI */}
        <KpiCard
          icon={CalendarOff}
          iconBg="bg-emerald-500/10 text-emerald-600 dark:text-emerald-400"
          label="Leave Balance"
          value={primaryLeave ? `${primaryLeave.available.toFixed(1)}d` : '—'}
          sub={primaryLeave?.leaveTypeName ?? 'No leave types'}
          sub2={primaryLeave ? `of ${primaryLeave.entitled.toFixed(1)} days entitled` : undefined}
          onClick={() => router.push('/leave')}
          emptyText={dashboard.leaveBalances.length === 0 ? 'No leave balances configured' : undefined}
        />

        {/* Attendance KPI */}
        <KpiCard
          icon={Clock}
          iconBg="bg-blue-500/10 text-blue-600 dark:text-blue-400"
          label="Today's Attendance"
          value={attendance?.status ?? 'Not yet'}
          sub={attendance ? minutes(attendance.totalWorkedMinutes) : 'No punch today'}
          sub2={attendance?.missingPunch ? 'Missing punch — please regularize' : attendance?.lateMinutes ? `Late ${attendance.lateMinutes}m` : attendance ? 'On time' : undefined}
          emptyText={!attendance ? 'No attendance record for today' : undefined}
        />

        {/* Payslip KPI */}
        <KpiCard
          icon={Banknote}
          iconBg="bg-violet-500/10 text-violet-600 dark:text-violet-400"
          label="Last Payslip"
          value={ps ? formatCurrency(ps.netSalary, ps.currency) : '—'}
          sub={ps?.period ?? undefined}
          sub2={ps?.nextPayrollDate ? `Next payroll: ${formatDate(ps.nextPayrollDate)}` : undefined}
          onClick={() => router.push('/payroll')}
          emptyText={!ps ? 'No finalised payslips yet' : undefined}
        />

        {/* Loans / OT KPI */}
        <KpiCard
          icon={CreditCard}
          iconBg="bg-rose-500/10 text-rose-600 dark:text-rose-400"
          label="Loans & OT"
          value={loans ? formatCurrency(loans.totalOutstanding, loans.currency) : dashboard.overtimeHoursThisMonth > 0 ? `${dashboard.overtimeHoursThisMonth}h OT` : '—'}
          sub={loans ? `${loans.activeLoanCount} active loan${loans.activeLoanCount !== 1 ? 's' : ''}` : 'No active loans'}
          sub2={dashboard.overtimeHoursThisMonth > 0 ? `${dashboard.overtimeHoursThisMonth}h overtime this month` : undefined}
          emptyText={!loans && dashboard.overtimeHoursThisMonth === 0 ? 'No active loans or overtime this month' : undefined}
        />
      </div>

      {/* ═══ ROW 2: Performance + Upcoming & Alerts (2 wider cards) ══════════ */}
      <div className="grid gap-5 lg:grid-cols-2">

        {/* Performance & KPIs */}
        <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
          <div className="flex items-center gap-2.5 border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
            <div className="flex h-7 w-7 items-center justify-center rounded-lg bg-sapphire/10 dark:bg-cyanAccent/10">
              <TrendingUp className="h-3.5 w-3.5 text-sapphire dark:text-cyanAccent" />
            </div>
            <p className="text-sm font-semibold text-slate-900 dark:text-white">Performance &amp; KPIs</p>
          </div>
          <div className="p-5 space-y-4">
            {perf ? (
              <>
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Current Cycle</p>
                    <p className="mt-0.5 text-sm font-semibold text-slate-900 dark:text-white">{perf.cycleName}</p>
                  </div>
                  {perf.lastRating !== null && (
                    <div className="text-right">
                      <p className="text-xs text-slate-400 dark:text-slate-500">Last rating</p>
                      <StarRating rating={Number(perf.lastRating)} />
                    </div>
                  )}
                </div>
                <GoalsBar done={perf.goalsCompleted} total={perf.goalsTotal} />
                <div className="flex items-center gap-4 pt-1">
                  <div className="flex items-center gap-1.5">
                    <Target className="h-3.5 w-3.5 text-slate-400" />
                    <span className="text-xs text-slate-500 dark:text-slate-400">{perf.goalsCompleted} of {perf.goalsTotal} goals done</span>
                  </div>
                  {dashboard.overtimeHoursThisMonth > 0 && (
                    <div className="flex items-center gap-1.5">
                      <Zap className="h-3.5 w-3.5 text-amber-500" />
                      <span className="text-xs text-slate-500 dark:text-slate-400">{dashboard.overtimeHoursThisMonth}h OT this month</span>
                    </div>
                  )}
                </div>
              </>
            ) : (
              <div className="flex flex-col items-center justify-center py-6 text-center gap-2">
                <Target className="h-8 w-8 text-slate-200 dark:text-slate-700" />
                <p className="text-sm text-slate-400 dark:text-slate-500">No active performance cycle</p>
                {dashboard.overtimeHoursThisMonth > 0 && (
                  <p className="text-xs text-slate-400 dark:text-slate-500 mt-1">
                    <span className="font-semibold text-amber-600 dark:text-amber-400">{dashboard.overtimeHoursThisMonth}h</span> overtime logged this month
                  </p>
                )}
              </div>
            )}
          </div>
        </section>

        {/* Upcoming & Alerts */}
        <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
          <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
            <div className="flex items-center gap-2.5">
              <div className="flex h-7 w-7 items-center justify-center rounded-lg bg-amber-500/10">
                <Calendar className="h-3.5 w-3.5 text-amber-600 dark:text-amber-400" />
              </div>
              <p className="text-sm font-semibold text-slate-900 dark:text-white">Upcoming &amp; Alerts</p>
            </div>
            {totalUpcoming > 0 && (
              <span className="rounded-full bg-rose-500/10 px-2 py-0.5 text-[10px] font-bold text-rose-600 dark:text-rose-400">
                {totalUpcoming} item{totalUpcoming !== 1 ? 's' : ''}
              </span>
            )}
          </div>
          <div className="space-y-3.5 p-5">
            {totalUpcoming === 0 ? (
              <p className="text-sm text-slate-400 dark:text-slate-500">No upcoming items or alerts.</p>
            ) : (
              <>
                {nextLeave && (
                  <UpcomingRow
                    dot="bg-emerald-500"
                    label={`Leave: ${formatDate(nextLeave.startDate)} – ${formatDate(nextLeave.endDate)}`}
                    sub={`${nextLeave.leaveTypeName} · ${nextLeave.days} day${nextLeave.days !== 1 ? 's' : ''}`}
                    badge="Approved"
                    badgeColor="bg-emerald-500/10 text-emerald-700 dark:text-emerald-400"
                  />
                )}
                {dashboard.documentAlerts.map((doc) => (
                  <UpcomingRow
                    key={doc.id}
                    dot="bg-rose-500"
                    label={`${doc.documentType} expiring`}
                    sub={doc.expiryDate ? `Expires ${formatDate(doc.expiryDate)}` : 'Expiry date not set'}
                    badge="Alert"
                    badgeColor="bg-rose-500/10 text-rose-700 dark:text-rose-400"
                  />
                ))}
                {dashboard.actionItems.map((item) => (
                  <UpcomingRow
                    key={item.id}
                    dot="bg-amber-500"
                    label={item.title}
                    sub={item.dueAtUtc ? `Due ${formatDate(item.dueAtUtc)}` : item.category}
                    badge="Open"
                    badgeColor="bg-amber-500/10 text-amber-700 dark:text-amber-400"
                  />
                ))}
                {dashboard.pendingRequests > 0 && (
                  <UpcomingRow
                    dot="bg-blue-500"
                    label={`${dashboard.pendingRequests} pending request${dashboard.pendingRequests !== 1 ? 's' : ''}`}
                    sub="HR requests awaiting action"
                    badge={`${dashboard.pendingRequests}`}
                    badgeColor="bg-blue-500/10 text-blue-700 dark:text-blue-400"
                  />
                )}
              </>
            )}
          </div>
        </section>
      </div>

      {/* ═══ ROW 3: Leave Balances (all types) ══════════════════════════════ */}
      <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
        <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">Leave Balances</p>
          <button type="button" onClick={() => router.push('/leave')} className="text-[11px] font-medium text-sapphire hover:underline dark:text-cyanAccent">
            Request leave
          </button>
        </div>
        <div className="p-5">
          {dashboard.leaveBalances.length === 0 ? (
            <p className="text-sm text-slate-400 dark:text-slate-500">No leave balances configured for this year.</p>
          ) : (
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {dashboard.leaveBalances.map((b) => (
                <LeaveBar
                  key={b.leaveTypeId}
                  name={b.leaveTypeName}
                  available={b.available}
                  entitled={b.entitled ?? b.available}
                />
              ))}
            </div>
          )}
        </div>
      </section>

      {/* ═══ ROW 4: Announcements + Action items / AI + HR Request ══════════ */}
      <div className="grid gap-5 xl:grid-cols-[1.5fr_1fr]">

        {/* Left: Action center + AI */}
        <div className="space-y-5">

          {/* Action center */}
          {(dashboard.actionItems.length > 0 || dashboard.documentAlerts.length > 0) && (
            <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
              <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">Action Center</p>
                <span className="rounded-full bg-rose-500/10 px-2 py-0.5 text-[10px] font-bold text-rose-600 dark:text-rose-400">
                  {dashboard.actionItems.length + dashboard.documentAlerts.length} open
                </span>
              </div>
              <div className="divide-y divide-slate-50 dark:divide-white/[0.04]">
                {dashboard.documentAlerts.map((doc) => (
                  <div key={doc.id} className="flex items-center justify-between gap-4 px-5 py-3.5">
                    <div className="flex items-start gap-3 min-w-0">
                      <div className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-rose-500/10">
                        <AlertCircle className="h-3.5 w-3.5 text-rose-500" />
                      </div>
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-slate-900 dark:text-white truncate">{doc.documentType}</p>
                        <p className="text-xs text-slate-500 dark:text-slate-400 truncate">{doc.fileName || 'Document'} · expires {doc.expiryDate ? formatDate(doc.expiryDate) : 'not set'}</p>
                      </div>
                    </div>
                    <StatusChip label={doc.approvalStatus} />
                  </div>
                ))}
                {dashboard.actionItems.map((item) => (
                  <div key={item.id} className="flex items-center justify-between gap-4 px-5 py-3.5">
                    <div className="flex items-start gap-3 min-w-0">
                      <div className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-amber-500/10">
                        <Clock className="h-3.5 w-3.5 text-amber-500" />
                      </div>
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-slate-900 dark:text-white truncate">{item.title}</p>
                        <p className="text-xs text-slate-500 dark:text-slate-400">{item.category}</p>
                      </div>
                    </div>
                    <StatusChip label={item.dueAtUtc ? 'Due' : 'Open'} />
                  </div>
                ))}
              </div>
            </section>
          )}

          {/* AI assistant */}
          <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
            <div className="flex items-center gap-2.5 border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
              <div className="flex h-7 w-7 items-center justify-center rounded-lg bg-sapphire/10 dark:bg-cyanAccent/10">
                <MessageSquareText className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
              </div>
              <p className="text-sm font-semibold text-slate-900 dark:text-white">HR Assistant</p>
            </div>
            <div className="space-y-3 p-5">
              <textarea
                value={question}
                onChange={(e) => setQuestion(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) askAi(); }}
                placeholder="Ask anything — leave balance, policies, payslip dates…"
                rows={3}
                className="w-full resize-none rounded-xl border border-slate-200 bg-slate-50/80 px-4 py-3 text-sm text-slate-900 placeholder-slate-400 outline-none transition focus:border-sapphire/50 focus:ring-2 focus:ring-sapphire/10 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-white dark:placeholder-slate-600 dark:focus:border-cyanAccent/40"
              />
              <div className="flex items-center justify-between gap-3">
                <p className="text-[10px] text-slate-400 dark:text-slate-600">Cmd+Enter to send</p>
                <button
                  type="button"
                  onClick={askAi}
                  disabled={asking || !question.trim()}
                  className="inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-semibold text-white transition hover:bg-sapphire/90 disabled:opacity-50 dark:bg-cyanAccent dark:text-slate-900"
                >
                  {asking ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
                  {asking ? 'Thinking…' : 'Ask'}
                </button>
              </div>
              {answer && (
                <div className="rounded-xl border border-sapphire/15 bg-sapphire/[0.04] p-4 text-sm leading-relaxed text-slate-700 dark:border-cyanAccent/15 dark:bg-cyanAccent/[0.04] dark:text-slate-200">
                  {answer}
                </div>
              )}
            </div>
          </section>
        </div>

        {/* Right: Announcements + HR Request */}
        <div className="space-y-5">

          {/* Announcements */}
          <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
            <div className="border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
              <p className="text-sm font-semibold text-slate-900 dark:text-white">Announcements</p>
            </div>
            <div className="space-y-2.5 p-5">
              {dashboard.announcements.length === 0 ? (
                <p className="text-sm text-slate-400 dark:text-slate-500">No active announcements.</p>
              ) : dashboard.announcements.map((a) => (
                <AnnouncementCard key={a.id} title={a.title} body={a.body} />
              ))}
            </div>
          </section>

          {/* HR request */}
          <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
            <div className="border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
              <p className="text-sm font-semibold text-slate-900 dark:text-white">Submit HR Request</p>
            </div>
            <div className="space-y-3 p-5">
              {submitted && (
                <div className="flex items-center gap-2 rounded-lg bg-emerald-500/10 px-3 py-2 text-sm font-medium text-emerald-600 dark:text-emerald-400">
                  <CheckCircle2 className="h-4 w-4 shrink-0" /> Request submitted successfully!
                </div>
              )}
              <input
                value={ticketSubject}
                onChange={(e) => setTicketSubject(e.target.value)}
                placeholder="Subject"
                className="w-full rounded-xl border border-slate-200 bg-slate-50/80 px-4 py-2.5 text-sm text-slate-900 placeholder-slate-400 outline-none transition focus:border-sapphire/50 focus:ring-2 focus:ring-sapphire/10 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-white dark:placeholder-slate-600"
              />
              <textarea
                value={ticketMessage}
                onChange={(e) => setTicketMessage(e.target.value)}
                placeholder="What do you need from HR?"
                rows={3}
                className="w-full resize-none rounded-xl border border-slate-200 bg-slate-50/80 px-4 py-2.5 text-sm text-slate-900 placeholder-slate-400 outline-none transition focus:border-sapphire/50 focus:ring-2 focus:ring-sapphire/10 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-white dark:placeholder-slate-600"
              />
              <button
                type="button"
                onClick={createTicket}
                disabled={submitting || !ticketSubject.trim() || !ticketMessage.trim()}
                className="inline-flex w-full items-center justify-center gap-1.5 rounded-xl bg-slate-900 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-700 disabled:opacity-50 dark:bg-white dark:text-slate-900 dark:hover:bg-slate-100"
              >
                {submitting ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
                {submitting ? 'Submitting…' : 'Submit request'}
              </button>

              {myRequests.length > 0 && (
                <div className="space-y-2 border-t border-slate-100 pt-3 dark:border-white/[0.07]">
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">My Requests</p>
                  {myRequests.map((r) => {
                    const tone = r.status === 'Closed' || r.status === 'Resolved'
                      ? 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400'
                      : r.isOverdue
                        ? 'bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400'
                        : r.hrResponded
                          ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-500/10 dark:text-emerald-400'
                          : 'bg-amber-50 text-amber-600 dark:bg-amber-500/10 dark:text-amber-400';
                    return (
                      <button key={r.id} type="button" onClick={() => openTicket(r.id)}
                        className="flex w-full items-center justify-between gap-2 rounded-lg border border-slate-100 px-3 py-2 text-left hover:bg-slate-50 dark:border-white/[0.07] dark:hover:bg-white/[0.03]">
                        <div className="min-w-0">
                          <p className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">{r.subject}</p>
                          <p className="truncate text-xs text-slate-400">{new Date(r.createdAtUtc).toLocaleDateString()}</p>
                        </div>
                        <span className={`shrink-0 rounded-full px-2 py-0.5 text-[10px] font-semibold ${tone}`}>{r.responseStatus}</span>
                      </button>
                    );
                  })}
                </div>
              )}
            </div>
          </section>

          {/* Quick navigation cards */}
          <div className="grid grid-cols-2 gap-2">
            {[
              { icon: CalendarOff, label: 'Request Leave', path: '/leave', bg: 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-400', border: 'border-emerald-100 dark:border-emerald-500/20' },
              { icon: FileText, label: 'My Payslips', path: '/payroll', bg: 'bg-violet-500/10 text-violet-700 dark:text-violet-400', border: 'border-violet-100 dark:border-violet-500/20' },
            ].map(({ icon: Icon, label, path, bg, border }) => (
              <button
                key={label}
                type="button"
                onClick={() => router.push(path)}
                className={`flex items-center gap-2 rounded-xl border p-3 text-left hover:shadow-sm transition ${border} bg-white dark:bg-white/[0.02]`}
              >
                <div className={`flex h-7 w-7 shrink-0 items-center justify-center rounded-lg ${bg}`}>
                  <Icon className="h-3.5 w-3.5" />
                </div>
                <span className="text-xs font-semibold text-slate-700 dark:text-slate-200">{label}</span>
                <ChevronRight className="ml-auto h-3.5 w-3.5 text-slate-300 dark:text-slate-600" />
              </button>
            ))}
          </div>
        </div>
      </div>

      {/* HR request thread modal */}
      {(openThread || threadLoading) && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 p-4" onClick={() => { setOpenThread(null); setThreadLoading(false); }}>
          <div className="flex max-h-[80vh] w-full max-w-lg flex-col rounded-2xl bg-white shadow-xl dark:bg-slate-900" onClick={(e) => e.stopPropagation()}>
            {threadLoading || !openThread ? (
              <div className="flex items-center justify-center p-10"><Loader2 className="h-5 w-5 animate-spin text-slate-400" /></div>
            ) : (
              <>
                <div className="flex items-start justify-between gap-3 border-b border-slate-100 px-5 py-4 dark:border-white/[0.07]">
                  <div className="min-w-0">
                    <p className="truncate text-sm font-semibold text-slate-900 dark:text-white">{openThread.request.subject}</p>
                    <p className="mt-0.5 text-xs text-slate-400">{openThread.responseStatus}</p>
                  </div>
                  <button type="button" aria-label="Close" onClick={() => setOpenThread(null)} className="rounded-lg p-1 text-slate-400 hover:bg-slate-100 dark:hover:bg-white/10"><X className="h-4 w-4" /></button>
                </div>
                <div className="flex-1 space-y-3 overflow-y-auto px-5 py-4">
                  <div className="rounded-lg bg-slate-50 px-3 py-2 text-sm text-slate-700 dark:bg-white/[0.04] dark:text-slate-300">{openThread.request.description}</div>
                  {openThread.comments.length === 0 && <p className="text-center text-xs text-slate-400">No replies yet. HR will respond here.</p>}
                  {openThread.comments.map((c) => {
                    const fromHr = c.authorType === 'HR';
                    return (
                      <div key={c.id} className={`flex ${fromHr ? 'justify-start' : 'justify-end'}`}>
                        <div className={`max-w-[80%] rounded-2xl px-3 py-2 text-sm ${fromHr ? 'bg-sapphire/10 text-slate-800 dark:bg-sapphire/20 dark:text-slate-100' : 'bg-slate-900 text-white dark:bg-white dark:text-slate-900'}`}>
                          <p className="mb-0.5 text-[10px] font-semibold opacity-70">{fromHr ? (c.authorName || 'HR') : 'You'} · {new Date(c.createdAtUtc).toLocaleString()}</p>
                          <p className="whitespace-pre-wrap">{c.comment}</p>
                        </div>
                      </div>
                    );
                  })}
                </div>
                <div className="flex items-center gap-2 border-t border-slate-100 px-4 py-3 dark:border-white/[0.07]">
                  <input
                    value={replyText}
                    onChange={(e) => setReplyText(e.target.value)}
                    onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendReply(); } }}
                    placeholder="Reply to HR…"
                    className="flex-1 rounded-xl border border-slate-200 bg-slate-50/80 px-3 py-2 text-sm text-slate-900 outline-none focus:border-sapphire/50 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-white"
                  />
                  <button type="button" aria-label="Send reply" onClick={sendReply} disabled={replying || !replyText.trim()}
                    className="inline-flex items-center gap-1.5 rounded-xl bg-slate-900 px-3 py-2 text-sm font-semibold text-white disabled:opacity-50 dark:bg-white dark:text-slate-900">
                    {replying ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
                  </button>
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
