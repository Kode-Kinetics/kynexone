import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Request, type TestInfo } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Cost accounting v1 (WP-F phase 3) on the Payroll workspace: screen 4, the Lock preview on Payroll → GL Journal (cost-centre
 * column, grouping, blockers with fix links, the lock confirmation and every refusal Lock can answer), the uncosted
 * acknowledgement on Approve, and screen 7, the "Payroll journal by cost centre" report on Payroll → Reports. Every API request is
 * intercepted (no backend, no real tenant), so this proves the UI contract (states, wiring, wording, EN and AR), not authorisation.
 *   npx next dev -p 5198 &
 *   PAYROLL_CC_BASE_URL=http://127.0.0.1:5198 npx playwright test -c e2e/playwright.payroll-cost-centre-journal.config.ts
 * CC_SHOTS_DIR, when set, also receives the review screenshots.
 */

const companySa = { id: 'co-sa', name: 'Masar Trading KSA', tradeName: 'Masar KSA', defaultCurrency: 'SAR', wpsEmployerId: '', gosiEmployerId: '' };
const companyAe = { id: 'co-ae', name: 'Masar Services UAE', tradeName: 'Masar UAE', defaultCurrency: 'AED', wpsEmployerId: '', gosiEmployerId: '' };

const run = (over: Record<string, unknown>) => ({
  id: 'run-jun', companyId: 'co-sa', year: 2026, month: 6, status: 'Approved', runType: 'Regular', totalGrossSalary: 412300, totalDeductions: 0,
  totalNetSalary: 400000, totalEmployerStatutoryCost: 0, employeeCount: 48, createdAtUtc: '2026-06-25T08:00:00Z', processedAtUtc: '2026-06-26T08:00:00Z', lockedAtUtc: null, ...over,
});

const M1_EN = '12 employee(s) have no cost centre, and account 5001 Basic salary requires one. Assign a cost centre to each employee, their position, their department, or set a company default cost centre, then lock again.';
const M1_AR = 'لدى 12 موظف/موظفين لا يوجد مركز تكلفة، والحساب 5001 Basic salary يتطلب مركز تكلفة. عيّن مركز تكلفة للموظف أو لمنصبه أو لقسمه، أو حدّد مركز تكلفة افتراضيًا للشركة، ثم أعد القفل.';
const M2_EN = 'The cost centre set at the Position level is inactive, deleted or belongs to another company, so cost cannot be assigned. Fix it in Setup, then lock again.';
const M2_AR = 'مركز التكلفة المحدد على مستوى المنصب غير نشط أو محذوف أو يتبع شركة أخرى، لذا لا يمكن توزيع التكلفة. صحّحه من الإعدادات ثم أعد القفل.';
const M6_EN = 'Cost-centre assignments or posting rules changed after this run was approved, so it was sent back for approval. Process and approve it again.';
const M6_AR = 'تغيّرت تعيينات مراكز التكلفة أو قواعد الترحيل بعد اعتماد هذه المسيرة، لذا أُعيدت للاعتماد. أعد المعالجة والاعتماد.';
const SOD_EN = 'You changed cost-accounting settings for this company since the last lock, so someone else must lock or export this run.';
const SOD_AR = 'لقد غيّرت إعدادات محاسبة التكلفة لهذه الشركة منذ آخر قفل، لذا يجب أن يقوم مستخدم آخر بقفل هذه المسيرة أو تصديرها.';

interface Options {
  locale?: 'en' | 'ar';
  /** Costing is on for the company. Default true. */
  costingOn?: boolean;
  /** The preview carries blockers (12 employees without a cost centre on 5001, 1 invalid at the position level). */
  blocked?: boolean;
  /** The preview carries only the rounding refusal. */
  roundingBlocked?: boolean;
  /** No payroll.lock: the preview can be read but not locked. */
  readOnly?: boolean;
  /** The selected run is already locked and was locked before cost centres. */
  legacyLocked?: boolean;
  /** Six employees post Optional lines uncosted, so Approve needs the acknowledgement. */
  uncosted?: boolean;
  /** Both companies have an Approved run (group view). */
  twoCompanies?: boolean;
  /** The user has finance.gl.read (the report). Default true. */
  glRead?: boolean;
}

interface Recorded { method: string; path: string; search: string; body: unknown }

const ccLines = (blocked: boolean) => [
  { componentCode: 'BASIC', componentName: 'Basic salary', glAccount: '5001 — Basic salary', glAccountName: 'Basic salary', entryType: 'DR', amount: 180000, costCentreCode: 'SALES', costCentreName: 'Sales' },
  { componentCode: 'BASIC', componentName: 'Basic salary', glAccount: '5001 — Basic salary', glAccountName: 'Basic salary', entryType: 'DR', amount: 120000, costCentreCode: 'OPS', costCentreName: 'Operations' },
  { componentCode: 'RECOV', componentName: 'Recovery', glAccount: '5009 — Recoveries', glAccountName: 'Recoveries', entryType: 'CR', amount: 2000, costCentreCode: 'SALES', costCentreName: 'Sales' },
  ...(blocked ? [{ componentCode: 'BASIC', componentName: 'Basic salary', glAccount: '5001 — Basic salary', glAccountName: 'Basic salary', entryType: 'DR', amount: 12000, costCentreCode: null, costCentreName: null }] : []),
  { componentCode: 'NET', componentName: 'Net pay', glAccount: '2100 — Salaries payable', glAccountName: 'Salaries payable', entryType: 'CR', amount: blocked ? 310000 : 298000, costCentreCode: null, costCentreName: null },
];

const blockerList = () => [
  ...Array.from({ length: 12 }, (_, i) => ({
    employeeId: 400 + i, employeeCode: `E-04${String(12 + i)}`, level: 'company', departmentId: i === 0 ? 'dep-cs' : null, positionId: i < 2 ? 'pos-1' : null,
    parentDepartmentId: null, reason: 'cost_centre_required', accountCode: '5001',
  })),
  { employeeId: 77, employeeCode: 'E-0077', level: 'position', departmentId: null, positionId: 'pos-9', parentDepartmentId: null, reason: 'cost_centre_invalid', accountCode: '5002' },
];
const groupList = () => [
  {
    error: 'cost_centre_invalid', level: 'Position', employeeCount: 1, employees: [{ id: 77, code: 'E-0077' }], amount: 900, message: M2_EN, messageAr: M2_AR,
  },
  {
    error: 'cost_centre_required', accountCode: '5001', accountName: 'Basic salary', employeeCount: 12,
    employees: Array.from({ length: 10 }, (_, i) => ({ id: 400 + i, code: `E-04${String(12 + i)}` })), amount: 12000, message: M1_EN, messageAr: M1_AR,
  },
];

function journalFor(options: Options, state: { fixed: boolean; locked: boolean }) {
  const blocked = !!options.blocked && !state.fixed;
  const on = options.costingOn !== false;
  const posted = state.locked || !!options.legacyLocked;
  const legacy = !!options.legacyLocked;
  const entries = !on || legacy
    ? ccLines(false).map((l) => ({ ...l, costCentreCode: null, costCentreName: null }))
    : ccLines(blocked);
  const debit = entries.filter((e) => e.entryType === 'DR').reduce((s, e) => s + e.amount, 0);
  const credit = entries.filter((e) => e.entryType === 'CR').reduce((s, e) => s + e.amount, 0);
  return {
    runId: 'run-jun', period: '2026-06', currency: 'SAR', isPosted: posted, entries,
    costing: {
      enabled: on,
      blockers: blocked ? blockerList() : [],
      groups: blocked ? groupList() : [],
      roundingBlocked: !!options.roundingBlocked,
      fallbackEmployees: on ? 3 : 0, fallbackAmount: on ? 4500 : 0,
      uncostedEmployees: options.uncosted ? 6 : 0, uncostedAmount: options.uncosted ? 7200 : 0,
    },
    totalDebits: Math.round(debit * 100) / 100, totalCredits: Math.round(credit * 100) / 100, isBalanced: Math.abs(debit - credit) < 0.01,
    catchAllComponents: [], blockingProblems: [], roundingAdjustment: null,
  };
}

const allocation = (over: Record<string, unknown> = {}) => ({
  runId: 'run-may', period: '2026-05', currency: 'SAR', basis: 'Stamped', label: null, totalEmployees: 23, totalGross: 1500, totalNet: 1350, totalEmployerCost: 150, totalCost: 1650,
  reconciles: true, retroNote: 'Reports show where cost was booked at the time.', retroNoteAr: 'تعرض التقارير مكان تحميل التكلفة وقت الترحيل.',
  allocations: [
    { costCenterId: 'cc-sales', costCenterCode: 'SALES', costCenterName: 'Sales', employeeCount: 14, grossSalary: 1000, netSalary: 900, employerCost: 100, totalCost: 1100, percentOfTotal: 66.67, contraDeductions: 50, journalNet: 1050, clearedAgainstAccrual: 0, roundingAdjustment: 0, smallCell: false },
    { costCenterId: 'cc-ops', costCenterCode: 'OPS', costCenterName: 'Operations', employeeCount: 9, grossSalary: 500, netSalary: 450, employerCost: 50, totalCost: 550, percentOfTotal: 33.33, contraDeductions: 0, journalNet: 550, clearedAgainstAccrual: 0, roundingAdjustment: 0, smallCell: false },
  ],
  ...over,
});

async function boot(page: Page, options: Options = {}, startTab: 'runs' | 'gl-journal' | 'approvals' | 'reports' = 'runs') {
  const writes: Recorded[] = [];
  const reads: Recorded[] = [];
  const errors: string[] = [];
  const state = {
    fixed: false, locked: false, runStatus: options.legacyLocked ? 'Locked' : 'Approved',
    lockRefusal: null as null | { status: number; body: unknown },
    journalDown: false, approveRefusal: null as null | { status: number; body: unknown },
    allocationDown: false, allocationResponse: allocation() as Record<string, unknown>, allocationFor: {} as Record<string, Record<string, unknown>>,
    approved: false,
  };
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'payroll-cc-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });

  const runs = () => [
    run({ status: state.locked ? 'Locked' : state.runStatus, lockedAtUtc: state.locked || options.legacyLocked ? '2026-06-30T08:00:00Z' : null }),
    run({ id: 'run-may', month: 5, status: 'Locked', lockedAtUtc: '2026-05-31T08:00:00Z' }),
    run({ id: 'run-proc', month: 7, status: 'Processed' }),
    ...(options.twoCompanies ? [run({ id: 'run-ae', companyId: 'co-ae', status: 'Approved' })] : []),
  ];

  await page.route('**/api/**', async (route) => {
    const request: Request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    const body = (() => { try { return request.postDataJSON(); } catch { return request.postData(); } })();
    const rec = { method, path: p, search: url.search, body };
    if (method === 'GET') reads.push(rec); else writes.push(rec);

    if (p === '/api/auth/me') {
      return reply({
        id: 'user-fin', tenantId: 'cc-tenant', tenantSlug: 'cc-fixture', fullName: 'Fatima Finance', roles: ['Admin'],
        permissions: ['payroll.read', 'payroll.approve', ...(options.readOnly ? [] : ['payroll.lock']), 'dashboard.read', ...(options.glRead === false ? [] : ['finance.gl.read'])],
        accountType: options.twoCompanies ? 'Group' : 'SingleCompany', isGroupScope: !!options.twoCompanies,
        companies: (options.twoCompanies ? [companySa, companyAe] : [companySa]).map((c) => ({ id: c.id, name: c.name, code: c.id, countryCode: 'SA', isActive: true })),
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);
    if (p === '/api/payroll/companies') return reply(options.twoCompanies ? [companySa, companyAe] : [companySa]);
    if (p === '/api/companies') return reply(paged([]));

    if (p === '/api/payroll/runs' && method === 'GET') return reply(paged(runs()));
    if (p === '/api/payroll/reports/summary') return reply({ totalRuns: 3, lockedRuns: 1, totalGrossYtd: 0, totalNetYtd: 0, ytdByCompany: [] });
    if (p === '/api/finance/gl/cost-accounting/readiness') {
      const companyId = url.searchParams.get('companyId');
      const enabled = companyId === 'co-ae' ? false : options.costingOn !== false;
      return reply({ companyId, settings: { enabled, defaultCostCenterId: 'cc-admin', smallCellMin: 0, awaitingRun: false }, checks: [], blockers: [], smallCellExposure: { count: 0, threshold: 3 } });
    }
    if (/^\/api\/payroll\/runs\/[^/]+\/gl-journal$/.test(p)) {
      if (state.journalDown) return reply({ message: 'The journal service is down.' }, 500);
      const j = journalFor(options, state);
      return reply(p.includes('run-ae') ? { ...j, runId: 'run-ae', costing: { ...j.costing, enabled: false } } : j);
    }
    if (p === '/api/payroll/runs/run-jun/lock' && method === 'POST') {
      if (state.lockRefusal) { const r = state.lockRefusal; state.lockRefusal = null; return reply(r.body, r.status); }
      state.locked = true;
      return reply(run({ status: 'Locked', lockedAtUtc: '2026-06-30T08:00:00Z' }));
    }
    if (/^\/api\/payroll\/runs\/[^/]+\/approvals$/.test(p)) return reply([]);
    if (/^\/api\/payroll\/runs\/[^/]+\/population$/.test(p)) return reply({ runId: 'run-proc', runType: 'Regular', includesRecurringPay: true, populationMode: 'All', eligibleCount: 48, includedCount: 48, excludedCount: 0, notEligibleCount: 0, included: [], excluded: [], notEligible: [] });
    if (/^\/api\/payroll\/runs\/[^/]+\/validation-overrides$/.test(p)) return reply({ runId: 'run-proc', overrides: [], overridableCodes: [], nonOverridableCodes: [], paidOutsideBankFile: [] });
    if (p === '/api/payroll/runs/run-proc/approve' && method === 'POST') {
      if (state.approveRefusal) { const r = state.approveRefusal; state.approveRefusal = null; return reply(r.body, r.status); }
      state.approved = true;
      return reply(run({ id: 'run-proc', month: 7, status: 'Approved' }));
    }
    const allocMatch = /^\/api\/payroll\/runs\/([^/]+)\/cost-center-allocation$/.exec(p);
    if (allocMatch) {
      if (state.allocationDown) return reply({ message: 'The report service is down.' }, 500);
      return reply(state.allocationFor[allocMatch[1]] ?? { ...state.allocationResponse, runId: allocMatch[1] });
    }
    if (p.startsWith('/api/finance/gl/journal-exports')) return reply([]);
    if (p.startsWith('/api/finance/gl/periods')) return reply([]);
    if (p.startsWith('/api/payroll/')) return reply({ message: 'Not part of this fixture.' }, 404);
    if (p.startsWith('/api/admin/') || p.includes('country-packs')) return reply([]);
    return reply(paged([]));
  });

  await page.goto('/payroll');
  const names = { runs: 'Payroll Runs', 'gl-journal': 'GL Journal', approvals: 'Approvals', reports: 'Reports' };
  await page.locator(`#payroll-tab-${startTab}`).click();
  await expect(page.locator(`#payroll-tab-${startTab}`)).toHaveAttribute('aria-selected', 'true', { timeout: 60_000 });
  void names;
  return { writes, reads, errors, state };
}

async function shot(page: Page, info: TestInfo, name: string) {
  const file = info.outputPath(`${name}.png`);
  await page.screenshot({ path: file, fullPage: true, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach(name, { path: file, contentType: 'image/png' });
  const dir = process.env.CC_SHOTS_DIR;
  if (dir) {
    fs.mkdirSync(dir, { recursive: true });
    fs.copyFileSync(file, path.join(dir, `${name}.png`));
  }
}

const tid = (page: Page, id: string) => page.getByTestId(id);
const axeSeriousOrWorse = async (page: Page, scope: string) => {
  const result = await new AxeBuilder({ page }).include(scope).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  return result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.nodes.map((n) => `${n.target.join(' ')} ${n.html.slice(0, 120)}`).join(' | ')}`);
};
const reviewAndLock = (page: Page) => tid(page, 'cc-lock-review-run-jun');
const openPreview = async (page: Page) => {
  await reviewAndLock(page).click();
  await expect(tid(page, 'cc-lock-preview')).toBeVisible();
};

// ═════════════════════════════ Screen 4: Lock preview ═════════════════════════════

test('with costing on, an Approved run offers "Review & lock" and opens the preview; with costing off it keeps the plain Lock', async ({ page }) => {
  await boot(page, { twoCompanies: true });
  await expect(reviewAndLock(page)).toHaveText('Review & lock');
  // The other company has costing off: its card keeps today's button.
  await expect(page.getByRole('button', { name: 'Lock', exact: true })).toHaveCount(1);
  await reviewAndLock(page).click();
  await expect(page.locator('#payroll-tab-gl-journal')).toHaveAttribute('aria-selected', 'true');
  await expect(tid(page, 'cc-lock-preview')).toBeVisible();
  await expect(tid(page, 'cc-lock-run-select')).toHaveValue('run-jun');
});

test('the preview groups by cost centre first, with Not costed last, a status chip, KPIs and a balanced journal', async ({ page }, info) => {
  const state = await boot(page);
  await openPreview(page);
  await expect(tid(page, 'cc-lock-status-chip')).toHaveText('Preview — not posted yet');
  await expect(tid(page, 'cc-lock-kpi-debits')).toContainText('300,000.00');
  await expect(tid(page, 'cc-lock-kpi-credits')).toContainText('300,000.00');
  await expect(tid(page, 'cc-lock-kpi-uncosted')).toContainText('0');
  await expect(tid(page, 'cc-lock-groupby-cc')).toHaveAttribute('aria-checked', 'true');
  const groups = page.locator('[data-testid^="cc-lock-group-"]');
  await expect(groups).toHaveCount(3);
  await expect(groups.nth(0)).toHaveAttribute('data-testid', 'cc-lock-group-OPS');
  await expect(groups.nth(1)).toHaveAttribute('data-testid', 'cc-lock-group-SALES');
  await expect(groups.nth(2)).toHaveAttribute('data-testid', 'cc-lock-group-none');
  await expect(tid(page, 'cc-lock-group-SALES')).toContainText('180,000.00');
  await expect(tid(page, 'cc-lock-group-SALES')).toContainText('Net 178,000.00');
  await expect(tid(page, 'cc-lock-group-none')).toContainText('Not costed (balance-sheet lines)');
  await expect(tid(page, 'cc-lock-blockers')).toHaveCount(0);
  await expect(tid(page, 'cc-lock-submit')).not.toHaveAttribute('aria-disabled', 'true');
  // (dev strict mode runs an effect twice, so the count is 1 in a production build and 2 here)
  expect(state.reads.filter((r) => r.path === '/api/payroll/runs/run-jun/gl-journal').length).toBeGreaterThan(0);
  expect(state.writes).toHaveLength(0);
  await shot(page, info, 'cc-en-lock-preview');
});

test('group by account lists each account with its cost centre on every line', async ({ page }) => {
  await boot(page);
  await openPreview(page);
  await tid(page, 'cc-lock-groupby-account').click();
  await expect(tid(page, 'cc-lock-groupby-account')).toHaveAttribute('aria-checked', 'true');
  await expect(tid(page, 'cc-lock-group-5001')).toContainText('Basic salary');
  await expect(tid(page, 'cc-lock-group-5001')).toContainText('SALES');
  await expect(tid(page, 'cc-lock-group-5001')).toContainText('OPS');
  await expect(page.getByRole('columnheader', { name: 'Cost centre', exact: true })).toBeVisible();
  // arrow keys move the radio selection back
  await tid(page, 'cc-lock-groupby-account').press('ArrowLeft');
  await expect(tid(page, 'cc-lock-groupby-cc')).toHaveAttribute('aria-checked', 'true');
});

test('search narrows the lines and says nothing matches when nothing does', async ({ page }) => {
  await boot(page);
  await openPreview(page);
  await tid(page, 'cc-lock-search').fill('recoveries');
  await expect(tid(page, 'cc-lock-group-SALES')).toBeVisible();
  await expect(tid(page, 'cc-lock-group-OPS')).toHaveCount(0);
  await tid(page, 'cc-lock-search').fill('zzz');
  await expect(page.getByText('No journal lines match.')).toBeVisible();
});

test('blockers: a role=alert banner with one card per group, up to 10 employees with fix links, and Lock disabled with its reason', async ({ page, context }, info) => {
  const state = await boot(page, { blocked: true, uncosted: true });
  await openPreview(page);
  const banner = tid(page, 'cc-lock-blockers');
  await expect(banner).toHaveAttribute('role', 'alert');
  await expect(banner.getByRole('heading', { name: 'Lock is blocked: 2 problems' })).toBeVisible();
  await expect(tid(page, 'cc-lock-blocker-cost_centre_required')).toContainText(M1_EN);
  await expect(tid(page, 'cc-lock-blocker-cost_centre_invalid')).toContainText(M2_EN);
  // the first group row: nothing at any level, so the department and the position come before the employee
  const row = tid(page, 'cc-lock-blocker-row-E-0412');
  await expect(row).toContainText('Nothing set at any level');
  const employeeLink = tid(page, 'cc-lock-fix-employee-E-0412');
  await expect(employeeLink).toHaveAttribute('target', '_blank');
  await expect(employeeLink).toHaveAttribute('rel', /noopener/);
  await expect(employeeLink).toHaveAttribute('href', '/people/400');
  await expect(employeeLink).toHaveAttribute('aria-label', 'Fix employee E-0412. Opens in a new tab');
  await expect(tid(page, 'cc-lock-fix-department-E-0412')).toHaveAttribute('href', /tab=departments&department=dep-cs/);
  await expect(tid(page, 'cc-lock-fix-position-E-0412')).toHaveAttribute('href', '/setup?tab=designations');
  await expect(tid(page, 'cc-lock-blocker-cost_centre_invalid').locator('[data-testid^="cc-lock-blocker-row-"]')).toHaveCount(1);
  await expect(tid(page, 'cc-lock-blocker-row-E-0077')).toContainText("The position's cost centre is inactive, deleted or in another company");
  // 12 employees, 10 listed
  await expect(tid(page, 'cc-lock-blocker-cost_centre_required').locator('[data-testid^="cc-lock-blocker-row-"]')).toHaveCount(10);
  await expect(tid(page, 'cc-lock-more-link')).toContainText('…and 2 more. See all in Cost accounting readiness.');
  await expect(tid(page, 'cc-lock-more-link')).toHaveAttribute('href', '/setup?tab=costAccounting');
  await expect(tid(page, 'cc-lock-set-default').first()).toHaveAttribute('href', '/setup?tab=costAccounting');
  // the fix link opens a new tab
  const [popup] = await Promise.all([context.waitForEvent('page'), employeeLink.click()]);
  expect(popup.url()).toContain('/people/400');
  await popup.close();
  // warnings do not block, and the missing group leads the table
  await expect(tid(page, 'cc-lock-warning-W1')).toContainText('6 employees have no cost centre; their Optional-account lines post uncosted.');
  await expect(tid(page, 'cc-lock-warning-W2')).toBeVisible();
  await expect(page.locator('[data-testid^="cc-lock-group-"]').first()).toHaveAttribute('data-testid', 'cc-lock-group-missing');
  await expect(tid(page, 'cc-lock-group-missing')).toContainText('Missing cost centre');
  // Lock stays focusable, says why, and sends nothing
  const submit = tid(page, 'cc-lock-submit');
  await expect(submit).toHaveAttribute('aria-disabled', 'true');
  await expect(submit).toHaveAccessibleDescription('Fix the problems above to lock.');
  await submit.focus();
  await expect(submit).toBeFocused();
  // Playwright treats aria-disabled as not actionable, so force the click a keyboard or pointer user could make.
  await submit.click({ force: true });
  expect(state.writes.filter((w) => w.path.endsWith('/lock'))).toHaveLength(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(await axeSeriousOrWorse(page, '[data-testid="cc-lock-preview"]')).toEqual([]);
  await shot(page, info, 'cc-en-lock-blocked');
});

test('Re-check reads the journal again and clears the banner once the problems are fixed', async ({ page }) => {
  const state = await boot(page, { blocked: true });
  await openPreview(page);
  await expect(tid(page, 'cc-lock-blockers')).toBeVisible();
  const before = state.reads.filter((r) => r.path.endsWith('/gl-journal')).length;
  state.state.fixed = true;
  await tid(page, 'cc-lock-recheck').click();
  await expect(tid(page, 'cc-lock-blockers')).toHaveCount(0);
  expect(state.reads.filter((r) => r.path.endsWith('/gl-journal')).length).toBe(before + 1);
  await expect(tid(page, 'cc-lock-submit')).not.toHaveAttribute('aria-disabled', 'true');
});

test('locking asks first, then posts once, and the preview becomes the locked journal', async ({ page }) => {
  const state = await boot(page);
  await openPreview(page);
  await tid(page, 'cc-lock-submit').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Lock Jun 2026 for Masar KSA?');
  await expect(dialog).toContainText('This posts 4 journal lines.');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect(state.writes.filter((w) => w.path.endsWith('/lock'))).toHaveLength(0);
  await tid(page, 'cc-lock-submit').click();
  await tid(page, 'cc-lock-confirm').click();
  await expect(tid(page, 'cc-lock-status-chip')).toHaveText('Locked');
  expect(state.writes.filter((w) => w.path === '/api/payroll/runs/run-jun/lock')).toHaveLength(1);
  await expect(tid(page, 'cc-lock-submit')).toHaveCount(0);
});

test('a locked run from before cost centres shows one Not recorded group', async ({ page }) => {
  await boot(page, { legacyLocked: true });
  await page.locator('#payroll-tab-gl-journal').click();
  await tid(page, 'cc-lock-run-select').selectOption('run-jun');
  await page.getByRole('button', { name: 'Generate Journal' }).click();
  await expect(tid(page, 'cc-lock-preview')).toBeVisible();
  await expect(tid(page, 'cc-lock-legacy-note')).toContainText('Not recorded (run locked before cost centres)');
  await expect(tid(page, 'cc-lock-status-chip')).toHaveText('Locked');
  await expect(page.locator('[data-testid^="cc-lock-group-"]')).toHaveCount(1);
});

test('a refusal at Lock re-renders the blockers from the 422, in the viewer language', async ({ page }) => {
  const state = await boot(page, { locale: 'ar' });
  await reviewAndLock(page).click();
  await expect(tid(page, 'cc-lock-preview')).toBeVisible();
  state.state.lockRefusal = { status: 422, body: { error: 'cost_centre_required', message: M1_EN, messageAr: M1_AR, blockers: blockerList(), groups: groupList() } };
  await tid(page, 'cc-lock-submit').click();
  await tid(page, 'cc-lock-confirm').click();
  const banner = tid(page, 'cc-lock-blockers');
  await expect(banner).toBeVisible();
  await expect(tid(page, 'cc-lock-blocker-cost_centre_required')).toContainText(M1_AR);
  await expect(tid(page, 'cc-lock-blocker-cost_centre_invalid')).toContainText(M2_AR);
  await expect(banner.getByRole('heading')).toBeFocused();
});

test('M-6: a run sent back after approval says so and offers the approvals tab', async ({ page }) => {
  const state = await boot(page);
  await openPreview(page);
  state.state.lockRefusal = { status: 409, body: { error: 'cost_centre_changed_after_approval', message: M6_EN, messageAr: M6_AR, status: 'Processed' } };
  await tid(page, 'cc-lock-submit').click();
  await tid(page, 'cc-lock-confirm').click();
  await expect(tid(page, 'cc-lock-sent-back-banner')).toContainText(M6_EN);
  await tid(page, 'cc-lock-sent-back-banner').getByRole('button', { name: 'Go to approvals' }).click();
  await expect(page.locator('#payroll-tab-approvals')).toHaveAttribute('aria-selected', 'true');
});

test('separation of duties: the 403 sentence shows in Arabic and the run stays Approved', async ({ page }) => {
  const state = await boot(page, { locale: 'ar' });
  await reviewAndLock(page).click();
  state.state.lockRefusal = { status: 403, body: { error: 'separation_of_duties', message: SOD_EN, messageAr: SOD_AR } };
  await tid(page, 'cc-lock-submit').click();
  await tid(page, 'cc-lock-confirm').click();
  await expect(tid(page, 'cc-lock-error')).toHaveText(SOD_AR);
  await expect(tid(page, 'cc-lock-submit')).toBeVisible();
});

test('a user without payroll.lock reads the preview, sees a note, and has no Lock button', async ({ page }) => {
  await boot(page, { readOnly: true });
  await reviewAndLock(page).click();
  await expect(tid(page, 'cc-lock-preview')).toBeVisible();
  await expect(tid(page, 'cc-lock-readonly-note')).toHaveText('You do not have permission to lock payroll runs.');
  await expect(tid(page, 'cc-lock-submit')).toHaveCount(0);
});

test('with costing off the old journal shows, with a note that links to Cost accounting', async ({ page }) => {
  await boot(page, { costingOn: false }, 'gl-journal');
  await tid(page, 'cc-lock-run-select').selectOption('run-jun');
  await page.getByRole('button', { name: 'Generate Journal' }).click();
  await expect(tid(page, 'cc-lock-costing-off-note')).toContainText('Cost accounting is off for this company. Lines post without cost centres.');
  await expect(tid(page, 'cc-lock-costing-off-note').getByRole('link')).toHaveAttribute('href', '/setup?tab=costAccounting');
  await expect(tid(page, 'cc-lock-groupby-cc')).toHaveCount(0);
});

test('a journal that fails to load is an error with Retry, never an empty journal', async ({ page }) => {
  const state = await boot(page);
  state.state.journalDown = true;
  await reviewAndLock(page).click();
  await expect(page.getByRole('alert').filter({ hasText: 'The journal service is down.' })).toBeVisible();
  state.state.journalDown = false;
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(tid(page, 'cc-lock-preview')).toBeVisible();
});

test('Approve: uncosted Optional lines need an acknowledgement, and only an affirmed count is sent', async ({ page }) => {
  const state = await boot(page, { uncosted: true }, 'approvals');
  await page.getByLabel('Select payroll run to approve').selectOption('run-proc');
  const ack = tid(page, 'cc-approve-uncosted-ack');
  await expect(ack).toContainText('6 employees post lines on Optional accounts without a cost centre');
  const approve = page.getByRole('button', { name: 'Approve — Final' });
  await expect(approve).toBeDisabled();
  await ack.getByRole('checkbox').check();
  await expect(approve).toBeEnabled();
  await approve.click();
  await expect.poll(() => state.writes.filter((w) => w.path === '/api/payroll/runs/run-proc/approve').length).toBe(1);
  expect(state.writes.find((w) => w.path === '/api/payroll/runs/run-proc/approve')!.body).toMatchObject({ expectedUncostedCount: 6 });
});

test('Approve: nothing uncosted means no acknowledgement and no count', async ({ page }) => {
  const state = await boot(page, {}, 'approvals');
  await page.getByLabel('Select payroll run to approve').selectOption('run-proc');
  await expect(page.getByRole('button', { name: 'Approve — Final' })).toBeEnabled();
  await expect(tid(page, 'cc-approve-uncosted-ack')).toHaveCount(0);
  await page.getByRole('button', { name: 'Approve — Final' }).click();
  await expect.poll(() => state.writes.filter((w) => w.path === '/api/payroll/runs/run-proc/approve').length).toBe(1);
  expect((state.writes.find((w) => w.path === '/api/payroll/runs/run-proc/approve')!.body as { expectedUncostedCount?: number | null }).expectedUncostedCount ?? null).toBeNull();
});

test('Arabic: the preview and the blocker banner read right to left with codes and amounts as left-to-right islands', async ({ page }, info) => {
  await boot(page, { locale: 'ar', blocked: true });
  await reviewAndLock(page).click();
  await expect(tid(page, 'cc-lock-preview')).toBeVisible();
  await expect(tid(page, 'cc-lock-status-chip')).toHaveText('معاينة — لم تُرحَّل بعد');
  await expect(tid(page, 'cc-lock-blockers').getByRole('heading')).toHaveText('القفل متوقف: مشكلتان');
  await expect(tid(page, 'cc-lock-fix-employee-E-0412')).toContainText('تعديل الموظف');
  await expect(tid(page, 'cc-lock-group-missing')).toContainText('مركز التكلفة مفقود');
  await expect(tid(page, 'cc-lock-set-default').first()).toContainText('تحديد المركز الافتراضي للشركة');
  await expect(tid(page, 'cc-lock-recheck')).toHaveText('إعادة الفحص');
  await expect(tid(page, 'cc-lock-submit')).toContainText('قفل المسيرة');
  expect(await page.evaluate(() => document.documentElement.dir)).toBe('rtl');
  // codes are LTR islands
  await expect(tid(page, 'cc-lock-blocker-row-E-0412').locator('bdi').first()).toHaveAttribute('dir', 'ltr');
  await shot(page, info, 'cc-ar-lock-blocked');
  await tid(page, 'cc-lock-recheck').scrollIntoViewIfNeeded();
});
