import { expect, test } from '@playwright/test';
import { translate, type MessageParams } from '../src/i18n/translations';
import { employeeStateLabel } from '../src/lib/employeeAccess';
import { readinessLabel } from '../src/lib/readinessLabel';

const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);
const arabic = /[؀-ۿ]/;

test.describe('Access and onboarding labels', () => {
  test('a Draft employee reads Draft, not just "Awaiting pay approval"', () => {
    expect(employeeStateLabel('awaiting_pay_approval', 'Draft')).toBe('Awaiting pay approval (Draft)');
    expect(employeeStateLabel('not_active', 'Draft')).toBe('Not active yet (Draft)');
    expect(ar(employeeStateLabel('not_active', 'Draft'))).toMatch(arabic);
    expect(ar(employeeStateLabel('awaiting_pay_approval', 'Draft'))).toMatch(arabic);
  });

  test('other statuses keep their own wording', () => {
    expect(employeeStateLabel('active', 'Active')).toBe('Active');
    expect(employeeStateLabel('awaiting_pay_approval', 'Pending')).toBe('Awaiting pay approval');
    expect(employeeStateLabel('ended', 'Terminated')).toBe('Employment ended');
  });

  test('the document requirement label is translated, for any document type', () => {
    expect(readinessLabel(en, 'Contract document')).toBe('Contract document');
    expect(readinessLabel(ar, 'Contract document')).toBe('مستند العقد');
    expect(readinessLabel(ar, 'Passport document')).toBe('مستند جواز السفر');
    expect(readinessLabel(ar, 'Some unknown label')).toBe('Some unknown label');
    expect(readinessLabel(ar, 'Identity document')).toBe(ar('Identity document'));
  });
});
