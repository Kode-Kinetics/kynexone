'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { AlertCircle, Banknote, CalendarOff, FileSignature, RefreshCw, Zap } from 'lucide-react';
import { ESS_PAYSLIPS_PATH } from '../lib/essPayslip';
import { ESS_LEAVE_PATH, ESS_OVERTIME_PATH, ESS_REQUESTS_PATH, readSeenReplies } from '../lib/essSelfService';
import { ESS_DOCUMENTS_PATH } from '../routes/essSections';
import { essActionsApi, essApi, type EssDashboard, type EssHrRequest, type EssRosterEntry } from '../api/ess';
import { advancesApi, loansApi, type EmployeeLoan, type SalaryAdvance } from '../api/loans';
import type { LeaveRequest, LeaveType } from '../api/leave';
import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { useCanWriteEss } from '../components/ess/EssParts';
import { MyDayHero } from '../components/ess/myday/MyDayHero';
import { MyDayTracker } from '../components/ess/myday/MyDayTracker';
import { AskKodyCard, ComingUpCard, QuickActionsCard, type QuickAction } from '../components/ess/myday/MyDayCards';
import { MyDayRecords } from '../components/ess/myday/MyDayRecords';
import { MyDayTabs, myDayPanelId, myDayTabId, type MyDayView } from '../components/ess/myday/MyDayTabs';
import {
  advanceItem, loanItem, buildNextStep, dateOnly, dayKey, deriveDay, hrRequestItem, leaveItem, orderTracker,
  type TrackerItem, type UpcomingEntry,
} from '../lib/myDay';

/**
 * Self-Service overview, "My day": today's hours and the one next step in the hero, the employee's
 * requests as named steps, what is coming up, and (second tab) the standing records. Everything on
 * it comes from the dashboard payload and the employee's own request lists; a source that fails to
 * load says so, and a refresh keeps the screen as it was until the new data is in (no skeleton).
 * Net pay is masked until the employee shows it, for this visit only. There is no clock-in here.
 */

const TONE = {
  emerald: 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-300',
  violet: 'bg-violet-500/10 text-violet-700 dark:text-violet-300',
  blue: 'bg-blue-500/10 text-blue-700 dark:text-blue-300',
  amber: 'bg-amber-500/10 text-amber-700 dark:text-amber-300',
};

interface Secondary {
  hr: EssHrRequest[];
  leave: LeaveRequest[];
  advances: SalaryAdvance[];
  loans: EmployeeLoan[];
  roster: EssRosterEntry[] | null;
  leaveTypes: LeaveType[];
}
type SourceName = 'hr' | 'leave' | 'advances' | 'loans' | 'roster';

const EMPTY: Secondary = { hr: [], leave: [], advances: [], loans: [], roster: null, leaveTypes: [] };

function MyDaySkeleton() {
  const { t } = useLocale();
  return (
    <div className="space-y-4" aria-busy="true" role="status" aria-label={t('Loading your workspace')}>
      <div className="flex items-center justify-between gap-4">
        <div className="kx-skeleton h-12 w-64 max-w-full" />
        <div className="kx-skeleton h-10 w-56 max-w-full" />
      </div>
      <div className="grid gap-4 lg:grid-cols-12">
        <div className="kx-skeleton h-72 rounded-2xl lg:col-span-7" />
        <div className="kx-skeleton h-72 rounded-2xl lg:col-span-5" />
      </div>
      <div className="grid gap-4 lg:grid-cols-3">
        {[1, 2, 3].map((i) => <div key={i} className="kx-skeleton h-48 rounded-2xl" />)}
      </div>
    </div>
  );
}

export function EmployeeSelfServicePage() {
  const { user, hasPermission } = useAuth();
  const { t, locale } = useLocale();
  const fx = useFormat();
  const canWrite = useCanWriteEss();
  const { isFeatureEnabled, verdictForPath } = useFeatureFlags();

  const [dashboard, setDashboard] = useState<EssDashboard | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [loadedAt, setLoadedAt] = useState<number | null>(null);
  const [data, setData] = useState<Secondary>(EMPTY);
  const [failed, setFailed] = useState<SourceName[]>([]);
  const [view, setView] = useState<MyDayView>('today');
  // Net pay stays hidden until shown, and is hidden again on every visit: this is not persisted.
  const [masked, setMasked] = useState(true);
  const [announce, setAnnounce] = useState('');

  const mounted = useRef(true);
  const started = useRef(false);
  const dashboardRef = useRef<EssDashboard | null>(null);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);

  /** The lists beside the dashboard, read together. Each fails on its own and says which. */
  const loadSecondary = useCallback(async () => {
    const d = dashboardRef.current;
    if (!d) return;
    const from = dayKey(new Date());
    const to = dayKey(new Date(Date.now() + 13 * 86_400_000));
    const names: SourceName[] = ['hr', 'leave', 'advances', 'loans', 'roster'];
    const [hr, leave, advances, loans, roster, types] = await Promise.allSettled([
      essApi.hrRequests(),
      essActionsApi.myLeaveRequests(d.profile.employeeId),
      hasPermission('loans.self') ? advancesApi.list({ mine: true, pageSize: 50 }).then((r) => r.items) : Promise.resolve([] as SalaryAdvance[]),
      hasPermission('loans.self') ? loansApi.list({ mine: true, pageSize: 50 }).then((r) => r.items) : Promise.resolve([] as EmployeeLoan[]),
      essApi.myRoster(from, to),
      essActionsApi.leaveTypes(),
    ]);
    if (!mounted.current) return;
    // A failed read keeps what the screen already had for that list; it never turns into "none".
    setData((prev) => ({
      hr: hr.status === 'fulfilled' ? hr.value : prev.hr,
      leave: leave.status === 'fulfilled' ? leave.value : prev.leave,
      advances: advances.status === 'fulfilled' ? advances.value : prev.advances,
      loans: loans.status === 'fulfilled' ? loans.value : prev.loans,
      roster: roster.status === 'fulfilled' ? roster.value : prev.roster,
      leaveTypes: types.status === 'fulfilled' ? types.value : prev.leaveTypes,
    }));
    setFailed(names.filter((_, i) => [hr, leave, advances, loans, roster][i].status === 'rejected'));
  }, [hasPermission]);

  /** The first load shows the skeleton; every later one keeps the screen and swaps the data in. */
  const load = useCallback(async () => {
    const had = dashboardRef.current !== null;
    if (had) setRefreshing(true); else setLoading(true);
    try {
      const d = await essApi.dashboard();
      if (!mounted.current) return;
      dashboardRef.current = d;
      setDashboard(d);
      setError('');
      setRefreshFailed(false);
      await loadSecondary();
      if (!mounted.current) return;
      setLoadedAt(Date.now());
      if (had) setAnnounce(t('Updated just now.'));
    } catch (err: unknown) {
      if (!mounted.current) return;
      if (had) {
        setRefreshFailed(true);
        setAnnounce(t('Refresh failed. Your earlier information is still shown.'));
      } else {
        const e = err as { response?: { data?: { message?: string } } };
        setError(e.response?.data?.message ?? t('Unable to load your workspace.'));
      }
    } finally {
      if (mounted.current) { setLoading(false); setRefreshing(false); }
    }
  }, [loadSecondary, t]);

  useEffect(() => {
    // One read of the dashboard per visit (each read writes an audit row); strict mode would run this twice.
    if (started.current) return;
    started.current = true;
    void load();
  }, [load]);

  // The tab lives in the address so a reload (or a shared link) lands on it.
  useEffect(() => { if (window.location.hash === '#tab=records') setView('records'); }, []);
  const chooseView = (v: MyDayView) => {
    setView(v);
    try { window.history.replaceState(window.history.state, '', `${window.location.pathname}${window.location.search}${v === 'records' ? '#tab=records' : ''}`); } catch { /* the tab still switches */ }
  };

  const leaveName = (id: string | null, stored: string) => {
    const ty = data.leaveTypes.find((x) => (id ? x.id === id : x.nameEn === stored));
    return locale === 'ar' && ty?.nameAr ? ty.nameAr : stored;
  };

  if (loading && !dashboard) return <MyDaySkeleton />;

  if (!dashboard) {
    return (
      <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-rose-200 bg-rose-50 p-6 text-center text-body text-rose-800 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200">
        <AlertCircle className="h-6 w-6" aria-hidden="true" />
        {error || t('Your self-service workspace is empty.')}
        <button type="button" onClick={() => void load()} className="btn-danger btn-sm">{t('Retry')}</button>
      </div>
    );
  }

  const nowMs = Date.now();
  const attendance = dashboard.attendanceToday;
  const today = dateOnly(attendance?.workDate) ?? dayKey(new Date(nowMs));
  const firstName = (dashboard.profile.fullName ?? user?.fullName ?? '').split(' ')[0];
  const ps = dashboard.payrollSnapshot;
  const loanGroups = dashboard.loanSummaries ?? (dashboard.loansSummary ? [dashboard.loansSummary] : []);
  const wholeMoney = (n: number, currency: string) => fx.money(n, currency, { decimals: 0 });
  const mayOpenLoans = ['loans.self', 'loans.read', 'loans.write'].some((p) => hasPermission(p));
  const loansHref = hasPermission('loans.self') ? '/ess/loans' : mayOpenLoans ? '/loans?mine=true' : null;
  const overtimeOn = isFeatureEnabled('overtime');
  const fmt = { date: fx.date, dateTime: fx.dateTime, time: fx.time, number: (n: number) => fx.number(n) };
  const ctx = { t, fx: fmt, nowMs };

  // The tracker: open HR requests, waiting (or just decided) leave, and loans and advances waiting on approval or payout.
  const seen = readSeenReplies(user?.id);
  const open = data.hr.filter((r) => r.responseStatus !== 'Closed' && r.status !== 'Closed');
  const tracker: TrackerItem[] = orderTracker([
    ...open.map((r) => hrRequestItem(r, seen, ctx)),
    ...data.leave.map((l) => leaveItem(l, leaveName, ctx)).filter((x): x is TrackerItem => !!x),
    ...data.advances.map((a) => advanceItem(a, ctx, loansHref)).filter((x): x is TrackerItem => !!x),
    ...data.loans.map((l) => loanItem(l, ctx, loansHref)).filter((x): x is TrackerItem => !!x),
  ]);
  const sourceLabels: Record<SourceName, string> = { hr: t('HR requests'), leave: t('Leave requests'), advances: t('Salary advances'), loans: t('Loans'), roster: t('Shifts') };
  const trackerFailed = failed.filter((s) => s !== 'roster').map((s) => sourceLabels[s]);

  const day = deriveDay({
    t, fx: fmt, dashboard, nowMs, today, leaveName,
    shiftName: data.roster?.find((s) => s.date.slice(0, 10) === today)?.shiftName ?? null,
  });
  const { step: next } = buildNextStep({
    t, fx: fmt, dashboard, canWrite, today,
    unseenReplies: open.filter((r) => r.responseStatus === 'Responded' && !seen.has(r.id)),
    decidedLeave: tracker.filter((i) => i.kind === 'leave' && i.needsYou),
    openRequests: tracker.length, sourceFailed: trackerFailed.length > 0, onLeave: day.onLeave,
  });

  // Coming up: dated things from the payload, soonest first; undated ones that need the employee come last.
  const upcoming: UpcomingEntry[] = [];
  const nextLeave = dashboard.nextApprovedLeave;
  if (nextLeave) {
    upcoming.push({ id: 'leave-next', date: dateOnly(nextLeave.startDate), tone: 'normal', href: ESS_LEAVE_PATH,
      label: t('{type}, {count, plural, one {# day} other {# days}}', { type: leaveName(null, nextLeave.leaveTypeName), count: nextLeave.days }), sub: t('Approved') });
  }
  for (const l of data.leave) {
    if (!['Submitted', 'Draft', 'PendingManagerApproval', 'PendingHRApproval'].includes(l.status) || (dateOnly(l.endDate) ?? '') < today) continue;
    upcoming.push({ id: `leave-${l.id}`, date: dateOnly(l.startDate), tone: 'pending', href: ESS_LEAVE_PATH,
      label: t('{type}, {count, plural, one {# day} other {# days}}', { type: leaveName(l.leaveTypeId, l.leaveTypeName), count: l.totalDays }), sub: t('Not final: waiting for approval') });
  }
  if (ps?.nextPayrollDate) {
    // The server's month-end estimate, not a date HR has set: labelled as such.
    upcoming.push({ id: 'pay', date: dateOnly(ps.nextPayrollDate), tone: 'normal', label: t('Expected pay date (estimate)'), sub: t('Month-end estimate, confirmed when HR approves payroll') });
  }
  for (const g of loanGroups.filter((x) => x.nextInstallmentDate)) {
    upcoming.push({ id: `loan-${g.currency}`, date: dateOnly(g.nextInstallmentDate), tone: 'normal', href: loansHref ?? undefined,
      label: t('Loan instalment'), sub: g.nextInstallmentAmount != null ? wholeMoney(g.nextInstallmentAmount, g.currency) : undefined });
  }
  for (const doc of dashboard.documentAlerts) {
    upcoming.push({ id: `doc-${doc.id}`, date: dateOnly(doc.expiryDate), tone: 'warn', href: canWrite ? ESS_REQUESTS_PATH : undefined,
      label: t('{document} is expiring', { document: t(doc.documentType) }), sub: canWrite ? t('Tell HR so it can be renewed') : undefined });
  }
  for (const item of dashboard.actionItems) {
    upcoming.push({ id: `task-${item.id}`, date: dateOnly(item.dueAtUtc), tone: 'warn',
      href: item.category === 'EmployeeProfileCompletion' ? '/ess/onboarding' : undefined, label: item.title });
  }

  const primaryLeave = dashboard.leaveBalances.filter((b) => b.statutoryEntitlementDays == null)
    .find((b) => /annual|casual/i.test(b.leaveTypeName)) ?? dashboard.leaveBalances.find((b) => b.statutoryEntitlementDays == null);
  const quick: QuickAction[] = [
    { key: 'leave', icon: CalendarOff, tone: TONE.emerald, label: canWrite ? t('Request leave') : t('Open my leave'), href: ESS_LEAVE_PATH,
      sub: primaryLeave ? t('{type}: {days} days available', { type: leaveName(primaryLeave.leaveTypeId, primaryLeave.leaveTypeName), days: fx.number(primaryLeave.available) }) : t('Leave balances appear when HR sets them up.') },
    { key: 'payslip', icon: Banknote, tone: TONE.violet, label: t('View Payslip'), href: ESS_PAYSLIPS_PATH,
      sub: ps?.period ? fx.period(ps.period) : t('Payslips appear after your first payroll run.') },
    // Read-only (ess.read without ess.write) users are not promised a request they cannot make.
    { key: 'letter', icon: FileSignature, tone: TONE.blue, label: canWrite ? t('Request an HR letter') : t('My letters'), href: ESS_DOCUMENTS_PATH, sub: t('Salary certificate, experience letter and more') },
    ...(canWrite && overtimeOn ? [{ key: 'overtime', icon: Zap, tone: TONE.amber, label: t('Request overtime'), href: ESS_OVERTIME_PATH,
      sub: dashboard.overtimeHoursThisMonth > 0 ? t('{hours} h of approved overtime this month', { hours: fx.number(dashboard.overtimeHoursThisMonth) }) : t('Hours you worked beyond your shift') }] : []),
  ].filter((a) => verdictForPath(a.href).allowed);

  const retrySecondary = () => { void loadSecondary(); };
  const toggleMask = () => {
    setMasked((m) => !m);
    setAnnounce(masked ? t('Net pay shown') : t('Net pay hidden'));
  };
  const updated = loadedAt ? t('Updated {time}', { time: fx.time(loadedAt) }) : '';

  return (
    <div className="space-y-4 max-sm:pb-14" data-testid="ess-myday">
      <div role="status" aria-live="polite" className="sr-only">{announce}</div>

      <header className="flex flex-wrap items-center gap-x-4 gap-y-3">
        <div className="min-w-0 flex-1 max-sm:basis-full">
          <h1 className="text-h2 font-semibold text-slate-900 sm:text-h1 dark:text-white">{t('My day')}</h1>
          <p data-purpose className="max-w-[70ch] text-body text-slate-700 dark:text-slate-300">{t('Your work today, your requests and what is coming up.')}</p>
        </div>
        <MyDayTabs active={view} onChange={chooseView} />
        <button type="button" onClick={() => void load()} aria-busy={refreshing} disabled={refreshing}
          aria-label={t('Refresh my day. {updated}', { updated })} className="btn-secondary btn-sm">
          <RefreshCw className={`h-3.5 w-3.5 ${refreshing ? 'animate-spin' : ''}`} aria-hidden="true" />
          <span className="tabular-nums max-sm:sr-only">{updated}</span>
        </button>
      </header>

      {refreshFailed && (
        <p role="status" className="flex flex-wrap items-center gap-2 rounded-xl border border-amber-300 bg-amber-50 px-3 py-2 text-body-sm text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
          <AlertCircle className="h-4 w-4 shrink-0" aria-hidden="true" />
          {t('Refresh failed. Showing figures from {time}.', { time: loadedAt ? fx.time(loadedAt) : '' })}
          <button type="button" onClick={() => void load()} className="rounded font-semibold underline outline-none focus-visible:ring-2 focus-visible:ring-sapphire">{t('Retry')}</button>
        </p>
      )}

      {/* The panel carries `hidden`; the grid is one level down, because a display utility would override the attribute. */}
      <div id={myDayPanelId('today')} role="tabpanel" aria-labelledby={myDayTabId('today')} hidden={view !== 'today'}>
        <div className="grid gap-4 lg:grid-cols-12">
          <div className="min-w-0 lg:col-span-7 [&>*]:h-full">
            <MyDayHero dashboard={dashboard} firstName={firstName} day={day} next={next} today={today} masked={masked} onToggleMask={toggleMask}
              onRetry={retrySecondary} stale={refreshFailed} />
          </div>
          <div className="min-w-0 lg:col-span-5 [&>*]:h-full">
            <MyDayTracker items={tracker} failed={trackerFailed} onRetry={retrySecondary} />
          </div>
          <div className="min-w-0 lg:col-span-4 [&>*]:h-full"><QuickActionsCard actions={quick} /></div>
          <div className="min-w-0 lg:col-span-4 [&>*]:h-full"><ComingUpCard entries={upcoming} today={today} /></div>
          <div className="min-w-0 lg:col-span-4 [&>*]:h-full"><AskKodyCard /></div>
        </div>
      </div>

      <div id={myDayPanelId('records')} role="tabpanel" aria-labelledby={myDayTabId('records')} hidden={view !== 'records'}>
        <MyDayRecords dashboard={dashboard} leaveName={leaveName} roster={data.roster} rosterFailed={failed.includes('roster')} onRetryRoster={retrySecondary}
          loansHref={loansHref} canWrite={canWrite} />
      </div>

      {view === 'today' && next.cta && (
        <div className="fixed inset-x-0 bottom-[calc(4rem+env(safe-area-inset-bottom))] z-20 border-t border-[color:var(--wg-line)] bg-[color:var(--wg-surface)] py-2 ps-4 pe-20 sm:hidden" data-testid="ess-action-bar">
          {next.cta.href
            ? <Link href={next.cta.href} data-primary-action className="btn-primary btn-lg w-full">{next.cta.label}</Link>
            : <button type="button" data-primary-action onClick={retrySecondary} className="btn-primary btn-lg w-full">{next.cta.label}</button>}
        </div>
      )}
    </div>
  );
}
