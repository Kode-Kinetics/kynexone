import { expect, test } from '@playwright/test';
import {
  EMPTY_RULE_FORM, acknowledgedWarningCodes, effectiveFromOk, todayIso, ruleFormFromViews, ruleFormToChanges, validateRuleForm, warningFor, warningsForForm,
  type PayrollRuleView,
} from '../src/lib/payrollRules';
import { payrollRules } from '../src/i18n/payrollRules';
import { probationOverLimitFromError } from '../src/lib/probationOverLimit';

/** Setup → Payroll rules. Mirrors CompanyPayrollRules.Evaluate: meaningless values stop here, below-floor values warn. */
test('an empty form is valid and warns about nothing: every rule falls back to the platform default', () => {
  expect(validateRuleForm(EMPTY_RULE_FORM)).toEqual({});
  expect(warningsForForm(EMPTY_RULE_FORM)).toEqual([]);
});

test('values at or above the statutory minimum raise no warning', () => {
  expect(warningFor('rest_day_multiplier', '1.5')).toBeNull();
  expect(warningFor('holiday_multiplier', '2')).toBeNull();
  expect(warningFor('leave_cashout_on_exit', 'true')).toBeNull();
  expect(warningFor('max_probation_days', '90')).toBeNull();
});

test('values below a statutory minimum warn instead of being refused', () => {
  expect(warningFor('rest_day_multiplier', '1.25')).toBe('BELOW_OVERTIME_FLOOR');
  expect(warningFor('holiday_multiplier', '1')).toBe('BELOW_OVERTIME_FLOOR');
  expect(warningFor('leave_cashout_on_exit', 'false')).toBe('LEAVE_CASHOUT_OFF');
  expect(warningFor('max_probation_days', '91')).toBe('PROBATION_OVER_90');
  expect(warningFor('max_probation_days', '180')).toBe('PROBATION_OVER_90');
  expect(warningFor('max_probation_days', '181')).toBe('PROBATION_OVER_180');
  expect(validateRuleForm({ ...EMPTY_RULE_FORM, rest_day_multiplier: '1.25', max_probation_days: '200' })).toEqual({});
});

test('meaningless values are refused with the field named', () => {
  const errors = validateRuleForm({
    ...EMPTY_RULE_FORM, rest_day_multiplier: '0.5', holiday_multiplier: '9', max_probation_days: '400', proration_basis: 'Weekly',
  });
  expect(Object.keys(errors).sort()).toEqual(['holiday_multiplier', 'max_probation_days', 'proration_basis', 'rest_day_multiplier']);
  expect(validateRuleForm({ ...EMPTY_RULE_FORM, max_probation_days: '1.5' })).toHaveProperty('max_probation_days');
});

test('only changed rules are sent, and an emptied rule is cleared back to its default', () => {
  const views = [
    { key: 'proration_basis', value: 'CalendarActual', isSet: true },
    { key: 'absence_deduction_base', value: null, isSet: false },
  ] as PayrollRuleView[];
  const initial = ruleFormFromViews(views);
  expect(initial.proration_basis).toBe('CalendarActual');
  expect(ruleFormToChanges({ ...initial, proration_basis: '', absence_deduction_base: 'Gross' }, initial)).toEqual([
    { key: 'proration_basis', value: null },
    { key: 'absence_deduction_base', value: 'Gross' },
  ]);
  expect(ruleFormToChanges(initial, initial)).toEqual([]);
});

test('every Arabic string is translated', () => {
  for (const [key, value] of Object.entries(payrollRules.ar)) expect(value.trim(), key).not.toBe('');
});

test('a probation_over_limit 409 is recognised; other conflicts are not', () => {
  const conflict = { response: { status: 409, data: { error: 'probation_over_limit', message: 'x', days: 120, maxDays: 90 } } };
  expect(probationOverLimitFromError(conflict)?.maxDays).toBe(90);
  expect(probationOverLimitFromError({ response: { status: 409, data: { error: 'possible_duplicate' } } })).toBeNull();
  expect(probationOverLimitFromError({ response: { status: 400, data: { error: 'probation_over_limit' } } })).toBeNull();
});

test('the acknowledgement echoes only the warnings of the rules being changed now', () => {
  const initial = { ...EMPTY_RULE_FORM, leave_cashout_on_exit: 'false' };            // saved earlier, already confirmed
  const form = { ...initial, rest_day_multiplier: '1.25', holiday_multiplier: '1' };
  expect(acknowledgedWarningCodes(form, initial)).toEqual(['holiday_multiplier:BELOW_OVERTIME_FLOOR', 'rest_day_multiplier:BELOW_OVERTIME_FLOOR']);
  expect(acknowledgedWarningCodes(initial, initial)).toEqual([]);
  const now = new Date('2026-10-26T10:00:00Z');
  expect(todayIso(now)).toBe('2026-10-26');
  expect(effectiveFromOk('2026-10-26', now)).toBe(true);
  expect(effectiveFromOk('2026-10-25', now)).toBe(false);
  expect(effectiveFromOk('', now)).toBe(false);
});

test('the probation warning copy has an Arabic translation', () => {
  expect(payrollRules.ar['Keep this probation and save']).toBeTruthy();
  expect(payrollRules.ar['Probation longer than this company allows']).toBeTruthy();
});
