import { test, expect } from '@playwright/test';
import { countryRuleRemovalEffect } from '../src/lib/countryRuleEffects';

test('weekend_days names its effect on the work week', () => {
  expect(countryRuleRemovalEffect('weekend_days')).toContain('work week');
  expect(countryRuleRemovalEffect(' Weekend_Days ')).toContain('work week');
});

test('other keys say they do not change payroll', () => {
  expect(countryRuleRemovalEffect('min_wage')).toContain('does not change payroll');
});
