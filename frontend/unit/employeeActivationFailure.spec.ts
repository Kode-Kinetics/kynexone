import { expect, test } from '@playwright/test';
import { employeeActivationFailure } from '../src/lib/employeeActivationFailure';

test('each blocker and the fallback use the selected language', () => {
  const translate = (s: string) => s === 'Pay approval' ? 'موافقة الأجور' : 'تعذر التفعيل';
  expect(employeeActivationFailure({ response: { data: { blocking: [{ label: 'Pay approval' }] } } }, translate)).toBe('موافقة الأجور');
  expect(employeeActivationFailure(new Error('network'), translate)).toBe('تعذر التفعيل');
});

test('activation explains each returned blocker instead of hiding Finance approval', () => {
  expect(employeeActivationFailure({ response: { data: { blocking: [
    { key: 'pay:compensationUnapproved', label: 'Pay package awaiting Finance approval' },
    { key: 'onboarding:task:1', label: 'Onboarding task open: Verify contract' },
  ] } } })).toContain('Pay package awaiting Finance approval');
  expect(employeeActivationFailure({ response: { data: { message: 'The employee changed. Reload the record.' } } }))
    .toBe('The employee changed. Reload the record.');
});

test('a failed network request still gives a usable error', () => {
  expect(employeeActivationFailure(new Error('offline'))).toContain('could not be activated');
});

test('a refused activation shows the server message verbatim, untranslated', async () => {
  const { activationRejectionMessage } = await import('../src/lib/employeeActivationFailure');
  const refused = { response: { data: { message: 'Pay must be approved by someone else.', blocking: [{ label: 'Other label' }] } } };
  expect(activationRejectionMessage(refused, text => `AR:${text}`)).toBe('Pay must be approved by someone else.');
  expect(activationRejectionMessage({ response: { data: {} } }, text => `AR:${text}`)).toMatch(/^AR:The employee could not be activated/);
});
