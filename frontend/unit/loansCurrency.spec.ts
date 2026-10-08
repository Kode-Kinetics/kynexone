import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { createFormatter } from '../src/lib/format';

// Loans carried in through the opening-balance import before #188 have Currency = null in production.
// Formatting them with toLocaleString({ style: 'currency', currency: null }) throws "Invalid currency code"
// and takes the whole loan panel down. Loan screens format money through the shared formatter instead.

test('the shared formatter shows a bare amount for a missing currency, and never throws', () => {
  const f = createFormatter({ locale: 'en', timeZone: 'Asia/Riyadh' });
  expect(() => f.plain.money(4800, null)).not.toThrow();
  expect(f.plain.money(4800, null)).toMatch(/4,800/);
  expect(f.plain.money(4800, 'SAR')).toMatch(/SAR|﷼/);
  expect(() => f.plain.money(4800, 'NOT-A-CODE')).not.toThrow();
});

test('no loan screen formats money by hand with a currency code', () => {
  const dir = join(__dirname, '..', 'src', 'components', 'loans');
  const files = [...readdirSync(dir).map((f) => join(dir, f)), join(__dirname, '..', 'src', 'views', 'LoansPage.tsx')]
    .filter((f) => f.endsWith('.tsx'));
  for (const file of files) {
    expect(readFileSync(file, 'utf8'), file).not.toMatch(/style:\s*'currency',\s*currency/);
  }
});
