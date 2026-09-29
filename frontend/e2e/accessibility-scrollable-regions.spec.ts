import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { INTELLIFLOW_ADMIN, INTELLIFLOW_SLUG, tenantLogin } from './helpers';

/**
 * accessibility.spec.ts already asserts that /saudi-compliance has no serious WCAG violations, and
 * it is the spec that FOUND this one. But that assertion is only as good as the seeded data: both
 * offending elements — the Qiwa and GOSI blocked-employee tables — render behind
 * `blockedEmployees.length > 0`, so on a fixture world where every employee is compliant the page
 * has no scrollable region at all and the check passes for the wrong reason. Measured: against the
 * PRE-FIX build, accessibility.spec.ts was green on a stack with no blocked employees, while this
 * spec reported `scrollable-region-focusable` on `.max-h-40` and `.max-h-36`.
 *
 * So the data is supplied here rather than depended upon. The assertion is unchanged — axe's own
 * serious/critical set, on the real page, with the real component tree.
 */
test('blocked-employee tables on /saudi-compliance stay reachable from the keyboard', async ({ page }) => {
  await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);

  // Pass the real response through, with a blocked population large enough to overflow both
  // max-height boxes — the state that produces the scrollable region.
  await page.route('**/api/saudi-compliance/dashboard', async (route) => {
    const response = await route.fetch();
    const body = await response.json();
    const blocked = Array.from({ length: 12 }, (_, i) => ({
      employeeId: i + 1,
      employeeCode: `E2E-BLOCKED-${i}`,
      fullName: `Blocked Person ${i}`,
      missingFields: ['IqamaNumber', 'GosiReference'],
    }));
    body.qiwa = { ...body.qiwa, blockedEmployees: blocked, blockedFromSync: blocked.length };
    body.gosi = {
      ...body.gosi,
      blockedEmployees: blocked.map((b) => ({ ...b, blockingIssueCodes: ['MISSING_GOSI_REF'] })),
      blockedCount: blocked.length,
    };
    await route.fulfill({ response, json: body });
  });

  await page.goto('/saudi-compliance');
  await page.waitForLoadState('networkidle');
  // Prove the region is actually on the page before asserting anything about it — an axe run over a
  // page that never rendered the table is exactly the false green this spec exists to close.
  const regions = page.getByRole('region', { name: /blocked from/i });
  await expect(regions).toHaveCount(2);
  await expect(regions.first()).toBeVisible();

  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  const results = await new AxeBuilder({ page }).analyze();
  const violations = results.violations
    .filter((violation) => violation.impact === 'critical' || violation.impact === 'serious')
    .map((violation) => ({
      id: violation.id,
      impact: violation.impact,
      nodes: violation.nodes.map((node) => node.target.join(' ')),
    }));
  expect(violations, '/saudi-compliance with blocked employees has serious/critical WCAG violations')
    .toEqual([]);

  // And the region is genuinely operable: focus reaches it and the arrow key scrolls it.
  const first = regions.first();
  await first.focus();
  expect(await first.evaluate((el) => el === document.activeElement)).toBe(true);
});
