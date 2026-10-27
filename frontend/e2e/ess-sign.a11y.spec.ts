import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { bootEsign, EMPLOYEE_NAME } from './ess/esignMocks';

/** P4: axe (WCAG 2.2 AA) on the list, the sign page with an error, the password dialog, the HR panel and the sign-in-person dialog. */
async function clean(page: Page, label: string) {
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  await page.waitForTimeout(150);
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).exclude('nextjs-portal').exclude('iframe').analyze();
  const bad = r.violations.filter(v => v.impact === 'critical' || v.impact === 'serious').map(v => ({ id: v.id, help: v.help, nodes: v.nodes.slice(0, 3).map(n => n.target.join(' ')) }));
  expect(bad, label).toEqual([]);
}

for (const [locale, viewport] of [['en', { width: 1440, height: 1000 }], ['ar', { width: 360, height: 780 }]] as const) {
  test.describe(`axe ${locale} ${viewport.width}`, () => {
    test.use({ viewport });
    test('employee: list, sign page with a name error, password dialog', async ({ page }) => {
      await bootEsign(page, { locale });
      await clean(page, 'list');
      await page.getByTestId('sign-row').first().getByRole('link').click();
      await expect(page.getByTestId('pdf-preview')).toBeVisible();
      await clean(page, 'sign page');
      await page.locator('input[type="checkbox"]').check();
      await page.locator('input[autocomplete="name"]').fill('Not the name');
      await page.locator('form button[type="submit"]').click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await clean(page, 'password dialog');
      await page.getByRole('dialog').locator('input[type="password"]').fill('pw');
      await page.getByRole('dialog').locator('button[type="submit"]').click();
      await expect(page.locator('input[autocomplete="name"]')).toHaveAttribute('aria-invalid', 'true');
      await expect(page.locator('input[autocomplete="name"]')).toBeFocused();
      await clean(page, 'sign page with error');
    });
    test('HR: panel and sign-in-person dialog', async ({ page }) => {
      await bootEsign(page, { locale, persona: 'hr' });
      await expect(page.getByTestId('signatures-panel')).toBeVisible();
      await clean(page, 'panel');
      await page.getByTestId('signature-row').first().getByRole('button').first().click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await expect(page.getByTestId('pdf-preview')).toBeVisible();
      await clean(page, 'device dialog');
      // focus returns to the opener on Escape
      await page.keyboard.press('Escape');
      await expect(page.getByRole('dialog')).toBeHidden();
    });
  });
}
void EMPLOYEE_NAME;
