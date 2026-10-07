import { expect, test, type Page } from '@playwright/test';

/**
 * Self-Service workspace, driven in a real browser against a mocked API (no stack needed):
 * the sidebar carries one Self-Service entry; pay, leave, requests and benefits are sections and
 * tabs inside the workspace; the overview leads with what needs the employee.
 *
 * Run: npx playwright test -c e2e/playwright.ess-workspace.config.ts
 * Screenshots: ESS_EVIDENCE_DIR=/some/dir npx playwright test -c e2e/playwright.ess-workspace.config.ts
 */

const dashboard = {
  profile: { employeeId: 17, employeeCode: 'EMP-SA-0017', fullName: 'Amira Mansour', jobTitle: 'Payroll Specialist', department: 'Finance', profilePhotoUrl: '', profileCompletenessScore: 85 },
  attendanceToday: { workDate: '2026-10-07', status: 'Present', totalWorkedMinutes: 262, missingPunch: true, lateMinutes: 0, overtimeMinutes: 0 },
  leaveBalances: [
    { leaveTypeId: 'lt-annual', leaveTypeName: 'Annual Leave', entitled: 21, used: 6, pending: 0, available: 15 },
    { leaveTypeId: 'lt-sick', leaveTypeName: 'Sick Leave', entitled: 30, used: 2, pending: 0, available: 28 },
    { leaveTypeId: 'lt-hajj', leaveTypeName: 'Hajj Leave', entitled: 0, used: 0, pending: 0, available: 0, statutoryEntitlementDays: 10 },
  ],
  pendingRequests: 1,
  documentAlerts: [{ id: 'doc-1', documentType: 'Iqama', fileName: 'iqama.pdf', expiryDate: '2026-11-02', approvalStatus: 'Approved' }],
  announcements: [{ id: 'a-1', title: 'National Day holiday', body: 'The office is closed on 23 September. Payroll runs as normal.', audience: 'All', publishedAtUtc: '2026-09-15T08:00:00Z' }],
  notifications: [],
  actionItems: [],
  payrollSnapshot: { netSalary: 14250, currency: 'SAR', period: 'Sep 2026', nextPayrollDate: '2026-10-31' },
  loansSummary: null,
  loanSummaries: [{ totalOutstanding: 8000, currency: 'SAR', activeLoanCount: 1, nextInstallmentAmount: 1000, nextInstallmentDate: '2026-10-27' }],
  performanceSnapshot: null,
  overtimeHoursThisMonth: 6,
  nextApprovedLeave: { leaveTypeName: 'Annual Leave', startDate: '2026-11-15', endDate: '2026-11-20', days: 5 },
  tenureMonths: 38,
};

const hrRequests = [
  { id: 'r-1', subject: 'Bank letter for a car loan', description: '', priority: 'Normal', status: 'Open', categoryName: 'Letters', dueAtUtc: '2026-10-09T00:00:00Z', createdAtUtc: '2026-10-02T09:00:00Z', hrResponded: true, isOverdue: false, responseStatus: 'Responded' },
  { id: 'r-2', subject: 'Update my home address', description: '', priority: 'Low', status: 'Open', categoryName: 'Personal details', dueAtUtc: '2026-10-10T00:00:00Z', createdAtUtc: '2026-10-05T09:00:00Z', hrResponded: false, isOverdue: false, responseStatus: 'Awaiting HR response' },
];

interface BootOptions {
  disabledKeys?: string[];
  locale?: 'en' | 'ar';
  permissions?: string[];
  country?: string;
  /** Module catalog entries; `enabled: false` switches a module (and its paths) off. */
  modules?: Array<{ key: string; labelEn: string; enabled: boolean; navPaths: string[] }>;
  failRequests?: boolean;
}

async function boot(page: Page, route: string, opts: BootOptions = {}) {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (m) => { if (m.type() === 'error' && !m.text().includes('status of 503')) errors.push(m.text()); });
  await page.addInitScript((locale) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    if (locale === 'ar') localStorage.setItem('kynexone-locale-choice-v2', 'ar');
  }, opts.locale ?? 'en');
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const json = (body: unknown) => route.fulfill({ json: body });
    if (path === '/api/auth/me') {
      return json({
        id: 'user-1', employeeId: 17, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Amira Mansour', roles: ['HR Manager'],
        // An HR manager is an employee first: dashboard and People in the menu, Self-Service for her own record.
        permissions: opts.permissions ?? ['ess.read', 'ess.write', 'dashboard.read', 'employees.read', 'loans.self'],
        companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: opts.country ?? 'SA', isActive: true }],
      });
    }
    if (path === '/api/features/disabled-keys') return json(opts.disabledKeys ?? ['release_a']);
    if (path === '/api/features/modules') return json(opts.modules ?? []);
    if (path === '/api/ess/dashboard') return json(dashboard);
    if (path === '/api/ess/hr-requests/my') return opts.failRequests ? route.fulfill({ status: 500, json: { message: 'boom' } }) : json(hrRequests);
    if (path === '/api/ess/hr-requests/r-1') {
      return json({ request: hrRequests[0], comments: [{ id: 'c-1', comment: 'Your bank letter is ready to collect.', authorType: 'HR', authorName: 'HR Team', createdAtUtc: '2026-10-06T10:00:00Z' }], hrResponded: true, isOverdue: false, responseStatus: 'Responded' });
    }
    if (path === '/api/hr-requests/categories') return json([{ id: 'cat-1', name: 'General HR', isActive: true }]);
    if (path === '/api/ess/my-roster') return json([
      { id: 's-1', date: '2026-10-08', shiftName: 'Morning 08:00–16:00', shiftColor: '#2F6BFF' },
      { id: 's-2', date: '2026-10-09', shiftName: 'Morning 08:00–16:00', shiftColor: '#2F6BFF' },
    ]);
    if (path === '/api/leave/types') return json([]);
    if (path === '/api/ess/document-requests/types') return json([{ letterType: 'salary_certificate', nameEn: 'Salary certificate', nameAr: 'تعريف بالراتب' }]);
    if (path === '/api/ess/document-requests') return json([]);
    if (path === '/api/ess/payslips') return json([]);
    if (path === '/api/tenant-admin/localization') return json({ currencyCode: 'SAR' });
    if (path === '/api/notifications') return json([]);
    return json({ items: [], total: 0 });
  });
  await page.goto(route);
  return errors;
}

const shot = async (page: Page, name: string, project: string) => {
  if (process.env.ESS_EVIDENCE_DIR) await page.screenshot({ path: `${process.env.ESS_EVIDENCE_DIR}/${name}-${project}.png`, fullPage: true });
};

test('the sidebar offers one Self-Service entry; its pages are tabs inside the workspace', async ({ page, isMobile }, info) => {
  const errors = await boot(page, '/ess');
  await expect(page.getByRole('heading', { level: 1, name: /Amira/ })).toBeVisible({ timeout: 60_000 });

  if (!isMobile) {
    const nav = page.locator('nav[aria-label="Primary navigation"]');
    await expect(nav.getByRole('button', { name: 'Self-Service', exact: true })).toBeVisible();
    for (const gone of ['My Payslips', 'My Leave', 'My Overtime', 'My HR Requests', 'My Benefits']) {
      await expect(nav.getByRole('button', { name: gone, exact: true }), gone).toHaveCount(0);
    }
  }

  const sections = page.getByRole('navigation', { name: 'Self-service sections' });
  await expect(sections.getByRole('link')).toHaveText(['Overview', 'Pay', 'Leave and time', 'Requests', 'Benefits']);
  await expect(sections.getByRole('link', { name: 'Overview' })).toHaveAttribute('aria-current', 'page');

  // Exceptions first: the missing punch, HR's reply and the expiring iqama, each with its next step.
  const attention = page.getByTestId('ess-attention');
  await expect(attention.getByRole('heading', { name: 'Needs your attention' })).toBeVisible();
  await expect(attention).toContainText('A punch is missing from today');
  await expect(attention).toContainText('HR replied to “Bank letter for a car loan”');
  await expect(attention).toContainText('Iqama is expiring');
  await expect(attention.getByRole('link', { name: 'Read the reply' })).toHaveAttribute('href', '/ess/requests?open=r-1');
  await expect(attention).toContainText('3 items');
  // A punch missing today: the day is not "Present" yet.
  await expect(page.getByTestId('ess-tile-attendance')).toContainText('Incomplete');
  await expect(page.getByTestId('ess-tile-attendance')).not.toContainText('Present');
  // The pay date is the server's month-end guess, and says so.
  await expect(page.getByTestId('ess-home-upcoming')).toContainText('Expected pay date (estimate)');
  await expect(page.getByTestId('ess-tile-payslip')).not.toContainText('Next payroll');
  await expect(page.getByText(/Profile \d+% complete/)).toHaveCount(0);

  // Every headline number links to the records behind it.
  await expect(page.getByTestId('ess-tile-leave')).toHaveAttribute('href', '/ess/leave');
  await expect(page.getByTestId('ess-tile-payslip')).toHaveAttribute('href', '/ess/payslips');
  await expect(page.getByTestId('ess-tile-loans')).toHaveAttribute('href', '/loans?mine=true');
  await expect(page.getByTestId('ess-tile-payslip')).toContainText('Net pay for September 2026');
  await expect(page.getByTestId('ess-tile-loans')).toContainText('Outstanding on 1 active loan');
  await expect(page.getByTestId('ess-tile-attendance')).toContainText('6 h of approved overtime this month');
  await expect(page.getByTestId('ess-tile-leave')).toContainText('15 days');

  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await shot(page, 'ess-overview', info.project.name);

  // Into a section: the tab row appears for a section with more than one page, and follows the URL.
  await sections.getByRole('link', { name: 'Requests' }).click();
  await expect(page).toHaveURL(/\/ess\/requests$/, { timeout: 60_000 }); // a dev server compiles each page on first visit
  // The workspace stays mounted across tabs, so the chosen tab keeps keyboard focus.
  await expect(sections.getByRole('link', { name: 'Requests' })).toBeFocused();
  await expect(sections.getByRole('link', { name: 'Requests' })).toHaveAttribute('aria-current', 'page');
  const tabs = page.getByRole('navigation', { name: 'Pages in Requests' });
  await expect(tabs.getByRole('link')).toHaveText(['HR requests', 'Letters', 'Exit and re-entry visa']);
  await expect(tabs.getByRole('link', { name: 'HR requests' })).toHaveAttribute('aria-current', 'page');
  await expect(page.getByRole('heading', { level: 1, name: 'My HR Requests' })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByText('Back to Self-Service')).toHaveCount(0);

  await tabs.getByRole('link', { name: 'Letters' }).click();
  await expect(page).toHaveURL(/\/ess\/documents$/, { timeout: 60_000 }); // a dev server compiles each page on first visit
  await expect(page.getByRole('heading', { level: 1, name: 'My letters' })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('ess-documents-request').getByRole('button', { name: 'Request letter' })).toBeVisible();
  // On another tab of its section, the section link marks the section, not the page.
  await expect(sections.getByRole('link', { name: 'Requests' })).toHaveAttribute('aria-current', 'true');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await shot(page, 'ess-documents', info.project.name);

  // A one-page section has no tab row; Pay has only Payslips while Release A is off.
  await sections.getByRole('link', { name: 'Pay' }).click();
  await expect(page).toHaveURL(/\/ess\/payslips$/, { timeout: 60_000 }); // a dev server compiles each page on first visit
  await expect(page.getByRole('navigation', { name: /^Pages in / })).toHaveCount(0);

  expect(errors, errors.join('\n')).toEqual([]);
});

test('Release A adds Package and Deductions as tabs under Pay', async ({ page }) => {
  await boot(page, '/ess/payslips', { disabledKeys: [] });
  const tabs = page.getByRole('navigation', { name: 'Pages in Pay' });
  await expect(tabs.getByRole('link')).toHaveText(['Payslips', 'Package', 'Deductions'], { timeout: 60_000 });
  await expect(tabs.getByRole('link', { name: 'Payslips' })).toHaveAttribute('aria-current', 'page');
});

test('the workspace reads right to left in Arabic without overflowing', async ({ page }, info) => {
  const errors = await boot(page, '/ess', { locale: 'ar' });
  const sections = page.getByRole('navigation', { name: 'أقسام الخدمة الذاتية' });
  await expect(sections.getByRole('link', { name: 'الراتب' })).toBeVisible({ timeout: 60_000 });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByTestId('ess-attention')).toContainText('يحتاج إلى انتباهك');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await shot(page, 'ess-overview-ar', info.project.name);
  expect(errors, errors.join('\n')).toEqual([]);
});

test('"Read the reply" opens that request with HR\'s reply', async ({ page }) => {
  await boot(page, '/ess');
  await page.getByTestId('ess-attention').getByRole('link', { name: 'Read the reply' }).click();
  await expect(page).toHaveURL(/\/ess\/requests\?open=r-1$/, { timeout: 60_000 }); // a dev server compiles each page on first visit
  await expect(page.getByTestId('my-requests-thread')).toContainText('Your bank letter is ready to collect.', { timeout: 30_000 });
});

test('a read-only employee is offered nothing to submit, and no loans link without a loans permission', async ({ page }) => {
  await boot(page, '/ess', { permissions: ['ess.read'] });
  await expect(page.getByRole('heading', { level: 1, name: /Amira/ })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByRole('link', { name: 'Apply Leave' })).toHaveCount(0);
  await expect(page.getByTestId('ess-attention').getByRole('link', { name: 'Ask for a correction' })).toHaveCount(0);
  await expect(page.getByTestId('ess-home-quick-actions')).not.toContainText('Request an HR letter');
  await expect(page.getByTestId('ess-home-quick-actions')).toContainText('My letters');
  await expect(page.getByTestId('ess-tile-loans')).not.toHaveAttribute('href', /.*/);
  await page.goto('/ess/documents');
  await expect(page.getByTestId('ess-documents-request')).toContainText('cannot send requests');
});

test('without ess.read there is no workspace menu, only the refusal', async ({ page }) => {
  await boot(page, '/ess', { permissions: ['dashboard.read'] });
  await expect(page.getByText(/access/i).first()).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('ess-workspace-nav')).toHaveCount(0);
});

test('outside Saudi Arabia there is no Exit and re-entry visa tab', async ({ page }) => {
  await boot(page, '/ess/requests', { country: 'KW' });
  const tabs = page.getByRole('navigation', { name: 'Pages in Requests' });
  await expect(tabs.getByRole('link')).toHaveText(['HR requests', 'Letters'], { timeout: 60_000 });
});

test('a module switched off takes its section out of the workspace', async ({ page }) => {
  await boot(page, '/ess', { modules: [{ key: 'benefits', labelEn: 'Benefits', enabled: false, navPaths: ['/ess/benefits'] }] });
  const sections = page.getByRole('navigation', { name: 'Self-service sections' });
  await expect(sections.getByRole('link')).toHaveText(['Overview', 'Pay', 'Leave and time', 'Requests'], { timeout: 60_000 });
});

test('a failed HR-requests read is never shown as "nothing needs your attention"', async ({ page }) => {
  await boot(page, '/ess', { failRequests: true });
  const attention = page.getByTestId('ess-attention');
  await expect(attention).toContainText('Your HR requests could not be loaded', { timeout: 60_000 });
  await expect(attention).not.toContainText('Nothing needs your attention');
  await expect(page.getByTestId('ess-home-hr-requests')).toContainText('could not be loaded');
});
