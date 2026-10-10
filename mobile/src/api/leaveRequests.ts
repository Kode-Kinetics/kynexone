// ============================================================
// Leave — request form data and "My requests" (lane A2).
//
// Shapes checked against backend-dotnet/Zayra.Api/Controllers/Leave/LeaveRequestsController.cs,
// LeaveTypesController.cs and LeaveBalancesController.cs, and against the local demo API.
// There is NO server-side day-count preview: the server counts working days at submit time and
// returns them on the created request (totalDays). The app shows that number after sending.
// ============================================================

import { apiGet, apiPost } from './client';
import { userStorage } from '@/storage';
import { mapLeaveBalanceRow, type LeaveBalanceRow } from './leaveBalanceMapper';
import { leaveStatusKind, nextApproverFrom, type ApproverKind, type LeaveStatusKind } from '@/features/leave/leaveLogic';

export type { LeaveBalanceRow } from './leaveBalanceMapper';
export { mapLeaveBalanceRow, simpleBalanceEquation } from './leaveBalanceMapper';

type Paged<T> = { items?: T[]; data?: T[]; total?: number };

function itemsOf<T>(result: Paged<T> | T[] | null | undefined): T[] {
  if (!result) return [];
  if (Array.isArray(result)) return result;
  return result.items ?? result.data ?? [];
}

async function ownEmployeeId(): Promise<number> {
  const user = await userStorage.getUser();
  const id = Number(user?.employeeId);
  if (!Number.isFinite(id) || id <= 0) {
    throw Object.assign(new Error('employee_not_linked'), { response: { status: 403, data: { code: 'ess_employee_not_linked' } } });
  }
  return id;
}

export interface LeaveTypeOption {
  id: string;
  nameEn: string;
  nameAr?: string;
  isHalfDayAllowed: boolean;
  requiresReason: boolean;
  requiresAttachment: boolean;
}

export interface MyLeaveRequest {
  id: string;
  leaveTypeId: string;
  leaveTypeName: string;
  startDate: string;
  endDate: string;
  /** Days the server counted (working days; 0.5 for a half day). */
  totalDays: number;
  isHalfDay: boolean;
  reason: string;
  rawStatus: string;
  kind: LeaveStatusKind;
  submittedAt: string;
  decidedAt: string | null;
  rejectionReason: string;
  cancellationReason: string;
}

export interface NextApprover {
  kind: ApproverKind;
  name?: string;
}

function mapRequest(raw: any): MyLeaveRequest {
  const rawStatus = String(raw.status ?? '');
  return {
    id: String(raw.id ?? ''),
    leaveTypeId: String(raw.leaveTypeId ?? ''),
    leaveTypeName: String(raw.leaveTypeName ?? ''),
    startDate: String(raw.startDate ?? ''),
    endDate: String(raw.endDate ?? ''),
    totalDays: Number(raw.totalDays ?? 0),
    isHalfDay: String(raw.dayType ?? '').toLowerCase().startsWith('half'),
    reason: String(raw.reason ?? ''),
    rawStatus,
    kind: leaveStatusKind(rawStatus),
    submittedAt: String(raw.submittedAtUtc ?? raw.createdAtUtc ?? ''),
    decidedAt: raw.decidedAtUtc ?? null,
    rejectionReason: String(raw.rejectionReason ?? ''),
    cancellationReason: String(raw.cancellationReason ?? ''),
  };
}

export const leaveRequestsApi = {
  /** GET /leave/types — active types with the rules the form must respect. */
  async getTypes(): Promise<LeaveTypeOption[]> {
    const types = await apiGet<any[]>('/leave/types');
    return (types ?? [])
      .filter((t) => t.isActive !== false)
      .map((t) => ({
        id: String(t.id),
        nameEn: String(t.nameEn ?? t.name ?? ''),
        nameAr: t.nameAr || undefined,
        isHalfDayAllowed: Boolean(t.isHalfDayAllowed),
        requiresReason: Boolean(t.requiresReason),
        requiresAttachment: Boolean(t.requiresAttachment),
      }));
  },

  /**
   * GET /leave/balances/employee/{self} — only the types that carry a balance appear. Every figure is
   * the server's (see leaveBalanceMapper.ts for the definitions). Lane A1's Home tile uses this too.
   */
  async getBalances(): Promise<LeaveBalanceRow[]> {
    const employeeId = await ownEmployeeId();
    const rows = await apiGet<any[]>(`/leave/balances/employee/${employeeId}`);
    return (rows ?? []).map(mapLeaveBalanceRow);
  },

  /** GET /leave/requests?employeeId={self} — every status, newest first. */
  async getMine(): Promise<MyLeaveRequest[]> {
    const employeeId = await ownEmployeeId();
    const result = await apiGet<Paged<any>>(`/leave/requests?employeeId=${employeeId}&page=1&pageSize=100`);
    return itemsOf(result).map(mapRequest);
  },

  /** GET /leave/requests/{id} — the approval steps, to say who the request is waiting for. */
  async getNextApprover(id: string): Promise<NextApprover | null> {
    const detail = await apiGet<any>(`/leave/requests/${id}`);
    return nextApproverFrom(Array.isArray(detail?.approvals) ? detail.approvals : []);
  },

  /** POST /leave/requests/{id}/withdraw — a request not yet approved; releases the days held for it. */
  async withdraw(id: string, reason?: string): Promise<MyLeaveRequest> {
    const result = await apiPost<any>(`/leave/requests/${id}/withdraw`, { reason: reason?.trim() || undefined });
    return mapRequest(result);
  },

  /** POST /leave/requests/{id}/cancel — on approved leave this opens a cancellation the approver decides. */
  async requestCancellation(id: string, reason?: string): Promise<void> {
    await apiPost(`/leave/requests/${id}/cancel`, { reason: reason?.trim() || undefined });
  },

  /** POST /leave/requests. Returns the created request with the server's own day count. */
  async submit(payload: {
    leaveTypeId: string;
    startDate: string;
    endDate: string;
    isHalfDay: boolean;
    reason?: string;
    attachmentDocumentId?: string;
  }): Promise<MyLeaveRequest> {
    const employeeId = await ownEmployeeId();
    // A half day is one date — enforced here too, so no caller can send a half day over a range.
    const endDate = payload.isHalfDay ? payload.startDate : payload.endDate;
    const result = await apiPost<any>('/leave/requests', {
      employeeId,
      leaveTypeId: payload.leaveTypeId,
      startDate: payload.startDate,
      endDate,
      dayType: payload.isHalfDay ? 'Half' : 'Full',
      reason: payload.reason?.trim() || undefined,
      isEmergency: false,
      attachmentDocumentId: payload.attachmentDocumentId || undefined,
    });
    return mapRequest(result);
  },
};
