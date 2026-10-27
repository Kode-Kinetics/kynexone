import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { emptyHireForm } from '../src/lib/hire/hireForm';
import { reviewItemLabel } from '../src/lib/hire/reviewLabels';
import { reviewWarnings } from '../src/lib/hire/reviewWarnings';
import { formatIsoDate } from '../src/lib/hire/dates';
import { bankNameFor, ibanBankCode } from '../src/lib/hire/iban';

const tIn = (loc: 'en' | 'ar') => (k: string, p?: Record<string, string | number>) => {
  let s = LOCALE_DICTS[loc][k] ?? k;
  for (const [n, v] of Object.entries(p ?? {})) s = s.replace(`{${n}}`, String(v));
  return s;
};
const hasArabic = (s: string) => /[؀-ۿ]/.test(s);

test.describe('CD34 raw hire-review keys render through t() in EN and AR', () => {
  for (const key of ['data:joining_backdated', 'pay:compensationUnapproved', 'pay:bankUnverified', 'pay:ibanMissing', 'pay:salaryMissing', 'pay:molIdMissing', 'pay:payrollProfileMissing']) {
    test(key, () => {
      const item = { key, label: key };
      const en = reviewItemLabel(item, tIn('en'));
      const ar = reviewItemLabel(item, tIn('ar'));
      expect(en).not.toBe(key);
      expect(en).not.toMatch(/^(pay|data):/);
      expect(hasArabic(ar), ar).toBe(true);
    });
  }
  test('data:joining_backdated reads as the backdated-start sentence', () => {
    expect(reviewItemLabel({ key: 'data:joining_backdated', label: '' }, tIn('en'))).toBe('The start date is more than 30 days in the past.');
  });
  test('an unknown pay: or data: key never shows raw in Arabic', () => {
    const ar = reviewItemLabel({ key: 'pay:somethingNew', label: 'pay:somethingNew' }, tIn('ar'));
    expect(ar).not.toMatch(/^pay:/);
    expect(hasArabic(ar)).toBe(true);
    const ar2 = reviewItemLabel({ key: 'data:somethingNew', label: '' }, tIn('ar'));
    expect(hasArabic(ar2)).toBe(true);
  });
  test('a key the catalog does not know still falls back to the server label', () => {
    expect(reviewItemLabel({ key: 'SomeFutureKey', label: 'Some future label' }, tIn('ar'))).toBe('Some future label');
  });
});

test.describe('CD34 Review warnings list (the same statutory checks the server will ask about)', () => {
  const today = '2026-10-27';
  const base = { ...emptyHireForm(), englishName: 'Alex Morgan', companyId: 'c1', startDate: '2026-11-01' };
  test('a clean form has none', () => { expect(reviewWarnings(base, { today })).toEqual([]); });
  test('backdated by more than 30 days and further than 180 days ahead', () => {
    expect(reviewWarnings({ ...base, startDate: '2026-08-01' }, { today })).toContain('joining_backdated_over_30d');
    expect(reviewWarnings({ ...base, startDate: '2026-09-27' }, { today })).not.toContain('joining_backdated_over_30d');
    expect(reviewWarnings({ ...base, startDate: '2027-05-30' }, { today })).toContain('joining_over_180d_future');
  });
  test('probation longer than 180 days', () => {
    expect(reviewWarnings({ ...base, probationEndDate: '2027-06-30' }, { today })).toContain('probation_over_limit');
    expect(reviewWarnings({ ...base, probationEndDate: '2027-02-01' }, { today })).not.toContain('probation_over_limit');
  });
  test('age at the start date: under 15, under 18, over 70', () => {
    expect(reviewWarnings({ ...base, dob: '2015-01-01' }, { today })).toContain('under_minimum_age');
    expect(reviewWarnings({ ...base, dob: '2010-01-01' }, { today })).toContain('juvenile_15_17');
    expect(reviewWarnings({ ...base, dob: '1950-01-01' }, { today })).toContain('age_over_70');
    expect(reviewWarnings({ ...base, dob: '1990-01-01' }, { today })).toEqual([]);
  });
  test('an identity document that has expired or expires within 90 days', () => {
    expect(reviewWarnings({ ...base, idExpiry: '2026-10-01' }, { today })).toContain('id_expired');
    expect(reviewWarnings({ ...base, idExpiry: '2026-12-15' }, { today })).toContain('id_expiring_90d');
    expect(reviewWarnings({ ...base, idExpiry: '2028-01-01' }, { today })).toEqual([]);
    expect(reviewWarnings({ ...base, passportExpiry: '2026-10-01' }, { today })).toContain('id_expired');
  });
});

test.describe('CD34 localised dates', () => {
  test('English is not an ISO string', () => {
    const s = formatIsoDate('2026-10-14', 'en');
    expect(s).not.toBe('2026-10-14');
    expect(s).toContain('2026');
    expect(s).toMatch(/October/);
  });
  test('Arabic uses the Arabic month with Latin digits', () => {
    const s = formatIsoDate('2026-10-14', 'ar');
    expect(hasArabic(s)).toBe(true);
    expect(s).toContain('2026');
    expect(s).not.toMatch(/[٠-٩]/);
  });
  test('an impossible date returns empty', () => { expect(formatIsoDate('2026-02-30', 'en')).toBe(''); });
});

test.describe('CD29 bank name from the SAMA code', () => {
  const banks = [{ code: '80', nameEn: 'Al Rajhi Bank', nameAr: 'مصرف الراجحي' }, { code: '10', nameEn: 'The National Commercial Bank', nameAr: 'البنك الأهلي التجاري' }];
  test('IBAN characters 5 and 6', () => {
    expect(ibanBankCode('SA03 8000 0000 6080 1016 7519')).toBe('80');
    expect(ibanBankCode('AE070331234567890123456')).toBe('');
    expect(ibanBankCode('SA03')).toBe('');
  });
  test('the name follows the viewer language; unknown code and no list give nothing', () => {
    expect(bankNameFor('SA0380000000608010167519', banks, 'en')).toBe('Al Rajhi Bank');
    expect(bankNameFor('SA0380000000608010167519', banks, 'ar')).toBe('مصرف الراجحي');
    expect(bankNameFor('SA0399000000608010167519', banks, 'en')).toBe('');
    expect(bankNameFor('SA0380000000608010167519', null, 'en')).toBe('');
    expect(bankNameFor('AE070331234567890123456', banks, 'en')).toBe('');
  });
});
