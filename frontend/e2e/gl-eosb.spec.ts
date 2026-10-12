import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Setup → GL & rates → End-of-service provision, against synthetic intercepted API responses (no backend,
 * no real tenant). Run from frontend with a dev server up:
 *   npx next dev -p 5541 &
 *   GL_EOSB_BASE_URL=http://127.0.0.1:5541 npx playwright test -c e2e/playwright.gl-eosb.config.ts
 * Proves the UI contract (switch, figures + drill-down, true-up and legacy reclass maker-checker, refusals,
 * empty/error states, Arabic), not server authorization.
 */

const companySa = {
  id: 'co-sa', legalNameEn: 'Masar Trading KSA', legalNameAr: 'مسار للتجارة', tradeName: 'Masar KSA',
  countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active',
};

interface Options {
  locale?: 'en' | 'ar';
  provisioningOn?: boolean;
  provisionDown?: boolean;
  legacyEmpty?: boolean;
  /** POST legacy reclass answers 422 gl_period_closed. */
  closedPeriod?: boolean;
}
interface Recorded { method: string; path: string; search: string; body: unknown }

const adjustment = (over: Record<string, unknown>) => ({
  companyId: 'co-sa', kind: 'EosbTrueUp', status: 'Pending', period: '2026-09', accountingDate: '2026-09-30', amount: 1250.5,
  currency: 'SAR', sourceAccount: '5110 - End of Service Benefit Expense', targetAccount: '2310 - End of Service Benefit Provision',
  reason: 'Actuarial valuation FY2026', reportReference: 'ACT-2026-09', requestedByName: 'Layla Haddad',
  requestedAtUtc: '2026-10-09T09:00:00Z', decidedByName: null, decidedAtUtc: null, decisionNote: null,
  requestedByMe: false, canApprove: true, ...over,
});

async function boot(page: Page, options: Options = {}) {
  const writes: Recorded[] = [];
  const errors: string[] = [];
  const state = { on: !!options.provisioningOn };
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'gl-eosb-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });

  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    const body = (() => {
      try { return request.postDataJSON(); } catch { return request.postData(); }
    })();
    if (method !== 'GET') writes.push({ method, path: p, search: url.search, body });

    if (p === '/api/auth/me') {
      return reply({
        id: 'user-maker', tenantId: 'gl-tenant', tenantSlug: 'gl-fixture', fullName: 'Omar Maker', roles: ['Admin'],
        permissions: ['organization.read', 'organization.write', 'dashboard.read', 'finance.gl.read', 'finance.gl.manage', 'payroll.rates.read', 'payroll.rates.manage'],
        accountType: 'SingleCompany', isGroupScope: false,
        companies: [{ id: 'co-sa', name: companySa.legalNameEn, code: 'co-sa', countryCode: 'SA', isActive: true }],
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);
    if (p === '/api/companies') return reply(paged([companySa]));

    // ── End-of-service provision ──
    if (p === '/api/finance/gl/eosb/provision') {
      if (options.provisionDown) return reply({ code: 'internal_error', message: 'The provision service is unavailable.', traceId: 't-1' }, 500);
      return reply({
        companyId: 'co-sa', provisionAccount: '2310 - End of Service Benefit Provision', expenseAccount: '5110 - End of Service Benefit Expense',
        provisioning: {
          enabled: state.on, value: state.on ? 'StatutoryMonthly' : 'Off', effectiveFrom: state.on ? '2026-06-01' : null,
          rateKey: 'payparameter.eosb_provisioning', currentRateId: state.on ? 'rate-eosb' : null,
          latestValue: state.on ? 'StatutoryMonthly' : null, latestEffectiveFrom: state.on ? '2026-06-01' : null,
          inheritedFromTenant: false, scenario: 'Employer termination',
        },
        glBalance: 16250.5, employeeObligation: 15000, employeeProvisionCarried: 15000, trueUpLayer: 1250.5,
        employees: state.on ? [
          { employeeId: 1, employeeCode: 'E001', employeeName: 'Sara Al-Qahtani', lastMeasuredPeriod: '2026-08', obligation: 15000,
            lastMovement: 416.67, wageBase: 10000, serviceYears: 3.17, rule: 'KSA-LaborLaw-Art84', provisionCarried: 15000 },
        ] : [],
        history: [],
        trueUps: [
          adjustment({ id: 'adj-other' }),
          adjustment({ id: 'adj-mine', amount: -300, reportReference: 'ACT-2', requestedByName: 'Omar Maker', requestedByMe: true, canApprove: false }),
          adjustment({ id: 'adj-done', status: 'Approved', amount: 900, reportReference: 'ACT-1', decidedByName: 'Layla Haddad', decidedAtUtc: '2026-09-01T10:00:00Z', canApprove: false }),
        ],
      });
    }
    if (p === '/api/finance/gl/eosb/true-up' && method === 'POST') return reply(adjustment({ id: 'adj-new', requestedByMe: true, canApprove: false }));
    if (p === '/api/finance/gl/reclassifications/legacy-absence' && method === 'GET') {
      return reply({
        companyId: 'co-sa',
        accounts: options.legacyEmpty ? [] : [
          { account: '2104 - Attendance Adjustment Payable', code: '2104', debits: 0, credits: 1200, balance: 1200,
            suggestedTarget: '5008 - Attendance & Late Deductions (contra)', pendingRequestId: null },
          { account: '2105 - Leave Deduction Payable', code: '2105', debits: 0, credits: 300, balance: 300,
            suggestedTarget: '5009 - Unpaid Leave & LOP Deductions (contra)', pendingRequestId: 'adj-leave' },
        ],
        unattributed: options.legacyEmpty ? [] : [{ account: '2104 - Attendance Adjustment Payable', code: '2104', balance: 50 }],
        requests: options.legacyEmpty ? [] : [adjustment({
          id: 'adj-leave', kind: 'LegacyAbsenceReclass', amount: 300, sourceAccount: '2105 - Leave Deduction Payable',
          targetAccount: '5009 - Unpaid Leave & LOP Deductions (contra)', accountingDate: '2026-10-10', reportReference: null,
          reason: 'Not owed to anyone', requestedByName: 'Omar Maker', requestedByMe: true, canApprove: false,
        })],
      });
    }
    if (p === '/api/finance/gl/reclassifications/legacy-absence' && method === 'POST') {
      if (options.closedPeriod) {
        return reply({ error: 'gl_period_closed', message: 'The accounting date falls in 2026-09, which is closed. Choose a date in an open period.' }, 422);
      }
      return reply(adjustment({ id: 'adj-reclass', kind: 'LegacyAbsenceReclass', requestedByMe: true, canApprove: false }));
    }
    if (p.startsWith('/api/finance/gl/adjustments/')) return reply({ ok: true });
    if (p.startsWith('/api/finance/rates/company') && method !== 'GET') {
      state.on = (body as { rateValue?: string })?.rateValue === 'StatutoryMonthly';
      return reply({ id: 'rate-eosb', ...(body as object) });
    }

    // ── The rest of GL & rates (the default sub-tab loads before ours is chosen) ──
    if (p === '/api/finance/gl/resolve') return reply({ companyId: 'co-sa', rows: [] });
    if (p === '/api/finance/gl/control-accounts/health') return reply({ generatedAtUtc: '2026-10-10T08:00:00Z', companyId: 'co-sa', healthy: true, violations: [], accounts: [] });
    if (p.startsWith('/api/finance/')) return reply([]);
    if (p === '/api/organization/cost-centers' || p === '/api/grades' || p === '/api/branches' || p === '/api/departments') return reply(paged([]));
    if (p.startsWith('/api/admin/') || p.includes('country-packs') || p.startsWith('/api/statutory-rules')) return reply([]);
    return reply(paged([]));
  });

  await page.goto('/setup?tab=glMapping');
  const ar = options.locale === 'ar';
  await expect(page.getByRole('heading', { level: 1, name: ar ? 'الإعداد والإدارة' : 'Setup & Administration', exact: true })).toBeVisible({ timeout: 60_000 });
  await page.getByRole('tab', { name: ar ? 'مخصص مكافأة نهاية الخدمة' : 'End-of-service provision', exact: true }).click();
  return { writes, errors };
}

/** GL_EOSB_SHOTS_DIR, when set, receives review screenshots. */
async function shot(page: Page, name: string) {
  const dir = process.env.GL_EOSB_SHOTS_DIR;
  if (!dir) return;
  await page.screenshot({ path: `${dir}/${name}.png`, fullPage: true, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
}

const provision = (page: Page) => page.getByRole('region', { name: 'End-of-service provision', exact: true });
const legacy = (page: Page) => page.getByRole('region', { name: 'Move balances booked under the old absence-deduction accounts', exact: true });

test('figures carry their definitions, drill down to employees, and Approve shows only where the server allows it', async ({ page }) => {
  const s = await boot(page, { provisioningOn: true });
  const p = provision(page);
  await expect(p.getByText('Monthly provisioning is on for Masar Trading KSA since', { exact: false })).toBeVisible();
  const figures = p.getByTestId('eosb-figures');
  await expect(figures).toContainText('Provision in the books');
  await expect(figures).toContainText('16,250.50');
  await expect(figures).toContainText('Credits minus debits on 2310 - End of Service Benefit Provision');
  await expect(figures).toContainText('Actuarial adjustments');

  await p.getByRole('button', { name: 'Show the 1 employee behind these figures', exact: true }).click();
  const employees = p.getByTestId('eosb-employees');
  await expect(employees).toContainText('E001');
  await expect(employees).toContainText('Sara Al-Qahtani');
  await expect(employees).toContainText('2026-08');

  const other = p.getByTestId('adjustment-adj-other');
  await expect(other).toContainText('Waiting for approval');
  await expect(other).toContainText('Raise the provision by');
  await expect(other.getByRole('button', { name: 'Approve and post', exact: true })).toBeVisible();
  const mine = p.getByTestId('adjustment-adj-mine');
  await expect(mine).toContainText('Lower the provision by');
  await expect(mine).toContainText('You requested this, so someone else must approve it.');
  await expect(mine.getByRole('button')).toHaveCount(0);
  await expect(p.getByTestId('adjustment-adj-done')).toContainText('Approved and posted');
  await expect(p.getByTestId('adjustment-adj-done').getByRole('button')).toHaveCount(0);

  await other.getByRole('button', { name: 'Approve and post', exact: true }).click();
  await expect.poll(() => s.writes.some((w) => w.path === '/api/finance/gl/adjustments/adj-other/approve')).toBe(true);

  // Reject needs a reason before it can be sent.
  await other.getByRole('button', { name: 'Reject', exact: true }).click();
  const confirm = other.getByRole('button', { name: 'Confirm rejection', exact: true });
  await expect(confirm).toBeDisabled();
  await other.getByLabel('Why reject it? (required)').fill('Report not signed');
  await confirm.click();
  await expect.poll(() => s.writes.find((w) => w.path === '/api/finance/gl/adjustments/adj-other/reject')?.body).toEqual({ note: 'Report not signed' });
  await shot(page, 'eosb-on');
  expect(s.errors).toEqual([]);
});

test('switching provisioning on writes the company rate from the first of the chosen month', async ({ page }) => {
  const s = await boot(page);
  const p = provision(page);
  await expect(p.getByText('Monthly provisioning is off for Masar Trading KSA.', { exact: false })).toBeVisible();
  await expect(p.getByTestId('eosb-no-employees')).toContainText('No employee has been measured yet.');
  await p.getByTestId('eosb-switch-month').fill('2026-11');
  await p.getByRole('button', { name: 'Start monthly provisioning', exact: true }).click();
  await expect(p.getByTestId('eosb-notice')).toContainText('Monthly provisioning will start from 2026-11.');
  const write = s.writes.find((w) => w.path === '/api/finance/rates/company');
  expect(write?.method).toBe('POST');
  expect(write?.search).toContain('companyId=co-sa');
  expect(write?.body).toEqual({ rateKey: 'payparameter.eosb_provisioning', rateValue: 'StatutoryMonthly', effectiveFrom: '2026-11-01' });
  await expect(p.getByRole('button', { name: 'Stop monthly provisioning', exact: true })).toBeVisible();
});

test('a true-up is requested with its report reference and is only sent when complete', async ({ page }) => {
  const s = await boot(page, { provisioningOn: true });
  const form = provision(page).getByTestId('eosb-trueup-form');
  const send = form.getByRole('button', { name: 'Request adjustment', exact: true });
  await expect(send).toBeDisabled();
  await form.getByLabel('Month').fill('2026-09');
  await form.getByLabel('Amount').fill('-400.25');
  await form.getByLabel('Actuarial report reference').fill('ACT-2026-09');
  await expect(send).toBeDisabled();
  await form.getByLabel('Reason').fill('Lower discount-rate effect');
  await send.click();
  await expect(provision(page).getByTestId('eosb-notice')).toContainText('A second person must approve it before it posts.');
  expect(s.writes.find((w) => w.path === '/api/finance/gl/eosb/true-up')?.body).toEqual({
    companyId: 'co-sa', period: '2026-09', amount: -400.25, reportReference: 'ACT-2026-09', reason: 'Lower discount-rate effect',
  });
});

test('old 2104/2105 balances: a move is requested in plain words, and a closed period is refused verbatim', async ({ page }) => {
  const s = await boot(page, { closedPeriod: true });
  const l = legacy(page);
  await expect(l.getByTestId('legacy-2104')).toContainText('still recorded as owed');
  await expect(l.getByTestId('legacy-2104')).toContainText('Suggested destination: 5008 - Attendance & Late Deductions (contra)');
  await expect(l.getByTestId('legacy-2105')).toContainText('A move is waiting for approval');
  await expect(l.getByTestId('legacy-2105').getByRole('button')).toHaveCount(0);
  await expect(l.getByTestId('legacy-unattributed')).toContainText('without a company');

  await l.getByTestId('legacy-2104').getByRole('button', { name: 'Move this balance', exact: true }).click();
  const form = l.getByTestId('legacy-form');
  await form.getByLabel('Accounting date').fill('2026-09-30');
  await form.getByLabel('Reason').fill('Absence deductions are not owed');
  await form.getByRole('button', { name: 'Request the move', exact: true }).click();
  await expect(l.getByTestId('legacy-error')).toHaveText('The accounting date falls in 2026-09, which is closed. Choose a date in an open period.');
  expect(s.writes.find((w) => w.path === '/api/finance/gl/reclassifications/legacy-absence')?.body).toEqual({
    companyId: 'co-sa', sourceAccount: '2104 - Attendance Adjustment Payable', accountingDate: '2026-09-30',
    targetAccountCode: null, reason: 'Absence deductions are not owed',
  });
});

test('a failed load shows the reason and Try again; empty legacy accounts say there is nothing to move', async ({ page }) => {
  await boot(page, { provisionDown: true, legacyEmpty: true });
  const p = provision(page);
  await expect(p.getByTestId('eosb-load-error')).toContainText('The end-of-service provision could not be loaded.');
  await expect(p.getByRole('button', { name: 'Try again', exact: true })).toBeVisible();
  await expect(p.getByTestId('eosb-figures')).toHaveCount(0);
  await expect(legacy(page).getByTestId('legacy-empty')).toContainText('Nothing to move');
});

test('the panel reads in Arabic, right to left, with no serious accessibility violations', async ({ page }) => {
  await boot(page, { locale: 'ar', provisioningOn: true });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const p = page.getByRole('region', { name: 'مخصص مكافأة نهاية الخدمة', exact: true });
  await expect(p.getByText('المخصص المسجّل في الدفاتر')).toBeVisible();
  await expect(p.getByRole('button', { name: 'اعتماد وترحيل', exact: true })).toBeVisible();
  await expect(page.getByRole('region', { name: 'نقل الأرصدة المسجّلة في حسابات خصومات الغياب القديمة', exact: true })).toBeVisible();
  await shot(page, 'eosb-ar');
  const results = await new AxeBuilder({ page }).include('[data-testid="eosb-panel"]').include('[data-testid="legacy-absence"]').analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`)).toEqual([]);
});
