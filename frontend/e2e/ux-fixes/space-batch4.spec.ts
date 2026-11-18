import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { translate } from '../../src/i18n/translations';
import { ALL_PERMISSIONS, boot, type ApiMocks, type Persona } from '../setup-consolidation/fixture';
import { radar } from '../fixtures/renewals/fixtures';

/**
 * Space pass, batch 4 (Screen Standard 1 and 2): the main task of a screen sits in the first screenful at 1366x768 and 1280x720,
 * in English and Arabic, and the page never scrolls sideways. Every API call is intercepted; nothing is read or written.
 *
 *   /leave              the section tabs start above y=240 (the company filter was a full-width banner of its own: tabs at 330)
 *   /shifts             the header buttons do not wrap and the roster shows at least 11 employees at 768 high, 10 at 720 (it showed 8 and 7; Auto Plan and AI Plan wrapped)
 *   /hr-letters         Letter settings sits in the header row, not on a row of its own above the tabs
 *   /contract-renewals  the renewal reviews table starts above y=560 (600 at 720 high); it started at 660 and 700 under 13 tiles
 *   /recruitment        the Offers list shows at least four offers (three at 720 high); it showed two and one
 *   /setup?tab=grades   at least six grades in view (five at 720 high), Edit and Remove side by side, no sideways scroll (four rows, buttons stacked)
 *   /setup?tab=departments  at least seven departments in view (it showed four or five)
 *
 * SPACE_SHOTS_DIR=<dir> also saves a screenshot per screen, viewport and language (the before/after evidence).
 */
const SHOTS = process.env.SPACE_SHOTS_DIR;
const VIEWPORTS = [{ width: 1366, height: 768 }, { width: 1280, height: 720 }] as const;
const LOCALES = ['en', 'ar'] as const;
type Locale = (typeof LOCALES)[number];

async function shot(page: Page, screen: string, locale: Locale) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  const vp = page.viewportSize();
  await page.screenshot({ path: `${SHOTS}/${process.env.SPACE_PHASE ?? 'run'}-${screen}-${locale}-${vp?.width}x${vp?.height}.png` });
}
const noSideways = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
const topOf = async (page: Page, selector: string) => page.locator(selector).first().evaluate(el => Math.round(el.getBoundingClientRect().top));

const persona = (roles: string[], extra: string[] = []): Persona => ({ roles, permissions: [...ALL_PERMISSIONS, 'contracts.renewal.read', 'contracts.renewal.manage', ...extra] });
const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });

// ── /shifts ────────────────────────────────────────────────────────────────────────────────────────────────────────────
const weekFrom = (() => { const d = new Date(); const day = (d.getDay() + 6) % 7; d.setDate(d.getDate() - day); return d; })();
const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const SHIFT = { id: 's1', tenantId: 't', code: 'M', name: 'Morning', startTime: '08:00', endTime: '16:00', breakMinutes: 30, color: '#10b981', isActive: true, createdAtUtc: '2026-01-01T00:00:00Z' };
const ROSTER = {
  from: iso(weekFrom), to: iso(weekFrom),
  employees: Array.from({ length: 16 }, (_, i) => ({ id: i + 1, fullName: `Employee Number ${i + 1}`, department: 'Human Resources', employeeCode: `E-${i + 1}` })),
  assignments: [{ id: 'a1', employeeId: 1, date: iso(weekFrom), shiftDefinitionId: 's1', shiftName: 'Morning', shiftCode: 'M', shiftColor: '#10b981' }],
};

// ── /recruitment (Offers) ──────────────────────────────────────────────────────────────────────────────────────────────
const offer = (n: number) => ({
  id: `offer-${n}`, candidateName: `Candidate ${n}`, position: 'Sales Executive', revisionNumber: 1, version: 10, status: 'Sent', communicationOutcome: 'AcceptedByRelay', responseDeadline: '2026-11-19T00:00:00Z',
});

// ── /setup (Grades, Departments) ───────────────────────────────────────────────────────────────────────────────────────
const grade = (n: number) => ({ id: `g-${n}`, code: `G${n}`, name: `Grade ${n} - Executive`, band: n % 2 ? 'Senior Manager' : 'Assistant Manager', level: n, minSalary: 10000 * n, midSalary: 12000 * n, maxSalary: 17000 * n, currency: 'SAR', isActive: true, companyId: 'fixture-company' });
const department = (n: number) => ({ id: `d-${n}`, code: `D${n}`, nameEn: `Department ${n}`, nameAr: `القسم ${n}`, isActive: true, companyId: 'fixture-company', costCenterId: null });

for (const locale of LOCALES) {
  for (const viewport of VIEWPORTS) {
    test.describe(`space batch 4 (${locale} ${viewport.width}x${viewport.height})`, () => {
      test.use({ viewport });

      test('/leave: the section tabs start in the first screenful and the company filter stays', async ({ page }) => {
        await boot(page, { persona: persona(['HR Manager']), locale, url: '/leave' });
        const tabs = page.getByRole('tablist').first();
        await expect(tabs).toBeVisible({ timeout: 60_000 });
        const filter = page.getByRole('combobox', { name: translate(locale, 'Filter by company') });
        await expect(filter).toBeVisible({ timeout: 30_000 });
        await shot(page, 'leave', locale);
        expect(await tabs.evaluate(el => Math.round(el.getBoundingClientRect().top)), 'section tabs top').toBeLessThanOrEqual(240);
        expect((await filter.boundingBox())!.width, 'company filter width').toBeLessThanOrEqual(280);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('/shifts: the header buttons stay on one line and the roster shows at least 11 employees', async ({ page }) => {
        const mocks: ApiMocks = { '/api/shifts/definitions': [SHIFT], '/api/shifts/roster': ROSTER };
        await boot(page, { persona: persona(['HR Manager']), locale, url: '/shifts', mocks });
        await expect(page.locator('main table tbody tr').first()).toBeVisible({ timeout: 60_000 });
        await expect(page.locator('main table tbody tr')).toHaveCount(16);
        await shot(page, 'shifts', locale);
        const buttonHeights = await page.getByRole('button', { name: new RegExp(`^(${translate(locale, 'Auto Plan')}|${translate(locale, 'AI Plan')})$`) }).evaluateAll(els => els.map(el => Math.round(el.getBoundingClientRect().height)));
        expect(buttonHeights.length, 'Auto Plan and AI Plan are present').toBe(2);
        for (const h of buttonHeights) expect(h, 'a plan button is one line high').toBeLessThanOrEqual(40);
        const rowsInView = await page.locator('main table tbody tr').evaluateAll((rows, h) => rows.filter(r => r.getBoundingClientRect().bottom <= h).length, viewport.height);
        expect(rowsInView, 'roster rows fully in view').toBeGreaterThanOrEqual(viewport.height === 768 ? 11 : 10);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('/hr-letters: Letter settings sits in the header row', async ({ page }) => {
        await boot(page, { persona: persona(['HR Manager']), locale, url: '/hr-letters' });
        const settings = page.getByRole('link', { name: translate(locale, 'Letter settings') });
        await expect(settings).toBeVisible({ timeout: 60_000 });
        await shot(page, 'hr-letters', locale);
        const h1 = await page.locator('h1').first().boundingBox();
        const s = await settings.boundingBox();
        expect(s!.y, 'Letter settings is level with the title, not under the purpose line').toBeLessThanOrEqual(h1!.y + 30);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('/contract-renewals: the renewal reviews table starts in the first screenful', async ({ page }) => {
        await boot(page, {
          persona: persona(['HR Manager']), locale, url: '/contract-renewals',
          mocks: { '/api/contracts/renewals/radar': radar },
        });
        const reviews = page.getByRole('region', { name: translate(locale, 'Renewal reviews') });
        await expect(reviews.locator('tbody tr').first()).toBeVisible({ timeout: 60_000 });
        await shot(page, 'contract-renewals', locale);
        expect(await reviews.evaluate(el => Math.round(el.getBoundingClientRect().top)), 'reviews table top').toBeLessThanOrEqual(viewport.height === 768 ? 560 : 600);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('/recruitment Offers: at least four offers are in view', async ({ page }) => {
        await boot(page, {
          persona: persona(['HR Manager']), locale, url: '/recruitment?tab=offers',
          mocks: { '/api/recruitment/offers/history': { total: 12, items: Array.from({ length: 12 }, (_, i) => offer(i + 1)) } },
        });
        // The register's own words come from the offer-lifecycle dictionary, so the rows are found by structure, not by label.
        const rows = page.locator('main section article');
        await expect(rows.first()).toBeVisible({ timeout: 60_000 });
        await expect(rows).toHaveCount(12);
        await shot(page, 'recruitment-offers', locale);
        const inView = await rows.evaluateAll((els, h) => els.filter(el => el.getBoundingClientRect().bottom <= h).length, viewport.height);
        expect(inView, 'offers fully in view').toBeGreaterThanOrEqual(viewport.height === 768 ? 4 : 3);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('/setup Grades: at least six grades are in view, Edit and Remove side by side, no sideways scroll', async ({ page }) => {
        await boot(page, { persona: persona(['HR Manager'], ['organization.delete']), locale, url: '/setup?tab=grades', mocks: { '/api/grades': paged(Array.from({ length: 14 }, (_, i) => grade(i + 1))) } });
        const rows = page.locator('[data-module-section], main').locator('table tbody tr');
        await expect(rows.first()).toBeVisible({ timeout: 60_000 });
        await expect(rows).toHaveCount(14);
        await shot(page, 'setup-grades', locale);
        const inView = await rows.evaluateAll((trs, h) => trs.filter(r => r.getBoundingClientRect().bottom <= h).length, viewport.height);
        expect(inView, 'grade rows fully in view').toBeGreaterThanOrEqual(viewport.height === 768 ? 6 : 5);
        const buttons = await rows.first().getByRole('button').evaluateAll(els => els.map(el => Math.round(el.getBoundingClientRect().top)));
        expect(new Set(buttons).size, 'Edit and Remove share one line').toBe(1);
        const scroller = await page.locator('main table').first().evaluate(t => t.parentElement!.scrollWidth - t.parentElement!.clientWidth);
        expect(scroller, 'the grades table scrolls sideways by (px)').toBeLessThanOrEqual(0);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });

      test('/setup Departments: at least seven departments are in view', async ({ page }) => {
        await boot(page, { persona: persona(['HR Manager'], ['organization.delete']), locale, url: '/setup?tab=departments', mocks: { '/api/departments': paged(Array.from({ length: 14 }, (_, i) => department(i + 1))) } });
        const rows = page.locator('main table tbody tr');
        await expect(rows.first()).toBeVisible({ timeout: 60_000 });
        await expect(rows).toHaveCount(14);
        await shot(page, 'setup-departments', locale);
        const inView = await rows.evaluateAll((trs, h) => trs.filter(r => r.getBoundingClientRect().bottom <= h).length, viewport.height);
        expect(inView, 'department rows fully in view').toBeGreaterThanOrEqual(7);
        const scroller = await page.locator('main table').first().evaluate(t => t.parentElement!.scrollWidth - t.parentElement!.clientWidth);
        expect(scroller, 'the departments table scrolls sideways by (px)').toBeLessThanOrEqual(0);
        expect(await noSideways(page)).toBeLessThanOrEqual(0);
      });
    });
  }
}
