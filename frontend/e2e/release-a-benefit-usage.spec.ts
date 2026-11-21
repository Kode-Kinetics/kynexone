import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { measureText } from '../src/lib/benefitUsage';

/**
 * Benefit utilisation, which works like a loan request, in a real browser against route mocks shaped like the backend DTOs
 * (BenefitUtilisationDto, BenefitUsageRequestDto, EssBenefitUsageDto): HR sees entitlement, used and left per employee and per child
 * or traveller and requests a use with evidence; Finance approves or rejects with a reason; the employee sees "My benefits — used
 * and left" in My package and requests their own. English and Arabic, desktop and phone. Fixture lane (playwright.fixture.config.ts):
 * it cannot prove the server returns these payloads; BenefitUsageTests, BenefitUsageEducationTests and BenefitUsagePostgresTests do.
 *
 * Screenshots go to BENEFIT_USAGE_SHOTS when it is set.
 */

type Locale = 'en' | 'ar';
const say = (locale: Locale, key: string, values: Record<string, string | number> = {}) =>
  Object.entries(values).reduce((text, [k, v]) => text.split(`{${k}}`).join(String(v)), (LOCALE_DICTS[locale] as Record<string, string>)[key] ?? key);
const SHOTS = process.env.BENEFIT_USAGE_SHOTS;
const shot = async (page: Page, name: string) => {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: true });
};

const ticketRow = {
  employeeId: 1, employeeName: 'Mohammed Abdelrahman', employeeCode: 'E-1', companyId: 'c1', benefitCode: 'AIR_TICKET',
  labelEn: 'Annual air ticket', labelAr: 'تذكرة السفر السنوية', verificationState: 'Verified', measure: 'Units', currency: null,
  entitlement: 3, pending: 0, approved: 1, remaining: 2, periodStart: '2026-02-01', periodEnd: '2027-01-31', dependantScope: 'Family',
  maxDependants: 2, perBeneficiary: 1,
  beneficiaries: [
    { dependantId: null, name: 'Mohammed Abdelrahman', cap: 1, used: 1, remaining: 0, named: true },
    { dependantId: 'd-wife', name: 'Amina', cap: 1, used: 0, remaining: 1, named: false },
    { dependantId: 'd-son', name: 'Omar', cap: 1, used: 0, remaining: 1, named: false },
  ],
  canRequest: true,
};
const educationRow = {
  ...ticketRow, benefitCode: 'EDUCATION', labelEn: "Children's education allowance", labelAr: 'بدل تعليم الأبناء', measure: 'Amount', currency: 'SAR',
  entitlement: 40000, pending: 12500, approved: 0, remaining: 27500, dependantScope: 'Children', perBeneficiary: 20000,
  beneficiaries: [
    { dependantId: 'd-son', name: 'Omar', cap: 20000, used: 12500, remaining: 7500, named: true },
    { dependantId: 'd-daughter', name: 'Lina', cap: 20000, used: 0, remaining: 20000, named: false },
  ],
};
const unverifiedRow = {
  ...ticketRow, employeeId: 2, employeeName: 'Ramon Dela Cruz', employeeCode: 'E-2', verificationState: 'Unverified', entitlement: 1, approved: 0,
  remaining: 1, dependantScope: 'None', maxDependants: null, perBeneficiary: null, beneficiaries: [], canRequest: false,
};
const pendingRequest = {
  id: 'r-1', employeeId: 1, employeeName: 'Mohammed Abdelrahman', companyId: 'c1', benefitCode: 'EDUCATION', labelEn: "Children's education allowance",
  labelAr: 'بدل تعليم الأبناء', status: 'Pending', periodStart: '2026-02-01', periodEnd: '2027-01-31', usageDate: '2026-09-01', units: null,
  amount: 12500, currency: 'SAR', dependantId: 'd-son', dependantName: 'Omar', academicYear: '2026/2027', providerName: 'Al Noor International School',
  evidenceReference: 'INV-2026-118', note: null, requestedByUserId: 'u-emp', requestedByName: 'Mohammed Abdelrahman', createdAtUtc: '2026-09-02T08:00:00Z',
  decidedByName: null, decidedAtUtc: null, decisionReason: null, canDecide: false, canCancel: true,
};
const approvedRequest = {
  ...pendingRequest, id: 'r-2', benefitCode: 'AIR_TICKET', labelEn: 'Annual air ticket', labelAr: 'تذكرة السفر السنوية', status: 'Approved',
  usageDate: '2026-08-10', units: 1, amount: null, currency: null, dependantId: null, dependantName: null, academicYear: null, providerName: 'Saudia',
  evidenceReference: 'BKG-065-1234', requestedByName: 'Hala Al-Qahtani', decidedByName: 'Faisal Finance', decidedAtUtc: '2026-07-21T08:00:00Z', canCancel: false,
};
const essUsage = {
  benefits: [ticketRow, educationRow].map((r) => ({ ...r })),
  requests: [
    { id: 'r-1', benefitCode: 'EDUCATION', labelEn: "Children's education allowance", labelAr: 'بدل تعليم الأبناء', status: 'Pending', usageDate: '2026-09-01',
      units: null, amount: 12500, currency: 'SAR', forName: 'Omar', academicYear: '2026/2027', providerName: 'Al Noor International School',
      decisionReason: null, createdAtUtc: '2026-09-02T08:00:00Z', canCancel: true },
    { id: 'r-2', benefitCode: 'AIR_TICKET', labelEn: 'Annual air ticket', labelAr: 'تذكرة السفر السنوية', status: 'Approved', usageDate: '2026-08-10', units: 1,
      amount: null, currency: null, forName: null, academicYear: null, providerName: 'Saudia', decisionReason: null, createdAtUtc: '2026-07-20T08:00:00Z', canCancel: false },
  ],
};
const essPackage = {
  asOf: '2026-10-06', currency: 'SAR', gradeName: 'Supervisor', gradeNameAr: 'مشرف', companyName: 'Masar Facility Services', companyNameAr: 'مسار للخدمات',
  termStartsOn: '2026-02-01', fixedUntil: '2027-01-31', renewalReviewOpensOn: null, dependantsOnFile: 3,
  lines: [{ componentCode: 'HOUSING', labelEn: 'Housing allowance', labelAr: 'بدل السكن', group: 'pay', eligible: true, offered: true, fixed: false,
    valueType: 'PercentOfBasic', amount: null, rate: 0.25, monthlyCash: 2000, coverageTier: null, quantity: null, dependantScope: 'None', maxDependants: null,
    dependantsCovered: 0, limitPeriod: 'Monthly', reasonCode: null, reasonCriterion: null, eligibleFrom: null, resolvedAmount: 2000,
    why: { basis: 'salary', gradeName: 'Supervisor', gradeNameAr: 'مشرف', companyRule: false, since: '2026-02-01', until: null } }],
};

async function boot(page: Page, locale: Locale, permissions: string[], url: string) {
  const errors: string[] = [];
  const calls: { path: string; body: unknown }[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(([l]) => {
    localStorage.setItem('zayra_access_token', 'fixture-token');
    localStorage.setItem('zayra_refresh_token', 'fixture-refresh');
    localStorage.setItem('kynexone-locale', l);
    localStorage.setItem('kynexone-locale-choice-v2', l);
    localStorage.setItem('kynexone.theme', 'light');
  }, [locale]);
  // A stand-in for a server that still answers canDecide for anyone holding payroll.approve: the screen must not offer the decision on that alone.
  const finance = permissions.includes('benefits.usage.approve') || permissions.includes('payroll.approve');
  const hr = permissions.includes('entitlements.manage');
  await page.route('**/api/**', (route) => {
    const req = route.request();
    const p = new URL(req.url()).pathname;
    const json = (b: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) });
    if (p === '/api/auth/me') return json({
      id: 'u-viewer', tenantId: 't1', tenantSlug: 'masar', email: 'x@masar.test', fullName: 'Viewer', roles: ['HR Director'], permissions,
      accountType: 'SingleCompany', isGroupScope: true, employeeId: 1,
      companies: [{ id: 'c1', name: 'Masar Facility Services', code: 'MFS', countryCode: 'SAU', isActive: true }],
    });
    if (p === '/api/features/disabled-keys' || p === '/api/features/modules' || p === '/api/notifications') return json([]);
    if (p === '/api/tenant-admin/localization') return json({ defaultTimezone: 'Asia/Riyadh', calendarSystem: 'Gregorian', hijriDatesEnabled: false });
    if (p === '/api/entitlements/usage') return json([educationRow, ticketRow, unverifiedRow]);
    // The server decides each viewer's actions: Finance may decide, HR may withdraw.
    if (p === '/api/entitlements/usage/requests' && req.method() === 'GET')
      return json([{ ...pendingRequest, canDecide: finance, canCancel: hr }, approvedRequest]);
    if (p === '/api/ess/package/usage' && req.method() === 'GET') return json(essUsage);
    if (p === '/api/ess/package') return json(essPackage);
    if (req.method() === 'POST' && (p.startsWith('/api/entitlements/usage/requests') || p.startsWith('/api/ess/package/usage'))) {
      calls.push({ path: p, body: req.postDataJSON() });
      return json({ ...pendingRequest, id: 'r-new' });
    }
    return json({ items: [], total: 0 });
  });
  await page.goto(url);
  return { errors, calls };
}

/** The form control a field label names (a required mark or an `i` button beside the label never matches). */
const control = (scope: ReturnType<Page['getByTestId']>, label: string) => scope.getByLabel(label).and(scope.locator('select, input, textarea'));

const HR = ['entitlements.read', 'entitlements.manage'];
const FINANCE = ['benefits.usage.approve'];
// HR Manager holds payroll.approve but not the benefit-usage key: it makes requests, it does not decide them.
const HR_MANAGER = ['entitlements.read', 'entitlements.manage', 'payroll.approve'];
const EMPLOYEE = ['ess.read', 'ess.write'];
const ticketName = (l: Locale) => (l === 'ar' ? 'تذكرة السفر السنوية' : 'Annual air ticket');
const educationName = (l: Locale) => (l === 'ar' ? 'بدل تعليم الأبناء' : "Children's education allowance");

for (const locale of ['en', 'ar'] as const) {
  test.describe(`benefit usage (${locale})`, () => {
    test('HR sees entitlement, used and left per employee and per child or traveller; an unconfirmed row cannot be requested', async ({ page }) => {
      const { errors } = await boot(page, locale, HR, '/benefits/usage');
      await expect(page.getByRole('heading', { name: say(locale, 'Benefit usage'), level: 1 })).toBeVisible();
      const rows = page.getByTestId('usage-row');
      await expect(rows).toHaveCount(3);
      const education = rows.filter({ hasText: educationName(locale) });
      await expect(education).toContainText(say(locale, 'Left to use'));
      await expect(education).toContainText(say(locale, '{amount} waiting for Finance', { amount: measureText(12500, 'Amount', 'SAR', locale) }));
      await expect(education).toContainText('Omar');
      const unverified = rows.filter({ hasText: 'Ramon Dela Cruz' });
      await expect(unverified).toContainText(say(locale, 'Not confirmed against the contract'));
      await expect(unverified.getByRole('button', { name: say(locale, 'Request a benefit for {name}', { name: 'Ramon Dela Cruz' }) })).toHaveCount(0);
      const waiting = page.getByTestId('usage-waiting');
      await expect(waiting.getByRole('button', { name: say(locale, 'Approve') })).toHaveCount(0);
      await expect(waiting).toContainText(say(locale, 'Finance decides this. Whoever requested it, and the employee it is for, cannot.'));
      await shot(page, `hr-usage-${locale}-desktop`);
      await page.setViewportSize({ width: 390, height: 844 });
      await expect(rows.first()).toBeVisible();
      await shot(page, `hr-usage-${locale}-phone`);
      expect(errors).toEqual([]);
    });

    test('HR requests an education claim for a child; the dialog checks it first and sends one request id', async ({ page }) => {
      const { errors, calls } = await boot(page, locale, HR, '/benefits/usage');
      await page.getByTestId('usage-row').filter({ hasText: educationName(locale) }).getByRole('button', { name: say(locale, 'Request a benefit for {name}', { name: 'Mohammed Abdelrahman' }) }).click();
      const dialog = page.getByTestId('usage-request-dialog');
      await control(dialog, say(locale, 'Benefit')).selectOption('EDUCATION');
      await expect(page.getByTestId('usage-request-left')).toBeVisible();
      await page.getByTestId('usage-request-submit').click();
      await expect(dialog.getByText(say(locale, 'Choose the child this claim is for.'))).toBeVisible();
      await expect(dialog.getByText(say(locale, 'Give the academic year the fees are for.'))).toBeVisible();
      expect(calls).toHaveLength(0);
      await control(dialog, say(locale, 'Child')).selectOption('d-son');
      await control(dialog, say(locale, 'Amount ({currency})', { currency: 'SAR' })).fill('7500');
      await control(dialog, say(locale, 'Academic year')).fill('2026/2027');
      await control(dialog, say(locale, 'School')).fill('Al Noor International School');
      await control(dialog, say(locale, 'Evidence reference')).fill('INV-9');
      await shot(page, `hr-request-education-${locale}`);
      await page.getByTestId('usage-request-submit').click();
      await expect(dialog).toHaveCount(0);
      expect(calls).toHaveLength(1);
      expect(calls[0].path).toBe('/api/entitlements/usage/requests');
      expect(calls[0].body).toMatchObject({ benefitCode: 'EDUCATION', employeeId: 1, amount: 7500, units: null, currency: 'SAR', dependantId: 'd-son', academicYear: '2026/2027' });
      expect((calls[0].body as { requestId: string }).requestId).toMatch(/^[0-9a-f-]{36}$/);
      expect(errors).toEqual([]);
    });

    test('a server refusal is shown in words at the dialog', async ({ page }) => {
      const { errors } = await boot(page, locale, HR, '/benefits/usage');
      await page.route('**/api/entitlements/usage/requests', (route) => route.request().method() === 'POST'
        ? route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: 'usage_dependant_cap', message: 'x' }) })
        : route.fallback());
      await page.getByTestId('usage-row').filter({ hasText: ticketName(locale) }).getByRole('button', { name: say(locale, 'Request a benefit for {name}', { name: 'Mohammed Abdelrahman' }) }).click();
      const dialog = page.getByTestId('usage-request-dialog');
      await control(dialog, say(locale, 'Benefit')).selectOption('AIR_TICKET');
      await control(dialog, say(locale, 'Who it is for')).selectOption('d-son');
      await control(dialog, say(locale, 'Number of tickets or items')).fill('1');
      await control(dialog, say(locale, 'Evidence reference')).fill('BKG-1');
      await page.getByTestId('usage-request-submit').click();
      await expect(page.getByTestId('usage-request-error')).toHaveText(say(locale, 'The grade’s limit of dependants for this period is reached. Use a child or traveller already named.'));
      expect(errors).toEqual([]);
    });

    test('Finance approves a waiting request, and rejects only with a reason', async ({ page }) => {
      const { errors, calls } = await boot(page, locale, FINANCE, '/benefits/usage');
      const waiting = page.getByTestId('usage-waiting');
      await expect(waiting.getByTestId('usage-request')).toContainText('INV-2026-118');
      await expect(page.getByRole('button', { name: say(locale, 'Request a benefit for {name}', { name: 'Mohammed Abdelrahman' }) })).toHaveCount(0);
      await shot(page, `finance-queue-${locale}`);
      await waiting.getByRole('button', { name: say(locale, 'Reject') }).click();
      await waiting.getByRole('button', { name: say(locale, 'Reject') }).click();
      await expect(waiting.getByText(say(locale, 'Say why.'))).toBeVisible();
      expect(calls).toHaveLength(0);
      await waiting.getByRole('button', { name: say(locale, 'Cancel') }).click();
      await waiting.getByRole('button', { name: say(locale, 'Approve') }).click();
      await expect.poll(() => calls.length).toBe(1);
      expect(calls[0]).toMatchObject({ path: '/api/entitlements/usage/requests/r-1/decision', body: { decision: 'Approved' } });
      expect(errors).toEqual([]);
    });

    test('a user without benefits.usage.approve (HR Manager, who holds payroll.approve) is offered no Approve or Reject', async ({ page }) => {
      const { errors } = await boot(page, locale, HR_MANAGER, '/benefits/usage');
      const waiting = page.getByTestId('usage-waiting');
      await expect(waiting.getByTestId('usage-request')).toContainText('INV-2026-118');
      await expect(waiting.getByRole('button', { name: say(locale, 'Approve') })).toHaveCount(0);
      await expect(waiting.getByRole('button', { name: say(locale, 'Reject') })).toHaveCount(0);
      await expect(waiting.getByText(say(locale, 'Finance decides this. Whoever requested it, and the employee it is for, cannot.'))).toBeVisible();
      expect(errors).toEqual([]);
    });

    test('the employee sees My benefits — used and left, requests their own, and sees their requests', async ({ page }) => {
      const { errors, calls } = await boot(page, locale, EMPLOYEE, '/ess/package');
      const card = page.getByTestId('my-benefit-usage');
      await expect(card.getByRole('heading', { name: say(locale, 'My benefits — used and left') })).toBeVisible();
      await expect(card).toContainText(say(locale, '{name}: {left} left of {cap}', { name: say(locale, 'You'),
        left: measureText(0, 'Units', null, locale), cap: measureText(1, 'Units', null, locale) }));
      await expect(card).toContainText(say(locale, 'Waiting for Finance'));
      await shot(page, `ess-my-benefits-${locale}-desktop`);
      await card.getByRole('button', { name: say(locale, 'Request a benefit') }).click();
      const dialog = page.getByTestId('usage-request-dialog');
      await control(dialog, say(locale, 'Benefit')).selectOption('AIR_TICKET');
      await control(dialog, say(locale, 'Who it is for')).selectOption('d-wife');
      await control(dialog, say(locale, 'Number of tickets or items')).fill('1');
      await control(dialog, say(locale, 'Evidence reference')).fill('BKG-55');
      await shot(page, `ess-request-${locale}`);
      await page.getByTestId('usage-request-submit').click();
      await expect(dialog).toHaveCount(0);
      expect(calls[0]).toMatchObject({ path: '/api/ess/package/usage', body: { benefitCode: 'AIR_TICKET', units: 1, dependantId: 'd-wife' } });
      await page.setViewportSize({ width: 390, height: 844 });
      await expect(card).toBeVisible();
      await shot(page, `ess-my-benefits-${locale}-phone`);
      expect(errors).toEqual([]);
    });
  });
}
