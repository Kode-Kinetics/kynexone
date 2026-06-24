'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  Plus, ChevronLeft, Users, Briefcase, ClipboardList, UserPlus,
  CheckCircle, Clock, XCircle, ChevronRight, X, Star, Send,
  FileText, AlertCircle, ArrowRight, Calendar, BarChart3, Bot,
  Target, BookOpen, Award, TrendingUp, LayoutGrid,
} from 'lucide-react';
import { ImportExportToolbar, downloadCsv } from '../components/ImportExportToolbar';
import client from '../api/client';

// ── Recruitment import/export helpers ─────────────────────────────────────────

const jobOpeningsImportExport = {
  export: async () => {
    const csv = await client.get<string>('/api/recruitment/openings/export', { responseType: 'text' }).then(r => r.data);
    downloadCsv(csv, 'job-openings.csv');
  },
  template: async () => {
    const csv = await client.get<string>('/api/recruitment/openings/import-template', { responseType: 'text' }).then(r => r.data);
    downloadCsv(csv, 'job-openings-template.csv');
  },
  import: (csvContent: string) =>
    client.post<{ received: number; created: number; skipped: number; errors: string[] }>('/api/recruitment/openings/import', { csvContent }).then(r => r.data),
};
import {
  requisitionsApi, openingsApi, candidatesApi, applicationsApi,
  workforcePlanningApi, interviewsApi, assessmentsApi, offersApi,
  onboardingApi, recruitmentReportsApi,
} from '../api/recruitment';
import type {
  ManpowerRequisition, JobOpening, Candidate, JobApplication,
  ApplicationDetail, KanbanStage, OfferLetter,
  RecruitmentStats, RequisitionStats,
  WorkforcePlan, InterviewSchedule as ExtInterviewSchedule,
  CandidateAssessment, OnboardingTask,
} from '../api/recruitment';
import { StatusChip } from '../components/StatusChip';
import { useTenantSettings } from '../contexts/TenantSettingsContext';

// ── Helpers ───────────────────────────────────────────────────────────────────

function ago(utc: string) {
  const d = Math.floor((Date.now() - new Date(utc).getTime()) / 86400000);
  if (d === 0) return 'Today';
  if (d === 1) return 'Yesterday';
  return `${d}d ago`;
}

function fmt(n: number | null | undefined) {
  if (n == null) return '—';
  return n.toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 0 });
}

const PRIORITY_COLORS: Record<string, string> = {
  Low: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
  Medium: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  High: 'bg-orange-50 text-orange-700 dark:bg-orange-500/10 dark:text-orange-400',
  Critical: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
};

const STAGE_COLORS: Record<string, string> = {
  Applied: '#64748b',
  Screening: '#2F6BFF',
  Assessment: '#f59e0b',
  Interview: '#8b5cf6',
  Offer: '#00C896',
  Hired: '#10b981',
};

const reqStatusTone = (s: string) => {
  switch (s) {
    case 'Approved': return { label: 'Approved', tone: 'emerald' as const };
    case 'Rejected': return { label: 'Rejected', tone: 'rose' as const };
    case 'Submitted':
    case 'PendingApproval': return { label: 'Pending', tone: 'amber' as const };
    case 'Converted': return { label: 'Converted', tone: 'blue' as const };
    default: return { label: s, tone: 'slate' as const };
  }
};

const openingStatusTone = (s: string) => {
  switch (s) {
    case 'Open': return { label: 'Open', tone: 'emerald' as const };
    case 'InProgress': return { label: 'In Progress', tone: 'blue' as const };
    case 'OnHold': return { label: 'On Hold', tone: 'amber' as const };
    case 'Closed': return { label: 'Closed', tone: 'slate' as const };
    default: return { label: s, tone: 'slate' as const };
  }
};

// ── KPI Card ──────────────────────────────────────────────────────────────────

function KpiCard({ label, value, sub, icon: Icon, color }: {
  label: string; value: number | string; sub?: string;
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

// ── Overview Tab ───────────────────────────────────────────────────────────────

function OverviewTab({ onNavigate }: { onNavigate: (tab: Tab) => void }) {
  const [stats, setStats] = useState<RecruitmentStats | null>(null);
  const [reqStats, setReqStats] = useState<RequisitionStats | null>(null);
  const [recentOpenings, setRecentOpenings] = useState<JobOpening[]>([]);

  useEffect(() => {
    openingsApi.stats().then(setStats).catch(() => {});
    requisitionsApi.stats().then(setReqStats).catch(() => {});
    openingsApi.list({ status: 'Open' }).then(r => setRecentOpenings(r.items.slice(0, 5))).catch(() => {});
  }, []);

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <KpiCard label="Open Positions" value={stats?.openPositions ?? '—'} icon={Briefcase} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
        <KpiCard label="Active Applications" value={stats?.activeApplications ?? '—'} icon={Users} color="bg-emeraldZ/10 text-emeraldZ dark:bg-emeraldZ/20" />
        <KpiCard label="Offers Pending" value={stats?.offersPending ?? '—'} icon={FileText} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
        <KpiCard label="Hired This Month" value={stats?.hiredThisMonth ?? '—'} icon={CheckCircle} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        {/* Requisition summary */}
        <div className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">Requisition Pipeline</h3>
            <button type="button" onClick={() => onNavigate('requisitions')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">View all</button>
          </div>
          <div className="space-y-3">
            {[
              { label: 'Draft', val: reqStats?.draft ?? 0, color: 'bg-slate-200 dark:bg-slate-700' },
              { label: 'Pending Approval', val: reqStats?.pending ?? 0, color: 'bg-amber-400' },
              { label: 'Approved', val: reqStats?.approved ?? 0, color: 'bg-emeraldZ' },
              { label: 'Converted to Openings', val: reqStats?.converted ?? 0, color: 'bg-sapphire' },
            ].map(row => (
              <div key={row.label} className="flex items-center gap-3">
                <div className={`h-2 w-2 shrink-0 rounded-full ${row.color}`} />
                <span className="flex-1 text-sm text-slate-700 dark:text-slate-300">{row.label}</span>
                <span className="text-sm font-semibold tabular-nums text-slate-900 dark:text-white">{row.val}</span>
              </div>
            ))}
          </div>
        </div>

        {/* Active openings */}
        <div className="surface p-5">
          <div className="mb-4 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-slate-800 dark:text-white">Active Job Openings</h3>
            <button type="button" onClick={() => onNavigate('openings')} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">View all</button>
          </div>
          {recentOpenings.length === 0 && <p className="text-sm text-slate-400 dark:text-slate-500">No open positions yet.</p>}
          <div className="space-y-3">
            {recentOpenings.map(o => (
              <div key={o.id} className="flex items-center justify-between">
                <div className="min-w-0">
                  <p className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">{o.title}</p>
                  <p className="text-xs text-slate-400 dark:text-slate-500">{o.departmentName} · {o.activeApplications ?? 0} applicants</p>
                </div>
                <span className="ml-3 shrink-0 text-xs font-semibold text-sapphire dark:text-cyanAccent">{o.remaining ?? o.headCount - o.filledCount} left</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

// ── Create Requisition Modal ───────────────────────────────────────────────────

interface CreateReqModalProps { onClose: () => void; onSaved: () => void; }

function CreateReqModal({ onClose, onSaved }: CreateReqModalProps) {
  const { currencyCode } = useTenantSettings();
  const [form, setForm] = useState({
    departmentName: '', designationTitle: '', headCount: 1, employmentType: 'Full-Time',
    priority: 'Medium', justification: '', requiredSkills: '', minExperienceYears: '',
    maxExperienceYears: '', budgetFrom: '', budgetTo: '', targetJoiningDate: '', requestedByName: '',
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string | number) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!form.departmentName || !form.designationTitle || !form.justification) {
      setError('Department, Designation and Justification are required.');
      return;
    }
    setSaving(true);
    setError('');
    try {
      await requisitionsApi.create({
        departmentName: form.departmentName,
        designationTitle: form.designationTitle,
        headCount: form.headCount,
        employmentType: form.employmentType,
        priority: form.priority,
        justification: form.justification,
        requiredSkills: form.requiredSkills,
        minExperienceYears: form.minExperienceYears ? Number(form.minExperienceYears) : undefined,
        maxExperienceYears: form.maxExperienceYears ? Number(form.maxExperienceYears) : undefined,
        budgetFrom: form.budgetFrom ? Number(form.budgetFrom) : undefined,
        budgetTo: form.budgetTo ? Number(form.budgetTo) : undefined,
        targetJoiningDate: form.targetJoiningDate || undefined,
        requestedByName: form.requestedByName,
      });
      onSaved();
    } catch {
      setError('Failed to create requisition.');
      setSaving(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="flex w-full max-w-lg flex-col rounded-2xl border border-slate-200 bg-white shadow-2xl max-h-[90vh] dark:border-white/10 dark:bg-[#0D1221]">
        <div className="flex shrink-0 items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-white/10">
          <h3 className="font-semibold text-slate-900 dark:text-white">New Manpower Requisition</h3>
          <button type="button" aria-label="Close" onClick={onClose} className="grid h-7 w-7 place-items-center rounded-lg text-slate-400 hover:bg-slate-100 dark:hover:bg-white/10"><X className="h-4 w-4" /></button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto px-6 py-4">
          <div className="space-y-3">
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Department *</label>
                <input className="input w-full" placeholder="e.g. Engineering" value={form.departmentName} onChange={e => set('departmentName', e.target.value)} />
              </div>
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Designation *</label>
                <input className="input w-full" placeholder="e.g. Software Engineer" value={form.designationTitle} onChange={e => set('designationTitle', e.target.value)} />
              </div>
            </div>
            <div className="grid grid-cols-3 gap-3">
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Head Count</label>
                <input type="number" className="input w-full" min={1} value={form.headCount} onChange={e => set('headCount', Number(e.target.value))} aria-label="Head count" />
              </div>
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Type</label>
                <select className="select w-full" value={form.employmentType} onChange={e => set('employmentType', e.target.value)} aria-label="Employment type">
                  {['Full-Time', 'Part-Time', 'Contract', 'Internship'].map(t => <option key={t}>{t}</option>)}
                </select>
              </div>
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Priority</label>
                <select className="select w-full" value={form.priority} onChange={e => set('priority', e.target.value)} aria-label="Priority">
                  {['Low', 'Medium', 'High', 'Critical'].map(p => <option key={p}>{p}</option>)}
                </select>
              </div>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Justification *</label>
              <textarea className="input w-full resize-none" rows={3} placeholder="Business need and justification…" value={form.justification} onChange={e => set('justification', e.target.value)} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Required Skills</label>
              <textarea className="input w-full resize-none" rows={2} placeholder="e.g. React, TypeScript, Node.js…" value={form.requiredSkills} onChange={e => set('requiredSkills', e.target.value)} />
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Budget Range ({currencyCode}/mo)</label>
                <div className="flex items-center gap-1">
                  <input className="input w-full" type="number" placeholder="From" value={form.budgetFrom} onChange={e => set('budgetFrom', e.target.value)} aria-label="Budget from" />
                  <span className="text-slate-400">–</span>
                  <input className="input w-full" type="number" placeholder="To" value={form.budgetTo} onChange={e => set('budgetTo', e.target.value)} aria-label="Budget to" />
                </div>
              </div>
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Target Join Date</label>
                <input type="date" className="input w-full" value={form.targetJoiningDate} onChange={e => set('targetJoiningDate', e.target.value)} aria-label="Target joining date" />
              </div>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Requested By</label>
              <input className="input w-full" placeholder="Your name" value={form.requestedByName} onChange={e => set('requestedByName', e.target.value)} />
            </div>
            {error && <p className="text-xs text-rose-500">{error}</p>}
          </div>
        </div>

        <div className="flex shrink-0 justify-end gap-2 border-t border-slate-100 px-6 py-4 dark:border-white/10">
          <button type="button" className="btn-secondary text-sm" onClick={onClose}>Cancel</button>
          <button type="button" className="btn-primary text-sm" onClick={save} disabled={saving}>{saving ? 'Saving…' : 'Create Requisition'}</button>
        </div>
      </div>
    </div>,
    document.body
  );
}

// ── Requisitions Tab ───────────────────────────────────────────────────────────

function RequisitionsTab({ onCreateOpening }: { onCreateOpening: (req: ManpowerRequisition) => void }) {
  const [items, setItems] = useState<ManpowerRequisition[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [acting, setActing] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const r = await requisitionsApi.list({ status: statusFilter || undefined });
      setItems(r.items); setTotal(r.total);
    } catch { /**/ }
    finally { setLoading(false); }
  }, [statusFilter]);

  useEffect(() => { load(); }, [load]);

  const submit = async (id: string) => {
    setActing(id);
    try { await requisitionsApi.submit(id); load(); } catch { /**/ }
    finally { setActing(null); }
  };

  const approve = async (id: string) => {
    setActing(id);
    try { await requisitionsApi.approve(id); load(); } catch { /**/ }
    finally { setActing(null); }
  };

  const reject = async (id: string) => {
    const reason = window.prompt('Rejection reason:');
    if (!reason) return;
    setActing(id);
    try { await requisitionsApi.reject(id, reason); load(); } catch { /**/ }
    finally { setActing(null); }
  };

  return (
    <div>
      <div className="mb-4 flex items-center gap-3">
        <select className="select w-44" value={statusFilter} onChange={e => setStatusFilter(e.target.value)} aria-label="Filter by status">
          <option value="">All Statuses</option>
          {['Draft', 'Submitted', 'PendingApproval', 'Approved', 'Rejected', 'Converted'].map(s => <option key={s}>{s}</option>)}
        </select>
        <span className="ml-auto text-xs text-slate-500 dark:text-slate-400">{total} requisitions</span>
        <button type="button" className="btn-primary flex items-center gap-1.5 text-sm" onClick={() => setCreateOpen(true)}>
          <Plus className="h-3.5 w-3.5" />New Requisition
        </button>
      </div>

      <div className="overflow-hidden rounded-xl border border-slate-200 dark:border-white/10">
        <table className="min-w-full text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 dark:border-white/10 dark:bg-white/[0.03]">
            <tr>
              {['Number', 'Position', 'Department', 'Count', 'Priority', 'Status', 'Actions'].map(h => (
                <th key={h} className="px-4 py-3 text-left text-xs font-semibold text-slate-500 dark:text-slate-400">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
            {loading && <tr><td colSpan={7} className="py-10 text-center"><div className="mx-auto h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>}
            {!loading && items.length === 0 && <tr><td colSpan={7} className="py-10 text-center text-sm text-slate-400 dark:text-slate-500">No requisitions found.</td></tr>}
            {!loading && items.map(r => (
              <tr key={r.id} className="hover:bg-slate-50/60 dark:hover:bg-white/[0.02]">
                <td className="px-4 py-3 font-mono text-xs text-slate-600 dark:text-slate-400">{r.requisitionNumber}</td>
                <td className="px-4 py-3">
                  <p className="font-medium text-slate-800 dark:text-slate-200">{r.designationTitle}</p>
                  <p className="text-xs text-slate-400 dark:text-slate-500">{ago(r.createdAtUtc)}</p>
                </td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-400">{r.departmentName}</td>
                <td className="px-4 py-3 text-center font-semibold text-slate-800 dark:text-slate-200">{r.headCount}</td>
                <td className="px-4 py-3">
                  <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${PRIORITY_COLORS[r.priority] ?? ''}`}>{r.priority}</span>
                </td>
                <td className="px-4 py-3"><StatusChip {...reqStatusTone(r.status)} /></td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-1">
                    {r.status === 'Draft' && (
                      <button type="button" disabled={acting === r.id} onClick={() => submit(r.id)} className="rounded-lg bg-sapphire/10 px-2.5 py-1 text-xs font-medium text-sapphire hover:bg-sapphire/20 disabled:opacity-50 dark:bg-sapphire/20 dark:text-cyanAccent">
                        Submit
                      </button>
                    )}
                    {(r.status === 'Submitted' || r.status === 'PendingApproval') && (
                      <>
                        <button type="button" disabled={acting === r.id} onClick={() => approve(r.id)} className="rounded-lg bg-emerald-50 px-2.5 py-1 text-xs font-medium text-emerald-600 hover:bg-emerald-100 disabled:opacity-50 dark:bg-emerald-500/10 dark:text-emerald-400">Approve</button>
                        <button type="button" disabled={acting === r.id} onClick={() => reject(r.id)} className="rounded-lg bg-rose-50 px-2.5 py-1 text-xs font-medium text-rose-600 hover:bg-rose-100 disabled:opacity-50 dark:bg-rose-500/10 dark:text-rose-400">Reject</button>
                      </>
                    )}
                    {r.status === 'Approved' && (
                      <button type="button" onClick={() => onCreateOpening(r)} className="rounded-lg bg-blue-50 px-2.5 py-1 text-xs font-medium text-blue-600 hover:bg-blue-100 dark:bg-blue-500/10 dark:text-blue-400">
                        Open Job
                      </button>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {createOpen && <CreateReqModal onClose={() => setCreateOpen(false)} onSaved={() => { setCreateOpen(false); load(); }} />}
    </div>
  );
}

// ── Create Job Opening Modal ───────────────────────────────────────────────────

function CreateOpeningModal({ requisition, onClose, onSaved }: {
  requisition: ManpowerRequisition | null; onClose: () => void; onSaved: () => void;
}) {
  const { currencyCode } = useTenantSettings();
  const [form, setForm] = useState({
    title: requisition?.designationTitle ?? '',
    departmentName: requisition?.departmentName ?? '',
    designationTitle: requisition?.designationTitle ?? '',
    employmentType: requisition?.employmentType ?? 'Full-Time',
    headCount: requisition?.headCount ?? 1,
    description: '', requirements: '', responsibilities: '',
    salaryFrom: '', salaryTo: '', location: '', assignedHrName: '',
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string | number) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!form.title || !form.departmentName) { setError('Title and Department are required.'); return; }
    setSaving(true); setError('');
    try {
      await openingsApi.create({
        requisitionId: requisition?.id,
        title: form.title, departmentName: form.departmentName,
        designationTitle: form.designationTitle, employmentType: form.employmentType,
        headCount: Number(form.headCount), description: form.description,
        requirements: form.requirements, responsibilities: form.responsibilities,
        salaryFrom: form.salaryFrom ? Number(form.salaryFrom) : undefined,
        salaryTo: form.salaryTo ? Number(form.salaryTo) : undefined,
        location: form.location, assignedHrName: form.assignedHrName,
      });
      onSaved();
    } catch { setError('Failed to create opening.'); setSaving(false); }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="flex w-full max-w-lg flex-col rounded-2xl border border-slate-200 bg-white shadow-2xl max-h-[90vh] dark:border-white/10 dark:bg-[#0D1221]">
        <div className="flex shrink-0 items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-white/10">
          <h3 className="font-semibold text-slate-900 dark:text-white">New Job Opening{requisition ? ` from ${requisition.requisitionNumber}` : ''}</h3>
          <button type="button" aria-label="Close" onClick={onClose} className="grid h-7 w-7 place-items-center rounded-lg text-slate-400 hover:bg-slate-100 dark:hover:bg-white/10"><X className="h-4 w-4" /></button>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto px-6 py-4">
          <div className="space-y-3">
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Job Title *</label>
              <input className="input w-full" placeholder="e.g. Senior Software Engineer" aria-label="Job title" value={form.title} onChange={e => set('title', e.target.value)} />
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Department *</label>
                <input className="input w-full" placeholder="e.g. Engineering" aria-label="Department" value={form.departmentName} onChange={e => set('departmentName', e.target.value)} />
              </div>
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Head Count</label>
                <input type="number" className="input w-full" min={1} value={form.headCount} onChange={e => set('headCount', Number(e.target.value))} aria-label="Head count" />
              </div>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Job Description</label>
              <textarea className="input w-full resize-none" rows={3} placeholder="Describe the role and responsibilities…" aria-label="Job description" value={form.description} onChange={e => set('description', e.target.value)} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Requirements (one per line)</label>
              <textarea className="input w-full resize-none" rows={3} placeholder="5+ years of experience&#10;Strong TypeScript skills…" value={form.requirements} onChange={e => set('requirements', e.target.value)} />
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Salary From ({currencyCode})</label>
                <input type="number" className="input w-full" value={form.salaryFrom} onChange={e => set('salaryFrom', e.target.value)} aria-label="Salary from" />
              </div>
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Salary To ({currencyCode})</label>
                <input type="number" className="input w-full" value={form.salaryTo} onChange={e => set('salaryTo', e.target.value)} aria-label="Salary to" />
              </div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Location</label>
                <input className="input w-full" placeholder="e.g. Dubai, UAE" aria-label="Location" value={form.location} onChange={e => set('location', e.target.value)} />
              </div>
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Assigned HR</label>
                <input className="input w-full" placeholder="HR name" aria-label="Assigned HR name" value={form.assignedHrName} onChange={e => set('assignedHrName', e.target.value)} />
              </div>
            </div>
            {error && <p className="text-xs text-rose-500">{error}</p>}
          </div>
        </div>
        <div className="flex shrink-0 justify-end gap-2 border-t border-slate-100 px-6 py-4 dark:border-white/10">
          <button type="button" className="btn-secondary text-sm" onClick={onClose}>Cancel</button>
          <button type="button" className="btn-primary text-sm" onClick={save} disabled={saving}>{saving ? 'Saving…' : 'Create Opening'}</button>
        </div>
      </div>
    </div>,
    document.body
  );
}

// ── Job Openings Tab ───────────────────────────────────────────────────────────

function OpeningsTab({ onSelectOpening, requisitionToOpen, onOpeningCreated }: {
  onSelectOpening: (o: JobOpening) => void;
  requisitionToOpen: ManpowerRequisition | null;
  onOpeningCreated: () => void;
}) {
  const { currencyCode } = useTenantSettings();
  const [items, setItems] = useState<JobOpening[]>([]);
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(!!requisitionToOpen);

  const load = () => {
    setLoading(true);
    openingsApi.list().then(r => { setItems(r.items); }).catch(() => {}).finally(() => setLoading(false));
  };

  useEffect(() => { load(); }, []);
  useEffect(() => { if (requisitionToOpen) setCreateOpen(true); }, [requisitionToOpen]);

  return (
    <div>
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <p className="text-sm text-slate-500 dark:text-slate-400">{items.length} opening{items.length !== 1 ? 's' : ''}</p>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <ImportExportToolbar
            entityName="Job Openings"
            onExport={jobOpeningsImportExport.export}
            onDownloadTemplate={jobOpeningsImportExport.template}
            onImport={jobOpeningsImportExport.import}
          />
          <button type="button" className="btn-primary flex items-center gap-1.5 text-sm" onClick={() => setCreateOpen(true)}>
            <Plus className="h-3.5 w-3.5" />New Opening
          </button>
        </div>
      </div>

      {loading && <div className="flex justify-center py-12"><div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>}

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {!loading && items.map(o => (
          <button
            key={o.id}
            type="button"
            onClick={() => onSelectOpening(o)}
            className="surface group flex flex-col gap-3 p-5 text-left transition hover:border-sapphire/30 hover:shadow-md dark:hover:border-cyanAccent/20"
          >
            <div className="flex items-start justify-between gap-2">
              <div className="min-w-0">
                <p className="truncate font-semibold text-slate-800 group-hover:text-sapphire dark:text-white dark:group-hover:text-cyanAccent">{o.title}</p>
                <p className="text-xs text-slate-400 dark:text-slate-500">{o.jobCode}</p>
              </div>
              <StatusChip {...openingStatusTone(o.status)} />
            </div>
            <div className="space-y-1 text-xs text-slate-500 dark:text-slate-400">
              <p>{o.departmentName}</p>
              {o.salaryFrom && <p>{currencyCode} {fmt(o.salaryFrom)}–{fmt(o.salaryTo)}/mo</p>}
              {o.location && <p>{o.location}</p>}
            </div>
            <div className="flex items-center justify-between border-t border-slate-100 pt-3 dark:border-white/[0.06]">
              <div className="flex items-center gap-3 text-xs">
                <span className="font-semibold text-sapphire dark:text-cyanAccent">{o.activeApplications ?? 0}</span>
                <span className="text-slate-400">applicants</span>
              </div>
              <div className="flex items-center gap-1 text-xs text-slate-400">
                <span>{o.headCount - o.filledCount} position{o.headCount - o.filledCount !== 1 ? 's' : ''} left</span>
                <ChevronRight className="h-3.5 w-3.5" />
              </div>
            </div>
          </button>
        ))}
      </div>

      {createOpen && (
        <CreateOpeningModal
          requisition={requisitionToOpen}
          onClose={() => setCreateOpen(false)}
          onSaved={() => { setCreateOpen(false); load(); onOpeningCreated(); }}
        />
      )}
    </div>
  );
}

// ── Application Detail Drawer ──────────────────────────────────────────────────

function ApplicationDrawer({ id, onClose, onRefresh }: { id: string; onClose: () => void; onRefresh: () => void }) {
  const { currencyCode } = useTenantSettings();
  const [detail, setDetail] = useState<ApplicationDetail | null>(null);
  const [tab, setTab] = useState<'timeline' | 'interviews' | 'offer'>('timeline');
  const [acting, setActing] = useState('');
  const [offerForm, setOfferForm] = useState({ department: '', startDate: '', basicSalary: '', housingAllowance: '', transportAllowance: '', otherAllowances: '', probationMonths: 3 });
  const [interviewForm, setInterviewForm] = useState({ interviewType: 'HR Screening', interviewerNames: '', scheduledAt: '', durationMinutes: 60, mode: 'Video', meetingLink: '', location: '' });
  const [noteText, setNoteText] = useState('');
  const [rejectReason, setRejectReason] = useState('');

  const load = () => {
    applicationsApi.get(id).then(setDetail).catch(() => {});
  };
  useEffect(() => { load(); }, [id]);

  const app = detail?.application;
  if (!app) return null;

  const nextStage = (() => {
    const stages = ['Applied', 'Screening', 'Assessment', 'Interview', 'Offer', 'Hired'];
    const idx = stages.indexOf(app.stage);
    return idx >= 0 && idx < stages.length - 1 ? stages[idx + 1] : null;
  })();

  const doAdvance = async () => {
    setActing('advance');
    try { await applicationsApi.advance(id); load(); onRefresh(); } catch { /**/ }
    finally { setActing(''); }
  };

  const doReject = async () => {
    if (!rejectReason.trim()) return;
    setActing('reject');
    try { await applicationsApi.reject(id, rejectReason); load(); onRefresh(); } catch { /**/ }
    finally { setActing(''); }
  };

  const doNote = async () => {
    if (!noteText.trim()) return;
    setActing('note');
    try { await applicationsApi.addNote(id, noteText); setNoteText(''); load(); } catch { /**/ }
    finally { setActing(''); }
  };

  const doScheduleInterview = async () => {
    setActing('interview');
    try {
      await applicationsApi.scheduleInterview(id, {
        interviewType: interviewForm.interviewType,
        interviewerNames: interviewForm.interviewerNames,
        scheduledAt: interviewForm.scheduledAt,
        durationMinutes: interviewForm.durationMinutes,
        mode: interviewForm.mode,
        meetingLink: interviewForm.meetingLink,
        location: interviewForm.location,
      });
      load();
    } catch { /**/ }
    finally { setActing(''); }
  };

  const doGenerateOffer = async () => {
    setActing('offer');
    try {
      await applicationsApi.generateOffer(id, {
        department: offerForm.department,
        startDate: offerForm.startDate,
        basicSalary: Number(offerForm.basicSalary),
        housingAllowance: Number(offerForm.housingAllowance),
        transportAllowance: Number(offerForm.transportAllowance),
        otherAllowances: Number(offerForm.otherAllowances) || 0,
        probationMonths: offerForm.probationMonths,
      });
      setTab('offer'); load();
    } catch { /**/ }
    finally { setActing(''); }
  };

  const offer = detail.offer as OfferLetter | null;

  const offerAction = async (action: 'send' | 'accept' | 'decline') => {
    if (!offer) return;
    setActing(action);
    try {
      if (action === 'send') await applicationsApi.sendOffer(offer.id);
      else if (action === 'accept') await applicationsApi.acceptOffer(offer.id);
      else {
        const reason = window.prompt('Decline reason:') ?? '';
        await applicationsApi.declineOffer(offer.id, reason);
      }
      load(); onRefresh();
    } catch { /**/ }
    finally { setActing(''); }
  };

  return (
    <div className="fixed inset-0 z-50 flex">
      <div className="flex-1 bg-black/40" onClick={onClose} />
      <div className="flex h-full w-full max-w-lg flex-col overflow-hidden border-l border-slate-200 bg-white dark:border-white/10 dark:bg-[#0B1020]">
        {/* Header */}
        <div className="flex items-start gap-3 border-b border-slate-200 p-5 dark:border-white/10">
          <div className="flex-1 min-w-0">
            <p className="truncate font-semibold text-slate-900 dark:text-white">{app.candidateName}</p>
            <p className="text-xs text-slate-500 dark:text-slate-400">{app.jobTitle}</p>
            <div className="mt-2 flex items-center gap-2">
              <span className="inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-semibold text-white" style={{ backgroundColor: STAGE_COLORS[app.stage] ?? '#64748b' }}>
                {app.stage}
              </span>
              {app.status !== 'Active' && (
                <span className="rounded-full bg-rose-50 px-2.5 py-0.5 text-xs font-semibold text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">{app.status}</span>
              )}
            </div>
          </div>
          <button type="button" aria-label="Close" onClick={onClose} className="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-slate-400 hover:bg-slate-100 dark:hover:bg-white/10"><X className="h-4 w-4" /></button>
        </div>

        {/* Stage advance/reject actions */}
        {app.status === 'Active' && app.stage !== 'Hired' && (
          <div className="flex gap-2 border-b border-slate-100 px-5 py-3 dark:border-white/[0.06]">
            {nextStage && (
              <button type="button" disabled={!!acting} onClick={doAdvance}
                className="flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-xs font-semibold text-white hover:bg-sapphire/90 disabled:opacity-50">
                <ArrowRight className="h-3.5 w-3.5" />Move to {nextStage}
              </button>
            )}
            <div className="flex items-center gap-1.5 rounded-lg border border-rose-200 px-2 py-1 dark:border-rose-500/30">
              <input className="w-28 bg-transparent text-xs text-slate-600 outline-none placeholder:text-slate-400 dark:text-slate-300" placeholder="Reject reason…" value={rejectReason} onChange={e => setRejectReason(e.target.value)} />
              <button type="button" disabled={!rejectReason.trim() || !!acting} onClick={doReject} className="text-xs font-medium text-rose-500 hover:text-rose-600 disabled:opacity-40">Reject</button>
            </div>
          </div>
        )}

        {/* Candidate info */}
        {detail.candidate && (
          <div className="border-b border-slate-100 px-5 py-3 dark:border-white/[0.06]">
            <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-xs">
              <div><span className="text-slate-400">Email </span><span className="text-slate-700 dark:text-slate-300">{detail.candidate.email}</span></div>
              <div><span className="text-slate-400">Phone </span><span className="text-slate-700 dark:text-slate-300">{detail.candidate.phone || '—'}</span></div>
              <div><span className="text-slate-400">Experience </span><span className="text-slate-700 dark:text-slate-300">{detail.candidate.totalExperienceYears}y</span></div>
              <div><span className="text-slate-400">Source </span><span className="text-slate-700 dark:text-slate-300">{detail.candidate.source}</span></div>
            </div>
          </div>
        )}

        {/* Sub-tabs */}
        <div className="flex gap-1 border-b border-slate-200 px-5 dark:border-white/[0.06]">
          {(['timeline', 'interviews', 'offer'] as const).map(t => (
            <button key={t} type="button" onClick={() => setTab(t)}
              className={`py-2.5 text-xs font-medium capitalize transition ${tab === t ? 'border-b-2 border-sapphire text-sapphire dark:border-cyanAccent dark:text-cyanAccent' : 'text-slate-500 hover:text-slate-800 dark:text-slate-400'}`}>
              {t}
              {t === 'interviews' && detail.interviews.length > 0 && <span className="ml-1 rounded-full bg-slate-100 px-1.5 py-0.5 text-[10px] dark:bg-white/10">{detail.interviews.length}</span>}
            </button>
          ))}
        </div>

        {/* Content */}
        <div className="flex-1 overflow-y-auto px-5 py-4">
          {/* Timeline */}
          {tab === 'timeline' && (
            <div>
              <div className="space-y-3">
                {detail.events.map(ev => (
                  <div key={ev.id} className="flex gap-3">
                    <div className="mt-1 h-2 w-2 shrink-0 rounded-full bg-sapphire" />
                    <div className="flex-1">
                      <div className="flex items-baseline justify-between">
                        <p className="text-xs font-semibold text-slate-800 dark:text-slate-200">{ev.eventType.replace(/([A-Z])/g, ' $1').trim()}</p>
                        <p className="text-[10px] text-slate-400 dark:text-slate-500">{ago(ev.createdAtUtc)}</p>
                      </div>
                      {ev.notes && <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{ev.notes}</p>}
                      {ev.performedByName && <p className="text-[10px] text-slate-400">by {ev.performedByName}</p>}
                    </div>
                  </div>
                ))}
                {detail.events.length === 0 && <p className="text-xs text-slate-400 dark:text-slate-500">No events yet.</p>}
              </div>
              <div className="mt-4 flex gap-2">
                <input className="input flex-1" placeholder="Add a note…" value={noteText} onChange={e => setNoteText(e.target.value)} />
                <button type="button" disabled={!noteText.trim() || acting === 'note'} onClick={doNote} className="btn-secondary text-xs">Add</button>
              </div>
            </div>
          )}

          {/* Interviews */}
          {tab === 'interviews' && (
            <div className="space-y-4">
              {detail.interviews.map(iv => (
                <div key={iv.id} className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
                  <div className="flex items-center justify-between">
                    <p className="text-xs font-semibold text-slate-800 dark:text-slate-200">{iv.interviewType}</p>
                    <span className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${iv.status === 'Completed' ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-blue-50 text-blue-600 dark:bg-blue-500/10 dark:text-blue-400'}`}>{iv.status}</span>
                  </div>
                  <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{new Date(iv.scheduledAt).toLocaleString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })} · {iv.mode}</p>
                  {iv.interviewerNames && <p className="text-xs text-slate-400">With: {iv.interviewerNames}</p>}
                  {iv.overallRating && (
                    <div className="mt-2 flex items-center gap-1">
                      {Array.from({ length: 5 }, (_, i) => (
                        <Star key={i} className={`h-3.5 w-3.5 ${i < iv.overallRating! ? 'fill-amber-400 text-amber-400' : 'text-slate-200 dark:text-slate-700'}`} />
                      ))}
                      <span className="ml-1 text-xs font-medium text-slate-600 dark:text-slate-400">{iv.recommendation}</span>
                    </div>
                  )}
                  {iv.feedbackNotes && <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{iv.feedbackNotes}</p>}
                </div>
              ))}

              {/* Schedule new interview */}
              {app.status === 'Active' && (
                <div className="rounded-lg border border-dashed border-slate-200 p-4 dark:border-white/10">
                  <p className="mb-3 text-xs font-semibold text-slate-700 dark:text-slate-300">Schedule Interview</p>
                  <div className="space-y-2">
                    <select className="select w-full" value={interviewForm.interviewType} onChange={e => setInterviewForm(f => ({ ...f, interviewType: e.target.value }))} aria-label="Interview type">
                      {['HR Screening', 'Technical', 'Manager', 'Panel', 'Final'].map(t => <option key={t}>{t}</option>)}
                    </select>
                    <input className="input w-full" placeholder="Interviewer names" value={interviewForm.interviewerNames} onChange={e => setInterviewForm(f => ({ ...f, interviewerNames: e.target.value }))} />
                    <div className="grid grid-cols-2 gap-2">
                      <input type="datetime-local" className="input w-full" value={interviewForm.scheduledAt} onChange={e => setInterviewForm(f => ({ ...f, scheduledAt: e.target.value }))} aria-label="Scheduled at" />
                      <select className="select w-full" value={interviewForm.mode} onChange={e => setInterviewForm(f => ({ ...f, mode: e.target.value }))} aria-label="Interview mode">
                        {['Video', 'InPerson', 'Phone'].map(m => <option key={m}>{m}</option>)}
                      </select>
                    </div>
                    {interviewForm.mode === 'Video' && (
                      <input className="input w-full" placeholder="Meeting link" value={interviewForm.meetingLink} onChange={e => setInterviewForm(f => ({ ...f, meetingLink: e.target.value }))} />
                    )}
                    <button type="button" disabled={acting === 'interview' || !interviewForm.scheduledAt} onClick={doScheduleInterview}
                      className="btn-primary w-full text-xs">
                      {acting === 'interview' ? 'Scheduling…' : 'Schedule Interview'}
                    </button>
                  </div>
                </div>
              )}
            </div>
          )}

          {/* Offer */}
          {tab === 'offer' && (
            <div className="space-y-4">
              {offer ? (
                <div className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
                  <div className="mb-3 flex items-center justify-between">
                    <p className="text-sm font-semibold text-slate-800 dark:text-white">Offer Letter</p>
                    <span className={`rounded-full px-2 py-0.5 text-[10px] font-medium ${offer.status === 'Accepted' ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-500/10' : offer.status === 'Declined' ? 'bg-rose-50 text-rose-500' : 'bg-blue-50 text-blue-600 dark:bg-blue-500/10'}`}>{offer.status}</span>
                  </div>
                  <div className="space-y-1 text-xs text-slate-600 dark:text-slate-400">
                    <div className="flex justify-between"><span>Basic Salary</span><span className="font-medium">{currencyCode} {fmt(offer.basicSalary)}</span></div>
                    <div className="flex justify-between"><span>Housing</span><span>{currencyCode} {fmt(offer.housingAllowance)}</span></div>
                    <div className="flex justify-between"><span>Transport</span><span>{currencyCode} {fmt(offer.transportAllowance)}</span></div>
                    <div className="flex justify-between border-t border-slate-100 pt-1 font-semibold text-slate-800 dark:border-white/10 dark:text-white">
                      <span>Gross Total</span><span>{currencyCode} {fmt(offer.grossSalary)}</span>
                    </div>
                  </div>
                  <div className="mt-3 flex gap-2">
                    <a href={`/api/recruitment/applications/offers/${offer.id}/html`} target="_blank" rel="noreferrer"
                      className="flex items-center gap-1 rounded-lg bg-slate-100 px-2.5 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-200 dark:bg-white/10 dark:text-slate-300">
                      <FileText className="h-3.5 w-3.5" />Preview
                    </a>
                    {offer.status === 'Draft' && (
                      <button type="button" disabled={acting === 'send'} onClick={() => offerAction('send')}
                        className="flex items-center gap-1 rounded-lg bg-sapphire px-2.5 py-1.5 text-xs font-semibold text-white hover:bg-sapphire/90 disabled:opacity-50">
                        <Send className="h-3.5 w-3.5" />Send to Candidate
                      </button>
                    )}
                    {offer.status === 'Sent' && (
                      <>
                        <button type="button" disabled={!!acting} onClick={() => offerAction('accept')}
                          className="flex items-center gap-1 rounded-lg bg-emerald-500 px-2.5 py-1.5 text-xs font-semibold text-white hover:bg-emerald-600 disabled:opacity-50">
                          <CheckCircle className="h-3.5 w-3.5" />Accept
                        </button>
                        <button type="button" disabled={!!acting} onClick={() => offerAction('decline')}
                          className="flex items-center gap-1 rounded-lg border border-rose-300 px-2.5 py-1.5 text-xs font-medium text-rose-500 hover:bg-rose-50 disabled:opacity-50">
                          <XCircle className="h-3.5 w-3.5" />Decline
                        </button>
                      </>
                    )}
                  </div>
                </div>
              ) : (
                app.stage === 'Offer' && app.status === 'Active' ? (
                  <div className="rounded-lg border border-dashed border-slate-200 p-4 dark:border-white/10">
                    <p className="mb-3 text-xs font-semibold text-slate-700 dark:text-slate-300">Generate Offer Letter</p>
                    <div className="space-y-2">
                      <input className="input w-full" placeholder="Department" value={offerForm.department} onChange={e => setOfferForm(f => ({ ...f, department: e.target.value }))} />
                      <input type="date" className="input w-full" value={offerForm.startDate} onChange={e => setOfferForm(f => ({ ...f, startDate: e.target.value }))} aria-label="Start date" />
                      <div className="grid grid-cols-2 gap-2">
                        <input type="number" className="input w-full" placeholder={`Basic Salary (${currencyCode})`} value={offerForm.basicSalary} onChange={e => setOfferForm(f => ({ ...f, basicSalary: e.target.value }))} aria-label="Basic salary" />
                        <input type="number" className="input w-full" placeholder="Housing Allowance" value={offerForm.housingAllowance} onChange={e => setOfferForm(f => ({ ...f, housingAllowance: e.target.value }))} aria-label="Housing allowance" />
                      </div>
                      <div className="grid grid-cols-2 gap-2">
                        <input type="number" className="input w-full" placeholder="Transport Allowance" value={offerForm.transportAllowance} onChange={e => setOfferForm(f => ({ ...f, transportAllowance: e.target.value }))} aria-label="Transport allowance" />
                        <input type="number" className="input w-full" placeholder="Other Allowances" value={offerForm.otherAllowances} onChange={e => setOfferForm(f => ({ ...f, otherAllowances: e.target.value }))} aria-label="Other allowances" />
                      </div>
                      <button type="button" disabled={acting === 'offer' || !offerForm.startDate || !offerForm.basicSalary} onClick={doGenerateOffer}
                        className="btn-primary w-full text-xs">
                        {acting === 'offer' ? 'Generating…' : 'Generate Offer Letter'}
                      </button>
                    </div>
                  </div>
                ) : (
                  <p className="text-xs text-slate-400 dark:text-slate-500">No offer letter yet. Advance application to Offer stage first.</p>
                )
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

// ── Pipeline (Kanban) View ─────────────────────────────────────────────────────

function PipelineView({ opening, onBack }: { opening: JobOpening; onBack: () => void }) {
  const [kanban, setKanban] = useState<KanbanStage[]>([]);
  const [rejected, setRejected] = useState<JobApplication[]>([]);
  const [loading, setLoading] = useState(true);
  const [selectedAppId, setSelectedAppId] = useState<string | null>(null);
  const [addModalOpen, setAddModalOpen] = useState(false);
  const [candidates, setCandidates] = useState<Candidate[]>([]);
  const [selectedCandidateId, setSelectedCandidateId] = useState('');
  const [adding, setAdding] = useState(false);

  const load = () => {
    setLoading(true);
    applicationsApi.kanban(opening.id)
      .then(r => { setKanban(r.stages); setRejected(r.rejected); })
      .catch(() => {})
      .finally(() => setLoading(false));
  };

  useEffect(() => { load(); }, [opening.id]);

  const openAddModal = async () => {
    const res = await candidatesApi.list({ status: 'Active' }).catch(() => ({ items: [] as Candidate[] }));
    setCandidates(res.items);
    setSelectedCandidateId(res.items[0]?.id ?? '');
    setAddModalOpen(true);
  };

  const addCandidate = async () => {
    if (!selectedCandidateId) return;
    setAdding(true);
    try { await applicationsApi.apply(opening.id, selectedCandidateId); setAddModalOpen(false); load(); } catch { /**/ }
    finally { setAdding(false); }
  };

  const totalActive = kanban.reduce((sum, s) => sum + s.applications.length, 0);

  return (
    <div className="flex h-full flex-col">
      {/* Breadcrumb header */}
      <div className="mb-4 flex items-center gap-3">
        <button type="button" onClick={onBack} className="flex items-center gap-1.5 text-sm text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200">
          <ChevronLeft className="h-4 w-4" />Job Openings
        </button>
        <span className="text-slate-300 dark:text-slate-600">/</span>
        <span className="text-sm font-semibold text-slate-800 dark:text-slate-200">{opening.title}</span>
        <StatusChip {...openingStatusTone(opening.status)} />
        <span className="ml-auto text-xs text-slate-400 dark:text-slate-500">{totalActive} active</span>
        <button type="button" className="btn-primary flex items-center gap-1.5 text-sm" onClick={openAddModal}>
          <UserPlus className="h-3.5 w-3.5" />Add Candidate
        </button>
      </div>

      {loading ? (
        <div className="flex justify-center py-12"><div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>
      ) : (
        <div className="flex gap-3 overflow-x-auto pb-4">
          {kanban.map(col => (
            <div key={col.stage} className="flex w-56 shrink-0 flex-col gap-2">
              <div className="flex items-center gap-2 px-1">
                <div className="h-2.5 w-2.5 rounded-full" style={{ backgroundColor: STAGE_COLORS[col.stage] ?? '#64748b' }} />
                <p className="text-xs font-semibold text-slate-700 dark:text-slate-300">{col.stage}</p>
                <span className="ml-auto rounded-full bg-slate-100 px-1.5 py-0.5 text-[10px] font-medium text-slate-500 dark:bg-white/10 dark:text-slate-400">{col.applications.length}</span>
              </div>
              <div className="flex flex-col gap-2 rounded-xl border border-slate-200/70 bg-slate-50 p-2 dark:border-white/[0.06] dark:bg-white/[0.02]" style={{ minHeight: 120 }}>
                {col.applications.map(app => (
                  <button
                    key={app.id}
                    type="button"
                    onClick={() => setSelectedAppId(app.id)}
                    className="rounded-lg border border-slate-200 bg-white p-3 text-left shadow-sm transition hover:border-sapphire/30 hover:shadow-md dark:border-white/10 dark:bg-white/[0.04] dark:hover:border-cyanAccent/20"
                  >
                    <p className="text-xs font-semibold text-slate-800 dark:text-slate-200">{app.candidateName}</p>
                    <p className="mt-0.5 text-[10px] text-slate-400 dark:text-slate-500">{app.candidateEmail}</p>
                    <p className="mt-1.5 text-[10px] text-slate-400 dark:text-slate-500">{ago(app.stageChangedAtUtc ?? app.appliedAtUtc)}</p>
                  </button>
                ))}
                {col.applications.length === 0 && (
                  <p className="py-4 text-center text-[10px] text-slate-300 dark:text-slate-700">No candidates</p>
                )}
              </div>
            </div>
          ))}

          {/* Rejected column */}
          {rejected.length > 0 && (
            <div className="flex w-44 shrink-0 flex-col gap-2">
              <div className="flex items-center gap-2 px-1">
                <div className="h-2.5 w-2.5 rounded-full bg-rose-400" />
                <p className="text-xs font-semibold text-slate-700 dark:text-slate-300">Rejected</p>
                <span className="ml-auto rounded-full bg-slate-100 px-1.5 py-0.5 text-[10px] font-medium text-slate-500 dark:bg-white/10">{rejected.length}</span>
              </div>
              <div className="flex flex-col gap-2 rounded-xl border border-rose-100 bg-rose-50/50 p-2 dark:border-rose-500/10 dark:bg-rose-500/[0.03]" style={{ minHeight: 60 }}>
                {rejected.map(app => (
                  <div key={app.id} className="rounded-lg border border-rose-100 bg-white px-3 py-2 dark:border-rose-500/10 dark:bg-white/[0.03]">
                    <p className="text-[10px] font-medium text-rose-600 dark:text-rose-400">{app.candidateName}</p>
                    <p className="text-[10px] text-slate-400">{app.stage}</p>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      )}

      {/* Add candidate modal */}
      {addModalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
          <div className="w-96 rounded-2xl border border-slate-200 bg-white p-6 shadow-2xl dark:border-white/10 dark:bg-[#0D1221]">
            <div className="mb-4 flex items-center justify-between">
              <h3 className="text-sm font-semibold text-slate-900 dark:text-white">Add Candidate to Pipeline</h3>
              <button type="button" aria-label="Close" onClick={() => setAddModalOpen(false)} className="grid h-7 w-7 place-items-center rounded-lg text-slate-400 hover:bg-slate-100 dark:hover:bg-white/10"><X className="h-4 w-4" /></button>
            </div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Select Candidate</label>
            <select className="select w-full" value={selectedCandidateId} onChange={e => setSelectedCandidateId(e.target.value)} aria-label="Select candidate">
              {candidates.map(c => <option key={c.id} value={c.id}>{c.fullName || `${c.firstName} ${c.lastName}`} — {c.currentJobTitle}</option>)}
            </select>
            {candidates.length === 0 && <p className="mt-2 text-xs text-rose-500">No candidates in talent pool. Add candidates first.</p>}
            <div className="mt-4 flex justify-end gap-2">
              <button type="button" className="btn-secondary text-sm" onClick={() => setAddModalOpen(false)}>Cancel</button>
              <button type="button" className="btn-primary text-sm" disabled={adding || !selectedCandidateId} onClick={addCandidate}>{adding ? 'Adding…' : 'Add to Pipeline'}</button>
            </div>
          </div>
        </div>
      )}

      {selectedAppId && (
        <ApplicationDrawer
          id={selectedAppId}
          onClose={() => setSelectedAppId(null)}
          onRefresh={load}
        />
      )}
    </div>
  );
}

// ── Add Candidate Modal ────────────────────────────────────────────────────────

function AddCandidateModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState({
    firstName: '', lastName: '', email: '', phone: '', currentJobTitle: '',
    currentCompany: '', totalExperienceYears: 0, educationLevel: 'Bachelor',
    nationality: '', linkedInUrl: '', source: 'Direct', tags: '', resumeUrl: '',
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string | number) => setForm(f => ({ ...f, [k]: v }));

  const save = async () => {
    if (!form.firstName || !form.email) { setError('First name and email are required.'); return; }
    setSaving(true); setError('');
    try {
      await candidatesApi.create({ ...form });
      onSaved();
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to add candidate.');
      setSaving(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="flex w-full max-w-md flex-col rounded-2xl border border-slate-200 bg-white shadow-2xl max-h-[90vh] dark:border-white/10 dark:bg-[#0D1221]">
        <div className="flex shrink-0 items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-white/10">
          <h3 className="font-semibold text-slate-900 dark:text-white">Add to Talent Pool</h3>
          <button type="button" aria-label="Close" onClick={onClose} className="grid h-7 w-7 place-items-center rounded-lg text-slate-400 hover:bg-slate-100 dark:hover:bg-white/10"><X className="h-4 w-4" /></button>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto px-6 py-4">
          <div className="space-y-3">
            <div className="grid grid-cols-2 gap-3">
              <div><label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">First Name *</label><input className="input w-full" placeholder="First name" aria-label="First name" value={form.firstName} onChange={e => set('firstName', e.target.value)} /></div>
              <div><label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Last Name</label><input className="input w-full" placeholder="Last name" aria-label="Last name" value={form.lastName} onChange={e => set('lastName', e.target.value)} /></div>
            </div>
            <div><label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Email *</label><input type="email" className="input w-full" placeholder="email@example.com" value={form.email} onChange={e => set('email', e.target.value)} /></div>
            <div className="grid grid-cols-2 gap-3">
              <div><label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Phone</label><input className="input w-full" placeholder="+971 50 000 0000" aria-label="Phone" value={form.phone} onChange={e => set('phone', e.target.value)} /></div>
              <div><label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Experience (years)</label><input type="number" className="input w-full" min={0} value={form.totalExperienceYears} onChange={e => set('totalExperienceYears', Number(e.target.value))} aria-label="Experience years" /></div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div><label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Current Title</label><input className="input w-full" placeholder="e.g. Senior Developer" aria-label="Current job title" value={form.currentJobTitle} onChange={e => set('currentJobTitle', e.target.value)} /></div>
              <div><label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Current Company</label><input className="input w-full" placeholder="Company name" aria-label="Current company" value={form.currentCompany} onChange={e => set('currentCompany', e.target.value)} /></div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Education</label>
                <select className="select w-full" value={form.educationLevel} onChange={e => set('educationLevel', e.target.value)} aria-label="Education level">
                  {['High School', 'Diploma', 'Bachelor', 'Master', 'PhD'].map(l => <option key={l}>{l}</option>)}
                </select>
              </div>
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Source</label>
                <select className="select w-full" value={form.source} onChange={e => set('source', e.target.value)} aria-label="Source">
                  {['Direct', 'LinkedIn', 'Referral', 'JobBoard', 'Walk-In', 'Agency'].map(s => <option key={s}>{s}</option>)}
                </select>
              </div>
            </div>
            <div><label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Nationality</label><input className="input w-full" placeholder="e.g. UAE" aria-label="Nationality" value={form.nationality} onChange={e => set('nationality', e.target.value)} /></div>
            <div><label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Resume / CV URL <span className="text-slate-400 font-normal">(LinkedIn, Google Drive, etc.)</span></label><input type="url" className="input w-full" placeholder="https://…" value={form.resumeUrl} onChange={e => set('resumeUrl', e.target.value)} /></div>
            {error && <p className="text-xs text-rose-500">{error}</p>}
          </div>
        </div>
        <div className="flex shrink-0 justify-end gap-2 border-t border-slate-100 px-6 py-4 dark:border-white/10">
          <button type="button" className="btn-secondary text-sm" onClick={onClose}>Cancel</button>
          <button type="button" className="btn-primary text-sm" disabled={saving} onClick={save}>{saving ? 'Adding…' : 'Add Candidate'}</button>
        </div>
      </div>
    </div>,
    document.body
  );
}

// ── Candidates Tab ─────────────────────────────────────────────────────────────

function CandidatesTab() {
  const [items, setItems] = useState<Candidate[]>([]);
  const [total, setTotal] = useState(0);
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [addOpen, setAddOpen] = useState(false);
  const searchRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const load = (q = search) => {
    setLoading(true);
    candidatesApi.list({ search: q || undefined }).then(r => { setItems(r.items); setTotal(r.total); }).catch(() => {}).finally(() => setLoading(false));
  };

  useEffect(() => { load(); }, []);

  const onSearch = (q: string) => {
    setSearch(q);
    if (searchRef.current) clearTimeout(searchRef.current);
    searchRef.current = setTimeout(() => load(q), 300);
  };

  return (
    <div>
      <div className="mb-4 flex items-center gap-3">
        <input className="input w-72" placeholder="Search name, email, title…" value={search} onChange={e => onSearch(e.target.value)} />
        <span className="ml-auto text-xs text-slate-500 dark:text-slate-400">{total} candidates</span>
        <button type="button" className="btn-primary flex items-center gap-1.5 text-sm" onClick={() => setAddOpen(true)}>
          <Plus className="h-3.5 w-3.5" />Add Candidate
        </button>
      </div>

      <div className="overflow-hidden rounded-xl border border-slate-200 dark:border-white/10">
        <table className="min-w-full text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 dark:border-white/10 dark:bg-white/[0.03]">
            <tr>
              {['Name', 'Title / Company', 'Experience', 'Education', 'Source', 'CV', 'Applied'].map(h => (
                <th key={h} className="px-4 py-3 text-left text-xs font-semibold text-slate-500 dark:text-slate-400">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
            {loading && <tr><td colSpan={7} className="py-10 text-center"><div className="mx-auto h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>}
            {!loading && items.length === 0 && <tr><td colSpan={7} className="py-10 text-center text-sm text-slate-400 dark:text-slate-500">No candidates yet. Add your first candidate to the talent pool.</td></tr>}
            {!loading && items.map(c => (
              <tr key={c.id} className="hover:bg-slate-50/60 dark:hover:bg-white/[0.02]">
                <td className="px-4 py-3">
                  <p className="font-medium text-slate-800 dark:text-slate-200">{c.fullName || `${c.firstName} ${c.lastName}`}</p>
                  <p className="text-xs text-slate-400 dark:text-slate-500">{c.email}</p>
                </td>
                <td className="px-4 py-3">
                  <p className="text-slate-700 dark:text-slate-300">{c.currentJobTitle || '—'}</p>
                  <p className="text-xs text-slate-400 dark:text-slate-500">{c.currentCompany || ''}</p>
                </td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-400">{c.totalExperienceYears}y</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-400">{c.educationLevel || '—'}</td>
                <td className="px-4 py-3">
                  <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs text-slate-600 dark:bg-white/10 dark:text-slate-400">{c.source}</span>
                </td>
                <td className="px-4 py-3">
                  {c.resumeUrl ? (
                    <a href={c.resumeUrl} target="_blank" rel="noopener noreferrer" className="flex items-center gap-1 text-xs font-medium text-sapphire hover:underline dark:text-cyanAccent">
                      <FileText className="h-3.5 w-3.5" />View CV
                    </a>
                  ) : <span className="text-xs text-slate-300 dark:text-slate-600">—</span>}
                </td>
                <td className="px-4 py-3 text-slate-500 dark:text-slate-400">{c.applicationCount ?? 0}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {addOpen && <AddCandidateModal onClose={() => setAddOpen(false)} onSaved={() => { setAddOpen(false); load(); }} />}
    </div>
  );
}

// ── Workforce Planning Tab ────────────────────────────────────────────────────

function WorkforcePlanningTab() {
  const [plans, setPlans] = useState<WorkforcePlan[]>([]);
  const [summary, setSummary] = useState<{ totalPlans: number; totalGap: number; totalBudget: number; approved: number } | null>(null);
  const [showCreate, setShowCreate] = useState(false);
  const [form, setForm] = useState({ planName: '', planYear: new Date().getFullYear(), departmentName: '', currentHeadcount: 0, plannedHeadcount: 0, budgetAllocated: 0, currencyCode: 'USD', notes: '' });
  const [saving, setSaving] = useState(false);

  const load = async () => {
    try {
      const [r, s] = await Promise.all([workforcePlanningApi.list(), workforcePlanningApi.summary()]);
      setPlans(r.items); setSummary(s);
    } catch {}
  };

  useEffect(() => { load(); }, []);

  const save = async () => {
    setSaving(true);
    try { await workforcePlanningApi.create(form); setShowCreate(false); setForm({ planName: '', planYear: new Date().getFullYear(), departmentName: '', currentHeadcount: 0, plannedHeadcount: 0, budgetAllocated: 0, currencyCode: 'USD', notes: '' }); load(); } catch {} finally { setSaving(false); }
  };

  const STATUS_COLORS: Record<string, string> = {
    Draft: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
    Approved: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
    InProgress: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400',
    Closed: 'bg-slate-200 text-slate-500',
  };

  return (
    <div className="space-y-5">
      {summary && (
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
          {[
            { label: 'Total Plans', value: summary.totalPlans, color: 'bg-sapphire/10 text-sapphire dark:bg-sapphire/20' },
            { label: 'Approved', value: summary.approved, color: 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-400' },
            { label: 'Total Headcount Gap', value: summary.totalGap, color: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400' },
            { label: 'Budget Allocated', value: `${summary.totalBudget.toLocaleString()}`, color: 'bg-violet-50 text-violet-700 dark:bg-violet-500/10 dark:text-violet-400' },
          ].map(k => (
            <div key={k.label} className="surface flex items-center gap-3 p-4">
              <div className={`h-8 w-8 shrink-0 rounded-lg grid place-items-center ${k.color}`}>
                <Target className="h-4 w-4" />
              </div>
              <div><p className="text-2xl font-extrabold text-slate-950 dark:text-white">{k.value}</p><p className="text-xs text-slate-500 dark:text-slate-400">{k.label}</p></div>
            </div>
          ))}
        </div>
      )}

      <div className="surface p-5">
        <div className="mb-4 flex items-center justify-between">
          <h3 className="text-sm font-semibold text-slate-800 dark:text-white">Workforce Plans</h3>
          <button type="button" onClick={() => setShowCreate(true)} className="flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-xs font-medium text-white hover:bg-sapphire/90">
            <Plus className="h-3.5 w-3.5" /> New Plan
          </button>
        </div>

        {showCreate && (
          <div className="mb-4 rounded-xl border border-slate-200 dark:border-white/10 p-4 space-y-3 bg-slate-50 dark:bg-white/[0.03]">
            <h4 className="text-sm font-semibold text-slate-800 dark:text-white">New Workforce Plan</h4>
            <div className="grid grid-cols-2 gap-3">
              {[['planName', 'Plan Name', 'text'], ['planYear', 'Year', 'number'], ['departmentName', 'Department', 'text']].map(([k, l, t]) => (
                <div key={k}>
                  <label htmlFor={`wfp-${k}`} className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">{l}</label>
                  <input id={`wfp-${k}`} type={t} title={l as string} value={(form as Record<string, unknown>)[k] as string} onChange={e => setForm(f => ({ ...f, [k]: t === 'number' ? Number(e.target.value) : e.target.value }))}
                    className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-900 dark:text-white" />
                </div>
              ))}
              {[['currentHeadcount', 'Current HC', 'number'], ['plannedHeadcount', 'Planned HC', 'number'], ['budgetAllocated', 'Budget', 'number']].map(([k, l, t]) => (
                <div key={k}>
                  <label htmlFor={`wfp-${k}`} className="block text-xs font-medium text-slate-600 dark:text-slate-400 mb-1">{l}</label>
                  <input id={`wfp-${k}`} type={t} title={l as string} value={(form as Record<string, unknown>)[k] as number} onChange={e => setForm(f => ({ ...f, [k]: Number(e.target.value) }))}
                    className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-900 dark:text-white" />
                </div>
              ))}
            </div>
            <div className="flex items-center gap-2 pt-1">
              <button type="button" onClick={save} disabled={saving} className="rounded-lg bg-sapphire px-4 py-1.5 text-xs font-medium text-white disabled:opacity-50 hover:bg-sapphire/90">{saving ? 'Saving…' : 'Create Plan'}</button>
              <button type="button" onClick={() => setShowCreate(false)} className="text-xs text-slate-500 hover:text-slate-700 dark:text-slate-400">Cancel</button>
            </div>
          </div>
        )}

        {plans.length === 0 ? (
          <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">No workforce plans found. Create the first plan.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-slate-200 dark:border-white/10">
                  {['Code', 'Name', 'Year', 'Department', 'Gap', 'Budget', 'Status'].map(h => (
                    <th key={h} className="pb-2 text-left text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {plans.map(p => (
                  <tr key={p.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                    <td className="py-2.5 pr-3 font-mono text-xs text-sapphire dark:text-cyanAccent">{p.planCode}</td>
                    <td className="py-2.5 pr-3 font-medium text-slate-800 dark:text-slate-200">{p.planName}</td>
                    <td className="py-2.5 pr-3 text-slate-500 dark:text-slate-400">{p.planYear}</td>
                    <td className="py-2.5 pr-3 text-slate-500 dark:text-slate-400">{p.departmentName || '—'}</td>
                    <td className="py-2.5 pr-3 font-semibold text-slate-900 dark:text-white">{p.gapCount > 0 ? `+${p.gapCount}` : p.gapCount}</td>
                    <td className="py-2.5 pr-3 text-slate-500 dark:text-slate-400">{p.currencyCode} {p.budgetAllocated.toLocaleString()}</td>
                    <td className="py-2.5">
                      <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[p.status] ?? ''}`}>{p.status}</span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

// ── Interviews Tab ────────────────────────────────────────────────────────────

function InterviewsTab() {
  const [interviews, setInterviews] = useState<ExtInterviewSchedule[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [showSchedule, setShowSchedule] = useState(false);
  const [applications, setApplications] = useState<JobApplication[]>([]);
  const [schedForm, setSchedForm] = useState({
    applicationId: '', interviewType: 'Technical', interviewerNames: '',
    scheduledAt: '', durationMinutes: 60, mode: 'Video', meetingLink: '', location: '',
  });
  const [schedSaving, setSchedSaving] = useState(false);
  const [schedError, setSchedError] = useState('');
  const [cancelling, setCancelling] = useState<string | null>(null);

  const load = async () => {
    setLoading(true);
    try { const r = await interviewsApi.list(undefined, statusFilter || undefined); setInterviews(r.items); } catch {} finally { setLoading(false); }
  };

  const loadApps = async () => {
    try { const r = await applicationsApi.list({ pageSize: 100 }); setApplications(r.items); } catch {}
  };

  useEffect(() => { load(); }, [statusFilter]);

  const openSchedule = () => { loadApps(); setShowSchedule(true); };

  const submitSchedule = async () => {
    if (!schedForm.applicationId || !schedForm.interviewerNames || !schedForm.scheduledAt) {
      setSchedError('Application, interviewers and scheduled date/time are required.'); return;
    }
    setSchedSaving(true); setSchedError('');
    try {
      await interviewsApi.schedule({ ...schedForm, durationMinutes: Number(schedForm.durationMinutes) });
      setShowSchedule(false);
      setSchedForm({ applicationId: '', interviewType: 'Technical', interviewerNames: '', scheduledAt: '', durationMinutes: 60, mode: 'Video', meetingLink: '', location: '' });
      load();
    } catch { setSchedError('Failed to schedule interview.'); }
    finally { setSchedSaving(false); }
  };

  const cancelInterview = async (id: string) => {
    setCancelling(id);
    try { await interviewsApi.cancel(id); load(); } catch {} finally { setCancelling(null); }
  };

  const MODE_ICON: Record<string, string> = { InPerson: '🏢', Video: '📹', Phone: '📞' };
  const RECOMMENDATION_COLORS: Record<string, string> = {
    StrongHire: 'text-emerald-600 dark:text-emerald-400',
    Hire: 'text-blue-600 dark:text-blue-400',
    Hold: 'text-amber-600 dark:text-amber-400',
    Reject: 'text-rose-600 dark:text-rose-400',
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select title="Filter by interview status" value={statusFilter} onChange={e => setStatusFilter(e.target.value)}
          className="rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200">
          <option value="">All Statuses</option>
          {['Scheduled', 'Completed', 'Cancelled', 'NoShow'].map(s => <option key={s}>{s}</option>)}
        </select>
        <button type="button" onClick={openSchedule} className="ml-auto flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-sm font-medium text-white hover:bg-sapphire/90">
          <Calendar className="h-3.5 w-3.5" />Schedule Interview
        </button>
      </div>

      {showSchedule && (
        <div className="rounded-xl border border-slate-200 dark:border-white/10 bg-slate-50 dark:bg-white/[0.03] p-4 space-y-3">
          <div className="flex items-center justify-between">
            <h4 className="text-sm font-semibold text-slate-800 dark:text-white">Schedule New Interview</h4>
            <button type="button" aria-label="Close" title="Close" onClick={() => setShowSchedule(false)} className="text-slate-400 hover:text-slate-600"><X className="h-4 w-4" /></button>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div className="col-span-2">
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Application *</label>
              <select title="Select application" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={schedForm.applicationId} onChange={e => setSchedForm(f => ({ ...f, applicationId: e.target.value }))}>
                <option value="">— Select candidate/application —</option>
                {applications.map(a => <option key={a.id} value={a.id}>{a.candidateName} — {a.jobTitle} ({a.stage})</option>)}
              </select>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Interview Type</label>
              <select title="Interview type" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={schedForm.interviewType} onChange={e => setSchedForm(f => ({ ...f, interviewType: e.target.value }))}>
                {['Technical', 'HR', 'Culture Fit', 'Final', 'Panel'].map(t => <option key={t}>{t}</option>)}
              </select>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Mode</label>
              <select title="Interview mode" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={schedForm.mode} onChange={e => setSchedForm(f => ({ ...f, mode: e.target.value }))}>
                {['Video', 'InPerson', 'Phone'].map(m => <option key={m}>{m}</option>)}
              </select>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Interviewer(s) *</label>
              <input className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                placeholder="e.g. Sarah Ahmed, John Doe" value={schedForm.interviewerNames} onChange={e => setSchedForm(f => ({ ...f, interviewerNames: e.target.value }))} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Duration (minutes)</label>
              <input type="number" min={15} step={15} title="Duration in minutes" placeholder="60" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={schedForm.durationMinutes} onChange={e => setSchedForm(f => ({ ...f, durationMinutes: Number(e.target.value) }))} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Date & Time *</label>
              <input type="datetime-local" title="Interview date and time" placeholder="Select date and time" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={schedForm.scheduledAt} onChange={e => setSchedForm(f => ({ ...f, scheduledAt: e.target.value }))} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Meeting Link</label>
              <input type="url" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                placeholder="https://meet.google.com/…" value={schedForm.meetingLink} onChange={e => setSchedForm(f => ({ ...f, meetingLink: e.target.value }))} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Location</label>
              <input className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                placeholder="Office / Room" value={schedForm.location} onChange={e => setSchedForm(f => ({ ...f, location: e.target.value }))} />
            </div>
          </div>
          {schedError && <p className="text-xs text-rose-500">{schedError}</p>}
          <div className="flex items-center gap-2 pt-1">
            <button type="button" onClick={submitSchedule} disabled={schedSaving}
              className="rounded-lg bg-sapphire px-4 py-1.5 text-xs font-medium text-white disabled:opacity-50 hover:bg-sapphire/90">
              {schedSaving ? 'Scheduling…' : 'Schedule'}
            </button>
            <button type="button" onClick={() => setShowSchedule(false)} className="text-xs text-slate-500 hover:text-slate-700 dark:text-slate-400">Cancel</button>
          </div>
        </div>
      )}

      {loading ? <p className="text-center text-sm text-slate-400 py-8">Loading…</p> : interviews.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">No interviews found. Schedule the first one above.</p>
      ) : (
        <div className="space-y-2">
          {interviews.map(iv => (
            <div key={iv.id} className="surface p-4">
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0">
                  <div className="flex items-center gap-2 mb-1">
                    <span className="text-sm font-semibold text-slate-900 dark:text-white">{iv.interviewType}</span>
                    <span className="text-xs text-slate-400">{MODE_ICON[iv.mode] ?? '🗓'} {iv.mode}</span>
                    <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${
                      iv.status === 'Completed' ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' :
                      iv.status === 'Cancelled' ? 'bg-rose-50 text-rose-700' : 'bg-blue-50 text-blue-700'
                    }`}>{iv.status}</span>
                  </div>
                  <p className="text-xs text-slate-500 dark:text-slate-400">
                    {new Date(iv.scheduledAt).toLocaleString()} · {iv.durationMinutes}min
                  </p>
                  <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">Interviewers: {iv.interviewerNames}</p>
                  {iv.recommendation && (
                    <p className={`text-xs font-medium mt-1 ${RECOMMENDATION_COLORS[iv.recommendation] ?? ''}`}>
                      Recommendation: {iv.recommendation}
                    </p>
                  )}
                </div>
                <div className="flex items-center gap-2 shrink-0">
                  {iv.overallRating && (
                    <div className="text-right">
                      <p className="text-lg font-bold text-slate-900 dark:text-white">{iv.overallRating}/5</p>
                      <p className="text-xs text-slate-400">Rating</p>
                    </div>
                  )}
                  {iv.status === 'Scheduled' && (
                    <button type="button" onClick={() => cancelInterview(iv.id)} disabled={cancelling === iv.id}
                      className="rounded-lg border border-rose-200 px-2.5 py-1 text-xs font-medium text-rose-600 hover:bg-rose-50 disabled:opacity-50 dark:border-rose-500/30 dark:text-rose-400 dark:hover:bg-rose-500/10">
                      {cancelling === iv.id ? '…' : 'Cancel'}
                    </button>
                  )}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Assessments Tab ───────────────────────────────────────────────────────────

function AssessmentsTab() {
  const [assessments, setAssessments] = useState<CandidateAssessment[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [showAssign, setShowAssign] = useState(false);
  const [applications, setApplications] = useState<JobApplication[]>([]);
  const [templates, setTemplates] = useState<{ id: string; title: string; assessmentType: string; durationMinutes: number }[]>([]);
  const [assignForm, setAssignForm] = useState({ applicationId: '', templateId: '', expiryDays: 7 });
  const [assignSaving, setAssignSaving] = useState(false);
  const [assignError, setAssignError] = useState('');
  const [recording, setRecording] = useState<string | null>(null);
  const [scoreInput, setScoreInput] = useState<Record<string, string>>({});

  const load = async () => {
    setLoading(true);
    try { const r = await assessmentsApi.list(undefined, statusFilter || undefined); setAssessments(r.items); } catch {} finally { setLoading(false); }
  };

  useEffect(() => { load(); }, [statusFilter]);

  const openAssign = async () => {
    try {
      const [appsRes, tmplRes] = await Promise.all([applicationsApi.list({ pageSize: 100 }), assessmentsApi.listTemplates()]);
      setApplications(appsRes.items);
      setTemplates(tmplRes);
    } catch {}
    setShowAssign(true);
  };

  const submitAssign = async () => {
    if (!assignForm.applicationId || !assignForm.templateId) { setAssignError('Application and template are required.'); return; }
    setAssignSaving(true); setAssignError('');
    try {
      await assessmentsApi.send({ ...assignForm, expiryDays: Number(assignForm.expiryDays) });
      setShowAssign(false);
      setAssignForm({ applicationId: '', templateId: '', expiryDays: 7 });
      load();
    } catch { setAssignError('Failed to assign assessment.'); }
    finally { setAssignSaving(false); }
  };

  const recordResult = async (id: string) => {
    const raw = scoreInput[id];
    const score = Number(raw);
    if (!raw || isNaN(score) || score < 0 || score > 100) return;
    setRecording(id);
    try { await assessmentsApi.recordResult(id, score); load(); } catch {} finally { setRecording(null); }
  };

  const STATUS_COLORS: Record<string, string> = {
    Pending: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
    Sent: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400',
    Completed: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
    Expired: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select title="Filter by assessment status" value={statusFilter} onChange={e => setStatusFilter(e.target.value)}
          className="rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200">
          <option value="">All Statuses</option>
          {['Pending', 'Sent', 'InProgress', 'Completed', 'Expired'].map(s => <option key={s}>{s}</option>)}
        </select>
        <button type="button" onClick={openAssign} className="ml-auto flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-sm font-medium text-white hover:bg-sapphire/90">
          <ClipboardList className="h-3.5 w-3.5" />Assign Assessment
        </button>
      </div>

      {showAssign && (
        <div className="rounded-xl border border-slate-200 dark:border-white/10 bg-slate-50 dark:bg-white/[0.03] p-4 space-y-3">
          <div className="flex items-center justify-between">
            <h4 className="text-sm font-semibold text-slate-800 dark:text-white">Assign Assessment</h4>
            <button type="button" aria-label="Close" title="Close" onClick={() => setShowAssign(false)} className="text-slate-400 hover:text-slate-600"><X className="h-4 w-4" /></button>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div className="col-span-2">
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Application *</label>
              <select title="Select application" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={assignForm.applicationId} onChange={e => setAssignForm(f => ({ ...f, applicationId: e.target.value }))}>
                <option value="">— Select candidate —</option>
                {applications.map(a => <option key={a.id} value={a.id}>{a.candidateName} — {a.jobTitle}</option>)}
              </select>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Assessment Template *</label>
              <select title="Select template" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={assignForm.templateId} onChange={e => setAssignForm(f => ({ ...f, templateId: e.target.value }))}>
                <option value="">— Select template —</option>
                {templates.map(t => <option key={t.id} value={t.id}>{t.title} ({t.assessmentType}, {t.durationMinutes}min)</option>)}
              </select>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Expires in (days)</label>
              <input type="number" min={1} max={30} title="Expiry days" placeholder="7" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={assignForm.expiryDays} onChange={e => setAssignForm(f => ({ ...f, expiryDays: Number(e.target.value) }))} />
            </div>
          </div>
          {assignError && <p className="text-xs text-rose-500">{assignError}</p>}
          <div className="flex items-center gap-2 pt-1">
            <button type="button" onClick={submitAssign} disabled={assignSaving}
              className="rounded-lg bg-sapphire px-4 py-1.5 text-xs font-medium text-white disabled:opacity-50 hover:bg-sapphire/90">
              {assignSaving ? 'Sending…' : 'Send Assessment'}
            </button>
            <button type="button" onClick={() => setShowAssign(false)} className="text-xs text-slate-500 hover:text-slate-700 dark:text-slate-400">Cancel</button>
          </div>
        </div>
      )}

      {loading ? <p className="text-center text-sm text-slate-400 py-8">Loading…</p> : assessments.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">No assessments found. Assign one above.</p>
      ) : (
        <div className="surface overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-200 dark:border-white/10">
                {['Assessment', 'Status', 'Sent', 'Expires', 'Score', 'Result', 'Actions'].map(h => (
                  <th key={h} className="p-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/5">
              {assessments.map(a => (
                <tr key={a.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.02]">
                  <td className="p-3 font-medium text-slate-800 dark:text-slate-200">{a.templateName}</td>
                  <td className="p-3">
                    <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[a.status] ?? ''}`}>{a.status}</span>
                  </td>
                  <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{a.sentAtUtc ? new Date(a.sentAtUtc).toLocaleDateString() : '—'}</td>
                  <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{a.expiresAtUtc ? new Date(a.expiresAtUtc).toLocaleDateString() : '—'}</td>
                  <td className="p-3 font-semibold text-slate-900 dark:text-white">{a.scorePercentage != null ? `${a.scorePercentage.toFixed(0)}%` : '—'}</td>
                  <td className="p-3">
                    {a.passed === true && <span className="text-xs font-medium text-emerald-600 dark:text-emerald-400">✓ Passed</span>}
                    {a.passed === false && <span className="text-xs font-medium text-rose-600 dark:text-rose-400">✗ Failed</span>}
                    {a.passed == null && <span className="text-xs text-slate-400">—</span>}
                  </td>
                  <td className="p-3">
                    {a.status === 'Completed' && a.scorePercentage == null && (
                      <div className="flex items-center gap-1">
                        <input type="number" min={0} max={100} title="Score (0–100)" placeholder="Score 0–100"
                          className="w-24 rounded-md border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-2 py-1 text-xs text-slate-800 dark:text-slate-200"
                          value={scoreInput[a.id] ?? ''} onChange={e => setScoreInput(s => ({ ...s, [a.id]: e.target.value }))} />
                        <button type="button" onClick={() => recordResult(a.id)} disabled={recording === a.id}
                          className="rounded-md bg-emerald-600 px-2 py-1 text-xs font-medium text-white hover:bg-emerald-700 disabled:opacity-50">
                          {recording === a.id ? '…' : 'Save'}
                        </button>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Offers Tab ────────────────────────────────────────────────────────────────

function OffersTab() {
  const { currencyCode } = useTenantSettings();
  const [offers, setOffers] = useState<OfferLetter[]>([]);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [showCreate, setShowCreate] = useState(false);
  const [applications, setApplications] = useState<JobApplication[]>([]);
  const [offerForm, setOfferForm] = useState({
    applicationId: '', offeredJobTitle: '', offeredDepartment: '', startDate: '',
    basicSalary: 0, housingAllowance: 0, transportAllowance: 0, otherAllowances: 0,
    probationMonths: 3, responseDeadline: '',
  });
  const [offerSaving, setOfferSaving] = useState(false);
  const [offerError, setOfferError] = useState('');
  const [actioning, setActioning] = useState<string | null>(null);

  const load = async () => {
    setLoading(true);
    try { const r = await offersApi.list(undefined, statusFilter || undefined); setOffers(r.items); } catch {} finally { setLoading(false); }
  };

  useEffect(() => { load(); }, [statusFilter]);

  const openCreate = async () => {
    try { const r = await applicationsApi.list({ pageSize: 100 }); setApplications(r.items); } catch {}
    setShowCreate(true);
  };

  const submitOffer = async () => {
    if (!offerForm.applicationId || !offerForm.offeredJobTitle || !offerForm.startDate) {
      setOfferError('Application, job title and start date are required.'); return;
    }
    setOfferSaving(true); setOfferError('');
    try {
      await offersApi.create({ ...offerForm, basicSalary: Number(offerForm.basicSalary), housingAllowance: Number(offerForm.housingAllowance), transportAllowance: Number(offerForm.transportAllowance), otherAllowances: Number(offerForm.otherAllowances), probationMonths: Number(offerForm.probationMonths) });
      setShowCreate(false);
      setOfferForm({ applicationId: '', offeredJobTitle: '', offeredDepartment: '', startDate: '', basicSalary: 0, housingAllowance: 0, transportAllowance: 0, otherAllowances: 0, probationMonths: 3, responseDeadline: '' });
      load();
    } catch { setOfferError('Failed to create offer.'); }
    finally { setOfferSaving(false); }
  };

  const sendOffer = async (id: string) => {
    setActioning(id);
    try { await offersApi.send(id); load(); } catch {} finally { setActioning(null); }
  };

  const acceptOffer = async (id: string) => {
    setActioning(id);
    try { await offersApi.accept(id); load(); } catch {} finally { setActioning(null); }
  };

  const declineOffer = async (id: string) => {
    setActioning(id);
    try { await offersApi.decline(id, 'Declined by candidate'); load(); } catch {} finally { setActioning(null); }
  };

  const STATUS_COLORS: Record<string, string> = {
    Draft: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
    Sent: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400',
    Accepted: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
    Declined: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
    Approved: 'bg-violet-50 text-violet-700 dark:bg-violet-500/10 dark:text-violet-400',
    Expired: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select title="Filter by offer status" value={statusFilter} onChange={e => setStatusFilter(e.target.value)}
          className="rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200">
          <option value="">All Statuses</option>
          {['Draft', 'PendingApproval', 'Approved', 'Sent', 'Accepted', 'Declined', 'Expired'].map(s => <option key={s}>{s}</option>)}
        </select>
        <button type="button" onClick={openCreate} className="ml-auto flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-sm font-medium text-white hover:bg-sapphire/90">
          <FileText className="h-3.5 w-3.5" />Create Offer
        </button>
      </div>

      {showCreate && (
        <div className="rounded-xl border border-slate-200 dark:border-white/10 bg-slate-50 dark:bg-white/[0.03] p-4 space-y-3">
          <div className="flex items-center justify-between">
            <h4 className="text-sm font-semibold text-slate-800 dark:text-white">New Offer Letter</h4>
            <button type="button" aria-label="Close" title="Close" onClick={() => setShowCreate(false)} className="text-slate-400 hover:text-slate-600"><X className="h-4 w-4" /></button>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div className="col-span-2">
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Application *</label>
              <select title="Select application" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={offerForm.applicationId} onChange={e => setOfferForm(f => ({ ...f, applicationId: e.target.value }))}>
                <option value="">— Select candidate —</option>
                {applications.map(a => <option key={a.id} value={a.id}>{a.candidateName} — {a.jobTitle}</option>)}
              </select>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Offered Job Title *</label>
              <input placeholder="e.g. Senior Software Engineer" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={offerForm.offeredJobTitle} onChange={e => setOfferForm(f => ({ ...f, offeredJobTitle: e.target.value }))} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Department</label>
              <input placeholder="e.g. Engineering" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={offerForm.offeredDepartment} onChange={e => setOfferForm(f => ({ ...f, offeredDepartment: e.target.value }))} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Start Date *</label>
              <input type="date" title="Start date" placeholder="Start date" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={offerForm.startDate} onChange={e => setOfferForm(f => ({ ...f, startDate: e.target.value }))} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Response Deadline</label>
              <input type="date" title="Response deadline" placeholder="Response deadline" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={offerForm.responseDeadline} onChange={e => setOfferForm(f => ({ ...f, responseDeadline: e.target.value }))} />
            </div>
            {[['basicSalary', 'Basic Salary'], ['housingAllowance', 'Housing Allowance'], ['transportAllowance', 'Transport Allowance'], ['otherAllowances', 'Other Allowances']].map(([k, l]) => (
              <div key={k}>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{l} ({currencyCode})</label>
                <input type="number" min={0} title={l} placeholder="0" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                  value={(offerForm as Record<string, unknown>)[k] as number} onChange={e => setOfferForm(f => ({ ...f, [k]: Number(e.target.value) }))} />
              </div>
            ))}
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Probation (months)</label>
              <input type="number" min={0} max={12} title="Probation months" placeholder="3" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={offerForm.probationMonths} onChange={e => setOfferForm(f => ({ ...f, probationMonths: Number(e.target.value) }))} />
            </div>
          </div>
          {offerError && <p className="text-xs text-rose-500">{offerError}</p>}
          <div className="flex items-center gap-2 pt-1">
            <button type="button" onClick={submitOffer} disabled={offerSaving}
              className="rounded-lg bg-sapphire px-4 py-1.5 text-xs font-medium text-white disabled:opacity-50 hover:bg-sapphire/90">
              {offerSaving ? 'Creating…' : 'Create Offer'}
            </button>
            <button type="button" onClick={() => setShowCreate(false)} className="text-xs text-slate-500 hover:text-slate-700 dark:text-slate-400">Cancel</button>
          </div>
        </div>
      )}

      {loading ? <p className="text-center text-sm text-slate-400 py-8">Loading…</p> : offers.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">No offers found. Create one above.</p>
      ) : (
        <div className="space-y-2">
          {offers.map(o => (
            <div key={o.id} className="surface p-4">
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0">
                  <p className="font-semibold text-slate-900 dark:text-white">{o.candidateName}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400">{o.offeredJobTitle} · Starts {o.startDate}</p>
                  <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">
                    Gross: <span className="font-semibold text-slate-700 dark:text-slate-300">{o.grossSalary.toLocaleString()}</span>
                    {o.responseDeadline && ` · Deadline: ${new Date(o.responseDeadline).toLocaleDateString()}`}
                  </p>
                </div>
                <div className="flex items-center gap-2 shrink-0">
                  <span className={`rounded-full px-2.5 py-1 text-xs font-medium ${STATUS_COLORS[o.status] ?? ''}`}>{o.status}</span>
                  {o.status === 'Draft' && (
                    <button type="button" onClick={() => sendOffer(o.id)} disabled={actioning === o.id}
                      className="rounded-lg bg-sapphire px-2.5 py-1 text-xs font-medium text-white hover:bg-sapphire/90 disabled:opacity-50">
                      {actioning === o.id ? '…' : 'Send'}
                    </button>
                  )}
                  {o.status === 'Sent' && (
                    <>
                      <button type="button" onClick={() => acceptOffer(o.id)} disabled={actioning === o.id}
                        className="rounded-lg bg-emerald-600 px-2.5 py-1 text-xs font-medium text-white hover:bg-emerald-700 disabled:opacity-50">
                        {actioning === o.id ? '…' : 'Accept'}
                      </button>
                      <button type="button" onClick={() => declineOffer(o.id)} disabled={actioning === o.id}
                        className="rounded-lg border border-rose-200 px-2.5 py-1 text-xs font-medium text-rose-600 hover:bg-rose-50 disabled:opacity-50 dark:border-rose-500/30 dark:text-rose-400">
                        Decline
                      </button>
                    </>
                  )}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Onboarding Tab ────────────────────────────────────────────────────────────

function OnboardingTab() {
  const [tasks, setTasks] = useState<OnboardingTask[]>([]);
  const [summary, setSummary] = useState<{ total: number; pending: number; completed: number; blocked: number; completionPct: number } | null>(null);
  const [loading, setLoading] = useState(true);
  const [statusFilter, setStatusFilter] = useState('');
  const [showAdd, setShowAdd] = useState(false);
  const [taskForm, setTaskForm] = useState({
    taskTitle: '', taskDescription: '', category: 'General', assignedToName: '', dueDate: '', isMandatory: false,
  });
  const [taskSaving, setTaskSaving] = useState(false);
  const [taskError, setTaskError] = useState('');
  const [updatingStatus, setUpdatingStatus] = useState<string | null>(null);

  const load = async () => {
    setLoading(true);
    try {
      const [r, s] = await Promise.all([
        onboardingApi.listTasks({ status: statusFilter || undefined }),
        onboardingApi.summary(),
      ]);
      setTasks(r.items); setSummary(s);
    } catch {} finally { setLoading(false); }
  };

  useEffect(() => { load(); }, [statusFilter]);

  const submitTask = async () => {
    if (!taskForm.taskTitle) { setTaskError('Task title is required.'); return; }
    setTaskSaving(true); setTaskError('');
    try {
      await onboardingApi.createTask({ ...taskForm });
      setShowAdd(false);
      setTaskForm({ taskTitle: '', taskDescription: '', category: 'General', assignedToName: '', dueDate: '', isMandatory: false });
      load();
    } catch { setTaskError('Failed to create task.'); }
    finally { setTaskSaving(false); }
  };

  const updateStatus = async (id: string, status: string) => {
    setUpdatingStatus(id);
    try { await onboardingApi.updateStatus(id, status); load(); } catch {} finally { setUpdatingStatus(null); }
  };

  const STATUS_COLORS: Record<string, string> = {
    Pending: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
    InProgress: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400',
    Completed: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
    Blocked: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
    Skipped: 'bg-slate-100 text-slate-500',
  };

  const CATEGORY_ICONS: Record<string, string> = {
    Document: '📄', IT: '💻', Training: '📚', Policy: '📋', Access: '🔑', General: '✅',
  };

  return (
    <div className="space-y-4">
      {summary && (
        <div className="surface p-4">
          <div className="mb-2 flex items-center justify-between">
            <p className="text-sm font-medium text-slate-700 dark:text-slate-300">Overall Onboarding Progress</p>
            <p className="text-sm font-bold text-slate-900 dark:text-white">{summary.completionPct.toFixed(0)}%</p>
          </div>
          <div className="h-2 rounded-full bg-slate-200 dark:bg-white/10">
            <div className="h-2 rounded-full bg-sapphire transition-all" style={{ width: `${summary.completionPct}%` }} />
          </div>
          <div className="mt-3 flex gap-4 text-xs text-slate-500 dark:text-slate-400">
            <span>Total: <strong>{summary.total}</strong></span>
            <span className="text-amber-600">Pending: <strong>{summary.pending}</strong></span>
            <span className="text-emerald-600">Done: <strong>{summary.completed}</strong></span>
            <span className="text-rose-600">Blocked: <strong>{summary.blocked}</strong></span>
          </div>
        </div>
      )}

      <div className="flex items-center gap-3">
        <select title="Filter by task status" value={statusFilter} onChange={e => setStatusFilter(e.target.value)}
          className="rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200">
          <option value="">All Statuses</option>
          {['Pending', 'InProgress', 'Completed', 'Blocked', 'Skipped'].map(s => <option key={s}>{s}</option>)}
        </select>
        <button type="button" onClick={() => setShowAdd(true)} className="ml-auto flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-1.5 text-sm font-medium text-white hover:bg-sapphire/90">
          <Plus className="h-3.5 w-3.5" />Add Task
        </button>
      </div>

      {showAdd && (
        <div className="rounded-xl border border-slate-200 dark:border-white/10 bg-slate-50 dark:bg-white/[0.03] p-4 space-y-3">
          <div className="flex items-center justify-between">
            <h4 className="text-sm font-semibold text-slate-800 dark:text-white">New Onboarding Task</h4>
            <button type="button" aria-label="Close" title="Close" onClick={() => setShowAdd(false)} className="text-slate-400 hover:text-slate-600"><X className="h-4 w-4" /></button>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div className="col-span-2">
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Task Title *</label>
              <input placeholder="e.g. Complete employment contract" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={taskForm.taskTitle} onChange={e => setTaskForm(f => ({ ...f, taskTitle: e.target.value }))} />
            </div>
            <div className="col-span-2">
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Description</label>
              <input placeholder="Optional details" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={taskForm.taskDescription} onChange={e => setTaskForm(f => ({ ...f, taskDescription: e.target.value }))} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Category</label>
              <select title="Category" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={taskForm.category} onChange={e => setTaskForm(f => ({ ...f, category: e.target.value }))}>
                {['General', 'Document', 'IT', 'Training', 'Policy', 'Access'].map(c => <option key={c}>{c}</option>)}
              </select>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Assigned To</label>
              <input placeholder="Name of responsible person" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={taskForm.assignedToName} onChange={e => setTaskForm(f => ({ ...f, assignedToName: e.target.value }))} />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Due Date</label>
              <input type="date" title="Due date" placeholder="Due date" className="w-full rounded-lg border border-slate-200 dark:border-white/10 bg-white dark:bg-white/5 px-3 py-1.5 text-sm text-slate-800 dark:text-slate-200"
                value={taskForm.dueDate} onChange={e => setTaskForm(f => ({ ...f, dueDate: e.target.value }))} />
            </div>
            <div className="flex items-center gap-2 pt-4">
              <input type="checkbox" id="ob-mandatory" className="h-4 w-4 rounded border-slate-300 text-sapphire"
                checked={taskForm.isMandatory} onChange={e => setTaskForm(f => ({ ...f, isMandatory: e.target.checked }))} />
              <label htmlFor="ob-mandatory" className="text-xs font-medium text-slate-600 dark:text-slate-400">Mandatory task</label>
            </div>
          </div>
          {taskError && <p className="text-xs text-rose-500">{taskError}</p>}
          <div className="flex items-center gap-2 pt-1">
            <button type="button" onClick={submitTask} disabled={taskSaving}
              className="rounded-lg bg-sapphire px-4 py-1.5 text-xs font-medium text-white disabled:opacity-50 hover:bg-sapphire/90">
              {taskSaving ? 'Creating…' : 'Create Task'}
            </button>
            <button type="button" onClick={() => setShowAdd(false)} className="text-xs text-slate-500 hover:text-slate-700 dark:text-slate-400">Cancel</button>
          </div>
        </div>
      )}

      {loading ? <p className="text-center text-sm text-slate-400 py-8">Loading…</p> : tasks.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">No onboarding tasks found. Add the first task above.</p>
      ) : (
        <div className="space-y-2">
          {tasks.map(t => (
            <div key={t.id} className="surface flex items-center gap-4 p-3">
              <span className="text-lg shrink-0">{CATEGORY_ICONS[t.category] ?? '✅'}</span>
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium text-slate-900 dark:text-white">{t.taskTitle}</p>
                <p className="text-xs text-slate-500 dark:text-slate-400">{t.category} · {t.assignedToName || 'Unassigned'}{t.dueDate ? ` · Due: ${t.dueDate}` : ''}</p>
              </div>
              <div className="flex items-center gap-2 shrink-0">
                {t.isMandatory && <span className="text-xs text-amber-600 dark:text-amber-400 font-medium">Required</span>}
                <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_COLORS[t.status] ?? ''}`}>{t.status}</span>
                {t.status !== 'Completed' && (
                  <button type="button" onClick={() => updateStatus(t.id, 'Completed')} disabled={updatingStatus === t.id}
                    className="rounded-md bg-emerald-600 px-2 py-0.5 text-xs font-medium text-white hover:bg-emerald-700 disabled:opacity-50">
                    {updatingStatus === t.id ? '…' : '✓ Done'}
                  </button>
                )}
                {t.status === 'Pending' && (
                  <button type="button" onClick={() => updateStatus(t.id, 'InProgress')} disabled={updatingStatus === t.id}
                    className="rounded-md border border-blue-200 px-2 py-0.5 text-xs font-medium text-blue-600 hover:bg-blue-50 disabled:opacity-50 dark:border-blue-500/30 dark:text-blue-400">
                    Start
                  </button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Reports Tab ───────────────────────────────────────────────────────────────

function ReportsTab() {
  const [pipeline, setPipeline] = useState<{ byStage: { stage: string; count: number }[]; hiredThisMonth: number } | null>(null);
  const [timeToHire, setTimeToHire] = useState<{ hiredCount: number; avgDaysToHire: number } | null>(null);
  const [source, setSource] = useState<{ candidatesBySource: { source: string; total: number }[]; hiredBySource: { source: string; hired: number }[] } | null>(null);

  useEffect(() => {
    recruitmentReportsApi.pipelineSummary().then(setPipeline).catch(() => {});
    recruitmentReportsApi.timeToHire().then(setTimeToHire).catch(() => {});
    recruitmentReportsApi.sourceEffectiveness().then(setSource).catch(() => {});
  }, []);

  return (
    <div className="space-y-5">
      <div className="grid gap-4 sm:grid-cols-3">
        <div className="surface p-5">
          <p className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400 mb-1">Hired This Month</p>
          <p className="text-3xl font-bold text-slate-900 dark:text-white">{pipeline?.hiredThisMonth ?? '—'}</p>
        </div>
        <div className="surface p-5">
          <p className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400 mb-1">Avg. Time to Hire</p>
          <p className="text-3xl font-bold text-slate-900 dark:text-white">{timeToHire ? `${timeToHire.avgDaysToHire}d` : '—'}</p>
        </div>
        <div className="surface p-5">
          <p className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400 mb-1">Total Hired (YTD)</p>
          <p className="text-3xl font-bold text-slate-900 dark:text-white">{timeToHire?.hiredCount ?? '—'}</p>
        </div>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="surface p-5">
          <h3 className="text-sm font-semibold text-slate-800 dark:text-white mb-4">Pipeline by Stage</h3>
          {pipeline?.byStage.map(s => (
            <div key={s.stage} className="flex items-center gap-3 mb-2">
              <span className="w-24 shrink-0 text-xs text-slate-500 dark:text-slate-400">{s.stage}</span>
              <div className="flex-1 h-2 rounded-full bg-slate-200 dark:bg-white/10">
                <div className="h-2 rounded-full bg-sapphire" style={{ width: `${Math.min(100, s.count * 5)}%` }} />
              </div>
              <span className="shrink-0 text-xs font-semibold text-slate-900 dark:text-white w-8 text-right">{s.count}</span>
            </div>
          ))}
          {!pipeline?.byStage.length && <p className="text-sm text-slate-400">No pipeline data.</p>}
        </div>

        <div className="surface p-5">
          <h3 className="text-sm font-semibold text-slate-800 dark:text-white mb-4">Source Effectiveness</h3>
          {source?.candidatesBySource.map(s => {
            const hired = source.hiredBySource.find(h => h.source === s.source)?.hired ?? 0;
            const rate = s.total > 0 ? (hired / s.total * 100).toFixed(0) : '0';
            return (
              <div key={s.source} className="flex items-center justify-between mb-2 text-sm">
                <span className="text-slate-600 dark:text-slate-400">{s.source}</span>
                <span className="text-xs text-slate-500 dark:text-slate-400">{s.total} candidates · {hired} hired ({rate}%)</span>
              </div>
            );
          })}
          {!source?.candidatesBySource.length && <p className="text-sm text-slate-400">No source data.</p>}
        </div>
      </div>
    </div>
  );
}

// ── Recruitment AI Insights Tab ───────────────────────────────────────────────

function RecruitmentAITab() {
  const [insights, setInsights] = useState<{ type: string; severity: string; title: string; description: string; isAdvisory: boolean }[]>([]);
  const [loading, setLoading] = useState(true);
  const [genTime, setGenTime] = useState<string | null>(null);

  useEffect(() => {
    recruitmentReportsApi.aiInsights()
      .then((r: { generatedAt: string; insights: typeof insights }) => { setInsights(r.insights); setGenTime(r.generatedAt); })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  const SEV_COLORS: Record<string, string> = {
    High: 'border-l-rose-500 bg-rose-50 dark:bg-rose-500/5',
    Medium: 'border-l-amber-500 bg-amber-50 dark:bg-amber-500/5',
    Low: 'border-l-blue-400 bg-blue-50 dark:bg-blue-500/5',
  };

  return (
    <div className="space-y-4">
      <div className="rounded-xl border border-amber-200 bg-amber-50 dark:border-amber-500/20 dark:bg-amber-500/5 px-4 py-3 text-xs text-amber-700 dark:text-amber-400">
        <Bot className="inline h-3.5 w-3.5 mr-1.5" />
        All insights are <strong>advisory only</strong>. Final hiring and staffing decisions remain with authorized HR personnel.
        {genTime && <span className="ml-2 opacity-60">Generated: {new Date(genTime).toLocaleString()}</span>}
      </div>

      {loading ? <p className="text-center text-sm text-slate-400 py-8">Loading AI insights…</p> : insights.length === 0 ? (
        <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">No insights generated yet.</p>
      ) : (
        <div className="space-y-3">
          {insights.map((ins, i) => (
            <div key={i} className={`rounded-xl border-l-4 p-4 ${SEV_COLORS[ins.severity] ?? 'border-l-slate-400 bg-slate-50'}`}>
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <p className="text-sm font-semibold text-slate-900 dark:text-slate-100">{ins.title}</p>
                  <p className="mt-1 text-xs text-slate-600 dark:text-slate-400">{ins.description}</p>
                </div>
                <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-medium ${
                  ins.severity === 'High' ? 'bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-400' :
                  ins.severity === 'Medium' ? 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400' :
                  'bg-blue-100 text-blue-700 dark:bg-blue-500/20 dark:text-blue-400'
                }`}>{ins.severity}</span>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Page ──────────────────────────────────────────────────────────────────────

type Tab = 'overview' | 'requisitions' | 'openings' | 'candidates' | 'workforce' | 'interviews' | 'assessments' | 'offers' | 'onboarding' | 'reports' | 'ai';

export function RecruitmentPage({ initialTab }: { initialTab?: Tab } = {}) {
  const [tab, setTab] = useState<Tab>(initialTab ?? 'overview');
  const [selectedOpening, setSelectedOpening] = useState<JobOpening | null>(null);
  const [requisitionToOpen, setRequisitionToOpen] = useState<ManpowerRequisition | null>(null);

  const tabs: { id: Tab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
    { id: 'overview', label: 'Overview', icon: ClipboardList },
    { id: 'workforce', label: 'Workforce Planning', icon: Target },
    { id: 'requisitions', label: 'Requisitions', icon: AlertCircle },
    { id: 'openings', label: 'Job Openings', icon: Briefcase },
    { id: 'candidates', label: 'Talent Pool', icon: Users },
    { id: 'interviews', label: 'Interviews', icon: Calendar },
    { id: 'assessments', label: 'Assessments', icon: BookOpen },
    { id: 'offers', label: 'Offers', icon: Award },
    { id: 'onboarding', label: 'Onboarding', icon: UserPlus },
    { id: 'reports', label: 'Reports', icon: BarChart3 },
    { id: 'ai', label: 'AI Insights', icon: Bot },
  ];

  const handleCreateOpening = (req: ManpowerRequisition) => {
    setRequisitionToOpen(req);
    setTab('openings');
  };

  return (
    <div className="space-y-5 p-4 sm:p-6">
      <div>
        <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Recruitment & Requisitions</h1>
        <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
          Manage manpower requisitions, job openings, and candidate pipeline end-to-end.
        </p>
      </div>

      {/* Tab bar */}
      <div className="flex gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]" style={{ width: 'fit-content' }}>
        {tabs.map(t => (
          <button
            key={t.id}
            type="button"
            onClick={() => { setTab(t.id); if (t.id !== 'openings') setSelectedOpening(null); }}
            className={`flex items-center gap-1.5 rounded-lg px-4 py-1.5 text-sm font-medium transition ${
              tab === t.id
                ? 'bg-white text-sapphire shadow-sm dark:bg-white/10 dark:text-cyanAccent'
                : 'text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'
            }`}
          >
            <t.icon className="h-3.5 w-3.5" />
            {t.label}
          </button>
        ))}
      </div>

      {/* Content */}
      {tab === 'overview' && <OverviewTab onNavigate={setTab} />}
      {tab === 'workforce' && <WorkforcePlanningTab />}
      {tab === 'requisitions' && <RequisitionsTab onCreateOpening={handleCreateOpening} />}
      {tab === 'openings' && !selectedOpening && (
        <OpeningsTab
          onSelectOpening={setSelectedOpening}
          requisitionToOpen={requisitionToOpen}
          onOpeningCreated={() => setRequisitionToOpen(null)}
        />
      )}
      {tab === 'openings' && selectedOpening && (
        <PipelineView
          opening={selectedOpening}
          onBack={() => setSelectedOpening(null)}
        />
      )}
      {tab === 'candidates' && <CandidatesTab />}
      {tab === 'interviews' && <InterviewsTab />}
      {tab === 'assessments' && <AssessmentsTab />}
      {tab === 'offers' && <OffersTab />}
      {tab === 'onboarding' && <OnboardingTab />}
      {tab === 'reports' && <ReportsTab />}
      {tab === 'ai' && <RecruitmentAITab />}
    </div>
  );
}
