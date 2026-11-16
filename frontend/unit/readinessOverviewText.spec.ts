import { expect, test } from '@playwright/test';
import { readinessItemText, readinessReasonText } from '../src/lib/readinessOverviewText';
import { translate } from '../src/i18n/translations';

const en = (key: string, params?: Record<string, string | number | null | undefined>) => translate('en', key, params);
const ar = (key: string, params?: Record<string, string | number | null | undefined>) => translate('ar', key, params);
const latin = (s: string) => s.match(/[A-Za-z]{4,}/g) ?? [];

// The new-hire readiness overview printed the API's raw reason token ("statutory") and English labels under Arabic.
test.describe('readiness overview text', () => {
  for (const token of ['statutory', 'approval', 'recommended', 'expired', 'invalid', 'provisional', 'not_evaluable']) {
    test(`reason token ${token} is a sentence in both languages, never the token`, () => {
      const english = readinessReasonText(en, token);
      const arabic = readinessReasonText(ar, token);
      expect(english).not.toBe(token);
      expect(arabic).not.toBe(english);
      expect(latin(arabic ?? ''), arabic ?? '').toEqual([]);
    });
  }

  test('a sentence from the server is looked up as written, and nothing becomes null', () => {
    expect(readinessReasonText(en, 'Some sentence the server wrote')).toBe('Some sentence the server wrote');
    expect(readinessReasonText(en, undefined)).toBeNull();
    expect(readinessReasonText(en, '')).toBeNull();
  });

  test('item labels read in Arabic from the key, not the English label', () => {
    const items = [
      { key: 'BankIban', label: 'IBAN (WPS)' },
      { key: 'QiwaContractNumber', label: 'Qiwa contract number' },
      { key: 'GosiReference', label: 'GOSI reference' },
      { key: 'SalaryStructure', label: 'Salary' },
      { key: 'SalaryPackageApproval', label: 'Salary package waiting for Finance approval — it must be approved by a different person before this employee can be activated' },
    ];
    for (const item of items) {
      expect(readinessItemText(en, item), item.key).toBe(item.label);
      const arabic = readinessItemText(ar, item);
      expect(arabic, item.key).not.toBe(item.label);
      expect(latin(arabic.replace(/WPS|GOSI|IBAN/g, '')), arabic).toEqual([]);
    }
  });

  test('the overview chrome is in Arabic', () => {
    for (const key of ['Employee overview', 'Recommended next action', 'Guidance is based on the employee’s current readiness and access state.', 'Open requirements', 'Record summary',
      'Not provided', 'Nothing requires attention.', 'No readiness blockers are currently reported.', 'Profile completeness', 'Work location', 'Complete {item}',
      'This is the highest-priority open requirement.', 'Complete this requirement to keep the employee record ready.']) {
      expect(ar(key, { item: 'x' }), key).not.toBe(key);
    }
  });
});
