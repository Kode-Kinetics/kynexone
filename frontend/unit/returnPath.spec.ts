import { expect, test } from '@playwright/test';
import { localReturnPathOrNull, safeLocalReturnPath } from '../src/lib/publicAuth';

/**
 * Where sign-in (?from=) and the user-management guided flow (?returnTo=) send someone afterwards.
 * Each unsafe value below resolves, or once decoded resolves, to another site under `new URL()`.
 */
const OFF_SITE = [
  '/\t/evil.example',
  '/\n/evil.example',
  '/\r/evil.example',
  '/\\evil.example',
  '/\\/evil.example',
  '//evil.example',
  '/%09/evil.example',
  decodeURIComponent('/%09/evil.example'),
  '/%2F/evil.example',
  '/%5Cevil.example',
  ' /evil.example',
  'https://evil.example',
  'javascript:alert(1)',
  'data:text/html,x',
  'evil.example',
  '/%E0%A4%A',
  '',
];

test('off-site and malformed return paths fall back to the dashboard', () => {
  for (const value of OFF_SITE) {
    expect(safeLocalReturnPath(value), JSON.stringify(value)).toBe('/dashboard');
    expect(localReturnPathOrNull(value), JSON.stringify(value)).toBeNull();
  }
  expect(safeLocalReturnPath(null)).toBe('/dashboard');
  expect(safeLocalReturnPath(undefined)).toBe('/dashboard');
});

test('every accepted path stays on the page origin', () => {
  const origin = 'https://app.kynexone.test';
  for (const value of OFF_SITE) {
    const kept = localReturnPathOrNull(value);
    if (kept !== null) expect(new URL(kept, origin).origin).toBe(origin);
  }
});

test('a local path keeps its query and fragment', () => {
  expect(safeLocalReturnPath('/payroll?x=1#y')).toBe('/payroll?x=1#y');
  expect(safeLocalReturnPath('/reports?month=1')).toBe('/reports?month=1');
  expect(safeLocalReturnPath('/people?search=John%20Smith')).toBe('/people?search=John%20Smith');
  expect(safeLocalReturnPath('/dashboard')).toBe('/dashboard');
  expect(localReturnPathOrNull('/ess')).toBe('/ess');
});

test('dot segments are resolved before the path is used', () => {
  expect(safeLocalReturnPath('/a/../payroll')).toBe('/payroll');
});
