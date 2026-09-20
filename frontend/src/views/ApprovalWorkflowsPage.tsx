'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { AlertTriangle, ArrowDown, ArrowLeft, ArrowUp, CheckCircle2, Eye, Info, Plus, Power, RefreshCw, Trash2, Users } from 'lucide-react';
import {
  APPROVER_TYPES,
  APPROVER_TYPE_LABELS,
  apiErrorBody,
  approvalWorkflowsApi,
} from '../api/approvals';
import type {
  ApprovalEntityDescriptor,
  ApprovalRoutePreview,
  ApprovalWorkflow,
  ApprovalWorkflowInput,
  ApprovalWorkflowStepInput,
  ApproverType,
} from '../api/approvals';
import { departmentsApi, gradesApi } from '../api/organization';
import type { DepartmentDto, GradeDto } from '../api/organization';
import { rolesApi } from '../api/identity';
import { ApprovalGovernanceToggle } from '../components/ApprovalGovernanceToggle';
import { EmployeeSearchSelect } from '../components/EmployeeSearchSelect';
import type { EmployeeSelection } from '../components/EmployeeSearchSelect';
import { Modal } from '../components/Modal';
import { StatusChip } from '../components/StatusChip';

// ── helpers ──────────────────────────────────────────────────────────────────

type Scope = 'default' | 'department' | 'grade' | 'both';

const KNOWN_ROLES = ['HR Manager', 'Manager', 'HR Officer', 'Admin', 'Payroll Officer', 'Auditor'];

/** The role label stored for person-type steps; only Role steps take a free choice. */
const roleLabelFor = (type: ApproverType, current: string): string => {
  switch (type) {
    case 'Manager': return 'Manager';
    case 'Supervisor': return 'Supervisor';
    case 'DepartmentHead': return 'Department Head';
    case 'HR': return 'HR Manager';
    case 'SpecificEmployee': return 'Approver';
    default: return current;
  }
};

const scopeOf = (w: { departmentId: string | null; gradeId: string | null }): Scope =>
  w.departmentId && w.gradeId ? 'both' : w.departmentId ? 'department' : w.gradeId ? 'grade' : 'default';

const scopeLabel = (w: { departmentId: string | null; gradeId: string | null; isDefault: boolean }, departments: DepartmentDto[], grades: GradeDto[]) => {
  const dept = departments.find((d) => d.id === w.departmentId)?.nameEn ?? (w.departmentId ? 'Department' : null);
  const grade = grades.find((g) => g.id === w.gradeId)?.name ?? (w.gradeId ? 'Grade' : null);
  if (dept && grade) return `${dept} · ${grade}`;
  if (dept) return `Department: ${dept}`;
  if (grade) return `Grade: ${grade}`;
  return w.isDefault ? 'Tenant default' : 'Tenant-wide';
};

const stepSummary = (w: ApprovalWorkflow) =>
  [...w.steps].sort((a, b) => a.stepOrder - b.stepOrder)
    .map((s) => `${s.stepOrder}. ${s.stepName || s.approverRole}${s.isFinalStep ? ' (final)' : ''}`)
    .join(' → ');

interface StepDraft {
  key: string;
  stepName: string;
  approverType: ApproverType;
  approverRole: string;
  specificEmployee: EmployeeSelection | null;
  specificEmployeeId: number | null;
  escalationAfterHours: string;
}

interface WorkflowDraft {
  id: string | null;
  code: string;
  name: string;
  entityName: string;
  isActive: boolean;
  scope: Scope;
  departmentId: string;
  gradeId: string;
  steps: StepDraft[];
  inFlightRequests: number;
}

let keySeq = 0;
const nextKey = () => `s${++keySeq}`;

const blankStep = (type: ApproverType = 'Manager'): StepDraft => ({
  key: nextKey(), stepName: APPROVER_TYPE_LABELS[type], approverType: type, approverRole: roleLabelFor(type, ''),
  specificEmployee: null, specificEmployeeId: null, escalationAfterHours: '',
});

const draftFrom = (w: ApprovalWorkflow | null, entityName: string): WorkflowDraft => w
  ? {
    id: w.id, code: w.code, name: w.name, entityName: w.entityName, isActive: w.isActive, scope: scopeOf(w),
    departmentId: w.departmentId ?? '', gradeId: w.gradeId ?? '', inFlightRequests: w.inFlightRequests ?? 0,
    steps: [...w.steps].sort((a, b) => a.stepOrder - b.stepOrder).map((s) => ({
      key: nextKey(),
      stepName: s.stepName,
      approverType: (APPROVER_TYPES as readonly string[]).includes(s.approverType) ? s.approverType as ApproverType : 'Role',
      approverRole: s.approverRole,
      specificEmployee: null,
      specificEmployeeId: s.specificEmployeeId,
      escalationAfterHours: s.escalationAfterHours ? String(s.escalationAfterHours) : '',
    })),
  }
  : {
    id: null, code: '', name: '', entityName, isActive: true, scope: 'default', departmentId: '', gradeId: '', inFlightRequests: 0,
    steps: [blankStep('Manager'), blankStep('HR')],
  };

/** Client-side echo of the server's chain rules, so the admin sees the problem before a round trip. */
const validateDraft = (d: WorkflowDraft): string[] => {
  const errors: string[] = [];
  if (!d.code.trim()) errors.push('A code is required.');
  if (!d.name.trim()) errors.push('A name is required.');
  if (!d.entityName) errors.push('Choose which kind of request this workflow approves.');
  if ((d.scope === 'department' || d.scope === 'both') && !d.departmentId) errors.push('Choose a department for this scope.');
  if ((d.scope === 'grade' || d.scope === 'both') && !d.gradeId) errors.push('Choose a grade for this scope.');
  if (d.steps.length === 0) errors.push('A workflow needs at least one step. The last step is the final step and completes the request.');
  d.steps.forEach((s, i) => {
    if (!s.stepName.trim()) errors.push(`Step ${i + 1} needs a name.`);
    if (s.approverType === 'Role' && !s.approverRole.trim()) errors.push(`Step ${i + 1} needs a role.`);
    if (s.approverType === 'SpecificEmployee' && !s.specificEmployeeId) errors.push(`Step ${i + 1} needs an employee.`);
    const hours = s.escalationAfterHours.trim();
    if (hours && (!/^\d+$/.test(hours) || Number(hours) < 1 || Number(hours) > 720)) errors.push(`Step ${i + 1}: escalation hours must be between 1 and 720.`);
  });
  return errors;
};

const toInput = (d: WorkflowDraft): ApprovalWorkflowInput => ({
  code: d.code.trim().toUpperCase(),
  name: d.name.trim(),
  entityName: d.entityName,
  isActive: d.isActive,
  isDefault: d.scope === 'default',
  departmentId: d.scope === 'department' || d.scope === 'both' ? d.departmentId : null,
  gradeId: d.scope === 'grade' || d.scope === 'both' ? d.gradeId : null,
  steps: d.steps.map<ApprovalWorkflowStepInput>((s, i) => ({
    stepOrder: i + 1,
    stepName: s.stepName.trim(),
    approverType: s.approverType,
    approverRole: roleLabelFor(s.approverType, s.approverRole.trim()),
    specificEmployeeId: s.approverType === 'SpecificEmployee' ? s.specificEmployeeId : null,
    escalationAfterHours: s.escalationAfterHours.trim() ? Number(s.escalationAfterHours) : null,
    // F1's rule: exactly one final step and it is the last one. The editor keeps them in step order,
    // so the last row is the final step by construction; the server re-checks.
    isFinalStep: i === d.steps.length - 1,
  })),
});

const serverErrorMessage = (err: unknown, fallback: string) => {
  const body = apiErrorBody(err);
  const status = (err as { response?: { status?: number } })?.response?.status;
  if (body.message) return body.message;
  if (status === 403) return 'You do not have permission to manage approval workflows.';
  return fallback;
};

// ── page ─────────────────────────────────────────────────────────────────────

export function ApprovalWorkflowsPage() {
  const [entities, setEntities] = useState<ApprovalEntityDescriptor[]>([]);
  const [entity, setEntity] = useState<string>('');
  const [workflows, setWorkflows] = useState<ApprovalWorkflow[]>([]);
  const [departments, setDepartments] = useState<DepartmentDto[]>([]);
  const [grades, setGrades] = useState<GradeDto[]>([]);
  const [roles, setRoles] = useState<string[]>(KNOWN_ROLES);
  const [loading, setLoading] = useState(true);
  const [listLoading, setListLoading] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [editing, setEditing] = useState<WorkflowDraft | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveErrors, setSaveErrors] = useState<string[]>([]);
  const [toggling, setToggling] = useState<string | null>(null);
  const [previewEntity, setPreviewEntity] = useState('');
  const [previewEmployee, setPreviewEmployee] = useState<EmployeeSelection | null>(null);
  const [preview, setPreview] = useState<ApprovalRoutePreview | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState('');

  const loadReference = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const [ents, depts, grds] = await Promise.all([
        approvalWorkflowsApi.entities(),
        departmentsApi.list(undefined, 1, 200).then((r) => r.items).catch(() => [] as DepartmentDto[]),
        gradesApi.list(1, 200).then((r) => r.items).catch(() => [] as GradeDto[]),
      ]);
      setEntities(ents);
      setDepartments(depts);
      setGrades(grds);
      setEntity((current) => current || ents[0]?.entityName || '');
      setPreviewEntity((current) => current || ents[0]?.entityName || '');
    } catch (err) {
      setError(serverErrorMessage(err, 'Could not load approval configuration from the API.'));
    } finally {
      setLoading(false);
    }
    // Roles are Admin-only; anyone else keeps the known-role suggestions.
    rolesApi.list().then((r) => setRoles(Array.from(new Set([...r.filter((x) => x.isActive).map((x) => x.name), ...KNOWN_ROLES]))))
      .catch(() => undefined);
  }, []);

  const loadWorkflows = useCallback(async () => {
    if (!entity) return;
    setListLoading(true);
    try {
      const res = await approvalWorkflowsApi.list({ entityName: entity, page: 1, pageSize: 100 });
      setWorkflows(res.items);
      setError('');
    } catch (err) {
      setError(serverErrorMessage(err, 'Could not load workflows from the API.'));
      setWorkflows([]);
    } finally {
      setListLoading(false);
    }
  }, [entity]);

  useEffect(() => { loadReference(); }, [loadReference]);
  useEffect(() => { loadWorkflows(); }, [loadWorkflows]);

  const currentEntity = useMemo(() => entities.find((e) => e.entityName === entity), [entities, entity]);

  const openNew = () => { setSaveErrors([]); setEditing(draftFrom(null, entity)); };
  const openEdit = (w: ApprovalWorkflow) => { setSaveErrors([]); setEditing(draftFrom(w, w.entityName)); };

  const save = async () => {
    if (!editing) return;
    const errors = validateDraft(editing);
    if (errors.length > 0) { setSaveErrors(errors); return; }
    setSaving(true);
    setSaveErrors([]);
    try {
      const input = toInput(editing);
      if (editing.id) await approvalWorkflowsApi.update(editing.id, input);
      else await approvalWorkflowsApi.create(input);
      setNotice(editing.id
        ? `Workflow ${input.code} saved. The change applies to new requests only; requests already in flight keep the chain they were routed by.`
        : `Workflow ${input.code} created. It applies to requests submitted from now on.`);
      setEditing(null);
      if (entity !== input.entityName) setEntity(input.entityName);
      else await loadWorkflows();
    } catch (err) {
      setSaveErrors([serverErrorMessage(err, 'Could not save the workflow. Please try again.')]);
    } finally {
      setSaving(false);
    }
  };

  const toggleActive = async (w: ApprovalWorkflow) => {
    setToggling(w.id);
    setError('');
    try {
      if (w.isActive) {
        await approvalWorkflowsApi.deactivate(w.id);
        setNotice(`${w.code} deactivated. New requests will no longer use it; ${w.inFlightRequests} in-flight request${w.inFlightRequests === 1 ? '' : 's'} keep${w.inFlightRequests === 1 ? 's' : ''} it.`);
      } else {
        await approvalWorkflowsApi.activate(w.id);
        setNotice(`${w.code} activated for new requests.`);
      }
      await loadWorkflows();
    } catch (err) {
      setError(serverErrorMessage(err, 'Could not change the workflow status.'));
    } finally {
      setToggling(null);
    }
  };

  const runPreview = async () => {
    if (!previewEntity) return;
    setPreviewLoading(true);
    setPreviewError('');
    setPreview(null);
    try {
      setPreview(await approvalWorkflowsApi.preview(previewEntity, previewEmployee?.intId ?? null));
    } catch (err) {
      setPreviewError(serverErrorMessage(err, 'Could not run the preview.'));
    } finally {
      setPreviewLoading(false);
    }
  };

  // ── draft editing helpers ──
  const patch = (p: Partial<WorkflowDraft>) => setEditing((d) => (d ? { ...d, ...p } : d));
  const patchStep = (key: string, p: Partial<StepDraft>) =>
    setEditing((d) => d ? { ...d, steps: d.steps.map((s) => (s.key === key ? { ...s, ...p } : s)) } : d);
  const moveStep = (index: number, dir: -1 | 1) =>
    setEditing((d) => {
      if (!d) return d;
      const target = index + dir;
      if (target < 0 || target >= d.steps.length) return d;
      const steps = [...d.steps];
      [steps[index], steps[target]] = [steps[target], steps[index]];
      return { ...d, steps };
    });
  const removeStep = (key: string) => setEditing((d) => (d ? { ...d, steps: d.steps.filter((s) => s.key !== key) } : d));
  const addStep = () => setEditing((d) => (d ? { ...d, steps: [...d.steps, blankStep('Role')] } : d));

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <Link href="/approvals" className="inline-flex items-center gap-1 text-xs font-semibold text-slate-500 hover:underline dark:text-slate-400">
            <ArrowLeft className="h-3.5 w-3.5" /> Approval Center
          </Link>
          <h1 className="mt-1 text-2xl font-extrabold text-slate-950 dark:text-white">Approval Workflows</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">Who approves each kind of request, in what order, and for whom</p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <button type="button" onClick={() => { loadReference(); loadWorkflows(); }} className="btn-secondary inline-flex h-9 items-center gap-2 px-3 text-sm">
            <RefreshCw className="h-4 w-4" /> Refresh
          </button>
          <button type="button" onClick={openNew} disabled={loading || !entity} className="btn-primary inline-flex h-9 items-center gap-2 px-3 text-sm disabled:opacity-60">
            <Plus className="h-4 w-4" /> New workflow
          </button>
        </div>
      </div>

      <div className="flex items-start gap-2 rounded-lg border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-800 dark:border-blue-500/30 dark:bg-blue-500/10 dark:text-blue-200">
        <Info className="mt-0.5 h-4 w-4 shrink-0" />
        <p>Changes here apply to <strong>new requests only</strong>. A request already in flight keeps the workflow it was routed by until it completes, even if that workflow is edited or deactivated.</p>
      </div>

      {error && (
        <div role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm font-medium text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">{error}</div>
      )}
      {notice && (
        <div role="status" className="flex items-start justify-between gap-3 rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm font-medium text-emerald-700 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-300">
          <span className="inline-flex items-start gap-2"><CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" />{notice}</span>
          <button type="button" onClick={() => setNotice('')} className="text-xs font-semibold underline">Dismiss</button>
        </div>
      )}

      {/* Governance */}
      <section className="surface rounded-xl p-5">
        <ApprovalGovernanceToggle onChanged={() => setPreview(null)} />
      </section>

      {/* Entity tabs + list */}
      <div className="flex flex-wrap items-center gap-2 rounded-lg border border-slate-200 bg-white p-2 dark:border-white/10 dark:bg-white/[0.03]">
        {loading && entities.length === 0 && <span className="px-2 text-sm text-slate-400">Loading request types…</span>}
        {entities.map((e) => (
          <button
            key={e.entityName}
            type="button"
            onClick={() => setEntity(e.entityName)}
            className={`h-8 rounded-md px-3 text-sm font-semibold transition ${entity === e.entityName ? 'bg-slate-900 text-white dark:bg-white dark:text-slate-950' : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/10'}`}
          >
            {e.label}
          </button>
        ))}
      </div>

      {currentEntity && (
        <div className={`flex items-start gap-2 rounded-lg border px-4 py-3 text-xs ${currentEntity.enforcedByModule ? 'border-slate-200 bg-slate-50 text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300' : 'border-amber-200 bg-amber-50 text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200'}`}>
          {currentEntity.enforcedByModule ? <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" /> : <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />}
          <p><strong>{currentEntity.label}:</strong> {currentEntity.note}</p>
        </div>
      )}

      <div className="surface overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full min-w-[900px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Workflow', 'Applies to', 'Steps', 'In flight', 'Status', ''].map((h) => (
                  <th key={h} className="px-4 py-3 text-left text-xs font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {(loading || listLoading) && <tr><td colSpan={6} className="py-12 text-center"><div className="mx-auto h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></td></tr>}
              {!loading && !listLoading && workflows.length === 0 && (
                <tr>
                  <td colSpan={6} className="py-16 text-center">
                    <Users className="mx-auto mb-3 h-10 w-10 text-slate-200 dark:text-slate-700" />
                    <p className="text-sm text-slate-400 dark:text-slate-500">No workflow configured for {currentEntity?.label ?? 'this request type'}.</p>
                    <p className="mt-1 text-xs text-slate-400 dark:text-slate-500">Until one exists, submissions of this type are refused with &ldquo;approval route not configured&rdquo;.</p>
                  </td>
                </tr>
              )}
              {!loading && !listLoading && workflows.map((w) => (
                <tr key={w.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-3">
                    <p className="font-medium text-slate-900 dark:text-white">{w.name}</p>
                    <p className="mt-0.5 font-mono text-xs text-slate-400">{w.code}</p>
                  </td>
                  <td className="px-4 py-3 text-slate-700 dark:text-slate-200">{scopeLabel(w, departments, grades)}</td>
                  <td className="px-4 py-3 text-xs text-slate-600 dark:text-slate-300">{stepSummary(w) || <span className="text-rose-500">No steps</span>}</td>
                  <td className="px-4 py-3 text-slate-700 dark:text-slate-200">{w.inFlightRequests}</td>
                  <td className="px-4 py-3"><StatusChip label={w.isActive ? 'Active' : 'Inactive'} tone={w.isActive ? 'emerald' : 'slate'} dot /></td>
                  <td className="px-4 py-3">
                    <div className="flex justify-end gap-1">
                      <button type="button" onClick={() => openEdit(w)} className="btn-secondary h-8 px-3 text-xs">Edit</button>
                      <button type="button" onClick={() => toggleActive(w)} disabled={toggling === w.id}
                        className={`btn-secondary inline-flex h-8 items-center gap-1 px-3 text-xs disabled:opacity-60 ${w.isActive ? 'text-rose-500 hover:border-rose-300' : 'text-emerald-600 hover:border-emerald-300'}`}>
                        <Power className="h-3.5 w-3.5" /> {toggling === w.id ? 'Saving…' : w.isActive ? 'Deactivate' : 'Activate'}
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {/* Preview */}
      <section className="surface rounded-xl p-5">
        <h2 className="inline-flex items-center gap-2 text-sm font-bold text-slate-950 dark:text-white"><Eye className="h-4 w-4" /> Preview: who would approve?</h2>
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">Asks the same router a real submission uses, so this is exactly what would happen if the employee submitted now.</p>
        <div className="mt-4 grid gap-3 md:grid-cols-[220px_1fr_auto]">
          <div>
            <label htmlFor="preview-entity" className="mb-1 block text-xs font-semibold text-slate-600 dark:text-slate-300">Request type</label>
            <select id="preview-entity" value={previewEntity} onChange={(e) => setPreviewEntity(e.target.value)} className="input w-full">
              {entities.map((e) => <option key={e.entityName} value={e.entityName}>{e.label}</option>)}
            </select>
          </div>
          <div>
            <span className="mb-1 block text-xs font-semibold text-slate-600 dark:text-slate-300">Employee (optional — leave empty for a request with no employee)</span>
            <EmployeeSearchSelect value={previewEmployee} onChange={setPreviewEmployee} placeholder="Search an employee…" />
          </div>
          <div className="flex items-end">
            <button type="button" onClick={runPreview} disabled={previewLoading || !previewEntity} className="btn-primary h-9 px-4 text-sm disabled:opacity-60">{previewLoading ? 'Checking…' : 'Preview'}</button>
          </div>
        </div>
        {previewError && <p role="alert" className="mt-3 text-xs font-medium text-rose-600 dark:text-rose-300">{previewError}</p>}
        {preview && (
          <div className="mt-4 space-y-3">
            {preview.outcome === 'Routed' ? (
              <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-200">
                <strong>{preview.workflowName}</strong> <span className="font-mono text-xs">({preview.workflowCode})</span> applies
                {preview.employeeName ? <> to <strong>{preview.employeeName}</strong></> : null}, matched on <strong>{preview.matchedOn}</strong>.
                {preview.requireDistinctApproverPerStep && <span className="ml-1">Different-person rule is on.</span>}
              </div>
            ) : (
              <div role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-800 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200">
                <strong>{preview.outcome === 'NotConfigured' ? 'No workflow applies.' : 'The workflow that applies is invalid.'}</strong> {preview.message}
                {preview.errorCode && <span className="ml-1 font-mono text-xs">[{preview.errorCode}]</span>}
              </div>
            )}
            {preview.steps.length > 0 && (
              <ol className="divide-y divide-slate-100 rounded-lg border border-slate-200 dark:divide-white/[0.06] dark:border-white/10">
                {preview.steps.map((s) => (
                  <li key={s.stepOrder} className="flex flex-wrap items-center gap-3 px-4 py-2.5 text-sm">
                    <span className="inline-flex h-6 w-6 items-center justify-center rounded-full bg-slate-900 text-xs font-bold text-white dark:bg-white dark:text-slate-950">{s.stepOrder}</span>
                    <div className="min-w-0 flex-1">
                      <p className="font-semibold text-slate-800 dark:text-slate-100">{s.stepName} {s.isFinalStep && <span className="ml-1 rounded bg-emerald-100 px-1.5 py-0.5 text-[10px] font-bold uppercase text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-300">Final</span>}</p>
                      <p className="text-xs text-slate-500 dark:text-slate-400">{APPROVER_TYPE_LABELS[s.approverType as ApproverType] ?? s.approverType} · {s.approverRole}{s.escalationAfterHours ? ` · escalates after ${s.escalationAfterHours}h` : ''}</p>
                    </div>
                    <div className="text-right">
                      <p className={`text-sm font-semibold ${s.escalated ? 'text-rose-600 dark:text-rose-300' : 'text-slate-800 dark:text-slate-100'}`}>{s.approverName || `${s.queueRole} queue`}</p>
                      <p className="text-xs text-slate-400">{s.escalated ? `escalated to ${s.queueRole} queue` : s.approverUserId ? 'decides personally' : `anyone with role ${s.queueRole}`}</p>
                    </div>
                  </li>
                ))}
              </ol>
            )}
            {preview.warnings.length > 0 && (
              <ul className="space-y-1">
                {preview.warnings.map((w) => (
                  <li key={w} className="flex items-start gap-2 text-xs text-amber-700 dark:text-amber-300"><AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />{w}</li>
                ))}
              </ul>
            )}
          </div>
        )}
      </section>

      {/* Editor */}
      <Modal isOpen={!!editing} size="xl" title={editing?.id ? `Edit workflow ${editing.code}` : 'New approval workflow'} onClose={() => setEditing(null)}
        footer={
          <>
            <button type="button" onClick={() => setEditing(null)} className="btn-secondary">Cancel</button>
            <button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">{saving ? 'Saving…' : editing?.id ? 'Save changes' : 'Create workflow'}</button>
          </>
        }>
        {editing && (
          <div className="space-y-5">
            {editing.id && editing.inFlightRequests > 0 && (
              <div className="flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
                <Info className="mt-0.5 h-4 w-4 shrink-0" />
                <p>{editing.inFlightRequests} request{editing.inFlightRequests === 1 ? ' is' : 's are'} in flight on this workflow. They keep the chain they were routed by; your edits apply to new requests only.</p>
              </div>
            )}

            <div className="grid gap-3 sm:grid-cols-3">
              <div>
                <label htmlFor="wf-code" className="mb-1 block text-xs font-semibold text-slate-600 dark:text-slate-300">Code</label>
                <input id="wf-code" value={editing.code} onChange={(e) => patch({ code: e.target.value })} className="input w-full font-mono uppercase" placeholder="LEAVE-ENG" maxLength={80} />
              </div>
              <div>
                <label htmlFor="wf-name" className="mb-1 block text-xs font-semibold text-slate-600 dark:text-slate-300">Name</label>
                <input id="wf-name" value={editing.name} onChange={(e) => patch({ name: e.target.value })} className="input w-full" placeholder="Engineering leave approval" maxLength={180} />
              </div>
              <div>
                <label htmlFor="wf-entity" className="mb-1 block text-xs font-semibold text-slate-600 dark:text-slate-300">Request type</label>
                <select id="wf-entity" value={editing.entityName} onChange={(e) => patch({ entityName: e.target.value })} className="input w-full">
                  {entities.map((e) => <option key={e.entityName} value={e.entityName}>{e.label}</option>)}
                </select>
              </div>
            </div>

            <fieldset>
              <legend className="mb-1 text-xs font-semibold text-slate-600 dark:text-slate-300">Applies to</legend>
              <div className="flex flex-wrap gap-2">
                {([
                  ['default', 'Everyone (tenant default)'],
                  ['department', 'A department'],
                  ['grade', 'A grade'],
                  ['both', 'A department and grade'],
                ] as const).map(([value, label]) => (
                  <label key={value} className={`inline-flex cursor-pointer items-center gap-2 rounded-md border px-3 py-1.5 text-sm ${editing.scope === value ? 'border-sapphire bg-blue-50 text-slate-900 dark:bg-blue-500/10 dark:text-white' : 'border-slate-200 text-slate-600 dark:border-white/10 dark:text-slate-300'}`}>
                    <input type="radio" name="wf-scope" value={value} checked={editing.scope === value} onChange={() => patch({ scope: value })} className="accent-sapphire" />
                    {label}
                  </label>
                ))}
              </div>
              <div className="mt-2 grid gap-3 sm:grid-cols-2">
                {(editing.scope === 'department' || editing.scope === 'both') && (
                  <div>
                    <label htmlFor="wf-dept" className="mb-1 block text-xs font-semibold text-slate-600 dark:text-slate-300">Department</label>
                    <select id="wf-dept" value={editing.departmentId} onChange={(e) => patch({ departmentId: e.target.value })} className="input w-full">
                      <option value="">Choose…</option>
                      {departments.map((d) => <option key={d.id} value={d.id}>{d.nameEn} ({d.code})</option>)}
                    </select>
                  </div>
                )}
                {(editing.scope === 'grade' || editing.scope === 'both') && (
                  <div>
                    <label htmlFor="wf-grade" className="mb-1 block text-xs font-semibold text-slate-600 dark:text-slate-300">Grade</label>
                    <select id="wf-grade" value={editing.gradeId} onChange={(e) => patch({ gradeId: e.target.value })} className="input w-full">
                      <option value="">Choose…</option>
                      {grades.map((g) => <option key={g.id} value={g.id}>{g.name} ({g.code})</option>)}
                    </select>
                  </div>
                )}
              </div>
              <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">The most specific active workflow wins: department + grade, then department, then grade, then the tenant default. Only one active workflow may cover the same scope for a request type.</p>
            </fieldset>

            <div>
              <div className="mb-2 flex items-center justify-between">
                <h3 className="text-xs font-semibold text-slate-600 dark:text-slate-300">Steps, in order</h3>
                <button type="button" onClick={addStep} className="btn-secondary inline-flex h-8 items-center gap-1 px-3 text-xs"><Plus className="h-3.5 w-3.5" /> Add step</button>
              </div>
              {editing.steps.length === 0 && (
                <p className="rounded-lg border border-dashed border-slate-300 px-4 py-6 text-center text-sm text-slate-400 dark:border-white/20">No steps yet. Add at least one.</p>
              )}
              <ol className="space-y-2">
                {editing.steps.map((s, i) => {
                  const isLast = i === editing.steps.length - 1;
                  return (
                    <li key={s.key} className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
                      <div className="flex flex-wrap items-start gap-3">
                        <span className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-slate-900 text-xs font-bold text-white dark:bg-white dark:text-slate-950">{i + 1}</span>
                        <div className="grid min-w-0 flex-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
                          <div>
                            <label htmlFor={`step-name-${s.key}`} className="mb-1 block text-[11px] font-semibold text-slate-500 dark:text-slate-400">Step name</label>
                            <input id={`step-name-${s.key}`} value={s.stepName} onChange={(e) => patchStep(s.key, { stepName: e.target.value })} className="input w-full" maxLength={120} />
                          </div>
                          <div>
                            <label htmlFor={`step-type-${s.key}`} className="mb-1 block text-[11px] font-semibold text-slate-500 dark:text-slate-400">Approver</label>
                            <select id={`step-type-${s.key}`} value={s.approverType} onChange={(e) => {
                              const type = e.target.value as ApproverType;
                              patchStep(s.key, { approverType: type, approverRole: roleLabelFor(type, type === 'Role' ? s.approverRole : ''), stepName: s.stepName || APPROVER_TYPE_LABELS[type] });
                            }} className="input w-full">
                              {APPROVER_TYPES.map((t) => <option key={t} value={t}>{APPROVER_TYPE_LABELS[t]}</option>)}
                            </select>
                          </div>
                          {s.approverType === 'Role' && (
                            <div>
                              <label htmlFor={`step-role-${s.key}`} className="mb-1 block text-[11px] font-semibold text-slate-500 dark:text-slate-400">Role</label>
                              <input id={`step-role-${s.key}`} list="wf-role-options" value={s.approverRole} onChange={(e) => patchStep(s.key, { approverRole: e.target.value })} className="input w-full" placeholder="HR Manager" maxLength={80} />
                            </div>
                          )}
                          {s.approverType === 'SpecificEmployee' && (
                            <div>
                              <span className="mb-1 block text-[11px] font-semibold text-slate-500 dark:text-slate-400">Employee{s.specificEmployeeId && !s.specificEmployee ? ` (current: #${s.specificEmployeeId})` : ''}</span>
                              <EmployeeSearchSelect value={s.specificEmployee} onChange={(emp) => patchStep(s.key, { specificEmployee: emp, specificEmployeeId: emp ? emp.intId : null })} placeholder="Search…" />
                            </div>
                          )}
                          <div>
                            <label htmlFor={`step-esc-${s.key}`} className="mb-1 block text-[11px] font-semibold text-slate-500 dark:text-slate-400">Escalate after (hours)</label>
                            <input id={`step-esc-${s.key}`} type="number" min={1} max={720} value={s.escalationAfterHours} onChange={(e) => patchStep(s.key, { escalationAfterHours: e.target.value })} className="input w-full" placeholder="none" />
                          </div>
                        </div>
                        <div className="flex shrink-0 items-center gap-1">
                          <button type="button" onClick={() => moveStep(i, -1)} disabled={i === 0} aria-label="Move step up" className="btn-secondary h-8 w-8 p-0 disabled:opacity-30"><ArrowUp className="mx-auto h-3.5 w-3.5" /></button>
                          <button type="button" onClick={() => moveStep(i, 1)} disabled={isLast} aria-label="Move step down" className="btn-secondary h-8 w-8 p-0 disabled:opacity-30"><ArrowDown className="mx-auto h-3.5 w-3.5" /></button>
                          <button type="button" onClick={() => removeStep(s.key)} aria-label="Remove step" className="btn-secondary h-8 w-8 p-0 text-rose-500 hover:border-rose-300"><Trash2 className="mx-auto h-3.5 w-3.5" /></button>
                        </div>
                      </div>
                      <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400">
                        {isLast
                          ? <><strong>Final step.</strong> Approving it completes the request (and moves the leave balance).</>
                          : 'Approving this step hands the request to the next one.'}
                        {(s.approverType === 'Manager' || s.approverType === 'Supervisor' || s.approverType === 'DepartmentHead') && ' If the employee has no such person, the step escalates to the HR Manager queue.'}
                        {s.approverType === 'HR' && ' Anyone holding the HR Manager role can decide it.'}
                      </p>
                    </li>
                  );
                })}
              </ol>
              <datalist id="wf-role-options">{roles.map((r) => <option key={r} value={r} />)}</datalist>
              <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">Rules: exactly one final step and it is the last one; each step needs a role or an employee where its type requires one. The server refuses anything else.</p>
            </div>

            <div className="flex items-center gap-2">
              <input id="wf-active" type="checkbox" checked={editing.isActive} onChange={(e) => patch({ isActive: e.target.checked })} className="h-4 w-4 accent-sapphire" />
              <label htmlFor="wf-active" className="text-sm text-slate-700 dark:text-slate-200">Active — used for new requests</label>
            </div>

            {saveErrors.length > 0 && (
              <ul role="alert" className="space-y-1 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-xs font-medium text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
                {saveErrors.map((e) => <li key={e}>{e}</li>)}
              </ul>
            )}
          </div>
        )}
      </Modal>
    </div>
  );
}
