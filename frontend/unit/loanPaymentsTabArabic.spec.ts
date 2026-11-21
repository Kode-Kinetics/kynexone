import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { translate } from '../src/i18n/translations';

// The Loan payments tab is the screen where a client sees "a separate batch per type". It must read in Arabic end to end:
// no bare English text between tags, and every key it passes to t() has an Arabic translation.
const source = readFileSync(join(process.cwd(), 'src/components/loans/LoanPaymentsTab.tsx'), 'utf8');
const ARABIC = /[؀-ۿ]/;

test('the loan payments tab has no bare English text between tags', () => {
  const bare = [...source.matchAll(/>\s*([A-Z][A-Za-z ,.'’()—→-]{3,}?)\s*</g)].map((m) => m[1]).filter((s) => !s.includes('{') && s !== 'Promise'); // Promise: a TypeScript generic, not text
  expect(bare).toEqual([]);
});

test('every t() key and every column or label key on the tab is translated into Arabic', () => {
  const keys = new Set<string>();
  for (const m of source.matchAll(/\bt\(\s*(['"])((?:\\.|(?!\1).)+)\1/g)) keys.add(m[2].replace(/\\'/g, "'"));
  expect(keys.size).toBeGreaterThan(20);
  const missing = [...keys].filter((k) => !ARABIC.test(translate('ar', k)));
  expect(missing).toEqual([]);
});
