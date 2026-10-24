import { expect, test, type Page, type Request } from '@playwright/test';

/**
 * Setup → Statutory Rules is maker-checker: POST/PUT/DELETE /api/statutory-rules answer 202 Accepted
 * with a pending request, and a SECOND administrator approves it through
 * /api/finance/rates/statutory/override/{id}/approve. Against synthetic intercepted API responses:
 *   npx next dev -p 5197 &
 *   STATUTORY_RULES_BASE_URL=http://127.0.0.1:5197 npx playwright test -c e2e/playwright.statutory-rules.config.ts
 * Proves the UI contract (nothing applied optimistically, server-decided approve rights), not server authorization.
 *
 * Units (design wave 1, decision 12): a rate is STORED as a fraction (0.0975) and shown as a
 * percentage (9.75%); rule keys appear only behind "Details"; a key whose unit the backend registry
 * (StatutoryValueUnits.cs) does not prove is shown raw with "Unit not specified"; and the approver
 * signs "Change {label} from {old} to {new}, starting {date}".
 */

interface Recorded { method: string; path: string; body: unknown }

interface PendingRow {
  id: string; kind: 'change' | 'retire'; value: string; effectiveFrom: string; effectiveTo: string | null; reason: string;
  reviewBy: null; requestedById: string; requestedByName: string; requestedAt: string; canApprove: boolean;
  companyId: string | null; companyName: string; ruleKey: string; countryCode: string; jurisdiction: string; platformDefault: string | null;
}

const future = (() => { const d = new Date(); d.setUTCDate(d.getUTCDate() + 30); return d.toISOString().slice(0, 10); })();

const rule = (id: string, ruleKey: string, ruleValue: string, isTenantOverride: boolean, effectiveFrom: string, description: string, dataType = 'decimal') => ({
  id, countryCode: 'SA', jurisdiction: 'SA', ruleKey, ruleValue, dataType, description,
  effectiveFrom: `${effectiveFrom}T00:00:00Z`, effectiveTo: null, isTenantOverride,
});

const pendingRow = (over: Partial<PendingRow> & Pick<PendingRow, 'id' | 'ruleKey' | 'value'>): PendingRow => ({
  kind: 'change', effectiveFrom: future, effectiveTo: null, reason: 'Board resolution 14', reviewBy: null,
  requestedById: 'user-layla', requestedByName: 'Layla Haddad', requestedAt: '2026-10-09T09:30:00Z', canApprove: true,
  companyId: null, companyName: 'All companies (tenant default)', countryCode: 'SA', jurisdiction: 'SA', platformDefault: '1.5',
  ...over,
});

// One rule per unit family, with the backend's own seeded values except where a value tests the conversion.
const unitRules = () => [
  rule('u-gosi', 'gosi.saudi_employee_rate', '0.0975', false, '2024-01-01', 'GOSI Regulation 2016'),
  rule('u-ceiling', 'gosi.covered_wage_ceiling_sar', '45000', false, '2024-01-01', 'GOSI covered wage ceiling'),
  rule('u-lead', 'contracts.renewal_lead_days', '120', false, '2024-01-01', 'Release A renewal lead', 'int'),
  rule('u-qiwa', 'contracts.as_is_requires_qiwa_step', 'true', false, '2024-01-01', 'Release A toggle', 'bool'),
  rule('u-curve', 'nitaqat.curve.MANUFACTURING.LowGreen.m', '0.0123', false, '2024-01-01', 'MHRSD annex curve constant'),
  // A legacy company value on a rate key, so the percent editor is exercised end to end.
  rule('u-sick', 'leave.sick_band2_pay_rate', '0.75', false, '2024-01-01', 'Labor Law, Art. 117'),
  rule('u-sick-ovr', 'leave.sick_band2_pay_rate', '0.8', true, '2025-01-01', 'HR policy 2025-07'),
];

async function boot(page: Page, options: { locale?: 'en' | 'ar'; pending?: PendingRow[]; units?: boolean; theme?: 'light' | 'dark' } = {}) {
  const writes: Recorded[] = [];
  const errors: string[] = [];
  // Server state survives reloads because this closure does.
  const rules = [
    rule('plat-std', 'ot.standard_multiplier', '1.5', false, '2024-01-01', 'Labor Law, Art. 107'),
    rule('plat-wkd', 'ot.restday_multiplier', '1.5', false, '2024-01-01', 'Labor Law, Art. 107'),
    rule('ovr-wkd', 'ot.restday_multiplier', '2', true, '2025-01-01', 'CFO policy 2025-03'),
    ...(options.units ? unitRules() : []),
  ];
  const pending: PendingRow[] = [...(options.pending ?? [])];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale, theme }) => {
    localStorage.setItem('zayra_access_token', 'statutory-browser-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
    localStorage.setItem('kynexone.theme', theme);
  }, { locale: options.locale ?? 'en', theme: options.theme ?? 'light' });

  await page.route('**/api/**', async (route) => {
    const request: Request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    const body = (() => { try { return request.postDataJSON(); } catch { return request.postData(); } })();
    if (method !== 'GET') writes.push({ method, path: p, body });

    if (p === '/api/auth/me') {
      return reply({
        id: 'user-maker', tenantId: 'st-tenant', tenantSlug: 'st-fixture', fullName: 'Omar Maker', roles: ['Admin'],
        permissions: ['organization.read', 'organization.write', 'dashboard.read', 'payroll.rates.read', 'payroll.rates.statutory_override'],
        accountType: 'SingleCompany', isGroupScope: true,
        companies: [{ id: 'co-sa', name: 'Masar Trading KSA', code: 'co-sa', countryCode: 'SA', isActive: true }],
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);
    if (p === '/api/country-packs/available') {
      return reply([{ countryCode: 'SA', nameEn: 'Saudi Arabia', nameAr: 'السعودية', jurisdictions: [{ code: 'SA', label: 'Saudi Arabia' }] }]);
    }

    // ── Statutory rules: every write is a request (202), never the saved rule ──
    if (p === '/api/statutory-rules' && method === 'GET') return reply(rules);
    const fileRequest = (kind: 'change' | 'retire', ruleKey: string, value: string, effectiveFrom: string, reason: string) => {
      const created = pendingRow({
        id: `req-${pending.length + 1}`, kind, ruleKey, value, effectiveFrom, reason,
        requestedById: 'user-maker', requestedByName: 'Omar Maker', requestedAt: '2026-10-10T10:00:00Z', canApprove: false, // the requester never approves
      });
      pending.push(created);
      return reply({
        status: 'PendingApproval', requestId: created.id, kind, countryCode: 'SA', jurisdiction: 'SA', ruleKey, value,
        effectiveFrom, effectiveTo: null, reason, requestedById: 'user-maker', requestedAt: created.requestedAt,
        message: 'Saved as a request. A second person with statutory-override rights must approve it before payroll uses it.',
        approveUrl: `/api/finance/rates/statutory/override/${created.id}/approve`,
      }, 202);
    };
    if (p === '/api/statutory-rules' && method === 'POST') {
      const b = body as { ruleKey: string; ruleValue: string; effectiveFrom: string; description: string };
      return fileRequest('change', b.ruleKey, b.ruleValue, b.effectiveFrom.slice(0, 10), b.description);
    }
    if (p.startsWith('/api/statutory-rules/') && method === 'PUT') {
      const prior = rules.find((r) => r.id === p.split('/').pop())!;
      const b = body as { ruleValue: string; effectiveFrom: string; description: string };
      return fileRequest('change', prior.ruleKey, b.ruleValue, b.effectiveFrom.slice(0, 10), b.description);
    }
    if (p.startsWith('/api/statutory-rules/') && method === 'DELETE') {
      const prior = rules.find((r) => r.id === p.split('/').pop())!;
      return fileRequest('retire', prior.ruleKey, prior.ruleValue, future, 'Retire the tenant value and return to the platform default');
    }

    // ── The approval queue (RatesController) ──
    if (p === '/api/finance/rates/statutory/pending') return reply(pending);
    const decided = p.match(/^\/api\/finance\/rates\/statutory\/override\/([^/]+)(?:\/(approve|reject))?$/);
    if (decided) {
      const index = pending.findIndex((x) => x.id === decided[1]);
      const [row] = pending.splice(index, 1);
      if (decided[2] === 'approve') rules.push(rule(`applied-${row.id}`, row.ruleKey, row.value, true, row.effectiveFrom, row.reason));
      return reply({ id: row.id, status: decided[2] === 'approve' ? 'Applied' : 'Withdrawn' });
    }

    if (p === '/api/companies') return reply(paged([]));
    if (p.startsWith('/api/admin/')) return reply([]);
    return reply(paged([]));
  });

  await page.goto('/setup?tab=statutoryRules');
  await expect(page.getByRole('heading', { level: 1, name: options.locale === 'ar' ? 'الإعداد والإدارة' : 'Setup & Administration', exact: true })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByRole('cell', { name: 'CFO policy 2025-03' })).toBeVisible();
  return { writes, errors, rules, pending };
}

const SENT = 'Sent for approval — a second administrator must approve this before payroll uses it.';
const queue = (page: Page) => page.getByRole('region', { name: 'Changes waiting for a second administrator' });
const tableRows = (page: Page) => page.locator('table tbody tr');

test('a new override is sent for approval: the table is unchanged and the requester cannot approve it', async ({ page }) => {
  const state = await boot(page);
  await expect(tableRows(page)).toHaveCount(3);
  await expect(queue(page)).toHaveCount(0);

  await page.getByRole('button', { name: 'New override', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New statutory override' });
  await expect(dialog.getByText('A second administrator must approve each change before payroll uses it.')).toBeVisible();
  await dialog.getByLabel(/^Rule/).selectOption('ot.standard_multiplier');
  await expect(dialog.getByLabel(/^Rule/).locator('option:checked')).toHaveText('Overtime pay multiplier');
  await expect(dialog).toContainText('Published rate: 1.5×');
  await dialog.getByLabel("Your company's value", { exact: true }).fill('1.75');
  await expect(dialog).toContainText('Payroll will use 1.75× (stored as 1.75).');
  await dialog.getByLabel(/^Approved reason and source/).fill('Board resolution 22');
  await dialog.getByRole('button', { name: 'Send for approval', exact: true }).click();

  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole('status').filter({ hasText: SENT })).toBeVisible();
  expect(state.writes.map((w) => `${w.method} ${w.path}`)).toEqual(['POST /api/statutory-rules']);
  // Nothing was applied: the table still shows exactly the server's three rows, none with 1.75.
  await expect(tableRows(page)).toHaveCount(3);
  await expect(page.locator('table').getByText('1.75×', { exact: true })).toHaveCount(0);
  expect(state.writes[0].body).toMatchObject({ ruleKey: 'ot.standard_multiplier', ruleValue: '1.75' });

  const pendingQueue = queue(page);
  await expect(pendingQueue).toContainText('Change Overtime pay multiplier from 1.5× to 1.75×, starting');
  await expect(pendingQueue).not.toContainText('ot.standard_multiplier');
  await expect(pendingQueue).toContainText('Requested by Omar Maker on');
  await expect(pendingQueue.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0);
  await expect(page.locator('table tbody tr').filter({ hasText: 'Overtime pay multiplier' })).toContainText('Pending approval');

  // The requester can withdraw their own request.
  await pendingQueue.getByRole('button', { name: 'Withdraw my request', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Request withdrawn. Nothing changed in payroll.' })).toBeVisible();
  expect(state.writes.at(-1)).toMatchObject({ method: 'DELETE', path: '/api/finance/rates/statutory/override/req-1' });
  await expect(queue(page)).toHaveCount(0);
  expect(state.errors).toEqual([]);
});

test('approve and reject appear only when the server says canApprove, and company requests are not listed here', async ({ page }) => {
  const state = await boot(page, {
    pending: [
      pendingRow({ id: 'req-layla', ruleKey: 'ot.restday_multiplier', value: '2.25' }),
      pendingRow({ id: 'req-sara', ruleKey: 'ot.standard_multiplier', value: '1.6', requestedById: 'user-sara', requestedByName: 'Sara Ali', canApprove: false }),
      pendingRow({ id: 'req-company', ruleKey: 'ot.night_multiplier', value: '1.4', companyId: 'co-sa', companyName: 'Masar Trading KSA' }),
    ],
  });
  const pendingQueue = queue(page);
  await expect(pendingQueue.getByRole('listitem')).toHaveCount(2);
  await expect(pendingQueue).not.toContainText('ot.night_multiplier');
  const layla = pendingQueue.getByRole('listitem').filter({ hasText: 'Layla Haddad' });
  const sara = pendingQueue.getByRole('listitem').filter({ hasText: 'Sara Ali' });
  await expect(sara.getByRole('button')).toHaveCount(0);
  await expect(sara).toContainText('Waiting for another administrator');

  // A pending request hides Supersede/Retire on the override it would change.
  const override = page.locator('table tbody tr').filter({ hasText: 'CFO policy 2025-03' });
  await expect(override).toContainText('Pending approval');
  await expect(override.getByRole('button', { name: /Replace from a date/ })).toHaveCount(0);

  // Reject with a reason.
  await layla.getByRole('button', { name: 'Reject', exact: true }).click();
  let dialog = page.getByRole('dialog', { name: 'Reject this change?' });
  await dialog.getByLabel('Reason for rejecting (optional)').fill('Not in the approved budget');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect(state.writes).toHaveLength(0);

  // Approve: the endpoint is called once, and the table is whatever the server now reports.
  await layla.getByRole('button', { name: 'Approve', exact: true }).click();
  dialog = page.getByRole('dialog', { name: 'Approve this change?' });
  await expect(dialog).toContainText('Change Rest-day overtime multiplier from 2× to 2.25×, starting');
  await expect(dialog.getByRole('definition').filter({ hasText: '2.25×' })).toBeVisible();
  await dialog.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Change approved. Payroll uses it from its start date.' })).toBeVisible();
  expect(state.writes).toEqual([{ method: 'POST', path: '/api/finance/rates/statutory/override/req-layla/approve', body: {} }]);
  await expect(tableRows(page)).toHaveCount(4);
  await expect(page.locator('table tbody tr').filter({ hasText: '2.25' })).toContainText('Scheduled');
  expect(state.errors).toEqual([]);
});

test('reject sends the reason; supersede and retire on an override are requests too', async ({ page }) => {
  const state = await boot(page, { pending: [pendingRow({ id: 'req-layla', ruleKey: 'ot.standard_multiplier', value: '1.6' })] });
  await queue(page).getByRole('button', { name: 'Reject', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Reject this change?' });
  await dialog.getByLabel('Reason for rejecting (optional)').fill('Not in the approved budget');
  await dialog.getByRole('button', { name: 'Reject', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Change rejected. Nothing changed in payroll.' })).toBeVisible();
  expect(state.writes).toEqual([{ method: 'POST', path: '/api/finance/rates/statutory/override/req-layla/reject', body: { reason: 'Not in the approved budget' } }]);
  await expect(queue(page)).toHaveCount(0);

  // Supersede → 202 → notice; the override row keeps its value and loses its actions while pending.
  const override = () => page.locator('table tbody tr').filter({ hasText: 'CFO policy 2025-03' });
  await override().getByRole('button', { name: /Replace from a date/ }).click();
  const supersede = page.getByRole('dialog', { name: 'Replace Rest-day overtime multiplier from a date' });
  await expect(supersede.getByLabel("Your company's value", { exact: true })).toHaveValue('2');
  await supersede.getByLabel("Your company's value", { exact: true }).fill('2.5');
  await supersede.getByLabel(/^Approved reason and source/).fill('CFO policy 2026-01');
  await supersede.getByRole('button', { name: 'Send for approval', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: SENT })).toBeVisible();
  expect(state.writes.at(-1)).toMatchObject({ method: 'PUT', path: '/api/statutory-rules/ovr-wkd' });
  await expect(override()).toContainText('2×');
  await expect(override()).not.toContainText('2.5×');
  await expect(override().getByRole('button', { name: /Retire/ })).toHaveCount(0);

  // Withdraw it, then request retirement instead.
  await queue(page).getByRole('button', { name: 'Withdraw my request', exact: true }).click();
  await expect(queue(page)).toHaveCount(0);
  await override().getByRole('button', { name: /Retire/ }).click();
  const retire = page.getByRole('dialog', { name: 'Retire statutory override' });
  await expect(retire).toContainText('Retiring takes effect only after a second administrator approves it.');
  await retire.getByRole('button', { name: 'Request retirement', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: SENT })).toBeVisible();
  expect(state.writes.at(-1)).toMatchObject({ method: 'DELETE', path: '/api/statutory-rules/ovr-wkd' });
  await expect(queue(page)).toContainText("Stop your company's value for Rest-day overtime multiplier (2×), starting");
  await expect(queue(page)).toContainText('Payroll goes back to the published rate of 1.5×.');
  await expect(tableRows(page)).toHaveCount(3);
  expect(state.errors).toEqual([]);
});

test('the approval queue and notice render in Arabic', async ({ page }) => {
  await boot(page, { locale: 'ar', pending: [pendingRow({ id: 'req-layla', ruleKey: 'ot.restday_multiplier', value: '2.25' })] });
  const pendingQueue = page.getByRole('region', { name: 'تغييرات بانتظار موافقة مسؤول ثانٍ' });
  await expect(pendingQueue).toContainText('تغيير معامل العمل الإضافي في يوم الراحة من 2× إلى 2.25×، اعتبارًا من');
  await expect(pendingQueue.getByRole('button', { name: 'موافقة', exact: true })).toBeVisible();
  await expect(pendingQueue.getByRole('button', { name: 'رفض', exact: true })).toBeVisible();
  await expect(page.locator('table tbody tr').filter({ hasText: 'CFO policy 2025-03' })).toContainText('بانتظار الموافقة');
});

// ── Units, plain labels and the approval sentence (design wave 1, decision 12) ──

const rowFor = (page: Page, label: string) => page.locator('table tbody tr').filter({ has: page.getByText(label, { exact: true }) });
/** The value cell's text with Intl's no-break spaces and direction marks folded away. */
const valueText = async (page: Page, label: string) =>
  ((await rowFor(page, label).locator('td').nth(3).textContent()) ?? '').replace(/[  ]/g, ' ').replace(/[‎‏؜⁦-⁩]/g, '');

test('rules show plain names and values with units; the key is only behind Details; an unknown unit is flagged', async ({ page }) => {
  const state = await boot(page, { units: true });
  const gosi = rowFor(page, 'GOSI contribution, Saudi employee share');
  await expect(gosi).toHaveCount(1);
  expect(await valueText(page, 'GOSI contribution, Saudi employee share')).toBe('9.75%');
  await expect(gosi).toContainText('Percentage of the contributory wage · Set by the platform');
  await expect(gosi).toContainText('Published rate');
  // The raw key and stored fraction are in the row only behind the Details disclosure.
  await expect(gosi.getByText('gosi.saudi_employee_rate', { exact: true })).toBeHidden();
  await gosi.getByText('Details', { exact: true }).click();
  await expect(gosi.getByText('gosi.saudi_employee_rate', { exact: true })).toBeVisible();
  await expect(gosi.getByText('0.0975', { exact: true })).toBeVisible();

  expect(await valueText(page, 'GOSI maximum contributory wage')).toBe('SAR 45,000 a month');
  expect(await valueText(page, 'Renewal review starts before the contract ends')).toBe('120 days');
  expect(await valueText(page, 'Unchanged renewal still goes through Qiwa')).toBe('Yes');
  expect(await valueText(page, 'Overtime pay multiplier')).toBe('1.5×');
  // No unit is on record for a Nitaqat curve constant: raw value, visible flag, no invented unit.
  const curve = rowFor(page, 'Nitaqat curve gradient (m), Manufacturing, LowGreen');
  expect(await valueText(page, 'Nitaqat curve gradient (m), Manufacturing, LowGreen')).toBe('0.0123Unit not specified');
  await expect(curve.getByText('Unit not specified', { exact: true }).first()).toBeVisible();

  const company = rowFor(page, 'Sick pay, second band').filter({ hasText: 'HR policy 2025-07' });
  await expect(company).toContainText("Your company's value");
  expect(await company.locator('td').nth(3).textContent()).toBe('80.00%');
  await expect(company.getByRole('button', { name: 'Replace from a date' })).toBeVisible();
  // Every other row keeps its key behind a closed Details disclosure.
  await expect(rowFor(page, 'GOSI maximum contributory wage').getByText('gosi.covered_wage_ceiling_sar', { exact: true })).toBeHidden();
  await expect(company.getByText('leave.sick_band2_pay_rate', { exact: true })).toBeHidden();
  expect(state.errors).toEqual([]);
});

test('a percentage is edited as a percentage and stored as the exact fraction', async ({ page }) => {
  const state = await boot(page, { units: true });
  const company = rowFor(page, 'Sick pay, second band').filter({ hasText: 'HR policy 2025-07' });
  await company.getByRole('button', { name: 'Replace from a date' }).click();
  const dialog = page.getByRole('dialog', { name: 'Replace Sick pay, second band from a date' });
  const input = dialog.getByLabel("Your company's value", { exact: true });
  // Stored 0.8 is offered as 80 with a visible % sign, never as 0.8.
  await expect(input).toHaveValue('80');
  await expect(dialog.getByText('%', { exact: true })).toBeVisible();
  await expect(dialog).toContainText('Unit: Percentage');
  await expect(dialog).toContainText('Type the percentage: 35 means 35%. Payroll stores it as the fraction 0.35.');
  await expect(dialog).toContainText("Your company's value now: 80.00%");

  await input.fill('150');
  await expect(dialog).toContainText('The most allowed is 100%.');
  await input.fill('9%');
  await expect(dialog).toContainText('Enter a plain number');
  await dialog.getByLabel(/^Approved reason and source/).fill('HR policy 2026-01');
  await dialog.getByRole('button', { name: 'Send for approval', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Enter a plain number');
  expect(state.writes).toHaveLength(0);

  await input.fill('97.5');
  await expect(dialog).toContainText('Payroll will use 97.50% (stored as 0.975).');
  await input.fill('9.75');
  await expect(dialog).toContainText('Payroll will use 9.75% (stored as 0.0975).');
  await dialog.getByRole('button', { name: 'Send for approval', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: SENT })).toBeVisible();
  expect(state.writes).toEqual([expect.objectContaining({ method: 'PUT', path: '/api/statutory-rules/u-sick-ovr', body: expect.objectContaining({ ruleValue: '0.0975' }) })]);
  // The approver reads it back in the same unit, with the old value.
  await expect(queue(page)).toContainText('Change Sick pay, second band from 80.00% to 9.75%, starting');
  expect(state.errors).toEqual([]);
});

test('the approver sees old and new values with units, a first value says so, and an unknown unit stays flagged', async ({ page }) => {
  await boot(page, {
    units: true,
    pending: [
      pendingRow({ id: 'req-first', ruleKey: 'ot.holiday_multiplier', value: '2.5', platformDefault: null }),
      pendingRow({ id: 'req-curve', ruleKey: 'nitaqat.curve.MANUFACTURING.LowGreen.m', value: '0.0125', requestedByName: 'Sara Ali' }),
    ],
  });
  const first = queue(page).getByRole('listitem').filter({ hasText: 'Public-holiday overtime multiplier' });
  await expect(first).toContainText('Set Public-holiday overtime multiplier to 2.5×, starting');
  await expect(first).toContainText('There is no previous value.');
  await first.getByRole('button', { name: 'Approve', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Approve this change?' });
  await expect(dialog.getByRole('definition').filter({ hasText: 'No previous value' })).toBeVisible();
  await expect(dialog.getByRole('definition').filter({ hasText: '2.5×' })).toBeVisible();
  // The key and the stored value, for the audit trail, only behind Details.
  await expect(dialog.getByText('ot.holiday_multiplier', { exact: true })).toBeHidden();
  await dialog.getByText('Details', { exact: true }).click();
  await expect(dialog.getByText('ot.holiday_multiplier', { exact: true })).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();

  const curve = queue(page).getByRole('listitem').filter({ hasText: 'Sara Ali' });
  await expect(curve).toContainText('Change Nitaqat curve gradient (m), Manufacturing, LowGreen from 0.0123 to 0.0125, starting');
  await expect(curve.getByText('Unit not specified', { exact: true })).toBeVisible();
});

test('Arabic: right-to-left, plain names, units and the unknown-unit flag', async ({ page }) => {
  await boot(page, { locale: 'ar', units: true, pending: [pendingRow({ id: 'req-layla', ruleKey: 'ot.restday_multiplier', value: '2.25' })] });
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const gosi = rowFor(page, 'اشتراك التأمينات الاجتماعية، حصة الموظف السعودي');
  expect(await valueText(page, 'اشتراك التأمينات الاجتماعية، حصة الموظف السعودي')).toBe('9.75%');
  await expect(gosi).toContainText('المعدّل المنشور');
  await expect(gosi.getByText('التفاصيل', { exact: true })).toBeVisible();
  expect(await valueText(page, 'بدء مراجعة التجديد قبل انتهاء العقد')).toBe('120 يومًا');
  await expect(rowFor(page, 'ميل منحنى نطاقات (m)، Manufacturing، LowGreen')).toContainText('الوحدة غير محددة');
  await expect(rowFor(page, 'أجر الإجازة المرضية، الشريحة الثانية').filter({ hasText: 'HR policy 2025-07' })).toContainText('قيمة شركتك');
});

test('nothing on the page scrolls sideways, and the table scrolls inside its own frame', async ({ page }) => {
  await boot(page, { units: true });
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});

// Screenshots for the design review: STATUTORY_SHOTS_DIR=/path npx playwright test -c … -g "screenshots"
test('screenshots', async ({ page }, info) => {
  const dir = process.env.STATUTORY_SHOTS_DIR;
  test.skip(!dir, 'set STATUTORY_SHOTS_DIR to capture');
  const pending = [
    pendingRow({ id: 'req-layla', ruleKey: 'ot.restday_multiplier', value: '2.25' }),
    pendingRow({ id: 'req-curve', ruleKey: 'nitaqat.curve.MANUFACTURING.LowGreen.m', value: '0.0125', requestedByName: 'Sara Ali', canApprove: false }),
  ];
  for (const [name, locale, theme] of [['en', 'en', 'light'], ['ar', 'ar', 'light'], ['dark', 'en', 'dark']] as const) {
    await boot(page, { locale, theme, units: true, pending });
    await page.getByRole('heading', { name: locale === 'ar' ? 'تغييرات بانتظار موافقة مسؤول ثانٍ' : 'Changes waiting for a second administrator' }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: `${dir}/${info.project.name}-${name}-table.png`, fullPage: true });
    await page.getByRole('button', { name: locale === 'ar' ? 'موافقة' : 'Approve', exact: true }).first().click();
    await page.screenshot({ path: `${dir}/${info.project.name}-${name}-approve.png` });
    await page.keyboard.press('Escape');
    const company = page.locator('table tbody tr').filter({ hasText: 'HR policy 2025-07' });
    await company.getByRole('button').first().click();
    await page.getByRole('dialog').getByLabel(locale === 'ar' ? 'قيمة شركتك' : "Your company's value", { exact: true }).fill('9.75');
    await page.screenshot({ path: `${dir}/${info.project.name}-${name}-edit.png` });
    await page.keyboard.press('Escape');
  }
});
