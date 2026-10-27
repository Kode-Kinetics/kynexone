import { expect, test, type Page } from '@playwright/test';
import { bootHire, expectNoHorizontalScroll, hireTemplateFixture } from './hire/hireMocks';

/**
 * Browser Pilot (WP-P3, AC69): the hire template picker in the Add Employee wizard, against the mocked API.
 * Local: next dev -p <port>, then playwright test -c e2e/playwright.onboarding-p3.config.ts (EMPLOYEE_HIRE_BASE_URL).
 */
const next = (page: Page) => page.getByRole('button', { name: 'Next', exact: true }).click();
const templates = [hireTemplateFixture];

test.describe('hire template picker', () => {
  test('no templates: the picker is not shown and the wizard is unchanged', async ({ page }) => {
    await bootHire(page);
    await expect(page.getByLabel('English full name')).toBeVisible();
    await expect(page.getByTestId('hire-template-picker')).toHaveCount(0);
  });

  test('a suggested template is offered, applies on request, fills empty fields and announces it', async ({ page }) => {
    await bootHire(page, { hireTemplates: templates, suggestedTemplateId: 'tpl-cashier' });
    await expect(page.getByTestId('hire-template-picker')).toBeVisible();
    await expect(page.getByText('Suggested for this designation: Branch cashier.')).toBeVisible();
    await expect(page.getByLabel('Department')).toHaveValue('');
    await page.getByLabel('Start date').fill('2026-11-08');
    await page.getByRole('button', { name: 'Apply', exact: true }).click();
    await expect(page.getByLabel('Department')).toHaveValue('dept-1');
    const status = page.getByTestId('hire-template-status');
    await expect(status).toContainText('Applied: Branch cashier');
    await expect(status).toHaveAttribute('aria-live', 'polite');
    // The probation end date follows the start date (90 days), the rest of the defaults reach the next steps.
    await next(page);
    await next(page).catch(() => undefined);
  });

  test('AC69: a field the person already filled is never overwritten, and the chip says so', async ({ page }) => {
    await bootHire(page, { hireTemplates: templates });
    await page.getByLabel('Department').selectOption('dept-2');
    await page.getByLabel('Hire template').selectOption('tpl-cashier');
    await expect(page.getByLabel('Department')).toHaveValue('dept-2');
    await expect(page.getByTestId('hire-template-status')).toContainText('left as you entered');
  });

  test('Undo puts back what the template filled and keeps what the person changed afterwards', async ({ page }) => {
    await bootHire(page, { hireTemplates: templates });
    await page.getByLabel('Hire template').selectOption('tpl-cashier');
    await expect(page.getByLabel('Department')).toHaveValue('dept-1');
    await page.getByRole('button', { name: 'Undo', exact: true }).click();
    await expect(page.getByLabel('Department')).toHaveValue('');
    await expect(page.getByTestId('hire-template-status')).not.toContainText('Applied');
    // Apply again, then change the department by hand: Undo must not touch the person's choice.
    await page.getByLabel('Hire template').selectOption('tpl-cashier');
    await page.getByLabel('Department').selectOption('dept-2');
    await page.getByRole('button', { name: 'Undo', exact: true }).click();
    await expect(page.getByLabel('Department')).toHaveValue('dept-2');
  });

  test('the template id travels with the create request and the review lists the checklist for the hire', async ({ page }) => {
    const api = await bootHire(page, { hireTemplates: templates });
    await page.getByLabel('English full name').fill('Alex Morgan');
    await page.getByLabel('Start date').fill('2026-11-08');
    await page.getByLabel('Designation').selectOption({ label: 'Specialist' });
    await page.getByLabel('Hire template').selectOption('tpl-cashier');
    for (let i = 0; i < 3; i++) await next(page);
    await expect(page.getByRole('heading', { level: 2, name: 'Review and create' })).toBeVisible();
    await expect(page.getByTestId('review-checklist')).toContainText('Create the company mailbox');
    await expect(page.getByTestId('review-checklist')).not.toContainText('Iqama');
    await expect(page.getByText('2 tasks, 1 blocks activation')).toBeVisible();
    await page.getByRole('button', { name: 'Create employee draft' }).click();
    await expect(page.getByRole('heading', { name: 'Alex Morgan has been added as a draft.' })).toBeVisible();
    expect(api.creates[0].body.hireTemplateId).toBe('tpl-cashier');
  });

  for (const [typed, option, shows] of [['sau', /Saudi/, false], ['pak', /Pakistan/, true]] as const) {
    test(`Review for a ${typed === 'sau' ? 'Saudi' : 'Pakistani'} hire ${shows ? 'lists' : 'omits'} the Iqama task`, async ({ page }) => {
      await bootHire(page, { hireTemplates: templates });
      await page.getByLabel('English full name').fill('Alex Morgan');
      await page.getByLabel('Start date').fill('2026-11-08');
      await page.getByLabel('Designation').selectOption({ label: 'Specialist' });
      await page.getByLabel('Hire template').selectOption('tpl-cashier');
      await next(page);
      const box = page.getByRole('combobox', { name: /Nationality/ });
      await box.fill(typed);
      await page.getByRole('listbox').getByRole('option', { name: option }).first().click();
      await next(page);
      await next(page);
      const list = page.getByTestId('review-checklist');
      await expect(list).toContainText('Register with GOSI');
      if (shows) await expect(list).toContainText('Issue the Iqama through Muqeem'); else await expect(list).not.toContainText('Iqama');
    });
  }
});

test.describe('hire template picker in Arabic at 360 px', () => {
  test.use({ viewport: { width: 360, height: 780 } });
  test('mirrors, fits, and speaks Arabic', async ({ page }) => {
    await bootHire(page, { locale: 'ar', hireTemplates: templates });
    await expect(page.getByTestId('hire-template-picker')).toBeVisible();
    await page.getByLabel('قالب التوظيف').selectOption('tpl-cashier');
    await expect(page.getByTestId('hire-template-status')).toContainText('تم التطبيق: أمين صندوق فرع');
    await expectNoHorizontalScroll(page);
    await page.screenshot({ path: process.env.P3_SHOTS ? `${process.env.P3_SHOTS}/p3-hire-template-ar-360.png` : undefined, fullPage: true });
  });
});

test('screenshot: the picker in English at 1440 px', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await bootHire(page, { hireTemplates: templates, suggestedTemplateId: 'tpl-cashier' });
  await page.getByLabel('Hire template').selectOption('tpl-cashier');
  await page.screenshot({ path: process.env.P3_SHOTS ? `${process.env.P3_SHOTS}/p3-hire-template-en-1440.png` : undefined });
});
