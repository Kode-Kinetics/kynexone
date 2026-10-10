import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Request, type TestInfo } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Setup → GL & rates, against synthetic intercepted API responses (no backend, no real tenant).
 * Run from frontend with a dev server up:
 *   npx next dev -p 5196 &
 *   GL_SETUP_BASE_URL=http://127.0.0.1:5196 npx playwright test -c e2e/playwright.gl-setup.config.ts
 * Proves the UI contract (states, wiring, confirmation, i18n), not server authorization.
 * GL_SHOTS_DIR, when set, also receives the review screenshots.
 */

const year = new Date().getFullYear();
const iso = (y: number, m: number, d: number) => `${y}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`;

const companySa = {
  id: 'co-sa', legalNameEn: 'Masar Trading KSA', legalNameAr: 'مسار للتجارة', tradeName: 'Masar KSA',
  countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active',
};
const companyAe = {
  id: 'co-ae', legalNameEn: 'Masar Services UAE', legalNameAr: '', tradeName: 'Masar UAE',
  countryCode: 'AE', jurisdiction: 'UAE', defaultCurrency: 'AED', isActive: true, approvalStatus: 'Active',
};

interface GlOptions {
  groupMode?: boolean;
  locale?: 'en' | 'ar';
  /** GET /drivers fails with a 500 until the test sets `control.driversDown = false`. */
  driversDown?: boolean;
}

interface Recorded { method: string; path: string; search: string; body: unknown }

const groupAccounts = [
  { id: 'acc-5001', companyId: null, code: '5001', name: 'Basic Salary Expense', accountType: 'Expense', isActive: true },
  { id: 'acc-5002', companyId: null, code: '5002', name: 'Housing Allowance Expense', accountType: 'Expense', isActive: true },
  { id: 'acc-2100', companyId: null, code: '2100', name: 'Salaries Payable', accountType: 'Liability', isActive: true },
  { id: 'acc-1400', companyId: null, code: '1400', name: 'Employee Loans Receivable', accountType: 'Asset', isActive: true },
];
const companyAccounts = [
  { id: 'acc-5090', companyId: 'co-sa', code: '5090', name: 'Riyadh Site Allowance', accountType: 'Expense', isActive: true },
];

function drivers(companyId: string | null) {
  return [
    {
      id: 'drv-basic', companyId: null, key: 'EARN:BASIC', label: 'Basic salary', category: 'Earning', postingSide: 'DR', accountType: 'Expense',
      defaultCode: '5001', defaultName: 'Basic Salary Expense', matchSource: null, matchMode: 'Exact', matchComponentCode: 'BASIC',
      emitsEmployerExpensePair: false, pairedExpenseDriverKey: null, isSystem: true, isActive: true, sortOrder: 10, inherited: companyId !== null,
    },
    {
      id: 'drv-loan', companyId: null, key: 'LOAN_RECEIVABLE', label: 'Loan recovery', category: 'Balancing', postingSide: 'CR', accountType: 'Asset',
      defaultCode: '1499', defaultName: 'Old Loans Account', matchSource: 'Loan', matchMode: 'Any', matchComponentCode: null,
      emitsEmployerExpensePair: false, pairedExpenseDriverKey: null, isSystem: true, isActive: true, sortOrder: 20, inherited: companyId !== null,
    },
  ];
}

function resolveRows() {
  return {
    companyId: null,
    rows: [
      {
        driverKey: 'EARN:BASIC', label: 'Basic salary', category: 'Earning', side: 'DR', componentCodes: ['BASIC'],
        account: { id: 'acc-5001', code: '5001', name: 'Basic Salary Expense' }, source: 'GroupMapping', existsInChart: true, isActive: true, problem: null,
      },
      {
        driverKey: 'LOAN_RECEIVABLE', label: 'Loan recovery', category: 'Balancing', side: 'CR', componentCodes: [],
        account: { id: null, code: '1499', name: 'Old Loans Account' }, source: 'GroupDriverDefault', existsInChart: false, isActive: true, problem: 'NotInChart',
      },
    ],
  };
}

const health = {
  generatedAtUtc: '2026-10-10T08:00:00Z', companyId: null, healthy: false,
  violations: ['Bonus Payable carries a debit balance; a liability must not be negative.'],
  accounts: [
    { driver: 'BONUS_PAYABLE', account: '2300 - Bonus Payable', nature: 'Liability', scopedBalance: -50, tenantBalance: -50, isHealthy: false, violation: 'Bonus Payable carries a debit balance; a liability must not be negative.' },
    { driver: 'LOAN_RECEIVABLE', account: '1400 - Employee Loans Receivable', nature: 'Asset', scopedBalance: 10, tenantBalance: 10, isHealthy: true, violation: null },
  ],
};

const registry = [
  { rateKey: 'allowance.site', rateCategory: 'Allowance', dataType: 'decimal', unit: 'amount', minValue: 0, maxValue: 100000, description: 'Site allowance' },
  { rateKey: 'ot.weekend_multiplier', rateCategory: 'PayParameter', dataType: 'decimal', unit: 'multiplier', minValue: 1, maxValue: 3, description: 'Weekend overtime multiplier' },
];

function companyRates(companyId: string | null) {
  const group = [
    { id: 'rate-g-weekend', companyId: null, rateKey: 'ot.weekend_multiplier', rateCategory: 'PayParameter', rateValue: '1.5', dataType: 'decimal', unit: 'multiplier', effectiveFrom: iso(year - 1, 1, 1), effectiveTo: null, status: 'Active', notes: '', inEffect: true, label: 'Weekend overtime multiplier' },
  ];
  if (!companyId) return group;
  return [
    ...group.map((r) => ({ ...r, inherited: true })),
    { id: 'rate-c-site-now', companyId, rateKey: 'allowance.site', rateCategory: 'Allowance', rateValue: '750', dataType: 'decimal', unit: 'amount', currency: 'SAR', effectiveFrom: iso(year, 1, 1), effectiveTo: null, status: 'Active', notes: 'Approved by CFO', inEffect: true, label: 'Site allowance' },
    { id: 'rate-c-site-old', companyId, rateKey: 'allowance.site', rateCategory: 'Allowance', rateValue: '500', dataType: 'decimal', unit: 'amount', currency: 'SAR', effectiveFrom: iso(year - 1, 1, 1), effectiveTo: iso(year - 1, 12, 31), status: 'Superseded', notes: '', inEffect: false, label: 'Site allowance' },
    { id: 'rate-c-site-next', companyId, rateKey: 'allowance.site', rateCategory: 'Allowance', rateValue: '800', dataType: 'decimal', unit: 'amount', currency: 'SAR', effectiveFrom: iso(year + 1, 1, 1), effectiveTo: null, status: 'Active', notes: '', inEffect: false, label: 'Site allowance' },
  ];
}

interface Pending {
  id: string; companyId: string; companyName: string; ruleKey: string; countryCode: string; value: string; effectiveFrom: string;
  effectiveTo: string | null; reason: string; reviewBy: string; requestedById: string; requestedByName: string; requestedAt: string; canApprove: boolean;
}

async function boot(page: Page, options: GlOptions = {}) {
  const writes: Recorded[] = [];
  const reads: Recorded[] = [];
  const errors: string[] = [];
  const control = { driversDown: !!options.driversDown };
  // Server-side pending overrides: they survive a page reload because this closure does.
  const pending: Pending[] = [
    {
      id: 'ovr-other', companyId: 'co-ae', companyName: 'Masar Services UAE', ruleKey: 'ot.standard_multiplier', countryCode: 'AE', value: '1.6',
      effectiveFrom: iso(year, 11, 1), effectiveTo: null, reason: 'Board resolution 14', reviewBy: iso(year + 1, 6, 30),
      requestedById: 'user-layla', requestedByName: 'Layla Haddad', requestedAt: '2026-10-09T09:30:00Z', canApprove: true,
    },
  ];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'gl-browser-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });

  await page.route('**/api/**', async (route) => {
    const request: Request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const companyId = url.searchParams.get('companyId');
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    const body = (() => {
      try { return request.postDataJSON(); } catch { return request.postData(); }
    })();
    const rec = { method, path: p, search: url.search, body };
    if (method === 'GET') reads.push(rec); else writes.push(rec);

    if (p === '/api/auth/me') {
      return reply({
        id: 'user-maker', tenantId: 'gl-tenant', tenantSlug: 'gl-fixture', fullName: 'Omar Maker', roles: ['Admin'],
        permissions: ['organization.read', 'organization.write', 'dashboard.read', 'finance.gl.read', 'finance.gl.manage', 'finance.gl.drivers.manage',
          'finance.gl.drivers.author_predicates', 'payroll.rates.read', 'payroll.rates.manage', 'payroll.rates.statutory_override'],
        accountType: options.groupMode ? 'Group' : 'SingleCompany',
        isGroupScope: !!options.groupMode,
        companies: (options.groupMode ? [companySa, companyAe] : [companySa]).map((c) => ({ id: c.id, name: c.legalNameEn, code: c.id, countryCode: c.countryCode, isActive: true })),
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);
    if (p === '/api/companies') return reply(paged(options.groupMode ? [companySa, companyAe] : [companySa]));

    // ── GL ──
    if (p === '/api/finance/gl/accounts' && method === 'GET') return reply(companyId ? companyAccounts : groupAccounts);
    if (p === '/api/finance/gl/accounts' && method === 'POST') return reply({ id: 'acc-new', companyId, ...(body as object), isActive: true });
    if (p === '/api/finance/gl/accounts/export') return route.fulfill({ status: 200, contentType: 'text/csv', body: 'code,name,type\n5001,Basic Salary Expense,Expense\n' });
    if (p === '/api/finance/gl/accounts/import') {
      const dryRun = url.searchParams.get('dryRun') === 'true';
      return reply({ dryRun, created: 2, updated: 1, unchanged: 3, errors: [] });
    }
    if (p.startsWith('/api/finance/gl/accounts/')) return method === 'DELETE' ? route.fulfill({ status: 204 }) : reply({ ...(body as object) });
    if (p === '/api/finance/gl/mappings' && method === 'GET') {
      return reply([
        { driverKey: 'EARN:BASIC', label: 'Basic salary', category: 'Earning', accountType: 'Expense', defaultAccount: '5001 — Basic Salary Expense', mappedAccountId: 'acc-5001', segmentCostCenterId: null, inherited: companyId !== null },
        { driverKey: 'LOAN_RECEIVABLE', label: 'Loan recovery', category: 'Balancing', accountType: 'Asset', defaultAccount: '1499 — Old Loans Account', mappedAccountId: null, segmentCostCenterId: null, inherited: companyId !== null },
      ]);
    }
    if (p === '/api/finance/gl/mappings') return reply({ count: 1, changed: 1, removed: 0 });
    if (p === '/api/finance/gl/drivers' && method === 'GET') {
      if (control.driversDown) return reply({ code: 'internal_error', message: 'boom', traceId: 'trace-1' }, 500);
      return reply(drivers(companyId));
    }
    if (p.startsWith('/api/finance/gl/drivers/')) return reply({ ...(body as object) });
    if (p === '/api/finance/gl/resolve') return reply(resolveRows());
    if (p === '/api/finance/gl/control-accounts/health') return reply(health);
    if (p === '/api/finance/gl/seed-defaults') return reply({ accounts: 4, mappings: 2, drivers: 3, rateDefinitions: 1 });

    // ── Rates ──
    if (p === '/api/finance/rates/company/registry') return reply(registry);
    if (p === '/api/finance/rates/company' && method === 'GET') return reply(companyRates(companyId));
    if (p.startsWith('/api/finance/rates/company')) return method === 'DELETE' ? route.fulfill({ status: 204 }) : reply({ ...(body as object) });
    if (p === '/api/finance/rates/statutory/pending') return reply(pending);
    if (p === '/api/finance/rates/statutory' && method === 'GET') {
      const cc = url.searchParams.get('countryCode') ?? url.searchParams.get('country') ?? '';
      const mine = (key: string) => {
        const pend = pending.find((x) => x.companyId === companyId && x.ruleKey === key);
        return pend ? { id: pend.id, value: pend.value, effectiveFrom: pend.effectiveFrom, effectiveTo: pend.effectiveTo, reason: pend.reason, reviewBy: pend.reviewBy, requestedById: pend.requestedById, requestedByName: pend.requestedByName, requestedAt: pend.requestedAt, canApprove: pend.canApprove } : null;
      };
      return reply([
        {
          ruleKey: 'gosi.saudi_employee_rate', resolvedValue: 0.0975, platformDefault: 0.0975, isOverride: false, overrideId: null, overrideValue: null, reason: null,
          effectiveFrom: null, effectiveTo: null, reviewBy: null, defaultDriftedSinceOverride: false, statutoryLocked: true, neverApplied: false, notice: null,
          ruleEffectiveFrom: '2025-07-01', ruleEffectiveTo: null, source: 'Social Insurance Law, Art. 18', verified: true, appliedByPayroll: true,
          direction: null, displayUnit: 'percent', pendingOverride: companyId ? mine('gosi.saudi_employee_rate') : null,
          ...(companyId ? {} : { countryCode: cc, overriddenInCompanies: 1 }),
        },
        {
          ruleKey: 'ot.standard_multiplier', resolvedValue: 1.5, platformDefault: 1.5, isOverride: false, overrideId: null, overrideValue: null, reason: null,
          effectiveFrom: null, effectiveTo: null, reviewBy: null, defaultDriftedSinceOverride: false, statutoryLocked: false, neverApplied: false, notice: null,
          ruleEffectiveFrom: '2024-01-01', ruleEffectiveTo: null, source: 'Labor Law, Art. 107', verified: false, appliedByPayroll: true,
          direction: 'floor', displayUnit: 'multiplier', pendingOverride: companyId ? mine('ot.standard_multiplier') : null,
          ...(companyId ? {} : { countryCode: cc, overriddenInCompanies: 0 }),
        },
      ]);
    }
    if (p === '/api/finance/rates/statutory/override' && method === 'POST') {
      const b = body as { companyId: string; ruleKey: string; overrideValue: string; effectiveFrom: string; effectiveTo: string | null; reason: string; reviewBy: string; countryCode: string };
      const created: Pending = {
        id: `ovr-${pending.length + 1}`, companyId: b.companyId, companyName: 'Masar Trading KSA', ruleKey: b.ruleKey, countryCode: b.countryCode,
        value: b.overrideValue, effectiveFrom: b.effectiveFrom, effectiveTo: b.effectiveTo, reason: b.reason, reviewBy: b.reviewBy,
        requestedById: 'user-maker', requestedByName: 'Omar Maker', requestedAt: '2026-10-10T10:00:00Z', canApprove: false, // the requester never approves
      };
      pending.push(created);
      return reply({ id: created.id, companyId: b.companyId, countryCode: b.countryCode, jurisdiction: 'SA', ruleKey: b.ruleKey, overrideValue: b.overrideValue, dataType: 'decimal', effectiveFrom: b.effectiveFrom, effectiveTo: b.effectiveTo, reviewBy: b.reviewBy, reason: b.reason, status: 'PendingApproval', isOverride: true, overridesStatutoryDefault: '1.5', approvedBy: null, approvedAtUtc: null });
    }

    if (p === '/api/organization/cost-centers' || p === '/api/grades' || p === '/api/branches' || p === '/api/departments') return reply(paged([]));
    if (p.startsWith('/api/admin/') || p.includes('country-packs')) return reply([]);
    return reply(paged([]));
  });

  await page.goto('/setup?tab=glMapping');
  await expect(page.getByRole('heading', { level: 1, name: options.locale === 'ar' ? 'الإعداد والإدارة' : 'Setup & Administration', exact: true })).toBeVisible({ timeout: 60_000 });
  return { writes, reads, errors, control };
}

async function shot(page: Page, info: TestInfo, name: string) {
  const file = info.outputPath(`${name}.png`);
  await page.screenshot({ path: file, fullPage: true, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach(name, { path: file, contentType: 'image/png' });
  const dir = process.env.GL_SHOTS_DIR;
  if (dir) {
    fs.mkdirSync(dir, { recursive: true });
    fs.copyFileSync(file, path.join(dir, `${name}.png`));
  }
}

const tab = (page: Page, name: string) => page.getByRole('tab', { name, exact: true });

test('chart of accounts: real headers, search and type groups, and delete asks a translated confirmation first', async ({ page }) => {
  const state = await boot(page, { groupMode: true });
  await expect(tab(page, 'Accounts & Mapping')).toHaveAttribute('aria-selected', 'true');
  const chart = page.getByRole('region', { name: 'Chart of Accounts', exact: true });
  for (const header of ['Code', 'Name', 'Type', 'Scope', 'Status', 'Actions']) {
    await expect(chart.getByRole('columnheader', { name: header, exact: true })).toBeVisible();
  }
  await expect(chart.getByRole('columnheader', { name: 'Expense (2)', exact: true })).toBeVisible();
  await expect(chart.getByRole('columnheader', { name: 'Liability (1)', exact: true })).toBeVisible();

  await chart.getByLabel('Search accounts by code or name').fill('loans');
  await expect(chart.getByRole('cell', { name: '1400', exact: true })).toBeVisible();
  await expect(chart.getByRole('cell', { name: '5001', exact: true })).toHaveCount(0);
  await chart.getByLabel('Search accounts by code or name').fill('');
  await chart.getByLabel('Filter by account type').selectOption('Liability');
  await expect(chart.getByRole('cell', { name: '2100', exact: true })).toBeVisible();
  await expect(chart.getByRole('cell', { name: '1400', exact: true })).toHaveCount(0);
  await chart.getByLabel('Filter by account type').selectOption('');

  // Delete is a labelled text button and never fires without a confirmation.
  await chart.getByRole('button', { name: 'Delete account 1400', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Delete account 1400?' });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(state.writes.filter((w) => w.method === 'DELETE')).toHaveLength(0);
  await chart.getByRole('button', { name: 'Delete account 1400', exact: true }).click();
  await page.getByRole('dialog', { name: 'Delete account 1400?' }).getByRole('button', { name: 'Delete', exact: true }).click();
  await expect.poll(() => state.writes.filter((w) => w.method === 'DELETE').map((w) => w.path)).toEqual(['/api/finance/gl/accounts/acc-1400']);

  // Load standard chart: explained, confirmed, and reported with the server's counts.
  await expect(chart.getByText('Adds only accounts and drivers that are missing; never changes existing ones.')).toBeVisible();
  await chart.getByRole('button', { name: 'Load standard payroll chart', exact: true }).click();
  await page.getByRole('dialog', { name: 'Load standard payroll chart?' }).getByRole('button', { name: 'Load standard chart', exact: true }).click();
  await expect(page.getByText('Added 4 accounts, 3 drivers, 2 mappings and 1 rate definitions.')).toBeVisible();
  expect(state.errors).toEqual([]);
});

test('import CSV runs a dry run first, previews the result, and applies only on request', async ({ page }) => {
  const state = await boot(page, { groupMode: true });
  await page.getByLabel('Choose a CSV file to import').setInputFiles({ name: 'accounts.csv', mimeType: 'text/csv', buffer: Buffer.from('code,name,type\n5003,Transport,Expense\n') });
  const dialog = page.getByRole('dialog', { name: 'Import chart of accounts' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('row', { name: /Will be created\s+2/ })).toBeVisible();
  await expect(dialog.getByRole('row', { name: /Will be updated\s+1/ })).toBeVisible();
  const imports = () => state.writes.filter((w) => w.path === '/api/finance/gl/accounts/import').map((w) => new URL(`http://x${w.search}`).searchParams.get('dryRun'));
  expect(imports()).toEqual(['true']);
  await dialog.getByRole('button', { name: 'Apply import', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(imports()).toEqual(['true', 'false']);
  await expect(page.getByText('Import applied: 2 created, 1 updated, 3 unchanged.')).toBeVisible();
});

test('"Where does it post?" lists problems first and the health banner links to the row', async ({ page }, info) => {
  await boot(page, { groupMode: true });
  const banner = page.getByRole('region', { name: 'Posting health' });
  await expect(banner).toContainText('2 posting problems need attention');
  await expect(banner).toContainText('Loan recovery: It posts to an account code that is not in the chart of accounts.');
  await expect(banner).toContainText('Control account 2300 - Bonus Payable: Bonus Payable carries a debit balance');

  const where = page.getByRole('region', { name: 'Where does it post?', exact: true });
  for (const header of ['Driver', 'Pay components', 'Side', 'Effective account', 'Source', 'Problem']) {
    await expect(where.getByRole('columnheader', { name: header, exact: true })).toBeVisible();
  }
  const bodyRows = where.locator('tbody tr');
  await expect(bodyRows.first()).toContainText('Loan recovery');
  await expect(bodyRows.first()).toContainText('Driver default');
  await expect(bodyRows.first()).toContainText('Credit');
  await expect(bodyRows.nth(1)).toContainText('Inherited from group');
  await expect(bodyRows.nth(1)).toContainText('Debit');

  await tab(page, 'Posting Drivers').click();
  await banner.getByRole('button', { name: /^Loan recovery:/ }).click();
  await expect(tab(page, 'Accounts & Mapping')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('#gl-route-LOAN_RECEIVABLE')).toBeFocused();
  await page.evaluate(() => window.scrollTo(0, 0));
  await shot(page, info, 'gl-en-group-accounts-mapping');
});

test('a company-scoped user still sees the group accounts the company inherits', async ({ page }) => {
  const state = await boot(page);
  await expect(page.getByLabel('Configuration scope')).toHaveValue('co-sa');
  const chart = page.getByRole('region', { name: 'Chart of Accounts', exact: true });
  const own = chart.getByRole('row').filter({ hasText: 'Riyadh Site Allowance' });
  await expect(own).toContainText('Company');
  const inherited = chart.getByRole('row').filter({ hasText: 'Basic Salary Expense' });
  await expect(inherited).toContainText('Inherited from group');
  await expect(inherited.getByRole('button')).toHaveCount(0);
  // The group list is read with no companyId, even though this user is not group-scoped.
  expect(state.reads.some((r) => r.path === '/api/finance/gl/accounts' && !r.search.includes('companyId'))).toBe(true);
  // Mapping is a real table and inherited group accounts are selectable.
  const mapping = page.getByRole('region', { name: 'Payroll GL Mapping', exact: true });
  await expect(mapping.getByRole('columnheader', { name: 'Posting line', exact: true })).toBeVisible();
  await expect(mapping.getByLabel('Account for Basic salary').locator('option', { hasText: '5001 — Basic Salary Expense (group)' })).toHaveCount(1);
});

test('a system driver can be renamed and its fallback picked from the chart', async ({ page }, info) => {
  const state = await boot(page, { groupMode: true });
  await tab(page, 'Posting Drivers').click();
  await expect(page.getByText("System drivers' routing rules are locked; you can rename them and change their fallback account.").first()).toBeVisible();
  const row = page.getByRole('row').filter({ hasText: 'EARN:BASIC' });
  await expect(row).toContainText('5001');
  await expect(row).toContainText('Inherited from group');
  await shot(page, info, 'gl-en-drivers');
  await row.getByRole('button', { name: 'Edit label & fallback', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Edit label & fallback: EARN:BASIC' });
  await expect(dialog.getByLabel('Key')).toHaveCount(0);
  await dialog.getByLabel('Name').fill('Basic pay');
  await dialog.getByLabel('Fallback account').selectOption('2100');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  const put = state.writes.find((w) => w.method === 'PUT' && w.path === '/api/finance/gl/drivers/drv-basic');
  expect(put?.body).toMatchObject({ key: 'EARN:BASIC', label: 'Basic pay', defaultCode: '2100', defaultName: 'Salaries Payable', matchMode: 'Exact', matchComponentCode: 'BASIC' });
});

test('company rates show one in-effect row per rate, with source, scheduled rows and history', async ({ page }, info) => {
  await boot(page, { groupMode: true });
  await page.getByLabel('Configuration scope').selectOption('co-sa');
  await tab(page, 'Company Rates').click();
  const site = page.locator('tbody').filter({ has: page.locator('[data-rate-key="allowance.site"]') });
  const current = site.locator('[data-rate-key="allowance.site"]');
  await expect(current).toContainText('Site allowance');
  await expect(current).toContainText('allowance.site');
  await expect(current).toContainText(/SAR\s750\.00/);
  await expect(current).toContainText('Company');
  await expect(current.getByRole('button', { name: 'End-date', exact: true })).toBeVisible();
  await expect(current.getByRole('button', { name: 'Delete', exact: true })).toHaveCount(0);
  const scheduled = site.getByRole('row').filter({ hasText: 'Scheduled' });
  await expect(scheduled).toContainText(/SAR\s800\.00/);
  await expect(scheduled.getByRole('button', { name: 'Delete', exact: true })).toBeVisible();
  await site.getByText('History (1 earlier value)').click();
  await expect(site.getByRole('cell', { name: /SAR\s500\.00/ })).toBeVisible();
  await expect(site.getByText('Replaced', { exact: true })).toBeVisible();
  const weekend = page.locator('[data-rate-key="ot.weekend_multiplier"]');
  await expect(weekend).toContainText('1.5×');
  await expect(weekend).toContainText('Inherited from group');
  await shot(page, info, 'gl-en-company-rates');
});

test('statutory: group view formats 9.75%, and a pending override survives reload without Approve for its requester', async ({ page }) => {
  const state = await boot(page, { groupMode: true });
  await tab(page, 'Statutory Rates').click();
  const statutory = page.getByRole('region', { name: 'Statutory Rates', exact: true });
  const gosi = statutory.getByRole('row').filter({ hasText: 'gosi.saudi_employee_rate' });
  await expect(gosi).toContainText('GOSI contribution, Saudi employee share');
  await expect(gosi).toContainText('9.75%');
  await expect(gosi).toContainText('Overridden in 1 company');
  await expect(gosi).toContainText('Source Social Insurance Law, Art. 18');
  await expect(statutory.getByRole('row').filter({ hasText: 'ot.standard_multiplier' })).toContainText('Pending legal verification');
  await expect(page.getByRole('button', { name: 'Request override' })).toHaveCount(0);
  // A checker sees requests raised by other people, with Approve.
  const pendingSection = page.getByRole('region', { name: 'Pending approvals', exact: true });
  await expect(pendingSection.getByRole('row').filter({ hasText: 'Layla Haddad' }).getByRole('button', { name: 'Approve' })).toBeVisible();

  await page.getByLabel('Configuration scope').selectOption('co-sa');
  const ot = statutory.getByRole('row').filter({ hasText: 'ot.standard_multiplier' });
  await expect(ot).toContainText('Can only be increased above the statutory minimum.');
  await ot.getByRole('button', { name: 'Request override', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Request override: Overtime pay multiplier' });
  await dialog.getByLabel('Override value').fill('1.75');
  await expect(dialog).toContainText('Shown as 1.75×');
  await dialog.getByLabel('Review by').fill(iso(year + 1, 3, 31));
  await dialog.getByLabel('Reason').fill('Collective agreement clause 7');
  await dialog.getByRole('button', { name: 'Submit for approval', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(ot).toContainText('Pending approval');
  await expect(ot.getByRole('button', { name: 'Approve' })).toHaveCount(0);
  expect(state.writes.some((w) => w.path === '/api/finance/rates/statutory/override')).toBe(true);

  await page.reload();
  await page.getByLabel('Configuration scope').selectOption('co-sa');
  await tab(page, 'Statutory Rates').click();
  const after = statutory.getByRole('row').filter({ hasText: 'ot.standard_multiplier' });
  await expect(after).toContainText('Pending approval');
  await expect(after).toContainText('1.75×');
  await expect(after).toContainText('Requested by Omar Maker');
  await expect(after.getByRole('button', { name: 'Approve' })).toHaveCount(0);
  const mine = page.getByRole('region', { name: 'Pending approvals', exact: true }).getByRole('row').filter({ hasText: 'Omar Maker' });
  await expect(mine).toContainText('Waiting for another approver');
  await expect(mine.getByRole('button', { name: 'Approve' })).toHaveCount(0);
});

test('a failed load shows the reason and Retry, never the empty state', async ({ page }) => {
  const state = await boot(page, { groupMode: true, driversDown: true });
  await tab(page, 'Posting Drivers').click();
  await expect(page.getByText('This could not be loaded.')).toBeVisible();
  await expect(page.getByText(/No posting drivers are set up yet/)).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Add driver' })).toBeDisabled();
  state.control.driversDown = false;
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.getByRole('row').filter({ hasText: 'EARN:BASIC' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Add driver' })).toBeEnabled();
});

test('the GL tab renders in Arabic, right to left', async ({ page }, info) => {
  const state = await boot(page, { groupMode: true, locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByRole('tab', { name: 'الحسابات والربط', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('heading', { name: 'دليل الحسابات', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'إلى أين يُرحَّل؟', exact: true })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'الرمز', exact: true }).first()).toBeVisible();
  await expect(page.getByRole('cell', { name: 'دائن', exact: true }).first()).toBeVisible();
  await page.getByRole('tab', { name: 'المعدلات النظامية', exact: true }).click();
  const gosi = page.getByRole('region', { name: 'المعدلات النظامية', exact: true }).getByRole('row').filter({ hasText: 'gosi.saudi_employee_rate' });
  await expect(gosi).toContainText('اشتراك التأمينات الاجتماعية، حصة الموظف السعودي');
  await expect(gosi).toContainText('9.75%');
  await expect(page.getByText('بانتظار التحقق النظامي').first()).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  await shot(page, info, 'gl-ar-statutory');
  expect(state.errors).toEqual([]);
});

test('every GL sub-tab fits a phone-width screen without horizontal page scroll', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const state = await boot(page, { groupMode: true });
  const contained = () => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
  await expect(page.getByRole('region', { name: 'Chart of Accounts', exact: true })).toBeVisible();
  expect(await contained()).toBe(true);
  for (const name of ['Posting Drivers', 'Company Rates', 'Statutory Rates']) {
    await tab(page, name).click();
    await expect(tab(page, name)).toHaveAttribute('aria-selected', 'true');
    await page.waitForLoadState('networkidle');
    expect(await contained(), `${name} overflows the page`).toBe(true);
  }
  expect(state.errors).toEqual([]);
});

test('the GL tab has no serious or critical accessibility violations', async ({ page }) => {
  await boot(page, { groupMode: true });
  const serious = async () => {
    const result = await new AxeBuilder({ page }).include('#setup-setting-content').analyze();
    return result.violations
      .filter((v) => v.impact === 'critical' || v.impact === 'serious')
      .map((v) => `${v.id}: ${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ')}`);
  };
  await expect(page.getByRole('region', { name: 'Chart of Accounts', exact: true })).toBeVisible();
  expect(await serious()).toEqual([]);
  for (const name of ['Posting Drivers', 'Company Rates', 'Statutory Rates']) {
    await tab(page, name).click();
    await page.waitForLoadState('networkidle');
    expect(await serious(), name).toEqual([]);
  }
});
