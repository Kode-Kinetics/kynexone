import { expect, test, type Page } from '@playwright/test';

/**
 * Home view switch, fixture lane (e2e/playwright.fixture.config.ts).
 *
 * A login that holds BOTH self-service and the workforce dashboard, and has an employee record,
 * gets a "My day" / "Company" switch on /dashboard. It opens on "My day" and remembers the last
 * choice. A login without an employee record (the usual setup admin) has no personal view and
 * must land straight on the company dashboard, never on a self-service error.
 */

const NOW = new Date('2026-11-03T09:00:00Z');
const months = ['Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov'];

const BASE_USER = {
  id: 'u-home', tenantId: 't1', tenantSlug: 'evostel', email: 'fahad@evostel.test', fullName: 'Fahad Alharbi',
  accountType: 'SingleCompany', isGroupScope: false, companies: [],
};
const DUAL = { ...BASE_USER, roles: ['Employee', 'HR Manager'], employeeId: 7, permissions: ['ess.read', 'dashboard.read', 'employees.read', 'approvals.read'] };
const ADMIN_NO_RECORD = { ...BASE_USER, roles: ['Admin'], permissions: ['ess.read', 'dashboard.read', 'employees.read'] };
const ESS_ONLY_PORTAL = { ...DUAL, accessMode: 'ESSOnly' };
const NO_DASHBOARD_PERMISSION = { ...DUAL, permissions: ['ess.read', 'employees.read'] };
const COMPANY_ONLY = { ...BASE_USER, roles: ['Payroll Manager'], employeeId: 7, permissions: ['dashboard.read', 'employees.read'] };

const ESS_DASHBOARD = {
  profile: { employeeId: 7, employeeCode: 'EMP-0007', fullName: 'Fahad Alharbi', jobTitle: 'HR Officer', department: 'Human Resources', profilePhotoUrl: '', profileCompletenessScore: 80 },
  attendanceToday: null, leaveBalances: [], pendingRequests: 0, documentAlerts: [], announcements: [], notifications: [], actionItems: [],
  payrollSnapshot: null, loansSummary: null, tenureMonths: 14, overtimeHoursThisMonth: 0,
};

const COMPANY_DASHBOARD = {
  summary: { totalEmployees: 3, activeEmployees: 3, presentToday: 0, onLeave: 0, absent: 0, overtimeHours: 0, churnRisk: 0, attendanceRecordsToday: 0 },
  trends: months.map((m) => ({ month: m, attendanceRate: 0, overtimeHours: 0 })),
  overview: { pendingApprovals: 0, approvalQueue: [], payrollSummary: null, payrollByEntity: [], workforceMix: [], headcountByDepartment: [], alerts: [], openLeaveRequests: 0, newJoinersThisMonth: 0, complianceAlertsTotal: 0, complianceCriticalTotal: 0 },
  payrollTrends: months.map((m) => ({ month: m, totalNet: 0, employeeCount: 3, status: 'Open' })),
  activityFeed: [],
  kpis: { pendingLeaveRequests: 0, pendingAttendanceCorrections: 0, attendanceExceptions: 0, expiringDocuments: 0, expiredDocuments: 0, missingDocuments: 0, qiwaEnabled: false },
};

async function open(page: Page, user: object, opts: { remembered?: 'me' | 'company' } = {}) {
  await page.clock.setFixedTime(NOW);
  await page.addInitScript(([remembered, uid]) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    if (remembered && !sessionStorage.getItem('home-seeded')) { localStorage.setItem(`kx.home-view.${uid}`, remembered); sessionStorage.setItem('home-seeded', '1'); }
  }, [opts.remembered ?? '', BASE_USER.id] as const);
  await page.route('**/api/**', (route) => {
    const p = new URL(route.request().url()).pathname;
    const json = (b: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(user);
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });
    if (p === '/api/dashboard/full') return json(COMPANY_DASHBOARD);
    if (p === '/api/ess/dashboard') return json(ESS_DASHBOARD);
    // The self-service list endpoints answer with bare arrays, not the paged shape below.
    if (p === '/api/ess/my-roster' || p === '/api/ess/hr-requests/my' || p.startsWith('/api/ess/document-requests') || p === '/api/leave/types') return json([]);
    if (p === '/api/ai/status') return json({ enabled: false });
    return json({ items: [], total: 0 });
  });
  await page.goto('/dashboard');
}

const tablist = (page: Page) => page.getByRole('tablist', { name: 'Home view' });
const companyHeading = (page: Page) => page.getByRole('heading', { name: 'HR Command Center' });
const myDayHeading = (page: Page) => page.getByRole('heading', { name: /Fahad/ });

test.describe('Home view switch', () => {
  test('a login with both views opens on My day', async ({ page }) => {
    await open(page, DUAL);
    await expect(tablist(page)).toBeVisible({ timeout: 30_000 });
    await expect(tablist(page).getByRole('tab', { name: 'My day' })).toHaveAttribute('aria-selected', 'true');
    await expect(myDayHeading(page)).toBeVisible();
    await expect(companyHeading(page)).toHaveCount(0);
  });

  test('switching to Company shows the HR Command Center and is remembered after a reload', async ({ page }) => {
    await open(page, DUAL);
    await tablist(page).getByRole('tab', { name: 'Company' }).click();
    await expect(companyHeading(page)).toBeVisible({ timeout: 15_000 });
    await expect(myDayHeading(page)).toHaveCount(0);
    await page.reload();
    await expect(companyHeading(page)).toBeVisible({ timeout: 30_000 });
    await expect(tablist(page).getByRole('tab', { name: 'Company' })).toHaveAttribute('aria-selected', 'true');
  });

  test('switching back to My day is remembered too', async ({ page }) => {
    await open(page, DUAL, { remembered: 'company' });
    await expect(companyHeading(page)).toBeVisible({ timeout: 30_000 });
    await tablist(page).getByRole('tab', { name: 'My day' }).click();
    await expect(myDayHeading(page)).toBeVisible();
    await page.reload();
    await expect(myDayHeading(page)).toBeVisible({ timeout: 30_000 });
  });

  test('the tabs work from the keyboard and the panel is labelled by the active tab', async ({ page }) => {
    await open(page, DUAL);
    const me = tablist(page).getByRole('tab', { name: 'My day' });
    await expect(me).toBeVisible({ timeout: 30_000 });
    await me.focus();
    await page.keyboard.press('ArrowRight');
    await expect(companyHeading(page)).toBeVisible({ timeout: 15_000 });
    await expect(tablist(page).getByRole('tab', { name: 'Company' })).toBeFocused();
    await expect(page.getByRole('tabpanel', { name: 'Company' })).toBeVisible();
  });

  test('a remembered Company choice never mounts My day first', async ({ page }) => {
    let essCalls = 0;
    page.on('request', (req) => { if (new URL(req.url()).pathname === '/api/ess/dashboard') essCalls += 1; });
    await open(page, DUAL, { remembered: 'company' });
    await expect(companyHeading(page)).toBeVisible({ timeout: 30_000 });
    expect(essCalls).toBe(0);
  });

  test('an admin with no employee record goes straight to Company, with no switch and no error', async ({ page }) => {
    await open(page, ADMIN_NO_RECORD);
    await expect(companyHeading(page)).toBeVisible({ timeout: 30_000 });
    await expect(tablist(page)).toHaveCount(0);
    await expect(page.getByText('Unable to load your workspace.')).toHaveCount(0);
  });

  test('a workforce-dashboard login without self-service sees no switch', async ({ page }) => {
    await open(page, COMPANY_ONLY);
    await expect(companyHeading(page)).toBeVisible({ timeout: 30_000 });
    await expect(tablist(page)).toHaveCount(0);
  });

  test('a self-service-only portal login gets no switch even if a role grants the dashboard', async ({ page }) => {
    await open(page, ESS_ONLY_PORTAL);
    await expect(myDayHeading(page)).toBeVisible({ timeout: 30_000 });
    await expect(companyHeading(page)).toHaveCount(0);
    await expect(tablist(page)).toHaveCount(0);
  });

  test('without the dashboard permission there is no switch (the API would refuse the Company view)', async ({ page }) => {
    await open(page, NO_DASHBOARD_PERMISSION);
    await page.waitForTimeout(2500);
    await expect(tablist(page)).toHaveCount(0);
  });

  test('the name card shows the most privileged role, not the first one listed', async ({ page }) => {
    await open(page, DUAL);
    await expect(tablist(page)).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText('HR Manager').first()).toBeVisible();
    await expect(page.getByText(/^Employee$/)).toHaveCount(0);
  });
});
