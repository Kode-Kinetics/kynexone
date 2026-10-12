import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import type { EmployeeCreateRequest } from '../src/api/employees';

const company = {
  id: 'company-wizard', legalNameEn: 'Wizard Test Company', legalNameAr: '', tradeName: 'Wizard Test Company',
  countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', emailDomain: 'example.test',
  workEmailPattern: 'first.last', isActive: true, approvalStatus: 'Active',
};
const grade = { id: 'grade-wizard', code: 'G5', name: 'Professional', band: 'P', level: 5, minSalary: 5000, midSalary: 10000, maxSalary: 15000, currency: 'SAR', isActive: true };
const manager = { id: 17, employeeCode: 'TEST-017', fullName: 'Morgan Reviewer', englishName: 'Morgan Reviewer', status: 'Active', companyId: company.id };
const duplicate = { employeeId: 44, employeeCode: 'TEST-044', fullName: 'Alex Morgan', status: 'Draft', matchType: 'probable', signals: ['Name and date of birth match'], canView: true };
const headings = ['Start with the person', 'Place them in the organization', 'Set up payroll', 'Build the salary package', 'Add identity documents', 'Review employee details'];
const stepNames = ['Person', 'Employment', 'Payroll', 'Salary', 'Identity', 'Review'];

interface MockOptions {
  quick?: boolean;
  multipleCompanies?: boolean;
  missingCountry?: boolean;
  failFirstCreate?: boolean;
  duplicate?: boolean;
  transliteration?: {
    status?: number;
    result?: { suggestion: string; requiresManualEntry?: boolean };
    waitFor?: Promise<void>;
  };
}

async function boot(page: Page, options: MockOptions = {}) {
  const creates: EmployeeCreateRequest[] = [];
  const probes: unknown[] = [];
  const transliterations: unknown[] = [];
  const updates: Array<{ effectiveDate: string; changes: Record<string, unknown> }> = [];
  const errors: string[] = [];
  const testCompany = { ...company, countryCode: options.missingCountry ? '' : company.countryCode };
  const companies = options.multipleCompanies ? [testCompany, { ...testCompany, id: 'company-other', legalNameEn: 'Other Test Company' }] : [testCompany];
  let saved: Record<string, unknown> | undefined;
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() !== 'error') return;
    if ((options.failFirstCreate || options.transliteration?.status === 503) && /Failed to load resource.*503/.test(message.text())) return;
    errors.push(message.text());
  });
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'wizard-browser-fixture'));
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const body = request.postData() ? request.postDataJSON() : undefined;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (path === '/api/auth/me') return reply({
      id: 'wizard-reviewer', tenantId: 'wizard-tenant', tenantSlug: 'wizard-fixture', fullName: 'HR Reviewer',
      roles: ['HR Manager'], permissions: ['employees.read', 'employees.write', 'employees.approve'],
      isGroupScope: !!options.multipleCompanies, accountType: options.multipleCompanies ? 'Group' : 'SingleCompany',
      companies: companies.map(item => ({ id: item.id, name: item.legalNameEn, code: 'TEST', countryCode: item.countryCode, isActive: true })),
    });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/companies') return reply(paged(companies));
    if (path === '/api/branches') return reply(paged([{ id: 'branch-wizard', companyId: company.id, code: 'RYD', nameEn: 'Riyadh', isActive: true }]));
    if (path === '/api/departments') return reply(paged([{ id: 'department-wizard', branchId: 'branch-wizard', code: 'OPS', nameEn: 'Operations', managerEmployeeId: manager.id, isActive: true }]));
    if (path === '/api/designations') return reply(paged([{ id: 'designation-wizard', departmentId: 'department-wizard', code: 'SPEC', titleEn: 'Specialist', gradeId: grade.id, isActive: true }]));
    if (path === '/api/grades') return reply(paged([grade, { ...grade, id: 'grade-other', code: 'G6', name: 'Senior' }]));
    if (path === '/api/compensation/benefits/grade-defaults') return reply([]);
    if (path.endsWith('/pay-scale')) return reply([]);
    if (path === '/api/organization/cost-centers') return reply(paged([{ id: 'cost-wizard', companyId: company.id, code: 'OPS', nameEn: 'Operations cost center', isActive: true }]));
    if (path === '/api/employees/field-catalog') return reply({ countryCode: testCompany.countryCode, fields: [] });
    if (path === '/api/localization/transliterate') {
      transliterations.push(body);
      await options.transliteration?.waitFor;
      return reply(options.transliteration?.result ?? { suggestion: 'محمد عمر', requiresManualEntry: false }, options.transliteration?.status);
    }
    if (path === '/api/employees/derive-work-email') {
      const localPart = body.localPart ?? body.englishName.trim().toLowerCase().split(/\s+/).join('.');
      return reply({ domain: company.emailDomain, pattern: company.workEmailPattern, localPart, workEmail: `${localPart}@${company.emailDomain}`, unique: true, suggestion: localPart, status: 'derived' });
    }
    if (path === '/api/employees/duplicate-check') {
      probes.push(body);
      return reply({ hasStrong: false, hasProbable: !!options.duplicate, matches: options.duplicate ? [duplicate] : [] });
    }
    if (path === '/api/employees' && request.method() === 'POST') {
      creates.push(body);
      if (options.failFirstCreate && creates.length === 1) return reply({ message: 'Temporary save failure. Please try again.' }, 503);
      saved = { ...body, id: 901, employeeCode: body.employeeCode || 'TEST-901', fullName: body.englishName, status: 'Draft', accessState: body.workEmail ? 'not_started' : 'waiting_for_work_email', profileCompletenessScore: 25, readinessState: 'NeedsAttention', activationBlockersCount: 1, salaryBreakdown: body.salaryBreakdown?.basicSalary ? body.salaryBreakdown : null, documents: [], history: [], complianceRecords: body.complianceRecords ?? [] };
      return reply(saved, 201);
    }
    if (path === '/api/employees') return reply(paged(url.searchParams.get('status') === 'Active' ? [manager] : saved ? [saved] : []));
    if (path === '/api/employees/901' && request.method() === 'PUT') {
      updates.push(body);
      saved = { ...saved, ...body.changes };
      return reply(saved);
    }
    if (path === '/api/employees/901') return reply(saved);
    if (path === '/api/employee-completion/901') return reply({ requested: false, status: 'NotRequested', selfServiceAvailable: false });
    if (path === '/api/employee-access/summary') return reply(saved ? { [saved.workEmail ? 'not_started' : 'waiting_for_work_email']: 1 } : {});
    if (path === '/api/employee-access/901') return reply({
      employeeId: 901, employeeName: saved?.fullName, employeeCode: 'TEST-901', workEmail: saved?.workEmail ?? null,
      state: saved?.workEmail ? 'not_started' : 'waiting_for_work_email', canIssue: !!saved?.workEmail,
      codeExpiresAtUtc: null, codeIssuedByName: null, lastCodeExpiredAtUtc: null, lastSignInAtUtc: null,
      stoppedReason: null, blockedCode: null, blockedReason: null, reasonCode: null, emailDelivery: false,
    });
    if (path.endsWith('/readiness')) return reply({ employeeId: 901, state: 'NeedsAttention', score: 25, progress: { present: 1, requiredTotal: 4 }, policy: { countryCode: 'SA', tier: 'Standard', sources: [] }, blocking: [], payBlocking: [], recommended: [], present: [], expiringSoon: [], disclaimer: 'Synthetic browser fixture.' });
    if (path.includes('/features/') || path === '/api/notifications' || path.endsWith('/documents') || path.endsWith('/history')) return reply([]);
    // Even unrelated shell requests stay local to this synthetic session.
    return reply(paged([]));
  });
  await page.goto('/people');
  await expect(page).toHaveURL(/\/people(?:\?|$)/);
  await expect(page).not.toHaveTitle('');
  await expect(page.getByRole('button', { name: 'Add Employee', exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Add Employee', exact: true }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Add Employee', exact: true });
  await expect(dialog.getByRole('heading', { name: headings[0], exact: true })).toBeVisible();
  if (!options.quick) {
    await expandOptional(dialog, 'Additional personal details');
  }
  return { dialog, creates, probes, transliterations, updates, errors };
}

async function expandOptional(dialog: Locator, label: string) {
  const details = dialog.locator('details').filter({ has: dialog.page().getByText(label, { exact: true }) });
  await expect(details).toHaveCount(1);
  if (await details.getAttribute('open') === null) await details.locator('summary').click();
}

async function next(dialog: Locator, index: number) {
  await dialog.getByRole('button', { name: `Next: ${stepNames[index]}`, exact: true }).click();
  await expect(dialog.getByRole('heading', { name: headings[index], exact: true })).toBeVisible();
  if (index === 1) {
    await dialog.getByRole('checkbox', { name: /^Set up payroll and documents now/ }).check();
    await expandOptional(dialog, 'More employment details');
  }
  if (index === 2) await expandOptional(dialog, 'Additional payroll details');
  if (index === 3) await expandOptional(dialog, 'Add allowances and deductions');
}

async function toReview(dialog: Locator, start = 1) {
  for (let index = start; index < headings.length; index++) await next(dialog, index);
}

async function finishCreated(dialog: Locator) {
  await expect(dialog.getByTestId('employee-added')).toContainText('Alex Morgan has been added.');
  await expect(dialog.getByRole('button', { name: 'Create Employee', exact: true })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Later', exact: true }).click();
  await expect(dialog).toHaveCount(0);
}

async function evidence(page: Page, info: TestInfo, name: string) {
  const path = info.outputPath(`${name}-${info.project.name}.png`);
  const activePanel = page.locator('[data-employee-step]:visible');
  if (await activePanel.count()) await expect(activePanel).toHaveCSS('opacity', '1');
  await page.screenshot({ path, fullPage: false, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach(name, { path, contentType: 'image/png' });
}

async function expectContained(dialog: Locator, page: Page) {
  const bounds = await dialog.boundingBox();
  const viewport = page.viewportSize()!;
  expect(bounds).not.toBeNull();
  expect(bounds!.x).toBeGreaterThanOrEqual(0);
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width + 1);
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport.height + 1);
  expect(await dialog.evaluate(node => node.scrollWidth <= node.clientWidth + 1)).toBe(true);
}

test('quick creation starts with three steps and saves only after review', async ({ page }, info) => {
  const { dialog, creates, probes, errors } = await boot(page, { quick: true });
  const progress = dialog.getByRole('navigation', { name: 'Employee setup progress' });
  await expect(progress.getByRole('button')).toHaveCount(3);
  await expect(dialog.getByLabel('Preferred name', { exact: true })).not.toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Create Employee', exact: true })).toHaveCount(0);
  await expectContained(dialog, page);
  await evidence(page, info, 'quick-person');
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await dialog.getByRole('button', { name: 'Next: Employment', exact: true }).click();
  await expect(dialog.getByRole('heading', { name: headings[1], exact: true })).toBeVisible();
  await expect(dialog.getByRole('checkbox', { name: /^Set up payroll and documents now/ })).not.toBeChecked();
  await expect(dialog.getByRole('button', { name: 'Next: Review', exact: true })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Next: Payroll', exact: true })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Next: Review', exact: true }).click();
  await expect(dialog.getByRole('heading', { name: headings[5], exact: true })).toBeVisible();
  expect(creates).toHaveLength(0);
  expect(probes).toHaveLength(0);
  await expectContained(dialog, page);
  await evidence(page, info, 'quick-review');
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).click();
  await finishCreated(dialog);
  expect(creates).toHaveLength(1);
  expect(creates[0]).toMatchObject({ englishName: 'Alex Morgan', companyId: company.id });
  expect(creates[0].gender ?? '').toBe('');
  expect(creates[0]).not.toHaveProperty('workEmail');
  expect(errors).toEqual([]);
});

test('saving a draft from Person persists it across reload and resumes the same employee', async ({ page }, info) => {
  const { dialog, creates, updates, errors } = await boot(page, { quick: true });
  await dialog.getByRole('button', { name: 'Save draft and exit', exact: true }).click();
  await expect(dialog.getByText('Enter the employee’s English full name to continue.', { exact: true })).toBeVisible();
  expect(creates).toHaveLength(0);
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await dialog.getByLabel('Arabic full name', { exact: true }).fill('الاسم المعتمد');
  await dialog.getByRole('button', { name: 'Save draft and exit', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByText('Alex Morgan was saved as a draft.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Resume draft', exact: true })).toBeVisible();
  expect(creates).toHaveLength(1);
  expect(creates[0]).toMatchObject({ englishName: 'Alex Morgan', arabicName: 'الاسم المعتمد', companyId: company.id });
  expect(creates[0].gender ?? '').toBe('');
  await page.reload();
  await page.getByRole('button', { name: 'Open profile for Alex Morgan', exact: true }).click();
  const resumed = page.getByRole('dialog').filter({ hasText: 'Alex Morgan' });
  await expect(resumed.getByRole('textbox', { name: /^English full name/ })).toHaveValue('Alex Morgan');
  await expect(resumed.getByRole('textbox', { name: 'Arabic name', exact: true })).toHaveValue('الاسم المعتمد');
  await resumed.getByRole('textbox', { name: /^Preferred name/ }).fill('Alex resumed');
  await expectContained(resumed, page);
  await evidence(page, info, 'resumed-draft');
  await resumed.getByRole('button', { name: /^Step 6: Review/ }).click();
  expect(updates).toHaveLength(0);
  await resumed.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(resumed).toHaveCount(0);
  expect(creates).toHaveLength(1);
  expect(updates).toHaveLength(1);
  expect(updates[0].changes).toEqual({ preferredName: 'Alex resumed' });
  await page.reload();
  await page.getByRole('button', { name: 'Open profile for Alex Morgan', exact: true }).click();
  await expect(page.getByRole('dialog').getByRole('textbox', { name: /^Preferred name/ })).toHaveValue('Alex resumed');
  expect(errors).toEqual([]);
});

test('optional bank and salary data survive returning to quick setup and saving a draft', async ({ page }) => {
  const { dialog, creates, errors } = await boot(page);
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await next(dialog, 1);
  await next(dialog, 2);
  await dialog.getByLabel('Bank name', { exact: true }).fill('Test Bank');
  await next(dialog, 3);
  await dialog.getByLabel('Basic salary', { exact: true }).fill('7000');
  await dialog.getByRole('button', { name: /^Step 2: Employment/ }).click();
  await dialog.getByRole('checkbox', { name: /^Set up payroll and documents now/ }).uncheck();
  await dialog.getByRole('button', { name: 'Next: Review', exact: true }).click();
  await expect(dialog.locator('[data-employee-step="5"]')).toContainText('Test Bank');
  await dialog.getByRole('button', { name: 'Edit Salary', exact: true }).click();
  await expect(dialog.getByLabel('Basic salary', { exact: true })).toHaveValue('7000');
  await expect(dialog.getByRole('button', { name: 'Create Employee', exact: true })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Save draft and exit', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByText('Alex Morgan was saved as a draft.', { exact: true })).toBeVisible();
  expect(creates).toHaveLength(1);
  expect(creates[0]).toMatchObject({ englishName: 'Alex Morgan', payrollProfile: { bankName: 'Test Bank' }, salaryBreakdown: { basicSalary: 7000 } });
  expect(errors).toEqual([]);
});

test('a failed draft save retains the entered data and retries the same payload', async ({ page }) => {
  const { dialog, creates, errors } = await boot(page, { quick: true, failFirstCreate: true });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await dialog.getByRole('button', { name: 'Save draft and exit', exact: true }).click();
  await expect(dialog.getByText(/Temporary save failure/)).toBeVisible();
  await expect(dialog.getByLabel('English full name', { exact: false })).toHaveValue('Alex Morgan');
  expect(creates).toHaveLength(1);
  await dialog.getByRole('button', { name: 'Save draft and exit', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(creates).toHaveLength(2);
  expect(creates[1]).toEqual(creates[0]);
  expect(errors).toEqual([]);
});

test('an invalid entered work email cannot be persisted by Save draft and exit', async ({ page }) => {
  const { dialog, creates, errors } = await boot(page, { quick: true });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await dialog.getByRole('button', { name: 'Next: Employment', exact: true }).click();
  await dialog.getByRole('textbox', { name: 'Work email', exact: true }).fill('alex+alias');
  await dialog.getByRole('button', { name: 'Save draft and exit', exact: true }).click();
  await expect(dialog.getByRole('heading', { name: headings[1], exact: true })).toBeVisible();
  await expect(dialog.getByTestId('work-email-problem')).toContainText(/work email/i);
  expect(creates).toHaveLength(0);
  await dialog.getByRole('textbox', { name: 'Work email', exact: true }).fill('alex.it-issued');
  await dialog.getByRole('button', { name: 'Save draft and exit', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(creates).toHaveLength(1);
  expect(creates[0].workEmail).toBe('alex.it-issued@example.test');
  expect(errors).toEqual([]);
});

test('the duplicate override preserves Save draft and exit intent', async ({ page }) => {
  const { dialog, creates, probes, errors } = await boot(page, { quick: true, duplicate: true });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await dialog.getByRole('button', { name: 'Save draft and exit', exact: true }).click();
  await expect(dialog.getByRole('alert').filter({ hasText: 'Possible existing employee' })).toBeVisible();
  expect(probes).toHaveLength(1);
  expect(creates).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Create anyway', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByText('Alex Morgan was saved as a draft.', { exact: true })).toBeVisible();
  await expect(page.getByTestId('employee-added')).toHaveCount(0);
  expect(creates).toHaveLength(1);
  expect(creates[0].acknowledgeDuplicate).toBe(true);
  expect(probes).toHaveLength(1);
  expect(errors).toEqual([]);
});

test('a multi-company draft requires an explicit employing company before saving', async ({ page }) => {
  const { dialog, creates, errors } = await boot(page, { quick: true, multipleCompanies: true });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await dialog.getByRole('button', { name: 'Save draft and exit', exact: true }).click();
  await expect(dialog.getByRole('heading', { name: headings[1], exact: true })).toBeVisible();
  await expect(dialog.getByText('Choose an employing company before saving.', { exact: true })).toBeVisible();
  expect(creates).toHaveLength(0);
  await dialog.getByRole('combobox', { name: 'Company', exact: true }).selectOption('company-other');
  await dialog.getByRole('button', { name: 'Save draft and exit', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(creates).toHaveLength(1);
  expect(creates[0].companyId).toBe('company-other');
  expect(errors).toEqual([]);
});

test('guides keyboard navigation, requires a name, and keeps creation until review', async ({ page }, info) => {
  const { dialog, creates, probes, errors } = await boot(page);
  await expectContained(dialog, page);
  await evidence(page, info, 'profile');
  const advance = dialog.getByRole('button', { name: 'Next: Employment', exact: true });
  await advance.focus();
  await page.keyboard.press('Enter');
  await expect(dialog.getByText('Enter the employee’s English full name to continue.', { exact: true })).toBeVisible();
  await expect(dialog.getByRole('heading', { name: headings[0], exact: true })).toBeVisible();
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await expect(dialog.getByText('Enter the employee’s English full name to continue.', { exact: true })).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Create Employee', exact: true })).toHaveCount(0);
  await advance.focus();
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('heading', { name: headings[1], exact: true })).toBeFocused();
  await dialog.getByRole('checkbox', { name: /^Set up payroll and documents now/ }).check();
  await expect(dialog.getByRole('textbox', { name: 'Work email', exact: true })).toHaveValue('');
  await expectContained(dialog, page);
  await evidence(page, info, 'employment');
  for (let index = 2; index < headings.length; index++) {
    await expect(dialog.getByRole('button', { name: 'Create Employee', exact: true })).toHaveCount(0);
    await next(dialog, index);
    await expectContained(dialog, page);
    const nextAction = dialog.getByRole('button', { name: index === 5 ? 'Create Employee' : `Next: ${stepNames[index + 1]}`, exact: true });
    await expect(nextAction).toBeInViewport();
  }
  await expect(dialog.getByRole('button', { name: 'Create Employee', exact: true })).toBeEnabled();
  expect(creates).toHaveLength(0);
  expect(probes).toHaveLength(0);
  await evidence(page, info, 'review');
  // Keyboard focus remains trapped in the dialog at its last action.
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).focus();
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('button', { name: 'Close', exact: true })).toBeFocused();
  if (info.project.name.includes('reduced-motion')) {
    expect(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches)).toBe(true);
    expect(await dialog.evaluate(node => node.getAnimations({ subtree: true }).filter(animation => animation.playState === 'running').length)).toBe(0);
  }
  expect(errors).toEqual([]);
});

test('retains entered values across Back and review edits and submits the existing payload', async ({ page }, info) => {
  const { dialog, creates, errors } = await boot(page);
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await dialog.getByLabel('Preferred name', { exact: true }).fill('Alex');
  await dialog.getByLabel('Personal email', { exact: true }).fill('alex.personal@example.test');
  await next(dialog, 1);
  await dialog.getByRole('textbox', { name: 'Work email', exact: true }).fill('alex.it-issued');
  await dialog.getByRole('button', { name: 'Back', exact: true }).click();
  await expect(dialog.getByLabel('Preferred name', { exact: true })).toHaveValue('Alex');
  await next(dialog, 1);
  await expect(dialog.getByRole('textbox', { name: 'Work email', exact: true })).toHaveValue('alex.it-issued');
  await dialog.getByRole('combobox', { name: 'Branch', exact: true }).selectOption('branch-wizard');
  await dialog.getByRole('combobox', { name: 'Department', exact: true }).selectOption('department-wizard');
  await expect(dialog.getByRole('combobox', { name: 'Line manager', exact: true })).toHaveValue('17');
  await dialog.getByRole('combobox', { name: 'Designation', exact: true }).selectOption('designation-wizard');
  await expect(dialog.getByRole('combobox', { name: 'Grade', exact: true })).toHaveValue(grade.id);
  await expect(dialog.getByRole('combobox', { name: 'Grade', exact: true }).locator('option')).toHaveCount(2);
  await dialog.getByLabel('Job title', { exact: true }).fill('Operations specialist');
  await dialog.getByLabel('Joining date', { exact: false }).fill('2026-10-15');
  await next(dialog, 2);
  await dialog.getByLabel('Bank name', { exact: true }).fill('Test Bank');
  await dialog.getByRole('textbox', { name: /^IBAN/ }).fill('SA0380000000608010167519');
  await dialog.getByLabel('Account number', { exact: true }).fill('608010167519');
  await next(dialog, 3);
  await dialog.getByLabel('Basic salary', { exact: true }).fill('7000');
  await dialog.getByLabel('Housing allowance', { exact: true }).fill('2000');
  await dialog.getByLabel('Fixed deduction', { exact: true }).fill('100');
  await dialog.getByLabel('Effective date', { exact: true }).fill('2026-10-15');
  await evidence(page, info, 'salary');
  await next(dialog, 4);
  await dialog.getByLabel(/^Passport number/i).fill('TEST-PASSPORT-001');
  await next(dialog, 5);
  await dialog.getByRole('button', { name: 'Edit Profile', exact: true }).click();
  await dialog.getByLabel('Preferred name', { exact: true }).fill('Alex M');
  await dialog.getByRole('button', { name: 'Return to review', exact: true }).click();
  await expect(dialog.getByRole('heading', { name: headings[5], exact: true })).toBeVisible();
  await expect(dialog.locator('[data-employee-step="5"]')).toContainText('alex.it-issued@example.test');
  expect(creates).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).click();
  await finishCreated(dialog);
  expect(creates).toHaveLength(1);
  expect(creates[0]).toMatchObject({
    englishName: 'Alex Morgan', preferredName: 'Alex M', manualEmployeeCode: false,
    personalEmail: 'alex.personal@example.test', workEmail: 'alex.it-issued@example.test', companyId: company.id,
    branchId: 'branch-wizard', departmentId: 'department-wizard', designationId: 'designation-wizard', gradeId: grade.id,
    reportingManagerEmployeeId: 17, jobTitle: 'Operations specialist', joiningDate: '2026-10-15',
    payrollProfile: { bankName: 'Test Bank', iban: 'SA0380000000608010167519', accountNumber: '608010167519', paymentMethod: 'BankTransfer', salaryCurrency: 'SAR', wpsEligible: true, eosbEligible: true },
    salaryBreakdown: { basicSalary: 7000, housingAllowance: 2000, fixedDeduction: 100, effectiveDate: '2026-10-15', currency: 'SAR' },
  });
  expect(creates[0].complianceRecords).toEqual(expect.arrayContaining([expect.objectContaining({ fieldKey: 'passport_number', fieldValue: 'TEST-PASSPORT-001' })]));
  expect(creates[0]).not.toHaveProperty('costCenterId');
  expect(creates[0]).not.toHaveProperty('acknowledgeDuplicate');
  expect(errors).toEqual([]);
});

test('preserves the reviewed draft after a server failure and retries once', async ({ page }) => {
  const { dialog, creates, errors } = await boot(page, { failFirstCreate: true });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await next(dialog, 1);
  await dialog.getByRole('textbox', { name: 'Work email', exact: true }).fill('alex.it-issued');
  await toReview(dialog, 2);
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).click();
  await expect(dialog.getByText(/Temporary save failure/)).toBeVisible();
  await expect(dialog.getByRole('heading', { name: headings[5], exact: true })).toBeVisible();
  expect(creates).toHaveLength(1);
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).click();
  await finishCreated(dialog);
  expect(creates).toHaveLength(2);
  expect(creates[1]).toEqual(creates[0]);
  expect(errors).toEqual([]);
});

test('requires an explicit duplicate decision before sending the create request', async ({ page }, info) => {
  const { dialog, creates, probes, errors } = await boot(page, { duplicate: true });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await dialog.getByLabel('Date of birth', { exact: false }).fill('1990-04-18');
  await dialog.getByRole('button', { name: 'Next: Employment', exact: true }).click();
  await expect(dialog.getByRole('alert').filter({ hasText: 'Possible existing employee' })).toBeVisible();
  expect(creates).toHaveLength(0);
  expect(probes).toHaveLength(1);
  await dialog.getByRole('button', { name: 'Continue with this draft', exact: true }).click();
  await expect(dialog.getByRole('heading', { name: headings[1], exact: true })).toBeVisible();
  await dialog.getByRole('checkbox', { name: /^Set up payroll and documents now/ }).check();
  await toReview(dialog, 2);
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).click();
  await expect(dialog.getByRole('alert').filter({ hasText: 'Possible existing employee' })).toBeVisible();
  expect(creates).toHaveLength(0);
  expect(probes).toHaveLength(2);
  await evidence(page, info, 'duplicate');
  await dialog.getByRole('button', { name: 'Create anyway', exact: true }).click();
  await expect(dialog.getByTestId('employee-added')).toContainText('Add a work email so Alex Morgan can sign in.');
  await finishCreated(dialog);
  expect(creates).toHaveLength(1);
  expect(creates[0].acknowledgeDuplicate).toBe(true);
  expect(creates[0]).not.toHaveProperty('workEmail');
  expect(probes).toHaveLength(2);
  expect(errors).toEqual([]);
});

test('keeps the company-country gate and its actionable setup link', async ({ page }) => {
  const { dialog, creates } = await boot(page, { missingCountry: true });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await next(dialog, 1);
  await expect(dialog.getByRole('alert')).toContainText('has no country set');
  await expect(dialog.getByRole('link', { name: 'Setup → Companies', exact: true })).toHaveAttribute('href', '/setup?tab=companies');
  const advance = dialog.getByRole('button', { name: 'Next: Payroll', exact: true });
  if (await advance.isEnabled()) await advance.click();
  await expect(dialog.getByRole('heading', { name: headings[1], exact: true })).toBeVisible();
  expect(creates).toHaveLength(0);
});

test('keeps salary-band validation on the salary step and allows correction', async ({ page }) => {
  const { dialog, creates, errors } = await boot(page);
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await next(dialog, 1);
  await dialog.getByRole('combobox', { name: 'Grade', exact: true }).selectOption(grade.id);
  await next(dialog, 2);
  await next(dialog, 3);
  await dialog.getByLabel('Basic salary', { exact: true }).fill('20000');
  await dialog.getByRole('button', { name: 'Next: Identity', exact: true }).click();
  await expect(dialog.getByText(/Salary package must be within/)).toBeVisible();
  await expect(dialog.getByRole('heading', { name: headings[3], exact: true })).toBeVisible();
  await dialog.getByLabel('Basic salary', { exact: true }).fill('10000');
  await next(dialog, 4);
  await next(dialog, 5);
  expect(creates).toHaveLength(0);
  expect(errors).toEqual([]);
});

test('suggests a person name only on request and carries the reviewed Arabic name through the wizard', async ({ page }, info) => {
  const { dialog, creates, transliterations, errors } = await boot(page);
  const englishName = dialog.getByLabel('English full name', { exact: false });
  const arabicName = dialog.getByLabel('Arabic full name', { exact: true });
  const suggest = dialog.getByRole('button', { name: 'Suggest (AR)', exact: true });
  await expect(suggest).toBeDisabled();
  await englishName.fill('Muhammad Umar');
  await arabicName.focus();
  await expect(arabicName).toHaveValue('');
  expect(transliterations).toHaveLength(0);
  await expect(dialog.getByText('Check the Arabic spelling against the employee’s passport or ID.', { exact: true })).toBeVisible();
  await suggest.click();
  await expect(dialog.getByText('محمد عمر', { exact: true })).toBeVisible();
  await expect(arabicName).toHaveValue('');
  expect(transliterations).toEqual([{ text: 'Muhammad Umar', target: 'ar', kind: 'person-name' }]);
  await expectContained(dialog, page);
  await evidence(page, info, 'arabic-name-suggestion-preview');
  await dialog.getByRole('button', { name: 'Dismiss suggestion', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Use this spelling', exact: true })).toHaveCount(0);
  await expect(arabicName).toHaveValue('');
  await suggest.click();
  await expect(dialog.getByText('محمد عمر', { exact: true })).toBeVisible();
  await dialog.getByRole('button', { name: 'Use this spelling', exact: true }).click();
  await expect(arabicName).toHaveValue('محمد عمر');
  await expect(arabicName).toBeFocused();
  await toReview(dialog);
  const profile = dialog.locator('[data-employee-step="5"] dl').filter({ hasText: 'Arabic full name' });
  await expect(profile).toContainText('Arabic full name');
  await expect(profile).toContainText('محمد عمر');
  await dialog.getByRole('button', { name: 'Edit Profile', exact: true }).click();
  await expect(arabicName).toHaveValue('محمد عمر');
  expect(creates).toHaveLength(0);
  expect(transliterations).toEqual(Array(2).fill({ text: 'Muhammad Umar', target: 'ar', kind: 'person-name' }));
  expect(errors).toEqual([]);
});

test('unsupported Arabic name suggestions explain manual entry and keep the entered Arabic name', async ({ page }, info) => {
  const { dialog, creates, transliterations, errors } = await boot(page, {
    transliteration: { result: { suggestion: '', requiresManualEntry: true } },
  });
  await dialog.getByLabel('English full name', { exact: false }).fill('Unlisted Person');
  const arabicName = dialog.getByLabel('Arabic full name', { exact: true });
  await arabicName.fill('الاسم المعتمد');
  await dialog.getByRole('button', { name: 'Suggest (AR)', exact: true }).click();
  await expect(dialog.getByText('No reliable suggestion for this name. Enter the Arabic spelling from the employee’s passport or ID.', { exact: true })).toBeVisible();
  await expect(arabicName).toHaveValue('الاسم المعتمد');
  await expect(dialog.getByRole('button', { name: 'Suggest (AR)', exact: true })).toBeEnabled();
  await evidence(page, info, 'arabic-name-manual-entry');
  expect(transliterations).toEqual([{ text: 'Unlisted Person', target: 'ar', kind: 'person-name' }]);
  expect(creates).toHaveLength(0);
  expect(errors).toEqual([]);
});

test('a failed Arabic suggestion request is visible and leaves manual entry usable', async ({ page }) => {
  const { dialog, creates, transliterations, errors } = await boot(page, { transliteration: { status: 503 } });
  await dialog.getByLabel('English full name', { exact: false }).fill('Muhammad Umar');
  const arabicName = dialog.getByLabel('Arabic full name', { exact: true });
  await arabicName.fill('محمد عمر');
  await dialog.getByRole('button', { name: 'Suggest (AR)', exact: true }).click();
  await expect(dialog.getByText('Could not get a suggestion. Try again or enter the Arabic name manually.', { exact: true })).toBeVisible();
  await expect(arabicName).toHaveValue('محمد عمر');
  await arabicName.fill('محمد عمرو');
  await expect(arabicName).toHaveValue('محمد عمرو');
  await expect(dialog.getByRole('button', { name: 'Suggest (AR)', exact: true })).toBeEnabled();
  expect(transliterations).toHaveLength(1);
  expect(creates).toHaveLength(0);
  expect(errors).toEqual([]);
});

test('an older backend response without a reliability signal cannot be applied as an Arabic name', async ({ page }) => {
  const { dialog, creates, transliterations, errors } = await boot(page, {
    transliteration: { result: { suggestion: 'محمد ومار' } },
  });
  await dialog.getByLabel('English full name', { exact: false }).fill('Muhammad Umar');
  const arabicName = dialog.getByLabel('Arabic full name', { exact: true });
  await arabicName.fill('محمد عمر');
  await dialog.getByRole('button', { name: 'Suggest (AR)', exact: true }).click();
  await expect(dialog.getByText('No reliable suggestion for this name. Enter the Arabic spelling from the employee’s passport or ID.', { exact: true })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Use this spelling', exact: true })).toHaveCount(0);
  await expect(dialog.getByText('محمد ومار', { exact: true })).toHaveCount(0);
  await expect(arabicName).toHaveValue('محمد عمر');
  expect(transliterations).toHaveLength(1);
  expect(creates).toHaveLength(0);
  expect(errors).toEqual([]);
});

for (const changedDraft of ['English name changed and restored', 'Arabic name manually edited', 'dialog closed and reopened'] as const) {
  test(`a delayed Arabic suggestion cannot overwrite the draft after ${changedDraft}`, async ({ page }) => {
    let releaseSuggestion!: () => void;
    const waitFor = new Promise<void>(resolve => { releaseSuggestion = resolve; });
    const { dialog, creates, transliterations, errors } = await boot(page, { transliteration: { waitFor } });
    const englishName = dialog.getByLabel('English full name', { exact: false });
    const arabicName = dialog.getByLabel('Arabic full name', { exact: true });
    const suggest = dialog.getByRole('button', { name: 'Suggest (AR)', exact: true });
    await englishName.fill('Muhammad Umar');
    await suggest.click();
    await expect.poll(() => transliterations.length).toBe(1);
    await expect(dialog.getByRole('button', { name: 'Suggesting…', exact: true })).toBeDisabled();
    let expectedArabic = '';
    if (changedDraft === 'English name changed and restored') {
      await englishName.fill('Ahmed Ali');
      await englishName.fill('Muhammad Umar');
    } else if (changedDraft === 'Arabic name manually edited') {
      expectedArabic = 'محمد عمرو';
      await arabicName.fill(expectedArabic);
    } else {
      page.once('dialog', prompt => prompt.accept());
      await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
      await expect(dialog).toHaveCount(0);
      await page.getByRole('button', { name: 'Add Employee', exact: true }).first().click();
      await expect(englishName).toHaveValue('');
      await englishName.fill('Ahmed Ali');
      expectedArabic = 'أحمد علي';
      await arabicName.fill(expectedArabic);
    }
    const completed = page.waitForResponse(response => response.url().endsWith('/api/localization/transliterate'));
    releaseSuggestion();
    await (await completed).finished();
    await expect(suggest).toBeEnabled();
    await expect(arabicName).toHaveValue(expectedArabic);
    await expect(dialog.getByRole('button', { name: 'Use this spelling', exact: true })).toHaveCount(0);
    // Navigating away and back also verifies the retained form state after the response.
    await next(dialog, 1);
    await dialog.getByRole('button', { name: 'Back', exact: true }).click();
    await expect(arabicName).toHaveValue(expectedArabic);
    expect(transliterations).toHaveLength(1);
    expect(creates).toHaveLength(0);
    expect(errors).toEqual([]);
  });
}


test('grade benefits preview follows grade and joining date and appears in review', async ({ page }, info) => {
  const { dialog, errors } = await boot(page, { quick: true });
  const previewRequests: string[] = [];
  await page.route('**/api/compensation/benefits/grade-defaults?**', async route => {
    const url = new URL(route.request().url());
    previewRequests.push(url.search);
    const senior = url.searchParams.get('gradeId') === 'grade-other';
    await route.fulfill({ json: [{ benefitPlanId: 'medical', code: 'MED', name: senior ? 'Medical Platinum' : 'Medical Gold', planType: 'Medical', currency: 'SAR', eligible: true, blockingReason: null, eligibilityRuleId: 'medical-rule', entitlementTier: senior ? 'Platinum' : 'Gold', maximumBenefitAmount: 25000, limitPeriod: 'Annual', effectiveFrom: url.searchParams.get('effectiveFrom'), effectiveTo: null },
      { benefitPlanId: 'education', code: 'EDU', name: 'Child education', planType: 'Education', currency: 'SAR', eligible: false, blockingReason: 'Requires 12 months service.', eligibilityRuleId: 'education-rule', entitlementTier: 'Family', maximumBenefitAmount: 15000, limitPeriod: 'Annual', effectiveFrom: url.searchParams.get('effectiveFrom'), effectiveTo: null }] });
  });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await dialog.getByRole('button', { name: 'Next: Employment', exact: true }).click();
  await expect(dialog.getByRole('combobox', { name: 'Grade', exact: true })).toBeVisible();
  await dialog.getByRole('combobox', { name: 'Grade', exact: true }).selectOption('grade-wizard');
  await dialog.getByLabel('Joining date', { exact: false }).fill('2026-10-15');
  const preview = dialog.locator('[data-employee-step="1"]').getByTestId('employee-grade-benefits');
  await expect(preview).toContainText('Medical Gold');
  await expect(preview).toContainText('Assigned by default');
  await expect(preview).toContainText('Requires 12 months service.');
  await dialog.getByRole('combobox', { name: 'Grade', exact: true }).selectOption('grade-other');
  await expect(preview).toContainText('Medical Platinum');
  await expect(preview).not.toContainText('Medical Gold');
  await dialog.getByLabel('Joining date', { exact: false }).fill('2026-11-01');
  await expect.poll(() => previewRequests.at(-1)).toContain('effectiveFrom=2026-11-01');
  await evidence(page, info, 'grade-benefits');
  await dialog.getByRole('button', { name: 'Next: Review', exact: true }).click();
  await expect(dialog.getByRole('heading', { name: 'Review employee details', exact: true })).toBeVisible();
  await expect(dialog.getByText('Default benefits', { exact: true })).toBeVisible();
  const reviewedBenefits = dialog.locator('[data-employee-step="5"]').getByTestId('employee-grade-benefits');
  await expect(reviewedBenefits.getByText('Medical Platinum', { exact: true })).toBeVisible();
  await expect(reviewedBenefits).toContainText('25,000');
  await expect(reviewedBenefits).toContainText('Starts on 2026-11-01');
  await expect(dialog.getByText('Child education: Requires 12 months service.', { exact: true })).toBeVisible();
  expect(previewRequests.some(search => search.includes('companyId=company-wizard'))).toBe(true);
  expect(errors).toEqual([]);
});
