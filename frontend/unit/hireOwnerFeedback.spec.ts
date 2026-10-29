import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { emptyHireForm } from '../src/lib/hire/hireForm';
import { reviewWarnings } from '../src/lib/hire/reviewWarnings';
import { SERVER_CODE_KEYS } from '../src/lib/hire/problem';
import { buildCountryOptions, countryOptionsFromApi, searchCountries } from '../src/lib/hire/nationality';

const today = '2026-10-28';
const form = { ...emptyHireForm(), englishName: 'Alex Morgan', companyId: 'c1', startDate: '2026-11-01' };

test.describe('OD8 grade differs from the designation', () => {
  test('the Review step foresees the warning only when the grades differ', () => {
    const dg = { gradeId: 'g1', gradeCode: 'G1' };
    expect(reviewWarnings({ ...form, designationId: 'd1', gradeId: 'g2' }, { today, designationGrade: dg })).toContain('grade_differs_from_designation');
    expect(reviewWarnings({ ...form, designationId: 'd1', gradeId: 'g1' }, { today, designationGrade: dg })).not.toContain('grade_differs_from_designation');
    expect(reviewWarnings({ ...form, gradeId: 'g2' }, { today })).not.toContain('grade_differs_from_designation');
  });
  test('the server code has an English and an Arabic sentence for the confirmation dialog', () => {
    const key = SERVER_CODE_KEYS.grade_differs_from_designation;
    expect(key).toBeTruthy();
    expect(LOCALE_DICTS.en[key]).toBe(key);
    expect(LOCALE_DICTS.ar[key]).toMatch(/[؀-ۿ]/);
  });
});

test.describe('OD7 copy', () => {
  test('the work email hint explains it is the Self-Service sign-in, in English and Arabic', () => {
    const key = 'The employee signs in to Self-Service with this address. Enter the full address IT issued, for example name@company.com.';
    expect(LOCALE_DICTS.en[key]).toBe(key);
    expect(LOCALE_DICTS.ar[key]).toMatch(/[؀-ۿ]/);
  });
});

const fixture = JSON.parse(fs.readFileSync(path.resolve(__dirname, 'fixtures/reference-nationalities.json'), 'utf8')) as Array<{ iso2: string; nameEn: string; nameAr: string; class: string }>;

test.describe('OD10 nationality list', () => {
  test('the real endpoint shape (iso2) becomes picker options with a code for every country', () => {
    const options = countryOptionsFromApi(fixture);
    expect(options.length).toBeGreaterThanOrEqual(245);
    expect(options.every(o => /^[A-Z]{2}$/.test(o.code) && o.nameEn && /[؀-ۿ]/.test(o.nameAr))).toBe(true);
    expect(new Set(options.map(o => o.code)).size).toBe(options.length);
  });
  test('a malformed row is dropped, not turned into an undefined code', () => {
    expect(countryOptionsFromApi([{ nameEn: 'X', nameAr: 'س' }, { iso2: 'PK', nameEn: 'Pakistan', nameAr: 'باكستان' }] as never)).toEqual([{ code: 'PK', nameEn: 'Pakistan', nameAr: 'باكستان' }]);
  });
  test('the built-in fallback is the backend catalog, country for country', () => {
    const fallback = buildCountryOptions();
    expect(fallback.map(o => [o.code, o.nameEn, o.nameAr])).toEqual(fixture.map(f => [f.iso2, f.nameEn, f.nameAr]));
  });
  test('the fixture is the backend catalog (the .cs data rows)', () => {
    const cs = fs.readFileSync(path.resolve(__dirname, '../../backend-dotnet/Zayra.Api/Application/Employees/Onboarding/NationalityCatalogData.cs'), 'utf8');
    const rows = cs.split('Aliases')[0].split('\n').filter(l => /^[A-Z]{2}\|/.test(l)).map(l => l.trim().split('|'));
    expect(rows.map(r => [r[0], r[1], r[2]])).toEqual(fixture.map(f => [f.iso2, f.nameEn, f.nameAr]));
  });
  test('Saudi first, the rest sorted in the display language; Arabic search works', () => {
    const options = buildCountryOptions();
    const en = searchCountries(options, '', 'en');
    expect(en[0].code).toBe('SA');
    const ar = searchCountries(options, '', 'ar');
    expect(ar[0].code).toBe('SA');
    const rest = ar.slice(6).map(o => o.nameAr);
    expect([...rest].sort((a, b) => a.localeCompare(b, 'ar'))).toEqual(rest);
    expect(searchCountries(options, 'الفلبين', 'ar')[0].code).toBe('PH');
    expect(searchCountries(options, 'sri', 'en').map(o => o.code)).toContain('LK');
  });
  test('the picker lists every match, not the first 60', () => {
    const src = fs.readFileSync(path.resolve(__dirname, '../src/views/employees/hire/fields/CountryPicker.tsx'), 'utf8');
    expect(src).not.toMatch(/\.slice\(0,\s*60\)/);
    expect(fs.readFileSync(path.resolve(__dirname, '../src/views/employees/hire/HireWizard.tsx'), 'utf8')).not.toMatch(/code: n\.code/);
  });
});
