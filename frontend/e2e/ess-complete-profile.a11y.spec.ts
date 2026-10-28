import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { bootEss } from './ess/essMocks';

/** P2: axe (WCAG 2.2 AA) on the list, a form with errors, the step-up dialog and the HR inbox. Serious and critical fail. */
async function clean(page: Page, label: string) {
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  await page.waitForTimeout(150);
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).exclude('nextjs-portal').analyze();
  const bad = r.violations.filter(v => v.impact === 'critical' || v.impact === 'serious').map(v => ({ id: v.id, help: v.help, nodes: v.nodes.slice(0, 3).map(n => n.target.join(' ')) }));
  expect(bad, label).toEqual([]);
}

for (const [locale, viewport] of [['en', { width: 1440, height: 1000 }], ['ar', { width: 360, height: 780 }]] as const) {
  test.describe(`axe ${locale} ${viewport.width}`, () => {
    test.use({ viewport });
    test('employee: list, form with errors, step-up dialog', async ({ page }, info) => {
      const shots = process.env.P2_SHOTS;
      await bootEss(page, { locale, sections: ['person', 'emergency', 'id', 'bank', 'documents'], states: { emergency: 'ChangesRequested' } });
      await clean(page, 'list');
      if (locale === 'en' && shots) await page.screenshot({ path: `${shots}/p2-ess-list-en-1440.png`, animations: 'disabled' });
      await page.getByTestId('section-person').click();
      await clean(page, 'form');
      const phone = page.locator('input[type="tel"]');
      await phone.fill('bad');
      await page.locator('form button[type="submit"]').click();
      await expect(page.locator('[aria-invalid="true"]')).toHaveCount(1);
      await expect(page.locator('[tabindex="-1"][role="alert"]')).toBeFocused();
      await clean(page, 'form with errors');
      if (locale === 'en' && shots) await page.screenshot({ path: `${shots}/p2-ess-form-errors-en-1440.png`, animations: 'disabled' });
      if (viewport.width > 1000) await page.getByTestId('section-id').click(); else { await page.getByRole('button').filter({ hasText: /Back to the list|العودة إلى القائمة/ }).click(); await page.getByTestId('section-id').click(); }
      await page.locator('input[inputmode="numeric"]').fill('1000000008');
      await page.locator('form button[type="submit"]').click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await clean(page, 'step-up dialog');
      if (locale === 'en' && shots) await page.screenshot({ path: `${shots}/p2-ess-stepup-en-1440.png`, animations: 'disabled' });
      await page.keyboard.press('Escape');
      // focus order: the section list comes before the form on both layouts
      await expect(page.getByTestId('section-form')).toBeVisible();
    });
    test('HR inbox', async ({ page }) => {
      const shots = process.env.P2_SHOTS;
      await bootEss(page, { locale, persona: 'hr' });
      await clean(page, 'inbox');
      await page.getByTestId('inbox-item').first().getByRole('button').first().click();
      await page.getByTestId('inbox-item').first().getByRole('button').filter({ hasText: /Request changes|طلب تعديل/ }).click();
      await expect(page.locator('textarea')).toBeFocused();
      await clean(page, 'inbox with decision form');
      if (locale === 'en' && shots) await page.screenshot({ path: `${shots}/p2-hr-review-en-1440.png`, animations: 'disabled' });
    });
  });
}

test.describe('step-up dialog is a true modal', () => {
  test.use({ viewport: { width: 320, height: 640 } });
  test('the page behind it is inert, and axe is clean even when a wide Arabic font lets the dialog overlap the navigation', async ({ page }) => {
    await bootEss(page, { locale: 'ar', sections: ['id'] });
    // The CI condition: wider glyphs make the dialog taller and the navigation links shorter, so they overlap.
    await page.addStyleTag({ content: '*{letter-spacing:.08em!important}[role=dialog]{font-size:20px!important}' });
    await page.getByTestId('section-id').click();
    await page.locator('input[inputmode="numeric"]').fill('1000000008');
    await page.locator('form button[type="submit"]').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAttribute('aria-modal', 'true');
    // Everything outside the dialog is inert: not focusable, not in the accessibility tree.
    const outside = await page.evaluate(() => {
      const d = document.querySelector('[role="dialog"]')!;
      return Array.from(document.querySelectorAll('nav[aria-label] a')).map(a => ({ href: a.getAttribute('href'), inert: !!a.closest('[inert]'), inDialog: d.contains(a) }));
    });
    expect(outside.length).toBeGreaterThan(3);
    for (const link of outside) expect(link.inert || link.inDialog, link.href ?? '').toBe(true);
    for (let i = 0; i < 12; i++) {
      await page.keyboard.press('Tab');
      expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), `Tab ${i}`).toBe(true);
    }
    await clean(page, 'step-up dialog over the navigation');
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    expect(await page.evaluate(() => document.querySelectorAll('[inert]').length)).toBe(0);
    await expect(page.locator('nav[aria-label] a').first()).toBeVisible();
  });
});
