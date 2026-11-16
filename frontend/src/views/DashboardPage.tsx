'use client';

/**
 * HR Command Center (approved concept G).
 *
 * Desktop, top to bottom: header, Needs attention, the payroll hero band (net pay, trend, run
 * progress, headcount), four analytic tiles, then department attendance beside approvals, then
 * document expiries beside workforce composition. Phones and portrait tablets split the same
 * content into Today / Actions / Insights.
 *
 * Data trust rules this page keeps: every figure names its period; today's attendance is never
 * shown as a monthly rate; no data is drawn as empty, never as zero; missing API fields degrade
 * to plain statements instead of invented values. Glass is only on floating chrome; data sits
 * on solid cards; the page backdrop is an authored wash (see .wg-canvas).
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import type { KeyboardEvent } from 'react';
import Link from 'next/link';
import { RefreshCw } from 'lucide-react';
import { dashboardApi } from '../api/dashboard';
import { payrollApi } from '../api/payroll';
import type { DashboardFull } from '../api/dashboard';
import { useTenantSettings } from '../contexts/TenantSettingsContext';
import { useCompany } from '../contexts/CompanyContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useAuth } from '../contexts/AuthContext';
import { isEmployeeOnly } from '../lib/homePath';
import { useWorkforceFindings } from '../hooks/useWorkforceFindings';
import { usePayrollCompanies } from '../hooks/usePayrollCompanies';
import { resolvePayrollRunCurrency, resolvePayrollRunsCurrency } from '../lib/payrollCurrency';
import { useMediaQuery } from '../hooks/useMediaQuery';
import { useT } from '../hooks/useT';
import { EmployeeSelfServicePage } from './EmployeeSelfServicePage';
import { useFormat } from '../hooks/useFormat';
import { msg } from '../i18n/translations';
import { ErrorBanner } from '../components/ui/ErrorBanner';
import { AttentionStrip } from '../components/dashboard/AttentionStrip';
import { ApprovalDeadEndCard } from '../components/dashboard/ApprovalDeadEndCard';
import { NeedsYourAttention } from '../components/dashboard/NeedsYourAttention';
import { buildTasks } from '../components/dashboard/taskList';
import { PayrollHero } from '../components/dashboard/PayrollHero';
import { LeaveDecisionDrawer, type DecisionOutcome } from '../components/dashboard/LeaveDecisionDrawer';
import { PayrollHeroStage } from '../components/dashboard/PayrollHeroStage';
import { NeedsYouCard } from '../components/dashboard/NeedsYouCard';
import { KpiRow } from '../components/dashboard/KpiRow';
import { AttendanceHeatmap } from '../components/dashboard/AttendanceHeatmap';
import { ApprovalsTable } from '../components/dashboard/ApprovalsTable';
import { ExpiryTimeline } from '../components/dashboard/ExpiryTimeline';
import { Composition } from '../components/dashboard/Composition';
import { ActivityPanel, QuickActions, RecentActivityCard } from '../components/dashboard/ActivityPanel';
import { PayrollByDepartment } from '../components/dashboard/PayrollByDepartment';
import { DashboardViews } from '../components/dashboard/DashboardViews';
import { StageTabs, STAGE_PANEL_ID, STAGE_VIEWS, stageTabId, type StageSwitchVia, type StageViewId } from '../components/dashboard/StageTabs';
import { useStageFrame } from '../hooks/useStageFrame';
import { buildAttention, parseApprovalSubject, tenantHour } from '../components/dashboard/dashboardModel';

// ── Heading: tenant-calendar date, tenant-timezone clock ─────────────────────

function useTenantClock() {
  const { calendarSystem, hijriDatesEnabled, defaultTimezone } = useTenantSettings();
  const f = useFormat();
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 30_000);
    return () => clearInterval(id);
  }, []);
  // The TENANT's stated zone, or — when it has not stated one — `undefined`, which makes Intl use
  // the VIEWER's own browser zone. Never a hard-coded zone: this header read 'America/New_York' for
  // every tenant whose localization row was missing, so a Riyadh customer opened the product's
  // FIRST screen on yesterday's date while the compliance panel beside it showed today's.
  // The blank comes from the API (TenantAdminController.UnstatedLocalizationAsync) and from
  // TenantSettingsContext's own defaults, so both the "not stated" and "not loaded yet" cases land
  // on the viewer's zone rather than on a foreign one.
  const tz = defaultTimezone.trim() || undefined;
  // lib/format.ts applies the same zone (and the viewer's language) to every value below.
  const safe = (fn: () => string) => { try { return fn(); } catch { return null; } };
  const greg = safe(() => f.gregorian(now, 'weekdayDate'));
  const hijri = safe(() => f.hijri(now, 'long'));
  const time = safe(() => f.time(now));
  const wantHijri = calendarSystem === 'Hijri' && !!hijri;
  return {
    now,
    tz,
    primary: wantHijri ? hijri : greg,
    // Intl's islamic-umalqura output already ends in "AH"; appending it again printed "AH AH".
    secondary: hijriDatesEnabled && hijri && greg ? (wantHijri ? greg : hijri) : null,
    time,
    wantHijri,
    fmtTime: (d: Date) => safe(() => f.time(d)) ?? d.toTimeString().slice(0, 5),
  };
}

// ── Page ─────────────────────────────────────────────────────────────────────

/** 12 months, so the payroll trend spans a year; shorter series are sliced where shown. */
const MONTHS = 12;

/**
 * Desktop "stage": from 1280 x 740 up the dashboard is exactly one screen tall and never scrolls
 * the page; its panels are split across three tabs instead (Overview, Departments, Compliance).
 * Smaller windows, tablets and phones keep the stacked / swipe layouts below.
 */
const STAGE_QUERY = '(min-width: 1280px) and (min-height: 740px)';
const STAGE_STORAGE_KEY = 'kx-dashboard-stage-view';
const clamp = (n: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, n));

function CompanyDashboard() {
  const t = useT();
  const f = useFormat();
  const clock = useTenantClock();
  const { companies, selectedCompanyId, isGroupScope, companyVersion } = useCompany();
  const { isFeatureEnabled } = useFeatureFlags();
  const { hasPermission } = useAuth();
  // F10: payroll figures are for payroll readers only (the API also withholds them), and the reports
  // link is only offered to someone who can open reports.
  const canReadPayroll = hasPermission('payroll.read');
  const canReadReports = hasPermission('reports.read');
  const findings = useWorkforceFindings();
  // R03: a payroll amount is in its run's company currency, resolved exactly as the payroll workspace does.
  const payrollCompanies = usePayrollCompanies(canReadPayroll);

  const [data, setData] = useState<DashboardFull | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [loadedAt, setLoadedAt] = useState<Date | null>(null);
  const [refreshFailed, setRefreshFailed] = useState(false);
  const inFlight = useRef(false);
  const loadedAtRef = useRef<number>(0);
  // Leave requests are decided in a drawer over the dashboard; the list below catches up once the server confirms.
  const [reviewId, setReviewId] = useState<string | null>(null);
  const [decidedNote, setDecidedNote] = useState<{ outcome: DecisionOutcome; name: string } | null>(null);
  const compact = useMediaQuery('(max-width: 1023.98px)');
  const phone = useMediaQuery('(max-width: 639.98px)');
  const stage = useMediaQuery(STAGE_QUERY);
  const stageWide = useMediaQuery('(min-width: 1536px)');
  const frame = useStageFrame(stage);
  const [stageView, setStageView] = useState<StageViewId>('overview');
  // The last section is remembered for the session, read after mount so server and client markup agree.
  useEffect(() => {
    try {
      const v = sessionStorage.getItem(STAGE_STORAGE_KEY);
      if (v === 'departments' || v === 'compliance') setStageView(v);
    } catch { /* storage unavailable */ }
  }, []);
  const chooseStageView = (v: StageViewId, via: StageSwitchVia = 'pointer') => {
    try { sessionStorage.setItem(STAGE_STORAGE_KEY, v); } catch { /* storage unavailable */ }
    if (v === stageView) return;
    // A pointer switch cross-fades the panel (View Transitions API, where it exists). Keyboard
    // switches and reduced motion swap at once, per the screen standard.
    const doc = document as Document & { startViewTransition?: (cb: () => void) => unknown };
    const still = via === 'keyboard' || window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (!doc.startViewTransition || still) { setStageView(v); return; }
    const vt = doc.startViewTransition(() => flushSync(() => setStageView(v))) as { ready?: Promise<unknown>; finished?: Promise<unknown> } | undefined;
    // A skipped transition rejects these promises; the view has already swapped, so there is nothing to report.
    vt?.ready?.catch(() => {});
    vt?.finished?.catch(() => {});
  };

  const load = useCallback(async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    setLoading(true);
    setError(null);
    try {
      setData(await dashboardApi.full(MONTHS));
      setLoadedAt(new Date());
      loadedAtRef.current = Date.now();
      setRefreshFailed(false);
    } catch {
      // Keep the last good figures on screen, marked as such, instead of blanking the page.
      setRefreshFailed(true);
      // A dictionary key, translated where it renders (this callback must not depend on the language).
      setError(msg('Dashboard data could not be loaded. Figures shown are from the last successful load, if any.'));
    } finally {
      setLoading(false);
      inFlight.current = false;
    }
  }, []);

  // Reload when the company scope changes: the figures are company-scoped.
  useEffect(() => { void load(); }, [load, companyVersion]);
  const onRefresh = () => { void load(); findings.reload(); };
  // Keeps a dashboard left open honest without a real-time channel: while the tab is visible, figures older
  // than three minutes are re-read quietly (the old ones stay on screen), and again when the tab is revisited.
  useEffect(() => {
    const STALE_MS = 3 * 60_000;
    const refreshIfStale = () => {
      if (document.visibilityState !== 'visible' || !loadedAtRef.current) return;
      if (Date.now() - loadedAtRef.current > STALE_MS) void load();
    };
    const id = window.setInterval(refreshIfStale, 60_000);
    document.addEventListener('visibilitychange', refreshIfStale);
    return () => { window.clearInterval(id); document.removeEventListener('visibilitychange', refreshIfStale); };
  }, [load]);
  useEffect(() => {
    if (!decidedNote) return;
    const id = window.setTimeout(() => setDecidedNote(null), 8000);
    return () => window.clearTimeout(id);
  }, [decidedNote]);

  const payrollEnabled = isFeatureEnabled('payroll') || !!data?.overview.payrollSummary || (data?.payrollTrends.some((p) => p.totalNet > 0) ?? false);
  const hour = tenantHour(clock.tz, clock.now);
  // Payroll readiness items link to /payroll, so they are only raised for someone who can open it.
  const showPayrollAttention = payrollEnabled && canReadPayroll;
  const attention = useMemo(
    () => buildAttention(data, findings.insights, Date.now(), { payroll: showPayrollAttention, t, relative: (iso) => f.relative(iso), period: (ym) => f.period(ym, 'long') }),
    [data, findings.insights, showPayrollAttention, t, f],
  );
  // Payroll validation errors for this month: asked only of someone who can open payroll. A failed
  // read is reported as unavailable, never as zero errors.
  const [payrollErrors, setPayrollErrors] = useState<number | null>(null);
  const [payrollCheckFailed, setPayrollCheckFailed] = useState(false);
  useEffect(() => {
    if (!showPayrollAttention) { setPayrollErrors(null); setPayrollCheckFailed(false); return; }
    let cancelled = false;
    payrollApi.getOverview()
      .then((o) => { if (!cancelled) { setPayrollErrors(o.totalValidationErrors ?? 0); setPayrollCheckFailed(false); } })
      .catch(() => { if (!cancelled) { setPayrollErrors(null); setPayrollCheckFailed(true); } });
    return () => { cancelled = true; };
  }, [showPayrollAttention, companyVersion]);
  const tasks = useMemo(() => buildTasks({ data, payrollValidationErrors: payrollErrors }), [data, payrollErrors]);
  // Tiles show the last six months; the hero uses the full year.
  const tileData = useMemo(() => (data ? { ...data, trends: data.trends.slice(-6) } : null), [data]);

  const latestRun = data?.overview.payrollSummary ?? null;
  const runCurrency = latestRun
    ? resolvePayrollRunCurrency({ companyId: latestRun.companyId ?? null }, payrollCompanies.companies, payrollCompanies.state)
    : null;
  const trendCurrency = resolvePayrollRunsCurrency(
    (data?.payrollTrends ?? []).filter((p) => p.totalNet > 0).map((p) => ({ companyId: p.companyId ?? null })),
    payrollCompanies.companies, payrollCompanies.state);
  const payrollCurrency = runCurrency?.status === 'resolved' ? runCurrency.currency : null;
  const currencyNote = runCurrency?.status === 'unavailable' ? runCurrency.reason : null;

  const companyName = isGroupScope
    ? t('All companies')
    : companies.find((c) => c.id === selectedCompanyId)?.name ?? (companies.length === 1 ? companies[0].name : null);
  const pending = data?.overview.pendingApprovals ?? 0;
  const asOf = loadedAt ? clock.fmtTime(loadedAt) : null;
  const minutesOld = loadedAt ? Math.floor((clock.now.getTime() - loadedAt.getTime()) / 60_000) : 0;

  const header = (
    <header className="flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
      <div className={`min-w-0 ${stage ? 'flex flex-col' : ''}`}>
        <h1 className={`${stage ? 'text-h1' : 'text-[32px]'} font-semibold leading-[1.1] tracking-[-0.025em] text-slate-950 dark:text-white`}>{t('HR Command Center')}</h1>
        {/* On the stage the date line sits ABOVE the title as an eyebrow (CSS order), but stays after the heading in the DOM. */}
        <p className={stage ? 'order-first mb-0.5 text-meta font-medium text-slate-600 dark:text-slate-400' : 'mt-1 text-body-sm text-slate-600 dark:text-slate-400'}>
          {companyName && <span className="font-medium text-slate-900 dark:text-white">{companyName}<span aria-hidden className="mx-1.5 text-slate-400">·</span></span>}
          {clock.primary}{clock.secondary ? ` (${clock.secondary})` : ''}, <span className="tabular-nums">{clock.time}</span>
        </p>
        {stage && <p className="mt-0.5 hidden text-body-sm text-slate-700 min-[1400px]:block dark:text-slate-300">{t('See what needs you today across payroll, people and compliance.')}</p>}
      </div>
      {stage && data && <StageTabs active={stageView} onChange={chooseStageView} badges={{ compliance: data.overview.complianceCriticalTotal }} />}
      <div className="flex shrink-0 flex-wrap items-center gap-2">
        {(refreshFailed || minutesOld >= 10) && (
          <span role="status" className={`rounded-lg px-2.5 py-1.5 text-xs font-medium ${refreshFailed ? 'bg-rose-50 text-rose-900 dark:bg-rose-500/10 dark:text-rose-200' : 'bg-amber-50 text-amber-900 dark:bg-amber-500/10 dark:text-amber-200'}`}>
            {refreshFailed ? (asOf ? t('Refresh failed. Showing figures from {time}.', { time: asOf }) : t('Figures could not be loaded.')) : t('Loaded {count} min ago.', { count: minutesOld })}
          </span>
        )}
        <button type="button" onClick={onRefresh} disabled={loading}
          className="wg-press inline-flex h-10 items-center gap-2 rounded-xl border border-[color:var(--wg-line-strong)] bg-[color:var(--wg-surface)] px-3 text-body-sm font-medium text-slate-700 hover:text-slate-900 disabled:opacity-60 dark:text-slate-200 dark:hover:text-white">
          <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} aria-hidden />
          <span className="tabular-nums">{asOf ? t('Updated {time}', { time: asOf }) : t('Refresh')}</span>
          <span className="sr-only">{t('Refresh dashboard')}</span>
        </button>
        {canReadReports && (
          <Link href="/reports" className={stage
            ? 'wg-press inline-flex h-10 items-center rounded-xl border border-[color:var(--wg-line-strong)] bg-[color:var(--wg-surface)] px-4 text-body-sm font-semibold text-slate-800 hover:text-slate-950 dark:text-slate-100 dark:hover:text-white'
            : 'wg-press inline-flex h-10 items-center rounded-xl bg-sapphire px-4 text-body-sm font-semibold text-white hover:bg-blue-700 dark:bg-blue-600'}>
            {t('Open reports')}
          </Link>
        )}
      </div>
    </header>
  );

  const skeleton = (
    <div className="flex flex-col gap-5" aria-hidden>
      <div className="wg-hero h-[300px] rounded-2xl opacity-60" />
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">{[0, 1, 2, 3].map((k) => <div key={k} className="wg-card h-[210px]" />)}</div>
    </div>
  );

  // Pay packages no reviewer can approve: only for someone who can fix it (create a user), one company at a time.
  const deadEndEl = <ApprovalDeadEndCard companyId={selectedCompanyId} canManage={hasPermission('security.manage')} />;
  const attentionEl = data ? <AttentionStrip items={attention} findingsUnavailable={findings.enabled && findings.failed} compact={stage} /> : null;
  const tasksEl = data ? <NeedsYourAttention tasks={tasks} payrollUnavailable={payrollCheckFailed} compact={stage} /> : null;
  const wide = useMediaQuery('(min-width: 1280px)');
  // Stage sizing: what the frame leaves is shared between the hero chart, the KPI tiles and the
  // approvals list. A short window (`tight`) compacts the tiles and drops the shortcut dock, whose
  // corner the assistant button then has to itself; a tall one (`showActivity`) adds recent activity.
  const frameH = frame.height ?? 800;
  const tight = !stageWide || frameH < 740;
  const showDock = frameH >= 720;
  const dockH = showDock ? 60 : 0;
  const panelH = frameH - 92 - dockH;                      // minus the header and its gap
  const showActivity = frameH >= 880;
  // The needs-you card: a heading, a group label or two and one row per item (six at most).
  const taskBlock = 70 + Math.min(tasks.length + attention.length, 6) * 34;
  const approvalRows = clamp(Math.floor((panelH - taskBlock - 12 - (showDock ? 0 : 56) - (showActivity ? 200 : 0) - 150) / 52), 1, 8);
  const heroEl = data && canReadPayroll
    ? (stage
        ? <PayrollHeroStage data={data} payrollEnabled={payrollEnabled} currency={payrollCurrency} compact={frameH < 680}
            trendCurrency={trendCurrency.status === 'resolved' ? trendCurrency.currency : null} currencyNote={currencyNote} />
        : <PayrollHero data={data} payrollEnabled={payrollEnabled} dense={wide} currency={payrollCurrency}
            trendCurrency={trendCurrency.status === 'resolved' ? trendCurrency.currency : null} currencyNote={currencyNote} />)
    : null;
  const needsEl = data ? <NeedsYouCard tasks={tasks} attention={attention} payrollUnavailable={payrollCheckFailed} findingsUnavailable={findings.enabled && findings.failed} /> : null;
  const kpiEl = tileData ? <KpiRow data={tileData} hour={hour} asOf={asOf} dense={wide} columns={stage && stageWide ? 4 : 2} tight={stage && tight} stage={stage} /> : null;
  const heatEl = data ? <AttendanceHeatmap data={data} /> : null;
  const reviewItem = data?.overview.approvalQueue.find((q) => q.id === reviewId) ?? null;
  const drawerEl = (
    <>
      <LeaveDecisionDrawer item={reviewItem} onClose={() => setReviewId(null)}
        onDecided={(outcome, q) => { setReviewId(null); setDecidedNote({ outcome, name: q.employeeName ?? parseApprovalSubject(q.title).name ?? '' }); void load(); }} />
      <p role="status" className={decidedNote ? 'rounded-lg bg-emerald-50 px-3 py-2 text-body-sm font-medium text-emerald-900 dark:bg-emerald-500/10 dark:text-emerald-200' : 'sr-only'}>
        {decidedNote && (decidedNote.outcome === 'rejected'
          ? t('Rejected the leave request for {name}.', { name: decidedNote.name })
          : decidedNote.outcome === 'advanced'
            ? t('Approved your step for {name}; the request moves to the next approver.', { name: decidedNote.name })
            : t('Approved the leave request for {name}.', { name: decidedNote.name }))}
      </p>
    </>
  );
  const approvalsEl = <ApprovalsTable queue={data?.overview.approvalQueue ?? []} pending={pending} loading={!data && loading} compact={phone} dense={wide}
    narrow={stage} fill={stage && !showActivity} maxRows={stage ? approvalRows : undefined} onReview={setReviewId} />;
  const expiryEl = data ? <ExpiryTimeline data={data} /> : null;
  const compEl = data ? <Composition data={data} /> : null;
  const payDeptEl = data && canReadPayroll ? <PayrollByDepartment data={data} currency={payrollCurrency} /> : null;
  const activityEl = data ? <ActivityPanel feed={data.activityFeed ?? []} /> : null;

  if (compact) {
    return (
      <div className="mx-auto flex w-full min-w-0 flex-col gap-4" aria-label={t('HR Command Center')}>
        {error && <ErrorBanner message={t(error)} onDismiss={() => setError(null)} />}
        {header}
{drawerEl}
        {!data ? skeleton : (
          <DashboardViews
            today={<div className="flex flex-col gap-4">{deadEndEl}{attentionEl}{tasksEl}{heroEl}{kpiEl}</div>}
            actions={<div className="flex flex-col gap-4">{approvalsEl}{expiryEl}{activityEl}</div>}
            insights={<div className="flex flex-col gap-4">{payDeptEl}{heatEl}{compEl}</div>}
            badges={{ actions: pending }}
          />
        )}
      </div>
    );
  }

  if (stage) {
    // One screen, no page scroll: header, attention strip, one section, shortcut dock.
    const heatOrPay = heatEl || payDeptEl;
    const panels: Record<StageViewId, React.ReactNode> = {
      overview: (
        <div className="grid h-full min-h-0 grid-cols-12 gap-3.5">
          <div className={`flex min-h-0 min-w-0 flex-col gap-3.5 ${stageWide ? 'col-span-8' : 'col-span-7'}`}>
            {heroEl && <div className="min-h-0 flex-1">{heroEl}</div>}
            <div className={heroEl ? 'shrink-0' : 'min-h-0 flex-1'}>{kpiEl}</div>
          </div>
          <div className={`flex min-h-0 min-w-0 flex-col gap-3.5 ${stageWide ? 'col-span-4' : 'col-span-5'} ${showDock ? '' : 'pb-14'}`}>
            {needsEl}
            <div className={showActivity ? 'shrink-0' : 'min-h-0 flex-1'}>{approvalsEl}</div>
            {showActivity && data && <div className="min-h-0 flex-1"><RecentActivityCard feed={data.activityFeed ?? []} limit={4} headingId="activity-mini-heading" /></div>}
          </div>
        </div>
      ),
      departments: (
        <div className="grid h-full min-h-0 grid-cols-12 gap-3.5">
          {heatOrPay && (
            <div className="col-span-8 flex min-h-0 min-w-0 flex-col gap-3.5">
              {heatEl && <div className="min-h-0 shrink overflow-auto">{heatEl}</div>}
              {payDeptEl && <div className="flex min-h-0 flex-1 flex-col overflow-auto [&>section]:flex-1">{payDeptEl}</div>}
            </div>
          )}
          <div className={`min-h-0 min-w-0 [&>section]:h-full ${heatOrPay ? 'col-span-4' : 'col-span-12'}`}>{compEl}</div>
        </div>
      ),
      compliance: (
        // Dense deadlines add timeline lanes; the panel then scrolls inside the frame instead of the page.
        <div className="flex h-full min-h-0 flex-col gap-3.5 overflow-y-auto">
          {data && <ExpiryTimeline data={data} part="stats" />}
          <div className="grid shrink-0 grid-cols-12 gap-3.5">
            <div className="col-span-7 min-h-0 min-w-0 [&>section]:h-full">{data ? <ExpiryTimeline data={data} part="timeline" /> : null}</div>
            <div className="col-span-5 min-w-0 [&>section]:h-full">{data ? <ExpiryTimeline data={data} part="coverage" /> : null}</div>
          </div>
          <div className="min-h-[120px] flex-1">{data ? <RecentActivityCard feed={data.activityFeed ?? []} limit={8} columns={2} /> : null}</div>
        </div>
      ),
    };
    return (
      <div ref={frame.ref} style={frame.style} className="mx-auto flex w-full min-w-0 max-w-[1680px] flex-col gap-3" aria-label="HR Command Center">
        {error && <ErrorBanner message={t(error)} onDismiss={() => setError(null)} />}
        {header}
{drawerEl}
        {!data ? skeleton : (
          <>
            {deadEndEl}
            {/* All three sections stay mounted and the inactive ones are hidden, so charts draw once
                per visit to the dashboard, not on every tab change. */}
            <div role="tabpanel" id={STAGE_PANEL_ID} aria-labelledby={stageTabId(stageView)} tabIndex={0} className="min-h-0 flex-1 outline-none focus-visible:ring-2 focus-visible:ring-sapphire">
              {STAGE_VIEWS.map((v) => <div key={v.id} hidden={v.id !== stageView} className={`h-full min-h-0 ${!showDock && v.id !== 'overview' ? 'pb-14' : ''}`}>{panels[v.id]}</div>)}
            </div>
            {showDock && <QuickActions />}
          </>
        )}
      </div>
    );
  }

  return (
    <div className="mx-auto flex w-full min-w-0 max-w-[1680px] flex-col gap-3.5" aria-label="HR Command Center">
      {error && <ErrorBanner message={t(error)} onDismiss={() => setError(null)} />}
      {header}
{drawerEl}
      {!data ? skeleton : (
        <>
          {deadEndEl}
          {attentionEl}
          {tasksEl}
          {/* At a glance: two independent columns that each stack upward, so no box waits
              for its neighbour's height and nothing leaves a gap. Below 1280 px the same pieces
              stack in one column. */}
          {/* The last card in each column stretches, so both columns end level with no gaps. */}
          <div className="grid min-w-0 grid-cols-1 gap-3.5 xl:grid-cols-12">
            <div className="flex min-w-0 flex-col gap-3.5 xl:col-span-7">
              {heroEl}
              {payDeptEl}
              {heatEl}
              <div className="flex min-w-0 flex-1 flex-col [&>section]:flex-1">{expiryEl}</div>
            </div>
            <div className="flex min-w-0 flex-col gap-3.5 xl:col-span-5">
              {kpiEl}
              {approvalsEl}
              <div className="flex min-w-0 flex-1 flex-col [&>section]:flex-1">{compEl}</div>
            </div>
          </div>
          {activityEl}
        </>
      )}
    </div>
  );
}

// ── Home: "My day" for the person, "Company" for the workforce ───────────────
// Everyone is an employee first. Someone who also holds the workforce dashboard permission gets a
// switch between their own day and the company view; it opens on "My day" and remembers the
// last choice. The switch only chooses a view: every figure is still gated by the API's permissions.

type HomeView = 'me' | 'company';

const HOME_VIEWS: HomeView[] = ['me', 'company'];

export function DashboardPage() {
  const t = useT();
  const { user } = useAuth();
  // "My day" needs an employee record linked to this login; an admin without one has no personal
  // view to show, and the self-service page would only answer with an error.
  // A self-service-only login (lib/homePath.ts decides who that is, including the ESSOnly portal) gets
  // no switch; it stays on its own view even if a role also grants the workforce dashboard.
  const employeeOnly = isEmployeeOnly(user) && user?.employeeId != null;
  const canSeeBoth = !isEmployeeOnly(user) && !!user?.permissions.includes('ess.read') && !!user?.permissions.includes('dashboard.read') && user?.employeeId != null;
  const storageKey = `kx.home-view.${user?.id ?? 'anon'}`;
  // Unknown until the browser's remembered choice is read, so the wrong view never mounts first
  // (it would flash, and fire the other view's requests for nothing).
  const [view, setView] = useState<HomeView | null>(null);

  useEffect(() => {
    let remembered: HomeView = 'me';
    try { if (window.localStorage.getItem(storageKey) === 'company') remembered = 'company'; } catch { /* private mode */ }
    setView(remembered);
  }, [storageKey]);

  const choose = (v: HomeView) => {
    setView(v);
    try { window.localStorage.setItem(storageKey, v); } catch { /* private mode */ }
  };

  if (employeeOnly) return <EmployeeSelfServicePage />;
  if (!canSeeBoth) return <CompanyDashboard />;

  const labels: Record<HomeView, string> = { me: t('My day'), company: t('Company') };
  const onKeyDown = (e: KeyboardEvent, current: HomeView) => {
    const step = e.key === 'ArrowRight' || e.key === 'ArrowDown' ? 1 : e.key === 'ArrowLeft' || e.key === 'ArrowUp' ? -1 : 0;
    if (!step) return;
    e.preventDefault();
    const next = HOME_VIEWS[(HOME_VIEWS.indexOf(current) + step + HOME_VIEWS.length) % HOME_VIEWS.length];
    choose(next);
    document.getElementById(`home-tab-${next}`)?.focus();
  };

  return (
    <div className="flex flex-col gap-3.5">
      <div role="tablist" aria-label={t('Home view')} className="inline-flex w-fit gap-1 rounded-xl border border-[color:var(--wg-line-strong)] bg-[color:var(--wg-surface)] p-1">
        {HOME_VIEWS.map((id) => (
          <button key={id} id={`home-tab-${id}`} type="button" role="tab" aria-selected={view === id} aria-controls="home-panel"
            tabIndex={view === id ? 0 : -1} onClick={() => choose(id)} onKeyDown={(e) => onKeyDown(e, id)}
            className={`h-8 rounded-lg px-4 text-body-sm font-semibold transition ${view === id ? 'bg-sapphire text-white dark:bg-blue-600' : 'text-slate-700 hover:text-slate-950 dark:text-slate-300 dark:hover:text-white'}`}>
            {labels[id]}
          </button>
        ))}
      </div>
      <div id="home-panel" role="tabpanel" aria-labelledby={view ? `home-tab-${view}` : undefined}>
        {view === 'me' ? <EmployeeSelfServicePage /> : view === 'company' ? <CompanyDashboard /> : null}
      </div>
    </div>
  );
}
