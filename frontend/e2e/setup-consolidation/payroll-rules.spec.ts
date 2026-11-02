import { expect, test, type Page } from '@playwright/test';
import { ADMIN, boot, type ApiMocks } from './fixture';

/**
 * Setup → Payroll rules, against an intercepted API: the list, saving a normal choice, the warning and acknowledgement
 * for a value below the statutory minimum, and the Arabic layout.
 */
const view = (key: string, value: string | null, extra: object = {}) => ({
  key, value, isSet: value != null, warningCode: null, warning: null, acknowledged: false,
  acknowledgedAtUtc: null, acknowledgedBy: null, acknowledgementNote: null, ...extra,
});
const KEYS = ['rest_day_work_mode', 'rest_day_multiplier', 'holiday_multiplier', 'leave_cashout_on_exit', 'leave_cashout_basis', 'max_probation_days', 'proration_basis', 'absence_deduction_base'];
const rules = (overrides: Record<string, object> = {}, extra: object = {}) => ({
  companyId: 'fixture-company',
  rules: KEYS.map(key => (overrides[key] ?? view(key, null))),
  pending: [],
  annualLeaveTypeFound: true,
  ...extra,
});
const mocks = (body = rules()): ApiMocks => ({ '/api/payroll/company-rules': body });
const open = (page: Page, body = rules(), locale: 'en' | 'ar' = 'en') =>
  boot(page, { persona: { roles: ['HR Manager'], permissions: [...ADMIN.permissions, 'payroll.rules.manage', 'payroll.approve'] }, url: '/setup?tab=payrollRules', mocks: mocks(body), locale });
const panel = (page: Page) => page.getByTestId('payroll-rules');
const editor = (page: Page) => page.getByTestId('payroll-rules-editor');

test('lists every rule with its current choice or the platform default', async ({ page }) => {
  const { errors, writes } = await open(page, rules({ proration_basis: view('proration_basis', 'CalendarActual') }));
  await expect(page.locator('#setup-section-heading')).toHaveText('Payroll rules', { timeout: 30_000 });
  await expect(panel(page).getByTestId('payroll-rule-proration_basis')).toContainText('Actual days in the month');
  await expect(panel(page).getByTestId('payroll-rule-proration_basis')).toContainText('Your choice');
  await expect(panel(page).getByTestId('payroll-rule-absence_deduction_base')).toContainText('Platform default');
  expect(errors).toEqual([]);
  expect(writes).toEqual([]);
});

test('saves a normal choice with only the changed rule, no acknowledgement needed', async ({ page }) => {
  const puts: unknown[] = [];
  await open(page);
  await page.route('**/api/payroll/company-rules', async route => {
    if (route.request().method() !== 'PUT') return route.fallback();
    puts.push(route.request().postDataJSON());
    return route.fulfill({ json: rules({ absence_deduction_base: view('absence_deduction_base', 'Gross') }) });
  });
  await panel(page).getByRole('button', { name: 'Edit payroll rules' }).click();
  await editor(page).getByLabel('An absent day is deducted from').selectOption('Gross');
  await editor(page).getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Payroll rules saved.' })).toBeVisible();
  expect(puts).toEqual([{ companyId: 'fixture-company', changes: [{ key: 'absence_deduction_base', value: 'Gross' }], acknowledge: false }]);
});

test('a value below the statutory minimum warns, needs the acknowledgement, then a confirmation, and is sent acknowledged', async ({ page }) => {
  const puts: unknown[] = [];
  await open(page);
  await page.route('**/api/payroll/company-rules', async route => {
    if (route.request().method() !== 'PUT') return route.fallback();
    puts.push(route.request().postDataJSON());
    return route.fulfill({ json: rules() });
  });
  await panel(page).getByRole('button', { name: 'Edit payroll rules' }).click();
  await editor(page).getByLabel('Rest-day pay multiplier').fill('1.25');
  await expect(editor(page)).toContainText('below the statutory 150%');
  const save = editor(page).getByRole('button', { name: 'Save changes' });
  await expect(save).toBeDisabled();
  await editor(page).getByLabel(/I understand these values are below/).check();
  await expect(save).toBeDisabled();
  await editor(page).getByLabel('Reason (required)').fill('Signed contract terms');
  await save.click();
  const confirm = page.getByRole('button', { name: 'Confirm and save' });
  await expect(confirm).toBeVisible();
  expect(puts).toEqual([]);
  await confirm.click();
  await expect(page.getByRole('status').filter({ hasText: 'Payroll rules saved.' })).toBeVisible();
  expect(puts).toEqual([{ companyId: 'fixture-company', changes: [{ key: 'rest_day_multiplier', value: '1.25' }], acknowledge: true, note: 'Signed contract terms', acknowledgedWarnings: ['rest_day_multiplier:BELOW_OVERTIME_FLOOR'] }]);
});

test('a change that lowers pay is sent for approval and says so', async ({ page }) => {
  await open(page);
  await page.route('**/api/payroll/company-rules', async route => {
    if (route.request().method() !== 'PUT') return route.fallback();
    return route.fulfill({ status: 202, json: { status: 'PendingApproval', requestId: 'r1', view: rules() } });
  });
  await panel(page).getByRole('button', { name: 'Edit payroll rules' }).click();
  await editor(page).getByLabel('An absent day is deducted from').selectOption('Gross');
  await editor(page).getByLabel('Reason (needed if the change lowers pay)').fill('Agreed with finance');
  await editor(page).getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Sent for approval.' })).toBeVisible();
});

test('with no second approver the refusal is shown in the language of the screen', async ({ page }) => {
  await open(page, rules(), 'ar');
  await page.route('**/api/payroll/company-rules', async route => {
    if (route.request().method() !== 'PUT') return route.fallback();
    return route.fulfill({ status: 409, json: { code: 'ERR_PAYROLL_RULE_NO_APPROVER', message: 'needs a second person', messageAr: 'يحتاج إلى شخص آخر' } });
  });
  await panel(page).getByTestId('payroll-rule-absence_deduction_base').waitFor({ timeout: 30_000 });
  await panel(page).getByRole('button').first().click();
  await editor(page).locator('select').last().selectOption('Gross');
  await editor(page).getByRole('button').last().click();
  await expect(editor(page).getByRole('alert')).toContainText('يحتاج إلى شخص آخر');
});

test('a pending change by someone else can be approved here; one the user made cannot', async ({ page }) => {
  const item = (id: string, requestedBy: string) => ({
    id, requestedBy, requestedAtUtc: '2026-10-26T09:00:00Z', reason: 'Agreed with finance', effectiveFrom: '2026-11-01', warnings: [],
    changes: [{ key: 'absence_deduction_base', from: null, to: 'Gross' }],
  });
  const calls: string[] = [];
  await open(page, rules({}, { pending: [item('theirs', 'someone-else'), item('mine', 'reviewer')] }));
  await page.route('**/api/payroll/company-rules/*/approve', async route => { calls.push(route.request().url()); return route.fulfill({ json: rules() }); });
  await expect(page.getByTestId('payroll-rule-pending-theirs').getByRole('button', { name: 'Approve' })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('payroll-rule-pending-mine').getByRole('button', { name: 'Approve' })).toHaveCount(0);
  await expect(page.getByTestId('payroll-rule-pending-mine')).toContainText('You made this change');
  await page.getByTestId('payroll-rule-pending-theirs').getByRole('button', { name: 'Approve' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Change approved and applied.' })).toBeVisible();
  expect(calls).toHaveLength(1);
});

test('shows the one-time notice that leave payout on exit now defaults on, and a notice when there is no annual type', async ({ page }) => {
  await open(page, rules({}, { annualLeaveTypeFound: false }));
  await expect(page.getByTestId('payroll-rules-cashout-notice')).toContainText('Art. 111', { timeout: 30_000 });
  await expect(page.getByTestId('payroll-rules-no-annual-type')).toContainText('No annual-leave type');
  await page.getByTestId('payroll-rules-cashout-notice').getByRole('button', { name: 'Got it' }).click();
  await expect(page.getByTestId('payroll-rules-cashout-notice')).toHaveCount(0);
  await page.reload();
  await expect(page.getByTestId('payroll-rules')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('payroll-rules-cashout-notice')).toHaveCount(0);
});

test('stops a meaningless value before sending it', async ({ page }) => {
  const { writes } = await open(page);
  await panel(page).getByRole('button', { name: 'Edit payroll rules' }).click();
  await editor(page).getByLabel('Holiday pay multiplier').fill('9');
  await editor(page).getByRole('button', { name: 'Save changes' }).click();
  await expect(editor(page).getByText('Enter a multiplier between 1 and 5.')).toBeVisible();
  expect(writes).toEqual([]);
});

test('shows a rule already saved below the minimum with its warning and when it was confirmed', async ({ page }) => {
  await open(page, rules({ leave_cashout_on_exit: view('leave_cashout_on_exit', 'false', {
    warningCode: 'LEAVE_CASHOUT_OFF', acknowledged: true, acknowledgedAtUtc: '2026-10-20T09:00:00Z' }) }));
  const card = panel(page).getByTestId('payroll-rule-leave_cashout_on_exit');
  await expect(card).toContainText('Do not pay out untaken annual leave', { timeout: 30_000 });
  await expect(page.getByTestId('payroll-rule-leave_cashout_on_exit-warning')).toContainText('Art. 111');
  await expect(page.getByTestId('payroll-rule-leave_cashout_on_exit-warning')).toContainText('Confirmed on');
});

for (const viewport of [{ name: 'desktop', width: 1440, height: 1000 }, { name: 'phone', width: 375, height: 812 }]) {
  test(`renders in Arabic at ${viewport.name} width without horizontal overflow`, async ({ page }) => {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await open(page, rules(), 'ar');
    await expect(panel(page)).toBeVisible({ timeout: 30_000 });
    await expect(panel(page)).toContainText('الإعداد الافتراضي للمنصة');
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(1);
  });
}
