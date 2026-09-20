import client from './client';
import type { PagedResult } from './organization';

// W2-B — expense claims. Amounts are always in the claim's `currency` (the employee's legal-entity
// payroll currency); there is deliberately no per-line currency.

export type ExpenseClaimStatus = 'Draft' | 'Submitted' | 'Approved' | 'Rejected' | 'Scheduled' | 'Paid' | 'Cancelled';

export const EXPENSE_STATUSES: ExpenseClaimStatus[] = ['Draft', 'Submitted', 'Approved', 'Rejected', 'Scheduled', 'Paid', 'Cancelled'];

export interface ExpenseClaimLine {
  id: string;
  lineNumber: number;
  expenseDate: string;
  categoryCode: string;
  categoryName: string;
  amount: number;
  description: string;
  hasReceipt: boolean;
  receiptFileName: string | null;
  receiptContentType: string | null;
  receiptSizeBytes: number | null;
}

export interface ExpenseApprovalProgress {
  status: string;
  currentStepOrder: number;
  currentApproverName: string;
  currentApproverRole: string;
  dueAtUtc: string | null;
  canDecide: boolean;
}

export interface ExpenseClaim {
  id: string;
  claimNumber: string;
  employeeId: number;
  employeeName: string;
  companyId: string | null;
  title: string;
  currency: string;
  totalAmount: number;
  status: ExpenseClaimStatus;
  createdAtUtc: string;
  submittedAtUtc: string | null;
  decidedAtUtc: string | null;
  rejectionReason: string | null;
  approvalRequestId: string | null;
  payrollRunId: string | null;
  payrollPeriod: string | null;
  scheduledAtUtc: string | null;
  paidAtUtc: string | null;
  lines: ExpenseClaimLine[];
  approval: ExpenseApprovalProgress | null;
}

export interface ExpenseCategory {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  isActive: boolean;
  maxAmountPerClaim: number | null;
  receiptRequiredAbove: number | null;
}

export interface EssExpenseConfig {
  currency: string | null;
  multiCurrencySupported: boolean;
  maxReceiptBytes: number;
  receiptContentTypes: string[];
  categories: ExpenseCategory[];
}

export interface ExpenseLineInput {
  id?: string | null;
  expenseDate: string;
  categoryCode: string;
  amount: number;
  description: string;
}

export interface SaveExpenseClaimInput {
  title?: string | null;
  lines: ExpenseLineInput[];
}

export interface ExpenseViolation {
  lineNumber: number | null;
  code: string;
  message: string;
}

export interface ExpenseClaimQuery {
  status?: string;
  employeeId?: number;
  categoryCode?: string;
  from?: string;
  to?: string;
  search?: string;
  payrollRunId?: string;
  page?: number;
  pageSize?: number;
}

export interface ExpensePayoutItem {
  claimId: string;
  claimNumber: string;
  employeeId: number;
  amount: number;
  outcome: 'Scheduled' | 'Skipped';
  reason: string | null;
}

export interface ExpensePayoutResult {
  payrollRunId: string;
  scheduled: number;
  skipped: number;
  items: ExpensePayoutItem[];
}

/** Pulls the server's `{ message, violations[] }` contract out of an axios error. */
export function expenseError(err: unknown, fallback: string): { message: string; violations: ExpenseViolation[] } {
  const e = err as { response?: { status?: number; data?: { message?: string; violations?: ExpenseViolation[] } } };
  if (e?.response?.status === 403) return { message: 'You do not have access to do this.', violations: [] };
  return { message: e?.response?.data?.message ?? fallback, violations: e?.response?.data?.violations ?? [] };
}

/** Employee self-service — always the caller's own claims. */
export const essExpensesApi = {
  config: () => client.get<EssExpenseConfig>('/api/ess/expenses/config').then((r) => r.data),
  list: (params: { status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<ExpenseClaim>>('/api/ess/expenses', { params }).then((r) => r.data),
  get: (id: string) => client.get<ExpenseClaim>(`/api/ess/expenses/${id}`).then((r) => r.data),
  create: (body: SaveExpenseClaimInput) => client.post<ExpenseClaim>('/api/ess/expenses', body).then((r) => r.data),
  update: (id: string, body: SaveExpenseClaimInput) => client.put<ExpenseClaim>(`/api/ess/expenses/${id}`, body).then((r) => r.data),
  uploadReceipt: (id: string, lineId: string, file: File) => {
    const form = new FormData();
    form.append('file', file);
    return client.post<ExpenseClaim>(`/api/ess/expenses/${id}/lines/${lineId}/receipt`, form).then((r) => r.data);
  },
  receiptUrl: (id: string, lineId: string) => `/api/ess/expenses/${id}/lines/${lineId}/receipt`,
  downloadReceipt: (id: string, lineId: string) =>
    client.get<Blob>(`/api/ess/expenses/${id}/lines/${lineId}/receipt`, { responseType: 'blob' }).then((r) => r.data),
  submit: (id: string) => client.post<ExpenseClaim>(`/api/ess/expenses/${id}/submit`).then((r) => r.data),
  cancel: (id: string) => client.post<ExpenseClaim>(`/api/ess/expenses/${id}/cancel`).then((r) => r.data),
};

/** Approvers, HR and finance. */
export const expensesApi = {
  list: (params: ExpenseClaimQuery = {}) =>
    client.get<PagedResult<ExpenseClaim>>('/api/expenses', { params }).then((r) => r.data),
  get: (id: string) => client.get<ExpenseClaim>(`/api/expenses/${id}`).then((r) => r.data),
  approvals: () => client.get<ExpenseClaim[]>('/api/expenses/approvals').then((r) => r.data),
  decide: (id: string, decision: 'Approve' | 'Reject', comments?: string) =>
    client.post<ExpenseClaim>(`/api/expenses/${id}/decision`, { decision, comments }).then((r) => r.data),
  schedule: (payrollRunId: string, claimIds?: string[]) =>
    client.post<ExpensePayoutResult>('/api/expenses/payroll/schedule', { payrollRunId, claimIds }).then((r) => r.data),
  unschedule: (id: string) => client.post<ExpenseClaim>(`/api/expenses/${id}/unschedule`).then((r) => r.data),
  downloadReceipt: (id: string, lineId: string) =>
    client.get<Blob>(`/api/expenses/${id}/lines/${lineId}/receipt`, { responseType: 'blob' }).then((r) => r.data),
  categories: (includeInactive = false) =>
    client.get<ExpenseCategory[]>('/api/expenses/categories', { params: { includeInactive } }).then((r) => r.data),
  updatePolicy: (code: string, body: { maxAmountPerClaim: number | null; receiptRequiredAbove: number | null }) =>
    client.put<ExpenseCategory>(`/api/expenses/categories/${encodeURIComponent(code)}/policy`, body).then((r) => r.data),
};

export function formatMoney(amount: number, currency: string | null | undefined) {
  const n = amount.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  return currency ? `${n} ${currency}` : n;
}

export function openBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.target = '_blank';
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}
