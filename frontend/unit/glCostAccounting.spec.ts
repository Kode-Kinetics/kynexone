import { expect, test } from '@playwright/test';
import {
  apiErrorCode,
  canRequireCostCentre,
  cleanRule,
  isLockedIntoApprovedRun,
  isPnlType,
  matchesRuleFilter,
  pickServerMessage,
  ruleBlockedForType,
} from '../src/lib/glCostAccounting';

// Cost accounting v1, WP-F. These are the pure rules behind the GL screens; the screens themselves are
// covered by e2e/gl-cost-centres.spec.ts.

test.describe('account type versus cost centre rule', () => {
  test('only income and expense accounts can carry a cost centre (older rows say Revenue)', () => {
    expect(isPnlType('Expense')).toBe(true);
    expect(isPnlType('Income')).toBe(true);
    expect(isPnlType('Revenue')).toBe(true);
    for (const type of ['Asset', 'Liability', 'Equity', '', 'Other']) expect(isPnlType(type), type).toBe(false);
    expect(canRequireCostCentre('Expense')).toBe(true);
    expect(canRequireCostCentre('Liability')).toBe(false);
  });

  test('Required is blocked on asset, liability and equity accounts; Optional and Not allowed never are', () => {
    expect(ruleBlockedForType('Required', 'Asset')).toBe(true);
    expect(ruleBlockedForType('Required', 'Liability')).toBe(true);
    expect(ruleBlockedForType('Required', 'Equity')).toBe(true);
    expect(ruleBlockedForType('Required', 'Expense')).toBe(false);
    expect(ruleBlockedForType('Optional', 'Liability')).toBe(false);
    expect(ruleBlockedForType('NotAllowed', 'Asset')).toBe(false);
  });

  test('a row from an older server has no rule and reads as Optional, the migration default', () => {
    expect(cleanRule(undefined)).toBe('Optional');
    expect(cleanRule(null)).toBe('Optional');
    expect(cleanRule('Required')).toBe('Required');
    expect(cleanRule('NotAllowed')).toBe('NotAllowed');
    expect(cleanRule('Sometimes')).toBe('Optional');
  });

  test('the rule filter keeps everything for All rules and compares the cleaned rule otherwise', () => {
    expect(matchesRuleFilter({ costCenterRule: 'Required' }, '')).toBe(true);
    expect(matchesRuleFilter({ costCenterRule: 'Required' }, 'Required')).toBe(true);
    expect(matchesRuleFilter({ costCenterRule: 'Required' }, 'Optional')).toBe(false);
    expect(matchesRuleFilter({}, 'Optional')).toBe(true);
  });
});

test.describe('which sentence the user reads (AM-5)', () => {
  const body = { message: 'Enter a reason for this change.', messageAr: 'أدخل سببًا لهذا التغيير.' };

  test('Arabic prefers messageAr, English prefers message', () => {
    expect(pickServerMessage(body, 'ar')).toBe('أدخل سببًا لهذا التغيير.');
    expect(pickServerMessage(body, 'en')).toBe('Enter a reason for this change.');
  });

  test('either side falls back to the other, and blank or missing text is null so the caller can describe the error', () => {
    expect(pickServerMessage({ message: 'English only' }, 'ar')).toBe('English only');
    expect(pickServerMessage({ messageAr: 'عربي فقط' }, 'en')).toBe('عربي فقط');
    expect(pickServerMessage({ message: '  ', messageAr: '' }, 'ar')).toBeNull();
    expect(pickServerMessage(undefined, 'ar')).toBeNull();
    expect(pickServerMessage('<html>', 'ar')).toBeNull();
  });
});

test.describe('server refusals', () => {
  const refusal = (status: number, data: unknown) => ({ response: { status, data } });

  test('the error code comes from code or error', () => {
    expect(apiErrorCode(refusal(409, { code: 'locked_into_approved_run' }))).toBe('locked_into_approved_run');
    expect(apiErrorCode(refusal(409, { error: 'cost_accounting_blockers' }))).toBe('cost_accounting_blockers');
    expect(apiErrorCode(new Error('network'))).toBeNull();
  });

  test('M-7 is a 409 with locked_into_approved_run and nothing else', () => {
    expect(isLockedIntoApprovedRun(refusal(409, { code: 'locked_into_approved_run' }))).toBe(true);
    expect(isLockedIntoApprovedRun(refusal(409, { code: 'cost_accounting_has_costed_runs' }))).toBe(false);
    expect(isLockedIntoApprovedRun(refusal(422, { code: 'locked_into_approved_run' }))).toBe(false);
    expect(isLockedIntoApprovedRun(undefined)).toBe(false);
  });
});
