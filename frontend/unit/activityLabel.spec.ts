import { expect, test } from '@playwright/test';
import { translate, type MessageParams } from '../src/i18n/translations';
import { auditActionLabel } from '../src/lib/activityLabel';

const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);
const arabic = /[؀-ۿ]/;

test.describe('recent activity labels', () => {
  const codes = [
    'payroll.erp_posting.confirmed_from_export', 'payroll.wps.generated', 'payroll.bank_export.generated', 'payroll.bank_export.downloaded',
    'payroll.run.locked', 'payroll.run.approved', 'payroll.run.processed', 'payroll.payslips.generated', 'payroll.wps.downloaded',
  ];

  test('known audit codes read as short sentences in both languages', () => {
    for (const code of codes) {
      const english = auditActionLabel(en, code);
      expect(english, code).not.toContain('_');
      expect(english, code).not.toContain('.');
      expect(auditActionLabel(ar, code), code).toMatch(arabic);
    }
    expect(auditActionLabel(en, 'payroll.erp_posting.confirmed_from_export')).toBe('ERP posting confirmed from export');
    expect(auditActionLabel(en, 'payroll.wps.generated')).toBe('WPS file generated');
  });

  test('an unknown code is humanised, never shown raw', () => {
    expect(auditActionLabel(en, 'payroll.something_new.happened')).toBe('Payroll something new happened');
    expect(auditActionLabel(en, 'LeaveApproved')).toBe('Leave approved');
    expect(auditActionLabel(en, '')).toBe('—');
  });

  test('a plain sentence from the leave log keeps working', () => {
    expect(auditActionLabel(en, 'Approved')).toBe('Approved');
  });
});

test.describe('Voided has one Arabic wording', () => {
  test('the status, the pipeline stage and the sentence agree', () => {
    const word = ar('enum.Status.Voided');
    expect(ar('Voided')).toBe(word);
    expect(ar('Voided.')).toBe(`${word}.`);
    expect(ar('This run was voided. It has no further stages.')).toContain('إبطال');
  });
});
