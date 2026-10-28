import { expect, test } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import { emptyHireForm, type HireForm } from '../src/lib/hire/hireForm';
import { validateHire, type FieldIssue } from '../src/lib/hire/hireValidation';
import { validateIban } from '../src/lib/hire/iban';
import { issueText } from '../src/views/employees/hire/issueText';

const ctx = { today: '2026-10-27', emailDomain: 'example.test', companyCountry: 'SA' };
const four: HireForm = { ...emptyHireForm(), englishName: 'Alex Morgan', companyId: 'c1', startDate: '2026-11-01', designationId: 'd1' };
const msg = (issues: FieldIssue[], field: string, locale: 'en' | 'ar' = 'en') => {
  const i = issues.find(x => x.field === field);
  return i ? issueText(i, (k, p) => translate(locale, k, p), locale) : undefined;
};

test.describe('CD26 date of birth sentences (future and over 100 are hard, under 15 is a warning)', () => {
  test('a future date says so; no age range is named', () => {
    const m = msg(validateHire({ ...four, dob: '2027-01-01' }, ctx, 'draft'), 'dob');
    expect(m).toBe('Date of birth cannot be in the future.');
    expect(m).not.toMatch(/80|15/);
    expect(msg(validateHire({ ...four, dob: '2027-01-01' }, ctx, 'draft'), 'dob', 'ar')).toMatch(/[؀-ۿ]/);
  });
  test('over 100 years says so', () => {
    expect(msg(validateHire({ ...four, dob: '1920-01-01' }, ctx, 'draft'), 'dob')).toBe('Date of birth cannot be more than 100 years ago.');
  });
  test('an impossible date is a plain invalid-date error; a 14-year-old is not a client error', () => {
    expect(msg(validateHire({ ...four, dob: '2000-02-31' }, ctx, 'draft'), 'dob')).toBe('Enter a valid date.');
    expect(validateHire({ ...four, dob: '2015-01-01' }, ctx, 'draft')).toEqual([]);
  });
});

test.describe('CD26/CD29 IBAN: checksum and format are different sentences, and the rule follows the company country', () => {
  const validSa = 'SA0380000000608010167519';
  const badChecksumSa = 'SA0380000000608010167518';
  test('Saudi company: format error keeps the 24-character sentence, checksum error is its own', () => {
    expect(msg(validateHire({ ...four, iban: 'SA03800000006080101675' }, ctx, 'draft'), 'iban')).toBe('Saudi IBANs have 24 characters and start with SA.');
    expect(msg(validateHire({ ...four, iban: badChecksumSa }, ctx, 'draft'), 'iban')).toBe('This IBAN is not valid. Check the digits.');
    expect(msg(validateHire({ ...four, iban: badChecksumSa }, ctx, 'draft'), 'iban', 'ar')).toMatch(/[؀-ۿ]/);
    expect(validateHire({ ...four, iban: validSa }, ctx, 'draft')).toEqual([]);
  });
  test('Saudi company: a non-SA IBAN is refused with the Saudi sentence', () => {
    expect(msg(validateHire({ ...four, iban: 'AE070331234567890123456' }, ctx, 'draft'), 'iban')).toBe('Saudi IBANs have 24 characters and start with SA.');
  });
  test('UAE company: a valid AE IBAN passes, a wrong-length one is a format error, a bad checksum is the checksum error', () => {
    const ae = { ...ctx, companyCountry: 'AE' };
    expect(validateHire({ ...four, iban: 'AE07 0331 2345 6789 0123 456' }, ae, 'draft')).toEqual([]);
    expect(msg(validateHire({ ...four, iban: 'AE07033123456789012345' }, ae, 'draft'), 'iban')).toBe('Check the IBAN country code and length.');
    expect(msg(validateHire({ ...four, iban: 'AE070331234567890123457' }, ae, 'draft'), 'iban')).toBe('This IBAN is not valid. Check the digits.');
  });
  test('validateIban takes the company country', () => {
    expect(validateIban('AE070331234567890123456', 'AE')).toBe('ok');
    expect(validateIban('AE070331234567890123456', 'SA')).toBe('saudi_structure');
    expect(validateIban('AE07033123456789012345', 'AE')).toBe('format');
    expect(validateIban(badChecksumSa, 'SA')).toBe('checksum');
    expect(validateIban(validSa)).toBe('ok');
  });
});

test.describe('CD26 value_too_long names the field and its own limit (client side)', () => {
  test('Arabic name, preferred name, work location and MOL ID each carry their own limit', () => {
    const cases: Array<[Partial<HireForm>, string, string]> = [
      [{ arabicName: 'ا'.repeat(181) }, 'arabicName', 'Use 180 characters or fewer in Arabic full name.'],
      [{ preferredName: 'a'.repeat(121) }, 'preferredName', 'Use 120 characters or fewer in Preferred name.'],
      [{ workLocation: 'a'.repeat(121) }, 'workLocation', 'Use 120 characters or fewer in Work location.'],
      [{ molId: '1'.repeat(41) }, 'molId', 'Use 40 characters or fewer in MOL ID.'],
    ];
    for (const [over, field, en] of cases) expect(msg(validateHire({ ...four, ...over }, ctx, 'draft'), field)).toBe(en);
    const ar = msg(validateHire({ ...four, molId: '1'.repeat(41) }, ctx, 'draft'), 'molId', 'ar')!;
    expect(ar).toContain('40');
    expect(ar).toContain('رقم المنشأة');
  });
  test('the name keeps its own 150 sentence', () => {
    expect(msg(validateHire({ ...four, englishName: 'A'.repeat(151) }, ctx, 'draft'), 'englishName')).toBe('Use 150 characters or fewer.');
  });
});

test.describe('CD17/CD26 contract end date', () => {
  const fixed = { ...four, contractType: 'Fixed term' };
  test('fixed-term: end must be after start', () => {
    expect(msg(validateHire({ ...fixed, contractEndDate: '2026-11-01' }, ctx, 'draft'), 'contractEndDate')).toBe('The contract end date must be after the start date.');
    expect(validateHire({ ...fixed, contractEndDate: '2027-11-01' }, ctx, 'draft')).toEqual([]);
  });
  test('an end date without a fixed-term contract is a hard error', () => {
    expect(msg(validateHire({ ...four, contractType: 'Permanent', contractEndDate: '2027-11-01' }, ctx, 'draft'), 'contractEndDate')).toBe('A contract end date needs a fixed-term contract type.');
    expect(msg(validateHire({ ...four, contractEndDate: '2027-11-01' }, ctx, 'draft'), 'contractEndDate')).toBe('A contract end date needs a fixed-term contract type.');
  });
});

test.describe('CD27 passport, Border Number; CD31 probation', () => {
  const pk = { ...four, nationalityCode: 'PK', idType: 'iqama' as const, idNumber: '2000000006' };
  test('passport number and expiry are checked', () => {
    expect(msg(validateHire({ ...pk, passportNumber: 'ab12' }, ctx, 'draft'), 'passportNumber')).toBe('Enter 6 to 20 letters or digits.');
    expect(msg(validateHire({ ...pk, passportExpiry: '2030-02-31' }, ctx, 'draft'), 'passportExpiry')).toBe('Enter a valid date.');
    expect(validateHire({ ...pk, passportNumber: 'ab1234567', passportExpiry: '2030-05-05' }, ctx, 'draft')).toEqual([]);
  });
  test('Border Number is 10 digits starting with 3', () => {
    const b = { ...four, nationalityCode: 'PK', idType: 'border' as const };
    expect(validateHire({ ...b, idNumber: '3000000004' }, ctx, 'draft')).toEqual([]);
    expect(msg(validateHire({ ...b, idNumber: '2000000006' }, ctx, 'draft'), 'idNumber')).toBe('Border number must be 10 digits starting with 3.');
  });
  test('probation end date must be a real date; its 180-day limit is a server warning, not an error', () => {
    expect(msg(validateHire({ ...four, probationEndDate: '2027-02-31' }, ctx, 'draft'), 'probationEndDate')).toBe('Enter a valid date.');
    expect(validateHire({ ...four, probationEndDate: '2027-12-31' }, ctx, 'draft')).toEqual([]);
  });
  test('the new fields name their wizard step', () => {
    const issues = validateHire({ ...pk, passportNumber: 'x', probationEndDate: '2027-02-31' }, ctx, 'draft');
    expect(Object.fromEntries(issues.map(i => [i.field, i.step]))).toMatchObject({ passportNumber: 3, probationEndDate: 3 });
  });
});
