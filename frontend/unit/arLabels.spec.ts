import { expect, test } from '@playwright/test';
import { createFormatter } from '../src/lib/format';
import { enumLabel } from '../src/i18n/enumLabel';
import { translate, type MessageParams } from '../src/i18n/translations';
import { languageLabel, yearsLabel } from '../src/lib/displayLabels';

const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);
const arabic = /[؀-ۿ]/;

test.describe('currency label', () => {
  test('Arabic shows the Arabic currency symbol, English the code', () => {
    expect(createFormatter({ locale: 'ar' }).currencyLabel('SAR')).toBe('ر.س.');
    expect(createFormatter({ locale: 'en' }).currencyLabel('SAR')).toBe('SAR');
    expect(createFormatter({ locale: 'ar' }).currencyLabel('')).toBe('');
    expect(createFormatter({ locale: 'ar' }).currencyLabel('XYZ')).toBe('XYZ');
  });

  test('a salary range carries the currency once and uses the shared number formatter', () => {
    const f = createFormatter({ locale: 'ar' });
    expect(f.moneyRange(5000, 8000, 'SAR')).toBe(`5,000–8,000 ${f.currencyLabel('SAR')}`);
    expect(createFormatter({ locale: 'en' }).moneyRange(5000.5, 8000, 'SAR')).toBe('SAR 5,000.50–8,000.00');
  });

  test('an hourly rate is formatted, not printed raw', () => {
    expect(createFormatter({ locale: 'en' }).plain.number(168.75, { minimumFractionDigits: 2, maximumFractionDigits: 2 })).toBe('168.75');
  });
});

test.describe('small labels', () => {
  test('years of experience are a translated, pluralised phrase', () => {
    expect(yearsLabel(en, 3)).toBe('3 yrs');
    expect(yearsLabel(en, 1)).toBe('1 yr');
    expect(yearsLabel(ar, 3)).toBe('3 سنوات');
    expect(yearsLabel(ar, 2)).toBe('سنتان');
    expect(yearsLabel(ar, 11)).toBe('11 سنة');
  });

  test('payslip language shows a name, not a code', () => {
    expect(languageLabel(en, 'en')).toBe('English');
    expect(languageLabel(ar, 'EN')).toBe('الإنجليزية');
    expect(languageLabel(ar, 'ar')).toBe('العربية');
    expect(languageLabel(ar, 'xx')).toBe('XX');
  });

  test('interview recommendations are translated', () => {
    for (const v of ['StrongHire', 'Hire', 'Hold', 'Reject', 'Maybe', 'Shortlist', 'NoHire']) {
      expect(enumLabel(ar, 'InterviewRecommendation', v), v).toMatch(arabic);
    }
    expect(enumLabel(en, 'InterviewRecommendation', 'StrongHire')).toBe('Strong hire');
  });
});

test.describe('period labels', () => {
  test('a YYYY-MM month is formatted as a month name, not passed through', () => {
    expect(createFormatter({ locale: 'ar' }).period('2026-11', 'long')).toBe(createFormatter({ locale: 'ar' }).period('Nov 2026', 'long'));
    expect(createFormatter({ locale: 'en' }).period('2026-11', 'long')).toBe('November 2026');
  });
});

test.describe('plurals and group names', () => {
  const key = '{count, plural, one {# package needs another reviewer because you are not independent of it.} other {# packages need another reviewer because you are not independent of them.}}';
  test('one package is singular in English and Arabic', () => {
    expect(en(key, { count: 1 })).toBe('1 package needs another reviewer because you are not independent of it.');
    expect(en(key, { count: 3 })).toBe('3 packages need another reviewer because you are not independent of them.');
    expect(ar(key, { count: 1 })).toContain('حزمة واحدة');
    expect(ar(key, { count: 2 })).toContain('حزمتان');
    expect(ar(key, { count: 5 })).toContain('5 حزم');
  });

  test('Saudization group names read in Arabic', () => {
    expect(enumLabel(ar, 'NitaqatGroup', 'NonSaudi')).toBe('غير سعودي');
    expect(enumLabel(ar, 'NitaqatGroup', 'FullTime')).toBe('دوام كامل');
    expect(enumLabel(en, 'NitaqatGroup', 'NonSaudi')).toBe('Non-Saudi');
  });
});
