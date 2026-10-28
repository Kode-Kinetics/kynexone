import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { formatAmount } from '../src/lib/hire/money';

test.describe('amounts use the app formatter (A12)', () => {
  test('Arabic locale keeps Latin digits, grouping and the currency, like the rest of the app', () => {
    expect(formatAmount(12000.5, 'SAR', 'ar')).toMatch(/12,000\.50/);
    expect(formatAmount(12000.5, 'SAR', 'ar')).not.toMatch(/[٠-٩]/);
    expect(formatAmount(12000.5, 'SAR', 'en')).toMatch(/12,000\.50/);
  });
  test('G09 ratchet: no toLocaleString( anywhere in the wizard files', () => {
    const roots = [path.resolve(__dirname, '../src/views/employees/hire'), path.resolve(__dirname, '../src/lib/hire')];
    const offenders: string[] = [];
    const walk = (dir: string) => { for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const f = path.join(dir, e.name);
      if (e.isDirectory()) walk(f); else if (/\.tsx?$/.test(e.name) && /toLocale\w*String\(|toLocaleDateString\(|toISOString\(\)\.slice/.test(fs.readFileSync(f, 'utf8'))) offenders.push(f);
    } };
    for (const r of roots) if (fs.existsSync(r)) walk(r);
    expect(offenders).toEqual([]);
  });
});
