import { expect, test } from '@playwright/test';
import { ALL_PERMISSIONS, boot, type Persona } from '../setup-consolidation/fixture';

/** A disabled control says why in text that is always visible and tied to it with aria-describedby (not a hover popover). */
const persona: Persona = { roles: ['Admin'], permissions: [...ALL_PERMISSIONS, 'security.manage'] };

test('People: Add Employee at the plan limit shows its reason as visible text tied to the button', async ({ page }) => {
  await boot(page, {
    persona, url: '/people',
    mocks: {
      '/api/tenant-admin/usage': { activeEmployees: 10, maxEmployees: 10, activeUsers: 1, maxUsers: 5, storageUsedMb: 1, maxStorageMb: 100 },
      '/api/employees': { items: [], total: 0, page: 1, pageSize: 25 },
    },
  });
  const button = page.getByRole('button', { name: 'Add Employee' }).first();
  await expect(button).toBeDisabled({ timeout: 30_000 });
  const reason = page.locator('#employee-limit-reason');
  await expect(reason).toBeVisible();
  await expect(reason).toContainText('Employee limit reached (10/10)');
  await expect(button).toHaveAttribute('aria-describedby', 'employee-limit-reason');
});
