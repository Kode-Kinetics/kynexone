import { expect, test, type Page } from '@playwright/test';

// Art. 84 end-of-service rates are defaults, not walls (owner decision 2026-10-25). A rate under the statutory 15 / 30 days a
// year is allowed once the admin ticks "I understand employees may claim the difference" and gives a reason; the save
// carries `acknowledgedWarnings: [{ code, reason }]`. A mocked API: browser contracts only, no tenant writes.
const gcc = { id: 'gcc-sa', countryCode: 'SA', isActive: true, wpsEnabled: true, wpsAgentId: '', wpsMolCode: '', sifEnabled: true,
  eosbEnabled: true, eosbYears1To5Rate: 15, eosbYearsAbove5Rate: 30, eosbMinYears: 1, workWeek: 'SunThu', weekendDays: 'FriSat',
  visaTrackingEnabled: true, visaAlertDays: 60, iqamaRequired: true, iqamaAlertDays: 30, emiratesIdRequired: false,
  ramadanHoursEnabled: false, ramadanReducedHoursPerDay: 2 };

const check = (rate: number, field: string, code: string, label: string, labelAr: string, statutory: number) => ({
  code, field, label, labelAr, below: rate < statutory, statutoryValue: statutory, configuredValue: rate,
  citation: 'Labor Law Art. 84', citationAr: 'نظام العمل، المادة 84',
  message: `The end-of-service rate is ${rate} days a year, below the legal minimum of ${statutory} days a year (Labor Law Art. 84). Employees may claim the difference.`,
  messageAr: `معدل مكافأة نهاية الخدمة هو ${rate} يومًا في السنة، وهو أقل من الحد الأدنى النظامي البالغ ${statutory} يومًا (نظام العمل، المادة 84).`,
});
const checksFor = (b: { eosbYears1To5Rate: number; eosbYearsAbove5Rate: number }) => [
  check(b.eosbYears1To5Rate, 'eosbYears1To5Rate', 'eosb_tier1_below_minimum', 'Years 1 to 5', 'السنوات 1 إلى 5', 15),
  check(b.eosbYearsAbove5Rate, 'eosbYearsAbove5Rate', 'eosb_tier2_below_minimum', 'Years above 5', 'ما بعد السنة الخامسة', 30),
];

async function boot(page: Page, locale: 'en' | 'ar') {
  const writes: Record<string, unknown>[] = [];
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript((l: string) => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', l);
  }, locale);
  await page.route('**/api/**', async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ json, status });
    if (path === '/api/admin/gcc-settings/floor-check') return reply({ checks: checksFor(request.postDataJSON()) });
    if (request.method() === 'POST' && path === '/api/admin/gcc-settings') {
      const body = request.postDataJSON() as Record<string, unknown>;
      writes.push(body);
      const below = checksFor(body as never).filter(c => c.below);
      if (below.length && !body.acknowledgedWarnings)
        return reply({ type: 'about:blank', title: 'Acknowledgement required.', status: 409, code: 'warnings_require_acknowledgement', traceId: 't', warnings: below.map(c => ({ ...c, requiresPermission: false })) }, 409);
      return reply({ ...gcc, ...body });
    }
    if (request.method() !== 'GET') return reply({ id: 'saved' });
    if (path === '/api/auth/me') return reply({ id: 'hr', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'HR', roles: ['Admin'], permissions: ['qiwa.read', 'organization.read', 'security.manage'], accountType: 'Group', isGroupScope: true, companies: [] });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/admin/gcc-settings') return reply([gcc]);
    if (path === '/api/companies' || path === '/api/companies/all') return reply({ items: [], total: 0, page: 1, pageSize: 100 });
    if (path === '/api/notifications') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto('/setup?tab=saudiCompliance&section=labor');
  await expect(page.getByTitle(locale === 'en' ? 'EOSB Rate Years 1 to 5' : 'معدل مكافأة نهاية الخدمة للسنوات 1 إلى 5')).toBeVisible({ timeout: 90_000 });
  return { writes, errors };
}

test('the Saudi default 15/30 is shown; a rate under it needs the box and a reason, then saves with acknowledgedWarnings', async ({ page }) => {
  const { writes, errors } = await boot(page, 'en');
  const rate1 = page.getByTitle('EOSB Rate Years 1 to 5');
  await expect(rate1).toHaveValue('15');
  await expect(page.getByTestId('floor-eosb_tier1_below_minimum')).toContainText('Saudi default: 15');

  await rate1.fill('10');
  const row = page.getByTestId('floor-eosb_tier1_below_minimum');
  await expect(row).toHaveAttribute('data-status', 'below');
  await expect(row).toContainText('Below the legal minimum of 15 (Labor Law Art. 84)');

  await page.getByRole('button', { name: 'Save Labor Settings' }).click();
  await expect(page.getByText('Tick the box and give a reason to save below the legal minimum.')).toBeVisible();
  expect(writes).toHaveLength(0);

  await page.getByTestId('below-minimum-ack').check();
  await page.getByTestId('below-minimum-reason').fill('Casual staff are on a shorter scheme');
  await page.getByRole('button', { name: 'Save Labor Settings' }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].eosbYears1To5Rate).toBe(10);
  expect(writes[0].acknowledgedWarnings).toEqual([{ code: 'eosb_tier1_below_minimum', reason: 'Casual staff are on a shorter scheme' }]);
  expect(errors).toEqual([]);
});

test('a rate above the statutory minimum is more generous than the law and needs no acknowledgement', async ({ page }) => {
  const { writes } = await boot(page, 'en');
  await page.getByTitle('EOSB Rate Years 1 to 5').fill('21');
  await expect(page.getByTestId('floor-eosb_tier1_below_minimum')).toContainText('More generous than the law');
  await expect(page.getByTestId('below-minimum-ack')).toHaveCount(0);
  await page.getByRole('button', { name: 'Save Labor Settings' }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].acknowledgedWarnings).toBeUndefined();
});

test('in Arabic the default, the warning, the checkbox and the reason prompt are Arabic', async ({ page }) => {
  await boot(page, 'ar');
  await expect(page.getByTestId('floor-eosb_tier1_below_minimum')).toContainText('الافتراضي السعودي: 15');
  await page.getByTitle('معدل مكافأة نهاية الخدمة للسنوات 1 إلى 5').fill('10');
  await expect(page.getByTestId('floor-eosb_tier1_below_minimum')).toContainText('أقل من الحد الأدنى النظامي البالغ 15 (نظام العمل، المادة 84)');
  await expect(page.getByText('أدرك أن للموظفين المطالبة بالفرق')).toBeVisible();
  await expect(page.getByText('لماذا هي أقل من الحد الأدنى النظامي؟ (من 10 إلى 500 حرف)')).toBeVisible();
});
