'use client';

import { useT } from '../hooks/useT';
import { useFormat } from '../hooks/useFormat';
import type { Formatter } from '../lib/format';
import { Modal as SharedModal } from '../components/Modal';
import { enumLabel } from '../i18n/enumLabel';
import { InfoTip } from '../components/InfoTip';
import { useAuth } from '../contexts/AuthContext';
import { EmployeePicker } from '../components/EmployeePicker';
import type { SelectedEmployee } from '../components/EmployeePicker';
import { useTenantSettings } from '../contexts/TenantSettingsContext';
import { useEffect, useLayoutEffect, useState } from 'react';
import { usePagedList } from '../hooks/usePagedList';
import { ListWindowFooter } from '../components/ListWindowFooter';
import { requestFailureReason } from '../lib/requestFailure';
import { apiErrorReason, notifyApiError } from '../api/client';
import { LoadFailedNotice } from '../components/ui/LoadFailedRow';
import { msg } from '../i18n/translations';
import { useConfirm } from '../components/gl/glUi';
import { ModuleSettingsLink } from '../components/setup/ModuleSettingsLink';
import {
  AlertTriangle, BarChart2, Calculator, CheckCircle2, Clock3, FileClock,
  Layers3, Plus, RefreshCw, Settings, TimerReset, TrendingUp,
  Users, WalletCards, XCircle,
} from 'lucide-react';
import {
  overtimeApi,
  type OvertimePolicy, type OvertimeRequest, type OvertimeSummary,
  type OvertimeCalculation, type OvertimePayrollImpact, type OvertimeCompOffConversion,
  type OvertimeCapException,
} from '../api/overtime';

import { EnumLabel, type EnumName } from '../components/EnumLabel';
// ── Shared helpers ──────────────────────────────────────────────────────────────

const today = new Date().toISOString().slice(0, 10);
const dt = (date: string, time: string) => new Date(`${date}T${time}:00`).toISOString();

type Translate = (key: string, params?: Record<string, string | number | null | undefined>) => string;

function fmtDate(fx: Formatter, s: string | null | undefined) {
  if (!s) return '—';
  return fx.date(s, 'medium');
}

function fmtMins(t: Translate, m: number) {
  const h = Math.floor(m / 60);
  const min = m % 60;
  if (h > 0 && min > 0) return t('{hours}h {minutes}m', { hours: h, minutes: min });
  if (h > 0) return t('{hours}h', { hours: h });
  return t('{minutes}m', { minutes: min });
}

// Overtime is stored in whole minutes and the API derives hours from them, so 50 approved minutes
// arrive as 0.8333333333333333 rather than a pre-rounded 0.83. Show up to four decimals — enough
// that the hours on screen still reconcile with the amount beside them — and trim, so a whole hour
// still reads "1h" exactly as it did before.
function fmtHours(h: number) {
  return String(Math.round(h * 10000) / 10000);
}

function fmtAmt(fx: Formatter, n: number, currency = 'USD') {
  return fx.money(n, currency);
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

function StatusBadge({ status, enumName = 'OvertimeStatus' }: { status: string; enumName?: EnumName }) {
  const cls = STATUS_COLOR[status] ?? 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400';
  return <span className={`inline-flex rounded-lg px-2 py-0.5 text-xs font-medium ${cls}`}><EnumLabel enum={enumName} value={status} /></span>;
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
        <p className="text-2xl font-semibold tabular-nums text-slate-900 dark:text-white">{value}</p>
        <p className="mt-0.5 truncate text-xs text-slate-500 dark:text-slate-400">{label}</p>
        {sub && <p className="text-caption text-slate-400 dark:text-slate-500">{sub}</p>}
      </div>
    </div>
  );
}

/** The shared accessible dialog (role=dialog, focus trap, Escape) with this page's title/size vocabulary. */
function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: React.ReactNode; wide?: boolean }) {
  const t = useT();
  return (
    <SharedModal isOpen title={title} onClose={onClose} size={wide ? 'lg' : 'md'} closeLabel={t('Close')}>
      {children}
    </SharedModal>
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

const SOURCE_LABEL: Record<string, string> = {
  Manual: msg('Manual'), Project: msg('Project'), Emergency: msg('Emergency'), Shift: msg('Shift'),
};
const BASIS_LABEL: Record<string, string> = {
  BasicSalary: msg('Basic salary'), GrossSalary: msg('Gross salary'), FixedHourlyRate: msg('Fixed hourly rate'),
};
const PENDING_STATUSES = ['PendingManager', 'PendingHR', 'Approved', 'Rejected'] as const;

/** Page-footer wording for a list that is one page of a longer one ("Showing 100 of 1,234 requests"). */
function footerLabels(t: Translate, fx: Formatter, shown: number, total: number | null) {
  return {
    window: t('Showing {shown} of {total} requests', { shown: fx.integer(shown), total: fx.integer(total ?? shown) }),
    loadMore: t('Load more'),
    loading: t('Loading…'),
  };
}

// ── Tabs ────────────────────────────────────────────────────────────────────────

type Tab = 'dashboard' | 'submit' | 'my-ot' | 'team-ot' | 'approvals' | 'calc-preview' | 'payroll-review' | 'reports';

const TABS: { key: Tab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { key: 'dashboard', label: msg('Dashboard'), icon: BarChart2 },
  { key: 'submit', label: msg('Submit OT'), icon: Plus },
  { key: 'my-ot', label: msg('My OT'), icon: Clock3 },
  { key: 'team-ot', label: msg('Team OT'), icon: Users },
  { key: 'approvals', label: msg('Approvals'), icon: CheckCircle2 },
  { key: 'calc-preview', label: msg('Calculation Preview'), icon: Calculator },
  { key: 'payroll-review', label: msg('Payroll Review'), icon: WalletCards },
  { key: 'reports', label: msg('Reports'), icon: TrendingUp },
];

// ── Dashboard Tab ───────────────────────────────────────────────────────────────

function DashboardTab({ onNavigate }: { onNavigate: (tab: Tab) => void }) {
  const t = useT();
  const fx = useFormat();
  const { hasPermission } = useAuth();
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
        <KpiCard label={t('Total Requests')} value={summary?.totalRequests ?? '—'} icon={FileClock} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
        <KpiCard label={t('Pending Approval')} value={summary?.pendingRequests ?? '—'} icon={Clock3} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
        <KpiCard label={t('Approved Hours')} value={summary?.approvedHours != null ? t('{hours}h', { hours: fmtHours(summary.approvedHours) }) : '—'} icon={CheckCircle2} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
        <KpiCard label={t('Payroll Amount')} value={summary?.payrollAmount != null ? fmtAmt(fx, summary.payrollAmount, currencyCode) : '—'} icon={WalletCards} color="bg-cyan-100 text-cyan-600 dark:bg-cyan-500/20 dark:text-cyan-400" />
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <div className="surface p-5 lg:col-span-2">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">{t('Recent OT Requests')}</h3>
            <button type="button" onClick={() => onNavigate('team-ot')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">{t('View all')}</button>
          </div>
          {recent.length === 0 ? <p className="text-sm text-slate-400">{t('No overtime requests yet.')}</p> : (
            <div className="divide-y divide-slate-100 dark:divide-white/5">
              {recent.map(r => (
                <div key={r.id} className="flex items-center justify-between py-3">
                  <div className="min-w-0">
                    <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{r.employeeName}</p>
                    <p className="text-xs text-slate-400">{fmtDate(fx, r.workDate)} · {fmtMins(t, r.requestedMinutes)} · {t(SOURCE_LABEL[r.source] ?? r.source)}</p>
                  </div>
                  <StatusBadge status={r.status} />
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="surface p-5">
          <h3 className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">{t('Quick Actions')}</h3>
          <div className="space-y-2">
            {([
              [msg('Submit OT Request'), 'submit', Plus],
              [msg('Approve Requests'), 'approvals', CheckCircle2],
              [msg('Team OT View'), 'team-ot', Users],
              [msg('Calculation Preview'), 'calc-preview', Calculator],
              [msg('Payroll Review'), 'payroll-review', WalletCards],
            ] as [string, Tab, React.ComponentType<{ className?: string }>][])
              .filter(([, tab]) => tab !== 'payroll-review' || hasPermission('payroll.read'))
              .map(([label, tab, Icon]) => (
              <button key={tab} type="button" onClick={() => onNavigate(tab)}
                className="flex w-full items-center gap-3 rounded-lg p-2.5 text-start hover:bg-slate-50 dark:hover:bg-white/5">
                <Icon className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />
                <span className="text-sm text-slate-700 dark:text-slate-300">{t(label)}</span>
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
  const t = useT();
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
      setError(t('Employee ID, work date and times are required.')); return;
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
      const serverMessage = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(serverMessage ?? t('Failed to submit overtime request.'));
    }
    setSaving(false);
  };

  if (success) return (
    <div className="surface flex flex-col items-center py-20 text-center">
      <CheckCircle2 className="mb-3 h-12 w-12 text-emerald-500" />
      <p className="text-lg font-semibold text-slate-800 dark:text-white">{t('Overtime Request Submitted')}</p>
      <p className="mt-1 text-sm text-slate-400">{t('Your request is pending manager approval.')}</p>
      <button type="button" className={`mt-6 ${btn.primary}`} onClick={() => { setSuccess(false); setForm(f => ({ ...f, reason: '', employeeId: selfEmployeeId ? String(selfEmployeeId) : '' })); }}>{t('Submit Another')}</button>
    </div>
  );

  const mins = calcMinutes();

  return (
    <div className="max-w-xl space-y-5">
      <div className="surface p-5">
        <p className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">{t('Employee & Date')}</p>
        <div className="space-y-3">
          {isEmployee ? (
            <p className="text-sm text-slate-500 dark:text-slate-400">{t('Submitting overtime for: Employee #{id}', { id: form.employeeId })}</p>
          ) : (
            <EmployeePicker label={t('Employee *')} required value={otPickedEmp} onChange={setOtPickedEmp} placeholder={t('Search by name or employee code…')} />
          )}
          <div className="grid grid-cols-2 gap-3">
            <Field label={t('Work Date *')} info={t('The day the overtime was actually worked — not the date you submit the request.')} infoKey="overtime.work_date"><input type="date" aria-label={t('Work date')} className={inp} value={form.workDate} onChange={e => set('workDate', e.target.value)} /></Field>
            <Field label={t('Start Time *')} info={t('When the overtime began, in 24-hour time (e.g. 18:00 = 6pm).')} infoKey="overtime.start_time"><input type="time" aria-label={t('Start time')} className={inp} value={form.startTime} onChange={e => set('startTime', e.target.value)} /></Field>
            <Field label={t('End Time *')}><input type="time" aria-label={t('End time')} className={inp} value={form.endTime} onChange={e => set('endTime', e.target.value)} /></Field>
          </div>
          {mins > 0 && (
            <div className="rounded-lg bg-sapphire/5 px-4 py-2.5 dark:bg-sapphire/10">
              <p className="text-sm text-sapphire dark:text-cyanAccent">{t('Duration: {duration} ({hours} hours)', { duration: fmtMins(t, mins), hours: Math.round(mins / 60 * 100) / 100 })}</p>
            </div>
          )}
        </div>
      </div>

      <div className="surface p-5">
        <p className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">{t('Policy & Type')}</p>
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('OT Policy')} info={t('The overtime policy that sets the pay multiplier (e.g. 1.25x weekday, 1.5x holiday). Configured in Overtime settings, under Setup.')} infoKey="overtime.policy">
            <select aria-label={t('OT Policy')} className={sel} value={form.policyId} onChange={e => set('policyId', e.target.value)}>
              <option value="">{t('Auto-select default')}</option>
              {policies.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </Field>
          <Field label={t('Source')}>
            <select aria-label={t('Source')} className={sel} value={form.source} onChange={e => set('source', e.target.value)}>
              {['Manual', 'Project', 'Emergency', 'Shift'].map(s => <option key={s} value={s}>{t(SOURCE_LABEL[s])}</option>)}
            </select>
          </Field>
        </div>
        <div className="mt-3">
          <Field label={t('Reason')}>
            <textarea className={inp} rows={3} value={form.reason} onChange={e => set('reason', e.target.value)} placeholder={t('Reason for overtime…')} />
          </Field>
        </div>
      </div>

      {error && <p className="rounded-lg bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-400">{error}</p>}
      <button type="button" className={btn.primary} onClick={submit} disabled={saving}>{saving ? t('Submitting…') : t('Submit OT Request')}</button>
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
  const t = useT();
  const fx = useFormat();
  if (requests.length === 0) return (
    <div className="flex flex-col items-center py-16 text-center">
      <FileClock className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
      <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No overtime records found')}</p>
    </div>
  );

  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[640px] text-sm">
        <thead>
          <tr className="border-b border-slate-100 dark:border-white/[0.07]">
            {[msg('Employee'), msg('Date'), msg('Requested'), msg('Approved'), msg('Source'), msg('Status'), ...(showActions ? [msg('Actions')] : [])].map(h => (
              <th key={h} className="px-3 py-2 text-start text-xs font-semibold uppercase text-slate-400">{t(h)}</th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
          {requests.map(r => (
            <tr key={r.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
              <td className="px-3 py-2 font-medium text-slate-900 dark:text-white">{r.employeeName || t('Emp #{id}', { id: r.employeeId })}</td>
              <td className="px-3 py-2 text-slate-500">{fmtDate(fx, r.workDate)}</td>
              <td className="px-3 py-2 text-slate-500">{fmtMins(t, r.requestedMinutes)}</td>
              <td className="px-3 py-2 text-slate-500">{r.approvedMinutes > 0 ? fmtMins(t, r.approvedMinutes) : '—'}</td>
              <td className="px-3 py-2 text-slate-500">{t(SOURCE_LABEL[r.source] ?? r.source)}</td>
              <td className="px-3 py-2"><StatusBadge status={r.status} /></td>
              {showActions && (
                <td className="px-3 py-2">
                  {r.status.startsWith('Pending') && onApprove && onReject ? (
                    <div className="flex gap-2">
                      <button type="button" onClick={() => onApprove(r)} className={`${btn.sm} text-emerald-600`}><CheckCircle2 className="h-3 w-3" aria-hidden="true" /> {t('Approve')}</button>
                      <button type="button" onClick={() => onReject(r)} className={`${btn.sm} text-rose-500`}><XCircle className="h-3 w-3" aria-hidden="true" /> {t('Reject')}</button>
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
  const t = useT();
  const fx = useFormat();
  const [empId, setEmpId] = useState(selfEmployeeId ? String(selfEmployeeId) : '');
  const [myOtFilterEmp, setMyOtFilterEmp] = useState<SelectedEmployee | null>(null);
  const [statusFilter, setStatusFilter] = useState('');

  useEffect(() => { if (myOtFilterEmp) setEmpId(String(myOtFilterEmp.id)); }, [myOtFilterEmp]);

  // One page at a time with the server's total: this used to take the first 100 as "the" list.
  const eid = (isEmployee && selfEmployeeId) ? selfEmployeeId : (empId ? Number(empId) : undefined);
  const list = usePagedList<OvertimeRequest>((page, pageSize) =>
    overtimeApi.requests({ employeeId: eid, status: statusFilter || undefined, page, pageSize }));
  const { items: requests, loading, reload } = list;
  const load = () => { void reload(); };
  useEffect(() => { void reload(); }, [statusFilter, reload]);
  const count = list.total ?? requests.length;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        {!isEmployee && <div className="w-60"><EmployeePicker value={myOtFilterEmp} onChange={emp => { setMyOtFilterEmp(emp); if (!emp) setEmpId(''); }} placeholder={t('Filter by employee…')} /></div>}
        <select aria-label={t('Filter by status')} className={`${sel} w-48`} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
          <option value="">{t('All Statuses')}</option>
          {PENDING_STATUSES.map(s => <option key={s} value={s}>{enumLabel(t, 'OvertimeStatus', s)}</option>)}
        </select>
        {!isEmployee && <button type="button" className={btn.primary} onClick={load}>{t('Search')}</button>}
        <p className="ms-auto text-sm text-slate-400">{t('{count, plural, one {# request} other {# requests}}', { count })}</p>
      </div>
      {list.error != null && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{t('Overtime requests could not be loaded. {reason}', { reason: requestFailureReason(list.error) })}</p>}
      <div className="surface overflow-hidden">
        {loading ? <p className="p-8 text-center text-sm text-slate-400">{t('Loading…')}</p> : <OTRequestsTable requests={requests} />}
        <ListWindowFooter shown={requests.length} total={list.total} noun="requests" labels={footerLabels(t, fx, requests.length, list.total)} loadingMore={list.loadingMore} onLoadMore={() => void list.loadMore()} />
      </div>
    </div>
  );
}

// ── Team OT Tab ─────────────────────────────────────────────────────────────────

function TeamOTTab() {
  const t = useT();
  const fx = useFormat();
  const [statusFilter, setStatusFilter] = useState('');
  // Attendance-derived overtime is now capped by the same policy a hand-keyed request obeys. What
  // the policy will not pay is worked time, so it is shown to the person who ran the detection
  // rather than dropped — the whole point of capping instead of truncating.
  const [capped, setCapped] = useState<OvertimeCapException[]>([]);
  const [detectError, setDetectError] = useState('');

  const detect = async () => {
    setDetectError('');
    try {
      const result = await overtimeApi.detectFromAttendance(today, today);
      setCapped(result.capped);
      load();
    } catch {
      setDetectError(t('Detection failed. An active overtime policy is required before attendance can raise overtime.'));
    }
  };

  // One page at a time with the server's total: this used to take the first 200 as "the" list.
  const list = usePagedList<OvertimeRequest>((page, pageSize) =>
    overtimeApi.requests({ status: statusFilter || undefined, page, pageSize }));
  const { items: requests, loading, reload } = list;
  const load = () => { void reload(); };
  useEffect(() => { void reload(); }, [statusFilter, reload]);
  const count = list.total ?? requests.length;

  const approve = async (r: OvertimeRequest) => {
    try { await overtimeApi.approve(r.id, r.requestedMinutes, 'Approved'); load(); } catch (e) { notifyApiError(e, t('Approval failed.')); }
  };
  // Rejection asks for a reason in an accessible dialog (it used to be a native prompt()).
  const [rejectTarget, setRejectTarget] = useState<OvertimeRequest | null>(null);
  const [rejectReason, setRejectReason] = useState('');
  const [rejecting, setRejecting] = useState(false);
  const reject = (r: OvertimeRequest) => { setRejectReason(''); setRejectTarget(r); };
  const doReject = async () => {
    if (!rejectTarget) return;
    setRejecting(true);
    try { await overtimeApi.reject(rejectTarget.id, rejectReason); setRejectTarget(null); load(); } catch (e) { notifyApiError(e, t('Rejection failed.')); }
    setRejecting(false);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <select aria-label={t('Filter by status')} className={`${sel} w-48`} value={statusFilter} onChange={e => setStatusFilter(e.target.value)}>
          <option value="">{t('All Statuses')}</option>
          {PENDING_STATUSES.map(s => <option key={s} value={s}>{enumLabel(t, 'OvertimeStatus', s)}</option>)}
        </select>
        <button type="button" className={btn.ghost} onClick={detect}>
          <RefreshCw className="h-4 w-4" aria-hidden="true" /> {t('Detect from Attendance')}
        </button>
        <p className="ms-auto text-sm text-slate-400">{t('{count, plural, one {# request} other {# requests}}', { count })}</p>
      </div>
      {list.error != null && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{t('Overtime requests could not be loaded. {reason}', { reason: requestFailureReason(list.error) })}</p>}
      {detectError && (
        <div className="surface border-s-4 border-s-rose-500 p-4 text-sm text-rose-600 dark:text-rose-400">{detectError}</div>
      )}
      {capped.length > 0 && (
        <div className="surface border-s-4 border-s-amber-500 p-4">
          <div className="flex items-start gap-2">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-500" />
            <div className="space-y-2">
              <p className="text-sm font-semibold text-slate-900 dark:text-white">
                {t('{count, plural, one {# attendance day was not paid in full by the overtime policy} other {# attendance days were not paid in full by the overtime policy}}', { count: capped.length })}
              </p>
              <ul className="space-y-1 text-sm text-slate-600 dark:text-slate-300">
                {capped.map(c => (
                  <li key={c.id}>
                    <span className="font-medium">{t('Emp #{id}', { id: c.employeeId })} · {fmtDate(fx, c.workDate)}</span> — {c.details}
                  </li>
                ))}
              </ul>
              <p className="text-xs text-slate-400">

                {t('These are saved as attendance exceptions so they can be resolved rather than lost.')}
              </p>
            </div>
          </div>
        </div>
      )}
      <div className="surface overflow-hidden">
        {loading ? <p className="p-8 text-center text-sm text-slate-400">{t('Loading…')}</p> : <OTRequestsTable requests={requests} onApprove={approve} onReject={reject} showActions />}
        <ListWindowFooter shown={requests.length} total={list.total} noun="requests" labels={footerLabels(t, fx, requests.length, list.total)} loadingMore={list.loadingMore} onLoadMore={() => void list.loadMore()} />
      </div>
      {rejectTarget && (
        <Modal title={t('Reject OT — {name}', { name: rejectTarget.employeeName || `#${rejectTarget.employeeId}` })} onClose={() => { if (!rejecting) setRejectTarget(null); }}>
          <div className="space-y-4">
            <Field label={t('Rejection reason')}>
              <textarea className={inp} rows={3} value={rejectReason} onChange={e => setRejectReason(e.target.value)} placeholder={t('Reason for rejection…')} />
            </Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setRejectTarget(null)} disabled={rejecting}>{t('Cancel')}</button>
              <button type="button" className={btn.danger} onClick={() => void doReject()} disabled={rejecting}>{rejecting ? t('Rejecting…') : t('Reject')}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Approvals Tab ───────────────────────────────────────────────────────────────

function ApprovalsTab({ isAdmin, isHRManager, isManager }: { isAdmin: boolean; isHRManager: boolean; isManager: boolean }) {
  const t = useT();
  const fx = useFormat();
  const isFinalApprover = isAdmin || isHRManager;
  // Approve/forward and reject are `overtime.approve` on the API (OvertimeController). A Supervisor
  // sees this queue but does not hold the key, so the buttons only ever returned 403.
  const { hasPermission } = useAuth();
  const canDecide = hasPermission('overtime.approve');
  const [requests, setRequests] = useState<OvertimeRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [approveModal, setApproveModal] = useState<OvertimeRequest | null>(null);
  const [approveMinutes, setApproveMinutes] = useState('');
  const [approveNotes, setApproveNotes] = useState('');
  const [rejectModal, setRejectModal] = useState<OvertimeRequest | null>(null);
  const [rejectNotes, setRejectNotes] = useState('');
  const [saving, setSaving] = useState(false);
  // A failed load says so; "No pending overtime approvals" after a failure would tell an approver the queue is clear.
  const [loadError, setLoadError] = useState<unknown>(null);

  const load = async () => {
    setLoading(true);
    try {
      if (isAdmin) {
        // The whole queue, page by page: an approver must see every pending request, not the first 100.
        const [r1, r2] = await Promise.all([
          overtimeApi.allRequests({ status: 'PendingManager' }),
          overtimeApi.allRequests({ status: 'PendingHR' }),
        ]);
        setRequests([...r1, ...r2].sort((a, b) => (a.workDate < b.workDate ? 1 : -1)));
      } else {
        const status = isHRManager ? 'PendingHR' : 'PendingManager';
        setRequests(await overtimeApi.allRequests({ status }));
      }
      setLoadError(null);
    } catch (e) { setRequests([]); setLoadError(e); }
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
    } catch (e) { notifyApiError(e, t('Approval failed.')); }
    setSaving(false);
  };

  const doReject = async () => {
    if (!rejectModal) return;
    setSaving(true);
    try {
      await overtimeApi.reject(rejectModal.id, rejectNotes || undefined);
      setRejectModal(null);
      load();
    } catch (e) { notifyApiError(e, t('Rejection failed.')); }
    setSaving(false);
  };

  const queueTitle = isAdmin ? t('All Pending Approvals') : isHRManager ? t('Pending HR Approval — Step 2') : t('Pending Your Approval — Step 1');
  const queueSubtitle = isAdmin
    ? t('As admin, approving any request finalises it immediately, bypassing the standard HR step.')
    : isHRManager
    ? t('These requests have been forwarded by a manager. Adjust hours if needed, then approve or reject.')
    : t('Approving forwards the request to HR for final review and payroll calculation.');

  return (
    <div className="space-y-4">
      <div>
        <p className="text-sm font-semibold text-slate-800 dark:text-white">{queueTitle}</p>
        <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{queueSubtitle}</p>
      </div>

      {loading ? (
        <p className="text-sm text-slate-400">{t('Loading…')}</p>
      ) : loadError != null ? (
        <div className="surface"><LoadFailedNotice error={loadError} onRetry={() => { void load(); }} /></div>
      ) : requests.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <CheckCircle2 className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">
            {isHRManager ? t('No overtime awaiting HR approval') : t('No pending overtime approvals')}
          </p>
        </div>
      ) : (
        <div className="space-y-3">
          {requests.map(r => (
            <div key={r.id} className="surface p-5">
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{r.employeeName || t('Employee #{id}', { id: r.employeeId })}</p>
                    <StatusBadge status={r.status} />
                    {isAdmin && (
                      <span className={`rounded px-1.5 py-0.5 text-caption font-semibold ${r.status === 'PendingManager' ? 'bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400' : 'bg-orange-100 text-orange-600 dark:bg-orange-500/20 dark:text-orange-400'}`}>
                        {r.status === 'PendingManager' ? t('Step 1 — Manager') : t('Step 2 — HR')}
                      </span>
                    )}
                  </div>
                  <div className="mt-1.5 flex flex-wrap items-center gap-3">
                    <span className="text-xs text-slate-500">{fmtDate(fx, r.workDate)}</span>
                    <span className="rounded bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-700 dark:bg-white/10 dark:text-slate-300">
                      {fmtMins(t, r.requestedMinutes)}
                    </span>
                    <span className="text-xs text-slate-500">{t(SOURCE_LABEL[r.source] ?? r.source)}</span>
                  </div>
                  {r.reason && <p className="mt-2 text-xs text-slate-500">“{r.reason}”</p>}
                </div>
                {canDecide && <div className="flex shrink-0 gap-2">
                  <button type="button" className={btn.ghost} onClick={() => { setRejectModal(r); setRejectNotes(''); }}>{t('Reject')}</button>
                  <button type="button" className={btn.primary} onClick={() => openApprove(r)}>
                    {isFinalApprover ? t('Approve') : t('Forward to HR')}
                  </button>
                </div>}
              </div>
            </div>
          ))}
        </div>
      )}

      {approveModal && (
        <Modal
          title={isFinalApprover ? t('Approve OT — {name}', { name: approveModal.employeeName || `#${approveModal.employeeId}` }) : t('Forward to HR — {name}', { name: approveModal.employeeName || `#${approveModal.employeeId}` })}
          onClose={() => setApproveModal(null)}
        >
          <div className="space-y-4">
            {isFinalApprover ? (
              <>
                <Field label={t('Approved Minutes')} info={t('Adjust if the actual overtime differs from what was requested.')}>
                  <input type="number" aria-label={t('Approved minutes')} className={inp} value={approveMinutes} onChange={e => setApproveMinutes(e.target.value)} />
                </Field>
                <p className="text-xs text-slate-400">
                  {t('Requested: {requested} · Will approve: {approved}', { requested: fmtMins(t, approveModal.requestedMinutes), approved: fmtMins(t, Number(approveMinutes) || approveModal.requestedMinutes) })}
                </p>
                {isAdmin && approveModal.status === 'PendingManager' && (
                  <div className="rounded-lg bg-blue-50 px-4 py-3 text-xs text-blue-700 dark:bg-blue-500/10 dark:text-blue-400">

                    {t('Admin override — this will skip the manager→HR chain and approve immediately.')}
                  </div>
                )}
              </>
            ) : (
              <div className="rounded-lg bg-amber-50 px-4 py-3 dark:bg-amber-500/10">
                <p className="text-sm font-medium text-amber-700 dark:text-amber-400">{t('Forwarding to HR for final approval')}</p>
                <p className="mt-1 text-xs text-amber-600 dark:text-amber-500">{t('The HR team will review, set the final approved hours, and trigger payroll calculation.')}</p>
              </div>
            )}
            <Field label={t('Notes (optional)')}>
              <textarea className={inp} rows={2} value={approveNotes} onChange={e => setApproveNotes(e.target.value)} placeholder={t('Add notes for the next reviewer…')} />
            </Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setApproveModal(null)}>{t('Cancel')}</button>
              <button type="button" className={btn.primary} onClick={doApprove} disabled={saving}>
                {saving ? t('Processing…') : isFinalApprover ? t('Approve & Calculate') : t('Forward to HR')}
              </button>
            </div>
          </div>
        </Modal>
      )}

      {rejectModal && (
        <Modal title={t('Reject OT — {name}', { name: rejectModal.employeeName || `#${rejectModal.employeeId}` })} onClose={() => setRejectModal(null)}>
          <div className="space-y-4">
            <Field label={t('Rejection Reason *')}>
              <textarea className={inp} rows={3} value={rejectNotes} onChange={e => setRejectNotes(e.target.value)} placeholder={t('Reason for rejection…')} />
            </Field>
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setRejectModal(null)}>{t('Cancel')}</button>
              <button type="button" className={btn.danger} onClick={doReject} disabled={saving}>{saving ? t('Rejecting…') : t('Reject')}</button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Policies Tab ────────────────────────────────────────────────────────────────

function CreatePolicyModal({ existing, onClose, onSaved }: { existing?: OvertimePolicy | null; onClose: () => void; onSaved: () => void }) {
  const t = useT();
  const [form, setForm] = useState(existing ? {
    code: existing.code, name: existing.name, hourlyRateBasis: existing.hourlyRateBasis, fixedHourlyRate: existing.fixedHourlyRate,
    standardMonthlyHours: existing.standardMonthlyHours, minimumMinutes: existing.minimumMinutes, maximumMinutesPerDay: existing.maximumMinutesPerDay,
    monthlyCapMinutes: existing.monthlyCapMinutes, roundingRule: existing.roundingRule,
    requiresApproval: existing.requiresApproval, allowCompOffConversion: existing.allowCompOffConversion,
    // Real saved values are loaded when the editor opens (see the effect below).
    regularDayMultiplier: 1.25, weekendMultiplier: 1.5, holidayMultiplier: 2.0,
  } : {
    code: '', name: '', hourlyRateBasis: 'BasicSalary', fixedHourlyRate: 0,
    standardMonthlyHours: 240, minimumMinutes: 30, maximumMinutesPerDay: 240,
    monthlyCapMinutes: 3600, roundingRule: 'Nearest15',
    requiresApproval: true, allowCompOffConversion: true,
    regularDayMultiplier: 1.25, weekendMultiplier: 1.5, holidayMultiplier: 2.0,
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [loadingSaved, setLoadingSaved] = useState(!!existing);
  useEffect(() => {
    if (!existing) return;
    let live = true;
    overtimeApi.policy(existing.id)
      .then(p => { if (live) setForm(f => ({ ...f, ...p, code: p.code, name: p.name })); })
      .catch(e => { if (live) setError(apiErrorReason(e, t('Could not load the saved policy values.'))); })
      .finally(() => { if (live) setLoadingSaved(false); });
    return () => { live = false; };
  }, [existing, t]);
  const set = (k: keyof typeof form, v: string | number | boolean) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!form.code || !form.name) { setError(t('Code and name are required.')); return; }
    setSaving(true); setError('');
    try {
      if (existing) await overtimeApi.updatePolicy(existing.id, form); else await overtimeApi.createPolicy(form);
      onSaved();
    } catch (e) { setError(apiErrorReason(e, t('Save failed.'))); setSaving(false); }
  };

  return (
    <Modal title={existing ? t('Edit Overtime Policy') : t('New Overtime Policy')} onClose={onClose} wide>
      <div className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Code *')}><input aria-label={t('Policy code')} className={inp} disabled={!!existing} value={form.code} onChange={e => set('code', e.target.value)} placeholder={t('For example, GCC-OT-STANDARD')} /></Field>
          <Field label={t('Name *')}><input aria-label={t('Policy name')} className={inp} value={form.name} onChange={e => set('name', e.target.value)} placeholder={t('For example, Standard GCC Overtime')} /></Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Hourly Rate Basis')} info={t('How the base hourly rate is derived: from basic salary, gross salary, or a fixed amount you enter below.')} infoKey="overtime.rate_basis">
            <select aria-label={t('Hourly rate basis')} className={sel} value={form.hourlyRateBasis} onChange={e => set('hourlyRateBasis', e.target.value)}>
              {['BasicSalary', 'GrossSalary', 'FixedHourlyRate'].map(b => <option key={b} value={b}>{t(BASIS_LABEL[b])}</option>)}
            </select>
          </Field>
          {form.hourlyRateBasis === 'FixedHourlyRate' && (
            <Field label={t('Fixed Hourly Rate')}><input type="number" step="0.01" aria-label={t('Fixed hourly rate')} className={inp} value={form.fixedHourlyRate} onChange={e => set('fixedHourlyRate', Number(e.target.value))} /></Field>
          )}
          <Field label={t('Standard Monthly Hours')}><input type="number" aria-label={t('Standard monthly hours')} className={inp} value={form.standardMonthlyHours} onChange={e => set('standardMonthlyHours', Number(e.target.value))} /></Field>
        </div>

        <div className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
          <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">{t('OT Multipliers (× Hourly Rate)')}</p>
          <div className="grid grid-cols-3 gap-3">
            <Field label={t('Regular Day')}><input type="number" step="0.05" aria-label={t('Regular day multiplier')} className={inp} value={form.regularDayMultiplier} onChange={e => set('regularDayMultiplier', Number(e.target.value))} /></Field>
            <Field label={t('Weekend')}><input type="number" step="0.05" aria-label={t('Weekend multiplier')} className={inp} value={form.weekendMultiplier} onChange={e => set('weekendMultiplier', Number(e.target.value))} /></Field>
            <Field label={t('Public Holiday')}><input type="number" step="0.05" aria-label={t('Public holiday multiplier')} className={inp} value={form.holidayMultiplier} onChange={e => set('holidayMultiplier', Number(e.target.value))} /></Field>
          </div>
        </div>

        <div className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
          <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">{t('Caps & Rules')}</p>
          <div className="grid grid-cols-3 gap-3">
            <Field label={t('Min Minutes')}><input type="number" aria-label={t('Minimum minutes')} className={inp} value={form.minimumMinutes} onChange={e => set('minimumMinutes', Number(e.target.value))} /></Field>
            <Field label={t('Max Min/Day')}><input type="number" aria-label={t('Maximum minutes per day')} className={inp} value={form.maximumMinutesPerDay} onChange={e => set('maximumMinutesPerDay', Number(e.target.value))} /></Field>
            <Field label={t('Monthly Cap (min)')}><input type="number" aria-label={t('Monthly cap in minutes')} className={inp} value={form.monthlyCapMinutes} onChange={e => set('monthlyCapMinutes', Number(e.target.value))} /></Field>
          </div>
          <div className="mt-3 grid grid-cols-2 gap-2">
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form.requiresApproval} onChange={e => set('requiresApproval', e.target.checked)} className="rounded" />

              {t('Requires Manager Approval')}
            </label>
            <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={form.allowCompOffConversion} onChange={e => set('allowCompOffConversion', e.target.checked)} className="rounded" />

              {t('Allow Comp-Off Conversion')}
            </label>
          </div>
        </div>

        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving || loadingSaved}>{saving ? t('Saving…') : existing ? t('Save Policy') : t('Create Policy')}</button>
        </div>
      </div>
    </Modal>
  );
}

/** Overtime policies, shown as the Overtime policies section of Setup. */
export function PoliciesTab({ onDirtyChange }: { onDirtyChange?: (dirty: boolean) => void } = {}) {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [policies, setPolicies] = useState<OvertimePolicy[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [showCreate, setShowCreate] = useState(false);
  const [editing, setEditing] = useState<OvertimePolicy | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  useLayoutEffect(() => { onDirtyChange?.(showCreate || editing !== null); return () => onDirtyChange?.(false); }, [showCreate, editing, onDirtyChange]);

  const load = () => {
    setLoading(true);
    overtimeApi.policies()
      .then(rows => { setPolicies(rows); setLoadError(null); })
      .catch(error => { setPolicies([]); setLoadError(error); })
      .finally(() => setLoading(false));
  };
  useEffect(load, []);

  const toggle = async (p: OvertimePolicy) => {
    const turnOff = p.isActive;
    const ok = await confirm({
      title: turnOff ? t('Turn off overtime policy') : t('Turn on overtime policy'),
      message: turnOff
        ? t('Turn off this overtime policy? New requests can no longer use it. Requests already filed keep their figures, and it can be turned back on later.')
        : t('Turn this overtime policy back on?'),
      confirmLabel: turnOff ? t('Turn off') : t('Turn on'),
      tone: turnOff ? 'danger' : undefined,
    });
    if (!ok) return;
    setBusyId(p.id);
    try { await overtimeApi.setPolicyActive(p.id, !turnOff); load(); } catch (e) { notifyApiError(e); }
    finally { setBusyId(null); }
  };
  const remove = async (p: OvertimePolicy) => {
    const ok = await confirm({
      title: t('Delete overtime policy'),
      message: t('Delete this overtime policy? Only a policy no request was ever filed under can be deleted; otherwise turn it off.'),
      confirmLabel: t('Delete'),
      tone: 'danger',
    });
    if (!ok) return;
    setBusyId(p.id);
    try { await overtimeApi.deletePolicy(p.id); load(); } catch (e) { notifyApiError(e); }
    finally { setBusyId(null); }
  };
  const activeCount = policies.filter(p => p.isActive).length;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <p className="text-xs text-slate-500">{activeCount > 1 ? t('When several policies are active, requests without a chosen policy use the oldest active one.') : ''}</p>
        <button type="button" className={btn.primary} onClick={() => setShowCreate(true)}><Plus className="h-4 w-4" aria-hidden="true" /> {t('New Policy')}</button>
      </div>
      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : loadError != null ? (
        <div className="surface"><LoadFailedNotice error={loadError} onRetry={load} /></div>
      ) : policies.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Settings className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No overtime policies yet')}</p>
        </div>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {policies.map(p => (
            <div key={p.id} className="surface p-4">
              <TimerReset className="mb-3 h-5 w-5 text-sapphire dark:text-cyanAccent" />
              <p className="font-semibold text-slate-900 dark:text-white">{p.name}</p>
              <p className="mt-1 text-xs text-slate-500">{p.code}</p>
              <div className="mt-3 space-y-1 text-xs text-slate-500">
                <p>{t('Basis: {value}', { value: t(BASIS_LABEL[p.hourlyRateBasis] ?? p.hourlyRateBasis) })}</p>
                <p>{t('Standard hours: {hours}h/month', { hours: p.standardMonthlyHours })}</p>
                <p>{t('Min: {min} min · Max/day: {max} min', { min: p.minimumMinutes, max: p.maximumMinutesPerDay })}</p>
                <p>{t('Monthly cap: {value}', { value: fmtMins(t, p.monthlyCapMinutes) })}</p>
              </div>
              <div className="mt-3 flex flex-wrap gap-1.5">
                {p.requiresApproval && <span className="rounded bg-amber-50 px-1.5 py-0.5 text-caption font-medium text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">{t('Approval Req.')}</span>}
                {p.allowCompOffConversion && <span className="rounded bg-teal-50 px-1.5 py-0.5 text-caption font-medium text-teal-700 dark:bg-teal-500/10 dark:text-teal-400">{t('Comp-Off')}</span>}
                <span className={`rounded px-1.5 py-0.5 text-caption font-medium ${p.isActive ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400'}`}>{p.isActive ? t('Active') : t('Off')}</span>
              </div>
              <div className="mt-3 flex flex-wrap gap-1.5 border-t border-slate-100 pt-3 dark:border-white/[0.07]">
                <button type="button" className={btn.ghost} onClick={() => setEditing(p)}>{t('Edit')}</button>
                <button type="button" className={btn.ghost} disabled={busyId === p.id} onClick={() => toggle(p)}>{p.isActive ? t('Turn off') : t('Turn on')}</button>
                <button type="button" className={btn.ghost} disabled={busyId === p.id} onClick={() => remove(p)}>{t('Delete')}</button>
              </div>
            </div>
          ))}
        </div>
      )}
      {(showCreate || editing) && <CreatePolicyModal key={editing?.id ?? 'new'} existing={editing} onClose={() => { setShowCreate(false); setEditing(null); }} onSaved={() => { setShowCreate(false); setEditing(null); load(); }} />}
      {confirmDialog}
    </div>
  );
}

// ── Calculation Preview Tab ─────────────────────────────────────────────────────

function CalcPreviewTab() {
  const t = useT();
  const fx = useFormat();
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
        <div className="w-64"><EmployeePicker value={calcPickedEmp} onChange={emp => { setCalcPickedEmp(emp); if (!emp) setEmpId(''); }} placeholder={t('Filter by employee (optional)…')} /></div>
        <button type="button" className={btn.primary} onClick={search}>{t('Search')}</button>
      </div>
      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : calculations.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Calculator className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No calculations yet — approve overtime requests to see calculations')}</p>
        </div>
      ) : (
        <div className="surface overflow-hidden">
          <table className="w-full min-w-[640px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[msg('Employee'), msg('Hours'), msg('Hourly Rate'), msg('Multiplier'), msg('Amount'), msg('Currency'), msg('Calculated')].map(h => (
                  <th key={h} className="px-3 py-2 text-start text-xs font-semibold uppercase text-slate-400">{t(h)}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {calculations.map(c => (
                <tr key={c.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-3 py-2 text-slate-700 dark:text-slate-300">{t('Emp #{id}', { id: c.employeeId })}</td>
                  <td className="px-3 py-2 font-medium text-slate-900 dark:text-white">{t('{hours}h', { hours: fmtHours(c.approvedHours) })}</td>
                  <td className="px-3 py-2 text-slate-500">{c.hourlyRate.toFixed(2)}</td>
                  <td className="px-3 py-2 text-slate-500">×{c.multiplier}</td>
                  <td className="px-3 py-2 font-semibold text-emerald-600 dark:text-emerald-400">{c.amount.toFixed(2)}</td>
                  <td className="px-3 py-2 text-slate-500">{c.currency}</td>
                  <td className="px-3 py-2 text-xs text-slate-400">{fmtDate(fx, c.createdAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="surface p-4">
        <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">{t('Formula Reference')}</p>
        {/*
          Audit O12. This block used to read "OT Pay = Approved Hours × Hourly Rate × Multiplier",
          which is not what is paid and never was: the engine applies
          base hourly + basic hourly × (multiplier − 1) — KSA Art. 107's "hourly wage plus 50% of
          his basic wage". On the canonical 60/40 package the old shape produced 187.50 for an hour
          actually paid at 162.50. It is the same wrong arithmetic the payslip line used to print, so
          it changes with it. The multipliers are described as the configured/statutory floor rather
          than restated as hard-coded numbers, which is what made them drift in the first place.
        */}
        <div className="rounded-lg bg-slate-50 p-4 font-mono text-sm text-slate-700 dark:bg-white/5 dark:text-slate-300">

          {t('OT Pay = Approved Hours × (Base Hourly + Basic Hourly × (Multiplier − 1))')}
          <br /><br />
          <span className="text-xs text-slate-400">
            {t('Base Hourly = the rate the hour starts from — the full wage where the country pack sets one (Saudi Arabia), otherwise basic. Basic Hourly = Basic Salary ÷ Standard Monthly Hours, the base the uplift is measured on.')}<br />
            {t('Multiplier = the rate configured for the day worked (regular day, weekend, public holiday), never below the statutory floor for that day.')}<br />
            {t('Example (basic 18,000 + allowances 12,000 over 240 h, regular day at 1.5×): 1 h × (125.00 + 75.00 × 0.50) = 162.50.')}
          </span>
        </div>
      </div>
    </div>
  );
}

// ── Payroll Review Tab ──────────────────────────────────────────────────────────

function PayrollReviewTab() {
  const t = useT();
  const fx = useFormat();
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
        <KpiCard label={t('Pending Payroll Items')} value={impacts.length} icon={WalletCards} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
        <KpiCard label={t('Total OT Amount')} value={fmtAmt(fx, total, currencyCode)} icon={TrendingUp} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
        <KpiCard label={t('Comp-Off Pending')} value={conversions.filter(c => c.status === 'Pending').length} icon={Layers3} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
      </div>

      <div className="surface overflow-hidden">
        <div className="border-b border-slate-100 px-5 py-3 dark:border-white/5">
          <p className="text-sm font-semibold text-slate-800 dark:text-white">{t('Overtime Pending Payroll Import')}</p>
          <p className="text-xs text-slate-400">{t('These approved OT amounts will be included in the next payroll run.')}</p>
        </div>
        {loading ? <p className="p-8 text-center text-sm text-slate-400">{t('Loading…')}</p> : impacts.length === 0 ? (
          <p className="p-8 text-center text-sm text-slate-400">{t('No overtime pending payroll import.')}</p>
        ) : (
          <table className="w-full min-w-[480px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[msg('Employee'), msg('OT Hours'), msg('Amount'), msg('Status'), msg('Created')].map(h => (
                  <th key={h} className="px-4 py-2 text-start text-xs font-semibold uppercase text-slate-400">{t(h)}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {impacts.map(i => (
                <tr key={i.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-2 font-medium text-slate-900 dark:text-white">{t('Emp #{id}', { id: i.employeeId })}</td>
                  <td className="px-4 py-2 text-slate-500">{t('{hours}h', { hours: fmtHours(i.hours) })}</td>
                  <td className="px-4 py-2 font-semibold text-emerald-600 dark:text-emerald-400">{i.amount.toFixed(2)}</td>
                  <td className="px-4 py-2"><StatusBadge status={i.status} /></td>
                  <td className="px-4 py-2 text-xs text-slate-400">{fmtDate(fx, i.createdAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {conversions.length > 0 && (
        <div className="surface overflow-hidden">
          <div className="border-b border-slate-100 px-5 py-3 dark:border-white/5">
            <p className="text-sm font-semibold text-slate-800 dark:text-white">{t('Comp-Off Conversions')}</p>
          </div>
          <table className="w-full min-w-[480px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[msg('Employee'), msg('OT Hours'), msg('Comp-Off Days'), msg('Status')].map(h => (
                  <th key={h} className="px-4 py-2 text-start text-xs font-semibold uppercase text-slate-400">{t(h)}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {conversions.map(c => (
                <tr key={c.id}>
                  <td className="px-4 py-2 font-medium text-slate-900 dark:text-white">{t('Emp #{id}', { id: c.employeeId })}</td>
                  <td className="px-4 py-2 text-slate-500">{t('{hours}h', { hours: c.overtimeHours })}</td>
                  <td className="px-4 py-2 text-slate-500">{t('{days}d', { days: c.compOffDays })}</td>
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
  const t = useT();
  const fx = useFormat();
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
        <Field label={t('From')}><input type="date" aria-label={t('From date')} className={`${inp} w-44`} value={from} onChange={e => setFrom(e.target.value)} /></Field>
        <Field label={t('To')}><input type="date" aria-label={t('To date')} className={`${inp} w-44`} value={to} onChange={e => setTo(e.target.value)} /></Field>
        <button type="button" className={`${btn.primary} mt-4`} onClick={load}>{t('Run Report')}</button>
      </div>

      {summary && (
        <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
          <KpiCard label={t('Total Requests')} value={summary.totalRequests} icon={FileClock} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
          <KpiCard label={t('Approved')} value={summary.approvedRequests} icon={CheckCircle2} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
          <KpiCard label={t('Pending')} value={summary.pendingRequests} icon={Clock3} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
          <KpiCard label={t('Approved Hours')} value={t('{hours}h', { hours: fmtHours(summary.approvedHours) })} icon={TrendingUp} color="bg-cyan-100 text-cyan-600 dark:bg-cyan-500/20 dark:text-cyan-400" />
        </div>
      )}

      <div className="surface p-5">
        <p className="mb-3 text-sm font-semibold text-slate-800 dark:text-white">{t('OT Payroll Amount')}</p>
        {summary ? (
          <p className="text-3xl font-semibold tabular-nums text-emerald-600 dark:text-emerald-400">
            {fmtAmt(fx, summary.payrollAmount, currencyCode)}
          </p>
        ) : (
          <p className="text-sm text-slate-400">{t('Run the report to see figures.')}</p>
        )}
        <p className="mt-2 text-xs text-slate-400">{t('Total approved overtime payroll amount for the selected period.')}</p>
      </div>

      <div className="surface p-5">
        <div className="flex items-start gap-3">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-500" />
          <div>
            <p className="text-sm font-semibold text-slate-800 dark:text-white">{t('OT Anomaly Alerts')}</p>
            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">

              {t('If approved overtime payout exceeds 35% of regular gross earnings for any employee, the payroll engine flags this as a warning. Review the Payroll Validation tab after processing.')}
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}

// ── Main Page ───────────────────────────────────────────────────────────────────

export function OvertimePage() {
  const t = useT();
  const { user, hasPermission } = useAuth();
  const { currencyCode } = useTenantSettings();
  const isAdmin    = user?.roles.some(r => r === 'Admin') ?? false;
  const isHRManager = user?.roles.some(r => r === 'HR Manager') ?? false;
  const isManager  = !isAdmin && !isHRManager && (user?.roles.some(r => ['Manager', 'Supervisor'].includes(r)) ?? false);
  const isEmployee = !isAdmin && !isHRManager && !isManager;
  const selfEmployeeId = (user as { employeeId?: number } | undefined)?.employeeId;

  const [activeTab, setActiveTab] = useState<Tab>('dashboard');

  const visibleTabs = TABS.filter(tab => {
    // Per-employee overtime pay: the API requires payroll.read.
    if (tab.key === 'payroll-review' && !hasPermission('payroll.read')) return false;
    if (isEmployee) return ['dashboard', 'submit', 'my-ot'].includes(tab.key);
    if (isManager)  return ['dashboard', 'submit', 'my-ot', 'team-ot', 'approvals'].includes(tab.key);
    return true; // HR Manager & Admin see all tabs
  });

  const roleBadge = isAdmin
    ? <span className="rounded-lg bg-blue-100 px-2 py-0.5 text-xs font-semibold text-blue-700 dark:bg-blue-500/15 dark:text-blue-400">{t('Admin')}</span>
    : isHRManager
    ? <span className="rounded-lg bg-purple-100 px-2 py-0.5 text-xs font-semibold text-purple-700 dark:bg-purple-500/15 dark:text-purple-400">{t('HR Manager')}</span>
    : isManager
    ? <span className="rounded-lg bg-amber-100 px-2 py-0.5 text-xs font-semibold text-amber-700 dark:bg-amber-500/15 dark:text-amber-400">{t('Manager')}</span>
    : <span className="rounded-lg bg-emerald-100 px-2 py-0.5 text-xs font-semibold text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-400">{t('Employee')}</span>;

  const renderTab = () => {
    switch (activeTab) {
      case 'dashboard':     return <DashboardTab onNavigate={setActiveTab} />;
      case 'submit':        return <SubmitOTTab selfEmployeeId={selfEmployeeId} isEmployee={isEmployee} />;
      case 'my-ot':         return <MyOTTab selfEmployeeId={selfEmployeeId} isEmployee={isEmployee} />;
      case 'team-ot':       return <TeamOTTab />;
      case 'approvals':     return <ApprovalsTab isAdmin={isAdmin} isHRManager={isHRManager} isManager={isManager} />;
      case 'calc-preview':  return <CalcPreviewTab />;
      case 'payroll-review': return <PayrollReviewTab />;
      case 'reports':       return <ReportsTab />;
    }
  };

  return (
    <div className="space-y-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold text-slate-950 dark:text-white">{t('Overtime Management')}</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
            {isEmployee   ? t('Submit and track your overtime requests.') :
             isManager    ? t('Approve team overtime and forward to HR for final review.') :
             isHRManager  ? t('Final approval, hour adjustments, calculations, and payroll review.') :
             t('GCC overtime policies, approvals, calculations, and payroll integration.')}
          </p>
        </div>
        <div className="flex shrink-0 flex-wrap items-center justify-end gap-2">
          <ModuleSettingsLink sections={['overtimePolicies']} label={msg('Overtime settings')} />
          {roleBadge}
        </div>
      </div>

      <div className="flex gap-1 overflow-x-auto rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/5">
        {visibleTabs.map(tab => (
          <button
            key={tab.key}
            type="button"
            onClick={() => setActiveTab(tab.key)}
            className={`flex shrink-0 items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-medium transition-colors ${activeTab === tab.key ? 'bg-white shadow-sm text-sapphire dark:bg-slate-800 dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'}`}
          >
            <tab.icon className="h-3.5 w-3.5" aria-hidden="true" />
            {t(tab.label)}
          </button>
        ))}
      </div>

      <div>{renderTab()}</div>
    </div>
  );
}
