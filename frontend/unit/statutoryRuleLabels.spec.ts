import { expect, test } from '@playwright/test';
import { createFormatter } from '../src/lib/format';
import { translate } from '../src/i18n/translations';
import {
  describeStatutoryChange, formatStatutoryValue, fractionToPercent, parseInputValue, percentToFraction,
  shiftDecimal, statutoryRuleName, statutoryUnitFor, toInputValue,
} from '../src/lib/statutoryRuleLabels';

/**
 * lib/statutoryRuleLabels.ts: the unit, name and value formatting behind Setup → Statutory rules.
 * A rate is STORED as a fraction (0.0975 = 9.75%) and payroll multiplies it straight into the wage,
 * so a misread unit moves payroll. Pure functions; no browser.
 */

const en = (k: string, p?: Record<string, string | number>) => translate('en', k, p);
const ar = (k: string, p?: Record<string, string | number>) => translate('ar', k, p);
const fEn = createFormatter({ locale: 'en', timeZone: 'UTC' });
const fAr = createFormatter({ locale: 'ar', timeZone: 'UTC' });
/** Intl puts a no-break space between a currency code and the amount; compare as plain spaces. */
const sp = (s: string) => s.replace(/[  ]/g, ' ').replace(/[‎‏؜⁦-⁩]/g, '');
const show = (key: string, raw: string, dataType = 'decimal', t = en, f = fEn) => {
  const d = formatStatutoryValue(t, f, statutoryUnitFor(key, dataType), raw);
  return { text: sp(d.text), flag: d.flag };
};

test.describe('percent <-> fraction is exact decimal arithmetic', () => {
  const pairs: Array<[string, string]> = [
    ['0.0975', '9.75'], ['0.09', '9'], ['0.0075', '0.75'], ['0.125', '12.5'], ['0.0583', '5.83'], ['0.0833', '8.33'],
    ['0.000001', '0.0001'], ['1.0', '100'], ['0.3', '30'], ['0', '0'], ['0.35', '35'], ['0.07', '7'], ['0.14', '14'],
  ];
  for (const [fraction, percent] of pairs) {
    test(`${fraction} <-> ${percent}%`, () => {
      expect(fractionToPercent(fraction)).toBe(percent);
      const back = percentToFraction(percent)!;
      expect(Number(back)).toBe(Number(fraction));
      // Exactly the stored string (trailing zeros aside), never a float artefact like 0.09750000000000001.
      expect(back).toBe(shiftDecimal(fraction, 0));
    });
  }

  test('float multiplication would drift; the string shift does not', () => {
    expect(0.07 * 100).not.toBe(7); // 7.000000000000001 — the bug this file exists to avoid
    expect(0.14 * 100).not.toBe(14);
    expect(fractionToPercent('0.07')).toBe('7');
    expect(fractionToPercent('0.14')).toBe('14');
    expect(fractionToPercent('0.0975')).toBe('9.75');
    expect(percentToFraction('9.75')).toBe('0.0975');
    expect(percentToFraction('7')).toBe('0.07');
  });

  test('edit round trip: stored -> input -> stored is identity for every seeded rate', () => {
    const unit = statutoryUnitFor('gosi.saudi_employee_rate', 'decimal');
    for (const stored of ['0.0975', '0.09', '0.0075', '0.02', '0.000125', '0.3']) {
      const typed = toInputValue(unit, stored);
      const parsed = parseInputValue(en, unit, typed);
      expect(parsed).toEqual({ ok: true, stored: shiftDecimal(stored, 0) });
    }
  });

  test('shiftDecimal refuses anything that is not a plain decimal', () => {
    for (const bad of ['', '9%', '1,5', 'abc', '.', '1e-3']) expect(shiftDecimal(bad, 2), bad).toBeNull();
    expect(shiftDecimal('.5', 2)).toBe('50');
    expect(shiftDecimal('5.', -2)).toBe('0.05');
  });
});

test.describe('units mirror the backend registry (StatutoryValueUnits.SpecFor)', () => {
  // [key, dataType, expected kind] — every family the seeder writes.
  const cases: Array<[string, string, string]> = [
    ['gosi.saudi_employee_rate', 'decimal', 'fraction'],
    ['gosi.saned_rate', 'decimal', 'fraction'],
    ['gosi.gcc.BH.employee_rate', 'decimal', 'fraction'],
    ['nitaqat.default_target_ratio', 'decimal', 'fraction'],
    ['leave.sick_band2_pay_rate', 'decimal', 'fraction'],
    ['dews.tier1_monthly_rate', 'decimal', 'fraction'],
    ['gosi.covered_wage_ceiling_sar', 'decimal', 'amount'],
    ['nitaqat.counting_wage_floor_sar', 'decimal', 'amount'],
    ['gpssa.contribution_salary_max', 'decimal', 'amount'],
    ['ot.standard_multiplier', 'decimal', 'multiplier'],
    ['lop.monthly_day_divisor', 'decimal', 'divisor'],
    ['leave.annual_base_days', 'decimal', 'days'],
    ['contracts.renewal_lead_days', 'int', 'days'],
    ['leave.event_date_max_lead_days.paternity', 'decimal', 'days'],
    ['leave.annual_tier_threshold_years', 'decimal', 'years'],
    ['ksa.art55.max_total_years', 'int', 'years'],
    ['lop.standard_work_minutes_per_day', 'decimal', 'minutesPerDay'],
    ['workhours.ramadan_minutes_per_week', 'decimal', 'minutesPerWeek'],
    ['ot.standard_monthly_hours', 'decimal', 'hours'],
    ['ksa.art55.max_consecutive_renewals', 'int', 'count'],
    ['eosb.include_transport', 'bool', 'boolean'],
    ['contracts.as_is_requires_qiwa_step', 'bool', 'boolean'],
    ['nitaqat.curve.MANUFACTURING.verified', 'decimal', 'boolean'],
    ['ksa.unified_contract_from', 'string', 'date'],
    ['ot.hourly_base', 'string', 'choice'],
    ['ksa.art55.reading', 'string', 'choice'],
    ['workhours.ramadan_scope', 'string', 'text'],
    ['nitaqat.curve.MANUFACTURING.LowGreen.m', 'decimal', 'unknown'],
    ['some.unrecognised_key', 'decimal', 'unknown'],
  ];
  for (const [key, type, kind] of cases) {
    test(`${key} is ${kind}`, () => expect(statutoryUnitFor(key, type).kind).toBe(kind));
  }

  test('a contribution rate is capped at 30%, any other rate at 100%', () => {
    expect(statutoryUnitFor('gosi.saudi_employee_rate').max).toBe(0.3);
    expect(statutoryUnitFor('nitaqat.default_target_ratio').max).toBe(1);
  });

  test('currencies come from the key or its seed, never a guess', () => {
    expect(statutoryUnitFor('gosi.covered_wage_ceiling_sar')).toMatchObject({ currency: 'SAR', perMonth: true });
    expect(statutoryUnitFor('gpssa.contribution_salary_min')).toMatchObject({ currency: 'AED', perMonth: false });
    expect(statutoryUnitFor('other.contribution_salary_max').currency).toBeUndefined();
  });
});

test.describe('values are shown with their unit', () => {
  test('English', () => {
    expect(show('gosi.saudi_employee_rate', '0.0975')).toEqual({ text: '9.75%', flag: undefined });
    expect(show('gosi.saudi_employee_rate', '0.1')).toEqual({ text: '10.00%', flag: undefined });
    expect(show('gosi.saned_rate', '0.0075').text).toBe('0.75%');
    expect(show('gpssa.national_employer_rate', '0.125').text).toBe('12.50%');
    expect(show('gosi.saudi_employee_rate', '0.000125').text).toBe('0.0125%');
    expect(show('gosi.covered_wage_ceiling_sar', '45000').text).toBe('SAR 45,000 a month');
    expect(show('gpssa.contribution_salary_max', '50000').text).toBe('AED 50,000');
    expect(show('ot.standard_multiplier', '1.5').text).toBe('1.5×');
    expect(show('lop.monthly_day_divisor', '30').text).toBe('30 days');
    expect(show('leave.annual_base_days', '21').text).toBe('21 days');
    expect(show('leave.paternity_days', '1').text).toBe('1 day');
    expect(show('lop.standard_work_minutes_per_day', '480').text).toBe('480 minutes a day');
    expect(show('ot.standard_monthly_hours', '240').text).toBe('240 hours');
    expect(show('ksa.art55.max_total_years', '4', 'int').text).toBe('4 years');
    expect(show('ksa.art55.max_consecutive_renewals', '3', 'int').text).toBe('3 renewals');
    expect(show('eosb.include_transport', 'true', 'bool').text).toBe('Yes');
    expect(show('eosb.exclude_unpaid_leave', 'false', 'bool').text).toBe('No');
    expect(show('nitaqat.curve.MANUFACTURING.verified', '0').text).toBe('No');
    expect(show('ot.hourly_base', 'wage', 'string').text).toBe('Full wage');
    expect(show('ksa.unified_contract_from', '2025-10-06', 'string').text).toContain('2025');
  });

  test('an unknown unit is shown raw, with the flag', () => {
    expect(show('nitaqat.curve.MANUFACTURING.LowGreen.m', '0.0123')).toEqual({ text: '0.0123', flag: 'Unit not specified' });
    expect(show('some.unrecognised_key', '7')).toEqual({ text: '7', flag: 'Unit not specified' });
  });

  test('an unreadable value is shown raw, with a flag, never reinterpreted', () => {
    expect(show('gosi.saudi_employee_rate', '9%')).toEqual({ text: '9%', flag: 'Value not readable' });
    expect(show('eosb.include_transport', 'maybe', 'bool').flag).toBe('Value not readable');
  });

  test('Arabic keeps Latin digits and translates the unit', () => {
    expect(show('gosi.saudi_employee_rate', '0.0975', 'decimal', ar, fAr).text).toBe('9.75%');
    expect(show('leave.annual_base_days', '21', 'decimal', ar, fAr).text).toBe('21 يومًا');
    expect(show('eosb.include_transport', 'true', 'bool', ar, fAr).text).toBe('نعم');
    expect(show('some.unrecognised_key', '7', 'decimal', ar, fAr).flag).toBe('الوحدة غير محددة');
    const amount = show('gosi.covered_wage_ceiling_sar', '45000', 'decimal', ar, fAr).text;
    expect(amount).toContain('45,000');
    expect(amount).toContain('شهري'); // the shared '{amount} a month' key (releaseA/package.ts)
  });
});

test.describe('typing a value', () => {
  const rate = statutoryUnitFor('gosi.saudi_employee_rate');
  test('a percentage is typed as a percentage and stored as a fraction', () => {
    expect(parseInputValue(en, rate, '9.75')).toEqual({ ok: true, stored: '0.0975' });
    expect(parseInputValue(en, rate, '10')).toEqual({ ok: true, stored: '0.1' });
    expect(parseInputValue(en, rate, '٩٫٧٥')).toEqual({ ok: true, stored: '0.0975' });
  });
  test('a percentage above the cap, with a % sign, or too precise is refused', () => {
    expect(parseInputValue(en, rate, '31').ok).toBe(false);
    expect(parseInputValue(en, rate, '9%').ok).toBe(false);
    expect(parseInputValue(en, rate, '9.12345').ok).toBe(false);
    expect(parseInputValue(en, rate, '30')).toEqual({ ok: true, stored: '0.3' });
  });
  test('other units are stored as typed, within their range', () => {
    expect(parseInputValue(en, statutoryUnitFor('nitaqat.counting_wage_floor_sar'), '4000')).toEqual({ ok: true, stored: '4000' });
    expect(parseInputValue(en, statutoryUnitFor('nitaqat.counting_wage_floor_sar'), '4,000').ok).toBe(false);
    expect(parseInputValue(en, statutoryUnitFor('ot.restday_multiplier'), '11').ok).toBe(false);
    expect(parseInputValue(en, statutoryUnitFor('lop.standard_work_minutes_per_day'), '480.5').ok).toBe(false);
    expect(parseInputValue(en, statutoryUnitFor('contracts.as_is_requires_qiwa_step', 'bool'), 'false')).toEqual({ ok: true, stored: 'false' });
    expect(parseInputValue(en, statutoryUnitFor('ot.hourly_base', 'string'), 'gross').ok).toBe(false);
    expect(parseInputValue(en, statutoryUnitFor('nitaqat.curve.X.LowGreen.m'), '0.0123')).toEqual({ ok: true, stored: '0.0123' });
  });
});

test.describe('names and the approval sentence', () => {
  test('plain names, with generated families and the key as the last resort', () => {
    expect(statutoryRuleName(en, 'gosi.saudi_employee_rate')).toBe('GOSI contribution, Saudi employee share');
    expect(statutoryRuleName(en, 'gosi.gcc.BH.employer_rate')).toBe('GOSI for GCC nationals from BH, employer share');
    expect(statutoryRuleName(en, 'nitaqat.curve.MANUFACTURING.LowGreen.m')).toBe('Nitaqat curve gradient (m), Manufacturing, LowGreen');
    expect(statutoryRuleName(en, 'some.unrecognised_key')).toBe('some.unrecognised_key');
    expect(statutoryRuleName(ar, 'gosi.saudi_employee_rate')).toBe('اشتراك التأمينات الاجتماعية، حصة الموظف السعودي');
  });

  test('change, first value, and retirement', () => {
    expect(describeStatutoryChange(en, { kind: 'change', label: 'Overtime pay multiplier', oldText: '1.5×', newText: '1.75×', date: '9 Nov 2026' }))
      .toBe('Change Overtime pay multiplier from 1.5× to 1.75×, starting 9 Nov 2026');
    expect(describeStatutoryChange(en, { kind: 'change', label: 'X', oldText: null, newText: '2×', date: 'D' }))
      .toBe('Set X to 2×, starting D. There is no previous value.');
    expect(describeStatutoryChange(en, { kind: 'retire', label: 'X', oldText: '2×', newText: '1.5×', date: 'D' }))
      .toBe("Stop your company's value for X (2×), starting D. Payroll goes back to the published rate of 1.5×.");
    expect(describeStatutoryChange(ar, { kind: 'change', label: 'س', oldText: '1.5×', newText: '2×', date: 'D' }))
      .toBe('تغيير س من 1.5× إلى 2×، اعتبارًا من D');
  });
});
