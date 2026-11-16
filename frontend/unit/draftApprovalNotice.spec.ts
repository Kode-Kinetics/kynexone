import { test, expect } from '@playwright/test';
import { draftApprovalNotice } from '../src/lib/newHireReview';
import { LOCALE_DICTS } from '../src/i18n/translations';

test.describe('what the New hires screen says once a draft is approved', () => {
  test('an active employee reads as before', () => {
    const notice = draftApprovalNotice({ name: 'Layla', employeeCode: 'EMP-7', status: 'Active' });
    expect(notice).toBe('Layla is now employee EMP-7.');
  });

  test('an employee whose pay waits for a second approver is not called active; HR is told what happens next', () => {
    const notice = draftApprovalNotice({ name: 'Layla', employeeCode: 'EMP-7', status: 'Draft' });
    expect(notice).toBe('Layla is now employee EMP-7. Their pay package is waiting for a second approver; activate them once it is approved.');
  });

  test('the Arabic wording exists', () => {
    const arabic = draftApprovalNotice({ name: 'ليلى', employeeCode: 'EMP-7', status: 'Draft' }, (k, p) => {
      const template = LOCALE_DICTS.ar[k] ?? k;
      return template.replace(/\{(\w+)\}/g, (_, name) => String(p?.[name] ?? ''));
    });
    expect(arabic).toContain('معتمِد ثانٍ');
    expect(arabic).toContain('EMP-7');
  });
});
