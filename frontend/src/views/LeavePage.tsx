'use client';

import { useEffect, useState } from 'react';
import { useAutoTranslate } from '../hooks/useAutoTranslate';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { EmployeePicker } from '../components/EmployeePicker';
import type { SelectedEmployee } from '../components/EmployeePicker';
import {
  Activity, AlertTriangle, BarChart2, Building2, Calendar, CheckCircle,
  ChevronRight, Clock, FileText, GitBranch, Plus, Settings, Star,
  Trash2, TrendingUp, Users, X, Zap, Shield, RefreshCw,
} from 'lucide-react';
import {
  leaveTypesApi, leavePoliciesApi, leaveBalancesApi, leaveRequestsApi,
  holidayCalendarApi, encashmentApi, compOffApi, absenceApi,
  leaveCalendarApi, leaveReportsApi, leaveAIApi,
} from '../api/leave';
import type {
  LeaveType, LeavePolicy, EmployeeLeaveBalance, LeaveRequest,
  PublicHolidayCalendar, PublicHoliday, LeaveBlackoutDate,
  LeaveEncashmentRequest, CompOffCredit, AbsenceRecord,
  LeaveCalendarEntry, LeaveAIInsight, LeaveDashboard,
} from '../api/leave';
import { ImportExportToolbar, downloadCsv } from '../components/ImportExportToolbar';
import { InfoTip } from '../components/InfoTip';
import client from '../api/client';
import { companiesApi, branchesApi } from '../api/organization';
import type { CompanyDto, BranchDto } from '../api/organization';
import { useTenantSettings } from '../contexts/TenantSettingsContext';

// ── Leave import/export helpers ───────────────────────────────────────────────

const leaveTypesImportExport = {
  export: async () => {
    const csv = await client.get<string>('/api/leave/types/export', { responseType: 'text' }).then(r => r.data);
    downloadCsv(csv, 'leave-types.csv');
  },
  template: async () => {
    const csv = await client.get<string>('/api/leave/types/import-template', { responseType: 'text' }).then(r => r.data);
    downloadCsv(csv, 'leave-types-template.csv');
  },
  import: (csvContent: string) =>
    client.post<{ received: number; created: number; skipped: number; errors: string[] }>('/api/leave/types/import', { csvContent }).then(r => r.data),
};

const leaveRequestsImportExport = {
  export: async () => {
    const csv = await client.get<string>('/api/leave/requests/export', { responseType: 'text' }).then(r => r.data);
    downloadCsv(csv, 'leave-requests.csv');
  },
  template: async () => {
    const csv = await client.get<string>('/api/leave/requests/import-template', { responseType: 'text' }).then(r => r.data);
    downloadCsv(csv, 'leave-requests-template.csv');
  },
  import: (csvContent: string) =>
    client.post<{ received: number; created: number; skipped: number; errors: string[] }>('/api/leave/requests/import', { csvContent }).then(r => r.data),
};

// ── Helpers ───────────────────────────────────────────────────────────────────

function fmtDate(s: string | null | undefined) {
  if (!s) return '—';
  return new Date(s).toLocaleDateString('en-US', { day: 'numeric', month: 'short', year: 'numeric' });
}

function fmtAmt(n: number) {
  return n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function daysBetween(start: string, end: string) {
  const d = Math.ceil((new Date(end).getTime() - new Date(start).getTime()) / 86400000) + 1;
  return Math.max(0, d);
}

const STATUS_COLORS: Record<string, string> = {
  Draft: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
  Submitted: 'bg-sky-50 text-sky-700 dark:bg-sky-500/10 dark:text-sky-400',
  PendingManagerApproval: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  PendingHRApproval: 'bg-orange-50 text-orange-700 dark:bg-orange-500/10 dark:text-orange-400',
  Approved: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
  Rejected: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  Cancelled: 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400',
  Withdrawn: 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400',
  PayrollProcessed: 'bg-teal-50 text-teal-700 dark:bg-teal-500/10 dark:text-teal-400',
  Pending: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  Active: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
  HRApproved: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400',
  Processed: 'bg-teal-50 text-teal-700 dark:bg-teal-500/10 dark:text-teal-400',
};

function StatusBadge({ status }: { status: string }) {
  const cls = STATUS_COLORS[status] ?? 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400';
  const label = status.replace(/([A-Z])/g, ' $1').trim();
  return <span className={`inline-flex rounded-md px-2 py-0.5 text-xs font-medium ${cls}`}>{label}</span>;
}

// ── Shared UI ─────────────────────────────────────────────────────────────────

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

function Modal({ title, onClose, children, wide }: {
  title: string; onClose: () => void; children: React.ReactNode; wide?: boolean;
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className={`relative w-full rounded-2xl bg-white shadow-2xl dark:bg-slate-800 ${wide ? 'max-w-3xl' : 'max-w-lg'} max-h-[90vh] overflow-y-auto`}>
        <div className="sticky top-0 flex items-center justify-between border-b border-slate-100 bg-white px-6 py-4 dark:border-white/10 dark:bg-slate-800">
          <h2 className="text-sm font-semibold text-slate-900 dark:text-white">{title}</h2>
          <button type="button" onClick={onClose} className="rounded-lg p-1 hover:bg-slate-100 dark:hover:bg-white/10">
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

function LeaveColorDot({ color }: { color: string }) {
  return <span className="inline-block h-2.5 w-2.5 shrink-0 rounded-full" style={{ backgroundColor: color }} />;
}

// ── Group / Company Context ───────────────────────────────────────────────────

export interface GroupFilter {
  companyId?: string;
  branchId?: string;
}

function GroupContextBar({
  companies, companyId, onCompanyChange,
  branches, branchId, onBranchChange,
}: {
  companies: CompanyDto[];
  companyId: string;
  onCompanyChange: (id: string) => void;
  branches: BranchDto[];
  branchId: string;
  onBranchChange: (id: string) => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2 rounded-xl border border-sapphire/20 bg-sapphire/5 px-4 py-2.5 dark:border-cyanAccent/20 dark:bg-cyanAccent/5">
      <div className="flex items-center gap-1.5 text-xs font-medium text-sapphire dark:text-cyanAccent">
        <Building2 className="h-3.5 w-3.5" />
        <span>Group</span>
      </div>
      <ChevronRight className="h-3.5 w-3.5 text-slate-300 dark:text-white/20" />
      <select
        title="Filter by company"
        className={`${sel} w-52 !border-sapphire/30 !bg-white dark:!bg-white/10`}
        value={companyId}
        onChange={e => { onCompanyChange(e.target.value); onBranchChange(''); }}
      >
        <option value="">All Group Companies</option>
        {companies.map(c => (
          <option key={c.id} value={c.id}>{c.legalNameEn || c.tradeName}</option>
        ))}
      </select>
      {companyId && branches.length > 0 && (
        <>
          <ChevronRight className="h-3.5 w-3.5 text-slate-300 dark:text-white/20" />
          <div className="flex items-center gap-1.5 text-xs text-slate-500 dark:text-slate-400">
            <GitBranch className="h-3.5 w-3.5" />
          </div>
          <select
            title="Filter by branch"
            className={`${sel} w-44 !border-sapphire/30 !bg-white dark:!bg-white/10`}
            value={branchId}
            onChange={e => onBranchChange(e.target.value)}
          >
            <option value="">All Branches</option>
            {branches.map(b => (
              <option key={b.id} value={b.id}>{b.nameEn}</option>
            ))}
          </select>
        </>
      )}
      {(companyId || branchId) && (
        <button
          type="button"
          className="ml-1 text-xs text-slate-400 underline hover:text-slate-600 dark:text-slate-500 dark:hover:text-slate-300"
          onClick={() => { onCompanyChange(''); onBranchChange(''); }}
        >
          Clear
        </button>
      )}
      <span className="ml-auto text-[11px] text-slate-400 dark:text-slate-500">
        {companyId
          ? (branchId
            ? branches.find(b => b.id === branchId)?.nameEn ?? 'Branch'
            : companies.find(c => c.id === companyId)?.legalNameEn ?? 'Company')
          : `${companies.length} compan${companies.length !== 1 ? 'ies' : 'y'} in group`}
      </span>
    </div>
  );
}

// ── Dashboard Tab ─────────────────────────────────────────────────────────────

function DashboardTab({ onNavigate, groupFilter = {} }: { onNavigate: (tab: Tab) => void; groupFilter?: GroupFilter }) {
  const [dash, setDash] = useState<LeaveDashboard | null>(null);
  const [onLeave, setOnLeave] = useState<LeaveCalendarEntry[]>([]);
  const [pending, setPending] = useState<LeaveRequest[]>([]);

  useEffect(() => {
    leaveReportsApi.dashboard(groupFilter).then(setDash).catch(() => {});
    leaveCalendarApi.today().then(data => setOnLeave(Array.isArray(data) ? data : [])).catch(() => {});
    leaveRequestsApi.list({ status: 'PendingManagerApproval', ...groupFilter })
      .then(r => setPending(Array.isArray(r?.items) ? r.items.slice(0, 6) : []))
      .catch(() => {});
  }, [groupFilter.companyId, groupFilter.branchId]);

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <KpiCard label="On Leave Today" value={dash?.onLeaveToday ?? onLeave.length} icon={Users} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
        <KpiCard label="Pending Approvals" value={dash?.pendingApprovals ?? pending.length} icon={Clock} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
        <KpiCard label="Unauthorized Absences" value={dash?.unauthorizedAbsences ?? '—'} icon={AlertTriangle} color="bg-rose-100 text-rose-600 dark:bg-rose-500/20 dark:text-rose-400" />
        <KpiCard label="Pending Encashments" value={dash?.pendingEncashments ?? '—'} icon={TrendingUp} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <div className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">On Leave Today</h3>
            <button type="button" onClick={() => onNavigate('calendar')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">Calendar</button>
          </div>
          {onLeave.length === 0 ? (
            <p className="text-sm text-slate-400">No employees on leave today.</p>
          ) : (
            <div className="space-y-3">
              {onLeave.slice(0, 6).map((e, i) => (
                <div key={i} className="flex items-center gap-3">
                  <LeaveColorDot color={e.colorCode || '#2F6BFF'} />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">{e.employeeName}</p>
                    <p className="text-xs text-slate-400">{e.departmentName} · {e.leaveTypeName}</p>
                  </div>
                  <span className="text-xs tabular-nums text-slate-400">{e.totalDays}d</span>
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">Pending Approvals</h3>
            <button type="button" onClick={() => onNavigate('approvals')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">View all</button>
          </div>
          {pending.length === 0 ? (
            <p className="text-sm text-slate-400">No pending approvals.</p>
          ) : (
            <div className="space-y-3">
              {pending.map(r => (
                <div key={r.id} className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <p className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">{r.employeeName}</p>
                    <p className="text-xs text-slate-400">{r.leaveTypeName} · {fmtDate(r.startDate)} – {fmtDate(r.endDate)}</p>
                  </div>
                  <span className="shrink-0 text-xs font-semibold text-amber-600 dark:text-amber-400">{r.totalDays}d</span>
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="surface p-5">
          <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Quick Actions</h3>
          <div className="space-y-2">
            {([
              ['Apply for Leave', 'apply', Plus],
              ['Pending Approvals', 'approvals', CheckCircle],
              ['Team Calendar', 'calendar', Calendar],
              ['Leave Balance', 'balance', BarChart2],
              ['Absence Regularization', 'absences', RefreshCw],
              ['Leave Insights', 'ai-insights', Zap],
            ] as [string, Tab, React.ComponentType<{ className?: string }>][]).map(([label, t, Icon]) => (
              <button key={t} type="button" onClick={() => onNavigate(t)}
                className="flex w-full items-center gap-3 rounded-lg p-2.5 text-left hover:bg-slate-50 dark:hover:bg-white/5">
                <Icon className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />
                <span className="text-sm text-slate-700 dark:text-slate-300">{label}</span>
                <ChevronRight className="ml-auto h-3.5 w-3.5 text-slate-300" />
              </button>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

// ── Balance Tab ───────────────────────────────────────────────────────────────

function BalanceTab({ selfEmployeeId, groupFilter = {} }: { selfEmployeeId?: number; groupFilter?: GroupFilter }) {
  const [balances, setBalances] = useState<EmployeeLeaveBalance[]>([]);
  const [loading, setLoading] = useState(true);
  const [empId, setEmpId] = useState(selfEmployeeId ? String(selfEmployeeId) : '');
  const [balancePickedEmp, setBalancePickedEmp] = useState<SelectedEmployee | null>(null);
  const [year, setYear] = useState(new Date().getFullYear());

  useEffect(() => {
    if (balancePickedEmp) setEmpId(String(balancePickedEmp.id));
  }, [balancePickedEmp]);
  const [adjustModal, setAdjustModal] = useState<EmployeeLeaveBalance | null>(null);
  const [adjAmount, setAdjAmount] = useState('');
  const [adjReason, setAdjReason] = useState('');

  const load = () => {
    setLoading(true);
    leaveBalancesApi.list({ employeeId: empId ? Number(empId) : undefined, year, ...groupFilter })
      .then(setBalances).catch(() => {}).finally(() => setLoading(false));
  };
  useEffect(load, [year, groupFilter.companyId, groupFilter.branchId]);

  const adjust = async () => {
    if (!adjustModal || !adjReason) return;
    try {
      await leaveBalancesApi.adjust({ employeeId: adjustModal.employeeId, leaveTypeId: adjustModal.leaveTypeId, year, amount: Number(adjAmount), reason: adjReason });
      setAdjustModal(null); setAdjAmount(''); setAdjReason(''); load();
    } catch { alert('Adjustment failed.'); }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        {!selfEmployeeId && (
          <div className="w-64">
            <EmployeePicker value={balancePickedEmp} onChange={emp => { setBalancePickedEmp(emp); if (emp) setEmpId(String(emp.id)); }} placeholder="Search employee…" />
          </div>
        )}
        <select className={`${sel} w-32`} value={year} onChange={e => setYear(Number(e.target.value))}>
          {[year - 1, year, year + 1].map(y => <option key={y} value={y}>{y}</option>)}
        </select>
        {!selfEmployeeId && <button type="button" className={btn.primary} onClick={load}>Search</button>}
        <p className="ml-auto text-sm text-slate-400">{balances.length} balance{balances.length !== 1 ? 's' : ''}</p>
      </div>

      {loading ? <p className="text-sm text-slate-400">Loading…</p> : balances.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <BarChart2 className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No balance records found</p>
        </div>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {balances.map(b => {
            const available = b.entitled + b.accrued + b.carriedForward + b.manualAdjustment - b.used - b.pending - b.encashed;
            const pct = b.entitled > 0 ? Math.min(100, (b.used / b.entitled) * 100) : 0;
            return (
              <div key={b.id} className="surface p-5">
                <div className="mb-3 flex items-start justify-between gap-2">
                  <div>
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{b.leaveTypeName}</p>
                    <p className="text-xs text-slate-400">{b.employeeName} · {b.year}</p>
                  </div>
                  <button type="button" className={btn.sm} onClick={() => { setAdjustModal(b); setAdjAmount('0'); setAdjReason(''); }}>Adjust</button>
                </div>
                <div className="mb-3 h-2 rounded-full bg-slate-100 dark:bg-white/10">
                  <div className={`h-2 rounded-full transition-all ${pct > 90 ? 'bg-rose-400' : pct > 70 ? 'bg-amber-400' : 'bg-emerald-500'}`} style={{ width: `${pct}%` }} />
                </div>
                <div className="grid grid-cols-3 gap-2 text-center">
                  {[
                    { label: 'Entitled', val: b.entitled },
                    { label: 'Used', val: b.used },
                    { label: 'Available', val: available },
                    { label: 'Pending', val: b.pending },
                    { label: 'Carried Fwd', val: b.carriedForward },
                    { label: 'Encashed', val: b.encashed },
                  ].map(x => (
                    <div key={x.label} className="rounded-lg bg-slate-50 px-2 py-1.5 dark:bg-white/5">
                      <p className={`text-base font-bold tabular-nums ${x.label === 'Available' ? 'text-sapphire dark:text-cyanAccent' : 'text-slate-700 dark:text-slate-300'}`}>{x.val}</p>
                      <p className="text-[10px] text-slate-400">{x.label}</p>
                    </div>
                  ))}
                </div>
              </div>
            );
          })}
        </div>
      )}

      {adjustModal && (
        <Modal title={`Adjust Balance — ${adjustModal.leaveTypeName} (${adjustModal.employeeName})`} onClose={() => setAdjustModal(null)}>
          <div className="space-y-4">
            <Field label="Adjustment Amount (use negative to deduct)">
              <input type="number" step="0.5" className={inp} value={adjAmount} onChange={e => setAdjAmount(e.target.value)} />
            </Field>
            <Field label="Reason (required for audit trail) *">
              <textarea className={inp} rows={3} value={adjReason} onChange={e => setAdjReason(e.target.value)} placeholder="State the reason for this balance adjustment…" />
            </Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setAdjustModal(null)}>Cancel</button>
              <button type="button" className={btn.primary} onClick={adjust}>Apply Adjustment</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Apply Leave Tab ───────────────────────────────────────────────────────────

function ApplyLeaveTab({ selfEmployeeId, isEmployee = false }: { selfEmployeeId?: number; isEmployee?: boolean }) {
  const [leaveTypes, setLeaveTypes] = useState<LeaveType[]>([]);
  const [pickedEmployee, setPickedEmployee] = useState<SelectedEmployee | null>(null);
  const [delegatePicked, setDelegatePicked] = useState<SelectedEmployee | null>(null);
  const [form, setForm] = useState({
    employeeId: selfEmployeeId ? String(selfEmployeeId) : '', employeeName: '', departmentName: '', designationTitle: '',
    leaveTypeId: '', startDate: '', endDate: '', dayType: 'Full',
    hoursRequested: '', reason: '', isEmergency: false,
    delegateEmployeeId: '', delegateEmployeeName: '',
  });

  // Sync picker selection into form fields
  useEffect(() => {
    if (pickedEmployee) {
      setForm(f => ({
        ...f,
        employeeId: String(pickedEmployee.id),
        employeeName: pickedEmployee.fullName,
        departmentName: pickedEmployee.department,
        designationTitle: pickedEmployee.designation,
      }));
    }
  }, [pickedEmployee]);

  useEffect(() => {
    if (delegatePicked) {
      setForm(f => ({ ...f, delegateEmployeeId: String(delegatePicked.id), delegateEmployeeName: delegatePicked.fullName }));
    }
  }, [delegatePicked]);
  const [balance, setBalance] = useState<EmployeeLeaveBalance | null>(null);
  const [saving, setSaving] = useState(false);
  const [success, setSuccess] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string | boolean) => setForm(f => ({ ...f, [k]: v }));

  useEffect(() => { leaveTypesApi.list().then(setLeaveTypes).catch(() => {}); }, []);

  useEffect(() => {
    if (!form.employeeId || !form.leaveTypeId) { setBalance(null); return; }
    leaveBalancesApi.list({ employeeId: Number(form.employeeId), leaveTypeId: form.leaveTypeId, year: new Date().getFullYear() })
      .then(bs => setBalance(bs[0] ?? null)).catch(() => setBalance(null));
  }, [form.employeeId, form.leaveTypeId]);

  const requestedDays = form.startDate && form.endDate ? daysBetween(form.startDate, form.endDate) : 0;
  const available = balance ? (balance.entitled + balance.accrued + balance.carriedForward + balance.manualAdjustment - balance.used - balance.pending - balance.encashed) : null;
  const selectedType = leaveTypes.find(t => t.id === form.leaveTypeId);

  const submit = async () => {
    if (!form.employeeId || !form.leaveTypeId || !form.startDate || !form.endDate) {
      setError('Employee ID, Leave Type, and dates are required.'); return;
    }
    setSaving(true); setError('');
    try {
      await leaveRequestsApi.create({
        employeeId: Number(form.employeeId), employeeName: form.employeeName,
        departmentName: form.departmentName, designationTitle: form.designationTitle,
        leaveTypeId: form.leaveTypeId, startDate: form.startDate, endDate: form.endDate,
        dayType: form.dayType, hoursRequested: form.hoursRequested ? Number(form.hoursRequested) : 0,
        reason: form.reason, isEmergency: form.isEmergency,
        delegateEmployeeId: form.delegateEmployeeId ? Number(form.delegateEmployeeId) : undefined,
        delegateEmployeeName: form.delegateEmployeeName,
      });
      setSuccess(true);
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to submit request.');
    }
    setSaving(false);
  };

  if (success) return (
    <div className="surface flex flex-col items-center py-20 text-center">
      <CheckCircle className="mb-3 h-12 w-12 text-emerald-500" />
      <p className="text-lg font-semibold text-slate-800 dark:text-white">Leave Request Submitted</p>
      <p className="mt-1 text-sm text-slate-400">Your request is pending manager approval.</p>
      <button type="button" className={`mt-6 ${btn.primary}`} onClick={() => { setSuccess(false); setForm(f => ({ ...f, startDate: '', endDate: '', reason: '' })); }}>Apply Another</button>
    </div>
  );

  return (
    <div className="max-w-2xl space-y-5">
      <div className="surface p-5">
        <p className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Employee</p>
        {isEmployee ? (
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Applying on behalf of: <span className="font-medium text-slate-800 dark:text-white">{form.employeeName || `Employee #${form.employeeId}`}</span>
          </p>
        ) : (
          <div className="space-y-3">
            <EmployeePicker
              label="Employee *"
              required
              value={pickedEmployee}
              onChange={setPickedEmployee}
              placeholder="Search by name or employee code…"
            />
            {pickedEmployee && (
              <div className="grid grid-cols-2 gap-3 text-xs text-slate-500 dark:text-slate-400">
                <div><span className="font-medium">Department:</span> {pickedEmployee.department || '—'}</div>
                <div><span className="font-medium">Designation:</span> {pickedEmployee.designation || '—'}</div>
              </div>
            )}
          </div>
        )}
      </div>

      <div className="surface p-5">
        <p className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Leave Details</p>
        <div className="space-y-4">
          <Field label="Leave Type *" info="The category of leave (annual, sick, etc.). Determines the balance used, whether a reason or attachment is required, and the approval flow." infoKey="leave.leave_type">
            <select className={sel} value={form.leaveTypeId} onChange={e => set('leaveTypeId', e.target.value)}>
              <option value="">Select leave type…</option>
              {leaveTypes.map(t => <option key={t.id} value={t.id}>{t.nameEn}{!t.isPaid ? ' (Unpaid)' : ''}</option>)}
            </select>
          </Field>

          {balance !== null && (
            <div className={`rounded-lg border p-3 ${available !== null && available < requestedDays ? 'border-rose-200 bg-rose-50 dark:border-rose-500/20 dark:bg-rose-500/10' : 'border-emerald-200 bg-emerald-50 dark:border-emerald-500/20 dark:bg-emerald-500/10'}`}>
              <div className="flex items-center justify-between">
                <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Available Balance</span>
                <span className={`text-lg font-bold tabular-nums ${available !== null && available < requestedDays ? 'text-rose-600 dark:text-rose-400' : 'text-emerald-600 dark:text-emerald-400'}`}>{available?.toFixed(2)} days</span>
              </div>
              {selectedType && !selectedType.isPaid && (
                <p className="mt-1 text-xs text-amber-700 dark:text-amber-400">⚠ This is unpaid leave — payroll deduction will apply.</p>
              )}
            </div>
          )}

          <div className="grid grid-cols-2 gap-3">
            <Field label="Start Date *" info="First day of leave (inclusive). Must be on or before the end date." infoKey="leave.start_date"><input type="date" className={inp} value={form.startDate} onChange={e => set('startDate', e.target.value)} /></Field>
            <Field label="End Date *" info="Last day of leave (inclusive). Total days are calculated automatically from the policy and day type." infoKey="leave.end_date"><input type="date" className={inp} value={form.endDate} onChange={e => set('endDate', e.target.value)} /></Field>
          </div>

          {requestedDays > 0 && (
            <div className="rounded-lg bg-slate-50 px-4 py-3 dark:bg-white/5">
              <p className="text-sm text-slate-600 dark:text-slate-400">Duration: <strong className="text-slate-900 dark:text-white">{requestedDays} calendar day{requestedDays !== 1 ? 's' : ''}</strong> (exact working days per policy calculated server-side)</p>
            </div>
          )}

          <Field label="Day Type" info="Full day, half day or hourly leave. Half day and hourly count as partial days against the balance." infoKey="leave.day_type">
            <select className={sel} value={form.dayType} onChange={e => set('dayType', e.target.value)}>
              <option value="Full">Full Day</option>
              {selectedType?.isHalfDayAllowed && <><option value="Half-AM">Half Day (Morning)</option><option value="Half-PM">Half Day (Afternoon)</option></>}
              {selectedType?.isHourlyAllowed && <option value="Hourly">Hourly</option>}
            </select>
          </Field>
          {form.dayType === 'Hourly' && (
            <Field label="Hours Requested"><input type="number" step="0.5" min="1" max="8" className={inp} value={form.hoursRequested} onChange={e => set('hoursRequested', e.target.value)} /></Field>
          )}

          <Field label={`Reason${selectedType?.requiresReason ? ' *' : ''}`}>
            <textarea className={inp} rows={3} value={form.reason} onChange={e => set('reason', e.target.value)} placeholder={selectedType?.requiresReason ? 'Reason is required for this leave type…' : 'Optional reason…'} />
          </Field>

          {selectedType?.requiresAttachment && (
            <div className="rounded-lg border border-dashed border-slate-300 p-4 text-center text-sm text-slate-400 dark:border-white/20">
              This leave type requires a supporting document. Upload after submission via the request detail.
            </div>
          )}

          <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
            <input type="checkbox" checked={form.isEmergency} onChange={e => set('isEmergency', e.target.checked)} className="rounded" />
            Emergency leave request
          </label>
        </div>
      </div>

      <div className="surface p-5">
        <p className="mb-1 text-sm font-semibold text-slate-800 dark:text-white">Delegation (optional)</p>
        <p className="mb-3 text-xs text-slate-400">Assign a colleague to handle responsibilities during your absence.</p>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Delegate Employee">
            <EmployeePicker value={delegatePicked} onChange={setDelegatePicked} placeholder="Search delegate employee…" />
          </Field>
          <Field label="Delegate Name"><input className={inp} value={form.delegateEmployeeName} onChange={e => set('delegateEmployeeName', e.target.value)} /></Field>
        </div>
      </div>

      {error && <p className="rounded-lg bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-400">{error}</p>}
      <button type="button" className={btn.primary} onClick={submit} disabled={saving}>{saving ? 'Submitting…' : 'Submit Leave Request'}</button>
    </div>
  );
}

// ── My Requests Tab ───────────────────────────────────────────────────────────

function MyRequestsTab() {
  const [requests, setRequests] = useState<LeaveRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [cancelModal, setCancelModal] = useState<LeaveRequest | null>(null);
  const [cancelReason, setCancelReason] = useState('');

  const load = () => {
    setLoading(true);
    leaveRequestsApi.list({ status: statusFilter || undefined }).then(r => { setRequests(r.items); setLoading(false); }).catch(() => setLoading(false));
  };
  useEffect(load, [statusFilter]);

  const withdraw = async (id: string) => { try { await leaveRequestsApi.withdraw(id); load(); } catch { alert('Withdrawal failed.'); } };
  const cancel = async () => {
    if (!cancelModal || !cancelReason) return;
    try { await leaveRequestsApi.cancel(cancelModal.id, cancelReason); setCancelModal(null); load(); } catch { alert('Cancellation failed.'); }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <select className={`${sel} w-56`} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
          <option value="">All Statuses</option>
          {['Draft', 'Submitted', 'PendingManagerApproval', 'PendingHRApproval', 'Approved', 'Rejected', 'Cancelled', 'Withdrawn', 'PayrollProcessed'].map(s => (
            <option key={s} value={s}>{s.replace(/([A-Z])/g, ' $1').trim()}</option>
          ))}
        </select>
        <p className="text-sm text-slate-400">{requests.length} request{requests.length !== 1 ? 's' : ''}</p>
        <div className="ml-auto">
          <ImportExportToolbar
            entityName="Leave Requests"
            onExport={leaveRequestsImportExport.export}
            onDownloadTemplate={leaveRequestsImportExport.template}
            onImport={leaveRequestsImportExport.import}
          />
        </div>
      </div>

      {loading ? <p className="text-sm text-slate-400">Loading…</p> : requests.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <FileText className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No leave requests</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {requests.map(r => (
            <div key={r.id} className="px-5 py-4">
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.leaveTypeName}</p>
                    {r.isEmergency && <span className="rounded bg-rose-100 px-1.5 py-0.5 text-[10px] font-bold text-rose-600 dark:bg-rose-500/20 dark:text-rose-400">EMERGENCY</span>}
                  </div>
                  <p className="mt-0.5 text-xs text-slate-400">{fmtDate(r.startDate)} – {fmtDate(r.endDate)} · {r.totalDays} day{r.totalDays !== 1 ? 's' : ''}</p>
                  {r.rejectionReason && <p className="mt-1 text-xs text-rose-600 dark:text-rose-400">Rejected: {r.rejectionReason}</p>}
                  {r.reason && <p className="mt-1 text-xs text-slate-500 dark:text-slate-400 line-clamp-1">{r.reason}</p>}
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <StatusBadge status={r.status} />
                  {['Submitted', 'PendingManagerApproval'].includes(r.status) && (
                    <button type="button" className={btn.sm} onClick={() => withdraw(r.id)}>Withdraw</button>
                  )}
                  {r.status === 'Approved' && (
                    <button type="button" className={btn.sm} onClick={() => { setCancelModal(r); setCancelReason(''); }}>Cancel</button>
                  )}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}

      {cancelModal && (
        <Modal title="Cancel Leave Request" onClose={() => setCancelModal(null)}>
          <div className="space-y-4">
            <p className="text-sm text-slate-600 dark:text-slate-400">Cancelling approved leave requires manager review.</p>
            <Field label="Cancellation Reason *">
              <textarea className={inp} rows={3} value={cancelReason} onChange={e => setCancelReason(e.target.value)} />
            </Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setCancelModal(null)}>Back</button>
              <button type="button" className={btn.danger} onClick={cancel}>Request Cancellation</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Approvals Tab ─────────────────────────────────────────────────────────────

function ApprovalsTab({ groupFilter = {} }: { groupFilter?: GroupFilter }) {
  const [requests, setRequests] = useState<LeaveRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [rejectModal, setRejectModal] = useState<LeaveRequest | null>(null);
  const [rejectReason, setRejectReason] = useState('');

  const load = () => {
    setLoading(true);
    leaveRequestsApi.list({ status: 'PendingManagerApproval', ...groupFilter }).then(r => { setRequests(r.items); setLoading(false); }).catch(() => setLoading(false));
  };
  useEffect(load, [groupFilter.companyId, groupFilter.branchId]);

  const approve = async (id: string) => { try { await leaveRequestsApi.approve(id); load(); } catch { alert('Approval failed.'); } };
  const reject = async () => {
    if (!rejectModal || !rejectReason) return;
    try { await leaveRequestsApi.reject(rejectModal.id, rejectReason); setRejectModal(null); load(); } catch { alert('Rejection failed.'); }
  };

  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-500 dark:text-slate-400">{requests.length} pending approval{requests.length !== 1 ? 's' : ''}</p>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : requests.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <CheckCircle className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No pending approvals</p>
        </div>
      ) : (
        <div className="space-y-3">
          {requests.map(r => (
            <div key={r.id} className="surface p-5">
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0">
                  <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                  <p className="text-xs text-slate-400">{r.departmentName} · {r.designationTitle}</p>
                  <div className="mt-2 flex flex-wrap items-center gap-3">
                    <span className="rounded bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-700 dark:bg-white/10 dark:text-slate-300">{r.leaveTypeName}</span>
                    <span className="text-xs text-slate-500">{fmtDate(r.startDate)} – {fmtDate(r.endDate)}</span>
                    <span className="text-xs font-semibold text-sapphire dark:text-cyanAccent">{r.totalDays} day{r.totalDays !== 1 ? 's' : ''}</span>
                    {r.isEmergency && <span className="rounded bg-rose-100 px-1.5 py-0.5 text-[10px] font-bold text-rose-600 dark:bg-rose-500/20 dark:text-rose-400">EMERGENCY</span>}
                  </div>
                  {r.reason && <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">"{r.reason}"</p>}
                </div>
                <div className="flex shrink-0 flex-col items-end gap-2">
                  <p className="text-xs text-slate-400">Submitted {fmtDate(r.submittedAtUtc)}</p>
                  <div className="flex gap-2">
                    <button type="button" className={btn.ghost} onClick={() => { setRejectModal(r); setRejectReason(''); }}>Reject</button>
                    <button type="button" className={btn.primary} onClick={() => approve(r.id)}>Approve</button>
                  </div>
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
      {rejectModal && (
        <Modal title={`Reject Leave — ${rejectModal.employeeName}`} onClose={() => setRejectModal(null)}>
          <div className="space-y-4">
            <Field label="Rejection Reason *">
              <textarea className={inp} rows={3} value={rejectReason} onChange={e => setRejectReason(e.target.value)} placeholder="Provide a clear reason for rejection…" />
            </Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setRejectModal(null)}>Cancel</button>
              <button type="button" className={btn.danger} onClick={reject}>Reject Request</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Calendar Tab ──────────────────────────────────────────────────────────────

function CalendarTab({ groupFilter = {} }: { groupFilter?: GroupFilter }) {
  const today = new Date();
  const [month, setMonth] = useState(today.getMonth());
  const [calYear, setCalYear] = useState(today.getFullYear());
  const [entries, setEntries] = useState<LeaveCalendarEntry[]>([]);
  const [dept, setDept] = useState('');

  const firstDay = new Date(calYear, month, 1);
  const lastDay = new Date(calYear, month + 1, 0);
  const fromDate = firstDay.toISOString().split('T')[0];
  const toDate = lastDay.toISOString().split('T')[0];

  useEffect(() => {
    leaveCalendarApi.entries({ fromDate, toDate, departmentName: dept || undefined, ...groupFilter }).then(setEntries).catch(() => {});
  }, [month, calYear, dept, groupFilter.companyId, groupFilter.branchId]);

  const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  const startPad = firstDay.getDay();
  const totalCells = startPad + lastDay.getDate();
  const cells = Array.from({ length: Math.ceil(totalCells / 7) * 7 }, (_, i) => {
    const dayNum = i - startPad + 1;
    return dayNum >= 1 && dayNum <= lastDay.getDate() ? dayNum : null;
  });

  const entriesForDay = (day: number) => {
    const d = `${calYear}-${String(month + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
    return entries.filter(e => e.startDate <= d && e.endDate >= d && e.status === 'Approved');
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <button type="button" className={btn.ghost} onClick={() => { const d = new Date(calYear, month - 1); setMonth(d.getMonth()); setCalYear(d.getFullYear()); }}>‹ Prev</button>
        <p className="text-sm font-semibold text-slate-800 dark:text-white">{new Date(calYear, month).toLocaleDateString('en-US', { month: 'long', year: 'numeric' })}</p>
        <button type="button" className={btn.ghost} onClick={() => { const d = new Date(calYear, month + 1); setMonth(d.getMonth()); setCalYear(d.getFullYear()); }}>Next ›</button>
        <input className={`${inp} w-48`} placeholder="Filter by department…" value={dept} onChange={e => setDept(e.target.value)} />
      </div>

      <div className="surface overflow-hidden">
        <div className="grid grid-cols-7 border-b border-slate-100 dark:border-white/5">
          {DAYS.map(d => <div key={d} className="py-2 text-center text-xs font-semibold text-slate-400">{d}</div>)}
        </div>
        <div className="grid grid-cols-7">
          {cells.map((day, i) => {
            const isToday = day !== null && day === today.getDate() && month === today.getMonth() && calYear === today.getFullYear();
            const dayEntries = day !== null ? entriesForDay(day) : [];
            return (
              <div key={i} className={`min-h-[80px] border-b border-r border-slate-100 p-1.5 dark:border-white/5 ${!day ? 'bg-slate-50/50 dark:bg-white/[0.02]' : ''}`}>
                {day && (
                  <>
                    <span className={`text-xs font-medium ${isToday ? 'flex h-5 w-5 items-center justify-center rounded-full bg-sapphire text-white' : 'text-slate-600 dark:text-slate-400'}`}>{day}</span>
                    <div className="mt-1 space-y-0.5">
                      {dayEntries.slice(0, 3).map((e, j) => (
                        <div key={j} className="truncate rounded px-1 py-0.5 text-[10px] font-medium text-white" style={{ backgroundColor: e.colorCode || '#2F6BFF' }}>{e.employeeName.split(' ')[0]}</div>
                      ))}
                      {dayEntries.length > 3 && <p className="text-[9px] text-slate-400">+{dayEntries.length - 3} more</p>}
                    </div>
                  </>
                )}
              </div>
            );
          })}
        </div>
      </div>

      {entries.length > 0 && (
        <div className="surface p-4">
          <p className="mb-2 text-xs font-semibold text-slate-500 dark:text-slate-400">Approved leaves this month:</p>
          <div className="flex flex-wrap gap-3">
            {entries.filter(e => e.status === 'Approved').slice(0, 12).map((e, i) => (
              <div key={i} className="flex items-center gap-1.5">
                <LeaveColorDot color={e.colorCode || '#2F6BFF'} />
                <span className="text-xs text-slate-600 dark:text-slate-300">{e.employeeName} ({e.leaveTypeName}, {e.totalDays}d)</span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// ── Leave Types Tab ───────────────────────────────────────────────────────────

function CreateLeaveTypeModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState({ code: '', nameEn: '', nameAr: '', category: 'Annual', isPaid: true, isHalfDayAllowed: true, isHourlyAllowed: false, requiresAttachment: false, requiresReason: false, maxConsecutiveDays: 0, colorCode: '#2F6BFF', sortOrder: 0 });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string | number | boolean) => setForm(f => ({ ...f, [k]: v }));
  const { translation: autoLeaveNameAr, isTranslating: translatingLeaveNameAr } = useAutoTranslate(form.nameEn);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (autoLeaveNameAr && !form.nameAr) set('nameAr', autoLeaveNameAr); }, [autoLeaveNameAr]);

  const save = async () => {
    if (!form.code || !form.nameEn) { setError('Code and English name are required.'); return; }
    setSaving(true); setError('');
    try { await leaveTypesApi.create(form); onSaved(); } catch { setError('Save failed.'); setSaving(false); }
  };

  return (
    <Modal title="New Leave Type" onClose={onClose}>
      <div className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Code *"><input className={inp} value={form.code} onChange={e => set('code', e.target.value)} placeholder="e.g. ANNUAL" /></Field>
          <Field label="Category">
            <select className={sel} value={form.category} onChange={e => set('category', e.target.value)}>
              {['Annual', 'Sick', 'Emergency', 'Unpaid', 'Maternity', 'Paternity', 'Marriage', 'Bereavement', 'Hajj', 'Study', 'Compensatory', 'WFH', 'Custom'].map(c => <option key={c}>{c}</option>)}
            </select>
          </Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Name (English) *"><input className={inp} value={form.nameEn} onChange={e => set('nameEn', e.target.value)} /></Field>
          <Field label="Name (Arabic)"><input className={inp} dir="rtl" value={form.nameAr} onChange={e => set('nameAr', e.target.value)} placeholder={translatingLeaveNameAr && !form.nameAr ? 'Translating…' : undefined} /></Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Max Consecutive Days (0=unlimited)"><input type="number" min={0} className={inp} value={form.maxConsecutiveDays} onChange={e => set('maxConsecutiveDays', Number(e.target.value))} /></Field>
          <Field label="Colour"><input type="color" className="h-10 w-full rounded-lg border border-slate-200 dark:border-white/10" value={form.colorCode} onChange={e => set('colorCode', e.target.value)} /></Field>
        </div>
        <div className="grid grid-cols-2 gap-2">
          {([['isPaid', 'Paid Leave'], ['isHalfDayAllowed', 'Half-Day Allowed'], ['isHourlyAllowed', 'Hourly Allowed'], ['requiresAttachment', 'Requires Attachment'], ['requiresReason', 'Requires Reason']] as [keyof typeof form, string][]).map(([k, l]) => (
            <label key={k} className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form[k] as boolean} onChange={e => set(k, e.target.checked)} className="rounded" />{l}
            </label>
          ))}
        </div>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Saving…' : 'Create Leave Type'}</button>
        </div>
      </div>
    </Modal>
  );
}

function LeaveTypesTab() {
  const [types, setTypes] = useState<LeaveType[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const load = () => { setLoading(true); leaveTypesApi.list().then(setTypes).catch(() => {}).finally(() => setLoading(false)); };
  useEffect(load, []);
  const del = async (id: string) => { if (!confirm('Deactivate this leave type?')) return; try { await leaveTypesApi.delete(id); load(); } catch { alert('Failed.'); } };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-end gap-2">
        <ImportExportToolbar
          entityName="Leave Types"
          onExport={leaveTypesImportExport.export}
          onDownloadTemplate={leaveTypesImportExport.template}
          onImport={leaveTypesImportExport.import}
        />
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" /> New Leave Type</button>
      </div>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {types.map(t => (
            <div key={t.id} className="surface p-4">
              <div className="mb-3 flex items-start justify-between gap-2">
                <div className="flex items-center gap-2">
                  <LeaveColorDot color={t.colorCode} />
                  <div>
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{t.nameEn}</p>
                    {t.nameAr && <p className="text-xs text-slate-400" dir="rtl">{t.nameAr}</p>}
                  </div>
                </div>
                <button type="button" className={btn.sm} onClick={() => del(t.id)}>Remove</button>
              </div>
              <div className="flex flex-wrap gap-1.5">
                <span className="rounded bg-slate-100 px-2 py-0.5 text-[10px] font-medium text-slate-600 dark:bg-white/10 dark:text-slate-400">{t.category}</span>
                <span className={`rounded px-2 py-0.5 text-[10px] font-medium ${t.isPaid ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400'}`}>{t.isPaid ? 'Paid' : 'Unpaid'}</span>
                {t.isHalfDayAllowed && <span className="rounded bg-blue-50 px-2 py-0.5 text-[10px] font-medium text-blue-600 dark:bg-blue-500/10 dark:text-blue-400">Half-Day</span>}
                {t.isHourlyAllowed && <span className="rounded bg-purple-50 px-2 py-0.5 text-[10px] font-medium text-purple-600 dark:bg-purple-500/10 dark:text-purple-400">Hourly</span>}
                {t.requiresAttachment && <span className="rounded bg-amber-50 px-2 py-0.5 text-[10px] font-medium text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">Attachment Req.</span>}
              </div>
              <p className="mt-2 text-xs text-slate-400">Code: {t.code}{t.maxConsecutiveDays > 0 ? ` · Max ${t.maxConsecutiveDays}d consecutive` : ''}</p>
            </div>
          ))}
        </div>
      )}
      {showCreate && <CreateLeaveTypeModal onClose={() => setShowCreate(false)} onSaved={() => { setShowCreate(false); load(); }} />}
    </div>
  );
}

// ── Policies Tab ──────────────────────────────────────────────────────────────

function PolicyModal({ leaveTypes, existing, onClose, onSaved }: { leaveTypes: LeaveType[]; existing?: LeavePolicy; onClose: () => void; onSaved: () => void }) {
  const isEdit = !!existing;
  const [form, setForm] = useState({
    name: existing?.name ?? '',
    leaveTypeId: existing?.leaveTypeId ?? '',
    countryCode: existing?.countryCode ?? 'UAE',
    departmentName: existing?.departmentName ?? '',
    grade: existing?.grade ?? '',
    employmentType: existing?.employmentType ?? '',
    contractType: existing?.contractType ?? '',
    gender: existing?.gender ?? '',
    appliesOnProbation: existing?.appliesOnProbation ?? false,
    annualEntitlementDays: existing?.annualEntitlementDays ?? 21,
    accrualMethod: existing?.accrualMethod ?? 'Monthly',
    carryForwardMax: existing?.carryForwardMax ?? 0,
    carryForwardExpiry: existing?.carryForwardExpiry ?? 0,
    encashmentAllowed: existing?.encashmentAllowed ?? false,
    encashmentMaxDays: existing?.encashmentMaxDays ?? 0,
    minimumDaysPerRequest: existing?.minimumDaysPerRequest ?? 1,
    maximumDaysPerRequest: existing?.maximumDaysPerRequest ?? 0,
    noticeRequiredDays: existing?.noticeRequiredDays ?? 0,
    weekendsIncluded: existing?.weekendsIncluded ?? false,
    publicHolidaysIncluded: existing?.publicHolidaysIncluded ?? false,
    payrollImpact: existing?.payrollImpact ?? 'Full',
    status: existing?.status ?? 'Draft',
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string | number | boolean) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!form.name || !form.leaveTypeId) { setError('Name and Leave Type are required.'); return; }
    setSaving(true); setError('');
    try {
      if (isEdit) {
        await leavePoliciesApi.update(existing!.id, form);
      } else {
        await leavePoliciesApi.create(form);
      }
      onSaved();
    } catch { setError('Save failed.'); setSaving(false); }
  };

  return (
    <Modal title={isEdit ? 'Edit Leave Policy' : 'New Leave Policy'} onClose={onClose} wide>
      <div className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Policy Name *"><input className={inp} value={form.name} onChange={e => set('name', e.target.value)} placeholder="e.g. UAE Annual Leave — Full-Time" /></Field>
          <Field label="Leave Type *" info="The category of leave (annual, sick, etc.). Determines the balance used, whether a reason or attachment is required, and the approval flow." infoKey="leave.leave_type">
            <select className={sel} value={form.leaveTypeId} onChange={e => set('leaveTypeId', e.target.value)}>
              <option value="">Select…</option>
              {leaveTypes.map(t => <option key={t.id} value={t.id}>{t.nameEn}</option>)}
            </select>
          </Field>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Country">
            <select className={sel} value={form.countryCode} onChange={e => set('countryCode', e.target.value)}>
              {['UAE', 'KSA', 'QAT', 'KWT', 'BHR', 'OMN', 'OTHER'].map(c => <option key={c}>{c}</option>)}
            </select>
          </Field>
          <Field label="Department (blank=all)"><input className={inp} value={form.departmentName} onChange={e => set('departmentName', e.target.value)} /></Field>
          <Field label="Grade (blank=all)"><input className={inp} value={form.grade} onChange={e => set('grade', e.target.value)} /></Field>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Employment Type">
            <select className={sel} value={form.employmentType} onChange={e => set('employmentType', e.target.value)}>
              <option value="">All</option>
              {['Full-Time', 'Part-Time', 'Contract', 'Intern'].map(t => <option key={t}>{t}</option>)}
            </select>
          </Field>
          <Field label="Contract Type"><input className={inp} value={form.contractType} onChange={e => set('contractType', e.target.value)} placeholder="All" /></Field>
          <Field label="Gender">
            <select className={sel} value={form.gender} onChange={e => set('gender', e.target.value)}>
              <option value="">All</option><option>Male</option><option>Female</option>
            </select>
          </Field>
        </div>

        <div className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
          <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">Entitlement &amp; Accrual</p>
          <div className="grid grid-cols-3 gap-3">
            <Field label="Annual Days"><input type="number" step="0.5" className={inp} value={form.annualEntitlementDays} onChange={e => set('annualEntitlementDays', Number(e.target.value))} /></Field>
            <Field label="Accrual Method">
              <select className={sel} value={form.accrualMethod} onChange={e => set('accrualMethod', e.target.value)}>
                {['Monthly', 'Yearly', 'Prorated'].map(m => <option key={m}>{m}</option>)}
              </select>
            </Field>
            <Field label="Carry-Forward Max (0=none)"><input type="number" step="0.5" className={inp} value={form.carryForwardMax} onChange={e => set('carryForwardMax', Number(e.target.value))} /></Field>
          </div>
        </div>

        <div className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
          <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">Request Rules</p>
          <div className="grid grid-cols-3 gap-3">
            <Field label="Min Days/Request"><input type="number" step="0.5" className={inp} value={form.minimumDaysPerRequest} onChange={e => set('minimumDaysPerRequest', Number(e.target.value))} /></Field>
            <Field label="Max Days/Request (0=unlimited)"><input type="number" step="0.5" className={inp} value={form.maximumDaysPerRequest} onChange={e => set('maximumDaysPerRequest', Number(e.target.value))} /></Field>
            <Field label="Notice Required (days)"><input type="number" className={inp} value={form.noticeRequiredDays} onChange={e => set('noticeRequiredDays', Number(e.target.value))} /></Field>
          </div>
          <div className="mt-3 grid grid-cols-2 gap-2">
            {([['weekendsIncluded', 'Count Weekends'], ['publicHolidaysIncluded', 'Count Public Holidays'], ['encashmentAllowed', 'Encashment Allowed'], ['appliesOnProbation', 'Applies on Probation']] as [keyof typeof form, string][]).map(([k, l]) => (
              <label key={k} className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                <input type="checkbox" checked={form[k] as boolean} onChange={e => set(k, e.target.checked)} className="rounded" />{l}
              </label>
            ))}
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <Field label="Payroll Impact">
            <select className={sel} value={form.payrollImpact} onChange={e => set('payrollImpact', e.target.value)}>
              {['Full', 'Half', 'None', 'Custom'].map(p => <option key={p}>{p}</option>)}
            </select>
          </Field>
          <Field label="Status">
            <select className={sel} value={form.status} onChange={e => set('status', e.target.value)}>
              {['Draft', 'Active', 'Inactive'].map(s => <option key={s}>{s}</option>)}
            </select>
          </Field>
        </div>

        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>Cancel</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? 'Saving…' : isEdit ? 'Save Changes' : 'Create Policy'}</button>
        </div>
      </div>
    </Modal>
  );
}

function PoliciesTab() {
  const [policies, setPolicies] = useState<LeavePolicy[]>([]);
  const [leaveTypes, setLeaveTypes] = useState<LeaveType[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('Active');
  const [showCreate, setShowCreate] = useState(false);
  const [editPolicy, setEditPolicy] = useState<LeavePolicy | null>(null);

  const load = () => {
    setLoading(true);
    Promise.all([
      leavePoliciesApi.list({ status: statusFilter || undefined }).then(setPolicies),
      leaveTypesApi.list().then(setLeaveTypes),
    ]).finally(() => setLoading(false));
  };
  useEffect(load, [statusFilter]);

  const typeName = (id: string) => leaveTypes.find(t => t.id === id)?.nameEn ?? id;

  const archive = async (id: string, name: string) => {
    if (!confirm(`Archive "${name}"? It will no longer be applied to new requests.`)) return;
    try { await leavePoliciesApi.delete(id); load(); } catch { alert('Failed to archive policy.'); }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select className={`${sel} w-40`} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
          <option value="">All</option>
          {['Draft', 'Active', 'Inactive', 'Archived'].map(s => <option key={s}>{s}</option>)}
        </select>
        <p className="flex-1 text-sm text-slate-400">{policies.length} polic{policies.length !== 1 ? 'ies' : 'y'}</p>
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" /> New Policy</button>
      </div>

      {loading ? <p className="text-sm text-slate-400">Loading…</p> : policies.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Settings className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No policies configured</p>
          <p className="text-xs text-slate-400">GCC leave rules are configurable — not hardcoded.</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {policies.map(p => (
            <div key={p.id} className="flex items-start justify-between gap-4 px-5 py-4">
              <div className="min-w-0 flex-1">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{p.name}</p>
                <p className="mt-0.5 text-xs text-slate-400">
                  {typeName(p.leaveTypeId)} · {p.countryCode} · {p.annualEntitlementDays}d/yr · {p.accrualMethod}
                  {p.departmentName ? ` · ${p.departmentName}` : ' · All depts'}
                  {p.grade ? ` · Grade ${p.grade}` : ''}
                </p>
                <div className="mt-1.5 flex flex-wrap gap-1.5">
                  {p.encashmentAllowed && <span className="rounded bg-amber-50 px-1.5 py-0.5 text-[10px] font-medium text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">Encashment ✓</span>}
                  {p.weekendsIncluded && <span className="rounded bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-500 dark:bg-white/10">Wknd incl.</span>}
                  {p.publicHolidaysIncluded && <span className="rounded bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-500 dark:bg-white/10">PH incl.</span>}
                  <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${p.payrollImpact === 'None' ? 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400' : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400'}`}>Payroll: {p.payrollImpact}</span>
                  <StatusBadge status={p.status} />
                </div>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <button type="button" className={btn.ghost} onClick={() => setEditPolicy(p)}>
                  <Settings className="h-3.5 w-3.5" /> Edit
                </button>
                {p.status !== 'Archived' && (
                  <button type="button" className={btn.danger} onClick={() => archive(p.id, p.name)}>
                    <X className="h-3.5 w-3.5" /> Archive
                  </button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
      {showCreate && <PolicyModal leaveTypes={leaveTypes} onClose={() => setShowCreate(false)} onSaved={() => { setShowCreate(false); load(); }} />}
      {editPolicy && <PolicyModal leaveTypes={leaveTypes} existing={editPolicy} onClose={() => setEditPolicy(null)} onSaved={() => { setEditPolicy(null); load(); }} />}
    </div>
  );
}

// ── Holiday Calendar Tab ──────────────────────────────────────────────────────

const BLANK_HOLIDAY_FORM = { nameEn: '', nameAr: '', date: '', hijriDate: '', holidayType: 'National', isRecurring: false, isOptional: false };
const BLANK_CAL_FORM = { name: '', countryCode: 'UAE', calendarYear: new Date().getFullYear() };

function HolidayCalendarTab() {
  const [calendars, setCalendars] = useState<PublicHolidayCalendar[]>([]);
  const [selected, setSelected] = useState<PublicHolidayCalendar | null>(null);
  const [holidays, setHolidays] = useState<PublicHoliday[]>([]);
  const [blackouts, setBlackouts] = useState<LeaveBlackoutDate[]>([]);

  // Calendar modal
  const [calModal, setCalModal] = useState<'none' | 'create' | 'edit'>('none');
  const [calForm, setCalForm] = useState({ ...BLANK_CAL_FORM });

  // Holiday modal
  const [editingHoliday, setEditingHoliday] = useState<PublicHoliday | null>(null);
  const [holidayModal, setHolidayModal] = useState<'none' | 'add' | 'edit'>('none');
  const [holidayForm, setHolidayForm] = useState({ ...BLANK_HOLIDAY_FORM });

  const { translation: autoHolidayNameAr, isTranslating: translatingHolidayNameAr } = useAutoTranslate(holidayForm.nameEn);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (autoHolidayNameAr && !holidayForm.nameAr) setHolidayForm(f => ({ ...f, nameAr: autoHolidayNameAr })); }, [autoHolidayNameAr]);

  const reloadCalendars = () => holidayCalendarApi.listCalendars().then(cs => {
    setCalendars(cs);
    // Keep selected in sync after rename
    setSelected(prev => prev ? (cs.find(c => c.id === prev.id) ?? prev) : null);
  }).catch(() => {});

  const reloadHolidays = (cal: PublicHolidayCalendar) =>
    holidayCalendarApi.listHolidays(cal.id).then(setHolidays).catch(() => {});

  useEffect(() => {
    reloadCalendars();
    holidayCalendarApi.listBlackouts().then(setBlackouts).catch(() => {});
  }, []);

  useEffect(() => {
    if (!selected) return;
    reloadHolidays(selected);
  }, [selected]);

  // ── error helper ──
  const apiErr = (err: unknown) => {
    const ax = err as { response?: { status?: number; data?: unknown } };
    if (ax?.response) return `${ax.response.status}: ${JSON.stringify(ax.response.data)}`;
    return String(err);
  };

  // ── Calendar save (create or edit) ──
  const saveCal = async () => {
    try {
      if (calModal === 'edit' && selected) {
        const updated = await holidayCalendarApi.updateCalendar(selected.id, calForm);
        setSelected(updated);
        await reloadCalendars();
      } else {
        await holidayCalendarApi.createCalendar(calForm);
        await reloadCalendars();
      }
      setCalModal('none');
    } catch (err) { alert(`Failed: ${apiErr(err)}`); }
  };

  const openEditCal = (c: PublicHolidayCalendar) => {
    setCalForm({ name: c.name, countryCode: c.countryCode, calendarYear: c.calendarYear });
    setCalModal('edit');
  };

  const delCal = async (c: PublicHolidayCalendar) => {
    if (!confirm(`Delete calendar "${c.name}"? All ${holidays.length} holidays will be removed.`)) return;
    try {
      await holidayCalendarApi.deleteCalendar(c.id);
      if (selected?.id === c.id) { setSelected(null); setHolidays([]); }
      await reloadCalendars();
    } catch (err) { alert(`Failed: ${apiErr(err)}`); }
  };

  // Normalise date from API (DateOnly → "YYYY-MM-DD" for <input type=date>)
  const toInputDate = (d: unknown): string => {
    if (!d) return '';
    if (typeof d === 'string') {
      // Already ISO string — take only the date part
      return d.split('T')[0];
    }
    // DateOnly serialised as object { year, month, day } (legacy .NET builds)
    if (typeof d === 'object') {
      const o = d as { year?: number; month?: number; day?: number };
      if (o.year && o.month && o.day) {
        return `${o.year}-${String(o.month).padStart(2, '0')}-${String(o.day).padStart(2, '0')}`;
      }
    }
    return String(d).split('T')[0];
  };

  // ── Holiday save (add or edit) ──
  const saveHoliday = async () => {
    if (!selected || !holidayForm.nameEn || !holidayForm.date) return;
    try {
      if (holidayModal === 'edit' && editingHoliday) {
        await holidayCalendarApi.updateHoliday(editingHoliday.id, holidayForm);
      } else {
        await holidayCalendarApi.addHoliday(selected.id, holidayForm);
      }
      setHolidayModal('none');
      setEditingHoliday(null);
      await reloadHolidays(selected);
    } catch (err) { alert(`Failed: ${apiErr(err)}`); }
  };

  const openEditHoliday = (h: PublicHoliday) => {
    setEditingHoliday(h);
    setHolidayForm({
      nameEn: h.nameEn,
      nameAr: h.nameAr ?? '',
      date: toInputDate(h.date),
      hijriDate: h.hijriDate ?? '',
      holidayType: h.holidayType ?? 'National',
      isRecurring: h.isRecurring ?? false,
      isOptional: h.isOptional ?? false,
    });
    setHolidayModal('edit');
  };

  const delHoliday = async (h: PublicHoliday) => {
    if (!confirm(`Remove "${h.nameEn}"?`)) return;
    try {
      await holidayCalendarApi.deleteHoliday(h.id);
      setHolidays(prev => prev.filter(x => x.id !== h.id));
    } catch (err) { alert(`Failed: ${apiErr(err)}`); }
  };

  const openAddHoliday = () => {
    setEditingHoliday(null);
    setHolidayForm({ ...BLANK_HOLIDAY_FORM });
    setHolidayModal('add');
  };

  return (
    <div className="space-y-4">
      <div className="grid gap-4 lg:grid-cols-3">
        {/* ── Calendars list ── */}
        <div className="surface p-4">
          <div className="mb-3 flex items-center justify-between">
            <p className="text-sm font-semibold text-slate-800 dark:text-white">Calendars</p>
            <button type="button" className={btn.sm} onClick={() => { setCalForm({ ...BLANK_CAL_FORM }); setCalModal('create'); }}>
              <Plus className="h-3 w-3" />
            </button>
          </div>
          {calendars.length === 0 ? <p className="text-xs text-slate-400">No calendars yet.</p> : (
            <div className="space-y-2">
              {calendars.map(c => (
                <div key={c.id}
                  className={`group flex items-start justify-between rounded-lg p-3 transition ${selected?.id === c.id ? 'bg-sapphire/10 dark:bg-sapphire/20' : 'hover:bg-slate-50 dark:hover:bg-white/5'}`}>
                  <button type="button" className="min-w-0 flex-1 text-left" onClick={() => setSelected(c)}>
                    <p className={`text-sm font-medium truncate ${selected?.id === c.id ? 'text-sapphire dark:text-cyanAccent' : 'text-slate-700 dark:text-slate-300'}`}>{c.name}</p>
                    <p className="text-xs text-slate-400">{c.countryCode} · {c.calendarYear}</p>
                  </button>
                  <div className="ml-1 flex shrink-0 items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                    <button type="button" title="Edit calendar" className="rounded p-1 text-slate-400 hover:text-sapphire dark:hover:text-cyanAccent" onClick={() => { setSelected(c); openEditCal(c); }}>
                      <Settings className="h-3 w-3" />
                    </button>
                    <button type="button" title="Delete calendar" className="rounded p-1 text-slate-400 hover:text-rose-500" onClick={() => { setSelected(c); delCal(c); }}>
                      <Trash2 className="h-3 w-3" />
                    </button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        {/* ── Holidays panel ── */}
        <div className="surface col-span-2 p-4">
          {!selected ? (
            <div className="flex h-full min-h-[200px] items-center justify-center">
              <p className="text-sm text-slate-400">Select a calendar to view holidays</p>
            </div>
          ) : (
            <>
              <div className="mb-3 flex items-center justify-between">
                <p className="text-sm font-semibold text-slate-800 dark:text-white">{selected.name} — {holidays.length} holidays</p>
                <button type="button" className={btn.primary} onClick={openAddHoliday}><Plus className="h-4 w-4" /> Add Holiday</button>
              </div>
              <div className="divide-y divide-slate-100 dark:divide-white/5">
                {holidays.map(h => (
                  <div key={h.id} className="group flex items-center justify-between py-3">
                    <div>
                      <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{h.nameEn}</p>
                      {h.nameAr && <p className="text-xs text-slate-400" dir="rtl">{h.nameAr}</p>}
                      <div className="mt-0.5 flex items-center gap-2">
                        <span className="text-xs text-slate-400">{fmtDate(h.date)}</span>
                        {h.hijriDate && <span className="text-xs text-slate-400">({h.hijriDate} Hijri)</span>}
                        <span className="rounded bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-500 dark:bg-white/10">{h.holidayType}</span>
                        {h.isOptional && <span className="rounded bg-amber-50 px-1.5 py-0.5 text-[10px] text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">Optional</span>}
                        {h.isRecurring && <span className="rounded bg-blue-50 px-1.5 py-0.5 text-[10px] text-blue-600 dark:bg-blue-500/10 dark:text-blue-400">Recurring</span>}
                      </div>
                    </div>
                    <div className="flex shrink-0 items-center gap-2">
                      <button type="button" className={btn.ghost} onClick={() => openEditHoliday(h)}>
                        <Settings className="h-3.5 w-3.5" /> Edit
                      </button>
                      <button type="button" className={btn.danger} onClick={() => delHoliday(h)}>
                        <X className="h-3.5 w-3.5" /> Remove
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            </>
          )}
        </div>
      </div>

      {blackouts.length > 0 && (
        <div className="surface p-4">
          <p className="mb-3 text-sm font-semibold text-slate-800 dark:text-white">Blackout / Critical Dates</p>
          <div className="divide-y divide-slate-100 dark:divide-white/5">
            {blackouts.map(b => (
              <div key={b.id} className="flex items-center justify-between py-3">
                <div>
                  <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{b.nameEn}</p>
                  <p className="text-xs text-slate-400">{fmtDate(b.startDate)} – {fmtDate(b.endDate)} · {b.isCompanyWide ? 'Company-wide' : b.departmentName || 'All'}</p>
                </div>
                <span className="text-xs text-slate-400">{b.reason}</span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* ── Calendar modal (create / edit) ── */}
      {calModal !== 'none' && (
        <Modal title={calModal === 'edit' ? 'Edit Calendar' : 'New Holiday Calendar'} onClose={() => setCalModal('none')}>
          <div className="space-y-4">
            <Field label="Calendar Name *">
              <input className={inp} value={calForm.name} onChange={e => setCalForm(f => ({ ...f, name: e.target.value }))} placeholder="e.g. UAE Public Holidays 2026" />
            </Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Country">
                <select className={sel} value={calForm.countryCode} onChange={e => setCalForm(f => ({ ...f, countryCode: e.target.value }))}>
                  {['UAE', 'KSA', 'QAT', 'KWT', 'BHR', 'OMN'].map(c => <option key={c}>{c}</option>)}
                </select>
              </Field>
              <Field label="Year">
                <input type="number" className={inp} value={calForm.calendarYear} onChange={e => setCalForm(f => ({ ...f, calendarYear: Number(e.target.value) }))} title="Calendar year" placeholder="2026" />
              </Field>
            </div>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setCalModal('none')}>Cancel</button>
              <button type="button" className={btn.primary} onClick={saveCal}>
                {calModal === 'edit' ? 'Save Changes' : 'Create'}
              </button>
            </div>
          </div>
        </Modal>
      )}

      {/* ── Holiday modal (add / edit) ── */}
      {holidayModal !== 'none' && (
        <Modal title={holidayModal === 'edit' ? 'Edit Holiday' : 'Add Public Holiday'} onClose={() => { setHolidayModal('none'); setEditingHoliday(null); }}>
          <div className="space-y-4">
            <div className="grid grid-cols-2 gap-3">
              <Field label="Name (English) *">
                <input className={inp} value={holidayForm.nameEn} onChange={e => setHolidayForm(f => ({ ...f, nameEn: e.target.value }))} placeholder="e.g. National Day" />
              </Field>
              <Field label="Name (Arabic)">
                <input className={inp} dir="rtl" value={holidayForm.nameAr}
                  onChange={e => setHolidayForm(f => ({ ...f, nameAr: e.target.value }))}
                  placeholder={translatingHolidayNameAr && !holidayForm.nameAr ? 'Translating…' : undefined} />
              </Field>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Date *">
                <input type="date" className={inp} value={holidayForm.date} onChange={e => setHolidayForm(f => ({ ...f, date: e.target.value }))} title="Holiday date" />
              </Field>
              <Field label="Hijri Date (optional)">
                <input className={inp} value={holidayForm.hijriDate} onChange={e => setHolidayForm(f => ({ ...f, hijriDate: e.target.value }))} placeholder="e.g. 1 Muharram 1448" />
              </Field>
            </div>
            <Field label="Holiday Type">
              <select className={sel} value={holidayForm.holidayType} onChange={e => setHolidayForm(f => ({ ...f, holidayType: e.target.value }))}>
                {['National', 'Religious', 'Eid', 'Company', 'Islamic'].map(ht => <option key={ht}>{ht}</option>)}
              </select>
            </Field>
            <div className="flex gap-4">
              <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                <input type="checkbox" checked={holidayForm.isRecurring} onChange={e => setHolidayForm(f => ({ ...f, isRecurring: e.target.checked }))} className="rounded" />
                Recurring
              </label>
              <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                <input type="checkbox" checked={holidayForm.isOptional} onChange={e => setHolidayForm(f => ({ ...f, isOptional: e.target.checked }))} className="rounded" />
                Optional
              </label>
            </div>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => { setHolidayModal('none'); setEditingHoliday(null); }}>Cancel</button>
              <button type="button" className={btn.primary} onClick={saveHoliday}>
                {holidayModal === 'edit' ? 'Save Changes' : 'Add Holiday'}
              </button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Encashment Tab ────────────────────────────────────────────────────────────

function EncashmentTab({ groupFilter = {} }: { groupFilter?: GroupFilter }) {
  const [requests, setRequests] = useState<LeaveEncashmentRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [leaveTypes, setLeaveTypes] = useState<LeaveType[]>([]);
  const [showCreate, setShowCreate] = useState(false);
  const [encashPickedEmp, setEncashPickedEmp] = useState<SelectedEmployee | null>(null);
  const [form, setForm] = useState({ employeeId: '', employeeName: '', leaveTypeId: '', year: new Date().getFullYear(), daysToEncash: '', amountPerDay: '', reason: '' });
  const [saving, setSaving] = useState(false);
  const { currencyCode } = useTenantSettings();
  const set = (k: keyof typeof form, v: string | number) => setForm(f => ({ ...f, [k]: v }));

  useEffect(() => {
    if (encashPickedEmp) setForm(f => ({ ...f, employeeId: String(encashPickedEmp.id), employeeName: encashPickedEmp.fullName }));
  }, [encashPickedEmp]);

  const load = () => { setLoading(true); encashmentApi.list(groupFilter).then(setRequests).catch(() => {}).finally(() => setLoading(false)); };
  useEffect(() => { load(); leaveTypesApi.list().then(setLeaveTypes).catch(() => {}); }, [groupFilter.companyId, groupFilter.branchId]);

  const create = async () => {
    if (!form.employeeId || !form.leaveTypeId || !form.daysToEncash || !form.amountPerDay) return;
    setSaving(true);
    try {
      await encashmentApi.create({ employeeId: Number(form.employeeId), leaveTypeId: form.leaveTypeId, year: Number(form.year), daysToEncash: Number(form.daysToEncash), amountPerDay: Number(form.amountPerDay), reason: form.reason });
      setShowCreate(false); load();
    } catch { alert('Failed.'); }
    setSaving(false);
  };

  const hrApprove = async (id: string) => { try { await encashmentApi.hrApprove(id); load(); } catch { alert('Failed.'); } };
  const reject = async (id: string) => { const n = prompt('Rejection notes:') ?? ''; try { await encashmentApi.reject(id, n); load(); } catch { alert('Failed.'); } };

  return (
    <div className="space-y-4">
      <div className="flex justify-end"><button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" /> Request Encashment</button></div>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : requests.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <TrendingUp className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No encashment requests</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {requests.map(r => (
            <div key={r.id} className="flex items-center justify-between px-5 py-4">
              <div>
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName}</p>
                <p className="text-xs text-slate-400">{r.leaveTypeName} · {r.year} · {r.daysToEncash} days</p>
                <p className="mt-1 text-sm font-bold text-emerald-600 dark:text-emerald-400">{currencyCode} {fmtAmt(r.totalAmount)}</p>
              </div>
              <div className="flex items-center gap-3">
                <StatusBadge status={r.status} />
                {r.status === 'Pending' && (
                  <div className="flex gap-2">
                    <button type="button" className={btn.primary} onClick={() => hrApprove(r.id)}>HR Approve</button>
                    <button type="button" className={btn.danger} onClick={() => reject(r.id)}>Reject</button>
                  </div>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
      {showCreate && (
        <Modal title="Leave Encashment Request" onClose={() => setShowCreate(false)}>
          <div className="space-y-4">
            <EmployeePicker label="Employee *" required value={encashPickedEmp} onChange={setEncashPickedEmp} placeholder="Search employee…" />
            <Field label="Leave Type *" info="The category of leave (annual, sick, etc.). Determines the balance used, whether a reason or attachment is required, and the approval flow." infoKey="leave.leave_type">
              <select className={sel} value={form.leaveTypeId} onChange={e => set('leaveTypeId', e.target.value)}>
                <option value="">Select…</option>
                {leaveTypes.map(t => <option key={t.id} value={t.id}>{t.nameEn}</option>)}
              </select>
            </Field>
            <div className="grid grid-cols-3 gap-3">
              <Field label="Year"><input type="number" className={inp} value={form.year} onChange={e => set('year', Number(e.target.value))} /></Field>
              <Field label="Days to Encash *"><input type="number" step="0.5" className={inp} value={form.daysToEncash} onChange={e => set('daysToEncash', e.target.value)} /></Field>
              <Field label={`Daily Rate (${currencyCode}) *`}><input type="number" step="0.01" aria-label={`Daily rate in ${currencyCode}`} className={inp} value={form.amountPerDay} onChange={e => set('amountPerDay', e.target.value)} /></Field>
            </div>
            {form.daysToEncash && form.amountPerDay && (
              <div className="rounded-lg bg-emerald-50 px-4 py-3 dark:bg-emerald-500/10">
                <p className="text-sm font-semibold text-emerald-700 dark:text-emerald-400">Estimated: {currencyCode} {fmtAmt(Number(form.daysToEncash) * Number(form.amountPerDay))}</p>
              </div>
            )}
            <Field label="Reason"><textarea className={inp} rows={2} value={form.reason} onChange={e => set('reason', e.target.value)} /></Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setShowCreate(false)}>Cancel</button>
              <button type="button" className={btn.primary} onClick={create} disabled={saving}>{saving ? 'Submitting…' : 'Submit'}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Comp-Off Tab ──────────────────────────────────────────────────────────────

function CompOffTab({ groupFilter = {} }: { groupFilter?: GroupFilter }) {
  const [credits, setCredits] = useState<CompOffCredit[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [compOffPickedEmp, setCompOffPickedEmp] = useState<SelectedEmployee | null>(null);
  const [form, setForm] = useState({ employeeId: '', employeeName: '', workedDate: '', workType: 'HolidayWork', hoursWorked: '', daysEarned: '', expiryDate: '' });
  const [saving, setSaving] = useState(false);
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  useEffect(() => {
    if (compOffPickedEmp) setForm(f => ({ ...f, employeeId: String(compOffPickedEmp.id), employeeName: compOffPickedEmp.fullName }));
  }, [compOffPickedEmp]);

  const load = () => { setLoading(true); compOffApi.list(groupFilter).then(setCredits).catch(() => {}).finally(() => setLoading(false)); };
  useEffect(load, [groupFilter.companyId, groupFilter.branchId]);

  const create = async () => {
    if (!form.employeeId || !form.workedDate || !form.daysEarned) return;
    setSaving(true);
    try { await compOffApi.create({ employeeId: Number(form.employeeId), workedDate: form.workedDate, workType: form.workType, hoursWorked: Number(form.hoursWorked), daysEarned: Number(form.daysEarned), expiryDate: form.expiryDate || undefined }); setShowCreate(false); load(); }
    catch { alert('Failed.'); }
    setSaving(false);
  };

  const approve = async (id: string) => { try { await compOffApi.approve(id); load(); } catch { alert('Failed.'); } };

  return (
    <div className="space-y-4">
      <div className="flex justify-end"><button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" /> Add Comp-Off Credit</button></div>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : credits.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Star className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No comp-off credits</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {credits.map(c => (
            <div key={c.id} className="flex items-center justify-between px-5 py-4">
              <div>
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{c.employeeName}</p>
                <p className="text-xs text-slate-400">{c.workType} · {fmtDate(c.workedDate)} · {c.daysEarned}d earned</p>
                {c.expiryDate && <p className="text-xs text-amber-600 dark:text-amber-400">Expires: {fmtDate(c.expiryDate)}</p>}
              </div>
              <div className="flex items-center gap-3">
                <StatusBadge status={c.status} />
                {c.status === 'Pending' && <button type="button" className={btn.primary} onClick={() => approve(c.id)}>Approve</button>}
              </div>
            </div>
          ))}
        </div>
      )}
      {showCreate && (
        <Modal title="Create Comp-Off Credit" onClose={() => setShowCreate(false)}>
          <div className="space-y-4">
            <EmployeePicker label="Employee *" required value={compOffPickedEmp} onChange={setCompOffPickedEmp} placeholder="Search employee…" />
            <div className="grid grid-cols-2 gap-3">
              <Field label="Worked Date *"><input type="date" className={inp} value={form.workedDate} onChange={e => set('workedDate', e.target.value)} /></Field>
              <Field label="Work Type">
                <select className={sel} value={form.workType} onChange={e => set('workType', e.target.value)}>
                  {['Overtime', 'HolidayWork', 'WeekendWork'].map(t => <option key={t}>{t}</option>)}
                </select>
              </Field>
            </div>
            <div className="grid grid-cols-3 gap-3">
              <Field label="Hours Worked"><input type="number" step="0.5" className={inp} value={form.hoursWorked} onChange={e => set('hoursWorked', e.target.value)} /></Field>
              <Field label="Days Earned *"><input type="number" step="0.5" className={inp} value={form.daysEarned} onChange={e => set('daysEarned', e.target.value)} /></Field>
              <Field label="Expiry Date"><input type="date" className={inp} value={form.expiryDate} onChange={e => set('expiryDate', e.target.value)} /></Field>
            </div>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setShowCreate(false)}>Cancel</button>
              <button type="button" className={btn.primary} onClick={create} disabled={saving}>{saving ? 'Saving…' : 'Create'}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Absences Tab ──────────────────────────────────────────────────────────────

function AbsencesTab({ groupFilter = {} }: { groupFilter?: GroupFilter }) {
  const [absences, setAbsences] = useState<AbsenceRecord[]>([]);
  const [loading, setLoading] = useState(true);
  const [regModal, setRegModal] = useState<AbsenceRecord | null>(null);
  const [regReason, setRegReason] = useState('');

  const load = () => { setLoading(true); absenceApi.list(groupFilter).then(setAbsences).catch(() => {}).finally(() => setLoading(false)); };
  useEffect(load, [groupFilter.companyId, groupFilter.branchId]);

  const regularize = async () => {
    if (!regModal || !regReason) return;
    try { await absenceApi.submitRegularization({ employeeId: regModal.employeeId, absenceRecordId: regModal.id, reason: regReason }); setRegModal(null); load(); }
    catch { alert('Failed.'); }
  };

  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-500 dark:text-slate-400">{absences.length} absence record{absences.length !== 1 ? 's' : ''}</p>
      {loading ? <p className="text-sm text-slate-400">Loading…</p> : absences.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Shield className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No absence records</p>
        </div>
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {absences.map(a => (
            <div key={a.id} className="flex items-center justify-between px-5 py-4">
              <div>
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{a.employeeName}</p>
                <p className="text-xs text-slate-400">{a.departmentName} · {fmtDate(a.absenceDate)} · {a.absenceType}</p>
                <p className={`mt-1 text-xs font-medium ${a.payrollImpact === 'Deduction' ? 'text-rose-600 dark:text-rose-400' : 'text-slate-400'}`}>Payroll: {a.payrollImpact}</p>
              </div>
              <div className="flex items-center gap-3">
                {a.isRegularized ? (
                  <span className="text-xs text-emerald-600 dark:text-emerald-400">Regularized</span>
                ) : (
                  <button type="button" className={btn.ghost} onClick={() => { setRegModal(a); setRegReason(''); }}>Regularize</button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
      {regModal && (
        <Modal title={`Regularize Absence — ${regModal.employeeName}`} onClose={() => setRegModal(null)}>
          <div className="space-y-4">
            <p className="text-sm text-slate-600 dark:text-slate-400">Absence date: <strong className="text-slate-900 dark:text-white">{fmtDate(regModal.absenceDate)}</strong></p>
            <Field label="Reason for Regularization *">
              <textarea className={inp} rows={3} value={regReason} onChange={e => setRegReason(e.target.value)} placeholder="Explain why this absence should be regularized…" />
            </Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setRegModal(null)}>Cancel</button>
              <button type="button" className={btn.primary} onClick={regularize}>Submit Request</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Reports Tab ───────────────────────────────────────────────────────────────

function ReportsTab({ groupFilter = {} }: { groupFilter?: GroupFilter }) {
  const [view, setView] = useState<'onleave' | 'sick'>('onleave');
  const [onLeave, setOnLeave] = useState<LeaveRequest[]>([]);
  const [sickTrend, setSickTrend] = useState<{ month: string; count: number; totalDays: number }[]>([]);

  useEffect(() => {
    leaveReportsApi.onLeaveToday(groupFilter).then(setOnLeave).catch(() => {});
    leaveReportsApi.sickLeaveTrend(groupFilter).then(setSickTrend).catch(() => {});
  }, [groupFilter.companyId, groupFilter.branchId]);

  return (
    <div className="space-y-4">
      <div className="flex gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]" style={{ width: 'fit-content' }}>
        {([['onleave', 'On Leave Today'], ['sick', 'Sick Leave Trend']] as const).map(([v, l]) => (
          <button key={v} type="button" onClick={() => setView(v)} className={`rounded-lg px-4 py-1.5 text-sm font-medium transition ${view === v ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400'}`}>{l}</button>
        ))}
      </div>

      {view === 'onleave' && (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          {onLeave.length === 0 ? <p className="px-5 py-8 text-center text-sm text-slate-400">No employees on leave today</p> : onLeave.map((r, i) => (
            <div key={i} className="flex items-center justify-between px-5 py-3">
              <div>
                <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{r.employeeName}</p>
                <p className="text-xs text-slate-400">{r.departmentName} · {r.leaveTypeName}</p>
              </div>
              <div className="text-right">
                <p className="text-xs text-slate-500">{fmtDate(r.startDate)} – {fmtDate(r.endDate)}</p>
                <p className="text-xs font-semibold text-sapphire dark:text-cyanAccent">{r.totalDays}d</p>
              </div>
            </div>
          ))}
        </div>
      )}

      {view === 'sick' && (
        <div className="surface p-5">
          <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">Sick Leave — Last 12 Months</h3>
          {sickTrend.length === 0 ? <p className="text-sm text-slate-400">No sick leave data.</p> : (
            <div className="space-y-3">
              {sickTrend.map(m => (
                <div key={m.month} className="flex items-center gap-4">
                  <span className="w-20 shrink-0 text-xs text-slate-500">{m.month}</span>
                  <div className="flex-1 rounded-full bg-slate-100 dark:bg-white/10">
                    <div className="h-2 rounded-full bg-amber-400" style={{ width: `${Math.min(100, (m.totalDays / Math.max(...sickTrend.map(x => x.totalDays), 1)) * 100)}%` }} />
                  </div>
                  <span className="w-20 text-right text-xs font-semibold tabular-nums text-slate-600 dark:text-slate-300">{m.totalDays}d / {m.count}</span>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ── AI Insights Tab ───────────────────────────────────────────────────────────

function AIInsightsTab() {
  const [insights, setInsights] = useState<LeaveAIInsight[]>([]);
  const [loading, setLoading] = useState(true);
  const [generating, setGenerating] = useState(false);

  const load = () => { setLoading(true); leaveAIApi.list().then(setInsights).catch(() => {}).finally(() => setLoading(false)); };
  useEffect(load, []);

  const generate = async () => { setGenerating(true); try { await leaveAIApi.generate(); load(); } catch { alert('Generation failed.'); } setGenerating(false); };
  const ack = async (id: string) => { try { await leaveAIApi.acknowledge(id); load(); } catch { alert('Failed.'); } };

  const SEV_CARD: Record<string, string> = {
    Info: 'border-blue-200 bg-blue-50 dark:border-blue-500/20 dark:bg-blue-500/10',
    Warning: 'border-amber-200 bg-amber-50 dark:border-amber-500/20 dark:bg-amber-500/10',
    Alert: 'border-rose-200 bg-rose-50 dark:border-rose-500/20 dark:bg-rose-500/10',
  };
  const SEV_TEXT: Record<string, string> = {
    Info: 'text-blue-700 dark:text-blue-400',
    Warning: 'text-amber-700 dark:text-amber-400',
    Alert: 'text-rose-700 dark:text-rose-400',
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-xs text-amber-700 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-400">
          <strong>Advisory only.</strong> Insights are for HR review and do not reject leave, penalize employees, or make final decisions.
        </div>
        <button type="button" className={btn.primary} onClick={generate} disabled={generating}><Zap className="h-4 w-4" />{generating ? 'Generating…' : 'Generate Insights'}</button>
      </div>

      {loading ? <p className="text-sm text-slate-400">Loading…</p> : insights.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Zap className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">No insights yet</p>
          <p className="text-xs text-slate-400">Click Generate Insights to analyse leave patterns.</p>
        </div>
      ) : (
        <div className="space-y-3">
          {insights.map(ins => (
            <div key={ins.id} className={`rounded-xl border p-4 ${SEV_CARD[ins.severity] ?? SEV_CARD['Info']} ${ins.isAcknowledged ? 'opacity-60' : ''}`}>
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="mb-1 flex items-center gap-2">
                    <span className={`text-xs font-bold ${SEV_TEXT[ins.severity] ?? ''}`}>{ins.severity.toUpperCase()}</span>
                    <span className="rounded bg-white/60 px-2 py-0.5 text-[10px] font-medium text-slate-600 dark:bg-white/10 dark:text-slate-400">{ins.insightType}</span>
                    {ins.affectedDepartment && <span className="text-[10px] text-slate-400">{ins.affectedDepartment}</span>}
                  </div>
                  <p className={`text-sm font-semibold ${SEV_TEXT[ins.severity] ?? ''}`}>{ins.title}</p>
                  <p className="mt-1 text-sm text-slate-700 dark:text-slate-300">{ins.summary}</p>
                  <p className="mt-1 text-[10px] text-slate-400">{fmtDate(ins.createdAtUtc)}</p>
                </div>
                {!ins.isAcknowledged && <button type="button" className={btn.sm} onClick={() => ack(ins.id)}>Acknowledge</button>}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Root Page ─────────────────────────────────────────────────────────────────

type Tab = 'dashboard' | 'balance' | 'apply' | 'requests' | 'approvals' | 'calendar' | 'types' | 'policies' | 'holidays' | 'encashment' | 'compoff' | 'absences' | 'reports' | 'ai-insights';

const TABS: { id: Tab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { id: 'dashboard', label: 'Dashboard', icon: Activity },
  { id: 'balance', label: 'Leave Balance', icon: BarChart2 },
  { id: 'apply', label: 'Apply Leave', icon: Plus },
  { id: 'requests', label: 'My Requests', icon: FileText },
  { id: 'approvals', label: 'Approvals', icon: CheckCircle },
  { id: 'calendar', label: 'Calendar', icon: Calendar },
  { id: 'types', label: 'Leave Types', icon: Settings },
  { id: 'policies', label: 'Policies', icon: Shield },
  { id: 'holidays', label: 'Holidays', icon: Star },
  { id: 'encashment', label: 'Encashment', icon: TrendingUp },
  { id: 'compoff', label: 'Comp-Off', icon: RefreshCw },
  { id: 'absences', label: 'Absences', icon: AlertTriangle },
  { id: 'reports', label: 'Reports', icon: Zap },
  { id: 'ai-insights', label: 'Insights', icon: Users },
];

export function LeavePage() {
  const { user } = useAuth();
  const { t } = useLocale();
  const [tab, setTab] = useState<Tab>('dashboard');
  const [companies, setCompanies] = useState<CompanyDto[]>([]);
  const [branches, setBranches] = useState<BranchDto[]>([]);
  const [companyId, setCompanyId] = useState('');
  const [branchId, setBranchId] = useState('');

  const isAdmin    = user?.roles.some(r => ['Admin', 'HR Manager', 'HR Officer'].includes(r)) ?? false;
  const isManager  = !isAdmin && (user?.roles.some(r => ['Manager', 'Supervisor'].includes(r)) ?? false);
  const isEmployee = !isAdmin && !isManager;
  const selfEmployeeId = user?.employeeId;

  useEffect(() => {
    if (isAdmin) {
      companiesApi.list(1, 100).then(r => setCompanies(r.items)).catch(() => {});
    }
  }, [isAdmin]);

  useEffect(() => {
    if (companyId) {
      branchesApi.list(companyId, 1, 100).then(r => setBranches(r.items)).catch(() => {});
    } else {
      setBranches([]);
      setBranchId('');
    }
  }, [companyId]);

  const groupFilter: GroupFilter = {
    companyId: companyId || undefined,
    branchId: branchId || undefined,
  };

  const requestsLabel = isAdmin ? 'All Requests' : isManager ? 'Team Requests' : 'My Leave';

  const visibleTabs = TABS
    .map(t => t.id === 'requests' ? { ...t, label: requestsLabel } : t)
    .filter(t => {
      if (['types', 'policies', 'holidays', 'reports', 'ai-insights'].includes(t.id)) return isAdmin;
      if (t.id === 'approvals') return isAdmin || isManager;
      if (t.id === 'absences') return isAdmin || isManager;
      if (t.id === 'encashment') return isAdmin || isManager;
      if (t.id === 'compoff') return isAdmin || isManager;
      return true;
    });

  const subtitle = isAdmin
    ? 'Manage leave across all group companies — policies, balances, workflows, encashment, comp-off, absence records, and insights.'
    : isManager
    ? 'Approve team leave requests, track your team\'s balances, and manage absences.'
    : 'Apply for leave, track your balance, and view your leave history.';

  return (
    <div className="space-y-5 p-4 sm:p-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">{t('Leave & Absence Management')}</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">{subtitle}</p>
        </div>
        <span className={`shrink-0 rounded-full px-3 py-1 text-xs font-semibold ${
          isAdmin   ? 'bg-sapphire/10 text-sapphire dark:bg-sapphire/20 dark:text-cyanAccent'
          : isManager ? 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400'
          : 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-400'
        }`}>
          {isAdmin ? 'Admin / HR' : isManager ? 'Manager' : 'Employee'}
        </span>
      </div>

      {isAdmin && companies.length > 0 && (
        <GroupContextBar
          companies={companies}
          companyId={companyId}
          onCompanyChange={setCompanyId}
          branches={branches}
          branchId={branchId}
          onBranchChange={setBranchId}
        />
      )}

      <div className="overflow-x-auto pb-1">
        <div className="flex gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]" style={{ width: 'max-content' }}>
          {visibleTabs.map(tb => (
            <button key={tb.id} type="button" onClick={() => setTab(tb.id)}
              className={`flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium transition ${tab === tb.id ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'}`}>
              <tb.icon className="h-3.5 w-3.5" />
              {t(tb.label)}
            </button>
          ))}
        </div>
      </div>

      {tab === 'dashboard'  && <DashboardTab onNavigate={setTab} groupFilter={groupFilter} />}
      {tab === 'balance'    && <BalanceTab selfEmployeeId={isEmployee ? selfEmployeeId : undefined} groupFilter={groupFilter} />}
      {tab === 'apply'      && <ApplyLeaveTab selfEmployeeId={isEmployee ? selfEmployeeId : undefined} isEmployee={isEmployee} />}
      {tab === 'requests'   && <MyRequestsTab />}
      {tab === 'approvals'  && <ApprovalsTab groupFilter={groupFilter} />}
      {tab === 'calendar'   && <CalendarTab groupFilter={groupFilter} />}
      {tab === 'types'      && <LeaveTypesTab />}
      {tab === 'policies'   && <PoliciesTab />}
      {tab === 'holidays'   && <HolidayCalendarTab />}
      {tab === 'encashment' && <EncashmentTab groupFilter={groupFilter} />}
      {tab === 'compoff'    && <CompOffTab groupFilter={groupFilter} />}
      {tab === 'absences'   && <AbsencesTab groupFilter={groupFilter} />}
      {tab === 'reports'    && <ReportsTab groupFilter={groupFilter} />}
      {tab === 'ai-insights'&& <AIInsightsTab />}
    </div>
  );
}
