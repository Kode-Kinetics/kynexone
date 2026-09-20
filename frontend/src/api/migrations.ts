import client from './client';

// ── Migration / opening-balance import ────────────────────────────────────────
//
// Before this client existed the engine had no caller at all: a consultant drove
// `/api/migrations` from Postman, against a JSON envelope whose sections are CSV strings.
// That is survivable once. It is not survivable when the customer's own team has to
// re-run it four times during parallel, at 11pm, with corrections.

export interface MigrationPackageRequest {
  externalBatchId: string | null;
  sections: Record<string, string>;
  dryRun: boolean;
}

export interface LockedPeriodRefusal {
  companyId: string;
  companyName: string;
  payrollRunId: string;
  period: string;
  runType: string;
  cutoverDate: string;
  reason: string;
}

export interface MigrationReconciliation {
  batchId: string;
  status: string;
  packageChecksum: string;
  receivedRows: number;
  createdRows: number;
  updatedRows: number;
  skippedRows: number;
  errorRows: number;
  currentSection: string;
  sectionCounts: Record<string, number>;
  /** Control total per section — the money the FILE claims, to tie to the source report. */
  sectionTotals: Record<string, number>;
  errors: string[];
  lockedPeriodRefusals: LockedPeriodRefusal[];
}

/** Thrown shape when a commit is refused because a payroll period is already locked. */
export interface LockedPeriodConflict {
  code: 'cutover_period_locked';
  message: string;
  refusals: LockedPeriodRefusal[];
}

export interface CarriedInRow {
  entityType: string;
  entityId: string;
  cutoverDate: string;
  carriedAmount: number;
  currency: string;
  sourceSystem: string;
  sourceRecordId: string;
  migrationBatchId: string;
  reference: string | null;
  currentBalance: number | null;
  movementSinceCutover: number | null;
}

export interface CutoverEntity {
  companyId: string | null;
  companyName: string;
  cutoverDate: string;
  status: string;
  sourceSystem: string;
  notes: string;
  carriedIn: { entityType: string; rows: number; employees: number; carriedTotal: number }[];
}

/**
 * Section metadata for the wizard. The ORDER matches the engine's own apply order, which is
 * load-bearing: companyCutover has to be first because every opening balance is validated
 * against the cutover of the company that owns the employee.
 */
export interface SectionMeta {
  key: string;
  label: string;
  description: string;
  /** Does this section carry a balance in from a previous system? */
  openingBalance: boolean;
}

export const MIGRATION_SECTIONS: SectionMeta[] = [
  { key: 'companyCutover', label: 'Cutover dates', description: 'One row per legal entity, naming the date this product takes over its payroll. Import this first — nothing else is accepted without it.', openingBalance: false },
  { key: 'roles', label: 'Roles', description: 'Security roles carried across from the previous system.', openingBalance: false },
  { key: 'users', label: 'Users', description: 'Login accounts, invited rather than activated.', openingBalance: false },
  { key: 'leaveBalances', label: 'Leave balances', description: 'Entitled, accrued, used, carried forward and encashed, per leave type.', openingBalance: true },
  { key: 'attendanceDaily', label: 'Attendance history', description: 'Daily records including late, early-exit and overtime minutes.', openingBalance: false },
  { key: 'employeeHistory', label: 'Employment history', description: 'Job and status changes, as an audit trail.', openingBalance: false },
  { key: 'payrollOpeningBalances', label: 'Year-to-date payroll', description: 'YTD gross, deductions and net, plus the statutory and tax split for the annual reconciliation.', openingBalance: true },
  { key: 'loans', label: 'Loans', description: 'Outstanding balances with their remaining instalment schedules, imported mid-life so payroll keeps deducting correctly.', openingBalance: true },
  { key: 'advances', label: 'Salary advances', description: 'Outstanding advance balances and their remaining repayments.', openingBalance: true },
  { key: 'eosbOpeningProvision', label: 'End-of-service provision', description: 'The accrued liability already on the customer’s balance sheet, per employee.', openingBalance: true },
  { key: 'benefitsEnrollments', label: 'Benefits enrolments', description: 'Plan enrolments and contributions.', openingBalance: false },
  { key: 'documentManifests', label: 'Document manifest', description: 'Pointers to documents in the legacy store. No files are moved.', openingBalance: false },
  { key: 'contracts', label: 'Contracts', description: 'Employment contracts and their basic salary.', openingBalance: false },
  { key: 'reconciliationSignoffs', label: 'Reconciliation sign-off', description: 'Who prepared and who approved the reconciliation, with the variance count and evidence.', openingBalance: false },
];

export const migrationsApi = {
  templates: () => client.get<Record<string, string>>('/api/migrations/template').then((r) => r.data),

  preview: (payload: MigrationPackageRequest) =>
    client.post<MigrationReconciliation>('/api/migrations/preview', payload).then((r) => r.data),

  commit: (payload: MigrationPackageRequest) =>
    client.post<MigrationReconciliation>('/api/migrations/commit', payload).then((r) => r.data),

  resume: (batchId: string, payload: MigrationPackageRequest) =>
    client.post<MigrationReconciliation>(`/api/migrations/${batchId}/resume`, payload).then((r) => r.data),

  status: (batchId: string) =>
    client.get<MigrationReconciliation>(`/api/migrations/${batchId}`).then((r) => r.data),

  cutoverStatus: (companyId?: string) =>
    client
      .get<{ entities: CutoverEntity[] }>('/api/payroll/parallel-run/cutover', {
        params: companyId ? { companyId } : undefined,
      })
      .then((r) => r.data),

  carriedIn: (employeeCode: string) =>
    client
      .get<{ employeeCode: string; carriedIn: CarriedInRow[]; message?: string }>(
        `/api/payroll/parallel-run/employees/${encodeURIComponent(employeeCode)}/carried-in`,
      )
      .then((r) => r.data),
};
