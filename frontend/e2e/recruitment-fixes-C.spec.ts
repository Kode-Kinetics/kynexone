import { expect, test, type Page, type Request } from '@playwright/test';
import fs from 'node:fs';

/**
 * Recruitment walkthrough fixes, region C (Interviews, Assessments, the offer workspace and send dialog, the line manager's screen, the new-hire
 * compensation handoff, the two-step banner), against a mocked API: nothing is read or written. English and Arabic, desktop and a 390px phone.
 * Run: RECRUITMENT_FIXES_PORT=5180 RECRUITMENT_FIXES_DIST=.next-claude-recfixC npx playwright test -c e2e/playwright.recruitment-fixes.config.ts recruitment-fixes-C
 * RECRUITMENT_FIXES_SHOTS=<dir> also saves screenshots.
 */

const HR_PERMS = ['recruitment.read', 'recruitment.write', 'recruitment.approve', 'employees.read', 'employees.sensitive', 'employees.write', 'dashboard.read'];
const ISO = '2026-11-19T04:43:06.1954Z';
const FUTURE = '2030-01-15T09:30:00Z';
const interview = (over: Record<string, unknown> = {}) => ({ id: 'iv-1', applicationId: 'app-1', candidateName: 'Alice Almutairi', jobTitle: 'Operations Specialist - Dairy Line 2 with a long title', interviewType: 'Technical', interviewerNames: 'Dairy HR Manager, Liu Wei, Another Panel Member With A Long Name', scheduledAt: FUTURE, durationMinutes: 60, mode: 'Video', meetingLink: '', location: '', status: 'Scheduled', overallRating: null, recommendation: '', feedbackNotes: '', completedAt: null, createdAtUtc: ISO, ...over });
const FEEDBACK = [{ id: 'fb-1', interviewScheduleId: 'iv-1', applicationId: 'app-1', interviewerName: 'Dairy HR Manager', interviewerRole: '', communicationScore: 0, technicalScore: 0, cultureFitScore: 0, problemSolvingScore: 0, leadershipScore: 0, overallScore: 4, strengths: '', concerns: '', notes: 'Strong hands-on dairy experience', recommendation: 'Proceed', submittedAtUtc: '2026-11-19T18:30:00Z' }];
const assessment = (over: Record<string, unknown> = {}) => ({ id: 'as-1', applicationId: 'app-1', candidateId: 'c-1', templateId: 't-1', templateName: 'Dairy Operations Aptitude', status: 'Sent', sentAtUtc: '2026-11-19T09:00:00Z', completedAtUtc: null, expiresAtUtc: '2026-11-26T09:00:00Z', scoreObtained: null, totalMarks: null, scorePercentage: null, passed: null, createdAtUtc: ISO, ...over });
const app = { id: 'app-1', version: 3, jobOpeningId: 'op-1', jobTitle: 'Operations Specialist', candidateId: 'c-1', candidateName: 'Alice Almutairi', candidateEmail: 'alice@example.invalid', stage: 'Interview', stageOrder: 3, status: 'Active', rejectionReason: '', offeredSalary: null, appliedAtUtc: '2026-11-01T08:00:00Z', stageChangedAtUtc: null, hiredAtUtc: null, onboardingDraftId: null };

const offerRow = { id: 'offer-1', applicationId: 'app-1', companyId: 'c1', seriesId: 's1', revisionNumber: 1, version: 3, candidateName: 'Alice Almutairi', position: 'Operations Specialist', status: 'Draft', communicationOutcome: 'NotIssued', originalProvenance: 'Frozen', generatedAtUtc: ISO, responseDeadline: FUTURE };
const offerBody = { ...offerRow, offeredJobTitle: 'Operations Specialist', offeredDepartment: 'Operations', startDate: '2031-01-15', basicSalary: 7000, housingAllowance: 0, transportAllowance: 0, otherAllowances: 0, grossSalary: 7000, probationMonths: 3, sentAtUtc: null, acceptedAtUtc: null, recipientEmail: 'alice@example.invalid', frozenAtUtc: null };
const APPROVAL_REQUIRED = 'This offer needs approval before it can be sent. Request approval from an HR Manager or Admin who did not write the offer.';
const cap = (allowed: string[], issueReason = 'blocked') => Object.fromEntries(['editDraft', 'revise', 'freeze', 'withdraw', 'recordResponse', 'issue', 'downloadOriginal'].map(n => [n, { allowed: allowed.includes(n), reason: allowed.includes(n) ? null : n === 'issue' ? issueReason : n === 'editDraft' ? 'Create new revision to change submitted terms.' : 'blocked' }]));
const artifact = { id: 'art-1', sha256: 'abc', sizeBytes: 1000, provenance: 'Frozen', frozenAtUtc: ISO, offerVersion: 3 };
const approval = (status: string, extra: Record<string, unknown> = {}) => ({ id: 'ap-1', offerLetterId: 'offer-1', stepOrder: 1, approverName: 'Dairy Admin', approverUserId: 'u2', approverRole: 'Admin', status, comments: '', decidedAtUtc: null, ...extra });

type OfferState = 'draft' | 'frozen' | 'pending' | 'rejected' | 'approved';
function offerWorkspace(state: OfferState, approverUserId = 'u2') {
  const frozen = state !== 'draft';
  const status = state === 'pending' ? 'PendingApproval' : state === 'approved' ? 'Approved' : 'Draft';
  const approvals = state === 'pending' ? [approval('Pending', { approverUserId })] : state === 'rejected' ? [approval('Rejected', { comments: 'Package is above the plant budget' })] : state === 'approved' ? [approval('Approved')] : [];
  const allowed = state === 'draft' ? ['editDraft', 'revise', 'freeze', 'withdraw'] : state === 'approved' ? ['revise', 'withdraw', 'issue'] : ['revise', 'withdraw'];
  return { offer: { ...offerBody, status, frozenAtUtc: frozen ? ISO : null }, artifact: frozen ? artifact : null, originalProvenance: frozen ? 'Frozen' : 'NotFrozen', approvals, capabilities: cap(allowed, state === 'frozen' ? APPROVAL_REQUIRED : 'blocked'), revisions: [{ ...offerRow, status }], changedFields: [], communicationOutcome: 'NotIssued', compensationSnapshot: null, designSnapshot: null };
}

interface Boot {
  locale: 'en' | 'ar'; url: string; roles?: string[]; permissions?: string[];
  interviews?: unknown[]; assessments?: unknown[]; offerState?: OfferState; mfa?: boolean; managerInterviews?: unknown[]; managerFeedback?: unknown[];
  readiness?: Record<string, unknown>; draftReview?: boolean; approverUserId?: string;
}

async function boot(page: Page, o: Boot) {
  const calls: { method: string; path: string; body: unknown }[] = [];
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(locale => { localStorage.setItem('zayra_access_token', 'fixture'); localStorage.setItem('kynexone-locale-choice-v2', locale); }, o.locale);
  const state = o.offerState ?? 'draft';
  let managerFeedback = [...(o.managerFeedback ?? [])];
  await page.route('**/api/**', async route => {
    const request: Request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const json = (body: unknown, status = 200) => route.fulfill({ status, json: body });
    if (request.method() !== 'GET') calls.push({ method: request.method(), path, body: request.postDataJSON() });
    if (path === '/api/auth/me') return json({ id: 'u1', employeeId: 9, tenantId: 't1', tenantSlug: 'fixture', fullName: 'Recruiter Test', roles: o.roles ?? ['Admin'], permissions: o.permissions ?? HR_PERMS, companies: [{ id: 'c1', name: 'Dairy Company', code: 'DC', countryCode: 'SA', isActive: true }] });
    if (path === '/api/auth/mfa/status') return json(o.mfa ? { promptToEnroll: true, enforceFromUtc: '2026-12-01T00:00:00Z' } : { promptToEnroll: false });
    if (path === '/api/features/disabled-keys') return json(['release_a']);
    if (path === '/api/features/modules') return json([]);
    if (path === '/api/tenant-admin/localization') return json({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: o.locale, defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/notifications') return json([]);
    if (path === '/api/recruitment/interviews/mine') return json({ total: (o.managerInterviews ?? []).length, items: o.managerInterviews ?? [] });
    if (path === '/api/recruitment/interviews/iv-1/feedback' && request.method() === 'GET') return json(FEEDBACK);
    if (path === '/api/recruitment/interviews/iv-1/feedback' && request.method() === 'POST') {
      const body = request.postDataJSON();
      const saved = { id: 'fb-9', interviewerName: 'Recruiter Test', submittedAtUtc: '2026-11-19T19:00:00Z', ...body };
      managerFeedback = [{ ...FEEDBACK[0], id: 'fb-9', overallScore: body.overallScore, notes: body.notes, recommendation: body.recommendation }, ...managerFeedback];
      return json(saved);
    }
    if (path === '/api/recruitment/interviews/iv-1' && request.method() === 'GET') return json({ interview: interview(), feedbacks: managerFeedback });
    if (/\/api\/recruitment\/interviews\/[^/]+\/cancel$/.test(path)) return json(interview({ status: 'Cancelled' }));
    if (path === '/api/recruitment/interviews') return json({ items: o.interviews ?? [], total: (o.interviews ?? []).length, page: 1, pageSize: 20 });
    if (path === '/api/recruitment/assessments') return json({ items: o.assessments ?? [], total: (o.assessments ?? []).length, page: 1, pageSize: 20 });
    if (path === '/api/recruitment/assessments/templates') return json([]);
    if (path === '/api/recruitment/applications') return json({ items: [app], total: 1, page: 1, pageSize: 100 });
    if (path === '/api/recruitment/offers/history') return json({ items: [offerRow], total: 1, page: 1, pageSize: 20 });
    if (path === '/api/recruitment/offers/mail-readiness') return json(o.readiness ?? { configured: true, mode: 'Live', source: 'Tenant', fromAddress: 'hr@example.invalid', fromName: 'HR', canManageSettings: false, blockedReason: null, configurationFingerprint: 'fp-1', recipientRestricted: false });
    if (path === '/api/recruitment/offers/offer-1/workspace') return json(offerWorkspace(state, o.approverUserId));
    if (path === '/api/recruitment/offers/offer-1/events') return json({ items: [], total: 0, page: 1, pageSize: 20 });
    if (path === '/api/recruitment/offers/offer-1/approver-options') return json([{ userId: 'u2', name: 'Dairy Admin', email: 'admin@example.invalid', role: 'Admin' }]);
    if (path === '/api/recruitment/offers/offer-1') {
      const w = offerWorkspace(state, o.approverUserId);
      return json({ offer: w.offer, approvals: w.approvals, approval: { required: true, isAuthor: true, canSend: state === 'approved', sendBlockedReason: null, myPendingStepId: null } });
    }
    if (path === '/api/recruitment/offers/component-options') return json({ policy: { companyId: 'c1', currency: 'SAR', precision: 2, requiredComponentCodes: ['BASIC'], expectedVersion: 1 }, options: [], canonicalBasic: { code: 'BASIC', labelEn: 'Basic salary', labelAr: 'الراتب الأساسي', kind: 'Cash', frequency: 'Monthly', structureField: 'BasicSalary', required: true } });
    if (path === '/api/recruitment/offer-templates/defaults') return json({ design: { schemaVersion: 2 }, tokens: [] });
    if (path === '/api/recruitment/offer-templates') return json([]);
    if (path === '/api/recruitment/requisitions/my-departments') return json({ items: [{ id: 'dep-1', name: 'Sales' }] });
    if (path === '/api/recruitment/requisitions') return json({ items: [], total: 0 });
    if (path === '/api/employees/drafts' && request.method() === 'GET') return json({ items: [draftItem], total: 1, page: 1, pageSize: 25, counts: { awaitingApproval: 1, draft: 0, activated: 0, rejected: 0, cancelled: 0 } });
    if (path === '/api/employees/drafts/d1') return json({ summary: draftItem, draft: { id: 'd1', status: 'PendingHrApproval', englishName: 'Alice Almutairi', arabicName: '', personalEmail: 'alice@example.invalid', workEmail: '', phone: '', nationality: 'SA', countryCode: 'SA', department: 'Operations', designation: 'Operations Specialist', branch: '', workLocation: '', joiningDate: '2027-01-15', contractType: 'Permanent', probationEndDate: null, salary: 7000 }, documentCount: 0, activationCheck: { canActivate: false, resolvedCompanyName: 'Dairy Company', problems: [], advisories: [] } });
    if (path === '/api/employees/drafts/d1/offer-compensation') return json({ acceptedOfferId: 'offer-1', sourceCompanyId: 'c1', state: 'NeedsReview', version: 1, snapshot: null, dispositions: [], effectiveDate: '2027-01-15', confirmedAtUtc: null, blockers: [], placementCompanyId: 'c1', placementProvenance: 'AcceptedOffer', placementGuidance: '' });
    if (path.startsWith('/api/recruitment/')) return json({ items: [], total: 0, page: 1, pageSize: 20 });
    return json({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(o.url);
  return { calls, errors };
}

const draftItem = { id: 'd1', status: 'PendingHrApproval', currentStep: 'Review', name: 'Alice Almutairi', arabicName: '', department: 'Operations', designation: 'Operations Specialist', branch: '', joiningDate: '2027-01-15', source: 'Recruitment', applicationId: 'app-1', jobTitle: 'Operations Specialist', createdByUserId: 'u3', createdByName: 'HR Maker', isMine: false, canApprove: true, approveBlockedReason: null, profileCompletenessScore: 80, createdAtUtc: ISO, submittedAtUtc: ISO, decidedAtUtc: null, decidedByName: null, decisionReason: null, activatedEmployeeId: null, activatedEmployeeCode: null };

async function shot(page: Page, name: string, locale: string) {
  const dir = process.env.RECRUITMENT_FIXES_SHOTS;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: `${dir}/C-${name}-${locale}-${page.viewportSize()!.width >= 1000 ? 'desktop' : 'phone'}.png` });
}

/** The page never scrolls sideways, and the element is fully inside the viewport. */
async function noSidewaysScroll(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(1);
}
/** On a phone nothing may be a sideways-scrolling strip (the Assessments table used to clip its Result and Actions columns inside one). */
async function noInnerSideScroll(page: Page) {
  const strips = await page.evaluate(() => [...document.querySelectorAll('main *')].filter(el => {
    const style = getComputedStyle(el);
    return ['auto', 'scroll'].includes(style.overflowX) && el.scrollWidth > el.clientWidth + 1;
  }).map(el => el.tagName + '.' + String(el.className).slice(0, 40)));
  expect(strips).toEqual([]);
}
/** Visible words in Latin letters that are not data (names sit in <bdi>, inputs and the mock's own values are skipped): in Arabic these are untranslated labels. */
async function latinLeaks(page: Page, data: RegExp, scope = 'main') {
  return page.evaluate(([source, root]) => {
    const skip = new RegExp(source, 'i');
    const out: string[] = [];
    const walker = document.createTreeWalker(document.querySelector(root) ?? document.body, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      const el = n.parentElement!;
      if (el.closest('bdi,script,style,textarea,input,[data-nextjs-dialog-overlay],nextjs-portal')) continue;
      const text = (n.textContent ?? '').trim();
      const words = text.replace(/\b(PDF|SMTP|HR|SAR|IBAN)\b/g, '');
      if (/[A-Za-z]{3,}/.test(words) && !skip.test(text)) out.push(text);
    }
    return out;
  }, [data.source, scope]);
}
const MOCK_DATA = /HR Maker|^(Tenant|Platform)$|^BASIC$|^Operations$|Alice|Almutairi|Operations Specialist|Dairy|Liu Wei|Panel Member|Strong hands|Solid candidate|Recruiter Test|Package is above|Aptitude|Safety awareness|template name|@example|fp-1|^SAR|^[A-Z]{2,4}$|^HR$/;
const leaks = async (page: Page, scope = 'main') => (await latinLeaks(page, MOCK_DATA, scope));
async function insideViewport(locator: ReturnType<Page['locator']>, page: Page) {
  await locator.scrollIntoViewIfNeeded();
  const box = (await locator.boundingBox())!;
  const width = page.viewportSize()!.width;
  expect(box.x).toBeGreaterThanOrEqual(-1);
  expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
}

for (const locale of ['en', 'ar'] as const) {
  const ar = locale === 'ar';
  const L = ar ? {
    interviews: 'المقابلات', assessments: 'التقييمات', cancel: /^إلغاء$/, cancelTitle: 'إلغاء هذه المقابلة؟', cancelConfirm: 'إلغاء المقابلة', panel: 'ملاحظات اللجنة',
    past: 'وقت البدء هذا قد مضى', schedule: 'جدولة مقابلة', give: 'إضافة ملاحظات', send: 'إرسال الملاحظات', saved: /تم حفظ ملاحظاتك/, recordResult: 'تسجيل النتيجة',
    frozen: 'مسودة، الأصل مثبت', rejected: 'رفضه أحد المعتمدين', pending: 'بانتظار الموافقة', waiting: 'بانتظار قرار المعتمِد', nextAction: 'الإجراء التالي', pickerReason: /هناك موافقة بانتظار القرار/,
    approvals: 'الموافقات', sendReview: 'مراجعة الإرسال', radioEmail: 'إرساله بالبريد من هنا', radioExternal: 'أرسلته بنفسي بالفعل', record: 'تسجيل إرسال خارجي', sendEmail: 'إرسال بريد إلكتروني', notIssued: /لا يُصدر العرض إلا عند/,
    needsReview: 'تحتاج إلى مراجعة', acceptedOffer: 'العرض المقبول', banner: 'التحقق بخطوتين مطلوب لدورك الوظيفي',
  } : {
    interviews: 'Interviews', assessments: 'Assessments', cancel: /^Cancel$/, cancelTitle: 'Cancel this interview?', cancelConfirm: 'Cancel interview', panel: 'Panel feedback',
    past: 'That start time has already passed', schedule: 'Schedule Interview', give: 'Give feedback', send: 'Send feedback', saved: /Your feedback on .* was saved/, recordResult: 'Record result',
    frozen: 'Draft, original frozen', rejected: 'Rejected by an approver', pending: 'Pending approval', waiting: 'Waiting for the approver to decide', nextAction: 'Next action', pickerReason: /An approval is already waiting for a decision/,
    approvals: 'Approvals', sendReview: 'Send review', radioEmail: 'Email it from here', radioExternal: 'I already sent it myself', record: 'Record external sending', sendEmail: 'Send email', notIssued: /The offer is not issued until you press Record external sending/,
    needsReview: 'Needs review', acceptedOffer: 'Accepted offer', banner: 'Two-step sign-in is required for your role',
  };
  const tab = (page: Page, name: string) => page.getByRole('button', { name, exact: true }).first().click();

  test.describe(`recruitment fixes C (${locale})`, () => {
    test('1/2/3 Interviews: fits the phone, shows the panel feedback, confirms a cancel, refuses a past start', async ({ page }) => {
      const { calls, errors } = await boot(page, { locale, url: '/recruitment', interviews: [interview(), interview({ id: 'iv-2', status: 'Completed', overallRating: 4, recommendation: 'Hire', feedbackNotes: 'Strong hands-on dairy experience with a long note that must wrap on a phone screen and never push the page sideways' })] });
      await tab(page, L.interviews);
      await expect(page.getByText('Alice Almutairi').first()).toBeVisible();
      await noSidewaysScroll(page);
      for (const b of await page.getByRole('button', { name: L.cancel }).all()) await insideViewport(b, page);
      for (const b of await page.getByRole('button', { name: L.panel, exact: true }).all()) await insideViewport(b, page);
      // The name column keeps a readable width beside the buttons (it used to be squeezed to a few characters).
      expect((await page.locator('p', { hasText: 'Alice Almutairi' }).first().boundingBox())!.width).toBeGreaterThan(Math.min(200, page.viewportSize()!.width * 0.5));
      await noInnerSideScroll(page);
      // Tap targets: 44px and 8px apart on a phone (Complete and Cancel sit side by side), compact on desktop.
      const chips = [page.getByRole('button', { name: ar ? 'مكتمل' : 'Complete', exact: true }).first(), page.getByRole('button', { name: L.cancel }).first(), page.getByRole('button', { name: L.panel, exact: true }).first()];
      const boxes = await Promise.all(chips.map(c => c.boundingBox()));
      if (page.viewportSize()!.width < 640) {
        for (const b of boxes) expect(b!.height).toBeGreaterThanOrEqual(44);
        const row = boxes.filter(b => Math.abs(b!.y - boxes[0]!.y) < 4).sort((a, b) => a!.x - b!.x);
        for (let i = 1; i < row.length; i++) expect(row[i]!.x - (row[i - 1]!.x + row[i - 1]!.width)).toBeGreaterThanOrEqual(8);
      } else for (const b of boxes) expect(b!.height).toBeLessThan(40);
      // The card title is never clipped (mixed English names in an Arabic page used to lose their start).
      expect(await page.locator('p', { hasText: 'Alice Almutairi' }).first().evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
      await shot(page, 'interviews', locale);

      // Panel feedback (item 2): the manager's note, rating, recommendation and time are shown on demand.
      await page.getByRole('button', { name: L.panel, exact: true }).first().click();
      const region = page.getByRole('region', { name: L.panel });
      await expect(region.getByText('Strong hands-on dairy experience')).toBeVisible();
      await expect(region.getByText('Dairy HR Manager')).toBeVisible();
      await expect(region).toContainText('4/5');
      if (ar) expect(await leaks(page)).toEqual([]);
      await noSidewaysScroll(page);
      await shot(page, 'interviews-feedback', locale);

      // Cancelling asks first (item 3): backing out sends nothing, confirming sends once.
      await page.getByRole('button', { name: L.cancel }).first().click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toContainText(L.cancelTitle);
      await shot(page, 'interviews-cancel-confirm', locale);
      await dialog.getByRole('button', { name: L.cancel }).click();
      await expect(dialog).toHaveCount(0);
      expect(calls.filter(c => c.path.endsWith('/cancel'))).toHaveLength(0);
      await page.getByRole('button', { name: L.cancel }).first().click();
      await page.getByRole('dialog').getByRole('button', { name: L.cancelConfirm, exact: true }).click();
      await expect.poll(() => calls.filter(c => c.path.endsWith('/cancel')).length).toBe(1);

      // A start time in the past is refused inline (item 3), and nothing is posted.
      await page.getByRole('button', { name: L.schedule, exact: true }).click();
      await page.getByRole('combobox', { name: ar ? 'اختر الطلب' : 'Select application' }).selectOption('app-1');
      await page.getByPlaceholder(ar ? /سارة/ : /Sarah/).fill('Liu Wei');
      // The date and time come from the app's 24-hour selects, not the browser's 12-hour control; the application select isolates mixed-language text.
      await expect(page.locator('input[type=datetime-local]')).toHaveCount(0);
      await expect(page.getByRole('combobox', { name: ar ? 'اختر الطلب' : 'Select application' })).toHaveAttribute('dir', 'ltr');
      const when = page.getByRole('group', { name: ar ? 'تاريخ ووقت المقابلة' : 'Interview date and time' }).locator('select');
      await when.nth(1).selectOption('1'); await when.nth(2).selectOption('2022'); await when.nth(0).selectOption('1'); await when.nth(3).selectOption('9'); await when.nth(4).selectOption('0');
      await expect(page.getByText(L.past)).toBeVisible();
      await expect(page.getByRole('button', { name: ar ? 'جدولة' : 'Schedule', exact: true })).toBeDisabled();
      await shot(page, 'interviews-past-start', locale);
      expect(calls.filter(c => c.method === 'POST' && c.path === '/api/recruitment/interviews')).toHaveLength(0);
      expect(errors).toEqual([]);
    });

    test('1/6 Assessments: reachable on the phone, dates in the app format', async ({ page }) => {
      const { errors } = await boot(page, { locale, url: '/recruitment', assessments: [assessment(), assessment({ id: 'as-2', status: 'Completed', scorePercentage: 78, passed: true, templateName: 'Safety awareness with a rather long template name' })] });
      await tab(page, L.assessments);
      await expect(page.getByText('Dairy Operations Aptitude').first()).toBeVisible();
      await noSidewaysScroll(page);
      // 19 Nov 2026, never 11/19/2026.
      await expect(page.locator('body')).not.toContainText(/\d{1,2}\/\d{1,2}\/\d{4}/);
      await expect(page.locator('body')).toContainText(/19 (Nov|نوفمبر)/);
      await noInnerSideScroll(page);
      await insideViewport(page.getByRole('button', { name: ar ? 'تكليف بتقييم' : 'Assign Assessment', exact: true }), page);
      const input = page.getByRole('spinbutton').first();
      await insideViewport(input, page);
      await insideViewport(page.getByRole('button', { name: L.recordResult, exact: true }), page);
      await expect(page.getByText(ar ? 'ناجح' : 'Passed').first()).toBeVisible();
      // The score box is labelled in the app language, and an unscored row says so instead of a bare dash.
      const placeholder = await page.getByRole('spinbutton').first().getAttribute('placeholder');
      if (ar) expect(placeholder).not.toMatch(/Score|Marks/); else expect(placeholder).toMatch(/Score|Marks/);
      await expect(page.getByText(ar ? 'لم يُقيَّم بعد' : 'Not scored yet').first()).toBeVisible();
      if (ar) expect(await leaks(page)).toEqual([]);
      await shot(page, 'assessments', locale);
      expect(errors).toEqual([]);
    });

    test('2 line manager: saving feedback confirms it and shows it back', async ({ page }) => {
      const { calls, errors } = await boot(page, { locale, url: '/recruitment', roles: ['Manager'], permissions: ['recruitment.interview.feedback', 'recruitment.requisition.raise', 'dashboard.read'], managerInterviews: [{ id: 'iv-1', applicationId: 'app-1', interviewType: 'Technical', scheduledAt: '2026-11-19T07:30:00Z', durationMinutes: 60, mode: 'Video', location: '', meetingLink: '', status: 'Completed', candidateName: 'Alice Almutairi', jobTitle: 'Operations Specialist' }] });
      await expect(page.getByText('Alice Almutairi').first()).toBeVisible();
      await page.getByRole('button', { name: L.give, exact: true }).click();
      await page.getByRole('textbox').last().fill('Solid candidate, would hire');
      // The requisitions card header: the title on its own line without an orphan word, the primary button one line and full width on a phone.
      if (page.viewportSize()!.width < 640) {
        const newBtn = (await page.getByRole('button', { name: ar ? 'طلب توظيف جديد' : 'New Requisition', exact: true }).boundingBox())!;
        expect(newBtn.height).toBeLessThan(56);
        expect(newBtn.width).toBeGreaterThan(page.viewportSize()!.width * 0.7);
        expect((await page.getByRole('heading', { name: ar ? 'طلبات التوظيف الخاصة بي' : 'My requisitions' }).boundingBox())!.height).toBeLessThan(32);
      }
      await shot(page, 'manager-feedback-form', locale);
      await page.getByRole('button', { name: L.send, exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: L.saved })).toBeVisible();
      await expect(page.getByRole('region', { name: ar ? 'ملاحظاتك' : 'Your feedback' })).toContainText('Solid candidate, would hire');
      await noSidewaysScroll(page);
      if (ar) expect(await leaks(page)).toEqual([]);
      await shot(page, 'manager-feedback-saved', locale);
      expect(calls.filter(c => c.path.endsWith('/feedback'))).toHaveLength(1);
      expect(errors).toEqual([]);
    });

    for (const [state, expectStatus, expectNext, primary] of [
      ['draft', ar ? 'مسودة' : 'Draft', ar ? 'الإجراء التالي: تثبيت الأصل' : 'Next action: Freeze original', ar ? 'تثبيت الأصل' : 'Freeze original'],
      ['frozen', L.frozen, ar ? 'الإجراء التالي: طلب الاعتماد' : 'Next action: Request approval', ar ? 'طلب الاعتماد' : 'Request approval'],
      ['pending', L.pending, L.waiting, ''],
      ['rejected', L.rejected, ar ? 'الإجراء التالي: إنشاء مراجعة' : 'Next action: Create revision', ar ? 'إنشاء مراجعة' : 'Create revision'],
    ] as const) {
      test(`4 offer workspace says the true status and next step (${state})`, async ({ page }) => {
        const { errors } = await boot(page, { locale, url: '/recruitment/offers/offer-1', offerState: state });
        await expect(page.getByRole('heading', { name: 'Alice Almutairi' })).toBeVisible();
        const header = page.locator('section header').first();
        await expect(header).toContainText(expectStatus);
        await expect(header).toContainText(expectNext);
        if (state === 'frozen') await expect(header).not.toContainText(ar ? 'مسودة · لم يصدر' : 'Draft · Not issued');
        // The next action names a button that is there, and that button is the one primary.
        if (primary) {
          await expect(header.getByRole('button', { name: primary, exact: true })).toHaveClass(/btn-primary/);
          await expect(header.locator('button.btn-primary')).toHaveCount(1);
        } else await expect(header.locator('button.btn-primary')).toHaveCount(0);
        // The response-deadline time is one hour:minute group on its own row, in order, beside no stray colon.
        if (state === 'draft') {
          const deadline = page.getByRole('group', { name: ar ? 'الموعد النهائي للرد' : 'Response deadline' });
          const selects = deadline.locator('select');
          const [day, hour, minute] = await Promise.all([selects.nth(0).boundingBox(), selects.nth(3).boundingBox(), selects.nth(4).boundingBox()]);
          expect(Math.abs(hour!.y - minute!.y)).toBeLessThan(3);
          expect(minute!.x).toBeGreaterThan(hour!.x);
          if (page.viewportSize()!.width < 640) expect(hour!.y).toBeGreaterThan(day!.y + day!.height - 2);
          const colon = deadline.locator('[aria-hidden=true]').last();
          const c = (await colon.boundingBox())!;
          expect(c.x).toBeGreaterThan(hour!.x); expect(c.x).toBeLessThan(minute!.x);
        }
        if (ar) expect(await leaks(page)).toEqual([]);
        await shot(page, `offer-${state}`, locale);
        if (state === 'pending' || state === 'frozen') {
          await page.getByRole('tab', { name: L.approvals, exact: true }).click();
          if (state === 'pending') {
            await expect(page.getByText(L.pickerReason)).toBeVisible();
            await expect(page.getByRole('combobox', { name: ar ? 'معتمِد العرض' : 'Offer approver' })).toHaveCount(0);
          } else {
            await expect(page.getByRole('combobox', { name: ar ? 'معتمِد العرض' : 'Offer approver' })).toBeVisible();
          }
          await shot(page, `offer-${state}-approvals`, locale);
        }
        expect(errors).toEqual([]);
      });
    }

    test('4 next action for the approver and for someone who cannot act is honest', async ({ page }) => {
      // The named approver is asked to decide, with a primary button that opens the Approvals tab.
      await boot(page, { locale, url: '/recruitment/offers/offer-1', offerState: 'pending', approverUserId: 'u1' });
      const header = page.locator('section header').first();
      await expect(header.getByRole('button', { name: ar ? 'مراجعة واتخاذ القرار' : 'Review and decide', exact: true })).toHaveClass(/btn-primary/);
      await header.getByRole('button', { name: ar ? 'مراجعة واتخاذ القرار' : 'Review and decide', exact: true }).click();
      await expect(page.getByRole('tab', { name: L.approvals, exact: true })).toHaveAttribute('aria-selected', 'true');
    });

    test('4 a recruiter who cannot ask for approval is told who can, not offered a button that is not there', async ({ page }) => {
      await boot(page, { locale, url: '/recruitment/offers/offer-1', offerState: 'frozen', roles: ['Recruiter'] });
      const header = page.locator('section header').first();
      await expect(header).toContainText(ar ? 'يجب أن يطلب مدير موارد بشرية أو مدير نظام الاعتماد' : 'An HR Manager or Admin has to request approval');
      await expect(header.getByRole('button', { name: ar ? 'طلب الاعتماد' : 'Request approval', exact: true })).toHaveCount(0);
      await expect(header).not.toContainText(ar ? 'الإجراء التالي: طلب الاعتماد' : 'Next action: Request approval');
    });

    for (const capture of [false, true]) {
      test(`5 send dialog: two distinct choices and a plain explanation${capture ? ' (capture mode)' : ''}`, async ({ page }) => {
        const { errors } = await boot(page, { locale, url: '/recruitment/offers/offer-1', offerState: 'approved', readiness: capture ? { configured: true, mode: 'Capture', source: 'Platform', fromAddress: 'x@example.invalid', fromName: 'HR', canManageSettings: false, blockedReason: null, configurationFingerprint: 'fp-1', recipientRestricted: false } : undefined });
        await page.getByRole('button', { name: L.sendReview, exact: true }).first().click();
        const dialog = page.getByRole('dialog');
        const group = dialog.getByRole('radiogroup');
        await expect(group).toBeVisible();
        await expect(group.getByRole('radio')).toHaveCount(2);
        await expect(group.getByRole('radio', { name: L.radioEmail })).toHaveAttribute('aria-checked', 'true');
        // Exactly one button is named "Send email": the submit, never the choice.
        await expect(dialog.getByRole('button', { name: L.sendEmail, exact: true })).toHaveCount(1);
        if (capture) await expect(dialog).toContainText(ar ? 'تُلتقط للاختبار فقط' : 'only captured for testing');
        // No developer text: the source is a plain sentence, the name comes before the address, and the check code is behind a disclosure.
        await expect(dialog).toContainText(ar ? 'يُرسل من بريد' : 'Sent from');
        await expect(dialog).toContainText('HR <hr@example.invalid>'.replace('hr@', capture ? 'x@' : 'hr@'));
        await expect(dialog).not.toContainText(/Sender source|Reviewed sender fingerprint|Tenant|Platform/);
        await expect(dialog.getByText('fp-1')).toBeHidden();
        if (ar) expect(await leaks(page, '[role=dialog]')).toEqual([]);
        await shot(page, `send-dialog${capture ? '-capture' : ''}`, locale);
        await group.getByRole('radio', { name: L.radioExternal }).click();
        await expect(group.getByRole('radio', { name: L.radioExternal })).toHaveAttribute('aria-checked', 'true');
        await expect(dialog.getByText(L.notIssued)).toBeVisible();
        await expect(dialog.getByRole('button', { name: L.record, exact: true })).toHaveCount(1);
        await expect(dialog.getByRole('button', { name: L.sendEmail, exact: true })).toHaveCount(0);
        if (!capture) await shot(page, 'send-dialog-external', locale);
        expect(errors).toEqual([]);
      });
    }

    test('8 new-hire draft shows human labels and the company name, not codes or ids', async ({ page }) => {
      const { errors } = await boot(page, { locale, url: '/people/new-hires' });
      await page.locator('table tbody tr button').first().click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toContainText(L.needsReview);
      await expect(dialog).toContainText(L.acceptedOffer);
      await expect(dialog).not.toContainText('NeedsReview');
      await expect(dialog).not.toContainText('AcceptedOffer');
      await expect(dialog).not.toContainText(/[0-9a-f]{8}-[0-9a-f]{4}/);
      await expect(dialog).toContainText('Dairy Company');
      if (ar) expect(await leaks(page, '[role=dialog]')).toEqual([]);
      await shot(page, 'new-hire-detail', locale);
      expect(errors).toEqual([]);
    });

    test('9 the two-step sign-in banner is in the app language', async ({ page }) => {
      await boot(page, { locale, url: '/recruitment', mfa: true });
      await expect(page.getByText(L.banner)).toBeVisible();
      if (ar) await expect(page.getByText('Two-step sign-in is required')).toHaveCount(0);
      await shot(page, 'mfa-banner', locale);
    });
  });
}
