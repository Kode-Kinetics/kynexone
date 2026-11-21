import { expect, test, type Page, type Request } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import fs from 'node:fs';

/**
 * Recruitment walkthrough fixes, region B (pipeline, application drawer, add-candidate dialogs, talent pool), against a mocked API.
 *   1 the application drawer and both add-candidate dialogs are real dialogs: named, focus in, Tab trapped, Esc closes, focus returns
 *   2 the pipeline's stage names are translated, the later stages are reachable and signalled, the empty text has contrast
 *   3 blacklist: the action only for the roles the API allows, with a reason; marked in the list and drawer; no move for a blacklisted candidate
 *   4 adding a candidate who already applied shows the API's message and keeps the form usable
 *   5 the talent pool and the pipeline fit a 390px phone with no sideways page scroll
 * Run: RECRUITMENT_FIXES_PORT=5179 RECRUITMENT_FIXES_DIST=.next-claude-recfixB npx playwright test -c e2e/playwright.recruitment-fixes.config.ts recruitment-fixes-B
 * RECRUITMENT_FIXES_SHOTS=<dir> also saves screenshots.
 */

const OPENING = { id: 'op-1', jobCode: 'JOB-1', requisitionId: null, title: 'Sales Executive', departmentName: 'Sales', designationTitle: 'Sales Executive', employmentType: 'Full-Time', headCount: 1, filledCount: 0, description: '', requirements: '', responsibilities: '', salaryFrom: null, salaryTo: null, location: 'Riyadh', status: 'Open', assignedHrName: '', createdAtUtc: '2026-11-01T08:00:00Z', activeApplications: 2, remaining: 1 };
const CANDIDATES = [
  { id: 'c-1', firstName: 'Nora', lastName: 'Alharbi', fullName: 'Nora Alharbi', email: 'nora@example.invalid', phone: '+966 50 123 4567', currentJobTitle: 'Account Manager', currentCompany: 'Example Trading Company', totalExperienceYears: 4, educationLevel: 'Bachelor', nationality: 'Saudi', linkedInUrl: '', resumeUrl: 'https://example.invalid/cv', source: 'Direct', status: 'Active', tags: '', createdAtUtc: '2026-11-01T08:00:00Z', applicationCount: 1 },
  { id: 'c-2', firstName: 'Mallory', lastName: 'Blocked', fullName: 'Mallory Blocked', email: 'mallory@example.invalid', phone: '', currentJobTitle: 'Packer', currentCompany: '', totalExperienceYears: 2, educationLevel: 'Diploma', nationality: '', linkedInUrl: '', resumeUrl: '', source: 'Referral', status: 'Blacklisted', tags: '', createdAtUtc: '2026-11-02T08:00:00Z', applicationCount: 1 },
];
const app = (over: Record<string, unknown> = {}) => ({ id: 'app-1', version: 3, jobOpeningId: 'op-1', jobTitle: 'Sales Executive', candidateId: 'c-1', candidateName: 'Nora Alharbi', candidateEmail: 'nora@example.invalid', stage: 'Applied', stageOrder: 0, status: 'Active', rejectionReason: '', offeredSalary: null, appliedAtUtc: '2026-11-01T08:00:00Z', stageChangedAtUtc: null, hiredAtUtc: null, onboardingDraftId: null, ...over });
const APPS = [app(), app({ id: 'app-2', candidateId: 'c-2', candidateName: 'Mallory Blocked', candidateEmail: 'mallory@example.invalid' })];
const STAGES = ['Applied', 'Screening', 'Assessment', 'Interview', 'Offer', 'Hired'];
const DUPLICATE = 'This candidate has already applied for this opening.';

interface Boot { locale: 'en' | 'ar'; roles?: string[]; apply?: 'ok' | 'duplicate'; createCandidate?: 'ok' | 'duplicate'; tab?: 'pipeline' | 'pool' }

async function boot(page: Page, opts: Boot) {
  const posts: { path: string; body: unknown }[] = [];
  const errors: string[] = [];
  const blacklisted = new Set<string>(['c-2']);
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(locale => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', locale); }, opts.locale);
  await page.route('**/api/**', async route => {
    const request: Request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (request.method() !== 'GET') posts.push({ path, body: request.postDataJSON() });
    const candidates = () => CANDIDATES.map(c => ({ ...c, status: blacklisted.has(c.id) ? 'Blacklisted' : 'Active' }));
    if (path === '/api/auth/me') return json({ id: 'u1', employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', fullName: 'Recruiter Test', roles: opts.roles ?? ['HR Manager'], permissions: ['recruitment.read', 'recruitment.write', 'employees.read', 'dashboard.read'], companies: [{ id: 'c1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] });
    if (path === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/tenant-admin/localization') return json({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'en', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/notifications') return json([]);
    if (path === '/api/recruitment/openings') return json({ items: [OPENING], total: 1, page: 1, pageSize: 50 });
    if (path === '/api/recruitment/openings/op-1') return json({ opening: OPENING, stageCounts: [] });
    if (path === '/api/recruitment/applications/kanban/op-1') return json({ stages: STAGES.map((stage, order) => ({ stage, order, applications: stage === 'Applied' ? APPS : [] })), rejected: [{ ...app({ id: 'app-9', candidateName: 'Rana Rejected', stage: 'Screening', status: 'Rejected' }) }] });
    const detail = /^\/api\/recruitment\/applications\/(app-\d)$/.exec(path);
    if (detail && request.method() === 'GET') {
      const a = APPS.find(x => x.id === detail[1])!;
      const candidate = candidates().find(c => c.id === a.candidateId)!;
      return json({ application: a, candidate, events: [{ id: 'e1', eventType: 'ApplicationCreated', stage: 'Applied', notes: 'Application created.', performedByName: 'HR', createdAtUtc: '2026-11-01T08:00:00Z' }], interviews: [], offer: null });
    }
    if (/^\/api\/recruitment\/candidates\/c-\d\/blacklist$/.test(path)) { blacklisted.add(path.split('/')[4]); return json({ id: path.split('/')[4], status: 'Blacklisted' }); }
    if (path === '/api/recruitment/candidates' && request.method() === 'POST') {
      return opts.createCandidate === 'duplicate' ? json({ message: 'A candidate with this email already exists.' }, 409) : json(CANDIDATES[0]);
    }
    if (path === '/api/recruitment/candidates') {
      const status = url.searchParams.get('status');
      const items = candidates().filter(c => !status || c.status === status);
      return json({ items, total: items.length, page: 1, pageSize: 100 });
    }
    if (path === '/api/recruitment/applications' && request.method() === 'POST') {
      return opts.apply === 'duplicate' ? json({ message: DUPLICATE }, 409) : json(APPS[0]);
    }
    if (path.startsWith('/api/recruitment/')) return json({ items: [], total: 0, page: 1, pageSize: 20 });
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto('/recruitment');
  return { posts, errors };
}

async function shot(page: Page, name: string, locale: string) {
  const dir = process.env.RECRUITMENT_FIXES_SHOTS;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.waitForTimeout(450); // let a docked drawer finish sliding in
  await page.screenshot({ path: `${dir}/B-${name}-${locale}-${page.viewportSize()!.width >= 1000 ? 'desktop' : 'phone'}.png` });
}

const openTab = (page: Page, name: RegExp) => page.getByRole('button', { name }).first().click();
const POOL = /^Talent Pool|^مجمع المواهب|^قاعدة المواهب/;
const OPENINGS = /^Job Openings|^الوظائف الشاغرة/;
async function openPipeline(page: Page) {
  await openTab(page, OPENINGS);
  await page.getByText('Sales Executive').first().click();
  await expect(page.getByText('Nora Alharbi').first()).toBeVisible();
}
const noPageScroll = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth);
const inViewport = (page: Page, selector: string) => page.locator(selector).evaluateAll((els, width) => els.every(el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.left >= -1 && r.right <= (width as number) + 1; }), 390);

for (const locale of ['en', 'ar'] as const) {
  const L = locale === 'en'
    ? { close: 'Close', blacklist: 'Blacklist', blacklistAny: /^Blacklist/, blacklisted: 'Blacklisted', stages: STAGES, addCandidate: /Add Candidate/, addToPipeline: /Add to Pipeline/, alreadyApplied: DUPLICATE, emailExists: 'A candidate with this email already exists.', noLonger: 'The application is no longer active.' }
    : { close: 'إغلاق', blacklist: 'إدراج في القائمة السوداء', blacklistAny: /إدراج.*القائمة السوداء/, blacklisted: 'مدرج في القائمة السوداء', stages: ['تم التقديم', 'الفرز', 'التقييم', 'المقابلة', 'العرض', 'تم التعيين'], addCandidate: /إضافة مرشح/, addToPipeline: /إضافة إلى المسار|إضافة إلى مسار/, alreadyApplied: 'سبق لهذا المرشح التقدم لهذه الوظيفة.', emailExists: 'يوجد مرشح بهذا البريد الإلكتروني بالفعل.', noLonger: '' };

  test.describe(`recruitment fixes B (${locale})`, () => {
    test('1 the application drawer is a named dialog: focus in, Tab trapped, Esc closes, focus returns to the card', async ({ page }) => {
      const { errors } = await boot(page, { locale });
      await openPipeline(page);
      const card = page.getByRole('button', { name: /Nora Alharbi/ }).first();
      await card.focus();
      await card.click();
      const dialog = page.getByRole('dialog', { name: 'Nora Alharbi' });
      await expect(dialog).toBeVisible();
      await expect(dialog).toHaveAttribute('aria-modal', 'true');
      await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]'))).toBe(true);
      for (let i = 0; i < 25; i++) {
        await page.keyboard.press('Tab');
        expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), `Tab ${i + 1} stays in the drawer`).toBe(true);
      }
      await shot(page, 'drawer', locale);
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(card).toBeFocused();
      expect(errors).toEqual([]);
    });

    test('1b the Talent Pool add dialog is a named dialog that Esc closes, returning focus to its button', async ({ page }) => {
      await boot(page, { locale });
      await openTab(page, POOL);
      const opener = page.getByRole('button', { name: L.addCandidate }).first();
      await opener.focus();
      await opener.click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await expect(dialog).toHaveAttribute('aria-modal', 'true');
      await expect(dialog).toHaveAccessibleName(/.+/);
      for (let i = 0; i < 25; i++) {
        await page.keyboard.press('Tab');
        expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), `Tab ${i + 1}`).toBe(true);
      }
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(opener).toBeFocused();
    });

    test('1c the pipeline add dialog is a named dialog that Esc closes, returning focus to its button', async ({ page }) => {
      await boot(page, { locale });
      await openPipeline(page);
      const opener = page.getByRole('button', { name: L.addCandidate }).first();
      await opener.focus();
      await opener.click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await expect(dialog).toHaveAttribute('aria-modal', 'true');
      await expect(dialog).toHaveAccessibleName(/.+/);
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(opener).toBeFocused();
    });

    test('2 the stage columns are named in the reader language and the later stages are reachable by keyboard with a visible cue', async ({ page }) => {
      await boot(page, { locale });
      await openPipeline(page);
      const region = page.getByRole('region', { name: locale === 'en' ? 'Hiring stages, scrolls sideways' : 'مراحل التوظيف، تُمرَّر أفقيًا' });
      await expect(region).toBeVisible();
      for (const name of L.stages) await expect(region.getByText(name, { exact: true }).first()).toBeAttached();
      if (locale === 'ar') for (const name of STAGES) await expect(region.getByText(name, { exact: true })).toHaveCount(0);
      const width = page.viewportSize()!.width;
      if (width < 1000) {
        // A phone shows a column or two at a time: the cue and the keyboard path matter most there.
        await expect(page.getByTestId('scroll-fade-end')).toBeVisible();
      } else {
        await expect(page.getByTestId('scroll-fade-end')).toBeVisible();
      }
      await shot(page, 'kanban', locale);
      await region.focus();
      await expect(region).toBeFocused();
      await page.keyboard.press('End');
      // The Hired column is in view (on a phone the Rejected column beside it takes the last of the width).
      const hired = region.getByText(L.stages[5], { exact: true }).first().locator('xpath=ancestor::div[contains(@class,"w-56")][1]');
      await expect(hired).toBeInViewport();
      await expect(page.getByTestId('scroll-fade-end')).toHaveCount(0);
      expect(await noPageScroll(page)).toBe(true);
    });

    test('2c the empty-column text reads at 4.5:1 or better, and desktop has explicit scroll controls for the cut-off columns', async ({ page }) => {
      await boot(page, { locale });
      await openPipeline(page);
      const rgb = (css: string) => (css.match(/[\d.]+/g) ?? []).slice(0, 3).map(Number);
      const lum = (c: number[]) => { const [r, g, b] = c.map(v => { const x = v / 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
      const empty = page.getByText(locale === 'en' ? 'No candidates' : 'لا يوجد مرشحون', { exact: true }).first();
      const colours = await empty.evaluate(el => { let bgEl: Element | null = el; let bg = 'rgba(0, 0, 0, 0)'; while (bgEl && /rgba\(.*, 0\)|transparent/.test(bg)) { bg = getComputedStyle(bgEl).backgroundColor; bgEl = bgEl.parentElement; } return { fg: getComputedStyle(el).color, bg }; });
      const [hi, lo] = [lum(rgb(colours.fg)), lum(rgb(colours.bg))].sort((a, b) => b - a);
      expect((hi + 0.05) / (lo + 0.05)).toBeGreaterThanOrEqual(4.5);
      if (page.viewportSize()!.width >= 1000) {
        const later = page.getByRole('button', { name: locale === 'en' ? 'Show later stages' : 'عرض المراحل التالية' });
        const earlier = page.getByRole('button', { name: locale === 'en' ? 'Show earlier stages' : 'عرض المراحل السابقة' });
        await expect(later).toBeVisible();
        await expect(earlier).toBeDisabled();
        await later.click();
        await expect(earlier).toBeEnabled();
        await later.click(); await later.click();
        await expect(later).toBeDisabled();
        await expect(page.getByRole('region', { name: locale === 'en' ? 'Hiring stages, scrolls sideways' : 'مراحل التوظيف، تُمرَّر أفقيًا' }).getByText(L.stages[5], { exact: true }).first()).toBeInViewport();
      }
    });

    test('2b the kanban has no serious colour-contrast violation (the empty-column text)', async ({ page }) => {
      await boot(page, { locale });
      await openPipeline(page);
      await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
      const results = await new AxeBuilder({ page }).withRules(['color-contrast']).analyze();
      expect(results.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target.join(' ')) }))).toEqual([]);
    });

    test('3 HR Manager sees Blacklist, gives a reason of at least 10 characters, and the person is marked Blacklisted', async ({ page }) => {
      const { posts } = await boot(page, { locale, roles: ['HR Manager'] });
      await openTab(page, POOL);
      await expect(page.getByText('Nora Alharbi')).toBeVisible();
      // The person who is already blacklisted is marked and offers no action.
      const mallory = page.getByRole('row', { name: /Mallory Blocked/ });
      await expect(mallory.getByText(L.blacklisted, { exact: true })).toBeVisible();
      await expect(mallory.getByRole('button', { name: L.blacklistAny })).toHaveCount(0);
      const nora = page.getByRole('row', { name: /Nora Alharbi/ });
      await expect(nora.getByText(L.blacklisted, { exact: true })).toHaveCount(0);
      await shot(page, 'pool', locale);
      await nora.getByRole('button', { name: L.blacklistAny }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      const confirm = dialog.getByRole('button').filter({ hasText: locale === 'en' ? /^Blacklist$/ : /^إدراج في القائمة السوداء$/ });
      await expect(confirm).toBeDisabled();
      await dialog.getByTestId('confirm-reason').fill('short');
      await expect(confirm).toBeDisabled();
      await dialog.getByTestId('confirm-reason').fill('Falsified two reference letters.');
      await shot(page, 'blacklist-confirm', locale);
      await confirm.click();
      await expect.poll(() => posts.find(p => p.path === '/api/recruitment/candidates/c-1/blacklist')?.body).toEqual({ reason: 'Falsified two reference letters.' });
      await expect(page.getByRole('row', { name: /Nora Alharbi/ }).getByText(L.blacklisted, { exact: true })).toBeVisible();
    });

    test('3b HR Officer is not offered Blacklist anywhere (the API refuses them)', async ({ page }) => {
      await boot(page, { locale, roles: ['HR Officer'] });
      await openTab(page, POOL);
      await expect(page.getByText('Nora Alharbi')).toBeVisible();
      await expect(page.getByRole('button', { name: L.blacklistAny })).toHaveCount(0);
      await openPipeline(page);
      await page.getByRole('button', { name: /Nora Alharbi/ }).first().click();
      await expect(page.getByRole('dialog', { name: 'Nora Alharbi' })).toBeVisible();
      await expect(page.getByRole('button', { name: L.blacklistAny })).toHaveCount(0);
    });

    test('3c a blacklisted candidate in the drawer is marked, has no Blacklist action, and Move to the next stage is disabled with the reason', async ({ page }) => {
      await boot(page, { locale, roles: ['HR Manager'] });
      await openPipeline(page);
      await page.getByRole('button', { name: /Mallory Blocked/ }).first().click();
      const dialog = page.getByRole('dialog', { name: 'Mallory Blocked' });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByText(L.blacklisted, { exact: true }).first()).toBeVisible();
      await expect(dialog.getByRole('button', { name: new RegExp(`^${L.blacklist}$`) })).toHaveCount(0);
      const move = dialog.getByRole('button', { name: locale === 'en' ? /Move to Screening/ : /النقل إلى الفرز|نقل إلى الفرز|الانتقال إلى الفرز/ });
      await expect(move).toBeDisabled();
      const reason = locale === 'en' ? 'Blacklisted candidates cannot be moved to another stage.' : 'لا يمكن نقل المرشحين المدرجين في القائمة السوداء إلى مرحلة أخرى.';
      await expect(dialog.getByText(reason)).toBeVisible();
      await expect(move).toHaveAccessibleDescription(reason);
      await shot(page, 'drawer-blacklisted', locale);
    });

    test('3e the kanban card of a blacklisted candidate carries its own Blacklisted marker, and an active one does not', async ({ page }) => {
      await boot(page, { locale, roles: ['HR Manager'] });
      await openPipeline(page);
      await expect(page.getByRole('button', { name: /Mallory Blocked/ }).first().getByText(L.blacklisted, { exact: true })).toBeVisible();
      await expect(page.getByRole('button', { name: /Nora Alharbi/ }).first().getByText(L.blacklisted, { exact: true })).toHaveCount(0);
    });

    test('3f the blacklisted drawer: label and value are separate, the note is translated, chips have room, disabled controls stay legible', async ({ page }) => {
      await boot(page, { locale, roles: ['HR Manager'] });
      await openPipeline(page);
      await page.getByRole('button', { name: /Mallory Blocked/ }).first().click();
      const dialog = page.getByRole('dialog', { name: 'Mallory Blocked' });
      await expect(dialog).toBeVisible();
      await page.waitForTimeout(450);
      // Experience and Source (and Email and Phone) are a term over its value, in a different colour, never one run of text.
      const pairs = dialog.locator('dl > div');
      await expect(pairs).toHaveCount(4);
      for (let i = 0; i < 4; i++) {
        const facts = await pairs.nth(i).evaluate(el => { const a = el.querySelector('dt')!; const b = el.querySelector('dd')!; return { stacked: b.getBoundingClientRect().top >= a.getBoundingClientRect().bottom - 0.5, apart: getComputedStyle(a).color !== getComputedStyle(b).color }; });
        expect(facts, `pair ${i}`).toEqual({ stacked: true, apart: true });
      }
      // The server's default note is shown in the reader's language.
      if (locale === 'ar') { await expect(dialog.getByText('Application created.')).toHaveCount(0); await expect(dialog.getByText('أُنشئ الطلب.')).toBeVisible(); }
      // The chips are not hard against the header divider above them.
      const room = await dialog.locator('[data-testid="drawer-standing"]').evaluate(el => { const r = el.getBoundingClientRect(); const first = el.firstElementChild!.getBoundingClientRect(); return first.top - r.top; });
      expect(room).toBeGreaterThanOrEqual(8);
      // A disabled control still reads as disabled (a plain muted fill) and its label has contrast of at least 4.5:1.
      const lum = (rgb: number[]) => { const [r, g, b] = rgb.map(v => { const c = v / 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
      const ratio = (a: number[], b: number[]) => { const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x); return (hi + 0.05) / (lo + 0.05); };
      const rgb = (css: string) => (css.match(/[\d.]+/g) ?? []).slice(0, 3).map(Number);
      for (const name of [locale === 'en' ? /Move to Screening/ : /نقل إلى الفرز/, locale === 'en' ? /^Reject$/ : /^رفض$/]) {
        const button = dialog.getByRole('button', { name });
        await expect(button).toBeDisabled();
        const style = await button.evaluate(el => { const cs = getComputedStyle(el); return { color: cs.color, bg: cs.backgroundColor, opacity: cs.opacity }; });
        expect(style.opacity, String(name)).toBe('1');
        expect(ratio(rgb(style.color), rgb(style.bg)), `${name} label contrast`).toBeGreaterThanOrEqual(4.5);
      }
    });

    test('3d an active candidate in the drawer can be blacklisted by HR Manager, and the drawer then shows the mark', async ({ page }) => {
      const { posts } = await boot(page, { locale, roles: ['Admin'] });
      await openPipeline(page);
      await page.getByRole('button', { name: /Nora Alharbi/ }).first().click();
      const dialog = page.getByRole('dialog', { name: 'Nora Alharbi' });
      await expect(dialog.getByRole('button', { name: /Move to|النقل إلى|نقل إلى|الانتقال إلى/ })).toBeEnabled();
      await dialog.getByRole('button', { name: new RegExp(`^${L.blacklist}$`) }).click();
      const confirmDialog = page.getByRole('dialog').last();
      await confirmDialog.getByTestId('confirm-reason').fill('Did not attend three agreed interviews.');
      await confirmDialog.getByRole('button').filter({ hasText: locale === 'en' ? /^Blacklist$/ : /^إدراج في القائمة السوداء$/ }).click();
      await expect.poll(() => posts.find(p => p.path === '/api/recruitment/candidates/c-1/blacklist')?.body).toEqual({ reason: 'Did not attend three agreed interviews.' });
      await expect(dialog.getByText(L.blacklisted, { exact: true }).first()).toBeVisible();
      await expect(dialog.getByRole('button', { name: /Move to|النقل إلى|نقل إلى|الانتقال إلى/ })).toBeDisabled();
    });

    test('4 adding a candidate who already applied shows the API message inline and keeps the form usable', async ({ page }) => {
      const { posts } = await boot(page, { locale, apply: 'duplicate' });
      await openPipeline(page);
      await page.getByRole('button', { name: L.addCandidate }).first().click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await dialog.getByRole('button', { name: L.addToPipeline }).click();
      await expect(dialog.getByRole('alert')).toHaveText(L.alreadyApplied);
      await expect(dialog).toBeVisible();
      await expect(dialog.getByRole('combobox')).toBeEnabled();
      await expect(dialog.getByRole('button', { name: L.addToPipeline })).toBeEnabled();
      expect(posts.filter(p => p.path === '/api/recruitment/applications')).toHaveLength(1);
      await shot(page, 'duplicate-add', locale);
      // Choosing another candidate clears the message.
      await dialog.getByRole('combobox').selectOption({ index: 0 });
      await expect(dialog.getByRole('alert')).toHaveCount(0);
    });

    test('4b adding a candidate whose email already exists shows the message in the reader language', async ({ page }) => {
      await boot(page, { locale, createCandidate: 'duplicate' });
      await openTab(page, POOL);
      await page.getByRole('button', { name: L.addCandidate }).first().click();
      const dialog = page.getByRole('dialog');
      await dialog.getByPlaceholder(/First name|الاسم الأول/).fill('Nora');
      await dialog.getByPlaceholder('email@example.com').fill('nora@example.invalid');
      await dialog.getByRole('button', { name: L.addCandidate }).click();
      await expect(dialog.getByRole('alert')).toHaveText(L.emailExists);
      await expect(dialog).toBeVisible();
    });

    test('5 the Talent Pool fits the screen: no sideways page scroll, every row and action reachable', async ({ page }) => {
      await boot(page, { locale, roles: ['HR Manager'] });
      await openTab(page, POOL);
      await expect(page.getByText('Nora Alharbi')).toBeVisible();
      expect(await noPageScroll(page)).toBe(true);
      const phone = page.viewportSize()!.width < 640;
      if (phone) {
        for (const name of ['Nora Alharbi', 'Mallory Blocked']) await expect(page.getByText(name, { exact: true })).toBeInViewport();
        expect(await inViewport(page, 'tbody tr td:visible')).toBe(true);
        const action = page.getByRole('row', { name: /Nora Alharbi/ }).getByRole('button', { name: L.blacklistAny });
        await expect(action).toBeInViewport();
        const box = await action.boundingBox();
        expect(box!.height).toBeGreaterThanOrEqual(28);
      } else {
        await expect(page.getByRole('columnheader').first()).toBeVisible();
        expect(await page.getByRole('columnheader').count()).toBeGreaterThanOrEqual(7);
      }
      await shot(page, 'pool-layout', locale);
    });

    test('5b the pipeline header and cards fit the screen with no sideways page scroll', async ({ page }) => {
      await boot(page, { locale, roles: ['HR Manager'] });
      await openPipeline(page);
      expect(await noPageScroll(page)).toBe(true);
      const add = page.getByRole('button', { name: L.addCandidate }).first();
      await expect(add).toBeInViewport();
      await expect(page.getByRole('button', { name: /Nora Alharbi/ }).first()).toBeInViewport();
      await shot(page, 'kanban-layout', locale);
    });
  });
}
