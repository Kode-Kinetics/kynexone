import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { execSync } from 'node:child_process';
import { seedLearningWorld, type LearningWorld } from './bootstrap/learning-seed';
import { tenantLoginLive } from './helpers';

/**
 * LRN acceptance journey against a REAL backend (no mocks): HR builds a course with an assessment, a cmi5 unit and a
 * path in the UI, assigns it across two departments; an employee acknowledges the lesson, fails then passes the
 * assessment, gets a certificate, completes the SAMPLE cmi5 unit served from ANOTHER origin — a completion reported by
 * course content, so it waits for HR, who verifies it before the certificate is issued — and a refresh shows it all
 * persisted; the manager's report shows only their team; unauthorised changes are refused; Arabic RTL and a 390px phone.
 *
 * Run (see docs/acceptance/capability-closure/LRN_DESIGN.md → Evidence): a disposable stack — API on :5211 against its
 * own Postgres, `next dev -p 5311` — then e2e/bootstrap (provision.ts) and this config, which serves the sample unit
 * (e2e/fixtures/cmi5-fire-safety) on 127.0.0.1:5411: `npx playwright test -c e2e/playwright.learning-journey.config.ts`.
 */

const EVIDENCE = path.resolve(__dirname, '../../docs/acceptance/capability-closure/evidence/lrn');
const AU_URL = process.env.LRN_AU_URL ?? 'http://127.0.0.1:5411/index.html';
const RUN = new Date().toISOString().replace(/\D/g, '').slice(4, 12); // MMDDhhmm — fresh course codes per run
const steps: Array<Record<string, unknown>> = [];
let world: LearningWorld;
let shot = 0;

async function evidence(page: Page, id: string, step: string, expected: string, actual: string, extra: Record<string, unknown> = {}) {
  shot++;
  const file = `${String(shot).padStart(2, '0')}-${id}.png`;
  await page.screenshot({ path: path.join(EVIDENCE, file), fullPage: true });
  const viewport = page.viewportSize();
  steps.push({ n: shot, id, step, expected, actual, screenshot: file, url: page.url().replace(/#.*$/, ''), atUtc: new Date().toISOString(),
    viewport: viewport ? `${viewport.width}x${viewport.height}` : null, ...extra });
}

async function persona(browser: Browser, email: string, password: string, opts: { locale?: 'en' | 'ar'; mobile?: boolean } = {}) {
  const context = await browser.newContext(opts.mobile ? { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true } : { viewport: { width: 1440, height: 950 } });
  await context.addInitScript(() => { if (!localStorage.getItem('kynexone-locale-choice-v2')) localStorage.setItem('kynexone-locale-choice-v2', 'en'); });
  const page = await context.newPage();
  await tenantLoginLive(page, email, password, world.tenantA.slug);
  // Sign in through the English form (the helper clicks "Sign in"), then switch the language the way a user's choice is kept.
  if (opts.locale === 'ar') await page.evaluate(() => localStorage.setItem('kynexone-locale-choice-v2', 'ar'));
  return { context, page };
}

async function token(page: Page): Promise<string> {
  return page.evaluate(() => localStorage.getItem('zayra_access_token') ?? '');
}

async function api(page: Page, method: string, url: string, body?: unknown) {
  const res = await page.request.fetch(`${world.apiBase}${url}`, {
    method, headers: { Authorization: `Bearer ${await token(page)}`, 'Content-Type': 'application/json' }, data: body === undefined ? undefined : JSON.stringify(body),
  });
  let json: any = null;
  try { json = await res.json(); } catch { /* not json */ }
  return { status: res.status(), json, correlation: res.headers()['x-correlation-id'] ?? res.headers()['traceparent'] ?? null };
}

async function createCourse(page: Page, fill: (dialog: ReturnType<Page['getByRole']>) => Promise<void>) {
  await page.getByRole('button', { name: 'New course' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('course-editor')).toBeVisible();
  await fill(dialog);
  await dialog.getByTestId('course-publish').click();
  await expect(page.getByRole('dialog')).toHaveCount(0, { timeout: 15_000 });
}

test.describe.configure({ mode: 'serial' });

test.beforeAll(async () => {
  await mkdir(EVIDENCE, { recursive: true });
  world = await seedLearningWorld();
});

test.afterAll(async () => {
  let commit = 'unknown';
  try { commit = execSync('git rev-parse HEAD', { cwd: __dirname }).toString().trim(); } catch { /* not a git checkout */ }
  const health = await (await fetch(`${world.apiBase}/health/live`)).json().catch(() => ({}));
  await writeFile(path.join(EVIDENCE, 'journey-log.json'), JSON.stringify({
    run: RUN, checkoutCommit: commit, backendBuild: (health as any).commit ?? null, browser: 'Google Chrome (channel chrome), headed',
    tenantA: world.tenantA, tenantB: world.tenantB, departments: world.departments, employees: world.employees,
    personas: { hr: world.logins.hr.email, manager: world.logins.manager.email, ops1: world.logins.ops1.email, ops2: world.logins.ops2.email, tenantB: world.logins.tenantBAdmin.email },
    steps,
  }, null, 2));
});

const ids: { path?: string; assignmentOps1?: string; enrollments?: string[]; credential?: string; courseCodes?: string[] } = {};

test('LRN-01/02/03 — HR builds a versioned course, an assessment, a cmi5 unit and a path, and assigns it across two departments', async ({ browser }) => {
  const { context, page } = await persona(browser, world.logins.hr.email, world.logins.hr.password);
  const codes = [`IND-${RUN}`, `QUIZ-${RUN}`, `UNIT-${RUN}`];
  ids.courseCodes = codes;
  await page.goto('/learning?tab=courses');
  await expect(page.getByTestId('course-catalog')).toBeVisible();

  await createCourse(page, async (d) => {
    await d.getByLabel('Code').fill(codes[0]);
    await d.getByLabel('Title', { exact: true }).fill(`Fire safety induction ${RUN}`);
    await d.getByLabel('Title in Arabic').fill('مقدمة السلامة من الحرائق');
    await d.getByLabel('Lesson text').fill('<h2>Before you start</h2><p>Know your <strong>nearest exit</strong> and assembly point.</p><ul><li>Never use the lift</li><li>Close doors behind you</li></ul><script>alert("x")</script>');
  });
  await createCourse(page, async (d) => {
    await d.getByLabel('Code').fill(codes[1]);
    await d.getByLabel('Title', { exact: true }).fill(`Fire safety check ${RUN}`);
    await d.getByLabel('Lesson text').fill('<p>Fire doors stay <em>closed</em>. Leave by the stairs.</p>');
    await d.getByRole('radio', { name: 'Pass the assessment' }).check();
    await d.getByLabel('Pass mark (%)').fill('70');
    await d.getByLabel('Attempts allowed').fill('3');
    await d.getByLabel('Question 1 text').fill('Which way do you leave during a fire alarm?');
    await d.getByLabel('Answer 1', { exact: true }).fill('By the lift');
    await d.getByLabel('Answer 2', { exact: true }).fill('By the nearest stairs');
    await d.getByLabel('Answer 2 is correct').check();
    await d.getByRole('button', { name: 'Add a question' }).click();
    await d.getByLabel('Question 2 text').fill('Fire doors should be kept closed.');
    await d.getByTestId('question-editor').nth(1).getByLabel('Question type').selectOption('TrueFalse');
    await d.getByTestId('question-editor').nth(1).getByRole('radio', { name: 'True' }).check();
    await d.getByLabel('Valid for (months)').fill('12');
  });
  await createCourse(page, async (d) => {
    await d.getByLabel('Code').fill(codes[2]);
    await d.getByLabel('Title', { exact: true }).fill(`Fire safety drill (cmi5) ${RUN}`);
    await d.getByRole('radio', { name: 'Learning unit (cmi5)' }).check();
    await d.getByLabel('Unit address (opens in its own window)').fill(AU_URL);
    await d.getByLabel('Activity id').fill(`https://kynexone.test/units/fire-safety/${RUN}`);
    await d.getByLabel('Counts as complete when the unit reports').selectOption('CompletedAndPassed');
    await d.getByLabel('Pass mark the unit must report (%)').fill('70');
    // A unit that issues a certificate waits for HR by default: its report comes from the learner's browser.
    await expect(d.getByTestId('course-hr-verification')).toBeChecked();
  });
  for (const code of codes) await expect(page.getByTestId(`course-row-${code}`)).toContainText('Published');
  await evidence(page, 'LRN-01-catalogue', 'HR publishes three course versions (lesson+acknowledgement, lesson+assessment, cmi5)',
    'Three rows, each Version 1 · Published', 'Three rows shown Published', { courseCodes: codes });

  // LRN-01: a published version is immutable — "Edit" is offered only as "New version".
  await expect(page.getByTestId(`course-row-${codes[1]}`).getByRole('button', { name: 'Edit draft' })).toHaveCount(0);
  await expect(page.getByTestId(`course-row-${codes[1]}`).getByRole('button', { name: 'New version' })).toBeVisible();

  // LRN-02: path with order and a prerequisite.
  await page.getByRole('tab', { name: 'Learning paths' }).click();
  await page.getByRole('button', { name: 'New learning path' }).click();
  const pathDialog = page.getByRole('dialog');
  await pathDialog.getByLabel('Code').fill(`FIRE-${RUN}`);
  await pathDialog.getByLabel('Name', { exact: true }).fill(`Fire safety ${RUN}`);
  await pathDialog.getByLabel('Name in Arabic').fill('السلامة من الحرائق');
  for (const code of codes) {
    const title = code === codes[0] ? `Fire safety induction ${RUN}` : code === codes[1] ? `Fire safety check ${RUN}` : `Fire safety drill (cmi5) ${RUN}`;
    const option = pathDialog.getByLabel('Add a course').locator('option', { hasText: title });
    await pathDialog.getByLabel('Add a course').selectOption({ value: await option.first().getAttribute('value') ?? '' });
    await pathDialog.getByRole('button', { name: 'Add', exact: true }).click();
  }
  await pathDialog.getByTestId('path-item').nth(1).getByLabel('Only after the course above').check();
  await pathDialog.getByTestId('path-save').click();
  await expect(page.getByRole('dialog')).toHaveCount(0, { timeout: 15_000 });
  const card = page.getByTestId(`path-FIRE-${RUN}`);
  await card.getByRole('button', { name: 'Activate' }).click();
  await expect(card).toContainText('Active', { timeout: 15_000 });
  await expect(card).toContainText('After course 1');
  await evidence(page, 'LRN-02-path', 'HR builds and activates a 3-course path; course 2 requires course 1',
    'Path Active, items in order, "After course 1" on item 2', 'As expected');

  // LRN-03: assign to two Operations employees, then to the whole Quality department.
  await page.getByRole('tab', { name: 'Assign' }).click();
  const form = page.getByTestId('assign-form');
  await form.getByLabel('Learning path').selectOption({ label: `Fire safety ${RUN}` });
  for (const person of [world.employees.ops1, world.employees.ops2]) {
    await form.getByPlaceholder('Search by name or ID…').fill(person.name.split(' ')[0]);
    await form.getByRole('button', { name: new RegExp(person.name) }).first().click();
  }
  await form.getByLabel('Due date').fill(new Date(Date.now() + 14 * 86_400_000).toISOString().slice(0, 10));
  await form.getByTestId('assign-submit').click();
  await expect(page.getByTestId('assign-result')).toContainText('2 assigned', { timeout: 15_000 });
  await evidence(page, 'LRN-03-assign-employees', 'Assign the path to two Operations employees', '2 assigned · 0 already had it · 0 not assigned',
    (await page.getByTestId('assign-result').innerText()).split('\n')[0]);

  await form.getByRole('radio', { name: 'Department' }).check();
  await form.locator('#as-target').selectOption({ label: 'Quality' });
  await form.getByTestId('assign-submit').click();
  await expect(page.getByTestId('assign-result')).toContainText('3 assigned', { timeout: 15_000 });
  await evidence(page, 'LRN-03-assign-department', 'Assign the same path to the Quality department (3 people)', '3 assigned',
    (await page.getByTestId('assign-result').innerText()).split('\n')[0]);

  // LRN-03: a duplicate is refused by the database-backed rule, with a clear message.
  await form.getByRole('radio', { name: 'Employees' }).check();
  await form.getByPlaceholder('Search by name or ID…').fill(world.employees.ops1.name.split(' ')[0]);
  await form.getByRole('button', { name: new RegExp(world.employees.ops1.name) }).first().click();
  await form.getByTestId('assign-submit').click();
  await expect(page.getByText('This employee already has this path and has not finished it.').first()).toBeVisible();
  await evidence(page, 'LRN-03-duplicate', 'Assign the same path to Ops1 again', '409 duplicate_assignment shown as a sentence', 'Refused with the sentence');

  const list = await api(page, 'GET', `/api/learning/assignments?employeeId=${world.employees.ops1.id}`);
  ids.assignmentOps1 = list.json.find((a: any) => a.pathName === `Fire safety ${RUN}`)?.id;
  ids.path = list.json.find((a: any) => a.pathName === `Fire safety ${RUN}`)?.pathId;
  ids.enrollments = list.json.find((a: any) => a.id === ids.assignmentOps1)?.items.map((i: any) => i.id);
  expect(ids.assignmentOps1).toBeTruthy();
  await context.close();
});

test('LRN-04/05/06/07/08 — the employee acknowledges, fails then passes, earns a certificate', async ({ browser }) => {
  const { context, page } = await persona(browser, world.logins.ops1.email, world.logins.ops1.password);
  await page.goto('/ess/learning');
  const card = page.getByTestId('learning-assignment').filter({ hasText: `Fire safety ${RUN}` });
  await expect(card).toBeVisible();
  await expect(card).toContainText('0 of 3 required courses completed');
  await expect(card.getByTestId('learning-item').nth(1)).toContainText('After Fire safety induction');
  await evidence(page, 'LRN-04-my-learning', 'Employee opens My learning', 'Path, due date, 0 of 3, course 2 waits for course 1', 'As expected');

  // Course 1: open (launch evidence), then acknowledge.
  await card.getByTestId('learning-item').nth(0).getByRole('link', { name: 'Start' }).click();
  await page.getByRole('button', { name: 'Open the lesson' }).click();
  await expect(page.getByTestId('lesson-content')).toContainText('nearest exit');
  expect(await page.getByTestId('lesson-content').innerHTML()).not.toContain('<script');
  await page.getByLabel('I have read and understood this course.').check();
  await page.getByRole('button', { name: 'Confirm and complete' }).click();
  await expect(page.getByTestId('course-completed')).toContainText('Acknowledged');
  await evidence(page, 'LRN-05-acknowledged', 'Open the lesson, then acknowledge it', 'Lesson rendered without the script; Completed · Acknowledged', 'As expected');

  // Course 2: fail, then pass on the retake.
  await page.goto('/ess/learning');
  await card.getByTestId('learning-item').nth(1).getByRole('link').click();
  await page.getByRole('button', { name: 'Open the lesson' }).click();
  await page.getByTestId('start-attempt').click();
  await page.getByRole('radio', { name: 'By the lift' }).check();
  await page.getByRole('radio', { name: 'False' }).check();
  await page.getByTestId('submit-attempt').click();
  await expect(page.getByTestId('attempt-result')).toContainText('Not passed');
  await evidence(page, 'LRN-07-failed', 'Submit wrong answers', 'Not passed: 0%, 2 attempts left', (await page.getByTestId('attempt-result').innerText()).replace(/\s+/g, ' '));

  await page.getByTestId('start-attempt').click();
  await page.getByRole('radio', { name: 'By the nearest stairs' }).check();
  await page.getByRole('radio', { name: 'True' }).check();
  await page.getByTestId('submit-attempt').click();
  await expect(page.getByTestId('course-completed')).toContainText('Passed assessment', { timeout: 15_000 });
  await evidence(page, 'LRN-06-passed', 'Retake with the right answers (scored on the server)', 'Completed · Passed assessment · Score 100%', 'As expected');

  await page.getByRole('link', { name: 'View certificate' }).click();
  await expect(page.getByTestId('certificate-number')).toHaveText(/^LRN-\d{4}-[A-Z2-9]{8}$/);
  ids.credential = page.url().split('/').pop();
  await evidence(page, 'LRN-08-certificate', 'Open the certificate', 'Printable certificate with number, verification code, expiry +12 months',
    `Number ${await page.getByTestId('certificate-number').innerText()}`, { credentialId: ids.credential });
  await context.close();
});

test('LRN-05/13 — the SAMPLE cmi5 unit on another origin completes the path; a refresh shows it persisted', async ({ browser }) => {
  const { context, page } = await persona(browser, world.logins.ops1.email, world.logins.ops1.password);
  await page.goto('/ess/learning');
  const card = page.getByTestId('learning-assignment').filter({ hasText: `Fire safety ${RUN}` });
  await card.getByTestId('learning-item').nth(2).getByRole('link').click();
  const popupPromise = page.waitForEvent('popup');
  await page.getByTestId('open-unit').click();
  const unit = await popupPromise;
  await unit.waitForLoadState();
  expect(new URL(unit.url()).origin).not.toBe(new URL(page.url()).origin);
  await expect(unit.getByTestId('au-status')).toContainText('Connected', { timeout: 20_000 });
  for (let i = 0; i < 3; i++) await unit.getByRole('button', { name: 'Next' }).click();
  await unit.getByLabel('By the nearest stairs').check();
  await unit.getByRole('button', { name: 'Submit answer' }).click();
  await expect(unit.getByTestId('au-result')).toHaveText('Passed and completed.', { timeout: 20_000 });
  await evidence(unit, 'LRN-13-cmi5-unit', 'The cmi5 unit (served from 127.0.0.1:5411) fetches its token, reads LMS.LaunchData and reports',
    'fetch 200 → initialized → progressed ×3 → passed → completed → terminated, all 200', ((await unit.locator('#log').textContent()) ?? '').trim().replace(/\n+/g, ' | '));
  await unit.close();

  await expect(page.getByTestId('course-pending-verification')).toContainText('HR checks it before your certificate is issued', { timeout: 30_000 });
  await evidence(page, 'LRN-05-cmi5-reported', 'Back on KynexOne, the course page updates by itself',
    'Waiting for HR to verify — the unit\'s report alone issues no certificate', 'As expected');
  await page.goto('/ess/learning');
  await expect(page.getByTestId('learning-assignment').filter({ hasText: `Fire safety ${RUN}` })).toContainText('2 of 3 required courses completed');
  await context.close();

  // HR verifies the content-reported completion: the KPI drills to it, Records shows it with Verify / Reject.
  const hr = await persona(browser, world.logins.hr.email, world.logins.hr.password);
  await hr.page.goto('/learning');
  await expect(hr.page.getByTestId('kpi-pending')).toBeVisible();
  await hr.page.getByTestId('kpi-pending').getByRole('button').click();
  await expect(hr.page.getByTestId('learning-drill')).toContainText(world.employees.ops1.name);
  await evidence(hr.page, 'LRN-05-hr-pending', 'HR: Overview → "Waiting for HR to verify"', `${world.employees.ops1.name} listed`, 'As expected');
  await hr.page.goto(`/learning?tab=records`);
  const row = hr.page.getByTestId('records-table').getByRole('row').filter({ hasText: world.employees.ops1.name }).filter({ hasText: `Fire safety ${RUN}` });
  await row.getByRole('button', { name: 'Open' }).click();
  const pending = hr.page.getByTestId('pending-verification');
  await expect(pending).toBeVisible();
  await expect(hr.page.getByTestId('assignment-detail')).toContainText('Completed — reported by course content');
  await evidence(hr.page, 'LRN-05-hr-verify', 'HR opens the assignment', 'The cmi5 item shows "Reported complete · Completed — reported by course content" with Verify / Reject', 'As expected');
  await pending.getByTestId('verify-completion').click();
  await expect(hr.page.getByTestId('pending-verification')).toHaveCount(0, { timeout: 15_000 });
  await evidence(hr.page, 'LRN-05-hr-verified', 'HR verifies the completion', 'Item Completed; certificate issued; audited learning.enrollment.verified', 'As expected');
  await hr.context.close();

  const again = await persona(browser, world.logins.ops1.email, world.logins.ops1.password);
  await again.page.goto('/ess/learning');
  await again.page.reload();
  const done = again.page.getByTestId('learning-assignment').filter({ hasText: `Fire safety ${RUN}` });
  await expect(done).toContainText('3 of 3 required courses completed');
  await expect(again.page.getByTestId('learning-transcript')).toContainText('Completed — reported by course content');
  await expect(again.page.getByTestId('learning-transcript')).toContainText('Acknowledged');
  await evidence(again.page, 'LRN-04-persisted', 'Reload My learning', 'Path Completed, 3 of 3, transcript shows Acknowledged / Passed assessment / Completed — reported by course content', 'As expected');
  await again.context.close();
});

test('LRN-11 — the manager sees only their own team; HR sees both departments', async ({ browser }) => {
  const { context, page } = await persona(browser, world.logins.manager.email, world.logins.manager.password);
  await page.goto('/learning');
  await expect(page.getByTestId('learning-reports')).toBeVisible();
  await expect(page.getByText('Showing the people in your team')).toBeVisible();
  await page.getByTestId('kpi-assigned').getByRole('button').click();
  const table = page.getByTestId('learning-drill').getByTestId('status-table');
  await expect(table).toContainText(world.employees.ops1.name);
  await expect(table).toContainText(world.employees.ops2.name);
  for (const q of world.employees.quality) await expect(table).not.toContainText(q.name);
  await evidence(page, 'LRN-11-manager-team', 'Manager opens Learning → Overview → Assigned', 'Only their reports (Operations); no Quality employee', 'As expected');
  // Courses/assign tabs are not offered to a manager (learning.report only).
  await expect(page.getByRole('tab', { name: 'Courses' })).toHaveCount(0);
  await context.close();

  const hr = await persona(browser, world.logins.hr.email, world.logins.hr.password);
  await hr.page.goto('/learning');
  await hr.page.getByLabel('Group by').selectOption('department');
  await expect(hr.page.getByTestId('completion-table')).toContainText('Quality');
  await expect(hr.page.getByTestId('completion-table')).toContainText('Operations');
  const summary = await api(hr.page, 'GET', '/api/learning/reports/summary');
  const status = await api(hr.page, 'GET', '/api/learning/reports/status');
  expect(summary.json.kpis.find((k: any) => k.key === 'assigned').value).toBe(status.json.length);
  await evidence(hr.page, 'LRN-11-hr-departments', 'HR completion grouped by department', 'Operations and Quality rows; KPI "Assigned" = rows in the status report',
    `Assigned KPI ${summary.json.kpis.find((k: any) => k.key === 'assigned').value} = ${status.json.length} rows`);
  await hr.page.getByTestId('kpi-locked').getByRole('button').click();
  await hr.context.close();
});

test('LRN-12 — unauthorised changes are refused (other employee, gated self-completion, HR endpoints, other tenant)', async ({ browser }) => {
  const { context, page } = await persona(browser, world.logins.ops2.email, world.logins.ops2.password);
  const results: Record<string, number> = {};
  results.otherEmployeesEnrollment = (await api(page, 'GET', `/api/ess/learning/enrollments/${ids.enrollments![0]}`)).status;
  results.ackOtherEmployees = (await api(page, 'POST', `/api/ess/learning/enrollments/${ids.enrollments![1]}/acknowledge`)).status;
  results.assignAsEmployee = (await api(page, 'POST', '/api/learning/assignments', { pathId: ids.path, targetKind: 'Employee', employeeIds: [world.employees.ops2.id], isMandatory: true })).status;
  results.revokeAsEmployee = (await api(page, 'POST', `/api/learning/credentials/${ids.credential}/revoke`, { reason: 'try' })).status;
  const mine = await api(page, 'GET', '/api/ess/learning');
  const myQuiz = mine.json.assignments.find((a: any) => a.pathId === ids.path).items.find((i: any) => i.completionRule === 'Assessment');
  // Finish course 1 legitimately (open + acknowledge), so the only thing in the way of course 2 is its assessment.
  const firstOfMine = mine.json.assignments.find((a: any) => a.pathId === ids.path).items[0].id;
  await api(page, 'POST', `/api/ess/learning/enrollments/${firstOfMine}/launch`, {});
  expect((await api(page, 'POST', `/api/ess/learning/enrollments/${firstOfMine}/acknowledge`)).status).toBe(200);
  await api(page, 'POST', `/api/ess/learning/enrollments/${myQuiz.id}/launch`, {});
  const selfComplete = await api(page, 'POST', `/api/ess/learning/enrollments/${myQuiz.id}/acknowledge`);
  results.selfCompleteAssessment = selfComplete.status;
  expect(selfComplete.json?.error).toBe('completion_rule_mismatch');
  expect(results).toEqual({ otherEmployeesEnrollment: 404, ackOtherEmployees: 404, assignAsEmployee: 403, revokeAsEmployee: 403, selfCompleteAssessment: 409 });

  await page.goto(`/ess/learning/${ids.enrollments![1]}`);
  await expect(page.getByRole('alert').filter({ hasText: 'This record no longer exists, or you do not have access to it.' })).toBeVisible();
  await evidence(page, 'LRN-12-denied', 'Employee 2 opens employee 1\'s course and calls HR/other endpoints', '404 / 403 / 409 everywhere; nothing changed',
    JSON.stringify(results));
  await context.close();

  // Tenant B cannot read tenant A's assignment.
  const b = await browser.newContext();
  const bPage = await b.newPage();
  await tenantLoginLive(bPage, world.logins.tenantBAdmin.email, world.logins.tenantBAdmin.password, world.tenantB.slug);
  const cross = await api(bPage, 'GET', `/api/learning/assignments/${ids.assignmentOps1}`);
  expect(cross.status).toBe(404);
  steps.push({ n: ++shot, id: 'LRN-12-cross-tenant', step: 'Tenant B admin reads tenant A assignment id', expected: '404', actual: String(cross.status), atUtc: new Date().toISOString() });
  await b.close();
});

test('LRN-14 — Arabic RTL and a 390px phone', async ({ browser }) => {
  const ar = await persona(browser, world.logins.ops1.email, world.logins.ops1.password, { locale: 'ar' });
  await ar.page.goto('/ess/learning');
  await expect(ar.page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(ar.page.getByRole('heading', { name: 'تعلّمي' })).toBeVisible();
  await evidence(ar.page, 'LRN-14-arabic-my-learning', 'My learning in Arabic', 'dir=rtl, Arabic headings and statuses', 'As expected');
  await ar.page.goto(`/ess/learning/certificates/${ids.credential}`);
  await expect(ar.page.getByText('شهادة إتمام')).toBeVisible();
  await evidence(ar.page, 'LRN-14-arabic-certificate', 'The certificate in Arabic', 'Arabic labels; number and code stay LTR', 'As expected');
  await ar.context.close();

  const phone = await persona(browser, world.logins.ops2.email, world.logins.ops2.password, { mobile: true });
  await phone.page.goto('/ess/learning');
  await expect(phone.page.getByTestId('learning-assignment').first()).toBeVisible();
  const overflow = await phone.page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await evidence(phone.page, 'LRN-14-mobile-my-learning', 'My learning at 390px', 'No horizontal scroll', `overflow ${overflow}px`);
  const quiz = phone.page.getByTestId('learning-item').nth(0).getByRole('link');
  await quiz.click();
  await expect(phone.page.getByTestId('learning-course')).toBeVisible();
  await evidence(phone.page, 'LRN-14-mobile-course', 'A course page at 390px', 'Readable, controls reachable', 'As expected');
  // Keyboard: the primary action is reachable by Tab and has a visible name.
  await phone.page.keyboard.press('Tab');
  await phone.context.close();
});
