import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type TestInfo } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import type { CompanySetupDraft, CompanySetupDraftBody, CompanySetupPlan } from '../src/api/companySetup';
import { PART_COPY, type PartId } from '../src/lib/companySetupWizard';

/**
 * Step 5, Work & pay rules, one part at a time (docs/design/company-setup-wizard.md, Addendum B4/B5): the embedded
 * Leave and Country rules editors, the save chips, the unsaved-editor guard, More settings (side panel + pages) and
 * "Back to company setup". A part opens from `&part=` on the draft address, the rail's nested parts (lg and up) or
 * the part tabs (below lg).
 *
 * Every API request is intercepted: this proves the UI and the request shapes, never tenant data. Locally:
 *   npx next dev -H 127.0.0.1 -p 5176      (never :5173 — that is a prebuilt image)
 *   npx playwright test -c e2e/playwright.company-setup-rules.config.ts
 * Screenshots go to docs/evidence/wizard-rules-parts/.
 */

type Locale = 'en' | 'ar';
const T = (locale: Locale) => (key: string, params?: Record<string, string | number>) => translate(locale, key, params);
const EVIDENCE = path.resolve(__dirname, '../../docs/evidence/wizard-rules-parts');

const COMPANY = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
const OTHER_COMPANY = '9d8a6f10-2b3c-4d5e-8f90-a1b2c3d4e5f6';
const DRAFT = '7b0c8a52-1f7e-4a8e-9a39-0d6f2b4b8c11';

function body(): CompanySetupDraftBody {
  return {
    existingCompanyId: COMPANY, startFrom: 'defaults',
    company: { legalNameEn: 'Evostel LLC', legalNameAr: 'شركة إيفوستل', tradeName: 'Evostel', countryCode: 'SA', currency: 'SAR', registrationNumber: '1010999888',
      molEstablishmentNumber: '7-7654321', qiwaEstablishmentId: '7-7654321', emailDomain: 'evostel.com', workEmailPattern: 'first.last', firstPayrollMonth: '2026-11',
      cutoverStatus: 'Planned', baseline: { legalNameEn: 'Evostel LLC', tradeName: 'Evostel', firstPayrollMonth: '2026-11' } },
    branches: [{ existingId: 'br-hq', code: 'HQ', nameEn: 'Head Office', nameAr: 'المقر الرئيسي', city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: true, locations: [],
      baseline: { nameEn: 'Head Office', nameAr: 'المقر الرئيسي', city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: 'true' } }],
    costCentres: [], departments: [], designations: [], grades: [],
    rules: {
      workWeek: { weekendDays: 'Fri,Sat', override: false, baseline: { weekendDays: 'Fri,Sat', override: 'false' } }, holidayCalendar: { usePack: true }, leavePolicies: { usePack: true },
      employeeNumbering: { prefix: 'EVO', useCountryPrefix: false, useBranchPrefix: false, useDepartmentPrefix: false, useYear: false, paddingLength: 4, nextSequence: 42,
        baseline: { prefix: 'EVO', nextSequence: '42' } },
    },
    companyAdmin: { mode: 'groupOnly' },
  };
}

const PLAN: CompanySetupPlan = {
  ok: true, problems: [], warnings: [],
  items: [
    { area: 'gccSetting', code: 'SA', label: 'End-of-service (Saudi Labour Law)', labelAr: 'مكافأة نهاية الخدمة (نظام العمل السعودي)', action: 'unchanged' },
    { area: 'holidayCalendar', code: 'SA-2026', label: 'Saudi public holidays 2026', action: 'reuse' },
    { area: 'leavePolicy', code: 'ANNUAL', label: 'Annual leave (21 days)', action: 'create' },
  ],
};

interface Server { policyPosts: unknown[]; draftPuts: number; writes: string[] }

async function boot(page: Page, locale: Locale = 'en'): Promise<{ server: Server; pageErrors: string[] }> {
  const server: Server = { policyPosts: [], draftPuts: 0, writes: [] };
  const pageErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'rules-fixture-token');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale });
  let draft: CompanySetupDraft = { id: DRAFT, version: 3, step: 5, status: 'Open', body: body(), createdByUserId: 'u-admin', createdByName: 'Group Owner', createdAtUtc: '2026-10-23T08:00:00Z', updatedAtUtc: '2026-10-23T08:00:00Z' };
  const policies: Record<string, unknown>[] = [
    { id: 'pol-annual', name: 'Saudi annual leave', leaveTypeId: 'annual-type', countryCode: 'KSA', annualEntitlementDays: 21, accrualMethod: 'Monthly', status: 'Active', payrollImpact: 'Full' },
  ];
  await page.route('**/api/**', async route => {
    const request = route.request();
    const method = request.method();
    const p = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (method !== 'GET') server.writes.push(`${method} ${p}`);
    if (p === '/api/auth/me') return reply({
      id: 'u-admin', tenantId: 'rules-tenant', tenantSlug: 'rules-fixture', fullName: 'Group Owner', email: 'owner@rules.test', roles: ['Admin'],
      permissions: ['organization.read', 'organization.write', 'organization.setup.apply', 'dashboard.read', 'leave.read', 'leave.write', 'attendance.read', 'payroll.rates.read', 'payroll.read', 'benefits.read'],
      accountType: 'Group', isGroupScope: true,
      companies: [{ id: COMPANY, name: 'Evostel LLC', code: 'EVO', countryCode: 'SA', isActive: true }, { id: OTHER_COMPANY, name: 'Sister Trading', code: 'SIS', countryCode: 'SA', isActive: true }],
    });
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/company-setup/context') return reply({
      canCreate: true, creationMode: 'GroupSelfServiceWithinLimit', accountType: 'Group', companiesUsed: 2, companiesAllowed: 5, isGroupScope: true,
      sisterCompanies: [], canGrantCompanyAdmin: true,
      manageableCompanies: [{ id: COMPANY, legalNameEn: 'Evostel LLC', legalNameAr: 'شركة إيفوستل', countryCode: 'SA', readinessDone: 5, readinessTotal: 9, openDraftId: DRAFT }],
    });
    if (p === '/api/company-setup/drafts' && method === 'GET') return reply([]);
    if (p === '/api/company-setup/defaults') return reply(body());
    if (p === `/api/company-setup/drafts/${DRAFT}`) {
      if (method === 'PUT') {
        const put = request.postDataJSON() as { version: number; step: number; body: CompanySetupDraftBody };
        server.draftPuts++;
        draft = { ...draft, version: draft.version + 1, step: put.step, body: put.body };
      }
      return reply(draft);
    }
    if (p === '/api/company-setup/dry-run') return reply(PLAN);
    if (p === '/api/access/ceiling') return reply({ userId: 'u-admin', isAdmin: true, heldPermissions: [], roles: [{ roleId: 'r-admin', name: 'Admin', canAssign: true }] });
    // Leave editors (views/LeavePage) — live saves.
    if (p === '/api/leave/types') return reply([{ id: 'annual-type', code: 'ANNUAL', nameEn: 'Annual leave', category: 'Annual', isPaid: true, isHalfDayAllowed: true, isHourlyAllowed: false, requiresAttachment: false, requiresReason: false, maxConsecutiveDays: 0, colorCode: '#2F6BFF', sortOrder: 0, isActive: true }]);
    if (p === '/api/leave/policies' && method === 'POST') {
      const posted = request.postDataJSON() as Record<string, unknown>;
      server.policyPosts.push(posted);
      const created = { ...posted, id: `pol-${policies.length + 1}` };
      policies.push(created);
      return reply(created, 201);
    }
    if (p === '/api/leave/policies') return reply(paged(policies.filter(item => !new URL(request.url()).searchParams.get('status') || item.status === new URL(request.url()).searchParams.get('status'))));
    if (p === '/api/leave/holidays/calendars') return reply([
      { id: 'cal-sa', name: 'Saudi public holidays 2026', countryCode: 'KSA', companyId: null, branchId: null, calendarYear: 2026, isActive: true, createdAtUtc: '2026-01-01T00:00:00Z' },
      { id: 'cal-evo', name: 'Evostel site holidays 2026', countryCode: 'KSA', companyId: COMPANY, branchId: null, calendarYear: 2026, isActive: true, createdAtUtc: '2026-01-01T00:00:00Z' },
      { id: 'cal-sister', name: 'Sister Trading holidays 2026', countryCode: 'KSA', companyId: OTHER_COMPANY, branchId: null, calendarYear: 2026, isActive: true, createdAtUtc: '2026-01-01T00:00:00Z' },
    ]);
    if (p === '/api/leave/holidays/blackouts') return reply([]);
    if (p.startsWith('/api/leave/holidays/calendars/') && p.endsWith('/holidays')) return reply([]);
    // Country rules (SetupPage GCCSettingsTab): rows were saved as ISO-3 and ISO-2 over time.
    if (p === '/api/admin/gcc-settings') return reply([
      { id: 'gcc-sa', countryCode: 'SAU', wpsEnabled: true, wpsAgentId: '7-7654321', wpsMolCode: '', sifEnabled: false, eosbEnabled: true, eosbYears1To5Rate: 0.5, eosbYearsAbove5Rate: 1, eosbMinYears: 2, workWeek: 'Sun-Thu', weekendDays: 'Fri,Sat', visaTrackingEnabled: true, visaAlertDays: 30, iqamaRequired: true, iqamaAlertDays: 30, emiratesIdRequired: false, ramadanHoursEnabled: true, ramadanReducedHoursPerDay: 2 },
      { id: 'gcc-ae', countryCode: 'ARE', wpsEnabled: true, wpsAgentId: '', wpsMolCode: '', sifEnabled: true, eosbEnabled: true, eosbYears1To5Rate: 0.7, eosbYearsAbove5Rate: 1, eosbMinYears: 1, workWeek: 'Mon-Fri', weekendDays: 'Sat,Sun', visaTrackingEnabled: true, visaAlertDays: 30, iqamaRequired: false, iqamaAlertDays: 30, emiratesIdRequired: true, ramadanHoursEnabled: true, ramadanReducedHoursPerDay: 2 },
    ]);
    // Statutory rules side panel.
    if (p === '/api/statutory-rules' || p === '/api/country-packs/available' || p === '/api/finance/rates/statutory/pending') return reply([]);
    // The Shifts page, for the way back.
    if (p === '/api/shifts/roster') return reply({ assignments: [], employees: [], definitions: [] });
    if (p === '/api/shifts/policy') return reply({});
    if (p.startsWith('/api/shifts')) return reply([]);
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/companies') return reply(paged([]));
    return reply(paged([]));
  });
  return { server, pageErrors };
}

type RulesPart = Extract<PartId, 'workRules' | 'leave' | 'endOfService' | 'moreSettings'>;

const partUrl = (part: RulesPart) => `/setup/company/${COMPANY}/complete?draft=${DRAFT}&part=${part}`;

/** The part on screen: the shell's h2 names it, and RulesStep renders only that part. */
async function expectPart(page: Page, t: ReturnType<typeof T>, part: RulesPart) {
  await expect(page.getByRole('heading', { level: 2, name: t(PART_COPY[part].title), exact: true })).toBeVisible();
  await expect(page.getByTestId('rules-step')).toHaveAttribute('data-part', part);
  await expect(page).toHaveURL(new RegExp(`[?&]part=${part}(&|$)`));
}

async function openPart(page: Page, t: ReturnType<typeof T>, part: RulesPart) {
  await page.goto(partUrl(part));
  await expectPart(page, t, part);
}

/** How a reader moves to another part: the rail's nested part (lg and up) or the part tab (below lg). */
function partControl(page: Page, part: RulesPart) {
  return page.viewportSize()!.width >= 1024
    ? page.getByTestId('wizard-rail-parts').locator(`button[data-part="${part}"]`)
    : page.getByTestId(`part-tab-${part}`);
}

async function goToPart(page: Page, t: ReturnType<typeof T>, part: RulesPart) {
  await partControl(page, part).click();
  await expectPart(page, t, part);
}

async function contained(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), 'page must not scroll sideways').toBe(true);
}

async function shot(page: Page, info: TestInfo, name: string, fullPage = true) {
  fs.mkdirSync(EVIDENCE, { recursive: true });
  await page.screenshot({ path: path.join(EVIDENCE, `${name}-${info.project.name}.png`), fullPage, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
}

for (const locale of ['en', 'ar'] as const) {
  test(`each step-5 part says how it saves and hosts the live editors (${locale})`, async ({ page }, info) => {
    const t = T(locale);
    const { pageErrors } = await boot(page, locale);
    await openPart(page, t, 'workRules');
    await expect(page.locator('html')).toHaveAttribute('dir', locale === 'ar' ? 'rtl' : 'ltr');
    await expect(page.getByTestId('rules-part-leave')).toHaveCount(0);
    await expect(page.getByTestId('save-mode-update')).toHaveText(t('Saved when you press Save all changes'));
    await expect(page.getByTestId('numbering-preview')).toContainText('EVO-0042');
    await contained(page);
    await shot(page, info, `${locale}-part-workRules`);

    await goToPart(page, t, 'leave');
    const leave = page.getByTestId('rules-part-leave');
    await expect(leave.getByTestId('save-mode-live').first()).toHaveText(t('Saves as you edit'));
    await expect(leave.getByTestId('pack-leave').locator('[data-field="rules.leavePolicies.usePack"]')).toBeChecked();
    await expect(leave.getByTestId('pack-leave')).toContainText(t('Statutory leave policies'));
    // The real Leave policies editor, embedded: its saved rows, no page title of its own.
    await expect(leave.getByText('Saudi annual leave')).toBeVisible();
    await expect(page.getByRole('heading', { level: 1, name: 'Leave' })).toHaveCount(0);
    // Holiday calendars: this company's own and the workspace-wide ones, never another company's.
    const lists = leave.getByTestId('leave-editors');
    await lists.getByTestId('leave-tab-holidays').click();
    await expect(lists.getByText('Saudi public holidays 2026', { exact: true })).toBeVisible();
    await expect(lists.getByText('Evostel site holidays 2026')).toBeVisible();
    await expect(lists.getByText('Sister Trading holidays 2026')).toHaveCount(0);
    await lists.getByTestId('leave-tab-policies').click();
    await contained(page);
    await shot(page, info, `${locale}-part-leave`);

    // Country rules, focused on Saudi Arabia (a row saved as SAU counts; the UAE row does not show).
    await goToPart(page, t, 'endOfService');
    const eos = page.getByTestId('rules-part-endOfService');
    await expect(eos.getByTestId('save-mode-live').first()).toHaveText(t('Saves as you edit'));
    await expect(eos.getByText(locale === 'ar' ? 'مكافأة نهاية الخدمة (نظام العمل السعودي)' : 'End-of-service (Saudi Labour Law)')).toBeVisible();
    await expect(eos.getByTestId('country-rules-editor').getByText('SAU', { exact: true })).toBeVisible();
    await expect(eos.getByTestId('country-rules-editor').getByText('ARE', { exact: true })).toHaveCount(0);
    await contained(page);
    await shot(page, info, `${locale}-part-endOfService`);

    await goToPart(page, t, 'moreSettings');
    await expect(page.getByTestId('save-mode-elsewhere')).toHaveText(t('Each setting saves on its own screen'));
    await expect(page.getByTestId('more-settings')).toBeVisible();
    await contained(page);
    await shot(page, info, `${locale}-part-moreSettings`);
    expect(pageErrors).toEqual([]);
  });
}

test('adding a leave policy in the embedded editor saves live and says so in the part header', async ({ page }, info) => {
  const t = T('en');
  const { server } = await boot(page);
  await openPart(page, t, 'leave');
  const leave = page.getByTestId('rules-part-leave');
  await expect(leave.getByText('Saudi annual leave')).toBeVisible();
  await leave.getByRole('button', { name: 'New Policy' }).click();
  // An open, unsaved form is known to the step (the wizard asks before leaving).
  await expect(page.getByTestId('rules-step')).toHaveAttribute('data-editor-dirty', 'true');
  const dialog = page.getByRole('dialog', { name: 'New Leave Policy' });
  await dialog.getByLabel('Policy Name *').fill('Evostel extra annual days');
  await dialog.getByLabel('Leave Type *').selectOption('annual-type');
  await dialog.getByLabel('Status').selectOption('Active');
  await dialog.getByRole('button', { name: 'Create Policy' }).click();
  await expect(dialog).toHaveCount(0);
  expect(server.policyPosts).toHaveLength(1);
  // The wizard's company owns it: leave types and policies are per company (2026-10-23).
  expect(server.policyPosts[0]).toMatchObject({ name: 'Evostel extra annual days', leaveTypeId: 'annual-type', companyId: COMPANY });
  await expect(leave.getByText('Evostel extra annual days')).toBeVisible();
  await expect(page.getByTestId('rules-step')).toHaveAttribute('data-editor-dirty', 'false');
  // Inline, never a toast: "Saved · 1 added", then "Saved".
  const chip = leave.getByTestId('save-mode-live').first();
  await expect(chip).toHaveText(t('Saved · {summary}', { summary: t('{count} added', { count: 1 }) }));
  await shot(page, info, 'en-leave-policy-saved');
  await expect(chip).toHaveText(t('Saved'), { timeout: 6000 });
  // The leave policy is not part of the draft: no commit, no section save.
  expect(server.writes.filter(w => w.includes('/company-setup/') && !w.includes('/drafts/') && !w.includes('dry-run'))).toEqual([]);
});

test('an open editor form blocks a move to another part until the reader discards it', async ({ page }) => {
  const t = T('en');
  const { server } = await boot(page);
  await openPart(page, t, 'leave');
  const leave = page.getByTestId('rules-part-leave');
  await leave.getByRole('button', { name: 'New Policy' }).click();
  const form = page.getByRole('dialog', { name: 'New Leave Policy' });
  await form.getByLabel('Policy Name *').fill('Half-typed policy');
  await expect(page.getByTestId('rules-step')).toHaveAttribute('data-editor-dirty', 'true');
  // The rail (or part tab) is behind the editor's dialog; fire its click as a reader's would land.
  const target = partControl(page, 'endOfService');
  await target.dispatchEvent('click');
  const ask = page.getByRole('dialog', { name: t('Save your changes to {editor}?', { editor: t('Leave policies') }) });
  await expect(ask).toBeVisible();
  // Keep editing: nothing moves and the typed value is still there.
  await ask.getByRole('button', { name: t('Keep editing') }).click();
  await expect(ask).toHaveCount(0);
  await expectPart(page, t, 'leave');
  await expect(form.getByLabel('Policy Name *')).toHaveValue('Half-typed policy');
  // Discard changes: the move goes ahead and nothing was saved.
  await target.dispatchEvent('click');
  await expect(ask).toBeVisible();
  await ask.getByRole('button', { name: t('Discard changes') }).click();
  await expectPart(page, t, 'endOfService');
  await expect(page.getByRole('dialog', { name: 'New Leave Policy' })).toHaveCount(0);
  expect(server.policyPosts).toEqual([]);
});

test('More settings: a side panel opens in place, Esc closes it and focus returns to the opener', async ({ page }, info) => {
  const t = T('en');
  await boot(page);
  await openPart(page, t, 'moreSettings');
  const more = page.getByTestId('more-settings');
  await expect(more.getByTestId('more-settings-shifts')).toHaveAttribute('data-kind', 'page');
  await expect(more.getByTestId('more-settings-statutoryRules')).toHaveAttribute('data-kind', 'panel');
  const opener = more.getByTestId('more-settings-statutoryRules');
  await opener.click();
  const drawer = page.getByTestId('settings-drawer');
  await expect(drawer).toBeVisible();
  await expect(drawer).toHaveAttribute('data-state', 'open');
  await expect(drawer.getByRole('heading', { level: 2, name: t('Statutory rules') })).toBeVisible();
  await expect(page.getByTestId('settings-drawer-close')).toBeFocused();
  await expect(drawer.getByTestId('editor-skeleton')).toHaveCount(0);
  // Tab stays inside the panel.
  for (let i = 0; i < 6; i++) await page.keyboard.press('Tab');
  expect(await drawer.evaluate(el => el.contains(document.activeElement))).toBe(true);
  await contained(page);
  await shot(page, info, 'en-more-settings-drawer', false);
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('settings-drawer-layer')).toHaveCount(0);
  await expect(opener).toBeFocused();
  // "Leave policies, holidays and leave types" opens the Leave & holidays part.
  await expect(more.getByTestId('more-settings-leave')).toHaveAttribute('data-kind', 'part');
  await more.getByTestId('more-settings-leave').click();
  await expectPart(page, t, 'leave');
});

test('a page link leaves with the way back; Back to company setup returns to the More settings part', async ({ page }, info) => {
  const t = T('en');
  const { pageErrors } = await boot(page);
  await openPart(page, t, 'moreSettings');
  const shifts = page.getByTestId('more-settings-shifts');
  await expect(shifts).toHaveAttribute('href', `/shifts?returnTo=companySetup&company=${COMPANY}&draft=${DRAFT}&part=moreSettings`);
  await shifts.click();
  await expect(page).toHaveURL(/\/shifts\?returnTo=companySetup/);
  const bar = page.getByTestId('company-setup-return');
  await expect(bar, pageErrors.join('\n')).toBeVisible();
  await expect(bar.getByRole('link', { name: t('Back to company setup') })).toHaveAttribute('href', partUrl('moreSettings'));
  // The assistant launcher keeps its corner: the bar sits at the top of the content.
  expect((await bar.boundingBox())!.y).toBeLessThan(200);
  await contained(page);
  await shot(page, info, 'en-shifts-return-bar', false);
  await bar.getByRole('link', { name: t('Back to company setup') }).click();
  await expectPart(page, t, 'moreSettings');
  // Back in the wizard, the remembered way back is cleared.
  expect(await page.evaluate(() => sessionStorage.getItem('kynexone-company-setup-return:v1'))).toBeNull();
});

test('the way back survives a page that drops its query, can be dismissed, and a forged one is ignored', async ({ page }) => {
  await boot(page);
  await page.goto(`/shifts?returnTo=companySetup&company=${COMPANY}&draft=${DRAFT}&part=moreSettings`);
  await expect(page.getByTestId('company-setup-return')).toBeVisible();
  // The same page without the query (as a page that rewrites its own address leaves it): the per-tab copy keeps the bar.
  await page.goto('/shifts');
  await expect(page.getByTestId('company-setup-return')).toBeVisible();
  await page.getByTestId('company-setup-return-dismiss').click();
  await expect(page.getByTestId('company-setup-return')).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  await expect(page.getByTestId('company-setup-return')).toHaveCount(0);
  await page.goto(`/shifts?returnTo=companySetup&company=//evil.test&draft=${DRAFT}&part=moreSettings`);
  await expect(page.getByTestId('company-setup-return')).toHaveCount(0);
  await page.goto(`/shifts?returnTo=companySetup&company=${COMPANY}&draft=${DRAFT}&part=javascript:alert(1)`);
  await expect(page.getByTestId('company-setup-return')).toHaveCount(0);
});

test('Arabic: arrows mirror once, the panel opens from the left, no sideways scroll', async ({ page }, info) => {
  const t = T('ar');
  await boot(page, 'ar');
  await openPart(page, t, 'moreSettings');
  await page.mouse.move(0, 0);
  const arrow = page.getByTestId('more-settings-shifts').locator('svg').last();
  expect(await arrow.evaluate(el => getComputedStyle(el).scale)).toBe('-1 1');
  // No rtl: transform on top of the global mirror (that would flip it back): identity once the hover nudge settles.
  await expect.poll(() => arrow.evaluate(el => getComputedStyle(el).transform)).toMatch(/^(none|matrix\(1, 0, 0, 1, 0, 0\))$/);
  await page.getByTestId('more-settings-statutoryRules').click();
  const drawer = page.getByTestId('settings-drawer');
  await expect(drawer).toHaveAttribute('data-state', 'open');
  // From the inline end, which is the left in Arabic (after its 200 ms entrance).
  await expect.poll(async () => Math.round((await drawer.boundingBox())!.x)).toBe(0);
  await expect(drawer.getByTestId('editor-skeleton')).toHaveCount(0);
  await contained(page);
  await shot(page, info, 'ar-more-settings-drawer', false);
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('settings-drawer-layer')).toHaveCount(0);
  await shot(page, info, 'ar-more-settings');
});
