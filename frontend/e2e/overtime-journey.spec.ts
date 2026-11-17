import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { translate } from '../src/i18n/translations';

/**
 * Overtime, fixture lane (e2e/playwright.fixture.config.ts), English and Arabic, every project (desktop and phone included).
 *
 * The journey: an employee files overtime, HR gives the final approval, and the approved amount is in Payroll Review. Then
 * what each role is offered: nothing the API refuses (OvertimeController + the seeded permissions) is on the screen.
 * Every API call is intercepted; the mock keeps the request's state across the persona switch.
 */

type Locale = 'en' | 'ar';
type Persona = { roles: string[]; permissions: string[]; employeeId?: number };

const EMPLOYEE: Persona = { roles: ['Employee'], permissions: ['dashboard.read', 'ess.read', 'ess.write', 'overtime.read'], employeeId: 7 };
const HR_MANAGER: Persona = {
  roles: ['HR Manager'],
  permissions: ['dashboard.read', 'employees.read', 'overtime.read', 'overtime.write', 'overtime.approve', 'overtime.policy_manage', 'payroll.read'],
};
const MANAGER: Persona = { roles: ['Manager'], permissions: ['dashboard.read', 'overtime.read', 'overtime.approve', 'ess.read', 'ess.write'], employeeId: 3 };
const MANAGER_TEAM_FILING: Persona = { ...MANAGER, permissions: [...MANAGER.permissions, 'overtime.file_for_team'] };
const SUPERVISOR: Persona = { roles: ['Supervisor'], permissions: ['dashboard.read', 'overtime.read', 'ess.read', 'ess.write'], employeeId: 4 };

const WORK_DATE = new Date().toISOString().slice(0, 10);

function overtimeRequest(status: string, over: Record<string, unknown> = {}) {
  return {
    id: 'ot-1', tenantId: 't1', employeeId: 7, employeeName: 'Sara Ali', overtimePolicyId: 'p1', overtimeTypeId: null,
    workDate: WORK_DATE, startTimeUtc: `${WORK_DATE}T15:00:00Z`, endTimeUtc: `${WORK_DATE}T17:00:00Z`,
    requestedMinutes: 120, approvedMinutes: status === 'Approved' ? 120 : 0, source: 'Manual', reason: 'Month-end close', status,
    createdAtUtc: `${WORK_DATE}T18:00:00Z`, ...over,
  };
}

async function boot(page: Page, locale: Locale, persona: { current: Persona }) {
  const state = { request: null as ReturnType<typeof overtimeRequest> | null, created: [] as unknown[], approvals: [] as unknown[] };
  await page.addInitScript((lang) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone.theme', 'light');
    localStorage.setItem('kynexone-locale-choice-v2', lang);
  }, locale);
  await page.route('**/api/**', (route) => {
    const req = route.request();
    const url = new URL(req.url());
    const p = url.pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    const who = persona.current;
    if (p === '/api/auth/me') {
      return json({
        id: 'u1', tenantId: 't1', tenantSlug: 'acme', email: 'user@acme.test', fullName: 'Fixture User', accountType: 'Tenant',
        isGroupScope: true, companies: [], roles: who.roles, permissions: who.permissions, employeeId: who.employeeId,
      });
    }
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });
    if (p === '/api/overtime/reports/summary') {
      const approved = state.request?.status === 'Approved';
      return json({ totalRequests: state.request ? 1 : 0, approvedRequests: approved ? 1 : 0, pendingRequests: state.request && !approved ? 1 : 0, approvedHours: approved ? 2 : 0, payrollAmount: approved ? 175 : 0 });
    }
    if (p === '/api/overtime/policies') return json([{ id: 'p1', code: 'STD', name: 'Standard', isActive: true, hourlyRateBasis: 'BasicSalary' }]);
    if (p === '/api/overtime/requests' && req.method() === 'POST') {
      state.created.push(req.postDataJSON());
      state.request = overtimeRequest('PendingHR');
      return json(state.request, 201);
    }
    if (p === '/api/overtime/requests') {
      const status = url.searchParams.get('status');
      const items = state.request && (!status || status === state.request.status) ? [state.request] : [];
      return json({ items, total: items.length, page: 1, pageSize: Number(url.searchParams.get('pageSize') ?? 100) });
    }
    if (p === '/api/overtime/requests/ot-1/approve' && req.method() === 'POST') {
      state.approvals.push(req.postDataJSON());
      state.request = overtimeRequest('Approved');
      return json({ id: 'calc-1', overtimeRequestId: 'ot-1', employeeId: 7, approvedMinutes: 120, hourlyRate: 62.5, multiplier: 1.5, amount: 175, currency: 'SAR' });
    }
    if (p === '/api/overtime/payroll-review') {
      return json(state.request?.status === 'Approved'
        ? [{ id: 'imp-1', overtimeRequestId: 'ot-1', employeeId: 7, payrollRunId: null, minutes: 120, hours: 2, amount: 175, status: 'PendingPayroll', createdAtUtc: `${WORK_DATE}T18:05:00Z` }]
        : []);
    }
    if (p === '/api/overtime/comp-off-conversions' || p === '/api/overtime/calculations' || p === '/api/overtime/types') return json([]);
    return json({ items: [], total: 0 });
  });
  return state;
}

const tr = (locale: Locale, key: string, params?: Record<string, string | number>) => translate(locale, key, params);

async function openTab(page: Page, locale: Locale, tab: string) {
  await page.getByRole('button', { name: tr(locale, tab), exact: true }).click();
}

async function seriousAxe(page: Page) {
  const results = await new AxeBuilder({ page }).include('main').analyze();
  return results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => v.id);
}

for (const locale of ['en', 'ar'] as const) {
  test.describe(`overtime journey (${locale})`, () => {
    test('an employee files, HR approves, and the amount is in Payroll Review', async ({ page }) => {
      const persona = { current: EMPLOYEE };
      const state = await boot(page, locale, persona);
      await page.goto('/overtime');
      await expect(page.getByRole('heading', { name: tr(locale, 'Overtime Management') })).toBeVisible({ timeout: 60_000 });
      if (locale === 'ar') await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');

      // 1. The employee files for themselves: no employee picker is offered.
      await openTab(page, locale, 'Submit OT');
      await expect(page.getByText(tr(locale, 'Submitting overtime for: Employee #{id}', { id: 7 }))).toBeVisible();
      await page.getByLabel(tr(locale, 'Start time'), { exact: true }).fill('18:00');
      await page.getByLabel(tr(locale, 'End time'), { exact: true }).fill('20:00');
      await page.getByPlaceholder(tr(locale, 'Reason for overtime…')).fill('Month-end close');
      expect(await seriousAxe(page)).toEqual([]);
      await page.getByRole('button', { name: tr(locale, 'Submit OT Request') }).click();
      await expect(page.getByText(tr(locale, 'Overtime Request Submitted'))).toBeVisible();
      expect(state.created).toHaveLength(1);
      expect(state.created[0]).toMatchObject({ employeeId: 7, workDate: WORK_DATE, reason: 'Month-end close' });

      // 2. HR gives the final approval (the manager step is mocked as done: the request is at PendingHR).
      persona.current = HR_MANAGER;
      await page.reload();
      await openTab(page, locale, 'Approvals');
      await expect(page.getByText('Sara Ali')).toBeVisible();
      expect(await seriousAxe(page)).toEqual([]);
      await page.getByRole('button', { name: tr(locale, 'Approve'), exact: true }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await dialog.getByRole('button', { name: tr(locale, 'Approve & Calculate') }).click();
      await expect(dialog).toHaveCount(0);
      expect(state.approvals).toEqual([{ approvedMinutes: 120 }]);

      // 3. The approved amount waits in Payroll Review.
      await openTab(page, locale, 'Payroll Review');
      // The amount in the reader's digits (Latin in English, Arabic-Indic possibly in Arabic).
      const reviewRow = page.getByRole('row').filter({ hasText: /175[.,٫]00|١٧٥٫٠٠/ });
      await expect(reviewRow).toBeVisible();
      expect(await seriousAxe(page)).toEqual([]);
      expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(1);
    });

    test('each role is offered only what the API lets it do', async ({ page }) => {
      const persona = { current: MANAGER };
      const state = await boot(page, locale, persona);
      state.request = overtimeRequest('PendingHR');
      await page.goto('/overtime');
      await expect(page.getByRole('heading', { name: tr(locale, 'Overtime Management') })).toBeVisible({ timeout: 60_000 });

      // A line manager without overtime.write: Detect resolves to it on the API, so it is not offered; a step-2 row is HR's.
      await openTab(page, locale, 'Team OT');
      await expect(page.getByText('Sara Ali')).toBeVisible();
      await expect(page.getByRole('button', { name: tr(locale, 'Detect from Attendance') })).toHaveCount(0);
      await expect(page.getByRole('row').filter({ hasText: 'Sara Ali' }).getByRole('button', { name: tr(locale, 'Approve'), exact: true })).toHaveCount(0);
      // Filing for someone else needs overtime.write or the team key: without either, no picker.
      await openTab(page, locale, 'Submit OT');
      await expect(page.getByText(tr(locale, 'Submitting overtime for: Employee #{id}', { id: 3 }))).toBeVisible();

      // With the team key (company setting on), the picker is offered.
      persona.current = MANAGER_TEAM_FILING;
      await page.reload();
      await openTab(page, locale, 'Submit OT');
      await expect(page.getByPlaceholder(tr(locale, 'Search by name or employee code…'))).toBeVisible();

      // A manager decides step 1.
      state.request = overtimeRequest('PendingManager');
      await openTab(page, locale, 'Team OT');
      await expect(page.getByRole('row').filter({ hasText: 'Sara Ali' }).getByRole('button', { name: tr(locale, 'Approve'), exact: true })).toBeVisible();

      // A Supervisor holds no overtime.approve: no decision buttons at all.
      persona.current = SUPERVISOR;
      await page.reload();
      await openTab(page, locale, 'Team OT');
      await expect(page.getByText('Sara Ali')).toBeVisible();
      await expect(page.getByRole('button', { name: tr(locale, 'Approve'), exact: true })).toHaveCount(0);
      await expect(page.getByRole('button', { name: tr(locale, 'Reject'), exact: true })).toHaveCount(0);
      expect(await seriousAxe(page)).toEqual([]);
    });
  });
}
