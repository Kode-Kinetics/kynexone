import { expect, test } from '@playwright/test';
import { createFormatter, dateLocale, numberLocale } from '../src/lib/format';
import { formatMessage, messageArgs } from '../src/i18n/message';
import { translate } from '../src/i18n/translations';

/**
 * lib/format.ts and i18n/message.ts: the shared formatter and the placeholder/plural engine
 * behind `t(key, params)`. Pure functions; no browser.
 */

const LATIN_ONLY = /^[^٠-٩۰-۹]*$/; // no Arabic-Indic or Persian digits
/** Intl puts a no-break space between a currency code and the amount; compare as plain spaces. */
const sp = (s: string) => s.replace(/\u00a0/g, ' ');
const SEP_15 = new Date(Date.UTC(2026, 8, 15, 9, 30));

test.describe('lib/format', () => {
  test('Arabic keeps Latin digits for numbers, amounts and dates (CTO decision)', () => {
    const f = createFormatter({ locale: 'ar', timeZone: 'Asia/Riyadh' });
    for (const s of [f.number(1234567.5), f.money(1250, 'SAR'), f.percent(45.6), f.date(SEP_15, 'long'), f.hijri(SEP_15), f.time(SEP_15)]) {
      expect(s, s).toMatch(LATIN_ONLY);
      expect(s, s).toMatch(/\d/);
    }
    expect(f.number(1234567.5)).toBe('1,234,567.5');
    expect(numberLocale('ar')).toBe('ar-SA-u-nu-latn');
  });

  test('Gregorian is explicit, so an ar-SA browser default never turns dates Hijri', () => {
    expect(dateLocale('ar')).toBe('ar-SA-u-ca-gregory-nu-latn');
    const f = createFormatter({ locale: 'ar', timeZone: 'UTC' });
    expect(f.date(SEP_15, 'long')).toContain('2026');
    expect(f.date(SEP_15, 'long')).toContain('سبتمبر');
  });

  test('money never invents a currency', () => {
    const f = createFormatter({ locale: 'en' });
    expect(f.money(1250)).toBe('1,250.00');
    expect(sp(f.money(1250, 'SAR'))).toBe('SAR 1,250.00');
    expect(sp(f.money(1250, 'SAR', { decimals: 0 }))).toBe('SAR 1,250');
    expect(f.money(null, 'SAR')).toBe('—');
    expect(sp(createFormatter({ locale: 'en', currency: 'SAR' }).money(5))).toBe('SAR 5.00');
    // Arabic uses the local symbol, still with Latin digits.
    const ar = createFormatter({ locale: 'ar' }).money(1250, 'SAR');
    expect(ar).toContain('1,250.00');
    expect(ar).toContain('ر.س');
  });

  test('compact money reads 1.25M / 12.3K, labelled only when a currency is known', () => {
    const f = createFormatter({ locale: 'en' });
    expect(sp(f.moneyCompact(1_250_000, 'SAR'))).toBe('SAR 1.25M');
    expect(sp(f.moneyCompact(12_340, 'SAR'))).toBe('SAR 12.3K');
    expect(f.moneyCompact(950, null)).toBe('950');
  });

  test('the tenant date pattern drives short dates', () => {
    const d = new Date(Date.UTC(2026, 0, 5, 12));
    expect(createFormatter({ locale: 'en', timeZone: 'UTC', dateFormat: 'DD/MM/YYYY' }).date(d, 'short')).toBe('05/01/2026');
    expect(createFormatter({ locale: 'en', timeZone: 'UTC', dateFormat: 'MM/DD/YYYY' }).date(d, 'short')).toBe('01/05/2026');
    expect(createFormatter({ locale: 'en', timeZone: 'UTC', dateFormat: 'YYYY-MM-DD' }).date(d, 'short')).toBe('2026-01-05');
    expect(createFormatter({ locale: 'ar', timeZone: 'UTC', dateFormat: 'DD/MM/YYYY' }).date(d, 'short')).toBe('05/01/2026');
  });

  test('a Hijri tenant leads with Umm al-Qura and shows Gregorian alongside when dual dates are on', () => {
    const hijriFirst = createFormatter({ locale: 'en', timeZone: 'UTC', calendarSystem: 'Hijri', hijriDatesEnabled: true });
    const both = hijriFirst.dateBoth(SEP_15, 'long');
    expect(both).toMatch(/AH/);
    expect(both).toContain('(15 September 2026)');
    const gregFirst = createFormatter({ locale: 'en', timeZone: 'UTC', hijriDatesEnabled: true });
    expect(gregFirst.dateBoth(SEP_15, 'long')).toMatch(/^15 September 2026 \(.+AH\)$/);
    expect(createFormatter({ locale: 'en', timeZone: 'UTC' }).dateBoth(SEP_15, 'long')).toBe('15 September 2026');
  });

  test('a bare calendar date does not slip a day west of UTC', () => {
    const f = createFormatter({ locale: 'en', dateFormat: 'YYYY-MM-DD' });
    expect(f.date('2026-03-01', 'short')).toBe('2026-03-01');
  });

  test('API period labels are localised', () => {
    expect(createFormatter({ locale: 'en' }).period('Sep 2026')).toBe('September 2026');
    expect(createFormatter({ locale: 'ar' }).period('Sep 2026')).toBe('سبتمبر 2026');
    expect(createFormatter({ locale: 'en' }).period('Sep', 'short')).toMatch(/^Sept?$/);
    expect(createFormatter({ locale: 'en' }).period('FY 2026')).toBe('FY 2026');
  });

  test('time is 24-hour in the tenant zone', () => {
    expect(createFormatter({ locale: 'en', timeZone: 'Asia/Riyadh' }).time(SEP_15)).toBe('12:30');
  });

  test('missing values render as an em dash, never "Invalid Date" or NaN', () => {
    const f = createFormatter({ locale: 'en' });
    expect(f.date(null)).toBe('—');
    expect(f.date('not a date')).toBe('—');
    expect(f.number(Number.NaN)).toBe('—');
  });
});

test.describe('i18n/message', () => {
  const fmt = (n: number) => n.toLocaleString('en-US');

  test('fills named placeholders and leaves a missing one visible', () => {
    expect(formatMessage('Due in {time}', { time: '2 days' }, 'en', fmt)).toBe('Due in 2 days');
    expect(formatMessage('Due in {time}', {}, 'en', fmt)).toBe('Due in {time}');
  });

  test('English plurals', () => {
    const tpl = '{count, plural, one {# employee is missing a document} other {# employees are missing a document}}';
    expect(formatMessage(tpl, { count: 1 }, 'en', fmt)).toBe('1 employee is missing a document');
    expect(formatMessage(tpl, { count: 1200 }, 'en', fmt)).toBe('1,200 employees are missing a document');
  });

  test('Arabic plurals pick among six forms, with =N exact matches', () => {
    const tpl = '{count, plural, =0 {لا موظفين} one {موظف واحد} two {موظفان} few {# موظفين} many {# موظفًا} other {# موظف}}';
    expect(formatMessage(tpl, { count: 0 }, 'ar', fmt)).toBe('لا موظفين');
    expect(formatMessage(tpl, { count: 2 }, 'ar', fmt)).toBe('موظفان');
    expect(formatMessage(tpl, { count: 3 }, 'ar', fmt)).toBe('3 موظفين');
    expect(formatMessage(tpl, { count: 11 }, 'ar', fmt)).toBe('11 موظفًا');
    expect(formatMessage(tpl, { count: 100 }, 'ar', fmt)).toBe('100 موظف');
  });

  test('placeholders inside plural branches are filled and reported', () => {
    const tpl = '{count, plural, one {# request for {name}} other {# requests for {name}}}';
    expect(formatMessage(tpl, { count: 2, name: 'Sara' }, 'en', fmt)).toBe('2 requests for Sara');
    expect([...messageArgs(tpl)].sort()).toEqual(['count', 'name']);
  });

  test('text that merely contains braces is left alone', () => {
    expect(formatMessage('Use {curly} and { not an arg }', { curly: 'x' }, 'en', fmt)).toBe('Use x and { not an arg }');
  });

  test('translate() formats numbers with Latin digits in Arabic and ignores non-object params', () => {
    expect(translate('ar', 'nonexistent key {n}', { n: 12345 })).toBe('nonexistent key 12,345');
    // items.map(t) passes an index as the 2nd argument; it must not be treated as params.
    expect(translate('en', 'Plain {x}', 3 as unknown as Record<string, number>)).toBe('Plain {x}');
  });
});
