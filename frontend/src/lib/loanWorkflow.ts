import type { LoanApproval, LoanRepaymentMethod } from '../api/loans';

export const repaymentMethodLabels: Record<LoanRepaymentMethod, string> = {
  BankTransfer: 'Bank transfer', DirectDebit: 'Direct debit', Cash: 'Cash receipt', PayrollDeduction: 'Payroll deduction',
};

export function loanErrorMessage(error: unknown, fallback: string): string {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  if (data && typeof data === 'object') {
    const body = data as Record<string, unknown>;
    if (typeof body.detail === 'string') return body.detail;
    if (typeof body.message === 'string') return body.message;
    if (body.errors && typeof body.errors === 'object') {
      const messages = Object.values(body.errors).flat().filter((item): item is string => typeof item === 'string');
      if (messages.length) return messages.join(' ');
    }
    if (typeof body.title === 'string') return body.title;
  }
  return fallback;
}

export function canDecideLoan(approval: LoanApproval, approvals: LoanApproval[], user: { id: string; roles: string[] } | null): boolean {
  if (!user || approval.status !== 'Pending') return false;
  if (approvals.some(step => step.stepOrder < approval.stepOrder && step.status !== 'Approved')) return false;
  if (user.roles.includes('Admin')) return true;
  if (!user.roles.some(role => ['HR Manager', 'HR Director'].includes(role))) return false;
  return user.roles.includes(loanApprovalRole(approval.approverRole));
}

export function loanApprovalRole(role: string): string {
  return ['Finance', 'Finance Approver', 'Manager'].includes(role) ? 'HR Manager' : role;
}

export function localDateToday(): string {
  const today = new Date();
  return `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`;
}
