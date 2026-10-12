import { expect, test, type Page } from '@playwright/test';

async function boot(page: Page, manage = true, rejectSave = false) {
  const saves: Record<string, unknown>[] = [];
  const company = { id: 'company-benefits', name: 'Benefits Test Company', code: 'TEST', countryCode: 'SA', isActive: true };
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'benefit-setup-fixture'));
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (path === '/api/auth/me') return reply({ id: 'benefit-admin', tenantId: 'benefit-tenant', tenantSlug: 'benefits-fixture', fullName: 'HR Manager', roles: ['HR Manager'], permissions: ['employees.read', 'employees.write', 'employees.approve', ...(manage ? ['approvals.manage'] : [])], isGroupScope: false, accountType: 'SingleCompany', companies: [company] });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/approval-workflows' && request.method() === 'POST') {
      saves.push(request.postDataJSON());
      if (rejectSave) return route.fulfill({ status: 400, json: { message: 'Complete or withdraw pending requests before changing this route.' } });
      return reply({ id: 'route-benefits', ...request.postDataJSON() });
    }
    if (path === '/api/compensation/benefits/plans' || path === '/api/compensation/benefits/enrollments' || path.includes('/features/') || path === '/api/notifications') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto('/benefits');
  await expect(page.getByRole('heading', { name: 'Benefits Administration' })).toBeVisible();
  return saves;
}

test('administrator configures an ordered route with only the last step final', async ({ page }) => {
  const saves = await boot(page);
  await page.getByRole('button', { name: 'Additional benefit approvals', exact: true }).click();
  const modal = page.getByRole('dialog', { name: 'Additional benefit approvals' });
  await expect(modal.getByLabel('Approver role for step 1')).toHaveValue('HR Manager');
  await modal.getByRole('button', { name: 'Add approval step' }).click();
  await expect(modal.getByRole('button', { name: 'Save approval route' })).toBeDisabled();
  await modal.getByLabel('Approver role for step 2').fill('Admin');
  await modal.getByRole('button', { name: 'Save approval route' }).click();
  await expect(modal.getByRole('status')).toHaveText('Approval route saved.');
  expect(saves).toHaveLength(1);
  expect(saves[0]).toMatchObject({ entityName: 'BenefitAdditionalGrant', isDefault: true, isActive: true, departmentId: null, gradeId: null,
    steps: [{ stepOrder: 1, approverRole: 'HR Manager', approverType: 'Role', isFinalStep: false }, { stepOrder: 2, approverRole: 'Admin', approverType: 'Role', isFinalStep: true }] });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('route save failure preserves the proposed route and displays the server reason', async ({ page }) => {
  await boot(page, true, true);
  await page.getByRole('button', { name: 'Additional benefit approvals', exact: true }).click();
  const modal = page.getByRole('dialog', { name: 'Additional benefit approvals' });
  await modal.getByLabel('Approver role for step 1').fill('Admin');
  await modal.getByRole('button', { name: 'Save approval route' }).click();
  await expect(modal.getByRole('alert')).toContainText('Complete or withdraw pending requests');
  await expect(modal.getByLabel('Approver role for step 1')).toHaveValue('Admin');
});

test('HR without workflow management permission cannot open route setup', async ({ page }) => {
  await boot(page, false);
  await expect(page.getByRole('button', { name: 'Additional benefit approvals', exact: true })).toHaveCount(0);
});
