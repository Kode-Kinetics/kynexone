import type { EmployeeCreateRequest } from '../../api/employees';
import type { HireForm } from './hireForm';
import { parseMobile } from './phone';
import { normalizeDigits } from './saudiId';
import { normalizeIban } from './iban';

export interface AcknowledgedWarning { code: string; reason: string }
/** The create request plus the additive fields of 40-ARCHITECTURE 3.2. */
export type HireCreateRequest = EmployeeCreateRequest & {
  nationalityCode?: string;
  contractEndDate?: string;
  duplicateOverrideReason?: string;
  acknowledgedWarnings?: AcknowledgedWarning[];
};
export interface PayloadContext { companyCountry: string; currency: string }
export interface PayloadExtras { acknowledgedWarnings?: AcknowledgedWarning[]; duplicateOverrideReason?: string }

const ID_FIELD: Record<string, string> = { nationalId: 'id_number', iqama: 'iqama_number', passport: 'passport_number', gccId: 'civil_id' };

const blank = (v: string) => (v ?? '').trim() === '';
const opt = (v: string) => blank(v) ? undefined : v.trim();

/** "12,000.50" -> 12000.5; undefined for blank or not a number. */
export function parseAmount(v: string): number | undefined {
  if (blank(v)) return undefined;
  const n = Number(normalizeDigits(v).replace(/[,\s]/g, ''));
  return Number.isFinite(n) ? n : undefined;
}

export function newIdempotencyKey(): string {
  const c = typeof crypto !== 'undefined' ? crypto : undefined;
  if (c?.randomUUID) return c.randomUUID();
  const b = new Uint8Array(16);
  if (c?.getRandomValues) c.getRandomValues(b); else for (let i = 0; i < 16; i++) b[i] = Math.floor(Math.random() * 256);
  b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
  const h = [...b].map(x => x.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

/** Only what was filled goes out; nothing is defaulted (no joining date, no gender, no zero salary). */
export function buildCreateRequest(form: HireForm, ctx: PayloadContext, extras: PayloadExtras = {}): HireCreateRequest {
  const mobile = blank(form.mobileNational) ? undefined : parseMobile(form.mobileNational, form.mobileCountry || ctx.companyCountry)?.e164;
  const num = (v: string) => { const n = parseAmount(v); return n && n > 0 ? n : undefined; };
  const salary = { basicSalary: num(form.basic), housingAllowance: num(form.housing), transportAllowance: num(form.transport), otherAllowance: num(form.other) };
  const hasSalary = Object.values(salary).some(v => v !== undefined);
  const iban = blank(form.iban) ? undefined : normalizeIban(form.iban);
  const hasPayroll = !!iban || !blank(form.molId);
  const idDigits = form.idType === 'passport' || form.idType === 'gccId' ? form.idNumber.trim().toUpperCase() : normalizeDigits(form.idNumber);
  const body: HireCreateRequest = {
    manualEmployeeCode: form.manualCode,
    englishName: form.englishName.trim(),
    gender: form.gender,
    employeeCode: form.manualCode ? opt(form.employeeCode) : undefined,
    arabicName: opt(form.arabicName),
    preferredName: opt(form.preferredName),
    dateOfBirth: opt(form.dob),
    nationalityCode: opt(form.nationalityCode),
    maritalStatus: opt(form.maritalStatus),
    personalEmail: opt(form.personalEmail),
    mobileNumber: mobile,
    companyId: opt(form.companyId),
    branchId: opt(form.branchId),
    departmentId: opt(form.departmentId),
    designationId: opt(form.designationId),
    gradeId: opt(form.gradeId),
    reportingManagerEmployeeId: blank(form.managerId) ? undefined : Number(form.managerId),
    employmentType: opt(form.employmentType),
    contractType: opt(form.contractType),
    contractEndDate: opt(form.contractEndDate),
    joiningDate: opt(form.startDate),
    workLocation: opt(form.workLocation),
    payrollProfile: hasPayroll ? { iban, molId: opt(form.molId), salaryCurrency: ctx.currency, wpsEligible: true, eosbEligible: true } : undefined,
    salaryBreakdown: hasSalary ? { ...salary, currency: ctx.currency } : undefined,
    complianceRecords: !blank(form.idNumber) && form.idType
      ? [{ countryCode: ctx.companyCountry, fieldKey: ID_FIELD[form.idType], fieldLabel: ID_FIELD[form.idType], fieldValue: idDigits, expiryDate: opt(form.idExpiry), isSensitive: true, isRequired: false }]
      : undefined,
    acknowledgedWarnings: extras.acknowledgedWarnings?.length ? extras.acknowledgedWarnings : undefined,
    duplicateOverrideReason: extras.duplicateOverrideReason,
  };
  if (form.idChecksumOverrideReason.trim() && body.acknowledgedWarnings === undefined && !blank(form.idNumber)) {
    body.acknowledgedWarnings = [{ code: 'id_checksum_invalid', reason: form.idChecksumOverrideReason.trim() }];
  }
  return body;
}
