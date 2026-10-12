// ============================================================
// My loans — /api/finance/loans (lane C)
// ============================================================
//
// loans.self callers see only their own loans (the server forces ?mine=true). Shapes follow
// EmployeeLoanDto (Application/Finance/FinanceDtos.cs), LoanInstallment, ListOfferedLoanTypes and
// GetLoanEligibility in Controllers/Finance/LoansController*.cs. Nothing here decides eligibility.

import { apiGet, apiPost } from './client';
import type {
  Loan,
  LoanEligibility,
  LoanInstallment,
  LoanPolicySummary,
  OfferedLoanType,
} from '@/features/loans/loanRules';

function num(value: unknown): number {
  const n = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(n) ? n : 0;
}

function numOrNull(value: unknown): number | null {
  if (value === null || value === undefined || value === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

function str(value: unknown): string {
  return typeof value === 'string' ? value : value === null || value === undefined ? '' : String(value);
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.map(str) : [];
}

function mapLoan(raw: any): Loan {
  return {
    id: str(raw?.id),
    loanTypeId: str(raw?.loanTypeId),
    loanTypeName: str(raw?.loanTypeName),
    loanNumber: str(raw?.loanNumber),
    requestedAmount: num(raw?.requestedAmount),
    approvedAmount: num(raw?.approvedAmount),
    requestedInstallments: num(raw?.requestedInstallments),
    approvedInstallments: num(raw?.approvedInstallments),
    installmentAmount: num(raw?.installmentAmount),
    totalRepaid: num(raw?.totalRepaid),
    outstandingBalance: num(raw?.outstandingBalance),
    status: str(raw?.status),
    rejectionReason: raw?.rejectionReason ? str(raw.rejectionReason) : null,
    repaymentMethod: str(raw?.repaymentMethod),
    currency: raw?.currency ? str(raw.currency) : null,
    createdAtUtc: str(raw?.createdAtUtc),
  };
}

function mapInstallment(raw: any): LoanInstallment {
  return {
    id: str(raw?.id),
    installmentNumber: num(raw?.installmentNumber),
    dueDate: str(raw?.dueDate),
    amountDue: num(raw?.amountDue),
    amountPaid: num(raw?.amountPaid),
    status: str(raw?.status),
    paidDate: raw?.paidDate ? str(raw.paidDate) : null,
  };
}

export interface LoanApplication {
  loanTypeId: string;
  amount: number;
  installments: number;
  repaymentMethod: string;
  notes: string;
}

export const loansApi = {
  async getMyLoans(): Promise<Loan[]> {
    const page = await apiGet<{ total: number; items: any[] }>('/finance/loans', { params: { mine: true, pageSize: 50 } });
    return (page?.items ?? []).map(mapLoan);
  },

  async getInstallments(loanId: string): Promise<LoanInstallment[]> {
    const rows = await apiGet<any[]>(`/finance/loans/${encodeURIComponent(loanId)}/installments`);
    return (rows ?? []).map(mapInstallment).sort((a, b) => a.installmentNumber - b.installmentNumber);
  },

  async getOfferedTypes(): Promise<OfferedLoanType[]> {
    const rows = await apiGet<any[]>('/finance/loans/types/offered');
    return (rows ?? []).map((r) => ({
      loanTypeId: str(r?.loanTypeId),
      code: str(r?.code),
      nameEn: str(r?.nameEn),
      nameAr: str(r?.nameAr),
      gradeLimited: Boolean(r?.gradeLimited),
      offered: Boolean(r?.offered),
      reasonCode: r?.reasonCode ? str(r.reasonCode) : null,
      reasonText: r?.reasonText ? str(r.reasonText) : null,
    }));
  },

  /** Instalment ceiling per loan type (LoanType.MaxInstallments). */
  async getTypeLimits(): Promise<Map<string, number>> {
    const rows = await apiGet<any[]>('/finance/loans/types');
    return new Map((rows ?? []).map((r) => [str(r?.id), num(r?.maxInstallments)] as const));
  },

  async getPolicies(): Promise<LoanPolicySummary[]> {
    const rows = await apiGet<any[]>('/finance/loans/policies');
    return (rows ?? []).map((r) => ({
      id: str(r?.id),
      loanTypeId: str(r?.loanTypeId),
      maxInstallments: num(r?.maxInstallments),
      allowedRepaymentMethods: strings(r?.allowedRepaymentMethods),
    }));
  },

  /** No amount = preview (the limits for this type). With amount + instalments = the real verdict. */
  async checkEligibility(args: {
    loanTypeId: string;
    repaymentMethod: string;
    amount?: number;
    installments?: number;
  }): Promise<LoanEligibility> {
    const raw = await apiGet<any>('/finance/loans/eligibility', {
      params: {
        loanTypeId: args.loanTypeId,
        repaymentMethod: args.repaymentMethod,
        amount: args.amount,
        installments: args.installments,
      },
    });
    return {
      eligible: Boolean(raw?.eligible),
      reasons: strings(raw?.reasons),
      codes: strings(raw?.codes),
      maxAvailableAmount: numOrNull(raw?.maxAvailableAmount),
      available: numOrNull(raw?.available),
      currency: raw?.currency ? str(raw.currency) : null,
      policyId: raw?.policyId ? str(raw.policyId) : null,
      preview: Boolean(raw?.preview),
    };
  },

  /** The server resolves the employee from the session (EmployeeId empty + no EmployeeIntId). */
  async apply(application: LoanApplication): Promise<Loan> {
    const raw = await apiPost<any>('/finance/loans', {
      employeeId: '00000000-0000-0000-0000-000000000000',
      employeeName: '',
      loanTypeId: application.loanTypeId,
      requestedAmount: application.amount,
      requestedInstallments: application.installments,
      notes: application.notes.trim() || null,
      repaymentMethod: application.repaymentMethod,
    });
    return mapLoan(raw);
  },

  /** Whether the company has the Loans module on (GET /features/disabled-keys lists switched-off modules). */
  async moduleEnabled(): Promise<boolean> {
    const disabled = await apiGet<string[]>('/features/disabled-keys');
    return !(disabled ?? []).map((k) => String(k).toLowerCase()).includes('finance');
  },
};
