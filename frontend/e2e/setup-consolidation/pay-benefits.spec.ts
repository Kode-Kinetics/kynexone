import { expect, test } from '@playwright/test';
import { ADMIN, boot, movedSectionSpecs, type Persona } from './fixture';
import { setupSectionEntries } from '../setupSections';

// Pay & benefits: Benefits and Loans keep enrolments, loans, advances and bonus batches; their configuration is in Setup.
// Benefit plans is the section for a tenant WITHOUT Release A; with Release A it is retired (tests at the end of this file).
movedSectionSpecs([
  { id: 'benefitPlans', label: 'Benefit plans', titleSuffix: ' (without Release A)', disabledKeys: ['release_a'], group: 'Pay & benefits', old: '/benefits?tab=plans',
    ready: page => page.getByRole('button', { name: /New plan|Add plan|Create.*plan/i }).first(),
    module: { path: '/benefits', goneTab: 'Plans', door: 'Benefit settings' } },
  { id: 'loanTypes', label: 'Loan types', group: 'Pay & benefits', old: '/loans?tab=loanTypes',
    ready: page => page.getByRole('button', { name: /New loan type|Add loan type|Add Type/i }).first(),
    module: { path: '/loans', goneTab: 'Loan Types', door: 'Loan settings' } },
  { id: 'loanPolicies', label: 'Loan policies', group: 'Pay & benefits', old: '/loans?tab=loanPolicies',
    ready: page => page.getByRole('button', { name: /policy/i }).first(),
    module: { path: '/loans', goneTab: 'Loan Policies', door: 'Loan settings' } },
  { id: 'advancePolicy', label: 'Salary advance policy', group: 'Pay & benefits', old: '/loans?tab=advancePolicy',
    ready: page => page.getByRole('button', { name: /Save/i }).first(),
    module: { path: '/loans', goneTab: 'Advance Policy', door: 'Loan settings' } },
  { id: 'bonusTypes', label: 'Bonus types', group: 'Pay & benefits', old: '/loans?tab=bonusTypes',
    ready: page => page.getByRole('button', { name: /bonus type/i }).first(),
    module: { path: '/loans', goneTab: 'Bonus Types', door: 'Loan settings' } },
  { id: 'payslipTemplates', label: 'Payslip templates', group: 'Pay & benefits', old: '/payroll/templates',
    ready: page => page.getByRole('button', { name: /New Template/i }) },
]);

// Release A: Benefits by grade is the one place to configure benefits. It sits in Jobs & headcount right below Grades; the
// Benefit plans section and the empty Benefits Administration page are retired, and /benefits hands over to it.
const READER: Persona = { ...ADMIN, permissions: [...ADMIN.permissions, 'entitlements.read'] };
const jobsAndHeadcount = (page: import('@playwright/test').Page) => setupSectionEntries(page).getByRole('list', { name: 'Jobs & headcount', exact: true });

test.describe('Benefits by grade replaces Benefit plans (Release A)', () => {
  test('Setup lists Benefits by grade right below Grades and no Benefit plans under Pay & benefits', async ({ page }) => {
    const { errors, writes } = await boot(page, { persona: READER, url: '/setup' });
    await expect(page.locator('nav.setup-page-nav')).toBeAttached({ timeout: 30_000 });
    const names = await jobsAndHeadcount(page).getByRole('link').allInnerTexts();
    expect(names.slice(0, 2), 'Jobs & headcount starts Grades, then Benefits by grade').toEqual(['Grades', 'Benefits by grade']);
    expect(names).toContain('Designations');
    await expect(setupSectionEntries(page).getByRole('link', { name: 'Benefit plans', exact: true })).toHaveCount(0);
    await expect(setupSectionEntries(page).getByRole('list', { name: 'Pay & benefits', exact: true }).getByRole('link', { name: 'Loan types', exact: true })).toBeAttached();
    expect(errors, 'No application runtime errors').toEqual([]);
    expect(writes, 'Opening Setup writes nothing').toEqual([]);
  });

  test('the retired Benefit plans address lands on the first available section and says it is unavailable', async ({ page }) => {
    await boot(page, { persona: READER, url: '/setup?tab=benefitPlans' });
    await expect(page).toHaveURL(/unavailable=benefitPlans/, { timeout: 30_000 });
    await expect(page.locator('#setup-section-heading')).not.toHaveText('Benefit plans');
    await expect(page.locator('[data-module-section="benefitPlans"]')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add benefit plan', exact: true })).toHaveCount(0);
  });

  test('/benefits with nothing left on it hands over to Benefits by grade, which is the menu shortcut', async ({ page }) => {
    const { errors } = await boot(page, { persona: READER, url: '/benefits' });
    await expect(page).toHaveURL(/\/benefits\/by-grade$/, { timeout: 30_000 });
    await expect(page.getByRole('heading', { name: 'Benefits Administration' })).toHaveCount(0);
    const menu = page.getByRole('navigation', { name: 'Primary navigation', exact: true });
    await expect(menu.getByRole('button', { name: 'Benefits by grade', exact: true })).toBeVisible();
    await expect(menu.getByRole('button', { name: 'Benefits', exact: true })).toHaveCount(0);
    expect(errors, 'No application runtime errors').toEqual([]);
  });

  test('/benefits keeps enrolments made before, points to Benefits by grade and offers no plan settings', async ({ page }) => {
    const plan = { id: 'edu', companyId: 'fixture-company', code: 'EDU', name: 'Child Education', planType: 'Education', classification: 'Discretionary', currency: 'SAR', effectiveFrom: '2026-01-01', effectiveTo: null, requiresEnrollment: true, isActive: true, paymentPolicy: null, policyVersion: 1 };
    const { errors } = await boot(page, { persona: READER, url: '/benefits', mocks: { '/api/compensation/benefits/plans': [plan] } });
    await expect(page.getByTestId('benefits-moved-note')).toBeVisible({ timeout: 30_000 });
    await expect(page).toHaveURL(/\/benefits$/);
    await expect(page.getByTestId('benefits-moved-note').getByRole('link', { name: 'Open Benefits by grade', exact: true })).toHaveAttribute('href', '/benefits/by-grade');
    await expect(page.getByRole('link', { name: 'Benefit settings', exact: true })).toHaveCount(0);
    await expect(page.getByRole('tab', { name: 'Plans', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /Add benefit plan|New plan/ })).toHaveCount(0);
    expect(errors, 'No application runtime errors').toEqual([]);
  });
});
