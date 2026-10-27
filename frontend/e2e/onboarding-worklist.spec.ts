import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/** Browser Pilot (WP-P3, AC66): the onboarding worklist against a mocked API. */
const item = (id: string, over: Record<string, unknown> = {}) => ({
  taskId: id, employee: { id: 11, code: 'E-011', name: 'Amal Hassan', startDate: '2026-11-08' }, title: `Task ${id}`, titleAr: null, ownerRole: 'Payroll',
  dueDate: '2026-10-01', overdue: true, blocksActivation: false, status: 'Pending', linkSection: null, evidenceRequired: false, evidenceDocumentId: null, ...over,
});

async function boot(page: Page, items: any[], locale: 'en' | 'ar' = 'en') {
  const patches: Array<{ id: string; body: any }> = [];
  const queries: string[] = [];
  await page.addInitScript((l: string) => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', l); }, locale);
  await page.route('**/api/**', async route => {
    const req = route.request(); const url = new URL(req.url()); const p = url.pathname;
    const json = (j: unknown, status = 200) => route.fulfill({ status, json: j });
    if (p === '/api/auth/me') return json({ id: 'u', tenantId: 't', tenantSlug: 'f', fullName: 'Reviewer', roles: ['Payroll Officer'], permissions: ['employees.read'], accountType: 'Single', isGroupScope: true, companies: [] });
    if (p === '/api/features/disabled-keys') return json([]);
    if (p === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (p === '/api/onboarding/worklist') { queries.push(url.search); return json({ total: items.length, items }); }
    const m = p.match(/^\/api\/onboarding\/tasks\/(.+)$/);
    if (m && req.method() === 'PATCH') {
      patches.push({ id: m[1], body: req.postDataJSON() });
      const row = items.find(i => i.taskId === m[1]);
      if (row?.evidenceRequired) return json({ code: 'task_evidence_required', title: 'x', status: 422 }, 422);
      items.splice(items.indexOf(row), 1);
      return json({ taskId: m[1], status: 'Completed' });
    }
    return json(p.includes('/features/') ? [] : { items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto('/people/onboarding-tasks');
  return { patches, queries };
}

test('lists open tasks with an overdue badge in text, and completes one', async ({ page }) => {
  const items = [item('a'), item('b', { dueDate: null, overdue: false, blocksActivation: true, employee: { id: 12, code: 'E-012', name: 'Omar Said', startDate: '2026-12-01' } })];
  const { patches } = await boot(page, items);
  const table = page.getByTestId('worklist-table');
  await expect(table).toBeVisible({ timeout: 30_000 });
  await expect(table.locator('caption')).toBeAttached();
  const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).exclude('nextjs-portal').analyze();
  expect(axe.violations.filter(v => v.impact === 'critical' || v.impact === 'serious').map(v => v.id)).toEqual([]);
  await expect(page.getByTestId('worklist-row-a')).toContainText('Overdue');
  await expect(page.getByTestId('worklist-row-b')).toContainText('Due when the start date is set');
  await expect(page.getByTestId('worklist-row-b')).toContainText('Blocks activation');
  await page.getByTestId('worklist-row-a').getByRole('button', { name: 'Mark complete' }).click();
  await expect(page.getByText('1 tasks completed.')).toBeVisible();
  expect(patches).toEqual([{ id: 'a', body: { status: 'Completed' } }]);
  await expect(page.getByTestId('worklist-row-a')).toHaveCount(0);
});

test('bulk complete asks first, and a task that needs a document is reported, not hidden', async ({ page }) => {
  const items = [item('a'), item('b', { evidenceRequired: true })];
  const { patches } = await boot(page, items);
  await expect(page.getByTestId('worklist-table')).toBeVisible({ timeout: 30_000 });
  await page.getByRole('checkbox', { name: 'Select all tasks' }).check();
  await page.getByRole('button', { name: 'Complete selected' }).click();
  await expect(page.getByRole('dialog')).toContainText('2 tasks will be marked completed.');
  await page.getByRole('dialog').getByRole('button', { name: 'Complete', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: '1 tasks completed' })).toContainText('1 tasks completed, 1 could not be: Upload a document for this task before completing it.');
  expect(patches.map(p => p.id).sort()).toEqual(['a', 'b']);
});

test('filters go to the server and an empty list says so', async ({ page }) => {
  const { queries } = await boot(page, []);
  await expect(page.getByTestId('worklist-empty')).toBeVisible({ timeout: 30_000 });
  await page.getByLabel('Who does it').selectOption('IT');
  await page.getByLabel('Overdue only').check();
  await expect.poll(() => queries.at(-1) ?? '').toContain('ownerRole=IT');
  expect(queries.at(-1)).toContain('overdue=true');
});

test('Arabic at 360 px keeps the table reachable', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 780 });
  await boot(page, [item('a', { titleAr: 'مهمة الرواتب' })], 'ar');
  await expect(page.getByTestId('worklist-table')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('worklist-row-a')).toContainText('مهمة الرواتب');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: process.env.P3_SHOTS ? `${process.env.P3_SHOTS}/p3-worklist-ar-360.png` : undefined });
});

test('screenshot: the worklist in English at 1440 px', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await boot(page, [item('a'), item('b', { ownerRole: 'IT', dueDate: '2026-11-20', overdue: false, status: 'InProgress', blocksActivation: true })]);
  await expect(page.getByTestId('worklist-table')).toBeVisible({ timeout: 30_000 });
  await page.screenshot({ path: process.env.P3_SHOTS ? `${process.env.P3_SHOTS}/p3-worklist-en-1440.png` : undefined });
});
