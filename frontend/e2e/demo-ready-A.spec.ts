import { expect, test, type Page } from '@playwright/test';
import { employees } from './employee-workspace.fixture';
import { demoReadyA } from '../src/i18n/demoReadyA';

/**
 * Demo-readiness fixes, stream A, against a mocked API (nothing is read or written). EN and AR, desktop and phone.
 *   1 Pay approvals: after Approve the page says who activates next
 *   2 Employee Access tab: "Employee completion" follows the real status
 *   3 New hires review: a note beside Save review says saving clears the confirmation
 *   4 Onboarding tasks / Profile review empty states say why and what to do next
 *   5 /welcome: the password is masked by default, with an accessible show/hide toggle
 *   6 Group Overview is not offered to a plain Employee
 * Run: DEMO_READY_PORT=5183 DEMO_READY_DIST=.next-claude-demoreadyA npx playwright test -c e2e/playwright.demo-ready.config.ts demo-ready-A
 */
const SHOTS = process.env.DEMO_READY_SHOTS_DIR;
type Lang = 'en' | 'ar';
const LANGS: Lang[] = ['en', 'ar'];
const ar = (key: string) => demoReadyA.ar[key];

const errorsByPage = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  errorsByPage.set(page, errors);
  page.on('pageerror', e => errors.push(e.message));
});
test.afterEach(async ({ page }) => { expect(errorsByPage.get(page), 'No application runtime errors').toEqual([]); });

async function shot(page: Page, name: string, lang: Lang, info: { project: { name: string } }) {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/A-${name}-${lang}-${info.project.name}.png`, fullPage: true });
}
// The first compile of a route reloads the document on a cold dev server: warm it, then run the test.
async function warm(page: Page, url: string) {
  await page.route('**/api/**', route => route.fulfill({ json: {} }));
  await page.goto(url).catch(() => undefined);
  await page.waitForTimeout(1500);
  await page.unroute('**/api/**');
}

const HR_PERMS = ['employees.read', 'employees.write', 'employees.approve', 'employees.sensitive', 'employees.access.issue', 'employees.access.reset', 'dashboard.read', 'ess.read'];
const EMPLOYEE_PERMS = ['dashboard.read', 'profile.read', 'ess.read', 'ess.write', 'performance.read', 'loans.self'];

interface Boot {
  lang?: Lang;
  roles?: string[];
  permissions?: string[];
  accountType?: 'Company' | 'Group';
  employeeStatus?: string;
  selfServiceAvailable?: boolean;
  emailDelivery?: boolean;
  signInCodeDelivery?: 'Email' | 'PrintedSlip';
  draftItem?: Record<string, unknown>;
  draftCounts?: Record<string, number>;
  payList?: Record<string, unknown>;
}

const DRAFT = {
  id: 'd-1', currentStep: 'HR approval', name: 'Rana Saeed', arabicName: 'رنا سعيد', department: 'IT', designation: 'Officer', branch: 'Riyadh HQ',
  joiningDate: '2026-10-20', source: 'Manual', applicationId: null, jobTitle: null, createdByUserId: 'hr-2', createdByName: 'Sara Ali', isMine: false,
  canApprove: true, approveBlockedReason: null, profileCompletenessScore: 90, createdAtUtc: '2026-10-07T08:00:00Z', submittedAtUtc: '2026-10-07T08:05:00Z',
  decidedAtUtc: null, decidedByName: null, decisionReason: null, activatedEmployeeCode: null, status: 'PendingHrApproval', activatedEmployeeId: null,
};
const COMPONENTS = [
  { code: 'BASIC', labelEn: 'Basic salary', labelAr: 'الراتب الأساسي', kind: 'Cash', amount: 8000, description: null, frequency: 'Monthly', included: true, exclusionReason: null, payComponentId: null, order: 1, required: true, structureField: 'BasicSalary', mappingSource: 'Policy' },
];
const HANDOFF = {
  acceptedOfferId: 'o-1', sourceCompanyId: 'fixture-company', state: 'NeedsReview', version: 3, effectiveDate: '2026-10-20', confirmedAtUtc: null, blockers: [],
  placementCompanyId: 'fixture-company', placementProvenance: 'Offer', placementGuidance: '', dispositions: [],
  snapshot: { schemaVersion: 1, currency: 'SAR', precision: 2, components: COMPONENTS, monthlyCash: 8000, annualRecurringCash: 96000, oneTimeCash: 0, basicSalary: 8000, housingAllowance: 0, transportAllowance: 0, otherAllowances: 0, policyVersion: 1, policySource: 'Policy' },
};
const PAY_DETAIL = {
  employee: { id: 41, code: 'EMP-0041', name: 'Layla Al-Harbi', companyId: 'c1', companyName: 'Acme Arabia', designation: 'Senior Analyst' },
  components: { basic: 8000, housing: 2000, transport: 500, food: 0, mobile: 0, other: 0, fixedDeduction: 0, gross: 10500, currency: 'SAR' },
  bank: { bankName: 'Riyad Bank', ibanLast4: '7519', accountLast4: null, accountHolderName: 'Layla Al-Harbi' },
  enteredBy: { name: 'Huda HR Officer', atUtc: '2026-10-26T09:15:00Z' }, lastChangedBy: { name: 'Huda HR Officer', atUtc: '2026-10-26T09:15:00Z' },
  fingerprint: 'a'.repeat(64), sharedIbanCount: 0, lastRejection: null,
};

async function boot(page: Page, url: string, options: Boot = {}) {
  await page.addInitScript(lang => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', lang);
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
  }, options.lang ?? 'en');
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    if (path === '/api/auth/me') return reply({
      id: 'u1', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Huda HR', roles: options.roles ?? ['HR Manager'],
      permissions: options.permissions ?? HR_PERMS, accountType: options.accountType ?? 'Company', isGroupScope: false, employeeId: 42,
      companies: [{ id: 'fixture-company', name: 'Acme Arabia', code: 'fixture-company', countryCode: 'SAU', isActive: true }],
    });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR', defaultLanguage: 'en', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/features/disabled-keys') return reply([]);
    if (path.includes('/features/')) return reply([]);
    if (path === '/api/notifications' || path.startsWith('/api/ai/policy/')) return reply([]);
    if (path === '/api/companies') return reply({ items: [{ id: 'fixture-company', legalNameEn: 'Acme Arabia', countryCode: 'SAU', emailDomain: 'acme.test', isActive: true, approvalStatus: 'Active' }], total: 1, page: 1, pageSize: 25 });

    // 2: the employee record
    if (path === '/api/employee-completion/901') return reply({ requested: false, status: 'NotRequested', selfServiceAvailable: options.selfServiceAvailable ?? false });
    if (path === '/api/employee-access/901') return reply({
      employeeId: 901, employeeName: employees[901].fullName, employeeCode: 'EMP-1', workEmail: employees[901].workEmail, state: 'not_started',
      loginEnabled: false, passwordConfigured: false, canIssue: true, emailDelivery: !!options.emailDelivery,
      ...(options.signInCodeDelivery ? { signInCodeDelivery: options.signInCodeDelivery, workEmailEnteredByCaller: false } : {}),
      codeExpiresAtUtc: null, codeIssuedByName: null, lastCodeExpiredAtUtc: null, lastSignInAtUtc: null, stoppedReason: null, blockedCode: null, blockedReason: null,
    });
    if (path === '/api/employees/901/readiness') return reply({
      employeeId: 901, state: 'Ready', score: 100, progress: { present: 5, requiredTotal: 5 }, policy: { countryCode: 'SAU', tier: 'Activation', sources: [] },
      blocking: [], payBlocking: [], recommended: [], present: [], expiringSoon: [], disclaimer: '',
    });
    if (path === '/api/employees/901') return reply({ ...employees[901], status: options.employeeStatus ?? 'Active' });

    // 3: New hires review
    if (path === '/api/employees/drafts/d-1/offer-compensation') return reply(HANDOFF);
    if (path === '/api/employees/drafts/d-1') return reply({
      summary: DRAFT,
      draft: { id: 'd-1', status: 'PendingHrApproval', englishName: 'Rana Saeed', arabicName: 'رنا سعيد', personalEmail: '', workEmail: 'rana.saeed@acme.test', phone: '', nationality: 'Saudi', countryCode: 'SA', department: 'IT', designation: 'Officer', branch: 'Riyadh HQ', workLocation: '', joiningDate: '2026-10-20', contractType: 'Unlimited', probationEndDate: null, salary: null },
      documentCount: 0, activationCheck: { canActivate: true, resolvedCompanyName: 'Acme Arabia', problems: [], advisories: [] },
    });
    if (path === '/api/employees/drafts') return reply({ items: [{ ...DRAFT, ...(options.draftItem ?? {}) }], total: 1, page: 1, pageSize: 25, counts: options.draftCounts ?? { awaitingApproval: 1, draft: 0, activated: 0, rejected: 0, cancelled: 0 } });

    // 1 + 10: Pay approvals
    if (path === '/api/pay-approvals' && request.method() === 'GET') return reply(options.payList ?? { items: [], count: 0, hasMore: false, requiresOtherReviewerCount: 0 });
    if (path === '/api/pay-approvals/41' && request.method() === 'GET') return reply(PAY_DETAIL);
    if (path === '/api/pay-approvals/41/decision') return reply({ decision: 'approved' });

    // 4: the two empty inboxes
    if (path.includes('/onboarding') && path.includes('worklist')) return reply({ items: [], total: 0, page: 1, pageSize: 100 });
    if (path === '/api/hr/profile-completion/inbox') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 25 });
  });
  await page.goto(url);
}

test.describe('1 Pay approvals: the next step after Approve', () => {
  for (const lang of LANGS) {
    test(`after Approve the page says who activates next (${lang})`, async ({ page }, info) => {
      await warm(page, '/pay-approvals/41');
      await boot(page, '/pay-approvals/41', { lang, permissions: ['payroll.approve', 'payroll.read', 'dashboard.read'], roles: ['Finance Approver'] });
      await page.locator('#pa-reason').fill('Checked the figures and the account holder name.');
      await page.getByRole('button', { name: lang === 'en' ? 'Approve' : 'موافقة', exact: true }).click({ timeout: 45_000 });
      const next = page.getByTestId('pay-approval-next-step');
      await expect(next).toBeVisible();
      await expect(next).toHaveText(lang === 'en' ? 'Next: a second HR person activates the employee from New hires.' : ar('Next: a second HR person activates the employee from New hires.'));
      await shot(page, 'pay-approval-next-step', lang, info);
    });
  }
});

test.describe('2 Employee completion follows the real status', () => {
  const PROFILE = '/people/901?section=access-onboarding';
  test.beforeEach(async ({ page }) => { await warm(page, PROFILE); });
  for (const lang of LANGS) {
    test(`an Active employee without a sign-in is not told to complete activation (${lang})`, async ({ page }, info) => {
      await boot(page, PROFILE, { lang, employeeStatus: 'Active', selfServiceAvailable: false });
      const section = page.getByTestId('employee-completion-handoff');
      await expect(section).toBeVisible({ timeout: 45_000 });
      const waiting = lang === 'en' ? 'HR must complete activation requirements first' : 'استكمال متطلبات التفعيل أولاً';
      await expect(section).toContainText(lang === 'en' ? 'has not set up their Self-Service sign-in yet' : 'لم يُعدّ هذا الموظف تسجيل دخوله');
      await expect(section).not.toContainText(waiting);
      await shot(page, 'completion-active', lang, info);
    });
  }
  test('a Draft employee still waits for the activation requirements', async ({ page }) => {
    await boot(page, PROFILE, { employeeStatus: 'Draft', selfServiceAvailable: false });
    await expect(page.getByTestId('employee-completion-handoff')).toContainText('HR must complete activation requirements first', { timeout: 45_000 });
  });
});

test.describe('3 New hires review: saving clears the confirmation', () => {
  for (const lang of LANGS) {
    test(`a note beside Save review says so (${lang})`, async ({ page }, info) => {
      await warm(page, '/people/new-hires');
      await boot(page, '/people/new-hires', { lang });
      await page.getByRole('button', { name: lang === 'en' ? 'Review and decide' : 'مراجعة واتخاذ القرار', exact: true }).click({ timeout: 45_000 });
      const note = page.getByTestId('handoff-save-note');
      await expect(note).toBeVisible({ timeout: 30_000 });
      await expect(note).toHaveText(lang === 'en' ? 'Saving changes clears the confirmation. Confirm every disposition again before you approve.' : ar('Saving changes clears the confirmation. Confirm every disposition again before you approve.'));
      await note.scrollIntoViewIfNeeded();
      await shot(page, 'new-hires-save-note', lang, info);
    });
  }
});

test.describe('4 empty onboarding screens say why and what next', () => {
  for (const lang of LANGS) {
    test(`Onboarding tasks: why it is empty and a link to the checklist templates for those who may open them (${lang})`, async ({ page }, info) => {
      await warm(page, '/people/onboarding-tasks');
      await boot(page, '/people/onboarding-tasks', { lang, roles: ['HR Manager'], permissions: [...HR_PERMS, 'onboarding.templates.manage'] });
      const empty = page.getByTestId('worklist-empty');
      await expect(empty).toBeVisible({ timeout: 45_000 });
      await expect(empty).toContainText(lang === 'en' ? 'appear here when a new hire is activated' : 'تظهر مهام الانضمام');
      const link = empty.getByRole('link');
      await expect(link).toHaveAttribute('href', '/setup?tab=onboardingChecklistTemplates');
      await shot(page, 'onboarding-tasks-empty', lang, info);
    });
    test(`Onboarding tasks: no link for someone who may not open the templates (${lang})`, async ({ page }) => {
      await warm(page, '/people/onboarding-tasks');
      await boot(page, '/people/onboarding-tasks', { lang, roles: ['HR Officer'], permissions: HR_PERMS });
      const empty = page.getByTestId('worklist-empty');
      await expect(empty).toBeVisible({ timeout: 45_000 });
      await expect(empty).toContainText(lang === 'en' ? 'appear here when a new hire is activated' : 'تظهر مهام الانضمام');
      await expect(empty.getByRole('link')).toHaveCount(0);
    });
    test(`Profile review: says where submissions come from (${lang})`, async ({ page }, info) => {
      await warm(page, '/people/onboarding-review');
      await boot(page, '/people/onboarding-review', { lang });
      const next = page.getByTestId('review-empty-next');
      await expect(next).toBeVisible({ timeout: 45_000 });
      await expect(next).toContainText(lang === 'en' ? "request an employee's details" : 'تطلب بيانات موظف');
      await shot(page, 'onboarding-review-empty', lang, info);
    });
  }
});

test.describe('5 /welcome masks the password by default', () => {
  test.use({ storageState: { cookies: [], origins: [] } });
  for (const lang of LANGS) {
    test(`type=password, a labelled toggle that works from the keyboard (${lang})`, async ({ page }, info) => {
      await page.addInitScript(l => localStorage.setItem('kynexone-locale-choice-v2', l), lang);
      await page.route('**/api/**', route => { const url = route.request().url(); return route.fulfill({ json: url.includes('password-policy') ? { minLength: 10 } : url.includes('welcome/redeem') ? { tenantSlug: 'acme' } : {} }); });
      await warm(page, '/welcome');
      await page.route('**/api/**', route => { const url = route.request().url(); return route.fulfill({ json: url.includes('password-policy') ? { minLength: 10 } : url.includes('welcome/redeem') ? { tenantSlug: 'acme' } : {} }); });
      // A hash-only change would not reload the document, and the slip's boot script runs only on a fresh load.
      await page.goto('/login');
      await page.goto('/welcome#e=noah%40acme.test&c=4821%207730&w=acme');
      const box = page.locator('#wc-pw');
      await expect(box).toBeVisible({ timeout: 45_000 });
      await expect(box).toHaveAttribute('type', 'password');
      await box.fill('Secret2026xyz');
      const toggle = page.getByTestId('welcome-password-toggle');
      await expect(toggle).toHaveAttribute('aria-label', lang === 'en' ? 'Show password' : /إظهار|عرض/);
      await shot(page, 'welcome-masked', lang, info);
      await toggle.focus();
      await page.keyboard.press('Enter');
      await expect(box).toHaveAttribute('type', 'text');
      await expect(toggle).toHaveAttribute('aria-pressed', 'true');
      await page.keyboard.press('Enter');
      await expect(box).toHaveAttribute('type', 'password');
      // Only one password-like input and it is never the visible code box.
      await expect(page.locator('input[autocomplete="new-password"]')).toHaveCount(1);
    });
  }
});

test.describe('6 Group Overview is not offered to a plain Employee', () => {
  async function menuLinks(page: Page, info: { project: { name: string } }) {
    if (info.project.name === 'phone') await page.getByRole('button', { name: /Open navigation|فتح التنقل|القائمة/ }).click({ timeout: 45_000 });
  }
  for (const lang of LANGS) {
    test(`an Employee in a Group account has no Group Overview link (${lang})`, async ({ page }, info) => {
      await warm(page, '/ess/jobs');
      await boot(page, '/ess/jobs', { lang, roles: ['Employee'], permissions: EMPLOYEE_PERMS, accountType: 'Group' });
      await menuLinks(page, info);
      // The control: the Self-Service entry is there, so an absent Group Overview is the role rule, not a menu that never rendered.
      await expect(page.getByText(lang === 'en' ? 'Self-Service' : 'الخدمة الذاتية', { exact: true }).first()).toBeVisible({ timeout: 45_000 });
      await expect(page.getByText(lang === 'en' ? 'Group Overview' : 'نظرة على المجموعة', { exact: true })).toHaveCount(0);
      await shot(page, 'employee-nav', lang, info);
    });
  }
  test('an HR Manager in a Group account still sees it', async ({ page }, info) => {
    await warm(page, '/people/onboarding-tasks');
    await boot(page, '/people/onboarding-tasks', { roles: ['HR Manager'], permissions: HR_PERMS, accountType: 'Group' });
    await menuLinks(page, info);
    await expect(page.getByText('Group Overview', { exact: true }).first()).toBeVisible({ timeout: 45_000 });
  });
});

// PHASE 2: the three read fields the API now adds. Each screen must also work when they are absent (an older API).
test.describe('8 the sign-in code button says what will happen', () => {
  const PROFILE = '/people/901?section=access-onboarding';
  test.beforeEach(async ({ page }) => { await warm(page, PROFILE); });
  for (const lang of LANGS) {
    const email = lang === 'en' ? 'Email sign-in code' : 'إرسال رمز التفعيل بالبريد الإلكتروني';
    const print = lang === 'en' ? 'Issue sign-in code and print slip' : ar('Issue sign-in code and print slip');
    const row = (page: Page) => page.getByRole('group', { name: /Employment and sign-in status|حالة التوظيف وتسجيل الدخول/ }).getByTestId('state-login');
    test(`Email: the label says it is emailed and no generic help line is shown (${lang})`, async ({ page }, info) => {
      await boot(page, PROFILE, { lang, employeeStatus: 'Draft', emailDelivery: true, signInCodeDelivery: 'Email' });
      await expect(row(page).getByRole('button', { name: email })).toBeVisible({ timeout: 45_000 });
      await expect(row(page).getByTestId('issue-code-help')).toHaveCount(0);
      await shot(page, 'signin-email', lang, info);
    });
    test(`PrintedSlip: the label says a slip is printed (${lang})`, async ({ page }, info) => {
      await boot(page, PROFILE, { lang, employeeStatus: 'Draft', emailDelivery: true, signInCodeDelivery: 'PrintedSlip' });
      await expect(row(page).getByRole('button', { name: print })).toBeVisible({ timeout: 45_000 });
      await expect(row(page).getByTestId('issue-code-help')).toHaveCount(0);
      await shot(page, 'signin-print', lang, info);
    });
    test(`field absent: the earlier label and help line are the fallback (${lang})`, async ({ page }) => {
      await boot(page, PROFILE, { lang, employeeStatus: 'Draft', emailDelivery: true });
      await expect(row(page).getByRole('button', { name: lang === 'en' ? 'Issue sign-in code' : 'إصدار رمز تسجيل الدخول' })).toBeVisible({ timeout: 45_000 });
      await expect(row(page).getByTestId('issue-code-help')).toBeVisible();
    });
  }
});

test.describe('9 New hires: the Activated tile counts Active employee records', () => {
  const ACTIVATED = { status: 'Activated', activatedEmployeeId: 901, activatedEmployeeCode: 'EMP-0049' };
  for (const lang of LANGS) {
    test(`an approved hire whose record is not Active is not counted or labelled Activated (${lang})`, async ({ page }, info) => {
      await warm(page, '/people/new-hires');
      await boot(page, '/people/new-hires', { lang, draftItem: { ...ACTIVATED, employeeStatus: 'Draft' }, draftCounts: { awaitingApproval: 0, draft: 0, activated: 1, activeEmployees: 0, rejected: 0, cancelled: 0 } });
      const tile = page.getByRole('group', { name: /Filter new hires by status|تصفية/ }).getByRole('button', { name: lang === 'en' ? /^Activated/ : /^مفعّل|^تم التفعيل/ });
      await expect(tile).toContainText('0', { timeout: 45_000 });
      await expect(page.getByText(lang === 'en' ? 'Record created, not active yet' : ar('Record created, not active yet')).locator('visible=true').first()).toBeVisible(); // the table on desktop, the card list on a phone
      await shot(page, 'new-hires-tile', lang, info);
    });
  }
  test('with an Active record the tile counts it; an older API without the fields keeps the old count', async ({ page }) => {
    await warm(page, '/people/new-hires');
    await boot(page, '/people/new-hires', { draftItem: { ...ACTIVATED, employeeStatus: 'Active' }, draftCounts: { awaitingApproval: 0, draft: 0, activated: 1, activeEmployees: 1, rejected: 0, cancelled: 0 } });
    const tile = page.getByRole('group', { name: /Filter new hires by status/ }).getByRole('button', { name: /^Activated/ });
    await expect(tile).toContainText('1', { timeout: 45_000 });
    await expect(page.getByRole('table').getByText('Record created, not active yet')).toHaveCount(0);
  });
  test('an older API without the fields keeps the old count and chip', async ({ page }) => {
    await warm(page, '/people/new-hires');
    await boot(page, '/people/new-hires', { draftItem: ACTIVATED, draftCounts: { awaitingApproval: 0, draft: 0, activated: 2, rejected: 0, cancelled: 0 } });
    const tile = page.getByRole('group', { name: /Filter new hires by status/ }).getByRole('button', { name: /^Activated/ });
    await expect(tile).toContainText('2', { timeout: 45_000 });
  });
});

test.describe('10 Pay approvals: why the reader cannot approve, per package', () => {
  const BLOCKED = [
    { employeeId: 41, employeeCode: 'EMP-0041', name: 'Layla Al-Harbi', companyName: 'Acme Arabia', blockedForCaller: 'EnteredByYou' },
    { employeeId: 42, employeeCode: 'EMP-0042', name: 'Omar Nasser', companyName: 'Acme Arabia', blockedForCaller: 'YouAreTheEmployee' },
    { employeeId: 43, employeeCode: 'EMP-0043', name: 'Sara Qadri', companyName: 'Acme Arabia', blockedForCaller: 'YouIssuedSignInCode' },
  ];
  const perms = ['payroll.approve', 'payroll.read', 'dashboard.read'];
  for (const lang of LANGS) {
    test(`each package names its own reason (${lang})`, async ({ page }, info) => {
      await warm(page, '/pay-approvals');
      await boot(page, '/pay-approvals', { lang, permissions: perms, payList: { items: [], count: 0, hasMore: false, requiresOtherReviewerCount: 3, blockedItems: BLOCKED } });
      const list = page.getByTestId('pay-approvals-blocked-list');
      await expect(list).toBeVisible({ timeout: 45_000 });
      for (const key of ['You entered this pay package.', 'You are the employee.', "You issued this employee's sign-in code."]) {
        await expect(list).toContainText(lang === 'en' ? key : ar(key));
      }
      await expect(list).toContainText('Layla Al-Harbi');
      await shot(page, 'pay-approvals-blocked', lang, info);
    });
  }
  test('an older API without blockedItems keeps the generic rule text and does not crash', async ({ page }) => {
    await warm(page, '/pay-approvals');
    await boot(page, '/pay-approvals', { permissions: perms, payList: { items: [], count: 0, hasMore: false, requiresOtherReviewerCount: 1 } });
    const notice = page.getByTestId('pay-approvals-hidden-notice');
    await expect(notice).toContainText('someone other than the person who entered it, the employee', { timeout: 45_000 });
    await expect(page.getByTestId('pay-approvals-blocked-list')).toHaveCount(0);
  });
});
