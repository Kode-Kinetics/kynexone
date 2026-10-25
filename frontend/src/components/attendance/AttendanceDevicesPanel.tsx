'use client';

import { FormEvent, useCallback, useEffect, useLayoutEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import type { ReactNode } from 'react';
import { AlertTriangle, Copy, Fingerprint, History, Key, Pencil, Trash2, Webhook, X } from 'lucide-react';
import { attendanceApi } from '../../api/attendance';
import type { AttendanceDevice, AttendanceDeviceRequest, AttendanceDeviceSyncLog, DeviceKeyResult } from '../../api/attendance';
import { InfoTip } from '../InfoTip';
import { StatusChip } from '../StatusChip';
import { useAuth } from '../../contexts/AuthContext';
import { unavailableMessage } from '../../lib/attendanceLoadState';
import { DomainUnavailable, Empty, Panel, dateTime, statusTone } from './attendanceUi';

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
    // Stored credentials are never returned, so an edit form starts blank; defaulting the name to 'api_key'
    // would overwrite a stored parameter name on save. New devices still default it (emptyDeviceForm).
    authParamName: creds.paramName || '', authParamValue: creds.paramValue || '',
    customHeaders: jsonToKv(d.customHeadersJson),
    deviceParams: jsonToKv(d.deviceParametersJson),
    fieldMappings: jsonToKv(d.fieldMappingsJson),
  };
}

/**
 * The attendance devices (biometric, RFID, REST, SFTP, CSV sources) and their connection keys: the Attendance devices
 * section of Setup. It loads and manages the devices itself, so it needs nothing from the Attendance workspace.
 * Reading the list is for Admin, HR Manager, HR Officer and Auditor; adding, changing, testing, syncing and keys need
 * attendance.bulk_import (AttendanceController).
 */
export function AttendanceDevicesPanel({ onDirtyChange }: { onDirtyChange?: (dirty: boolean) => void }) {
  const { hasPermission } = useAuth();
  const canManageSources = hasPermission('attendance.bulk_import');
  const [devices, setDevices] = useState<AttendanceDevice[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadFailed, setLoadFailed] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [showAddDevice, setShowAddDevice] = useState(false);
  const [deviceForm, setDeviceForm] = useState<DeviceFormState>(emptyDeviceForm);
  const [editingDevice, setEditingDevice] = useState<AttendanceDevice | null>(null);
  const [editDeviceForm, setEditDeviceForm] = useState<DeviceFormState>(emptyDeviceForm);
  const [deleteConfirmDevice, setDeleteConfirmDevice] = useState<AttendanceDevice | null>(null);
  const [deviceKeyResult, setDeviceKeyResult] = useState<DeviceKeyResult | null>(null);
  const [syncLogsDevice, setSyncLogsDevice] = useState<AttendanceDevice | null>(null);
  const [syncLogs, setSyncLogs] = useState<AttendanceDeviceSyncLog[]>([]);
  const [syncLogsLoading, setSyncLogsLoading] = useState(false);
  const [syncLogsError, setSyncLogsError] = useState('');

  // An open device form is unsaved work: Setup asks before the section is left.
  const formOpen = showAddDevice || editingDevice !== null;
  useLayoutEffect(() => { onDirtyChange?.(formOpen); return () => onDirtyChange?.(false); }, [formOpen, onDirtyChange]);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const page = await attendanceApi.devices.list({ pageSize: 50 });
      setDevices(page.items);
      setLoadFailed(false);
    } catch {
      setDevices([]);
      setLoadFailed(true);
    } finally {
      setLoading(false);
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

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

  const submitDevice = (event: FormEvent) => {
    event.preventDefault();
    runAction(() => attendanceApi.devices.create(toDeviceRequest(deviceForm)), 'Attendance device saved.');
    setDeviceForm(emptyDeviceForm);
    setShowAddDevice(false);
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
    setSyncLogsError('');
    try {
      const logs = await attendanceApi.devices.logs(device.id);
      setSyncLogs(logs);
    } catch {
      setSyncLogs([]);
      setSyncLogsError('Sync logs could not be loaded. This is not an empty log history.');
    } finally {
      setSyncLogsLoading(false);
    }
  };

  const devicesUnavailable = unavailableMessage('devices', loadFailed ? { devices: 'failed' } : {});

  return (
    <div className="space-y-4">
      {(error || message) && (
        <div role={error ? 'alert' : 'status'} aria-live={error ? 'assertive' : 'polite'} className={`rounded-lg border px-4 py-3 text-sm ${error ? 'border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300' : 'border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-500/20 dark:bg-emerald-500/10 dark:text-emerald-300'}`}>
          {error || message}
        </div>
      )}

      <div className="flex items-center justify-between gap-3">
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Connect any attendance source — biometric, RFID, face recognition, REST API, SFTP, or manual CSV.
        </p>
        <div className="flex shrink-0 items-center gap-2">
          {loadFailed && <button type="button" onClick={() => { void load(); }} className="btn-secondary" disabled={loading}>Retry</button>}
          {canManageSources && <button type="button" onClick={() => { setDeviceForm(emptyDeviceForm); setShowAddDevice(true); }} className="btn-primary" disabled={loadFailed}>
            <Fingerprint className="h-4 w-4" />Add Device
          </button>}
        </div>
      </div>
      <Panel title="Configured Devices" action={loading ? 'Loading…' : devicesUnavailable ? 'Unavailable' : `${devices.length} sources`}>
        {loading ? <p role="status" className="text-sm text-slate-400">Loading…</p> : devicesUnavailable ? <DomainUnavailable message={devicesUnavailable} /> : <DeviceTable
          devices={devices}
          onTest={(id) => runAction(() => attendanceApi.devices.test(id), 'Connection test completed.')}
          onSync={(id) => runAction(() => attendanceApi.devices.sync(id), 'Sync attempt completed.')}
          onEdit={openEdit}
          onDelete={setDeleteConfirmDevice}
          onGenerateKey={generateKey}
          onViewLogs={openSyncLogs}
        />}
      </Panel>

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
          {!syncLogsLoading && syncLogsError && <div role="alert"><DomainUnavailable message={syncLogsError} /></div>}
          {!syncLogsLoading && !syncLogsError && syncLogs.length === 0 && <Empty text="No sync logs found for this device." />}
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

function Field({ label, children, info, infoKey }: { label: string; children: ReactNode; info?: string; infoKey?: string }) {
  return <label className="block text-xs font-semibold text-slate-500 dark:text-slate-400"><span className="flex items-center gap-1.5">{label}{info && <InfoTip text={info} fieldKey={infoKey} />}</span><div className="mt-1">{children}</div></label>;
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
