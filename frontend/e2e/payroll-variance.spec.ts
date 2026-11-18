import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { translate } from '../src/i18n/translations';
import { enumLabel } from '../src/i18n/enumLabel';

/**
 * Payroll → parallel-run variance (/payroll/variance), fixture lane (e2e/playwright.fixture.config.ts).
 *
 * Route-intercepted API. Proves, in English and Arabic (run it on the desktop and phone projects):
 *   - the empty state (no payroll runs yet) and the Compare button staying off until a run and a register are chosen
 *   - a run with differences: what is sent, the net tie-out in the RUN's own currency (not the tenant default),
 *     the lines outside tolerance first
 *   - a duplicate register row reported as a register error, not hidden
 *   - a run with no payslips: warned up front, and the server's 422 shown in the viewer's language with the status named
 *   - Arabic is right-to-left and no English sentence is left on the page
 *   - the phone does not scroll the whole page sideways (only the table does)
 * It does not prove the server's arithmetic — OpeningBalanceCutoverTests does.
 *
 * Evidence screenshots: VARIANCE_SHOTS=<dir> npx playwright test -c e2e/playwright.fixture.config.ts payroll-variance
 */
const RUN_PROCESSED = '33333333-3333-4333-8333-333333333333';
const RUN_DRAFT = '44444444-4444-4444-8444-444444444444';
const USER = {
  id: 'u1', tenantId: 't1', tenantSlug: 'intelliflow', email: 'admin@intelliflow.test', fullName: 'Yaser Al-Ghamdi',
  roles: ['Admin'], accountType: 'Tenant', isGroupScope: true, companies: [],
  permissions: ['dashboard.read', 'payroll.read', 'employees.read'],
};
const run = (id: string, month: number, status: string) => ({
  id, companyId: 'c1', year: 2026, month, status, runType: 'Regular',
  totalGrossSalary: 100000, totalDeductions: 10000, totalNetSalary: 90000, totalEmployerStatutoryCost: 9000, employeeCount: 2,
  createdAtUtc: '2026-10-01T08:00:00Z', processedAtUtc: status === 'Draft' ? null : '2026-10-02T08:00:00Z', lockedAtUtc: null,
});
const RUNS = [run(RUN_PROCESSED, 9, 'Processed'), run(RUN_DRAFT, 10, 'Draft')];

const line = (code: string, name: string, comp: string, mine: number, theirs: number, presence = 'Both') => ({
  employeeCode: code, employeeName: name, componentCode: comp, kynexOneAmount: mine, externalAmount: theirs,
  variance: Math.round((mine - theirs) * 100) / 100,
  variancePct: theirs === 0 ? (mine === 0 ? 0 : 100) : Math.round(((mine - theirs) / Math.abs(theirs)) * 1e6) / 1e4,
  isOutsideTolerance: Math.abs(mine - theirs) > 0.01, presence,
});
const REPORT = {
  runId: RUN_PROCESSED, period: '2026-09', tolerance: 0.01, toleranceType: 'Absolute',
  employeesInRun: 2, employeesInRegister: 2, matchedEmployees: 2, employeesOnlyInRun: 0, employeesOnlyInRegister: 0,
  linesCompared: 3, linesOutsideTolerance: 1, totalAbsoluteVariance: 250,
  kynexOneNetTotal: 23660, externalNetTotal: 23410,
  lines: [
    line('EMP-001', 'Sara Alharbi', 'NET', 11830, 11830),
    line('EMP-002', 'Omar Alqahtani', 'NET', 11830, 11580),
    line('EMP-002', 'Omar Alqahtani', 'GOSI_EMPLOYEE', 900, 0, 'OnlyInKynexOne'),
  ],
  registerErrors: [] as string[],
  // A company that pays in a different currency than the tenant default (SAR) — the page must show THIS.
  currency: 'KWD',
};
const DUPLICATE_ERROR = 'register row 3: duplicate of row 2 for EMP-001 / NET — ignored, the first row was kept. If the legacy system really has several lines for one component, add them up before uploading.';
// What the server sends for every kind of register complaint: a stable code, its parameters and the English fallback.
const DUPLICATE_DETAIL = { code: 'register_row_duplicate', row: 3, params: { row: '3', firstRow: '2', employeeCode: 'EMP-001', component: 'NET' }, message: DUPLICATE_ERROR };
const ALL_ERROR_KINDS = [
  { code: 'register_row_employee_code_required', row: 4, params: { row: '4' }, message: 'register row 4: EmployeeCode is required.' },
  { code: 'register_row_component_required', row: 5, params: { row: '5' }, message: 'register row 5: ComponentCode is required (use GROSS, DEDUCTIONS or NET for the totals).' },
  { code: 'register_row_amount_required', row: 6, params: { row: '6' }, message: 'register row 6: Amount is required and was blank — a blank is not read as zero.' },
  { code: 'register_row_amount_not_a_number', row: 7, params: { row: '7', value: 'N/A' }, message: "register row 7: Amount is not a number (found 'N/A')." },
  DUPLICATE_DETAIL,
];

type Locale = 'en' | 'ar';
type Opts = {
  locale: Locale;
  runs?: unknown[];
  varianceStatus?: number;
  varianceBody?: unknown;
  onVariance?: (body: unknown) => void;
};

async function open(page: Page, opts: Opts) {
  await page.addInitScript((locale) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, opts.locale);
  const runs = opts.runs ?? RUNS;
  await page.route('**/api/**', (route: Route) => {
    const req = route.request();
    const p = new URL(req.url()).pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(USER);
    if (p === '/api/payroll/runs' && req.method() === 'GET') return json({ items: runs, total: runs.length, page: 1, pageSize: 100 });
    // The tenant default is SAR; the run's company pays in KWD.
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false, defaultCurrency: 'SAR' });
    if (/^\/api\/payroll\/parallel-run\/[^/]+\/variance$/.test(p) && req.method() === 'POST') {
      opts.onVariance?.(req.postDataJSON());
      return json(opts.varianceBody ?? REPORT, opts.varianceStatus ?? 200);
    }
    return json([]);
  });
  await page.goto('/payroll/variance');
  await expect(page.getByRole('heading', { name: translate(opts.locale, 'Variance against the legacy register') })).toBeVisible({ timeout: 30_000 });
}

const attach = (page: Page, name = 'register.csv') =>
  page.locator('input[type=file]').setInputFiles({ name, mimeType: 'text/csv', buffer: Buffer.from('EmployeeCode,ComponentCode,Amount\nEMP-001,NET,11830\nEMP-002,NET,11580\n') });

const SHOTS = process.env.VARIANCE_SHOTS;
async function shot(page: Page, name: string, locale: Locale, project: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  // Pointer off every control, so the picture shows the resting page, not a hover shade.
  await page.mouse.move(0, 0);
  await page.screenshot({ path: path.join(SHOTS, `variance-${name}-${locale}-${project}.png`), fullPage: true });
}

for (const locale of ['en', 'ar'] as const) {
  const tr = (key: string, params?: Record<string, string | number>) => translate(locale, key, params);
  const compareName = tr('Compare');
  const compare = (page: Page) => page.getByRole('button', { name: compareName, exact: true });

  test.describe(`parallel-run variance (${locale})`, () => {
    test('empty state: no payroll runs yet', async ({ page }, info) => {
      await open(page, { locale, runs: [] });
      await expect(page.getByText(tr('There are no payroll runs yet. Process a run for the parallel month first — the comparison reads its payslips, earnings and deductions.'))).toBeVisible();
      await expect(page.locator('#variance-run')).toHaveCount(0);
      await expect(compare(page)).toBeDisabled();
      await expect(page.locator('html')).toHaveAttribute('dir', locale === 'ar' ? 'rtl' : 'ltr');
      await shot(page, 'empty', locale, info.project.name);
    });

    test('Compare stays off until a run and a register are chosen, then sends exactly what was set', async ({ page }) => {
      let sent: { registerCsv?: string; tolerance?: number; toleranceType?: string } | undefined;
      await open(page, { locale, onVariance: (b) => { sent = b as typeof sent; } });

      await expect(compare(page)).toBeDisabled();
      await page.locator('#variance-run').selectOption(RUN_PROCESSED);
      await expect(compare(page)).toBeDisabled();
      await attach(page);
      await expect(compare(page)).toBeEnabled();

      await page.locator('#variance-tolerance').fill('0.5');
      await page.locator('#variance-tolerance-type').selectOption('Percentage');
      await compare(page).click();

      await expect(page.getByRole('heading', { name: /2026-09/ })).toBeVisible();
      expect(sent?.tolerance).toBe(0.5);
      expect(sent?.toleranceType).toBe('Percentage');
      expect(sent?.registerCsv).toContain('EMP-002,NET,11580');
    });

    test("a run with differences: the net tie-out is in the RUN's currency and the view starts on the lines outside tolerance", async ({ page }, info) => {
      await open(page, { locale });
      await page.locator('#variance-run').selectOption(RUN_PROCESSED);
      await attach(page);
      await compare(page).click();

      await expect(page.getByRole('heading', { name: tr('Period {period} — {count, plural, one {# line} other {# lines}} outside tolerance.', { period: '⁦2026-09⁩', count: 1 }) })).toBeVisible();
      for (const amount of ['23,660.00', '23,410.00']) await expect(page.getByText(new RegExp(`${amount}\\s+KWD`)).first()).toBeVisible();
      await expect(page.getByText(/250\.00\s+KWD/).first()).toBeVisible();
      await expect(page.getByText(/\bSAR\b/)).toHaveCount(0);

      // Default view = outside tolerance: the matched EMP-001 row is not in it, the two differing lines are.
      const rows = page.locator('tbody tr');
      await expect(rows).toHaveCount(2);
      await expect(rows.first()).toContainText('EMP-002');
      await expect(page.getByRole('tab', { name: new RegExp(`^${tr('Matched')}`) })).toContainText('1');
      await shot(page, 'differences', locale, info.project.name);
      await page.getByRole('tab', { name: new RegExp(`^${tr('All lines')}`) }).click();
      await expect(rows).toHaveCount(3);

      // A phone scrolls the table sideways, never the whole page.
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(1);
    });

    test('a duplicate register row is reported as a register error, in the viewer\'s language', async ({ page }, info) => {
      await open(page, { locale, varianceBody: { ...REPORT, registerErrors: [DUPLICATE_ERROR], registerErrorDetails: [DUPLICATE_DETAIL] } });
      await page.locator('#variance-run').selectOption(RUN_PROCESSED);
      await attach(page);
      await compare(page).click();
      await expect(page.getByRole('heading', { name: tr('{count, plural, one {# row of the register could not be read} other {# rows of the register could not be read}}', { count: 1 }) })).toBeVisible();
      const expected = tr('Register row {row}: duplicate of row {firstRow} for {employeeCode} / {component} — ignored, the first row was kept. If the legacy system really has several lines for one component, add them up before uploading.',
        { row: '\u20663\u2069', firstRow: '\u20662\u2069', employeeCode: '\u2066EMP-001\u2069', component: '\u2066NET\u2069' });
      await expect(page.locator('li.font-mono')).toHaveText(expected);
      if (locale === 'ar') await expect(page.locator('li.font-mono')).not.toContainText('duplicate');
      await shot(page, 'duplicate-row', locale, info.project.name);
    });

    test('a server that sends only the English register errors still shows them', async ({ page }) => {
      await open(page, { locale, varianceBody: { ...REPORT, registerErrors: [DUPLICATE_ERROR] } });
      await page.locator('#variance-run').selectOption(RUN_PROCESSED);
      await attach(page);
      await compare(page).click();
      await expect(page.locator('li.font-mono')).toHaveText(DUPLICATE_ERROR);
    });

    test('an unreadable register is refused with the reasons in the viewer\'s language', async ({ page }) => {
      await open(page, {
        locale, varianceStatus: 422,
        varianceBody: { message: 'The external register could not be read.', errors: ['The external register was empty.'], errorDetails: [{ code: 'register_empty', row: null, params: {}, message: 'The external register was empty.' }] },
      });
      await page.locator('#variance-run').selectOption(RUN_PROCESSED);
      await attach(page);
      await compare(page).click();
      await expect(page.getByText(`${tr('The external register could not be read.')} ${tr('The external register was empty.')}`)).toBeVisible();
    });

    test('a run with no payslips: warned up front, and the server refusal is shown in the viewer\'s language naming the status', async ({ page }, info) => {
      await open(page, {
        locale,
        varianceStatus: 422,
        // English message plus the stable code and status: the page translates by code.
        varianceBody: { code: 'run_has_no_payslips', status: 'Draft', message: 'This payroll run has no payslips yet (status Draft), so there is nothing to compare.' },
      });
      await page.locator('#variance-run').selectOption(RUN_DRAFT);
      const status = enumLabel((k, p) => translate(locale, k, p), 'PayrollRunStatus', 'Draft');
      await expect(page.getByText(tr('This run is {status} and has no payslips yet, so every employee in the register will report as missing. Process the run first — a processed-but-unlocked run writes no GL and publishes no payslip, so it is safe to compare and re-process as many times as you need.', { status }))).toBeVisible();
      await attach(page);
      await compare(page).click();
      const refusal = page.getByText(tr('This payroll run has no payslips yet (status {status}), so there is nothing to compare. Process the run first — a processed-but-unlocked run posts no GL and publishes nothing, so it is safe to compare and re-process.', { status }));
      await expect(refusal).toBeVisible();
      await expect(refusal).toContainText(status);
      await shot(page, 'no-payslips', locale, info.project.name);
    });

    test('an unrecognised server refusal still reaches the user as sent', async ({ page }) => {
      await open(page, { locale, varianceStatus: 422, varianceBody: { message: 'The external register could not be read.', errors: ['register row 2: Amount is not a number (found \'abc\').'] } });
      await page.locator('#variance-run').selectOption(RUN_PROCESSED);
      await attach(page);
      await compare(page).click();
      await expect(page.getByText(/The external register could not be read\./)).toBeVisible();
    });

    test('no serious accessibility violations', async ({ page }) => {
      await open(page, { locale });
      await page.locator('#variance-run').selectOption(RUN_PROCESSED);
      await attach(page);
      await compare(page).click();
      await expect(page.getByText(/23,660\.00\s+KWD/).first()).toBeVisible();
      // Pointer off the Compare button: axe would otherwise measure its transient hover shade, not the resting page.
      await page.mouse.move(0, 0);
      const results = await new AxeBuilder({ page }).include('main').analyze();
      const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
      expect(serious.map((v) => `${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([]);
    });
  });
}

test.describe('parallel-run variance (Arabic only)', () => {
  test('right-to-left, and no English sentence is left on the page', async ({ page }, info) => {
    await open(page, { locale: 'ar', varianceBody: { ...REPORT, registerErrors: ALL_ERROR_KINDS.map((e) => e.message), registerErrorDetails: ALL_ERROR_KINDS } });
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');

    // Before and after a comparison, with the run picker and the report both on screen.
    const options = await page.locator('#variance-run option').allTextContents();
    expect(options.join(' ')).not.toMatch(/Processed|Draft|employees?\b/);
    await page.locator('#variance-run').selectOption(RUN_PROCESSED);
    await attach(page);
    await arCompare(page).click();
    await expect(page.getByText(/23,660\.00\s+KWD|KWD\s+23,660\.00/).first()).toBeVisible();
    // Open every tab so each line's "found in" badge and each empty-state sentence is on screen once.
    for (const tab of await page.getByRole('tab').all()) {
      await tab.click();
      await expectNoEnglish(page);
    }
    await shot(page, 'rtl', 'ar', info.project.name);
  });
});

function arCompare(page: Page) {
  return page.getByRole('button', { name: translate('ar', 'Compare'), exact: true });
}

/**
 * Latin words that are allowed to appear on the Arabic page: this product's own name, the CSV column and reserved
 * component codes, currency codes, and employee names and codes (data). Register errors are NOT excused: they are
 * rendered from the server's code and parameters, so any English sentence in them is a failure.
 */
const ALLOWED_LATIN = [
  // Table cells are read back glued together (name + component), so no word boundaries here; longest tokens first.
  /Sara Alharbi|Omar Alqahtani|Yaser Al-Ghamdi/g, /GOSI_EMPLOYEE|DEDUCTIONS|EmployeeCode|ComponentCode|Amount/g,
  /KynexOne/gi, /GROSS|NET|CSV|KWD|SAR/g, /EMP-\d+/g, /register\.csv/g,
];
async function expectNoEnglish(page: Page) {
  const text = await page.locator('main').evaluate((el) => {
    return (el as HTMLElement).innerText;
  });
  let rest = text;
  for (const re of ALLOWED_LATIN) rest = rest.replace(re, ' ');
  const leftovers = rest.match(/[A-Za-z]{3,}/g) ?? [];
  expect(leftovers, `English left on the Arabic page: ${leftovers.join(', ')}`).toEqual([]);
}
