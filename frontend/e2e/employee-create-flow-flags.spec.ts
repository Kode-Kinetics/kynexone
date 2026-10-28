import { expect, test, type Page } from '@playwright/test';

/**
 * CD57: the Add Employee entry never renders a blank screen and the legacy dialog is the default.
 * Errored flags show the legacy dialog at once; unanswered flags keep the module gate's busy state.
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

// Flags that never answer are held at the route level: ModuleGate shows its own aria-busy spinner until the
// request settles, so the entry component is not reached (its own 4 s fallback is covered by unit/createFlowMode.spec.ts).
test('flags that never answer: the module gate shows a busy state, not a blank page', async ({ page }) => {
  await boot(page, 'hang');
  await expect(page.locator('main [aria-busy="true"]')).toBeVisible();
});

test('flags that error: the legacy dialog straight away', async ({ page }) => {
  await boot(page, 'error');
  await expect(page.getByRole('dialog', { name: 'Add Employee', exact: true })).toBeVisible({ timeout: 3_000 });
});
