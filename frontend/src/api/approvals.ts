import client from './client';
import { requirePage } from '../lib/listResponse';
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
  /** Plain-language reason the caller cannot decide a pending request (e.g. they requested it). */
  decisionBlockedReason?: string | null;
  /** True when the caller raised this pending employee change and may take it back. */
  canWithdraw?: boolean;
  /** What is being approved, e.g. "IBAN, passport". */
  changeSummary?: string | null;
}

/** One field of a pending employee change: on file now → proposed. Masked values arrive already masked. */
export interface EmployeeChangeField {
  key: string;
  label: string;
  currentValue: string | null;
  proposedValue: string | null;
  masked: boolean;
}

/** Before → after for an employee-change approval; only returned to someone who may decide it now. */
export interface EmployeeChangeDetail {
  approvalRequestId: string;
  changeRequestId: string;
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  effectiveDate: string;
  changeStatus: string;
  fields: EmployeeChangeField[];
}

export interface ApprovalDecision {
  id: string;
  stepOrder: number;
  decision: string;
  comments: string;
  decidedAtUtc: string;
}

export const approvalsApi = {
  list: (params: { status?: string; entityName?: string; queue?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<ApprovalRequest>>('/api/approval-requests', { params }).then((r) => requirePage<PagedResult<ApprovalRequest>>(r.data, 'approval requests')),

  get: (id: string) =>
    client.get<ApprovalRequest>(`/api/approval-requests/${id}`).then((r) => r.data),

  decide: (id: string, decision: 'Approve' | 'Reject', comments = '') =>
    client.post<ApprovalRequest>(`/api/approval-requests/${id}/decisions`, { decision, comments }).then((r) => r.data),

  changeDetail: (id: string) =>
    client.get<EmployeeChangeDetail>(`/api/approval-requests/${id}/change-detail`).then((r) => r.data),

  withdraw: (id: string, reason = '') =>
    client.post<ApprovalRequest>(`/api/approval-requests/${id}/withdraw`, { reason }).then((r) => r.data),
};
