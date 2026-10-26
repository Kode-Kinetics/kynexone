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
  /** Every readiness check is ok. */
  allClear?: boolean;
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
    { id: 'acc-5091', companyId: 'co-sa', code: '5091', name: 'Remote Location Hardship Supplement for Long-Term Contractors and Seconded Staff', accountType: 'Expense', isActive: true, costCenterRule: 'Required', externalCode: 'SAP-600091-HARDSHIP' },
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

function clearReadiness(companyId: string, options: Options, enabled: boolean) {
  const r = readiness(companyId, options, enabled);
  return { ...r, checks: r.checks.map((c) => ({ ...c, severity: 'ok', count: 0, sample: [] })), smallCellExposure: { count: 0, threshold: 3 } };
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
      const r = options.allClear ? clearReadiness(companyId ?? 'co-sa', options, costing.enabled) : readiness(companyId ?? 'co-sa', options, costing.enabled);
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
  return result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.nodes.map((n) => `${n.target.join(' ')} ${n.any.map((a) => a.message).join(';')} ${n.html.slice(0, 120)}`).join(' | ')}`);
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
  await page.getByPlaceholder('5006').fill('5099');
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

test('costing off for a company: the rules note says they apply once it is on', async ({ page }) => {
  await boot(page, { groupMode: true });
  // Group scope has no single switch to point at.
  await expect(tid(page, 'cc-acct-costing-off-note')).toHaveCount(0);
  await page.getByLabel('Configuration scope').selectOption('co-sa');
  await expect(tid(page, 'cc-acct-costing-off-note')).toContainText('Cost accounting is off for this company. These rules apply once you turn it on.');
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
  await expect(tid(page, 'cc-acct-rule-5001')).toHaveText('مطلوب');
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

// ═════════════════════════════ Screen 3: Mappings (functional class, fixed cost centre) ═════════════════════════════

const mapping = (page: Page) => page.getByRole('region', { name: /^(Payroll GL Mapping|ربط الرواتب بدفتر الأستاذ العام)$/ });
const lastMappingPut = (state: { writes: Recorded[] }) => state.writes.filter((w) => w.method === 'PUT' && w.path === '/api/finance/gl/mappings').at(-1)!;

test('group view: the fixed cost centre is off with the per-company explanation, and the class rule row is listed', async ({ page }, info) => {
  await boot(page, { groupMode: true });
  const region = mapping(page);
  for (const header of ['Posting line', 'Account', 'Functional class', 'Fixed cost centre', 'Actions']) {
    await expect(region.getByRole('columnheader', { name: header, exact: true })).toBeVisible();
  }
  await expect(tid(page, 'cc-map-fixed-EARN:BASIC')).toBeDisabled();
  await expect(tid(page, 'cc-map-group-scope-hint')).toHaveText('Fixed cost centres are set per company. Switch to a company to set one.');
  await expect(tid(page, 'cc-map-class-row-EARN:BASIC-SELLING')).toBeVisible();
  await expect(tid(page, 'cc-map-class-row-EARN:BASIC-SELLING')).toContainText('Selling');
  await shot(page, info, 'cc-en-mappings-group');
});

test('company view: the fixed cost centre offers only this company\'s active cost centres, never a null-company one', async ({ page }) => {
  await boot(page);
  const select = tid(page, 'cc-map-fixed-EARN:HOUSING');
  await expect(select).toBeEnabled();
  await expect(select).toHaveValue('cc-admin');
  const values = await select.locator('option').evaluateAll((options) => options.map((o) => (o as HTMLOptionElement).value));
  expect(values).toEqual(['', 'cc-admin', 'cc-sales']);
  await expect(tid(page, 'cc-map-group-scope-hint')).toHaveCount(0);
});

test('a reason appears once something is edited, is required to save, and travels with the class and fixed cost centre', async ({ page }, info) => {
  const state = await boot(page);
  await expect(tid(page, 'cc-map-reason')).toHaveCount(0);
  await expect(tid(page, 'cc-map-save')).toBeDisabled();
  await page.getByLabel('Account for Loan recovery').selectOption('acc-1400');
  await tid(page, 'cc-map-fixed-LOAN_RECEIVABLE').selectOption('cc-sales');
  await expect(tid(page, 'cc-map-reason')).toBeVisible();
  await expect(tid(page, 'cc-map-save')).toBeDisabled();
  await shot(page, info, 'cc-en-mappings-company-edit');
  await tid(page, 'cc-map-reason').fill('Sales loans are charged to Sales');
  await tid(page, 'cc-map-save').click();
  await expect(page.getByText('Mappings saved.')).toBeVisible();
  const put = lastMappingPut(state);
  expect(search(put).get('reason')).toBe('Sales loans are charged to Sales');
  expect(search(put).get('companyId')).toBe('co-sa');
  expect(put.body).toEqual([
    { driverKey: 'EARN:HOUSING', accountId: 'acc-5090', segmentCostCenterId: 'cc-admin', functionalClass: null },
    { driverKey: 'LOAN_RECEIVABLE', accountId: 'acc-1400', segmentCostCenterId: 'cc-sales', functionalClass: null },
  ]);
});

test('picking a functional class turns the fixed cost centre off, and picking a fixed cost centre turns the class off', async ({ page }) => {
  await boot(page);
  const klass = tid(page, 'cc-map-class-EARN:HOUSING');
  const fixed = tid(page, 'cc-map-fixed-EARN:HOUSING');
  // Housing starts with a fixed cost centre, so its class is off and the hint says why.
  await expect(klass).toBeDisabled();
  await expect(tid(page, 'cc-map-exclusive-hint-EARN:HOUSING')).toHaveText('A posting rule uses either a functional class or a fixed cost centre, not both.');
  await fixed.selectOption('');
  await expect(klass).toBeEnabled();
  await klass.selectOption('SELLING');
  await expect(fixed).toBeDisabled();
  await expect(tid(page, 'cc-map-exclusive-hint-EARN:HOUSING')).toBeVisible();
  await expect(klass.locator('option[value="CAPITALISABLE"]')).toBeDisabled();
});

test('a row with no account has nothing to classify; clearing the account clears its class and fixed cost centre', async ({ page }) => {
  await boot(page);
  await expect(tid(page, 'cc-map-class-LOAN_RECEIVABLE')).toBeDisabled();
  await expect(tid(page, 'cc-map-fixed-LOAN_RECEIVABLE')).toBeDisabled();
  await page.getByLabel('Account for Housing allowance').selectOption('');
  await expect(tid(page, 'cc-map-fixed-EARN:HOUSING')).toHaveValue('');
  await expect(tid(page, 'cc-map-fixed-EARN:HOUSING')).toBeDisabled();
});

test('add a rule for a class offers only unused classes, and removing it returns the class', async ({ page }) => {
  await boot(page);
  const add = tid(page, 'cc-map-add-class-EARN:HOUSING');
  const values = () => add.locator('option').evaluateAll((options) => options.map((o) => (o as HTMLOptionElement).value));
  await expect(add).toBeEnabled();
  expect(await values()).toEqual(['', 'COGS', 'SELLING', 'G_AND_A', 'R_AND_D', 'OPERATIONS', 'CAPITALISABLE']);
  await expect(add.locator('option[value="CAPITALISABLE"]')).toBeDisabled();
  await add.selectOption('COGS');
  const classRow = tid(page, 'cc-map-class-row-EARN:HOUSING-COGS');
  await expect(classRow).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Cost of goods sold' })).toBeVisible();
  expect(await values()).not.toContain('COGS');
  await classRow.getByRole('combobox').selectOption('acc-5002');
  await expect(tid(page, 'cc-map-reason')).toBeVisible();
  await tid(page, 'cc-map-remove-EARN:HOUSING-COGS').click();
  await expect(classRow).toHaveCount(0);
  expect(await values()).toContain('COGS');
});

test('the same class twice on one line is an inline error and blocks Save', async ({ page }) => {
  await boot(page);
  await page.getByLabel('Account for Basic salary').selectOption('acc-5001');
  await tid(page, 'cc-map-class-EARN:BASIC').selectOption('SELLING');
  await expect(tid(page, 'cc-map-dup-error-EARN:BASIC')).toHaveText('This line already has a rule for this class.');
  await tid(page, 'cc-map-reason').fill('Test');
  await expect(tid(page, 'cc-map-save')).toBeDisabled();
});

test('M-7: a refusal while a run awaits lock shows the server sentence with a link to the run, and every control is off', async ({ page }) => {
  const state = await boot(page);
  state.ctl.mappingRefusal = { status: 409, body: lockedBody };
  await page.getByLabel('Account for Loan recovery').selectOption('acc-1400');
  await tid(page, 'cc-map-reason').fill('Policy');
  await tid(page, 'cc-map-save').click();
  const banner = tid(page, 'cc-map-locked-banner');
  await expect(banner).toContainText(M7_EN);
  await expect(banner.getByRole('link', { name: 'Open the approved run', exact: true })).toHaveAttribute('href', '/payroll');
  await expect(page.getByLabel('Account for Loan recovery')).toBeDisabled();
  await expect(tid(page, 'cc-map-save')).toBeDisabled();
});

test('another refusal shows above the table in the server\'s words, and the edits are kept', async ({ page }) => {
  const state = await boot(page);
  state.ctl.mappingRefusal = { status: 422, body: { error: 'fixed_cost_centre_invalid', code: 'fixed_cost_centre_invalid', message: 'The fixed cost centre must be an active cost centre of this company.', messageAr: 'يجب أن يكون مركز التكلفة الثابت مركزًا نشطًا تابعًا لهذه الشركة.' } };
  await page.getByLabel('Account for Loan recovery').selectOption('acc-1400');
  await tid(page, 'cc-map-reason').fill('Policy');
  await tid(page, 'cc-map-save').click();
  await expect(tid(page, 'cc-map-error')).toHaveText('The fixed cost centre must be an active cost centre of this company.');
  await expect(page.getByLabel('Account for Loan recovery')).toHaveValue('acc-1400');
  await expect(tid(page, 'cc-map-locked-banner')).toHaveCount(0);
});

test('a read-only reader sees the mapping with every control off, no reason field and no Save', async ({ page }) => {
  await boot(page, { readOnly: true });
  await expect(page.getByLabel('Account for Housing allowance')).toBeDisabled();
  await expect(tid(page, 'cc-map-fixed-EARN:HOUSING')).toBeDisabled();
  await expect(tid(page, 'cc-map-class-EARN:HOUSING')).toBeDisabled();
  await expect(tid(page, 'cc-map-save')).toHaveCount(0);
  await expect(tid(page, 'cc-map-reason')).toHaveCount(0);
  await expect(tid(page, 'cc-map-add-class-EARN:HOUSING')).toHaveCount(0);
});

test('Arabic mappings: RTL, translated headers and classes, and the server\'s Arabic refusal', async ({ page }, info) => {
  const state = await boot(page, { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const region = mapping(page);
  await expect(region.getByRole('columnheader', { name: 'التصنيف الوظيفي للتكلفة', exact: true })).toBeVisible();
  await expect(region.getByRole('columnheader', { name: 'مركز التكلفة الثابت', exact: true })).toBeVisible();
  await expect(tid(page, 'cc-map-class-row-EARN:BASIC-SELLING')).toContainText('البيع والتسويق');
  await shot(page, info, 'cc-ar-mappings-company');
  state.ctl.mappingRefusal = { status: 409, body: lockedBody };
  await page.getByLabel('حساب Loan recovery').selectOption('acc-1400');
  await tid(page, 'cc-map-reason').fill('سياسة');
  await tid(page, 'cc-map-save').click();
  await expect(tid(page, 'cc-map-locked-banner')).toContainText(M7_AR);
  expect(state.errors).toEqual([]);
});

test('axe: the mapping table, with a class row open, has no serious or critical violations', async ({ page }) => {
  await boot(page);
  await tid(page, 'cc-map-add-class-EARN:HOUSING').selectOption('COGS');
  await tid(page, 'cc-map-class-row-EARN:HOUSING-COGS').waitFor();
  expect(await axeSeriousOrWorse(page, '[aria-labelledby="gl-map-heading"]')).toEqual([]);
});


// ═════════════════════════════ Screen 5: Cost accounting readiness and the switch ═════════════════════════════

const openReadiness = async (page: Page) => {
  await page.getByRole('tab', { name: 'Cost accounting', exact: true }).click();
  await expect(tid(page, 'cc-ready-switch')).toBeVisible();
};
const switchEl = (page: Page) => tid(page, 'cc-ready-switch');

test('the Cost accounting tab names the company, shows the switch as text as well as a control, and lists every check with its severity and count', async ({ page }, info) => {
  const state = await boot(page, { groupMode: true });
  await openReadiness(page);
  await expect(tid(page, 'cc-ready-company')).toBeVisible();
  await expect(switchEl(page)).toHaveAttribute('role', 'switch');
  await expect(switchEl(page)).toHaveAttribute('aria-checked', 'false');
  await expect(page.getByTestId('cc-ready-state')).toHaveText('Cost accounting is off');
  expect(state.reads.some((r) => r.path === '/api/finance/gl/cost-accounting/readiness' && search(r).get('companyId') === 'co-sa')).toBe(true);
  await expect(tid(page, 'cc-ready-check-count-employees-unresolved')).toHaveText('12');
  await expect(tid(page, 'cc-ready-check-employees-unresolved')).toContainText('Employees with no cost centre');
  await expect(tid(page, 'cc-ready-check-employees-unresolved')).toContainText('Blocker');
  await expect(tid(page, 'cc-ready-check-accounts-no-rule')).toContainText('Warning');
  await expect(tid(page, 'cc-ready-check-rounding-default')).toContainText('OK');
  await expect(tid(page, 'cc-ready-check-count-no-company')).toHaveText('2');
  await shot(page, info, 'cc-en-readiness');
});

test('a check expands to its capped list with links, and says how many more there are', async ({ page }) => {
  await boot(page);
  await openReadiness(page);
  const toggle = tid(page, 'cc-ready-check-employees-unresolved');
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  const list = tid(page, 'cc-ready-check-list-employees-unresolved');
  await expect(list).toContainText('E-0412');
  await expect(list).toContainText('E-0431');
  await expect(list.getByRole('link', { name: /E-0412/ })).toHaveAttribute('href', /412/);
  await expect(list).toContainText('…and 10 more.');
});

test('fix links: employees go to Departments, accounts without a rule go to the chart, the rounding check to the default field', async ({ page }) => {
  await boot(page);
  await openReadiness(page);
  await expect(tid(page, 'cc-ready-check-fix-employees-unresolved')).toHaveAttribute('href', '/setup?tab=departments');
  await expect(tid(page, 'cc-ready-check-fix-no-company')).toHaveAttribute('href', '/setup?tab=costCenters');
  await tid(page, 'cc-ready-check-fix-accounts-no-rule').click();
  await expect(page.getByRole('tab', { name: 'Accounts & Mapping', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(tid(page, 'cc-acct-preset-open')).toBeVisible();
});

test('"Open Cost accounting" from the account rules note opens this tab for that company', async ({ page }) => {
  await boot(page);
  await tid(page, 'cc-acct-costing-off-note').getByRole('button', { name: 'Open Cost accounting', exact: true }).click();
  await expect(page.getByRole('tab', { name: 'Cost accounting', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(switchEl(page)).toBeVisible();
});

test('turning on with blockers: the dialog lists them, and Turn on anyway needs the acknowledgement and a reason', async ({ page }, info) => {
  const state = await boot(page);
  await openReadiness(page);
  await switchEl(page).click();
  const dialog = tid(page, 'cc-on-dialog');
  await expect(dialog).toBeVisible();
  await expect(page.getByRole('dialog', { name: 'Turn on cost accounting for Masar Trading KSA?' })).toBeVisible();
  await expect(tid(page, 'cc-on-dialog-blocker-employees-unresolved')).toContainText('12');
  await expect(tid(page, 'cc-on-dialog-blocker-invalid-reference')).toContainText('3');
  await expect(tid(page, 'cc-on-dialog-blocker-no-company')).toContainText('2');
  await expect(dialog).toContainText('1 cost centre holds fewer than 3 employees');
  const confirm = tid(page, 'cc-on-dialog-confirm');
  await expect(confirm).toHaveText('Turn on anyway');
  await expect(confirm).toBeDisabled();
  await tid(page, 'cc-on-dialog-reason').fill('Pilot with the Sales team');
  await expect(confirm).toBeDisabled();
  await shot(page, info, 'cc-en-turn-on-dialog');
  await tid(page, 'cc-on-dialog-ack').check();
  await expect(confirm).toBeEnabled();
  await confirm.click();
  await expect(page.getByText('Cost accounting turned on.')).toBeVisible();
  const put = state.writes.find((w) => w.method === 'PUT' && w.path === '/api/finance/gl/cost-accounting')!;
  expect(search(put).get('companyId')).toBe('co-sa');
  expect(put.body).toEqual({ enabled: true, reason: 'Pilot with the Sales team', acknowledgeBlockers: true });
  await expect(switchEl(page)).toHaveAttribute('aria-checked', 'true');
  await expect(page.getByTestId('cc-ready-state')).toHaveText('Cost accounting is on');
});

test('"Fix first" closes the dialog and leaves the switch off', async ({ page }) => {
  const state = await boot(page);
  await openReadiness(page);
  await switchEl(page).click();
  await tid(page, 'cc-on-dialog-fix-first').click();
  await expect(tid(page, 'cc-on-dialog')).toHaveCount(0);
  await expect(switchEl(page)).toHaveAttribute('aria-checked', 'false');
  expect(state.writes.filter((w) => w.method === 'PUT')).toHaveLength(0);
});

test('with nothing in the way: "Turn on", only a reason is needed, and the acknowledgement is not shown', async ({ page }) => {
  await boot(page, { allClear: true });
  await openReadiness(page);
  await expect(tid(page, 'cc-ready-allclear')).toHaveText('Ready. Nothing blocks the next Lock.');
  await switchEl(page).click();
  await expect(tid(page, 'cc-on-dialog')).toContainText('Nothing blocks the next Lock.');
  await expect(tid(page, 'cc-on-dialog-ack')).toHaveCount(0);
  await expect(tid(page, 'cc-on-dialog-confirm')).toHaveText('Turn on');
  await tid(page, 'cc-on-dialog-reason').fill('Go live');
  await expect(tid(page, 'cc-on-dialog-confirm')).toBeEnabled();
});

test('the dialog shows a loading state, and a failed dry-run keeps Turn on off because the user cannot see what they would agree to', async ({ page }) => {
  const state = await boot(page);
  await openReadiness(page);
  state.ctl.readinessDelayMs = 800;
  await switchEl(page).click();
  await expect(tid(page, 'cc-on-dialog-loading')).toBeVisible();
  await expect(tid(page, 'cc-on-dialog-blocker-employees-unresolved')).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
  state.ctl.readinessDelayMs = 0;
  state.ctl.readinessDown = true;
  await switchEl(page).click();
  await expect(tid(page, 'cc-on-dialog-error')).toBeVisible();
  await expect(tid(page, 'cc-on-dialog-confirm')).toBeDisabled();
});

test('a refusal on turn-on (blockers changed since the preview) shows the server\'s sentence in the dialog', async ({ page }) => {
  const state = await boot(page);
  await openReadiness(page);
  await switchEl(page).click();
  state.ctl.switchRefusal = { status: 409, body: { error: 'cost_accounting_blockers', code: 'cost_accounting_blockers', message: 'Cost accounting has readiness blockers or small-cell exposure. Fix them, or acknowledge them to turn it on anyway.', messageAr: 'هناك موانع' } };
  await tid(page, 'cc-on-dialog-reason').fill('x');
  await tid(page, 'cc-on-dialog-ack').check();
  await tid(page, 'cc-on-dialog-confirm').click();
  await expect(tid(page, 'cc-on-dialog')).toContainText('Cost accounting has readiness blockers or small-cell exposure.');
  await expect(switchEl(page)).toHaveAttribute('aria-checked', 'false');
});

test('turning off asks for a reason, and the server\'s refusal once costed runs are locked is shown in the dialog', async ({ page }) => {
  const state = await boot(page, { costingOn: true });
  await openReadiness(page);
  await expect(switchEl(page)).toHaveAttribute('aria-checked', 'true');
  await switchEl(page).click();
  await expect(tid(page, 'cc-off-dialog')).toContainText('Payroll stops splitting by cost centre. Lines already posted keep their cost centres.');
  await expect(tid(page, 'cc-off-dialog-confirm')).toBeDisabled();
  await tid(page, 'cc-off-dialog-reason').fill('Rolling back');
  state.ctl.switchRefusal = { status: 409, body: { error: 'cost_accounting_has_costed_runs', code: 'cost_accounting_has_costed_runs', message: 'Cost accounting cannot be turned off because a payroll run of this company was already locked with cost centres. Contact support if it must be reversed.', messageAr: 'لا يمكن إيقاف' } };
  await tid(page, 'cc-off-dialog-confirm').click();
  await expect(tid(page, 'cc-off-dialog')).toContainText('already locked with cost centres');
  state.ctl.switchRefusal = null;
  await tid(page, 'cc-off-dialog-confirm').click();
  await expect(page.getByText('Cost accounting turned off.')).toBeVisible();
  const puts = state.writes.filter((w) => w.method === 'PUT' && w.path === '/api/finance/gl/cost-accounting');
  expect(puts.at(-1)!.body).toEqual({ enabled: false, reason: 'Rolling back' });
  await expect(switchEl(page)).toHaveAttribute('aria-checked', 'false');
});

test('M-7: while a run awaits finance review or lock the switch and the default are off, with the sentence beside them', async ({ page }) => {
  await boot(page, { awaitingRun: true });
  await openReadiness(page);
  await expect(switchEl(page)).toBeDisabled();
  await expect(tid(page, 'cc-ready-default-cc')).toBeDisabled();
  await expect(tid(page, 'cc-ready-locked-banner')).toContainText(M7_EN);
  await expect(switchEl(page)).toHaveAttribute('aria-describedby', /.+/);
});

test('a read-only reader can read the checks but not change anything, and is told why', async ({ page }) => {
  await boot(page, { readOnly: true });
  await openReadiness(page);
  await expect(tid(page, 'cc-ready-readonly-note')).toContainText('You can view readiness but not change it. Changing needs the Manage GL permission.');
  await expect(switchEl(page)).toBeDisabled();
  await expect(tid(page, 'cc-ready-default-cc')).toBeDisabled();
  await tid(page, 'cc-ready-check-invalid-reference').click();
  await expect(tid(page, 'cc-ready-check-list-invalid-reference')).toContainText('ANALYST');
});

test('the default cost centre lists this company\'s active cost centres, and changing it asks for a reason', async ({ page }) => {
  const state = await boot(page);
  await openReadiness(page);
  const select = tid(page, 'cc-ready-default-cc');
  await expect(select).toHaveValue('cc-admin');
  const values = await select.locator('option').evaluateAll((options) => options.map((o) => (o as HTMLOptionElement).value));
  expect(values).toEqual(['', 'cc-admin', 'cc-sales']);
  await expect(tid(page, 'cc-ready-default-reason')).toHaveCount(0);
  await select.selectOption('cc-sales');
  await expect(tid(page, 'cc-ready-default-save')).toBeDisabled();
  await tid(page, 'cc-ready-default-reason').fill('Sales carries the pool');
  await tid(page, 'cc-ready-default-save').click();
  await expect(page.getByText('Saved.')).toBeVisible();
  const put = state.writes.find((w) => w.method === 'PUT' && w.path === '/api/finance/gl/cost-accounting')!;
  expect(put.body).toEqual({ defaultCostCenterId: 'cc-sales', reason: 'Sales carries the pool' });
});

test('a readiness load failure shows the reason and Retry, never an empty list', async ({ page }) => {
  const state = await boot(page);
  state.ctl.readinessDown = true;
  await page.getByRole('tab', { name: 'Cost accounting', exact: true }).click();
  const alert = page.getByRole('alert').filter({ hasText: 'This could not be loaded.' });
  await expect(alert).toBeVisible();
  await expect(tid(page, 'cc-ready-check-employees-unresolved')).toHaveCount(0);
  state.ctl.readinessDown = false;
  await alert.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(tid(page, 'cc-ready-check-employees-unresolved')).toBeVisible();
});

test('Arabic readiness: RTL, translated checks, plural count, and the dialog', async ({ page }, info) => {
  const state = await boot(page, { locale: 'ar' });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await page.getByRole('tab', { name: 'محاسبة التكلفة', exact: true }).click();
  await expect(tid(page, 'cc-ready-check-employees-unresolved')).toContainText('موظفون بلا مركز تكلفة');
  await expect(tid(page, 'cc-ready-check-employees-unresolved')).toContainText('عائق');
  await shot(page, info, 'cc-ar-readiness');
  await switchEl(page).click();
  await expect(tid(page, 'cc-on-dialog')).toContainText('إذا فعّلتها الآن فسيتعذّر قفل مسير الرواتب التالي بسبب');
  await shot(page, info, 'cc-ar-turn-on-dialog');
  expect(state.errors).toEqual([]);
});

test('axe: the readiness tab, and the turn-on dialog open over it, have no serious or critical violations', async ({ page }) => {
  await boot(page);
  await openReadiness(page);
  await tid(page, 'cc-ready-check-employees-unresolved').click();
  expect(await axeSeriousOrWorse(page, 'main')).toEqual([]);
  await switchEl(page).click();
  await expect(tid(page, 'cc-on-dialog-blocker-employees-unresolved')).toBeVisible();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});


// ═════════════════════════════ Layout at laptop widths ═════════════════════════════
// A finance user at 1280 or 1440 must see every column, and the fixed cost centre control, without hunting for a
// horizontal scrollbar, and nothing may be cut off by an ancestor that hides overflow.

for (const size of [{ width: 1280, height: 800 }, { width: 1440, height: 900 }]) {
  for (const locale of ['en', 'ar'] as const) {
    test(`layout ${size.width}x${size.height} ${locale}: the chart and the mapping table show every column without scrolling or clipping`, async ({ page }, info) => {
      await page.setViewportSize(size);
      await boot(page, { locale });
      await expect(tid(page, 'cc-map-fixed-EARN:HOUSING')).toBeVisible();
      await expect(chart(page).getByRole('cell', { name: '5091', exact: true })).toBeVisible();
      const problems = await page.evaluate(() => {
        const out: string[] = [];
        const vw = document.documentElement.clientWidth;
        const clips = (v: string) => v === 'hidden' || v === 'clip' || v === 'auto' || v === 'scroll';
        const check = (label: string, el: Element) => {
          const r = el.getBoundingClientRect();
          if (r.width === 0) return;
          if (r.left < -0.5 || r.right > vw + 0.5) out.push(`${label} outside the viewport`);
          for (let a = el.parentElement; a && a !== document.body; a = a.parentElement) {
            const cs = getComputedStyle(a);
            if (!clips(cs.overflowX)) continue;
            const ar = a.getBoundingClientRect();
            if (r.left < ar.left - 0.5 || r.right > ar.right + 0.5) {
              out.push(`${label} clipped by ${a.tagName.toLowerCase()}${a.getAttribute('data-testid') ? '#' + a.getAttribute('data-testid') : ''}`);
              break;
            }
          }
        };
        for (const [name, sel, extra] of [
          ['chart', '[role="group"][aria-labelledby="gl-coa-heading"]', ['tbody tr:not(:first-child) td']],
          ['mapping', '[role="group"][aria-labelledby="gl-map-heading"]', ['[data-testid="cc-map-fixed-EARN:HOUSING"]', '[data-testid="cc-map-add-class-EARN:HOUSING"]']],
        ] as const) {
          const wrap = document.querySelector(sel) as HTMLElement | null;
          if (!wrap) { out.push(`${name}: table not found`); continue; }
          if (wrap.scrollWidth > wrap.clientWidth + 1) {
            const wr = wrap.getBoundingClientRect();
            const wide = [...wrap.querySelectorAll('*')].filter((e) => e.tagName !== 'OPTION' && (e.getBoundingClientRect().right > wr.right - 1 || e.getBoundingClientRect().left < wr.left)).slice(0, 4).map((e) => `${e.tagName.toLowerCase()}[${(e.textContent ?? '').slice(0, 30)}|${e.className}]${Math.round(e.getBoundingClientRect().right - wr.right)}`);
            out.push(`${name}: scrolls horizontally (${wrap.scrollWidth} > ${wrap.clientWidth}) by ${wide.join(', ')}`);
          }
          wrap.querySelectorAll('thead th').forEach((th) => check(`${name} header "${th.textContent?.trim()}"`, th));
          for (const e of extra) wrap.querySelectorAll(e).forEach((el) => check(`${name} ${e}`, el));
        }
        return out;
      });
      expect(problems).toEqual([]);
      await shot(page, info, `cc-${locale}-layout-${size.width}`);
    });
  }
}

test('a long account name is cut with an ellipsis and keeps its full text in a title', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await boot(page);
  const name = chart(page).getByRole('row').filter({ hasText: '5091' }).locator('[data-testid="cc-acct-name-5091"]');
  await expect(name).toHaveAttribute('title', 'Remote Location Hardship Supplement for Long-Term Contractors and Seconded Staff');
  expect(await name.evaluate((el) => getComputedStyle(el).textOverflow)).toBe('ellipsis');
});


// ═════════════════════════════ Polish: names keep their beginning in RTL; the inherited badge is one line ═════════════════════════════

test('AR 1280: a Latin account name is cut at its end, never its beginning, and the title keeps the whole name', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await boot(page, { locale: 'ar', groupMode: true });
  const name = tid(page, 'cc-acct-name-5001');
  await expect(name).toHaveAttribute('title', 'Basic Salary Expense');
  const view = await name.evaluate((el) => {
    const text = el.firstChild as Text;
    const first = document.createRange();
    first.setStart(text, 0);
    first.setEnd(text, 1);
    const f = first.getBoundingClientRect();
    const box = el.getBoundingClientRect();
    return { direction: getComputedStyle(el).direction, firstCharInside: f.left >= box.left - 0.5 && f.right <= box.right + 0.5, firstChar: text.data[0] };
  });
  expect(view.firstChar).toBe('B');
  expect(view.direction).toBe('ltr');
  expect(view.firstCharInside).toBe(true);
});

for (const locale of ['en', 'ar'] as const) {
  test(`${locale} 1280: the inherited-from-group badge is one line, with the full sentence for tooltip and screen readers`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await boot(page, { locale });
    const badges = page.getByTestId('cc-inherited-badge');
    await expect(badges.first()).toBeVisible();
    const sizes = await badges.evaluateAll((els) => els.map((el) => {
      const cs = getComputedStyle(el);
      const lh = parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) * 1.2;
      return { height: el.getBoundingClientRect().height, lh };
    }));
    expect(sizes.length).toBeGreaterThan(3);
    for (const s of sizes) expect(s.height).toBeLessThanOrEqual(s.lh * 1.5 + 8);
    for (const s of sizes) expect(s.height).toBeLessThan(s.lh * 2 + 4);
    const first = badges.first();
    await expect(first).toHaveAttribute('title', locale === 'ar' ? 'محدد على مستوى المجموعة ولم يُخصَّص لهذه الشركة.' : 'Set at group level and not overridden for this company.');
    await expect(first).toContainText(locale === 'ar' ? 'موروث من المجموعة' : 'Inherited from group');
    await expect(first.locator('[aria-hidden="true"]')).toHaveText(locale === 'ar' ? 'المجموعة' : 'Group');
  });
}


test('AR 1280: the account and fixed cost centre selects start at the code, because a Latin label reads left to right', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await boot(page, { locale: 'ar' });
  for (const sel of [page.getByLabel('حساب Housing allowance'), tid(page, 'cc-map-fixed-EARN:HOUSING')]) {
    expect(await sel.evaluate((el) => (el as HTMLSelectElement).selectedOptions[0].text)).toMatch(/^[0-9A-Z]/);
    expect(await sel.evaluate((el) => getComputedStyle(el).direction)).toBe('ltr');
  }
  // An Arabic placeholder stays right to left.
  expect(await page.getByLabel('حساب Basic salary').evaluate((el) => getComputedStyle(el).direction)).toBe('rtl');
});

test('AR: the mapping sub-line keeps each interpolated account code and name in its own isolated run (bdi), and the text is unchanged', async ({ page }) => {
  await boot(page, { locale: 'ar' });
  // Company view, inheriting the group mapping: two accounts in one Arabic sentence.
  const line = tid(page, 'cc-map-fallback-EARN:BASIC');
  await expect(line).toHaveText('الحساب الاحتياطي 5001 — Basic Salary Expense · يرث 5001 — Basic Salary Expense');
  const runs = line.locator('bdi');
  await expect(runs).toHaveCount(2);
  await expect(runs.first()).toHaveText('5001 — Basic Salary Expense');
  // The isolate is a bdi element, not a control character in the text.
  expect(await line.evaluate((el) => /[\u2066-\u2069\u200e\u200f]/.test(el.textContent ?? ''))).toBe(false);
  // One account: one run.
  await expect(tid(page, 'cc-map-fallback-EARN:HOUSING').locator('bdi')).toHaveCount(1);
});


// ═════════════════════════════ AM-1: where each blocked employee is fixed ═════════════════════════════

const blockerReadiness = () => {
  const r = readiness('co-sa', {}, false);
  r.checks[0].sample = [
    { id: 450, code: 'E-0450', employeeId: 450, employeeCode: 'E-0450', level: 'parentDepartment', departmentId: 'dep-a', positionId: null, parentDepartmentId: 'dep-p', reason: 'parent_department_cost_centre_invalid' },
    { id: 431, code: 'E-0431', employeeId: 431, employeeCode: 'E-0431', level: 'company', departmentId: null, positionId: 'pos-1', parentDepartmentId: null, reason: 'none' },
    { id: 412, code: 'E-0412', employeeId: 412, employeeCode: 'E-0412', level: 'employee', departmentId: 'dep-cs', positionId: null, parentDepartmentId: null, reason: 'employee_cost_centre_invalid' },
  ] as never;
  r.checks[0].count = 3;
  return r;
};

test('AM-1: each blocked employee says why and links to the record to fix, the nearest cause first', async ({ page }, info) => {
  await boot(page);
  await page.route('**/api/finance/gl/cost-accounting/readiness*', (route) => route.fulfill({ json: blockerReadiness() }));
  await openReadiness(page);
  await tid(page, 'cc-ready-check-employees-unresolved').click();
  const parentRow = tid(page, 'cc-ready-sample-employees-unresolved-450');
  await expect(parentRow).toContainText('Its parent department\'s cost centre is inactive, deleted or in another company.');
  await expect(parentRow.getByTestId('cc-ready-sample-fix-parentDepartment')).toHaveAttribute('href', '/setup?tab=departments&department=dep-p');
  await expect(parentRow.getByTestId('cc-ready-sample-fix-department')).toHaveAttribute('href', '/setup?tab=departments&department=dep-a');
  await expect(parentRow.getByTestId('cc-ready-sample-fix-employee')).toHaveAttribute('href', /450/);
  const positionRow = tid(page, 'cc-ready-sample-employees-unresolved-431');
  await expect(positionRow).toContainText('No cost centre at any level.');
  await expect(positionRow.getByTestId('cc-ready-sample-fix-position')).toHaveAttribute('href', '/setup?tab=designations');
  const employeeRow = tid(page, 'cc-ready-sample-employees-unresolved-412');
  await expect(employeeRow).toContainText('Its own cost centre is inactive, deleted or in another company.');
  await expect(employeeRow.getByTestId('cc-ready-sample-fix-department')).toHaveCount(0);
  expect(await axeSeriousOrWorse(page, '[data-testid="cc-ready-check-list-employees-unresolved"]')).toEqual([]);
  await shot(page, info, 'cc-en-readiness-blocker-detail');
});

test('AM-1 in Arabic: the reasons and the link names are translated, and the codes stay left to right', async ({ page }, info) => {
  await boot(page, { locale: 'ar' });
  await page.route('**/api/finance/gl/cost-accounting/readiness*', (route) => route.fulfill({ json: blockerReadiness() }));
  await page.getByRole('tab', { name: 'محاسبة التكلفة', exact: true }).click();
  await expect(tid(page, 'cc-ready-switch')).toBeVisible();
  await tid(page, 'cc-ready-check-employees-unresolved').click();
  const parentRow = tid(page, 'cc-ready-sample-employees-unresolved-450');
  await expect(parentRow.getByTestId('cc-ready-sample-fix-parentDepartment')).toHaveText('القسم الأعلى');
  await expect(parentRow.getByTestId('cc-ready-sample-fix-department')).toHaveText('القسم');
  await expect(parentRow.getByTestId('cc-ready-sample-fix-employee')).toHaveText('موظف');
  await expect(parentRow).toContainText('غير نشط أو محذوف أو يتبع شركة أخرى');
  await expect(tid(page, 'cc-ready-sample-employees-unresolved-431')).toContainText('لا يوجد مركز تكلفة على أي مستوى');
  await shot(page, info, 'cc-ar-readiness-blocker-detail');
});
