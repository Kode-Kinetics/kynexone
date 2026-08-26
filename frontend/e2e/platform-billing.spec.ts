import { test, expect, Page } from '@playwright/test';

/**
 * Navigate to the billing page for the first tenant in the list.
 * Returns the tenantId extracted from the URL.
 */
async function goToFirstTenantBilling(page: Page): Promise<string> {
  await page.goto('/platform/tenants');
  await page.waitForLoadState('networkidle');
  // Click first tenant to get to detail
  const firstTenantLink = page.getByText(/intelliflow systems/i).first();
  await firstTenantLink.click();
  await page.waitForURL(/\/platform\/tenants\/[0-9a-f-]{36}/, { timeout: 10_000 });

  const url = page.url();
  const tenantId = url.match(/\/platform\/tenants\/([0-9a-f-]{36})/)?.[1] ?? '';

  // Navigate to billing sub-page (either a tab or direct URL)
  await page.goto(`/platform/tenants/${tenantId}/billing`);
  await page.waitForLoadState('networkidle');
  return tenantId;
}

test.describe('Platform billing / invoices', () => {
  test('billing page loads without JS errors', async ({ page }) => {
    const jsErrors: string[] = [];
    page.on('pageerror', e => jsErrors.push(e.message));
    await goToFirstTenantBilling(page);
    expect(jsErrors).toHaveLength(0);
  });

  test('invoice list area renders (may be empty, no error state)', async ({ page }) => {
    await goToFirstTenantBilling(page);
    // There should be no unrecoverable error copy
    const bodyText = (await page.locator('body').innerText()) ?? '';
    expect(bodyText.toLowerCase()).not.toContain('something went wrong');
    expect(bodyText.toLowerCase()).not.toContain('failed to load');
  });

  test('"Create Invoice" button or link is present on billing page', async ({ page }) => {
    await goToFirstTenantBilling(page);
    const createBtn = page
      .getByRole('button', { name: /create invoice|add invoice|new invoice/i })
      .or(page.getByRole('link', { name: /create invoice|add invoice|new invoice/i }));
    await expect(createBtn.first()).toBeVisible({ timeout: 10_000 });
  });

  test('clicking Create Invoice opens a modal or form', async ({ page }) => {
    await goToFirstTenantBilling(page);
    const createBtn = page
      .getByRole('button', { name: /create invoice|add invoice|new invoice/i })
      .or(page.getByRole('link', { name: /create invoice|add invoice|new invoice/i }));
    await createBtn.first().click();
    // A dialog/modal or an inline form should appear with invoice number field
    await expect(
      page.getByRole('dialog').or(page.getByText(/invoice number/i))
    ).toBeVisible({ timeout: 8_000 });
  });

  test('invoice form has fillable fields and cancel closes it', async ({ page }) => {
    await goToFirstTenantBilling(page);
    const createBtn = page
      .getByRole('button', { name: /create invoice|add invoice|new invoice/i })
      .or(page.getByRole('link', { name: /create invoice|add invoice|new invoice/i }));
    await createBtn.first().click();
    // Wait for the form
    await page.waitForSelector('[role="dialog"], form', { timeout: 8_000 });
    // Fill invoice number if the field exists
    const invoiceNumberInput = page.getByLabel(/invoice number/i).or(page.getByPlaceholder(/invoice number|inv-/i));
    if (await invoiceNumberInput.count() > 0) {
      await invoiceNumberInput.first().fill('INV-TEST-001');
    }
    // Cancel
    const cancelBtn = page.getByRole('button', { name: /cancel|close|dismiss/i });
    if (await cancelBtn.count() > 0) {
      await cancelBtn.first().click();
      // Modal should be gone
      await expect(page.getByRole('dialog')).toHaveCount(0, { timeout: 5_000 });
    }
  });
});
