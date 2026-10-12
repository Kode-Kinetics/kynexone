import client from './client';

// ─────────────────────────────────────────────────────────────────────────────
// CMP — merit planning, pay benchmarks and pay equity (/api/compensation/merit).
// The server owns every rule and every number: this client only carries them. Money is a decimal in JSON and is
// only ever displayed (useFormat), never recomputed here. Refusals arrive as 422 { error, message, blockers }.
// ─────────────────────────────────────────────────────────────────────────────

const BASE = '/api/compensation/merit';

export const COMP_PERMISSIONS = {
  read: 'compensation.read',
  plan: 'compensation.plan',
  manage: 'compensation.manage',
  approve: 'compensation.approve',
  benchmarks: 'compensation.benchmarks.manage',
  equity: 'compensation.equity.read',
} as const;

/** Every key that opens the page; the page then shows each audience its own tabs. */
export const COMPENSATION_PAGE_PERMISSIONS = Object.values(COMP_PERMISSIONS);

export type CycleStatus = 'Draft' | 'Planning' | 'Review' | 'Approved' | 'Applied' | 'Cancelled';
export type WorksheetStatus = 'Draft' | 'Submitted' | 'Returned' | 'Approved' | 'Cancelled';
export type ProrationRule = 'None' | 'EligibleDays';
export type PoolType = 'PercentOfEligibleBasic' | 'FixedAmount';
export type PayBasis = 'MonthlyBasic' | 'MonthlyGross';

export interface CycleSummary {
  id: string; companyId: string | null; companyName: string; name: string; status: CycleStatus; effectiveDate: string; currency: string;
  performancePeriodStart: string; performancePeriodEnd: string; worksheetCount: number; approvedWorksheetCount: number; version: number;
}

export interface Budget {
  departmentId: string; departmentName: string; hasBudget: boolean; poolType: PoolType | null; poolPercent: number | null; fixedAmount: number | null;
  eligibleCount: number; eligibleBasicCycle: number; pool: number; proposed: number; remaining: number; percentUsed: number | null; overspent: boolean;
}

export interface Guideline { id?: string; rating: string; compaRatioMin: number; compaRatioMax: number | null; minPercent: number; targetPercent: number; maxPercent: number }
export interface FxRate { id?: string; fromCurrency: string; rate: number; source: string; rateDate: string; enteredByName?: string; enteredAtUtc?: string }

export interface WorksheetSummary {
  id: string; managerEmployeeId: number; managerName: string; scenarioName: string; isPrimary: boolean; status: WorksheetStatus; revision: number;
  lineCount: number; proposedTotal: number; overspend: boolean; approvalRequestId: string | null; decisionComments: string | null; version: number; cycleId: string;
  /** False when the worksheet's team is in a company this login cannot open (my-worksheets only). */
  companyAccess?: boolean;
}

export interface PersonNote { employeeId: number; employeeCode: string; name: string; reason: string; ownerEmployeeId?: number | null; ownerName?: string | null; ownerCanBeOpened?: boolean }
export interface Blocker { code: string; message: string }
export interface Totals { eligibleCount: number; eligibleBasicCycle: number; pool: number; proposed: number; remaining: number; percentUsed: number | null; awardCount: number }

export interface ApplyLine {
  lineId: string; employeeId: number; employeeName: string; changeRequestId: string | null; changeStatus: string | null; blockedReason: string | null;
  newBasic: number; payCurrency: string; reversalChangeRequestId: string | null; reversalStatus: string | null;
}
export interface ApplySummary { awards: number; scheduled: number; applied: number; returnedForReview: number; cancelled: number; blocked: number; lines: ApplyLine[] }

export interface Cycle {
  id: string; companyId: string | null; companyName: string; name: string; status: CycleStatus; performancePeriodStart: string; performancePeriodEnd: string;
  effectiveDate: string; currency: string; payComponent: string; hireDateCutoff: string | null; eligibleStatuses: string[];
  includeGradeIds: string[]; excludeGradeIds: string[]; includeDepartmentIds: string[]; excludeDepartmentIds: string[];
  minimumEligibleDays: number; prorationRule: ProrationRule; equityIncludesNationality: boolean; version: number;
  budgets: Budget[]; guidelines: Guideline[]; fxRates: FxRate[]; payCurrencies: string[]; worksheets: WorksheetSummary[];
  uncovered: PersonNote[]; ineligible: PersonNote[]; blockers: Blocker[]; totals: Totals; allowedActions: string[]; apply: ApplySummary | null;
  approvedAtUtc: string | null; appliedAtUtc: string | null; cancelReason: string | null; retro: Retro | null;
}

export interface Retro { monthsInPast: number; paidPeriods: string[]; monthlyDeltaCycle: number; retroCostCycle: number; acknowledgementRequired: boolean }

export interface BenchmarkPosition {
  state: 'missing' | 'stale' | 'current' | 'out_of_scope'; setId: string | null; setName: string | null; isSynthetic: boolean; surveyDate: string | null;
  payBasis: PayBasis | null; p50Cycle: number | null; position: number | null; detail: string | null;
}

export interface Line {
  id: string; employeeId: number; employeeCode: string; employeeName: string; departmentName: string; designationName: string; gradeName: string;
  payCurrency: string; fxRate: number | null; currentBasic: number; currentGross: number; currentBasicCycle: number | null; currentGrossCycle: number | null;
  gradeMin: number | null; gradeMid: number | null; gradeMax: number | null; gradeCurrency: string | null; compaRatio: number | null; rangePosition: number | null;
  rating: string; eligibleDays: number; prorationFactor: number; guidelineMinPercent: number | null; guidelineTargetPercent: number | null; guidelineMaxPercent: number | null;
  proposedIncrease: number; proposedPercent: number; proratedIncrease: number; proratedIncreasePay: number; newBasic: number; newGross: number;
  newCompaRatio: number | null; outOfGuideline: boolean; justification: string | null; flags: string[]; blockingMessages: string[];
  proposedAsPercent: boolean; assignedByHr: boolean;
  benchmark: BenchmarkPosition; changeRequestId: string | null; changeStatus: string | null; applyBlockedReason: string | null;
}

export interface ScenarioRef { id: string; scenarioName: string; isPrimary: boolean; status: WorksheetStatus; proposedTotal: number }

export interface Worksheet {
  id: string; cycleId: string; cycleName: string; cycleStatus: CycleStatus; currency: string; effectiveDate: string; managerEmployeeId: number; managerName: string;
  scenarioName: string; isPrimary: boolean; status: WorksheetStatus; revision: number; version: number; overspendJustification: string | null;
  approvalRequestId: string | null; decisionComments: string | null; submittedAtUtc: string | null; lines: Line[]; excluded: PersonNote[];
  budgetImpact: Budget[]; proposedTotal: number; overspend: boolean; canEdit: boolean; canSubmit: boolean; blockers: Blocker[]; scenarios: ScenarioRef[];
}

export interface MyWorksheets { cycles: CycleSummary[]; worksheets: WorksheetSummary[] }

export interface EligibilityInput {
  hireDateCutoff: string | null; eligibleStatuses: string[]; includeGradeIds: string[]; excludeGradeIds: string[];
  includeDepartmentIds: string[]; excludeDepartmentIds: string[]; minimumEligibleDays: number; prorationRule: ProrationRule;
}

export interface LineInput { employeeId: number; proposedIncrease: number | null; proposedPercent: number | null; justification: string | null }

export interface EquityGroup { group: string; suppressed: boolean; count: number | null; meanCompaRatio: number | null; medianCompaRatio: number | null; meanGrossCycle: number | null; projectedMeanCompaRatio: number | null }
export interface EquityCohort { gradeName: string; designationName: string; suppressed: boolean; countLabel: string | null; groups: EquityGroup[]; gapPercent: number | null; projectedGapPercent: number | null; dimension: string | null }
export interface Equity { cycleId: string; currency: string; minimumCohort: number; cohorts: EquityCohort[]; nationalityCohorts: EquityCohort[]; excludedNoGrade: number; excludedNoSalary: number; notice: string }

export interface BenchmarkSet {
  id: string; companyId: string | null; name: string; provider: string; licenceStatement: string; surveyDate: string; geography: string; currency: string;
  payBasis: PayBasis; isSynthetic: boolean; isStale: boolean; sourceFileName: string; sourceFileSha256: string; rowCount: number; isActive: boolean;
  importedByName: string; importedAtUtc: string; mappedRows: number;
}
export interface BenchmarkRow { id: string; jobFamily: string; level: string; sampleSize: number; p25: number; p50: number; p75: number; p90: number; designationId: string | null; gradeId: string | null }
export interface ImportIssue { row: number; field: string; message: string }
export interface ImportResult { imported: boolean; set: BenchmarkSet | null; issues: ImportIssue[]; carriedMappings: number }

export interface BenchmarkImportMeta {
  companyId: string; name: string; provider: string; licenceStatement: string; surveyDate: string; geography: string; currency: string; payBasis: PayBasis; isSynthetic: boolean;
}

/** The server's refusal, if it is one: { error, message, blockers }. */
export function compRefusal(err: unknown): { error: string; message: string; blockers: Blocker[] } | null {
  const data = (err as { response?: { data?: { error?: unknown; message?: unknown; blockers?: unknown } } })?.response?.data;
  if (!data || typeof data.message !== 'string') return null;
  return { error: typeof data.error === 'string' ? data.error : 'error', message: data.message, blockers: Array.isArray(data.blockers) ? (data.blockers as Blocker[]) : [] };
}

const get = <T,>(url: string) => client.get<T>(url).then((r) => r.data);
const put = <T,>(url: string, body: unknown) => client.put<T>(url, body).then((r) => r.data);
const post = <T,>(url: string, body?: unknown) => client.post<T>(url, body ?? {}).then((r) => r.data);

export const compensationApi = {
  cycles: () => get<CycleSummary[]>(`${BASE}/cycles`),
  cycle: (id: string) => get<Cycle>(`${BASE}/cycles/${id}`),
  createCycle: (body: { companyId: string; name: string; performancePeriodStart: string; performancePeriodEnd: string; effectiveDate: string; eligibility: EligibilityInput; equityIncludesNationality: boolean }) =>
    post<Cycle>(`${BASE}/cycles`, body),
  updateCycle: (id: string, body: { version: number; name: string; performancePeriodStart: string; performancePeriodEnd: string; effectiveDate: string; eligibility: EligibilityInput; equityIncludesNationality: boolean }) =>
    put<Cycle>(`${BASE}/cycles/${id}`, body),
  saveBudgets: (id: string, budgets: { departmentId: string; poolType: PoolType; poolPercent: number | null; fixedAmount: number | null }[]) =>
    put<Cycle>(`${BASE}/cycles/${id}/budgets`, { budgets }),
  saveGuidelines: (id: string, guidelines: Guideline[]) => put<Cycle>(`${BASE}/cycles/${id}/guidelines`, { guidelines }),
  saveFx: (id: string, rates: FxRate[]) => put<Cycle>(`${BASE}/cycles/${id}/fx`, { rates }),
  transition: (id: string, action: 'open-planning' | 'start-review' | 'reopen' | 'approve' | 'cancel', version: number, reason?: string, acknowledgeRetroCost = false) =>
    post<Cycle>(`${BASE}/cycles/${id}/${action}`, { version, reason: reason ?? null, acknowledgeRetroCost }),
  openWorksheet: (cycleId: string, managerEmployeeId: number) => post<Worksheet>(`${BASE}/cycles/${cycleId}/worksheets`, { managerEmployeeId }),
  assign: (worksheetId: string, employeeId: number) => post<Worksheet>(`${BASE}/worksheets/${worksheetId}/assign`, { employeeId }),
  closeWorksheet: (worksheetId: string, reason: string) => post<Cycle>(`${BASE}/worksheets/${worksheetId}/close`, { reason }),
  apply: (id: string, acknowledgeRetroCost = false) => post<Cycle>(`${BASE}/cycles/${id}/apply`, { acknowledgeRetroCost }),
  withdraw: (lineId: string, reason: string) => post<ApplyLine>(`${BASE}/awards/${lineId}/withdraw`, { reason }),
  equity: (id: string) => get<Equity>(`${BASE}/cycles/${id}/equity`),
  createScenario: (cycleId: string, body: { managerEmployeeId: number | null; scenarioName: string; copyFromWorksheetId: string | null }) =>
    post<Worksheet>(`${BASE}/cycles/${cycleId}/scenarios`, body),
  myWorksheets: () => get<MyWorksheets>(`${BASE}/my-worksheets`),
  worksheet: (id: string) => get<Worksheet>(`${BASE}/worksheets/${id}`),
  saveWorksheet: (id: string, body: { version: number; overspendJustification: string | null; lines: LineInput[] }) => put<Worksheet>(`${BASE}/worksheets/${id}`, body),
  submitWorksheet: (id: string, version: number) => post<Worksheet>(`${BASE}/worksheets/${id}/submit`, { version }),
  makePrimary: (id: string, version: number) => post<Worksheet>(`${BASE}/worksheets/${id}/make-primary`, { version }),
  reverse: (lineId: string, effectiveDate: string, reason: string, acknowledgeLockedPeriods = false) =>
    post<ApplyLine>(`${BASE}/awards/${lineId}/reverse`, { effectiveDate, reason, acknowledgeLockedPeriods }),
  benchmarkSets: (companyId?: string) => get<BenchmarkSet[]>(`${BASE}/benchmarks${companyId ? `?companyId=${encodeURIComponent(companyId)}` : ''}`),
  benchmarkSet: (id: string) => get<{ set: BenchmarkSet; rows: BenchmarkRow[] }>(`${BASE}/benchmarks/${id}`),
  benchmarkTemplate: () => client.get<string>(`${BASE}/benchmarks/template.csv`, { responseType: 'text' }).then((r) => r.data),
  importBenchmarks: (file: File, meta: BenchmarkImportMeta) => {
    const form = new FormData();
    form.append('file', file);
    Object.entries(meta).forEach(([k, v]) => form.append(k, String(v)));
    return client.post<ImportResult>(`${BASE}/benchmarks/import`, form).then((r) => r.data);
  },
  mapRow: (rowId: string, designationId: string | null, gradeId: string | null) => put<BenchmarkRow>(`${BASE}/benchmarks/rows/${rowId}/mapping`, { designationId, gradeId }),
  retireSet: (id: string) => post<BenchmarkSet>(`${BASE}/benchmarks/${id}/retire`),
};
