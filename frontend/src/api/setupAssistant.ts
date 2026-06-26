import client from './client';

export interface SetupSections { org: boolean; leave: boolean; shifts: boolean; payroll: boolean; }

export interface CompanyProfile {
  countryCode: string;
  industry: string;
  companySize: string;
  currencyCode: string;
  notes?: string;
  sections: SetupSections;
}

export interface DraftDepartment { code: string; nameEn: string; }
export interface DraftDesignation { code: string; titleEn: string; departmentCode: string; jobLevel: string; isManagerRole: boolean; levelRank: number; }
export interface DraftGrade { code: string; name: string; band: string; level: number; }
export interface DraftLeaveType { code: string; nameEn: string; category: string; isPaid: boolean; maxConsecutiveDays: number; requiresAttachment: boolean; colorCode: string; }
export interface DraftShift { code: string; name: string; start: string; end: string; breakMinutes: number; color: string; }
export interface DraftWorkingWeek { workWeek: string; weekStartDay: string; }
export interface DraftPayComponent { code: string; name: string; componentType: string; calculationType: string; amount: number; percentage: number; isTaxable: boolean; }
export interface DraftStatutoryRule { ruleKey: string; ruleValue: string; dataType: string; description: string; }

export interface SetupDraft {
  departments: DraftDepartment[];
  designations: DraftDesignation[];
  grades: DraftGrade[];
  leaveTypes: DraftLeaveType[];
  shifts: DraftShift[];
  workingWeek: DraftWorkingWeek | null;
  payComponents: DraftPayComponent[];
  statutoryRules: DraftStatutoryRule[];
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
    departments: asArray<DraftDepartment>(d.departments),
    designations: asArray<DraftDesignation>(d.designations),
    grades: asArray<DraftGrade>(d.grades),
    leaveTypes: asArray<DraftLeaveType>(d.leaveTypes),
    shifts: asArray<DraftShift>(d.shifts),
    workingWeek: d.workingWeek ?? null,
    payComponents: asArray<DraftPayComponent>(d.payComponents),
    statutoryRules: asArray<DraftStatutoryRule>(d.statutoryRules),
  };
}

export const setupAssistantApi = {
  preview: (profile: CompanyProfile) =>
    client.post<SetupPreviewResult>('/api/setup-assistant/preview', profile).then(r => ({
      draft: normalizeDraft(r.data?.draft),
      notes: asArray<string>(r.data?.notes),
      engine: r.data?.engine ?? '',
    })),

  apply: (draft: SetupDraft, countryCode: string, currencyCode: string) =>
    client.post<{ applied: Record<string, number>; total: number }>(
      '/api/setup-assistant/apply', { draft, countryCode, currencyCode },
    ).then(r => r.data),
};
