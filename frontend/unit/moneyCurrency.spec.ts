import { expect, test } from '@playwright/test';
import { currencyForAmount } from '../src/lib/moneyCurrency';

// An amount shown with no currency of its own (a run with no payment batch yet) must read in the company's currency,
// never in a made-up dollar sign.

test('an amount with its own currency keeps it', () => {
  expect(currencyForAmount('AED', 'SAR')).toBe('AED');
});

test('an amount with no currency of its own uses the company currency', () => {
  expect(currencyForAmount(undefined, 'SAR')).toBe('SAR');
  expect(currencyForAmount('', 'SAR')).toBe('SAR');
  expect(currencyForAmount(null, 'SAR')).toBe('SAR');
});

test('with neither known, the money formatter gets no currency (it shows the bare number)', () => {
  expect(currencyForAmount(undefined, '')).toBeNull();
  expect(currencyForAmount(null, undefined)).toBeNull();
});
