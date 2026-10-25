import { expect, test, type Page } from '@playwright/test';

// The grade is the one place a pay package is defined. Setup → Grades → Edit Grade picks each line from the company's pay
// component catalogue (with "+ New component" inline), totals it and warns outside Min–Max; Payroll → Grade packages
// shows the result read-only with a link back to the grade. Every API call is intercepted; nothing is written.

const SHOTS = process.env.GRADE_PACKAGE_SHOTS_DIR;
async function shot(page: Page, name: string) {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: true });
}

const company = { id: 'co-alpha', legalNameEn: 'Alpha Trading LLC', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
const grade = { id: 'grade-g5', code: 'G5', name: 'Professional 5', nameAr: null, band: 'P', level: 5, minSalary: 15000, midSalary: 17500, maxSalary: 20000, currency: 'SAR', isActive: true, companyId: company.id };
const catalogue = [
  { id: null, companyId: null, code: 'BASIC', nameEn: 'Basic salary', nameAr: 'الراتب الأساسي', componentType: 'Earning', isTaxable: true, isSystem: true },
  { id: null, companyId: null, code: 'HOUSING', nameEn: 'Housing allowance', nameAr: 'بدل السكن', componentType: 'Earning', isTaxable: false, isSystem: true },
  { id: null, companyId: null, code: 'TRANSPORT', nameEn: 'Transport allowance', nameAr: 'بدل النقل', componentType: 'Earning', isTaxable: false, isSystem: true },
  { id: null, companyId: null, code: 'FIXED_DEDUCTION', nameEn: 'Fixed deduction', nameAr: 'خصم ثابت', componentType: 'Deduction', isTaxable: false, isSystem: true },
];
// A grade saved before the catalogue existed: BASIC is known, TICKET was typed free-hand.
const savedLines = [
  { id: 'l1', componentCode: 'BASIC', componentName: 'Basic', componentType: 'Earning', calculationType: 'Fixed', amount: 10000, percentage: 0, isTaxable: true, frequency: 'Monthly', sortOrder: 1, isActive: true },
  { id: 'l2', componentCode: 'TICKET', componentName: 'Air ticket', componentType: 'Benefit', calculationType: 'Fixed', amount: 2500, percentage: 0, isTaxable: false, frequency: 'Monthly', sortOrder: 2, isActive: true },
];

async function boot(page: Page, url: string) {
  const writes: Array<{ method: string; path: string; body: unknown }> = [];
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', 'en');
    localStorage.setItem('kynexone-company:reviewer', 'co-alpha');
  });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (request.method() !== 'GET') writes.push({ method: request.method(), path, body: request.postDataJSON() });
    if (path === '/api/auth/me') return reply({
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: ['Admin'],
      permissions: ['organization.read', 'organization.write', 'payroll.read', 'payroll.write', 'dashboard.read'],
      accountType: 'Group', isGroupScope: true,
      companies: [{ id: company.id, name: company.legalNameEn, code: 'ALPHA', countryCode: 'SAU', isActive: true }],
    });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/companies') return reply({ items: [company], total: 1, page: 1, pageSize: 100 });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/grades' && request.method() === 'GET') return reply({ items: [grade], total: 1, page: 1, pageSize: 100 });
    if (path === `/api/grades/${grade.id}` && request.method() === 'PUT') return reply(grade);
    if (path === `/api/grades/${grade.id}/pay-scale` && request.method() === 'GET') return reply(savedLines);
    if (path === `/api/grades/${grade.id}/pay-scale` && request.method() === 'PUT') return reply({ count: 3, structure: { id: 's1', code: 'GRADE-G5', versionNumber: 2 } });
    if (path === '/api/grades/package-components' && request.method() === 'GET') return reply(catalogue);
    if (path === '/api/grades/package-components' && request.method() === 'POST') {
      const body = request.postDataJSON() as { nameEn: string; nameAr?: string; componentType: string; isTaxable: boolean };
      return reply({ id: 'pc-ticket', companyId: company.id, code: 'AIR_TICKET', nameEn: body.nameEn, nameAr: body.nameAr || body.nameEn, componentType: body.componentType, isTaxable: body.isTaxable, isSystem: false });
    }
    if (path === '/api/payroll/grade-packages') return reply({
      source: 'GradePayScale',
      grades: [{
        gradeId: grade.id, gradeCode: 'G5', gradeName: 'Professional 5', gradeNameAr: null, companyId: company.id, companyName: 'Alpha Trading',
        currency: 'SAR', minSalary: 15000, maxSalary: 20000, isActive: true, monthlyGross: 13000, monthlyDeductions: 0, outsideRange: true,
        employeeCount: 4, structureCode: 'GRADE-G5-V2', structureVersion: 2, salariesOnStructure: 4,
        lines: [
          { code: 'BASIC', name: 'Basic salary', componentType: 'Earning', calculationType: 'Fixed', amount: 10000, percentage: 0, isTaxable: true, frequency: 'Monthly', periodAmount: 10000 },
          { code: 'HOUSING', name: 'Housing allowance', componentType: 'Earning', calculationType: 'PercentOfBasic', amount: 0, percentage: 25, isTaxable: false, frequency: 'Monthly', periodAmount: 2500 },
          { code: 'TRANSPORT', name: 'Transport allowance', componentType: 'Earning', calculationType: 'Fixed', amount: 500, percentage: 0, isTaxable: false, frequency: 'Monthly', periodAmount: 500 },
        ],
      }],
      olderStructures: [{
        id: 'old-1', companyId: company.id, companyName: 'Alpha Trading', code: 'ALPHA-STANDARD', name: 'Alpha standard structure', currency: 'SAR',
        effectiveDate: '2025-01-01', minGrossSalary: 0, maxGrossSalary: 0, minBasicSalary: 0, maxBasicSalary: 0, eligibleGradeIds: [], eligibleDesignationIds: [],
        versionNumber: 1, previousVersionId: null, isActive: true, createdAtUtc: '2025-01-01T00:00:00Z', assignedEmployeeCount: 1, components: [],
      }],
    });
    if (path === '/api/payroll/companies') return reply([{ id: company.id, name: company.legalNameEn, tradeName: 'Alpha Trading', currency: 'SAR' }]);
    // Other payroll reads are outside this contract: answer 404 so each screen takes its own empty/error path.
    if (path.startsWith('/api/payroll/')) return reply({ message: 'Not part of this fixture.' }, 404);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(url);
  return { writes, errors };
}

test('Edit Grade: lines come from the catalogue, a legacy line becomes a new component, the total is checked against the range', async ({ page }) => {
  const { writes, errors } = await boot(page, `/setup?tab=grades&grade=${grade.id}`);
  const dialog = page.getByRole('dialog', { name: 'Edit Grade' });
  await expect(dialog).toBeVisible({ timeout: 30_000 });
  const editor = dialog.getByTestId('grade-package-editor');
  const lines = editor.getByTestId('grade-package-line');
  await expect(lines).toHaveCount(2);

  // No free-text code inputs: each line is a dropdown of the company's pay components.
  await expect(editor.getByRole('textbox', { name: 'Component code' })).toHaveCount(0);
  await expect(lines.nth(0).getByRole('combobox', { name: 'Pay component' })).toHaveValue('BASIC');
  await expect(lines.nth(1).getByRole('alert')).toContainText('TICKET is not one of this company');
  await shot(page, 'edit-grade-legacy-line');

  // 10,000 basic; the benefit line is not monthly cash. Below the grade's 15,000 minimum.
  const total = editor.getByTestId('grade-package-total');
  await expect(total).toContainText('Gross monthly cash');
  await expect(total).toContainText('10,000');
  await expect(editor.getByTestId('grade-package-range-warning')).toContainText("Below this grade's minimum");

  // Saving with the legacy line is blocked on the client.
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toContainText('Choose a listed component for every line');
  expect(writes.filter(w => w.path.endsWith('/pay-scale'))).toHaveLength(0);

  // The legacy line is added to the catalogue in place and selected.
  await lines.nth(1).getByRole('button', { name: 'Add as new component' }).click();
  const form = editor.getByTestId('new-pay-component');
  await expect(form.getByRole('textbox', { name: 'Name (English)' })).toHaveValue('Air ticket');
  await form.getByRole('combobox', { name: 'Type' }).selectOption('Benefit');
  await form.getByRole('button', { name: 'Add to the list' }).click();
  await expect(lines.nth(1).getByRole('combobox', { name: 'Pay component' })).toHaveValue('AIR_TICKET');
  const created = writes.find(w => w.path === '/api/grades/package-components');
  expect(created?.body).toMatchObject({ nameEn: 'Air ticket', componentType: 'Benefit' });

  // Housing at 25% of basic, plus transport; a blank line left unfinished.
  await editor.getByRole('button', { name: 'Add line' }).click();
  await lines.nth(2).getByRole('combobox', { name: 'Pay component' }).selectOption('HOUSING');
  await lines.nth(2).getByRole('combobox', { name: 'Calculation type' }).selectOption('PercentOfBasic');
  await lines.nth(2).getByRole('spinbutton', { name: 'Percentage of basic' }).fill('25');
  await editor.getByRole('button', { name: 'Add line' }).click();
  await lines.nth(3).getByRole('combobox', { name: 'Pay component' }).selectOption('TRANSPORT');
  await lines.nth(3).getByRole('spinbutton', { name: 'Amount' }).fill('3000');
  await editor.getByRole('button', { name: 'Add line' }).click();
  await expect(lines).toHaveCount(5);
  // Basic cannot be a percentage of itself; a component already on the grade cannot be picked twice.
  await expect(lines.nth(0).getByRole('option', { name: '% of Basic' })).toBeDisabled();
  await expect(lines.nth(4).getByRole('option', { name: 'Housing allowance' })).toBeDisabled();

  await expect(total).toContainText('15,500');
  await expect(editor.getByTestId('grade-package-range-warning')).toHaveCount(0);
  await shot(page, 'edit-grade-package');

  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toBeHidden();
  const saved = writes.find(w => w.path === `/api/grades/${grade.id}/pay-scale`)!.body as Array<Record<string, unknown>>;
  expect(saved.map(l => l.componentCode)).toEqual(['BASIC', 'AIR_TICKET', 'HOUSING', 'TRANSPORT']);
  expect(saved[0]).toMatchObject({ componentName: 'Basic salary', isTaxable: true, amount: 10000 });
  expect(saved[2]).toMatchObject({ calculationType: 'PercentOfBasic', percentage: 25, amount: 0 });
  expect(errors).toEqual([]);
});

test('Payroll → Grade packages is read-only and links each package back to its grade', async ({ page }) => {
  const { writes, errors } = await boot(page, '/payroll');
  await page.getByRole('tab', { name: 'Grade Packages' }).click();
  const view = page.getByTestId('grade-packages');
  await expect(view).toBeVisible({ timeout: 30_000 });
  const card = view.getByTestId('grade-package-card');
  await expect(card).toHaveCount(1);
  await expect(card).toContainText('Professional 5');
  await expect(card).toContainText('Housing allowance');
  await expect(card.getByTestId('grade-package-gross')).toContainText('13,000');
  await expect(card).toContainText("Outside the grade's range");
  await expect(card).toContainText('4 employees');
  await expect(card).toContainText('GRADE-G5-V2');
  await expect(card.getByRole('link', { name: 'Edit in Setup' })).toHaveAttribute('href', `/setup?tab=grades&grade=${grade.id}`);

  const older = view.getByTestId('older-structures');
  await expect(older.getByRole('heading', { name: 'Older structures (read-only)' })).toBeVisible();
  await expect(older).toContainText('ALPHA-STANDARD');
  await shot(page, 'payroll-grade-packages');

  // Nothing here creates, edits or deletes a structure.
  for (const name of ['New Structure', 'Edit', 'Delete', 'Import']) await expect(view.getByRole('button', { name, exact: true })).toHaveCount(0);
  expect(writes).toEqual([]);
  expect(errors).toEqual([]);
});
