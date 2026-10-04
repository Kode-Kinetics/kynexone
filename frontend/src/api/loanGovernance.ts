import client from './client';
import type { LoanRepaymentMethod } from './loans';

export interface LoanPolicyInput {
  companyId: string; loanTypeId: string; policyName: string;
  maxAmount: number; maxTotalOutstanding: number; maxMultiplierOfSalary: number;
  maxInstallmentPercentOfSalary: number; minServiceMonths: number; maxInstallments: number;
  requireProbationCompleted: boolean; blockDuringNotice: boolean; blockOnOverdue: boolean;
  allowedEmploymentStatuses: string[]; allowedContractTypes: string[]; allowedRepaymentMethods: string[];
  allowedRepaymentFrequencies: string[];
  maxConcurrentLoans: number; cooldownMonthsAfterRepayment: number;
  additionalApprovalThreshold: number; additionalApproverRole: string;
  allowExceptions: boolean; allowEarlySettlement: boolean; allowRescheduling: boolean; isActive: boolean;
}
export interface LoanPolicy extends LoanPolicyInput { id: string; version: number; createdAtUtc: string; }
export interface LoanEligibility { eligible: boolean; reasons: string[]; codes?: string[]; canRequestException?: boolean; maxAvailableAmount: number | null; policyId?: string; policyVersion?: number; monthlySalary: number | null; committedAmount: number; }
export interface LoanChangeRequest {
  id: string; loanId: string; changeType: 'Reschedule' | 'PolicyException' | 'CollectionMethod'; status: string;
  reason: string; requestedInstallments?: number; requestedStartDate?: string; exceptionCodes?: string[];
  createdBy?: string; createdAtUtc: string; decisionReason?: string; decidedAtUtc?: string;
  requestedRepaymentMethod?: string; reference?: string;
}
export interface LoanCorrectionRequest {
  id: string; changeType: 'ReceiptReversal' | 'DisbursementReversal'; status: string;
  repaymentId?: string; effectiveDate: string; reference: string; reason: string; createdBy?: string;
  createdAtUtc: string; decisionReason?: string;
}
export const loanGovernanceApi = {
  policies: (params: { companyId?: string; loanTypeId?: string } = {}) => client.get<LoanPolicy[]>('/api/finance/loans/policies', { params }).then(r => r.data),
  createPolicy: (body: LoanPolicyInput) => client.post<LoanPolicy>('/api/finance/loans/policies', body).then(r => r.data),
  eligibility: (params: { employeeIntId?: number; loanTypeId: string; amount: number; installments: number; repaymentMethod: LoanRepaymentMethod }) => client.get<LoanEligibility>('/api/finance/loans/eligibility', { params }).then(r => r.data),
  refreshLifecycle: (id: string) => client.post(`/api/finance/loans/${id}/lifecycle/refresh`).then(r => r.data),
  reviewLifecycle: (id: string, body: { decision: 'Continue' | 'Hold' | 'Cancel'; reason: string }) => client.patch(`/api/finance/loans/${id}/lifecycle/review`, body).then(r => r.data),
  changes: (id: string) => client.get<LoanChangeRequest[]>(`/api/finance/loans/${id}/changes`).then(r => r.data),
  requestChange: (id: string, body: { changeType: 'Reschedule' | 'PolicyException' | 'CollectionMethod'; reason: string; installments?: number; startDate?: string; exceptionCodes?: string[]; repaymentMethod?: string; reconciliationReference?: string; confirmNoPayrollCollection?: boolean }) => client.post<LoanChangeRequest>(`/api/finance/loans/${id}/changes`, body).then(r => r.data),
  decideChange: (id: string, changeId: string, body: { decision: 'Approved' | 'Rejected'; reason: string }) => client.patch(`/api/finance/loans/${id}/changes/${changeId}/decide`, body).then(r => r.data),
  corrections: (id: string) => client.get<LoanCorrectionRequest[]>(`/api/finance/loans/${id}/corrections`).then(r => r.data),
  requestCorrection: (id: string, body: { changeType: 'ReceiptReversal' | 'DisbursementReversal'; repaymentId?: string; effectiveDate: string; reference: string; reason: string }) => client.post<LoanCorrectionRequest>(`/api/finance/loans/${id}/corrections`, body).then(r => r.data),
  decideCorrection: (id: string, changeId: string, body: { decision: 'Approved' | 'Rejected'; reason: string }) => client.patch(`/api/finance/loans/${id}/corrections/${changeId}/decide`, body).then(r => r.data),
};
