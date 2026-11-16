import { expect, test } from '@playwright/test';
import { translate, type MessageParams } from '../src/i18n/translations';
import { findingText } from '../src/lib/validationFindingText';
import { payItemLabel } from '../src/lib/payItemLabel';
import { validateRuleForm, EMPTY_RULE_FORM } from '../src/lib/payrollRules';

const ar = (k: string, p?: MessageParams) => translate('ar', k, p);
const latinWords = (s: string) => s.replace(/[⁦-⁩]/g, '').match(/[A-Za-z]{4,}/g) ?? [];

test.describe('held pay and unpaid-leave arrears copy', () => {
  test('the arrears and reversal payslip lines read in Arabic and keep the month as written', () => {
    for (const label of ['Unpaid leave arrears (2026-01)', 'Leave deduction reversal (2026-02)']) {
      const text = payItemLabel(ar, label);
      expect(latinWords(text), text).toEqual([]);
      expect(text).toContain('2026-0');
    }
  });

  test('the new run findings read in Arabic with no English left over', () => {
    for (const code of ['HELD_PAY_RELEASE_NOT_PAID', 'LEAVE_ARREARS_CARRIED_FORWARD']) {
      const text = findingText(ar, { code, message: 'Employee PN0001 (Someone): English sentence from the server.' });
      expect(latinWords(text), code).toEqual([]);
    }
  });

  test('the arrears cap can only be lowered: 1 to 50', () => {
    for (const ok of ['1', '20', '50']) expect(validateRuleForm({ ...EMPTY_RULE_FORM, arrears_deduction_cap_pct: ok })).toEqual({});
    for (const bad of ['0', '51', '100', 'half'])
      expect(validateRuleForm({ ...EMPTY_RULE_FORM, arrears_deduction_cap_pct: bad }).arrears_deduction_cap_pct).toBeTruthy();
  });
});
