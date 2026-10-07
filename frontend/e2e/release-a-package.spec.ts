import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';

/**
 * Release A R2 — the employee package panel, the proposal card and the dependants panel, in English and Arabic.
 * Fixture lane (e2e/playwright.fixture.config.ts): real pages against route-intercepted API responses. The payloads in
 * e2e/fixtures/release-a were serialised from the backend's own view models (EmployeePackageView, DependantDto), so the
 * screen is proven against the shape the server returns. What this cannot prove is that the server returns it — the
 * backend suites (PackageResolverTests, PackageProposalAndDependantsTests, the Postgres suites) do that.
 */

const FX = path.join(__dirname, 'fixtures', 'release-a');
const read = (file: string) => JSON.parse(fs.readFileSync(path.join(FX, file), 'utf8'));
const EVIDENCE = process.env.RELEASE_A_EVIDENCE_DIR;

const EMP_ID = 1;
const employee = {
  id: EMP_ID, publicId: '00000000-0000-0000-0000-000000000001', employeeCode: 'E-1', fullName: 'Mohammed Abdelrahman',
  firstName: 'Mohammed', lastName: 'Abdelrahman', email: 'm@masar.test', status: 'Active', nationality: 'Egyptian',
  saudiOrNonSaudi: 'Non-Saudi', companyId: 'c1', companyName: 'Masar Facility Services', department: 'Operations',
  designation: 'Supervisor', grade: 'G3', joiningDate: '2025-02-01', contractType: 'Fixed', complianceRecords: [], documents: [],
  history: [], transfers: [], arabicName: 'محمد عبدالرحمن', branch: 'Riyadh', profileCompletenessScore: 92, iqamaNumber: '2*******1',
  readinessState: 'Ready', activationBlockersCount: 0,
};
const documents = [
  { id: 'd1', employeeId: EMP_ID, documentType: 'Signed contract', fileName: 'CON-1-signed.pdf', storageUrl: '', isRequired: true,
    approvalStatus: 'Approved', versionNumber: 1, uploadedAtUtc: '2026-02-01T08:00:00Z' },
  { id: 'd2', employeeId: EMP_ID, documentType: 'Passport', fileName: 'passport.pdf', storageUrl: '', isRequired: true,
    approvalStatus: 'Approved', versionNumber: 1, uploadedAtUtc: '2026-02-01T08:00:00Z' },
];
const user = {
  id: 'u-checker', tenantId: 't1', tenantSlug: 'masar', email: 'hr@masar.test', fullName: 'HR Director', roles: ['HR Director'],
  permissions: ['employees.read', 'employees.write', 'entitlements.read', 'entitlements.manage', 'compliance.read'], accountType: 'SingleCompany',
  isGroupScope: true, companies: [{ id: 'c1', name: 'Masar Facility Services', code: 'MFS', countryCode: 'SAU', isActive: true }],
};

const contracts = [
  { id: 'k-propose', employeeId: '00000000-0000-0000-0000-000000000001', employeeName: 'Mohammed Abdelrahman', templateId: null,
    contractNumber: 'CON-2026-0001', contractType: 'Employment', status: 'Active', startDate: '2026-02-01', endDate: '2027-01-31',
    basicSalary: 8000, currencyCode: 'SAR', language: 'en', version: 1, previousVersionId: null, signedByEmployeeName: '',
    signedByEmployeeAtUtc: null, signedByHrName: 'HR Lead', signedByHrAtUtc: '2026-10-06T08:00:00Z', fileUrl: '', createdAtUtc: '2026-10-06T08:00:00Z', updatedAtUtc: null },
  { id: 'k-review', employeeId: '00000000-0000-0000-0000-000000000002', employeeName: 'Ramon Dela Cruz', templateId: null,
    contractNumber: 'CON-2026-0002', contractType: 'Employment', status: 'Active', startDate: '2026-03-01', endDate: '2027-02-28',
    basicSalary: 5000, currencyCode: 'SAR', language: 'en', version: 1, previousVersionId: null, signedByEmployeeName: '',
    signedByEmployeeAtUtc: null, signedByHrName: 'HR Lead', signedByHrAtUtc: '2026-10-06T08:00:00Z', fileUrl: '', createdAtUtc: '2026-10-06T08:00:00Z', updatedAtUtc: null },
];

async function open(page: Page, locale: 'en' | 'ar', view: unknown, dependants: unknown, path = `/people?employeeId=${EMP_ID}`) {
  const confirmBodies: unknown[] = [];
  const proposeBodies: { path: string; body: unknown }[] = [];
  await page.addInitScript(([l]) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone-locale', l);
    localStorage.setItem('kynexone.theme', 'light');
  }, [locale]);
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.route('**/api/**', (route) => {
    const req = route.request();
    const p = new URL(req.url()).pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json(user);
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });
    if (p === '/api/employees') return json({ items: [employee], total: 1, page: 1, pageSize: 25 });
    if (p === `/api/employees/${EMP_ID}`) return json(employee);
    if (p === `/api/employees/${EMP_ID}/documents`) return json(documents);
    if (p === `/api/employees/${EMP_ID}/readiness`) return json({ error: 'n/a' }, 404);
    if (/\/api\/entitlements\/employees\/\d+\/package$/.test(p)) return json(view);
    if (/\/api\/entitlements\/employees\/\d+\/dependants$/.test(p)) return json(dependants);
    if (/\/api\/entitlements\/employees\/\d+\/package\/propose$/.test(p) && req.method() === 'POST') {
      proposeBodies.push({ path: p, body: req.postDataJSON() });
      return json({ jobId: 'j1', status: 'Queued', deduplicated: false, statusUrl: '/api/jobs/j1' }, 202);
    }
    if (p === '/api/compliance/contracts') return json({ total: contracts.length, page: 1, items: contracts });
    if (p === '/api/entitlements/contracts/package-status') return json([
      { contractId: 'k-propose', employeeId: EMP_ID, nextAction: 'proposeBenefits' },
      { contractId: 'k-review', employeeId: 2, nextAction: 'reviewProposal' },
    ]);
    if (/\/api\/entitlements\/package\/proposals\/[^/]+\/confirm$/.test(p) && req.method() === 'POST') {
      confirmBodies.push(req.postDataJSON());
      return json({ confirmed: 2, skipped: [] });
    }
    return json(p.endsWith('s') ? [] : {});
  });
  await page.goto(path);
  if (path.startsWith('/people'))
    await page.getByRole('button', { name: locale === 'ar' ? 'الباقة' : 'Package', exact: true }).click({ timeout: 30_000 });
  return { errors, confirmBodies, proposeBodies };
}

async function noRawCodes(page: Page) {
  const text = await page.locator('main').innerText();
  expect(text, 'a reason code reached the screen').not.toMatch(/ENTITLEMENT_|PACKAGE_|GRADE_MISSING/);
  expect(text, 'an unfilled {placeholder} reached the screen').not.toMatch(/\{[a-z]+\}/);
}

for (const locale of ['en', 'ar'] as const) {
  const T = locale === 'ar'
    ? { proposed: 'الباقة المقترحة — لم تُثبَّت بعد', confirm: 'تأكيد مطابقتها للعقد الموقّع', dependants: 'المعالون', none: 'لا يوجد معالون مسجلون.',
        oneOfTwo: '1 من 2 من مزايا العقد مثبتة', waiting: 'بحاجة إلى تأكيد', ticket: 'تذكرة السفر السنوية',
        awaiting: 'مزايا هذا العقد بانتظار تأكيد شخص آخر.', propose: 'اقتراح المزايا', review: 'مراجعة المقترح' }
    : { proposed: 'Proposed package — not fixed yet', confirm: 'Confirm it matches the signed contract', dependants: 'Dependants',
        none: 'No dependants on file.', oneOfTwo: '1 of 2 contract benefits are fixed', waiting: 'Needs confirmation', ticket: 'Annual air ticket',
        awaiting: 'Benefits for this contract are waiting for a second person to confirm.', propose: 'Propose benefits', review: 'Review proposal' };

  test(`a proposal is confirmed by another HR user against the signed contract only (${locale})`, async ({ page }) => {
    const { errors, confirmBodies } = await open(page, locale, read('view-proposal.json'), read('dependants.json'));
    const card = page.locator(`section[aria-label="${locale === 'ar' ? 'الباقة المقترحة' : 'Proposed package'}"]`);
    await expect(card.getByRole('heading', { name: T.proposed })).toBeVisible({ timeout: 30_000 });
    // One next action while the proposal waits: review it (and nothing else to propose or fix).
    const waiting = page.getByRole('status').filter({ hasText: T.awaiting });
    await expect(waiting.getByRole('button', { name: T.review })).toBeVisible();
    await expect(page.getByRole('button', { name: T.propose })).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.dir)).toBe(locale === 'ar' ? 'rtl' : 'ltr');

    // Only the signed contract can be chosen — the passport on file is not offered.
    const select = card.getByRole('combobox');
    await expect(select.locator('option')).toHaveCount(2);
    await expect(select.locator('option', { hasText: 'passport.pdf' })).toHaveCount(0);
    const confirm = card.getByRole('button', { name: T.confirm });
    await expect(confirm).toBeDisabled();
    await select.selectOption('d1');
    await expect(confirm).toBeEnabled();

    // The dependants panel lists the four on file (wife, two children and a parent, who no benefit covers).
    const deps = page.locator(`section[aria-label="${T.dependants}"]`);
    await expect(deps.getByRole('listitem')).toHaveCount(4);
    await noRawCodes(page);
    if (EVIDENCE) await page.screenshot({ path: `${EVIDENCE}/r2-proposal-${locale}-${test.info().project.name}.png`, fullPage: true });

    await confirm.click();
    await expect.poll(() => confirmBodies.length).toBe(1);
    expect(confirmBodies[0]).toEqual({ contractId: read('view-proposal.json').contract.id, documentId: 'd1' });
    expect(errors).toEqual([]);
  });

  test(`a partly fixed package says how many are fixed and why the rest are not (${locale})`, async ({ page }) => {
    const { errors } = await open(page, locale, read('view-reasons.json'), []);
    await expect(page.getByText(T.oneOfTwo)).toBeVisible({ timeout: 30_000 });
    // The "not fixed yet" list names the benefit and its reason in one sentence.
    await expect(page.getByRole('listitem').filter({ hasText: `${T.ticket} — ` }).filter({ hasText: T.waiting })).toBeVisible();
    await expect(page.getByText(T.none)).toBeVisible();
    // Never "fixed until …" while a benefit is not fixed; and no "Fix" offer that would only repeat the skip.
    await expect(page.getByText(locale === 'ar' ? 'ثابتة حتى' : 'fixed until')).toHaveCount(0);
    await expect(page.getByRole('button', { name: locale === 'ar' ? 'تثبيت الباقة لهذه السنة التعاقدية' : 'Fix the package for this contract year' })).toHaveCount(0);
    await noRawCodes(page);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow, 'no horizontal page scroll').toBeLessThanOrEqual(1);
    if (EVIDENCE) await page.screenshot({ path: `${EVIDENCE}/r2-reasons-${locale}-${test.info().project.name}.png`, fullPage: true });
    expect(errors).toEqual([]);
  });

  test(`a contract activated after it started: its benefits wait for a second person, one next action (${locale})`, async ({ page }) => {
    const { errors, proposeBodies } = await open(page, locale, read('view-awaiting.json'), read('dependants.json'));
    const waiting = page.getByRole('status').filter({ hasText: T.awaiting });
    await expect(waiting).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('button', { name: T.review })).toHaveCount(0);
    await expect(page.getByRole('button', { name: locale === 'ar' ? 'تثبيت الباقة لهذه السنة التعاقدية' : 'Fix the package for this contract year' })).toHaveCount(0);
    await noRawCodes(page);
    if (EVIDENCE) await page.screenshot({ path: `${EVIDENCE}/r2-awaiting-${locale}-${test.info().project.name}.png`, fullPage: true });
    await waiting.getByRole('button', { name: T.propose }).click();
    await expect.poll(() => proposeBodies.length).toBe(1);
    expect(proposeBodies[0]).toEqual({ path: `/api/entitlements/employees/${EMP_ID}/package/propose`, body: { contractId: read('view-awaiting.json').contract.id } });
    expect(errors).toEqual([]);
  });

  test(`the contract register shows the same next action on each waiting contract (${locale})`, async ({ page }) => {
    const { errors, proposeBodies } = await open(page, locale, read('view-awaiting.json'), [], '/compliance?tab=contracts');
    const proposeRow = page.getByTestId('contract-benefits-k-propose');
    const reviewRow = page.getByTestId('contract-benefits-k-review');
    await expect(proposeRow.getByText(T.awaiting)).toBeVisible({ timeout: 30_000 });
    await expect(reviewRow.getByRole('link', { name: T.review })).toHaveAttribute('href', '/people?employeeId=2&tab=package');
    await expect(proposeRow.getByRole('link', { name: T.review })).toHaveCount(0);
    await noRawCodes(page);
    if (EVIDENCE) await page.screenshot({ path: `${EVIDENCE}/r2-register-${locale}-${test.info().project.name}.png`, fullPage: true });
    await proposeRow.getByRole('button', { name: T.propose }).click();
    await expect.poll(() => proposeBodies.length).toBe(1);
    expect(proposeBodies[0]).toEqual({ path: `/api/entitlements/employees/${EMP_ID}/package/propose`, body: { contractId: 'k-propose' } });
    expect(errors).toEqual([]);
  });
}
