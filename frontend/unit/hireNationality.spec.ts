import { expect, test } from '@playwright/test';
import { buildCountryOptions, defaultIdType, nationalityClass, searchCountries } from '../src/lib/hire/nationality';

const options = buildCountryOptions();

test.describe('nationality picker', () => {
  test('nationalityPickerOrder_sau_سعو: Saudi Arabia is listed first for both scripts', () => {
    expect(searchCountries(options, 'sau', 'en')[0].code).toBe('SA');
    expect(searchCountries(options, 'سعو', 'ar')[0].code).toBe('SA');
    expect(searchCountries(options, 'SAUDI', 'en')[0].code).toBe('SA');
  });
  test('empty query: Saudi pinned, then GCC, then A-Z', () => {
    const codes = searchCountries(options, '', 'en').map(o => o.code);
    expect(codes[0]).toBe('SA');
    expect(codes.slice(1, 6).sort()).toEqual(['AE', 'BH', 'KW', 'OM', 'QA']);
    const rest = searchCountries(options, '', 'en').slice(6).map(o => o.nameEn);
    expect([...rest].sort((a, b) => a.localeCompare(b))).toEqual(rest);
  });
  test('values are ISO 3166 alpha-2 codes', () => {
    expect(options.every(o => /^[A-Z]{2}$/.test(o.code))).toBe(true);
    expect(options.find(o => o.code === 'PK')?.nameAr).toMatch(/[؀-ۿ]/);
  });
  test('free text that is not a country matches nothing', () => {
    expect(searchCountries(options, 'Suadi', 'en')).toEqual([]);
  });
  test('Arabic normalisation: alef and ya variants match', () => {
    expect(searchCountries(options, 'الامارات', 'ar').map(o => o.code)).toContain('AE');
  });
  test('class and default ID type follow nationality', () => {
    expect(nationalityClass('SA')).toBe('saudi');
    expect(nationalityClass('KW')).toBe('gcc');
    expect(nationalityClass('PK')).toBe('expat');
    expect(nationalityClass('')).toBe('unknown');
    expect(defaultIdType('SA')).toBe('nationalId');
    expect(defaultIdType('PK')).toBe('iqama');
    expect(defaultIdType('PK', false)).toBe('passport');
    expect(defaultIdType('KW')).toBe('gccId');
    expect(defaultIdType('')).toBeNull();
  });
});
