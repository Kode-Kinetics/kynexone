import client from './client';

export type JawazatRoute = 'EmployerAssisted' | 'WorkerSelfServiceNotification';
export interface JawazatPolicy {
  schemaVersion: 1;
  employerAssistedEnabled: boolean;
  workerNotificationEnabled: boolean;
  maximumTripDays: number;
  minimumPassportValidityDays: number;
  ruleVersion: string;
  reviewedBy: string;
  reviewedAtUtc: string | null;
}
export interface JawazatPolicyResponse {
  profileId: string; companyId: string; effectiveFrom: string; effectiveTo: string | null;
  policy: JawazatPolicy; canCreateEmployerAssisted: boolean; canRecordWorkerNotification: boolean;
}
export interface JawazatPolicySnapshot {
  profileId: string; companyId: string; effectiveFrom: string; effectiveTo: string | null;
  capturedAtUtc: string; policy: JawazatPolicy;
}
export interface JawazatCheck {
  code: string; result: 'Passed' | 'Failed' | 'Unknown'; evidenceSource: string; evidenceAtUtc: string | null; reason: string;
}
export interface JawazatEvaluation {
  policySnapshot: JawazatPolicySnapshot; checks: JawazatCheck[]; canSubmitToGovernment: boolean;
}
export interface JawazatCapabilities {
  provider: string; isAvailable: boolean; supportedOperations: string[]; message: string;
}
export interface JawazatCreateInput {
  employeeId?: number; route: JawazatRoute; service: 'ExitReentryIssue';
  departureDate: string; returnDate: string; reason: string; idempotencyKey: string;
}
export interface JawazatRequest {
  id: string; employeeId: number; companyId: string; subject: string; status: string;
  approvalRequestId: string | null; workflowVersion: number; createdAtUtc: string;
  data: JawazatCreateInput & {
    schemaVersion: number; internalState: 'PendingApproval' | 'Approved' | 'Rejected' | 'NotificationRecorded';
    providerState: 'NotSubmitted' | 'ProviderUnavailable'; policySnapshot: JawazatPolicySnapshot;
    checks: JawazatCheck[]; lastProviderAttemptAtUtc?: string | null; providerMessage?: string | null; decisionNote?: string | null;
  };
}

const base = '/api/compliance/jawazat';
export const jawazatApi = {
  capabilities: (signal?: AbortSignal) => client.get<JawazatCapabilities>(`${base}/capabilities`, { signal }).then(r => r.data),
  policy: (employeeId?: number, signal?: AbortSignal) => client.get<JawazatPolicyResponse>(`${base}/policy`, { params: { employeeId }, signal }).then(r => r.data),
  evaluate: (body: JawazatCreateInput) => client.post<JawazatEvaluation>(`${base}/evaluate`, body).then(r => r.data),
  create: (body: JawazatCreateInput) => client.post<JawazatRequest>(`${base}/requests`, body).then(r => r.data),
  list: (signal?: AbortSignal) => client.get<JawazatRequest[]>(`${base}/requests`, { signal }).then(r => r.data),
  get: (id: string, signal?: AbortSignal) => client.get<JawazatRequest>(`${base}/requests/${id}`, { signal }).then(r => r.data),
  submit: (id: string) => client.post<JawazatRequest>(`${base}/requests/${id}/submit`).then(r => r.data),
};

export function jawazatError(error: unknown): string {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (typeof data === 'string') return data;
  if (data && typeof data === 'object') {
    const problem = data as { message?: string; detail?: string; title?: string };
    return problem.message || problem.detail || problem.title || 'The request could not be completed. Please retry.';
  }
  return 'The request could not be completed. Please retry.';
}
