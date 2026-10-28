import { expect, test, type Page } from '@playwright/test';

// Statutory minimums are defaults, not walls (owner decision 2026-10-25). A leave policy below the Saudi minimum is allowed
// once the admin ticks "I understand employees may claim the difference" and gives a reason; the save carries
// `acknowledgedWarnings: [{ code, reason }]` (the hire path's shape). A mocked API: browser contracts only, no tenant writes.
const A = { id: 'company-a', legalNameEn: 'Alpha Arabia LLC' };
const types = [
  { id: 'mat-a', code: 'MAT', nameEn: 'Maternity Leave', nameAr: 'إجازة الوضع', category: 'Parental', isPaid: true, isActive: true, companyId: A.id },
  { id: 'annual-a', code: 'ANNUAL', nameEn: 'Annual Leave', nameAr: 'الإجازة السنوية', category: 'Annual', isPaid: true, isActive: true, companyId: A.id },
];

const maternityCheck = (days: number) => ({
  code: 'leave_special_entitlement_below_minimum', field: 'annualEntitlementDays', label: 'Days granted', labelAr: 'عدد الأيام الممنوحة', below: days < 84,
  statutoryValue: 84, configuredValue: days, citation: 'Labor Law Art. 151', citationAr: 'نظام العمل، المادة 151',
  message: `Maternity leave: this policy grants ${days} days, below the legal minimum of 84 days (Labor Law Art. 151).`,
  messageAr: `إجازة الوضع: تمنح هذه السياسة ${days} يومًا، وهو أقل من الحد الأدنى النظامي البالغ 84 يومًا (نظام العمل، المادة 151).`,
});

interface Options { locale?: 'en' | 'ar'; screenKnowsMinimum?: boolean; belowList?: unknown[] }

async function boot(page: Page, url: string, section: string, options: Options = {}) {
  const writes: { path: string; body: Record<string, unknown> }[] = [];
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript((locale: string) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, options.locale ?? 'en');
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ json, status });
    if (path === '/api/leave/policies/floor-check') {
      const body = request.postDataJSON() as { leaveTypeId: string; countryCode?: string; annualEntitlementDays: number };
      const saudi = ['SA', 'KSA', 'SAU'].includes(body.countryCode ?? '');
      return reply({ checks: options.screenKnowsMinimum === false || body.leaveTypeId !== 'mat-a' || !saudi ? [] : [maternityCheck(body.annualEntitlementDays)] });
    }
    if (request.method() === 'POST' && path === '/api/leave/policies') {
      const body = request.postDataJSON() as Record<string, unknown>;
      writes.push({ path, body });
      if (!body.acknowledgedWarnings && Number(body.annualEntitlementDays) < 84 && body.leaveTypeId === 'mat-a' && ['SA', 'KSA', 'SAU'].includes(String(body.countryCode)))
        return reply({ type: 'about:blank', title: 'Acknowledgement required.', status: 409, code: 'warnings_require_acknowledgement', traceId: 't1',
          warnings: [{ ...maternityCheck(Number(body.annualEntitlementDays)), requiresPermission: false }] }, 409);
      return reply({ id: 'saved' });
    }
    if (request.method() !== 'GET') { writes.push({ path, body: request.postDataJSON() }); return reply({ id: 'saved' }); }
    if (path === '/api/auth/me') return reply({ id: 'hr', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'HR', roles: ['Admin'], permissions: ['leave.read', 'leave.write', 'leave.policy_manage', 'organization.read', 'payroll.rates.read'], accountType: 'Group', isGroupScope: true, companies: [A].map(c => ({ id: c.id, name: c.legalNameEn, code: c.id, countryCode: 'SAU', isActive: true })) });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return reply([]);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/leave/holidays/calendars' || path === '/api/leave/holidays/blackouts') return reply([]);
    if (path === '/api/companies' || path === '/api/companies/all') return reply({ items: [A], total: 1, page: 1, pageSize: 100 });
    if (path === '/api/leave/types') return reply(types);
    if (path === '/api/leave/policies') return reply({ items: [], total: 0, page: 1, pageSize: 100 });
    if (path === '/api/compliance/below-minimum') return reply({ items: options.belowList ?? [] });
    if (path === '/api/statutory-rules' || path === '/api/country-packs/available' || path === '/api/finance/rates/statutory/pending') return reply([]);
    if (path === '/api/notifications') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto(url);
  await expect(page.locator('#setup-section-heading')).toHaveText(section, { timeout: 90_000 });
  return { writes, errors };
}

async function openNewMaternityPolicy(page: Page) {
  await page.getByRole('button', { name: 'New Policy' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByPlaceholder('e.g. UAE Annual Leave — Full-Time').fill('Maternity KSA');
  await dialog.locator('select').filter({ has: page.locator('option', { hasText: 'Maternity Leave' }) }).selectOption('mat-a');
  await dialog.locator('select').filter({ has: page.locator('option', { hasText: 'KSA' }) }).first().selectOption('KSA');
  return dialog;
}

test('a new Saudi maternity policy starts at the statutory 84 ("Saudi default"); below it needs the box and a reason, then saves with acknowledgedWarnings', async ({ page }) => {
  const { writes, errors } = await boot(page, '/setup?tab=leavePolicies', 'Leave policies');
  const dialog = await openNewMaternityPolicy(page);
  const days = dialog.locator('input[type=number]').first();

  await expect(days).toHaveValue('84');
  await expect(dialog.getByTestId('floor-leave_special_entitlement_below_minimum')).toContainText('Saudi default: 84');

  await days.fill('70');
  const row = dialog.getByTestId('floor-leave_special_entitlement_below_minimum');
  await expect(row).toHaveAttribute('data-status', 'below');
  await expect(row).toContainText('Below the legal minimum of 84 (Labor Law Art. 151)');
  await expect(dialog.getByText('I understand employees may claim the difference')).toBeVisible();

  // Nothing is sent until the warning is acknowledged with a reason.
  await dialog.getByRole('button', { name: /Create|Save/ }).last().click();
  await expect(dialog).toContainText('Tick the box and give a reason to save below the legal minimum.');
  expect(writes).toHaveLength(0);

  await dialog.getByTestId('below-minimum-ack').check();
  await dialog.getByTestId('below-minimum-reason').fill('Board approved a shorter policy for this entity');
  await dialog.getByRole('button', { name: /Create|Save/ }).last().click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].body.annualEntitlementDays).toBe(70);
  expect(writes[0].body.acknowledgedWarnings).toEqual([{ code: 'leave_special_entitlement_below_minimum', reason: 'Board approved a shorter policy for this entity' }]);
  expect(errors).toEqual([]);
});

test('a value above the statutory minimum is "More generous than the law" and needs no acknowledgement', async ({ page }) => {
  const { writes } = await boot(page, '/setup?tab=leavePolicies', 'Leave policies');
  const dialog = await openNewMaternityPolicy(page);
  await dialog.locator('input[type=number]').first().fill('98');
  await expect(dialog.getByTestId('floor-leave_special_entitlement_below_minimum')).toContainText('More generous than the law');
  await expect(dialog.getByTestId('below-minimum-ack')).toHaveCount(0);
  await dialog.getByRole('button', { name: /Create|Save/ }).last().click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].body.acknowledgedWarnings).toBeUndefined();
});

test('in Arabic the minimum, the warning, the checkbox and the reason prompt are all Arabic', async ({ page }) => {
  await boot(page, '/setup?tab=leavePolicies', 'سياسات الإجازات', { locale: 'ar' });
  await page.getByRole('button', { name: 'New Policy' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.locator('select').filter({ has: page.locator('option', { hasText: 'Maternity Leave' }) }).selectOption('mat-a');
  await dialog.locator('select').filter({ has: page.locator('option', { hasText: 'KSA' }) }).first().selectOption('KSA');
  const days = dialog.locator('input[type=number]').first();
  await expect(days).toHaveValue('84');
  await expect(dialog.getByTestId('floor-leave_special_entitlement_below_minimum')).toContainText('الافتراضي السعودي: 84');
  await days.fill('70');
  await expect(dialog.getByTestId('floor-leave_special_entitlement_below_minimum')).toContainText('أقل من الحد الأدنى النظامي البالغ 84 (نظام العمل، المادة 151)');
  await expect(dialog.getByText('أدرك أن للموظفين المطالبة بالفرق')).toBeVisible();
  await expect(dialog.getByText('لماذا هي أقل من الحد الأدنى النظامي؟ (من 10 إلى 500 حرف)')).toBeVisible();
});

test('when the server knows a minimum the screen did not, the 409 is shown, nothing is lost, and the acknowledged resend saves', async ({ page }) => {
  const { writes } = await boot(page, '/setup?tab=leavePolicies', 'Leave policies', { screenKnowsMinimum: false });
  const dialog = await openNewMaternityPolicy(page);
  await dialog.locator('input[type=number]').first().fill('70');
  await expect(dialog.getByTestId('below-minimum-panel')).toHaveCount(0);

  await dialog.getByRole('button', { name: /Create|Save/ }).last().click();
  await expect(dialog.getByTestId('floor-leave_special_entitlement_below_minimum')).toHaveAttribute('data-status', 'below');
  await expect(dialog).toContainText('Tick the box and give a reason to save below the legal minimum.');
  expect(writes).toHaveLength(1);

  await dialog.getByTestId('below-minimum-ack').check();
  await dialog.getByTestId('below-minimum-reason').fill('Seasonal contractors are on a shorter policy');
  await dialog.getByRole('button', { name: /Create|Save/ }).last().click();
  await expect.poll(() => writes.length).toBe(2);
  expect(writes[1].body.acknowledgedWarnings).toEqual([{ code: 'leave_special_entitlement_below_minimum', reason: 'Seasonal contractors are on a shorter policy' }]);
});

test('Setup lists every rule below the legal minimum, acknowledged or not', async ({ page }) => {
  await boot(page, '/setup?tab=statutoryRules', 'Statutory rules', {
    belowList: [
      { source: 'leave_policy', code: 'leave_annual_below_minimum', companyId: A.id, companyName: A.legalNameEn, rule: 'Annual Leave: Annual leave days', ruleAr: 'الإجازة السنوية: أيام الإجازة السنوية', subject: 'Casual staff', configured: 15, statutory: 21, citation: 'Labor Law Art. 109', citationAr: 'نظام العمل، المادة 109', message: 'm', messageAr: null, acknowledged: true, acknowledgedBy: 'Hana HR', acknowledgedAtUtc: '2026-10-27T09:00:00Z', note: 'Casual staff agreed 15 days' },
      { source: 'eosb', code: 'eosb_tier1_below_minimum', companyId: null, companyName: null, rule: 'End of service: Years 1 to 5', ruleAr: 'x', subject: 'SA', configured: 10, statutory: 15, citation: 'Labor Law Art. 84', citationAr: null, message: 'm', messageAr: null, acknowledged: false, acknowledgedBy: null, acknowledgedAtUtc: null, note: null },
    ],
  });
  const rows = page.getByTestId('below-minimum-row');
  await expect(rows).toHaveCount(2);
  await expect(rows.first()).toContainText('Alpha Arabia LLC');
  await expect(rows.first()).toContainText('Labor Law Art. 109');
  await expect(rows.first()).toContainText('Hana HR');
  await expect(rows.nth(1)).toHaveAttribute('data-acknowledged', 'false');
  await expect(rows.nth(1)).toContainText('Not acknowledged');
});
