import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

// OD4: the scoped new-hire pay approval view. Every API call is intercepted; nothing is written.
const SHOTS = process.env.PAY_APPROVALS_SHOTS_DIR;

const ITEM = {
  employeeId: 41, employeeCode: 'EMP-0041', name: 'Layla Al-Harbi', companyId: 'c1', companyName: 'Workspace Fixture LLC', designation: 'Senior Analyst',
  currency: 'SAR', gross: 10500, ibanLast4: '7519', enteredBy: { name: 'Huda HR Officer', atUtc: '2026-10-26T09:15:00Z' }, lastChangedAtUtc: '2026-10-26T09:15:00Z', status: 'pending',
};
const DETAIL = {
  employee: { id: 41, code: 'EMP-0041', name: 'Layla Al-Harbi', companyId: 'c1', companyName: 'Workspace Fixture LLC', designation: 'Senior Analyst' },
  components: { basic: 8000, housing: 2000, transport: 500, food: 0, mobile: 0, other: 0, fixedDeduction: 0, gross: 10500, currency: 'SAR' },
  bank: { bankName: 'Riyad Bank', ibanLast4: '7519', accountLast4: null, accountHolderName: 'Layla Al-Harbi' },
  enteredBy: { name: 'Huda HR Officer', atUtc: '2026-10-26T09:15:00Z' },
  lastChangedBy: { name: 'Huda HR Officer', atUtc: '2026-10-26T09:15:00Z' },
  fingerprint: 'a'.repeat(64), sharedIbanCount: 1, lastRejection: null,
};

const errorsByPage = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  errorsByPage.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
});
test.afterEach(async ({ page }) => { expect(errorsByPage.get(page), 'No application runtime errors').toEqual([]); });

interface Boot { arabic?: boolean; permissions?: string[]; detail?: typeof DETAIL; decideStatus?: number; decideCode?: string; empty?: boolean }

async function boot(page: Page, url: string, options: Boot = {}) {
  const decisions: Array<Record<string, unknown>> = [];
  await page.addInitScript(({ arabic }) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', arabic ? 'ar' : 'en');
    localStorage.setItem('kynexone-company:reviewer', 'c1');
  }, { arabic: options.arabic ?? false });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (path === '/api/auth/me') return reply({
      id: 'finance', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Fatima Finance Approver', roles: ['Finance Approver'],
      permissions: options.permissions ?? ['payroll.approve', 'payroll.read', 'dashboard.read'], accountType: 'Company', isGroupScope: false,
      companies: [{ id: 'c1', name: 'Workspace Fixture LLC', code: 'c1', countryCode: 'SA', isActive: true }],
    });
    if (path === '/api/pay-approvals' && request.method() === 'GET') return reply({ items: options.empty ? [] : [ITEM, { ...ITEM, employeeId: 42, employeeCode: 'EMP-0042', name: 'Omar Al-Qahtani', status: 'rejected', ibanLast4: '1234' }], count: options.empty ? 0 : 2, hasMore: false });
    if (path === '/api/pay-approvals/41' && request.method() === 'GET') return reply(options.detail ?? DETAIL);
    if (path === '/api/pay-approvals/41/decision') {
      decisions.push(request.postDataJSON());
      if (options.decideStatus && options.decideStatus !== 200) return reply({ code: options.decideCode, title: options.decideCode }, options.decideStatus);
      return reply({ decision: request.postDataJSON().decision === 'approve' ? 'approved' : 'rejected' });
    }
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path.startsWith('/api/features')) return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(url);
  return { decisions };
}

async function shot(page: Page, name: string) { if (SHOTS) await page.screenshot({ path: `${SHOTS}/od4-${name}.png`, fullPage: true }); }

test.describe('Pay approvals queue', () => {
  test('lists the packages with the masked account only, and opens one', async ({ page }, info) => {
    await boot(page, '/pay-approvals');
    await expect(page.getByRole('heading', { level: 1, name: 'Pay approvals' })).toBeVisible({ timeout: 30_000 });
    const list = page.getByTestId('pay-approvals-list');
    await expect(list.getByText('Layla Al-Harbi')).toBeVisible();
    await expect(list.getByText('•••• 7519')).toBeVisible();
    await expect(list.getByText('Sent back')).toBeVisible();
    const html = await page.content();
    for (const secret of ['SA0380000000608010167519', '2123456789']) expect(html).not.toContain(secret);
    if (info.project.name === 'desktop') await shot(page, 'queue-en-1440');
    await page.getByRole('link', { name: 'Review pay package for Layla Al-Harbi' }).click();
    await expect(page).toHaveURL(/\/pay-approvals\/41$/);
  });
  test('says so when nothing is waiting', async ({ page }) => {
    await boot(page, '/pay-approvals', { empty: true });
    await expect(page.getByTestId('pay-approvals-empty')).toBeVisible({ timeout: 30_000 });
  });
  test('is closed to anyone without payroll.approve', async ({ page }) => {
    await boot(page, '/pay-approvals', { permissions: ['dashboard.read', 'employees.sensitive'] });
    await expect(page).toHaveURL(/\/dashboard/, { timeout: 30_000 });
  });
});

test.describe('Pay approval detail', () => {
  test('shows only the scoped figures and the shared-account warning, and approves what it showed', async ({ page }, info) => {
    const { decisions } = await boot(page, '/pay-approvals/41');
    await expect(page.getByRole('heading', { level: 1 })).toContainText('Layla Al-Harbi', { timeout: 30_000 });
    await expect(page.getByTestId('pa-iban')).toHaveText('•••• 7519');
    await expect(page.getByTestId('pa-gross')).toContainText('10,500.00');
    await expect(page.getByTestId('shared-iban-warning')).toContainText('another employee');
    const text = await page.locator('main').innerText();
    for (const hidden of ['Iqama', 'Passport', 'Date of birth', 'National ID', 'SA03']) expect(text).not.toContain(hidden);
    if (info.project.name === 'desktop') await shot(page, 'detail-en-1440');
    if (info.project.name === 'mobile') await shot(page, 'detail-en-360');

    await page.getByRole('button', { name: 'Approve' }).click();
    await expect(page.locator('#pa-reason-error')).toBeVisible();
    await page.getByLabel('Reason').fill('Matches the signed offer letter');
    await page.getByRole('button', { name: 'Approve' }).click();
    await expect(page.getByTestId('pay-approval-outcome')).toContainText('Approved');
    expect(decisions).toEqual([{ fingerprint: 'a'.repeat(64), decision: 'approve', reason: 'Matches the signed offer letter' }]);
  });
  test('a package that moved asks for a reload instead of approving', async ({ page }) => {
    await boot(page, '/pay-approvals/41', { decideStatus: 409, decideCode: 'package_changed' });
    await page.getByLabel('Reason').fill('Checked');
    await page.getByRole('button', { name: 'Approve' }).click();
    const failure = page.getByTestId('pay-approval-failure');
    await expect(failure).toContainText('changed after you opened it');
    await expect(failure.getByRole('button', { name: 'Reload the package' })).toBeVisible();
  });
  test('a barred approver is told why', async ({ page }) => {
    await boot(page, '/pay-approvals/41', { decideStatus: 403, decideCode: 'second_person_required' });
    await page.getByLabel('Reason').fill('Checked');
    await page.getByRole('button', { name: 'Send back' }).click();
    await expect(page.getByTestId('pay-approval-failure')).toContainText('you entered it');
  });
  test('has no accessibility violations', async ({ page }) => {
    await boot(page, '/pay-approvals/41');
    await expect(page.getByTestId('pa-gross')).toBeVisible({ timeout: 30_000 });
    const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    expect(result.violations.map(v => `${v.id}: ${v.nodes[0]?.html}`)).toEqual([]);
  });
  test('reads right to left in Arabic', async ({ page }, info) => {
    await boot(page, '/pay-approvals/41', { arabic: true });
    await expect(page.getByRole('heading', { level: 1 })).toContainText('Layla Al-Harbi', { timeout: 30_000 });
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByTestId('shared-iban-warning')).toContainText('موظف آخر');
    await expect(page.getByRole('button', { name: 'موافقة' })).toBeVisible();
    // CD65: the byline isolates the Latin name and the time so the Arabic sentence cannot reorder them
    const byline = await page.locator('dd', { hasText: 'Huda HR Officer' }).first().textContent();
    expect(byline).toContain('\u2068Huda HR Officer\u2069');
    expect(byline).toMatch(/\u2066[^\u2069]+\u2069/);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(1);
    if (info.project.name === 'mobile') await shot(page, 'detail-ar-360');
    if (info.project.name === 'desktop') await shot(page, 'detail-ar-1440');
  });
});
