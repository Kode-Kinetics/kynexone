'use client';

import { InfoTip } from '../components/InfoTip';
import { useAuth } from '../contexts/AuthContext';
import { EmployeePicker } from '../components/EmployeePicker';
import type { SelectedEmployee } from '../components/EmployeePicker';
import { useTenantSettings } from '../contexts/TenantSettingsContext';
import { useEffect, useState } from 'react';
import {
  AlertTriangle, BarChart2, Calculator, CheckCircle2, Clock3, FileClock,
  Layers3, Plus, RefreshCw, Settings, TimerReset, TrendingUp,
  Users, WalletCards, X, XCircle, Zap,
} from 'lucide-react';
import {
  overtimeApi,
  type OvertimePolicy, type OvertimeRequest, type OvertimeSummary,
  type OvertimeCalculation, type OvertimePayrollImpact, type OvertimeCompOffConversion,
} from '../api/overtime';

// ── Shared helpers ──────────────────────────────────────────────────────────────

const today = new Date().toISOString().slice(0, 10);
const dt = (date: string, time: string) => new Date(`${date}T${time}:00`).toISOString();

function fmtDate(s: string | null | undefined) {
  if (!s) return '—';
  return new Date(s).toLocaleDateString('en-US', { day: 'numeric', month: 'short', year: 'numeric' });
}

function fmtMins(m: number) {
  const h = Math.floor(m / 60);
  const min = m % 60;
  return h > 0 ? `${h}h ${min > 0 ? `${min}m` : ''}`.trim() : `${min}m`;
}

function fmtAmt(n: number, currency = 'USD') {
  return `${currency} ${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

const STATUS_COLOR: Record<string, string> = {
  PendingManager: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  PendingHR: 'bg-orange-50 text-orange-700 dark:bg-orange-500/10 dark:text-orange-400',
  Approved: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
  Rejected: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  Pending: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  Processed: 'bg-teal-50 text-teal-700 dark:bg-teal-500/10 dark:text-teal-400',
  PendingPayroll: 'bg-sky-50 text-sky-700 dark:bg-sky-500/10 dark:text-sky-400',
};

function StatusBadge({ status }: { status: string }) {
  const cls = STATUS_COLOR[status] ?? 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400';
  return <span className={`inline-flex rounded-md px-2 py-0.5 text-xs font-medium ${cls}`}>{status.replace(/([A-Z])/g, ' $1').trim()}</span>;
}

function KpiCard({ label, value, sub, icon: Icon, color }: {
  label: string; value: string | number; sub?: string;
  icon: React.ComponentType<{ className?: string }>; color: string;
}) {
  return (
    <div className="surface flex items-center gap-4 p-4">
      <div className={`grid h-10 w-10 shrink-0 place-items-center rounded-xl ${color}`}>
        <Icon className="h-5 w-5" />
      </div>
      <div className="min-w-0">
        <p className="text-2xl font-bold tabular-nums text-slate-900 dark:text-white">{value}</p>
        <p className="mt-0.5 truncate text-xs text-slate-500 dark:text-slate-400">{label}</p>
        {sub && <p className="text-[10px] text-slate-400 dark:text-slate-500">{sub}</p>}
      </div>
    </div>
  );
}

function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: React.ReactNode; wide?: boolean }) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className={`relative w-full rounded-2xl bg-white shadow-2xl dark:bg-slate-800 ${wide ? 'max-w-3xl' : 'max-w-lg'} max-h-[90vh] overflow-y-auto`}>
        <div className="sticky top-0 flex items-center justify-between border-b border-slate-100 bg-white px-6 py-4 dark:border-white/10 dark:bg-slate-800">
          <h2 className="text-sm font-semibold text-slate-900 dark:text-white">{title}</h2>
          <button type="button" onClick={onClose} aria-label="Close" className="rounded-lg p-1 hover:bg-slate-100 dark:hover:bg-white/10">
            <X className="h-4 w-4 text-slate-500" />
          </button>
        </div>
        <div className="p-6">{children}</div>
      </div>
    </div>
  );
}

function Field({ label, children, info, infoKey }: { label: string; children: React.ReactNode; info?: string; infoKey?: string }) {
  return (
    <div>
      <label className="mb-1 flex items-center gap-1.5 text-xs font-medium text-slate-600 dark:text-slate-300">{label}{info && <InfoTip text={info} fieldKey={infoKey} />}</label>
      {children}
    </div>
  );
}

const inp = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';
const sel = `${inp} appearance-none`;
const btn = {
  primary: 'inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-50',
  ghost: 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5',
  danger: 'inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50',
  sm: 'inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5',
};

// ── Tabs ────────────────────────────────────────────────────────────────────────

type Tab = 'dashboard' | 'submit' | 'my-ot' | 'team-ot' | 'approvals' | 'policies' | 'calc-preview' | 'payroll-review' | 'reports';

const TABS: { key: Tab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { key: 'dashboard', label: 'Dashboard', icon: BarChart2 },
  { key: 'submit', label: 'Submit OT', icon: Plus },
  { key: 'my-ot', label: 'My OT', icon: Clock3 },
  { key: 'team-ot', label: 'Team OT', icon: Users },
  { key: 'approvals', label: 'Approvals', icon: CheckCircle2 },
  { key: 'policies', label: 'Policies', icon: Settings },
  { key: 'calc-preview', label: 'Calculation Preview', icon: Calculator },
  { key: 'payroll-review', label: 'Payroll Review', icon: WalletCards },
  { key: 'reports', label: 'Reports', icon: TrendingUp },
];

// ── Dashboard Tab ───────────────────────────────────────────────────────────────

function DashboardTab({ onNavigate }: { onNavigate: (t: Tab) => void }) {
  const [summary, setSummary] = useState<OvertimeSummary | null>(null);
  const [recent, setRecent] = useState<OvertimeRequest[]>([]);
  const { currencyCode } = useTenantSettings();

  useEffect(() => {
    overtimeApi.summary().then(setSummary).catch(() => {});
    overtimeApi.requests({ pageSize: 8 }).then(r => setRecent(r.items)).catch(() => {});
  }, []);

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <KpiCard label="Total Requests" value={summary?.totalRequests ?? '—'} icon={FileClock} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
        <KpiCard label="Pending Approval" value={summary?.pendingRequests ?? '—'} icon={Clock3} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
        <KpiCard label="Approved Hours" value={summary?.approvedHours != null ? `${summary.approvedHours}h` : '—'} icon={CheckCircle2} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
        <KpiCard label="Payroll Amount" value={summary?.payrollAmount != null ? fmtAmt(summary.payrollAmount, currencyCode) : '—'} icon={WalletCards} color="bg-cyan-100 text-cyan-600 dark:bg-cyan-500/20 dark:text-cyan-400" />
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <div className="surface p-5 lg:col-span-2">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">Recent OT Requests</h3>
            <button type="button" onClick={() => onNavigate('team-ot')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">View all</button>
          </div>
          {recent.length === 0 ? <p className="text-sm text-slate-400">No overtime requests yet.</p> : (
            <div className="divide-y divide-slate-100 dark:divide-white/5">
              {recent.map(r => (
                <div key={r.id} className="flex items-center justify-between py-3">
                  <div className="min-w-0">
                    <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{r.employeeName}</p>
                    <p className="text-xs text-slate-400">{fmtDate(r.workDate)} · {fmtMins(r.requestedMinutes)} · {r.source}</p>
                  </div>
                  <StatusBadge status={r.status} />
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="surface p-5">
          <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Quick Actions</h3>
          <div className="space-y-2">
            {([
              ['Submit OT Request', 'submit', Plus],
              ['Approve Requests', 'approvals', CheckCircle2],
              ['Team OT View', 'team-ot', Users],
              ['OT Policies', 'policies', Settings],
              ['Calculation Preview', 'calc-preview', Calculator],
              ['Payroll Review', 'payroll-review', WalletCards],
            ] as [string, Tab, React.ComponentType<{ className?: string }>][]).map(([label, t, Icon]) => (
              <button key={t} type="button" onClick={() => onNavigate(t)}
                className="flex w-full items-center gap-3 rounded-lg p-2.5 text-left hover:bg-slate-50 dark:hover:bg-white/5">
                <Icon className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />
                <span className="text-sm text-slate-700 dark:text-slate-300">{label}</span>
              </button>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

// ── Submit OT Tab ───────────────────────────────────────────────────────────────

function SubmitOTTab({ selfEmployeeId, isEmployee }: { selfEmployeeId?: number; isEmployee?: boolean }) {
  const [policies, setPolicies] = useState<OvertimePolicy[]>([]);
  const [otPickedEmp, setOtPickedEmp] = useState<SelectedEmployee | null>(null);
  const [form, setForm] = useState({
    employeeId: selfEmployeeId ? String(selfEmployeeId) : '', workDate: today, startTime: '18:00', endTime: '20:00',
    reason: '', source: 'Manual', policyId: '', typeId: '',
  });
  const [saving, setSaving] = useState(false);
  const [success, setSuccess] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  useEffect(() => {
    if (otPickedEmp) setForm(f => ({ ...f, employeeId: String(otPickedEmp.id) }));
  }, [otPickedEmp]);

  useEffect(() => { overtimeApi.policies().then(setPolicies).catch(() => {}); }, []);

  const calcMinutes = () => {
    if (!form.startTime || !form.endTime) return 0;
    const start = new Date(`${form.workDate}T${form.startTime}`);
    const end = new Date(`${form.workDate}T${form.endTime}`);
    return Math.max(0, Math.round((end.getTime() - start.getTime()) / 60000));
  };

  const submit = async () => {
    if (!form.employeeId || !form.workDate || !form.startTime || !form.endTime) {
      setError('Employee ID, work date and times are required.'); return;
    }
    setSaving(true); setError('');
    try {
      await overtimeApi.createRequest({
        employeeId: Number(form.employeeId),
        overtimePolicyId: form.policyId || undefined,
        overtimeTypeId: form.typeId || undefined,
        workDate: form.workDate,
        startTimeUtc: dt(form.workDate, form.startTime),
        endTimeUtc: dt(form.workDate, form.endTime),
        source: form.source,
        reason: form.reason,
      });
      setSuccess(true);
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to submit overtime request.');
    }
    setSaving(false);
  };

  if (success) return (
    <div className="surface flex flex-col items-center py-20 text-center">
      <CheckCircle2 className="mb-3 h-12 w-12 text-emerald-500" />
      <p className="text-lg font-semibold text-slate-800 dark:text-white">Overtime Request Submitted</p>
      <p className="mt-1 text-sm text-slate-400">Your request is pending manager approval.</p>
      <button type="button" className={`mt-6 ${btn.primary}`} onClick={() => { setSuccess(false); setForm(f => ({ ...f, reason: '', employeeId: selfEmployeeId ? String(selfEmployeeId) : '' })); }}>Submit Another</button>
    </div>
  );

  const mins = calcMinutes();

  return (
    <div className="max-w-xl space-y-5">
      <div className="surface p-5">
        <p className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Employee & Date</p>
        <div className="space-y-3">
          {isEmployee ? (
            <p className="text-sm text-slate-500 dark:text-slate-400">Submitting overtime for: <span className="font-medium text-slate-800 dark:text-white">Employee #{form.employeeId}</span></p>
          ) : (
            <EmployeePicker label="Employee *" required value={otPickedEmp} onChange={setOtPickedEmp} placeholder="Search by name or employee code…" />
          )}
          <div className="grid grid-cols-2 gap-3">
            <Field label="Work Date *" info="The day the overtime was actually worked — not the date you submit the request." infoKey="overtime.work_date"><input type="date" aria-label="Work date" className={inp} value={form.workDate} onChange={e => set('workDate', e.target.value)} /></Field>
            <Field label="Start Time *" info="When the overtime began, in 24-hour time (e.g. 18:00 = 6pm)." infoKey="overtime.start_time"><input type="time" aria-label="Start time" className={inp} value={form.startTime} onChange={e => set('startTime', e.target.value)} /></Field>
            <Field label="End Time *"><input type="time" aria-label="End time" className={inp} value={form.endTime} onChange={e => set('endTime', e.target.value)} /></Field>
          </div>
          {mins > 0 && (
            <div className="rounded-lg bg-sapphire/5 px-4 py-2.5 dark:bg-sapphire/10">
              <p className="text-sm text-sapphire dark:text-cyanAccent">Duration: <strong>{fmtMins(mins)}</strong> ({Math.round(mins / 60 * 100) / 100} hours)</p>
            </div>
          )}
        </div>
      </div>

      <div className="surface p-5">
        <p className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Policy & Type</p>
        <div className="grid grid-cols-2 gap-3">
          <Field label="OT Policy" info="The overtime policy that sets the pay multiplier (e.g. 1.25x weekday, 1.5x holiday). Configured in the Policies tab." infoKey="overtime.policy">
            <select aria-label="OT Policy" className={sel} value={form.policyId} onChange={e => set('policyId', e.target.value)}>
              <option value="">Auto-select default</option>
              {policies.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </Field>
          <Field label="Source">
            <select aria-label="Source" className={sel} value={form.source} onChange={e => set('source', e.target.value)}>
              {['Manual', 'Project', 'Emergency', 'Shift'].map(s => <option key={s}>{s}</option>)}
            </select>
          </Field>
        </div>
        <div className="mt-3">
          <Field label="Reason">
            <textarea className={inp} rows={3} value={form.reason} onChange={e => set('reason', e.target.value)} placeholder="Reason for overtime…" />
          </Field>
        </div>
      </div>

      {error && <p className="rounded-lg bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-400">{error}</p>}
      <button type="button" className={btn.primary} onClick={submit} disabled={saving}>{saving ? 'Submitting…' : 'Submit OT Request'}</button>
    </div>
  );
}

// ── Requests List (reusable) ────────────────────────────────────────────────────

function OTRequestsTable({ requests, onApprove, onReject, showActions }: {
  requests: OvertimeRequest[];
  onApprove?: (r: OvertimeRequest) => void;
  onReject?: (r: OvertimeRequest) => void;
  showActions?: boolean;
}) {
  if (requests.length === 0) return (
    <div className="flex flex-col items-center py-16 text-center">
      <FileClock className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
      <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No overtime records found</p>
    </div>
  );

  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[640px] text-sm">
        <thead>
          <tr className="border-b border-slate-100 dark:border-white/[0.07]">
            {['Employee', 'Date', 'Requested', 'Approved', 'Source', 'Status', ...(showActions ? ['Actions'] : [])].map(h => (
              <th key={h} className="px-3 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
          {requests.map(r => (
            <tr key={r.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
              <td className="px-3 py-2 font-medium text-slate-900 dark:text-white">{r.employeeName || `Emp #${r.employeeId}`}</td>
              <td className="px-3 py-2 text-slate-500">{r.workDate}</td>
              <td className="px-3 py-2 text-slate-500">{fmtMins(r.requestedMinutes)}</td>
              <td className="px-3 py-2 text-slate-500">{r.approvedMinutes > 0 ? fmtMins(r.approvedMinutes) : '—'}</td>
              <td className="px-3 py-2 text-slate-500">{r.source}</td>
              <td className="px-3 py-2"><StatusBadge status={r.status} /></td>
              {showActions && (
                <td className="px-3 py-2">
                  {r.status.startsWith('Pending') && onApprove && onReject ? (
                    <div className="flex gap-2">
                      <button type="button" onClick={() => onApprove(r)} className={`${btn.sm} text-emerald-600`}><CheckCircle2 className="h-3 w-3" /> Approve</button>
                      <button type="button" onClick={() => onReject(r)} className={`${btn.sm} text-rose-500`}><XCircle className="h-3 w-3" /> Reject</button>
                    </div>
                  ) : <span className="text-xs text-slate-400">—</span>}
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ── My OT Tab ───────────────────────────────────────────────────────────────────

function MyOTTab({ selfEmployeeId, isEmployee }: { selfEmployeeId?: number; isEmployee?: boolean }) {
  const [requests, setRequests] = useState<OvertimeRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [empId, setEmpId] = useState(selfEmployeeId ? String(selfEmployeeId) : '');
  const [myOtFilterEmp, setMyOtFilterEmp] = useState<SelectedEmployee | null>(null);
  const [statusFilter, setStatusFilter] = useState('');

  useEffect(() => { if (myOtFilterEmp) setEmpId(String(myOtFilterEmp.id)); }, [myOtFilterEmp]);

  const load = () => {
    setLoading(true);
    const eid = (isEmployee && selfEmployeeId) ? selfEmployeeId : (empId ? Number(empId) : undefined);
    overtimeApi.requests({ employeeId: eid, status: statusFilter || undefined, pageSize: 100 })
      .then(r => { setRequests(r.items); setLoading(false); })
      .catch(() => setLoading(false));
  };
  useEffect(load, [statusFilter]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        {!isEmployee && <div className="w-60"><EmployeePicker value={myOtFilterEmp} onChange={emp => { setMyOtFilterEmp(emp); if (!emp) setEmpId(''); }} placeholder="Filter by employee…" /></div>}
        <select aria-label="Filter by status" className={`${sel} w-48`} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
          <option value="">All Statuses</option>
          {['PendingManager', 'PendingHR', 'Approved', 'Rejected'].map(s => <option key={s} value={s}>{s.replace(/([A-Z])/g, ' $1').trim()}</option>)}
        </select>
        {!isEmployee && <button type="button" className={btn.primary} onClick={load}>Search</button>}
        <p className="ml-auto text-sm text-slate-400">{requests.length} request{requests.length !== 1 ? 's' : ''}</p>
      </div>
      <div className="surface overflow-hidden">
        {loading ? <p className="p-8 text-center text-sm text-slate-400">Loading…</p> : <OTRequestsTable requests={requests} />}
      </div>
    </div>
  );
}

// ── Team OT Tab ─────────────────────────────────────────────────────────────────

function TeamOTTab() {
  const [requests, setRequests] = useState<OvertimeRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');

  const load = () => {
    setLoading(true);
    overtimeApi.requests({ status: statusFilter || undefined, pageSize: 200 })
      .then(r => { setRequests(r.items); setLoading(false); })
      .catch(() => setLoading(false));
  };
  useEffect(load, [statusFilter]);

  const approve = async (r: OvertimeRequest) => {
    try { await overtimeApi.approve(r.id, r.requestedMinutes, 'Approved'); load(); } catch { alert('Approval failed.'); }
  };
  const reject = async (r: OvertimeRequest) => {
    const notes = prompt('Rejection reason:');
    if (notes === null) return;
    try { await overtimeApi.reject(r.id, notes); load(); } catch { alert('Rejection failed.'); }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <select aria-label="Filter by status" className={`${sel} w-48`} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
          <option value="">All Statuses</option>
          {['PendingManager', 'PendingHR', 'Approved', 'Rejected'].map(s => <option key={s} value={s}>{s.replace(/([A-Z])/g, ' $1').trim()}</option>)}
        </select>
        <button type="button" className={btn.ghost} onClick={() => overtimeApi.detectFromAttendance(today, today).then(load).catch(() => {})}>
          <RefreshCw className="h-4 w-4" /> Detect from Attendance
        </button>
        <p className="ml-auto text-sm text-slate-400">{requests.length} request{requests.length !== 1 ? 's' : ''}</p>
      </div>
      <div className="surface overflow-hidden">
        {loading ? <p className="p-8 text-center text-sm text-slate-400">Loading…</p> : <OTRequestsTable requests={requests} onApprove={approve} onReject={reject} showActions />}
      </div>
    </div>
  );
}

// ── Approvals Tab ───────────────────────────────────────────────────────────────

function ApprovalsTab({ isAdmin, isHRManager, isManager }: { isAdmin: boolean; isHRManager: boolean; isManager: boolean }) {
  const isFinalApprover = isAdmin || isHRManager;
  const [requests, setRequests] = useState<OvertimeRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [approveModal, setApproveModal] = useState<OvertimeRequest | null>(null);
  const [approveMinutes, setApproveMinutes] = useState('');
  const [approveNotes, setApproveNotes] = useState('');
  const [rejectModal, setRejectModal] = useState<OvertimeRequest | null>(null);
  const [rejectNotes, setRejectNotes] = useState('');
  const [saving, setSaving] = useState(false);

  const load = async () => {
    setLoading(true);
    try {
      if (isAdmin) {
        const [r1, r2] = await Promise.all([
          overtimeApi.requests({ status: 'PendingManager', pageSize: 100 }),
          overtimeApi.requests({ status: 'PendingHR', pageSize: 100 }),
        ]);
        setRequests([...r1.items, ...r2.items].sort((a, b) => (a.workDate < b.workDate ? 1 : -1)));
      } else {
        const status = isHRManager ? 'PendingHR' : 'PendingManager';
        const r = await overtimeApi.requests({ status, pageSize: 100 });
        setRequests(r.items);
      }
    } catch { /* ignore */ }
    setLoading(false);
  };
  useEffect(() => { load(); }, []);

  const openApprove = (r: OvertimeRequest) => {
    setApproveModal(r);
    setApproveMinutes(String(r.requestedMinutes));
    setApproveNotes('');
  };

  const doApprove = async () => {
    if (!approveModal) return;
    setSaving(true);
    try {
      const mins = isFinalApprover ? (Number(approveMinutes) || approveModal.requestedMinutes) : approveModal.requestedMinutes;
      await overtimeApi.approve(approveModal.id, mins, approveNotes || undefined);
      setApproveModal(null);
      load();
    } catch { alert('Approval failed.'); }
    setSaving(false);
  };

  const doReject = async () => {
    if (!rejectModal) return;
    setSaving(true);
    try {
      await overtimeApi.reject(rejectModal.id, rejectNotes || undefined);
      setRejectModal(null);
      load();
    } catch { alert('Rejection failed.'); }
    setSaving(false);
  };

  const queueTitle = isAdmin ? 'All Pending Approvals' : isHRManager ? 'Pending HR Approval — Step 2' : 'Pending Your Approval — Step 1';
  const queueSubtitle = isAdmin
    ? 'As admin, approving any request finalises it immediately, bypassing the standard HR step.'
    : isHRManager
    ? 'These requests have been forwarded by a manager. Adjust hours if needed, then approve or reject.'
    : 'Approving forwards the request to HR for final review and payroll calculation.';

  return (
    <div className="space-y-4">
      <div>
        <p className="text-sm font-semibold text-slate-800 dark:text-white">{queueTitle}</p>
        <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{queueSubtitle}</p>
      </div>

      {loading ? (
        <p className="text-sm text-slate-400">Loading…</p>
      ) : requests.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <CheckCircle2 className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">
            {isHRManager ? 'No overtime awaiting HR approval' : 'No pending overtime approvals'}
          </p>
        </div>
      ) : (
        <div className="space-y-3">
          {requests.map(r => (
            <div key={r.id} className="surface p-5">
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName || `Employee #${r.employeeId}`}</p>
                    <StatusBadge status={r.status} />
                    {isAdmin && (
                      <span className={`rounded px-1.5 py-0.5 text-[10px] font-bold ${r.status === 'PendingManager' ? 'bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400' : 'bg-orange-100 text-orange-600 dark:bg-orange-500/20 dark:text-orange-400'}`}>
                        {r.status === 'PendingManager' ? 'Step 1 — Manager' : 'Step 2 — HR'}
                      </span>
                    )}
                  </div>
                  <div className="mt-1.5 flex flex-wrap items-center gap-3">
                    <span className="text-xs text-slate-500">{fmtDate(r.workDate)}</span>
                    <span className="rounded bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-700 dark:bg-white/10 dark:text-slate-300">
                      {fmtMins(r.requestedMinutes)}
                    </span>
                    <span className="text-xs text-slate-500">{r.source}</span>
                  </div>
                  {r.reason && <p className="mt-2 text-xs text-slate-500">"{r.reason}"</p>}
                </div>
                <div className="flex shrink-0 gap-2">
                  <button type="button" className={btn.ghost} onClick={() => { setRejectModal(r); setRejectNotes(''); }}>Reject</button>
                  <button type="button" className={btn.primary} onClick={() => openApprove(r)}>
                    {isFinalApprover ? 'Approve' : 'Forward to HR'}
                  </button>
                </div>
              </div>
            </div>
          ))}
        </div>
      )}

      {approveModal && (
        <Modal
          title={`${isFinalApprover ? 'Approve OT' : 'Forward to HR'} — ${approveModal.employeeName || `#${approveModal.employeeId}`}`}
          onClose={() => setApproveModal(null)}
        >
          <div className="space-y-4">
            {isFinalApprover ? (
              <>
                <Field label="Approved Minutes" info="Adjust if the actual overtime differs from what was requested.">
                  <input type="number" aria-label="Approved minutes" className={inp} value={approveMinutes} onChange={e => setApproveMinutes(e.target.value)} />
                </Field>
                <p className="text-xs text-slate-400">
                  Requested: {fmtMins(approveModal.requestedMinutes)} · Will approve: {fmtMins(Number(approveMinutes) || approveModal.requestedMinutes)}
                </p>
                {isAdmin && approveModal.status === 'PendingManager' && (
                  <div className="rounded-lg bg-blue-50 px-4 py-3 text-xs text-blue-700 dark:bg-blue-500/10 dark:text-blue-400">
                    Admin override — this will skip the manager→HR chain and approve immediately.
                  </div>
                )}
              </>
            ) : (
              <div className="rounded-lg bg-amber-50 px-4 py-3 dark:bg-amber-500/10">
                <p className="text-sm font-medium text-amber-700 dark:text-amber-400">Forwarding to HR for final approval</p>
                <p className="mt-1 text-xs text-amber-600 dark:text-amber-500">The HR team will review, set the final approved hours, and trigger payroll calculation.</p>
              </div>
            )}
            <Field label="Notes (optional)">
              <textarea className={inp} rows={2} value={approveNotes} onChange={e => setApproveNotes(e.target.value)} placeholder="Add notes for the next reviewer…" />
            </Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setApproveModal(null)}>Cancel</button>
              <button type="button" className={btn.primary} onClick={doApprove} disabled={saving}>
                {saving ? 'Processing…' : isFinalApprover ? 'Approve & Calculate' : 'Forward to HR'}
              </button>
            </div>
          </div>
        </Modal>
      )}

      {rejectModal && (
        <Modal title={`Reject OT — ${rejectModal.employeeName || `#${rejectModal.employeeId}`}`} onClose={() => setRejectModal(null)}>
          <div className="space-y-4">
            <Field label="Rejection Reason *">
              <textarea className={inp} rows={3} value={rejectNotes} onChange={e => setRejectNotes(e.target.value)} placeholder="Reason for rejection…" />
            </Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setRejectModal(null)}>Cancel</button>
              <button type="button" className={btn.danger} onClick={doReject} disabled={saving}>{saving ? 'Rejecting…' : 'Reject'}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Policies Tab ────────────────────────────────────────────────────────────────

function CreatePolicyModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState({
    code: '', name: '', hourlyRateBasis: 'BasicSalary', fixedHourlyRate: 0,
    standardMonthlyHours: 240, minimumMinutes: 30, maximumMinutesPerDay: 240,
    monthlyCapMinutes: 3600, roundingRule: 'Nearest15',
    requiresApproval: true, allowCompOffConversion: true,
    regularDayMultiplier: 1.25, weekendMultiplier: 1.5, holidayMultiplier: 2.0,
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string | number | boolean) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!form.code || !form.name) { setError('Code and name are required.'); return; }
    setSaving(true); setError('');
    try { await overtimeApi.createPolicy(form); onSaved(); } catch { setError('Save failed.'); setSaving(false); }
  };

  return (
    <Modal title="New Overtime Policy" onClose={onClose} wide>
      <div className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Code *"><input aria-label="Policy code" className={inp} value={form.code} onChange={e => set('code', e.target.value)} placeholder="e.g. GCC-OT-STANDARD" /></Field>
          <Field label="Name *"><input aria-label="Policy name" className={inp} value={form.name} onChange={e => set('name', e.target.value)} placeholder="e.g. Standard GCC Overtime" /></Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Hourly Rate Basis" info="How the base hourly rate is derived: from basic salary, gross salary, or a fixed amount you enter below." infoKey="overtime.rate_basis">
            <select aria-label="Hourly rate basis" className={sel} value={form.hourlyRateBasis} onChange={e => set('hourlyRateBasis', e.target.value)}>
              {['BasicSalary', 'GrossSalary', 'FixedHourlyRate'].map(b => <option key={b}>{b}</option>)}
            </select>
          </Field>
          {form.hourlyRateBasis === 'FixedHourlyRate' && (
            <Field label="Fixed Hourly Rate"><input type="number" step="0.01" aria-label="Fixed hourly rate" className={inp} value={form.fixedHourlyRate} onChange={e => set('fixedHourlyRate', Number(e.target.value))} /></Field>
          )}
          <Field label="Standard Monthly Hours"><input type="number" aria-label="Standard monthly hours" className={inp} value={form.standardMonthlyHours} onChange={e => set('standardMonthlyHours', Number(e.target.value))} /></Field>
        </div>

        <div className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
          <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">OT Multipliers (× Hourly Rate)</p>
          <div className="grid grid-cols-3 gap-3">
            <Field label="Regular Day"><input type="number" step="0.05" aria-label="Regular day multiplier" className={inp} value={form.regularDayMultiplier} onChange={e => set('regularDayMultiplier', Number(e.target.value))} /></Field>
            <Field label="Weekend"><input type="number" step="0.05" aria-label="Weekend multiplier" className={inp} value={form.weekendMultiplier} onChange={e => set('weekendMultiplier', Number(e.target.value))} /></Field>
            <Field label="Public Holiday"><input type="number" step="0.05" aria-label="Public holiday multiplier" className={inp} value={form.holidayMultiplier} onChange={e => set('holidayMultiplier', Number(e.target.value))} /></Field>
          </div>
        </div>

        <div className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
          <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">Caps & Rules</p>
          <div className="grid grid-cols-3 gap-3">
            <Field label="Min Minutes"><input type="number" aria-label="Minimum minutes" className={inp} value={form.minimumMinutes} onChange={e => set('minimumMinutes', Number(e.target.value))} /></Field>
            <Field label="Max Min/Day"><input type="number" aria-label="Maximum minutes per day" className={inp} value={form.maximumMinutesPerDay} onChange={e => set('maximumMinutesPerDay', Number(e.target.value))} /></Field>
            <Field label="Monthly Cap (min)"><input type="number" aria-label="Monthly cap in minutes" className={inp} value={form.monthlyCapMinutes} onChange={e => set('monthlyCapMinutes', Number(e.target.value))} /></Field>
          </div>
          <div className="mt-3 grid grid-cols-2 gap-2">
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form.requiresApproval} onChange={e => set('requiresApproval', e.target.checked)} className="rounded" />
              Requires Manager Approval
            </label>
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form.allowCompOffConversion} onChange={e => set('allowCompOffConversion', e.target.checked)} className="rounded" />
              Allow Comp-Off Conversion
            </label>
          </div>
        </div>

        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Saving…' : 'Create Policy'}</button>
        </div>
      </div>
    </Modal>
  );
}

function PoliciesTab() {
  const [policies, setPolicies] = useState<OvertimePolicy[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);

  const load = () => { setLoading(true); overtimeApi.policies().then(setPolicies).catch(() => {}).finally(() => setLoading(false)); };
  useEffect(load, []);

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" /> New Policy</button>
      </div>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : policies.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Settings className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No overtime policies yet</p>
        </div>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {policies.map(p => (
            <div key={p.id} className="surface p-4">
              <TimerReset className="mb-3 h-5 w-5 text-sapphire dark:text-cyanAccent" />
              <p className="font-semibold text-slate-900 dark:text-white">{p.name}</p>
              <p className="mt-1 text-xs text-slate-500">{p.code}</p>
              <div className="mt-3 space-y-1 text-xs text-slate-500">
                <p>Basis: <span className="font-medium text-slate-700 dark:text-slate-300">{p.hourlyRateBasis}</span></p>
                <p>Standard hours: <span className="font-medium text-slate-700 dark:text-slate-300">{p.standardMonthlyHours}h/month</span></p>
                <p>Min: <span className="font-medium text-slate-700 dark:text-slate-300">{p.minimumMinutes} min</span> · Max/day: <span className="font-medium text-slate-700 dark:text-slate-300">{p.maximumMinutesPerDay} min</span></p>
                <p>Monthly cap: <span className="font-medium text-slate-700 dark:text-slate-300">{fmtMins(p.monthlyCapMinutes)}</span></p>
              </div>
              <div className="mt-3 flex gap-1.5">
                {p.requiresApproval && <span className="rounded bg-amber-50 px-1.5 py-0.5 text-[10px] font-medium text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">Approval Req.</span>}
                {p.allowCompOffConversion && <span className="rounded bg-teal-50 px-1.5 py-0.5 text-[10px] font-medium text-teal-700 dark:bg-teal-500/10 dark:text-teal-400">Comp-Off</span>}
                <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${p.isActive ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400'}`}>{p.isActive ? 'Active' : 'Inactive'}</span>
              </div>
            </div>
          ))}
        </div>
      )}
      {showCreate && <CreatePolicyModal onClose={() => setShowCreate(false)} onSaved={() => { setShowCreate(false); load(); }} />}
    </div>
  );
}

// ── Calculation Preview Tab ─────────────────────────────────────────────────────

function CalcPreviewTab() {
  const [calculations, setCalculations] = useState<OvertimeCalculation[]>([]);
  const [empId, setEmpId] = useState('');
  const [calcPickedEmp, setCalcPickedEmp] = useState<SelectedEmployee | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => { if (calcPickedEmp) setEmpId(String(calcPickedEmp.id)); }, [calcPickedEmp]);

  const search = () => {
    setLoading(true);
    overtimeApi.calculations(empId ? Number(empId) : undefined)
      .then(setCalculations).catch(() => {}).finally(() => setLoading(false));
  };

  useEffect(() => { search(); }, []);

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <div className="w-64"><EmployeePicker value={calcPickedEmp} onChange={emp => { setCalcPickedEmp(emp); if (!emp) setEmpId(''); }} placeholder="Filter by employee (optional)…" /></div>
        <button type="button" className={btn.primary} onClick={search}>Search</button>
      </div>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : calculations.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Calculator className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No calculations yet — approve overtime requests to see calculations</p>
        </div>
      ) : (
        <div className="surface overflow-hidden">
          <table className="w-full min-w-[640px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Employee', 'Hours', 'Hourly Rate', 'Multiplier', 'Amount', 'Currency', 'Calculated'].map(h => (
                  <th key={h} className="px-3 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {calculations.map(c => (
                <tr key={c.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-3 py-2 text-slate-700 dark:text-slate-300">Emp #{c.employeeId}</td>
                  <td className="px-3 py-2 font-medium text-slate-900 dark:text-white">{c.approvedHours}h</td>
                  <td className="px-3 py-2 text-slate-500">{c.hourlyRate.toFixed(2)}</td>
                  <td className="px-3 py-2 text-slate-500">×{c.multiplier}</td>
                  <td className="px-3 py-2 font-bold text-emerald-600 dark:text-emerald-400">{c.amount.toFixed(2)}</td>
                  <td className="px-3 py-2 text-slate-500">{c.currency}</td>
                  <td className="px-3 py-2 text-xs text-slate-400">{fmtDate(c.createdAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="surface p-4">
        <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Formula Reference</p>
        <div className="rounded-lg bg-slate-50 p-4 font-mono text-sm text-slate-700 dark:bg-white/5 dark:text-slate-300">
          OT Pay = Approved Hours × Hourly Rate × Multiplier
          <br /><br />
          <span className="text-xs text-slate-400">
            Hourly Rate = (Basic Salary or Gross) ÷ Standard Monthly Hours<br />
            Multiplier: Regular Day = 1.25× · Weekend = 1.5× · Public Holiday = 2.0×
          </span>
        </div>
      </div>
    </div>
  );
}

// ── Payroll Review Tab ──────────────────────────────────────────────────────────

function PayrollReviewTab() {
  const [impacts, setImpacts] = useState<OvertimePayrollImpact[]>([]);
  const [conversions, setConversions] = useState<OvertimeCompOffConversion[]>([]);
  const [loading, setLoading] = useState(true);
  const { currencyCode } = useTenantSettings();

  useEffect(() => {
    Promise.all([
      overtimeApi.payrollReview().then(setImpacts).catch(() => {}),
      overtimeApi.compOffConversions().then(setConversions).catch(() => {}),
    ]).finally(() => setLoading(false));
  }, []);

  const total = impacts.reduce((sum, x) => sum + x.amount, 0);

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-3 gap-4">
        <KpiCard label="Pending Payroll Items" value={impacts.length} icon={WalletCards} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
        <KpiCard label="Total OT Amount" value={fmtAmt(total, currencyCode)} icon={TrendingUp} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
        <KpiCard label="Comp-Off Pending" value={conversions.filter(c => c.status === 'Pending').length} icon={Layers3} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
      </div>

      <div className="surface overflow-hidden">
        <div className="border-b border-slate-100 px-5 py-3 dark:border-white/5">
          <p className="text-sm font-semibold text-slate-800 dark:text-white">Overtime Pending Payroll Import</p>
          <p className="text-xs text-slate-400">These approved OT amounts will be included in the next payroll run.</p>
        </div>
        {loading ? <p className="p-8 text-center text-sm text-slate-400">Loading…</p> : impacts.length === 0 ? (
          <p className="p-8 text-center text-sm text-slate-400">No overtime pending payroll import.</p>
        ) : (
          <table className="w-full min-w-[480px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Employee', 'OT Hours', 'Amount', 'Status', 'Created'].map(h => (
                  <th key={h} className="px-4 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {impacts.map(i => (
                <tr key={i.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-2 font-medium text-slate-900 dark:text-white">Emp #{i.employeeId}</td>
                  <td className="px-4 py-2 text-slate-500">{i.hours}h</td>
                  <td className="px-4 py-2 font-semibold text-emerald-600 dark:text-emerald-400">{i.amount.toFixed(2)}</td>
                  <td className="px-4 py-2"><StatusBadge status={i.status} /></td>
                  <td className="px-4 py-2 text-xs text-slate-400">{fmtDate(i.createdAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {conversions.length > 0 && (
        <div className="surface overflow-hidden">
          <div className="border-b border-slate-100 px-5 py-3 dark:border-white/5">
            <p className="text-sm font-semibold text-slate-800 dark:text-white">Comp-Off Conversions</p>
          </div>
          <table className="w-full min-w-[480px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Employee', 'OT Hours', 'Comp-Off Days', 'Status'].map(h => (
                  <th key={h} className="px-4 py-2 text-left text-xs font-bold uppercase text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {conversions.map(c => (
                <tr key={c.id}>
                  <td className="px-4 py-2 font-medium text-slate-900 dark:text-white">Emp #{c.employeeId}</td>
                  <td className="px-4 py-2 text-slate-500">{c.overtimeHours}h</td>
                  <td className="px-4 py-2 text-slate-500">{c.compOffDays}d</td>
                  <td className="px-4 py-2"><StatusBadge status={c.status} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Reports Tab ─────────────────────────────────────────────────────────────────

function ReportsTab() {
  const [summary, setSummary] = useState<OvertimeSummary | null>(null);
  const { currencyCode } = useTenantSettings();
  const [from, setFrom] = useState(() => {
    const d = new Date(); d.setDate(1); return d.toISOString().slice(0, 10);
  });
  const [to, setTo] = useState(today);

  const load = () => { overtimeApi.summary(from, to).then(setSummary).catch(() => {}); };
  useEffect(load, []);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center gap-3">
        <Field label="From"><input type="date" aria-label="From date" className={`${inp} w-44`} value={from} onChange={e => setFrom(e.target.value)} /></Field>
        <Field label="To"><input type="date" aria-label="To date" className={`${inp} w-44`} value={to} onChange={e => setTo(e.target.value)} /></Field>
        <button type="button" className={`${btn.primary} mt-4`} onClick={load}>Run Report</button>
      </div>

      {summary && (
        <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
          <KpiCard label="Total Requests" value={summary.totalRequests} icon={FileClock} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
          <KpiCard label="Approved" value={summary.approvedRequests} icon={CheckCircle2} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
          <KpiCard label="Pending" value={summary.pendingRequests} icon={Clock3} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
          <KpiCard label="Approved Hours" value={`${summary.approvedHours}h`} icon={TrendingUp} color="bg-cyan-100 text-cyan-600 dark:bg-cyan-500/20 dark:text-cyan-400" />
        </div>
      )}

      <div className="surface p-5">
        <p className="mb-3 text-sm font-semibold text-slate-800 dark:text-white">OT Payroll Amount</p>
        {summary ? (
          <p className="text-3xl font-bold tabular-nums text-emerald-600 dark:text-emerald-400">
            {fmtAmt(summary.payrollAmount, currencyCode)}
          </p>
        ) : (
          <p className="text-sm text-slate-400">Run the report to see figures.</p>
        )}
        <p className="mt-2 text-xs text-slate-400">Total approved overtime payroll amount for the selected period.</p>
      </div>

      <div className="surface p-5">
        <div className="flex items-start gap-3">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-500" />
          <div>
            <p className="text-sm font-semibold text-slate-800 dark:text-white">OT Anomaly Alerts</p>
            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
              If approved overtime payout exceeds 35% of regular gross earnings for any employee, the payroll engine flags this as a warning. Review the Payroll Validation tab after processing.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}

// ── Main Page ───────────────────────────────────────────────────────────────────

export function OvertimePage() {
  const { user } = useAuth();
  const { currencyCode } = useTenantSettings();
  const isAdmin    = user?.roles.some(r => r === 'Admin') ?? false;
  const isHRManager = user?.roles.some(r => r === 'HR Manager') ?? false;
  const isManager  = !isAdmin && !isHRManager && (user?.roles.some(r => ['Manager', 'Supervisor'].includes(r)) ?? false);
  const isEmployee = !isAdmin && !isHRManager && !isManager;
  const selfEmployeeId = (user as { employeeId?: number } | undefined)?.employeeId;

  const [activeTab, setActiveTab] = useState<Tab>('dashboard');

  const visibleTabs = TABS.filter(t => {
    if (isEmployee) return ['dashboard', 'submit', 'my-ot'].includes(t.key);
    if (isManager)  return ['dashboard', 'submit', 'my-ot', 'team-ot', 'approvals'].includes(t.key);
    return true; // HR Manager & Admin see all tabs
  });

  const roleBadge = isAdmin
    ? <span className="rounded-md bg-blue-100 px-2 py-0.5 text-xs font-semibold text-blue-700 dark:bg-blue-500/15 dark:text-blue-400">Admin</span>
    : isHRManager
    ? <span className="rounded-md bg-purple-100 px-2 py-0.5 text-xs font-semibold text-purple-700 dark:bg-purple-500/15 dark:text-purple-400">HR Manager</span>
    : isManager
    ? <span className="rounded-md bg-amber-100 px-2 py-0.5 text-xs font-semibold text-amber-700 dark:bg-amber-500/15 dark:text-amber-400">Manager</span>
    : <span className="rounded-md bg-emerald-100 px-2 py-0.5 text-xs font-semibold text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-400">Employee</span>;

  const renderTab = () => {
    switch (activeTab) {
      case 'dashboard':     return <DashboardTab onNavigate={setActiveTab} />;
      case 'submit':        return <SubmitOTTab selfEmployeeId={selfEmployeeId} isEmployee={isEmployee} />;
      case 'my-ot':         return <MyOTTab selfEmployeeId={selfEmployeeId} isEmployee={isEmployee} />;
      case 'team-ot':       return <TeamOTTab />;
      case 'approvals':     return <ApprovalsTab isAdmin={isAdmin} isHRManager={isHRManager} isManager={isManager} />;
      case 'policies':      return <PoliciesTab />;
      case 'calc-preview':  return <CalcPreviewTab />;
      case 'payroll-review': return <PayrollReviewTab />;
      case 'reports':       return <ReportsTab />;
    }
  };

  return (
    <div className="space-y-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Overtime Management</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
            {isEmployee   ? 'Submit and track your overtime requests.' :
             isManager    ? 'Approve team overtime and forward to HR for final review.' :
             isHRManager  ? 'Final approval, hour adjustments, calculations, and payroll review.' :
             'GCC overtime policies, approvals, calculations, and payroll integration.'}
          </p>
        </div>
        {roleBadge}
      </div>

      <div className="flex gap-1 overflow-x-auto rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/5">
        {visibleTabs.map(t => (
          <button
            key={t.key}
            type="button"
            onClick={() => setActiveTab(t.key)}
            className={`flex shrink-0 items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium transition-colors ${activeTab === t.key ? 'bg-white shadow-sm text-sapphire dark:bg-slate-800 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'}`}
          >
            <t.icon className="h-3.5 w-3.5" />
            {t.label}
          </button>
        ))}
      </div>

      <div>{renderTab()}</div>
    </div>
  );
}
