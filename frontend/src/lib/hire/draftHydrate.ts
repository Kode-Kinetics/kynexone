import type { EmployeeDetail } from '../../api/employees';
import { emptyHireForm, type HireForm } from './hireForm';
import type { IdType } from './nationality';
import { formatNational } from './phone';
import { buildCountryOptions, foldText, type CountryOption } from './nationality';
import { DEFAULT_ZONE } from './dates';
import { dateLocale } from '../format';
import { parsePhoneNumberFromString } from 'libphonenumber-js/min';

export interface HydrateContext {
  emailDomain?: string; companyCountry: string;
  /** IANA zone date-times are cut in (the tenant/company zone). Defaults to Riyadh. */
  timeZone?: string;
  /** The nationality catalog the picker shows; the built-in ISO list when the API list has not arrived. */
  countryOptions?: readonly CountryOption[];
}

const text = (v: unknown) => (typeof v === 'string' ? v : '');
const amount = (v: number | undefined | null) => (typeof v === 'number' && v > 0 ? String(v) : '');

/**
 * A calendar date (YYYY-MM-DD) from whatever the API sent. The employee read serialises DateTime fields as
 * `2026-11-01T00:00:00Z`; a date input and isRealDate only take the date, and the date is the one in the company zone.
 * A value with no offset is already a local calendar value and keeps its date.
 */
export function normaliseDate(v: unknown, timeZone?: string | null): string {
  const t = typeof v === 'string' ? v.trim() : '';
  if (!t) return '';
  const plain = /^(\d{4}-\d{2}-\d{2})$/.exec(t);
  if (plain) return plain[1];
  const local = /^(\d{4}-\d{2}-\d{2})T[\d:.]*$/.exec(t);
  if (local) return local[1];
  if (!/^\d{4}-\d{2}-\d{2}T.*(Z|[+-]\d{2}:?\d{2})$/i.test(t)) return '';
  const at = new Date(t);
  if (Number.isNaN(at.getTime())) return '';
  const zone = (() => { const z = timeZone?.trim() || DEFAULT_ZONE; try { new Intl.DateTimeFormat('en', { timeZone: z }); return z; } catch { return DEFAULT_ZONE; } })();
  const parts = new Intl.DateTimeFormat(dateLocale('en'), { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(at);
  const get = (k: string) => parts.find(p => p.type === k)?.value ?? '';
  return `${get('year')}-${get('month')}-${get('day')}`;
}

/** The nationality as an ISO-2 code: the record's code; else a nationality that already is a code; else its country name looked up in the catalog. */
function nationalityOf(d: EmployeeDetail, options?: readonly CountryOption[]): string {
  const code = text(d.nationalityCode).toUpperCase();
  if (/^[A-Z]{2}$/.test(code)) return code;
  const raw = text(d.nationality).trim();
  if (/^[A-Za-z]{2}$/.test(raw)) return raw.toUpperCase();
  if (!raw) return '';
  const key = foldText(raw);
  const list = options?.length ? options : buildCountryOptions();
  return list.find(o => foldText(o.nameEn) === key || foldText(o.nameAr) === key)?.code ?? '';
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

  const day = (v: unknown) => normaliseDate(v, ctx.timeZone);
  const identities: Array<[IdType, string, string | undefined]> = [
    ['nationalId', text(d.idNumber), undefined], ['iqama', text(d.iqamaNumber), day(d.iqamaExpiryDate)],
    ['border', text(d.borderNumber), undefined], ['gccId', text(d.civilId), day(d.civilIdExpiryDate)],
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
    englishName: text(d.englishName) || text(d.fullName), companyId: text(d.companyId), startDate: day(d.joiningDate), designationId: text(d.designationId), gradeId: text(d.gradeId),
    branchId: text(d.branchId), departmentId: text(d.departmentId), managerId: d.managerEmployeeId ? String(d.managerEmployeeId) : '',
    workEmailLocal: workLocal, arabicName: text(d.arabicName), preferredName: text(d.preferredName), gender: text(d.gender), maritalStatus: text(d.maritalStatus),
    nationalityCode: nationalityOf(d, ctx.countryOptions), dob: day(d.dateOfBirth), mobileCountry, mobileNational, personalEmail: text(d.personalEmail),
    employmentType: text(d.employmentType), contractType: text(d.contractType),
    contractEndDate: day((d as { contractEndDate?: string }).contractEndDate), probationEndDate: day(d.probationEndDate), workLocation: text(d.workLocation),
    iban: text(d.payrollProfile?.iban) || text(d.bankIban), molId: text(d.payrollProfile?.molId),
    basic: amount(d.salaryBreakdown?.basicSalary), housing: amount(d.salaryBreakdown?.housingAllowance), transport: amount(d.salaryBreakdown?.transportAllowance), other: amount(d.salaryBreakdown?.otherAllowance),
    ...(primary
      ? { idType: primary[0], idNumber: primary[1], idExpiry: text(primary[2]), passportNumber: passport, passportExpiry: day(d.passportExpiryDate) }
      : passport ? { idType: 'passport' as const, idNumber: passport, idExpiry: day(d.passportExpiryDate) } : {}),
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
