import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { ADMIN, boot, type ApiMocks } from './fixture';
import { openSetupSection } from '../setupSections';

/**
 * Setup → Attendance rules, against an intercepted API: the list, creating, editing and turning rules off, the checks
 * made before anything is sent, the server's own refusals, and the Arabic / narrow-screen layout.
 */
const HR_MANAGER = { roles: ['HR Manager'], permissions: ADMIN.permissions };

const DEFAULT = {
  id: 'p-default', code: 'DEFAULT', name: 'Company-wide hours', branchId: null, departmentId: null, gradeId: null,
  graceMinutes: 10, earlyExitThresholdMinutes: 15, halfDayThresholdMinutes: 240, standardWorkMinutes: 480, breakMinutes: 60, isActive: true,
};
const NIGHT = { ...DEFAULT, id: 'p-night', code: 'NIGHT', name: 'Night operations', branchId: 'b-1', graceMinutes: 5, isActive: false };
const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
const structure: ApiMocks = {
  '/api/branches': paged([{ id: 'b-1', companyId: 'fixture-company', code: 'RYD', nameEn: 'Riyadh', nameAr: 'الرياض' }]),
  '/api/departments': paged([{ id: 'd-1', code: 'OPS', nameEn: 'Operations', nameAr: 'العمليات' }]),
  '/api/grades': paged([{ id: 'g-1', code: 'G1', name: 'Grade 1' }]),
};
const mocks = (policies: unknown[] = [DEFAULT, NIGHT]): ApiMocks => ({ '/api/attendance/policies': policies, ...structure });

const open = (page: Page, extra: Parameters<typeof boot>[1] extends infer O ? Partial<O> : never = {}) =>
  boot(page, { url: '/setup?tab=attendanceRules', mocks: mocks(), ...extra });

const panel = (page: Page) => page.getByTestId('attendance-rules');
const editor = (page: Page) => page.getByTestId('attendance-rules-editor');

test('lists each rule set with who it covers and its figures', async ({ page }) => {
  const { errors, writes } = await open(page);
  await expect(page.locator('#setup-section-heading')).toHaveText('Attendance rules', { timeout: 30_000 });
  const card = panel(page).getByTestId('attendance-rule-DEFAULT');
  await expect(card).toContainText('Company-wide hours');
  await expect(card).toContainText('Everyone');
  await expect(card).toContainText('10 min');
  await expect(card).toContainText('Active');
  const off = panel(page).getByTestId('attendance-rule-NIGHT');
  await expect(off).toContainText('Branch: Riyadh');
  await expect(off.getByRole('button', { name: 'Turn on' })).toBeVisible();
  expect(errors).toEqual([]);
  expect(writes).toEqual([]);
});

test('says what saving does to days already processed', async ({ page }) => {
  await open(page);
  await panel(page).getByRole('button', { name: 'New rules' }).click();
  await expect(editor(page)).toContainText('Saving does not change days already processed');
  await expect(editor(page)).toContainText('Recompute these days');
});

test('creates rules, sending the typed figures as numbers and an upper-case code', async ({ page }) => {
  const posted: unknown[] = [];
  await open(page);
  await page.route('**/api/attendance/policies', async route => {
    if (route.request().method() !== 'POST') return route.fallback();
    posted.push(route.request().postDataJSON());
    return route.fulfill({ status: 201, json: { ...DEFAULT, id: 'p-new', code: 'RYD-DAY', name: 'Riyadh day' } });
  });
  await panel(page).getByRole('button', { name: 'New rules' }).click();
  await editor(page).getByLabel('Name').fill('Riyadh day');
  await editor(page).getByLabel('Code').fill('ryd-day');
  await editor(page).getByLabel('Branch').selectOption({ label: 'Riyadh' });
  await editor(page).getByLabel('Grace period before late (minutes)').fill('5');
  await editor(page).getByRole('button', { name: 'Create rules' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Riyadh day saved.' })).toBeVisible();
  expect(posted).toEqual([{
    code: 'RYD-DAY', name: 'Riyadh day', branchId: 'b-1', departmentId: null, gradeId: null,
    graceMinutes: 5, earlyExitThresholdMinutes: 15, halfDayThresholdMinutes: 240, standardWorkMinutes: 480, breakMinutes: 60,
  }]);
});

test('stops a value the server would refuse before sending it, and names the field', async ({ page }) => {
  const { writes } = await open(page);
  await panel(page).getByRole('button', { name: 'New rules' }).click();
  await editor(page).getByLabel('Name').fill('Too generous');
  await editor(page).getByLabel('Code').fill('GEN');
  await editor(page).getByLabel('Grace period before late (minutes)').fill('500');
  await editor(page).getByLabel('Half day below (minutes)').fill('600');
  await editor(page).getByRole('button', { name: 'Create rules' }).click();
  await expect(editor(page).getByText('Enter a whole number of minutes from 0 to 120.')).toBeVisible();
  await expect(editor(page).getByText('Enter a whole number of minutes above 0 and no more than the working day.')).toBeVisible();
  await expect(editor(page).getByLabel('Grace period before late (minutes)')).toHaveAttribute('aria-invalid', 'true');
  expect(writes).toEqual([]);
});

test('editing keeps the code fixed and sends the changed figures to that policy', async ({ page }) => {
  const puts: { url: string; body: unknown }[] = [];
  await open(page);
  await page.route('**/api/attendance/policies/p-default', async route => {
    if (route.request().method() !== 'PUT') return route.fallback();
    puts.push({ url: route.request().url(), body: route.request().postDataJSON() });
    return route.fulfill({ json: { ...DEFAULT, graceMinutes: 20 } });
  });
  await panel(page).getByTestId('attendance-rule-DEFAULT').getByRole('button', { name: 'Edit' }).click();
  await expect(editor(page).getByLabel('Code')).toBeDisabled();
  await editor(page).getByLabel('Grace period before late (minutes)').fill('20');
  await editor(page).getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Company-wide hours saved.' })).toBeVisible();
  expect(puts).toHaveLength(1);
  expect(puts[0].url).toMatch(/\/api\/attendance\/policies\/p-default$/);
  expect(puts[0].body).toMatchObject({ code: 'DEFAULT', graceMinutes: 20 });
});

test('shows the server\'s own reason when it refuses, and keeps the form open', async ({ page }) => {
  await open(page);
  await page.route('**/api/attendance/policies', async route => {
    if (route.request().method() !== 'POST') return route.fallback();
    return route.fulfill({ status: 409, json: { message: 'The policy DEFAULT already covers the same employees. Edit that one, or choose a different branch, department or grade.' } });
  });
  await panel(page).getByRole('button', { name: 'New rules' }).click();
  await editor(page).getByLabel('Name').fill('Another');
  await editor(page).getByLabel('Code').fill('ANOTHER');
  await editor(page).getByRole('button', { name: 'Create rules' }).click();
  await expect(editor(page).getByRole('alert')).toContainText('already covers the same employees');
  await expect(editor(page).getByRole('button', { name: 'Create rules' })).toBeEnabled();
});

test('turning rules off asks first, then posts, and a refusal is reported', async ({ page }) => {
  const calls: string[] = [];
  await open(page);
  await page.route('**/api/attendance/policies/p-default/deactivate', async route => {
    calls.push(route.request().method());
    return route.fulfill({ status: 409, json: { message: 'This is the only active policy, so it cannot be turned off. Attendance needs at least one.' } });
  });
  await panel(page).getByTestId('attendance-rule-DEFAULT').getByRole('button', { name: 'Turn off' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Days already processed do not change.');
  expect(calls).toEqual([]);
  await dialog.getByRole('button', { name: 'Turn off' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'only active policy' })).toBeVisible();
  expect(calls).toEqual(['POST']);
});

test('is not offered to an HR Officer, who would only meet a refusal', async ({ page }) => {
  await boot(page, { persona: { roles: ['HR Officer'], permissions: ADMIN.permissions.filter(p => p !== 'attendance.policy_manage') }, url: '/setup?tab=attendanceRules', mocks: mocks() });
  await expect(page.getByTestId('attendance-rules')).toHaveCount(0);
});

test('is listed under Time & leave for an HR Manager and the Attendance page links to it', async ({ page }) => {
  // The Attendance workspace reads these as paged lists (the default reply is a plain array).
  const attendancePages = Object.fromEntries(
    ['devices', 'daily', 'events/raw', 'regularization/my', 'regularization/pending-approval', 'exceptions'].map(path => [`/api/attendance/${path}`, paged([])]),
  );
  await boot(page, { persona: HR_MANAGER, url: '/attendance', mocks: { ...mocks(), ...attendancePages } });
  const door = page.getByRole('link', { name: 'Attendance settings', exact: true });
  await expect(door).toBeVisible({ timeout: 30_000 });
  await door.click();
  await expect(page).toHaveURL(/\/setup\?tab=attendanceRules/);
  await expect(panel(page)).toBeVisible();
});

test('the retired Policy starter address lands on All setup areas and the studio is gone from the menu', async ({ page }) => {
  await boot(page, { url: '/setup?tab=aiSetup&path=policyStarter&companyId=fixture-company', mocks: mocks() });
  await expect(page).toHaveURL(/path=configuration/, { timeout: 30_000 });
  await expect(page.locator('#setup-section-heading')).toHaveText('All setup areas');
  await expect(page.getByRole('link', { name: 'Policy starter' })).toHaveCount(0);
  await openSetupSection(page, 'Attendance rules');
  await expect(panel(page)).toBeVisible();
});

for (const viewport of [{ name: 'desktop', width: 1440, height: 1000 }, { name: 'phone', width: 375, height: 812 }]) {
  for (const locale of ['en', 'ar'] as const) {
    test(`renders in ${locale} at ${viewport.name} width without horizontal overflow`, async ({ page }) => {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await open(page, { locale });
      await expect(panel(page).getByTestId('attendance-rule-DEFAULT')).toBeVisible({ timeout: 30_000 });
      if (locale === 'ar') {
        await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
        await expect(panel(page)).toContainText('قواعد');
      }
      const dir = process.env.ATTENDANCE_RULES_SHOTS;
      if (dir) { mkdirSync(dir, { recursive: true }); await page.screenshot({ path: `${dir}/${locale}-${viewport.name}-list.png`, fullPage: true }); }
      await panel(page).getByRole('button').first().click();
      await expect(editor(page)).toBeVisible();
      if (dir) await page.screenshot({ path: `${dir}/${locale}-${viewport.name}-editor.png` });
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, 'no horizontal scroll').toBeLessThanOrEqual(0);
    });
  }
}
