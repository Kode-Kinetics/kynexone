import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { normaliseReviewItems, reviewItemLabel } from '../src/lib/hire/reviewLabels';

const tIn = (loc: 'en' | 'ar') => (k: string) => LOCALE_DICTS[loc][k] ?? k;

test('I-2/I-8: known hire-review keys render through t() in English and Arabic', () => {
  const item = { key: 'pay:compensationUnapproved', label: 'Pay package awaiting Finance approval' };
  expect(reviewItemLabel(item, tIn('en'))).toBe('Pay package awaiting Finance approval');
  const ar = reviewItemLabel(item, tIn('ar'));
  expect(ar).not.toBe(item.label);
  expect(/[؀-ۿ]/.test(ar)).toBe(true);
});

test('unknown keys fall back to the server label, then to the key', () => {
  expect(reviewItemLabel({ key: 'SomeFutureKey', label: 'Some future label' }, tIn('ar'))).toBe('Some future label');
  expect(reviewItemLabel({ key: 'X', label: '' }, tIn('en'))).toBe('X');
});

test('items normalise from {key,label}, from bare strings, and ignore junk', () => {
  expect(normaliseReviewItems([{ key: 'a', label: 'A' }, 'B', 7, null, { key: 'c' }])).toEqual([
    { key: 'a', label: 'A' }, { key: 'B', label: 'B' }, { key: 'c', label: 'c' }]);
  expect(normaliseReviewItems(undefined)).toEqual([]);
});
