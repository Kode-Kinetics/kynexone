import { expect, test } from '@playwright/test';
import { ALL_PERMISSIONS, boot, type Persona } from '../setup-consolidation/fixture';

/**
 * Benefits has one home for what each grade receives: Benefits by grade (reached from Setup, Grades). /benefits, the
 * enrolment desk, points there for everyone who can open it, and says so plainly to someone who cannot: never a link
 * that ends on Access Denied, never the editor hidden with nothing in its place.
 */
const reader: Persona = { roles: ['HR Manager'], permissions: [...ALL_PERMISSIONS, 'entitlements.read'] };
const noEntitlements: Persona = { roles: ['HR Manager'], permissions: ALL_PERMISSIONS.filter(p => !p.startsWith('entitlements')) };

test('/benefits links to Benefits by grade for someone who can open it', async ({ page }) => {
  await boot(page, { persona: reader, url: '/benefits' });
  const door = page.locator('[data-benefits-door]');
  await expect(door).toBeVisible({ timeout: 30_000 });
  await expect(door.getByRole('link', { name: 'Open Benefits by grade' })).toHaveAttribute('href', '/benefits/by-grade');
});

test('/benefits tells someone without entitlements.read what they cannot see and who can grant it, with no dead link', async ({ page }) => {
  await boot(page, { persona: noEntitlements, url: '/benefits' });
  const door = page.locator('[data-benefits-door="restricted"]');
  await expect(door).toBeVisible({ timeout: 30_000 });
  await expect(door).toContainText("You can't see Benefits by grade");
  await expect(door).toContainText('HR administrator');
  await expect(page.getByRole('link', { name: 'Open Benefits by grade' })).toHaveCount(0);
});
