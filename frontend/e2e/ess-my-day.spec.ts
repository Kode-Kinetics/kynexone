import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * Employee home ("My day"), in a real browser against a mocked API (fixture lane, no stack).
 *
 * What it proves: net pay is masked until shown (and masked again on every visit); a refresh keeps
 * the screen (no skeleton) and the dashboard is read once per visit; the hero's hours are live only
 * when the API's punch times make that honest; there is no clock-in anywhere; the request tracker
 * names its steps; Arabic is right to left with Latin digits and isolated names; phone keeps one
 * primary action in a bottom bar. What it cannot prove: that the API returns these payloads.
 *
 *   FIXTURE_PORT=5175 NEXT_DIST_DIR=.next-claude-c DASHBOARD_EVIDENCE_DIR=$TMPDIR/myday-evidence \
 *     npx playwright test -c e2e/playwright.fixture.config.ts e2e/ess-my-day.spec.ts --project=desktop
 */

const EVIDENCE = process.env.DASHBOARD_EVIDENCE_DIR;
const DAY = 86_400_000;

const pad = (n: number) => String(n).padStart(2, '0');
const key = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const inDays = (n: number) => key(new Date(Date.now() + n * DAY));
const ago = (ms: number) => new Date(Date.now() - ms).toISOString();

interface Opts {
  locale?: 'en' | 'ar';
  theme?: 'light' | 'dark';
  permissions?: string[];
  /** Replaces fields of the dashboard payload. */
  dashboard?: Record<string, unknown>;
  hr?: unknown[];
  leave?: unknown[];
  advances?: unknown[];
  failAdvances?: boolean;
  failRoster?: boolean;
  /** Milliseconds the 2nd and later dashboard reads take; with `failAfterFirst` they fail instead. */
  laterDashboardDelay?: number;
  firstDashboardDelay?: number;
  failAfterFirst?: boolean;
  /** Every dashboard read fails with a message; boot returns as soon as the page has loaded. */
  failDashboard?: boolean;
  hash?: string;
}

const hrRequests = () => [
  { id: 'r-1', subject: 'Salary certificate for bank', description: '', priority: 'Normal', status: 'Open', categoryName: 'Letters', dueAtUtc: inDays(2), createdAtUtc: ago(3 * DAY), hrResponded: true, isOverdue: false, responseStatus: 'Responded' },
  { id: 'r-2', subject: 'Update my home address', description: '', priority: 'Low', status: 'Open', categoryName: 'Personal details', dueAtUtc: inDays(4), createdAtUtc: ago(1 * DAY), hrResponded: false, isOverdue: false, responseStatus: 'Awaiting HR response' },
];

const leaveRequests = () => [
  {
    id: 'l-1', employeeId: 7, leaveTypeId: 'lt-annual', leaveTypeName: 'Annual Leave', startDate: inDays(14), endDate: inDays(15), totalDays: 2,
    status: 'PendingHRApproval', createdAtUtc: ago(4 * DAY), submittedAtUtc: ago(4 * DAY), decidedAtUtc: null, rejectionReason: '',
    approvals: [{ id: 'a1', leaveRequestId: 'l-1', stepNumber: 1, approverRole: 'Manager', approverName: 'Faisal Al-Otaibi', decision: 'Approved', actedAtUtc: ago(2 * DAY) }],
  },
];

const advances = () => [
  { id: 'ad-1', employeeId: '7', advanceNumber: 'ADV-0012', requestedAmount: 2000, approvedAmount: 0, status: 'Pending', createdAtUtc: ago(2 * DAY), isLockedByPayroll: false },
];

function dashboard(over: Record<string, unknown> = {}) {
  return {
    profile: { employeeId: 7, employeeCode: 'EMP-0007', fullName: 'Noura Alqahtani', jobTitle: 'HR Business Partner', department: 'Human Resources', profilePhotoUrl: '', profileCompletenessScore: 85 },
    // One open punch and nothing closed: the hours so far are exactly "now minus the first punch".
    attendanceToday: { workDate: inDays(0), status: 'Present', firstInUtc: ago((4 * 60 + 12) * 60_000 + 7000), totalWorkedMinutes: 0, missingPunch: false, lateMinutes: 0, overtimeMinutes: 0 },
    attendanceTodaySource: 'processed',
    leaveBalances: [
      { leaveTypeId: 'lt-annual', leaveTypeName: 'Annual Leave', entitled: 21, used: 6, pending: 2, available: 13 },
      { leaveTypeId: 'lt-sick', leaveTypeName: 'Sick Leave', entitled: 30, used: 2, pending: 0, available: 28 },
      { leaveTypeId: 'lt-hajj', leaveTypeName: 'Hajj Leave', entitled: 0, used: 0, pending: 0, available: 0, statutoryEntitlementDays: 10 },
    ],
    pendingRequests: 2,
    documentAlerts: [{ id: 'doc-1', documentType: 'Passport', fileName: 'p.pdf', expiryDate: inDays(35), approvalStatus: 'Approved' }],
    announcements: [{ id: 'a-1', title: 'Founding Day: offices closed', body: 'Offices are closed on Monday 22 February.', audience: 'All', publishedAtUtc: ago(3 * DAY) }],
    notifications: [],
    actionItems: [{ id: 'task-1', title: 'Complete your emergency contact', category: 'EmployeeProfileCompletion', dueAtUtc: `${inDays(5)}T00:00:00Z` }],
    payrollSnapshot: { netSalary: 18450, currency: 'SAR', period: 'Oct 2026', nextPayrollDate: inDays(11) },
    loansSummary: null,
    loanSummaries: [{ totalOutstanding: 12000, currency: 'SAR', activeLoanCount: 1, nextInstallmentAmount: 1000, nextInstallmentDate: inDays(16) }],
    performanceSnapshot: { cycleName: 'H2 2026', goalsCompleted: 3, goalsTotal: 5, lastRating: 4.2 },
    overtimeHoursThisMonth: 6,
    nextApprovedLeave: { leaveTypeName: 'Annual Leave', startDate: inDays(30), endDate: inDays(34), days: 5 },
    tenureMonths: 29,
    ...over,
  };
}

async function boot(page: Page, opts: Opts = {}) {
  const errors: string[] = [];
  const calls = { dashboard: 0, ai: 0 };
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !/status of (500|503)/.test(m.text())) errors.push(m.text()); });
  await page.addInitScript(([locale, theme]) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone.theme', theme);
    if (locale === 'ar') localStorage.setItem('kynexone-locale-choice-v2', 'ar');
  }, [opts.locale ?? 'en', opts.theme ?? 'light']);
  await page.route('**/api/**', async (route) => {
    const req = route.request();
    const path = new URL(req.url()).pathname;
    const json = (body: unknown) => route.fulfill({ json: body });
    if (path === '/api/auth/me') {
      return json({
        id: 'user-7', employeeId: 7, tenantId: 'tenant-1', tenantSlug: 'fixture', fullName: 'Noura Alqahtani', roles: ['Employee'],
        permissions: opts.permissions ?? ['ess.read', 'ess.write', 'loans.self'], accessMode: 'FullPortal',
        companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }],
      });
    }
    if (path === '/api/features/disabled-keys') return json([]);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/ess/dashboard') {
      calls.dashboard += 1;
      if (opts.failDashboard) return route.fulfill({ status: 500, json: { message: 'The server is down.' } });
      if (calls.dashboard === 1 && opts.firstDashboardDelay) await new Promise((r) => setTimeout(r, opts.firstDashboardDelay));
      if (calls.dashboard > 1) {
        if (opts.laterDashboardDelay) await new Promise((r) => setTimeout(r, opts.laterDashboardDelay));
        if (opts.failAfterFirst) return route.fulfill({ status: 500, json: { message: 'boom' } });
      }
      return json(dashboard(opts.dashboard));
    }
    if (path === '/api/ess/hr-requests/my') return json(opts.hr ?? hrRequests());
    if (path === '/api/ess/my-roster') return opts.failRoster ? route.fulfill({ status: 500, json: { message: 'boom' } }) : json([
      { id: 's-1', date: inDays(0), shiftName: 'Day shift', shiftColor: '#2F6BFF' },
      { id: 's-2', date: inDays(1), shiftName: 'Day shift', shiftColor: '#2F6BFF' },
    ]);
    if (path === '/api/leave/types') return json([{ id: 'lt-annual', nameEn: 'Annual Leave', nameAr: 'إجازة سنوية', isActive: true }]);
    if (path === '/api/leave/requests') { const items = opts.leave ?? leaveRequests(); return json({ items, total: items.length, page: 1 }); }
    if (path === '/api/finance/advances') {
      if (opts.failAdvances) return route.fulfill({ status: 500, json: { message: 'boom' } });
      const items = opts.advances ?? advances();
      return json({ items, total: items.length });
    }
    if (path === '/api/ess/ai/ask') { calls.ai += 1; return json({ answer: 'You have 13 annual leave days available.' }); }
    if (path === '/api/tenant-admin/localization') return json({ currencyCode: 'SAR' });
    if (path === '/api/notifications') return json([]);
    return json({ items: [], total: 0 });
  });
  await page.goto(`/ess${opts.hash ?? ''}`);
  if (opts.failDashboard) return { errors, calls };
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 90_000 });
  await expect(page.getByTestId(opts.hash ? 'ess-myday' : 'ess-hero')).toBeVisible({ timeout: 30_000 });
  return { errors, calls };
}

const shot = async (page: Page, name: string, project: string) => {
  if (EVIDENCE) { await page.waitForTimeout(400); await page.screenshot({ path: `${EVIDENCE}/myday-${name}-${project}.png`, fullPage: true }); }
};
const isPhone = (page: Page) => (page.viewportSize()?.width ?? 1440) < 640;
const noSidewaysScroll = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

test.describe('My day: net pay', () => {
  test('is masked by default, shown on request, hidden again on the next visit', async ({ page }) => {
    await boot(page);
    const net = page.getByTestId('ess-net-pay');
    await expect(net).toContainText('••••••');
    // The amount is not in the page at all while masked: nothing to read, copy or find in the DOM.
    await expect(page.locator('body')).not.toContainText('18,450');
    expect(await page.content()).not.toContain('18,450');
    const toggle = page.getByRole('button', { name: 'Show net pay' });
    await expect(toggle).toBeVisible();
    await toggle.click();
    await expect(net).toContainText('SAR 18,450.00');
    await expect(page.getByRole('button', { name: 'Hide net pay' })).toBeVisible();
    await expect(page.getByRole('status').filter({ hasText: 'Net pay shown' })).toHaveCount(1);
    await page.getByRole('button', { name: 'Hide net pay' }).click();
    await expect(net).not.toContainText('18,450');
    // Not remembered: showing it, then visiting again, is masked again.
    await page.getByRole('button', { name: 'Show net pay' }).click();
    await expect(net).toContainText('18,450.00');
    await page.reload();
    await expect(page.getByTestId('ess-net-pay')).toContainText('••••••', { timeout: 60_000 });
    const stored = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }));
    expect(stored).not.toContain('18450');
  });

  test('the toggle works from the keyboard', async ({ page }) => {
    await boot(page);
    const toggle = page.getByTestId('ess-net-toggle');
    await toggle.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('ess-net-pay')).toContainText('18,450.00');
    await page.keyboard.press('Space');
    await expect(page.getByTestId('ess-net-pay')).toContainText('••••••');
  });
});

test.describe('My day: loading and refresh', () => {
  test('reads the dashboard once, and a refresh keeps the screen instead of flashing the skeleton', async ({ page }) => {
    const { calls } = await boot(page, { laterDashboardDelay: 1500 });
    await page.waitForTimeout(500);
    expect(calls.dashboard).toBe(1);
    // Switching tabs and expanding rows are not reads.
    await page.getByRole('tab', { name: 'My records' }).click();
    await page.getByRole('tab', { name: 'Today' }).click();
    expect(calls.dashboard).toBe(1);

    const refresh = page.getByRole('button', { name: /^Refresh my day/ });
    await refresh.click();
    await expect(refresh).toHaveAttribute('aria-busy', 'true');
    // While the new data is on its way: the hero is still there and no skeleton was ever drawn.
    await expect(page.getByTestId('ess-hero')).toBeVisible();
    await expect(page.getByRole('status', { name: 'Loading your workspace' })).toHaveCount(0);
    await expect(refresh).toHaveAttribute('aria-busy', 'false', { timeout: 15_000 });
    expect(calls.dashboard).toBe(2);
    await expect(page.getByRole('status').filter({ hasText: 'Updated just now.' })).toHaveCount(1);
  });

  test('a skeleton shows only on the first load', async ({ page }) => {
    // The first read is slow: the skeleton is the honest wait, then the page replaces it.
    const pending = boot(page, { firstDashboardDelay: 1500 });
    await expect(page.getByRole('status', { name: 'Loading your workspace' })).toBeVisible({ timeout: 90_000 });
    await pending;
    await expect(page.getByRole('status', { name: 'Loading your workspace' })).toHaveCount(0);
  });

  test('a failed refresh keeps what was on screen and says so', async ({ page }) => {
    await boot(page, { failAfterFirst: true });
    await page.getByRole('button', { name: /^Refresh my day/ }).click();
    await expect(page.getByText(/Refresh failed\. Showing figures from/)).toBeVisible();
    await expect(page.getByTestId('ess-hero')).toBeVisible();
    await expect(page.getByRole('status', { name: 'Loading your workspace' })).toHaveCount(0);
    await expect(page.getByTestId('ess-net-pay')).toContainText('••••••');
  });

  test('a first load that fails is an error with Retry, not an empty page', async ({ page }) => {
    await boot(page, { failDashboard: true });
    await expect(page.getByRole('alert').filter({ hasText: 'The server is down.' })).toBeVisible({ timeout: 90_000 });
    await expect(page.getByRole('button', { name: 'Retry' })).toBeVisible();
  });
});

test.describe('My day: today', () => {
  test('hours count up live from the first punch, and stop moving for reduced motion', async ({ page }) => {
    await boot(page);
    const hours = page.getByTestId('ess-live-hours');
    await expect(hours).toHaveText(/^4:1[2-3]:\d\d$/);
    await expect(hours).toHaveAttribute('role', 'timer');
    const a = await hours.textContent();
    await page.waitForTimeout(1500);
    expect(await hours.textContent()).not.toBe(a);
    await expect(page.getByTestId('ess-today')).toContainText('Hours worked today, counting since your');
  });

  test('with reduced motion the figure changes by the minute, not the second', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await boot(page);
    const hours = page.getByTestId('ess-live-hours');
    await expect(hours).toHaveText(/^\d:\d\d:\d\d$/);
    const a = await hours.textContent();
    await page.waitForTimeout(2200);
    expect(await hours.textContent()).toBe(a);
  });

  test('a day with closed periods shows the closed total, never a guessed live figure', async ({ page }) => {
    await boot(page, { dashboard: { attendanceToday: { workDate: inDays(0), status: 'Present', firstInUtc: ago(6 * 3600_000), totalWorkedMinutes: 180, missingPunch: false, lateMinutes: 0, overtimeMinutes: 0 } } });
    await expect(page.getByTestId('ess-live-hours')).toHaveCount(0);
    await expect(page.getByTestId('ess-today')).toContainText('3 h 0 min');
    await expect(page.getByTestId('ess-today')).toContainText('The current period is added when you clock out');
  });

  test('the arrival fact comes only from a processed day: late is named, a raw punch is provisional', async ({ page }) => {
    const punch = { workDate: inDays(0), status: 'Present', firstInUtc: ago(3 * 3600_000), totalWorkedMinutes: 0, missingPunch: false, overtimeMinutes: 0 };
    await boot(page, { dashboard: { attendanceToday: { ...punch, lateMinutes: 12 }, attendanceTodaySource: 'processed' } });
    await expect(page.getByTestId('ess-hero')).toContainText('Arrival');
    await expect(page.getByTestId('ess-hero')).toContainText('Late by 12 min');
    await expect(page.getByTestId('ess-hero')).not.toContainText('Provisional');
  });

  test('a raw (unprocessed) punch hides the arrival fact and says the day is provisional', async ({ page }) => {
    const punch = { workDate: inDays(0), status: 'Present', firstInUtc: ago(3 * 3600_000), totalWorkedMinutes: 0, missingPunch: false, lateMinutes: 0, overtimeMinutes: 0 };
    await boot(page, { dashboard: { attendanceToday: punch, attendanceTodaySource: 'raw' } });
    await expect(page.getByTestId('ess-hero')).not.toContainText('Arrival');
    await expect(page.getByTestId('ess-hero')).not.toContainText('On time');
    await expect(page.getByTestId('ess-hero')).toContainText('Provisional: lateness and status are confirmed when HR processes the day.');
  });

  test('no punch today is said plainly', async ({ page }) => {
    await boot(page, { dashboard: { attendanceToday: null, attendanceTodaySource: null } });
    await expect(page.getByTestId('ess-today')).toContainText('No attendance record for today');
    await expect(page.getByTestId('ess-today')).toContainText('Not in yet');
    await expect(page.getByTestId('ess-live-hours')).toHaveCount(0);
  });

  test('a missing punch leads, with the correction as the one primary action', async ({ page }) => {
    await boot(page, { hr: [], dashboard: { attendanceToday: { workDate: inDays(0), status: 'Present', firstInUtc: ago(9 * 3600_000), totalWorkedMinutes: 0, missingPunch: true, lateMinutes: 0, overtimeMinutes: 0 } } });
    await expect(page.getByTestId('ess-today')).toContainText('A punch is missing');
    const next = page.getByTestId('ess-attention');
    await expect(next).toContainText('Your next step');
    await expect(next).toContainText('A punch is missing from today');
    if (!isPhone(page)) await expect(next.getByRole('link', { name: 'Ask for a correction' })).toHaveAttribute('href', /^\/ess\/requests\?subject=/);
  });

  test('the next step is HR\'s unread reply, with one primary action', async ({ page }) => {
    await boot(page);
    const next = page.getByTestId('ess-attention');
    await expect(next).toContainText('Your next step');
    await expect(next).toContainText('HR replied to “Salary certificate for bank”');
    await expect(page.locator('[data-primary-action]:visible')).toHaveCount(1);
    const cta = page.locator('[data-primary-action]:visible');
    await expect(cta).toHaveText('Read the reply');
    await expect(cta).toHaveAttribute('href', '/ess/requests?open=r-1');
  });

  test('with nothing waiting the page says so and never claims more than it checked', async ({ page }) => {
    await boot(page, { hr: [], leave: [], advances: [], dashboard: { documentAlerts: [], actionItems: [] } });
    const next = page.getByTestId('ess-attention');
    await expect(next).toContainText('Nothing is waiting for you');
    await expect(next).toContainText('You have no open requests.');
    await expect(page.getByTestId('ess-home-hr-requests')).toContainText('Nothing waiting');
  });

  test('a list that failed to load is named, and the next step is not "all clear"', async ({ page }) => {
    await boot(page, { hr: [], leave: [], failAdvances: true, dashboard: { documentAlerts: [], actionItems: [] } });
    await expect(page.getByTestId('ess-home-hr-requests').getByRole('alert')).toContainText('Could not load: Salary advances');
    const next = page.getByTestId('ess-attention');
    await expect(next).toContainText('We could not check everything');
    await expect(next).not.toContainText('Nothing is waiting for you');
  });

  test('there is no clock-in or punch button anywhere', async ({ page }) => {
    await boot(page);
    for (const view of ['Today', 'My records']) {
      await page.getByRole('tab', { name: view }).click();
      await expect(page.getByRole('button', { name: /clock[ -]?in|punch in|check[ -]?in/i })).toHaveCount(0);
      await expect(page.getByRole('link', { name: /clock[ -]?in|punch in|check[ -]?in/i })).toHaveCount(0);
    }
    // The status line is a statement about today, not a control.
    await page.getByRole('tab', { name: 'Today' }).click();
    await expect(page.getByTestId('ess-today').getByText(/Clocked in at \d\d:\d\d/)).toBeVisible();
    await expect(page.getByTestId('ess-today').getByRole('button')).toHaveCount(0);
  });

  test('every figure and row comes from the payload', async ({ page }) => {
    await boot(page);
    const today = page.getByTestId('ess-hero');
    await expect(today).toContainText('EMP-0007');
    await expect(today).toContainText('HR Business Partner');
    await expect(today).toContainText('2 years 5 months of service');
    await expect(today).toContainText('Day shift');
    await expect(today).toContainText('On time');
    await expect(today).toContainText('6 h');
    await expect(today).toContainText('Next pay (estimate)');
    await expect(today).toContainText('Last net pay, October 2026');
    const upcoming = page.getByTestId('ess-home-upcoming');
    await expect(upcoming).toContainText('Expected pay date (estimate)');
    await expect(upcoming).toContainText('Passport is expiring');
    await expect(upcoming).toContainText('Complete your emergency contact');
    await expect(upcoming).toContainText('Not final: waiting for approval');
    await expect(upcoming).toContainText('in 11 days');
    await expect(page.getByTestId('ess-home-quick-actions')).toContainText('13 days available');
  });
});

test.describe('My day: requests', () => {
  test('each request shows named steps, the one waiting for the employee opens first', async ({ page }) => {
    await boot(page);
    const tracker = page.getByTestId('ess-home-hr-requests');
    await expect(tracker.getByRole('heading', { name: /My requests/ })).toContainText('4');
    const hr = tracker.locator('[data-request="hr-r-1"]');
    await expect(hr.getByRole('button', { name: /Salary certificate for bank/ })).toHaveAttribute('aria-expanded', 'true');
    await expect(hr.getByRole('list', { name: 'Steps' }).getByRole('listitem')).toHaveCount(4);
    await expect(hr.getByRole('listitem').filter({ hasText: 'HR replied' }).first()).toHaveAttribute('aria-current', 'step');
    await expect(hr.getByRole('link', { name: 'Read the reply' })).toHaveAttribute('href', '/ess/requests?open=r-1');

    const leave = tracker.locator('[data-request="leave-l-1"]');
    await leave.getByRole('button').first().click();
    const steps = leave.getByRole('list', { name: 'Steps' });
    await expect(steps).toContainText('Sent');
    await expect(steps).toContainText('Manager approval');
    await expect(steps).toContainText('Faisal Al-Otaibi');
    await expect(steps).toContainText('HR approval');
    await expect(steps.locator('[aria-current="step"]')).toContainText('HR approval');
    await expect(leave.getByRole('button').first()).toHaveAttribute('aria-expanded', 'true');
    // One row is open at a time: choosing another closes the first.
    await expect(hr.getByRole('button', { name: /Salary certificate for bank/ })).toHaveAttribute('aria-expanded', 'false');

    const adv = tracker.locator('[data-request="adv-ad-1"]');
    await adv.getByRole('button').first().click();
    await expect(adv.getByRole('list', { name: 'Steps' })).toContainText('Payout');
    await expect(adv).toContainText('ADV-0012');
  });

  test('"Ask Kody" answers from the API, and a suggested question asks it', async ({ page }) => {
    const { calls } = await boot(page);
    await page.getByRole('button', { name: 'How many leave days can I still take?' }).click();
    await expect(page.getByTestId('ess-home-kody')).toContainText('You have 13 annual leave days available.');
    expect(calls.ai).toBe(1);
    await page.getByRole('button', { name: 'Ask something else' }).click();
    await expect(page.getByRole('button', { name: 'When is my next salary?' })).toBeVisible();
  });
});

test.describe('My day: tabs and records', () => {
  test('Today and My records are a real tablist, in the address, and keep their state', async ({ page }) => {
    await boot(page);
    const tabs = page.getByRole('tablist', { name: 'My day views' });
    const today = tabs.getByRole('tab', { name: 'Today' });
    const records = tabs.getByRole('tab', { name: 'My records' });
    await expect(today).toHaveAttribute('aria-selected', 'true');
    await today.focus();
    await page.keyboard.press('ArrowRight');
    await expect(records).toBeFocused();
    await expect(records).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByRole('tabpanel', { name: 'My records' })).toBeVisible();
    await expect(page.getByTestId('ess-home-leave')).toContainText('13 of 21 days available');
    await expect(page.getByTestId('ess-home-leave')).toContainText('Statutory entitlement: 10 days per event');
    await expect(page.getByRole('heading', { name: 'Announcements' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'My Loans' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'My documents' })).toBeVisible();
    await expect(page.getByRole('tabpanel', { name: 'Today' })).toBeHidden();
    expect(page.url()).toContain('#tab=records');
    await shot(page, 'records', test.info().project.name);
    await page.reload();
    await expect(page.getByRole('tab', { name: 'My records' })).toHaveAttribute('aria-selected', 'true', { timeout: 60_000 });
  });

  test('a roster that failed to load says so with Retry instead of "no shifts"', async ({ page }) => {
    await boot(page, { failRoster: true, hash: '#tab=records' });
    const card = page.getByRole('region', { name: 'My Upcoming Shifts' });
    await expect(card).toContainText('Your roster could not be loaded.');
    await expect(card).not.toContainText('No shifts scheduled');
  });
});

test.describe('My day: read-only login', () => {
  test('is promised nothing it cannot do', async ({ page }) => {
    await boot(page, { permissions: ['ess.read'], hr: [], leave: [], dashboard: { documentAlerts: [], actionItems: [], attendanceToday: null, attendanceTodaySource: null } });
    const quick = page.getByTestId('ess-home-quick-actions');
    await expect(quick).toContainText('Open my leave');
    await expect(quick).toContainText('My letters');
    await expect(quick).not.toContainText('Request an HR letter');
    await expect(quick).not.toContainText('Request overtime');
    await expect(page.getByTestId('ess-attention')).toContainText('Open my payslips');
  });
});

test.describe('My day: Arabic', () => {
  test('reads right to left with Latin digits and isolated names, and keeps the pay masked', async ({ page }, info) => {
    await boot(page, { locale: 'ar' });
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('يومي');
    await expect(page.getByTestId('ess-attention')).toContainText('خطوتك التالية');
    await expect(page.getByTestId('ess-net-pay')).toContainText('••••••');
    await expect(page.getByRole('button', { name: 'إظهار صافي الراتب' })).toBeVisible();
    // Names and codes sit in their own <bdi>.
    await expect(page.getByTestId('ess-hero').locator('bdi', { hasText: 'Noura' })).toHaveCount(1);
    await expect(page.getByTestId('ess-hero').locator('bdi', { hasText: 'EMP-0007' })).toHaveCount(1);
    expect(await page.locator('main').innerText()).not.toMatch(/[٠-٩]/);
    expect(await noSidewaysScroll(page)).toBeLessThanOrEqual(1);
    await page.getByRole('button', { name: 'إظهار صافي الراتب' }).click();
    await expect(page.getByTestId('ess-net-pay')).toContainText('18,450.00');
    await shot(page, 'ar', info.project.name);
    await page.getByRole('tab', { name: 'سجلاتي' }).click();
    await expect(page.getByTestId('ess-home-leave')).toContainText('13');
    expect(await noSidewaysScroll(page)).toBeLessThanOrEqual(1);
    await shot(page, 'ar-records', info.project.name);
  });
});

test.describe('My day: Arabic arrival', () => {
  test('the arrival fact and the provisional note read in Arabic', async ({ page }) => {
    const punch = { workDate: inDays(0), status: 'Present', firstInUtc: ago(3 * 3600_000), totalWorkedMinutes: 0, missingPunch: false, lateMinutes: 0, overtimeMinutes: 0 };
    await boot(page, { locale: 'ar', dashboard: { attendanceToday: punch, attendanceTodaySource: 'processed' } });
    await expect(page.getByTestId('ess-hero')).toContainText('وقت الوصول');
    await expect(page.getByTestId('ess-hero')).toContainText('في الوقت المحدد');
  });

  test('a raw punch is marked provisional in Arabic and shows no arrival fact', async ({ page }) => {
    const punch = { workDate: inDays(0), status: 'Present', firstInUtc: ago(3 * 3600_000), totalWorkedMinutes: 0, missingPunch: false, lateMinutes: 0, overtimeMinutes: 0 };
    await boot(page, { locale: 'ar', dashboard: { attendanceToday: punch, attendanceTodaySource: 'raw' } });
    await expect(page.getByTestId('ess-hero')).not.toContainText('وقت الوصول');
    await expect(page.getByTestId('ess-hero')).toContainText('مبدئي: يتأكد التأخير والحالة عندما تعالج الموارد البشرية اليوم.');
  });
});

test.describe('My day: phone and layout', () => {
  test('phone stacks, keeps one primary action in a bottom bar, and does not scroll sideways', async ({ page }, info) => {
    await boot(page);
    expect(await noSidewaysScroll(page)).toBeLessThanOrEqual(1);
    const bar = page.getByTestId('ess-action-bar');
    if (isPhone(page)) {
      await expect(bar).toBeVisible();
      await expect(bar.getByRole('link', { name: 'Read the reply' })).toBeVisible();
      const hero = await page.getByTestId('ess-hero').boundingBox();
      const tracker = await page.getByTestId('ess-home-hr-requests').boundingBox();
      expect(tracker!.y).toBeGreaterThan(hero!.y + hero!.height - 1);
      const b = await bar.boundingBox();
      const vp = page.viewportSize()!;
      expect(b!.y + b!.height).toBeLessThanOrEqual(vp.height);
    } else {
      await expect(bar).toBeHidden();
    }
    await expect(page.locator('[data-primary-action]:visible')).toHaveCount(1);
    await shot(page, 'today', info.project.name);
  });

  test('1280 x 760 keeps the hero and the tracker side by side', async ({ page }, info) => {
    test.skip(info.project.name !== 'desktop', 'one desktop size is enough');
    await page.setViewportSize({ width: 1280, height: 760 });
    await boot(page);
    const hero = (await page.getByTestId('ess-hero').boundingBox())!;
    const tracker = (await page.getByTestId('ess-home-hr-requests').boundingBox())!;
    expect(Math.abs(hero.y - tracker.y)).toBeLessThan(2);
    expect(await noSidewaysScroll(page)).toBeLessThanOrEqual(1);
    await shot(page, '1280x760', info.project.name);
  });
});

test.describe('My day: accessibility', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`no serious axe violations (${theme})`, async ({ page }, info) => {
      await page.emulateMedia({ reducedMotion: 'reduce' });
      await boot(page, { theme });
      await expect(page.getByTestId('ess-home-upcoming')).toBeVisible();
      await shot(page, theme, info.project.name);
      const check = async (label: string) => {
        const results = await new AxeBuilder({ page }).include('main').analyze();
        const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical')
          .map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => `${n.target.join(' ')} :: ${n.failureSummary?.split('\n')[1] ?? ''}`) }));
        expect(serious, label).toEqual([]);
      };
      await check('today');
      await page.getByRole('button', { name: 'Show net pay' }).click();
      await check('net pay shown');
      await page.getByRole('tab', { name: 'My records' }).click();
      await check('records');
    });
  }

  test('a calm day (nothing waiting) passes contrast on the teal hero', async ({ page }, info) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await boot(page, { hr: [], leave: [], advances: [], dashboard: { documentAlerts: [], actionItems: [] } });
    await expect(page.getByTestId('ess-hero')).toHaveAttribute('data-tone', 'calm');
    await shot(page, 'calm', info.project.name);
    const results = await new AxeBuilder({ page }).include('main').analyze();
    expect(results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => v.id)).toEqual([]);
  });
});
