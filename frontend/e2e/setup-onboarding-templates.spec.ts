import { expect, test, type Page } from '@playwright/test';
import { STARTER_TASKS, bootSetup as boot } from './onboarding/p3Mocks';

/**
 * Browser Pilot (WP-P3): Setup > Onboarding checklist templates and Hire templates, against an intercepted API that keeps
 * its own state. Local: next dev -p <port>, EMPLOYEE_HIRE_BASE_URL=http://127.0.0.1:<port>, playwright test -c e2e/playwright.onboarding-p3.config.ts.
 */
const open = (page: Page, opts?: Parameters<typeof boot>[2]) => boot(page, '/setup?tab=onboardingChecklistTemplates', opts);
const heading = (page: Page) => page.locator('#setup-section-heading');

test.describe('checklist templates', () => {
  test('starter checklist: added for the company, listed, and nothing blocks activation', async ({ page }) => {
    const { st, errors } = await open(page);
    await expect(heading(page)).toHaveText('Onboarding checklist templates', { timeout: 30_000 });
    await expect(page.getByText('No checklists for this company yet.')).toBeVisible();
    await page.getByRole('button', { name: 'Add the Saudi starter checklist' }).click();
    await expect(page.getByTestId('checklist-Saudi Arabia starter checklist')).toContainText('3 tasks');
    await expect(page.getByRole('status').filter({ hasText: 'Added the Saudi starter checklist with 3 tasks' })).toBeVisible();
    expect(st.writes.map(w => `${w.method} ${w.path}`)).toContain('POST /api/onboarding/checklist-templates/starters/ksa/copy-to/co-1');
    expect(errors).toEqual([]);
  });

  test('create: reorder with buttons, preview for a Saudi and an expatriate hire, and a task blocks activation only when chosen', async ({ page }) => {
    const { st } = await open(page);
    await expect(heading(page)).toHaveText('Onboarding checklist templates', { timeout: 30_000 });
    await page.getByRole('button', { name: 'New checklist' }).click();
    const editor = page.getByTestId('checklist-editor');
    await editor.getByLabel('Name', { exact: false }).first().fill('Branch hires');
    await editor.getByTestId('task-row-0').getByLabel('Task', { exact: true }).fill('Open the mailbox');
    await editor.getByRole('button', { name: 'Add a task' }).click();
    const second = editor.getByTestId('task-row-1');
    await second.getByLabel('Task', { exact: true }).fill('Issue the Iqama');
    await second.getByLabel('Applies to').selectOption('expat');
    await second.getByLabel('Due (days from the joining date)').fill('30');
    await expect(second.getByRole('checkbox', { name: /Blocks activation/ })).not.toBeChecked();
    // Reorder without dragging.
    await second.getByRole('button', { name: 'Move task 2 up' }).click();
    await expect(editor.getByTestId('task-row-0').getByLabel('Task', { exact: true })).toHaveValue('Issue the Iqama');
    await expect(page.getByText('Moved task 2 up.')).toBeAttached();
    await expect(editor.getByTestId('task-row-0').getByRole('button', { name: 'Move task 1 up' })).toBeDisabled();
    // Preview.
    const preview = editor.getByTestId('checklist-preview');
    await preview.getByLabel('Joining date').fill('2026-11-08');
    await preview.getByLabel('For a').selectOption('saudi');
    await expect(preview).toContainText('1 task for this hire.');
    await expect(preview).not.toContainText('Issue the Iqama');
    await preview.getByLabel('For a').selectOption('expat');
    await expect(preview).toContainText('2 tasks for this hire.');
    await expect(preview).toContainText('2026-12-08');
    await editor.getByRole('button', { name: 'Save checklist' }).click();
    await expect(page.getByTestId('checklist-Branch hires')).toContainText('2 tasks');
    const post = st.writes.find(w => w.method === 'POST' && w.path === '/api/onboarding/checklist-templates')!;
    expect(post.body.companyId).toBe('co-1');
    expect(post.body.tasks.map((t: any) => t.title)).toEqual(['Issue the Iqama', 'Open the mailbox']);
    expect(post.body.tasks.every((t: any) => t.blocksActivation === false)).toBe(true);
  });

  test('a blank name and a repeated task title are refused before anything is sent', async ({ page }) => {
    const { st } = await open(page);
    await expect(heading(page)).toHaveText('Onboarding checklist templates', { timeout: 30_000 });
    await page.getByRole('button', { name: 'New checklist' }).click();
    const editor = page.getByTestId('checklist-editor');
    await editor.getByRole('button', { name: 'Add a task' }).click();
    await editor.getByTestId('task-row-0').getByLabel('Task', { exact: true }).fill('Same');
    await editor.getByTestId('task-row-1').getByLabel('Task', { exact: true }).fill('Same');
    await editor.getByRole('button', { name: 'Save checklist' }).click();
    await expect(editor.getByText('Enter a name.')).toBeVisible();
    await expect(editor.getByText('Each task needs its own title.')).toBeVisible();
    await expect(editor.getByTestId('task-row-1').getByLabel('Task', { exact: true })).toHaveAttribute('aria-invalid', 'true');
    expect(st.writes.filter(w => w.method === 'POST')).toHaveLength(0);
  });

  test('copy to a sister company, then remove (turned off when used) and restore', async ({ page }) => {
    const seed = [{ id: 'cl-a', companyId: 'co-1', name: 'Used list', nameAr: null, isDefault: false, isActive: true, isDeleted: false, used: 3, tasks: STARTER_TASKS }];
    const { st } = await open(page, { seedChecklists: seed });
    await expect(page.getByTestId('checklist-Used list')).toBeVisible({ timeout: 30_000 });
    await page.locator('#copy-cl-a').selectOption('co-2');
    await page.getByTestId('checklist-Used list').getByRole('button', { name: 'Copy', exact: true }).click();
    await expect(page.getByText('Copied to the other company.')).toBeVisible();
    expect(st.checklists.filter(c => c.companyId === 'co-2')).toHaveLength(1);
    expect(st.checklists.find(c => c.companyId === 'co-2').id).not.toBe('cl-a');
    await page.getByTestId('checklist-Used list').getByRole('button', { name: 'Remove', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Remove', exact: true }).click();
    await expect(page.getByText('Turned off, not deleted: 3 items already use it.')).toBeVisible();
    await page.getByLabel('Show removed checklists').check();
    await expect(page.getByTestId('checklist-Used list')).toContainText('Turned off');
    await page.getByTestId('checklist-Used list').getByRole('button', { name: 'Restore' }).click();
    await expect(page.getByText('Restored.')).toBeVisible();
  });

  test('Arabic at 360 px: mirrored, no horizontal scroll', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 780 });
    const seed = [{ id: 'cl-a', companyId: 'co-1', name: 'Branch hires', nameAr: 'تعيينات الفروع', isDefault: true, isActive: true, isDeleted: false, tasks: STARTER_TASKS }];
    await open(page, { locale: 'ar', seedChecklists: seed });
    await expect(page.getByTestId('checklist-list')).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('checklist-list')).toContainText('تعيينات الفروع');
    await page.getByRole('button', { name: 'تعديل' }).first().click();
    await expect(page.getByTestId('checklist-editor')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true);
    await page.waitForTimeout(900);
    await page.screenshot({ path: process.env.P3_SHOTS ? `${process.env.P3_SHOTS}/p3-checklist-templates-ar-360.png` : undefined });
  });

  test('screenshot: the editor in English at 1440 px', async ({ page }) => {
    const seed = [{ id: 'cl-a', companyId: 'co-1', name: 'Branch hires', nameAr: null, isDefault: true, isActive: true, isDeleted: false, tasks: STARTER_TASKS }];
    await open(page, { seedChecklists: seed });
    await expect(page.getByTestId('checklist-list')).toBeVisible({ timeout: 30_000 });
    await page.getByRole('button', { name: 'Edit', exact: true }).first().click();
    await expect(page.getByTestId('checklist-editor')).toBeVisible();
    await page.waitForTimeout(900);
    await page.screenshot({ path: process.env.P3_SHOTS ? `${process.env.P3_SHOTS}/p3-checklist-templates-en-1440.png` : undefined });
  });
});

test.describe('hire templates', () => {
  test('create with match keys and defaults, then remove and restore', async ({ page }) => {
    const { st } = await boot(page, '/setup?tab=hireTemplates');
    await expect(heading(page)).toHaveText('Hire templates', { timeout: 30_000 });
    await page.getByRole('button', { name: 'New hire template' }).click();
    const editor = page.getByTestId('hire-template-editor');
    await editor.getByRole('button', { name: 'Save hire template' }).click();
    await expect(editor.getByText('Enter a name.')).toBeVisible();
    await editor.getByLabel('Name', { exact: false }).first().fill('Branch cashier');
    await editor.getByLabel('Designation').selectOption('ds1');
    await editor.getByLabel('Probation (days)').fill('90');
    await editor.getByLabel('Work location').fill('Riyadh branch');
    await editor.getByRole('checkbox', { name: 'Identity' }).or(editor.getByRole('checkbox').first()).first().check();
    await editor.getByRole('button', { name: 'Save hire template' }).click();
    await expect(page.getByTestId('hire-template-Branch cashier')).toContainText('Matches: Cashier');
    const post = st.writes.find(w => w.method === 'POST' && w.path === '/api/onboarding/hire-templates')!;
    expect(post.body).toMatchObject({ companyId: 'co-1', matchDesignationId: 'ds1', defaults: { probationDays: 90, workLocation: 'Riyadh branch' } });
    await page.getByTestId('hire-template-Branch cashier').getByRole('button', { name: 'Remove' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Remove', exact: true }).click();
    await expect(page.getByText('Removed.')).toBeVisible();
    await page.getByLabel('Show removed hire templates').check();
    await page.getByTestId('hire-template-Branch cashier').getByRole('button', { name: 'Restore' }).click();
    await expect(page.getByText('Restored.')).toBeVisible();
  });

  test('a probation period over 180 days is refused before sending', async ({ page }) => {
    const { st } = await boot(page, '/setup?tab=hireTemplates');
    await expect(heading(page)).toHaveText('Hire templates', { timeout: 30_000 });
    await page.getByRole('button', { name: 'New hire template' }).click();
    const editor = page.getByTestId('hire-template-editor');
    await editor.getByLabel('Name', { exact: false }).first().fill('Long probation');
    await editor.getByLabel('Probation (days)').fill('400');
    await editor.getByRole('button', { name: 'Save hire template' }).click();
    await expect(editor.getByText('Enter a number of days from 0 to 180.')).toBeVisible();
    expect(st.writes.filter(w => w.method === 'POST')).toHaveLength(0);
  });
});
