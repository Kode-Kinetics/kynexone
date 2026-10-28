import type { HireForm } from './hireForm';
import { addYears, ageOn, isRealDate, startDateRange } from './dates';
import { validateIban } from './iban';
import { buildCountryOptions } from './nationality';
import { workEmailLocalProblem, workEmailProblemKey } from '../employeeAccess';
import { isFixedTerm, parseAmount } from './payload';
import { parseMobile } from './phone';
import { normalizeDigits, validateIdNumber, type IdKind } from './saudiId';

export type HireScope = 'draft' | 'create';

export interface FieldIssue {
  field: keyof HireForm;
  /** The wizard step (1..3) that owns the field, so the summary can send the user there. */
  step: 1 | 2 | 3;
  /** An i18n key (the English sentence). */
  key: string;
  params?: Record<string, string>;
}

export interface ValidationContext {
  /** Today in the company's zone (YYYY-MM-DD). */
  today: string;
  emailDomain?: string;
  companyCountry: string;
  /** Set when the chosen designation belongs to a grade. */
  designationGrade?: { gradeId: string; gradeCode: string };
}

const STEP: Partial<Record<keyof HireForm, 1 | 2 | 3>> = {
  englishName: 1, companyId: 1, startDate: 1, designationId: 1, gradeId: 1, workEmail: 1,
  nationalityCode: 2, dob: 2, mobileNational: 2, personalEmail: 2, arabicName: 2, preferredName: 2,
  idNumber: 3, idExpiry: 3, passportNumber: 3, passportExpiry: 3, probationEndDate: 3, iban: 3, molId: 3, workLocation: 3, basic: 3, housing: 3, transport: 3, other: 3, contractEndDate: 3,
};

/** CD26: the server's own limits (EmployeeCreateRequest MaxLength, payroll bounds), so a too-long value names its field and its limit. */
export const FIELD_LIMITS: Partial<Record<keyof HireForm, { limit: number; label: string }>> = {
  englishName: { limit: 150, label: 'English full name' },
  arabicName: { limit: 180, label: 'Arabic full name' },
  preferredName: { limit: 120, label: 'Preferred name' },
  workLocation: { limit: 120, label: 'Work location' },
  molId: { limit: 40, label: 'MOL ID' },
  employeeCode: { limit: 50, label: 'Employee code' },
  iban: { limit: 34, label: 'IBAN' },
  mobileNational: { limit: 60, label: 'Mobile number' },
  personalEmail: { limit: 254, label: 'Personal email' },
  workEmail: { limit: 254, label: 'Work email' },
  idNumber: { limit: 200, label: 'ID number' },
  passportNumber: { limit: 200, label: 'Passport number' },
};
export const TOO_LONG_KEY = 'Use {limit} characters or fewer in {field}.';

/** The wizard step that owns a field. */
export const stepOf = (field: keyof HireForm): 1 | 2 | 3 => STEP[field] ?? 1;

const MAX_AMOUNT = 9_999_999_999.99;
const KNOWN_COUNTRIES = new Set(buildCountryOptions().map(o => o.code));
const blank = (v: string) => (v ?? '').trim() === '';
const EN = new Intl.DisplayNames(['en'], { type: 'region' });

/**
 * Hard errors only (data integrity, CD6). Statutory floors (backdated start, age under 15, probation over 180 days,
 * a grade band, an expired ID) are server warnings answered with an acknowledgement, not errors here.
 */
export function validateHire(form: HireForm, ctx: ValidationContext, _scope: HireScope = 'create'): FieldIssue[] {
  const out: FieldIssue[] = [];
  const add = (field: keyof HireForm, key: string, params?: Record<string, string>) => out.push({ field, step: STEP[field] ?? 1, key, params });

  const name = form.englishName.trim();
  if (!name) add('englishName', 'Enter the employee\'s English full name.');
  else if ([...name].length > 150) add('englishName', 'Use 150 characters or fewer.');
  for (const f of ['arabicName', 'preferredName', 'workLocation', 'molId', 'employeeCode'] as const) {
    const lim = FIELD_LIMITS[f]!;
    if ([...form[f].trim()].length > lim.limit) add(f, TOO_LONG_KEY, { limit: String(lim.limit), field: lim.label });
  }

  if (blank(form.companyId)) add('companyId', 'Choose the employing company.');

  const { min, max } = startDateRange(ctx.today);
  // CD21: a missing start date or designation/grade is an activation need (see activationNeeds), never an error here.
  if (!blank(form.startDate) && (!isRealDate(form.startDate) || form.startDate < min || form.startDate > max)) add('startDate', 'Start date must be between {min} and {max}.', { min, max });

  // OD7: one full address on any domain; only its format is checked here (the server also checks it is not already taken).
  if (!blank(form.workEmail)) {
    const problem = workEmailLocalProblem(form.workEmail);
    const key = workEmailProblemKey(problem);
    if (key) add('workEmail', key);
  }

  if (!blank(form.nationalityCode) && !KNOWN_COUNTRIES.has(form.nationalityCode)) add('nationalityCode', 'Choose a nationality from the list.');

  if (!blank(form.dob)) {
    // Future and over 100 are hard; under 15 is a server warning (CD15), so there is no age floor here.
    if (!isRealDate(form.dob)) add('dob', 'Enter a valid date.');
    else if (form.dob > ctx.today) add('dob', 'Date of birth cannot be in the future.');
    else if (form.dob < addYears(ctx.today, -100) || ageOn(form.dob, ctx.today) > 100) add('dob', 'Date of birth cannot be more than 100 years ago.');
  }

  if (!blank(form.mobileNational)) {
    const country = form.mobileCountry || ctx.companyCountry;
    if (!parseMobile(form.mobileNational, country)) add('mobileNational', 'Enter a valid {country} mobile number.', { country: EN.of(country) ?? country, countryCode: country });
  }
  if (!blank(form.personalEmail) && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.personalEmail.trim())) add('personalEmail', 'Enter a valid email address.');

  if (!blank(form.idNumber) && form.idType) {
    const t = form.idType;
    if (t === 'nationalId' || t === 'iqama' || t === 'border') {
      const r = validateIdNumber(t as IdKind, form.idNumber);
      const overridden = r === 'checksum' && form.idChecksumOverrideReason.trim().length >= 10;
      if (r === 'format') add('idNumber', t === 'nationalId' ? 'National ID must be 10 digits starting with 1.' : t === 'iqama' ? 'Iqama number must be 10 digits starting with 2.' : 'Border number must be 10 digits starting with 3.');
      else if (r === 'checksum' && !overridden) add('idNumber', 'This ID number is not valid. Check the digits.');
    } else if (!/^[A-Z0-9]{6,20}$/.test(normalizeDigits(form.idNumber).toUpperCase())) {
      add('idNumber', 'Enter 6 to 20 letters or digits.');
    }
  }
  if (!blank(form.idExpiry) && !isRealDate(form.idExpiry)) add('idExpiry', 'Enter a valid date.');
  if (!blank(form.passportNumber) && !/^[A-Z0-9]{6,20}$/.test(normalizeDigits(form.passportNumber).toUpperCase())) add('passportNumber', 'Enter 6 to 20 letters or digits.');
  if (!blank(form.passportExpiry) && !isRealDate(form.passportExpiry)) add('passportExpiry', 'Enter a valid date.');
  if (!blank(form.probationEndDate) && !isRealDate(form.probationEndDate)) add('probationEndDate', 'Enter a valid date.');

  if (!blank(form.iban)) {
    const r = validateIban(form.iban, ctx.companyCountry);
    if (r === 'saudi_structure') add('iban', 'Saudi IBANs have 24 characters and start with SA.');
    else if (r === 'format') add('iban', 'Check the IBAN country code and length.');
    else if (r === 'checksum') add('iban', 'This IBAN is not valid. Check the digits.');
  }

  for (const f of ['basic', 'housing', 'transport', 'other'] as const) {
    if (blank(form[f])) continue;
    const n = parseAmount(form[f]);
    if (n === undefined) add(f, 'Enter the amount as a number.');
    else if (n < 0) add(f, 'Amount cannot be negative.');
    else if (n > MAX_AMOUNT) add(f, 'Enter an amount of 9,999,999,999.99 or less.');
  }

  if (!blank(form.contractEndDate)) {
    // CD17: an end date needs a fixed-term contract, and must be after the start.
    if (!isFixedTerm(form.contractType)) add('contractEndDate', 'A contract end date needs a fixed-term contract type.');
    else if (!isRealDate(form.contractEndDate) || (!blank(form.startDate) && isRealDate(form.startDate) && form.contractEndDate <= form.startDate)) {
      add('contractEndDate', 'The contract end date must be after the start date.');
    }
  }
  return out;
}

export interface ActivationNeed { field: keyof HireForm; step: 1 | 2 | 3; key: string }

/** CD21: what is still missing before the employee can be activated. Shown as a neutral hint and on Review, never as an error. */
export function activationNeeds(form: HireForm): ActivationNeed[] {
  const out: ActivationNeed[] = [];
  if (blank(form.startDate)) out.push({ field: 'startDate', step: 1, key: 'Start date' });
  if (blank(form.designationId) && blank(form.gradeId)) out.push({ field: 'designationId', step: 1, key: 'Designation or grade' });
  return out;
}
