import { expect, type Locator, type Page, type Request } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/**
 * Shared mocked API for the Setup → Company structure specs (Companies, Branches, Locations, Departments, Designations, Grades).
 * Every /api request is intercepted: this proves the UI contract (states, wiring, wording, EN and AR), not server
 * authorisation. The mock auth can represent any role set and permission set, so a read-only user is a real session.
 */

export const companySa = { id: 'co-sa', legalNameEn: 'Masar Trading KSA', legalNameAr: 'مسار للتجارة', tradeName: 'Masar KSA', countryCode: 'SA', jurisdiction: '', registrationNumber: 'CR-1', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
export const companyAe = { id: 'co-ae', legalNameEn: 'Masar Services UAE', legalNameAr: '', tradeName: 'Masar UAE', countryCode: 'AE', jurisdiction: '', registrationNumber: 'CR-2', defaultCurrency: 'AED', isActive: true, approvalStatus: 'Active' };

export type Entity = 'companies' | 'branches' | 'locations' | 'departments' | 'designations' | 'grades';

export interface FixtureOptions {
  locale?: 'en' | 'ar';
  roles?: string[];
  permissions?: string[];
  empty?: boolean;
  /** Entities whose list request answers 500 until `ctl.down` is cleared. */
  down?: Entity[];
  /** A grade's pay-scale lines answer 500 until `ctl.payScaleDown` is cleared. */
  payScaleDown?: boolean;
  /** Designation-driven access: what GET /api/designations/access-options answers. Absent = the feature is off. */
  access?: { enabled: boolean; canConfigure: boolean };
}

export interface Recorded { method: string; path: string; body: any }

export const MANAGER_PERMISSIONS = ['organization.read', 'organization.write', 'organization.delete', 'dashboard.read'];

const seed = {
  branches: () => [
    { id: 'br-ry', companyId: 'co-sa', companyName: 'Masar Trading KSA', code: 'RYD-01', nameEn: 'Riyadh HQ', nameAr: 'مقر الرياض', countryCode: 'SA', city: 'Riyadh', addressLine1: '', addressLine2: '', timeZoneId: 'Asia/Riyadh', laborOfficeCode: '', isHeadOffice: true, isActive: true },
    { id: 'br-off', companyId: 'co-sa', companyName: 'Masar Trading KSA', code: 'JED-01', nameEn: 'Jeddah Office', nameAr: '', countryCode: 'SA', city: 'Jeddah', addressLine1: '', addressLine2: '', timeZoneId: 'Asia/Riyadh', laborOfficeCode: '', isHeadOffice: false, isActive: false },
  ],
  locations: () => [
    { id: 'lo-1', code: 'RYD-HQ', nameEn: 'Riyadh Tower', nameAr: '', addressLine1: '', city: 'Riyadh', countryCode: 'SA', postalCode: '', latitude: 24.7, longitude: 46.7, geofenceRadiusMeters: 150, isActive: true },
    { id: 'lo-2', code: 'OLD-1', nameEn: 'Old Depot', nameAr: '', addressLine1: '', city: 'Dammam', countryCode: 'SA', postalCode: '', isActive: false },
  ],
  departments: () => [
    { id: 'de-fin', companyId: 'co-sa', code: 'FIN', nameEn: 'Finance', nameAr: 'المالية', isActive: true },
    { id: 'de-old', companyId: 'co-sa', code: 'OLD', nameEn: 'Legacy Unit', nameAr: '', isActive: false },
  ],
  designations: () => [
    { id: 'dg-swe', companyId: 'co-sa', code: 'SWE', titleEn: 'Software Engineer', titleAr: 'مهندس برمجيات', jobGrade: 'L3', gradeId: 'gr-5', jobLevel: 'Officer', jobDescription: '', isManagerRole: false, isActive: true, accessRoles: ['Payroll Officer'] },
    { id: 'dg-old', companyId: 'co-sa', code: 'OLD', titleEn: 'Retired Title', titleAr: '', jobGrade: '', isManagerRole: false, isActive: false },
  ],
  grades: () => [
    { id: 'gr-5', companyId: 'co-sa', code: 'G5', name: 'Professional Grade 5', nameAr: 'الدرجة المهنية 5', band: 'Professional', level: 5, minSalary: 5000, midSalary: 7000, maxSalary: 9000, currency: 'SAR', isActive: true },
    { id: 'gr-old', companyId: 'co-sa', code: 'G0', name: 'Retired Grade', nameAr: '', band: '', level: 0, minSalary: 0, midSalary: 0, maxSalary: 0, currency: 'SAR', isActive: false },
  ],
};

export async function boot(page: Page, tab: Entity, options: FixtureOptions = {}) {
  const writes: Recorded[] = [];
  const errors: string[] = [];
  const empty = !!options.empty;
  const rows = {
    companies: empty ? [] : [{ ...companySa }, { ...companyAe }],
    branches: empty ? [] : seed.branches(),
    locations: empty ? [] : seed.locations(),
    departments: empty ? [] : seed.departments(),
    designations: empty ? [] : seed.designations(),
    grades: empty ? [] : seed.grades(),
  } as Record<Entity, any[]>;
  const ctl = {
    down: new Set<Entity>(options.down ?? []),
    payScaleDown: !!options.payScaleDown,
    /** The next write answers this once. */
    refuse: null as null | { status: number; body: unknown },
    /** While set, every write waits for it (hold a save in flight). */
    gate: null as null | Promise<void>,
  };
  const roles = options.roles ?? ['HR Manager'];
  const permissions = options.permissions ?? MANAGER_PERMISSIONS;
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'company-structure-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });

  await page.route('**/api/**', async (route) => {
    const request: Request = route.request();
    const u = new URL(request.url());
    const p = u.pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    const body = (() => { try { return request.postDataJSON(); } catch { return request.postData(); } })();
    if (method !== 'GET') { writes.push({ method, path: p, body }); if (ctl.gate) await ctl.gate; }
    const refusal = () => { const r = ctl.refuse; ctl.refuse = null; return r; };

    if (p === '/api/auth/me') {
      return reply({
        id: 'user-1', tenantId: 'cs-tenant', tenantSlug: 'cs-fixture', fullName: 'Huda HR', roles, permissions,
        accountType: 'Group', isGroupScope: true,
        companies: [companySa, companyAe].map((c) => ({ id: c.id, name: c.legalNameEn, code: c.id, countryCode: c.countryCode, isActive: true })),
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/reference/countries') return reply([{ code: 'SA', name: 'Saudi Arabia' }, { code: 'AE', name: 'United Arab Emirates' }, { code: 'KW', name: 'Kuwait' }]);
    if (p === '/api/reference/currencies') return reply([{ code: 'SAR', name: 'Saudi Riyal' }]);
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/designations/access-options') {
      const a = options.access ?? { enabled: false, canConfigure: false };
      return reply({
        ...a,
        roles: a.enabled && a.canConfigure ? [{ name: 'Payroll Officer', description: 'Runs payroll' }, { name: 'Manager', description: 'Approves for the team' }] : [],
      });
    }
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);

    // The grade editor's own reads: no pay lines yet, and no pay components to pick from.
    if (/^\/api\/grades\/[^/]+\/pay-scale$/.test(p)) return method === 'GET' ? (ctl.payScaleDown ? reply({ code: 'internal_error', message: 'boom', traceId: 't-2' }, 500) : reply([])) : reply({ count: 0, structure: null });
    if (p === '/api/grades/package-components') return reply([]);

    const collections: Array<[Entity, string, boolean]> = [
      ['companies', '/api/companies', true], ['branches', '/api/branches', true], ['departments', '/api/departments', true], ['locations', '/api/admin/locations', false],
      ['designations', '/api/designations', true], ['grades', '/api/grades', true],
    ];
    for (const [entity, base, isPaged] of collections) {
      if (p === base && method === 'GET') {
        if (ctl.down.has(entity)) return reply({ code: 'internal_error', message: 'boom', traceId: 't-1' }, 500);
        return reply(isPaged ? paged(rows[entity]) : rows[entity]);
      }
      if (p === base && method === 'POST') {
        const r = refusal(); if (r) return reply(r.body, r.status);
        const created = { ...(body as object), id: `${entity}-new-${rows[entity].length}`, isActive: true };
        rows[entity].push(created);
        return reply(created, 201);
      }
      const one = p.match(new RegExp(`^${base.replace(/\//g, '\\/')}\\/([^/]+)$`));
      if (one && method === 'PUT') {
        const r = refusal(); if (r) return reply(r.body, r.status);
        const row = rows[entity].find((x) => x.id === one[1]);
        if (row) Object.assign(row, body);
        return reply(row ?? {});
      }
      if (one && method === 'DELETE') {
        const r = refusal(); if (r) return reply(r.body, r.status);
        const row = rows[entity].find((x) => x.id === one[1]);
        if (row) row.isActive = false;
        return reply({ outcome: 'turnedOff', message: 'Turned off.', usage: { total: 1, summary: '', items: [] } });
      }
      const restore = p.match(new RegExp(`^${base.replace(/\//g, '\\/')}\\/([^/]+)\\/restore$`));
      if (restore && method === 'POST') {
        const r = refusal(); if (r) return reply(r.body, r.status);
        const row = rows[entity].find((x) => x.id === restore[1]);
        if (row) row.isActive = true;
        return reply({ outcome: 'restored', message: 'Restored.' });
      }
    }
    const status = p.match(/^\/api\/companies\/([^/]+)\/status$/);
    if (status && method === 'PUT') {
      const r = refusal(); if (r) return reply(r.body, r.status);
      const row = rows.companies.find((x) => x.id === status[1]);
      if (row) row.isActive = (body as any).isActive;
      return reply(row ?? {});
    }
    if (/\/(export|import-template)$/.test(p)) return route.fulfill({ status: 200, contentType: 'text/csv', body: 'code,name\n' });
    if (p.startsWith('/api/admin/') || p.includes('country-packs')) return reply([]);
    return reply(paged([]));
  });

  await page.goto(`/setup?tab=${tab}`);
  await expect(page.getByRole('heading', { level: 1, name: options.locale === 'ar' ? 'الإعداد والإدارة' : 'Setup & Administration', exact: true })).toBeVisible({ timeout: 60_000 });
  return { writes, errors, ctl, rows };
}

export const axeSeriousOrWorse = async (page: Page, scope: string) => {
  const result = await new AxeBuilder({ page }).include(scope).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  return result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.nodes.map((n) => `${n.html.slice(0, 100)} ${n.any.map((a) => a.message).join(';')}`).join(' | ')}`);
};

/** Roles/permissions for the three kinds of person the screens must tell apart. */
export const READ_ONLY = { roles: ['HR Officer'], permissions: ['organization.read', 'dashboard.read'] };
export const NO_DELETE = { roles: ['HR Manager'], permissions: ['organization.read', 'organization.write', 'dashboard.read'] };

/** A form control by its accessible name (the label, without the required-star), whatever kind of control it is. */
export const field = (scope: Locator | Page, name: string) =>
  scope.getByRole('textbox', { name, exact: true })
    .or(scope.getByRole('combobox', { name, exact: true }))
    .or(scope.getByRole('spinbutton', { name, exact: true }));
/**
 * Holds organization.write but neither Admin nor HR Manager: Setup opens the tab, and the API refuses every write
 * (Branches, Departments, Locations and Companies controllers are role-gated). The screen must not offer what it refuses.
 */
export const WRITE_PERMISSION_WITHOUT_ROLE = { roles: ['HR Officer'], permissions: ['organization.read', 'organization.write', 'dashboard.read'] };

/**
 * Holds every organization permission but none of the roles the controllers require. Branches, Departments, Designations,
 * Grades and Companies (and the Locations import and export) are role-gated, so every write is refused for this person.
 * Locations Add, Edit, Remove and Restore are gated by permission alone (SetupSettingsController), so they are not.
 */
export const CUSTOM_ROLE_WITH_PERMISSIONS = { roles: ['Org Custodian'], permissions: MANAGER_PERMISSIONS };

/** Labels in `scope` that name nothing: not wrapping a control and not pointing (for=) at an element that exists. The read-only Status is not a control. */
export const orphanLabels = (scope: Locator) => scope.locator('label').evaluateAll((labels) => (labels as HTMLLabelElement[])
  .filter((l) => !l.querySelector('input,select,textarea') && !(l.htmlFor && document.getElementById(l.htmlFor)))
  .map((l) => (l.textContent ?? '').trim()).filter((text) => text !== 'Status'));
