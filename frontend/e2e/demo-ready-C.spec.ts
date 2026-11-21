import { expect, test, type Page } from '@playwright/test';
import fs from 'node:fs';

/**
 * Demo-readiness follow-ups from the live walk, stream C, against a mocked API: nothing is read or written.
 *   1 New hires: the blocked-row sentence is plain words (no permission key), translated
 *   3 Sign-in / welcome footer links are translated
 *   4 New hires on a phone: a card list with name, status, next action and a 44 px View, nothing clipped
 *   5 A Closed opening does not say "1 position left"
 *   6 A slow requisition submit shows a spinner and aria-busy on its own button
 * Run: DEMO_READY_PORT=5186 DEMO_READY_DIST=.next-claude-demoreadyC npx playwright test -c e2e/playwright.demo-ready.config.ts demo-ready-C
 */
const ISO = '2026-11-19T08:00:00Z';
const REVIEW = 'Accepted-offer compensation needs review and confirmation by a user with employees.sensitive before activation.';
const PERMS = ['recruitment.read', 'recruitment.write', 'recruitment.approve', 'employees.read', 'employees.approve', 'dashboard.read', 'setup.read'];
const draft = (over: Record<string, unknown>) => ({ id: 'd-1', status: 'Submitted', currentStep: 'Review', name: 'Alice Almutairi', arabicName: 'أليس المطيري', department: 'Operations', designation: 'Operations Specialist', branch: 'Riyadh', joiningDate: '2031-01-15', source: 'Recruitment', applicationId: 'app-1', jobTitle: 'Operations Specialist', createdByUserId: 'u-2', createdByName: 'Dairy HR', isMine: false, canApprove: false, approveBlockedReason: REVIEW, profileCompletenessScore: 80, createdAtUtc: ISO, submittedAtUtc: ISO, decidedAtUtc: null, decidedByName: null, decisionReason: null, activatedEmployeeId: null, activatedEmployeeCode: null, ...over });
const DRAFTS = [draft({}), draft({ id: 'd-2', name: 'Omar Haddad', status: 'Draft', approveBlockedReason: null, isMine: true })];
const opening = (over: Record<string, unknown>) => ({ id: 'o-1', jobCode: 'JOB-2026-0001', requisitionId: null, title: 'Sales Executive', departmentName: 'Sales', designationTitle: 'Sales Executive', employmentType: 'Full-Time', headCount: 2, filledCount: 1, description: '', requirements: '', responsibilities: '', salaryFrom: null, salaryTo: null, location: 'Riyadh', status: 'Open', assignedHrName: 'HR', createdAtUtc: ISO, activeApplications: 0, ...over });
const OPENINGS = [opening({}), opening({ id: 'o-2', jobCode: 'JOB-2026-0004', title: 'Closed Role', status: 'Closed', headCount: 1, filledCount: 0 })];
const reqRow = { id: 'rq-1', requisitionNumber: 'MRQ-0001', departmentId: 'dep-1', departmentName: 'Operations', designationId: 'des-1', designationTitle: 'Operations Specialist', headCount: 1, priority: 'Normal', status: 'Draft', createdAtUtc: ISO, employmentType: 'Full-Time', justification: 'x' };

async function boot(page: Page, locale: 'en' | 'ar', url: string, token = true) {
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(([loc, tok]) => { if (tok) localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', loc as string); }, [locale, token] as const);
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (path === '/api/auth/me') return json({ id: 'user-me', employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', fullName: 'Recruiter Test', roles: ['Admin'], permissions: PERMS });
    if (path === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/tenant-admin/localization') return json({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: locale, defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/notifications') return json([]);
    if (path === '/api/employees/drafts') return json({ items: DRAFTS, total: DRAFTS.length, page: 1, pageSize: 25, counts: { awaitingApproval: 1, draft: 1, activated: 0, rejected: 0, cancelled: 0 } });
    if (path === '/api/recruitment/openings') return json({ items: OPENINGS, total: OPENINGS.length });
    if (path === '/api/recruitment/openings/stats') return json({ openPositions: 4, activeApplications: 12, offersPending: 2, hiredThisMonth: 1 });
    if (path === '/api/recruitment/requisitions/stats') return json({ draft: 1, pending: 0, approved: 0, converted: 0 });
    if (path === '/api/recruitment/requisitions/rq-1/submit') { await new Promise(r => setTimeout(r, 2500)); return json({ ...reqRow, status: 'PendingApproval' }); }
    if (path === '/api/recruitment/requisitions' && request.method() === 'GET') return json({ items: [reqRow], total: 1, page: 1, pageSize: 25 });
    if (path.startsWith('/api/recruitment/')) return json({ items: [], total: 0, page: 1, pageSize: 20 });
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(url);
  return { errors };
}
async function shot(page: Page, name: string, locale: string) {
  const dir = process.env.DEMO_READY_SHOTS_DIR;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: `${dir}/C-${name}-${locale}-${page.viewportSize()!.width >= 1000 ? 'desktop' : 'phone'}.png` });
}
const isPhone = (page: Page) => page.viewportSize()!.width < 500;
const ARABIC = /[؀-ۿ]/;

for (const locale of ['en', 'ar'] as const) {
  test.describe(`demo-ready C (${locale})`, () => {
    test('1+4 New hires: plain blocked sentence, no key; on a phone every row is a card with a 44 px View in view', async ({ page }) => {
      const { errors } = await boot(page, locale, '/people/new-hires');
      const visible = page.locator(isPhone(page) ? '[data-testid="new-hires-cards"]' : 'table');
      await expect(visible).toContainText(locale === 'en' ? 'Alice Almutairi' : 'Alice');
      const text = (await page.locator('main').innerText());
      expect(text).not.toMatch(/employees\.(sensitive|approve)/);
      expect(text).not.toContain('Accepted-offer compensation needs review');
      if (locale === 'en') expect(text).toContain('may see sensitive employee data');
      else expect(await visible.innerText()).toMatch(ARABIC);
      if (isPhone(page)) {
        const cards = page.locator('[data-testid="new-hires-cards"] li');
        await expect(cards).toHaveCount(2);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
        for (let i = 0; i < 2; i++) {
          const view = cards.nth(i).getByRole('button').first();
          const b = (await view.boundingBox())!;
          expect(b.height).toBeGreaterThanOrEqual(43.5);
          expect(b.x).toBeGreaterThanOrEqual(0);
          expect(b.x + b.width).toBeLessThanOrEqual(390);
        }
      } else {
        await expect(page.locator('[data-testid="new-hires-cards"]')).toBeHidden();
      }
      await shot(page, 'newhires', locale);
      expect(errors).toEqual([]);
    });

    test('3 sign-in footer links are translated', async ({ page }) => {
      await boot(page, locale, '/login', false);
      const nav = page.getByRole('navigation', { name: /Legal and security/ }).first();
      await expect(nav).toBeVisible();
      const links = (await nav.innerText());
      if (locale === 'ar') { expect(links).not.toMatch(/Security|Privacy|Terms/); expect(links).toMatch(ARABIC); }
      else expect(links).toMatch(/Security[\s\S]*Privacy[\s\S]*Terms/);
      await shot(page, 'footer', locale);
    });

    test('5 a Closed opening shows its status, not "positions left"', async ({ page }) => {
      await boot(page, locale, '/recruitment');
      await page.locator('#recruitment-tab-openings').click();
      const open = page.locator('button', { hasText: 'JOB-2026-0001' });
      const closed = page.locator('button', { hasText: 'JOB-2026-0004' });
      await expect(open).toBeVisible();
      await expect(closed).toBeVisible();
      await expect(closed).not.toContainText(locale === 'en' ? /position(s)? left/ : /متبقٍ/);
      await expect(open).toContainText(locale === 'en' ? /1 position left|positions left/ : /متبقٍ/);
      await shot(page, 'openings', locale);
    });

    test('6 a slow requisition submit shows a spinner and aria-busy on its own button', async ({ page }) => {
      await boot(page, locale, '/recruitment');
      await page.locator('#recruitment-tab-requisitions').click();
      const scope = page.locator(isPhone(page) ? 'ul[aria-label]' : 'table');
      const submit = scope.getByRole('button', { name: locale === 'en' ? 'Submit for approval' : 'إرسال للموافقة' });
      await submit.click();
      await expect(submit).toHaveAttribute('aria-busy', 'true');
      await expect(submit.locator('svg.animate-spin')).toBeVisible();
      await shot(page, 'req-busy', locale);
    });
  });
}
