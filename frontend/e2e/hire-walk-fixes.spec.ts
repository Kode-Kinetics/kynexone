import { expect, test, type Page } from '@playwright/test';
import { employees } from './employee-workspace.fixture';

/**
 * Screen-level defects found walking a hire through to Self-Service, against a mocked API (nothing is read or written):
 *   1 the employee record refreshes itself after Activate (no stale Draft, no second Activate)
 *   2 the Recruitment overview lists In Progress openings, like its Open Positions tile
 *   3 the sign-in button does not promise an email the server may not send
 *   4 the New hires notice never says "is now employee" and "activate them once approved" together
 *   5 the Pay approvals notice says why the reader cannot approve, and who can
 * EN and AR, desktop and phone (the config's two projects).
 * Run: npx playwright test -c e2e/playwright.hire-walk-fixes.config.ts
 */
const SHOTS = process.env.HIRE_WALK_SHOTS_DIR;
type Lang = 'en' | 'ar';
const LANGS: Lang[] = ['en', 'ar'];

const errorsByPage = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  errorsByPage.set(page, errors);
  page.on('pageerror', e => errors.push(e.message));
});
test.afterEach(async ({ page }) => { expect(errorsByPage.get(page), 'No application runtime errors').toEqual([]); });

async function shot(page: Page, name: string, lang: Lang, info: { project: { name: string } }) {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}-${lang}-${info.project.name}.png`, fullPage: true });
}
// The first compile of a route reloads the document on a cold dev server: warm it, then run the test.
async function warm(page: Page, url: string) {
  await page.route('**/api/**', route => route.fulfill({ json: {} }));
  await page.goto(url).catch(() => undefined);
  await page.waitForTimeout(1500);
  await page.unroute('**/api/**');
}

const ADMIN_PERMS = ['employees.read', 'employees.write', 'employees.approve', 'employees.access.issue', 'employees.access.reset', 'dashboard.read'];

interface Boot {
  lang?: Lang;
  permissions?: string[];
  // profile
  emailDelivery?: boolean;
  activateDelayMs?: number;
  detailDelayAfterActivateMs?: number;
  codesPrintReason?: 'setter' | null;
  // recruitment
  openings?: Array<Record<string, unknown>>;
  openPositions?: number;
  // new hires
  approvedStatus?: string;
  approvedSalaryApprovalState?: 'Pending' | null;
  // pay approvals
  payApprovals?: { items: unknown[]; requiresOtherReviewerCount: number };
}

const OPENING = {
  id: 'op-1', jobCode: 'JOB-1', requisitionId: null, title: 'Sales Executive', departmentName: 'Sales', designationTitle: 'Sales Executive',
  employmentType: 'Full-Time', headCount: 1, filledCount: 0, description: '', requirements: '', responsibilities: '', salaryFrom: null, salaryTo: null,
  location: 'Riyadh', status: 'InProgress', assignedHrName: '', createdAtUtc: '2026-11-01T08:00:00Z', activeApplications: 1, remaining: 1,
};
const DRAFT = {
  id: 'd-1', currentStep: 'HR approval', name: 'Rana Saeed', arabicName: 'رنا سعيد', department: 'IT', designation: 'Officer', branch: 'Riyadh HQ',
  joiningDate: '2026-10-20', source: 'Manual', applicationId: null, jobTitle: null, createdByUserId: 'hr-2', createdByName: 'Sara Ali', isMine: false,
  canApprove: true, approveBlockedReason: null, profileCompletenessScore: 90, createdAtUtc: '2026-10-07T08:00:00Z', submittedAtUtc: '2026-10-07T08:05:00Z',
  decidedAtUtc: null, decidedByName: null, decisionReason: null, activatedEmployeeCode: null,
};

async function boot(page: Page, url: string, options: Boot = {}) {
  const calls = { detail: 0, activate: 0, codes: 0, approve: 0 };
  let activated = false;
  let approved = false;
  await page.addInitScript(lang => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', lang);
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
  }, options.lang ?? 'en');
  const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const params = new URL(request.url()).searchParams;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (path === '/api/auth/me') return reply({
      id: 'hr', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Huda HR', roles: ['HR Manager'],
      permissions: options.permissions ?? ADMIN_PERMS, accountType: 'Company', isGroupScope: false,
      companies: [{ id: 'fixture-company', name: 'Acme Arabia', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
    });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'en', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/companies') return reply({ items: [{ id: 'fixture-company', legalNameEn: 'Acme Arabia', countryCode: 'SAU', emailDomain: 'acme.test', isActive: true, approvalStatus: 'Active' }], total: 1, page: 1, pageSize: 100 });

    // 1 + 3: the employee record
    if (path === '/api/employee-completion/901') return reply({ requested: false, status: 'NotRequested', selfServiceAvailable: true });
    if (path === '/api/employee-access/codes') {
      calls.codes += 1;
      return reply({
        issued: [{ employeeId: 901, employeeName: employees[901].fullName, employeeCode: 'EMP-1', username: employees[901].workEmail, code: '12345678', delivery: 'print', printReason: options.codesPrintReason ?? 'setter', expiresAtUtc: '2026-12-01T10:00:00Z', tenantSlug: 'fixture' }],
        skipped: [], emailed: false, deliveryMessage: null,
      });
    }
    if (path === '/api/employee-access/901') return reply({
      employeeId: 901, employeeName: employees[901].fullName, employeeCode: 'EMP-1', workEmail: employees[901].workEmail, state: 'not_started',
      loginEnabled: false, passwordConfigured: false, canIssue: true, emailDelivery: !!options.emailDelivery,
      codeExpiresAtUtc: null, codeIssuedByName: null, lastCodeExpiredAtUtc: null, lastSignInAtUtc: null, stoppedReason: null, blockedCode: null, blockedReason: null,
    });
    if (path === '/api/employees/901/readiness') return reply({
      employeeId: 901, state: 'Ready', score: 100, progress: { present: 5, requiredTotal: 5 }, policy: { countryCode: 'SAU', tier: 'Activation', sources: [] },
      blocking: [], payBlocking: [], recommended: [], present: [], expiringSoon: [], disclaimer: '',
    });
    if (path === '/api/employees/901/activate') {
      calls.activate += 1;
      if (options.activateDelayMs) await sleep(options.activateDelayMs);
      activated = true;
      return reply({ ...employees[901], status: 'Active' });
    }
    if (path === '/api/employees/901') {
      calls.detail += 1;
      if (activated && options.detailDelayAfterActivateMs) await sleep(options.detailDelayAfterActivateMs);
      return reply({ ...employees[901], status: activated ? 'Active' : 'Draft' });
    }

    // 2: Recruitment overview
    if (path === '/api/recruitment/openings/stats') return reply({ openPositions: options.openPositions ?? 1, activeApplications: 1, offersPending: 0, hiredThisMonth: 0 });
    if (path === '/api/recruitment/requisitions/stats') return reply({ draft: 0, pending: 0, approved: 0, converted: 1 });
    if (path === '/api/recruitment/openings') {
      const status = params.get('status');
      const all = options.openings ?? [OPENING];
      const items = status ? all.filter(o => o.status === status) : all;
      return reply({ items, total: items.length, page: 1, pageSize: Number(params.get('pageSize') ?? 25) });
    }

    // 4: New hires
    if (path === '/api/employees/drafts/d-1/approve') {
      calls.approve += 1;
      approved = true;
      return reply({ ...employees[901], id: 901, employeeCode: 'EMP-0049', status: options.approvedStatus ?? 'Draft', salaryApprovalState: options.approvedSalaryApprovalState ?? undefined });
    }
    if (path === '/api/employees/drafts') return reply({
      items: [{ ...DRAFT, status: approved ? 'Activated' : 'PendingHrApproval', activatedEmployeeId: approved ? 901 : null, activatedEmployeeCode: approved ? 'EMP-0049' : null }],
      total: 1, page: 1, pageSize: 25, counts: { awaitingApproval: approved ? 0 : 1, draft: 0, activated: approved ? 1 : 0, rejected: 0, cancelled: 0 },
    });
    if (path === '/api/employees/drafts/d-1') return reply({
      summary: { ...DRAFT, status: 'PendingHrApproval' },
      draft: { id: 'd-1', status: 'PendingHrApproval', englishName: 'Rana Saeed', arabicName: 'رنا سعيد', personalEmail: '', workEmail: 'rana.saeed@acme.test', phone: '', nationality: 'Saudi', countryCode: 'SA', department: 'IT', designation: 'Officer', branch: 'Riyadh HQ', workLocation: '', joiningDate: '2026-10-20', contractType: 'Unlimited', probationEndDate: null, salary: null },
      documentCount: 0, activationCheck: { canActivate: true, resolvedCompanyName: 'Acme Arabia', problems: [], advisories: [] },
    });

    // 5: Pay approvals
    if (path === '/api/pay-approvals' && request.method() === 'GET') {
      const queue = options.payApprovals ?? { items: [], requiresOtherReviewerCount: 1 };
      return reply({ items: queue.items, count: queue.items.length, hasMore: false, requiresOtherReviewerCount: queue.requiresOtherReviewerCount });
    }
    return reply({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(url);
  return { calls };
}

const PROFILE = '/people/901?section=access-onboarding';
const group = (page: Page) => page.getByRole('group', { name: /Employment and sign-in status|حالة التوظيف وتسجيل الدخول/ });

test.describe('1 the employee record after Activate', () => {
  test.beforeEach(async ({ page }) => { await warm(page, PROFILE); });

  test('shows Active and no Activate button without a reload', async ({ page }, info) => {
    const { calls } = await boot(page, PROFILE);
    await expect(group(page).getByRole('button', { name: 'Activate', exact: true })).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('employee-status-badge')).toHaveText('Draft');
    await group(page).getByRole('button', { name: 'Activate', exact: true }).click();
    await page.getByRole('dialog', { name: /Activate/ }).getByRole('button', { name: 'Activate', exact: true }).click();
    await expect(page.getByTestId('employee-activated')).toBeVisible();
    // Behind the "now active" dialog the record already says Active: nothing waits for a reload or for the dialog to close.
    await expect(page.getByTestId('employee-status-badge')).toHaveText('Active');
    await expect(group(page).getByTestId('state-employee')).toContainText('Active');
    await expect(group(page).getByTestId('state-employee')).not.toContainText('Not active yet');
    await expect(group(page).getByRole('button', { name: 'Activate', exact: true })).toHaveCount(0);
    await shot(page, 'activate-done', 'en', info);
    await page.getByRole('button', { name: 'Later' }).click();
    await expect(page.getByTestId('employee-status-badge')).toHaveText('Active');
    await expect(group(page).getByRole('button', { name: 'Activate', exact: true })).toHaveCount(0);
    expect(calls.activate).toBe(1);
    expect(calls.detail).toBeGreaterThanOrEqual(2);
  });

  test('the Activate button cannot be pressed again while the request and the refresh are in flight', async ({ page }) => {
    const { calls } = await boot(page, PROFILE, { activateDelayMs: 400, detailDelayAfterActivateMs: 1200 });
    await group(page).getByRole('button', { name: 'Activate', exact: true }).click({ timeout: 45_000 });
    await page.getByRole('dialog', { name: /Activate/ }).getByRole('button', { name: 'Activate', exact: true }).click();
    // Activate has been answered; the refreshed record is still on its way. The row's button must still be disabled, never a live Activate.
    await expect.poll(() => calls.detail, { timeout: 10_000 }).toBeGreaterThanOrEqual(2);
    await expect(group(page).getByRole('button', { name: /Activat/ })).toBeDisabled();
    await expect(page.getByTestId('employee-activated')).toBeVisible({ timeout: 15_000 });
    await expect(group(page).getByRole('button', { name: 'Activate', exact: true })).toHaveCount(0);
    expect(calls.activate).toBe(1);
  });
});

test.describe('2 the Recruitment overview lists In Progress openings', () => {
  for (const lang of LANGS) {
    test(`an In Progress opening is listed beside the Open Positions tile (${lang})`, async ({ page }, info) => {
      await warm(page, '/recruitment');
      await boot(page, '/recruitment', { lang, permissions: ['recruitment.read', 'recruitment.write', 'employees.read', 'dashboard.read'] });
      const card = page.locator('h3', { hasText: lang === 'en' ? 'Active Job Openings' : 'الوظائف الشاغرة النشطة' }).locator('xpath=ancestor::div[contains(@class,"surface")][1]');
      await expect(card.getByText('Sales Executive')).toBeVisible({ timeout: 45_000 });
      await expect(card.getByText(lang === 'en' ? 'No open positions yet.' : /لا توجد/)).toHaveCount(0);
      await shot(page, 'recruitment-overview', lang, info);
    });
  }

  test('both an Open and an In Progress opening are listed, newest first, and a Closed one is not', async ({ page }) => {
    await warm(page, '/recruitment');
    await boot(page, '/recruitment', {
      permissions: ['recruitment.read', 'recruitment.write', 'employees.read', 'dashboard.read'],
      openings: [
        { ...OPENING, id: 'op-a', title: 'Older Open Role', status: 'Open', createdAtUtc: '2026-10-01T08:00:00Z' },
        { ...OPENING, id: 'op-b', title: 'Newer Busy Role', status: 'InProgress', createdAtUtc: '2026-11-05T08:00:00Z' },
        { ...OPENING, id: 'op-c', title: 'Closed Role', status: 'Closed', createdAtUtc: '2026-11-10T08:00:00Z' },
      ],
    });
    const card = page.locator('h3', { hasText: 'Active Job Openings' }).locator('xpath=ancestor::div[contains(@class,"surface")][1]');
    await expect(card.getByText('Older Open Role')).toBeVisible({ timeout: 45_000 });
    await expect(card.getByText('Newer Busy Role')).toBeVisible();
    await expect(card.getByText('Closed Role')).toHaveCount(0);
    const text = await card.innerText();
    expect(text.indexOf('Newer Busy Role')).toBeLessThan(text.indexOf('Older Open Role'));
  });
});

test.describe('3 the sign-in button does not promise an email', () => {
  test.beforeEach(async ({ page }) => { await warm(page, PROFILE); });
  for (const lang of LANGS) {
    test(`when the company can email, the button says what it does and a slip opens when the server prints (${lang})`, async ({ page }, info) => {
      const { calls } = await boot(page, PROFILE, { lang, emailDelivery: true });
      const label = lang === 'en' ? 'Issue sign-in code' : 'إصدار رمز تسجيل الدخول';
      const row = group(page).getByTestId('state-login');
      await expect(row.getByRole('button', { name: label })).toBeVisible({ timeout: 45_000 });
      await expect(group(page).getByRole('button', { name: /Email sign-in code|بالبريد الإلكتروني/ })).toHaveCount(0);
      // Plain words under the button say when it emails and when it prints; no title or aria text promises an email.
      await expect(row.getByTestId('issue-code-help')).toContainText(lang === 'en' ? 'a slip is printed' : 'ورقة');
      expect(await row.getByRole('button', { name: label }).getAttribute('title')).toBeNull();
      await shot(page, 'sign-in-code-row', lang, info);
      await row.getByRole('button', { name: label }).click();
      await expect(page.getByRole('dialog', { name: lang === 'en' ? 'Sign-in slips' : 'أوراق تسجيل الدخول' })).toBeVisible();
      expect(calls.codes).toBe(1);
    });
  }
});

test.describe('4 the New hires notice after Approve', () => {
  for (const lang of LANGS) {
    test(`a hire whose record is not active yet is not called an employee and told to activate in one breath (${lang})`, async ({ page }, info) => {
      await warm(page, '/people/new-hires');
      await boot(page, '/people/new-hires', { lang, approvedStatus: 'Draft' });
      await page.getByRole('button', { name: lang === 'en' ? 'Review and decide' : 'مراجعة واتخاذ القرار', exact: true }).click({ timeout: 45_000 });
      await page.getByRole('button', { name: lang === 'en' ? 'Approve and activate' : 'اعتماد وتفعيل', exact: true }).click();
      const notice = page.getByRole('status').filter({ hasText: 'EMP-0049' });
      await expect(notice).toBeVisible();
      const text = await notice.innerText();
      if (lang === 'en') {
        expect(text).not.toContain('is now employee');
        expect(text).toContain('not active yet');
        expect(text).toContain('waiting for a second approver');
      } else {
        expect(text).not.toContain('أصبح');
      }
      await shot(page, 'new-hires-notice', lang, info);
    });
  }
});

test.describe('5 the Pay approvals notice', () => {
  for (const lang of LANGS) {
    test(`it states the rule and says who can approve instead of "ask your administrator" (${lang})`, async ({ page }, info) => {
      await warm(page, '/pay-approvals');
      await boot(page, '/pay-approvals', { lang, permissions: ['payroll.approve', 'payroll.read', 'dashboard.read'], payApprovals: { items: [], requiresOtherReviewerCount: 1 } });
      const notice = page.getByTestId('pay-approvals-hidden-notice');
      await expect(notice).toBeVisible({ timeout: 45_000 });
      const text = await notice.innerText();
      if (lang === 'en') {
        expect(text).toContain('someone other than the person who entered it, the employee, or the person who issued the employee');
        expect(text).toContain('payroll approve permission');
        expect(text).not.toContain('Ask your administrator');
        expect(text).not.toContain('You created');
      } else {
        expect(text).not.toContain('Ask your administrator');
        expect(text).not.toContain('أنشأت');
      }
      await shot(page, 'pay-approvals-notice', lang, info);
    });
  }
});
