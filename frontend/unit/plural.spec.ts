import { test, expect } from '@playwright/test';
import { countOf, pluralCategory, pluralize } from '../src/lib/plural';

// The reported defect, verbatim: the Saudi Compliance score card read
// "1 urgent action require attention" — a count of one against a plural verb.
const URGENT = {
  one:   '{count} urgent action needs attention',
  other: '{count} urgent actions need attention',
};

test('one urgent action takes the singular noun and the singular verb', () => {
  expect(pluralize(1, URGENT)).toBe('1 urgent action needs attention');
});

test('two urgent actions take the plural noun and the plural verb', () => {
  expect(pluralize(2, URGENT)).toBe('2 urgent actions need attention');
});

test('zero takes the plural form in English', () => {
  expect(pluralize(0, URGENT)).toBe('0 urgent actions need attention');
});

test('large counts are grouped for the locale', () => {
  expect(pluralize(1234, URGENT)).toBe('1,234 urgent actions need attention');
});

test('countOf covers the plain noun case in both numbers', () => {
  expect(countOf(1, 'employee', 'employees')).toBe('1 employee');
  expect(countOf(2, 'employee', 'employees')).toBe('2 employees');
});

// The app ships EN and AR. Arabic has six plural categories, so a hand-rolled `n !== 1 ? 's' : ''`
// cannot be translated correctly — these assert the helper defers to the locale's own rules.
test('Arabic selects its own plural categories, not English ones', () => {
  expect(pluralCategory(0, 'ar')).toBe('zero');
  expect(pluralCategory(1, 'ar')).toBe('one');
  expect(pluralCategory(2, 'ar')).toBe('two');
  expect(pluralCategory(3, 'ar')).toBe('few');
  expect(pluralCategory(11, 'ar')).toBe('many');
});

test('an Arabic count with only English forms supplied still resolves to a real sentence', () => {
  // `two` / `few` / `many` were never supplied, so the fallback chain lands on `other`
  // rather than throwing or printing "undefined".
  const out = pluralize(3, URGENT, 'ar');
  expect(out).not.toContain('undefined');
  expect(out).toContain('urgent actions need attention');
});

test('an Arabic string with all six forms picks the right one', () => {
  const forms = {
    zero:  'لا توجد إجراءات عاجلة',
    one:   'إجراء عاجل واحد يتطلب انتباهك',
    two:   'إجراءان عاجلان يتطلبان انتباهك',
    few:   '{count} إجراءات عاجلة تتطلب انتباهك',
    many:  '{count} إجراءً عاجلاً يتطلب انتباهك',
    other: '{count} إجراء عاجل يتطلب انتباهك',
  };
  expect(pluralize(1, forms, 'ar')).toBe('إجراء عاجل واحد يتطلب انتباهك');
  expect(pluralize(2, forms, 'ar')).toBe('إجراءان عاجلان يتطلبان انتباهك');
  expect(pluralize(3, forms, 'ar')).toContain('إجراءات عاجلة');
});
