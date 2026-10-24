import { expect, test, type Page } from '@playwright/test';
import { setupSection } from './setupSections';

// Owner decision (2026-10-20): company policy documents are not uploaded, parsed or sent to AI; only manual
// company setup is enabled. policy_intake is an opt-in flag the real API lists in /api/features/disabled-keys
// until the platform team enables it. Every API request is intercepted: these are browser contracts, never
// tenant writes. Relative URLs only; the base URL comes from the config.

const company = { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };

async function boot(page: Page, url: string, options: { policyIntake: boolean }) {
  const policyCalls: string[] = [];
  const writes: string[] = [];
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
  });
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (path.startsWith('/api/ai/policy') || path.startsWith('/api/setup-assistant/policy')) policyCalls.push(`${request.method()} ${path}`);
    if (request.method() !== 'GET') writes.push(path);
    if (path === '/api/auth/me') return reply({ id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: ['Admin'],
      permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read', 'learning.read', 'leave.read', 'recruitment.read', 'recruitment.write', 'payroll.read', 'performance.read'],
      accountType: 'Group', isGroupScope: true, companies: [{ id: company.id, name: company.legalNameEn, code: company.id, countryCode: 'SAU', isActive: true }] });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/country-packs/available' || path === '/api/admin/system-settings' || path === '/api/admin/master-data/types') return reply([]);
    if (path === '/api/reference/countries') return reply([{ code: 'SA', name: 'Saudi Arabia' }]);
    if (path === '/api/dashboard/full') return route.fulfill({ status: 503, json: { message: 'Dashboard fixture unavailable' } });
    if (path === '/api/recruitment/onboarding/summary') return reply({ total: 0, pending: 0, completed: 0, blocked: 0, completionPct: 0 });
    if (path === '/api/companies') return reply({ items: [company], total: 1, page: 1, pageSize: 100 });
    // The real API's default: policy_intake is listed (off) until the platform team switches it on.
    if (path === '/api/features/disabled-keys') return reply(options.policyIntake ? ['release_a'] : ['release_a', 'policy_intake']);
    if (path.includes('/features/') || path === '/api/notifications') return reply([]);
    if (path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/recruitment/onboarding/checklists' || path.endsWith('/template-tasks')) return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(url);
  return { policyCalls, writes, errors };
}

const onboardingUrl = '/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=onboarding&setting=onboarding';
const studioUrl = '/setup?tab=aiSetup&path=configuration&companyId=fixture-company&area=leave';

test('flag off: Setup → Onboarding is the manual editor only, with no Fill from policy', async ({ page }) => {
  const state = await boot(page, onboardingUrl, { policyIntake: false });
  const focused = page.getByRole('region', { name: 'Onboarding checklists', exact: true });
  await expect(focused.getByRole('heading', { name: 'Onboarding checklists', exact: true })).toBeVisible({ timeout: 30_000 });
  // Manual setup is unchanged and usable.
  await expect(page.getByRole('button', { name: 'Checklist Templates', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create Checklist', exact: true })).toBeVisible();
  // No entry-method switch, no policy upload, no policy wording.
  await expect(page.getByRole('button', { name: 'Fill from policy', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Enter manually', exact: true })).toHaveCount(0);
  await expect(page.getByLabel('Onboarding entry method', { exact: true })).toHaveCount(0);
  await expect(page.getByText('upload a policy', { exact: false })).toHaveCount(0);
  await expect(page.locator('input[type=file]')).toHaveCount(0);
  expect(state.policyCalls, 'no policy endpoint is called while the flag is off').toEqual([]);
  expect(state.writes).toEqual([]);
  expect(state.errors).toEqual([]);
});

test('flag off: Setup shows no policy library, guide or upload', async ({ page }) => {
  const state = await boot(page, studioUrl, { policyIntake: false });
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible({ timeout: 30_000 });
  // Opens the section picker on a phone, so the absence below is checked against the full section list.
  await expect(await setupSection(page, 'Companies')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Policy library', exact: true })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Policy library', exact: true })).toHaveCount(0);

  // A saved deep link to the library falls back to All setup areas (its area is kept) instead of opening a refused page.
  await page.goto('/setup?tab=policyLibrary&area=leave&companyId=fixture-company');
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('heading', { level: 2, name: 'Policy library', exact: true })).toHaveCount(0);

  // Policy starter (the old studio, its own Setup section since Guided setup became the company chooser): manual
  // setup works; the policy guide, upload and library entry are gone.
  await page.goto('/setup?tab=aiSetup&path=policyStarter&companyId=fixture-company');
  await expect(page.getByRole('heading', { name: 'Start with your company', exact: true })).toBeVisible();
  await expect(page.getByLabel(/^Industry/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save for later', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Add your HR policy', exact: true })).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Existing HR policy', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Policy library / updates', exact: true })).toHaveCount(0);
  await expect(page.locator('input[type=file]')).toHaveCount(0);
  expect(state.policyCalls).toEqual([]);
  expect(state.writes).toEqual([]);
  expect(state.errors).toEqual([]);
});

// Positive control: the same fixture with the flag on still offers policy intake, so the assertions above
// prove the flag hides it rather than the page failing to render it at all.
test('flag on: the policy entry points come back', async ({ page }) => {
  await boot(page, onboardingUrl, { policyIntake: true });
  await expect(page.getByRole('button', { name: 'Fill from policy', exact: true })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('button', { name: 'Enter manually', exact: true })).toBeVisible();
  await page.goto(studioUrl);
  await expect(page.getByTestId('setup-studio-workspace')).toBeVisible({ timeout: 30_000 });
  // The library is a Setup section of its own while policy intake is on (the Studio's duplicate button is gone).
  await expect(await setupSection(page, 'Policy library')).toBeVisible();
  await page.goto('/setup?tab=aiSetup&path=policyStarter&companyId=fixture-company');
  await expect(page.getByRole('button', { name: 'Add your HR policy', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Policy library / updates', exact: true })).toBeVisible();
});
