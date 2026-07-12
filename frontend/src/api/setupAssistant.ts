import client from './client';

export interface SetupSections { org: boolean; leave: boolean; shifts: boolean; payroll: boolean; entity: boolean; governance: boolean; }

export interface CompanyProfile {
  countryCode: string;
  industry: string;
  companySize: string;
  currencyCode: string;
  notes?: string;
  legalEntityName?: string;
  branchCity?: string;
  operatingModel?: string;
  payrollModel?: string;
  approvalModel?: string;
  strictEntityScope: boolean;
  requireCostCenterForPayroll: boolean;
  requireGradeForApprovalPolicy: boolean;
  sections: SetupSections;
}

export interface DraftDepartment { code: string; nameEn: string; }
export interface DraftBranch { code: string; nameEn: string; city: string; isHeadOffice: boolean; }
export interface DraftCostCenter { code: string; name: string; departmentCode: string; }
export interface DraftDesignation { code: string; titleEn: string; departmentCode: string; gradeCode: string; jobLevel: string; isManagerRole: boolean; levelRank: number; }
export interface DraftGrade { code: string; name: string; band: string; level: number; minSalary: number; midSalary: number; maxSalary: number; currency: string; }
export interface DraftGradePayComponent { gradeCode: string; componentCode: string; componentName: string; componentType: string; calculationType: string; amount: number; percentage: number; isTaxable: boolean; frequency: string; }
export interface DraftLeaveType { code: string; nameEn: string; category: string; isPaid: boolean; maxConsecutiveDays: number; requiresAttachment: boolean; colorCode: string; }
export interface DraftShift { code: string; name: string; start: string; end: string; breakMinutes: number; color: string; }
export interface DraftWorkingWeek { workWeek: string; weekStartDay: string; }
export interface DraftPayComponent { code: string; name: string; componentType: string; calculationType: string; amount: number; percentage: number; isTaxable: boolean; }
export interface DraftStatutoryRule { ruleKey: string; ruleValue: string; dataType: string; description: string; }
export interface DraftEmployeeIdRule { companyPrefix: string; useCountryPrefix: boolean; useBranchPrefix: boolean; useDepartmentPrefix: boolean; useYear: boolean; paddingLength: number; nextSequence: number; allowManualOverride: boolean; }
export interface DraftHrConfig { useDeptHeadApproval: boolean; useHrFinalApproval: boolean; useSupervisorBeforeManager: boolean; allowDottedLineApproval: boolean; autoCreateDeptOnImport: boolean; autoCreateDesignationOnImport: boolean; requireImportPreviewBeforeCommit: boolean; allowCrossDeptManager: boolean; allowCrossLocationManager: boolean; requireCostCenterForPayroll: boolean; requireGradeForApprovalPolicy: boolean; }

export interface SetupDraft {
  branches: DraftBranch[];
  departments: DraftDepartment[];
  costCenters: DraftCostCenter[];
  designations: DraftDesignation[];
  grades: DraftGrade[];
  gradePayComponents: DraftGradePayComponent[];
  leaveTypes: DraftLeaveType[];
  shifts: DraftShift[];
  workingWeek: DraftWorkingWeek | null;
  payComponents: DraftPayComponent[];
  statutoryRules: DraftStatutoryRule[];
  employeeIdRule: DraftEmployeeIdRule | null;
  hrConfig: DraftHrConfig | null;
}

export interface SetupPreviewResult { draft: SetupDraft; notes: string[]; engine: string; }

const asArray = <T,>(v: unknown): T[] => (Array.isArray(v) ? (v as T[]) : []);

/**
 * Coerce a raw preview response into a render-safe shape. LLM-generated JSON
 * (or backend serialization) can omit/null any list; the UI maps over all of
 * them during render, so a missing array would throw mid-render and blank the
 * page. Every list defaults to []; workingWeek defaults to null.
 */
export function normalizeDraft(raw: unknown): SetupDraft {
  const d = (raw ?? {}) as Partial<SetupDraft>;
  return {
    branches: asArray<DraftBranch>(d.branches),
    departments: asArray<DraftDepartment>(d.departments),
    costCenters: asArray<DraftCostCenter>(d.costCenters),
    designations: asArray<DraftDesignation>(d.designations),
    grades: asArray<DraftGrade>(d.grades),
    gradePayComponents: asArray<DraftGradePayComponent>(d.gradePayComponents),
    leaveTypes: asArray<DraftLeaveType>(d.leaveTypes),
    shifts: asArray<DraftShift>(d.shifts),
    workingWeek: d.workingWeek ?? null,
    payComponents: asArray<DraftPayComponent>(d.payComponents),
    statutoryRules: asArray<DraftStatutoryRule>(d.statutoryRules),
    employeeIdRule: d.employeeIdRule ?? null,
    hrConfig: d.hrConfig ?? null,
  };
}

export const setupAssistantApi = {
  preview: (profile: CompanyProfile) =>
    client.post<SetupPreviewResult>('/api/setup-assistant/preview', profile).then(r => ({
      draft: normalizeDraft(r.data?.draft),
      notes: asArray<string>(r.data?.notes),
      engine: r.data?.engine ?? '',
    })),

  apply: (draft: SetupDraft, countryCode: string, currencyCode: string, legalEntityName?: string) =>
    client.post<{ applied: Record<string, number>; total: number }>(
      '/api/setup-assistant/apply', { draft, countryCode, currencyCode, legalEntityName },
    ).then(r => r.data),
};

export interface OrgStructureImportRequest {
  companiesCsv?: string;
  branchesCsv?: string;
  costCentersCsv?: string;
  departmentsCsv?: string;
  gradesCsv?: string;
  gradePayComponentsCsv?: string;
  designationsCsv?: string;
}

export interface OrgStructureImportResult {
  received: number;
  errors: number;
  warnings: number;
  hasBlockingErrors: boolean;
  committed: boolean;
  applied: Record<string, number>;
  rows: { rowNumber: number; entityCode?: string; entityName?: string; status: string; errors: string[]; warnings: string[] }[];
}

export const orgStructureImportApi = {
  template: () => client.get<string>('/api/setup/organization-structure-import/template', { responseType: 'text' }).then(r => r.data),
  preview: (body: OrgStructureImportRequest) =>
    client.post<OrgStructureImportResult>('/api/setup/organization-structure-import/preview', body).then(r => r.data),
  commit: (body: OrgStructureImportRequest) =>
    client.post<OrgStructureImportResult>('/api/setup/organization-structure-import/commit', body).then(r => r.data),
};
