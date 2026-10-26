import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { bootHire } from './hire/hireMocks';

/** G11: axe (WCAG 2.2 AA tags) on every step and state of the hire wizard, desktop EN and 360 px AR. Serious and critical fail. */
async function clean(page: Page, label: string) {
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  await page.waitForTimeout(150);
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).exclude('nextjs-portal').analyze();
  const bad = r.violations.filter(v => v.impact === 'critical' || v.impact === 'serious').map(v => ({ id: v.id, help: v.help, nodes: v.nodes.slice(0, 3).map(n => n.target.join(' ')) }));
  expect(bad, label).toEqual([]);
}

const L = {
  en: { name: 'English full name', start: 'Start date', desig: 'Designation', next: 'Next', save: 'Save draft', cancel: 'Cancel' },
  ar: { name: 'الاسم الكامل بالإنجليزية', start: 'تاريخ المباشرة', desig: 'المسمى الوظيفي', next: 'التالي', save: 'حفظ كمسودة', cancel: 'إلغاء' },
} as const;

for (const [locale, viewport] of [['en', { width: 1440, height: 1000 }], ['ar', { width: 360, height: 780 }]] as const) {
  test.describe(`axe ${locale} ${viewport.width}`, () => {
    test.use({ viewport });
    test('steps, error state, duplicate panel, dialogs, success', async ({ page }) => {
      const l = L[locale];
      await bootHire(page, { locale, duplicate: { status: 'Terminated', matchType: 'strong' }, warnFirstCreate: true });
      await clean(page, 'step 1 empty');
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await expect(page.getByTestId('error-summary')).toBeFocused();
      await clean(page, 'step 1 errors');
      await page.getByLabel(l.name).fill('Alex Morgan');
      await page.getByLabel(l.name).blur();
      await expect(page.getByTestId('error-summary')).toHaveCount(1);
      await page.getByLabel(l.start).fill('2026-01-05');
      await page.getByLabel(l.desig).selectOption({ label: 'Specialist' });
      await expect(page.locator('section[aria-label]').first()).toBeVisible();
      await clean(page, 'duplicate panel');
      await page.getByRole('button', { name: l.save, exact: true }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await clean(page, 'warning dialog');
      await page.keyboard.press('Escape');
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await clean(page, 'step 2');
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await clean(page, 'step 3');
      await page.getByRole('button', { name: l.next, exact: true }).click();
      await clean(page, 'review');
      await page.getByRole('button', { name: l.cancel, exact: true }).click();
      await expect(page.getByRole('alertdialog')).toBeVisible();
      await clean(page, 'discard dialog');
    });
  });
}
