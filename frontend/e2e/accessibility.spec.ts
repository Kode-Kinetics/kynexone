import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { INTELLIFLOW_ADMIN, INTELLIFLOW_SLUG, tenantLogin } from './helpers';

async function expectNoSeriousViolations(page: import('@playwright/test').Page, label: string) {
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  await page.waitForTimeout(150);
  const results = await new AxeBuilder({ page }).analyze();
  const violations = results.violations
    .filter((violation) => violation.impact === 'critical' || violation.impact === 'serious')
    .map((violation) => ({
      id: violation.id,
      impact: violation.impact,
      help: violation.help,
      nodes: violation.nodes.map((node) => ({
        target: node.target.join(' '),
        summary: node.failureSummary,
      })),
    }));
  expect(violations, `${label} has serious/critical WCAG violations`).toEqual([]);
}

test('public and platform sign-in surfaces have no serious accessibility violations', async ({ page }) => {
  await page.goto('/login');
  await expectNoSeriousViolations(page, 'tenant login');

  await page.goto('/platform/login');
  await expectNoSeriousViolations(page, 'platform login');
});

test('critical tenant workflows have no serious accessibility violations', async ({ page }) => {
  await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);

  for (const route of ['/dashboard', '/people', '/payroll', '/leave', '/saudi-compliance']) {
    await page.goto(route);
    await page.waitForLoadState('networkidle');
    await expect.poll(async () => (await page.locator('body').innerText()).trim().length).toBeGreaterThan(50);
    await expectNoSeriousViolations(page, route);
  }
});

test('platform dashboard has no serious accessibility violations', async ({ page }) => {
  await page.goto('/platform/dashboard');
  await page.waitForLoadState('networkidle');
  await expectNoSeriousViolations(page, 'platform dashboard');
});
