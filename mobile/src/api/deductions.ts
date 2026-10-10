// ============================================================
// "My deductions / خصوماتي" (Release A slice R3) — lane A2.
//
// Shapes: backend-dotnet/Zayra.Api/Controllers/Ess/EssDeductionsController.cs and the DTOs in
// Controllers/Payroll/DeductionStatementsController.cs (DeductionStatementDto, DeductionLineDto,
// DebtBalanceDto). Both routes sit behind the tenant's release_a flag: 403 feature_not_enabled
// (or 404) means "not available here" and the screen falls back to the payslip's own lines.
// ============================================================

import { apiGet } from './client';
import { errorShape } from '@/features/leave/leavePayText';

export interface DeductionReason {
  code: string;
  titleEn: string;
  titleAr: string;
  whyEn: string;
  whyAr: string;
  fixEn: string;
  fixAr: string;
}

export interface DeductionLine {
  componentCode: string;
  label: string;
  category: string;
  amount: number;
  countsTowardCap: boolean;
  loanNumber: string | null;
  loanType: string | null;
  loanTypeAr: string | null;
  balanceAfter: number | null;
  instalmentsRemaining: number | null;
  instalmentsTotal: number | null;
}

export interface DeductionStatement {
  slipId: string;
  year: number;
  month: number;
  currency: string;
  slipDeductionTotal: number;
  /** The lines add up to the payslip's deductions total. */
  reconciles: boolean;
  lines: DeductionLine[];
  reasons: DeductionReason[];
}

export interface DebtBalance {
  id: string;
  loanNo: string;
  type: string;
  typeAr: string | null;
  currency: string | null;
  outstanding: number;
  instalment: number;
  remaining: number | null;
  nextDueOn: string | null;
  deductedFromPay: boolean;
}

export interface MyDeductions {
  statements: DeductionStatement[];
  loans: DebtBalance[];
  advances: DebtBalance[];
}

/** The line the statement adds for OTHER runs in the same month; not part of this payslip's own deductions. */
export const OTHER_RUNS_CODE = 'OTHER_RUNS_THIS_PERIOD';

export type Availability<T> = { available: true; data: T } | { available: false };

const num = (v: unknown): number => (Number.isFinite(Number(v)) ? Number(v) : 0);
const numOrNull = (v: unknown): number | null => (v === null || v === undefined || !Number.isFinite(Number(v)) ? null : Number(v));

function mapReason(r: any): DeductionReason {
  return {
    code: String(r?.code ?? ''),
    titleEn: String(r?.titleEn ?? ''),
    titleAr: String(r?.titleAr ?? ''),
    whyEn: String(r?.whyEn ?? ''),
    whyAr: String(r?.whyAr ?? ''),
    fixEn: String(r?.fixEn ?? ''),
    fixAr: String(r?.fixAr ?? ''),
  };
}

function mapStatement(d: any): DeductionStatement {
  return {
    slipId: String(d?.slipId ?? ''),
    year: num(d?.year),
    month: num(d?.month),
    currency: String(d?.currency ?? ''),
    slipDeductionTotal: num(d?.slipDeductionTotal),
    reconciles: d?.reconciles !== false,
    lines: (Array.isArray(d?.lines) ? d.lines : []).map((l: any) => ({
      componentCode: String(l?.componentCode ?? ''),
      label: String(l?.label ?? ''),
      category: String(l?.category ?? 'Other'),
      amount: num(l?.amount),
      countsTowardCap: Boolean(l?.countsTowardCap),
      loanNumber: l?.loanNumber ?? null,
      loanType: l?.loanType ?? null,
      loanTypeAr: l?.loanTypeAr ?? null,
      balanceAfter: numOrNull(l?.balanceAfter),
      instalmentsRemaining: numOrNull(l?.instalmentsRemaining),
      instalmentsTotal: numOrNull(l?.instalmentsTotal),
    })),
    reasons: (Array.isArray(d?.reasons) ? d.reasons : []).map(mapReason),
  };
}

function mapBalance(b: any): DebtBalance {
  return {
    id: String(b?.id ?? ''),
    loanNo: String(b?.loanNo ?? ''),
    type: String(b?.type ?? ''),
    typeAr: b?.typeAr ?? null,
    currency: b?.currency ?? null,
    outstanding: num(b?.outstanding),
    instalment: num(b?.instalment),
    remaining: numOrNull(b?.remaining),
    nextDueOn: b?.nextDueOn ?? null,
    deductedFromPay: Boolean(b?.deductedFromPay),
  };
}

/** 403 (flag off / not linked) and 404 mean "not offered here"; anything else is a real failure. */
function unavailable(error: unknown): boolean {
  const e = errorShape(error);
  return e.status === 403 || e.status === 404;
}

export const deductionsApi = {
  /** GET /ess/payslips/{id}/deductions — why each deduction on one of your own payslips is made. */
  async forPayslip(payslipId: string): Promise<Availability<DeductionStatement>> {
    try {
      const d = await apiGet<any>(`/ess/payslips/${payslipId}/deductions`);
      return { available: true, data: mapStatement(d) };
    } catch (error) {
      if (unavailable(error)) return { available: false };
      throw error;
    }
  },

  /** GET /ess/deductions?months= — your statements plus the loans and advances still being repaid. */
  async mine(months = 6): Promise<Availability<MyDeductions>> {
    try {
      const d = await apiGet<any>(`/ess/deductions?months=${months}`);
      return {
        available: true,
        data: {
          statements: (Array.isArray(d?.statements) ? d.statements : []).map(mapStatement),
          loans: (Array.isArray(d?.balances?.loans) ? d.balances.loans : []).map(mapBalance),
          advances: (Array.isArray(d?.balances?.advances) ? d.balances.advances : []).map(mapBalance),
        },
      };
    } catch (error) {
      if (unavailable(error)) return { available: false };
      throw error;
    }
  },
};
