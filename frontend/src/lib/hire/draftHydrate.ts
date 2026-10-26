import type { EmployeeDetail } from '../../api/employees';
import { emptyHireForm, type HireForm } from './hireForm';
import type { IdType } from './nationality';
import { formatNational } from './phone';
import { parsePhoneNumberFromString } from 'libphonenumber-js/min';

export interface HydrateContext { emailDomain?: string; companyCountry: string }

const text = (v: unknown) => (typeof v === 'string' ? v : '');
const amount = (v: number | undefined | null) => (typeof v === 'number' && v > 0 ? String(v) : '');

/** The nationality as an ISO-2 code: the record's code when it has one, else a nationality that already is a code. */
function nationalityOf(d: EmployeeDetail & { nationalityCode?: string }): string {
  const code = text(d.nationalityCode).toUpperCase();
  if (/^[A-Z]{2}$/.test(code)) return code;
  const n = text(d.nationality).toUpperCase();
  return /^[A-Z]{2}$/.test(n) ? n : '';
}

/**
 * CD26: a saved draft (the existing employee GET) becomes the wizard's form again. The primary identity is whichever number
 * the record holds, in the order National ID, Iqama, Border Number, GCC ID, then the passport; a passport beside another
 * document is the second identity row.
 */
export function formFromEmployee(d: EmployeeDetail, ctx: HydrateContext): HireForm {
  const f = emptyHireForm();
  const domain = (ctx.emailDomain ?? '').trim().toLowerCase();
  const work = text(d.workEmail).trim();
  const workLocal = work && domain && work.toLowerCase().endsWith(`@${domain}`) ? work.slice(0, work.length - domain.length - 1) : work;

  const identities: Array<[IdType, string, string | undefined]> = [
    ['nationalId', text(d.idNumber), undefined], ['iqama', text(d.iqamaNumber), d.iqamaExpiryDate],
    ['border', text(d.borderNumber), undefined], ['gccId', text(d.civilId), d.civilIdExpiryDate],
  ];
  const primary = identities.find(([, n]) => n.trim() !== '');
  const passport = text(d.passportNumber).trim();
  let mobileCountry = '';
  let mobileNational = '';
  const phone = text(d.phone).trim();
  if (phone) {
    const parsed = parsePhoneNumberFromString(phone);
    if (parsed?.country) { mobileCountry = parsed.country; mobileNational = formatNational(parsed.number, parsed.country).replace(/[\s()-]/g, ''); }
    else mobileNational = phone;
  }

  return {
    ...f,
    englishName: text(d.englishName) || text(d.fullName), companyId: text(d.companyId), startDate: text(d.joiningDate), designationId: text(d.designationId), gradeId: text(d.gradeId),
    branchId: text(d.branchId), departmentId: text(d.departmentId), managerId: d.managerEmployeeId ? String(d.managerEmployeeId) : '',
    workEmailLocal: workLocal, arabicName: text(d.arabicName), preferredName: text(d.preferredName), gender: text(d.gender), maritalStatus: text(d.maritalStatus),
    nationalityCode: nationalityOf(d), dob: text(d.dateOfBirth), mobileCountry, mobileNational, personalEmail: text(d.personalEmail),
    employmentType: text(d.employmentType), contractType: text(d.contractType),
    contractEndDate: text((d as { contractEndDate?: string }).contractEndDate), probationEndDate: text(d.probationEndDate), workLocation: text(d.workLocation),
    iban: text(d.payrollProfile?.iban) || text(d.bankIban), molId: text(d.payrollProfile?.molId),
    basic: amount(d.salaryBreakdown?.basicSalary), housing: amount(d.salaryBreakdown?.housingAllowance), transport: amount(d.salaryBreakdown?.transportAllowance), other: amount(d.salaryBreakdown?.otherAllowance),
    ...(primary
      ? { idType: primary[0], idNumber: primary[1], idExpiry: text(primary[2]), passportNumber: passport, passportExpiry: text(d.passportExpiryDate) }
      : passport ? { idType: 'passport' as const, idNumber: passport, idExpiry: text(d.passportExpiryDate) } : {}),
  };
}

/** Only the record that was asked for, and only while it is still a draft, can be resumed. */
export function isResumable(d: Pick<EmployeeDetail, 'id' | 'status'>, requestedId: number): boolean {
  return d.id === requestedId && d.status === 'Draft';
}

/** 404 and 403 read the same on purpose (a draft that is not yours looks like one that is not there); anything else is a connection problem. */
export function draftLoadProblem(err: unknown): 'unavailable' | 'network' {
  const status = (err as { response?: { status?: number } })?.response?.status;
  return status === 404 || status === 403 ? 'unavailable' : 'network';
}
