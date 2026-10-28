import { expect, test, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

/** Browser Pilot (P3): skipping an onboarding task from the worklist, against a mocked API. */
const item = (id: string, over: Record<string, unknown> = {}) => ({
  taskId: id, employee: { id: 11, code: 'E-011', name: 'Amal Hassan', startDate: '2026-11-08' }, title: `Task ${id}`, titleAr: null, ownerRole: 'Payroll',
  dueDate: '2026-10-01', overdue: false, blocksActivation: false, status: 'Pending', linkSection: null, evidenceRequired: false, evidenceDocumentId: null, ...over,
});

async function boot(page: Page, items: any[], locale: 'en' | 'ar' = 'en', serverRefuses = false) {
  const patches: Array<{ id: string; body: any }> = [];
  await page.addInitScript((l: string) => {
    localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', l);
    document.addEventListener('DOMContentLoaded', () => { const s = document.createElement('style'); s.textContent = 'nextjs-portal{display:none!important}'; document.head.appendChild(s); });
  }, locale);
  await page.route('**/api/**', async route => {
    const req = route.request(); const p = new URL(req.url()).pathname;
    const json = (j: unknown, status = 200) => route.fulfill({ status, json: j });
    if (p === '/api/auth/me') return json({ id: 'u', tenantId: 't', tenantSlug: 'f', fullName: 'Reviewer', roles: ['HR Manager'], permissions: ['employees.read'], accountType: 'Single', isGroupScope: true, companies: [] });
    if (p === '/api/features/disabled-keys') return json([]);
    if (p === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (p === '/api/onboarding/worklist') return json({ total: items.length, items });
    const m = p.match(/^\/api\/onboarding\/tasks\/(.+)$/);
    if (m && req.method() === 'PATCH') {
      const body = req.postDataJSON();
      patches.push({ id: m[1], body });
      const row = items.find(i => i.taskId === m[1]);
      if (serverRefuses || (row.blocksActivation && String(body.note ?? '').trim().length < 5)) return json({ code: 'skip_reason_required', status: 422 }, 422);
      items.splice(items.indexOf(row), 1);
      return json({ taskId: m[1], status: 'Skipped' });
    }
    return json(p.includes('/features/') ? [] : { items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto('/people/onboarding-tasks');
  return { patches };
}

test.describe('English at 1440', () => {
  test.use({ viewport: { width: 1440, height: 1000 } });

  test('a blocking task needs a reason of five characters; the note goes with status Skipped', async ({ page }) => {
    const { patches } = await boot(page, [item('a', { blocksActivation: true }), item('b')]);
    await expect(page.getByTestId('worklist-table')).toBeVisible({ timeout: 30_000 });
    await page.getByTestId('worklist-row-a').getByRole('button', { name: /Skip/ }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('This task blocks activation.');
    const go = dialog.getByRole('button', { name: 'Skip task' });
    await expect(go).toBeDisabled();
    await dialog.getByLabel('Reason (required, at least 5 characters)').fill('abcd');
    await expect(go).toBeDisabled();
    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).exclude('nextjs-portal').analyze();
    expect(axe.violations.filter(v => v.impact === 'critical' || v.impact === 'serious').map(v => v.id)).toEqual([]);
    await dialog.getByLabel('Reason (required, at least 5 characters)').fill('Already on payroll via the old system');
    await go.click();
    await expect(page.getByText('Task skipped.')).toBeVisible();
    expect(patches).toEqual([{ id: 'a', body: { status: 'Skipped', note: 'Already on payroll via the old system' } }]);
    await expect(page.getByTestId('worklist-row-a')).toHaveCount(0);
  });

  test('a non-blocking task can be skipped without a reason, and sends no note', async ({ page }) => {
    const { patches } = await boot(page, [item('b')]);
    await expect(page.getByTestId('worklist-table')).toBeVisible({ timeout: 30_000 });
    await page.getByTestId('worklist-row-b').getByRole('button', { name: /Skip/ }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByLabel('Reason (optional)')).toBeVisible();
    await dialog.getByRole('button', { name: 'Skip task' }).click();
    await expect(page.getByText('Task skipped.')).toBeVisible();
    expect(patches).toEqual([{ id: 'b', body: { status: 'Skipped' } }]);
  });

  test('a 422 skip_reason_required from the server is shown in the dialog, which stays open', async ({ page }) => {
    await boot(page, [item('b')], 'en', true);
    await expect(page.getByTestId('worklist-table')).toBeVisible({ timeout: 30_000 });
    await page.getByTestId('worklist-row-b').getByRole('button', { name: /Skip/ }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Skip task' }).click();
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Give a reason of at least 5 characters for skipping');
    await expect(page.getByRole('dialog')).toBeVisible();
  });
});

test.describe('Arabic at 360', () => {
  test.use({ viewport: { width: 360, height: 780 } });

  test('the card has a Skip button, the dialog is Arabic, and nothing overflows', async ({ page }) => {
    const { patches } = await boot(page, [item('a', { blocksActivation: true, title: 'A very long onboarding task title that must wrap inside a narrow card without overflowing', titleAr: 'مهمة انضمام ذات عنوان طويل جدًا يجب أن يلتف داخل البطاقة الضيقة دون أن يتجاوز الشاشة' })], 'ar');
    await expect(page.getByTestId('worklist-cards')).toBeVisible({ timeout: 30_000 });
    expect(await page.evaluate(() => document.documentElement.dir)).toBe('rtl');
    await page.getByTestId('worklist-row-a').getByRole('button', { name: /تجاوز/ }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('هذه المهمة تمنع التفعيل');
    await expect(dialog.getByRole('button', { name: 'تجاوز المهمة' })).toBeDisabled();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    const box = await dialog.boundingBox();
    expect(box!.x).toBeGreaterThanOrEqual(0); expect(box!.x + box!.width).toBeLessThanOrEqual(360 + 1);
    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).exclude('nextjs-portal').analyze();
    expect(axe.violations.filter(v => v.impact === 'critical' || v.impact === 'serious').map(v => v.id)).toEqual([]);
    await dialog.getByLabel('السبب (مطلوب، 5 أحرف على الأقل)').fill('تم التسجيل مسبقًا');
    await dialog.getByRole('button', { name: 'تجاوز المهمة' }).click();
    await expect(page.getByText('تم تجاوز المهمة.')).toBeVisible();
    expect(patches).toEqual([{ id: 'a', body: { status: 'Skipped', note: 'تم التسجيل مسبقًا' } }]);
  });
});
