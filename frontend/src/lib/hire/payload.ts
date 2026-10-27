import type { EmployeeCreateRequest } from '../../api/employees';
import type { HireForm } from './hireForm';
import { parseMobile } from './phone';
import { normalizeDigits } from './saudiId';
import { normalizeIban } from './iban';

export interface AcknowledgedWarning { code: string; reason: string }
/** The create request plus the additive fields of 40-ARCHITECTURE 3.2. */
export type HireCreateRequest = Omit<EmployeeCreateRequest, 'payrollProfile'> & {
  /** CD30: the statutory flags are left to the server (company-country policy); the wizard never sets them. */
  payrollProfile?: Omit<NonNullable<EmployeeCreateRequest['payrollProfile']>, 'wpsEligible' | 'eosbEligible'> & { wpsEligible?: boolean; eosbEligible?: boolean };
  nationalityCode?: string;
  contractEndDate?: string;
  duplicateOverrideReason?: string;
  /** Sent with duplicateOverrideReason when an authorised user confirms "this is a different person"; the server refuses a strong match otherwise. */
  acknowledgeDuplicate?: boolean;
  acknowledgedWarnings?: AcknowledgedWarning[];
  /** P3: the hire template the wizard applied; the server uses it only to raise the onboarding checklist. */
  hireTemplateId?: string;
};
export interface PayloadContext { companyCountry: string; currency: string; /** The company's email domain; a work-email local part is sent as local@domain. */ emailDomain?: string }
export interface PayloadExtras { acknowledgedWarnings?: AcknowledgedWarning[]; duplicateOverrideReason?: string; hireTemplateId?: string }

const ID_FIELD: Record<string, string> = { nationalId: 'id_number', iqama: 'iqama_number', border: 'border_number', passport: 'passport_number', gccId: 'civil_id' };

/** True for a fixed-term contract type, however the reference value is spelled ("Fixed term", "FixedTerm", "Fixed"). */
export const isFixedTerm = (contractType: string) => /fixed/i.test(contractType ?? '');

/** CD26: the work email is the local part plus the company domain; a typed address on that domain is kept as typed. */
export function composeWorkEmail(local: string, domain?: string): string | undefined {
  const t = (local ?? '').trim();
  if (!t) return undefined;
  if (t.includes('@')) return t;
  const d = (domain ?? '').trim().replace(/^@/, '');
  return d ? `${t}@${d}` : undefined;
}

const blank = (v: string) => (v ?? '').trim() === '';
const opt = (v: string) => blank(v) ? undefined : v.trim();

/** "12,000.50" -> 12000.5; undefined for blank or not a number. */
export function parseAmount(v: string): number | undefined {
  if (blank(v)) return undefined;
  const n = Number(normalizeDigits(v).replace(/[,\s]/g, ''));
  return Number.isFinite(n) ? n : undefined;
}

/** CD42: crypto.randomUUID only. There is no weaker fallback; without it (an insecure context) the request is refused rather than keyed from a weak source. */
export function newIdempotencyKey(): string {
  const c = typeof crypto !== 'undefined' ? crypto : undefined;
  if (!c || typeof c.randomUUID !== 'function') throw new Error('crypto.randomUUID is not available in this browser context.');
  return c.randomUUID();
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
  const record = (kind: string, value: string, expiry: string) => ({ countryCode: ctx.companyCountry, fieldKey: ID_FIELD[kind], fieldLabel: ID_FIELD[kind], fieldValue: value, expiryDate: opt(expiry), isSensitive: true, isRequired: false });
  const records = [
    ...(!blank(form.idNumber) && form.idType ? [record(form.idType, idDigits, form.idExpiry)] : []),
    // CD27: an expat's passport is a second row; when the primary document already is the passport there is only one.
    ...(!blank(form.passportNumber) && form.idType !== 'passport' ? [record('passport', form.passportNumber.trim().toUpperCase(), form.passportExpiry)] : []),
  ];
  const body: HireCreateRequest = {
    manualEmployeeCode: form.manualCode,
    englishName: form.englishName.trim(),
    gender: form.gender,
    employeeCode: form.manualCode ? opt(form.employeeCode) : undefined,
    arabicName: opt(form.arabicName),
    preferredName: opt(form.preferredName),
    dateOfBirth: opt(form.dob),
    nationalityCode: /^[A-Z]{2}$/.test(form.nationalityCode) ? form.nationalityCode : undefined,
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
    contractEndDate: isFixedTerm(form.contractType) ? opt(form.contractEndDate) : undefined,
    probationEndDate: opt(form.probationEndDate),
    workEmail: composeWorkEmail(form.workEmailLocal, ctx.emailDomain),
    joiningDate: opt(form.startDate),
    workLocation: opt(form.workLocation),
    payrollProfile: hasPayroll ? { iban, molId: opt(form.molId), salaryCurrency: ctx.currency } : undefined,
    salaryBreakdown: hasSalary ? { ...salary, currency: ctx.currency } : undefined,
    complianceRecords: records.length ? records : undefined,
    acknowledgedWarnings: extras.acknowledgedWarnings?.length ? extras.acknowledgedWarnings : undefined,
    duplicateOverrideReason: extras.duplicateOverrideReason,
    hireTemplateId: extras.hireTemplateId,
    acknowledgeDuplicate: extras.duplicateOverrideReason?.trim() ? true : undefined,
  };
  if (form.idChecksumOverrideReason.trim() && body.acknowledgedWarnings === undefined && !blank(form.idNumber)) {
    body.acknowledgedWarnings = [{ code: 'id_checksum_invalid', reason: form.idChecksumOverrideReason.trim() }];
  }
  return body;
}
