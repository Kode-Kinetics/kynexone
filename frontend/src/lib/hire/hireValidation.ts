import type { HireForm } from './hireForm';
import { addYears, ageOn, isRealDate, startDateRange } from './dates';
import { validateIban } from './iban';
import { buildCountryOptions } from './nationality';
import { parseAmount } from './payload';
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
  englishName: 1, companyId: 1, startDate: 1, designationId: 1, gradeId: 1, workEmailLocal: 1,
  nationalityCode: 2, dob: 2, mobileNational: 2, personalEmail: 2,
  idNumber: 3, idExpiry: 3, iban: 3, basic: 3, housing: 3, transport: 3, other: 3, contractEndDate: 3,
};

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

  if (blank(form.companyId)) add('companyId', 'Choose the employing company.');

  const { min, max } = startDateRange(ctx.today);
  // CD21: a missing start date or designation/grade is an activation need (see activationNeeds), never an error here.
  if (!blank(form.startDate) && (!isRealDate(form.startDate) || form.startDate < min || form.startDate > max)) add('startDate', 'Start date must be between {min} and {max}.', { min, max });

  if (ctx.designationGrade && !blank(form.gradeId) && form.gradeId !== ctx.designationGrade.gradeId) {
    add('gradeId', 'This designation belongs to grade {grade}.', { grade: ctx.designationGrade.gradeCode });
  }

  if (!blank(form.workEmailLocal) && form.workEmailLocal.includes('@') && ctx.emailDomain) {
    if (!form.workEmailLocal.trim().toLowerCase().endsWith(`@${ctx.emailDomain.toLowerCase()}`)) add('workEmailLocal', 'Work email must end in @{domain}.', { domain: ctx.emailDomain });
  }

  if (!blank(form.nationalityCode) && !KNOWN_COUNTRIES.has(form.nationalityCode)) add('nationalityCode', 'Choose a nationality from the list.');

  if (!blank(form.dob)) {
    const bad = !isRealDate(form.dob) || form.dob > ctx.today || form.dob < addYears(ctx.today, -100);
    if (bad || ageOn(form.dob, ctx.today) > 100) add('dob', 'Date of birth must make the employee between 15 and 80 years old.');
  }

  if (!blank(form.mobileNational)) {
    const country = form.mobileCountry || ctx.companyCountry;
    if (!parseMobile(form.mobileNational, country)) add('mobileNational', 'Enter a valid {country} mobile number.', { country: EN.of(country) ?? country, countryCode: country });
  }
  if (!blank(form.personalEmail) && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.personalEmail.trim())) add('personalEmail', 'Enter a valid email address.');

  if (!blank(form.idNumber) && form.idType) {
    const t = form.idType;
    if (t === 'nationalId' || t === 'iqama') {
      const r = validateIdNumber(t as IdKind, form.idNumber);
      const overridden = r === 'checksum' && form.idChecksumOverrideReason.trim().length >= 10;
      if (r === 'format') add('idNumber', t === 'nationalId' ? 'National ID must be 10 digits starting with 1.' : 'Iqama number must be 10 digits starting with 2.');
      else if (r === 'checksum' && !overridden) add('idNumber', 'This ID number is not valid. Check the digits.');
    } else if (!/^[A-Z0-9]{6,20}$/.test(normalizeDigits(form.idNumber).toUpperCase())) {
      add('idNumber', 'Enter 6 to 20 letters or digits.');
    }
  }
  if (!blank(form.idExpiry) && !isRealDate(form.idExpiry)) add('idExpiry', 'Enter a valid date.');

  if (!blank(form.iban) && validateIban(form.iban) !== 'ok') add('iban', 'Saudi IBANs have 24 characters and start with SA.');

  for (const f of ['basic', 'housing', 'transport', 'other'] as const) {
    if (blank(form[f])) continue;
    const n = parseAmount(form[f]);
    if (n === undefined) add(f, 'Enter the amount as a number.');
    else if (n < 0) add(f, 'Amount cannot be negative.');
    else if (n > MAX_AMOUNT) add(f, 'Enter an amount of 9,999,999,999.99 or less.');
  }

  if (!blank(form.contractEndDate)) {
    if (!isRealDate(form.contractEndDate) || (!blank(form.startDate) && isRealDate(form.startDate) && form.contractEndDate <= form.startDate)) {
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
