import client from './client';
import type { PayrollRuleKey, PayrollRuleView } from '../lib/payrollRules';

export interface PendingPayrollRuleChange {
  id: string;
  requestedBy: string | null;
  requestedAtUtc: string;
  reason: string | null;
  effectiveFrom: string;
  changes: { key: PayrollRuleKey; from: string | null; to: string | null }[];
  warnings: string[];
}

export interface PayrollRulesResponse {
  companyId: string;
  rules: PayrollRuleView[];
  pending?: PendingPayrollRuleChange[];
  annualLeaveTypeFound?: boolean;
}

export interface PayrollRuleWarning { key: PayrollRuleKey; code: string; message: string }

export interface SavePayrollRulesBody {
  companyId: string;
  changes: { key: PayrollRuleKey; value: string | null }[];
  acknowledge: boolean;
  note?: string;
  /** First day the change applies (yyyy-MM-dd). Never in the past; omitted = today. */
  effectiveFrom?: string;
  /** Echoes the warnings being acknowledged, as "key:CODE" (the server checks they match). */
  acknowledgedWarnings?: string[];
}

/** 200: applied now. 202: a change that lowers pay, waiting for a different approver. */
export type SavePayrollRulesResult =
  | { status: 'applied'; view: PayrollRulesResponse }
  | { status: 'pending'; view: PayrollRulesResponse };

export const payrollRulesApi = {
  get: (companyId: string) =>
    client.get<PayrollRulesResponse>('/api/payroll/company-rules', { params: { companyId } }).then(r => r.data),
  save: (body: SavePayrollRulesBody): Promise<SavePayrollRulesResult> =>
    client.put<PayrollRulesResponse | { status: string; view: PayrollRulesResponse }>('/api/payroll/company-rules', body).then(r =>
      r.status === 202
        ? { status: 'pending' as const, view: (r.data as { view: PayrollRulesResponse }).view }
        : { status: 'applied' as const, view: r.data as PayrollRulesResponse }),
  approve: (id: string, note?: string) =>
    client.post<PayrollRulesResponse>(`/api/payroll/company-rules/${id}/approve`, { note }).then(r => r.data),
  reject: (id: string, note: string) =>
    client.post<PayrollRulesResponse>(`/api/payroll/company-rules/${id}/reject`, { note }).then(r => r.data),
};
