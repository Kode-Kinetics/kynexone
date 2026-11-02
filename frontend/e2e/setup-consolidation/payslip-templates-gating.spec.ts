import { expect, test } from '@playwright/test';
import { ADMIN, boot, type Persona } from './fixture';

/**
 * Creating a payslip template is `payroll.write` on the API (PayslipTemplatesController POST). A reader (`payroll.read`
 * only, e.g. a Payroll Officer without write) must not be shown the create buttons they could only meet a 403 with.
 */
const READER: Persona = { roles: ['Payroll Officer'], permissions: ['dashboard.read', 'payroll.read'] };
const TEMPLATE = { id: 't-1', name: 'Standard payslip', status: 'active', version: 1, isDefault: true, updatedAtUtc: '2026-10-01T00:00:00Z' };
const mocks = (items: unknown[]) => ({ '/api/payslip-templates': items, '/api/payslip-templates/registry': [] });
const url = '/setup?tab=payslipTemplates';

test('a payroll reader is not offered New Template', async ({ page }) => {
  await boot(page, { persona: READER, url, mocks: mocks([TEMPLATE]) });
  // The template's row (its Edit and Remove buttons repeat the name for screen readers, so a bare text match finds three).
  await expect(page.getByRole('row', { name: /Standard payslip/ })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('button', { name: /New Template/i })).toHaveCount(0);
});

test('a payroll reader is not offered Create Template on an empty list either', async ({ page }) => {
  await boot(page, { persona: READER, url, mocks: mocks([]) });
  await expect(page.getByText('No templates yet')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('button', { name: /Create Template/i })).toHaveCount(0);
});

test('someone with payroll.write still sees New Template', async ({ page }) => {
  await boot(page, { persona: ADMIN, url, mocks: mocks([TEMPLATE]) });
  await expect(page.getByRole('button', { name: /New Template/i })).toBeVisible({ timeout: 30_000 });
});
