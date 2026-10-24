import { expect, test, type Page } from '@playwright/test';
import { openSetupSection } from './setupSections';

// Browser contract tests only: every API response is synthetic and no tenant is changed.
const companies = [
  { id: 'evostel', legalNameEn: 'Evostel LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' },
  { id: 'other', legalNameEn: 'Other Company', countryCode: 'ARE', defaultCurrency: 'AED', isActive: true, approvalStatus: 'Active' },
];
async function boot(page: Page) {
  const writes: string[] = [];
  await page.addInitScript(() => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', 'en'); });
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (request.method() !== 'GET') writes.push(path);
    if (path === '/api/auth/me') return reply({ id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: ['Admin'], permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'ai.query', 'dashboard.read'], accountType: 'Group', isGroupScope: true, companies: companies.map(c => ({ id: c.id, name: c.legalNameEn, code: c.id, countryCode: c.countryCode, isActive: true })) });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/companies') return reply({ items: companies, total: 2, page: 1, pageSize: 100 });
    // policy_intake is ON in this fixture: it is absent from the disabled keys (the real API lists it until the platform team enables it).
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path === '/api/setup-assistant/preview') return reply({ configurationVersion: 1, engine: 'Browser fixture', notes: [], draft: {
      branches: [{ code: 'HQ', nameEn: 'Head Office', city: 'Dammam', isHeadOffice: true }], departments: [], costCenters: [], designations: [], grades: [], gradePayComponents: [], leaveTypes: [], shifts: [], payComponents: [], statutoryRules: [], leavePolicies: [], benefitPlans: [],
      workingWeek: null, employeeIdRule: null, hrConfig: null, holidayCalendar: null, attendancePolicy: null, overtimePolicy: null, localization: null,
    } });
    if (path.includes('/features/') || path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto('/setup?tab=aiSetup&path=policyStarter&companyId=evostel');
  await expect(page.getByRole('heading', { name: 'Start with your company', exact: true })).toBeVisible({ timeout: 60_000 });
  return writes;
}

test('save, reload, back and policy-library round trip preserve inputs without applying', async ({ page }) => {
  const writes = await boot(page);
  await expect(page.getByLabel(/^Country/)).toHaveValue('SA');
  await page.getByLabel(/^Industry/).fill('Services');
  await page.getByLabel(/^Suggested head office city/).fill('Dammam');
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await page.getByRole('button', { name: 'Save for later', exact: true }).click();
  await page.reload();
  await expect(page.getByRole('heading', { name: 'How does your team work?', exact: true })).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Saved draft resumed' })).toBeVisible();
  await page.getByRole('button', { name: 'Back', exact: true }).click();
  await expect(page.getByLabel(/^Suggested head office city/)).toHaveValue('Dammam');
  await page.getByRole('button', { name: 'Policy library / updates', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Policy library', exact: true })).toBeVisible();
  await openSetupSection(page, 'Policy starter');
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Services');
  await expect(page.getByLabel(/^Suggested head office city/)).toHaveValue('Dammam');
  expect(writes).toEqual([]);
});

test('edited generated preview survives reload and requires explicit review before Apply', async ({ page }) => {
  const writes = await boot(page);
  await page.getByLabel(/^Industry/).fill('Services');
  for (let index = 0; index < 4; index++) await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await page.getByRole('button', { name: 'Generate draft', exact: true }).click();
  await page.getByRole('button', { name: 'Edit HQ', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill('Reviewed Eastern Office');
  await page.getByRole('button', { name: 'Save for later', exact: true }).click();
  await page.reload();
  await expect(page.getByRole('button', { name: 'Review saved preview', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: /^Apply .*item/ })).toHaveCount(0);
  await page.getByRole('button', { name: 'Review saved preview', exact: true }).click();
  await expect(page.getByText('Reviewed Eastern Office · Dammam · Head office', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Apply 1 item(s) to Evostel LLC', exact: true })).toBeVisible();
  expect(writes).toEqual(['/api/setup-assistant/preview']);
  await page.getByRole('button', { name: 'Back', exact: true }).click();
  for (let index = 0; index < 3; index++) await page.getByRole('button', { name: 'Back', exact: true }).click();
  await page.getByLabel(/^Industry/).fill('Changed services');
  await expect(page.getByRole('button', { name: 'Review saved preview', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Save for later', exact: true }).click();
  await page.reload();
  await expect(page.getByRole('button', { name: 'Review saved preview', exact: true })).toHaveCount(0);
  expect(writes).toEqual(['/api/setup-assistant/preview']);
});

test('saved previews remain isolated when another company opens the Policy starter', async ({ page }) => {
  await boot(page);
  await page.getByLabel(/^Industry/).fill('Services');
  for (let index = 0; index < 4; index++) await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await page.getByRole('button', { name: 'Generate draft', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Apply 1 item(s) to Evostel LLC', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Save for later', exact: true }).click();
  await page.goto('/setup?tab=aiSetup&path=policyStarter&companyId=other');
  await expect(page.getByLabel(/^Legal entity name/)).toHaveValue('Other Company');
  await expect(page.getByRole('button', { name: 'Review saved preview', exact: true })).toHaveCount(0);
  await page.goto('/setup?tab=aiSetup&path=policyStarter&companyId=evostel');
  await expect(page.getByRole('button', { name: 'Review saved preview', exact: true })).toBeVisible();
});

test('switching company isolates draft inputs and returning resumes original company', async ({ page }) => {
  await boot(page);
  await page.getByLabel(/^Industry/).fill('Evostel services');
  await page.getByRole('button', { name: 'Save for later', exact: true }).click();
  await page.goto('/setup?tab=aiSetup&path=policyStarter&companyId=other');
  await expect(page.getByLabel(/^Legal entity name/)).toHaveValue('Other Company');
  await expect(page.getByLabel(/^Industry/)).toHaveValue('');
  await expect(page.getByLabel(/^Country/)).toHaveValue('AE');
  await page.goto('/setup?tab=aiSetup&path=policyStarter&companyId=evostel');
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Evostel services');
});

test('cancel requires confirmation and discard removes saved inputs', async ({ page }) => {
  const writes = await boot(page);
  await page.getByLabel(/^Industry/).fill('Keep unless confirmed');
  await page.getByRole('button', { name: 'Save for later', exact: true }).click();
  await page.getByRole('button', { name: 'Cancel setup', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByLabel(/^Industry/)).toHaveValue('Keep unless confirmed');
  await page.getByRole('button', { name: 'Cancel setup', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm', exact: true }).click();
  await page.goto('/setup?tab=aiSetup&path=policyStarter&companyId=evostel');
  await expect(page.getByLabel(/^Industry/)).toHaveValue('');
  expect(writes).toEqual([]);
});
