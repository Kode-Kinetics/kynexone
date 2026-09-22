import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * HR Command Center, fixture lane (e2e/playwright.fixture.config.ts).
 *
 * The primary fixture mirrors the IntelliFlow/Evostel tenant as observed: 12 active employees
 * in four departments, attendance captured only in August and September, one locked payroll
 * run, three leave approvals waiting 4-5 days, and 12 employees missing a required document.
 * That combination is what produced the contradictions this suite now guards against.
 * A second, fuller fixture proves the charts render when the data supports them.
 */

const EARLY = new Date('2026-09-22T02:58:00Z'); // 05:58 Riyadh: before the working day
const MIDDAY = new Date('2026-09-22T09:00:00Z'); // 12:00 Riyadh
const months = ['Oct', 'Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep'];
const EVIDENCE = process.env.DASHBOARD_EVIDENCE_DIR;

function dataset(rich: boolean, now: Date) {
  const ago = (h: number) => new Date(now.getTime() - h * 3.6e6).toISOString();
  return {
    summary: { totalEmployees: rich ? 148 : 12, activeEmployees: rich ? 142 : 12, presentToday: rich ? 118 : 0, onLeave: rich ? 9 : 0, absent: rich ? 6 : 0, overtimeHours: 16, churnRisk: 0, attendanceRecordsToday: rich ? 133 : 0 },
    trends: months.map((m, i) => ({ month: m, attendanceRate: rich ? [92, 93, 91, 94, 95, 94, 94, 95, 93, 96, 97, 95][i] : [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 98, 97][i], overtimeHours: rich ? [80, 90, 70, 100, 110, 95, 120, 96, 140, 110, 88, 64][i] : [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 12, 16][i] })),
    overview: {
      pendingApprovals: rich ? 14 : 3,
      approvalQueue: (rich
        ? [['Annual Leave - Raj Krishnamurthy', 130], ['Annual Leave - Amira Mansour', 100], ['Casual Leave - Sunita Patel', 98], ['Employee change approval - EMP-SA-ENG-0007 Omar Haddad', 50], ['Overtime - Layla Farouk', 20]]
        : [['Annual Leave - Raj Krishnamurthy', 125], ['Annual Leave - Amira Mansour', 100], ['Casual Leave - Sunita Patel', 98]]
      ).map(([t, h], i) => ({ id: `a${i}`, title: t as string, module: String(t).startsWith('Employee') ? 'EmployeeChangeRequest' : 'LeaveRequest', createdAtUtc: ago(h as number),
        ...(rich ? { department: ['Engineering', 'Product', 'Human Resources', 'Engineering', 'Operations'][i], detail: ['12 Oct to 18 Oct', '2 Oct to 6 Oct', '29 Sep', 'IBAN', '20 h, September'][i], dueAtUtc: ago((h as number) - 72) } : {}) })),
      payrollSummary: rich ? { periodLabel: 'Sep 2026', totalGross: 2030000, totalNet: 1893400, totalDeductions: 136600, employeeCount: 142, status: 'PendingFinanceReview', payDate: null, employerContributions: 212000 } : { periodLabel: 'Sep 2026', totalGross: 205200, totalNet: 194600, totalDeductions: 10600, employeeCount: 12, status: 'Locked' },
      payrollByEntity: [], workforceMix: [{ name: 'Full-time', value: 12 }],
      headcountByDepartment: rich
        ? [{ name: 'Engineering', value: 46 }, { name: 'Operations', value: 38 }, { name: 'Sales', value: 24 }, { name: 'Product', value: 14 }, { name: 'Human Resources', value: 9 }, { name: 'Finance', value: 7 }, { name: 'Legal', value: 4 }]
        : [{ name: 'Engineering', value: 6 }, { name: 'Product', value: 3 }, { name: 'Human Resources', value: 2 }, { name: 'Finance', value: 1 }],
      alerts: rich ? [
        { title: 'Iqama (residence permit) expired 12 Aug 2026', severity: 'Critical', employeeName: 'Omar Haddad', expiryDate: '2026-08-12', daysRemaining: -41, kind: 'iqama_number' },
        { title: 'Passport expires 03 Oct 2026', severity: 'Warning', employeeName: 'Layla Farouk', expiryDate: '2026-10-03', daysRemaining: 11, kind: 'passport_number' },
        { title: 'Work permit expires 28 Oct 2026', severity: 'Warning', employeeName: 'Raj Krishnamurthy', expiryDate: '2026-10-28', daysRemaining: 36, kind: 'work_permit' },
        { title: 'Iqama (residence permit) expires 13 Nov 2026', severity: 'Warning', employeeName: 'Noura Al-Qahtani', expiryDate: '2026-11-13', daysRemaining: 52, kind: 'iqama_number' },
        { title: 'Visa expires 14 Dec 2026', severity: 'Warning', employeeName: 'Maha Bakr', expiryDate: '2026-12-14', daysRemaining: 83, kind: 'visa' },
      ] : [],
      openLeaveRequests: 3, newJoinersThisMonth: rich ? 4 : 0, complianceAlertsTotal: rich ? 5 : 0, complianceCriticalTotal: rich ? 1 : 0,
    },
    payrollTrends: months.map((m, i) => ({ month: m, totalNet: rich ? [1520000, 1550000, 1580000, 1600000, 1630000, 1660000, 1700000, 1740000, 1780000, 1810000, 1850000, 1893400][i] : [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 194600][i], employeeCount: 12, status: 'Locked' })),
    analytics: rich ? {
      attendanceHeatmap: {
        days: Array.from({ length: 15 }, (_, i) => new Date(Date.UTC(2026, 8, 8 + i)).toISOString().slice(0, 10)),
        departments: ['Engineering', 'Operations', 'Sales', 'Product', 'Human Resources', 'Finance'].map((name, r) => ({
          name, headcount: [46, 38, 24, 14, 9, 7][r],
          cells: Array.from({ length: 15 }, (_, i) => {
            const date = new Date(Date.UTC(2026, 8, 8 + i));
            const weekend = date.getUTCDay() === 5 || date.getUTCDay() === 6;
            const rostered = weekend ? 0 : [46, 38, 24, 14, 9, 7][r];
            const rate = weekend ? null : Math.min(100, [95, 91, 88, 94, 96, 97][r] + ((i * 7 + r * 3) % 7) - 3 - (r === 1 && (i === 7 || i === 8) ? 9 : 0));
            return { date: date.toISOString().slice(0, 10), rostered, attended: rate == null ? 0 : Math.round(rostered * rate / 100), rate };
          }),
        })),
      },
      leaveUsage: { year: 2026, takenDays: 1184, entitlementDays: 4260, byType: [{ type: 'Annual', days: 812 }, { type: 'Sick', days: 256 }, { type: 'Hajj', days: 70 }, { type: 'Unpaid', days: 46 }] },
      nationality: { saudi: 54, nonSaudi: 88, unknown: 0, saudizationPct: 38.0, nitaqatBand: 'MediumGreen' },
      headcountTrend: months.map((m, i) => ({ month: m, active: [118, 121, 123, 126, 128, 131, 133, 135, 137, 138, 139, 142][i] })),
    } : undefined,
    activityFeed: [['payroll.run.locked', 70], ['attendance.processed', 71]].map(([a, h]) => ({ module: String(a).split('.')[0].replace(/^./, (c) => c.toUpperCase()), action: a, actor: 'System', occurredAt: ago(h as number) })),
    kpis: { pendingLeaveRequests: 3, pendingAttendanceCorrections: 0, attendanceExceptions: 0, expiringDocuments: 0, expiredDocuments: 0, missingDocuments: 12, qiwaEnabled: false },
  };
}

const USER = {
  id: 'u1', tenantId: 't1', tenantSlug: 'intelliflow', email: 'admin@intelliflow.test', fullName: 'Yaser Al-Ghamdi',
  roles: ['Admin'], accountType: 'Group', isGroupScope: true, companies: [],
  permissions: ['dashboard.read', 'employees.read', 'attendance.read', 'leave.read', 'approvals.read', 'approvals.decide', 'payroll.read', 'compliance.read', 'reports.read', 'ai.query', 'ai.insights_view'],
};

async function open(page: Page, opts: { rich?: boolean; now?: Date; theme?: 'light' | 'dark' } = {}) {
  const now = opts.now ?? EARLY;
  await page.clock.setFixedTime(now);
  await page.addInitScript((th) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', th);
    if (!sessionStorage.getItem('fixture-booted')) { sessionStorage.removeItem('kx-dashboard-view'); sessionStorage.setItem('fixture-booted', '1'); }
  }, opts.theme ?? 'light');
  await page.route('**/api/**', (route) => {
    const p = new URL(route.request().url()).pathname;
    const json = (b: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(USER);
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: true });
    if (p === '/api/dashboard/full') return json(dataset(!!opts.rich, now));
    if (p === '/api/ai/status') return json({ enabled: true, provider: 'fixture' });
    return json({ items: [], total: 0 });
  });
  await page.goto('/dashboard');
  await expect(page.getByRole('heading', { name: 'HR Command Center' })).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('#hero-heading')).toBeVisible({ timeout: 15_000 });
}

async function evidence(page: Page, name: string, fullPage = false) {
  if (!EVIDENCE) return;
  await page.waitForTimeout(350); // let view transitions (<= 280 ms) settle
  if (EVIDENCE) await page.screenshot({ path: `${EVIDENCE}/${name}.png`, fullPage });
}

test.describe('HR Command Center: data trust', () => {
  test('today is never mixed with history; states are distinct', async ({ page }) => {
    await open(page);
    const tiles = page.getByRole('region', { name: 'Key metrics' });
    await expect(tiles.getByRole('img', { name: 'No attendance recorded yet today' })).toBeVisible();
    await expect(tiles.getByText('The working day has not started. Nothing is late yet.')).toBeVisible();
  });

  test('after the working day starts with no punches, attendance says so', async ({ page }) => {
    await open(page, { now: MIDDAY });
    await expect(page.getByText('No punches recorded today. Check the attendance devices.')).toBeVisible();
  });

  test('missing documents are counted as employees, with a specific action', async ({ page }) => {
    await open(page);
    const attention = page.locator('section[aria-labelledby="attention-heading"]');
    await expect(attention.getByText('12 employees missing required documents')).toBeVisible();
    await expect(attention.getByText('Review missing documents')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Collect' })).toHaveCount(0);
  });

  test('payroll hero: one run is stated, not drawn as a trend; stepper follows status', async ({ page }) => {
    await open(page);
    const hero = page.locator('section[aria-labelledby="hero-heading"]');
    await expect(hero.getByText('SAR 194.6K')).toBeVisible();
    await expect(hero.getByText(/first payroll run on record/)).toBeVisible();
    // A locked run is finished: every step done, none left current.
    await expect(hero.getByRole('listitem').filter({ hasText: 'current step' })).toHaveCount(0);
    await expect(hero.getByRole('listitem').filter({ hasText: 'Locked' })).toContainText('done');
  });

  test('approvals reconcile: badge, rows and ages', async ({ page }, info) => {
    await open(page);
    if (info.project.name !== 'desktop') await page.getByRole('tab', { name: /To do/ }).click();
    const card = page.locator('section[aria-labelledby="approvals-heading"]');
    await expect(card.locator('#approvals-heading')).toContainText('3');
    await expect(card.getByText('Raj Krishnamurthy', { exact: true })).toBeVisible();
    await expect(card.getByText('Oldest waiting 5 days.', { exact: false })).toBeVisible();
  });

  test('older API without analytics degrades to statements, not invented charts', async ({ page }, info) => {
    await open(page);
    if (info.project.name !== 'desktop') await page.getByRole('tab', { name: /Insights/ }).click();
    // No analytics block: the heatmap is left out rather than shown empty, and leave falls back
    // to the requests waiting; nothing mentions services or infrastructure.
    await expect(page.locator('#heat-heading')).toHaveCount(0);
    await expect(page.getByText('leave requests waiting for a decision.')).toBeVisible();
    await expect(page.getByText(/analytics service/)).toHaveCount(0);
    await expect(page.locator('canvas')).toHaveCount(0);
  });
});

test.describe('HR Command Center: layout and interaction', () => {
  test('desktop: hero and decisions lead; keyboard reaches the actions', async ({ page }, info) => {
    test.skip(info.project.name !== 'desktop', 'desktop only');
    await open(page);
    await expect(page.locator('#attention-heading')).toBeInViewport();
    await expect(page.locator('#hero-heading')).toBeInViewport();
    await evidence(page, 'desktop-sparse');
    await evidence(page, 'desktop-sparse-full', true);
    await page.getByRole('link', { name: /Review payroll run|Open payroll run/ }).focus();
    await expect(page.getByRole('link', { name: /Review payroll run|Open payroll run/ })).toBeFocused();
  });

  test('rich data: trend, heatmap, 3D composition, timeline all render with text equivalents', async ({ page }, info) => {
    test.skip(info.project.name !== 'desktop', 'desktop only');
    await open(page, { rich: true });
    await expect(page.getByRole('img', { name: /^Net payroll by month, SAR: Oct 1\.52M/ })).toBeVisible();
    await expect(page.getByRole('img', { name: /^Operations, .*: \d+%, \d+ of 38/ }).first()).toBeVisible();
    await expect(page.getByRole('img', { name: /^Nationality mix: Saudi 54, non-Saudi 88/ })).toBeVisible();
    await expect(page.getByRole('img', { name: /^Headcount by department: Engineering 46/ })).toBeVisible();
    await expect(page.getByText('Passport', { exact: true }).first()).toBeVisible();
    await expect(page.getByRole('listitem').filter({ hasText: 'current step' })).toContainText('Finance review');
    // Today's heatmap column is marked as still filling in.
    await expect(page.getByRole('img', { name: /so far today/ }).first()).toBeVisible();
    await evidence(page, 'desktop-rich');
    await evidence(page, 'desktop-rich-full', true);
    await page.getByRole('button', { name: 'Show as table' }).first().click();
    await expect(page.getByRole('rowheader', { name: 'Engineering' })).toBeVisible();
  });

  test('compact: tabs move between views; choice survives reload', async ({ page }, info) => {
    test.skip(info.project.name === 'desktop', 'compact only');
    await open(page, { rich: true });
    await evidence(page, `${info.project.name}-today`);
    const tabs = page.getByRole('tablist', { name: 'Dashboard views' });
    await tabs.getByRole('tab', { name: /To do/ }).click();
    await expect(page.locator('#approvals-heading')).toBeInViewport();
    await evidence(page, `${info.project.name}-actions`);
    await tabs.getByRole('tab', { name: /Insights/ }).click();
    await evidence(page, `${info.project.name}-insights`);
    await page.reload();
    await expect(page.getByRole('tab', { name: /Insights/ })).toHaveAttribute('aria-selected', 'true', { timeout: 15_000 });
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(1);
  });

  test('phone: KPI tiles are a swipe rail and critical items stay in the plain list', async ({ page }, info) => {
    test.skip(info.project.name !== 'phone', 'phone only');
    await open(page);
    const rail = page.getByRole('region', { name: 'Key metrics' });
    const before = await rail.evaluate((el) => el.scrollLeft);
    await rail.evaluate((el) => el.scrollBy({ left: 300 }));
    expect(await rail.evaluate((el) => el.scrollLeft)).toBeGreaterThan(before);
    await expect(page.locator('section[aria-labelledby="attention-heading"]').getByText('12 employees missing required documents')).toBeVisible();
    await evidence(page, 'phone-bottom-nav');
  });

  test('assistant opens as a drawer, labelled advisory', async ({ page }, info) => {
    await open(page);
    await page.getByRole('button', { name: 'Open assistant' }).click();
    const drawer = page.getByRole('dialog', { name: 'Assistant' });
    await expect(drawer).toBeVisible();
    await expect(drawer.getByText('Advisory', { exact: true })).toBeVisible();
    await evidence(page, `${info.project.name}-assistant`);
    await page.keyboard.press('Escape');
    await expect(drawer).toBeHidden();
  });

  test('reduced motion: no keyframe animations run', async ({ page }, info) => {
    test.skip(info.project.name !== 'desktop', 'desktop only');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await open(page, { rich: true });
    const running = await page.evaluate(() => document.getAnimations()
      .filter((a) => a.playState === 'running' && typeof CSSAnimation !== 'undefined' && a instanceof CSSAnimation)
      .map((a) => `${(a as CSSAnimation).animationName}`));
    expect(running).toEqual([]);
  });
});

test.describe('HR Command Center: accessibility', () => {
  for (const mode of ['light', 'dark', 'forced-colors'] as const) {
    test(`no serious axe violations (${mode})`, async ({ page }, info) => {
      await page.emulateMedia({ reducedMotion: 'reduce', ...(mode === 'forced-colors' ? { forcedColors: 'active' as const } : {}) });
      await open(page, { rich: true, theme: mode === 'dark' ? 'dark' : 'light' });
      await evidence(page, `${info.project.name}-${mode}`, true);
      const results = await new AxeBuilder({ page }).include('main').analyze();
      const serious = results.violations
        .filter((v) => v.impact === 'serious' || v.impact === 'critical')
        .map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => `${n.target.join(' ')} :: ${n.failureSummary?.split('\n')[1] ?? ''}`) }));
      expect(serious).toEqual([]);
    });
  }

  test('200% zoom keeps the page usable (no sideways scroll)', async ({ page }, info) => {
    test.skip(info.project.name !== 'desktop', 'desktop only');
    await page.setViewportSize({ width: 720, height: 450 });
    await open(page, { rich: true });
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(1);
    await evidence(page, 'zoom-200');
  });
});
