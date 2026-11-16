import { expect, test } from '@playwright/test';
import { employmentTypeLabel } from '../src/lib/employmentType';
import { translate } from '../src/i18n/translations';

type P = Record<string, string | number | null | undefined>;
const en = (key: string, params?: P) => translate('en', key, params);
const ar = (key: string, params?: P) => translate('ar', key, params);

// The home dashboard printed the stored token ("full_time") in the sentence "All 9 active employees are full_time".
test.describe('employment type label', () => {
  const SPELLINGS: Array<[string, string]> = [['full_time', 'Full-time'], ['Full-Time', 'Full-time'], ['FullTime', 'Full-time'], ['part_time', 'Part-time'], ['contract', 'Contract'], ['temporary', 'Temporary'], ['intern', 'Intern'], ['seasonal', 'Seasonal']];
  for (const [raw, english] of SPELLINGS) {
    test(`${raw} reads as ${english} in English and in Arabic with no token left`, () => {
      expect(employmentTypeLabel(en, raw)).toBe(english);
      const arabic = employmentTypeLabel(ar, raw);
      expect(arabic).not.toBe(english);
      expect(arabic).not.toMatch(/[A-Za-z_]/);
    });
  }

  test('an unknown type reads as plain words, never the token', () => {
    expect(employmentTypeLabel(en, 'zero_hours')).toBe('Zero hours');
    expect(employmentTypeLabel(en, '')).toBe('—');
  });
});
