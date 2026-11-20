import { expect, test, type Page, type Request } from '@playwright/test';
import fs from 'node:fs';

/**
 * Recruitment walkthrough fixes, region A (requisitions, job openings and their dialogs), against a mocked API: nothing is read or written.
 *   1 phone: every requisition row action and the header action are reachable, and the page does not scroll sideways
 *   2 the three dialogs are real dialogs: named, focus moves in and back, Tab is trapped, Esc closes
 *   3 the author is offered neither Approve nor Reject on their own pending requisition
 *   4 a rejected requisition shows its reason and offers Edit and resubmit (edit, save, send)
 *   5 Close opening, with a confirmation, only for the roles the API allows
 * Run: RECRUITMENT_FIXES_PORT=5178 RECRUITMENT_FIXES_DIST=.next-claude-recfixA npx playwright test -c e2e/playwright.recruitment-fixes.config.ts recruitment-fixes-A
 */

const ME = 'user-me';
const REQUISITIONS = [
  { id: 'rq-1', requisitionNumber: 'MRQ-0001', departmentId: 'dep-1', departmentName: 'Operations', designationId: 'des-1', designationTitle: 'Operations Specialist', headCount: 2, employmentType: 'Full-Time', priority: 'Medium', justification: 'Cover the new shift.', requiredSkills: 'Planning', minExperienceYears: null, maxExperienceYears: null, budgetFrom: null, budgetTo: null, targetJoiningDate: null, status: 'Draft', requestedByName: 'Recruiter Test', requestedByUserId: ME, rejectionReason: '', createdAtUtc: '2026-11-18T08:00:00Z', submittedAtUtc: null, approvedAtUtc: null },
  { id: 'rq-2', requisitionNumber: 'MRQ-0002', departmentId: 'dep-1', departmentName: 'Operations', designationId: 'des-1', designationTitle: 'Warehouse Lead', headCount: 1, employmentType: 'Full-Time', priority: 'High', justification: 'Growth.', requiredSkills: '', minExperienceYears: null, maxExperienceYears: null, budgetFrom: null, budgetTo: null, targetJoiningDate: null, status: 'PendingApproval', requestedByName: 'Recruiter Test', requestedByUserId: ME, rejectionReason: '', createdAtUtc: '2026-11-18T09:00:00Z', submittedAtUtc: '2026-11-18T09:05:00Z', approvedAtUtc: null },
  { id: 'rq-3', requisitionNumber: 'MRQ-0003', departmentId: 'dep-1', departmentName: 'Sales', designationId: 'des-1', designationTitle: 'Account Manager', headCount: 3, employmentType: 'Full-Time', priority: 'Critical', justification: 'New region.', requiredSkills: '', minExperienceYears: null, maxExperienceYears: null, budgetFrom: null, budgetTo: null, targetJoiningDate: null, status: 'Submitted', requestedByName: 'Someone Else', requestedByUserId: 'user-other', rejectionReason: '', createdAtUtc: '2026-11-18T10:00:00Z', submittedAtUtc: '2026-11-18T10:05:00Z', approvedAtUtc: null },
  { id: 'rq-4', requisitionNumber: 'MRQ-0004', departmentId: 'dep-1', departmentName: 'Finance', designationId: 'des-1', designationTitle: 'Accountant', headCount: 1, employmentType: 'Contract', priority: 'Low', justification: 'Year-end close support.', requiredSkills: 'Excel', minExperienceYears: 2, maxExperienceYears: null, budgetFrom: null, budgetTo: null, targetJoiningDate: null, status: 'Rejected', requestedByName: 'Recruiter Test', requestedByUserId: ME, rejectionReason: 'Budget not approved for this quarter', createdAtUtc: '2026-11-17T08:00:00Z', submittedAtUtc: '2026-11-17T08:05:00Z', approvedAtUtc: null },
  { id: 'rq-5', requisitionNumber: 'MRQ-0005', departmentId: 'dep-1', departmentName: 'Sales', designationId: 'des-1', designationTitle: 'Sales Executive', headCount: 1, employmentType: 'Full-Time', priority: 'Medium', justification: 'Backfill.', requiredSkills: '', minExperienceYears: null, maxExperienceYears: null, budgetFrom: null, budgetTo: null, targetJoiningDate: null, status: 'Approved', requestedByName: 'Someone Else', requestedByUserId: 'user-other', rejectionReason: '', createdAtUtc: '2026-11-16T08:00:00Z', submittedAtUtc: '2026-11-16T08:05:00Z', approvedAtUtc: '2026-11-16T09:00:00Z' },
];
const OPENING = { id: 'op-1', jobCode: 'JOB-1', requisitionId: null, title: 'Sales Executive', departmentName: 'Sales', designationTitle: 'Sales Executive', employmentType: 'Full-Time', headCount: 2, filledCount: 0, description: '', requirements: '', responsibilities: '', salaryFrom: null, salaryTo: null, location: 'Riyadh', status: 'Open', assignedHrName: '', createdAtUtc: '2026-11-01T08:00:00Z', activeApplications: 3, remaining: 2 };

interface Boot { locale: 'en' | 'ar'; roles?: string[] }

async function boot(page: Page, opts: Boot) {
  const calls: { method: string; path: string; body: unknown }[] = [];
  const errors: string[] = [];
  let openingStatus = 'Open';
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(locale => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', locale); }, opts.locale);
  await page.route('**/api/**', async route => {
    const request: Request = route.request();
    const path = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (request.method() !== 'GET') calls.push({ method: request.method(), path, body: request.postDataJSON() });
    if (path === '/api/auth/me') return json({ id: ME, employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', fullName: 'Recruiter Test', roles: opts.roles ?? ['Admin'], permissions: ['recruitment.read', 'recruitment.write', 'recruitment.approve', 'employees.read', 'dashboard.read'], companies: [{ id: 'c1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] });
    if (path === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/tenant-admin/localization') return json({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'en', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/notifications') return json([]);
    if (path === '/api/recruitment/offers/placement-options') return json({ departments: [{ id: 'dep-1', name: 'Operations', code: 'OPS' }], designations: [{ id: 'des-1', name: 'Operations Specialist', code: 'OPSP' }], grades: [] });
    if (path === '/api/planning/headcount-check') return json({ hasEstablishment: false });
    if (path === '/api/recruitment/requisitions' && request.method() === 'GET') return json({ items: REQUISITIONS, total: REQUISITIONS.length, page: 1, pageSize: 25 });
    if (/^\/api\/recruitment\/requisitions\/rq-4$/.test(path) && request.method() === 'PUT') return json({ ...REQUISITIONS[3], status: 'Draft', rejectionReason: '' });
    if (/^\/api\/recruitment\/requisitions\/rq-4\/submit$/.test(path)) return json({ ...REQUISITIONS[3], status: 'PendingApproval' });
    if (path === '/api/recruitment/openings' && request.method() === 'GET') return json({ items: [{ ...OPENING, status: openingStatus }], total: 1, page: 1, pageSize: 100 });
    if (path === '/api/recruitment/openings' && request.method() === 'POST') return json({ message: 'This requisition already has an opening.', status: 'Converted' }, 409);
    if (path === '/api/recruitment/openings/op-1/close') { openingStatus = 'Closed'; return json({ ...OPENING, status: 'Closed' }); }
    if (path.startsWith('/api/recruitment/')) return json({ items: [], total: 0, page: 1, pageSize: 20 });
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto('/recruitment');
  return { calls, errors };
}

async function shot(page: Page, name: string, locale: string) {
  const dir = process.env.RECRUITMENT_FIXES_SHOTS;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: `${dir}/A-${name}-${locale}-${page.viewportSize()!.width >= 1000 ? 'desktop' : 'phone'}.png` });
}

// The list renders a card list and a table, one of them hidden: a text query must look at the one on screen.
const tab = (page: Page, name: RegExp) => page.getByRole('button', { name }).first().click();

for (const locale of ['en', 'ar'] as const) {
  const L = locale === 'en'
    ? { requisitions: /^Requisitions$/, openings: /^Job Openings$/, newReq: 'New Requisition', dialog: 'New Manpower Requisition', approve: 'Approve', reject: 'Reject', submit: 'Submit', openJob: 'Open Job', edit: 'Edit and resubmit', saveSend: 'Save and resubmit', reason: 'Budget not approved for this quarter', close: 'Close opening', closeTitle: 'Close this opening?', cancel: 'Cancel', closeX: 'Close', newOpening: 'New Opening', ai: 'AI Tools', aiTitle: 'Recruitment AI Tools', openingDialog: 'New Job Opening', alreadyOpened: 'This requisition already has an opening.' }
    : { requisitions: /^طلبات التوظيف$/, openings: /^الوظائف الشاغرة$/, newReq: 'طلب توظيف جديد', dialog: 'طلب قوى عاملة جديد', approve: 'موافقة', reject: 'رفض', submit: 'إرسال', openJob: 'فتح الوظيفة', edit: 'تعديل وإعادة الإرسال', saveSend: 'حفظ وإعادة الإرسال', reason: 'Budget not approved for this quarter', close: 'إغلاق الوظيفة الشاغرة', closeTitle: 'إغلاق هذه الوظيفة الشاغرة؟', cancel: 'إلغاء', closeX: 'إغلاق', newOpening: 'وظيفة شاغرة جديدة', ai: 'أدوات الذكاء الاصطناعي', aiTitle: 'أدوات الذكاء الاصطناعي للتوظيف', openingDialog: 'وظيفة شاغرة جديدة', alreadyOpened: 'لهذا الطلب وظيفة شاغرة بالفعل.' };
  const phone = (page: Page) => page.viewportSize()!.width < 640;

  test.describe(`recruitment fixes A (${locale})`, () => {
    test('1 every requisition action and the header action are reachable, with no sideways page scroll', async ({ page }) => {
      const { errors } = await boot(page, { locale });
      await tab(page, L.requisitions);
      await expect(page.getByText('MRQ-0003').locator('visible=true').first()).toBeVisible();
      const width = page.viewportSize()!.width;
      const reachable = async (name: string) => {
        const button = page.getByRole('button', { name, exact: true }).first();
        await expect(button).toBeVisible();
        const box = (await button.boundingBox())!;
        expect(box.x, `${name} starts inside the screen`).toBeGreaterThanOrEqual(0);
        expect(box.x + box.width, `${name} ends inside the screen`).toBeLessThanOrEqual(width + 0.5);
      };
      for (const name of [L.newReq, L.submit, L.approve, L.reject, L.openJob, L.edit]) await reachable(name);
      const sideways = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(sideways, 'the page does not scroll sideways').toBeLessThanOrEqual(0);
      if (phone(page)) {
        // A phone gets a card list, not a clipped table.
        await expect(page.getByRole('table')).toHaveCount(0);
      }
      await shot(page, 'requisitions-list', locale);
      expect(errors).toEqual([]);
    });

    test('2 the New Requisition dialog is a dialog: named, focus in and back, Tab trapped, Esc closes', async ({ page }) => {
      const { errors } = await boot(page, { locale });
      await tab(page, L.requisitions);
      const opener = page.getByRole('button', { name: L.newReq, exact: true });
      await opener.focus();
      await opener.click();
      const dialog = page.getByRole('dialog', { name: L.dialog });
      await expect(dialog).toBeVisible();
      await expect(dialog).toHaveAttribute('aria-modal', 'true');
      await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]'))).toBe(true);
      for (let i = 0; i < 40; i++) {
        await page.keyboard.press('Tab');
        expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), `Tab ${i} stays inside`).toBe(true);
      }
      for (let i = 0; i < 6; i++) {
        await page.keyboard.press('Shift+Tab');
        expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), `Shift+Tab ${i} stays inside`).toBe(true);
      }
      await shot(page, 'new-requisition-dialog', locale);
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(opener).toBeFocused();
      expect(errors).toEqual([]);
    });

    test('2b the New Job Opening and AI Tools dialogs are dialogs too', async ({ page }) => {
      const { errors } = await boot(page, { locale });
      await tab(page, L.openings);
      await expect(page.getByText('Sales Executive').first()).toBeVisible();
      const newOpening = page.getByRole('button', { name: L.newOpening, exact: true });
      await newOpening.click();
      const dialog = page.getByRole('dialog', { name: L.openingDialog });
      await expect(dialog).toBeVisible();
      await expect(dialog).toHaveAttribute('aria-modal', 'true');
      for (let i = 0; i < 30; i++) {
        await page.keyboard.press('Tab');
        expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), `Tab ${i} stays inside`).toBe(true);
      }
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(newOpening).toBeFocused();

      const aiButton = page.getByRole('button', { name: L.ai, exact: true });
      await aiButton.click();
      const ai = page.getByRole('dialog', { name: L.aiTitle });
      await expect(ai).toBeVisible();
      await expect(ai).toHaveAttribute('aria-modal', 'true');
      await page.keyboard.press('Escape');
      await expect(ai).toHaveCount(0);
      await expect(aiButton).toBeFocused();
      expect(errors).toEqual([]);
    });

    test('3 the author is not offered Approve or Reject on their own pending requisition; others are', async ({ page }) => {
      await boot(page, { locale });
      await tab(page, L.requisitions);
      await expect(page.getByText('MRQ-0002').locator('visible=true').first()).toBeVisible();
      // rq-2 is pending and mine; rq-3 is pending and someone else's: exactly one Approve and one Reject, both on rq-3.
      await expect(page.getByRole('button', { name: L.approve, exact: true })).toHaveCount(1);
      await expect(page.getByRole('button', { name: L.reject, exact: true })).toHaveCount(1);
      const row = page.getByRole('row').filter({ hasText: 'MRQ-0003' }).or(page.getByRole('listitem').filter({ hasText: 'MRQ-0003' })).first();
      await expect(row.getByRole('button', { name: L.reject, exact: true })).toBeVisible();
      // The author still has Submit on their draft.
      await expect(page.getByRole('button', { name: L.submit, exact: true })).toHaveCount(1);
    });

    test('4 a rejected requisition shows why, and the author can edit it and send it again', async ({ page }) => {
      const { calls, errors } = await boot(page, { locale });
      await tab(page, L.requisitions);
      await expect(page.getByText(L.reason).locator('visible=true').first()).toBeVisible();
      await page.getByText(L.reason).locator('visible=true').first().scrollIntoViewIfNeeded();
      await shot(page, 'rejected-requisition', locale);
      await page.getByRole('button', { name: L.edit, exact: true }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      // The form is filled from the rejected requisition.
      await expect(dialog.locator('textarea').first()).toHaveValue('Year-end close support.');
      await dialog.locator('textarea').first().fill('Year-end close support, budget now confirmed.');
      await shot(page, 'edit-rejected-dialog', locale);
      await dialog.getByRole('button', { name: L.saveSend, exact: true }).click();
      await expect(dialog).toHaveCount(0);
      expect(calls.map(c => `${c.method} ${c.path}`)).toEqual(['PUT /api/recruitment/requisitions/rq-4', 'POST /api/recruitment/requisitions/rq-4/submit']);
      expect((calls[0].body as { justification: string }).justification).toBe('Year-end close support, budget now confirmed.');
      expect(errors).toEqual([]);
    });

    test('5 an Admin can close an opening after a confirmation; the list shows it closed', async ({ page }) => {
      const { calls, errors } = await boot(page, { locale });
      await tab(page, L.openings);
      const close = page.getByRole('button', { name: L.close, exact: true });
      await expect(close).toBeVisible();
      await close.click();
      const confirm = page.getByRole('dialog', { name: L.closeTitle });
      await expect(confirm).toBeVisible();
      await shot(page, 'close-opening-confirm', locale);
      // Cancel changes nothing.
      await confirm.getByRole('button', { name: L.cancel, exact: true }).click();
      expect(calls).toEqual([]);
      await close.click();
      await page.getByRole('dialog', { name: L.closeTitle }).getByRole('button', { name: L.close, exact: true }).click();
      await expect.poll(() => calls.map(c => `${c.method} ${c.path}`)).toEqual(['POST /api/recruitment/openings/op-1/close']);
      await expect(page.getByRole('button', { name: L.close, exact: true })).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    test('6 on a phone every row action is a 44px target with 8px between neighbours; the desktop table stays compact', async ({ page }) => {
      await boot(page, { locale });
      await tab(page, L.requisitions);
      await expect(page.getByText('MRQ-0003').locator('visible=true').first()).toBeVisible();
      const boxes: { x: number; y: number; width: number; height: number }[] = [];
      for (const name of [L.submit, L.approve, L.reject, L.edit, L.openJob]) {
        const b = (await page.getByRole('button', { name, exact: true }).first().boundingBox())!;
        if (phone(page)) expect(b.height, `${name} is at least 44px high`).toBeGreaterThanOrEqual(43.5);
        else expect(b.height, `${name} stays compact on desktop`).toBeLessThan(36);
        boxes.push(b);
      }
      if (phone(page)) {
        // Approve and Reject sit side by side: at least 8px apart.
        const a = (await page.getByRole('button', { name: L.approve, exact: true }).boundingBox())!;
        const r = (await page.getByRole('button', { name: L.reject, exact: true }).boundingBox())!;
        const gap = Math.max(a.x, r.x) - Math.min(a.x + a.width, r.x + r.width);
        expect(gap, 'Approve and Reject are at least 8px apart').toBeGreaterThanOrEqual(7.5);
        await tab(page, L.openings);
        const c = (await page.getByRole('button', { name: L.close, exact: true }).first().boundingBox())!;
        expect(c.height, 'Close opening is at least 44px high').toBeGreaterThanOrEqual(43.5);
      }
    });

    test('7 the requisition dialog starts on its first field, and on a phone the choices stack one per row', async ({ page }) => {
      await boot(page, { locale });
      await tab(page, L.requisitions);
      await page.getByRole('button', { name: L.newReq, exact: true }).click();
      const dialog = page.getByRole('dialog', { name: L.dialog });
      await expect(dialog).toBeVisible();
      // Initial focus is the first field (the department choice), not a button.
      await expect.poll(() => page.evaluate(() => document.activeElement?.id)).toBe('requisition-department');
      const dep = (await dialog.locator('#requisition-department').boundingBox())!;
      const des = (await dialog.locator('#requisition-designation').boundingBox())!;
      const type = (await dialog.getByRole('combobox').nth(3).boundingBox())!;
      if (phone(page)) {
        expect(des.y, 'Designation sits on its own row').toBeGreaterThan(dep.y + dep.height - 1);
        expect(des.width, 'Designation is wide enough to read').toBeGreaterThan(220);
        expect(type.width).toBeGreaterThan(220);
      } else {
        expect(Math.abs(des.y - dep.y), 'three across on desktop').toBeLessThan(2);
      }
      // The trap, Esc and focus return still hold.
      await page.keyboard.press('Shift+Tab');
      expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]'))).toBe(true);
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(page.getByRole('button', { name: L.newReq, exact: true })).toBeFocused();
    });

    test('8 a refusal from the API is shown inline in plain words when an opening is created', async ({ page }) => {
      await boot(page, { locale });
      await tab(page, L.openings);
      await page.getByRole('button', { name: L.newOpening, exact: true }).click();
      const dialog = page.getByRole('dialog', { name: L.openingDialog });
      await dialog.getByRole('textbox').first().fill('Sales Executive');
      await dialog.locator('#opening-department').selectOption('dep-1');
      await dialog.locator('#opening-designation').selectOption('des-1');
      await dialog.getByRole('button', { name: locale === 'en' ? 'Create Opening' : 'إنشاء الوظيفة', exact: true }).click();
      await expect(dialog.getByText(L.alreadyOpened)).toBeVisible();
    });

    test('5b a role the API does not allow to close an opening is not shown the action', async ({ page }) => {
      await boot(page, { locale, roles: ['HR Officer'] });
      await tab(page, L.openings);
      await expect(page.getByText('Sales Executive').first()).toBeVisible();
      await expect(page.getByRole('button', { name: L.close, exact: true })).toHaveCount(0);
    });
  });
}
