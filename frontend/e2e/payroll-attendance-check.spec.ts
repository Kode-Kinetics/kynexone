/**
 * PAYROLL → VALIDATION: the run's attendance check renders from the live API.
 *
 * The check (GET /api/payroll/runs/{id}/attendance-reconciliation) lists the days whose pending attendance
 * deductions no longer match their punches — they block Process with ERR_ATTENDANCE_IMPACT_STALE — and the
 * missing-punch days whose late / early-exit minutes are held. This proves the panel is wired end to end on a
 * production build: the request succeeds for a real run, the panel never shows its load-failure state, and the
 * figures it shows are the ones the API returned.
 *
 * DATA. The IntelliFlow tenant's runs, provisioned by e2e/bootstrap/provision.ts. The fixture world contains no
 * data written by an older attendance rule (the API cannot create it), so a freshly provisioned run has no stale
 * days; held days depend on the provisioned attendance, so the assertion compares the screen with the API.
 */
import { test, expect, Locator, Page } from '@playwright/test';
import { INTELLIFLOW_SLUG, INTELLIFLOW_ADMIN, tenantLogin } from './helpers';

const content = (page: Page): Locator => page.locator('main');

test.describe('Payroll — attendance check on the Validation tab', () => {
  test('the attendance check loads for a run and matches what the API reports', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/payroll', { waitUntil: 'domcontentloaded' });
    await expect(content(page).getByRole('heading', { name: 'Payroll Management' })).toBeVisible({ timeout: 60_000 });

    const tab = content(page).getByRole('tab', { name: 'Validation', exact: true });
    await tab.click();
    await expect(tab).toHaveAttribute('aria-selected', 'true');

    const picker = content(page).locator('select[aria-label="Select payroll run"]');
    const options = picker.locator('option:not([value=""])');
    await expect(options.first(), 'the provisioned tenant must have at least one payroll run').toBeAttached();
    const runId = (await options.first().getAttribute('value'))!;

    const check = page.waitForResponse(r => r.url().includes(`/api/payroll/runs/${runId}/attendance-reconciliation`));
    await picker.selectOption(runId);
    const response = await check;
    expect(response.status(), 'the attendance check must load for a real run').toBe(200);
    const body = await response.json() as {
      consumesAttendance: boolean;
      stale: { dayCount: number; employeeCount: number };
      held: { dayCount: number };
    };

    await expect(content(page).getByTestId('attendance-check-failed')).toHaveCount(0);
    // The panel shows exactly what the API reported: a stale block only when there are stale days, a held block
    // only when there are held days, each naming the API's count.
    const stale = body.consumesAttendance ? body.stale.dayCount : 0;
    const held = body.consumesAttendance ? body.held.dayCount : 0;
    await expect(content(page).getByTestId('attendance-stale')).toHaveCount(stale > 0 ? 1 : 0);
    await expect(content(page).getByTestId('attendance-held')).toHaveCount(held > 0 ? 1 : 0);
    await expect(content(page).getByTestId('attendance-stale').filter({ hasText: String(stale) })).toHaveCount(stale > 0 ? 1 : 0);
    await expect(content(page).getByTestId('attendance-held').filter({ hasText: String(held) })).toHaveCount(held > 0 ? 1 : 0);
  });
});
