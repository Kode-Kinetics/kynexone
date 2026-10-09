import { expect, test, type Page, type TestInfo } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import type { CompanyProfile, MigrationImportBatchDto, OrgStructureImportRequest, SetupDraft } from '../src/api/setupAssistant';

/**
 * Real browser interactions against synthetic, intercepted API responses.
 * Run from frontend after starting its preview:
 *   npx playwright test -c e2e/playwright.setup-experience.config.ts
 * This proves the setup UI contract, not server authorization or a live deployment.
 */
const company = {
  id: 'setup-company', legalNameEn: 'Setup Fixture Company', legalNameAr: '', tradeName: 'Setup Fixture Company',
  countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active',
};

const draft: SetupDraft = {
  branches: [{ code: 'HQ', nameEn: 'Head Office', city: 'Riyadh', isHeadOffice: true }],
  departments: [{ code: 'OPS', nameEn: 'Operations' }],
  grades: [{ code: 'G1', name: 'Professional', band: 'P', level: 1, minSalary: 5000, midSalary: 7500, maxSalary: 10000, currency: 'SAR' }],
  costCenters: [], designations: [], gradePayComponents: [], leaveTypes: [], shifts: [], payComponents: [],
  statutoryRules: [], leavePolicies: [], workingWeek: null, employeeIdRule: null, hrConfig: null,
  holidayCalendar: null, attendancePolicy: null, overtimePolicy: null, localization: null,
};

interface ApplyRequest {
  draft: SetupDraft;
  countryCode: string;
  currencyCode: string;
  legalEntityName?: string;
}

interface BootOptions {
  missingLocalization?: boolean;
  legacySetup?: boolean;
  forbidFirstApply?: boolean;
  readOnly?: boolean;
  route?: string;
  locale?: 'en' | 'ar';
}

async function boot(page: Page, options: BootOptions = {}) {
  const previews: CompanyProfile[] = [];
  const applies: ApplyRequest[] = [];
  const errors: string[] = [];
  const unexpectedWrites: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'setup-browser-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (path === '/api/auth/me') return reply({
      id: 'setup-reviewer', tenantId: 'setup-tenant', tenantSlug: 'setup-fixture', fullName: 'Setup Reviewer',
      roles: options.readOnly ? ['Auditor'] : ['Admin'],
      permissions: options.readOnly ? ['organization.read'] : ['organization.read', 'organization.write', 'organization.setup.apply', 'dashboard.read', 'employees.approve', 'leave.policy_manage', 'overtime.policy_manage'],
      companies: [{ id: company.id, name: company.legalNameEn, code: 'TEST', countryCode: 'SA', isActive: true }],
    });
    if (path === '/api/tenant-admin/localization') return reply(options.missingLocalization
      ? { countryCode: '', currencyCode: '' }
      : { countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path === '/api/features/modules' || path === '/api/notifications') return reply([]);
    if (path === '/api/companies') return reply(paged([company]));
    if (path === '/api/grades' || path === '/api/branches' || path === '/api/departments' || path === '/api/organization/cost-centers') return reply(paged([]));
    if (path === '/api/setup-assistant/preview') {
      previews.push(request.postDataJSON() as CompanyProfile);
      return reply({ configurationVersion: options.legacySetup ? undefined : 1, draft, engine: 'Synthetic browser fixture', notes: ['Review the proposed rows before applying.'] });
    }
    if (path === '/api/setup-assistant/apply') {
      applies.push(request.postDataJSON() as ApplyRequest);
      if (options.forbidFirstApply && applies.length === 1) return reply({ message: 'This account cannot apply organization setup.' }, 403);
      return reply({ total: 2, applied: { branches: 1, grades: 1 } });
    }
    if (request.method() !== 'GET') unexpectedWrites.push(`${request.method()} ${path}`);
    // Keep every shell/settings request within this synthetic session as well.
    if (path.startsWith('/api/admin/') || path.includes('country-packs')) return reply([]);
    return reply(paged([]));
  });
  await page.goto(options.route ?? '/setup');
  await expect(page.getByRole('heading', { level: 1, name: options.locale === 'ar' ? 'إعداد الشركة' : 'Company setup', exact: true })).toBeVisible({ timeout: 60_000 });
  return { previews, applies, errors, unexpectedWrites };
}

async function companyDetails(page: Page) {
  await page.getByLabel(/^Industry/).fill('Healthcare');
  await page.getByLabel(/^Legal entity name/).fill('Meridian Health LLC');
  await page.getByLabel(/^Suggested head office city/).fill('Jeddah');
}

async function continueStep(page: Page) {
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(page.locator('#setup-aiSetup').getByRole('heading', { level: 2 })).toBeFocused();
}

async function toReview(page: Page) {
  for (let index = 0; index < 4; index++) await continueStep(page);
  await expect(page.getByRole('button', { name: 'Generate draft', exact: true })).toBeVisible();
}

async function generate(page: Page) {
  await page.getByRole('button', { name: 'Generate draft', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Apply 3 item(s) to workspace', exact: true })).toBeVisible();
}

async function contained(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
}

async function accessible(page: Page, selector: string) {
  const result = await new AxeBuilder({ page }).include(selector).analyze();
  expect(result.violations.filter(violation => violation.impact === 'critical' || violation.impact === 'serious')).toEqual([]);
}

async function evidence(page: Page, info: TestInfo, name: string) {
  const path = info.outputPath(`${name}-${info.project.name}.png`);
  await page.screenshot({ path, fullPage: false, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach(name, { path, contentType: 'image/png' });
}

test('preserves the selected profile, reviews editable rows, and retains a draft after apply is refused', async ({ page }, info) => {
  const state = await boot(page, { forbidFirstApply: true });
  await expect(page.getByLabel(/^Country/)).toHaveValue('SA');
  await expect(page.getByLabel(/^Currency/)).toHaveValue('SAR');
  await expect(page.locator('input[type="file"]:visible')).toHaveCount(1);
  await accessible(page, '#setup-aiSetup');
  await companyDetails(page);
  await page.getByLabel(/^Company size/).selectOption('201-500');
  await contained(page);
  await evidence(page, info, 'company-details');
  await continueStep(page);
  await page.getByLabel('How do people work?', { exact: false }).selectOption('TwoShifts');
  await page.getByLabel(/^Weekend/).selectOption('Sat-Sun');
  await page.getByRole('checkbox', { name: 'Biometric device', exact: true }).check();
  await page.getByRole('checkbox', { name: 'Time off in lieu', exact: true }).check();
  await continueStep(page);
  await page.getByLabel(/^Pay cycle/).selectOption('Monthly');
  await page.locator('summary').filter({ hasText: 'Planning preferences (optional)' }).click();
  await page.getByLabel(/^Approval preference/).selectOption('SupervisorFirst');
  await continueStep(page);
  await expect(page.getByRole('heading', { name: 'Salary grades', exact: true })).toBeVisible();
  await continueStep(page);
  await page.getByRole('checkbox', { name: 'Include Public holidays in the generated draft', exact: true }).uncheck();
  expect(state.previews).toHaveLength(0);
  expect(state.applies).toHaveLength(0);
  await generate(page);
  expect(state.previews).toHaveLength(1);
  expect(state.previews[0]).toMatchObject({
    industry: 'Healthcare', legalEntityName: 'Meridian Health LLC', branchCity: 'Jeddah', companySize: '201-500',
    countryCode: 'SA', currencyCode: 'SAR', workPattern: 'TwoShifts', weekendPattern: 'Sat-Sun',
    configuration: { attendanceMethods: ['WebCheckIn', 'BiometricDevice'], overtimeModes: ['PaidOvertime', 'CompensatoryOff'] }, payCycle: 'Monthly',
    payrollModel: 'GradeBased', approvalModel: 'SupervisorFirst',
    sections: { entity: true, org: true, leave: true, leavePolicies: true, shifts: true, attendance: true, payroll: true, holidays: false, governance: true, localization: true },
  });
  expect(state.applies).toHaveLength(0);
  await page.getByRole('button', { name: 'Edit HQ', exact: true }).click();
  const branch = page.locator('li').filter({ has: page.getByRole('button', { name: 'Edit HQ', exact: true }) });
  await branch.getByLabel('Name', { exact: true }).fill('Reviewed Head Office');
  await page.getByRole('button', { name: 'Remove OPS', exact: true }).click();
  const apply = page.getByRole('button', { name: 'Apply 2 item(s) to workspace', exact: true });
  await apply.click();
  await expect(page.getByText(/Applying requires the 'organization\.setup\.apply' permission/)).toBeVisible();
  await expect(branch.getByLabel('Name', { exact: true })).toHaveValue('Reviewed Head Office');
  await expect(page.getByRole('button', { name: 'Remove OPS', exact: true })).toHaveCount(0);
  await expect(apply).toBeEnabled();
  expect(state.applies).toHaveLength(1);
  expect(state.applies[0]).toMatchObject({ countryCode: 'SA', currencyCode: 'SAR', legalEntityName: 'Meridian Health LLC', draft: { departments: [], branches: [{ nameEn: 'Reviewed Head Office' }] } });
  await contained(page);
  await evidence(page, info, 'review-retained-after-refusal');
  await apply.click();
  await expect(page.getByText('Setup Applied', { exact: true })).toBeVisible();
  expect(state.applies).toHaveLength(2);
  expect(state.applies[1]).toEqual(state.applies[0]);
  expect(state.errors).toEqual([]);
  expect(state.unexpectedWrites).toEqual([]);
});

test('changing company size or draft sections invalidates the previously reviewed proposal', async ({ page }) => {
  const state = await boot(page);
  await companyDetails(page);
  await toReview(page);
  await generate(page);
  await page.getByRole('navigation', { name: 'Company setup steps' }).getByRole('button', { name: /Company details/ }).click();
  await page.getByLabel(/^Company size/).selectOption('500+');
  await toReview(page);
  await expect(page.getByRole('button', { name: /^Apply \d+ item/ })).toHaveCount(0);
  expect(state.previews).toHaveLength(1);
  await generate(page);
  expect(state.previews[1].companySize).toBe('500+');
  await page.locator('summary').filter({ hasText: /^Draft choices/ }).click();
  await page.getByRole('checkbox', { name: 'Include Public holidays in the generated draft', exact: true }).uncheck();
  await expect(page.getByRole('button', { name: /^Apply \d+ item/ })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Generate draft', exact: true })).toBeVisible();
  expect(state.applies).toHaveLength(0);
  expect(state.errors).toEqual([]);
});

test('requires country and currency when the workspace has not supplied them', async ({ page }) => {
  const state = await boot(page, { missingLocalization: true });
  await companyDetails(page);
  await expect(page.getByLabel(/^Country/)).toHaveValue('');
  await expect(page.getByLabel(/^Currency/)).toHaveValue('');
  const next = page.getByRole('button', { name: 'Continue', exact: true });
  if (await next.isEnabled()) await next.click();
  await expect(page.getByLabel(/^Industry/)).toBeVisible();
  expect(state.previews).toHaveLength(0);
  await page.getByLabel(/^Country/).selectOption('AE');
  await page.getByLabel(/^Currency/).selectOption('AED');
  await toReview(page);
  await generate(page);
  expect(state.previews[0]).toMatchObject({ countryCode: 'AE', currencyCode: 'AED' });
  expect(state.applies).toHaveLength(0);
  expect(state.errors).toEqual([]);
});

test('offers import separately from guided setup and preserves its organization entry point', async ({ page }, info) => {
  const state = await boot(page);
  await expect(page.locator('input[type="file"]:visible')).toHaveCount(1);
  await page.getByRole('button', { name: 'Import organization', exact: true }).click();
  await expect(page.locator('input[type="file"]:visible').first()).toBeVisible();
  await expect(page.getByLabel(/^Industry/)).not.toBeVisible();
  await accessible(page, '#setup-importOrganization');
  await contained(page);
  await evidence(page, info, 'import-entry');
  await page.getByRole('button', { name: 'Guided setup', exact: true }).click();
  await expect(page.getByLabel(/^Industry/)).toBeVisible();
  await expect(page.locator('input[type="file"]:visible')).toHaveCount(1);
  await page.getByRole('button', { name: 'Manage settings', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Manage settings', exact: true })).toBeVisible();
  await expect(page.getByText(company.legalNameEn, { exact: true }).first()).toBeVisible();
  await expect(page).toHaveURL(/tab=companies/);
  expect(state.previews).toHaveLength(0);
  expect(state.applies).toHaveLength(0);
  expect(state.unexpectedWrites).toEqual([]);
  expect(state.errors).toEqual([]);
});

test('renders the setup shell and organization import path in Arabic RTL', async ({ page }) => {
  const state = await boot(page, { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await page.getByRole('button', { name: 'استيراد هيكل المنظمة', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'استيراد منظمتك', exact: true })).toBeVisible();
  await expect(page.getByLabel('رفع حزمة المنظمة', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'التحقق من الملفات', exact: true })).toBeDisabled();
  await contained(page);
  expect(state.errors).toEqual([]);
  expect(state.unexpectedWrites).toEqual([]);
});

test('a read-only organization viewer cannot enter guided setup through a write-tab deep link', async ({ page }) => {
  const state = await boot(page, { readOnly: true, route: '/setup?tab=aiSetup' });
  await expect(page.getByText(company.legalNameEn, { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Guided setup', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Import organization', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Generate draft', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Add Company', exact: true })).toHaveCount(0);
  expect(state.previews).toHaveLength(0);
  expect(state.applies).toHaveLength(0);
  expect(state.unexpectedWrites).toEqual([]);
  expect(state.errors).toEqual([]);
  await contained(page);
});

test('requires an explicit legal entity and uses an existing company’s country and currency', async ({ page }) => {
  const state = await boot(page);
  await page.getByLabel(/^Industry/).fill('Healthcare');
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(page.getByLabel(/^Legal entity name/)).toBeVisible();
  await expect(page.getByRole('region', { name: 'Guided setup', exact: true }).getByRole('alert')).toContainText('legal entity name');
  expect(state.previews).toHaveLength(0);
  await page.getByLabel(/^Country/).selectOption('AE');
  await page.getByLabel(/^Currency/).selectOption('AED');
  await page.getByLabel(/^Legal entity name/).fill(company.legalNameEn);
  await expect(page.getByLabel(/^Country/)).toHaveValue(company.countryCode);
  await expect(page.getByLabel(/^Currency/)).toHaveValue(company.defaultCurrency);
  await toReview(page);
  await generate(page);
  await page.getByRole('button', { name: 'Apply 3 item(s) to workspace', exact: true }).click();
  await expect(page.getByText('Setup Applied', { exact: true })).toBeVisible();
  expect(state.previews[0]).toMatchObject({ legalEntityName: company.legalNameEn, countryCode: 'SA', currencyCode: 'SAR' });
  expect(state.applies[0]).toMatchObject({ legalEntityName: company.legalNameEn, countryCode: 'SA', currencyCode: 'SAR' });
  expect(state.errors).toEqual([]);
});

const packageA = '# companies\nLegalNameEn,CountryCode,DefaultCurrency\nImport Alpha,SA,SAR\n# departments\nCode,NameEn\nOPS,Operations\n';
const packageB = '# grades\nCode,Name,MinSalary,MaxSalary\nG2,Specialist,5000,10000\n';

function importBatch(sequence: number, status = 'DryRunPassed'): MigrationImportBatchDto {
  return {
    id: `batch-${sequence}`, externalBatchId: `ORG-TEST-${sequence}`, packageType: 'OrganizationStructure', status,
    packageChecksum: `checksum-${sequence}`, dryRun: status !== 'Committed', currentSection: 'review',
    receivedRows: 1, createdRows: 1, updatedRows: 0, skippedRows: 0, errorRows: 0,
    reconciliation: { sectionCounts: { grades: 1 }, identityCounts: {}, operationalCounts: {}, validationErrors: 0, validationWarnings: 0 },
    errors: [], createdAtUtc: '2026-10-08T12:00:00Z',
    result: { received: 1, errors: 0, warnings: 0, rows: [], hasBlockingErrors: false, committed: status === 'Committed', applied: status === 'Committed' ? { grades: 1 } : {} },
  };
}

type ImportFailure = 'blocked422' | 'failed422' | 'conflict409';

async function mockImport(page: Page, options: {
  validationWait?: Promise<void>;
  commitWait?: Promise<void>;
  failure?: ImportFailure;
} = {}) {
  const validations: OrgStructureImportRequest[] = [];
  const commits: string[] = [];
  await page.route('**/api/setup/organization-structure-import/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (path.endsWith('/batches/dry-run')) {
      validations.push(route.request().postDataJSON() as OrgStructureImportRequest);
      const sequence = validations.length;
      if (sequence === 1) await options.validationWait;
      return reply(importBatch(sequence));
    }
    if (path.endsWith('/commit')) {
      const id = path.split('/').at(-2)!;
      commits.push(id);
      await options.commitWait;
      const current = importBatch(Number(id.replace('batch-', '')));
      if (options.failure === 'blocked422') return reply({
        ...current, status: 'DryRunBlocked', errorRows: 1, errors: ['Branch HQ belongs to another company.'],
        result: { ...current.result, errors: 1, hasBlockingErrors: true, rows: [{ rowNumber: 2, entityCode: 'branches:HQ', status: 'Error', errors: ['Branch HQ belongs to another company.'], warnings: [] }] },
      }, 422);
      if (options.failure === 'failed422') return reply({
        ...current, status: 'Failed', errorRows: 1, errors: ['Stored import payload could not be read.'], result: { ...current.result, rows: null },
      }, 422);
      if (options.failure === 'conflict409') return reply({
        ...current, status: 'Failed', errorRows: 1, errors: ['Batch is no longer available; validate again.'], result: {},
      }, 409);
      return reply(importBatch(Number(id.replace('batch-', '')), 'Committed'));
    }
    if (path.endsWith('/reconciliation')) return reply(importBatch(validations.length));
    return route.fulfill({ status: 500, json: { message: 'Unexpected import request in browser test.' } });
  });
  return { validations, commits };
}

async function uploadPackage(page: Page, name: string, content: string) {
  await page.getByLabel('Upload an organization package', { exact: true }).setInputFiles({ name, mimeType: 'text/plain', buffer: Buffer.from(content) });
  await expect(page.getByText(name, { exact: true })).toBeVisible();
}

test('locks import operations during validation and commits only the replacement package', async ({ page }, info) => {
  const state = await boot(page);
  let releaseValidation!: () => void;
  let releaseCommit!: () => void;
  const imports = await mockImport(page, {
    validationWait: new Promise<void>(resolve => { releaseValidation = resolve; }),
    commitWait: new Promise<void>(resolve => { releaseCommit = resolve; }),
  });
  await page.getByRole('button', { name: 'Import organization', exact: true }).click();
  await uploadPackage(page, 'alpha.txt', packageA);
  await page.getByRole('button', { name: 'Validate files', exact: true }).click();
  await expect.poll(() => imports.validations.length).toBe(1);
  try {
    await expect(page.getByLabel('Upload an organization package', { exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Remove package', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Download template', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Validating…', exact: true })).toBeDisabled();
    await page.locator('summary').filter({ hasText: 'Upload individual CSV files instead' }).click();
    await expect(page.getByLabel('Grades & salary bands', { exact: true })).toBeDisabled();
    expect(imports.commits).toEqual([]);
  } finally { releaseValidation(); }
  await expect(page.getByRole('button', { name: 'Apply import', exact: true })).toBeEnabled();
  await uploadPackage(page, 'replacement.txt', packageB);
  await expect(page.getByRole('button', { name: 'Apply import', exact: true })).toHaveCount(0);
  await expect(page.getByText('alpha.txt', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Validate files', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Apply import', exact: true })).toBeEnabled();
  expect(imports.validations[1]).toEqual({ gradesCsv: 'Code,Name,MinSalary,MaxSalary\nG2,Specialist,5000,10000' });
  await page.getByRole('button', { name: 'Apply import', exact: true }).click();
  await expect.poll(() => imports.commits.length).toBe(1);
  try {
    await expect(page.getByRole('button', { name: 'Refresh status', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Validate files', exact: true })).toBeDisabled();
    await expect(page.getByLabel('Upload an organization package', { exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Applying…', exact: true })).toBeDisabled();
  } finally { releaseCommit(); }
  await expect(page.getByRole('button', { name: 'Import applied', exact: true })).toBeDisabled();
  expect(imports.commits).toEqual(['batch-2']);
  await contained(page);
  await evidence(page, info, 'import-applied');
  expect(state.errors).toEqual([]);
});

for (const failure of ['blocked422', 'failed422', 'conflict409'] as const) {
  test(`import ${failure} preserves recovery details and requires validation before another apply`, async ({ page }) => {
    const state = await boot(page);
    const imports = await mockImport(page, { failure });
    await page.getByRole('button', { name: 'Import organization', exact: true }).click();
    await uploadPackage(page, 'organization.txt', packageA);
    await page.getByRole('button', { name: 'Validate files', exact: true }).click();
    await page.getByRole('button', { name: 'Apply import', exact: true }).click();
    const message = failure === 'blocked422' ? 'Branch HQ belongs to another company.'
      : failure === 'failed422' ? 'Stored import payload could not be read.' : 'Batch is no longer available; validate again.';
    await expect(page.getByText(message, { exact: failure !== 'blocked422' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Apply import', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Validate files', exact: true })).toBeEnabled();
    await expect(page.getByText('organization.txt', { exact: true })).toBeVisible();
    expect(imports.commits).toEqual(['batch-1']);
    await contained(page);
    await page.getByRole('button', { name: 'Validate files', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Apply import', exact: true })).toBeEnabled();
    expect(imports.validations).toHaveLength(2);
    expect(imports.commits).toHaveLength(1);
    expect(state.errors).toEqual([]);
  });
}

test('collects scoped company policies, custom grades and benefits without activating them', async ({ page }, info) => {
  const state = await boot(page);
  await companyDetails(page);
  await page.getByLabel('Policy excerpts', { exact: true }).fill('Approved policy: Professional grade receives 30 days annual leave.');
  await expect(page.getByRole('checkbox', { name: 'Use these excerpts with the configured AI provider when generating the draft' })).not.toBeChecked();
  await continueStep(page);
  await page.getByRole('checkbox', { name: 'Configure leave entitlements from company policy', exact: true }).check();
  await page.getByLabel('Policy name', { exact: false }).fill('Professional annual leave');
  await page.getByLabel('Annual entitlement (days)', { exact: true }).fill('30');
  await page.getByLabel('Grade code (optional)', { exact: true }).fill('P1');
  await expect(page.getByRole('checkbox', { name: 'Prorate partial months by calendar days employed', exact: true })).toBeChecked();
  await page.getByRole('combobox', { name: 'Accrual', exact: true }).selectOption('Yearly');
  await expect(page.getByRole('checkbox', { name: 'Prorate partial months by calendar days employed', exact: true })).not.toBeChecked();
  await page.getByRole('combobox', { name: 'Accrual', exact: true }).selectOption('Monthly');
  await page.getByRole('checkbox', { name: 'Prorate partial months by calendar days employed', exact: true }).check();
  await page.getByRole('checkbox', { name: 'No overtime policy', exact: true }).check();
  await expect(page.getByRole('checkbox', { name: 'Paid overtime', exact: true })).not.toBeChecked();
  await page.getByRole('checkbox', { name: 'Paid overtime', exact: true }).check();
  await expect(page.getByRole('checkbox', { name: 'No overtime policy', exact: true })).not.toBeChecked();
  await accessible(page, '#setup-aiSetup');
  await continueStep(page);
  await page.getByRole('checkbox', { name: 'Customize management preferences', exact: true }).check();
  await page.getByRole('checkbox', { name: 'Dotted-line manager review', exact: true }).check();
  await continueStep(page);
  await page.getByRole('combobox', { name: 'How would you like to build your grades?', exact: true }).selectOption('manual');
  await page.getByRole('button', { name: 'Add salary grade', exact: true }).click();
  await page.getByRole('textbox', { name: 'Grade code *', exact: true }).fill('P1');
  await page.getByLabel('Grade name', { exact: false }).fill('Professional');
  await page.getByLabel('Minimum salary', { exact: true }).fill('5000');
  await page.getByLabel('Midpoint salary', { exact: true }).fill('7500');
  await page.getByLabel('Maximum salary', { exact: true }).fill('10000');
  await page.getByRole('combobox', { name: 'How would you like to build your grades?', exact: true }).selectOption('assisted');
  await page.getByRole('combobox', { name: 'How would you like to build your grades?', exact: true }).selectOption('manual');
  await expect(page.getByRole('textbox', { name: 'Grade code *', exact: true })).toHaveValue('P1');
  await page.getByRole('button', { name: 'Add benefit plan', exact: true }).click();
  await page.getByLabel('Benefit code', { exact: false }).fill('MED');
  await page.getByLabel('Benefit name', { exact: false }).fill('Company medical plan');
  await page.getByLabel('Effective from', { exact: false }).fill('2026-10-01');
  await page.getByLabel('Eligible grade codes', { exact: false }).pressSequentially('P1,P2');
  await page.getByLabel('Eligible grade codes', { exact: false }).fill('P1');
  await contained(page);
  await accessible(page, '#setup-aiSetup');
  await evidence(page, info, 'custom-grades-benefits');
  await continueStep(page);
  await generate(page);
  expect(state.previews[0].configuration?.usePolicySourceForAi).not.toBe(true);
  expect(state.previews[0].configuration).toMatchObject({
    policySourceText: 'Approved policy: Professional grade receives 30 days annual leave.',
    overtimeModes: ['PaidOvertime'],
    leavePolicies: [{ name: 'Professional annual leave', annualEntitlementDays: 30, gradeCode: 'P1', proratePartialMonths: true }],
    grades: [{ code: 'P1', minSalary: 5000, midSalary: 7500, maxSalary: 10000, currency: 'SAR' }],
    benefitPlans: [{ code: 'MED', effectiveFrom: '2026-10-01', currency: 'SAR', gradeCodes: ['P1'] }],
    hrConfig: { allowDottedLineApproval: true },
  });
  expect(state.applies).toHaveLength(0);
  expect(state.errors).toEqual([]);
});

test('refuses a server that would discard custom configuration', async ({ page }) => {
  const state = await boot(page, { legacySetup: true });
  await companyDetails(page);
  await toReview(page);
  await page.getByRole('button', { name: 'Generate draft', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'The setup service needs an update' })).toBeVisible();
  await expect(page.getByRole('button', { name: /^Apply \d+ item/ })).toHaveCount(0);
  expect(state.applies).toHaveLength(0);
});

test('refuses invalid grade ranges before requesting a draft and preserves entered values', async ({ page }) => {
  const state = await boot(page);
  await companyDetails(page);
  for (let i = 0; i < 3; i++) await continueStep(page);
  await page.getByRole('combobox', { name: 'How would you like to build your grades?', exact: true }).selectOption('manual');
  await page.getByRole('button', { name: 'Add salary grade', exact: true }).click();
  await page.getByRole('textbox', { name: 'Grade code *', exact: true }).fill('P1');
  await page.getByLabel('Grade name', { exact: false }).fill('Professional');
  await page.getByLabel('Minimum salary', { exact: true }).fill('9000');
  await page.getByLabel('Midpoint salary', { exact: true }).fill('7500');
  await page.getByLabel('Maximum salary', { exact: true }).fill('10000');
  await continueStep(page);
  await page.getByRole('button', { name: 'Generate draft', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Complete each grade' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Grade code *', exact: true })).toHaveValue('P1');
  expect(state.previews).toHaveLength(0);
  expect(state.applies).toHaveLength(0);
});

test('renders custom grade configuration in Arabic with an accessible mobile layout', async ({ page }, info) => {
  const state = await boot(page, { locale: 'ar' });
  await companyDetails(page);
  for (let i = 0; i < 3; i++) await continueStep(page);
  await expect(page.getByRole('heading', { name: 'درجات الرواتب', exact: true })).toBeVisible();
  await page.getByRole('combobox', { name: 'كيف ترغب في إعداد درجات الرواتب؟', exact: true }).selectOption('manual');
  await page.getByRole('button', { name: 'إضافة درجة راتب', exact: true }).click();
  await expect(page.getByLabel('الحد الأدنى للراتب', { exact: true })).toBeVisible();
  await contained(page);
  await accessible(page, '#setup-aiSetup');
  await evidence(page, info, 'arabic-custom-grades');
  expect(state.errors).toEqual([]);
});
