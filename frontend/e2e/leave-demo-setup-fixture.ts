import type { Page } from '@playwright/test';

/** The mocked API shared by the leave-demo-setup specs (balances, apply attachment, leave-type Arabic). Browser contracts only. */

export const SHOTS = process.env.LEAVE_SHOTS;
export const YEAR = new Date().getFullYear();

const types = [
  { id: 'lt-annual', code: 'ANNUAL', nameEn: 'Annual Leave', category: 'Annual', isActive: true, isPaid: true, isHalfDayAllowed: true, requiresAttachment: false, requiresReason: false },
  { id: 'lt-unpaid', code: 'UP', nameEn: 'Unpaid Leave', nameAr: 'إجازة بدون راتب', category: 'Unpaid', isActive: true, isPaid: false, isHalfDayAllowed: false, requiresAttachment: true, requiresReason: false },
];
const balanceRows = [
  { id: 'b1', employeeId: 9, employeeName: 'Sara Nasser', leaveTypeId: 'lt-annual', leaveTypeName: 'Annual Leave', year: YEAR, entitled: 0, accrued: 22, used: 0, pending: 0, carriedForward: 0, encashed: 0, expired: 0, manualAdjustment: 0, granted: 22, available: 22, negativeAllowed: false },
];

export interface Calls { openYear: Array<Record<string, unknown>>; essUpload: number; hrUpload: string[]; create: Array<Record<string, unknown>>; transliterate: Array<Record<string, unknown>> }
export interface Options { roles?: string[]; permissions?: string[]; locale?: 'en' | 'ar'; openYearResult?: { balancesCreated: number } }

export async function boot(page: Page, options: Options = {}) {
  const calls: Calls = { openYear: [], essUpload: 0, hrUpload: [], create: [], transliterate: [] };
  let opened = false;
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript((locale: string) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, options.locale ?? 'en');
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ json, status });
    if (path === '/api/auth/me') {
      return reply({
        id: 'hr', employeeId: 9, tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Layla Haddad', roles: options.roles ?? ['HR Manager'],
        permissions: options.permissions ?? ['leave.read', 'leave.write', 'leave.policy_manage', 'employees.read', 'employees.documents'],
        companies: [{ id: 'company-1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SAU', isActive: true }],
      });
    }
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return reply([]);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/leave/balances/open-year' && request.method() === 'POST') {
      calls.openYear.push(request.postDataJSON());
      opened = (options.openYearResult?.balancesCreated ?? 1) > 0;
      return reply({ year: YEAR, balancesCreated: options.openYearResult?.balancesCreated ?? 1, accrualEntries: 11, allocationEntries: 0 });
    }
    if (path === '/api/leave/balances') return reply({ items: opened ? balanceRows : [], total: opened ? balanceRows.length : 0, page: 1, pageSize: 50 });
    if (path === '/api/ess/documents' && request.method() === 'POST') { calls.essUpload += 1; return reply({ id: 'doc-own' }, 201); }
    if (/^\/api\/employees\/\d+\/documents$/.test(path) && request.method() === 'POST') { calls.hrUpload.push(path); return reply({ id: 'doc-hr' }, 201); }
    if (path === '/api/leave/requests' && request.method() === 'POST') { calls.create.push(request.postDataJSON()); return reply({ id: 'lr-1' }, 201); }
    if (path === '/api/leave/requests') return reply({ items: [], total: 0, page: 1 });
    if (path === '/api/leave/types') return reply(types);
    if (path === '/api/employees') return reply({ items: [{ id: 21, fullName: 'Omar Saleh', department: 'Operations', designation: 'Technician', employeeCode: 'E-21', status: 'Active' }], total: 1, page: 1, pageSize: 8 });
    if (path === '/api/localization/transliterate' && request.method() === 'POST') { calls.transliterate.push(request.postDataJSON()); return reply({ suggestion: 'إجازة الحج', requiresManualEntry: false }); }
    if (path === '/api/notifications') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 50 });
  });
  return { calls, errors };
}

export async function openLeaveTab(page: Page, name: string) {
  await page.goto('/leave');
  await page.getByRole('tab', { name }).click({ timeout: 90_000 });
}

