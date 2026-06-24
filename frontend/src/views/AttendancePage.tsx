'use client';

import { InfoTip } from '../components/InfoTip';
import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import type { ReactNode } from 'react';
import {
  Activity,
  AlertTriangle,
  Lightbulb,
  CalendarClock,
  CheckCircle2,
  Clock,
  Copy,
  Database,
  FileSpreadsheet,
  Fingerprint,
  History,
  Key,
  MapPin,
  Pencil,
  RefreshCw,
  ShieldCheck,
  Timer,
  Trash2,
  Upload,
  Users,
  X,
  Webhook,
} from 'lucide-react';
import { attendanceApi } from '../api/attendance';
import type {
  AttendanceAIInsight,
  AttendanceDailyRecord,
  AttendanceDashboardSummary,
  AttendanceDevice,
  AttendanceDeviceRequest,
  AttendanceDeviceSyncLog,
  AttendanceDeviceSyncSummary,
  AttendancePayrollSummary,
  AttendanceRawEvent,
  AttendanceRegularizationRequest,
  DeviceKeyResult,
} from '../api/attendance';
import { employeesApi } from '../api/employees';
import type { EmployeeListItem } from '../api/employees';
import { StatusChip } from '../components/StatusChip';

type TabKey = 'dashboard' | 'devices' | 'raw' | 'processing' | 'regularization' | 'reports' | 'ai';
type KvPair = { key: string; value: string };

interface DeviceFormState {
  deviceName: string; serialNumber: string; locationName: string; notes: string; isActive: boolean;
  deviceType: string; vendor: string;
  syncMethod: string; endpointUrl: string; ipAddress: string; port: string; syncFrequency: string;
  authType: string; authUsername: string; authPassword: string; authToken: string;
  authHeaderName: string; authHeaderValue: string; authParamName: string; authParamValue: string;
  customHeaders: KvPair[]; deviceParams: KvPair[]; fieldMappings: KvPair[];
}

const emptyKv = (): KvPair[] => [{ key: '', value: '' }];

const emptyDeviceForm: DeviceFormState = {
  deviceName: '', serialNumber: '', locationName: '', notes: '', isActive: true,
  deviceType: '', vendor: '',
  syncMethod: 'Push API', endpointUrl: '', ipAddress: '', port: '', syncFrequency: '',
  authType: 'None', authUsername: '', authPassword: '', authToken: '',
  authHeaderName: '', authHeaderValue: '', authParamName: 'api_key', authParamValue: '',
  customHeaders: emptyKv(), deviceParams: emptyKv(), fieldMappings: emptyKv(),
};

function kvJson(pairs: KvPair[]): string {
  return JSON.stringify(Object.fromEntries(pairs.filter(p => p.key.trim()).map(p => [p.key.trim(), p.value])));
}

function jsonToKv(json?: string): KvPair[] {
  if (!json || json === '{}') return emptyKv();
  try { return Object.entries(JSON.parse(json)).map(([key, value]) => ({ key, value: String(value) })); }
  catch { return emptyKv(); }
}

function toDeviceRequest(f: DeviceFormState): AttendanceDeviceRequest {
  const authCreds =
    f.authType === 'BasicAuth' ? { username: f.authUsername, password: f.authPassword } :
    f.authType === 'Bearer' ? { token: f.authToken } :
    f.authType === 'CustomHeader' ? { headerName: f.authHeaderName, headerValue: f.authHeaderValue } :
    f.authType === 'ApiKeyQuery' ? { paramName: f.authParamName, paramValue: f.authParamValue } : {};
  return {
    deviceName: f.deviceName, deviceType: f.deviceType || 'Other', vendor: f.vendor || 'Generic',
    serialNumber: f.serialNumber, locationName: f.locationName,
    ipAddress: f.ipAddress, endpointUrl: f.endpointUrl,
    port: f.port ? parseInt(f.port, 10) : undefined,
    syncMethod: f.syncMethod, syncFrequency: f.syncFrequency || 'Manual',
    authType: f.authType, authCredentialsJson: JSON.stringify(authCreds),
    customHeadersJson: kvJson(f.customHeaders),
    deviceParametersJson: kvJson(f.deviceParams),
    fieldMappingsJson: kvJson(f.fieldMappings),
    notes: f.notes, isActive: f.isActive,
  };
}

function deviceToForm(d: AttendanceDevice): DeviceFormState {
  const creds = (() => { try { return JSON.parse(d.authCredentialsJson || '{}'); } catch { return {}; } })();
  return {
    deviceName: d.deviceName, serialNumber: d.serialNumber, locationName: d.locationName,
    notes: d.notes || '', isActive: d.isActive,
    deviceType: d.deviceType, vendor: d.vendor,
    syncMethod: d.syncMethod || 'Push API', endpointUrl: d.endpointUrl, ipAddress: d.ipAddress,
    port: d.port?.toString() || '', syncFrequency: d.syncFrequency || '',
    authType: d.authType || 'None',
    authUsername: creds.username || '', authPassword: creds.password || '',
    authToken: creds.token || '',
    authHeaderName: creds.headerName || '', authHeaderValue: creds.headerValue || '',
    authParamName: creds.paramName || 'api_key', authParamValue: creds.paramValue || '',
    customHeaders: jsonToKv(d.customHeadersJson),
    deviceParams: jsonToKv(d.deviceParametersJson),
    fieldMappings: jsonToKv(d.fieldMappingsJson),
  };
}

const today = () => new Date().toISOString().slice(0, 10);
const nowLocal = () => new Date(Date.now() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 16);
const toUtc = (value: string) => value ? new Date(value).toISOString() : undefined;
const minutes = (value: number) => value > 0 ? `${Math.floor(value / 60)}h ${value % 60}m` : '0m';
const time = (value?: string) => value ? new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '-';
const dateTime = (value?: string) => value ? new Date(value).toLocaleString() : '-';

const tabs: Array<{ key: TabKey; label: string; icon: typeof Activity }> = [
  { key: 'dashboard', label: 'Live Dashboard', icon: Activity },
  { key: 'devices', label: 'Device Management', icon: Fingerprint },
  { key: 'raw', label: 'Raw Punch Logs', icon: Database },
  { key: 'processing', label: 'Processing', icon: RefreshCw },
  { key: 'regularization', label: 'Regularization', icon: ShieldCheck },
  { key: 'reports', label: 'Reports', icon: FileSpreadsheet },
  { key: 'ai', label: 'Insights', icon: Lightbulb },
];

const statusTone = (status: string): 'emerald' | 'rose' | 'amber' | 'blue' | 'slate' => {
  if (status === 'Present' || status === 'Approved' || status === 'Completed' || status === 'Success') return 'emerald';
  if (status === 'Absent' || status === 'Rejected' || status === 'Failed') return 'rose';
  if (status === 'Late' || status === 'Half day' || status.startsWith('Pending')) return 'amber';
  if (status === 'Processed') return 'blue';
  return 'slate';
};


export function AttendancePage() {
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

  const [filterDate, setFilterDate] = useState(today());
  const [statusFilter, setStatusFilter] = useState('');
  const [punchEmployeeId, setPunchEmployeeId] = useState('');
  const [punchDirection, setPunchDirection] = useState('In');
  const [punchSource, setPunchSource] = useState<'web' | 'mobile' | 'kiosk'>('web');
  const [showAddDevice, setShowAddDevice] = useState(false);
  const [deviceForm, setDeviceForm] = useState<DeviceFormState>(emptyDeviceForm);
  const [editingDevice, setEditingDevice] = useState<AttendanceDevice | null>(null);
  const [editDeviceForm, setEditDeviceForm] = useState<DeviceFormState>(emptyDeviceForm);
  const [deleteConfirmDevice, setDeleteConfirmDevice] = useState<AttendanceDevice | null>(null);
  const [deviceKeyResult, setDeviceKeyResult] = useState<DeviceKeyResult | null>(null);
  const [syncLogsDevice, setSyncLogsDevice] = useState<AttendanceDevice | null>(null);
  const [syncLogs, setSyncLogs] = useState<AttendanceDeviceSyncLog[]>([]);
  const [syncLogsLoading, setSyncLogsLoading] = useState(false);
  const [rawForm, setRawForm] = useState({ employeeCode: '', employeeId: '', deviceId: '', punchAt: nowLocal(), direction: 'In', source: 'API push', verificationMethod: 'RFID' });
  const [csvContent, setCsvContent] = useState('');
  const [processForm, setProcessForm] = useState({ fromDate: today(), toDate: today(), employeeId: '' });
  const [regularizationForm, setRegularizationForm] = useState({ employeeId: '', workDate: today(), requestType: 'Missed punch', requestedIn: '', requestedOut: '', reason: '' });
  const [decisionComment, setDecisionComment] = useState('Reviewed by HR.');

  const selectedEmployee = useMemo(
    () => employees.find((e) => e.id === Number(punchEmployeeId || regularizationForm.employeeId || processForm.employeeId)),
    [employees, punchEmployeeId, regularizationForm.employeeId, processForm.employeeId],
  );

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const [dashboard, day, raw, devicePage, employeePage, regPage, pendingPage, payroll, sync, ai] = await Promise.all([
        attendanceApi.dashboard(filterDate),
        attendanceApi.daily({ from: filterDate, to: filterDate, status: statusFilter || undefined, pageSize: 50 }),
        attendanceApi.events.raw({ from: filterDate, to: filterDate, pageSize: 50 }),
        attendanceApi.devices.list({ pageSize: 50 }),
        employeesApi.list({ pageSize: 100 }),
        attendanceApi.regularization.mine({ pageSize: 25 }),
        attendanceApi.regularization.pending({ pageSize: 25 }),
        attendanceApi.reports.payrollSummary(filterDate, filterDate),
        attendanceApi.reports.deviceSync(),
        attendanceApi.aiInsights(),
      ]);
      setSummary(dashboard);
      setDaily(day.items);
      setRawEvents(raw.items);
      setDevices(devicePage.items);
      setEmployees(employeePage.items);
      setRegularizations(regPage.items);
      setPendingRegularizations(pendingPage.items);
      setPayrollSummary(payroll);
      setDeviceSync(sync);
      setInsights(ai);
    } catch (err: any) {
      setError(err.response?.data?.message ?? err.message ?? 'Unable to load attendance workspace.');
    } finally {
      setLoading(false);
    }
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

  const submitDevice = (event: FormEvent) => {
    event.preventDefault();
    runAction(() => attendanceApi.devices.create(toDeviceRequest(deviceForm)), 'Attendance device saved.');
    setDeviceForm(emptyDeviceForm);
    setShowAddDevice(false);
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

  const openEdit = (device: AttendanceDevice) => {
    setEditingDevice(device);
    setEditDeviceForm(deviceToForm(device));
  };

  const submitEditDevice = async (event: FormEvent) => {
    event.preventDefault();
    if (!editingDevice) return;
    runAction(() => attendanceApi.devices.update(editingDevice.id, toDeviceRequest(editDeviceForm)), 'Device updated.');
    setEditingDevice(null);
  };

  const confirmDelete = async () => {
    if (!deleteConfirmDevice) return;
    runAction(() => attendanceApi.devices.remove(deleteConfirmDevice.id), `Device "${deleteConfirmDevice.deviceName}" deleted.`);
    setDeleteConfirmDevice(null);
  };

  const generateKey = async (device: AttendanceDevice) => {
    setSaving(true);
    setError('');
    setMessage('');
    try {
      const result = await attendanceApi.devices.generateKey(device.id);
      setDeviceKeyResult(result);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? err.message ?? 'Key generation failed.');
    } finally {
      setSaving(false);
    }
  };

  const openSyncLogs = async (device: AttendanceDevice) => {
    setSyncLogsDevice(device);
    setSyncLogsLoading(true);
    try {
      const logs = await attendanceApi.devices.logs(device.id);
      setSyncLogs(logs);
    } catch {
      setSyncLogs([]);
    } finally {
      setSyncLogsLoading(false);
    }
  };

  const totalWorked = daily.reduce((sum, item) => sum + item.totalWorkedMinutes, 0);

  return (
    <div className="space-y-5">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <p className="text-xs font-bold uppercase tracking-wide text-sapphire">Attendance & Time Tracking</p>
          <h1 className="mt-1 text-2xl font-bold text-slate-950 dark:text-white">Device-agnostic attendance command center</h1>
          <p className="mt-1 max-w-3xl text-sm text-slate-500 dark:text-slate-400">
            Live punches, device health, regularization, processing, payroll impacts, and exceptions from the database.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
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
        <div className={`rounded-lg border px-4 py-3 text-sm ${error ? 'border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300' : 'border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-500/20 dark:bg-emerald-500/10 dark:text-emerald-300'}`}>
          {error || message}
        </div>
      )}

      <div className="flex gap-2 overflow-x-auto pb-1">
        {tabs.map(({ key, label, icon: Icon }) => (
          <button key={key} type="button" onClick={() => setActiveTab(key)} className={`inline-flex h-9 shrink-0 items-center gap-2 rounded-lg px-3 text-sm font-semibold transition ${activeTab === key ? 'bg-sapphire text-white' : 'border border-slate-200 bg-white text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-300'}`}>
            <Icon className="h-4 w-4" />{label}
          </button>
        ))}
      </div>

      {activeTab === 'dashboard' && (
        <div className="grid gap-5 xl:grid-cols-[1fr_360px]">
          <div className="space-y-5">
            <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
              <Kpi label="Present" value={summary?.present ?? 0} icon={Users} tone="emerald" />
              <Kpi label="Absent" value={summary?.absent ?? 0} icon={AlertTriangle} tone="rose" />
              <Kpi label="Late" value={summary?.late ?? 0} icon={Clock} tone="amber" />
              <Kpi label="Missing Punch" value={summary?.missingPunch ?? 0} icon={Timer} tone="blue" />
            </div>

            <div className="grid gap-5 lg:grid-cols-[360px_1fr]">
              <form onSubmit={submitPunch} className="surface p-4">
                <SectionTitle icon={CalendarClock} title="Web / Mobile / Kiosk Punch" subtitle={selectedEmployee ? `${selectedEmployee.employeeCode} · ${selectedEmployee.fullName}` : 'Select an employee from live records'} />
                <div className="mt-4 space-y-3">
                  <EmployeeSelect value={punchEmployeeId} employees={employees} onChange={setPunchEmployeeId} />
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
                  <button type="submit" disabled={saving || !punchEmployeeId} className="btn-primary w-full justify-center"><Clock className="h-4 w-4" />Save Punch</button>
                </div>
              </form>

              <DailyTable records={daily} loading={loading} />
            </div>
          </div>

          <div className="space-y-5">
            <Panel title="Device Health" action={`${deviceSync.length} devices`}>
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
            </Panel>
            <Panel title="Exceptions" action={`${insights.length} open`}>
              <InsightList insights={insights.slice(0, 5)} />
            </Panel>
          </div>
        </div>
      )}

      {activeTab === 'devices' && (
        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Connect any attendance source — biometric, RFID, face recognition, REST API, SFTP, or manual CSV.
            </p>
            <button type="button" onClick={() => { setDeviceForm(emptyDeviceForm); setShowAddDevice(true); }} className="btn-primary shrink-0">
              <Fingerprint className="h-4 w-4" />Add Device
            </button>
          </div>
          <Panel title="Configured Devices" action={`${devices.length} sources`}>
            <DeviceTable
              devices={devices}
              onTest={(id) => runAction(() => attendanceApi.devices.test(id), 'Connection test completed.')}
              onSync={(id) => runAction(() => attendanceApi.devices.sync(id), 'Sync attempt completed.')}
              onEdit={openEdit}
              onDelete={setDeleteConfirmDevice}
              onGenerateKey={generateKey}
              onViewLogs={openSyncLogs}
            />
          </Panel>
        </div>
      )}

      {activeTab === 'raw' && (
        <div className="grid gap-5 xl:grid-cols-[420px_1fr]">
          <div className="space-y-5">
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
            <form onSubmit={submitImport} className="surface p-4">
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
            </form>
          </div>
          <Panel title="Raw Punch Logs" action={`${rawEvents.length} latest`}>
            <RawTable rows={rawEvents} />
          </Panel>
        </div>
      )}

      {activeTab === 'processing' && (
        <div className="grid gap-5 lg:grid-cols-[360px_1fr]">
          <form onSubmit={submitProcessing} className="surface p-4">
            <SectionTitle icon={RefreshCw} title="Process Attendance" subtitle="Transforms raw punches into daily attendance, exceptions, and payroll impacts." />
            <div className="mt-4 space-y-3">
              <input type="date" className="input w-full" value={processForm.fromDate} onChange={(e) => setProcessForm({ ...processForm, fromDate: e.target.value })} aria-label="From date" />
              <input type="date" className="input w-full" value={processForm.toDate} onChange={(e) => setProcessForm({ ...processForm, toDate: e.target.value })} aria-label="To date" />
              <EmployeeSelect value={processForm.employeeId} employees={employees} onChange={(value) => setProcessForm({ ...processForm, employeeId: value })} includeAll />
              <button type="submit" disabled={saving} className="btn-primary w-full justify-center">Process Records</button>
            </div>
          </form>
          <div className="grid gap-5 md:grid-cols-3">
            <Kpi label="Worked Today" value={minutes(totalWorked)} icon={Timer} tone="blue" />
            <Kpi label="Overtime Cases" value={summary?.overtimeEmployees ?? 0} icon={Clock} tone="amber" />
            <Kpi label="Payroll Locked" value={daily.filter((x) => x.isPayrollLocked).length} icon={ShieldCheck} tone="slate" />
          </div>
        </div>
      )}

      {activeTab === 'regularization' && (
        <div className="grid gap-5 xl:grid-cols-[420px_1fr]">
          <form onSubmit={submitRegularization} className="surface p-4">
            <SectionTitle icon={ShieldCheck} title="Correction Request" subtitle="Missed punch, wrong punch, WFH, site visit, or manual correction." />
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
              <button type="submit" disabled={saving || !regularizationForm.employeeId || !regularizationForm.reason} className="btn-primary w-full justify-center">Submit Request</button>
            </div>
          </form>
          <Panel title="Pending Approval Queue" action={`${pendingRegularizations.length} pending`}>
            <input className="input mb-3 w-full" value={decisionComment} onChange={(e) => setDecisionComment(e.target.value)} aria-label="Decision comment" />
            <RegularizationTable rows={pendingRegularizations.length ? pendingRegularizations : regularizations} onApprove={(id) => runAction(() => attendanceApi.regularization.approve(id, decisionComment), 'Regularization approved and attendance reprocessed.')} onReject={(id) => runAction(() => attendanceApi.regularization.reject(id, decisionComment), 'Regularization rejected.')} />
          </Panel>
        </div>
      )}

      {activeTab === 'reports' && (
        <div className="grid gap-5 xl:grid-cols-2">
          <Panel title="Payroll Attendance Summary" action={`${payrollSummary.length} employees`}>
            <PayrollTable rows={payrollSummary} />
          </Panel>
          <Panel title="Daily Exception Reports" action={filterDate}>
            <DailyTable records={daily.filter((x) => x.lateMinutes > 0 || x.missingPunch || x.status === 'Absent')} compact />
          </Panel>
        </div>
      )}

      {activeTab === 'ai' && (
        <div className="grid gap-5 lg:grid-cols-[1fr_360px]">
          <Panel title="Attendance Insights" action={`${insights.length} signals`}>
            <InsightList insights={insights} />
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

      {/* ── Add / Edit Device Modal ─────────────────────────────────────────── */}
      {(showAddDevice || editingDevice) && (
        <DeviceFormModal
          title={editingDevice ? `Edit — ${editingDevice.deviceName}` : 'Add Attendance Device'}
          form={editingDevice ? editDeviceForm : deviceForm}
          setForm={editingDevice ? setEditDeviceForm : setDeviceForm}
          onSubmit={editingDevice ? submitEditDevice : submitDevice}
          onClose={() => { setShowAddDevice(false); setEditingDevice(null); }}
          saving={saving}
        />
      )}

      {/* ── Delete Confirm Modal ────────────────────────────────────────────── */}
      {deleteConfirmDevice && (
        <Modal title="Delete Device" onClose={() => setDeleteConfirmDevice(null)}>
          <p className="text-sm text-slate-600 dark:text-slate-300">
            Are you sure you want to delete <span className="font-semibold text-slate-900 dark:text-white">{deleteConfirmDevice.deviceName}</span>?
            All sync logs for this device will be retained for audit, but the device will stop receiving punches.
          </p>
          <div className="mt-4 flex justify-end gap-2">
            <button type="button" onClick={() => setDeleteConfirmDevice(null)} className="btn-secondary">Cancel</button>
            <button type="button" onClick={confirmDelete} disabled={saving} className="btn-primary bg-rose-600 hover:bg-rose-700 border-rose-600 hover:border-rose-700">Delete Device</button>
          </div>
        </Modal>
      )}

      {/* ── API Key Modal ───────────────────────────────────────────────────── */}
      {deviceKeyResult && (
        <Modal title="API Key Generated" onClose={() => setDeviceKeyResult(null)}>
          <div className="space-y-4">
            <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-200">
              This key will only be shown once. Copy and configure it on your biometric device now.
            </div>
            <div>
              <p className="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">Device API Key — {deviceKeyResult.deviceName}</p>
              <div className="flex items-center gap-2">
                <code className="flex-1 rounded-lg border bg-slate-50 px-3 py-2 font-mono text-sm dark:bg-white/[0.04] dark:border-white/10">{deviceKeyResult.key}</code>
                <button type="button" onClick={() => navigator.clipboard.writeText(deviceKeyResult.key)} className="btn-secondary h-9 px-3" title="Copy key"><Copy className="h-4 w-4" /></button>
              </div>
            </div>
            <div className="rounded-lg border border-slate-200 bg-slate-50 p-4 dark:border-white/10 dark:bg-white/[0.04]">
              <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500 flex items-center gap-1.5"><Webhook className="h-3.5 w-3.5" />Biometric Device Configuration</p>
              <p className="text-xs text-slate-600 dark:text-slate-300 mb-2">Configure your device to POST attendance punches to:</p>
              <code className="block rounded border bg-white px-3 py-2 font-mono text-xs dark:bg-black/20 dark:border-white/10">{process.env.NEXT_PUBLIC_API_BASE_URL || '[API Base URL]'}/api/attendance/ingest</code>
              <p className="mt-3 text-xs text-slate-500 mb-1">Required HTTP header:</p>
              <code className="block rounded border bg-white px-3 py-2 font-mono text-xs dark:bg-black/20 dark:border-white/10">X-Device-Key: {deviceKeyResult.key}</code>
              <p className="mt-3 text-xs text-slate-500 mb-1">Example JSON body:</p>
              <code className="block rounded border bg-white px-3 py-2 font-mono text-xs whitespace-pre dark:bg-black/20 dark:border-white/10">{`{"punches":[{"employeeCode":"EMP-001","punchTimestampUtc":"2026-06-16T09:00:00Z","punchDirection":"In"}]}`}</code>
            </div>
            <div className="flex justify-end">
              <button type="button" onClick={() => setDeviceKeyResult(null)} className="btn-primary">Done</button>
            </div>
          </div>
        </Modal>
      )}

      {/* ── Sync Logs Modal ─────────────────────────────────────────────────── */}
      {syncLogsDevice && (
        <Modal title={`Sync Logs — ${syncLogsDevice.deviceName}`} onClose={() => setSyncLogsDevice(null)}>
          {syncLogsLoading && <p className="py-6 text-center text-sm text-slate-400">Loading logs…</p>}
          {!syncLogsLoading && syncLogs.length === 0 && <Empty text="No sync logs found for this device." />}
          {!syncLogsLoading && syncLogs.length > 0 && (
            <div className="space-y-2 max-h-96 overflow-y-auto">
              {syncLogs.map((log) => (
                <div key={log.id} className="rounded-lg border border-slate-100 p-3 dark:border-white/10">
                  <div className="flex items-center justify-between gap-3">
                    <div>
                      <p className="text-sm font-semibold text-slate-900 dark:text-white">{log.syncMethod}</p>
                      <p className="text-xs text-slate-400">{dateTime(log.completedAtUtc ?? log.startedAtUtc)} · {log.rawEventsReceived} events received · {log.rawEventsProcessed} processed</p>
                    </div>
                    <StatusChip label={log.status} tone={statusTone(log.status)} dot />
                  </div>
                  {log.errorMessage && (
                    <p className="mt-2 text-xs text-slate-500 dark:text-slate-400 border-t border-slate-100 dark:border-white/10 pt-2">{log.errorMessage}</p>
                  )}
                </div>
              ))}
            </div>
          )}
          <div className="mt-4 flex justify-end">
            <button type="button" onClick={() => setSyncLogsDevice(null)} className="btn-secondary">Close</button>
          </div>
        </Modal>
      )}
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

function Panel({ title, action, children }: { title: string; action?: string; children: ReactNode }) {
  return (
    <div className="surface overflow-hidden">
      <div className="flex items-center justify-between border-b border-slate-100 px-4 py-3 dark:border-white/[0.07]">
        <h2 className="text-sm font-bold text-slate-900 dark:text-white">{title}</h2>
        {action && <span className="text-xs font-semibold text-slate-400 dark:text-slate-500">{action}</span>}
      </div>
      <div className="p-4">{children}</div>
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

function Field({ label, children, info, infoKey }: { label: string; children: ReactNode; info?: string; infoKey?: string }) {
  return <label className="block text-xs font-semibold text-slate-500 dark:text-slate-400"><span className="flex items-center gap-1.5">{label}{info && <InfoTip text={info} fieldKey={infoKey} />}</span><div className="mt-1">{children}</div></label>;
}

function EmployeeSelect({ value, employees, onChange, includeAll }: { value: string; employees: EmployeeListItem[]; onChange: (value: string) => void; includeAll?: boolean }) {
  return (
    <select className="select w-full" value={value} onChange={(e) => onChange(e.target.value)} aria-label="Employee">
      <option value="">{includeAll ? 'All employees' : 'Select employee'}</option>
      {employees.map((employee) => <option key={employee.id} value={employee.id}>{employee.employeeCode} · {employee.fullName}</option>)}
    </select>
  );
}


function Empty({ text }: { text: string }) {
  return <div className="rounded-lg border border-dashed border-slate-200 p-6 text-center text-sm text-slate-400 dark:border-white/10 dark:text-slate-500">{text}</div>;
}

function DailyTable({ records, loading, compact }: { records: AttendanceDailyRecord[]; loading?: boolean; compact?: boolean }) {
  return (
    <div className="surface overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full min-w-[760px] text-sm">
          <thead><tr className="border-b border-slate-100 dark:border-white/[0.07]">{['Date', 'Employee', 'In', 'Out', 'Worked', 'Late', 'Missing', 'Status'].map((h) => <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>)}</tr></thead>
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

function DeviceTable({
  devices,
  onSync,
  onTest,
  onEdit,
  onDelete,
  onGenerateKey,
  onViewLogs,
}: {
  devices: AttendanceDevice[];
  onSync: (id: string) => void;
  onTest: (id: string) => void;
  onEdit: (device: AttendanceDevice) => void;
  onDelete: (device: AttendanceDevice) => void;
  onGenerateKey: (device: AttendanceDevice) => void;
  onViewLogs: (device: AttendanceDevice) => void;
}) {
  if (devices.length === 0) return <Empty text="No devices found. Add a connector-backed attendance source." />;
  return (
    <div className="space-y-3">
      {devices.map((d) => {
        const isPush = d.syncMethod?.toLowerCase().includes('push');
        const hasKey = !!d.apiKeyReference;
        const hasError = !!d.errorLog;
        return (
          <div key={d.id} className="rounded-xl border border-slate-200 bg-white dark:border-white/10 dark:bg-white/[0.03]">
            <div className="flex flex-wrap items-start gap-3 p-4">
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <p className="font-semibold text-slate-900 dark:text-white">{d.deviceName}</p>
                  <StatusChip label={d.lastSyncStatus || 'Never synced'} tone={statusTone(d.lastSyncStatus || 'Never')} dot />
                  {!d.isActive && <StatusChip label="Inactive" tone="rose" />}
                  {isPush && <span className="inline-flex items-center gap-1 rounded-full border border-blue-200 bg-blue-50 px-2 py-0.5 text-[11px] font-semibold text-blue-600 dark:border-blue-500/20 dark:bg-blue-500/10 dark:text-blue-300"><Webhook className="h-3 w-3" />Push</span>}
                </div>
                <p className="mt-1 text-xs text-slate-400">{d.vendor} · {d.deviceType} · SN: {d.serialNumber || '—'}</p>
                <p className="text-xs text-slate-400">{d.locationName || 'No location'} · {d.syncMethod} · {d.syncFrequency || 'Manual'}</p>
                {isPush && (
                  <div className="mt-2 rounded-lg border border-slate-100 bg-slate-50 px-3 py-2 dark:border-white/10 dark:bg-white/[0.03]">
                    <p className="text-[11px] font-semibold uppercase tracking-wide text-slate-400 mb-1">Webhook endpoint</p>
                    <code className="text-xs font-mono text-slate-700 dark:text-slate-300 break-all">
                      POST {process.env.NEXT_PUBLIC_API_BASE_URL || '[API Base URL]'}/api/attendance/ingest
                    </code>
                    <p className="mt-1 text-[11px] text-slate-400">
                      Header: <span className="font-mono">X-Device-Key: {hasKey ? '••••••••••' : '[generate a key below]'}</span>
                    </p>
                  </div>
                )}
                {hasError && (
                  <div className="mt-2 flex items-start gap-1.5 text-xs text-rose-600 dark:text-rose-400">
                    <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                    <span>{d.errorLog}</span>
                  </div>
                )}
              </div>
              <div className="flex flex-wrap gap-1.5 shrink-0">
                <button type="button" onClick={() => onTest(d.id)} className="btn-secondary h-8 px-2.5 text-xs" title="Test connection">Test</button>
                <button type="button" onClick={() => onSync(d.id)} className="btn-secondary h-8 px-2.5 text-xs" title="Trigger sync">Sync</button>
                <button type="button" onClick={() => onViewLogs(d)} className="btn-secondary h-8 px-2.5 text-xs" title="View sync logs"><History className="h-3.5 w-3.5" /></button>
                <button type="button" onClick={() => onGenerateKey(d)} className="btn-secondary h-8 px-2.5 text-xs" title={hasKey ? 'Rotate API key' : 'Generate API key'}><Key className="h-3.5 w-3.5" /></button>
                <button type="button" onClick={() => onEdit(d)} className="btn-secondary h-8 px-2.5 text-xs" title="Edit device"><Pencil className="h-3.5 w-3.5" /></button>
                <button type="button" onClick={() => onDelete(d)} className="btn-secondary h-8 px-2.5 text-xs text-rose-600 dark:text-rose-400 hover:border-rose-300" title="Delete device"><Trash2 className="h-3.5 w-3.5" /></button>
              </div>
            </div>
          </div>
        );
      })}
    </div>
  );
}

function RawTable({ rows }: { rows: AttendanceRawEvent[] }) {
  if (rows.length === 0) return <Empty text="No raw attendance events found for this date." />;
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[760px] text-sm">
        <thead><tr className="border-b border-slate-100 dark:border-white/[0.07]">{['Timestamp', 'Employee', 'Source', 'Direction', 'Method', 'Processed'].map((h) => <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>)}</tr></thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
          {rows.map((r) => <tr key={r.id}><td className="px-4 py-3 font-mono text-slate-700 dark:text-slate-300">{dateTime(r.punchTimestampUtc)}</td><td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.employeeCode || r.employeeId}</td><td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.source}</td><td className="px-4 py-3"><StatusChip label={r.punchDirection} tone="blue" /></td><td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.verificationMethod}</td><td className="px-4 py-3"><StatusChip label={r.isProcessed ? 'Processed' : 'Raw'} tone={r.isProcessed ? 'emerald' : 'amber'} dot /></td></tr>)}
        </tbody>
      </table>
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
        <thead><tr className="border-b border-slate-100 dark:border-white/[0.07]">{['Employee', 'Late', 'Early', 'Absences', 'OT', 'Lock'].map((h) => <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400">{h}</th>)}</tr></thead>
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

function KvEditor({ label, hint, pairs, onChange, keyPlaceholder, valuePlaceholder }: {
  label: string; hint?: string; pairs: KvPair[];
  onChange: (pairs: KvPair[]) => void;
  keyPlaceholder?: string; valuePlaceholder?: string;
}) {
  const set = (i: number, field: 'key' | 'value', val: string) => {
    const next = pairs.map((p, idx) => idx === i ? { ...p, [field]: val } : p);
    onChange(next);
  };
  const add = () => onChange([...pairs, { key: '', value: '' }]);
  const remove = (i: number) => onChange(pairs.length === 1 ? [{ key: '', value: '' }] : pairs.filter((_, idx) => idx !== i));
  return (
    <div>
      <div className="mb-1.5 flex items-center justify-between">
        <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">{label}</p>
        <button type="button" onClick={add} className="text-xs font-semibold text-sapphire hover:underline">+ Add row</button>
      </div>
      {hint && <p className="mb-2 text-[11px] text-slate-400">{hint}</p>}
      <div className="space-y-1.5">
        {pairs.map((p, i) => (
          <div key={i} className="flex gap-1.5">
            <input aria-label={`${label} key ${i + 1}`} className="input flex-1 font-mono text-xs" placeholder={keyPlaceholder ?? 'Key'} value={p.key} onChange={(e) => set(i, 'key', e.target.value)} />
            <input aria-label={`${label} value ${i + 1}`} className="input flex-1 font-mono text-xs" placeholder={valuePlaceholder ?? 'Value'} value={p.value} onChange={(e) => set(i, 'value', e.target.value)} />
            <button type="button" onClick={() => remove(i)} aria-label="Remove row" className="grid h-9 w-9 shrink-0 place-items-center rounded-lg text-slate-400 hover:bg-rose-50 hover:text-rose-500 dark:hover:bg-rose-500/10"><X className="h-3.5 w-3.5" /></button>
          </div>
        ))}
      </div>
    </div>
  );
}

function DeviceFormModal({ title, form, setForm, onSubmit, onClose, saving }: {
  title: string;
  form: DeviceFormState;
  setForm: (f: DeviceFormState) => void;
  onSubmit: (e: FormEvent) => void;
  onClose: () => void;
  saving: boolean;
}) {
  const f = form;
  const set = (patch: Partial<DeviceFormState>) => setForm({ ...f, ...patch });
  const isPush = f.syncMethod?.toLowerCase().includes('push');

  return (
    <Modal title={title} onClose={onClose} size="xl">
      <form onSubmit={onSubmit} className="space-y-6">

        {/* ── Basic Details ─────────────────────────────────────── */}
        <section>
          <p className="mb-3 text-[11px] font-bold uppercase tracking-widest text-slate-400">Basic Details</p>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Device name *">
              <input aria-label="Device name" className="input w-full" value={f.deviceName} onChange={(e) => set({ deviceName: e.target.value })} placeholder="Main Gate Biometric" required />
            </Field>
            <Field label="Serial number *" info="Printed on device hardware. Must be unique across this workspace." infoKey="attendance.device_serial">
              <input aria-label="Serial number" className="input w-full" value={f.serialNumber} onChange={(e) => set({ serialNumber: e.target.value })} placeholder="ZK-8880012345" required />
            </Field>
            <Field label="Location / branch">
              <input aria-label="Location" className="input w-full" value={f.locationName} onChange={(e) => set({ locationName: e.target.value })} placeholder="Main entrance, Floor 3…" />
            </Field>
            <Field label="Status">
              <select aria-label="Status" className="select w-full" value={f.isActive ? 'active' : 'inactive'} onChange={(e) => set({ isActive: e.target.value === 'active' })}>
                <option value="active">Active — accepting punches</option>
                <option value="inactive">Inactive — paused</option>
              </select>
            </Field>
          </div>
          <Field label="Notes / model info">
            <input aria-label="Notes" className="input w-full mt-1" value={f.notes} onChange={(e) => set({ notes: e.target.value })} placeholder="ZKTeco K20, firmware 2.3, installed 2026-01, IT contact: John…" />
          </Field>
        </section>

        {/* ── Device Identity ───────────────────────────────────── */}
        <section>
          <p className="mb-3 text-[11px] font-bold uppercase tracking-widest text-slate-400">Device Identity</p>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Vendor" info="Free text — enter any vendor name. Examples: ZKTeco, Hikvision, Suprema, Anviz, eSSL, Dahua, Generic." infoKey="attendance.vendor">
              <input aria-label="Vendor" list="vendor-suggestions" className="input w-full" value={f.vendor} onChange={(e) => set({ vendor: e.target.value })} placeholder="ZKTeco" />
              <datalist id="vendor-suggestions">
                {['ZKTeco','Hikvision','Suprema','Anviz','eSSL','Dahua','Identix','Realand','Generic biometric','Generic RFID','Custom REST API'].map(v => <option key={v} value={v} />)}
              </datalist>
            </Field>
            <Field label="Device type">
              <input aria-label="Device type" list="type-suggestions" className="input w-full" value={f.deviceType} onChange={(e) => set({ deviceType: e.target.value })} placeholder="Biometric fingerprint + face" />
              <datalist id="type-suggestions">
                {['Biometric fingerprint','Face recognition','RFID/card reader','Fingerprint + face','Fingerprint + RFID','All-in-one (face + finger + RFID)','Mobile app','Kiosk/tablet','REST API','SFTP/CSV feed'].map(v => <option key={v} value={v} />)}
              </datalist>
            </Field>
          </div>
        </section>

        {/* ── Connection ────────────────────────────────────────── */}
        <section>
          <p className="mb-3 text-[11px] font-bold uppercase tracking-widest text-slate-400">Connection</p>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Sync method" info="Push = device calls our webhook. Pull = we call the device on schedule. SDK = vendor library. Check device manual." infoKey="attendance.sync_method">
              <select aria-label="Sync method" className="select w-full" value={f.syncMethod} onChange={(e) => set({ syncMethod: e.target.value })}>
                <option>Push API</option><option>Pull API</option><option>SDK</option>
                <option>SFTP import</option><option>CSV import</option><option>Manual upload</option>
              </select>
            </Field>
            <Field label="Sync frequency">
              <input aria-label="Sync frequency" list="freq-suggestions" className="input w-full" value={f.syncFrequency} onChange={(e) => set({ syncFrequency: e.target.value })} placeholder="Every 5 minutes" />
              <datalist id="freq-suggestions">
                {['Real-time (Push)','Every 1 minute','Every 5 minutes','Every 15 minutes','Every 30 minutes','Hourly','Manual'].map(v => <option key={v} value={v} />)}
              </datalist>
            </Field>
            <Field label="Device IP / hostname">
              <input aria-label="Device IP address" className="input w-full font-mono" value={f.ipAddress} onChange={(e) => set({ ipAddress: e.target.value })} placeholder="192.168.1.50" />
            </Field>
            <Field label="Port">
              <input aria-label="Port" className="input w-full font-mono" value={f.port} onChange={(e) => set({ port: e.target.value })} placeholder="4370 (ZKTeco default)" />
            </Field>
            <Field label="API endpoint URL (Pull / REST devices)" info="Full URL the system will call to fetch attendance records. E.g. http://192.168.1.50/api/att/logs" infoKey="attendance.device_endpoint">
              <input aria-label="Endpoint URL" className="input w-full font-mono" value={f.endpointUrl} onChange={(e) => set({ endpointUrl: e.target.value })} placeholder="http://192.168.1.50/api/att/logs" />
            </Field>
          </div>
          {isPush && (
            <div className="mt-3 rounded-lg border border-blue-200 bg-blue-50 p-3 dark:border-blue-500/20 dark:bg-blue-500/10">
              <p className="text-xs font-semibold text-blue-700 dark:text-blue-300 mb-1 flex items-center gap-1.5"><Webhook className="h-3.5 w-3.5" />Push API — configure this device to POST to:</p>
              <code className="block text-xs font-mono text-blue-800 dark:text-blue-200">{process.env.NEXT_PUBLIC_API_BASE_URL || '[API Base URL]'}/api/attendance/ingest</code>
              <p className="mt-1 text-xs text-blue-600 dark:text-blue-300">Required header: <span className="font-mono">X-Device-Key: [generate after saving]</span></p>
            </div>
          )}
        </section>

        {/* ── Authentication ────────────────────────────────────── */}
        <section>
          <p className="mb-3 text-[11px] font-bold uppercase tracking-widest text-slate-400">Authentication (for Pull API requests)</p>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Auth type">
              <select aria-label="Auth type" className="select w-full" value={f.authType} onChange={(e) => set({ authType: e.target.value })}>
                <option value="None">None (open endpoint)</option>
                <option value="BasicAuth">Basic Auth (username + password)</option>
                <option value="Bearer">Bearer Token</option>
                <option value="CustomHeader">Custom Header (e.g. X-API-Key)</option>
                <option value="ApiKeyQuery">Query Param (e.g. ?api_key=xxx)</option>
              </select>
            </Field>
            {f.authType === 'BasicAuth' && <>
              <Field label="Username"><input aria-label="Auth username" className="input w-full" value={f.authUsername} onChange={(e) => set({ authUsername: e.target.value })} placeholder="admin" /></Field>
              <Field label="Password"><input aria-label="Auth password" type="password" className="input w-full" value={f.authPassword} onChange={(e) => set({ authPassword: e.target.value })} placeholder="••••••••" /></Field>
            </>}
            {f.authType === 'Bearer' && (
              <Field label="Bearer token"><input aria-label="Bearer token" className="input w-full font-mono" value={f.authToken} onChange={(e) => set({ authToken: e.target.value })} placeholder="eyJ..." /></Field>
            )}
            {f.authType === 'CustomHeader' && <>
              <Field label="Header name"><input aria-label="Header name" className="input w-full font-mono" value={f.authHeaderName} onChange={(e) => set({ authHeaderName: e.target.value })} placeholder="X-API-Key" /></Field>
              <Field label="Header value"><input aria-label="Header value" className="input w-full font-mono" value={f.authHeaderValue} onChange={(e) => set({ authHeaderValue: e.target.value })} placeholder="your-secret-key" /></Field>
            </>}
            {f.authType === 'ApiKeyQuery' && <>
              <Field label="Param name"><input aria-label="Query param name" className="input w-full font-mono" value={f.authParamName} onChange={(e) => set({ authParamName: e.target.value })} placeholder="api_key" /></Field>
              <Field label="Param value"><input aria-label="Query param value" className="input w-full font-mono" value={f.authParamValue} onChange={(e) => set({ authParamValue: e.target.value })} placeholder="your-key-value" /></Field>
            </>}
          </div>
        </section>

        {/* ── Custom HTTP Headers ───────────────────────────────── */}
        <section>
          <KvEditor
            label="Custom HTTP Headers"
            hint="Extra headers included on every Pull API request. E.g. Accept: application/json, X-ISAPI-Version: 2.0 (Hikvision), X-Tenant: acme"
            pairs={f.customHeaders}
            onChange={(pairs) => set({ customHeaders: pairs })}
            keyPlaceholder="Header-Name"
            valuePlaceholder="header-value"
          />
        </section>

        {/* ── Device Parameters ─────────────────────────────────── */}
        <section>
          <KvEditor
            label="Device Parameters"
            hint="Vendor-specific config. Common: poll_path=/iclock/cdata, employee_field=uid, timestamp_field=punch_time, direction_field=punch_state, batch_size=100, timeout_seconds=30, date_format=2006-01-02T15:04:05Z"
            pairs={f.deviceParams}
            onChange={(pairs) => set({ deviceParams: pairs })}
            keyPlaceholder="param_name"
            valuePlaceholder="value"
          />
        </section>

        {/* ── Field Mappings ────────────────────────────────────── */}
        <section>
          <KvEditor
            label="Field Mappings (Device field → System field)"
            hint="Map the device's JSON/XML field names to system fields. System fields: employeeCode, punchTimestampUtc, punchDirection, verificationMethod, latitude, longitude. E.g. uid→employeeCode, check_type→punchDirection"
            pairs={f.fieldMappings}
            onChange={(pairs) => set({ fieldMappings: pairs })}
            keyPlaceholder="device_field_name"
            valuePlaceholder="systemFieldName"
          />
        </section>

        <div className="flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-white/[0.07]">
          <button type="button" onClick={onClose} className="btn-secondary">Cancel</button>
          <button type="submit" disabled={saving} className="btn-primary">{saving ? 'Saving…' : 'Save Device'}</button>
        </div>
      </form>
    </Modal>
  );
}

function Modal({ title, onClose, children, size = 'md' }: { title: string; onClose: () => void; children: ReactNode; size?: 'md' | 'lg' | 'xl' }) {
  const maxW = size === 'xl' ? 'max-w-4xl' : size === 'lg' ? 'max-w-3xl' : 'max-w-2xl';
  if (typeof document === 'undefined') return null;
  return createPortal(
    <div className="fixed inset-0 z-[9999] overflow-y-auto bg-black/50 backdrop-blur-sm">
      <div className="flex min-h-screen items-start justify-center p-4" onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}>
        <div className={`relative my-8 w-full ${maxW} rounded-2xl border border-slate-200 bg-white shadow-2xl dark:border-white/10 dark:bg-[#0e1729]`}>
          <div className="sticky top-0 z-10 flex items-center justify-between rounded-t-2xl border-b border-slate-100 bg-white px-6 py-4 dark:border-white/[0.07] dark:bg-[#0e1729]">
            <h2 className="text-base font-bold text-slate-900 dark:text-white">{title}</h2>
            <button type="button" onClick={onClose} aria-label="Close" className="grid h-8 w-8 place-items-center rounded-lg text-slate-400 hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-white/10"><X className="h-4 w-4" /></button>
          </div>
          <div className="p-6">{children}</div>
        </div>
      </div>
    </div>,
    document.body
  );
}
