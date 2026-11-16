import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

// Which layout the page renders, from the real viewport (the app's own breakpoints), so the
// suite is right under any project: the fixture matrix here, or the Desktop Chrome CI lane.
function formOf(page: Page): 'desktop' | 'tablet' | 'phone' {
  const w = page.viewportSize()?.width ?? 1280;
  return w >= 1024 ? 'desktop' : w < 640 ? 'phone' : 'tablet';
}

// The one-screen stage (tabs, no page scroll) starts at 1280 x 740; below that the stacked layout stays.
function stageOn(page: Page): boolean {
  const v = page.viewportSize();
  return !!v && v.width >= 1280 && v.height >= 740;
}

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
// The run's legal entity. Payroll amounts are labelled with ITS currency (lib/payrollCurrency),
// resolved from GET /api/payroll/companies — never a hard-coded SAR.
const COMPANY = { id: 'c-intelliflow-ksa', name: 'IntelliFlow KSA', tradeName: '', defaultCurrency: 'SAR', wpsEmployerId: '', gosiEmployerId: '' };

/** A pending approval on the dashboard queue whose entity is NOT a plain leave request, yet whose title reads like one. */
// The oldest row, so the stage's row cap cannot hide it.
const ENCASHMENT_ROW = { id: 'enc1', title: 'Leave Encashment - Noura Al-Qahtani', module: 'LeaveEncashmentRequest', detail: '5 days', createdAtUtc: new Date(EARLY.getTime() - 140 * 3.6e6).toISOString() };

function dataset(rich: boolean, now: Date, extraQueue: Record<string, unknown>[] = [], withoutIds: string[] = []) {
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
        ...(rich ? { department: ['Engineering', 'Product', 'Human Resources', 'Engineering', 'Operations'][i], detail: ['12 Oct to 18 Oct', '2 Oct to 6 Oct', '29 Sep', 'IBAN', '20 h, September'][i], dueAtUtc: ago((h as number) - 72) } : {}) }))
        .filter((q) => !withoutIds.includes(q.id))
        .concat(extraQueue.map((q) => ({ createdAtUtc: ago(60), ...q })) as never),
      payrollSummary: rich ? { periodLabel: 'Sep 2026', totalGross: 2030000, totalNet: 1893400, totalDeductions: 136600, employeeCount: 142, status: 'PendingFinanceReview', payDate: null, employerContributions: 212000, companyId: COMPANY.id } : { periodLabel: 'Sep 2026', totalGross: 205200, totalNet: 194600, totalDeductions: 10600, employeeCount: 12, status: 'Locked', companyId: COMPANY.id },
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
    payrollTrends: months.map((m, i) => ({ month: m, totalNet: rich ? [1520000, 1550000, 1580000, 1600000, 1630000, 1660000, 1700000, 1740000, 1780000, 1810000, 1850000, 1893400][i] : [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 194600][i], employeeCount: 12, status: 'Locked', companyId: COMPANY.id })),
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

async function open(page: Page, opts: { extraQueue?: Record<string, unknown>[]; locale?: 'en' | 'ar'; rich?: boolean; now?: Date; theme?: 'light' | 'dark'; tenantTz?: string; user?: typeof USER } = {}) {
  const now = opts.now ?? EARLY;
  await page.clock.setFixedTime(now);
  if (opts.locale) await page.addInitScript((l) => localStorage.setItem('kynexone-locale-choice-v2', l), opts.locale);
  await page.addInitScript((th) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', th);
    if (!sessionStorage.getItem('fixture-booted')) { sessionStorage.removeItem('kx-dashboard-view'); sessionStorage.setItem('fixture-booted', '1'); }
  }, opts.theme ?? 'light');
  await page.route('**/api/**', (route) => {
    const p = new URL(route.request().url()).pathname;
    const json = (b: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(opts.user ?? USER);
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    // `tenantTz: ''` is the real API answer for a tenant that has stated no zone — see
    // TenantAdminController.UnstatedLocalizationAsync. It must NOT be a US zone, and the header
    // must then follow the viewer's own browser zone.
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: opts.tenantTz ?? 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: true });
    if (p === '/api/dashboard/full') return json(dataset(!!opts.rich, now, opts.extraQueue));
    if (p === '/api/payroll/companies') return json([COMPANY]);
    if (p === '/api/payroll/overview') return json({ totalValidationErrors: 3 });
    if (p === '/api/ai/status') return json({ enabled: true, provider: 'fixture' });
    return json({ items: [], total: 0 });
  });
  await page.goto('/dashboard');
  await expect(page.getByRole('heading', { name: opts.locale === 'ar' ? 'مركز قيادة الموارد البشرية' : 'HR Command Center' })).toBeVisible({ timeout: 30_000 });
  if ((opts.user ?? USER).permissions.includes('payroll.read'))
    await expect(page.locator('#hero-heading')).toBeVisible({ timeout: 15_000 });
}

async function evidence(page: Page, name: string, fullPage = false) {
  if (!EVIDENCE) return;
  await page.waitForTimeout(350); // let view transitions (<= 280 ms) settle
  if (EVIDENCE) await page.screenshot({ path: `${EVIDENCE}/${name}.png`, fullPage });
}

test.describe('HR Command Center: who sees payroll (F10)', () => {
  test('without payroll.read or reports.read there is no payroll card and no reports link', async ({ page }) => {
    const manager = { ...USER, roles: ['Manager'], permissions: ['dashboard.read', 'employees.read', 'approvals.read', 'manager.read'] };
    await open(page, { user: manager });
    await expect(page.getByRole('region', { name: 'Key metrics' })).toBeVisible();
    await expect(page.locator('#hero-heading')).toHaveCount(0);
    await expect(page.locator('#paydept-heading')).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Open reports' })).toHaveCount(0);
  });

  test('a payroll reader sees the run in its company currency', async ({ page }) => {
    await open(page);
    await expect(page.locator('section[aria-labelledby="hero-heading"]').getByText('SAR 194.6K').first()).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open reports' })).toBeVisible();
  });
});

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
    // The stacked layout has a chip strip; the one-screen stage folds it into the "Needs your attention" card.
    const attention = page.locator('section[aria-labelledby="attention-heading"], [data-testid="your-tasks"]');
    await expect(attention.getByText('12 employees missing required documents').first()).toBeVisible();
    await expect(attention.getByText('Review missing documents')).toBeVisible();
    // Missing documents need action; they are not shown as Critical.
    await expect(attention.getByRole('link', { name: /Action needed: 12 employees missing required documents/ })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Collect' })).toHaveCount(0);
  });

  test('payroll hero: one run is stated, not drawn as a trend; stepper follows status', async ({ page }) => {
    await open(page);
    const hero = page.locator('section[aria-labelledby="hero-heading"]');
    await expect(hero.getByText('SAR 194.6K').first()).toBeVisible();
    await expect(hero.getByText(/first payroll run on record/i)).toBeVisible();
    // A locked run is finished: every step done, none left current.
    await expect(hero.getByRole('listitem').filter({ hasText: 'current step' })).toHaveCount(0);
    await expect(hero.getByRole('listitem').filter({ hasText: 'Locked' })).toContainText('done');
  });

  test('approvals reconcile: badge, rows and ages', async ({ page }, info) => {
    await open(page);
    if (formOf(page) !== 'desktop') await page.getByRole('tab', { name: /To do/ }).click();
    const card = page.locator('section[aria-labelledby="approvals-heading"]');
    await expect(card.locator('#approvals-heading')).toContainText('3');
    await expect(card.getByText('Raj Krishnamurthy', { exact: true })).toBeVisible();
    await expect(card.getByText('Oldest waiting 5 days.', { exact: false })).toBeVisible();
  });

  test('older API without analytics degrades to statements, not invented charts', async ({ page }, info) => {
    await open(page);
    if (formOf(page) !== 'desktop') await page.getByRole('tab', { name: /Insights/ }).click();
    // No analytics block: the heatmap is left out rather than shown empty, and leave falls back
    // to the requests waiting; nothing mentions services or infrastructure.
    await expect(page.locator('#heat-heading')).toHaveCount(0);
    await expect(page.getByText('leave requests waiting for a decision.')).toBeVisible();
    await expect(page.getByText(/analytics service/)).toHaveCount(0);
    await expect(page.locator('canvas')).toHaveCount(0);
  });
});

test.describe('HR Command Center: needs your attention', () => {
  test('a task list sits above the monitoring figures, ordered critical first, each row one link', async ({ page }) => {
    test.skip(formOf(page) === 'phone', 'the phone shows it inside the Today view; covered by the compact tab test');
    await open(page);
    const tasks = page.getByTestId('your-tasks');
    await expect(tasks.getByRole('heading', { name: 'Needs your attention' })).toBeVisible();
    await expect(tasks.locator('[data-task]')).toHaveText([
      /3 payroll validation errors/,
      /3 approvals waiting for you/,
    ]);
    await expect(tasks.locator('[data-task="payroll-validation"]')).toHaveAttribute('href', '/payroll');
    await expect(tasks.locator('[data-task="approvals"]')).toHaveAttribute('href', '/approvals');
    const tasksBox = await tasks.boundingBox();
    const heroBox = await page.locator('#hero-heading').boundingBox();
    expect(tasksBox!.y).toBeLessThan(heroBox!.y);
  });
});

test.describe('HR Command Center: layout and interaction', () => {
  test('desktop: hero and decisions lead; keyboard reaches the actions', async ({ page }, info) => {
    test.skip(formOf(page) !== 'desktop', 'desktop only');
    await open(page);
    await expect(page.locator('#attention-heading, #your-tasks-heading').first()).toBeInViewport();
    await expect(page.locator('#hero-heading')).toBeInViewport();
    await evidence(page, 'desktop-sparse');
    await evidence(page, 'desktop-sparse-full', true);
    await page.getByRole('link', { name: /Review payroll run|Open payroll run/ }).focus();
    await expect(page.getByRole('link', { name: /Review payroll run|Open payroll run/ })).toBeFocused();
  });

  test('rich data: trend, heatmap, composition, timeline all render with text equivalents', async ({ page }, info) => {
    test.skip(formOf(page) !== 'desktop', 'desktop only');
    await open(page, { rich: true });
    // Overview: the hero trend, the run stepper, the Saudization tile.
    await expect(page.getByRole('img', { name: /^Net payroll by month, SAR: Oct 1\.52M/ })).toBeVisible();
    await expect(page.getByRole('img', { name: /^Nationality mix: Saudi 54, non-Saudi 88/ })).toBeVisible();
    await expect(page.getByRole('listitem').filter({ hasText: 'current step' })).toContainText('Finance review');
    await evidence(page, 'desktop-rich');
    // Departments: heatmap, headcount bars. (Stacked layout below 1280 x 740 has no tabs.)
    if (stageOn(page)) await page.getByRole('tab', { name: 'Departments' }).click();
    await expect(page.getByRole('img', { name: /^Operations, .*: \d+%, \d+ of 38/ }).first()).toBeVisible();
    await expect(page.getByRole('img', { name: /^Headcount by department: Engineering 46/ })).toBeVisible();
    // Today's heatmap column is marked as still filling in.
    await expect(page.getByRole('img', { name: /so far today/ }).first()).toBeVisible();
    await evidence(page, 'desktop-rich-departments');
    await page.getByRole('button', { name: 'Show as table' }).first().click();
    await expect(page.getByRole('rowheader', { name: 'Engineering' })).toBeVisible();
    // Compliance: the expiry timeline and its document coverage.
    if (stageOn(page)) await page.getByRole('tab', { name: 'Compliance' }).click();
    await expect(page.getByText('Passport', { exact: true }).first()).toBeVisible();
    await evidence(page, 'desktop-rich-compliance');
  });

  test('compact: tabs move between views; choice survives reload', async ({ page }, info) => {
    test.skip(formOf(page) === 'desktop', 'compact only');
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

  test('sidebar entries explain themselves on hover and keyboard focus', async ({ page }, info) => {
    test.skip(formOf(page) !== 'desktop', 'desktop rail only');
    await open(page);
    const nav = page.getByRole('navigation', { name: 'Primary navigation' });
    // The desktop rail keeps Overview plus one work area open so every section heading remains
    // visible without a page-height sidebar. Open the area whose entry we are exercising instead
    // of relying on the pre-redesign behaviour where every section was expanded at once.
    await nav.getByRole('button', { name: 'Finance & Talent', exact: true }).click();
    await nav.getByRole('button', { name: 'Payroll', exact: true }).hover();
    const tip = page.getByRole('tooltip');
    await expect(tip).toContainText('Run, review, approve and lock monthly payroll.');
    await page.mouse.move(900, 500);
    await expect(tip).toHaveCount(0);
    await nav.getByRole('button', { name: 'Administration', exact: true }).click();
    await nav.getByRole('button', { name: 'Approvals', exact: true }).focus();
    await page.keyboard.press('Shift+Tab');
    await page.keyboard.press('Tab');
    await expect(page.getByRole('tooltip')).toContainText('waiting for your decision');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('tooltip')).toHaveCount(0);
  });

  test('phone: KPI tiles are a swipe rail and critical items stay in the plain list', async ({ page }, info) => {
    test.skip(formOf(page) !== 'phone', 'phone only');
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
    const launcher = page.getByRole('button', { name: 'Open Kody, HR assistant' });
    await launcher.click();
    const drawer = page.getByRole('dialog', { name: 'Kody the HR Assistant' });
    await expect(drawer).toBeVisible();
    await expect(drawer.getByText('Advisory', { exact: true })).toBeVisible();
    await evidence(page, `${info.project.name}-assistant`);
    await page.keyboard.press('Escape');
    await expect(drawer).toBeHidden();
    await expect(launcher).toBeFocused();
  });

  test('reduced motion: no keyframe animations run', async ({ page }, info) => {
    test.skip(formOf(page) !== 'desktop', 'desktop only');
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
    test.skip(formOf(page) !== 'desktop', 'desktop only');
    await page.setViewportSize({ width: 720, height: 450 });
    await open(page, { rich: true });
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(1);
    await evidence(page, 'zoom-200');
  });
});

test.describe('HR Command Center: one-screen stage', () => {
  // The dashboard is exactly one viewport tall on a desktop window: nothing to scroll to.
  for (const size of [{ width: 1280, height: 760 }, { width: 1440, height: 800 }, { width: 1728, height: 917 }, { width: 1920, height: 1080 }]) {
    test(`${size.width}x${size.height}: every section fits the window, no page scroll`, async ({ page }) => {
      test.skip(formOf(page) !== 'desktop', 'desktop only');
      await page.setViewportSize(size);
      await open(page, { rich: true });
      for (const name of ['Overview', 'Departments', 'Compliance']) {
        await page.getByRole('tab', { name }).click();
        await expect(page.getByRole('tab', { name })).toHaveAttribute('aria-selected', 'true');
        const over = await page.evaluate(() => document.documentElement.scrollHeight - window.innerHeight);
        expect(over, `${name} scrolls the page by ${over}px`).toBeLessThanOrEqual(1);
        // Cards clip their overflow, so a page that does not scroll can still hide content. Nothing
        // inside the visible section may be taller than the box that holds it.
        const clipped = await page.evaluate(() => {
          const panel = document.getElementById('stage-panel');
          if (!panel) return ['no #stage-panel'];
          const out: string[] = [];
          for (const el of Array.from(panel.querySelectorAll<HTMLElement>('section, article, div'))) {
            if (el.offsetParent === null) continue;
            const cs = getComputedStyle(el);
            if (cs.overflowY === 'visible') continue;
            if (el.scrollHeight - el.clientHeight > 2 && cs.overflowY !== 'auto' && cs.overflowY !== 'scroll') {
              out.push(`${el.tagName.toLowerCase()}.${(el.className || '').toString().slice(0, 50)} +${el.scrollHeight - el.clientHeight}px`);
            }
          }
          return out;
        });
        expect(clipped, `${name} clips content`).toEqual([]);
        await evidence(page, `stage-${size.width}x${size.height}-${name.toLowerCase()}`);
      }
    });
  }

  test('tabs: arrow keys move between sections, the choice survives a reload, Kody clears the dock', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true });
    const tabs = page.getByRole('tablist', { name: 'Dashboard sections' });
    await expect(tabs.getByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'true');
    await tabs.getByRole('tab', { name: 'Overview' }).focus();
    await page.keyboard.press('ArrowRight');
    await expect(tabs.getByRole('tab', { name: 'Departments' })).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('#heat-heading')).toBeVisible();
    await expect(page.locator('#hero-heading')).toBeHidden();
    await page.reload();
    await expect(page.getByRole('tab', { name: 'Departments' })).toHaveAttribute('aria-selected', 'true', { timeout: 15_000 });
    const dock = await page.getByRole('navigation', { name: 'Quick actions' }).boundingBox();
    const kody = await page.getByRole('button', { name: 'Open Kody, HR assistant' }).boundingBox();
    const last = await page.getByRole('navigation', { name: 'Quick actions' }).getByRole('link').last().boundingBox();
    expect(dock && kody && last).toBeTruthy();
    expect(last!.x + last!.width).toBeLessThanOrEqual(kody!.x);
  });

  test('reading an earlier month on the trend swaps the hero figure to it and back', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true });
    const hero = page.locator('section[aria-labelledby="hero-heading"]');
    const figure = hero.locator('.text-display');
    const latest = await figure.innerText();
    await hero.getByRole('img', { name: /Net payroll by month/ }).focus();
    await page.keyboard.press('ArrowLeft');
    await expect(hero.getByText(/Net pay, /)).toBeVisible();
    await expect(figure).not.toHaveText(latest);
    await page.keyboard.press('Escape');
    await expect(figure).toHaveText(latest);
    await expect(hero.getByText(/Net pay, /)).toHaveCount(0);
  });

  /** A pending leave approval as GET /api/approval-requests/{id} returns it. */
  const leaveRequest = (canDecide = true) => ({
    id: 'a0', workflowId: 'w1', entityName: 'LeaveRequest', entityId: 'lr0', title: 'Annual Leave - Raj Krishnamurthy', status: 'Pending', currentStepOrder: 1,
    requestedByUserId: null, requestedForEmployeeId: 7, companyId: null, currentApproverEmployeeId: null, currentApproverUserId: null, currentApproverName: '', currentApproverRole: 'HR Manager',
    currentApproverType: 'Role', currentQueue: 'mine', slaHours: 72, dueAtUtc: null, isOverdue: true, ageHours: 130, lastRoutedAtUtc: null, escalatedAtUtc: null, escalatedToRole: '',
    priority: 'Normal', createdAtUtc: '2026-09-17T00:00:00Z', completedAtUtc: null, decisions: [], canDecide, stepPosition: 1, stepCount: 1,
  });

  test('a leave request is decided in a drawer: rejection needs a reason, nothing is shown as done before the server confirms', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true });
    const posts: string[] = [];
    await page.route('**/api/approval-requests/a0**', (route) => {
      const req = route.request();
      if (req.method() === 'POST') { posts.push(req.postData() ?? ''); return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(leaveRequest()) }); }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(leaveRequest()) });
    });
    await page.getByRole('button', { name: /Review.*Raj Krishnamurthy/ }).click();
    const drawer = page.getByRole('dialog');
    await expect(drawer.getByText('This decision is final and cannot be undone.')).toBeVisible();
    // A rejection without a reason is refused locally: no request leaves the page.
    await drawer.getByRole('button', { name: 'Reject' }).click();
    await expect(drawer.getByRole('alert')).toContainText('at least 5 characters');
    expect(posts).toHaveLength(0);
    await evidence(page, 'stage-leave-drawer');
    await drawer.getByRole('button', { name: 'Approve' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Approved the leave request for Raj Krishnamurthy.' })).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(posts).toHaveLength(1);
    expect(JSON.parse(posts[0])).toMatchObject({ decision: 'Approve' });
    // The decided row is gone, so focus lands on the list heading, not on a removed button.
    await expect(page.locator('#approvals-heading')).toBeFocused();
    // The confirmation floats over the page: it must not add a row to the fixed-height stage.
    const over = await page.evaluate(() => document.documentElement.scrollHeight - window.innerHeight);
    expect(over).toBeLessThanOrEqual(1);
  });

  test('a slow answer for a request the reviewer already left never replaces the request now open, and the decision goes to the one shown', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true });
    let releaseA: () => void = () => {};
    const heldA = new Promise<void>((r) => { releaseA = r; });
    const posts: string[] = [];
    await page.route('**/api/approval-requests/a0**', async (route) => {
      if (route.request().method() === 'POST') { posts.push('a0'); return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }); }
      await heldA; // Raj's request is still loading when the reviewer moves on
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(leaveRequest()) });
    });
    const amira = { ...leaveRequest(), id: 'a1', entityId: 'lr1', title: 'Annual Leave - Amira Mansour', requestedForEmployeeId: 8 };
    await page.route('**/api/approval-requests/a1**', (route) => {
      if (route.request().method() === 'POST') { posts.push('a1'); return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }); }
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(amira) });
    });
    await page.getByRole('button', { name: /Review.*Raj Krishnamurthy/ }).click();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await page.getByRole('button', { name: /Review.*Amira Mansour/ }).click();
    const drawer = page.getByRole('dialog');
    await expect(drawer.getByRole('button', { name: 'Approve' })).toBeVisible();
    releaseA();
    // Give Raj's late answer time to land, then prove it changed nothing.
    await page.waitForTimeout(300);
    await expect(drawer.getByText('Raj Krishnamurthy')).toHaveCount(0);
    await drawer.getByRole('button', { name: 'Approve' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Amira Mansour' })).toBeVisible();
    expect(posts).toEqual(['a1']);
  });

  test('a decision made while a refresh is running still gets its own reload afterwards', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    let fulls = 0;
    let release: () => void = () => {};
    const gate = new Promise<void>((r) => { release = r; });
    await open(page, { rich: true });
    const now = EARLY;
    await page.route('**/api/dashboard/full**', async (route) => {
      fulls += 1;
      if (fulls === 1) await gate; // the manual refresh is held open
      // From the second read on, the decided request has left the queue.
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(dataset(true, now, [], fulls >= 2 ? ['a0'] : [])) });
    });
    await page.route('**/api/approval-requests/a0**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(leaveRequest()) }));
    await page.getByRole('button', { name: /Refresh dashboard/ }).click();
    await expect.poll(() => fulls).toBe(1);
    await page.getByRole('button', { name: /Review.*Raj Krishnamurthy/ }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Approve' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Approved the leave request for Raj Krishnamurthy.' })).toBeVisible();
    release();
    // Two reads in all: the held one, then the follow-up the decision asked for while it was running.
    await expect.poll(() => fulls).toBe(2);
    await expect(page.getByRole('button', { name: /Review.*Raj Krishnamurthy/ })).toHaveCount(0);
  });

  test('a decision the server refuses changes nothing and says so', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true });
    await page.route('**/api/approval-requests/a0**', (route) => route.request().method() === 'POST'
      ? route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ message: 'conflict' }) })
      : route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(leaveRequest()) }));
    await page.getByRole('button', { name: /Review.*Raj Krishnamurthy/ }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Approve' }).click();
    // The server's own sentence is shown, not a generic one.
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('conflict');
    await expect(page.getByRole('status').filter({ hasText: 'Approved the leave request' })).toHaveCount(0);
  });

  test('a refusal with no message falls back to the generic line; Arabic shows messageAr', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true, locale: 'ar' });
    let body: unknown = {};
    await page.route('**/api/approval-requests/a0**', (route) => route.request().method() === 'POST'
      ? route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify(body) })
      : route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(leaveRequest()) }));
    await page.getByRole('button', { name: /Raj Krishnamurthy/ }).first().click();
    const drawer = page.getByRole('dialog');
    const approve = drawer.getByRole('button', { name: 'موافقة' });
    await approve.click();
    await expect(drawer.getByRole('alert')).toContainText('تعذّر حفظ القرار');
    body = { message: 'conflict', messageAr: 'تم تعديل الطلب بالفعل.' };
    await approve.click();
    await expect(drawer.getByRole('alert')).toContainText('تم تعديل الطلب بالفعل.');
  });

  test('a Leave Encashment row is not decided in the leave drawer: it opens the approvals queue', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true, extraQueue: [ENCASHMENT_ROW] });
    await expect(page.getByRole('button', { name: /Review.*Noura Al-Qahtani/ })).toHaveCount(0);
    await expect(page.getByRole('link', { name: /Review.*Noura Al-Qahtani/ })).toHaveAttribute('href', '/approvals');
    // A real leave request on the same list still opens the drawer.
    await expect(page.getByRole('button', { name: /Review.*Raj Krishnamurthy/ })).toBeVisible();
  });

  test('if the loaded request is not a leave request the drawer offers no decision, only the queue', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true });
    // The row said LeaveRequest, but the server's record is a different entity.
    await page.route('**/api/approval-requests/a0**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...leaveRequest(), entityName: 'LeaveEncashmentRequest' }) }));
    await page.getByRole('button', { name: /Review.*Raj Krishnamurthy/ }).click();
    const drawer = page.getByRole('dialog');
    await expect(drawer.getByRole('note')).toContainText('not a leave request');
    await expect(drawer.getByRole('button', { name: 'Approve' })).toHaveCount(0);
    await expect(drawer.getByRole('button', { name: 'Reject' })).toHaveCount(0);
    await expect(drawer.getByRole('link', { name: 'Open in approvals' })).toHaveAttribute('href', '/approvals');
  });

  test('someone who cannot decide sees why, with no Approve or Reject', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true });
    await page.route('**/api/approval-requests/a0**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...leaveRequest(false), decisionBlockedReason: 'You raised this request, so someone else must decide it.' }) }));
    await page.getByRole('button', { name: /Review.*Raj Krishnamurthy/ }).click();
    await expect(page.getByRole('dialog').getByRole('note')).toContainText('someone else must decide');
    await expect(page.getByRole('dialog').getByRole('button', { name: 'Approve' })).toHaveCount(0);
  });

  test('anything that is not a leave request still opens the approvals queue', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true });
    await expect(page.getByRole('button', { name: /Review.*Omar Haddad/ })).toHaveCount(0);
  });

  test('Arabic: the stage mirrors and still fits one screen', async ({ page }) => {
    test.skip(!stageOn(page), 'the stage needs at least 1280 x 740');
    await open(page, { rich: true, locale: 'ar' });
    await expect(page.getByRole('tab', { name: 'الإدارات' })).toBeVisible();
    // The stage's own accessible name is translated, not the English literal.
    await expect(page.locator('[aria-label="مركز قيادة الموارد البشرية"]')).toHaveCount(1);
    await expect(page.locator('[aria-label="HR Command Center"]')).toHaveCount(0);
    const over = await page.evaluate(() => document.documentElement.scrollHeight - window.innerHeight);
    expect(over).toBeLessThanOrEqual(1);
    await evidence(page, 'stage-ar-overview');
  });
});

/**
 * The header clock. THE FIRST LINE OF THE FIRST SCREEN, and it was wrong by seven hours.
 *
 * Observed live at 06:28 Riyadh on Thu 24 Sept 2026, the header read "All companies. Wed, 23 Sept
 * 2026, 23:28" — the WRONG DAY — because /api/tenant-admin/localization answered with the
 * TenantLocalizationSetting entity's America/New_York property default for a tenant that has no
 * localization row. The Saudi Compliance panel on the same page formats in the viewer's own zone
 * and read 24/09/2026 06:29, so the customer saw two clocks a day apart on one screen.
 *
 * Two rules, and the difference between them is the whole fix:
 *   - a tenant that HAS stated a zone is rendered in it, whatever zone the viewer is in;
 *   - a tenant that has NOT is rendered in the VIEWER's zone — never in a US one.
 *
 * EARLY is 02:58 UTC, which is 05:58 on Tue 22 Sept in Riyadh and 22:58 on MON 21 SEPT in New
 * York: the date differs, so a test that confused the two zones cannot pass by coincidence.
 */
const RIYADH_DATE = /Tue, 22 Sept? 2026/;
const NEW_YORK_DATE = /Mon, 21 Sept? 2026/;

function headerLine(page: Page) {
  return page.getByRole('heading', { name: 'HR Command Center' })
    .locator('xpath=following-sibling::p').first();
}

test.describe('HR Command Center: the header clock is the tenant\'s, not a hard-coded zone', () => {
  test.describe('viewer in New York, tenant in Riyadh', () => {
    test.use({ timezoneId: 'America/New_York' });

    test('the stated tenant zone wins over the viewer\'s', async ({ page }) => {
      await open(page, { tenantTz: 'Asia/Riyadh' });
      const line = headerLine(page);
      await expect(line).toContainText(RIYADH_DATE);
      await expect(line).toContainText('05:58');
      await expect(line).not.toContainText(NEW_YORK_DATE);
      await expect(line).not.toContainText('22:58');
    });
  });

  test.describe('viewer in Riyadh, tenant has stated no zone', () => {
    test.use({ timezoneId: 'Asia/Riyadh' });

    test('falls back to the viewer\'s zone, not to US Eastern', async ({ page }) => {
      await open(page, { tenantTz: '' });
      const line = headerLine(page);
      await expect(line).toContainText(RIYADH_DATE);
      await expect(line).toContainText('05:58');
      // The defect exactly: US Eastern put this header on the previous day.
      await expect(line).not.toContainText(NEW_YORK_DATE);
    });
  });

  test.describe('viewer in New York, tenant has stated no zone', () => {
    test.use({ timezoneId: 'America/New_York' });

    test('follows the viewer rather than any pinned zone', async ({ page }) => {
      await open(page, { tenantTz: '' });
      const line = headerLine(page);
      await expect(line).toContainText(NEW_YORK_DATE);
      await expect(line).toContainText('22:58');
    });
  });
});

// A stored snapshot band must not be named when the Saudization screen would withhold it (thresholds not loaded or verified).
test.describe('home Saudization tile follows the Saudization screen', () => {
  for (const locale of ['en', 'ar'] as const) {
    test(`unverified thresholds: the percentage shows and no band is named (${locale})`, async ({ page }) => {
      await open(page, { rich: true, locale });
      const tile = page.getByRole('link').filter({ hasText: locale === 'ar' ? /السعودة/ : /Saudization/ }).first();
      await expect(tile).toBeVisible();
      const text = await tile.innerText();
      expect(text).toContain('38.0%');
      expect(text).not.toMatch(/MediumGreen|Medium Green|Platinum|أخضر|بلاتيني/);
      await expect(tile).toContainText(locale === 'ar' ? 'النطاق غير موثّق' : 'Band not verified');
      await evidence(page, `home-saudization-unverified-${locale}`, true);
    });
  }
});
