import { expect, test } from '@playwright/test';
import { describeBulkError } from '../src/lib/attendanceBulkError';

const axiosError = (status: number, data?: unknown) => ({ isAxiosError: true, response: { status, data } });
const en = (key: string) => key;

test('a 403 that carries the server reason shows that reason', () => {
  expect(describeBulkError(axiosError(403, { message: 'You cannot record attendance for yourself.' }), en, 'FALLBACK')).toBe('You cannot record attendance for yourself.');
});

test('a bare 403 (Forbid() has no body) shows the plain-language fallback, not "error.forbidden"', () => {
  expect(describeBulkError(axiosError(403, ''), en, 'FALLBACK')).toBe('FALLBACK');
  expect(describeBulkError(axiosError(403), en, 'FALLBACK')).toBe('FALLBACK');
});

test('a 400 shows the server message verbatim', () => {
  expect(describeBulkError(axiosError(400, { message: 'The file has no data rows.' }), en, 'FALLBACK')).toBe('The file has no data rows.');
});

test('without a fallback a bare 403 uses the generic forbidden sentence', () => {
  expect(describeBulkError(axiosError(403), en)).toBe('error.forbidden');
});

test('an unreadable failure never prints raw JSON', () => {
  const text = describeBulkError(axiosError(500, { stack: 'at Foo.bar' }), en);
  expect(text).not.toContain('stack');
});

test('a 409 says another bulk attendance operation is running (server message wins when present)', () => {
  expect(describeBulkError(axiosError(409), en)).toBe('Another bulk attendance operation is already running for this company. Wait for it to finish, then try again.');
  expect(describeBulkError(axiosError(409, { message: 'Import batch 7 is still running.' }), en)).toBe('Import batch 7 is still running.');
});

test('a 429 says too many processing jobs are running (server message wins when present)', () => {
  expect(describeBulkError(axiosError(429), en, undefined, 'TOO_MANY')).toBe('TOO_MANY');
  expect(describeBulkError(axiosError(429, { message: 'Try again in 2 minutes.' }), en, undefined, 'TOO_MANY')).toBe('Try again in 2 minutes.');
});
