import { test, expect, Page, APIRequestContext } from '@playwright/test';
import {
  PLATFORM_EMAIL,
  PLATFORM_PASSWORD,
  apiPlatformFreshLogin,
  INTELLIFLOW_SLUG,
  EVOSTEL_SLUG,
} from './helpers';

/**
 * ============================================================================
 * KYNEX ONE QA & SECURITY AUDIT TEST SUITE
 * ============================================================================
 * Comprehensive E2E and code-level verification covering:
 *  1. Visual & Contrast Accessibility Regression (Admin Login Form Fields)
 *  2. Form Validation & Edge Cases (Tenant Provisioning)
 *  3. State Machine Integrity & Persistence (Active/Suspend & Feature Flags)
 *  4. Bulk & Destructive Workflows (Single & Multi-Select Deletions)
 *  5. Platform Owner Protection & Anti-Lockout Security (UI & API layers)
 * ============================================================================
 */

/** Helper: Calculate relative luminance according to WCAG 2.1 specifications */
function getRelativeLuminance(r: number, g: number, b: number): number {
  const [rs, gs, bs] = [r, g, b].map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * rs + 0.7152 * gs + 0.0722 * bs;
}

/** Helper: Calculate contrast ratio between two RGB/RGBA colors */
function getContrastRatio(rgb1: [number, number, number], rgb2: [number, number, number]): number {
  const l1 = getRelativeLuminance(rgb1[0], rgb1[1], rgb1[2]);
  const l2 = getRelativeLuminance(rgb2[0], rgb2[1], rgb2[2]);
  const brightest = Math.max(l1, l2);
  const darkest = Math.min(l1, l2);
  return (brightest + 0.05) / (darkest + 0.05);
}

/** Helper: Parse CSS color string (rgb, rgba, or hex) to [r, g, b] */
function parseCssColor(color: string): [number, number, number] {
  if (color.startsWith('#')) {
    const hex = color.slice(1);
    if (hex.length === 3) {
      return [
        parseInt(hex[0] + hex[0], 16),
        parseInt(hex[1] + hex[1], 16),
        parseInt(hex[2] + hex[2], 16),
      ];
    }
    return [
      parseInt(hex.slice(0, 2), 16),
      parseInt(hex.slice(2, 4), 16),
      parseInt(hex.slice(4, 6), 16),
    ];
  }
  const match = color.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/i);
  if (match) {
    return [parseInt(match[1], 10), parseInt(match[2], 10), parseInt(match[3], 10)];
  }
  return [0, 0, 0];
}

test.describe('1. Visual & Contrast Accessibility Regression', () => {
  test.beforeEach(async ({ page }) => {
    // Clear storage to access unauthenticated login surface
    await page.addInitScript(() => localStorage.removeItem('platform_access_token'));
    await page.goto('/platform/login');
    await page.waitForLoadState('networkidle');
  });

  test('Admin Login: input field text and placeholder meet WCAG AA contrast (≥ 4.5:1)', async ({ page }) => {
    const emailInput = page.locator('#platform-email');
    const passwordInput = page.locator('#platform-password');

    await expect(emailInput).toBeVisible({ timeout: 10_000 });
    await expect(passwordInput).toBeVisible({ timeout: 10_000 });

    // Inspect computed colors in DOM
    const inputStyles = await emailInput.evaluate((el) => {
      const computed = window.getComputedStyle(el);
      // Measure placeholder color if pseudo-element is accessible
      const placeholderComputed = window.getComputedStyle(el, '::placeholder');
      return {
        color: computed.color,
        backgroundColor: computed.backgroundColor,
        borderColor: computed.borderColor,
        placeholderColor: placeholderComputed.color,
      };
    });

    // Also sample container background to verify perceived contrast
    const containerBg = await page.locator('section').nth(1).evaluate((el) => {
      return window.getComputedStyle(el).backgroundColor;
    });

    const textColor = parseCssColor(inputStyles.color);
    const bgColor = parseCssColor(
      inputStyles.backgroundColor === 'rgba(0, 0, 0, 0)' ? containerBg : inputStyles.backgroundColor
    );
    const placeholderColor = parseCssColor(inputStyles.placeholderColor);

    const textContrast = getContrastRatio(textColor, bgColor);
    const placeholderContrast = getContrastRatio(placeholderColor, bgColor);

    // WCAG 2.1 Level AA requires ≥ 4.5:1 for normal text
    expect(
      textContrast,
      `Input text contrast (${textContrast.toFixed(2)}:1) between text ${inputStyles.color} and bg ${inputStyles.backgroundColor} must satisfy WCAG AA (>= 4.5:1)`
    ).toBeGreaterThanOrEqual(4.5);

    // Placeholder contrast check: Flag if placeholder contrast is below standard threshold
    expect(
      placeholderContrast,
      `Input placeholder contrast (${placeholderContrast.toFixed(2)}:1) between ${inputStyles.placeholderColor} and bg ${inputStyles.backgroundColor} must satisfy WCAG AA (>= 4.5:1)`
    ).toBeGreaterThanOrEqual(4.5);
  });

  test('Admin Login: theme synchronization between Tailwind dark class and .pa-input styles', async ({ page }) => {
    // Test dark mode appearance and ensure inputs don't render white backgrounds on dark container
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.evaluate(() => document.documentElement.classList.add('dark'));

    const emailInput = page.locator('#platform-email');
    const bgInfo = await emailInput.evaluate((el) => {
      const computed = window.getComputedStyle(el);
      return {
        bg: computed.backgroundColor,
        color: computed.color,
      };
    });

    const textColor = parseCssColor(bgInfo.color);
    const bgColor = parseCssColor(bgInfo.bg);
    const contrast = getContrastRatio(textColor, bgColor);

    expect(
      contrast,
      `Under dark theme, input text contrast (${contrast.toFixed(2)}:1) must remain >= 4.5:1`
    ).toBeGreaterThanOrEqual(4.5);
  });
});

test.describe('2. Form Validation & Edge Cases (Tenant Creation)', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/platform/tenants');
    await page.waitForLoadState('networkidle');
  });

  test('Incomplete tenant submission: validates required fields and displays inline error indicators', async ({ page }) => {
    const newTenantBtn = page.getByRole('button', { name: /new tenant|\+ provision/i }).first();
    await expect(newTenantBtn).toBeVisible({ timeout: 10_000 });
    await newTenantBtn.click();

    const modal = page.locator('div[role="dialog"]').or(page.locator('.fixed.inset-0'));
    await expect(modal.first()).toBeVisible({ timeout: 5_000 });

    const submitBtn = modal.getByRole('button', { name: /provision tenant|create/i });
    await expect(submitBtn).toBeVisible();

    // Fill partially invalid / edge-case values
    const nameInput = modal.locator('input[placeholder*="Acme"]').first();
    const slugInput = modal.locator('input[placeholder*="acme-corp"]').first();
    const emailInput = modal.locator('input[type="email"]').first();
    const passwordInput = modal.locator('input[type="password"], input[placeholder*="Min"]').first();

    // Intentionally fill invalid short password (8 chars, which matches misleading placeholder 'Min 8 characters'
    // but violates backend requirement of at least 10 characters)
    await nameInput.fill('QA Incomplete Tenant');
    await slugInput.fill('qa-incomplete');
    await emailInput.fill('qa-admin@example.com');
    await passwordInput.fill('Short1!'); // 7-8 chars -> violates backend min 10 chars requirement

    await submitBtn.click();

    // Verify whether an inline or field-level validation message appears
    const inlineError = modal.locator('.text-rose-400, [role="alert"], [aria-invalid="true"]');
    await expect(inlineError.first()).toBeVisible({ timeout: 5_000 });

    // Assert that the input element is highlighted or flagged as invalid
    const isPasswordInvalid = await passwordInput.evaluate((el) => {
      const input = el as HTMLInputElement;
      return (
        !input.validity.valid ||
        input.getAttribute('aria-invalid') === 'true' ||
        input.classList.contains('border-rose-500') ||
        input.classList.contains('border-red-500')
      );
    });

    expect(
      isPasswordInvalid,
      'Field-level error styling or validity rejection must flag the sub-10 character password'
    ).toBe(true);
  });

  test('Tenant creation: invalid slug constraints (uppercase, symbols, or invalid characters)', async ({ page }) => {
    const newTenantBtn = page.getByRole('button', { name: /new tenant|\+ provision/i }).first();
    await newTenantBtn.click();

    const modal = page.locator('div[role="dialog"]').or(page.locator('.fixed.inset-0')).first();
    const slugInput = modal.locator('input[placeholder*="acme-corp"]').first();

    // Attempt to enter invalid slug with capital letters and underscores
    await slugInput.fill('INVALID_SLUG_123!');
    const submitBtn = modal.getByRole('button', { name: /provision tenant|create/i });
    await submitBtn.click();

    // Modal must prevent submission or display validation error
    await expect(modal).toBeVisible();
    const slugValue = await slugInput.inputValue();
    expect(slugValue).not.toMatch(/^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/);
  });
});

test.describe('3. State Machine Integrity & Persistence', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/platform/tenants');
    await page.waitForLoadState('networkidle');
  });

  test('Tenant State Machine: Active vs Suspended button visibility and action mutual exclusion', async ({ page }) => {
    // Check Active tenant rows: "Suspend" should be present; "Reactivate" must NOT be shown
    const activeRows = page.locator('tbody tr').filter({ hasText: /active/i });
    if ((await activeRows.count()) > 0) {
      const firstActiveRow = activeRows.first();
      const suspendBtn = firstActiveRow.getByRole('button', { name: /suspend/i });
      const reactivateBtn = firstActiveRow.getByRole('button', { name: /reactivate/i });

      await expect(suspendBtn).toBeVisible({ timeout: 5_000 });
      await expect(reactivateBtn).toHaveCount(0);
    }

    // Check Suspended tenant rows (or filter to Suspended): "Reactivate" should be present; "Suspend" must NOT be shown
    const filterSelect = page.getByRole('combobox', { name: /filter by status/i });
    await filterSelect.selectOption('Suspended');
    await page.waitForTimeout(300);

    const suspendedRows = page.locator('tbody tr').filter({ hasText: /suspended/i });
    if ((await suspendedRows.count()) > 0) {
      const firstSuspendedRow = suspendedRows.first();
      const reactivateBtn = firstSuspendedRow.getByRole('button', { name: /reactivate/i });
      const suspendBtn = firstSuspendedRow.getByRole('button', { name: /suspend/i });

      await expect(reactivateBtn).toBeVisible({ timeout: 5_000 });
      await expect(suspendBtn).toHaveCount(0);
    }
  });

  test('Feature Flag persistence: toggling feature flag persists to database and survives browser refresh', async ({ page }) => {
    // Navigate to IntelliFlow tenant details
    const tenantRow = page.locator('tbody tr').filter({ hasText: /intelliflow/i }).first();
    await expect(tenantRow).toBeVisible({ timeout: 15_000 });
    await tenantRow.click();

    await page.waitForURL(/\/platform\/tenants\/[0-9a-f-]{36}/, { timeout: 10_000 });
    await page.waitForLoadState('networkidle');

    // Switch to Features tab
    const featuresTab = page.getByRole('button', { name: 'Features', exact: true });
    await expect(featuresTab).toBeVisible({ timeout: 10_000 });
    await featuresTab.click();

    // Locate the first switch
    const switches = page.getByRole('switch');
    await expect(switches.first()).toBeVisible({ timeout: 10_000 });

    const targetSwitch = switches.first();
    const initialState = await targetSwitch.getAttribute('aria-checked');
    const expectedNewState = initialState === 'true' ? 'false' : 'true';

    // Intercept feature flag PUT request
    const putPromise = page.waitForResponse(
      (resp) => resp.url().includes('/features/') && resp.request().method() === 'PUT'
    );

    // Toggle switch
    await targetSwitch.click();
    const putResp = await putPromise;
    expect(putResp.status(), 'PUT feature flag update must succeed with HTTP 200').toBe(200);

    // Verify switch updated in DOM
    await expect(targetSwitch).toHaveAttribute('aria-checked', expectedNewState, { timeout: 5_000 });

    // Hard reload the browser page to test persistence across page loads
    await page.reload({ waitUntil: 'networkidle' });

    // Re-verify on Features tab
    const featuresTabAfterReload = page.getByRole('button', { name: 'Features', exact: true });
    await featuresTabAfterReload.click();

    const switchAfterReload = page.getByRole('switch').first();
    await expect(switchAfterReload).toHaveAttribute('aria-checked', expectedNewState, { timeout: 10_000 });

    // Revert state for test cleanliness
    const revertPromise = page.waitForResponse(
      (resp) => resp.url().includes('/features/') && resp.request().method() === 'PUT'
    );
    await switchAfterReload.click();
    await revertPromise;
  });
});

test.describe('4. Bulk & Destructive Workflows', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/platform/tenants');
    await page.waitForLoadState('networkidle');
  });

  test('Single tenant delete: asserts 200/204 response and catches HTTP 409 disabled defect', async ({ page }) => {
    // Navigate to tenant detail page
    const tenantRow = page.locator('tbody tr').filter({ hasText: /evostel/i }).first();
    await expect(tenantRow).toBeVisible({ timeout: 15_000 });
    await tenantRow.click();

    await page.waitForURL(/\/platform\/tenants\/[0-9a-f-]{36}/, { timeout: 10_000 });

    // Locate delete button
    const deleteBtn = page.getByRole('button', { name: /delete tenant/i });
    await expect(deleteBtn).toBeVisible({ timeout: 10_000 });
    await deleteBtn.click();

    // Confirm dialog
    const confirmModal = page.locator('.fixed.inset-0').filter({ hasText: /delete tenant\?/i });
    await expect(confirmModal).toBeVisible();

    const confirmBtn = confirmModal.getByRole('button', { name: /delete tenant/i });

    // Intercept DELETE request
    const deletePromise = page.waitForResponse(
      (resp) => resp.url().includes('/api/platform/tenants/') && resp.request().method() === 'DELETE'
    );

    await confirmBtn.click();
    const deleteResp = await deletePromise;

    // DEFECT AUDIT ASSERTION:
    // Destructive actions currently return 409 Conflict ('tenant_delete_disabled')
    // A working production system expects 200 OK or 204 NoContent.
    if (deleteResp.status() === 409) {
      const body = await deleteResp.json();
      expect(body.error).toBe('tenant_delete_disabled');
      // Assert error message displayed to user
      await expect(page.getByText(/tenant deletion is temporarily disabled/i)).toBeVisible();
    } else {
      expect([200, 204]).toContain(deleteResp.status());
    }
  });

  test('Bulk delete: multi-select operations assert removal and catch bulk deletion conflict', async ({ page }) => {
    // Select first two tenant checkboxes
    const checkboxes = page.locator('tbody input[type="checkbox"]');
    const count = await checkboxes.count();
    expect(count).toBeGreaterThanOrEqual(1);

    await checkboxes.nth(0).check();

    // Bulk action bar should appear
    const bulkBar = page.locator('.border-sapphire\\/30, div').filter({ hasText: /selected/i });
    await expect(bulkBar.first()).toBeVisible({ timeout: 5_000 });

    const bulkDeleteBtn = page.getByRole('button', { name: /delete/i }).filter({ hasText: /delete/i });
    await expect(bulkDeleteBtn.first()).toBeVisible();
    await bulkDeleteBtn.first().click();

    // Confirm in modal
    const confirmModal = page.locator('.fixed.inset-0').filter({ hasText: /delete.*tenant/i });
    await expect(confirmModal).toBeVisible();

    const confirmBtn = confirmModal.getByRole('button', { name: /confirm/i });

    const bulkDeletePromise = page.waitForResponse(
      (resp) => resp.url().includes('/api/platform/tenants/bulk/delete') && resp.request().method() === 'POST'
    );

    await confirmBtn.click();
    const bulkResp = await bulkDeletePromise;

    // DEFECT AUDIT ASSERTION:
    // Bulk tenant deletion returns 409 Conflict ('bulk_tenant_delete_disabled')
    if (bulkResp.status() === 409) {
      const body = await bulkResp.json();
      expect(body.error).toBe('bulk_tenant_delete_disabled');
      // Assert user-facing failure toast/alert appears
      await expect(page.getByText(/bulk delete failed/i)).toBeVisible();
    } else {
      expect(bulkResp.status()).toBe(200);
    }
  });
});

test.describe('5. Platform Owner Protection & Anti-Lockout Security', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/platform/team');
    await page.waitForLoadState('networkidle');
  });

  test('UI Guardrail: Platform Owner role dropdown and deactivate button are disabled/hidden', async ({ page }) => {
    // Locate the Owner row
    const ownerRow = page.locator('tbody tr').filter({ hasText: /owner/i }).first();
    await expect(ownerRow).toBeVisible({ timeout: 10_000 });

    // 1. Verify role modification control: Owner row must NOT render an editable <select>
    const roleSelect = ownerRow.locator('select');
    await expect(roleSelect).toHaveCount(0);

    // Instead, a static badge should be rendered
    const ownerBadge = ownerRow.locator('span').filter({ hasText: /^Owner$/i });
    await expect(ownerBadge).toBeVisible();

    // 2. Verify deactivation control: Owner row must NOT render a Deactivate button
    const deactivateBtn = ownerRow.getByRole('button', { name: /deactivate/i });
    await expect(deactivateBtn).toHaveCount(0);
  });

  test('API Security Guardrail: Backend rejects Owner deactivation and privilege revocation with HTTP 403', async ({
    request,
  }) => {
    // Obtain platform token
    const token = await apiPlatformFreshLogin(request);

    // List team members to obtain the Platform Owner's ID
    const teamResp = await request.get('/api/platform/team', {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(teamResp.status()).toBe(200);
    const members = await teamResp.json();
    const owner = members.find((m: { role: string }) => m.role === 'Owner');
    expect(owner, 'Platform Owner user must exist in the platform database').toBeDefined();

    // Attempt 1: Revoke active status (Self-lockout / Owner deactivation trap)
    const patchActiveResp = await request.patch(`/api/platform/team/${owner.id}`, {
      headers: { Authorization: `Bearer ${token}` },
      data: { isActive: false },
    });

    // Attempt 2: Demote Owner role to Admin/Support (Privilege stripping)
    const patchRoleResp = await request.patch(`/api/platform/team/${owner.id}`, {
      headers: { Authorization: `Bearer ${token}` },
      data: { role: 'Support' },
    });

    // Attempt 3: Deactivate via DELETE endpoint
    const deleteResp = await request.delete(`/api/platform/team/${owner.id}`, {
      headers: { Authorization: `Bearer ${token}` },
    });

    // DEFECT AUDIT ASSERTION:
    // Security audit expectation: The API MUST return 403 Forbidden to guard Platform Owner privileges.
    // If the API returns 200 OK, flag this critical vulnerability (unprotected role mutation & lockout).
    const isProtected =
      patchActiveResp.status() === 403 &&
      patchRoleResp.status() === 403 &&
      deleteResp.status() === 403;

    expect(
      isProtected,
      `API must reject Owner deactivation / role alteration with HTTP 403 Forbidden. Got PATCH active: ${patchActiveResp.status()}, PATCH role: ${patchRoleResp.status()}, DELETE: ${deleteResp.status()}`
    ).toBe(true);
  });
});
