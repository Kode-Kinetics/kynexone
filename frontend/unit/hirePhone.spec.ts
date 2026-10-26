import { expect, test } from '@playwright/test';
import { dialCodeFor, exampleMobile, formatNational, mobileCountries, parseMobile } from '../src/lib/hire/phone';

test.describe('phone E.164', () => {
  test('Saudi local 05 number is stored as E.164 and shown nationally', () => {
    const parsed = parseMobile('0512345678', 'SA');
    expect(parsed?.e164).toBe('+966512345678');
    expect(formatNational('+966512345678', 'SA').replace(/\s/g, '')).toBe('0512345678');
  });
  test('a number already in international form wins over the selected country', () => {
    expect(parseMobile('+971501234567', 'SA')?.e164).toBe('+971501234567');
  });
  test('Saudi numbers must be mobile (5xxxxxxxx)', () => {
    expect(parseMobile('0112345678', 'SA')).toBeNull();
  });
  test('company country drives the dial code and the example (A8: no UAE example)', () => {
    expect(dialCodeFor('SA')).toBe('+966');
    expect(exampleMobile('SA')).toMatch(/^05/);
    expect(exampleMobile('AE')).toMatch(/^05/);
    expect(exampleMobile('SA')).not.toContain('971');
    expect(dialCodeFor('PK')).toBe('+92');
  });
  test('the picker offers real countries with dial codes', () => {
    const list = mobileCountries();
    expect(list.find(c => c.code === 'SA')?.dial).toBe('+966');
    expect(list.length).toBeGreaterThan(200);
  });
});
