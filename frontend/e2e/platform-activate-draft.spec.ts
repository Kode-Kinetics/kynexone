import { expect, test, type Page, type Request } from '@playwright/test';

/**
 * G-013: the company draft-approval mode is retired. The platform console no longer offers it, reads a tenant still holding it
 * as the default mode, and gives the one-time "Activate" for a company already stuck in Draft (after a confirmation).
 * Every API call is intercepted; nothing is written. Run: npx playwright test -c e2e/playwright.hotfix-dead-ends.config.ts
 */

const TENANT_ID = '11111111-1111-4111-8111-111111111111';

function tenant(over: Record<string, unknown> = {}) {
  return {
    id: TENANT_ID, name: 'Gulf Holding', slug: 'gulf', isActive: true, accountType: 'Group', companyCreationMode: 'GroupSelfServiceWithinLimit',
    companies: [
      { id: 'co-1', name: 'Gulf Holding KSA', code: 'KSA', countryCode: 'SA', isActive: true, approvalStatus: 'Active' },
      { id: 'co-2', name: 'Gulf Holding UAE', code: 'UAE', countryCode: 'AE', isActive: false, approvalStatus: 'Draft' },
    ],
    subscription: { plan: 'Pro', status: 'Active', maxEmployees: 100, maxUsers: 50, billingEmail: 'b@x.test', billingCycle: 'Monthly', monthlyAmount: 0, currencyCode: 'SAR', startedAtUtc: '2026-01-01T00:00:00Z', expiresAtUtc: null },
    featureFlags: [], localization: null, branding: null, userCount: 3, employeeCount: 10, ...over,
  };
}

async function boot(page: Page, over: Record<string, unknown> = {}, activateStatus = 200) {
  const errors: string[] = [];
  const posts: string[] = [];
  let current = tenant(over);
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(() => localStorage.setItem('platform_access_token', 'fixture'));
  await page.route('**/api/platform/**', async (route) => {
    const request: Request = route.request();
    const path = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (path === `/api/platform/tenants/${TENANT_ID}`) return json(current);
    if (path === `/api/platform/tenants/${TENANT_ID}/companies/co-2/approve` && request.method() === 'POST') {
      posts.push(path);
      if (activateStatus !== 200) return json({ error: 'company_not_draft', message: 'Only a company still in Draft can be activated here.' }, activateStatus);
      current = { ...current, companies: (current.companies as Array<Record<string, unknown>>).map(c => c.id === 'co-2' ? { ...c, isActive: true, approvalStatus: 'Active' } : c) } as ReturnType<typeof tenant>;
      return json({ companyId: 'co-2', approvalStatus: 'Active', isActive: true });
    }
    if (path === '/api/platform/auth/mfa/status') return json({ enrolled: true, promptToEnroll: false });
    return json([]);
  });
  await page.goto(`/platform/tenants/${TENANT_ID}?tab=governance`);
  return { errors, posts };
}

/** The label is not wired to its select in the console, so find the select inside the same block. */
const creationMode = (page: Page) => page.locator('div', { has: page.getByText('Company creation mode', { exact: true }) }).locator('select').last();

test.describe('Platform console: companies stuck in Draft', () => {
  test('the creation-mode choice no longer offers draft approval', async ({ page }) => {
    await boot(page);
    const select = creationMode(page);
    await expect(select).toBeVisible({ timeout: 60_000 });
    await expect(select.locator('option')).toHaveText(['Group self-service (within limit)', 'Platform controlled']);
    await expect(page.getByText('Group drafts + platform approval')).toHaveCount(0);
  });

  test('a tenant still on the retired mode reads as the default mode', async ({ page }) => {
    await boot(page, { companyCreationMode: 'GroupDraftPlatformApproval' });
    await expect(creationMode(page)).toHaveValue('GroupSelfServiceWithinLimit', { timeout: 60_000 });
  });

  test('a Draft company is activated after a confirmation, once', async ({ page }) => {
    const { errors, posts } = await boot(page);
    const draftRow = page.locator('tr', { hasText: 'Gulf Holding UAE' });
    await expect(draftRow).toContainText('Draft — not active yet', { timeout: 60_000 });
    await draftRow.getByRole('button', { name: 'Activate' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('Activate this company?');
    await expect(dialog).toContainText('recorded in the audit trail');
    expect(posts).toHaveLength(0);
    await dialog.getByRole('button', { name: 'Activate' }).click();
    await expect(draftRow).toContainText('Active');
    await expect(draftRow.getByRole('button', { name: 'Activate' })).toHaveCount(0);
    expect(posts).toEqual([`/api/platform/tenants/${TENANT_ID}/companies/co-2/approve`]);
    expect(errors).toEqual([]);
  });

  test('cancelling the confirmation activates nothing', async ({ page }) => {
    const { posts } = await boot(page);
    const draftRow = page.locator('tr', { hasText: 'Gulf Holding UAE' });
    await draftRow.getByRole('button', { name: 'Activate' }).click({ timeout: 60_000 });
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();
    await expect(draftRow).toContainText('Draft — not active yet');
    expect(posts).toHaveLength(0);
  });

  test('a refusal leaves the company in Draft and says so', async ({ page }) => {
    await boot(page, {}, 409);
    const draftRow = page.locator('tr', { hasText: 'Gulf Holding UAE' });
    await draftRow.getByRole('button', { name: 'Activate' }).click({ timeout: 60_000 });
    await page.getByRole('dialog').getByRole('button', { name: 'Activate' }).click();
    await expect(page.getByText('Could not activate the company.')).toBeVisible();
    await expect(draftRow).toContainText('Draft — not active yet');
  });
});
