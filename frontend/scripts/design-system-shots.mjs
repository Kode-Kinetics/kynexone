#!/usr/bin/env node
/**
 * Design-system evidence screenshots against `next dev -p 5174` with every /api call mocked.
 *
 *   node --experimental-strip-types scripts/design-system-shots.mjs <tag> [baseUrl]
 *
 * Writes docs/evidence/design-system/<tag>-<screen>-<en|ar>-<light|dark>.jpg. Read-only: any
 * non-GET request is refused, so this can never touch a real organisation.
 */
import { chromium } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const tag = process.argv[2] ?? 'after';
const base = process.argv[3] ?? 'http://localhost:5174';
const outDir = join(here, '..', '..', 'docs', 'evidence', 'design-system');
mkdirSync(outDir, { recursive: true });

// Reuse the dashboard fixture dataset from the existing fixture-lane spec.
const spec = readFileSync(join(here, '..', 'e2e', 'dashboard-glass.spec.ts'), 'utf8');
const i = spec.indexOf('function dataset(');
const j = spec.indexOf('\n}\n', i) + 3;
const months = spec.match(/const months = .*?\n/)[0];
const companyDecl = spec.match(/const COMPANY = .*?\n/)[0];
const tmp = join(tmpdir(), `ds-dataset-${process.pid}.mts`);
writeFileSync(tmp, `${months}${companyDecl}export ${spec.slice(i, j)}export { COMPANY };\n`);
const { dataset, COMPANY } = await import(pathToFileURL(tmp).href);

const NOW = new Date('2026-09-22T09:00:00Z');
const departments = ['Engineering', 'Operations', 'Sales', 'Product', 'Human Resources', 'Finance'];
const names = ['Raj Krishnamurthy', 'Amira Mansour', 'Sunita Patel', 'Omar Haddad', 'Layla Farouk', 'Noura Al-Qahtani', 'Maha Bakr', 'Yaser Al-Ghamdi', 'Hassan Idris', 'Fatima Zahra', 'Daniel Okoye', 'Lina Haddad'];
const arabic = ['راج كريشنامورتي', 'أميرة منصور', 'سونيتا باتيل', 'عمر حداد', 'ليلى فاروق', 'نورة القحطاني', 'مها بكر', 'ياسر الغامدي', 'حسن إدريس', 'فاطمة الزهراء', 'دانيال أوكوي', 'لينا حداد'];
const employees = names.map((fullName, k) => ({
  id: k + 1, publicId: `e${k + 1}`, employeeCode: `EMP-${1000 + k}`, fullName, arabicName: arabic[k],
  department: departments[k % departments.length], designation: ['Engineer', 'Analyst', 'Manager', 'Specialist'][k % 4], branch: 'Riyadh HQ',
  status: k === 5 ? 'OnLeave' : 'Active', profileCompletenessScore: 70 + ((k * 7) % 30),
  iqamaNumber: `2${String(100000000 + k * 7919).slice(0, 9)}`, readinessState: k % 5 === 0 ? 'NeedsAttention' : 'Ready',
  activationBlockersCount: k % 5 === 0 ? 2 : 0, workEmail: `${fullName.split(' ')[0].toLowerCase()}@intelliflow.test`, accessState: 'active',
}));
const runs = [['2026-09', 'PendingFinanceReview', 142, 2030000, 1893400], ['2026-08', 'Locked', 140, 2010000, 1874000], ['2026-07', 'Locked', 139, 1990000, 1860200]].map(([p, status, n, gross, net], k) => ({
  id: `run-${k}`, runId: `run-${k}`, year: 2026, month: 9 - k, periodLabel: `${['Sep', 'Aug', 'Jul'][k]} 2026`, period: p, status, employeeCount: n,
  totalGross: gross, totalNet: net, totalDeductions: gross - net, currency: 'SAR', companyId: COMPANY.id, companyName: COMPANY.name, runType: 'Regular', createdAtUtc: NOW.toISOString(),
}));

const permissions = ['dashboard.read', 'employees.read', 'employees.write', 'attendance.read', 'leave.read', 'leave.write', 'approvals.read', 'approvals.decide', 'payroll.read', 'payroll.write',
  'compliance.read', 'reports.read', 'ai.query', 'ai.insights_view', 'organization.read', 'organization.write', 'organization.setup.apply'];
const company = { id: COMPANY.id, legalNameEn: COMPANY.name, legalNameAr: 'إنتلي فلو السعودية', countryCode: 'SAU', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
const user = {
  id: 'u1', tenantId: 't1', tenantSlug: 'intelliflow', email: 'admin@intelliflow.test', fullName: 'Yaser Al-Ghamdi', roles: ['Admin'],
  permissions, accountType: 'Group', isGroupScope: true, companies: [{ id: COMPANY.id, name: COMPANY.name, code: 'KSA', countryCode: 'SAU', isActive: true }],
};

async function newPage(browser, { ar, dark }) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, locale: ar ? 'ar-SA' : 'en-US' });
  const page = await ctx.newPage();
  await page.clock.setFixedTime(NOW);
  await page.addInitScript(([isAr, theme, companyId]) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone-locale-choice-v2', isAr ? 'ar' : 'en');
    localStorage.setItem('kynexone.theme', theme);
    localStorage.setItem('kynexone-company:u1', companyId);
    localStorage.setItem('kynexone-company:reviewer', companyId);
  }, [ar, dark ? 'dark' : 'light', COMPANY.id]);
  await page.route('**/api/**', route => {
    const req = route.request();
    const p = new URL(req.url()).pathname;
    const json = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (req.method() !== 'GET') return route.fulfill({ status: 405, json: { message: 'Read-only fixture' } });
    if (p === '/api/auth/me') return json(user);
    if (p === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: true, countryCode: 'SAU', currencyCode: 'SAR' });
    if (p === '/api/dashboard/full') return json(dataset(true, NOW));
    if (p === '/api/payroll/companies') return json([COMPANY]);
    if (p === '/api/payroll/runs') return json({ items: runs, total: runs.length, page: 1, pageSize: 25 });
    if (p === '/api/employees') return json({ items: employees, total: 142, page: 1, pageSize: 25 });
    if (p === '/api/companies') return json({ items: [company], total: 1, page: 1, pageSize: 100 });
    if (p === '/api/payroll/overview') return json({ year: 2026, month: 9, totalCompanies: 1, totalActiveEmployees: 142, totalGrossPayroll: 2030000, totalNetPayroll: 1893400, totalValidationErrors: 2, totalPendingApprovals: 1,
      companies: [{ companyId: COMPANY.id, companyName: COMPANY.name, tradeName: '', currency: 'SAR', activeEmployees: 142, employeesWithSalary: 140, employeesMissingSalary: 2, salaryCoveragePercent: 98.6, payrollRunStatus: 'PendingFinanceReview',
        grossPayroll: 2030000, totalDeductions: 136600, netPayroll: 1893400, validationErrors: 2, validationWarnings: 5, pendingApprovals: 1, wpsEmployerId: '', gosiEmployerId: '', hasPayrollRun: true }] });
    if (p === '/api/payroll/readiness') return json({ year: 2026, month: 9, companyId: COMPANY.id, completionPercent: 71, isReadyForProcessing: false, totalActiveEmployees: 142, employeesWithSalary: 140, salaryCoveragePercent: 98.6, validationErrors: 2, payrollRunStatus: 'PendingFinanceReview',
      steps: [{ step: 1, label: 'Salary data', complete: true, detail: '140 of 142 employees have a salary' }, { step: 2, label: 'Attendance', complete: true, detail: 'Reconciled' }, { step: 3, label: 'Validation', complete: false, detail: '2 errors to fix' }] });
    if (p === '/api/payroll/reports/summary') return json({ totalRuns: 9, lockedRuns: 8, totalEmployeesPaid: 1260, totalGrossYtd: 17900000, totalNetYtd: 16700000 });
    if (p === '/api/company-setup/context') return json({ canCreate: true, creationMode: 'Group', accountType: 'Group', companiesUsed: 1, isGroupScope: true, sisterCompanies: [{ id: COMPANY.id, legalNameEn: COMPANY.name, countryCode: 'SAU' }], manageableCompanies: [] });
    if (p === '/api/company-setup/drafts') return json([]);
    if (p === '/api/ai/status') return json({ enabled: true, provider: 'fixture' });
    if (p === '/api/country-packs/available' || p === '/api/finance/gl/accounts' || p === '/api/finance/gl/mappings') return json([]);
    if (p === '/api/reference/countries') return json([{ code: 'SA', name: 'Saudi Arabia' }]);
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  return { ctx, page };
}

const screens = [
  { name: 'dashboard', url: '/dashboard', ready: p => p.locator('#hero-heading').waitFor({ timeout: 40000 }) },
  { name: 'setup-organisation', url: '/setup', ready: async p => { await p.waitForSelector('h1', { timeout: 40000 }); await p.getByRole('link', { name: /^(Departments|الإدارات)$/ }).first().click(); await p.waitForTimeout(1500); } },
  { name: 'payroll', url: '/payroll', ready: p => p.waitForSelector('h1', { timeout: 40000 }) },
  { name: 'employees', url: '/people', ready: p => p.getByText('Raj Krishnamurthy').first().waitFor({ timeout: 40000 }) },
  {
    name: 'form-drawer', url: '/setup',
    ready: async p => {
      await p.waitForSelector('h1', { timeout: 40000 });
      await p.getByRole('link', { name: /^(Departments|الإدارات)$/ }).first().click();
      await p.getByRole('button', { name: /Add Department|إضافة إدارة/ }).first().click();
      await p.waitForSelector('[role="dialog"]', { timeout: 10000 });
    },
  },
];

const only = process.env.SHOTS_ONLY?.split(',');
const browser = await chromium.launch();
for (const ar of [false, true]) {
  for (const dark of [false, true]) {
    const { ctx, page } = await newPage(browser, { ar, dark });
    for (const s of screens) {
      if (only && !only.includes(s.name)) continue;
      const file = join(outDir, `${tag}-${s.name}-${ar ? 'ar' : 'en'}-${dark ? 'dark' : 'light'}.jpg`);
      try {
        await page.goto(base + s.url, { waitUntil: 'domcontentloaded', timeout: 90000 });
        await s.ready(page);
        await page.waitForTimeout(900);
        await page.screenshot({ path: file, type: 'jpeg', quality: 62 });
        let axe = '';
        if (process.env.AXE) {
          const r = await new AxeBuilder({ page }).withRules(['color-contrast']).analyze();
          axe = ` contrast-violating-nodes=${r.violations.reduce((n, v) => n + v.nodes.length, 0)}`;
        }
        console.log('ok  ', file.split('/').pop() + axe);
      } catch (error) {
        await page.screenshot({ path: file, type: 'jpeg', quality: 62 }).catch(() => {});
        console.log('FAIL', file.split('/').pop(), String(error.message).split('\n')[0]);
      }
    }
    await ctx.close();
  }
}
await browser.close();
