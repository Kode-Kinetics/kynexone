import { test, expect } from '@playwright/test';
import { secondApproverNotice } from '../src/lib/secondApprover';

// H2: the notice is the server's own sentence, in the reader's language, and only when nobody else could approve.

const none = {
  hasEligibleApprover: false,
  message: 'Add a second person with Finance approval rights (payroll.approve) before proposing salary changes.',
  messageAr: 'أضف شخصاً ثانياً يملك صلاحية الموافقة المالية (payroll.approve) قبل اقتراح تغييرات الرواتب.',
};

test('no notice while a second approver exists, or while the status is unknown', () => {
  expect(secondApproverNotice({ hasEligibleApprover: true }, 'en')).toBeNull();
  expect(secondApproverNotice(null, 'en')).toBeNull();
  expect(secondApproverNotice(undefined, 'ar')).toBeNull();
});

test('with nobody else able to approve, the server sentence is shown in English or Arabic', () => {
  expect(secondApproverNotice(none, 'en')).toBe(none.message);
  expect(secondApproverNotice(none, 'ar')).toBe(none.messageAr);
});

test('a missing translation falls back to the other language rather than showing nothing', () => {
  expect(secondApproverNotice({ hasEligibleApprover: false, message: 'English only' }, 'ar')).toBe('English only');
  expect(secondApproverNotice({ hasEligibleApprover: false, messageAr: 'عربي فقط' }, 'en')).toBe('عربي فقط');
  expect(secondApproverNotice({ hasEligibleApprover: false }, 'en')).toBeNull();
});
