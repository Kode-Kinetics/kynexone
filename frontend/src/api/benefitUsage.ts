import client from './client';

// Benefit utilisation, the way loans work: a request (Pending) that Finance approves or rejects, and the requester may withdraw.
// HR and Finance: /api/entitlements/usage (read with entitlements.read or payroll.approve; HR requests with entitlements.manage;
// Finance decides with payroll.approve). Employee: /api/ess/package/usage (own benefits and requests; requests with ess.write).
// Dates are ISO yyyy-MM-dd strings. No money moves from any of it.

export type UsageStatus = 'Pending' | 'Approved' | 'Rejected' | 'Cancelled';
export type UsageMeasure = 'Units' | 'Amount';

export interface UsageBeneficiary {
  /** null = the employee. */
  dependantId: string | null;
  name: string;
  cap: number;
  used: number;
  remaining: number;
  /** Already has a request this period (counts toward the dependant limit). */
  named: boolean;
}

export interface UtilisationRow {
  employeeId: number;
  employeeName: string;
  employeeCode: string | null;
  companyId: string;
  benefitCode: string;
  labelEn: string;
  labelAr: string;
  verificationState: 'Verified' | 'Unverified' | string;
  measure: UsageMeasure;
  currency: string | null;
  entitlement: number;
  pending: number;
  approved: number;
  remaining: number;
  periodStart: string;
  periodEnd: string;
  dependantScope: string;
  maxDependants: number | null;
  perBeneficiary: number | null;
  beneficiaries: UsageBeneficiary[];
  canRequest: boolean;
}

export interface UsageRequest {
  id: string;
  employeeId: number;
  employeeName: string;
  companyId: string;
  benefitCode: string;
  labelEn: string;
  labelAr: string;
  status: UsageStatus;
  periodStart: string;
  periodEnd: string;
  usageDate: string;
  units: number | null;
  amount: number | null;
  currency: string | null;
  dependantId: string | null;
  dependantName: string | null;
  academicYear: string | null;
  providerName: string | null;
  evidenceReference: string | null;
  note: string | null;
  requestedByUserId: string;
  requestedByName: string | null;
  createdAtUtc: string;
  decidedByName: string | null;
  decidedAtUtc: string | null;
  decisionReason: string | null;
  canDecide: boolean;
  canCancel: boolean;
}

export interface UsageRequestInput {
  requestId: string;
  employeeId: number;
  benefitCode: string;
  usageDate: string;
  units: number | null;
  amount: number | null;
  evidenceReference: string;
  dependantId?: string | null;
  academicYear?: string | null;
  providerName?: string | null;
  note?: string | null;
  currency?: string | null;
}

export interface EssUsageRequest {
  id: string; benefitCode: string; labelEn: string; labelAr: string; status: UsageStatus; usageDate: string; units: number | null;
  amount: number | null; currency: string | null; forName: string | null; academicYear: string | null; providerName: string | null;
  decisionReason: string | null; createdAtUtc: string; canCancel: boolean;
}
export interface EssUsage { benefits: UtilisationRow[]; requests: EssUsageRequest[] }

export const benefitUsageApi = {
  utilisation: (params: { employeeId?: number; asOf?: string } = {}) =>
    client.get<UtilisationRow[]>('/api/entitlements/usage', { params }).then(r => r.data),
  requests: (params: { employeeId?: number; status?: UsageStatus } = {}) =>
    client.get<UsageRequest[]>('/api/entitlements/usage/requests', { params }).then(r => r.data),
  request: (input: UsageRequestInput) => client.post<UsageRequest>('/api/entitlements/usage/requests', input).then(r => r.data),
  decide: (id: string, decision: 'Approved' | 'Rejected', reason?: string) =>
    client.post<UsageRequest>(`/api/entitlements/usage/requests/${id}/decision`, { decision, reason: reason || null }).then(r => r.data),
  cancel: (id: string, reason: string) => client.post<UsageRequest>(`/api/entitlements/usage/requests/${id}/cancel`, { reason }).then(r => r.data),
  mine: () => client.get<EssUsage>('/api/ess/package/usage').then(r => r.data),
  requestMine: (input: UsageRequestInput) => client.post<EssUsageRequest>('/api/ess/package/usage', input).then(r => r.data),
  cancelMine: (id: string, reason: string) => client.post<EssUsageRequest>(`/api/ess/package/usage/${id}/cancel`, { reason }).then(r => r.data),
};
