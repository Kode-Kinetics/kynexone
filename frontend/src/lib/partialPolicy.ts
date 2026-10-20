import type { PolicyExtractionProposal } from '../api/setupAssistant';

export type PartialReviewEntry = PolicyExtractionProposal & {
  documentId: string; sourceHash: string; sourceName?: string; missingFields: string[];
};
export type PolicyShape = { kind: 'text' | 'number' | 'boolean' | 'strings'; nullable?: boolean; options?: string[]; integer?: boolean }
  | { kind: 'object'; fields: Record<string, PolicyShape> }
  | { kind: 'array'; item: PolicyShape };
const text: PolicyShape = { kind: 'text' };
const number: PolicyShape = { kind: 'number' };
const integer: PolicyShape = { kind: 'number', integer: true };
const boolean: PolicyShape = { kind: 'boolean' };
const scope: PolicyShape = { kind: 'text', nullable: true };
const choice = (...options: string[]): PolicyShape => ({ kind: 'text', options });
const object = (fields: Record<string, PolicyShape>): PolicyShape => ({ kind: 'object', fields });
const array = (item: PolicyShape): PolicyShape => ({ kind: 'array', item });
// Match the existing setup contract without supplying any missing policy values.
export const partialPolicyShapes: Record<string, PolicyShape> = {
  attendancePolicy: object({ code: text, name: text, graceMinutes: integer, lateThresholdMinutes: integer,
    earlyExitThresholdMinutes: integer, halfDayThresholdMinutes: integer, absentThresholdMinutes: integer,
    standardWorkMinutes: integer, breakMinutes: integer, roundingRule: choice('NearestMinute', 'Nearest15'),
    requiresOvertimeApproval: boolean, allowAbsenceToLeaveConversion: boolean }),
  overtimePolicy: object({ code: text, name: text, hourlyRateBasis: choice('BasicSalary', 'GrossSalary'), standardMonthlyHours: integer,
    minimumMinutes: integer, maximumMinutesPerDay: integer, monthlyCapMinutes: integer,
    roundingRule: choice('NearestMinute', 'Nearest15'), requiresApproval: boolean, allowCompOffConversion: boolean,
    multipliers: array(object({ dayCategory: choice('RegularDay', 'Weekend', 'PublicHoliday'), multiplier: number })) }),
  grades: array(object({ code: text, name: text, band: text, level: integer,
    minSalary: number, midSalary: number, maxSalary: number, currency: text })),
  leavePolicies: array(object({ name: text, leaveTypeCode: text, annualEntitlementDays: number,
    accrualMethod: choice('Monthly', 'Yearly'), encashmentAllowed: boolean, encashmentMaxDays: number,
    minimumDaysPerRequest: number, maximumDaysPerRequest: number, noticeRequiredDays: integer,
    weekendsIncluded: boolean, publicHolidaysIncluded: boolean, appliesOnProbation: boolean,
    payrollImpact: choice('Full', 'Unpaid'), proratePartialMonths: boolean,
    gradeCode: scope, departmentCode: scope, employmentType: scope })),
  benefitPlans: array(object({ code: text, name: text, planType: text, currency: text, effectiveFrom: text,
    effectiveTo: scope, requiresEnrollment: boolean, gradeCodes: { kind: 'strings', nullable: true } })),
};

export function missingPolicyFields(shape: PolicyShape, value: unknown, path = ''): string[] {
  if ('nullable' in shape && shape.nullable && value === null) return [];
  if (shape.kind === 'object') {
    const data = value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
    return Object.entries(shape.fields).flatMap(([key, child]) => missingPolicyFields(child, data[key], path ? `${path}.${key}` : key));
  }
  if (shape.kind === 'array') return !Array.isArray(value) || !value.length ? [path]
    : value.flatMap((item, index) => missingPolicyFields(shape.item, item, `${path}[${index}]`));
  if (shape.kind === 'boolean') return typeof value === 'boolean' ? [] : [path];
  if (shape.kind === 'number') return typeof value === 'number' && Number.isFinite(value) && value >= 0 && (!shape.integer || Number.isInteger(value) && value <= 2147483647) ? [] : [path];
  if (shape.kind === 'strings') return Array.isArray(value) && value.every(item => typeof item === 'string' && item.trim()) ? [] : [path];
  return typeof value === 'string' && value.trim() && (!shape.options || shape.options.includes(value)) ? [] : [path];
}
