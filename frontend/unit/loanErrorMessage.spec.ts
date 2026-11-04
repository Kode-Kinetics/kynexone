import { test, expect } from '@playwright/test';
import { loanErrorMessage } from '../src/lib/loanWorkflow';

// A Render/Vercel 502 used to arrive as a full HTML error page, and the Loan Payments tab printed every byte of it
// inside the red alert. A gateway outage now reads as one plain sentence, and markup is never shown.

const FALLBACK = 'Unable to load loan payments.';
const http = (status: number, data?: unknown) => ({ isAxiosError: true, response: { status, data } });
const RENDER_502 = '<!DOCTYPE html> <html lang="en"> <head><title>502</title><style>@font-face{}</style></head><body><h1>Bad Gateway</h1></body></html>';

test('a gateway outage reads as a plain sentence, whatever the body', () => {
  for (const status of [502, 503, 504]) {
    const message = loanErrorMessage(http(status, RENDER_502), FALLBACK);
    expect(message).toContain('temporarily unavailable');
    expect(message).not.toContain('<');
  }
});

test('an HTML body on any other status falls back instead of rendering markup', () => {
  expect(loanErrorMessage(http(500, RENDER_502), FALLBACK)).toBe(FALLBACK);
  expect(loanErrorMessage(http(500, '  <html><body>oops</body></html>'), FALLBACK)).toBe(FALLBACK);
});

test("the server's own plain-text and structured messages still win", () => {
  expect(loanErrorMessage(http(400, 'Maker-checker control: the batch creator cannot approve their own batch.'), FALLBACK))
    .toBe('Maker-checker control: the batch creator cannot approve their own batch.');
  expect(loanErrorMessage(http(409, { message: 'Only draft batches can be approved.' }), FALLBACK)).toBe('Only draft batches can be approved.');
  expect(loanErrorMessage(http(400, { errors: { Reason: ['A reason is required.'] } }), FALLBACK)).toBe('A reason is required.');
});

test('no response at all uses the fallback', () => {
  expect(loanErrorMessage(new Error('Network Error'), FALLBACK)).toBe(FALLBACK);
  expect(loanErrorMessage(http(400, '   '), FALLBACK)).toBe(FALLBACK);
});
