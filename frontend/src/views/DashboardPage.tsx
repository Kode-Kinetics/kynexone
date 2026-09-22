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
import Link from 'next/link';
import { RefreshCw } from 'lucide-react';
import { dashboardApi } from '../api/dashboard';
import type { DashboardFull } from '../api/dashboard';
import { useTenantSettings } from '../contexts/TenantSettingsContext';
import { useCompany } from '../contexts/CompanyContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useWorkforceFindings } from '../hooks/useWorkforceFindings';
import { useMediaQuery } from '../hooks/useMediaQuery';
import { useT } from '../hooks/useT';
import { ErrorBanner } from '../components/ui/ErrorBanner';
import { AttentionStrip } from '../components/dashboard/AttentionStrip';
import { PayrollHero } from '../components/dashboard/PayrollHero';
import { KpiRow } from '../components/dashboard/KpiRow';
import { AttendanceHeatmap } from '../components/dashboard/AttendanceHeatmap';
import { ApprovalsTable } from '../components/dashboard/ApprovalsTable';
import { ExpiryTimeline } from '../components/dashboard/ExpiryTimeline';
import { Composition } from '../components/dashboard/Composition';
import { DashboardViews } from '../components/dashboard/DashboardViews';
import { buildAttention, tenantHour } from '../components/dashboard/dashboardModel';

// ── Heading: tenant-calendar date, tenant-timezone clock ─────────────────────

function useTenantClock() {
  const { calendarSystem, hijriDatesEnabled, defaultTimezone } = useTenantSettings();
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 30_000);
    return () => clearInterval(id);
  }, []);
  const tz = defaultTimezone || undefined;
  const fmt = (opts: Intl.DateTimeFormatOptions, cal?: string) => {
    try { return new Intl.DateTimeFormat(cal ? `en-GB-u-ca-${cal}` : 'en-GB', { ...opts, timeZone: tz }).format(now); }
    catch { return null; }
  };
  const greg = fmt({ weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
  const hijri = fmt({ day: 'numeric', month: 'long', year: 'numeric' }, 'islamic-umalqura');
  const time = fmt({ hour: '2-digit', minute: '2-digit' });
  const wantHijri = calendarSystem === 'Hijri' && !!hijri;
  return {
    now,
    tz,
    primary: wantHijri ? hijri : greg,
    // Intl's islamic-umalqura output already ends in "AH"; appending it again printed "AH AH".
    secondary: hijriDatesEnabled && hijri && greg ? (wantHijri ? greg : hijri) : null,
    time,
    wantHijri,
    fmtTime: (d: Date) => {
      try { return new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit', timeZone: tz }).format(d); }
      catch { return d.toTimeString().slice(0, 5); }
    },
  };
}

// ── Page ─────────────────────────────────────────────────────────────────────

/** 12 months, so the payroll trend spans a year; shorter series are sliced where shown. */
const MONTHS = 12;

export function DashboardPage() {
  const t = useT();
  const clock = useTenantClock();
  const { companies, selectedCompanyId, isGroupScope, companyVersion } = useCompany();
  const { isFeatureEnabled } = useFeatureFlags();
  const findings = useWorkforceFindings();

  const [data, setData] = useState<DashboardFull | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [loadedAt, setLoadedAt] = useState<Date | null>(null);
  const [refreshFailed, setRefreshFailed] = useState(false);
  const inFlight = useRef(false);
  const compact = useMediaQuery('(max-width: 1023.98px)');
  const phone = useMediaQuery('(max-width: 639.98px)');

  const load = useCallback(async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    setLoading(true);
    setError(null);
    try {
      setData(await dashboardApi.full(MONTHS));
      setLoadedAt(new Date());
      setRefreshFailed(false);
    } catch {
      // Keep the last good figures on screen, marked as such, instead of blanking the page.
      setRefreshFailed(true);
      setError('Dashboard data could not be loaded. Figures shown are from the last successful load, if any.');
    } finally {
      setLoading(false);
      inFlight.current = false;
    }
  }, []);

  // Reload when the company scope changes: the figures are company-scoped.
  useEffect(() => { void load(); }, [load, companyVersion]);
  const onRefresh = () => { void load(); findings.reload(); };

  const payrollEnabled = isFeatureEnabled('payroll') || !!data?.overview.payrollSummary || (data?.payrollTrends.some((p) => p.totalNet > 0) ?? false);
  const hour = tenantHour(clock.tz, clock.now);
  const attention = useMemo(() => buildAttention(data, findings.insights), [data, findings.insights]);
  // Tiles show the last six months; the hero uses the full year.
  const tileData = useMemo(() => (data ? { ...data, trends: data.trends.slice(-6) } : null), [data]);

  const companyName = isGroupScope
    ? t('All companies')
    : companies.find((c) => c.id === selectedCompanyId)?.name ?? (companies.length === 1 ? companies[0].name : null);
  const pending = data?.overview.pendingApprovals ?? 0;
  const asOf = loadedAt ? clock.fmtTime(loadedAt) : null;
  const minutesOld = loadedAt ? Math.floor((clock.now.getTime() - loadedAt.getTime()) / 60_000) : 0;

  const header = (
    <header className="flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
      <div className="min-w-0">
        <h1 className="text-[26px] font-semibold leading-tight tracking-[-0.02em] text-slate-900 dark:text-white">{t('HR Command Center')}</h1>
        <p className="mt-0.5 text-[13px] text-slate-700 dark:text-slate-300">
          {companyName && <span className="font-medium text-slate-900 dark:text-white">{companyName}. </span>}
          {clock.primary}{clock.secondary ? ` (${clock.secondary})` : ''}, <span className="tabular-nums">{clock.time}</span>
        </p>
      </div>
      <div className="flex shrink-0 flex-wrap items-center gap-2">
        {(refreshFailed || minutesOld >= 10) && (
          <span role="status" className={`rounded-lg px-2.5 py-1.5 text-xs font-medium ${refreshFailed ? 'bg-rose-50 text-rose-900 dark:bg-rose-500/10 dark:text-rose-200' : 'bg-amber-50 text-amber-900 dark:bg-amber-500/10 dark:text-amber-200'}`}>
            {refreshFailed ? (asOf ? `${t('Refresh failed. Showing figures from')} ${asOf}.` : t('Figures could not be loaded.')) : `${t('Loaded')} ${minutesOld} ${t('min ago')}.`}
          </span>
        )}
        <button type="button" onClick={onRefresh} disabled={loading}
          className="wg-press inline-flex h-10 items-center gap-2 rounded-xl border border-[color:var(--wg-line-strong)] bg-[color:var(--wg-surface)] px-3 text-[13px] font-medium text-slate-700 hover:text-slate-900 disabled:opacity-60 dark:text-slate-200 dark:hover:text-white">
          <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} aria-hidden />
          <span className="tabular-nums">{asOf ? `${t('Updated')} ${asOf}` : t('Refresh')}</span>
          <span className="sr-only">{t('Refresh dashboard')}</span>
        </button>
        <Link href="/reports" className="wg-press inline-flex h-10 items-center rounded-xl bg-sapphire px-4 text-[13px] font-semibold text-white hover:bg-blue-700 dark:bg-blue-600">
          {t('Open reports')}
        </Link>
      </div>
    </header>
  );

  const skeleton = (
    <div className="flex flex-col gap-5" aria-hidden>
      <div className="wg-hero h-[300px] rounded-[22px] opacity-60" />
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">{[0, 1, 2, 3].map((k) => <div key={k} className="wg-card h-[210px]" />)}</div>
    </div>
  );

  const attentionEl = data ? <AttentionStrip items={attention} findingsUnavailable={findings.enabled && findings.failed} /> : null;
  const heroEl = data ? <PayrollHero data={data} payrollEnabled={payrollEnabled} /> : null;
  const kpiEl = tileData ? <KpiRow data={tileData} hour={hour} asOf={asOf} /> : null;
  const heatEl = data ? <AttendanceHeatmap data={data} /> : null;
  const approvalsEl = <ApprovalsTable queue={data?.overview.approvalQueue ?? []} pending={pending} loading={!data && loading} compact={phone} />;
  const expiryEl = data ? <ExpiryTimeline data={data} /> : null;
  const compEl = data ? <Composition data={data} /> : null;

  if (compact) {
    return (
      <div className="mx-auto flex w-full min-w-0 flex-col gap-4" aria-label="HR Command Center">
        {error && <ErrorBanner message={error} onDismiss={() => setError(null)} />}
        {header}
        {!data ? skeleton : (
          <DashboardViews
            today={<div className="flex flex-col gap-4">{attentionEl}{heroEl}{kpiEl}</div>}
            actions={<div className="flex flex-col gap-4">{approvalsEl}{expiryEl}</div>}
            insights={<div className="flex flex-col gap-4">{heatEl}{compEl}</div>}
            badges={{ actions: pending }}
          />
        )}
      </div>
    );
  }

  return (
    <div className="mx-auto flex w-full min-w-0 max-w-[1600px] flex-col gap-5" aria-label="HR Command Center">
      {error && <ErrorBanner message={error} onDismiss={() => setError(null)} />}
      {header}
      {!data ? skeleton : (
        <>
          {attentionEl}
          {heroEl}
          {kpiEl}
          {/* Side by side only when both exist and there is room; approvals take the row otherwise. */}
          <div className={`grid min-w-0 grid-cols-1 gap-5 ${data.analytics?.attendanceHeatmap ? 'min-[1380px]:grid-cols-[minmax(0,1.1fr)_minmax(0,1fr)]' : ''}`}>{heatEl}{approvalsEl}</div>
          <div className="grid min-w-0 grid-cols-1 gap-5 xl:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]">{expiryEl}{compEl}</div>
        </>
      )}
    </div>
  );
}
