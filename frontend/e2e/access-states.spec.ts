import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { employees } from './employee-workspace.fixture';

// Three separate answers on the profile (Employee active, Login enabled, Password configured), the truthful activation
// blockers, and the pay-approvals creator notice. Every API call is intercepted; nothing is written.
const SHOTS = process.env.ACCESS_STATES_SHOTS_DIR;
const PAY_BLOCKER = { key: 'pay:compensationUnapproved', label: 'Pay package awaiting Finance approval', category: 'pay' };
const OTHER_BLOCKER = { key: 'org:department', label: 'Department', category: 'org' };

const errorsByPage = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  errorsByPage.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
});
test.afterEach(async ({ page }) => { expect(errorsByPage.get(page), 'No application runtime errors').toEqual([]); });

type AccessState = 'waiting_for_work_email' | 'not_started' | 'code_given' | 'active' | 'stopped' | 'blocked';
interface Boot {
  arabic?: boolean;
  permissions?: string[];
  access?: AccessState;
  workEmail?: string;
  status?: string;
  blocking?: Array<{ key: string; label: string; category: string }>;
  readinessFails?: boolean;
  activate?: Array<{ status: number; json: unknown }>;
  eligibleReviewers?: Array<{ id: string; name: string }>;
  userId?: string;
  emptyQueueCount?: number;
}
const FULL = ['employees.read', 'employees.write', 'employees.approve', 'employees.access.issue', 'employees.access.reset', 'dashboard.read'];

async function boot(page: Page, url: string, options: Boot = {}) {
  const calls = { readiness: 0, access: 0, activate: 0 };
  const activations = [...(options.activate ?? [])];
  let blocking = options.blocking ?? [];
  await page.addInitScript(({ arabic }) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', arabic ? 'ar' : 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
  }, { arabic: options.arabic ?? false });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (path === '/api/auth/me') return reply({
      id: options.userId ?? 'hr', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Huda HR', roles: ['HR'],
      permissions: options.permissions ?? FULL, accountType: 'Company', isGroupScope: false,
      companies: [{ id: 'fixture-company', name: 'Evostel', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
    });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'en' });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/companies') return reply({ items: [{ id: 'fixture-company', legalNameEn: 'Evostel', countryCode: 'SAU', emailDomain: 'evostel.test', isActive: true, approvalStatus: 'Active' }], total: 1, page: 1, pageSize: 100 });
    if (path === '/api/employee-completion/901') return reply({ requested: false, status: 'NotRequested', selfServiceAvailable: true });
    if (path === '/api/employee-access/901') {
      calls.access += 1;
      const workEmail = options.workEmail ?? employees[901].workEmail;
      const state = options.access ?? 'not_started';
      return reply({ employeeId: 901, employeeName: employees[901].fullName, employeeCode: 'EMP-1', workEmail, state, canIssue: true, emailDelivery: false,
        codeExpiresAtUtc: state === 'code_given' ? '2026-12-01T10:00:00Z' : null, codeIssuedByName: null, lastCodeExpiredAtUtc: null,
        lastSignInAtUtc: state === 'active' ? '2026-10-01T10:00:00Z' : null, stoppedReason: state === 'stopped' ? 'disabled_by_admin' : null, blockedCode: null, blockedReason: null });
    }
    if (path === '/api/employees/901/readiness') {
      calls.readiness += 1;
      if (options.readinessFails) return reply({ message: 'down' }, 503);
      return reply({ employeeId: 901, state: blocking.length ? 'Blocked' : 'Ready', score: 60, progress: { present: 3, requiredTotal: 5 },
        policy: { countryCode: 'SAU', tier: 'Activation', sources: [] }, blocking, payBlocking: [], recommended: [], present: [], expiringSoon: [], disclaimer: '' });
    }
    if (path === '/api/employees/901/activate') {
      calls.activate += 1;
      const next = activations.shift();
      if (next) { if (next.status === 200) blocking = []; return reply(next.json, next.status); }
      blocking = [];
      return reply({ ...employees[901], status: 'Active' });
    }
    if (path === '/api/employees/901') return reply({ ...employees[901], workEmail: options.workEmail ?? employees[901].workEmail, status: options.status ?? 'Draft' });
    if (path === '/api/pay-approvals/setup') return reply({
      independentReviewerCount: 1, readyForNewHires: true, pendingCount: 1, checkedCount: 1, strandedCount: 0, hasMore: false,
      items: [{ employeeId: 901, name: employees[901].fullName, eligibleReviewers: options.eligibleReviewers ?? [] }],
    });
    if (path === '/api/pay-approvals' && request.method() === 'GET') return reply({ items: [], count: 0, hasMore: false, requiresOtherReviewerCount: options.emptyQueueCount ?? 0 });
    return reply({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(url);
  return { calls };
}

const PROFILE = '/people/901?section=access-onboarding';
// Known cold dev-server flake: the first compile of a route reloads the document. Warm it, then run the test.
async function warm(page: Page) {
  await page.route('**/api/**', route => route.fulfill({ json: {} }));
  await page.goto(PROFILE).catch(() => undefined);
  await page.waitForTimeout(1500);
  await page.unroute('**/api/**');
}
test.beforeEach(async ({ page }, info) => { if (info.title.includes('profile') || info.title.includes('blocker') || info.title.includes('server')) await warm(page); });

const group = (page: Page) => page.getByRole('group', { name: 'Employment and sign-in status' });
async function shot(page: Page, name: string) { if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: true }); }

test.describe('profile: three separate states', () => {
  test('profile shows three distinct rows, each with a status, who can act and a next action', async ({ page }, info) => {
    await boot(page, PROFILE, { access: 'not_started' });
    await expect(group(page)).toBeVisible({ timeout: 45_000 });
    const rows = group(page).locator('dl > div');
    await expect(rows).toHaveCount(3);
    await expect(group(page).locator('dt')).toHaveText(['Employee active', 'Login enabled', 'Password configured']);
    await expect(group(page).getByTestId('state-employee')).toContainText('Not active yet');
    await expect(group(page).getByTestId('state-login')).toContainText('No login yet');
    await expect(group(page).getByTestId('state-password')).toContainText('Not set up');
    for (const row of await rows.all()) {
      await expect(row).toContainText('Who can act');
      await expect(row).toContainText('Next action');
      expect(await row.getByRole('button').count()).toBeLessThanOrEqual(1);
    }
    await expect(group(page).getByText('Signing in does not activate employment.')).toBeVisible();
    await expect(group(page).getByRole('button', { name: 'Activate' })).toBeEnabled();
    await expect(group(page).getByRole('button', { name: 'Give access' })).toBeVisible();
    if (info.project.name === 'mobile') {
      for (const button of await group(page).getByRole('button').all()) {
        expect((await button.boundingBox())!.height).toBeGreaterThanOrEqual(43.5);
      }
    }
    await shot(page, `profile-en-${info.project.name}`);
  });

  test('profile with login and password set while not active leaves Activate disabled', async ({ page }) => {
    await boot(page, PROFILE, { access: 'active', blocking: [OTHER_BLOCKER] });
    await expect(group(page)).toBeVisible({ timeout: 45_000 });
    await expect(group(page).getByTestId('state-employee')).toContainText('Not active yet');
    await expect(group(page).getByTestId('state-login')).toContainText('Login enabled');
    await expect(group(page).getByTestId('state-password')).toContainText('Password set');
    await expect(group(page).getByRole('button', { name: 'Activate' })).toBeDisabled();
    await expect(group(page).getByRole('button', { name: 'Reset sign-in' })).toBeVisible();
  });

  test('profile code given but not used reads as login enabled, password not used', async ({ page }) => {
    await boot(page, PROFILE, { access: 'code_given' });
    await expect(group(page).getByTestId('state-login')).toContainText('Login enabled', { timeout: 45_000 });
    await expect(group(page).getByTestId('state-password')).toContainText('Code given, not used');
    await expect(group(page).getByRole('button', { name: 'Give new code' })).toBeVisible();
    await expect(group(page).getByRole('button', { name: 'Reset sign-in' })).toHaveCount(0);
  });

  test('profile Reset sign-in is per person and asks first; no reset-all exists', async ({ page }) => {
    await boot(page, PROFILE, { access: 'active' });
    await expect(group(page)).toBeVisible({ timeout: 45_000 });
    await group(page).getByRole('button', { name: 'Reset sign-in' }).click();
    await expect(group(page).getByRole('alertdialog')).toContainText('Their current password keeps working');
    await expect(page.getByText(/reset all|reset everyone|reset sign-in for all/i)).toHaveCount(0);
    await group(page).getByRole('button', { name: 'Cancel' }).click();
    await expect(group(page).getByRole('alertdialog')).toHaveCount(0);
  });

  test('profile without a work email dims rows B and C and does not offer access', async ({ page }) => {
    await boot(page, PROFILE, { access: 'waiting_for_work_email', workEmail: '' });
    await expect(group(page)).toBeVisible({ timeout: 45_000 });
    await expect(group(page).getByTestId('state-login')).toContainText('Needs work email first');
    await expect(group(page).getByTestId('state-password')).toContainText('Needs work email first');
    await expect(group(page).getByRole('button', { name: 'Give access' })).toHaveCount(0);
  });

  test('profile for a reader without approval or sign-in rights shows no buttons', async ({ page }) => {
    await boot(page, PROFILE, { access: 'active', permissions: ['employees.read', 'dashboard.read'], blocking: [] });
    await expect(group(page)).toBeVisible({ timeout: 45_000 });
    await expect(group(page).getByRole('button')).toHaveCount(0);
    await expect(group(page).getByTestId('state-employee')).toContainText('Ask an HR approver to activate.');
  });

  test('profile access status unavailable still lets the approver activate and offers Try again', async ({ page }) => {
    await boot(page, PROFILE, { blocking: [] });
    await page.unroute('**/api/employee-access/901').catch(() => undefined);
    await page.route('**/api/employee-access/901', route => route.fulfill({ status: 500, json: { message: 'down' } }));
    await page.reload();
    await expect(group(page).getByRole('button', { name: 'Try again' })).toBeVisible({ timeout: 45_000 });
    await expect(group(page).getByRole('button', { name: 'Activate' })).toBeEnabled();
  });

  test('profile has no accessibility violations', async ({ page }) => {
    await boot(page, PROFILE, { access: 'code_given', blocking: [PAY_BLOCKER], permissions: [...FULL, 'payroll.approve', 'security.manage'], eligibleReviewers: [{ id: 'fin', name: 'Fatima Finance Approver' }] });
    await expect(group(page)).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('eligible-reviewers')).toBeVisible();
    const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    expect(result.violations.map(v => `${v.id}: ${v.nodes[0]?.html}`)).toEqual([]);
  });

  test('profile reads right to left in Arabic with the agreed copy', async ({ page }, info) => {
    await boot(page, PROFILE, { arabic: true, access: 'code_given', blocking: [PAY_BLOCKER], permissions: [...FULL, 'payroll.approve'] });
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    const panel = page.getByRole('group', { name: 'حالة التوظيف وتسجيل الدخول' });
    await expect(panel).toBeVisible({ timeout: 45_000 });
    await expect(panel.locator('dt')).toHaveText(['الموظف نشط', 'تسجيل الدخول مفعّل', 'كلمة المرور مضبوطة']);
    await expect(panel.getByTestId('state-employee')).toContainText('بانتظار الموافقة على الأجر');
    await expect(panel.getByTestId('state-password')).toContainText('أُعطي رمز ولم يُستخدم');
    await expect(panel.getByText('تسجيل الدخول لا يُفعّل التوظيف.')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(1);
    const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
    expect(result.violations.map(v => `${v.id}: ${v.nodes[0]?.html}`)).toEqual([]);
    await shot(page, `profile-ar-${info.project.name}`);
  });
});

test.describe('activation blockers', () => {
  test('blocker for a Finance-capable administrator names who can act and the eligible reviewers', async ({ page }) => {
    await boot(page, PROFILE, { access: 'not_started', blocking: [PAY_BLOCKER], permissions: [...FULL, 'payroll.approve', 'security.manage'], eligibleReviewers: [{ id: 'fin', name: 'Fatima Finance Approver' }, { id: 'fin2', name: 'Omar Reviewer' }], userId: 'hr' });
    const detail = page.getByTestId('pay-blocker-detail');
    await expect(detail).toBeVisible({ timeout: 45_000 });
    await expect(detail).toContainText('a Finance Approver who did not enter or change this pay and is not this employee');
    await expect(page.getByTestId('eligible-reviewers')).toContainText('Fatima Finance Approver');
    await expect(page.getByTestId('eligible-reviewers')).toContainText('Omar Reviewer');
    await expect(detail).toContainText('You are not an independent reviewer for this pay package');
    await expect(detail.getByRole('link', { name: 'Pay approvals' })).toHaveAttribute('href', '/pay-approvals');
    await expect(detail.getByRole('link', { name: 'Check reviewer setup' })).toHaveAttribute('href', /\/setup\?tab=users&companyId=fixture-company/);
    await expect(group(page).getByTestId('state-employee')).toContainText('Awaiting pay approval');
    await expect(group(page).getByRole('button', { name: 'Activate' })).toBeDisabled();
  });

  test('blocker for someone without approval rights says to ask the administrator and lists no names', async ({ page }) => {
    await boot(page, PROFILE, { blocking: [PAY_BLOCKER], eligibleReviewers: [{ id: 'fin', name: 'Fatima Finance Approver' }] });
    const detail = page.getByTestId('pay-blocker-detail');
    await expect(detail).toBeVisible({ timeout: 45_000 });
    await expect(detail).toContainText('Ask your administrator.');
    await expect(page.getByTestId('eligible-reviewers')).toHaveCount(0);
    await expect(detail.getByRole('link')).toHaveCount(0);
  });

  test('blocker readiness unavailable says so, offers Retry and keeps Activate disabled', async ({ page }) => {
    const { calls } = await boot(page, PROFILE, { readinessFails: true });
    const blockers = page.getByTestId('activation-blockers');
    await expect(blockers).toContainText('Readiness could not be checked.', { timeout: 45_000 });
    await expect(group(page).getByRole('button', { name: 'Activate' })).toBeDisabled();
    const before = calls.readiness;
    await blockers.getByRole('button', { name: 'Retry' }).click();
    await expect.poll(() => calls.readiness).toBeGreaterThan(before);
  });

  test('server rejection shows the backend message verbatim, refetches, and the button is usable again', async ({ page }) => {
    const message = 'This employee cannot be activated yet: the approver of the pay package must differ from the person who entered it.';
    const { calls } = await boot(page, PROFILE, { access: 'not_started', blocking: [], activate: [{ status: 422, json: { error: 'employee_not_activatable', message, blocking: [] } }] });
    const activate = group(page).getByRole('button', { name: 'Activate' });
    await expect(activate).toBeEnabled({ timeout: 45_000 });
    const readinessBefore = calls.readiness;
    const accessBefore = calls.access;
    await activate.click();
    const alert = page.getByTestId('activation-blockers').getByRole('alert');
    await expect(alert).toHaveText(message);
    await expect.poll(() => calls.readiness).toBeGreaterThan(readinessBefore);
    await expect.poll(() => calls.access).toBeGreaterThan(accessBefore);
    await expect(activate).toBeEnabled();
    await activate.click();
    await expect(page.getByTestId('employee-activated')).toBeVisible();
    expect(calls.activate).toBe(2);
  });
});

test.describe('pay approvals creator notice', () => {
  test('an empty queue with 2 hidden packages says you created them and someone else must approve', async ({ page }) => {
    await boot(page, '/pay-approvals', { permissions: ['payroll.approve', 'security.manage', 'dashboard.read'], emptyQueueCount: 2 });
    const notice = page.getByTestId('pay-approvals-hidden-notice');
    await expect(notice).toContainText('You created 2 pay packages, so someone else must approve them. They do not appear in your queue.', { timeout: 45_000 });
    await expect(notice.getByRole('link', { name: 'Check reviewer setup' })).toHaveAttribute('href', '/setup?tab=users');
  });

  test('an empty queue with nothing hidden shows no creator notice', async ({ page }) => {
    await boot(page, '/pay-approvals', { permissions: ['payroll.approve', 'dashboard.read'], emptyQueueCount: 0 });
    await expect(page.getByTestId('pay-approvals-empty')).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('pay-approvals-hidden-notice')).toHaveCount(0);
  });
});
