'use client';

import { FormEvent, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  Activity,
  AlertTriangle,
  Lightbulb,
  CheckCircle2,
  Clock,
  Database,
  FileSpreadsheet,
  MapPin,
  RefreshCw,
  ShieldCheck,
  Timer,
  X,
  Users,
} from 'lucide-react';
import { attendanceApi } from '../api/attendance';
import type {
  AttendanceAIInsight,
  AttendanceDailyRecord,
  AttendanceDashboardSummary,
  AttendanceDevice,
  AttendanceDeviceSyncSummary,
  AttendancePayrollSummary,
  AttendanceRawEvent,
  AttendanceRegularizationRequest,
} from '../api/attendance';
import { employeesApi } from '../api/employees';
import { SelfieViewerModal } from '../components/attendance/SelfieViewerModal';
import { AttendanceExceptionsPanel } from '../components/attendance/AttendanceExceptionsPanel';
import { AttendanceCsvImportPanel } from '../components/attendance/AttendanceCsvImportPanel';
import { AttendanceRecordPanel } from '../components/attendance/AttendanceRecordPanel';
import { AttendanceProcessPanel } from '../components/attendance/AttendanceProcessPanel';
import { useT } from '../hooks/useT';
import { msg } from '../i18n/translations';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { enumLabel } from '../components/EnumLabel';
import type { EmployeeListItem } from '../api/employees';
import { StatusChip } from '../components/StatusChip';
import { useAuth } from '../contexts/AuthContext';
import { massEntryAllowed } from '../lib/attendanceBulkEntry';
import { ModuleSettingsLink } from '../components/setup/ModuleSettingsLink';
import { useModuleSectionHref } from '../hooks/useModuleSectionHref';
import { PageHeader } from '../components/ui/Page';
import { Field } from '../components/ui/Field';
import { EmptyState } from '../components/ui/EmptyState';
import { ResultBanner, type ResultNext } from '../components/ui/ResultBanner';
import { SkeletonRows } from '../components/ui/Skeleton';
import { describeApiError } from '../lib/apiError';
import { DomainUnavailable, Empty, Panel as BasePanel, SectionTitle, statusTone } from '../components/attendance/attendanceUi';
import { RovingTabList, TabPanel } from '../components/ui/RovingTabs';
import {
  attendanceErrorSummary,
  regularizationQueueUnavailableMessage,
  unavailableMessage,
  type AttendanceLoadErrors,
} from '../lib/attendanceLoadState';

type TabKey = 'dashboard' | 'raw' | 'processing' | 'regularization' | 'exceptions' | 'reports' | 'ai';
const today = () => new Date().toISOString().slice(0, 10);
const nowLocal = () => new Date(Date.now() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 16);
const toUtc = (value: string) => value ? new Date(value).toISOString() : undefined;

/** "3h 20m" in the viewer's language (the unit letters are part of the translated sentence). */
function useMinutes() {
  const t = useT();
  return useCallback((value: number) => value > 0
    ? t('{hours}h {minutes}m', { hours: Math.floor(value / 60), minutes: value % 60 })
    : t('{minutes}m', { minutes: 0 }), [t]);
}

const tabs: Array<{ key: TabKey; label: string; icon: typeof Activity }> = [
  { key: 'dashboard', label: msg('Live Dashboard'), icon: Activity },
  { key: 'raw', label: msg('Raw Punch Logs'), icon: Database },
  { key: 'processing', label: msg('Processing'), icon: RefreshCw },
  { key: 'regularization', label: msg('Regularization'), icon: ShieldCheck },
  { key: 'exceptions', label: msg('Exceptions'), icon: AlertTriangle },
  { key: 'reports', label: msg('Reports'), icon: FileSpreadsheet },
  { key: 'ai', label: msg('Insights'), icon: Lightbulb },
];

/**
 * The count captions on the panels arrive as English ("5 open", "12 latest", "Unavailable"); this turns them
 * into the viewer's language where they render, so the call sites keep their exact, tested wording.
 */
function localizeAction(action: string | undefined, t: ReturnType<typeof useT>, count: (n: number) => string): string | undefined {
  if (!action) return action;
  if (action === 'Unavailable') return t('Unavailable');
  const m = /^(\d+) (devices|open|latest|employees|signals|pending)$/.exec(action);
  if (!m) return action;
  const n = Number(m[1]);
  switch (m[2]) {
    case 'devices': return t('{count} devices', { count: count(n) });
    case 'open': return t('{count} open', { count: count(n) });
    case 'latest': return t('{count} latest', { count: count(n) });
    case 'employees': return t('{count} employees', { count: count(n) });
    case 'signals': return t('{count} signals', { count: count(n) });
    default: return t('{count} pending', { count: count(n) });
  }
}

function Panel({ title, action, children }: { title: string; action?: string; children: React.ReactNode }) {
  const t = useT();
  const fx = useFormat();
  return <BasePanel title={title} action={localizeAction(action, t, fx.integer)}>{children}</BasePanel>;
}

/** Punch direction values the API stores, in the viewer's language. */
function directionLabel(t: ReturnType<typeof useT>, value: string): string {
  switch (value) {
    case 'In': return t('Punch in');
    case 'Out': return t('Punch out');
    case 'BreakIn': return t('Break start');
    case 'BreakOut': return t('Break end');
    case 'Unknown': return t('Unknown');
    default: return value;
  }
}


export function AttendancePage() {
  // Devices and CSV import are `attendance.bulk_import`; processing is `attendance.write`
  // (AttendanceController). Read-only attendance roles (HR Assistant, payroll, managers, auditors)
  // used to be offered these and only ever got a 403.
  const { user, hasPermission } = useAuth();
  const t = useT();
  const fx = useFormat();
  const minutes = useMinutes();
  const canManageSources = hasPermission('attendance.bulk_import');
  const canProcess = hasPermission('attendance.write');
  // Recording attendance FOR an employee: attendance.write (or attendance.kiosk at the kiosk), as the server
  // requires. Without it the raw-event and correction forms are not offered. Punching for YOURSELF needs no
  // permission, so the punch form stays for any linked employee, fixed to their own record.
  const canPunchForOthers = canProcess || hasPermission('attendance.kiosk');
  const ownEmployeeId = typeof user?.employeeId === 'number' && user.employeeId > 0 ? user.employeeId : undefined;
  const canPunch = canPunchForOthers || ownEmployeeId !== undefined;
  const [activeTab, setActiveTab] = useState<TabKey>('dashboard');
  const [summary, setSummary] = useState<AttendanceDashboardSummary | null>(null);
  const [daily, setDaily] = useState<AttendanceDailyRecord[]>([]);
  const [rawEvents, setRawEvents] = useState<AttendanceRawEvent[]>([]);
  const [devices, setDevices] = useState<AttendanceDevice[]>([]);
  const [employees, setEmployees] = useState<EmployeeListItem[]>([]);
  const [regularizations, setRegularizations] = useState<AttendanceRegularizationRequest[]>([]);
  const [pendingRegularizations, setPendingRegularizations] = useState<AttendanceRegularizationRequest[]>([]);
  const [payrollSummary, setPayrollSummary] = useState<AttendancePayrollSummary[]>([]);
  const [deviceSync, setDeviceSync] = useState<AttendanceDeviceSyncSummary[]>([]);
  const [insights, setInsights] = useState<AttendanceAIInsight[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [messageNext, setMessageNext] = useState<ResultNext | undefined>(undefined);
  const [messageSeq, setMessageSeq] = useState(0);
  // The record panel stays mounted while closed (hidden), so a run in progress or a half-typed entry survives closing it.
  const [recordOpen, setRecordOpen] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);
  // Client-side filter behind the "Missing punch" tile: the daily rows already carry `missingPunch`, the API takes no such status.
  const [missingOnly, setMissingOnly] = useState(false);
  // A mass-entry run in progress, reported by the (hidden but mounted) record panel so the header can show it while closed.
  const [runProgress, setRunProgress] = useState<{ done: number; total: number } | null>(null);
  const recordButtonRef = useRef<HTMLButtonElement>(null);
  const recordRef = useRef<HTMLDivElement>(null);
  const dateRef = useRef<HTMLInputElement>(null);
  const [loadErrors, setLoadErrors] = useState<AttendanceLoadErrors>({});

  // Empty until resolved from the DATA on mount — see the effect below. Defaulting this to
  // today() opened the whole screen empty on every tenant, because attendance only exists for
  // days that have already happened.
  const [filterDate, setFilterDate] = useState('');
  const [statusFilter, setStatusFilter] = useState('');
  const [punchEmployeeId, setPunchEmployeeId] = useState('');
  // Without the on-behalf right the punch is always the caller's own.
  useEffect(() => { if (!canPunchForOthers && ownEmployeeId !== undefined) setPunchEmployeeId(String(ownEmployeeId)); }, [canPunchForOthers, ownEmployeeId]);
  const [punchDirection, setPunchDirection] = useState('In');
  const [punchSource, setPunchSource] = useState<'web' | 'mobile' | 'kiosk'>('web');
  const [rawForm, setRawForm] = useState({ employeeCode: '', employeeId: '', deviceId: '', punchAt: nowLocal(), direction: 'In', source: 'API push', verificationMethod: 'RFID' });
  const [regularizationForm, setRegularizationForm] = useState({ employeeId: '', workDate: today(), requestType: 'Missed punch', requestedIn: '', requestedOut: '', reason: '' });
  const [decisionComment, setDecisionComment] = useState(() => t('Reviewed by HR.'));

  const selectedEmployee = useMemo(
    () => employees.find((e) => e.id === Number(punchEmployeeId || regularizationForm.employeeId)),
    [employees, punchEmployeeId, regularizationForm.employeeId],
  );

  // Open on the most recent day that actually HAS attendance, resolved from the data itself.
  //
  // Why not today(): attendance is only ever written for days that have happened. The demo
  // seeders stop at yesterday, so the screen opened empty on every tenant. Re-seeding cannot fix
  // that and neither can seeding further ahead — a Tuesday reseed still leaves a Wednesday demo
  // looking at an empty day, and seeding attendance into the future would be fabricating
  // punches for days nobody worked. Asking the data "what is the latest day you have?" is the
  // only default that is correct on any day, with no reseed and nothing to re-run.
  //
  // The request deliberately sends no from/to: GetDailyAsync now defaults to a trailing window
  // ordered by WorkDate descending, so item[0] is the latest record. today() is used only when
  // the tenant has no attendance at all, which keeps a brand-new tenant on a sensible date.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      let resolved = today();
      try {
        const latest = await attendanceApi.daily({ pageSize: 1 });
        const workDate = latest.items[0]?.workDate;
        if (workDate) resolved = String(workDate).slice(0, 10);
      } catch {
        /* fall back to today() — the main load below surfaces any real error */
      }
      if (!cancelled) setFilterDate(resolved);
    })();
    return () => { cancelled = true; };
  }, []);

  const load = useCallback(async () => {
    if (!filterDate) return;   // wait for the initial date to resolve from the data
    setLoading(true);
    setError('');
    setLoadErrors({});
    setSummary(null);
    setDaily([]);
    setRawEvents([]);
    setDevices([]);
    setEmployees([]);
    setRegularizations([]);
    setPendingRegularizations([]);
    setPayrollSummary([]);
    setDeviceSync([]);
    setInsights([]);
    const [dashboard, day, raw, devicePage, employeePage, regPage, pendingPage, payroll, sync, ai] = await Promise.allSettled([
        attendanceApi.dashboard(filterDate),
        attendanceApi.daily({ from: filterDate, to: filterDate, status: statusFilter || undefined, pageSize: 50 }),
        attendanceApi.events.raw({ from: filterDate, to: filterDate, pageSize: 50 }),
        attendanceApi.devices.list({ pageSize: 50 }),
        employeesApi.listAll(),
        attendanceApi.regularization.mine({ pageSize: 25 }),
        attendanceApi.regularization.pending({ pageSize: 25 }),
        attendanceApi.reports.payrollSummary(filterDate, filterDate),
        attendanceApi.reports.deviceSync(),
        attendanceApi.aiInsights(),
      ]);
    const failures: AttendanceLoadErrors = {};
    if (dashboard.status === 'fulfilled') setSummary(dashboard.value); else failures.dashboard = 'failed';
    if (day.status === 'fulfilled') setDaily(day.value.items); else failures.daily = 'failed';
    if (raw.status === 'fulfilled') setRawEvents(raw.value.items); else failures.raw = 'failed';
    if (devicePage.status === 'fulfilled') setDevices(devicePage.value.items); else failures.devices = 'failed';
    if (employeePage.status === 'fulfilled') setEmployees(employeePage.value); else failures.employees = 'failed';
    if (regPage.status === 'fulfilled') setRegularizations(regPage.value.items); else failures.regularizations = 'failed';
    if (pendingPage.status === 'fulfilled') setPendingRegularizations(pendingPage.value.items); else failures.pendingRegularizations = 'failed';
    if (payroll.status === 'fulfilled') setPayrollSummary(payroll.value); else failures.payrollSummary = 'failed';
    if (sync.status === 'fulfilled') setDeviceSync(sync.value); else failures.deviceSync = 'failed';
    if (ai.status === 'fulfilled') setInsights(ai.value); else failures.insights = 'failed';
    setLoadErrors(failures);
    setUpdatedAt(new Date());
    setLoading(false);
  }, [filterDate, statusFilter]);

  useEffect(() => { load(); }, [load]);

  const runAction = async (action: () => Promise<unknown>, success: string, next?: ResultNext) => {
    setSaving(true);
    setError('');
    setMessage('');
    setMessageNext(undefined);
    try {
      await action();
      setMessage(success);
      setMessageNext(next);
      setMessageSeq((n) => n + 1);
      await load();
    } catch (err) {
      setError(describeApiError(err, t));
    } finally {
      setSaving(false);
    }
  };

  /** Move focus into the record panel (the first control that can take it) and bring it into view. */
  const focusRecord = useCallback(() => {
    const host = recordRef.current;
    if (!host) return;
    host.scrollIntoView?.({ block: 'nearest' });
    host.querySelector<HTMLElement>('input:not([disabled]), select:not([disabled]), button:not([disabled])')?.focus();
  }, []);
  const canRecord = canPunch || massEntryAllowed(hasPermission);
  const showRecord = recordOpen && activeTab === 'dashboard';
  // The header's one primary action: open the record panel (from another tab it first returns to the dashboard).
  const toggleRecord = () => {
    if (activeTab !== 'dashboard') { setActiveTab('dashboard'); setRecordOpen(true); return; }
    setRecordOpen((open) => !open);
  };
  const openRecord = () => { setActiveTab('dashboard'); setRecordOpen(true); };
  // Closing returns focus to the header button, which stays where it was (only its label changes).
  const closeRecord = () => { setRecordOpen(false); recordButtonRef.current?.focus(); };
  useEffect(() => {
    if (!showRecord) return;
    const frame = requestAnimationFrame(focusRecord);
    return () => cancelAnimationFrame(frame);
  }, [showRecord, focusRecord]);

  const submitPunch = (event: FormEvent) => {
    event.preventDefault();
    const employeeId = Number(punchEmployeeId);
    runAction(() => attendanceApi.punch[punchSource]({ employeeId, punchDirection, locationName: 'Web console' }), t('Punch saved to live attendance events.'),
      { label: t('Record another'), onClick: focusRecord });
  };

  const submitRawEvent = (event: FormEvent) => {
    event.preventDefault();
    runAction(() => attendanceApi.events.push({
      employeeId: rawForm.employeeId ? Number(rawForm.employeeId) : undefined,
      employeeCode: rawForm.employeeCode || undefined,
      deviceId: rawForm.deviceId || undefined,
      source: rawForm.source,
      punchTimestampUtc: toUtc(rawForm.punchAt)!,
      punchDirection: rawForm.direction,
      verificationMethod: rawForm.verificationMethod,
      rawPayloadJson: JSON.stringify({ source: rawForm.source, submittedFrom: 'Attendance workspace' }),
    }), t('Raw attendance event saved.'));
  };

  const submitRegularization = (event: FormEvent) => {
    event.preventDefault();
    runAction(() => attendanceApi.regularization.create({
      employeeId: Number(regularizationForm.employeeId),
      workDate: regularizationForm.workDate,
      requestType: regularizationForm.requestType,
      requestedInUtc: toUtc(regularizationForm.requestedIn),
      requestedOutUtc: toUtc(regularizationForm.requestedOut),
      reason: regularizationForm.reason,
    }), t('Regularization request saved for approval.'));
  };

  const totalWorked = daily.reduce((sum, item) => sum + item.totalWorkedMinutes, 0);
  const dashboardUnavailable = unavailableMessage('dashboard', loadErrors);
  const dailyUnavailable = unavailableMessage('daily', loadErrors);
  const deviceSyncUnavailable = unavailableMessage('deviceSync', loadErrors);
  const rawUnavailable = unavailableMessage('raw', loadErrors);
  const payrollSummaryUnavailable = unavailableMessage('payrollSummary', loadErrors);
  const insightsUnavailable = unavailableMessage('insights', loadErrors);
  const regularizationsUnavailable = unavailableMessage('regularizations', loadErrors);
  const correctionQueueUnavailable = regularizationQueueUnavailableMessage(loadErrors, pendingRegularizations.length);

  const clearFilters = () => { setStatusFilter(''); setMissingOnly(false); };
  // What the Live Dashboard table shows: the day's rows, narrowed to the missing punches when that tile is on.
  const shownDaily = useMemo(() => (missingOnly ? daily.filter((r) => r.missingPunch) : daily), [daily, missingOnly]);
  const filtering = !!statusFilter || missingOnly;
  // Announced in the live region next to Refresh, so a tile click is heard as well as seen.
  const resultsNote = filtering && !loading && activeTab === 'dashboard' ? t('{count, plural, one {# result} other {# results}}', { count: shownDaily.length }) : '';
  const toggleStatusTile = (status: string) => { setMissingOnly(false); setStatusFilter((current) => (current === status ? '' : status)); };
  const toggleMissingTile = () => { setStatusFilter(''); setMissingOnly((on) => !on); };

  const dailyEmpty = filtering ? (
    <EmptyState kind="filtered" title={t('No records match')}
      description={missingOnly
        ? t('No attendance with a missing punch on {date}.', { date: filterDate ? fx.date(filterDate) : '' })
        : t('No attendance with status {status} on {date}.', { status: enumLabel(t, 'AttendanceStatus', statusFilter), date: filterDate ? fx.date(filterDate) : '' })}
      action={{ onClick: clearFilters }} />
  ) : canRecord ? (
    <EmptyState kind="first-use" icon={Clock} title={t('Nothing recorded for this day yet')} description={t('Punches appear here once they are processed.')}
      action={{ label: t('Record attendance'), onClick: () => { setActiveTab('dashboard'); setRecordOpen(true); } }} />
  ) : (
    <EmptyState kind="filtered" title={t('Nothing recorded for this day yet')} description={t('Punches appear here once they are processed.')}
      action={{ label: t('Change the date'), onClick: () => dateRef.current?.focus() }} />
  );

  // What the tabs show: one day, optionally one status. Directly above the records they filter.
  const controls = (
    <div className="flex flex-wrap items-center gap-2">
      <input ref={dateRef} type="date" value={filterDate} onChange={(e) => setFilterDate(e.target.value)} className="input w-auto" aria-label={t('Attendance date')} />
      <select value={statusFilter} onChange={(e) => { setMissingOnly(false); setStatusFilter(e.target.value); }} className="select w-auto" aria-label={t('Status filter')}>
        <option value="">{t('All statuses')}</option>
        <option value="Present">{enumLabel(t, 'AttendanceStatus', 'Present')}</option>
        <option value="Late">{enumLabel(t, 'AttendanceStatus', 'Late')}</option>
        <option value="Half day">{enumLabel(t, 'AttendanceStatus', 'Half day')}</option>
        <option value="Absent">{enumLabel(t, 'AttendanceStatus', 'Absent')}</option>
      </select>
      <button type="button" onClick={load} className="btn-ghost" disabled={loading}><RefreshCw className="h-4 w-4" aria-hidden="true" />{t('Refresh')}</button>
      <span role="status" className="text-meta text-fg-muted">
        {resultsNote && <span className="sr-only">{resultsNote}. </span>}
        {updatedAt && !loading ? t('Updated {time}', { time: fx.time(updatedAt) }) : ''}
      </span>
    </div>
  );

  return (
    <div className="space-y-4">
      <PageHeader
        title={t('Attendance workspace')}
        purpose={activeTab === 'dashboard'
          ? t('See who is in, late or missing on a day, and record the punches that are missing.')
          : t('Review punches, device health, corrections, processing, payroll impacts, and exceptions from available data sources.')}
        actions={<>
          {runProgress && !showRecord && (
            // A run keeps going while the panel is closed; this chip says so and brings the panel back.
            <span role="status">
              <button type="button" className="btn-secondary" onClick={openRecord}>
                <RefreshCw className="h-4 w-4 animate-spin" aria-hidden="true" />{t('Recording {done} of {total}', { done: runProgress.done, total: runProgress.total })}
              </button>
            </span>
          )}
          <ModuleSettingsLink sections={['attendanceRules', 'attendanceDevices']} label={msg('Attendance settings')} />
        </>}
        primary={canRecord ? (
          // While the panel is open this is a plain Close, so the panel's own submit is the one primary on screen.
          <button ref={recordButtonRef} type="button" className={showRecord ? 'btn-secondary' : 'btn-primary'} onClick={toggleRecord} aria-expanded={showRecord} aria-controls="attendance-record-panel">
            {showRecord ? <X className="h-4 w-4" aria-hidden="true" /> : <Clock className="h-4 w-4" aria-hidden="true" />}{showRecord ? t('Close') : t('Record attendance')}
          </button>
        ) : undefined}
      />

      {error && (
        <div role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300">
          {error}
        </div>
      )}
      {message && <ResultBanner key={messageSeq} changed={message} next={messageNext} onDismiss={() => { setMessage(''); setMessageNext(undefined); }} />}

      {attendanceErrorSummary(loadErrors) && (
        <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
          <span>{attendanceErrorSummary(loadErrors)} {t('Successful sections remain available.')}</span>
          <button type="button" onClick={load} disabled={loading} className="btn-secondary h-8 px-3 text-xs">{t('Retry data sources')}</button>
        </div>
      )}

      <RovingTabList
        items={tabs.map(({ key, label, icon }) => ({ id: key, label: t(label), icon }))}
        activeId={activeTab}
        onChange={setActiveTab}
        idPrefix="attendance"
        label={t('Attendance workspace sections')}
      />

      {/* On the other tabs the day and status controls sit on their own row; on the Live Dashboard they share the KPI strip row. */}
      {activeTab !== 'dashboard' && controls}

      <TabPanel idPrefix="attendance" tabId={activeTab}>
      {activeTab === 'dashboard' && (
        <div className="space-y-5">
          {/* One row: the day's counts as filter buttons, then the date, status and Refresh they act on. */}
          <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
            {dashboardUnavailable ? <DomainUnavailable message={dashboardUnavailable} /> : (
              <div role="group" aria-label={t('Attendance at a glance')} className="surface grid min-w-0 grow grid-cols-2 gap-1 p-1 sm:grow-0 sm:auto-cols-max sm:grid-flow-col">
                <KpiTile label={t('Present')} value={summary?.present ?? 0} icon={Users} tone="emerald" pressed={statusFilter === 'Present'} onClick={() => toggleStatusTile('Present')} />
                <KpiTile label={t('Absent')} value={summary?.absent ?? 0} icon={AlertTriangle} tone="rose" pressed={statusFilter === 'Absent'} onClick={() => toggleStatusTile('Absent')} />
                <KpiTile label={t('Late')} value={summary?.late ?? 0} icon={Clock} tone="amber" pressed={statusFilter === 'Late'} onClick={() => toggleStatusTile('Late')} />
                <KpiTile label={t('Missing Punch')} value={summary?.missingPunch ?? 0} icon={Timer} tone="blue" pressed={missingOnly} onClick={toggleMissingTile} />
              </div>
            )}
            {controls}
          </div>

          <div ref={recordRef} id="attendance-record-panel" hidden={!showRecord} className="scroll-mb-28">
            <AttendanceRecordPanel
              single={canPunch ? (
                <form onSubmit={submitPunch} className="grid gap-3 sm:grid-cols-2 lg:grid-cols-[minmax(0,1fr)_200px_200px_auto] lg:items-end">
                  {canPunchForOthers
                    ? <Field label={t('Employee')} noInfo="a plain pick from the employee list"><EmployeeSelect value={punchEmployeeId} employees={employees} onChange={setPunchEmployeeId} /></Field>
                    : <p className="text-sm text-slate-600 dark:text-slate-300">{selectedEmployee ? <bdi>{`${selectedEmployee.employeeCode} · ${selectedEmployee.fullName}`}</bdi> : t('Select an employee from live records')}</p>}
                  <Field label={t('Punch source')} info={t('Where this punch comes from: web, mobile or kiosk. It is kept with the record and shown as the Source in Raw Punch Logs.')}>
                    <select value={punchSource} onChange={(e) => setPunchSource(e.target.value as 'web' | 'mobile' | 'kiosk')} className="select w-full">
                      <option value="web">{t('Web punch')}</option>
                      <option value="mobile">{t('Mobile punch')}</option>
                      <option value="kiosk">{t('Kiosk/tablet punch')}</option>
                    </select>
                  </Field>
                  <Field label={t('Punch direction')} info={t('In, out, or the start or end of a break. Daily hours and late time are worked out from these punches when attendance is processed.')}>
                    <select value={punchDirection} onChange={(e) => setPunchDirection(e.target.value)} className="select w-full">
                      <option value="In">{directionLabel(t, 'In')}</option>
                      <option value="Out">{directionLabel(t, 'Out')}</option>
                      <option value="BreakIn">{directionLabel(t, 'BreakIn')}</option>
                      <option value="BreakOut">{directionLabel(t, 'BreakOut')}</option>
                    </select>
                  </Field>
                  <button type="submit" disabled={saving || !punchEmployeeId || !!loadErrors.employees} className="btn-primary justify-center"><Clock className="h-4 w-4" aria-hidden="true" />{t('Save Punch')}</button>
                </form>
              ) : null}
              canMass={massEntryAllowed(hasPermission)}
              canProcess={canProcess}
              onRecorded={load}
              onClose={closeRecord}
              onRunProgress={setRunProgress}
            />
          </div>

          {/* The table takes the full width. Device health and exceptions sit under it, and beside it only on a screen wide enough
              (1680) to leave the table 960 px or more. */}
          <div className="grid gap-5 min-[1680px]:grid-cols-[minmax(0,1fr)_360px] min-[1680px]:items-start">
            {unavailableMessage('daily', loadErrors) ? <DomainUnavailable message={unavailableMessage('daily', loadErrors)!} /> : <DailyTable records={shownDaily} loading={loading} empty={dailyEmpty} />}
            <div className="grid gap-5 md:grid-cols-2 min-[1680px]:grid-cols-1">
              <Panel title={t('Device Health')} action={deviceSyncUnavailable ? 'Unavailable' : `${deviceSync.length} devices`}>
                {deviceSyncUnavailable ? <DomainUnavailable message={deviceSyncUnavailable} /> : (
                  <div className="space-y-3">
                    {deviceSync.length === 0 && <DeviceEmpty />}
                    {deviceSync.slice(0, 6).map((device) => (
                    <div key={device.deviceId} className="flex items-center justify-between rounded-lg border border-slate-100 p-3 dark:border-white/10">
                      <div>
                        <p dir="auto" className="text-sm font-semibold text-slate-900 dark:text-white">{device.deviceName}</p>
                        <p className="text-xs text-slate-500 dark:text-slate-400"><bdi>{device.vendor}</bdi></p>
                        <p className="text-xs text-slate-500 dark:text-slate-400"><bdi>{device.lastSyncAtUtc ? fx.dateTime(device.lastSyncAtUtc) : '-'}</bdi></p>
                      </div>
                      <StatusChip label={enumLabel(t, 'Status', device.status || 'Never')} tone={statusTone(device.status || 'Never')} dot />
                    </div>
                    ))}
                  </div>
                )}
              </Panel>
              <Panel title={t('Exceptions')} action={insightsUnavailable ? 'Unavailable' : `${insights.length} open`}>
                {insightsUnavailable ? <DomainUnavailable message={insightsUnavailable} /> : <InsightList insights={insights.slice(0, 5)} />}
              </Panel>
            </div>
          </div>
        </div>
      )}

      {activeTab === 'raw' && (
        <div className="grid gap-5 xl:grid-cols-[420px_1fr]">
          <div className="space-y-5">
            {canProcess && (
<form onSubmit={submitRawEvent} className="surface p-4">
              <SectionTitle icon={Database} title={t('Push Raw Event')} subtitle={t('Device/API events are saved before processing.')} />
              <div className="mt-4 grid gap-3">
                <input className="input" placeholder={t('Employee code or use employee ID below')} value={rawForm.employeeCode} onChange={(e) => setRawForm({ ...rawForm, employeeCode: e.target.value })} />
                <EmployeeSelect value={rawForm.employeeId} employees={employees} onChange={(value) => setRawForm({ ...rawForm, employeeId: value })} />
                <select className="select" value={rawForm.deviceId} onChange={(e) => setRawForm({ ...rawForm, deviceId: e.target.value })} aria-label={t('Device')}>
                  <option value="">{t('No device / API source')}</option>
                  {devices.map((d) => <option key={d.id} value={d.id}>{d.deviceName}</option>)}
                </select>
                <input type="datetime-local" className="input" value={rawForm.punchAt} onChange={(e) => setRawForm({ ...rawForm, punchAt: e.target.value })} aria-label={t('Punch timestamp')} />
                <div className="grid grid-cols-2 gap-3">
                  <select aria-label={t('Punch direction')} className="select" value={rawForm.direction} onChange={(e) => setRawForm({ ...rawForm, direction: e.target.value })}>{['In', 'Out', 'BreakIn', 'BreakOut', 'Unknown'].map((d) => <option key={d} value={d}>{directionLabel(t, d)}</option>)}</select>
                  <select aria-label={t('Verification method')} className="select" value={rawForm.verificationMethod} onChange={(e) => setRawForm({ ...rawForm, verificationMethod: e.target.value })}>
                    <option value="Fingerprint">{t('Fingerprint')}</option><option value="Face">{t('Face')}</option><option value="RFID">{t('RFID')}</option><option value="PIN">{t('PIN')}</option><option value="Mobile">{t('Mobile')}</option><option value="Web">{t('Web')}</option><option value="Manual">{t('Manual')}</option>
                  </select>
                </div>
                <button type="submit" disabled={saving || (!rawForm.employeeId && !rawForm.employeeCode)} className="btn-primary justify-center">{t('Save Raw Event')}</button>
              </div>
            </form>
)}
            {canManageSources && <AttendanceCsvImportPanel onImported={load} />}
          </div>
          <Panel title={t('Raw Punch Logs')} action={rawUnavailable ? 'Unavailable' : `${rawEvents.length} latest`}>
            {rawUnavailable ? <DomainUnavailable message={rawUnavailable} /> : <RawTable rows={rawEvents} canViewSelfie={hasPermission('attendance.evidence.view')} />}
          </Panel>
        </div>
      )}

      {activeTab === 'processing' && (
        <div className="grid gap-5 lg:grid-cols-[360px_1fr]">
          {dailyUnavailable && <DomainUnavailable message={dailyUnavailable} />}
          {canProcess && <AttendanceProcessPanel blocked={!!loadErrors.daily} onProcessed={load} />}
          <div className="grid gap-5 md:grid-cols-3">
            <Kpi label={t('Worked Today')} value={dailyUnavailable ? 'Unavailable' : minutes(totalWorked)} icon={Timer} tone="blue" />
            <Kpi label={t('Overtime Cases')} value={dashboardUnavailable ? 'Unavailable' : (summary?.overtimeEmployees ?? 0)} icon={Clock} tone="amber" />
            <Kpi label={t('Payroll Locked')} value={dailyUnavailable ? 'Unavailable' : daily.filter((x) => x.isPayrollLocked).length} icon={ShieldCheck} tone="slate" />
          </div>
        </div>
      )}

      {activeTab === 'regularization' && (
        <div className={canProcess ? "grid gap-5 xl:grid-cols-[420px_1fr]" : "grid gap-5"}>
          {canProcess && (
<form onSubmit={submitRegularization} className="surface p-4">
            <SectionTitle icon={ShieldCheck} title={t('Correction Request')} subtitle={t('Missed punch, wrong punch, WFH, site visit, or manual correction.')} />
            {regularizationsUnavailable && <div className="mt-4"><DomainUnavailable message={regularizationsUnavailable} /></div>}
            <div className="mt-4 space-y-3">
              <EmployeeSelect value={regularizationForm.employeeId} employees={employees} onChange={(value) => setRegularizationForm({ ...regularizationForm, employeeId: value })} />
              <input type="date" className="input w-full" value={regularizationForm.workDate} onChange={(e) => setRegularizationForm({ ...regularizationForm, workDate: e.target.value })} aria-label={t('Work date')} />
              <select aria-label={t('Request type')} className="select w-full" value={regularizationForm.requestType} onChange={(e) => setRegularizationForm({ ...regularizationForm, requestType: e.target.value })}>
                <option value="Missed punch">{t('Missed punch')}</option><option value="Wrong punch">{t('Wrong punch')}</option><option value="Work from home">{t('Work from home')}</option><option value="Site visit">{t('Site visit')}</option><option value="Manual attendance correction">{t('Manual attendance correction')}</option>
              </select>
              <div className="grid grid-cols-2 gap-3">
                <input type="datetime-local" className="input w-full" value={regularizationForm.requestedIn} onChange={(e) => setRegularizationForm({ ...regularizationForm, requestedIn: e.target.value })} aria-label={t('Requested in')} />
                <input type="datetime-local" className="input w-full" value={regularizationForm.requestedOut} onChange={(e) => setRegularizationForm({ ...regularizationForm, requestedOut: e.target.value })} aria-label={t('Requested out')} />
              </div>
              <textarea className="input min-h-24 w-full" value={regularizationForm.reason} onChange={(e) => setRegularizationForm({ ...regularizationForm, reason: e.target.value })} placeholder={t('Reason required')} />
              <button type="submit" disabled={saving || !regularizationForm.employeeId || !regularizationForm.reason || !!loadErrors.employees || !!loadErrors.regularizations} className="btn-primary w-full justify-center">{t('Submit Request')}</button>
            </div>
          </form>
)}
          <Panel title={t('Pending Approval Queue')} action={correctionQueueUnavailable ? 'Unavailable' : `${pendingRegularizations.length} pending`}>
            <input className="input mb-3 w-full" value={decisionComment} onChange={(e) => setDecisionComment(e.target.value)} aria-label={t('Decision comment')} />
            {correctionQueueUnavailable
              ? <DomainUnavailable message={correctionQueueUnavailable} />
              : <RegularizationTable rows={pendingRegularizations} onApprove={(id) => runAction(() => attendanceApi.regularization.approve(id, decisionComment), t('Regularization approved and attendance reprocessed.'))} onReject={(id) => runAction(() => attendanceApi.regularization.reject(id, decisionComment), t('Regularization rejected.'))} />}
          </Panel>
        </div>
      )}

      {activeTab === 'exceptions' && <AttendanceExceptionsPanel />}

      {activeTab === 'reports' && (
        <div className="grid gap-5 xl:grid-cols-2">
          <Panel title={t('Payroll Attendance Summary')} action={payrollSummaryUnavailable ? 'Unavailable' : `${payrollSummary.length} employees`}>
            {payrollSummaryUnavailable ? <DomainUnavailable message={payrollSummaryUnavailable} /> : <PayrollTable rows={payrollSummary} />}
          </Panel>
          <Panel title={t('Daily Exception Reports')} action={filterDate}>
            {unavailableMessage('daily', loadErrors) ? <DomainUnavailable message={unavailableMessage('daily', loadErrors)!} /> : <DailyTable records={daily.filter((x) => x.lateMinutes > 0 || x.missingPunch || x.status === 'Absent')} compact empty={<p className="px-4 py-8 text-center text-body-sm text-fg-muted">{t('No exceptions on this day.')}</p>} />}
          </Panel>
        </div>
      )}

      {activeTab === 'ai' && (
        <div className="grid gap-5 lg:grid-cols-[1fr_360px]">
          <Panel title={t('Attendance Insights')} action={insightsUnavailable ? 'Unavailable' : `${insights.length} signals`}>
            {insightsUnavailable ? <DomainUnavailable message={insightsUnavailable} /> : <InsightList insights={insights} />}
          </Panel>
          <Panel title={t('Human Review Guardrails')}>
            <div className="space-y-3 text-sm text-slate-600 dark:text-slate-300">
              <Guardrail text={t('Insights surface anomalies, buddy-punching risk, late trends, and sync failures.')} />
              <Guardrail text={t('Insights never reject corrections, penalize employees, or finalize payroll decisions.')} />
              <Guardrail text={t('Payroll impacts remain pending until approved records are reviewed by authorized users.')} />
            </div>
          </Panel>
        </div>
      )}
      </TabPanel>
    </div>
  );
}

function Kpi({ label, value, icon: Icon, tone }: { label: string; value: string | number; icon: typeof Activity; tone: 'emerald' | 'rose' | 'amber' | 'blue' | 'slate' }) {
  const t = useT();
  const color = {
    emerald: 'text-emeraldZ bg-emeraldZ/10',
    rose: 'text-rose-500 bg-rose-500/10',
    amber: 'text-amber-500 bg-amber-500/10',
    blue: 'text-sapphire bg-sapphire/10',
    slate: 'text-slate-500 bg-slate-500/10',
  }[tone];
  return (
    <div className="surface p-4">
      <div className="flex items-center justify-between gap-3">
        <p className="text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">{label}</p>
        <span className={`grid h-9 w-9 place-items-center rounded-lg ${color}`}><Icon className="h-4 w-4" /></span>
      </div>
      <p className="mt-3 text-2xl font-semibold text-slate-950 dark:text-white">{value === 'Unavailable' ? t('Unavailable') : value}</p>
    </div>
  );
}

/** A count in the slim strip: icon, label and number on one line. */
/** A count that is also a filter: pressed means the table below is narrowed to it. */
function KpiTile({ label, value, icon: Icon, tone, pressed, onClick }: { label: string; value: number; icon: typeof Activity; tone: 'emerald' | 'rose' | 'amber' | 'blue'; pressed: boolean; onClick: () => void }) {
  const color = { emerald: 'text-emeraldZ', rose: 'text-rose-500', amber: 'text-amber-600', blue: 'text-sapphire' }[tone];
  return (
    <button type="button" aria-pressed={pressed} onClick={onClick}
      className={`flex min-h-control-md items-center gap-2 rounded-lg px-3 py-1 text-start transition-colors duration-fast hover:bg-surface-sunken ${pressed ? 'bg-primary-soft ring-1 ring-primary' : ''}`}>
      <Icon className={`h-4 w-4 shrink-0 ${color}`} aria-hidden="true" />
      <span className="text-meta font-semibold text-fg-muted">{label}</span>
      <span className="ms-auto text-h4 sm:ms-1 font-semibold tabular-nums text-fg">{value}</span>
    </button>
  );
}

function EmployeeSelect({ value, employees, onChange, includeAll, id }: { value: string; employees: EmployeeListItem[]; onChange: (value: string) => void; includeAll?: boolean; id?: string }) {
  const t = useT();
  return (
    <select id={id} className="select w-full" value={value} onChange={(e) => onChange(e.target.value)} aria-label={t('Employee')}>
      <option value="">{includeAll ? t('All employees') : t('Select employee')}</option>
      {employees.map((employee) => <option key={employee.id} value={employee.id}>{employee.employeeCode} · {employee.fullName}</option>)}
    </select>
  );
}


/** True once `on` has held for `delayMs`: a wait shorter than that shows no skeleton at all (Screen Standard 4). */
function useAfterDelay(on: boolean, delayMs: number): boolean {
  const [late, setLate] = useState(false);
  useEffect(() => {
    if (!on) { setLate(false); return; }
    const timer = setTimeout(() => setLate(true), delayMs);
    return () => clearTimeout(timer);
  }, [on, delayMs]);
  return on && late;
}

/**
 * The day's records. From 640 px a table (no Date column: one date is filtered; the department sits inline with the name so
 * a row is about 40 px). Under 640 px a card list: nothing that signals risk (a missing punch, late minutes) is dropped to save width.
 */
function DailyTable({ records, loading, compact, empty }: { records: AttendanceDailyRecord[]; loading?: boolean; compact?: boolean; empty?: ReactNode }) {
  const t = useT();
  const fx = useFormat();
  const minutes = useMinutes();
  const showSkeleton = useAfterDelay(!!loading, 150);
  const heads = [msg('Employee'), msg('Check-in'), msg('Check-out'), msg('Worked'), msg('Late'), msg('Punch check'), msg('Status')];
  const time = (value?: string) => (value ? fx.time(value) : '-');
  const missingChip = (r: AttendanceDailyRecord) => (r.missingPunch ? <StatusChip label={t('Missing punch')} tone="rose" dot /> : <StatusChip label={t('Complete')} tone="emerald" />);
  return (
    <div data-daily-table className="surface overflow-hidden">
      {loading && showSkeleton && <div className="p-3"><SkeletonRows rows={8} /><span className="sr-only">{t('Loading live attendance...')}</span></div>}
      {!loading && records.length === 0 && (empty ?? <Empty text={t('No processed attendance records for this selection.')} />)}
      {records.length > 0 && (
        <>
          <ul data-daily-cards aria-label={t('Attendance records')} className="divide-y divide-line sm:hidden">
            {records.map((r) => (
              <li key={r.id} className="space-y-1 px-3 py-2.5">
                <div className="flex items-start justify-between gap-2">
                  <p className="min-w-0 break-words text-body font-medium text-fg"><bdi>{r.employeeName}</bdi></p>
                  <StatusChip label={enumLabel(t, 'AttendanceStatus', r.status)} tone={statusTone(r.status)} dot={!compact} />
                </div>
                <p className="text-meta text-fg-muted"><bdi>{r.department || r.branch}</bdi></p>
                <p className="text-body text-fg"><bdi className="font-mono">{time(r.firstInUtc)}</bdi> – <bdi className="font-mono">{time(r.lastOutUtc)}</bdi></p>
                {(r.missingPunch || r.lateMinutes > 0) && (
                  <div className="flex flex-wrap items-center gap-1.5">
                    {r.missingPunch && <StatusChip label={t('Missing punch')} tone="rose" dot />}
                    {r.lateMinutes > 0 && <StatusChip label={t('{minutes}m late', { minutes: r.lateMinutes })} tone="amber" />}
                  </div>
                )}
              </li>
            ))}
          </ul>
          <div className="hidden overflow-x-auto sm:block">
            <table className="w-full text-body">
              <thead><tr className="border-b border-line">{heads.map((h) => <th key={h} scope="col" className="px-4 py-2 text-start text-meta font-semibold uppercase tracking-wide text-fg-muted">{t(h)}</th>)}</tr></thead>
              <tbody className="divide-y divide-line">
                {records.map((r) => (
                  <tr key={r.id} className="hover:bg-surface-sunken">
                    <td className="px-4 py-2 text-fg"><span className="font-medium"><bdi>{r.employeeName}</bdi></span>{(r.department || r.branch) && <span className="ms-2 text-meta text-fg-muted"><bdi>{r.department || r.branch}</bdi></span>}</td>
                    <td className="whitespace-nowrap px-4 py-2 font-mono text-fg"><bdi>{time(r.firstInUtc)}</bdi></td>
                    <td className="whitespace-nowrap px-4 py-2 font-mono text-fg"><bdi>{time(r.lastOutUtc)}</bdi></td>
                    <td className="whitespace-nowrap px-4 py-2 text-fg">{minutes(r.totalWorkedMinutes)}</td>
                    <td className="whitespace-nowrap px-4 py-2 text-fg">{r.lateMinutes ? t('{minutes}m', { minutes: r.lateMinutes }) : '-'}</td>
                    <td className="px-4 py-2">{missingChip(r)}</td>
                    <td className="px-4 py-2"><StatusChip label={enumLabel(t, 'AttendanceStatus', r.status)} tone={statusTone(r.status)} dot={!compact} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}

/** No attendance devices yet: a link to add one where the viewer may, otherwise who to ask. */
function DeviceEmpty() {
  const t = useT();
  const href = useModuleSectionHref(['attendanceDevices']);
  return href
    ? <EmptyState kind="first-use" title={t('No devices connected')} description={t('Devices send punches automatically. Without one, punches are entered by hand.')} action={{ label: t('Add a device'), href }} className="px-2 py-4" />
    : <EmptyState kind="restricted" title={t('No devices connected')} description={t('Devices are added by your HR administrator.')} className="px-2 py-4" />;
}

function RawTable({ rows, canViewSelfie }: { rows: AttendanceRawEvent[]; canViewSelfie: boolean }) {
  const { t } = useLocale();
  const fx = useFormat();
  // The punch whose selfie HR is reviewing. The server says which punches have one (hasSelfie), never where it is stored.
  const [viewing, setViewing] = useState<{ id: string; employee: string; time: string } | null>(null);
  const closeViewer = useCallback(() => setViewing(null), []);
  if (rows.length === 0) return <Empty text={t('No raw attendance events found for this date.')} />;
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[760px] text-sm">
        <thead><tr className="border-b border-slate-100 dark:border-white/[0.07]">{[msg('Timestamp'), msg('Employee'), msg('Source'), msg('Direction'), msg('Method'), msg('Processed')].map((h) => <th key={h} className="px-4 py-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-400">{t(h)}</th>)}{canViewSelfie && <th className="px-4 py-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-400">{t('Selfie')}</th>}</tr></thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
          {rows.map((r) => {
            const employee = r.employeeCode || String(r.employeeId ?? '');
            const time = r.punchTimestampUtc ? fx.dateTime(r.punchTimestampUtc) : '-';
            return <tr key={r.id}><td className="px-4 py-3 font-mono text-slate-700 dark:text-slate-300">{time}</td><td className="px-4 py-3 text-slate-600 dark:text-slate-300">{employee}</td><td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.source}</td><td className="px-4 py-3"><StatusChip label={directionLabel(t, r.punchDirection)} tone="blue" /></td><td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.verificationMethod}</td><td className="px-4 py-3"><StatusChip label={r.isProcessed ? t('Processed') : t('Raw')} tone={r.isProcessed ? 'emerald' : 'amber'} dot /></td>{canViewSelfie && <td className="px-4 py-3">{r.hasSelfie && (
              <button type="button" className="btn-secondary px-3 py-1 text-xs" aria-label={t('View selfie for the punch by {employee} at {time}', { employee, time })}
                onClick={() => setViewing({ id: r.id, employee, time })}>{t('View selfie')}</button>
            )}</td>}</tr>;
          })}
        </tbody>
      </table>
      {viewing && <SelfieViewerModal rawEventId={viewing.id} employee={viewing.employee} time={viewing.time} onClose={closeViewer} />}
    </div>
  );
}

function RegularizationTable({ rows, onApprove, onReject }: { rows: AttendanceRegularizationRequest[]; onApprove: (id: string) => void; onReject: (id: string) => void }) {
  const t = useT();
  const fx = useFormat();
  if (rows.length === 0) return <Empty text={t('No regularization requests in the live queue.')} />;
  return (
    <div className="space-y-3">
      {rows.map((r) => (
        <div key={r.id} className="rounded-lg border border-slate-100 p-3 dark:border-white/10">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div><p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Employee #{id} · {type}', { id: r.employeeId, type: enumLabel(t, 'Status', r.requestType) })}</p><p className="text-xs text-slate-500">{fx.date(r.workDate)} · {r.reason}</p></div>
            <StatusChip label={enumLabel(t, 'Status', r.status)} tone={statusTone(r.status)} dot />
          </div>
          <div className="mt-3 flex gap-2"><button type="button" onClick={() => onApprove(r.id)} className="btn-secondary h-8 px-2 text-xs"><CheckCircle2 className="h-3.5 w-3.5" />{t('Approve')}</button><button type="button" onClick={() => onReject(r.id)} className="btn-secondary h-8 px-2 text-xs">{t('Reject')}</button></div>
        </div>
      ))}
    </div>
  );
}

function PayrollTable({ rows }: { rows: AttendancePayrollSummary[] }) {
  const t = useT();
  const minutes = useMinutes();
  if (rows.length === 0) return <Empty text={t('No payroll attendance impacts for this date range.')} />;
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[640px] text-sm">
        <thead><tr className="border-b border-slate-100 dark:border-white/[0.07]">{[msg('Employee'), msg('Late'), msg('Early'), msg('Absences'), msg('OT'), msg('Lock')].map((h) => <th key={h} className="px-4 py-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-400">{t(h)}</th>)}</tr></thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">{rows.map((r) => <tr key={r.employeeId}><td className="px-4 py-3 font-semibold text-slate-900 dark:text-white">{r.employeeName}</td><td className="px-4 py-3">{t('{minutes}m', { minutes: r.lateMinutes })}</td><td className="px-4 py-3">{t('{minutes}m', { minutes: r.earlyExitMinutes })}</td><td className="px-4 py-3">{r.absenceDays}</td><td className="px-4 py-3">{minutes(r.overtimeMinutes)}</td><td className="px-4 py-3"><StatusChip label={r.hasLockedRecords ? enumLabel(t, 'Status', 'Locked') : enumLabel(t, 'Status', 'Open')} tone={r.hasLockedRecords ? 'rose' : 'emerald'} /></td></tr>)}</tbody>
      </table>
    </div>
  );
}

function InsightList({ insights }: { insights: AttendanceAIInsight[] }) {
  const t = useT();
  if (insights.length === 0) return <Empty text={t('No attendance insights are open.')} />;
  return (
    <div className="space-y-3">
      {insights.map((item) => <div key={item.id} className="rounded-lg border border-slate-100 p-3 dark:border-white/10"><div className="flex items-start justify-between gap-3"><div><p dir="auto" className="text-sm font-semibold text-slate-900 dark:text-white">{item.title}</p><p dir="auto" className="mt-1 text-sm text-slate-500 dark:text-slate-400">{item.summary}</p></div><StatusChip label={enumLabel(t, 'Status', item.severity)} tone={item.severity === 'High' ? 'rose' : item.severity === 'Medium' ? 'amber' : 'blue'} /></div></div>)}
    </div>
  );
}

function Guardrail({ text }: { text: string }) {
  return <div className="flex gap-2"><MapPin className="mt-0.5 h-4 w-4 shrink-0 text-sapphire" /><p>{text}</p></div>;
}

