import { expect, test } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import { refusalFromError, refusalText } from '../src/lib/refusal';

/**
 * The refusal pattern (Screen Standard section 3e): what happened, why, what to do. Never "Failed to save",
 * "Invalid", "Error 422" or "Something went wrong" alone.
 */
const en = (key: string, params?: Record<string, string | number | null | undefined>) => translate('en', key, params);
const ar = (key: string, params?: Record<string, string | number | null | undefined>) => translate('ar', key, params);
const http = (status: number, data: unknown) => ({ isAxiosError: true, response: { status, data } });

test('refusalText is "what: why. fix." and skips the parts that are missing', () => {
  expect(refusalText({ what: 'Salary was not saved', why: 'the structure is no longer valid for this employee', fix: 'Choose another structure.' }))
    .toBe('Salary was not saved: the structure is no longer valid for this employee. Choose another structure.');
  expect(refusalText({ what: 'Salary was not saved:', why: 'The run is locked.' })).toBe('Salary was not saved: The run is locked.');
  expect(refusalText({ what: 'The request was not sent', fix: 'Try again.' })).toBe('The request was not sent. Try again.');
  expect(refusalText({ what: 'The request was not sent' })).toBe('The request was not sent.');
});

test('a server refusal sentence becomes the why, with the caller\'s what and fix', () => {
  const error = http(409, { code: 'conflict', message: 'The payroll run is locked.' });
  expect(refusalFromError(error, { what: 'The record was not deleted', fix: 'Void the run instead.' }, en, 'en'))
    .toBe('The record was not deleted: The payroll run is locked. Void the run instead.');
});

test('in Arabic a refusal that ships messageAr shows it; otherwise the translated status sentence', () => {
  const both = http(400, { message: 'Leave overlaps an approved request.', messageAr: 'تتداخل الإجازة مع طلب معتمد.' });
  expect(refusalFromError(both, { what: 'لم يُحفظ الطلب' }, ar, 'ar')).toContain('تتداخل الإجازة مع طلب معتمد.');
  expect(refusalFromError(both, { what: 'x' }, en, 'en')).toContain('Leave overlaps an approved request.');
});

test('a 403 says what is blocked and who can grant it', () => {
  const text = refusalFromError(http(403, { code: 'forbidden', message: 'Forbidden' }), { what: 'Payroll runs cannot be opened' }, en, 'en');
  expect(text).toContain('Payroll runs cannot be opened');
  expect(text).toContain('Ask your HR administrator to grant you access.');
});

test('an unreachable server and a 5xx never leak server text and say to try again', () => {
  const offline = refusalFromError({ isAxiosError: true }, { what: 'The request was not sent' }, en, 'en');
  expect(offline).toContain('The server could not be reached');
  const boom = refusalFromError(http(500, { message: 'NullReferenceException at X.Y' }), { what: 'The run was not approved' }, en, 'en');
  expect(boom).not.toContain('NullReference');
  expect(boom).toContain('Check your connection and try again in a moment.');
});

test('the generic words alone are never the whole message', () => {
  for (const error of [http(422, undefined), http(500, undefined), { isAxiosError: true }, new Error('boom')]) {
    const text = refusalFromError(error, { what: 'The leave request was not withdrawn' }, en, 'en');
    expect(text.startsWith('The leave request was not withdrawn')).toBe(true);
    expect(text).not.toMatch(/^(Failed to|Invalid|Error \d+|Something went wrong)/);
  }
});

test('notifyRefusal raises the app-wide error event with the full sentence, and leaves 401/402/403 to the interceptor', async () => {
  const { notifyRefusal } = await import('../src/lib/refusal');
  const events: string[] = [];
  const target = new EventTarget();
  target.addEventListener('zayra:error', event => events.push((event as CustomEvent<string>).detail));
  (globalThis as unknown as { window: EventTarget }).window = target;
  try {
    notifyRefusal(http(409, { code: 'conflict', message: 'The request was already decided.' }), { what: 'The leave request was not approved', fix: 'Reload the list.' }, en, 'en');
    notifyRefusal(http(403, { message: 'Forbidden' }), { what: 'x' }, en, 'en');
    notifyRefusal(http(401, undefined), { what: 'x' }, en, 'en');
    expect(events).toEqual(['The leave request was not approved: The request was already decided. Reload the list.']);
  } finally {
    delete (globalThis as unknown as { window?: EventTarget }).window;
  }
});
