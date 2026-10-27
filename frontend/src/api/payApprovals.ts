import client from './client';

/**
 * OD4: the scoped new-hire pay approval view. Everything here is deliberately narrow: the server returns only what the
 * decision needs (never an identity number, a date of birth or a full account number), and the approval binds to the
 * opaque fingerprint of the package that was shown.
 */
export interface PayApprovalActor { name: string; atUtc: string }

export interface PayApprovalListItem {
  employeeId: number;
  employeeCode: string;
  name: string;
  companyId: string | null;
  companyName: string;
  designation: string | null;
  currency: string | null;
  gross: number;
  ibanLast4: string | null;
  enteredBy: PayApprovalActor | null;
  lastChangedAtUtc: string | null;
  status: 'pending' | 'rejected';
}

export interface PayApprovalList { items: PayApprovalListItem[]; count: number; hasMore: boolean }

export interface PayApprovalDetail {
  employee: { id: number; code: string; name: string; companyId: string | null; companyName: string; designation: string | null };
  components: { basic: number; housing: number; transport: number; food: number; mobile: number; other: number; fixedDeduction: number; gross: number; currency: string | null };
  bank: { bankName: string; ibanLast4: string | null; accountLast4: string | null; accountHolderName: string };
  enteredBy: PayApprovalActor | null;
  lastChangedBy: PayApprovalActor | null;
  fingerprint: string;
  sharedIbanCount: number;
  lastRejection: { reason: string; byName: string; atUtc: string } | null;
}

export type PayApprovalDecision = 'approve' | 'reject';

export const payApprovalsApi = {
  list: () => client.get<PayApprovalList>('/api/pay-approvals').then(r => r.data),
  detail: (employeeId: number) => client.get<PayApprovalDetail>(`/api/pay-approvals/${employeeId}`).then(r => r.data),
  decide: (employeeId: number, body: { fingerprint: string; decision: PayApprovalDecision; reason: string }) =>
    client.post<{ decision: 'approved' | 'rejected' }>(`/api/pay-approvals/${employeeId}/decision`, body).then(r => r.data),
};
