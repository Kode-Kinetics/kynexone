import { test, expect } from '@playwright/test';
import { paymentReadinessHeadline, type PaymentPrerequisites } from '../src/lib/payrollPrerequisites';
import { translate } from '../src/i18n/translations';

// Readiness drift on a run that is already Locked or Paid cannot block that run's payment: the dashboard
// must not show it as "BLOCKS PAYMENT". It is a note on records to fix before next month.
const prerequisites = (over: Partial<PaymentPrerequisites> = {}): PaymentPrerequisites => ({
  evaluatedEmployees: 9, blockedEmployees: 2, employeesWithRecommendations: 0, attendanceChecked: true,
  companyBlocking: [], companyRecommended: [],
  blocking: [{ code: 'STATUTORY_MISSING', label: 'Required statutory details are missing or expired', nextAction: 'Open the employee', count: 2 }],
  recommended: [], employees: [], employeesTruncated: false, ...over,
});

test.describe('payment readiness on a settled run', () => {
  for (const status of ['Locked', 'Paid']) {
    test(`a ${status} run with unpaid-ready gaps reads as a note for next month, not a block`, () => {
      const h = paymentReadinessHeadline(prerequisites(), status);
      expect(h.tone).toBe('settled');
      expect(h.tone === 'settled' && h.title).toBe('2 of 9 active employees have records to fix before next month');
      expect(h.tone === 'settled' && h.detail).not.toMatch(/block/i);
    });
  }

  test('a company-level blocker on a Locked run is also only a note', () => {
    const h = paymentReadinessHeadline(prerequisites({
      blockedEmployees: 0, blocking: [],
      companyBlocking: [{ companyId: 'c', companyName: 'Co', code: 'X', label: 'No currency', nextAction: 'Set it' }],
    }), 'Locked');
    expect(h.tone).toBe('settled');
  });

  for (const status of ['Draft', 'Processed', 'PendingFinanceReview', 'Approved', null, undefined]) {
    test(`a ${String(status)} run keeps blocking`, () => {
      expect(paymentReadinessHeadline(prerequisites(), status).tone).toBe('blocked');
    });
  }

  test('a settled run with nothing to fix is still clear', () => {
    expect(paymentReadinessHeadline(prerequisites({ blockedEmployees: 0, blocking: [] }), 'Locked').tone).toBe('clear');
  });

  test('every headline carries a translation key with an Arabic text of its own', () => {
    const cases = [
      paymentReadinessHeadline(prerequisites(), 'Locked'),
      paymentReadinessHeadline(prerequisites(), 'Draft'),
      paymentReadinessHeadline(prerequisites({ blockedEmployees: 0, blocking: [], employeesWithRecommendations: 3 })),
      paymentReadinessHeadline(prerequisites({ blockedEmployees: 0, blocking: [] })),
    ];
    for (const h of cases) {
      if (h.tone === 'unknown' || h.tone === 'empty') throw new Error('unexpected tone');
      for (const key of [h.titleKey, h.detailKey]) {
        const ar = translate('ar', key, h.params);
        expect(ar, key).not.toBe(translate('en', key, h.params));
        expect(ar, key).not.toMatch(/[A-Za-z]{4,}/);
      }
    }
  });
});
