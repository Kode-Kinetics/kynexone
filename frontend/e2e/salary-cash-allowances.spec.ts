import { expect, test, type Page } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { bootEmployeeWorkspaceFixture, employees } from './employee-workspace.fixture';

/**
 * Monthly cash allowances (ALW_*) on a salary, in a real browser against route mocks (slice C2):
 *  - Finance approval: current beside proposed, every difference named in words, carried-forward said plainly, a stale refusal explained;
 *  - Assign Salary: an untouched section sends NO `allowances` (the server carries the lines forward), an edit sends the list, clearing sends [];
 *  - Edit employee (Salary step) and the employee profile salary panel.
 * Names come from the server on every line (employee detail `salaryBreakdown.allowances[]`, the salary list's `allowances`, the approval view), so a
 * reader without entitlements.read still sees names; the approval view also carries glDriverLabel, shown instead of the bare key.
 * Written without a run: the sandbox cannot launch Chromium. CI's browser lane runs it.
 *
 * Run: npx playwright test -c e2e/playwright.salary-allowances.config.ts (starts `next dev` on SALARY_ALLOWANCES_PORT, default 5198).
 */

type Locale = 'en' | 'ar';
const say = (locale: Locale, key: string, values: Record<string, string | number> = {}) =>
  Object.entries(values).reduce((text, [k, v]) => text.replaceAll(`{${k}}`, String(v)), (LOCALE_DICTS[locale] as Record<string, string>)[key] ?? key);

const meal = { code: 'ALW_MEAL', nameEn: 'Meal', nameAr: 'وجبات' };
const car = { code: 'ALW_CAR', nameEn: 'Car', nameAr: 'سيارة' };
const component = (c: { code: string; nameEn: string; nameAr: string }) => ({
  ...c, class: 'Allowance', floor: 'None', group: 'Wage', allowedValueTypes: ['Amount', 'PercentOfBasic'], allowedCoverageTiers: [], allowedDependantScopes: [],
  allowedLimitPeriods: [], defaultLimitPeriod: null, isFloor: false, canBeSkipped: true, isLoanFacility: false, floorReason: null, isCustom: true,
});
const matrix = { asOf: '2026-11-05', today: '2026-11-05', companyId: null, currency: 'SAR', grades: [], components: [component(meal), component(car)], cells: [], offerings: [], gaps: [] };

const line = (c: { code: string; nameEn: string; nameAr: string }, basis: 'Amount' | 'PercentOfBasic', amount: number, rate: number | null = null, extra: object = {}) =>
  ({ ...c, basis, rate, amount, glDriverKey: `EARN:${c.code}`, glDriverLabel: `${c.nameEn} expense account`, eosbIncluded: true, eosbExclusionReason: null, ...extra });

const change = (id: string, over: object) => ({
  id, batchId: null, source: 'Manual', employeeId: 901, employeeCode: 'EMP-901', employeeName: 'Layla Test', basicSalary: 6000, housingAllowance: 1500, transportAllowance: 0,
  foodAllowance: 0, mobileAllowance: 0, otherAllowance: 0, fixedDeduction: 0, effectiveDate: '2026-12-01', currency: 'SAR', previousBasicSalary: 5000, status: 'Pending',
  requestedByName: 'Hana HR', requestedAtUtc: '2026-11-05T08:00:00Z', decidedByName: '', decidedAtUtc: null, decisionNote: '', ...over,
});
const changes = [
  change('c-moved', {
    employeeName: 'Layla Test', allowancesCarriedForward: false,
    currentAllowances: [line(meal, 'Amount', 300), line(car, 'PercentOfBasic', 500, 0.1)],
    proposedAllowances: [line(meal, 'Amount', 300), line(car, 'PercentOfBasic', 600, 0.1), line({ code: 'ALW_PHONE', nameEn: 'Phone', nameAr: 'هاتف' }, 'Amount', 40, null, { eosbIncluded: false, eosbExclusionReason: 'Reimbursement, not wage' })],
  }),
  change('c-carried', {
    employeeId: 902, employeeName: 'Omar Test', allowancesCarriedForward: true,
    currentAllowances: [line(meal, 'Amount', 300)], proposedAllowances: [line(meal, 'Amount', 300)],
  }),
  change('c-nolabel', {
    employeeId: 903, employeeName: 'Nadia Test', allowancesCarriedForward: false,
    currentAllowances: [], proposedAllowances: [line(meal, 'Amount', 300, null, { glDriverLabel: null })],
  }),
];

const HR = ['dashboard.read', 'payroll.read', 'payroll.write', 'payroll.approve', 'employees.read', 'entitlements.read'];

// The salary list (GET /api/payroll/employee-salary-structures): each record carries its allowance lines, named; null when the viewer may not see amounts.
const salaryRecord = (allowances: unknown) => ({
  id: 'rec-901', employeeId: 901, salaryStructureId: 'ss-1', basicSalary: 5000, housingAllowance: 1000, transportAllowance: 0, foodAllowance: 0, mobileAllowance: 0,
  otherAllowance: 0, fixedDeduction: 0, effectiveDate: '2026-10-17', currency: 'SAR', isActive: true, createdAtUtc: '2026-10-17T08:00:00Z', allowances,
});
const mealOnRecord = { code: 'ALW_MEAL', nameEn: 'Meal', nameAr: 'وجبات', basis: 'Amount', rate: null, amount: 300 };

async function bootPayroll(page: Page, locale: Locale, opts: { onApprove?: (id: string) => { status: number; body: unknown }; permissions?: string[]; recordAllowances?: unknown } = {}) {
  const posts: { path: string; body: any }[] = [];
  await page.addInitScript(([lang]) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone-locale-choice-v2', lang);
  }, [locale]);
  await page.route('**/api/**', route => {
    const request = route.request(); const p = new URL(request.url()).pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json({ id: 'u-hr', tenantId: 't1', tenantSlug: 'solo', email: 'hr@solo.test', fullName: 'Hana HR', roles: ['Admin'], accountType: 'Tenant', isGroupScope: true, companies: [], permissions: opts.permissions ?? HR });
    if (p === '/api/features/disabled-keys') return json([]);
    if (p === '/api/payroll/salary-changes/approver-status') return json({ hasEligibleApprover: true });
    if (p === '/api/payroll/salary-changes' && request.method() === 'GET') return json(changes);
    const decide = /^\/api\/payroll\/salary-changes\/([^/]+)\/(approve|reject)$/.exec(p);
    if (decide && request.method() === 'POST') { const r = opts.onApprove?.(decide[1]) ?? { status: 200, body: {} }; return json(r.body, r.status); }
    if (p === '/api/entitlements/matrix') return json(matrix);
    // Assign Salary reads the current lines from this list, not from the employee (which needs employees.sensitive).
    if (p === '/api/payroll/employee-salary-structures' && request.method() === 'GET') return json([salaryRecord('recordAllowances' in opts ? opts.recordAllowances : [mealOnRecord])]);
    if (p === '/api/payroll/employee-salary-structures' && request.method() === 'POST') { posts.push({ path: p, body: request.postDataJSON() }); return json({ id: 'new' }, 202); }
    if (p === '/api/payroll/overview') return json({ year: 2026, month: 11, totalCompanies: 0, totalActiveEmployees: 0, totalGrossPayroll: 0, totalNetPayroll: 0, totalValidationErrors: 0, totalPendingApprovals: 0, companies: [] });
    if (p === '/api/tenant-admin/localization') return json({ countryCode: 'SA', currencyCode: 'SAR', defaultLanguage: locale });
    if (p.startsWith('/api/features') || p === '/api/notifications') return json([]);
    return json([]);
  });
  await page.goto('/payroll');
  await page.getByRole('tab', { name: say(locale, 'Employee Salary') }).click();
  return { posts };
}

for (const locale of ['en', 'ar'] as const) {
  test.describe(`Finance approval of a salary change (${locale})`, () => {
    test('current beside proposed, every difference in words, and the liability Finance takes on', async ({ page }) => {
      await bootPayroll(page, locale);
      const moved = page.getByTestId('salary-change-row').filter({ hasText: 'Layla Test' });
      await expect(moved).toBeVisible({ timeout: 30_000 });
      const rows = moved.getByTestId('salary-allowance-row');
      await expect(rows).toHaveCount(3);
      // Difference is a word and a mark in the row, not a colour: changed, unchanged and new each say so (data-change is on the row).
      await expect(moved.locator('tr[data-change="changed"]')).toHaveText(new RegExp(say(locale, 'Changed')));
      await expect(moved.locator('tr[data-change="unchanged"]')).toHaveText(new RegExp(say(locale, 'Unchanged')));
      await expect(moved.locator('tr[data-change="added"]')).toHaveText(new RegExp(say(locale, 'New')));
      await expect(moved.locator('tr[data-change="changed"]')).toContainText(locale === 'ar' ? car.nameAr : car.nameEn);
      await expect(moved.getByTestId('salary-allowance-carried')).toHaveText(say(locale, 'This change sets the monthly allowances shown below.'));
      // Where each posts and whether it counts in end of service, with the reason when it does not.
      const phone = moved.locator('tr[data-change="added"]');
      // The account is named in words (the server's glDriverLabel), not by the bare key.
      await expect(phone).toContainText('Phone expense account');
      await expect(phone).not.toContainText('EARN:ALW_PHONE');
      await expect(phone).toContainText('Reimbursement, not wage');
      await expect(moved.locator('tr[data-change="unchanged"]')).toContainText(say(locale, 'Counts toward end of service pay'));
    });

    test('a line the server sent without a label falls back to the bare key', async ({ page }) => {
      await bootPayroll(page, locale);
      const row = page.getByTestId('salary-change-row').filter({ hasText: 'Nadia Test' });
      await expect(row.locator('tr[data-change="added"]')).toContainText('EARN:ALW_MEAL', { timeout: 30_000 });
    });

    test('a change that does not mention allowances says they carry on unchanged', async ({ page }) => {
      await bootPayroll(page, locale);
      const carried = page.getByTestId('salary-change-row').filter({ hasText: 'Omar Test' });
      await expect(carried.getByTestId('salary-allowance-carried')).toHaveText(say(locale, 'This change does not touch the monthly allowances; the current ones carry on unchanged.'), { timeout: 30_000 });
      await expect(carried.locator('tr[data-change="unchanged"]')).toHaveCount(1);
    });

    test('a stale approval is refused with the reason, and nothing looks approved', async ({ page }) => {
      const reason = 'ALW_PHONE is not a monthly cash allowance that is in use: it was removed or switched off in Benefits by grade. Reject this change, or fix the allowance and approve again.';
      await bootPayroll(page, locale, { onApprove: () => ({ status: 409, body: { error: 'salary_change_stale', message: reason } }) });
      const moved = page.getByTestId('salary-change-row').filter({ hasText: 'Layla Test' });
      await moved.getByRole('button', { name: say(locale, 'Approve') }).click();
      const stale = moved.getByTestId('salary-change-stale');
      await expect(stale).toContainText(say(locale, 'This change is out of date, so it was not approved. Nothing was paid or changed.'));
      await expect(stale).toContainText(reason);
      await expect(stale).toHaveAttribute('role', 'alert');
      await expect(moved).toBeVisible(); // still pending
    });
  });
}

test.describe('Assign Salary: the monthly allowances section', () => {
  const start = async (page: Page, opts: Parameters<typeof bootPayroll>[2] = {}) => {
    const { posts } = await bootPayroll(page, 'en', opts);
    await page.getByRole('button', { name: 'Assign Salary' }).click();
    await page.getByLabel('Employee ID', { exact: true }).fill('901');
    await page.getByLabel('Basic Salary *').fill('5000');
    return { posts, section: page.getByTestId('salary-allowances') };
  };
  const open = async (page: Page) => {
    const { posts, section } = await start(page);
    // What untouched means is read from the salary list's record for the employee: the lines stay as they are.
    await expect(section.getByTestId('salary-allowances-note')).toHaveText('These stay as they are on the new salary. A percentage is worked out again on the new basic salary.');
    await expect(section.getByTestId('salary-allowance-line')).toHaveCount(1);
    return { posts, section };
  };

  test('a user who cannot list Benefits by grade still sees the existing lines by name, and cannot change them', async ({ page }) => {
    const { posts, section } = await start(page, { permissions: HR.filter(p => p !== 'entitlements.read') });
    const lines = section.getByTestId('salary-allowance-line');
    await expect(lines).toHaveCount(1);
    await expect(lines.first()).toContainText('Meal');
    await expect(lines.first()).toContainText('300.00');
    await expect(section).toContainText('You can’t add or change monthly allowances here. The ones already on the salary stay as they are.');
    await expect(section.getByRole('button', { name: 'Add an allowance' })).toHaveCount(0);
    await page.getByRole('button', { name: 'Assign Salary' }).last().click();
    await expect.poll(() => posts.length).toBe(1);
    expect(Object.keys(posts[0].body)).not.toContain('allowances'); // untouched: carried forward by the server
  });

  test('when the list masks the lines, the section says they stay as they are and sends none', async ({ page }) => {
    const { posts, section } = await start(page, { recordAllowances: null });
    await expect(section.getByTestId('salary-allowances-note')).toHaveText('The allowances already on this employee’s salary stay as they are. Add a line to set the allowances yourself instead.');
    await expect(section.getByTestId('salary-allowance-line')).toHaveCount(0);
    await page.getByRole('button', { name: 'Assign Salary' }).last().click();
    await expect.poll(() => posts.length).toBe(1);
    expect(Object.keys(posts[0].body)).not.toContain('allowances');
  });

  test('untouched sends no allowances at all, so the server carries the current lines forward', async ({ page }) => {
    const { posts } = await open(page);
    await page.getByRole('button', { name: 'Assign Salary' }).last().click();
    await expect.poll(() => posts.length).toBe(1);
    expect(posts[0].body.basicSalary).toBe(5000);
    expect(Object.keys(posts[0].body)).not.toContain('allowances');
  });

  test('adding a percentage line shows what it comes to, counts in the gross, and sends the list', async ({ page }) => {
    const { posts, section } = await open(page);
    await section.getByRole('button', { name: 'Add an allowance' }).click();
    await section.getByLabel('Allowance, line 2').selectOption('ALW_CAR');
    await section.getByLabel('Fixed amount or percentage of basic, line 2').selectOption('PercentOfBasic');
    await section.getByLabel('Percentage of basic, line 2').fill('10');
    await expect(section.getByTestId('salary-allowance-computed')).toContainText('500.00');
    await expect(section.getByTestId('salary-allowances-total')).toContainText('800.00'); // 300 + 500
    await expect(page.getByTestId('gross-includes-allowances')).toContainText('800.00');
    await expect(page.getByText(/Gross Total:.*5,800\.00/)).toBeVisible();
    await page.getByRole('button', { name: 'Assign Salary' }).last().click();
    await expect.poll(() => posts.length).toBe(1);
    expect(posts[0].body.allowances).toEqual([
      { code: 'ALW_MEAL', basis: 'Amount', amount: 300 },
      { code: 'ALW_CAR', basis: 'PercentOfBasic', rate: 0.1 },
    ]);
  });

  test('removing every line is a deliberate empty list, and a half-filled line is refused before anything is sent', async ({ page }) => {
    const { posts, section } = await open(page);
    await section.getByRole('button', { name: 'Remove allowance line 1' }).click();
    await section.getByRole('button', { name: 'Add an allowance' }).click();
    await page.getByRole('button', { name: 'Assign Salary' }).last().click();
    await expect(section.getByRole('alert')).toContainText('Choose the allowance for this line.');
    expect(posts).toHaveLength(0);
    await section.getByRole('button', { name: 'Remove allowance line 1' }).click();
    await page.getByRole('button', { name: 'Assign Salary' }).last().click();
    await expect.poll(() => posts.length).toBe(1);
    expect(posts[0].body.allowances).toEqual([]);
  });
});

test.describe('Employee profile and Edit employee', () => {
  const breakdown = {
    basicSalary: 5000, housingAllowance: 1000, transportAllowance: 0, foodAllowance: 0, mobileAllowance: 0, otherAllowance: 0, fixedDeduction: 0,
    currency: 'SAR', effectiveDate: '2026-10-17', salaryStructureCode: 'DIRECT',
    // The server names each line (nameEn/nameAr), so the profile needs no catalogue access.
    allowances: [{ code: 'ALW_MEAL', nameEn: meal.nameEn, nameAr: meal.nameAr, basis: 'Amount', rate: null, amount: 300 }, { code: 'ALW_CAR', nameEn: car.nameEn, nameAr: car.nameAr, basis: 'PercentOfBasic', rate: 0.1, amount: 500 }],
  };
  const permissions = ['employees.read', 'employees.write', 'employees.sensitive', 'dashboard.read', 'entitlements.read'];
  test.beforeEach(() => { (employees[901] as unknown as { salaryBreakdown?: unknown }).salaryBreakdown = breakdown; });
  test.afterEach(() => { delete (employees[901] as unknown as { salaryBreakdown?: unknown }).salaryBreakdown; });
  const withMatrix = (page: Page) => page.route('**/api/entitlements/matrix*', route => route.fulfill({ json: matrix }));

  for (const locale of ['en', 'ar'] as const) {
    test(`the profile lists the allowances in force by name, basis and amount (${locale})`, async ({ page }) => {
      await bootEmployeeWorkspaceFixture(page, { permissions, disabledFeatures: [], locale });
      await withMatrix(page);
      await page.goto('/people/901?section=pay-benefits');
      const list = page.getByTestId('employee-salary-allowances');
      await expect(list).toBeVisible();
      const items = list.getByTestId('employee-salary-allowance');
      await expect(items).toHaveCount(2);
      await expect(items.first()).toContainText(locale === 'ar' ? meal.nameAr : meal.nameEn);
      await expect(items.first()).toContainText('300.00');
      await expect(items.nth(1)).toContainText(locale === 'ar' ? car.nameAr : car.nameEn);
      await expect(items.nth(1)).toContainText('500.00');
      await expect(items.nth(1)).toContainText('10%');
    });
  }

  test('a reader without entitlements.read still sees the lines by name, not by code', async ({ page }) => {
    await bootEmployeeWorkspaceFixture(page, { permissions: permissions.filter(p => p !== 'entitlements.read'), disabledFeatures: [] });
    await page.route('**/api/entitlements/matrix*', route => route.fulfill({ status: 403, json: { error: 'forbidden' } }));
    await page.goto('/people/901?section=pay-benefits');
    const items = page.getByTestId('employee-salary-allowances').getByTestId('employee-salary-allowance');
    await expect(items).toHaveCount(2);
    await expect(items.first()).toContainText(meal.nameEn);
    await expect(items.first()).not.toContainText('ALW_MEAL');
    await expect(items.nth(1)).toContainText(car.nameEn);
  });

  test('a basic-only edit sends no allowances, so the server carries them forward', async ({ page }) => {
    const fixture = await bootEmployeeWorkspaceFixture(page, { permissions, disabledFeatures: [] });
    await withMatrix(page);
    await page.goto('/people/901/edit?section=pay-benefits&panel=salary');
    const flow = page.getByTestId('employee-edit-flow');
    await expect(flow.getByTestId('salary-allowances')).toBeVisible();
    await expect(flow.getByTestId('salary-allowance-line')).toHaveCount(2);
    await flow.locator('[id="edit-field-salaryBreakdown.basicSalary"]').fill('5200');
    await flow.getByRole('button', { name: /^Step 6: Review/ }).click();
    await flow.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect.poll(() => fixture.mutations.filter(m => m.method === 'PUT').length).toBe(1);
    const body = fixture.mutations.find(m => m.method === 'PUT')!.body as { changes: { salaryBreakdown: Record<string, unknown> } };
    expect(body.changes.salaryBreakdown.basicSalary).toBe(5200);
    expect(Object.keys(body.changes.salaryBreakdown)).not.toContain('allowances');
  });

  test('changing the allowances goes to the review and is sent as exactly the lines shown', async ({ page }) => {
    const fixture = await bootEmployeeWorkspaceFixture(page, { permissions, disabledFeatures: [] });
    await withMatrix(page);
    await page.goto('/people/901/edit?section=pay-benefits&panel=salary');
    const flow = page.getByTestId('employee-edit-flow');
    const section = flow.getByTestId('salary-allowances');
    await section.getByRole('button', { name: 'Remove allowance line 2' }).click();
    await expect(section.getByTestId('salary-allowances-note')).toHaveText('You changed these. Saving sends exactly the lines shown here.');
    await flow.getByRole('button', { name: /^Step 6: Review/ }).click();
    await expect(flow).toContainText('Other monthly allowances');
    await expect(flow).toContainText('Meal');
    await flow.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect.poll(() => fixture.mutations.filter(m => m.method === 'PUT').length).toBe(1);
    const body = fixture.mutations.find(m => m.method === 'PUT')!.body as { changes: { salaryBreakdown: { allowances: unknown } } };
    expect(body.changes.salaryBreakdown.allowances).toEqual([{ code: 'ALW_MEAL', basis: 'Amount', amount: 300 }]);
  });
});
