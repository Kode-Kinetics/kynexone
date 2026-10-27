import { expect, test, type Page } from '@playwright/test';

/**
 * CD57: the Add Employee entry never renders a blank screen and the legacy dialog is the default.
 * Unanswered flags show a status message and then the legacy dialog; errored flags show it at once.
 */
const company = {
  id: 'company-flags', legalNameEn: 'Flags Test Company', legalNameAr: '', tradeName: 'Flags Test Company',
  countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', emailDomain: 'example.test',
  workEmailPattern: 'first.last', isActive: true, approvalStatus: 'Active',
};

async function boot(page: Page, flags: 'hang' | 'error') {
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'flags-browser-fixture'));
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (path === '/api/auth/me') return route.fulfill({ json: {
      id: 'flags-reviewer', tenantId: 'flags-tenant', tenantSlug: 'flags-fixture', fullName: 'HR Reviewer',
      roles: ['HR Manager'], permissions: ['employees.read', 'employees.write'], isGroupScope: false, accountType: 'SingleCompany',
      companies: [{ id: company.id, name: company.legalNameEn, code: 'TEST', countryCode: 'SA', isActive: true }],
    } });
    if (path === '/api/features/disabled-keys' || path === '/api/features/modules') {
      if (flags === 'hang') return; // never answered
      return route.fulfill({ status: 500, json: { message: 'flags unavailable' } });
    }
    if (path === '/api/companies') return route.fulfill({ json: paged([company]) });
    if (path === '/api/notifications' || path.includes('/features/')) return route.fulfill({ json: [] });
    return route.fulfill({ json: paged([]) });
  });
  await page.goto('/people/new');
}

test('flags that never answer: a status message, then the legacy dialog after the wait', async ({ page }) => {
  await boot(page, 'hang');
  await expect(page.getByRole('status').filter({ hasText: 'Loading' })).toBeVisible();
  await expect(page.getByRole('dialog', { name: 'Add Employee', exact: true })).toBeVisible({ timeout: 10_000 });
});

test('flags that error: the legacy dialog straight away', async ({ page }) => {
  await boot(page, 'error');
  await expect(page.getByRole('dialog', { name: 'Add Employee', exact: true })).toBeVisible({ timeout: 3_000 });
});
