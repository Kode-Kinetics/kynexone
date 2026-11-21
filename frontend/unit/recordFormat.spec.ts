import { expect, test } from '@playwright/test';
import { createFormatter } from '../src/lib/format';
import { translate, type MessageParams } from '../src/i18n/translations';
import { describeChange, humaniseEventName, humaniseFieldList, recordDate, recordDateTime, looksLikeIso } from '../src/lib/recordFormat';

/**
 * The one formatting layer of the employee record: no raw ISO string, PascalCase event name or camelCase field name
 * reaches the screen, in English or Arabic. Pure functions; no browser.
 */
const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);
const fxEn = createFormatter({ locale: 'en', timeZone: 'Asia/Riyadh' });
const fxAr = createFormatter({ locale: 'ar', timeZone: 'Asia/Riyadh' });
const arabic = /[؀-ۿ]/;

test.describe('recordFormat: dates', () => {
  test('a calendar date or a midnight-UTC instant reads as 31 Oct 2026, whatever the viewer zone', () => {
    expect(recordDate(fxEn, '2026-10-31')).toBe('31 Oct 2026');
    expect(recordDate(fxEn, '2026-10-31T00:00:00Z')).toBe('31 Oct 2026');
    expect(recordDate(fxEn, '2026-10-31T00:00:00.0000000')).toBe('31 Oct 2026');
    // A zone west of UTC must not move the day.
    const west = createFormatter({ locale: 'en', timeZone: 'America/Los_Angeles' });
    expect(recordDate(west, '2026-10-31T00:00:00Z')).toBe('31 Oct 2026');
  });

  test('an instant with a time reads as a date and a clock time in the tenant zone', () => {
    const out = recordDateTime(fxEn, '2026-11-13T22:33:21.307121Z');
    expect(out).toBe('14 Nov 2026, 01:33');
    expect(looksLikeIso(out)).toBe(false);
  });

  test('Arabic keeps Latin digits and the Arabic month, never the raw string', () => {
    const date = recordDate(fxAr, '2026-10-31T00:00:00Z');
    expect(date).toMatch(arabic);
    expect(looksLikeIso(date)).toBe(false);
    expect(recordDateTime(fxAr, '2026-11-13T22:33:21.307121Z')).toMatch(arabic);
  });

  test('empty or unreadable values are an em dash or the text as given, never a crash', () => {
    expect(recordDate(fxEn, '')).toBe('—');
    expect(recordDate(fxEn, undefined)).toBe('—');
    expect(recordDate(fxEn, 'not a date')).toBe('not a date');
  });

  test('looksLikeIso finds the raw shapes only', () => {
    expect(looksLikeIso('2026-11-13T22:33:21.307121Z')).toBe(true);
    expect(looksLikeIso('2026-11-13T22:33')).toBe(true);
    expect(looksLikeIso('31 Oct 2026')).toBe(false);
  });
});

test.describe('recordFormat: event and field names', () => {
  test('a PascalCase event name is split into words', () => {
    expect(humaniseEventName(en, 'SensitiveChangeApproved')).toBe('Sensitive change approved');
    expect(humaniseEventName(en, 'StatusChanged')).toBe('Status changed');
    expect(humaniseEventName(en, 'payroll.wps.generated')).toBe('Payroll WPS generated');
    expect(humaniseEventName(en, '')).toBe('—');
  });

  test('a known event reads in Arabic and an unknown one is still words, not a code', () => {
    expect(humaniseEventName(ar, 'SensitiveChangeApproved')).toMatch(arabic);
    expect(humaniseEventName(ar, 'SomeBrandNewEvent')).not.toMatch(/[a-z][A-Z]/);
  });

  test('a field list is split into words and joined with a comma', () => {
    expect(humaniseFieldList(en, 'bankName,accountNumber')).toBe('Bank name, account number');
    expect(humaniseFieldList(en, 'wpsBankDetails')).toBe('WPS bank details');
    expect(humaniseFieldList(en, 'iqamaNumber,idNumber')).toBe('Iqama number, ID number');
    expect(humaniseFieldList(en, ' ')).toBe('');
  });

  test('Arabic joins a field list with the Arabic comma', () => {
    expect(humaniseFieldList(ar, 'bankName,accountNumber')).toContain('،');
  });
});

test.describe('recordFormat: one history row', () => {
  const row = (fieldName: string, oldValue: string | null, newValue: string | null) => ({ eventType: 'FieldChanged', fieldName, oldValue, newValue });

  test('both sides empty hides the arrow and says the details are withheld', () => {
    const out = describeChange(en, fxEn, row('iqamaNumber,idNumber', '', ''));
    expect(out.field).toBe('Iqama number, ID number');
    expect(out.detail).toBe('Details withheld');
    expect(`${out.field} ${out.detail}`).not.toContain('→');
  });

  test('a real change shows old to new, with dates formatted', () => {
    const out = describeChange(en, fxEn, row('joiningDate', '2026-10-17', '2026-10-31T00:00:00Z'));
    expect(out.detail).toBe('17 Oct 2026 → 31 Oct 2026');
  });

  test('an empty side reads as an em dash only when the other side has a value', () => {
    expect(describeChange(en, fxEn, row('department', '', 'QA Finance')).detail).toBe('— → QA Finance');
  });

  test('a row with no field name still has a title and no stray colon', () => {
    const out = describeChange(en, fxEn, row('', null, null));
    expect(out.field).toBe('');
    expect(out.detail).toBe('Details withheld');
  });
});
