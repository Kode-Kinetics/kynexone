import { expect, test } from '@playwright/test';
import { waitPhrase } from '../src/lib/retryAfter';

// Must match the API's LoginAbuseGuard.WaitPhrase thresholds.
test('a short Retry-After reads "in a few seconds"', () => {
  for (const v of ['1', '2', '6', '10', undefined, null, 'garbage']) expect(waitPhrase(v)).toBe('in a few seconds');
});

test('up to 90 seconds reads "in about a minute"', () => {
  for (const v of ['11', '45', '90']) expect(waitPhrase(v)).toBe('in about a minute');
});

test('up to 5 minutes reads "in a few minutes"', () => {
  for (const v of ['91', '200', '300']) expect(waitPhrase(v)).toBe('in a few minutes');
});

test('longer waits name the minutes', () => {
  expect(waitPhrase('301')).toBe('in about 6 minutes');
  expect(waitPhrase('900')).toBe('in about 15 minutes');
});
