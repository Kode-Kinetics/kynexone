import { expect, test } from '@playwright/test';
import { translate, type MessageParams } from '../src/i18n/translations';
import { enumLabel } from '../src/i18n/enumLabel';
import { wpsStatusNote } from '../src/lib/payrollBatchDisplay';

const ar = (k: string, p?: MessageParams) => translate('ar', k, p);
const en = (k: string, p?: MessageParams) => translate('en', k, p);

test.describe('batch status shown once', () => {
  test('a label that only repeats the status adds no second line', () => {
    expect(wpsStatusNote(en, 'Generated (file ready for the bank)', 'Generated')).toBeNull();
    expect(wpsStatusNote(en, 'Submitted to the bank', 'Submitted')).toBeNull();
    expect(wpsStatusNote(en, 'Draft', 'Draft')).toBeNull();
  });

  test('an acceptance caveat is still shown, translated', () => {
    const label = 'Accepted — evidence attached (not verified by Mudad)';
    expect(wpsStatusNote(en, label, 'Accepted')).toBe(label);
    expect(wpsStatusNote(ar, label, 'Accepted')).toMatch(/[؀-ۿ]/);
  });

  test('every WPS status reads in Arabic, including the ones the status control offers', () => {
    for (const s of ['Draft', 'Generated', 'Downloaded', 'Submitted', 'Accepted', 'Rejected', 'Paid', 'Reconciled']) {
      expect(enumLabel(ar, 'Status', s), s).toMatch(/[؀-ۿ]/);
    }
  });
});

test.describe('bank format acceptance caveat', () => {
  test('the API strings are translated, an unknown one passes through', async () => {
    const { bankAcceptanceLabel } = await import('../src/lib/payrollBatchDisplay');
    expect(bankAcceptanceLabel(en, 'specification-implemented; bank-acceptance-not-verified')).toBe('Specification implemented; bank acceptance not verified');
    expect(bankAcceptanceLabel(ar, 'specification-implemented; bank-acceptance-not-verified')).toMatch(/[؀-ۿ]/);
    expect(bankAcceptanceLabel(ar, 'customer-configured-layout; bank-acceptance-not-verified')).toMatch(/[؀-ۿ]/);
    expect(bankAcceptanceLabel(en, 'something else')).toBe('something else');
  });
});
