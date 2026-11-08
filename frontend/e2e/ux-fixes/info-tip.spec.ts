import { expect, test } from '@playwright/test';
import { ALL_PERMISSIONS, boot, type Persona } from '../setup-consolidation/fixture';

/**
 * The `i` (Screen Standard 3b) in a real browser: opens on hover and keyboard focus, stays open while the pointer is on
 * it (WCAG 1.4.13), Esc dismisses it and nothing else, and the developer key is shown only to the Setup permission.
 */
const withoutSetup: Persona = { roles: ['Admin'], permissions: ALL_PERMISSIONS.filter(p => p !== 'security.manage') };
const withSetup: Persona = { roles: ['Admin'], permissions: ALL_PERMISSIONS };
const open = async (page: Parameters<typeof boot>[0], persona: Persona, locale: 'en' | 'ar' = 'en') => {
  await boot(page, { persona, url: '/leave', locale });
  await page.getByRole('tab', { name: /Apply Leave|تقديم/ }).or(page.getByRole('button', { name: /Apply Leave/ })).first().click();
};

test('keyboard focus opens the popover, Esc dismisses it, focus stays on the icon', async ({ page }) => {
  await open(page, withoutSetup);
  const icon = page.getByRole('button', { name: 'About Leave Type *' });
  await icon.focus();
  const tip = page.getByRole('tooltip').filter({ hasText: 'The category of leave' });
  await expect(tip).toBeVisible();
  await expect(icon).toHaveAttribute('aria-expanded', 'true');
  await page.keyboard.press('Escape');
  await expect(tip).toBeHidden();
  await expect(icon).toBeFocused();
});

test('the popover stays open while the pointer moves onto it', async ({ page }) => {
  await open(page, withoutSetup);
  const icon = page.getByRole('button', { name: 'About Leave Type *' });
  await icon.hover();
  const tip = page.getByRole('tooltip').filter({ hasText: 'The category of leave' });
  await expect(tip).toBeVisible();
  await tip.hover();
  await expect(tip).toBeVisible();
  await page.mouse.move(2, 2);
  await expect(tip).toBeHidden();
});

test('an ordinary user never sees the developer key; the Setup permission holder does', async ({ page }) => {
  await open(page, withoutSetup);
  await page.getByRole('button', { name: 'About Leave Type *' }).focus();
  await expect(page.getByRole('tooltip').filter({ hasText: 'The category of leave' })).toBeVisible();
  await expect(page.getByText(/key:/i)).toHaveCount(0);
  await expect(page.getByText('leave.leave_type')).toHaveCount(0);
});

test('the Setup permission holder sees the field key', async ({ page }) => {
  await open(page, withSetup);
  await page.getByRole('button', { name: 'About Leave Type *' }).focus();
  await expect(page.getByText('leave.leave_type')).toBeVisible();
});

test('the hit area is 24 px on a mouse (44 px on a coarse pointer, by the media rule)', async ({ page }) => {
  await open(page, withoutSetup);
  const box = await page.getByRole('button', { name: 'About Leave Type *' }).boundingBox();
  expect(box?.width).toBe(24);
  expect(box?.height).toBe(24);
});

for (const locale of ['en', 'ar'] as const) {
  test(`${locale}: the popover opens in the same place every time (it flipped on every second open)`, async ({ page }) => {
    // A narrow viewport pushes the icon toward the edge where the popover has to flip.
    await page.setViewportSize({ width: 420, height: 900 });
    await open(page, withoutSetup, locale);
    const icon = page.getByRole('button', { name: /About Leave Type|حول/ }).first();
    const tip = page.getByRole('tooltip').filter({ hasText: /The category of leave|فئة الإجازة|نوع الإجازة/ }).first();
    // Push the icon toward the inline-end edge so the popover has to flip there.
    await icon.evaluate(el => { (el.parentElement as HTMLElement).style.marginInlineStart = '210px'; });
    const placements: number[] = [];
    for (let i = 0; i < 4; i++) {
      await icon.focus();
      await expect(tip).toBeVisible();
      const box = await tip.boundingBox();
      placements.push(Math.round(box!.x));
      expect(box!.x, 'inside the viewport on the left').toBeGreaterThanOrEqual(-1);
      expect(box!.x + box!.width, 'inside the viewport on the right').toBeLessThanOrEqual(421);
      await icon.blur();
      await expect(tip).toBeHidden();
    }
    expect(new Set(placements).size, `placements ${placements.join(', ')}`).toBe(1);
  });
}

for (const locale of ['en', 'ar'] as const) {
  test(`${locale}: on a 320 px screen the long Leave Type explanation stays fully inside the viewport (8 px margin)`, async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 640 });
    await open(page, withoutSetup, locale);
    const icon = page.getByRole('button', { name: /About Leave Type|حول/ }).first();
    const tip = page.getByRole('tooltip').filter({ hasText: /The category of leave|فئة الإجازة|نوع الإجازة/ }).first();
    // Put the icon mid-screen (x about 150-174) where neither edge has 280 px of room, so flipping alone is not enough.
    await icon.evaluate(el => { (el.parentElement as HTMLElement).style.marginInlineStart = '120px'; });
    const iconBox = await icon.boundingBox();
    // Neither a start-anchored nor an end-anchored 280 px popover fits from here: it must be clamped, not just flipped.
    expect(iconBox!.x, 'the icon sits where neither side has room').toBeGreaterThan(40);
    expect(iconBox!.x + iconBox!.width).toBeLessThan(280);
    for (let i = 0; i < 3; i++) {
      await icon.focus();
      await expect(tip).toBeVisible();
      const box = await tip.boundingBox();
      expect(box!.x, `open ${i}: left edge`).toBeGreaterThanOrEqual(7.5);
      expect(box!.x + box!.width, `open ${i}: right edge`).toBeLessThanOrEqual(312.5);
      await icon.blur();
      await expect(tip).toBeHidden();
    }
  });
}
