import { expect, test, type Page, type Route } from '@playwright/test';

type Handler = (route: Route, path: string, method: string) => Promise<boolean> | boolean;

/** Boots a group-scope Admin with every API call intercepted; `handler` answers the screen under test. */
async function boot(page: Page, tab: string, handler: Handler) {
  const company = { id: 'c1', name: 'Alpha', code: 'A', countryCode: 'SA', isActive: true };
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'removal-safety-fixture'));
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname; const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (path === '/api/auth/me') return reply({ id: 'u1', tenantId: 't1', tenantSlug: 'fixture', fullName: 'Admin', roles: ['Admin'],
      permissions: ['security.manage', 'organization.write', 'overtime.read', 'overtime.write', 'overtime.policy_manage', 'payroll.read', 'payroll.rates.read', 'payroll.rates.manage'],
      isGroupScope: true, accountType: 'MultiCompany', companies: [company] });
    if (path === '/api/features/disabled-keys') return reply([]);
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (await handler(route, path, method)) return;
    if (method === 'GET') return reply([]);
    return reply({});
  });
  await page.goto(`/setup?tab=${tab}`);
}

const fy = (over: Record<string, unknown>) => ({ id: 'fy', code: 'FY2026', year: 2026, startDate: '2026-01-01', endDate: '2026-12-31', status: 'Open',
  isCurrent: false, canClose: true, canDelete: true, closeBlockedReason: null, deleteBlockedReason: null, ...over });

test('fiscal years: blocked delete shows its reason and a closed year offers no close', async ({ page }) => {
  await boot(page, 'fiscalYears', async (route, path) => {
    if (path === '/api/admin/fiscal-years') { await route.fulfill({ json: [
      fy({ id: 'a', year: 2026, canDelete: false, deleteBlockedReason: 'Payroll runs inside this year already exist, so it cannot be deleted.' }),
      fy({ id: 'b', year: 2024, status: 'Closed', canClose: false, canDelete: false, deleteBlockedReason: 'This year is closed, so it is kept for the record and cannot be deleted.' }),
    ] }); return true; }
    return false;
  });
  await expect(page.getByText('Payroll runs inside this year already exist, so it cannot be deleted.')).toBeVisible();
  await expect(page.getByText('This year is closed, so it is kept for the record and cannot be deleted.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Close year' })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Delete fiscal year' })).toHaveCount(0);
});

test('fiscal years: a failed close shows the server reason', async ({ page }) => {
  await boot(page, 'fiscalYears', async (route, path, method) => {
    if (path === '/api/admin/fiscal-years' && method === 'GET') { await route.fulfill({ json: [fy({ id: 'a' })] }); return true; }
    if (path === '/api/admin/fiscal-years/a/close') { await route.fulfill({ status: 409, json: { code: 'FISCAL_YEAR_ALREADY_CLOSED', message: 'This fiscal year is already closed.' } }); return true; }
    return false;
  });
  await page.getByRole('button', { name: 'Close year' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Close year' }).click();
  await expect(page.getByText('This fiscal year is already closed.')).toBeVisible();
});

test('numbering rules: turn off asks first, then calls the active endpoint', async ({ page }) => {
  const calls: string[] = [];
  const rule = { id: 'r1', entityType: 'Employee', prefix: 'EMP', suffix: '', paddingLength: 5, separator: '-', includeYear: true, includeMonth: false, currentSequence: 0, resetYearly: true, isActive: true };
  await boot(page, 'numberingRules', async (route, path, method) => {
    if (path === '/api/admin/numbering-rules' && method === 'GET') { await route.fulfill({ json: [rule] }); return true; }
    if (path === '/api/admin/numbering-rules/r1/active') { calls.push(route.request().url()); await route.fulfill({ json: { ...rule, isActive: false } }); return true; }
    return false;
  });
  await page.getByRole('button', { name: 'Turn off', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Turn off this numbering rule?');
  await page.getByRole('dialog').getByRole('button', { name: 'Turn off', exact: true }).click();
  await expect.poll(() => calls.length).toBe(1);
  expect(calls[0]).toContain('active=false');
});

const policy = (over: Record<string, unknown>) => ({ id: 'p1', companyId: 'c1', countryCode: 'SA', effectiveFrom: '2026-01-01', effectiveTo: null,
  status: 'Active', incomeTaxRatePercent: 0, appliesToBonus: true, notes: '', usedByPayroll: false, ...over });

test('tax policies: unused policy can be edited, ended from a date and removed; used one says why not', async ({ page }) => {
  const seen: string[] = [];
  await boot(page, 'taxPolicies', async (route, path, method) => {
    if (path === '/api/company-tax-policies' && method === 'GET') { await route.fulfill({ json: [policy({ id: 'p1' }), policy({ id: 'p2', usedByPayroll: true })] }); return true; }
    if (path === '/api/company-tax-policies/p1/retire') { seen.push('retire:' + route.request().postData()); await route.fulfill({ json: policy({ id: 'p1' }) }); return true; }
    if (path === '/api/company-tax-policies/p1' && method === 'DELETE') { seen.push('delete'); await route.fulfill({ status: 204 }); return true; }
    return false;
  });
  await expect(page.getByText('Payroll has already been finalized under this policy, so it cannot be deleted.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Remove tax policy' })).toHaveCount(1);
  // Edit opens the editor with the saved values.
  await page.getByRole('button', { name: 'Edit' }).first().click();
  await expect(page.getByRole('dialog', { name: 'Edit tax policy' })).toBeVisible();
  await page.getByRole('button', { name: 'Cancel' }).click();
  // End from a date.
  await page.getByRole('button', { name: 'End from date' }).first().click();
  await page.getByLabel('Last day this policy applies').fill('2026-12-31');
  await page.getByRole('button', { name: 'End policy' }).click();
  await expect.poll(() => seen.some(s => s.startsWith('retire:') && s.includes('2026-12-31'))).toBe(true);
  // Remove asks first.
  await page.getByRole('button', { name: 'Remove tax policy' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Remove' }).click();
  await expect.poll(() => seen.includes('delete')).toBe(true);
});

test('country payroll keys: deleting weekend_days names its effect before it deletes', async ({ page }) => {
  let deleted = false;
  const rule = { id: 'k1', countryCode: 'SA', ruleKey: 'weekend_days', ruleValue: 'Friday,Saturday', dataType: 'string', isOverride: false, effectiveFrom: null, effectiveTo: null };
  await boot(page, 'gccSettings&view=payrollKeys', async (route, path, method) => {
    if (path === '/api/tenant-admin/country-rules' && method === 'GET') { await route.fulfill({ json: [rule] }); return true; }
    if (path === '/api/tenant-admin/country-rules/k1' && method === 'DELETE') { deleted = true; await route.fulfill({ status: 204 }); return true; }
    return false;
  });
  await page.getByRole('button', { name: /Delete rule weekend_days/ }).click();
  await expect(page.getByRole('dialog')).toContainText('changes the work week');
  expect(deleted).toBe(false);
  await page.getByRole('dialog').getByRole('button', { name: 'Delete' }).click();
  await expect.poll(() => deleted).toBe(true);
});

const otPolicy = (over: Record<string, unknown>) => ({ id: 'o1', code: 'STD', name: 'Standard', hourlyRateBasis: 'BasicSalary', fixedHourlyRate: 0, standardMonthlyHours: 240,
  minimumMinutes: 30, maximumMinutesPerDay: 240, monthlyCapMinutes: 3600, roundingRule: 'Nearest15', requiresApproval: true, allowCompOffConversion: true, isActive: true,
  createdAtUtc: '2026-01-01T00:00:00Z', ...over });

test('overtime policies: the edit form shows the saved multipliers and saves full values; turn off asks first', async ({ page }) => {
  let saved: Record<string, unknown> | null = null; let activeCall = '';
  await boot(page, 'overtimePolicies', async (route, path, method) => {
    if (path === '/api/overtime/policies' && method === 'GET') { await route.fulfill({ json: [otPolicy({}), otPolicy({ id: 'o2', code: 'ALT', name: 'Alternate' })] }); return true; }
    if (path === '/api/overtime/policies/o1' && method === 'GET') { await route.fulfill({ json: { ...otPolicy({}), regularDayMultiplier: 1.3, weekendMultiplier: 1.6, holidayMultiplier: 2.1 } }); return true; }
    if (path === '/api/overtime/policies/o1' && method === 'PUT') { saved = route.request().postDataJSON(); await route.fulfill({ json: otPolicy({}) }); return true; }
    if (path === '/api/overtime/policies/o1/active') { activeCall = route.request().url(); await route.fulfill({ json: otPolicy({ isActive: false }) }); return true; }
    return false;
  });
  await page.getByRole('button', { name: 'Edit', exact: true }).first().click();
  await expect(page.getByLabel('Regular day multiplier')).toHaveValue('1.3');
  await expect(page.getByLabel('Weekend multiplier')).toHaveValue('1.6');
  await expect(page.getByLabel('Public holiday multiplier')).toHaveValue('2.1');
  await page.getByRole('button', { name: 'Save Policy' }).click();
  await expect.poll(() => saved).not.toBeNull();
  expect(saved).toMatchObject({ regularDayMultiplier: 1.3, weekendMultiplier: 1.6, holidayMultiplier: 2.1 });
  await page.getByRole('button', { name: 'Turn off', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toContainText('Turn off this overtime policy?');
  await page.getByRole('dialog').getByRole('button', { name: 'Turn off', exact: true }).click();
  await expect.poll(() => activeCall).toContain('active=false');
});
