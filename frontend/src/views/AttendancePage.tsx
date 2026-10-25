'use client';

import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react';
import {
  Activity,
  AlertTriangle,
  Lightbulb,
  CalendarClock,
  CheckCircle2,
  Clock,
  Database,
  FileSpreadsheet,
  MapPin,
  RefreshCw,
  ShieldCheck,
  Timer,
  Upload,
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
import { useT } from '../hooks/useT';
import { msg } from '../i18n/translations';
import { useLocale } from '../contexts/LocaleContext';
import type { EmployeeListItem } from '../api/employees';
import { StatusChip } from '../components/StatusChip';
import { useAuth } from '../contexts/AuthContext';
import { ModuleSettingsLink } from '../components/setup/ModuleSettingsLink';
import { DomainUnavailable, Empty, Panel, dateTime, statusTone } from '../components/attendance/attendanceUi';
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
const minutes = (value: number) => value > 0 ? `${Math.floor(value / 60)}h ${value % 60}m` : '0m';
const time = (value?: string) => value ? new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '-';

const tabs: Array<{ key: TabKey; label: string; icon: typeof Activity }> = [
  { key: 'dashboard', label: 'Live Dashboard', icon: Activity },
  { key: 'raw', label: 'Raw Punch Logs', icon: Database },
  { key: 'processing', label: 'Processing', icon: RefreshCw },
  { key: 'regularization', label: 'Regularization', icon: ShieldCheck },
  { key: 'exceptions', label: msg('Exceptions'), icon: AlertTriangle },
  { key: 'reports', label: 'Reports', icon: FileSpreadsheet },
  { key: 'ai', label: 'Insights', icon: Lightbulb },
];


export function AttendancePage() {
  // Devices and CSV import are `attendance.bulk_import`; processing is `attendance.write`
  // (AttendanceController). Read-only attendance roles (HR Assistant, payroll, managers, auditors)
  // used to be offered these and only ever got a 403.
  const { user, hasPermission } = useAuth();
  const t = useT();
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
  const [csvContent, setCsvContent] = useState('');
  const [processForm, setProcessForm] = useState({ fromDate: today(), toDate: today(), employeeId: '' });
  const [regularizationForm, setRegularizationForm] = useState({ employeeId: '', workDate: today(), requestType: 'Missed punch', requestedIn: '', requestedOut: '', reason: '' });
  const [decisionComment, setDecisionComment] = useState('Reviewed by HR.');

  const selectedEmployee = useMemo(
    () => employees.find((e) => e.id === Number(punchEmployeeId || regularizationForm.employeeId || processForm.employeeId)),
    [employees, punchEmployeeId, regularizationForm.employeeId, processForm.employeeId],
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
    setLoading(false);
  }, [filterDate, statusFilter]);

  useEffect(() => { load(); }, [load]);

  const runAction = async (action: () => Promise<unknown>, success: string) => {
    setSaving(true);
    setError('');
    setMessage('');
    try {
      await action();
      setMessage(success);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? err.message ?? 'Attendance action failed.');
    } finally {
      setSaving(false);
    }
  };

  const submitPunch = (event: FormEvent) => {
    event.preventDefault();
    const employeeId = Number(punchEmployeeId);
    runAction(() => attendanceApi.punch[punchSource]({ employeeId, punchDirection, locationName: 'Web console' }), 'Punch saved to live attendance events.');
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
    }), 'Raw attendance event saved.');
  };

  const [csvFileName, setCsvFileName] = useState('');

  const handleCsvFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    setCsvFileName(file.name);
    const reader = new FileReader();
    reader.onload = (ev) => setCsvContent((ev.target?.result as string) ?? '');
    reader.readAsText(file);
    e.target.value = '';
  };

  const submitImport = async (event: FormEvent) => {
    event.preventDefault();
    setSaving(true);
    setError('');
    setMessage('');
    try {
      const batch = await attendanceApi.events.importCsv({ fileName: csvFileName || `attendance-${Date.now()}.csv`, csvContent });
      if (batch.failedRows > 0) {
        setMessage(`Imported ${batch.importedRows} of ${batch.totalRows} rows. ${batch.failedRows} rows failed — check that employee codes exist in the system.`);
      } else {
        setMessage(`✓ Imported all ${batch.importedRows} rows successfully.`);
      }
      setCsvContent('');
      setCsvFileName('');
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? err.message ?? 'CSV import failed.');
    } finally {
      setSaving(false);
    }
  };

  const submitProcessing = (event: FormEvent) => {
    event.preventDefault();
    runAction(() => attendanceApi.process({
      fromDate: processForm.fromDate,
      toDate: processForm.toDate,
      employeeId: processForm.employeeId ? Number(processForm.employeeId) : undefined,
    }), 'Attendance processed into daily records and payroll impacts.');
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
    }), 'Regularization request saved for approval.');
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

  return (
    <div className="space-y-5">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <p className="text-xs font-bold uppercase tracking-wide text-sapphire">Attendance & Time Tracking</p>
          <h1 className="mt-1 text-2xl font-bold text-slate-950 dark:text-white">Attendance workspace</h1>
          <p className="mt-1 max-w-3xl text-sm text-slate-500 dark:text-slate-400">
            Review punches, device health, corrections, processing, payroll impacts, and exceptions from available data sources.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <ModuleSettingsLink sections={['attendanceDevices']} label={msg('Attendance settings')} />
          <input type="date" value={filterDate} onChange={(e) => setFilterDate(e.target.value)} className="input" aria-label="Attendance date" />
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)} className="select" aria-label="Status filter">
            <option value="">All statuses</option>
            <option value="Present">Present</option>
            <option value="Late">Late</option>
            <option value="Half day">Half day</option>
            <option value="Absent">Absent</option>
          </select>
          <button type="button" onClick={load} className="btn-secondary" disabled={loading}><RefreshCw className="h-4 w-4" />Refresh</button>
        </div>
      </div>

      {(error || message) && (
        <div role={error ? 'alert' : 'status'} aria-live={error ? 'assertive' : 'polite'} className={`rounded-lg border px-4 py-3 text-sm ${error ? 'border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300' : 'border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-500/20 dark:bg-emerald-500/10 dark:text-emerald-300'}`}>
          {error || message}
        </div>
      )}

      {attendanceErrorSummary(loadErrors) && (
        <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
          <span>{attendanceErrorSummary(loadErrors)} Successful sections remain available.</span>
          <button type="button" onClick={load} disabled={loading} className="btn-secondary h-8 px-3 text-xs">Retry data sources</button>
        </div>
      )}

      <RovingTabList
        items={tabs.map(({ key, label, icon }) => ({ id: key, label: key === 'exceptions' ? t(label) : label, icon }))}
        activeId={activeTab}
        onChange={setActiveTab}
        idPrefix="attendance"
        label="Attendance workspace sections"
      />

      <TabPanel idPrefix="attendance" tabId={activeTab}>
      {activeTab === 'dashboard' && (
        <div className="grid gap-5 xl:grid-cols-[1fr_360px]">
          <div className="space-y-5">
            {dashboardUnavailable ? <DomainUnavailable message={dashboardUnavailable} /> : (
              <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
                <Kpi label="Present" value={summary?.present ?? 0} icon={Users} tone="emerald" />
                <Kpi label="Absent" value={summary?.absent ?? 0} icon={AlertTriangle} tone="rose" />
                <Kpi label="Late" value={summary?.late ?? 0} icon={Clock} tone="amber" />
                <Kpi label="Missing Punch" value={summary?.missingPunch ?? 0} icon={Timer} tone="blue" />
              </div>
            )}

            <div className={canPunch ? "grid gap-5 lg:grid-cols-[360px_1fr]" : "grid gap-5"}>
              {canPunch && (
<form onSubmit={submitPunch} className="surface p-4">
                <SectionTitle icon={CalendarClock} title="Web / Mobile / Kiosk Punch" subtitle={selectedEmployee ? `${selectedEmployee.employeeCode} · ${selectedEmployee.fullName}` : 'Select an employee from live records'} />
                <div className="mt-4 space-y-3">
                  {canPunchForOthers && <EmployeeSelect value={punchEmployeeId} employees={employees} onChange={setPunchEmployeeId} />}
                  <div className="grid grid-cols-2 gap-3">
                    <select value={punchSource} onChange={(e) => setPunchSource(e.target.value as 'web' | 'mobile' | 'kiosk')} className="select w-full" aria-label="Punch source">
                      <option value="web">Web punch</option>
                      <option value="mobile">Mobile punch</option>
                      <option value="kiosk">Kiosk/tablet punch</option>
                    </select>
                    <select value={punchDirection} onChange={(e) => setPunchDirection(e.target.value)} className="select w-full" aria-label="Punch direction">
                      <option>In</option>
                      <option>Out</option>
                      <option>BreakIn</option>
                      <option>BreakOut</option>
                    </select>
                  </div>
                  <button type="submit" disabled={saving || !punchEmployeeId || !!loadErrors.employees} className="btn-primary w-full justify-center"><Clock className="h-4 w-4" />Save Punch</button>
                </div>
              </form>
)}

              {unavailableMessage('daily', loadErrors) ? <DomainUnavailable message={unavailableMessage('daily', loadErrors)!} /> : <DailyTable records={daily} loading={loading} />}
            </div>
          </div>

          <div className="space-y-5">
            <Panel title="Device Health" action={deviceSyncUnavailable ? 'Unavailable' : `${deviceSync.length} devices`}>
              {deviceSyncUnavailable ? <DomainUnavailable message={deviceSyncUnavailable} /> : (
                <div className="space-y-3">
                  {deviceSync.length === 0 && <Empty text="No attendance devices configured yet." />}
                  {deviceSync.slice(0, 6).map((device) => (
                  <div key={device.deviceId} className="flex items-center justify-between rounded-lg border border-slate-100 p-3 dark:border-white/10">
                    <div>
                      <p className="text-sm font-semibold text-slate-900 dark:text-white">{device.deviceName}</p>
                      <p className="text-xs text-slate-500 dark:text-slate-400">{device.vendor} · {dateTime(device.lastSyncAtUtc)}</p>
                    </div>
                    <StatusChip label={device.status || 'Never'} tone={statusTone(device.status || 'Never')} dot />
                  </div>
                  ))}
                </div>
              )}
            </Panel>
            <Panel title="Exceptions" action={insightsUnavailable ? 'Unavailable' : `${insights.length} open`}>
              {insightsUnavailable ? <DomainUnavailable message={insightsUnavailable} /> : <InsightList insights={insights.slice(0, 5)} />}
            </Panel>
          </div>
        </div>
      )}

      {activeTab === 'raw' && (
        <div className="grid gap-5 xl:grid-cols-[420px_1fr]">
          <div className="space-y-5">
            {canProcess && (
<form onSubmit={submitRawEvent} className="surface p-4">
              <SectionTitle icon={Database} title="Push Raw Event" subtitle="Device/API events are saved before processing." />
              <div className="mt-4 grid gap-3">
                <input className="input" placeholder="Employee code or use employee ID below" value={rawForm.employeeCode} onChange={(e) => setRawForm({ ...rawForm, employeeCode: e.target.value })} />
                <EmployeeSelect value={rawForm.employeeId} employees={employees} onChange={(value) => setRawForm({ ...rawForm, employeeId: value })} />
                <select className="select" value={rawForm.deviceId} onChange={(e) => setRawForm({ ...rawForm, deviceId: e.target.value })} aria-label="Device">
                  <option value="">No device / API source</option>
                  {devices.map((d) => <option key={d.id} value={d.id}>{d.deviceName}</option>)}
                </select>
                <input type="datetime-local" className="input" value={rawForm.punchAt} onChange={(e) => setRawForm({ ...rawForm, punchAt: e.target.value })} aria-label="Punch timestamp" />
                <div className="grid grid-cols-2 gap-3">
                  <select aria-label="Punch direction" className="select" value={rawForm.direction} onChange={(e) => setRawForm({ ...rawForm, direction: e.target.value })}><option>In</option><option>Out</option><option>BreakIn</option><option>BreakOut</option><option>Unknown</option></select>
                  <select aria-label="Verification method" className="select" value={rawForm.verificationMethod} onChange={(e) => setRawForm({ ...rawForm, verificationMethod: e.target.value })}><option>Fingerprint</option><option>Face</option><option>RFID</option><option>PIN</option><option>Mobile</option><option>Web</option><option>Manual</option></select>
                </div>
                <button type="submit" disabled={saving || (!rawForm.employeeId && !rawForm.employeeCode)} className="btn-primary justify-center">Save Raw Event</button>
              </div>
            </form>
)}
            {canManageSources && <form onSubmit={submitImport} className="surface p-4">
              <SectionTitle icon={Upload} title="CSV Attendance Import" subtitle="Columns: employeeCode, punchTimestamp (ISO 8601), punchDirection, location (opt), method (opt)" />
              <div className="mt-4 flex items-center gap-2">
                <label className="btn-secondary flex cursor-pointer items-center gap-1.5 text-sm">
                  <Upload className="h-3.5 w-3.5" />
                  {csvFileName ? csvFileName : 'Choose CSV file'}
                  <input type="file" accept=".csv,text/csv" className="hidden" onChange={handleCsvFile} />
                </label>
                {csvFileName && <button type="button" onClick={() => { setCsvContent(''); setCsvFileName(''); }} className="text-xs text-slate-400 hover:text-rose-500">clear</button>}
              </div>
              <textarea className="input mt-3 min-h-28 w-full font-mono text-xs" value={csvContent} onChange={(e) => setCsvContent(e.target.value)} placeholder={`employeeCode,punchTimestamp,punchDirection\nEMP-001,2026-06-16T09:00:00Z,In\nEMP-001,2026-06-16T18:00:00Z,Out`} />
              <p className="mt-1 text-xs text-slate-400">Employee codes must match exactly what is registered in the system.</p>
              <button type="submit" disabled={saving || !csvContent.trim()} className="btn-primary mt-3 w-full justify-center">
                {saving ? 'Importing…' : 'Import CSV'}
              </button>
            </form>}
          </div>
          <Panel title="Raw Punch Logs" action={rawUnavailable ? 'Unavailable' : `${rawEvents.length} latest`}>
            {rawUnavailable ? <DomainUnavailable message={rawUnavailable} /> : <RawTable rows={rawEvents} canViewSelfie={hasPermission('attendance.evidence.view')} />}
          </Panel>
        </div>
      )}

      {activeTab === 'processing' && (
        <div className="grid gap-5 lg:grid-cols-[360px_1fr]">
          {dailyUnavailable && <DomainUnavailable message={dailyUnavailable} />}
          {canProcess && <form onSubmit={submitProcessing} className="surface p-4">
            <SectionTitle icon={RefreshCw} title="Process Attendance" subtitle="Transforms raw punches into daily attendance, exceptions, and payroll impacts." />
            <div className="mt-4 space-y-3">
              <input type="date" className="input w-full" value={processForm.fromDate} onChange={(e) => setProcessForm({ ...processForm, fromDate: e.target.value })} aria-label="From date" />
              <input type="date" className="input w-full" value={processForm.toDate} onChange={(e) => setProcessForm({ ...processForm, toDate: e.target.value })} aria-label="To date" />
              <EmployeeSelect value={processForm.employeeId} employees={employees} onChange={(value) => setProcessForm({ ...processForm, employeeId: value })} includeAll />
              <button type="submit" disabled={saving || !!loadErrors.daily || !!loadErrors.employees} className="btn-primary w-full justify-center">Process Records</button>
            </div>
          </form>}
          <div className="grid gap-5 md:grid-cols-3">
            <Kpi label="Worked Today" value={dailyUnavailable ? 'Unavailable' : minutes(totalWorked)} icon={Timer} tone="blue" />
            <Kpi label="Overtime Cases" value={dashboardUnavailable ? 'Unavailable' : (summary?.overtimeEmployees ?? 0)} icon={Clock} tone="amber" />
            <Kpi label="Payroll Locked" value={dailyUnavailable ? 'Unavailable' : daily.filter((x) => x.isPayrollLocked).length} icon={ShieldCheck} tone="slate" />
          </div>
        </div>
      )}

      {activeTab === 'regularization' && (
        <div className={canProcess ? "grid gap-5 xl:grid-cols-[420px_1fr]" : "grid gap-5"}>
          {canProcess && (
<form onSubmit={submitRegularization} className="surface p-4">
            <SectionTitle icon={ShieldCheck} title="Correction Request" subtitle="Missed punch, wrong punch, WFH, site visit, or manual correction." />
            {regularizationsUnavailable && <div className="mt-4"><DomainUnavailable message={regularizationsUnavailable} /></div>}
            <div className="mt-4 space-y-3">
              <EmployeeSelect value={regularizationForm.employeeId} employees={employees} onChange={(value) => setRegularizationForm({ ...regularizationForm, employeeId: value })} />
              <input type="date" className="input w-full" value={regularizationForm.workDate} onChange={(e) => setRegularizationForm({ ...regularizationForm, workDate: e.target.value })} aria-label="Work date" />
              <select aria-label="Request type" className="select w-full" value={regularizationForm.requestType} onChange={(e) => setRegularizationForm({ ...regularizationForm, requestType: e.target.value })}>
                <option>Missed punch</option><option>Wrong punch</option><option>Work from home</option><option>Site visit</option><option>Manual attendance correction</option>
              </select>
              <div className="grid grid-cols-2 gap-3">
                <input type="datetime-local" className="input w-full" value={regularizationForm.requestedIn} onChange={(e) => setRegularizationForm({ ...regularizationForm, requestedIn: e.target.value })} aria-label="Requested in" />
                <input type="datetime-local" className="input w-full" value={regularizationForm.requestedOut} onChange={(e) => setRegularizationForm({ ...regularizationForm, requestedOut: e.target.value })} aria-label="Requested out" />
              </div>
              <textarea className="input min-h-24 w-full" value={regularizationForm.reason} onChange={(e) => setRegularizationForm({ ...regularizationForm, reason: e.target.value })} placeholder="Reason required" />
              <button type="submit" disabled={saving || !regularizationForm.employeeId || !regularizationForm.reason || !!loadErrors.employees || !!loadErrors.regularizations} className="btn-primary w-full justify-center">Submit Request</button>
            </div>
          </form>
)}
          <Panel title="Pending Approval Queue" action={correctionQueueUnavailable ? 'Unavailable' : `${pendingRegularizations.length} pending`}>
            <input className="input mb-3 w-full" value={decisionComment} onChange={(e) => setDecisionComment(e.target.value)} aria-label="Decision comment" />
            {correctionQueueUnavailable
              ? <DomainUnavailable message={correctionQueueUnavailable} />
              : <RegularizationTable rows={pendingRegularizations} onApprove={(id) => runAction(() => attendanceApi.regularization.approve(id, decisionComment), 'Regularization approved and attendance reprocessed.')} onReject={(id) => runAction(() => attendanceApi.regularization.reject(id, decisionComment), 'Regularization rejected.')} />}
          </Panel>
        </div>
      )}

      {activeTab === 'exceptions' && <AttendanceExceptionsPanel />}

      {activeTab === 'reports' && (
        <div className="grid gap-5 xl:grid-cols-2">
          <Panel title="Payroll Attendance Summary" action={payrollSummaryUnavailable ? 'Unavailable' : `${payrollSummary.length} employees`}>
            {payrollSummaryUnavailable ? <DomainUnavailable message={payrollSummaryUnavailable} /> : <PayrollTable rows={payrollSummary} />}
          </Panel>
          <Panel title="Daily Exception Reports" action={filterDate}>
            {unavailableMessage('daily', loadErrors) ? <DomainUnavailable message={unavailableMessage('daily', loadErrors)!} /> : <DailyTable records={daily.filter((x) => x.lateMinutes > 0 || x.missingPunch || x.status === 'Absent')} compact />}
          </Panel>
        </div>
      )}

      {activeTab === 'ai' && (
        <div className="grid gap-5 lg:grid-cols-[1fr_360px]">
          <Panel title="Attendance Insights" action={insightsUnavailable ? 'Unavailable' : `${insights.length} signals`}>
            {insightsUnavailable ? <DomainUnavailable message={insightsUnavailable} /> : <InsightList insights={insights} />}
          </Panel>
          <Panel title="Human Review Guardrails">
            <div className="space-y-3 text-sm text-slate-600 dark:text-slate-300">
              <Guardrail text="Insights surface anomalies, buddy-punching risk, late trends, and sync failures." />
              <Guardrail text="Insights never reject corrections, penalize employees, or finalize payroll decisions." />
              <Guardrail text="Payroll impacts remain pending until approved records are reviewed by authorized users." />
            </div>
          </Panel>
        </div>
      )}
      </TabPanel>
    </div>
  );
}

function Kpi({ label, value, icon: Icon, tone }: { label: string; value: string | number; icon: typeof Activity; tone: 'emerald' | 'rose' | 'amber' | 'blue' | 'slate' }) {
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
        <p className="text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">{label}</p>
        <span className={`grid h-9 w-9 place-items-center rounded-lg ${color}`}><Icon className="h-4 w-4" /></span>
      </div>
      <p className="mt-3 text-2xl font-bold text-slate-950 dark:text-white">{value}</p>
    </div>
  );
}

function SectionTitle({ icon: Icon, title, subtitle }: { icon: typeof Activity; title: string; subtitle: string }) {
  return (
    <div className="flex gap-3">
      <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-sapphire/10 text-sapphire"><Icon className="h-4 w-4" /></span>
      <div>
        <h2 className="text-sm font-bold text-slate-900 dark:text-white">{title}</h2>
        <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{subtitle}</p>
      </div>
    </div>
  );
}

function EmployeeSelect({ value, employees, onChange, includeAll }: { value: string; employees: EmployeeListItem[]; onChange: (value: string) => void; includeAll?: boolean }) {
  return (
    <select className="select w-full" value={value} onChange={(e) => onChange(e.target.value)} aria-label="Employee">
      <option value="">{includeAll ? 'All employees' : 'Select employee'}</option>
      {employees.map((employee) => <option key={employee.id} value={employee.id}>{employee.employeeCode} · {employee.fullName}</option>)}
    </select>
  );
}


function DailyTable({ records, loading, compact }: { records: AttendanceDailyRecord[]; loading?: boolean; compact?: boolean }) {
  return (
    <div className="surface overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full min-w-[760px] text-sm">
          <thead><tr className="border-b border-slate-100 dark:border-white/[0.07]">{['Date', 'Employee', 'In', 'Out', 'Worked', 'Late', 'Missing', 'Status'].map((h) => <th key={h} className="px-4 py-3 text-start text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>)}</tr></thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
            {loading && <tr><td colSpan={8} className="py-12 text-center text-slate-400">Loading live attendance...</td></tr>}
            {!loading && records.length === 0 && <tr><td colSpan={8}><Empty text="No processed attendance records for this selection." /></td></tr>}
            {records.map((r) => (
              <tr key={r.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">{r.workDate}</td>
                <td className="px-4 py-3 text-slate-700 dark:text-slate-300">{r.employeeName}<p className="text-xs text-slate-400">{r.department || r.branch}</p></td>
                <td className="px-4 py-3 font-mono text-slate-600 dark:text-slate-300">{time(r.firstInUtc)}</td>
                <td className="px-4 py-3 font-mono text-slate-600 dark:text-slate-300">{time(r.lastOutUtc)}</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{minutes(r.totalWorkedMinutes)}</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.lateMinutes ? `${r.lateMinutes}m` : '-'}</td>
                <td className="px-4 py-3">{r.missingPunch ? <StatusChip label="Missing" tone="rose" dot /> : <StatusChip label="Clear" tone="emerald" />}</td>
                <td className="px-4 py-3"><StatusChip label={r.status} tone={statusTone(r.status)} dot={!compact} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function RawTable({ rows, canViewSelfie }: { rows: AttendanceRawEvent[]; canViewSelfie: boolean }) {
  const { t } = useLocale();
  // The punch whose selfie HR is reviewing. The server says which punches have one (hasSelfie), never where it is stored.
  const [viewing, setViewing] = useState<{ id: string; employee: string; time: string } | null>(null);
  const closeViewer = useCallback(() => setViewing(null), []);
  if (rows.length === 0) return <Empty text="No raw attendance events found for this date." />;
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[760px] text-sm">
        <thead><tr className="border-b border-slate-100 dark:border-white/[0.07]">{['Timestamp', 'Employee', 'Source', 'Direction', 'Method', 'Processed'].map((h) => <th key={h} className="px-4 py-3 text-start text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>)}{canViewSelfie && <th className="px-4 py-3 text-start text-xs font-bold uppercase tracking-wide text-slate-400">{t('Selfie')}</th>}</tr></thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
          {rows.map((r) => {
            const employee = r.employeeCode || String(r.employeeId ?? '');
            const time = dateTime(r.punchTimestampUtc);
            return <tr key={r.id}><td className="px-4 py-3 font-mono text-slate-700 dark:text-slate-300">{time}</td><td className="px-4 py-3 text-slate-600 dark:text-slate-300">{employee}</td><td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.source}</td><td className="px-4 py-3"><StatusChip label={r.punchDirection} tone="blue" /></td><td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.verificationMethod}</td><td className="px-4 py-3"><StatusChip label={r.isProcessed ? 'Processed' : 'Raw'} tone={r.isProcessed ? 'emerald' : 'amber'} dot /></td>{canViewSelfie && <td className="px-4 py-3">{r.hasSelfie && (
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
  if (rows.length === 0) return <Empty text="No regularization requests in the live queue." />;
  return (
    <div className="space-y-3">
      {rows.map((r) => (
        <div key={r.id} className="rounded-lg border border-slate-100 p-3 dark:border-white/10">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div><p className="text-sm font-semibold text-slate-900 dark:text-white">Employee #{r.employeeId} · {r.requestType}</p><p className="text-xs text-slate-500">{r.workDate} · {r.reason}</p></div>
            <StatusChip label={r.status} tone={statusTone(r.status)} dot />
          </div>
          <div className="mt-3 flex gap-2"><button type="button" onClick={() => onApprove(r.id)} className="btn-secondary h-8 px-2 text-xs"><CheckCircle2 className="h-3.5 w-3.5" />Approve</button><button type="button" onClick={() => onReject(r.id)} className="btn-secondary h-8 px-2 text-xs">Reject</button></div>
        </div>
      ))}
    </div>
  );
}

function PayrollTable({ rows }: { rows: AttendancePayrollSummary[] }) {
  if (rows.length === 0) return <Empty text="No payroll attendance impacts for this date range." />;
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[640px] text-sm">
        <thead><tr className="border-b border-slate-100 dark:border-white/[0.07]">{['Employee', 'Late', 'Early', 'Absences', 'OT', 'Lock'].map((h) => <th key={h} className="px-4 py-3 text-start text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>)}</tr></thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">{rows.map((r) => <tr key={r.employeeId}><td className="px-4 py-3 font-semibold text-slate-900 dark:text-white">{r.employeeName}</td><td className="px-4 py-3">{r.lateMinutes}m</td><td className="px-4 py-3">{r.earlyExitMinutes}m</td><td className="px-4 py-3">{r.absenceDays}</td><td className="px-4 py-3">{minutes(r.overtimeMinutes)}</td><td className="px-4 py-3"><StatusChip label={r.hasLockedRecords ? 'Locked' : 'Open'} tone={r.hasLockedRecords ? 'rose' : 'emerald'} /></td></tr>)}</tbody>
      </table>
    </div>
  );
}

function InsightList({ insights }: { insights: AttendanceAIInsight[] }) {
  if (insights.length === 0) return <Empty text="No attendance insights are open." />;
  return (
    <div className="space-y-3">
      {insights.map((item) => <div key={item.id} className="rounded-lg border border-slate-100 p-3 dark:border-white/10"><div className="flex items-start justify-between gap-3"><div><p className="text-sm font-semibold text-slate-900 dark:text-white">{item.title}</p><p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{item.summary}</p></div><StatusChip label={item.severity} tone={item.severity === 'High' ? 'rose' : item.severity === 'Medium' ? 'amber' : 'blue'} /></div></div>)}
    </div>
  );
}

function Guardrail({ text }: { text: string }) {
  return <div className="flex gap-2"><MapPin className="mt-0.5 h-4 w-4 shrink-0 text-sapphire" /><p>{text}</p></div>;
}

