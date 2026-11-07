import { expect, test } from '@playwright/test';
import { ALL_PERMISSIONS, boot, type Persona } from '../setup-consolidation/fixture';

/**
 * Benefits has one home for what each grade receives: Benefits by grade (reached from Setup, Grades). /benefits, the
 * enrolment desk, points there for everyone who can open it, and says so plainly to someone who cannot: never a link
 * that ends on Access Denied, never the editor hidden with nothing in its place.
 */
const reader: Persona = { roles: ['HR Manager'], permissions: [...ALL_PERMISSIONS, 'entitlements.read'] };
const noEntitlements: Persona = { roles: ['HR Manager'], permissions: ALL_PERMISSIONS.filter(p => !p.startsWith('entitlements')) };
// A Release A tenant with no plan and no enrolment is sent straight to Benefits by grade; the enrolment desk (and its door) is
// for a tenant that still has enrolments made before, so the fixture has one plan.
const plan = { id: 'edu', companyId: 'fixture-company', code: 'EDU', name: 'Child Education', planType: 'Education', classification: 'Discretionary', currency: 'SAR', effectiveFrom: '2026-01-01', effectiveTo: null, requiresEnrollment: true, isActive: true, paymentPolicy: null, policyVersion: 1 };
const mocks = { '/api/compensation/benefits/plans': [plan] };

test('/benefits links to Benefits by grade for someone who can open it', async ({ page }) => {
  await boot(page, { persona: reader, url: '/benefits', mocks });
  const door = page.locator('[data-benefits-door]');
  await expect(door).toBeVisible({ timeout: 30_000 });
  await expect(door.getByRole('link', { name: 'Open Benefits by grade' })).toHaveAttribute('href', '/benefits/by-grade');
});

test('/benefits tells someone without entitlements.read what they cannot see and who can grant it, with no dead link', async ({ page }) => {
  await boot(page, { persona: noEntitlements, url: '/benefits', mocks });
  const door = page.locator('[data-benefits-door="restricted"]');
  await expect(door).toBeVisible({ timeout: 30_000 });
  await expect(door).toContainText("You can't see Benefits by grade");
  await expect(door).toContainText('HR administrator');
  await expect(page.getByRole('link', { name: 'Open Benefits by grade' })).toHaveCount(0);
});

// Benefits by grade exists only for tenants with Release A switched on (the sidebar entry is gated the same way).
for (const [name, persona] of [['a reader', reader], ['someone without entitlements.read', noEntitlements]] as const) {
  test(`a tenant without Release A: /benefits shows no Benefits by grade door at all, for ${name}`, async ({ page }) => {
    await boot(page, { persona, url: '/benefits', disabledKeys: ['release_a'] });
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 30_000 });
    await expect(page.locator('[data-benefits-door]')).toHaveCount(0);
    await expect(page.getByText("You can't see Benefits by grade")).toHaveCount(0);
    await expect(page.locator('a[href="/benefits/by-grade"]')).toHaveCount(0);
  });
}
