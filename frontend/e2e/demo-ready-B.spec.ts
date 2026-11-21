import { expect, test, type Page, type Request } from '@playwright/test';
import fs from 'node:fs';

/**
 * Demo-readiness polish, stream B (Recruitment), against a mocked API: nothing is read or written.
 *   1 phone: the section menu is one scrollable tab strip (role tablist/tab), the active tab is in view, keyboard works
 *   2 phone: Overview KPI labels are shown in full (no ellipsis)
 *   3 Interview mode uses a line icon, not an emoji
 *   4 Draft requisition: 'Submit for approval' (not a bare verb) and 'Edit' (save only, no submit); 44 px phone targets
 *   5 Manager screen: the feedback chip is a 44 px phone target
 *   6 Onboarding tab with no checklists says why and what to do next
 *   7 Requisition 'target join date' is the shared locale date control, not the browser's mm/dd/yyyy input
 *   8 Offer header does not show the internal concurrency 'Version'
 * Run: DEMO_READY_PORT=5184 DEMO_READY_DIST=.next-claude-demoreadyB npx playwright test -c e2e/playwright.demo-ready.config.ts demo-ready-B
 */

const ME = 'user-me';
const ISO = '2026-11-19T08:00:00Z';
const FUTURE = '2031-01-15T09:00:00Z';
const HR_PERMS = ['recruitment.read', 'recruitment.write', 'recruitment.approve', 'employees.read', 'dashboard.read', 'setup.read'];
const MANAGER_PERMS = ['recruitment.requisition.raise', 'recruitment.interview.feedback', 'dashboard.read'];

const req = (over: Record<string, unknown>) => ({ id: 'rq-1', requisitionNumber: 'MRQ-0001', departmentId: 'dep-1', departmentName: 'Operations', designationId: 'des-1', designationTitle: 'Operations Specialist', headCount: 2, employmentType: 'Full-Time', priority: 'Medium', justification: 'Cover the new shift.', requiredSkills: 'Planning', minExperienceYears: null, maxExperienceYears: null, budgetFrom: null, budgetTo: null, targetJoiningDate: null, status: 'Draft', requestedByName: 'Recruiter Test', requestedByUserId: ME, rejectionReason: '', createdAtUtc: ISO, submittedAtUtc: null, approvedAtUtc: null, ...over });
const REQUISITIONS = [req({}), req({ id: 'rq-4', requisitionNumber: 'MRQ-0004', designationTitle: 'Accountant', status: 'Rejected', rejectionReason: 'Budget not approved' })];
const INTERVIEW = { id: 'iv-1', applicationId: 'app-1', candidateName: 'Alice Almutairi', jobTitle: 'Operations Specialist', interviewType: 'Technical', interviewerNames: 'Dairy HR Manager', scheduledAt: FUTURE, durationMinutes: 60, mode: 'Video', meetingLink: '', location: '', status: 'Scheduled', overallRating: null, recommendation: '', feedbackNotes: '', completedAt: null, createdAtUtc: ISO };
const FEEDBACK = [{ id: 'fb-1', interviewScheduleId: 'iv-1', applicationId: 'app-1', interviewerName: 'Me', interviewerRole: '', communicationScore: 0, technicalScore: 0, cultureFitScore: 0, problemSolvingScore: 0, leadershipScore: 0, overallScore: 4, strengths: '', concerns: '', notes: 'Strong', recommendation: 'Proceed', submittedAtUtc: ISO }];
const offerRow = { id: 'offer-1', applicationId: 'app-1', companyId: 'c1', seriesId: 's1', revisionNumber: 1, version: 7, candidateName: 'Alice Almutairi', position: 'Operations Specialist', status: 'Draft', communicationOutcome: 'NotIssued', originalProvenance: 'Frozen', generatedAtUtc: ISO, responseDeadline: FUTURE };
const offerBody = { ...offerRow, offeredJobTitle: 'Operations Specialist', offeredDepartment: 'Operations', startDate: '2031-01-15', basicSalary: 7000, housingAllowance: 0, transportAllowance: 0, otherAllowances: 0, grossSalary: 7000, probationMonths: 3, sentAtUtc: null, acceptedAtUtc: null, recipientEmail: 'alice@example.invalid', frozenAtUtc: ISO };
const capabilities = Object.fromEntries(['editDraft', 'revise', 'freeze', 'withdraw', 'recordResponse', 'issue', 'downloadOriginal'].map(n => [n, { allowed: n === 'revise' || n === 'withdraw', reason: 'blocked' }]));

interface Boot { locale: 'en' | 'ar'; url?: string; roles?: string[]; permissions?: string[]; checklists?: unknown[]; interviews?: unknown[]; mine?: unknown[] }

async function boot(page: Page, o: Boot) {
  const calls: { method: string; path: string; body: unknown }[] = [];
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(locale => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', locale); }, o.locale);
  await page.route('**/api/**', async route => {
    const request: Request = route.request();
    const path = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (request.method() !== 'GET') calls.push({ method: request.method(), path, body: request.postDataJSON() });
    if (path === '/api/auth/me') return json({ id: ME, employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', fullName: 'Recruiter Test', roles: o.roles ?? ['Admin'], permissions: o.permissions ?? HR_PERMS, companies: [{ id: 'c1', name: 'Acme Arabia', code: 'ACME', countryCode: 'SA', isActive: true }] });
    if (path === '/api/auth/mfa/status') return json({ promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/tenant-admin/localization') return json({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: o.locale, defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/notifications') return json([]);
    if (path === '/api/recruitment/offers/placement-options') return json({ departments: [{ id: 'dep-1', name: 'Operations', code: 'OPS' }], designations: [{ id: 'des-1', name: 'Operations Specialist', code: 'OPSP' }], grades: [] });
    if (path === '/api/planning/headcount-check') return json({ hasEstablishment: false });
    if (path === '/api/recruitment/openings/stats') return json({ openPositions: 4, activeApplications: 12, offersPending: 2, hiredThisMonth: 1 });
    if (path === '/api/recruitment/requisitions/stats') return json({ draft: 1, pending: 0, approved: 0, converted: 0 });
    if (path === '/api/recruitment/requisitions' && request.method() === 'GET') return json({ items: REQUISITIONS, total: REQUISITIONS.length, page: 1, pageSize: 25 });
    if (path === '/api/recruitment/requisitions/rq-1' && request.method() === 'PUT') return json({ ...REQUISITIONS[0] });
    if (path === '/api/recruitment/requisitions/rq-1/submit') return json({ ...REQUISITIONS[0], status: 'PendingApproval' });
    if (path === '/api/recruitment/requisitions/my-departments') return json({ items: [{ id: 'dep-1', name: 'Sales' }] });
    if (path === '/api/recruitment/requisitions/mine') return json({ items: [], total: 0 });
    if (path === '/api/recruitment/onboarding/checklists') return json(o.checklists ?? []);
    if (path === '/api/recruitment/onboarding/summary') return json({ total: 0, pending: 0, inProgress: 0, completed: 0, blocked: 0, mandatory: 0, mandatoryCompleted: 0, completionPct: 0 });
    if (path.endsWith('/ai-insights') || path.includes('/insights')) return json({ generatedAt: ISO, insights: [] });
    if (path === '/api/recruitment/onboarding/tasks') return json({ total: 0, items: [] });
    if (path === '/api/recruitment/interviews/mine') return json({ total: (o.mine ?? []).length, items: o.mine ?? [] });
    if (path === '/api/recruitment/interviews/iv-1/feedback') return json(FEEDBACK);
    if (path === '/api/recruitment/interviews/iv-1') return json({ interview: INTERVIEW, feedbacks: FEEDBACK });
    if (path === '/api/recruitment/interviews') return json({ items: o.interviews ?? [], total: (o.interviews ?? []).length, page: 1, pageSize: 20 });
    if (path === '/api/recruitment/offers/offer-1/workspace') return json({ offer: offerBody, artifact: { id: 'art-1', sha256: 'abc', sizeBytes: 1000, provenance: 'Frozen', frozenAtUtc: ISO, offerVersion: 7 }, originalProvenance: 'Frozen', approvals: [], capabilities, revisions: [{ ...offerRow }], changedFields: [], communicationOutcome: 'NotIssued', compensationSnapshot: null, designSnapshot: null });
    if (path === '/api/recruitment/offers/offer-1/events') return json({ items: [], total: 0, page: 1, pageSize: 20 });
    if (path === '/api/recruitment/offers/offer-1/approver-options') return json([]);
    if (path === '/api/recruitment/offers/offer-1') return json({ offer: offerBody, approvals: [], approval: { required: true, isAuthor: true, canSend: false, sendBlockedReason: null, myPendingStepId: null } });
    if (path === '/api/recruitment/offers/mail-readiness') return json({ configured: true, mode: 'Live', source: 'Tenant', fromAddress: 'hr@example.invalid', fromName: 'HR', canManageSettings: false, blockedReason: null, configurationFingerprint: 'fp-1' });
    if (path.startsWith('/api/recruitment/')) return json({ items: [], total: 0, page: 1, pageSize: 20 });
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(o.url ?? '/recruitment');
  return { calls, errors };
}

async function shot(page: Page, name: string, locale: string) {
  const dir = process.env.DEMO_READY_SHOTS_DIR;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: `${dir}/B-${name}-${locale}-${page.viewportSize()!.width >= 1000 ? 'desktop' : 'phone'}.png` });
}

const isPhone = (page: Page) => page.viewportSize()!.width < 500;
const tabId = (id: string) => `#recruitment-tab-${id}`;
const box = async (page: Page, selector: string) => (await page.locator(selector).first().boundingBox())!;

const L = {
  en: { sections: 'Recruitment sections', submit: 'Submit for approval', edit: 'Edit', saveChanges: 'Save changes', emptyTitle: 'Onboarding checklists are not set up', settings: 'Onboarding settings', addFeedback: 'Add more feedback', newReq: 'New Requisition' },
  ar: { sections: 'أقسام التوظيف', submit: 'إرسال للموافقة', edit: 'تعديل', saveChanges: 'حفظ التعديلات', emptyTitle: 'قوائم مهام التهيئة غير مُعدّة بعد', settings: 'إعدادات التهيئة', addFeedback: 'إضافة ملاحظات أخرى', newReq: 'طلب توظيف جديد' },
} as const;

for (const locale of ['en', 'ar'] as const) {
  const l = L[locale];
  test.describe(`demo-ready B (${locale})`, () => {
    test('1 section menu is one scrollable tab strip with the active tab in view, keyboard operable', async ({ page }) => {
      const { errors } = await boot(page, { locale, url: '/recruitment?tab=offers' });
      const list = page.getByRole('tablist', { name: l.sections });
      await expect(list).toBeVisible();
      await expect(list.getByRole('tab')).toHaveCount(11);
      await page.locator(tabId('requisitions')).click();
      await expect(page.locator(tabId('requisitions'))).toHaveAttribute('aria-selected', 'true');
      if (isPhone(page)) {
        const strip = await box(page, '[data-roving-tabs="recruitment"]');
        expect(strip.height).toBeLessThan(80);
        // The primary action is in the first screen, well above the old 60% line.
        const primary = await page.getByRole('button', { name: l.newReq }).first().boundingBox();
        expect(primary!.y).toBeLessThan(330);
        // Select the last tab by keyboard: it must scroll into the strip's visible bounds.
        await page.locator(tabId('requisitions')).focus();
        await page.keyboard.press('End');
        await expect(page.locator(tabId('ai'))).toHaveAttribute('aria-selected', 'true');
        const s = await box(page, '[data-roving-tabs="recruitment"]');
        const a = await box(page, tabId('ai'));
        expect(a.x).toBeGreaterThanOrEqual(s.x - 1);
        expect(a.x + a.width).toBeLessThanOrEqual(s.x + s.width + 1);
        expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(1);
      } else {
        await page.locator(tabId('requisitions')).focus();
        await page.keyboard.press('Home');
        await expect(page.locator(tabId('overview'))).toHaveAttribute('aria-selected', 'true');
      }
      await expect(page.getByRole('tabpanel')).toBeVisible();
      await page.locator(tabId('requisitions')).click();
      await shot(page, 'requisitions', locale);
      expect(errors).toEqual([]);
    });

    test('2 overview KPI labels are not truncated', async ({ page }) => {
      await boot(page, { locale });
      const labels = page.locator('p.text-xs.text-slate-500').filter({ hasText: /\S/ });
      await expect(page.getByRole('tabpanel')).toBeVisible();
      await expect.poll(async () => labels.count()).toBeGreaterThanOrEqual(4);
      const clipped = await page.locator('.surface.flex.items-center').evaluateAll(cards => cards.slice(0, 4).map(card => {
        const p = card.querySelectorAll('p')[1] as HTMLElement;
        return { text: p.textContent, ellipsis: getComputedStyle(p).textOverflow === 'ellipsis', cut: p.scrollWidth > p.clientWidth + 1 };
      }));
      expect(clipped).toHaveLength(4);
      for (const c of clipped) { expect(c.ellipsis, c.text ?? '').toBe(false); expect(c.cut, c.text ?? '').toBe(false); }
      await shot(page, 'overview', locale);
    });

    test('3 interview mode is a line icon, not an emoji', async ({ page }) => {
      await boot(page, { locale, interviews: [INTERVIEW] });
      await page.locator(tabId('interviews')).click();
      const row = page.getByText('Alice Almutairi').first();
      await expect(row).toBeVisible();
      const card = page.getByRole('tabpanel');
      await expect(card.locator('[data-mode-icon="Video"] svg')).toHaveCount(1);
      expect(await card.innerText()).not.toMatch(/\p{Extended_Pictographic}/u);
      await shot(page, 'interviews', locale);
    });

    test('4 draft requisition: Submit for approval, Edit saves without submitting, 44 px targets', async ({ page }) => {
      const { calls } = await boot(page, { locale });
      await page.locator(tabId('requisitions')).click();
      const visible = (n: string) => page.locator(isPhone(page) ? 'ul li' : 'tbody tr').filter({ hasText: 'MRQ-0001' }).getByRole('button', { name: n, exact: true });
      await expect(visible(l.submit)).toBeVisible();
      await expect(page.locator(isPhone(page) ? 'ul li' : 'tbody tr').filter({ hasText: 'MRQ-0001' }).getByRole('button', { name: /^(Submit|إرسال)$/ })).toHaveCount(0);
      if (isPhone(page)) {
        for (const n of [l.submit, l.edit]) expect((await visible(n).boundingBox())!.height).toBeGreaterThanOrEqual(43.5);
      }
      await visible(l.edit).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await expect(dialog.getByRole('textbox').first()).toBeVisible();
      await expect(dialog.locator('textarea').first()).toHaveValue('Cover the new shift.');
      await shot(page, 'edit-draft', locale);
      await dialog.getByRole('button', { name: l.saveChanges, exact: true }).click();
      await expect(dialog).toHaveCount(0);
      expect(calls.filter(c => c.method === 'PUT' && c.path.endsWith('/rq-1'))).toHaveLength(1);
      expect(calls.filter(c => c.path.endsWith('/submit'))).toHaveLength(0);
    });

    test('5 manager feedback chip is a 44 px phone target', async ({ page }) => {
      await boot(page, { locale, roles: ['Manager'], permissions: MANAGER_PERMS, mine: [INTERVIEW] });
      const chip = page.getByRole('button', { name: l.addFeedback });
      await expect(chip).toBeVisible();
      if (isPhone(page)) expect((await chip.boundingBox())!.height).toBeGreaterThanOrEqual(43.5);
      await shot(page, 'manager', locale);
    });

    test('6 onboarding tab with no checklists explains why and links to where to create one', async ({ page }) => {
      await boot(page, { locale, checklists: [] });
      await page.locator(tabId('onboarding')).click();
      await expect(page.getByText(l.emptyTitle)).toBeVisible();
      await expect(page.locator('[data-onboarding-empty] a[href*="/setup"]')).toBeVisible();
      await shot(page, 'onboarding-empty', locale);
    });

    test('7 target join date is the shared locale date control, not a native date input', async ({ page }) => {
      await boot(page, { locale });
      await page.locator(tabId('requisitions')).click();
      await page.getByRole('button', { name: l.newReq }).first().click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await expect(dialog.locator('input[type="date"]')).toHaveCount(0);
      await expect(dialog.getByRole('group', { name: /Target joining date|تاريخ الانضمام/ })).toBeVisible();
      await shot(page, 'new-requisition', locale);
    });

    test('8 offer header shows the revision, not the internal Version counter', async ({ page }) => {
      await boot(page, { locale, url: '/recruitment/offers/offer-1' });
      const header = page.locator('section > header p').first();
      await expect(header).toContainText('Operations Specialist');
      const text = await header.innerText();
      expect(text).not.toMatch(/Version|الإصدار|7|٧/);
      await shot(page, 'offer-header', locale);
    });
  });
}
