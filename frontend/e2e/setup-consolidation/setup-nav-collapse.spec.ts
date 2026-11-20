import { expect, test } from '@playwright/test';
import { boot, ADMIN } from './fixture';

// The Setup left menu is long: each group folds away, the choice is remembered, and the group holding the open section stays open.
test.describe('Setup left menu groups fold away', () => {
  test('a group can be collapsed and expanded, and the choice survives a reload', async ({ page }) => {
    await boot(page, { persona: ADMIN, url: '/setup?tab=companies', locale: 'en' });
    const nav = page.getByTestId('setup-page-nav').or(page.getByRole('navigation', { name: 'Setup sections' }));
    await expect(nav.first()).toBeVisible({ timeout: 60_000 });
    const timeToggle = page.getByTestId('setup-group-toggle-time').or(page.locator('[data-testid^="setup-group-toggle-"]').nth(3));
    const items = page.locator('#' + (await timeToggle.getAttribute('aria-controls')));
    await expect(timeToggle).toHaveAttribute('aria-expanded', 'true');
    await expect(items).toBeVisible();
    await timeToggle.click();
    await expect(timeToggle).toHaveAttribute('aria-expanded', 'false');
    await expect(items).toBeHidden();
    await page.reload();
    await expect(page.locator('[data-testid^="setup-group-toggle-"]').nth(3)).toHaveAttribute('aria-expanded', 'false');
    await page.locator('[data-testid^="setup-group-toggle-"]').nth(3).click();
    await expect(page.locator('[data-testid^="setup-group-toggle-"]').nth(3)).toHaveAttribute('aria-expanded', 'true');
  });

  test('the group holding the open section cannot be folded away, and "Collapse all" keeps it open', async ({ page }) => {
    await boot(page, { persona: ADMIN, url: '/setup?tab=companies', locale: 'en' });
    const toggles = page.locator('[data-testid^="setup-group-toggle-"]');
    await expect(toggles.first()).toBeVisible({ timeout: 60_000 });
    await page.getByTestId('setup-groups-toggle-all').click();
    const states = await toggles.evaluateAll(els => els.map(el => el.getAttribute('aria-expanded')));
    expect(states.filter(s => s === 'true'), 'only the group with the open section stays open').toHaveLength(1);
    await expect(page.getByRole('link', { name: 'Companies' }).first()).toBeVisible();
    await page.getByTestId('setup-groups-toggle-all').click();
    const after = await toggles.evaluateAll(els => els.map(el => el.getAttribute('aria-expanded')));
    expect(after.every(s => s === 'true')).toBe(true);
  });
});
