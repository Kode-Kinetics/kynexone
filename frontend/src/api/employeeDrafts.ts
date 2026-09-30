import client from './client';
import type { EmployeeDetail } from './employees';

/**
 * New-hire drafts: the step between an accepted offer (or a manually prepared hire) and an
 * active employee. Contract mirrors backend Application/Employees/EmployeeDraftLifecycle.cs.
 */

export type EmployeeDraftStatus =
  | 'Draft'
  | 'Submitted'
  | 'PendingHrApproval'
  | 'Activated'
  | 'Rejected'
  | 'Cancelled';

/** `awaiting` = Submitted or PendingHrApproval (the default), `open` = not yet decided, `all`, or one exact status. */
export type EmployeeDraftFilter = 'awaiting' | 'open' | 'all' | EmployeeDraftStatus;

export interface EmployeeDraftListItem {
  id: string;
  status: EmployeeDraftStatus | string;
  currentStep: string;
  name: string;
  arabicName: string;
  department: string;
  designation: string;
  branch: string;
  joiningDate: string | null;
  source: 'Recruitment' | 'Manual' | string;
  applicationId: string | null;
  jobTitle: string | null;
  createdByUserId: string | null;
  createdByName: string | null;
  isMine: boolean;
  canApprove: boolean;
  approveBlockedReason: string | null;
  profileCompletenessScore: number;
  createdAtUtc: string;
  submittedAtUtc: string | null;
  decidedAtUtc: string | null;
  decidedByName: string | null;
  decisionReason: string | null;
  activatedEmployeeId: number | null;
  activatedEmployeeCode: string | null;
}

export interface EmployeeDraftStatusCounts {
  awaitingApproval: number;
  draft: number;
  activated: number;
  rejected: number;
  cancelled: number;
}

export interface EmployeeDraftListResponse {
  items: EmployeeDraftListItem[];
  total: number;
  page: number;
  pageSize: number;
  counts: EmployeeDraftStatusCounts;
}

export interface EmployeeDraftActivationProblem {
  key: string;
  label: string;
  reason: string;
  fix: string;
}

export interface EmployeeDraftActivationCheck {
  canActivate: boolean;
  resolvedCompanyName: string | null;
  problems: EmployeeDraftActivationProblem[];
  advisories: string[];
}

/** The draft as HR entered it. Sensitive values arrive empty/null when the caller may not see them. */
export interface EmployeeDraftDetail {
  id: string;
  status: string;
  englishName: string;
  arabicName: string;
  personalEmail: string;
  workEmail: string;
  phone: string;
  nationality: string;
  countryCode: string;
  department: string;
  designation: string;
  branch: string;
  workLocation: string;
  joiningDate: string | null;
  contractType: string;
  probationEndDate: string | null;
  salary: number | null;
}

export interface EmployeeDraftReview {
  summary: EmployeeDraftListItem;
  draft: EmployeeDraftDetail;
  documentCount: number;
  /** Present while the draft is still open. */
  activationCheck: EmployeeDraftActivationCheck | null;
}

export const employeeDraftsApi = {
  list: (params: { status?: EmployeeDraftFilter; search?: string; page?: number; pageSize?: number } = {}) =>
    client.get<EmployeeDraftListResponse>('/api/employees/drafts', { params }).then((r) => r.data),

  get: (id: string) =>
    client.get<EmployeeDraftReview>(`/api/employees/drafts/${id}`).then((r) => r.data),

  submit: (id: string) =>
    client.post<void>(`/api/employees/drafts/${id}/submit`).then(() => undefined),

  approve: (id: string) =>
    client.post<EmployeeDetail>(`/api/employees/drafts/${id}/approve`).then((r) => r.data),

  reject: (id: string, reason: string) =>
    client.post<void>(`/api/employees/drafts/${id}/reject`, { reason }).then(() => undefined),

  cancel: (id: string, reason?: string) =>
    client.post<void>(`/api/employees/drafts/${id}/cancel`, { reason: reason ?? null }).then(() => undefined),

  /** Changes only the placement fields sent; every other draft field is left as it is. */
  updatePlacement: (id: string, placement: { department?: string; designation?: string; branch?: string }) =>
    client.put<EmployeeDraftDetail>(`/api/employees/drafts/${id}`, placement).then((r) => r.data),
};
