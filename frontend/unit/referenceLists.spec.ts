import { test, expect } from '@playwright/test';
import {
  findReferenceValue,
  referenceCode,
  referenceDefault,
  referenceLabel,
  referenceOptions,
  sameReferenceValue,
  type ReferenceValue,
} from '../src/lib/referenceLists';

// Employee forms store a reference-list CODE and show the label in the reader's language. Rows saved before that
// hold the English label and are not rewritten, so a legacy-label row and a code row must read the same.

const genders: ReferenceValue[] = [
  { code: 'MALE', valueEn: 'Male', valueAr: 'ذكر', isActive: true, isDefault: false, sortOrder: 1 },
  { code: 'FEMALE', valueEn: 'Female', valueAr: 'أنثى', isActive: true, isDefault: false, sortOrder: 2 },
  { code: 'OTHER', valueEn: 'Other', valueAr: 'آخر', isActive: false, isDefault: false, sortOrder: 3 },
];

test('a legacy label row and a code row resolve to the same value and the same label', () => {
  for (const stored of ['MALE', 'Male', 'male', ' Male ', 'ذكر']) {
    expect(referenceCode(genders, stored)).toBe('MALE');
    expect(referenceLabel(genders, stored, 'en')).toBe('Male');
  }
  expect(sameReferenceValue(genders, 'Female', 'FEMALE')).toBe(true);
  expect(sameReferenceValue(genders, 'Female', 'MALE')).toBe(false);
});

test('Arabic readers see the Arabic label for a code row and for a legacy label row', () => {
  expect(referenceLabel(genders, 'FEMALE', 'ar')).toBe('أنثى');
  expect(referenceLabel(genders, 'Female', 'ar')).toBe('أنثى');
  const noArabic: ReferenceValue[] = [{ code: 'FULL_TIME', valueEn: 'Full-Time', valueAr: '' }];
  expect(referenceLabel(noArabic, 'FULL_TIME', 'ar')).toBe('Full-Time');
  expect(referenceOptions(genders, 'ar').map((option) => option.label)).toEqual(['ذكر', 'أنثى']);
});

test('renaming a label in Setup does not orphan a row that stores the code', () => {
  const renamed = genders.map((value) => (value.code === 'MALE' ? { ...value, valueEn: 'Man', valueAr: 'رجل' } : value));
  expect(referenceCode(renamed, 'MALE')).toBe('MALE');
  expect(referenceLabel(renamed, 'MALE', 'en')).toBe('Man');
  expect(referenceLabel(renamed, 'MALE', 'ar')).toBe('رجل');
  // The form selects the renamed value for the code row; nothing is lost or duplicated.
  const options = referenceOptions(renamed, 'en', [], 'MALE');
  expect(options).toEqual([{ value: 'MALE', label: 'Man' }, { value: 'FEMALE', label: 'Female' }]);
});

test('forms offer active values by code, keep a retired or unknown current value, and fall back when there is no list', () => {
  expect(referenceOptions(genders, 'en')).toEqual([{ value: 'MALE', label: 'Male' }, { value: 'FEMALE', label: 'Female' }]);
  // A retired value the employee still holds stays selectable and labelled, first.
  expect(referenceOptions(genders, 'en', [], 'Other')[0]).toEqual({ value: 'OTHER', label: 'Other' });
  // A value the list does not know is kept exactly as typed.
  expect(referenceOptions(genders, 'en', [], 'Prefer not to say')[0]).toEqual({ value: 'Prefer not to say', label: 'Prefer not to say' });
  expect(referenceCode(genders, 'Prefer not to say')).toBe('Prefer not to say');
  // No list (Setup unreachable): the built-in labels, stored as before.
  expect(referenceOptions([], 'en', ['Male', 'Female'], 'Male')).toEqual([{ value: 'Male', label: 'Male' }, { value: 'Female', label: 'Female' }]);
});

test('a new record starts on the default value code, else the fallback read through the list', () => {
  expect(referenceDefault([{ code: 'FULL_TIME', valueEn: 'Full-Time', valueAr: '', isDefault: true }], 'Contract')).toBe('FULL_TIME');
  expect(referenceDefault([{ code: 'FULL_TIME', valueEn: 'Full-Time', valueAr: '' }], 'Full-Time')).toBe('FULL_TIME');
  expect(referenceDefault([], 'Full-Time')).toBe('Full-Time');
  expect(referenceDefault(genders, '')).toBe('');
});

test('a code wins over another value whose label reads the same', () => {
  const tricky: ReferenceValue[] = [{ code: 'A', valueEn: 'B', valueAr: '' }, { code: 'B', valueEn: 'Bee', valueAr: '' }];
  expect(findReferenceValue(tricky, 'B')?.code).toBe('B');
});
