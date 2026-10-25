import { expect, test, type Locator, type Page } from '@playwright/test';
import { openSetupSection, setupSectionEntries } from '../setupSections';

/**
 * Setup is the one home for configuration (src/lib/setupModuleSections.ts). These specs prove, for every screen that
 * moved, against an intercepted API (no tenant data is read or written):
 *  1. its old address leads to its Setup section;
 *  2. Setup lists it under its group, and opening it shows the module's real editor (never the "not moved" stub);
 *  3. the module keeps its daily work but no longer has the tab, and links to its settings in Setup;
 *  4. someone it was never for does not get it.
 */

export const ALL_PERMISSIONS = [
  'dashboard.read', 'ess.read', 'organization.read', 'organization.write',
  'leave.read', 'leave.write', 'attendance.read', 'attendance.write', 'attendance.bulk_import', 'overtime.read', 'overtime.write',
  'employees.read', 'employees.write', 'loans.read', 'loans.write', 'payroll.read', 'payroll.write',
  'recruitment.read', 'recruitment.write', 'performance.cycle_manage', 'engagement.read', 'engagement.manage',
  'compliance.read', 'compliance.write', 'qiwa.read', 'security.manage', 'users.manage', 'roles.manage',
];

export interface Persona {
  roles: string[];
  permissions: string[];
}
export const ADMIN: Persona = { roles: ['Admin'], permissions: ALL_PERMISSIONS };
/** An employee: their own leave, attendance and loans, which are also some Setup sections' permissions. */
export const EMPLOYEE: Persona = { roles: ['Employee'], permissions: ['dashboard.read', 'ess.read', 'leave.read', 'attendance.read', 'loans.self'] };

/** A reply for one API path (exact pathname), or a function of the request. */
export type ApiMocks = Record<string, unknown | ((url: URL, method: string) => unknown)>;

const COMPANY = { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };

export async function boot(page: Page, options: { persona?: Persona; url: string; mocks?: ApiMocks; disabledKeys?: string[]; flagsDelayMs?: number }) {
  const persona = options.persona ?? ADMIN;
  const errors: string[] = [];
  const writes: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
  });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    if (request.method() !== 'GET') writes.push(`${request.method()} ${path}`);
    const mock = options.mocks?.[path];
    if (mock !== undefined) return route.fulfill({ json: typeof mock === 'function' ? (mock as (u: URL, m: string) => unknown)(url, request.method()) : mock });
    if (path === '/api/auth/me') return route.fulfill({ json: {
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: persona.roles, permissions: persona.permissions,
      accountType: 'Single', isGroupScope: true,
      companies: [{ id: COMPANY.id, name: COMPANY.legalNameEn, code: COMPANY.id, countryCode: 'SA', isActive: true }],
    } });
    if (path === '/api/auth/mfa/status') return route.fulfill({ json: { promptToEnroll: false } });
    if (path === '/api/features/disabled-keys') {
      if (options.flagsDelayMs) await new Promise(resolve => setTimeout(resolve, options.flagsDelayMs));
      return route.fulfill({ json: options.disabledKeys ?? [] });
    }
    if (path === '/api/companies') return route.fulfill({ json: { items: [COMPANY], total: 1, page: 1, pageSize: 100 } });
    if (path === '/api/tenant-admin/localization') return route.fulfill({ json: { countryCode: 'SA', currencyCode: 'SAR' } });
    if (path === '/api/dashboard/full') return route.fulfill({ status: 503, json: { message: 'Dashboard fixture unavailable' } });
    // Most lists are plain arrays; a paged list reads `items` from its own mock.
    return route.fulfill({ json: [] });
  });
  await page.goto(options.url);
  return { errors, writes };
}

export interface MovedSection {
  /** The Setup tab id (`/setup?tab=…`). */
  id: string;
  /** Its name in Setup's navigation and header. */
  label: string;
  /** The navigation group it is listed under. */
  group: string;
  /** The old address, which must now lead here. */
  old: string;
  /** Something only the module's real editor shows once loaded. */
  ready: (page: Page) => Locator;
  mocks?: ApiMocks;
  /** For a module that keeps its daily work: its page, the tab that must be gone, and its settings door. */
  module?: { path: string; goneTab: string; door: string };
}

const heading = (page: Page) => page.locator('#setup-section-heading');

export function movedSectionSpecs(sections: readonly MovedSection[]) {
  for (const section of sections) {
    test.describe(section.label, () => {
      test(`${section.old} leads to Setup → ${section.label}`, async ({ page }) => {
        const { errors } = await boot(page, { url: section.old, mocks: section.mocks });
        await expect(page).toHaveURL(new RegExp(`/setup\\?tab=${section.id}(&|$)`));
        await expect(heading(page)).toHaveText(section.label, { timeout: 30_000 });
        expect(errors, 'No application runtime errors').toEqual([]);
      });

      test(`Setup lists ${section.label} under ${section.group} and opens the module's own editor`, async ({ page }) => {
        const { errors, writes } = await boot(page, { url: '/setup', mocks: section.mocks });
        await expect(page.locator('nav.setup-page-nav')).toBeAttached({ timeout: 30_000 });
        const list = setupSectionEntries(page).getByRole('list', { name: section.group, exact: true });
        await expect(list.getByRole('link', { name: section.label, exact: true })).toBeAttached();
        await openSetupSection(page, section.label);
        await expect(page).toHaveURL(new RegExp(`tab=${section.id}(&|$)`));
        await expect(heading(page)).toHaveText(section.label);
        const panel = page.locator(`[data-module-section="${section.id}"]`);
        await expect(panel).toBeVisible();
        await expect(panel.getByTestId('module-section-pending')).toHaveCount(0);
        await expect(section.ready(page)).toBeVisible({ timeout: 15_000 });
        expect(errors, 'No application runtime errors').toEqual([]);
        expect(writes, 'Opening a section writes nothing').toEqual([]);
      });

      if (section.module) {
        const module = section.module;
        test(`${module.path} keeps its daily work, drops the ${module.goneTab} tab and links to its settings`, async ({ page }) => {
          const { errors } = await boot(page, { url: module.path, mocks: section.mocks });
          const door = page.getByRole('link', { name: module.door, exact: true });
          await expect(door).toBeVisible({ timeout: 30_000 });
          await expect(page.getByRole('tab', { name: module.goneTab, exact: true })).toHaveCount(0);
          await expect(page.getByRole('button', { name: module.goneTab, exact: true })).toHaveCount(0);
          await door.click();
          await expect(page).toHaveURL(/\/setup\?tab=/);
          await expect(heading(page)).toBeVisible();
          expect(errors, 'No application runtime errors').toEqual([]);
        });
      }

      test(`an employee is not given ${section.label}`, async ({ page }) => {
        await boot(page, { persona: EMPLOYEE, url: `/setup?tab=${section.id}`, mocks: section.mocks });
        await expect(page.getByTestId('setup-no-sections').or(page.getByText('Access Denied'))).toBeVisible({ timeout: 30_000 });
        await expect(page.locator(`[data-module-section="${section.id}"]`)).toHaveCount(0);
      });
    });
  }
}
