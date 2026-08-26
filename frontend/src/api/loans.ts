import client from './client';

// ── Loan Types ────────────────────────────────────────────────────────────────

export interface LoanType {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  maxAmount: number;
  maxInstallments: number;
  repaymentFrequency: string;
  isInterestFree: boolean;
  interestRate: number;
  minServiceMonths: number;
  requiresApproval: boolean;
  isActive: boolean;
}

export interface EmployeeLoan {
  id: string;
  employeeId: string;
  employeeName: string;
  loanTypeId: string;
  loanTypeName: string;
  loanNumber: string;
  requestedAmount: number;
  approvedAmount: number;
  requestedInstallments: number;
  approvedInstallments: number;
  installmentAmount: number;
  repaymentFrequency: string;
  disbursementDate?: string;
  repaymentStartDate?: string;
  totalRepaid: number;
  outstandingBalance: number;
  status: string;
  rejectionReason?: string;
  notes: string;
  isLockedByPayroll: boolean;
  createdAtUtc: string;
}

export interface LoanApproval {
  id: string;
  loanId: string;
  stepOrder: number;
  approverRole: string;
  approvedByName: string;
  status: string;
  comments: string;
  decidedAtUtc?: string;
}

export interface LoanInstallment {
  id: string;
  loanId: string;
  installmentNumber: number;
  dueDate: string;
  amountDue: number;
  amountPaid: number;
  status: string;
  paidDate?: string;
}

// ── Advance Types ─────────────────────────────────────────────────────────────

export interface AdvancePolicy {
  id: string;
  policyName: string;
  maxPercentageOfSalary: number;
  maxAdvancesPerYear: number;
  minServiceMonths: number;
  allowInstallments: boolean;
  maxInstallments: number;
  cooldownMonths: number;
  requiresApproval: boolean;
  isActive: boolean;
}

export interface SalaryAdvance {
  id: string;
  employeeId: string;
  employeeName: string;
  advanceNumber: string;
  requestedAmount: number;
  approvedAmount: number;
  repaymentType: string;
  installments: number;
  installmentAmount: number;
  repaymentStartDate?: string;
  totalRepaid: number;
  outstandingBalance: number;
  reason: string;
  status: string;
  rejectionReason?: string;
  isLockedByPayroll: boolean;
  createdAtUtc: string;
}

export interface AdvanceInstallment {
  id: string;
  advanceId: string;
  installmentNumber: number;
  dueDate: string;
  amountDue: number;
  amountPaid: number;
  status: string;
}

// ── Bonus Types ───────────────────────────────────────────────────────────────

export interface BonusType {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  calculationMethod: string;
  defaultCalculationValue: number;
  // Eligibility
  frequency: string;
  minServiceMonths: number;
  proRataEligibility: boolean;
  requiresApproval: boolean;
  // Compliance flags
  isIncludedInEosb: boolean;
  isIncludedInGosiBase: boolean;
  isIncludedInWps: boolean;
  // Tax
  isTaxable: boolean;
  taxRegion: string;
  taxRate: number;
  notes: string;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc?: string;
}

export interface BonusBatch {
  id: string;
  bonusTypeId: string;
  bonusTypeName: string;
  batchNumber: string;
  batchName: string;
  paymentPeriod: string;
  paymentDate: string;
  totalAmount: number;
  employeeCount: number;
  status: string;
  notes: string;
  isLockedByPayroll: boolean;
  createdAtUtc: string;
}

export interface EmployeeBonus {
  id: string;
  bonusBatchId: string;
  employeeId: string;
  employeeName: string;
  department: string;
  bonusTypeName: string;
  basicSalary: number;
  calculationMethod: string;
  calculationValue: number;
  grossBonusAmount: number;
  taxWithheld: number;
  bonusAmount: number;  // net after tax
  taxRegion: string;
  paymentPeriod: string;
  status: string;
  notes: string;
}

export interface FinanceGlEntry {
  id: string;
  sourceModule: string;
  sourceEntityRef: string;
  eventType: string;
  debitAccount: string;
  creditAccount: string;
  amount: number;
  currency: string;
  entryDate: string;
  period: string;
  description: string;
  postedByName: string;
  createdAtUtc: string;
}

export interface AuditLogEntry {
  id: string;
  action: string;
  oldValuesJson: string;
  newValuesJson: string;
  performedByName: string;
  createdAtUtc: string;
}

// ── API clients ───────────────────────────────────────────────────────────────

export const loanTypesApi = {
  list: () =>
    client.get<LoanType[]>('/api/finance/loans/types').then(r => r.data),
  create: (body: { code: string; nameEn: string; nameAr?: string; maxAmount: number; maxInstallments: number; repaymentFrequency: string; isInterestFree: boolean; interestRate: number; minServiceMonths: number; requiresApproval: boolean }) =>
    client.post<LoanType>('/api/finance/loans/types', body).then(r => r.data),
};

export const loansApi = {
  list: (params: { employeeId?: string; status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; items: EmployeeLoan[] }>('/api/finance/loans', { params }).then(r => r.data),

  get: (id: string) =>
    client.get<{ loan: EmployeeLoan; installments: LoanInstallment[]; approvals: LoanApproval[]; auditLogs: AuditLogEntry[]; glEntries: FinanceGlEntry[] }>(`/api/finance/loans/${id}`).then(r => r.data),

  create: (body: { employeeId?: string; employeeName: string; loanTypeId: string; requestedAmount: number; requestedInstallments: number; notes?: string; employeeIntId?: number }) =>
    client.post<EmployeeLoan>('/api/finance/loans', body).then(r => r.data),

  settle: (id: string, body: { settlementType: string; settlementAmount: number; settlementDate: string; notes?: string }) =>
    client.patch<{ loan: EmployeeLoan }>(`/api/finance/loans/${id}/settle`, body).then(r => r.data),

  getInstallments: (id: string) =>
    client.get<LoanInstallment[]>(`/api/finance/loans/${id}/installments`).then(r => r.data),

  decide: (loanId: string, approvalId: string, body: { decision: string; comments?: string; approvedAmount?: number; approvedInstallments?: number; repaymentStartDate?: string }) =>
    client.patch(`/api/finance/loans/${loanId}/approvals/${approvalId}/decide`, body).then(r => r.data),

  audit: (params: { status?: string; period?: string } = {}) =>
    client.get<{ totalLoans: number; activeLoans: number; settledLoans: number; pendingLoans: number; totalDisbursed: number; totalOutstanding: number; totalRepaid: number; reconciliation: { loanNumber: string; employeeName: string; loanTypeName: string; status: string; approvedAmount: number; totalRepaid: number; outstandingBalance: number; isReconciled: boolean }[] }>('/api/finance/loans/audit', { params }).then(r => r.data),
};

export const advancePolicyApi = {
  get: () =>
    client.get<AdvancePolicy | null>('/api/finance/advances/policy').then(r => r.data),
  upsert: (body: Partial<AdvancePolicy>) =>
    client.post<AdvancePolicy>('/api/finance/advances/policy', body).then(r => r.data),
};

export const advancesApi = {
  list: (params: { employeeId?: string; status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; items: SalaryAdvance[] }>('/api/finance/advances', { params }).then(r => r.data),

  get: (id: string) =>
    client.get<{ advance: SalaryAdvance; installments: AdvanceInstallment[]; auditLogs: AuditLogEntry[]; glEntries: FinanceGlEntry[] }>(`/api/finance/advances/${id}`).then(r => r.data),

  create: (body: { employeeId?: string; employeeName: string; requestedAmount: number; repaymentType: string; installments: number; repaymentStartDate?: string; reason?: string; employeeIntId?: number }) =>
    client.post<SalaryAdvance>('/api/finance/advances', body).then(r => r.data),

  approve: (id: string, body: { approvedAmount: number; installments: number; repaymentStartDate?: string }) =>
    client.patch<SalaryAdvance>(`/api/finance/advances/${id}/approve`, body).then(r => r.data),

  reject: (id: string, reason?: string) =>
    client.patch<SalaryAdvance>(`/api/finance/advances/${id}/reject`, { reason }).then(r => r.data),

  audit: () =>
    client.get<{ totalAdvances: number; activeAdvances: number; settledAdvances: number; pendingAdvances: number; totalDisbursed: number; totalOutstanding: number; totalRepaid: number; reconciliation: { advanceNumber: string; employeeName: string; status: string; approvedAmount: number; totalRepaid: number; outstandingBalance: number; isReconciled: boolean }[] }>('/api/finance/advances/audit').then(r => r.data),
};

export interface BonusTypeRequest {
  code: string;
  nameEn: string;
  nameAr?: string;
  calculationMethod: string;
  defaultCalculationValue?: number;
  isTaxable: boolean;
  frequency?: string;
  minServiceMonths?: number;
  proRataEligibility?: boolean;
  requiresApproval?: boolean;
  isIncludedInEosb?: boolean;
  isIncludedInGosiBase?: boolean;
  isIncludedInWps?: boolean;
  taxRegion?: string;
  taxRate?: number;
  notes?: string;
  isActive?: boolean;
}

export const bonusTypesApi = {
  list: (includeInactive = false) =>
    client.get<BonusType[]>('/api/finance/bonuses/types', { params: { includeInactive } }).then(r => r.data),
  create: (body: BonusTypeRequest) =>
    client.post<BonusType>('/api/finance/bonuses/types', body).then(r => r.data),
  update: (id: string, body: BonusTypeRequest) =>
    client.put<BonusType>(`/api/finance/bonuses/types/${id}`, body).then(r => r.data),
  delete: (id: string) =>
    client.delete<{ deleted: boolean }>(`/api/finance/bonuses/types/${id}`).then(r => r.data),
};

export const bonusBatchesApi = {
  list: (params: { status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; items: BonusBatch[] }>('/api/finance/bonuses/batches', { params }).then(r => r.data),

  get: (id: string) =>
    client.get<{ batch: BonusBatch; bonuses: EmployeeBonus[]; auditLogs: AuditLogEntry[]; glEntries: FinanceGlEntry[] }>(`/api/finance/bonuses/batches/${id}`).then(r => r.data),

  create: (body: { bonusTypeId: string; batchName: string; paymentPeriod: string; paymentDate: string; notes?: string }) =>
    client.post<BonusBatch>('/api/finance/bonuses/batches', body).then(r => r.data),

  update: (id: string, body: { batchName?: string; paymentPeriod?: string; paymentDate?: string; notes?: string }) =>
    client.put<BonusBatch>(`/api/finance/bonuses/batches/${id}`, body).then(r => r.data),

  delete: (id: string) =>
    client.delete<{ deleted: boolean }>(`/api/finance/bonuses/batches/${id}`).then(r => r.data),

  addEmployee: (batchId: string, body: { employeeId?: string; employeeIntId: number; employeeName: string; department?: string; basicSalary: number; calculationMethod: string; calculationValue: number; notes?: string }) =>
    client.post<{ bonus: EmployeeBonus; grossBonusAmount: number; taxWithheld: number; netBonusAmount: number }>(`/api/finance/bonuses/batches/${batchId}/employees`, body).then(r => r.data),

  bulkAddEmployees: (batchId: string, body: { companyId?: string; departmentId?: string; department?: string; gradeId?: string; overrideCalculationValue?: number }) =>
    client.post<{ added: number; skippedDuplicate: number; skippedMinService: number; skippedNoSalary: number; totalNetAdded: number }>(`/api/finance/bonuses/batches/${batchId}/employees/bulk`, body).then(r => r.data),

  updateEmployee: (batchId: string, bonusId: string, body: { calculationMethod?: string; calculationValue?: number; basicSalary?: number; notes?: string }) =>
    client.put<{ bonus: EmployeeBonus; grossBonusAmount: number; taxWithheld: number; netBonusAmount: number }>(`/api/finance/bonuses/batches/${batchId}/employees/${bonusId}`, body).then(r => r.data),

  removeEmployee: (batchId: string, bonusId: string) =>
    client.delete(`/api/finance/bonuses/batches/${batchId}/employees/${bonusId}`),

  submit: (id: string) =>
    client.patch<BonusBatch>(`/api/finance/bonuses/batches/${id}/submit`).then(r => r.data),

  approve: (id: string, comments?: string) =>
    client.patch<BonusBatch>(`/api/finance/bonuses/batches/${id}/approve`, { comments }).then(r => r.data),

  reject: (id: string, reason?: string) =>
    client.patch<BonusBatch>(`/api/finance/bonuses/batches/${id}/reject`, { reason }).then(r => r.data),

  markPaid: (id: string, payrollRunId?: string) =>
    client.patch<BonusBatch>(`/api/finance/bonuses/batches/${id}/mark-paid`, { payrollRunId }).then(r => r.data),

  payrollPending: (paymentPeriod: string) =>
    client.get<{ count: number; totalAmount: number; bonuses: EmployeeBonus[] }>('/api/finance/bonuses/payroll-pending', { params: { paymentPeriod } }).then(r => r.data),

  audit: (period?: string) =>
    client.get<{ totalBatches: number; approvedBatches: number; paidBatches: number; totalBonusAmount: number; paidAmount: number; pendingPaymentAmount: number; byDepartment: { department: string; count: number; totalAmount: number }[] }>('/api/finance/bonuses/audit', { params: period ? { period } : {} }).then(r => r.data),
};
