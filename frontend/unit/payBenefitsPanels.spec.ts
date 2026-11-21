import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { payBenefitsPanels } from '../src/lib/payBenefitsPanels';

/**
 * The Pay & Benefits section of an employee record must let HR reach the package, the benefits and the deductions:
 * the panels exist, so the section lists them as links (nothing was reachable without a hand-typed address before).
 */
test.describe('Pay & Benefits panels', () => {
  test('a Release A workspace offers payroll, package and deductions', () => {
    expect(payBenefitsPanels({ releaseA: true, canReadBenefits: true }).map((p) => p.id)).toEqual(['payroll', 'package', 'deductions']);
  });

  test('without Release A the benefits panel is offered to a reader, and only to a reader', () => {
    expect(payBenefitsPanels({ releaseA: false, canReadBenefits: true }).map((p) => p.id)).toEqual(['payroll', 'benefits']);
    expect(payBenefitsPanels({ releaseA: false, canReadBenefits: false }).map((p) => p.id)).toEqual(['payroll']);
  });

  test('every panel label has an Arabic translation', () => {
    const ar = LOCALE_DICTS.ar as Record<string, string>;
    for (const panel of payBenefitsPanels({ releaseA: true, canReadBenefits: true })) {
      expect(ar[panel.label], panel.label).toBeTruthy();
    }
  });
});
