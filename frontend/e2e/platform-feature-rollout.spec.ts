// Platform console: opt-in rollout features are switched per tenant, with their real defaults and a confirmation,
// and never offered in the bulk Feature Rollout modal (all mocked; no stack needed).
import { test, expect } from '@playwright/test';
// Representative slice of GET /api/platform/feature-flags. The real list is pinned server-side by
// PlatformFeatureCatalogTests; this spec owns the console's behaviour with it.
const catalog = [
  { key: 'payroll', label: 'Payroll', category: 'Core', defaultEnabled: true },
  { key: 'recruitment', label: 'Recruitment', category: 'Core', defaultEnabled: true },
  { key: 'learning', label: 'Learning', category: 'Rollout (opt-in)', defaultEnabled: false },
  { key: 'succession', label: 'Succession & development', category: 'Rollout (opt-in)', defaultEnabled: false },
  { key: 'engagement', label: 'Engagement surveys', category: 'Rollout (opt-in)', defaultEnabled: false },
  { key: 'compensation', label: 'Merit planning & pay equity', category: 'Rollout (opt-in)', defaultEnabled: false },
  { key: 'release_a', label: 'Release A: entitlements & renewals', category: 'Rollout (opt-in)', defaultEnabled: false },
];
async function mock(page: import('@playwright/test').Page, puts: unknown[]) {
  let flags: Array<{ featureKey: string; isEnabled: boolean }> = [];
  await page.addInitScript(() => localStorage.setItem('platform_access_token', 'fixture'));
  await page.route('**/api/**', async route => {
    const req = route.request(); const path = new URL(req.url()).pathname;
    if (path === '/api/platform/feature-flags') return route.fulfill({ json: catalog });
    if (path === '/api/platform/auth/mfa/status') return route.fulfill({ json: { enrolled: true, required: false } });
    const put = /^\/api\/platform\/tenants\/t1\/features\/([a-z_]+)$/.exec(path);
    if (put && req.method() === 'PUT') { const body = req.postDataJSON(); puts.push({ key: put[1], body }); flags = [...flags.filter(f => f.featureKey !== put[1]), { featureKey: put[1], isEnabled: body.isEnabled }]; return route.fulfill({ json: {} }); }
    if (path === '/api/platform/tenants/t1') return route.fulfill({ json: { id: 't1', name: 'Fixture Tenant', slug: 'fixture', isActive: true, accountType: 'SingleCompany', companies: [],
      subscription: { plan: 'enterprise', status: 'Active', maxEmployees: 100, maxUsers: 20, billingEmail: 'b@example.test', billingCycle: 'monthly', monthlyAmount: 0, currencyCode: 'SAR', startedAtUtc: '2026-01-01T00:00:00Z', expiresAtUtc: null },
      featureFlags: flags, localization: null, branding: null, userCount: 1, activeUserCount: 1, activeEmployeeCount: 1 } });
    if (path === '/api/platform/tenants') return route.fulfill({ json: [{ id: 't1', name: 'Fixture Tenant', slug: 'fixture', isActive: true, plan: 'enterprise', status: 'Active', userCount: 1, employeeCount: 1 }] });
    return route.fulfill({ json: path.endsWith('s') ? [] : {} });
  });
}
test('rollout switches: real defaults, confirmation, and declined confirm changes nothing', async ({ page }) => {
  const puts: unknown[] = []; await mock(page, puts);
  await page.goto('/platform/tenants/t1?tab=features');
  await expect(page.getByText('Rollout (opt-in)', { exact: true })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByRole('switch', { name: 'Disable Recruitment' })).toHaveAttribute('aria-checked', 'true');
  const messages: string[] = [];
  page.once('dialog', d => { messages.push(d.message()); void d.dismiss(); });
  await page.getByRole('switch', { name: 'Enable Release A: entitlements & renewals' }).click();
  await expect.poll(() => messages.length).toBe(1);
  expect(messages[0]).toContain('Benefits by grade');
  expect(puts).toEqual([]);
  page.once('dialog', d => { messages.push(d.message()); void d.accept(); });
  await page.getByRole('switch', { name: 'Enable Learning' }).click();
  await expect(page.getByRole('switch', { name: 'Disable Learning' })).toHaveAttribute('aria-checked', 'true');
  expect(puts).toEqual([{ key: 'learning', body: { isEnabled: true } }]);
});
test('bulk feature modal offers modules only', async ({ page }) => {
  await mock(page, []);
  await page.goto('/platform/tenants');
  await page.getByRole('button', { name: 'Feature Rollout' }).click();
  const select = page.getByLabel('Feature to apply');
  await expect(select.locator('option').first()).not.toHaveText('No features available');
  const options = await select.locator('option').allInnerTexts();
  expect(options.length).toBeGreaterThan(0);
  expect(options.join('|')).not.toMatch(/Learning|Succession|Engagement|Merit|Release A/);
  expect(options.join('|')).toContain('Payroll');
});
