import client from './client';
import type { PagedResult } from './organization';

export interface ApprovalRequest {
  id: string;
  workflowId: string;
  entityName: string;
  entityId: string;
  title: string;
  status: string;
  currentStepOrder: number;
  requestedByUserId: string | null;
  requestedForEmployeeId: number | null;
  companyId: string | null;
  currentApproverEmployeeId: number | null;
  currentApproverUserId: string | null;
  currentApproverName: string;
  currentApproverRole: string;
  currentApproverType: string;
  currentQueue: string;
  slaHours: number;
  dueAtUtc: string | null;
  isOverdue: boolean;
  ageHours: number;
  lastRoutedAtUtc: string | null;
  escalatedAtUtc: string | null;
  escalatedToRole: string;
  priority: string;
  createdAtUtc: string;
  completedAtUtc: string | null;
  decisions: ApprovalDecision[];
  canDecide: boolean;
  /** W2-E — why the caller cannot decide although the step is routed to them (the different-person rule). */
  decisionBlockedReason?: string | null;
  /** W2-E — 1 on first submission; each resubmission after a send back starts a new round at step 1. */
  submissionRound?: number;
}

export interface ApprovalDecision {
  id: string;
  stepOrder: number;
  decision: string;
  comments: string;
  decidedAtUtc: string;
  submissionRound?: number;
}

/** W2-E — status an approval request holds while it is with its requester after a send back. */
export const RETURNED_TO_REQUESTER = 'ReturnedToRequester';

export interface LeaveResubmitChanges {
  startDate?: string;
  endDate?: string;
  dayType?: string;
  hoursRequested?: number;
  reason?: string;
}

export const approvalsApi = {
  list: (params: { status?: string; entityName?: string; queue?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<ApprovalRequest>>('/api/approval-requests', { params }).then((r) => r.data),

  get: (id: string) =>
    client.get<ApprovalRequest>(`/api/approval-requests/${id}`).then((r) => r.data),

  decide: (id: string, decision: 'Approve' | 'Reject', comments = '') =>
    client.post<ApprovalRequest>(`/api/approval-requests/${id}/decisions`, { decision, comments }).then((r) => r.data),

  /** W2-E (spec S5) — send a pending request back to its requester. The comment is required (1–1000). */
  sendBack: (id: string, comments: string) =>
    client.post<ApprovalRequest>(`/api/approval-requests/${id}/send-back`, { comments }).then((r) => r.data),

  /** W2-E — requests sent back to the caller (self-service scope: only their own). */
  myReturned: (params: { page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<ApprovalRequest>>('/api/my-approval-requests/returned', { params }).then((r) => r.data),

  /** W2-E — the requester resubmits a sent-back request; the chain restarts at step 1. */
  resubmit: (id: string, body: { comments?: string; leave?: LeaveResubmitChanges } = {}) =>
    client.post<ApprovalRequest>(`/api/my-approval-requests/${id}/resubmit`, body).then((r) => r.data),
};

// ── W2-E: workflow configuration ────────────────────────────────────────────

export const APPROVER_TYPES = ['Manager', 'Supervisor', 'DepartmentHead', 'SpecificEmployee', 'HR', 'Role'] as const;
export type ApproverType = (typeof APPROVER_TYPES)[number];

export const APPROVER_TYPE_LABELS: Record<ApproverType, string> = {
  Manager: 'Line manager',
  Supervisor: 'Supervisor',
  DepartmentHead: 'Department head',
  SpecificEmployee: 'A specific employee',
  HR: 'HR Manager queue',
  Role: 'Anyone with a role',
};

export interface ApprovalWorkflowStep {
  id: string;
  stepOrder: number;
  stepName: string;
  approverRole: string;
  approverType: string;
  specificEmployeeId: number | null;
  escalationAfterHours: number | null;
  isFinalStep: boolean;
}

export interface ApprovalWorkflow {
  id: string;
  code: string;
  name: string;
  entityName: string;
  isActive: boolean;
  steps: ApprovalWorkflowStep[];
  departmentId: string | null;
  gradeId: string | null;
  isDefault: boolean;
  /** Requests currently in flight on this workflow. Edits never change them. */
  inFlightRequests: number;
}

export interface ApprovalWorkflowStepInput {
  stepOrder: number;
  stepName: string;
  approverRole: string;
  approverType: string;
  specificEmployeeId?: number | null;
  escalationAfterHours?: number | null;
  isFinalStep: boolean;
}

export interface ApprovalWorkflowInput {
  code: string;
  name: string;
  entityName: string;
  isActive: boolean;
  steps: ApprovalWorkflowStepInput[];
  departmentId?: string | null;
  gradeId?: string | null;
  isDefault: boolean;
}

export interface ApprovalEntityDescriptor {
  entityName: string;
  label: string;
  enforcedByModule: boolean;
  note: string;
}

export interface ApprovalRoutePreviewStep {
  stepOrder: number;
  stepName: string;
  approverType: string;
  approverRole: string;
  isFinalStep: boolean;
  escalationAfterHours: number | null;
  queueRole: string;
  approverEmployeeId: number | null;
  approverUserId: string | null;
  approverName: string;
  escalated: boolean;
}

export interface ApprovalRoutePreview {
  entityName: string;
  employeeId: number | null;
  employeeName: string;
  outcome: 'Routed' | 'NotConfigured' | 'Invalid';
  errorCode: string | null;
  message: string | null;
  workflowId: string | null;
  workflowCode: string | null;
  workflowName: string | null;
  matchedOn: string | null;
  requireDistinctApproverPerStep: boolean;
  steps: ApprovalRoutePreviewStep[];
  warnings: string[];
}

export interface ApprovalGovernanceSettings {
  requireDistinctApproverPerStep: boolean;
}

/** Stable error codes the workflow configuration API returns as `{ code, message }` (HTTP 400). */
export interface ApiErrorBody { code?: string; message?: string }

export function apiErrorBody(err: unknown): ApiErrorBody {
  const data = (err as { response?: { data?: unknown } })?.response?.data;
  if (data && typeof data === 'object') return data as ApiErrorBody;
  return {};
}

export const approvalWorkflowsApi = {
  list: (params: { entityName?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<ApprovalWorkflow>>('/api/approval-workflows', { params }).then((r) => r.data),

  get: (id: string) =>
    client.get<ApprovalWorkflow>(`/api/approval-workflows/${id}`).then((r) => r.data),

  create: (body: ApprovalWorkflowInput) =>
    client.post<ApprovalWorkflow>('/api/approval-workflows', body).then((r) => r.data),

  update: (id: string, body: ApprovalWorkflowInput) =>
    client.put<ApprovalWorkflow>(`/api/approval-workflows/${id}`, body).then((r) => r.data),

  deactivate: (id: string) =>
    client.post<ApprovalWorkflow>(`/api/approval-workflows/${id}/deactivate`).then((r) => r.data),

  activate: (id: string) =>
    client.post<ApprovalWorkflow>(`/api/approval-workflows/${id}/activate`).then((r) => r.data),

  entities: () =>
    client.get<ApprovalEntityDescriptor[]>('/api/approval-workflows/entities').then((r) => r.data),

  preview: (entityName: string, employeeId?: number | null) =>
    client.get<ApprovalRoutePreview>('/api/approval-workflows/preview', { params: { entityName, employeeId: employeeId ?? undefined } }).then((r) => r.data),

  getSettings: () =>
    client.get<ApprovalGovernanceSettings>('/api/approval-workflows/settings').then((r) => r.data),

  saveSettings: (body: ApprovalGovernanceSettings) =>
    client.put<ApprovalGovernanceSettings>('/api/approval-workflows/settings', body).then((r) => r.data),
};
