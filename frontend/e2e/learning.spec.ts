import { expect, test, type Page } from '@playwright/test';

/**
 * Learning screens (LRN) against synthetic intercepted API responses — no backend, no real tenant.
 *   npx next dev -p 5311 &
 *   LEARNING_BASE_URL=http://127.0.0.1:5311 npx playwright test -c e2e/playwright.learning.config.ts
 * Proves the UI contract (states, refusals rendered as sentences, Arabic RTL, 390px phone, permission gating, the
 * feature flag), not server authorization — that is LearningAuthorizationReportTests and learning-journey.spec.ts.
 */

const HR_PERMS = ['dashboard.read', 'employees.read', 'employees.write', 'learning.read', 'learning.manage', 'learning.assign', 'learning.report', 'learning.records.correct'];
const EMP_PERMS = ['dashboard.read', 'profile.read', 'ess.read', 'ess.write'];

const course = (over: Record<string, unknown> = {}) => ({
  id: 'c-1', code: 'FIRE', versionNumber: 1, status: 'Published', title: 'Fire safety', titleAr: 'السلامة من الحرائق', contentKind: 'Lesson',
  completionRule: 'Assessment', validityMonths: 12, issuesCertificate: true, questionCount: 2, publishedAtUtc: '2026-10-01T08:00:00Z',
  updatedAtUtc: '2026-10-01T08:00:00Z', version: 2, ...over,
});

const item = (id: string, sequence: number, over: Record<string, unknown> = {}) => ({
  id, sequence, courseCode: `C${sequence}`, courseVersion: 1, title: `Course ${sequence}`, titleAr: `الدورة ${sequence}`, contentKind: 'Lesson',
  completionRule: 'Acknowledgement', isRequired: true, status: 'NotStarted', isBlocked: false, blockedByTitle: null, blockedByTitleAr: null,
  progressPercent: null, scorePercent: null, attemptsUsed: 0, attemptAllowance: 0, estimatedMinutes: 10, firstLaunchedAtUtc: null,
  completedAtUtc: null, completionSource: null, nextAction: 'open', ...over,
});

const assignment = {
  id: 'a-1', employeeId: 7, employeeName: 'Liu Wei', employeeCode: 'E-7', pathId: 'p-1', pathName: 'Fire safety path', pathNameAr: 'مسار السلامة',
  status: 'InProgress', isMandatory: true, dueDate: '2026-10-05', isOverdue: true, cycle: 1, source: 'Employee', requiredCount: 2, requiredCompleted: 1,
  progressPercent: 50, assignedAtUtc: '2026-09-20T08:00:00Z', completedAtUtc: null, cancelReason: null,
  items: [
    item('e-1', 1, { status: 'Completed', nextAction: 'done', completedAtUtc: '2026-09-21T08:00:00Z', completionSource: 'Acknowledgement', firstLaunchedAtUtc: '2026-09-21T07:00:00Z' }),
    item('e-2', 2, { completionRule: 'Assessment', attemptAllowance: 3, nextAction: 'take_assessment', status: 'InProgress', firstLaunchedAtUtc: '2026-09-22T08:00:00Z' }),
  ],
};

/** A cmi5 unit whose completion the content reported: waiting for HR to verify it. */
const pendingAssignment = {
  ...assignment, id: 'a-2', employeeId: 8, employeeName: 'Omar Siddiqui', employeeCode: 'E-8', isOverdue: false, awaitingVerification: true, requiredCount: 1, requiredCompleted: 0,
  progressPercent: 0, items: [item('e-3', 1, {
    contentKind: 'Cmi5', completionRule: 'Cmi5MoveOn', status: 'PendingVerification', nextAction: 'pending_verification', completedAtUtc: '2026-10-10T08:00:00Z',
    completionSource: 'Cmi5', firstLaunchedAtUtc: '2026-10-10T07:00:00Z', progressPercent: 100,
  })],
};

const credential = {
  id: 'cr-1', certificateNumber: 'LRN-2026-ABCD2345', verificationCode: 'K7Q2M9X4PZ', status: 'Active', employeeId: 7, employeeName: 'Liu Wei',
  employeeNameAr: 'ليو وي', employeeCode: 'E-7', courseCode: 'C1', courseVersion: 1, courseTitle: 'Course 1', courseTitleAr: 'الدورة 1',
  pathName: 'Fire safety path', pathNameAr: 'مسار السلامة', completionSource: 'Acknowledgement', scorePercent: null, completedOn: '2026-09-21',
  expiresOn: null, issuedAtUtc: '2026-09-21T08:00:00Z', supersedesCredentialId: null, supersedeReason: null, supersededAtUtc: null,
  revokedAtUtc: null, revokeReason: null, companyName: 'IntelliFlow',
};

interface Options { perms?: string[]; locale?: 'en' | 'ar'; learningOff?: boolean; employeeId?: number }

async function boot(page: Page, options: Options = {}) {
  const errors: string[] = [];
  const writes: Array<{ method: string; path: string; body: unknown }> = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(({ locale }) => {
    localStorage.setItem('zayra_access_token', 'learning-fixture');
    localStorage.setItem('kynexone-locale-choice-v2', locale);
  }, { locale: options.locale ?? 'en' });

  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const p = new URL(request.url()).pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    let body: unknown = null;
    try { body = request.postDataJSON(); } catch { body = request.postData(); }
    if (method !== 'GET' && p.includes('/learning')) writes.push({ method, path: p, body });

    if (p === '/api/auth/me') {
      return reply({
        id: 'u-1', tenantId: 't-1', tenantSlug: 'intelliflow', fullName: 'Fixture User', roles: ['HR Manager'], permissions: options.perms ?? HR_PERMS,
        accountType: 'SingleCompany', isGroupScope: true, employeeId: options.employeeId ?? null,
        companies: [{ id: 'co-1', name: 'IntelliFlow', code: 'IFL', countryCode: 'SA', isActive: true }],
      });
    }
    if (p === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (p === '/api/tenant-admin/localization') return reply({ countryCode: 'SA', currencyCode: 'SAR', defaultTimezone: 'Asia/Riyadh' });
    if (p === '/api/features/disabled-keys') return reply(options.learningOff ? ['release_a', 'learning'] : ['release_a']);
    if (p === '/api/features/modules' || p === '/api/notifications') return reply([]);

    // ── HR ──
    if (p === '/api/learning/reports/summary') {
      return reply({ kpis: [
        { key: 'assigned', value: 2, definition: '', drillReport: 'status', drillFilter: null },
        { key: 'open', value: 1, definition: '', drillReport: 'status', drillFilter: 'open' },
        { key: 'completed', value: 1, definition: '', drillReport: 'status', drillFilter: 'Completed' },
        { key: 'overdue', value: 1, definition: '', drillReport: 'overdue', drillFilter: null },
        { key: 'expiring', value: 0, definition: '', drillReport: 'expiring', drillFilter: '30' },
        { key: 'locked', value: 0, definition: '', drillReport: 'status', drillFilter: 'locked' },
        { key: 'pending', value: 1, definition: '', drillReport: 'pending', drillFilter: null },
      ], completionRatePercent: 50, scope: 'organization', asOf: '2026-10-11' });
    }
    if (p === '/api/learning/reports/completion') {
      return reply([{ groupBy: 'path', groupId: 'p-1', group: 'Fire safety path', assigned: 2, completed: 1, inProgress: 1, notStarted: 0, overdue: 1, completionRatePercent: 50 }]);
    }
    if (p === '/api/learning/reports/overdue' || p === '/api/learning/reports/status') {
      return reply([{ assignmentId: 'a-1', employeeId: 7, employeeCode: 'E-7', employeeName: 'Liu Wei', departmentId: 'd-1', department: 'Operations', pathId: 'p-1',
        path: 'Fire safety path', cycle: 1, status: 'InProgress', mandatory: true, dueDate: '2026-10-05', overdue: true, requiredItems: 2, completedItems: 1,
        progressPercent: 50, assignedAtUtc: '2026-09-20T08:00:00Z', completedAtUtc: null, employeeStatus: 'Suspended', awaitingVerification: false }]);
    }
    if (p === '/api/learning/courses' && method === 'GET') return reply([course(), course({ id: 'c-2', code: 'INDUCT', title: 'Induction', status: 'Draft', completionRule: 'Acknowledgement', questionCount: 0, validityMonths: null })]);
    if (p === '/api/learning/courses' && method === 'POST') return reply({ error: 'course_code_taken', message: 'x' }, 409);
    if (p === '/api/learning/paths') {
      return reply([{ id: 'p-1', code: 'FIRE-PATH', name: 'Fire safety path', nameAr: 'مسار السلامة', description: null, status: 'Active', eligibility: {},
        effectiveFrom: null, effectiveTo: null, renewalWindowDays: 30, liveAssignments: 1, completedAssignments: 1, updatedAtUtc: '2026-10-01T08:00:00Z', version: 1,
        items: [{ id: 'i-1', sequence: 1, courseId: 'c-1', courseCode: 'FIRE', courseTitle: 'Fire safety', courseTitleAr: null, courseStatus: 'Published', hasPublishedVersion: true, isRequired: true, prerequisiteSequence: null }] }]);
    }
    if (p === '/api/learning/rules') return reply([]);
    if (p === '/api/learning/assignments' && method === 'POST') {
      return reply({ created: 1, skipped: 1, refused: 1, results: [
        { employeeId: 8, employeeName: 'Omar Siddiqui', outcome: 'created', reasons: [], assignmentId: 'a-9' },
        { employeeId: 7, employeeName: 'Liu Wei', outcome: 'skipped', reasons: ['already_assigned'], assignmentId: null },
        { employeeId: 9, employeeName: 'Sara Ahmadi', outcome: 'refused', reasons: ['department_not_eligible'], assignmentId: null },
      ] });
    }
    if (p === '/api/learning/assignments') return reply([assignment, pendingAssignment]);
    if (p === '/api/learning/enrollments/e-3/verify') {
      return reply({ ...pendingAssignment, status: 'Completed', requiredCompleted: 1, items: [{ ...pendingAssignment.items[0], status: 'Completed', nextAction: 'done' }] });
    }
    if (p === '/api/learning/credentials/cr-1/revoke') return reply({ ...credential, status: 'Revoked' });
    if (p === '/api/learning/reports/pending') {
      return reply([{ assignmentId: 'a-2', employeeId: 8, employeeCode: 'E-8', employeeName: 'Omar Siddiqui', departmentId: 'd-1', department: 'Operations', pathId: 'p-1',
        path: 'Fire safety path', cycle: 1, status: 'InProgress', mandatory: true, dueDate: '2026-10-05', overdue: false, requiredItems: 1, completedItems: 0,
        progressPercent: 0, assignedAtUtc: '2026-09-20T08:00:00Z', completedAtUtc: null, employeeStatus: 'Active', awaitingVerification: true }]);
    }
    if (p === '/api/learning/credentials') return reply([credential]);
    if (p === '/api/departments') return reply(paged([{ id: 'd-1', code: 'OPS', nameEn: 'Operations', nameAr: 'العمليات', isActive: true }]));
    if (p === '/api/designations' || p === '/api/grades' || p === '/api/companies') return reply(paged([]));

    // ── Employee ──
    if (p === '/api/ess/learning') return reply({ assignments: [assignment], credentials: [credential], transcript: [
      { enrollmentId: 'e-1', courseCode: 'C1', courseVersion: 1, title: 'Course 1', titleAr: 'الدورة 1', pathName: 'Fire safety path', pathNameAr: 'مسار السلامة', cycle: 1,
        completedAtUtc: '2026-09-21T08:00:00Z', completionSource: 'Acknowledgement', scorePercent: null, credentialId: 'cr-1', certificateNumber: 'LRN-2026-ABCD2345',
        credentialStatus: 'Active', expiresOn: null },
    ] });
    if (p === '/api/ess/learning/enrollments/e-2') {
      return reply({ item: assignment.items[1], assignmentId: 'a-1', assignmentStatus: 'InProgress', pathName: 'Fire safety path', pathNameAr: 'مسار السلامة',
        description: null, descriptionAr: null, passMarkPercent: 70, questionCount: 1, attempts: [], openAttemptId: null, credentialId: null, linkUrl: null });
    }
    if (p === '/api/ess/learning/enrollments/e-2/acknowledge') return reply({ error: 'completion_rule_mismatch', message: 'x' }, 409);
    if (p === '/api/ess/learning/enrollments/e-2/attempts') {
      return reply({ id: 'at-1', attemptNo: 1, status: 'InProgress', passMarkPercent: 70, attemptsUsed: 1, attemptAllowance: 3, startedAtUtc: '2026-10-11T08:00:00Z',
        questions: [{ id: 'q1', kind: 'Single', prompt: 'Which way out?', promptAr: 'ما طريق الخروج؟', options: [{ id: 'q1o1', text: 'Lift', textAr: null }, { id: 'q1o2', text: 'Stairs', textAr: null }] }] });
    }
    if (p === '/api/ess/learning/attempts/at-1/submit') {
      return reply({ attemptId: 'at-1', attemptNo: 1, status: 'Failed', scorePercent: 0, passMarkPercent: 70, correct: 0, total: 1, attemptsRemaining: 2,
        enrollmentStatus: 'InProgress', credentialId: null, certificateNumber: null });
    }
    if (p === '/api/ess/learning/enrollments/e-3') {
      return reply({ item: pendingAssignment.items[0], assignmentId: 'a-2', assignmentStatus: 'InProgress', pathName: 'Fire safety path', pathNameAr: 'مسار السلامة',
        description: null, descriptionAr: null, passMarkPercent: null, questionCount: 0, attempts: [], openAttemptId: null, credentialId: null, linkUrl: null });
    }
    if (p === '/api/ess/learning/enrollments/e-404') return reply({ error: 'not_found', message: 'Course was not found.' }, 404);
    if (p === '/api/ess/learning/credentials/cr-1') return reply(credential);
    return reply(paged([]));
  });
  return { errors, writes };
}

test.describe('HR learning screen', () => {
  test('overview: every KPI says what it counts and drills to its rows; CSV is offered', async ({ page }) => {
    const { errors } = await boot(page);
    await page.goto('/learning');
    await expect(page.getByTestId('kpi-overdue')).toContainText('Open mandatory assignments past their due date that still need the employee to act.');
    await page.getByTestId('kpi-overdue').getByRole('button', { name: /Show the 1 records/ }).click();
    await expect(page.getByTestId('learning-drill')).toContainText('Liu Wei');
    // A suspended person stays counted as overdue, labelled.
    await expect(page.getByTestId('learning-drill').getByTestId('employee-suspended')).toHaveText('Suspended');
    await expect(page.getByTestId('learning-drill').getByRole('button', { name: 'Download CSV' })).toBeVisible();
    await expect(page.getByTestId('completion-table')).toContainText('50.0%');
    expect(errors).toEqual([]);
  });

  test('courses: a published version offers "New version", a draft offers Publish; the editor validates before sending', async ({ page }) => {
    const { writes } = await boot(page);
    await page.goto('/learning?tab=courses');
    await expect(page.getByTestId('course-row-FIRE').getByRole('button', { name: 'New version' })).toBeVisible();
    await expect(page.getByTestId('course-row-FIRE').getByRole('button', { name: 'Edit draft' })).toHaveCount(0);
    await expect(page.getByTestId('course-row-INDUCT').getByRole('button', { name: 'Publish' })).toBeVisible();
    await page.getByRole('button', { name: 'New course' }).click();
    await page.getByTestId('course-save-draft').click();
    await expect(page.getByText('Use 2–40 letters, digits or dashes, e.g. FIRE-SAFETY.')).toBeVisible();
    await expect(page.getByText('Give the course a title.')).toBeVisible();
    expect(writes).toEqual([]);
    // A server refusal is a sentence, not a status code.
    await page.getByLabel('Code').fill('FIRE');
    await page.getByLabel('Title', { exact: true }).fill('Fire again');
    await page.getByLabel('Lesson text').fill('<p>x</p>');
    await page.getByTestId('course-save-draft').click();
    await expect(page.getByText('A course with this code already exists. Open it and create a new version instead.')).toBeVisible();
  });

  test('assign: the bulk result lists who was assigned, skipped and refused, with reasons', async ({ page }) => {
    const { writes } = await boot(page);
    await page.goto('/learning?tab=assign');
    const form = page.getByTestId('assign-form');
    await form.getByRole('radio', { name: 'Department' }).check();
    await form.locator('#as-target').selectOption({ label: 'Operations' });
    await form.getByTestId('assign-submit').click();
    await expect(page.getByTestId('assign-result')).toContainText('1 assigned · 1 already had it · 1 not assigned');
    await expect(page.getByTestId('assign-result')).toContainText('Their department is not eligible for this path');
    expect(writes.at(-1)).toMatchObject({ method: 'POST', path: '/api/learning/assignments', body: { targetKind: 'Department', targetId: 'd-1' } });
  });

  test('a completion reported by course content waits for HR: the KPI drills to it, HR verifies it in Records', async ({ page }) => {
    const { writes, errors } = await boot(page);
    await page.goto('/learning');
    await expect(page.getByTestId('kpi-pending')).toContainText('waiting for HR to verify it before a certificate is issued');
    await page.getByTestId('kpi-pending').getByRole('button', { name: /Show the 1 records/ }).click();
    await expect(page.getByTestId('learning-drill')).toContainText('Omar Siddiqui');
    // Past its due date, but only HR is left to act: "Waiting for HR to verify", not "Overdue".
    await expect(page.getByTestId('learning-drill').getByTestId('status-table')).toContainText('Waiting for HR to verify');
    await expect(page.getByTestId('learning-drill').getByTestId('status-table')).not.toContainText('Overdue');
    await page.goto('/learning?tab=records');
    await expect(page.getByTestId('records-table').getByRole('row', { name: /Omar Siddiqui/ })).toContainText('Waiting for HR to verify');
    await page.getByTestId('records-table').getByRole('row', { name: /Omar Siddiqui/ }).getByRole('button', { name: 'Open' }).click();
    const pending = page.getByTestId('pending-verification');
    await expect(pending).toContainText('You cannot verify your own record.');
    await expect(page.getByTestId('assignment-detail')).toContainText('Completed — reported by course content');
    await pending.getByTestId('verify-completion').click();
    await expect.poll(() => writes.map((w) => w.path)).toContain('/api/learning/enrollments/e-3/verify');
    expect(errors).toEqual([]);
  });

  test('revoking a certificate asks for the date by which the course must be completed again', async ({ page }) => {
    const { writes } = await boot(page);
    await page.goto('/learning?tab=records');
    await page.getByTestId('records-credentials').getByRole('button', { name: 'Revoke' }).click();
    await expect(page.getByTestId('revoke-due')).toHaveValue(/^\d{4}-\d{2}-\d{2}$/);
    await page.getByTestId('revoke-due').fill('2030-01-15');
    await page.getByLabel('Reason').fill('Licence withdrawn after incident');
    await page.getByTestId('reason-confirm').click();
    await expect.poll(() => writes.at(-1)).toMatchObject({
      path: '/api/learning/credentials/cr-1/revoke', body: { reason: 'Licence withdrawn after incident', dueDate: '2030-01-15' },
    });
  });

  test('the cmi5 editor asks for the unit\'s pass mark, offers HR verification, and refuses a certificate on "opening is enough"', async ({ page }) => {
    const { writes } = await boot(page);
    await page.goto('/learning?tab=courses');
    await page.getByRole('button', { name: 'New course' }).click();
    await page.getByLabel('Code').fill('UNIT-1');
    await page.getByLabel('Title', { exact: true }).fill('Fire unit');
    await page.getByRole('radio', { name: 'Learning unit (cmi5)' }).check();
    await expect(page.getByTestId('course-hr-verification')).toBeChecked();
    await page.getByLabel('Counts as complete when the unit reports').selectOption('Passed');
    await expect(page.getByLabel('Pass mark the unit must report (%)')).toBeVisible();
    await page.getByLabel('Counts as complete when the unit reports').selectOption('NotApplicable');
    await page.getByTestId('course-save-draft').click();
    await expect(page.getByText('A unit that counts as complete when opened cannot issue a certificate or expire. Choose another rule, or turn the certificate off.')).toBeVisible();
    expect(writes).toEqual([]);
  });

  test('a user without learning permissions is turned away from /learning', async ({ page }) => {
    await boot(page, { perms: EMP_PERMS, employeeId: 7 });
    await page.goto('/learning');
    await page.waitForURL(/\/dashboard/);
  });

  test('with the learning flag off, the screen says so instead of half-working', async ({ page }) => {
    await boot(page, { learningOff: true });
    await page.goto('/learning');
    await expect(page.getByTestId('learning-disabled')).toContainText('Learning is not switched on for your organisation yet.');
  });
});

test.describe('My learning', () => {
  test('assignments, overdue, next steps, certificates and the transcript', async ({ page }) => {
    const { errors } = await boot(page, { perms: EMP_PERMS, employeeId: 7 });
    await page.goto('/ess/learning');
    const card = page.getByTestId('learning-assignment');
    await expect(card).toContainText('Overdue since');
    await expect(card).toContainText('1 of 2 required courses completed');
    await expect(card.getByRole('link', { name: /Take the assessment/ })).toBeVisible();
    await expect(page.getByTestId('learning-transcript')).toContainText('Acknowledged');
    expect(errors).toEqual([]);
  });

  test('a completion the course content reported shows as waiting for HR, with nothing for the learner to do', async ({ page }) => {
    const { errors } = await boot(page, { perms: EMP_PERMS, employeeId: 7 });
    await page.goto('/ess/learning/e-3');
    await expect(page.getByTestId('course-pending-verification')).toContainText('HR checks it before your certificate is issued');
    await expect(page.getByTestId('course-completed')).toHaveCount(0);
    expect(errors).toEqual([]);
  });

  test('an assessment cannot be submitted half-answered, and a fail says how many attempts are left', async ({ page }) => {
    await boot(page, { perms: EMP_PERMS, employeeId: 7 });
    await page.goto('/ess/learning/e-2');
    await page.getByTestId('start-attempt').click();
    await expect(page.getByTestId('submit-attempt')).toBeDisabled();
    await page.getByRole('radio', { name: 'Lift' }).check();
    await page.getByTestId('submit-attempt').click();
    await expect(page.getByTestId('attempt-result')).toContainText('You have 2 attempts left.');
  });

  test('someone else\'s course is "not found", with a way to retry', async ({ page }) => {
    await boot(page, { perms: EMP_PERMS, employeeId: 7 });
    await page.goto('/ess/learning/e-404');
    await expect(page.getByRole('alert').filter({ hasText: 'This record no longer exists, or you do not have access to it.' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();
  });

  test('Arabic: right-to-left with Arabic words', async ({ page }) => {
    await boot(page, { perms: EMP_PERMS, employeeId: 7, locale: 'ar' });
    await page.goto('/ess/learning');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    await expect(page.getByRole('heading', { name: 'تعلّمي' })).toBeVisible();
    await expect(page.getByTestId('learning-assignment')).toContainText('مسار السلامة');
    await page.goto('/ess/learning/certificates/cr-1');
    await expect(page.getByText('شهادة إتمام')).toBeVisible();
    await expect(page.getByTestId('certificate-person')).toHaveText('ليو وي');
  });

  test('a 390px phone: no sideways scrolling on My learning or a course', async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
    const page = await context.newPage();
    await boot(page, { perms: EMP_PERMS, employeeId: 7 });
    for (const url of ['/ess/learning', '/ess/learning/e-2']) {
      await page.goto(url);
      await expect(page.locator('main')).toBeVisible();
      await page.waitForTimeout(300);
      expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth), url).toBeLessThanOrEqual(1);
    }
    await context.close();
  });
});
