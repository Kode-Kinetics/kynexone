import fs from 'node:fs';
import type { Page, Route } from '@playwright/test';

/**
 * Shared mocked-API shell for the demo-polish specs: the signed-in Admin, the feature and localization reads every
 * screen makes, and a per-test route table. Nothing is read from or written to a real backend.
 */
export type Locale = 'en' | 'ar';
/** Return a JSON body to answer the request, or undefined to fall through to the defaults. */
export type Handler = (path: string, search: URLSearchParams, method: string, route: Route) => unknown | undefined;

export const PERMS = ['dashboard.read', 'payroll.read', 'payroll.write', 'payroll.approve', 'payroll.lock', 'employees.read', 'employees.write', 'employees.sensitive',
  'leave.read', 'leave.write', 'leave.approve', 'attendance.read', 'attendance.write', 'overtime.read', 'overtime.write', 'overtime.approve', 'recruitment.read', 'recruitment.write',
  'recruitment.approve', 'compliance.read', 'compliance.write', 'gosi.read', 'gosi.write', 'qiwa.read', 'finance.read', 'ess.read', 'ess.write', 'reports.read'];

export const USER = {
  id: 'u1', employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', email: 'hr@example.invalid', fullName: 'Test Account', roles: ['Admin'], accountType: 'Tenant',
  isGroupScope: true, permissions: PERMS,
  companies: [{ id: 'c1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }],
};
export const COMPANY = { id: 'c1', name: 'Acme Arabia', legalNameEn: 'Acme Arabia LLC', tradeName: 'Acme Arabia', defaultCurrency: 'SAR', countryCode: 'SA', isActive: true, wpsEmployerId: '1234567890', gosiEmployerId: '55512345', code: 'ACME' };

export async function boot(page: Page, opts: { locale: Locale; url: string; routes?: Handler; waitMs?: number }) {
  const errors: string[] = [];
  const requests: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript((locale: string) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
    localStorage.setItem('kynexone-company:reviewer', 'c1');
  }, opts.locale);
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    requests.push(`${request.method()} ${path}`);
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    const custom = opts.routes?.(path, url.searchParams, request.method(), route);
    if (custom !== undefined) return json(custom);
    if (path === '/api/auth/me') return json(USER);
    if (path === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (path.startsWith('/api/features')) return json([]);
    if (path === '/api/tenant-admin/localization') return json({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: opts.locale, defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });
    if (path === '/api/notifications') return json([]);
    if (path === '/api/companies' || path === '/api/companies/all') return json({ items: [COMPANY], total: 1, page: 1, pageSize: 100 });
    if (request.method() !== 'GET') return json({ id: 'saved' });
    return json({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(opts.url, { waitUntil: 'domcontentloaded' });
  if (opts.waitMs) await page.waitForTimeout(opts.waitMs);
  return { errors, requests };
}

/** Saves a screenshot when DEMO_POLISH_SHOTS is set (the fixed/ folder of the walkthrough notes). */
export async function shot(page: Page, name: string, locale: Locale) {
  const dir = process.env.DEMO_POLISH_SHOTS;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: `${dir}/${name}-${locale}.png`, fullPage: true });
}
