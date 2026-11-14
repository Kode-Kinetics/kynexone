import { expect, test } from '@playwright/test';
import { translate, type MessageParams } from '../src/i18n/translations';
import { approvalChangeSummary, approvalEntityLabel, approvalOwnerMeta, approvalQueueLabel, approvalTitleLabel } from '../src/lib/approvalDisplay';

const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);
const arabic = /[؀-ۿ]/;

test.describe('approvals queue text', () => {
  test('the entity name is humanised, in both languages', () => {
    expect(approvalEntityLabel(en, 'EmployeeChangeRequest')).toBe('Employee change request');
    expect(approvalEntityLabel(ar, 'EmployeeChangeRequest')).toMatch(arabic);
    expect(approvalEntityLabel(en, 'SomethingNewRequest')).toBe('Something new request');
  });

  test('a role queue reads as the role, not "Role:HR Manager"', () => {
    expect(approvalQueueLabel(en, 'Role:HR Manager')).toBe('HR Manager');
    expect(approvalQueueLabel(ar, 'Role:HR Manager')).toBe('مدير الموارد البشرية');
    expect(approvalQueueLabel(ar, 'Manager:Sara Ali')).toBe('Sara Ali');
    expect(approvalQueueLabel(en, 'Plain queue')).toBe('Plain queue');
  });

  test('the owner hint translates the type and the role', () => {
    expect(approvalOwnerMeta(en, 'Role', 'HR Manager')).toBe('Role · HR Manager');
    expect(approvalOwnerMeta(ar, 'Role', 'HR Manager')).toBe('الدور · مدير الموارد البشرية');
    expect(approvalOwnerMeta(ar, '', '')).toBe(ar('No route metadata'));
  });

  test('the change summary translates each field', () => {
    expect(approvalChangeSummary(en, 'bank name, IBAN')).toBe('bank name, IBAN');
    const out = approvalChangeSummary(ar, 'bank name, IBAN');
    expect(out).toMatch(arabic);
    expect(out).not.toContain('bank name');
    expect(approvalChangeSummary(ar, 'bank name, a brand new field')).toContain('a brand new field');
  });

  test('the employee change title keeps the code and name and translates the words', () => {
    expect(approvalTitleLabel(en, 'Employee change approval - EMP001 Sara Ali')).toBe('Employee change approval - EMP001 Sara Ali');
    const out = approvalTitleLabel(ar, 'Employee change approval - EMP001 Sara Ali');
    expect(out).toMatch(arabic);
    expect(out).toContain('EMP001 Sara Ali');
    expect(approvalTitleLabel(ar, 'Unknown title')).toBe('Unknown title');
  });

  test('"3 days old" uses the Arabic plural forms', () => {
    expect(ar('{days}d old', { days: 3 })).toBe('منذ 3 أيام');
    expect(ar('{days}d old', { days: 1 })).toBe('منذ يوم واحد');
    expect(ar('{days}d old', { days: 2 })).toBe('منذ يومين');
    expect(ar('{days}d old', { days: 11 })).toBe('منذ 11 يومًا');
    expect(ar('{hours}h old', { hours: 3 })).toBe('منذ 3 ساعات');
    expect(ar('{hours}h old', { hours: 2 })).toBe('منذ ساعتين');
  });
});
