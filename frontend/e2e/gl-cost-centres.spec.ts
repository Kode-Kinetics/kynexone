import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Request, type TestInfo } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Setup → Payroll accounting for cost accounting v1 (WP-F phase 1): the account "Cost centre rule" and its preset,
 * the mapping functional class and fixed cost centre, and the Cost accounting readiness and switch. Every API request
 * is intercepted (no backend, no real tenant), so this proves the UI contract (states, wiring, wording, EN and AR),
 * not server authorisation.
 *   npx next dev -p 5197 &
 *   GL_COST_CENTRES_BASE_URL=http://127.0.0.1:5197 npx playwright test -c e2e/playwright.gl-cost-centres.config.ts
 * GL_SHOTS_DIR, when set, also receives the review screenshots.
 */

const companySa = {
  id: 'co-sa', legalNameEn: 'Masar Trading KSA', legalNameAr: 'مسار للتجارة', tradeName: 'Masar KSA',
  countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active',
};
const companyAe = {
  id: 'co-ae', legalNameEn: 'Masar Services UAE', legalNameAr: '', tradeName: 'Masar UAE',
  countryCode: 'AE', jurisdiction: 'UAE', defaultCurrency: 'AED', isActive: true, approvalStatus: 'Active',
};

const M7_EN = 'A payroll run for this company is awaiting finance review or lock, and this change would alter what it posts. Lock the run, or reopen it first.';
const M7_AR = 'هناك مسيرة رواتب لهذه الشركة بانتظار المراجعة المالية أو القفل، وهذا التغيير سيغيّر ما ستقوم بترحيله. اقفل المسيرة أو أعد فتحها أولًا.';
const lockedBody = { error: 'locked_into_approved_run', code: 'locked_into_approved_run', message: M7_EN, messageAr: M7_AR };

interface Account { id: string; companyId: string | null; code: string; name: string; accountType: string; isActive: boolean; costCenterRule: string; externalCode?: string | null }

interface Options {
  groupMode?: boolean;
  locale?: 'en' | 'ar';
  /** finance.gl.read only: no manage, no organization.write. */
  readOnly?: boolean;
  /** Costing is already on for the company. */
  costingOn?: boolean;
  /** A run awaits finance review or lock (M-7 on every cost-accounting write). */
  awaitingRun?: boolean;
  /** The chart already follows the recommended preset. */
  presetNothingToChange?: boolean;
}

interface Recorded { method: string; path: string; search: string; body: unknown }

function seedAccounts(options: Options): Account[] {
  const done = !!options.presetNothingToChange;
  return [
    { id: 'acc-5001', companyId: null, code: '5001', name: 'Basic Salary Expense', accountType: 'Expense', isActive: true, costCenterRule: 'Required' },
    { id: 'acc-5002', companyId: null, code: '5002', name: 'Housing Allowance Expense', accountType: 'Expense', isActive: true, costCenterRule: done ? 'Required' : 'Optional' },
    { id: 'acc-2100', companyId: null, code: '2100', name: 'Salaries Payable', accountType: 'Liability', isActive: true, costCenterRule: 'NotAllowed' },
    { id: 'acc-1400', companyId: null, code: '1400', name: 'Employee Loans Receivable', accountType: 'Asset', isActive: true, costCenterRule: done ? 'NotAllowed' : 'Optional' },
    { id: 'acc-5090', companyId: 'co-sa', code: '5090', name: 'Riyadh Site Allowance', accountType: 'Expense', isActive: true, costCenterRule: 'Optional' },
  ];
}

const costCentres = [
  { id: 'cc-sales', companyId: 'co-sa', code: 'SALES', name: 'Sales', isActive: true },
  { id: 'cc-admin', companyId: 'co-sa', code: 'ADMIN', name: 'Administration', isActive: true },
  { id: 'cc-old', companyId: 'co-sa', code: 'OLD-OPS', name: 'Old operations', isActive: false },
  { id: 'cc-group', code: 'GROUP-POOL', name: 'Group pool (no company)', isActive: true },
  { id: 'cc-ae', companyId: 'co-ae', code: 'AE-OPS', name: 'UAE operations', isActive: true },
];

function readiness(companyId: string, options: Options, enabled: boolean) {
  return {
    companyId,
    settings: { enabled, defaultCostCenterId: 'cc-admin', smallCellMin: 0, awaitingRun: !!options.awaitingRun },
    checks: [
      { key: 'employees-unresolved', severity: 'blocker', count: 12, sample: [
        { id: 412, code: 'E-0412', employeeId: 412, employeeCode: 'E-0412', level: 'company', departmentId: 'dep-cs', positionId: null, parentDepartmentId: null, reason: 'none' },
        { id: 431, code: 'E-0431', employeeId: 431, employeeCode: 'E-0431', level: 'company', departmentId: null, positionId: 'pos-1', parentDepartmentId: null, reason: 'none' },
      ] },
      { key: 'invalid-reference', severity: 'blocker', count: 3, sample: [
        { id: 'dep-cs', code: 'CS', kind: 'department' }, { id: 'pos-1', code: 'ANALYST', kind: 'position' }, { id: 77, code: 'E-0077', kind: 'employee' },
      ] },
      { key: 'accounts-no-rule', severity: 'warning', count: 9, sample: [{ id: 'acc-5002', code: '5002' }] },
      { key: 'no-company', severity: 'blocker', count: 2, sample: [] },
      { key: 'rounding-default', severity: 'ok', count: 0, sample: [] },
      { key: 'mapping-invisible', severity: 'ok', count: 0, sample: [] },
      { key: 'small-cell', severity: 'warning', count: 1, sample: [{ id: 'cc-sales', code: 'SALES' }] },
      { key: 'rules-not-enforced', severity: 'ok', count: 0, sample: [] },
      { key: 'currency-scale', severity: 'ok', count: 0, sample: [] },
      { key: 'employees-no-company', severity: 'ok', count: 0, sample: [] },
    ],
    blockers: [],
    smallCellExposure: { count: 1, threshold: 3 },
  };
}

function mappingRows(companyId: string | null) {
  const inherited = companyId !== null;
  return [
    {
      driverKey: 'EARN:BASIC', label: 'Basic salary', category: 'Earning', accountType: 'Expense', allowedAccountTypes: ['Expense'],
      defaultAccount: '5001 — Basic Salary Expense', mappedAccountId: 'acc-5001', segmentCostCenterId: null, inherited,
      classRules: [{ functionalClass: 'SELLING', accountId: 'acc-5002', inherited }],
    },
    {
      driverKey: 'EARN:HOUSING', label: 'Housing allowance', category: 'Earning', accountType: 'Expense', allowedAccountTypes: ['Expense'],
      defaultAccount: '5002 — Housing Allowance Expense', mappedAccountId: companyId ? 'acc-5090' : 'acc-5002', segmentCostCenterId: companyId ? 'cc-admin' : null, inherited: false,
      classRules: [],
    },
    {
      driverKey: 'LOAN_RECEIVABLE', label: 'Loan recovery', category: 'Balancing', accountType: 'Asset', allowedAccountTypes: ['Asset'],
      defaultAccount: '1400 — Employee Loans Receivable', mappedAccountId: null, segmentCostCenterId: null, inherited, classRules: [],
    },
  ];
}

const control = () => ({
  /** PUT /accounts/{id} answers this status + body once. */
  accountRefusal: null as null | { status: number; body: unknown },
  /** POST preset (dryRun=false) answers this status + body. */
  presetRefusal: null as null | { status: number; body: unknown },
  mappingRefusal: null as null | { status: number; body: unknown },
  switchRefusal: null as null | { status: number; body: unknown },
  readinessDown: false,
  readinessDelayMs: 0,
});

async function boot(page: Page, options: Options = {}, initialUrl?: string) {
  const writes: Recorded[] = [];
  const reads: Recorded[] = [];
  const errors: string[] = [];
  const ctl = control();
  const accounts = seedAccounts(options);
  const costing = { enabled: !!options.costingOn, defaultCostCenterId: 'cc-admin' as string | null };
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
      const manage = !options.readOnly;
      return reply({
        id: 'user-maker', tenantId: 'gl-tenant', tenantSlug: 'gl-fixture', fullName: 'Omar Maker', roles: manage ? ['Admin'] : ['Auditor'],
        permissions: manage
          ? ['organization.read', 'organization.write', 'dashboard.read', 'finance.gl.read', 'finance.gl.manage', 'finance.gl.drivers.manage', 'payroll.rates.read', 'payroll.rates.manage']
          : ['organization.read', 'dashboard.read', 'finance.gl.read'],
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
    if (p === '/api/organization/cost-centers') return reply(paged(costCentres));

    // ── GL: accounts ──
    if (p === '/api/finance/gl/accounts' && method === 'GET') return reply(accounts.filter((a) => (companyId ? a.companyId === companyId : a.companyId === null)));
    if (p === '/api/finance/gl/accounts' && method === 'POST') {
      const b = body as Partial<Account>;
      const created: Account = { id: `acc-new-${accounts.length}`, companyId, code: b.code ?? '', name: b.name ?? '', accountType: b.accountType ?? 'Expense', isActive: true, costCenterRule: b.costCenterRule ?? 'Optional' };
      accounts.push(created);
      return reply(created);
    }
    if (p === '/api/finance/gl/accounts/cost-centre-rule-preset') {
      const dry = url.searchParams.get('dryRun') === 'true';
      const pnl = (t: string) => t === 'Expense' || t === 'Income';
      const pending = accounts.filter((a) => (companyId ? a.companyId === companyId : a.companyId === null)).map((a) => ({ a, to: pnl(a.accountType) ? 'Required' : 'NotAllowed' })).filter((x) => x.a.costCenterRule !== x.to);
      const scoped = accounts.filter((a) => (companyId ? a.companyId === companyId : a.companyId === null));
      const byType = ['Asset', 'Expense', 'Liability'].flatMap((type) => {
        const rows = pending.filter((x) => x.a.accountType === type);
        return rows.length ? [{ accountType: type, to: rows[0].to, count: rows.length }] : [];
      });
      if (dry) return reply({ dryRun: true, changed: pending.length, total: scoped.length, byType });
      if (ctl.presetRefusal) return reply(ctl.presetRefusal.body, ctl.presetRefusal.status);
      for (const x of pending) x.a.costCenterRule = x.to;
      return reply({ dryRun: false, changed: pending.length, total: scoped.length, byType });
    }
    if (p === '/api/finance/gl/accounts/export') return route.fulfill({ status: 200, contentType: 'text/csv', body: 'code,name,type\n' });
    if (p.startsWith('/api/finance/gl/accounts/') && method === 'PUT') {
      if (ctl.accountRefusal) { const r = ctl.accountRefusal; ctl.accountRefusal = null; return reply(r.body, r.status); }
      const id = p.split('/').pop();
      const acct = accounts.find((a) => a.id === id);
      if (acct) Object.assign(acct, body as object);
      return reply(acct ?? {});
    }
    if (p.startsWith('/api/finance/gl/accounts/')) return route.fulfill({ status: 204 });

    // ── GL: mappings, drivers, routing ──
    if (p === '/api/finance/gl/mappings' && method === 'GET') return reply(mappingRows(companyId));
    if (p === '/api/finance/gl/mappings' && method === 'PUT') {
      if (ctl.mappingRefusal) return reply(ctl.mappingRefusal.body, ctl.mappingRefusal.status);
      return reply({ count: Array.isArray(body) ? body.length : 0, changed: 1, removed: 0 });
    }
    if (p === '/api/finance/gl/drivers' && method === 'GET') return reply([]);
    if (p === '/api/finance/gl/resolve') return reply({ companyId: null, rows: [] });
    if (p === '/api/finance/gl/control-accounts/health') return reply({ generatedAtUtc: '2026-10-25T08:00:00Z', companyId: null, healthy: true, violations: [], accounts: [] });

    // ── GL: cost accounting ──
    if (p === '/api/finance/gl/cost-accounting/readiness' && method === 'GET') {
      if (ctl.readinessDelayMs) await new Promise((r) => setTimeout(r, ctl.readinessDelayMs));
      if (ctl.readinessDown) return reply({ code: 'internal_error', message: 'boom', traceId: 'trace-ready' }, 500);
      const r = readiness(companyId ?? 'co-sa', options, costing.enabled);
      r.settings.defaultCostCenterId = costing.defaultCostCenterId as string;
      return reply(r);
    }
    if (p === '/api/finance/gl/cost-accounting' && method === 'PUT') {
      if (ctl.switchRefusal) return reply(ctl.switchRefusal.body, ctl.switchRefusal.status);
      const b = body as { enabled?: boolean; defaultCostCenterId?: string | null };
      if (typeof b.enabled === 'boolean') costing.enabled = b.enabled;
      if (b.defaultCostCenterId !== undefined) costing.defaultCostCenterId = b.defaultCostCenterId;
      return reply({ companyId, changed: ['enabled'], settings: { enabled: costing.enabled, defaultCostCenterId: costing.defaultCostCenterId, smallCellMin: 0, awaitingRun: false }, checks: [] });
    }

    // ── Rates and the rest of Setup: nothing to see ──
    if (p === '/api/finance/rates/company/registry') return reply([]);
    if (p.startsWith('/api/finance/rates/')) return reply([]);
    if (p === '/api/grades' || p === '/api/branches' || p === '/api/departments') return reply(paged([]));
    if (p.startsWith('/api/admin/') || p.includes('country-packs')) return reply([]);
    return reply(paged([]));
  });

  await page.goto(initialUrl ?? '/setup?tab=glMapping');
  await expect(page.getByRole('heading', { level: 1, name: options.locale === 'ar' ? 'الإعداد والإدارة' : 'Setup & Administration', exact: true })).toBeVisible({ timeout: 60_000 });
  return { writes, reads, errors, ctl, accounts, costing };
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

const tid = (page: Page, id: string) => page.getByTestId(id);
const chart = (page: Page) => page.getByRole('region', { name: /^(Chart of Accounts|دليل الحسابات)$/ });
const search = (w: Recorded) => new URL(`http://x${w.search}`).searchParams;
const axeSeriousOrWorse = async (page: Page, scope: string) => {
  const result = await new AxeBuilder({ page }).include(scope).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  return result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`);
};

// ═════════════════════════════ Screen 2: GL accounts, Cost centre rule ═════════════════════════════

test('the chart shows each account\'s cost centre rule as text with an icon, and explains type versus cost centre in one sentence', async ({ page }, info) => {
  await boot(page, { groupMode: true });
  await expect(tid(page, 'cc-acct-explainer')).toContainText(
    'Account type says what kind of money an account holds (asset, liability, equity, income or expense); a cost centre says which team or department the cost belongs to, and only income and expense accounts can carry one.',
  );
  const region = chart(page);
  await expect(region.getByRole('columnheader', { name: 'Cost centre rule', exact: true })).toBeVisible();
  await expect(tid(page, 'cc-acct-rule-5001')).toHaveText('Required');
  await expect(tid(page, 'cc-acct-rule-5001')).toHaveAttribute('aria-label', 'Cost centre rule: Required');
  await expect(tid(page, 'cc-acct-rule-5002')).toHaveText('Optional');
  await expect(tid(page, 'cc-acct-rule-2100')).toHaveText('Not allowed');
  await expect(tid(page, 'cc-acct-rule-2100')).toHaveAttribute('aria-label', 'Cost centre rule: Not allowed');
  // The add form repeats the point in short form.
  await expect(tid(page, 'cc-acct-type-hint')).toHaveText('What kind of account this is. Cost centres are set separately, in Cost centre rule.');
  await shot(page, info, 'cc-en-accounts-rule');
});

test('the rule filter narrows the chart to one rule and says so', async ({ page }) => {
  await boot(page, { groupMode: true });
  await tid(page, 'cc-acct-rule-filter').selectOption('NotAllowed');
  await expect(chart(page).getByRole('cell', { name: '2100', exact: true })).toBeVisible();
  await expect(chart(page).getByRole('cell', { name: '5001', exact: true })).toHaveCount(0);
  await tid(page, 'cc-acct-rule-filter').selectOption('');
  await expect(chart(page).getByRole('cell', { name: '5001', exact: true })).toBeVisible();
});

test('a liability cannot require a cost centre: Required is disabled with its reason, and a type change that would break the rule is blocked, not rewritten', async ({ page }) => {
  const state = await boot(page, { groupMode: true });
  await chart(page).getByRole('row').filter({ hasText: 'Salaries Payable' }).getByRole('button', { name: 'Edit', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Edit account 2100' });
  const rule = tid(page, 'cc-acct-edit-rule');
  await expect(rule).toHaveValue('NotAllowed');
  await expect(rule.locator('option[value="Required"]')).toBeDisabled();
  await expect(dialog.getByText('Only expense and income accounts can require a cost centre.')).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();

  // An expense account that Requires a rule, retyped to Asset: the save is refused in the form, nothing is sent.
  await chart(page).getByRole('row').filter({ hasText: 'Basic Salary Expense' }).getByRole('button', { name: 'Edit', exact: true }).click();
  await page.getByRole('dialog', { name: 'Edit account 5001' }).getByLabel('Type', { exact: true }).selectOption('Asset');
  await page.getByRole('dialog', { name: 'Edit account 5001' }).getByRole('button', { name: 'Save', exact: true }).click();
  await expect(tid(page, 'cc-acct-edit-rule-error')).toHaveText('Only expense and income accounts can require a cost centre.');
  expect(state.writes.filter((w) => w.method === 'PUT')).toHaveLength(0);
  expect(await tid(page, 'cc-acct-edit-rule').inputValue()).toBe('Required');
});

test('saving an account sends its cost centre rule, and the server\'s refusal shows under the select', async ({ page }) => {
  const state = await boot(page, { groupMode: true });
  await chart(page).getByRole('row').filter({ hasText: 'Housing Allowance Expense' }).getByRole('button', { name: 'Edit', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Edit account 5002' });
  await tid(page, 'cc-acct-edit-rule').selectOption('NotAllowed');
  state.ctl.accountRefusal = { status: 422, body: { error: 'cost_centre_rule_invalid_for_type', code: 'cost_centre_rule_invalid_for_type', message: 'Only expense and income accounts can require a cost centre.', messageAr: 'لا يمكن إلزام مركز التكلفة إلا في حسابات المصروفات والإيرادات.' } };
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(tid(page, 'cc-acct-edit-rule-error')).toHaveText('Only expense and income accounts can require a cost centre.');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  const puts = state.writes.filter((w) => w.method === 'PUT' && w.path === '/api/finance/gl/accounts/acc-5002');
  expect(puts).toHaveLength(2);
  expect(puts[1].body).toMatchObject({ code: '5002', costCenterRule: 'NotAllowed' });
  await expect(tid(page, 'cc-acct-rule-5002')).toHaveText('Not allowed');
});

test('adding an account carries the chosen rule, defaulting to Optional', async ({ page }) => {
  const state = await boot(page, { groupMode: true });
  await expect(tid(page, 'cc-acct-form-rule')).toHaveValue('Optional');
  await page.getByLabel('Code', { exact: true }).first().fill('5099');
  await page.getByPlaceholder('Account name').fill('Transport');
  await tid(page, 'cc-acct-form-rule').selectOption('Required');
  await page.getByRole('button', { name: 'Add account', exact: true }).click();
  await expect.poll(() => state.writes.filter((w) => w.method === 'POST' && w.path === '/api/finance/gl/accounts').map((w) => (w.body as { costCenterRule?: string }).costCenterRule)).toEqual(['Required']);
});

test('apply recommended rules: a dry-run preview with counts, a required reason, then the chart reloads', async ({ page }, info) => {
  const state = await boot(page, { groupMode: true });
  await tid(page, 'cc-acct-preset-open').click();
  const dialog = tid(page, 'cc-acct-preset-dialog');
  await expect(dialog).toBeVisible();
  await expect(page.getByRole('dialog', { name: 'Apply recommended cost centre rules?' })).toBeVisible();
  await expect(dialog).toContainText('Expense and income accounts become Required. Asset, liability and equity accounts become Not allowed.');
  await expect(tid(page, 'cc-acct-preset-count')).toContainText('2 accounts will change.');
  await expect(dialog).toContainText('Nothing changes in payroll until Cost accounting is turned on.');
  const dry = state.writes.filter((w) => w.path === '/api/finance/gl/accounts/cost-centre-rule-preset');
  expect(dry).toHaveLength(1);
  expect(search(dry[0]).get('dryRun')).toBe('true');
  // Nothing is written until the reason is in.
  await expect(tid(page, 'cc-acct-preset-apply')).toBeDisabled();
  await shot(page, info, 'cc-en-preset-preview');
  await tid(page, 'cc-acct-preset-reason').fill('Finance policy 2026-04');
  await tid(page, 'cc-acct-preset-apply').click();
  await expect(page.getByText('Rules applied to 2 accounts.')).toBeVisible();
  await expect(dialog).toHaveCount(0);
  const applied = state.writes.filter((w) => w.path === '/api/finance/gl/accounts/cost-centre-rule-preset').at(-1)!;
  expect(search(applied).get('dryRun')).toBe('false');
  expect(applied.body).toEqual({ reason: 'Finance policy 2026-04' });
  await expect(tid(page, 'cc-acct-rule-5002')).toHaveText('Required');
  await expect(tid(page, 'cc-acct-rule-1400')).toHaveText('Not allowed');
});

test('apply recommended rules has nothing to do when the chart already follows them', async ({ page }) => {
  await boot(page, { groupMode: true, presetNothingToChange: true });
  await tid(page, 'cc-acct-preset-open').click();
  await expect(tid(page, 'cc-acct-preset-nochange')).toHaveText('All accounts already follow the recommended rules.');
  await expect(tid(page, 'cc-acct-preset-apply')).toBeDisabled();
});

test('a read-only reader sees the rule as plain text and no preset button', async ({ page }) => {
  await boot(page, { groupMode: true, readOnly: true });
  await expect(tid(page, 'cc-acct-rule-5001')).toHaveText('Required');
  await expect(tid(page, 'cc-acct-preset-open')).toHaveCount(0);
  await expect(chart(page).getByRole('button', { name: 'Edit', exact: true })).toHaveCount(0);
});

test('M-7: the preset is refused while a run awaits lock, and the server\'s sentence is shown with a banner that disables rule changes', async ({ page }) => {
  const state = await boot(page, { groupMode: true });
  state.ctl.presetRefusal = { status: 409, body: lockedBody };
  await tid(page, 'cc-acct-preset-open').click();
  await tid(page, 'cc-acct-preset-reason').fill('Policy');
  await tid(page, 'cc-acct-preset-apply').click();
  await expect(tid(page, 'cc-acct-preset-dialog')).toContainText(M7_EN);
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(tid(page, 'cc-acct-locked-banner')).toContainText(M7_EN);
  await expect(tid(page, 'cc-acct-preset-open')).toBeDisabled();
});

test('costing off for a company: the rules note says they apply once it is on, and links to Cost accounting', async ({ page }) => {
  await boot(page, { groupMode: true });
  // Group scope has no single switch to point at.
  await expect(tid(page, 'cc-acct-costing-off-note')).toHaveCount(0);
  await page.getByLabel('Configuration scope').selectOption('co-sa');
  await expect(tid(page, 'cc-acct-costing-off-note')).toContainText('Cost accounting is off for this company. These rules apply once you turn it on.');
  await expect(tid(page, 'cc-acct-costing-off-note').getByRole('button', { name: 'Open Cost accounting', exact: true })).toBeVisible();
});

test('costing on: no "off" note', async ({ page }) => {
  await boot(page, { costingOn: true });
  await expect(tid(page, 'cc-acct-explainer')).toBeVisible();
  await expect(tid(page, 'cc-acct-costing-off-note')).toHaveCount(0);
});

test('Arabic: RTL, the rule badges and explainer are translated, and a refusal shows the server\'s Arabic sentence (AM-5)', async ({ page }, info) => {
  const state = await boot(page, { groupMode: true, locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(tid(page, 'cc-acct-explainer')).toContainText('نوع الحساب يحدد طبيعة المبلغ');
  await expect(tid(page, 'cc-acct-rule-5001')).toHaveText('إلزامي');
  await expect(tid(page, 'cc-acct-rule-2100')).toHaveText('غير مسموح');
  await shot(page, info, 'cc-ar-accounts-rule');
  state.ctl.presetRefusal = { status: 409, body: lockedBody };
  await tid(page, 'cc-acct-preset-open').click();
  await expect(tid(page, 'cc-acct-preset-count')).toContainText('سيتغيّر');
  await tid(page, 'cc-acct-preset-reason').fill('سياسة');
  await shot(page, info, 'cc-ar-preset-preview');
  await tid(page, 'cc-acct-preset-apply').click();
  await expect(tid(page, 'cc-acct-preset-dialog')).toContainText(M7_AR);
  expect(state.errors).toEqual([]);
});

test('axe: the chart and the preset dialog have no serious or critical violations', async ({ page }) => {
  await boot(page, { groupMode: true });
  expect(await axeSeriousOrWorse(page, 'main')).toEqual([]);
  await tid(page, 'cc-acct-preset-open').click();
  await expect(tid(page, 'cc-acct-preset-dialog')).toBeVisible();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});
