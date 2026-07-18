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
    client.get<PagedResult<ApprovalRequest>>('/api/approval-requests', { params }).then((r) => r.data),

  get: (id: string) =>
    client.get<ApprovalRequest>(`/api/approval-requests/${id}`).then((r) => r.data),

  decide: (id: string, decision: 'Approve' | 'Reject', comments = '') =>
    client.post<ApprovalRequest>(`/api/approval-requests/${id}/decisions`, { decision, comments }).then((r) => r.data),
};
