import { test, expect } from '@playwright/test';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { COST_ACCOUNTING_SETUP_HREF } from '../src/lib/glCostAccounting';

// Cost accounting is a sub-tab of Setup → Payroll accounting (glMapping), opened by `glTab`, the same address
// "Find a setting" builds. There is no top-level `tab=costAccounting` section, so such a link lands nowhere.
test('the cost accounting link opens the Payroll accounting sub-tab', () => {
  expect(COST_ACCOUNTING_SETUP_HREF).toBe('/setup?tab=glMapping&glTab=costAccounting');
});

test('no screen links to a top-level costAccounting setup tab', () => {
  const offenders: string[] = [];
  const walk = (dir: string) => {
    for (const name of readdirSync(dir)) {
      const path = join(dir, name);
      if (statSync(path).isDirectory()) walk(path);
      else if (/\.(ts|tsx)$/.test(name) && readFileSync(path, 'utf8').includes("/setup?tab=costAccounting")) offenders.push(path);
    }
  };
  walk(join(__dirname, '..', 'src'));
  expect(offenders).toEqual([]);
});
