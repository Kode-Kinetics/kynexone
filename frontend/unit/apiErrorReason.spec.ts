import { test, expect } from '@playwright/test';
import { apiErrorReason } from '../src/api/client';

// Action failures used to read "Failed." (an alert with no reason). notifyApiError now shows the
// server's own sentence, or says the server could not be reached, before falling back.

const FALLBACK = 'The leave type could not be deactivated.';
const http = (status: number, data?: unknown) => ({ isAxiosError: true, response: { status, data } });

test("the server's own sentence wins over the fallback", () => {
  expect(apiErrorReason(http(409, { code: 'conflict', message: 'The leave type is used by 3 active policies.' }), FALLBACK))
    .toBe('The leave type is used by 3 active policies.');
  expect(apiErrorReason(http(400, { detail: 'Hours must be positive.' }), FALLBACK)).toBe('Hours must be positive.');
});

test('validation errors are joined into plain sentences', () => {
  expect(apiErrorReason(http(400, { title: 'One or more validation errors occurred.', errors: { Reason: ['A reason is required.'] } }), FALLBACK))
    .toBe('A reason is required.');
});

test('a bare machine code is never shown; the fallback is', () => {
  expect(apiErrorReason(http(422, { error: 'maker_checker_violation' }), FALLBACK)).toBe(FALLBACK);
  expect(apiErrorReason(http(500, {}), FALLBACK)).toBe(FALLBACK);
});

test('a proxy HTML page is not a reason', () => {
  expect(apiErrorReason(http(502, '<html><body>Bad Gateway</body></html>'), FALLBACK)).toBe(FALLBACK);
});

test('no response at all says the server could not be reached', () => {
  expect(apiErrorReason({ isAxiosError: true }, FALLBACK)).toBe('The server could not be reached. Check your connection, then retry.');
});

test('a non-HTTP error (a clipboard failure) keeps the fallback', () => {
  expect(apiErrorReason(new Error('denied'), FALLBACK)).toBe(FALLBACK);
});

test('server exception text from a 5xx is never shown; the fallback is', () => {
  expect(apiErrorReason(http(500, { message: 'NullReferenceException: Object reference not set to an instance of an object.' }), FALLBACK)).toBe(FALLBACK);
  expect(apiErrorReason(http(503, 'Service temporarily overloaded at node 7'), FALLBACK)).toBe(FALLBACK);
  expect(apiErrorReason(http(502, { detail: 'upstream connect error' }), FALLBACK)).toBe(FALLBACK);
});
