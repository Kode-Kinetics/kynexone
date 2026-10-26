import { expect, test } from '@playwright/test';
import { LOCALE_DICTS, translate } from '../src/i18n/translations';
import { emptyHireForm, type HireForm } from '../src/lib/hire/hireForm';
import { activationNeeds, validateHire, type FieldIssue } from '../src/lib/hire/hireValidation';

const ctx = { today: '2026-10-27', emailDomain: 'example.test', companyCountry: 'SA' };
const four: HireForm = { ...emptyHireForm(), englishName: 'Alex Morgan', companyId: 'c1', startDate: '2026-11-01', designationId: 'd1' };
const keys = (issues: FieldIssue[]) => issues.map(i => i.field);
const msg = (issues: FieldIssue[], field: string, locale: 'en' | 'ar' = 'en') => {
  const i = issues.find(x => x.field === field);
  return i ? translate(locale, i.key, i.params) : undefined;
};

// The error copy table in 20-UX-PRODUCT-SPEC section 3, verbatim.
const COPY: Array<[string, string, string]> = [
  ['Name empty', 'Enter the employee\'s English full name.', 'أدخل الاسم الكامل للموظف بالإنجليزية.'],
  ['Name too long', 'Use 150 characters or fewer.', 'استخدم 150 حرفًا أو أقل.'],
  ['Company missing', 'Choose the employing company.', 'اختر الشركة صاحبة العمل.'],
  ['Start date missing', 'Enter the start date.', 'أدخل تاريخ المباشرة.'],
  ['Start date out of range', 'Start date must be between {min} and {max}.', 'يجب أن يكون تاريخ المباشرة بين {min} و{max}.'],
  ['Designation/grade missing', 'Choose a designation or a grade.', 'اختر المسمى الوظيفي أو الدرجة.'],
  ['Grade conflict', 'This designation belongs to grade {grade}.', 'هذا المسمى الوظيفي تابع للدرجة {grade}.'],
  ['Nationality unknown', 'Choose a nationality from the list.', 'اختر الجنسية من القائمة.'],
  ['National ID invalid', 'National ID must be 10 digits starting with 1.', 'يجب أن يتكون رقم الهوية الوطنية من 10 أرقام ويبدأ بالرقم 1.'],
  ['Iqama invalid', 'Iqama number must be 10 digits starting with 2.', 'يجب أن يتكون رقم الإقامة من 10 أرقام ويبدأ بالرقم 2.'],
  ['ID checksum', 'This ID number is not valid. Check the digits.', 'رقم الهوية غير صالح. تحقق من الأرقام.'],
  ['DOB in the future', 'Date of birth cannot be in the future.', 'لا يمكن أن يكون تاريخ الميلاد في المستقبل.'],
  ['DOB over 100', 'Date of birth cannot be more than 100 years ago.', 'لا يمكن أن يكون تاريخ الميلاد قبل أكثر من 100 سنة.'],
  ['Phone invalid', 'Enter a valid {country} mobile number.', 'أدخل رقم جوال {country} صحيحًا.'],
  ['IBAN invalid', 'Saudi IBANs have 24 characters and start with SA.', 'يتكون الآيبان السعودي من 24 خانة ويبدأ بـ SA.'],
  ['Work email wrong domain', 'Work email must end in @{domain}.', 'يجب أن ينتهي البريد الإلكتروني الوظيفي بـ \u200e@{domain}.'],
  ['Negative amount', 'Amount cannot be negative.', 'لا يمكن أن يكون المبلغ سالبًا.'],
];

test.describe('hireValidation', () => {
  test('every error row of the UX copy table exists in the EN and AR catalogs', () => {
    for (const [name, en, ar] of COPY) {
      expect(LOCALE_DICTS.en[en], `en: ${name}`).toBe(en);
      expect(LOCALE_DICTS.ar[en], `ar: ${name}`).toBe(ar);
    }
  });

  test('draft scope needs only the English name and the company', () => {
    expect(keys(validateHire(emptyHireForm(), ctx, 'draft'))).toEqual(['englishName', 'companyId']);
    expect(validateHire({ ...emptyHireForm(), englishName: 'Alex Morgan', companyId: 'c1' }, ctx, 'draft')).toEqual([]);
  });
  test('CD21: create scope gates on the same draft floor; start date and designation/grade never block (AC 2, 6)', () => {
    const f = { ...emptyHireForm(), englishName: 'Alex', companyId: 'c1' };
    expect(validateHire(f, ctx, 'create')).toEqual([]);
    expect(validateHire(four, ctx, 'create')).toEqual([]);
  });
  test('CD21: activationNeeds lists the missing start date and designation-or-grade, neutrally', () => {
    const f = { ...emptyHireForm(), englishName: 'Alex', companyId: 'c1' };
    expect(activationNeeds(f).map(n => n.field)).toEqual(['startDate', 'designationId']);
    expect(activationNeeds({ ...f, startDate: '2026-11-01', gradeId: 'g1' })).toEqual([]);
    expect(activationNeeds(four)).toEqual([]);
  });
  test('name: empty, 150 ok, 151 too long, digits refused (AC 10)', () => {
    expect(msg(validateHire({ ...four, englishName: ' ' }, ctx, 'draft'), 'englishName')).toBe('Enter the employee\'s English full name.');
    expect(validateHire({ ...four, englishName: 'A'.repeat(150) }, ctx, 'draft')).toEqual([]);
    expect(msg(validateHire({ ...four, englishName: 'A'.repeat(151) }, ctx, 'draft'), 'englishName')).toBe('Use 150 characters or fewer.');
  });
  test('start date window is hard at today-50y..today+2y; older than 30 days is NOT an error (it is a server warning)', () => {
    expect(msg(validateHire({ ...four, startDate: '2028-10-28' }, ctx, 'create'), 'startDate')).toBe('Start date must be between 1976-10-27 and 2028-10-27.');
    expect(validateHire({ ...four, startDate: '2028-10-27' }, ctx, 'create')).toEqual([]);
    expect(keys(validateHire({ ...four, startDate: '1976-10-26' }, ctx, 'create'))).toEqual(['startDate']);
    expect(validateHire({ ...four, startDate: '2020-01-01' }, ctx, 'create')).toEqual([]);
    expect(keys(validateHire({ ...four, startDate: '2026-02-30' }, ctx, 'create'))).toEqual(['startDate']);
  });
  test('grade conflict: designation tied to G1, grade G2 chosen (AC 9)', () => {
    const c = { ...ctx, designationGrade: { gradeId: 'g1', gradeCode: 'G1' } };
    const issues = validateHire({ ...four, gradeId: 'g2' }, c, 'create');
    expect(msg(issues, 'gradeId')).toBe('This designation belongs to grade G1.');
    expect(validateHire({ ...four, gradeId: 'g1' }, c, 'create')).toEqual([]);
  });
  test('nationality must be a code from the list (AC 15)', () => {
    expect(msg(validateHire({ ...four, nationalityCode: 'Suadi' }, ctx, 'draft'), 'nationalityCode')).toBe('Choose a nationality from the list.');
    expect(validateHire({ ...four, nationalityCode: 'SA' }, ctx, 'draft')).toEqual([]);
  });
  test('ID: format vs checksum, by type (AC 19)', () => {
    const base = { ...four, nationalityCode: 'SA', idType: 'nationalId' as const };
    expect(msg(validateHire({ ...base, idNumber: '2000000006' }, ctx, 'draft'), 'idNumber')).toBe('National ID must be 10 digits starting with 1.');
    expect(msg(validateHire({ ...base, idNumber: '1000000009' }, ctx, 'draft'), 'idNumber')).toBe('This ID number is not valid. Check the digits.');
    expect(validateHire({ ...base, idNumber: '1000000008' }, ctx, 'draft')).toEqual([]);
    const iq = { ...four, nationalityCode: 'PK', idType: 'iqama' as const };
    expect(msg(validateHire({ ...iq, idNumber: '1000000008' }, ctx, 'draft'), 'idNumber')).toBe('Iqama number must be 10 digits starting with 2.');
    expect(validateHire({ ...iq, idNumber: '2000000006' }, ctx, 'draft')).toEqual([]);
    expect(validateHire({ ...iq, idType: 'passport', idNumber: 'AB1234567' }, ctx, 'draft')).toEqual([]);
  });
  test('ID checksum override reason lifts only the checksum error', () => {
    const f = { ...four, nationalityCode: 'SA', idType: 'nationalId' as const, idNumber: '1000000009' };
    expect(validateHire({ ...f, idChecksumOverrideReason: 'Legacy number confirmed on the card' }, ctx, 'draft')).toEqual([]);
    expect(keys(validateHire({ ...f, idNumber: '1000000009x', idChecksumOverrideReason: 'Legacy number confirmed on the card' }, ctx, 'draft'))).toEqual(['idNumber']);
  });
  test('date of birth: future and over 100 are hard; 14 is only a server warning (AC 20)', () => {
    expect(msg(validateHire({ ...four, dob: '2027-01-01' }, ctx, 'draft'), 'dob')).toBe('Date of birth cannot be in the future.');
    expect(keys(validateHire({ ...four, dob: '1920-01-01' }, ctx, 'draft'))).toEqual(['dob']);
    expect(validateHire({ ...four, dob: '2012-06-01' }, ctx, 'draft')).toEqual([]);
    expect(validateHire({ ...four, dob: '1990-06-01' }, ctx, 'draft')).toEqual([]);
  });
  test('phone is validated against its own country (AC 22-23)', () => {
    const f = { ...four, mobileCountry: 'SA' };
    expect(msg(validateHire({ ...f, mobileNational: '0412345678' }, ctx, 'draft'), 'mobileNational')).toBe('Enter a valid Saudi Arabia mobile number.');
    expect(validateHire({ ...f, mobileNational: '0512345678' }, ctx, 'draft')).toEqual([]);
  });
  test('Saudi IBAN (AC 24)', () => {
    expect(msg(validateHire({ ...four, iban: 'SA03800000006080101675' }, ctx, 'draft'), 'iban')).toBe('Saudi IBANs have 24 characters and start with SA.');
    expect(validateHire({ ...four, iban: 'SA03 8000 0000 6080 1016 7519' }, ctx, 'draft')).toEqual([]);
  });
  test('work email local part must not carry another domain', () => {
    expect(msg(validateHire({ ...four, workEmailLocal: 'alex@gmail.com' }, ctx, 'draft'), 'workEmailLocal')).toBe('Work email must end in @example.test.');
    expect(validateHire({ ...four, workEmailLocal: 'alex.morgan' }, ctx, 'draft')).toEqual([]);
  });
  test('amounts: negative and overflow (AC 25)', () => {
    expect(msg(validateHire({ ...four, housing: '-5' }, ctx, 'draft'), 'housing')).toBe('Amount cannot be negative.');
    expect(msg(validateHire({ ...four, basic: '12345678901' }, ctx, 'draft'), 'basic')).toBe('Enter an amount of 9,999,999,999.99 or less.');
    expect(msg(validateHire({ ...four, basic: 'abc' }, ctx, 'draft'), 'basic')).toBe('Enter the amount as a number.');
    expect(validateHire({ ...four, basic: '12,000.50' }, ctx, 'draft')).toEqual([]);
  });
  test('contract end must be after the start date (CD17), with EN + AR copy', () => {
    const issues = validateHire({ ...four, contractType: 'Fixed term', contractEndDate: '2026-11-01' }, ctx, 'draft');
    expect(msg(issues, 'contractEndDate')).toBe('The contract end date must be after the start date.');
    expect(msg(issues, 'contractEndDate', 'ar')).toBe('يجب أن يكون تاريخ انتهاء العقد بعد تاريخ المباشرة.');
    expect(validateHire({ ...four, contractType: 'Fixed term', contractEndDate: '2027-11-01' }, ctx, 'draft')).toEqual([]);
    expect(keys(validateHire({ ...four, contractType: 'Fixed term', contractEndDate: '2027-02-31' }, ctx, 'draft'))).toEqual(['contractEndDate']);
  });
  test('each issue names its step so the summary can send the user there', () => {
    const issues = validateHire({ ...emptyHireForm(), dob: '2999-01-01', housing: '-1', startDate: '2999-01-01' }, ctx, 'create');
    expect(Object.fromEntries(issues.map(i => [i.field, i.step]))).toMatchObject({ englishName: 1, companyId: 1, startDate: 1, dob: 2, housing: 3 });
  });
});
