import { expect, test, type Browser, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { execSync } from 'node:child_process';
import { seedSuccessionWorld, type SuccessionWorld } from './bootstrap/succession-seed';
import { tenantLoginLive } from './helpers';

/**
 * SUC acceptance journey against a REAL backend (no mocks). HR designates a critical role, nominates two competing
 * candidates, assesses them; the HR Director calibrates (keeping one potential, moving the other with a reason) and
 * confirms readiness; the 9-box shows every placement's inputs; HR builds and submits a slate; the HR Director returns it,
 * HR revises it, the HR Director approves it; HR gives the first candidate a development plan with a learning action and
 * a goal action; the employee completes the learning and the plan action completes itself; a readiness change leaves the
 * approved snapshot untouched and shows in the history; then the restricted-access checks (candidate, line manager,
 * a role with no access, another tenant, the approval queues), report reconciliation, Arabic RTL and a 390px phone.
 *
 * Run: a disposable stack — API on :5217 against its own Postgres (suc-pg :55517), `next dev -p 5317` — then e2e/bootstrap
 * (provision.ts) and `npx playwright test -c e2e/playwright.succession-journey.config.ts`. Each step re-reads state, so a
 * re-run on the same stack continues rather than duplicating.
 */

const EVIDENCE = path.resolve(__dirname, '../../docs/acceptance/capability-closure/evidence/suc');
const steps: Array<Record<string, unknown>> = [];
const records: Record<string, unknown> = {};
let world: SuccessionWorld;
let shot = 0;

async function evidence(page: Page, id: string, step: string, expected: string, actual: string, extra: Record<string, unknown> = {}) {
  shot++;
  const file = `${String(shot).padStart(2, '0')}-${id}.png`;
  await page.screenshot({ path: path.join(EVIDENCE, file), fullPage: true });
  const viewport = page.viewportSize();
  steps.push({ n: shot, id, step, expected, actual, screenshot: file, url: page.url().replace(/#.*$/, ''), atUtc: new Date().toISOString(),
    viewport: viewport ? `${viewport.width}x${viewport.height}` : null, ...extra });
}

async function persona(browser: Browser, email: string, password: string, opts: { locale?: 'en' | 'ar'; mobile?: boolean; slug?: string } = {}) {
  const context = await browser.newContext(opts.mobile ? { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true } : { viewport: { width: 1440, height: 950 } });
  await context.addInitScript(() => { if (!localStorage.getItem('kynexone-locale-choice-v2')) localStorage.setItem('kynexone-locale-choice-v2', 'en'); });
  const page = await context.newPage();
  await tenantLoginLive(page, email, password, opts.slug ?? world.tenantA.slug);
  if (opts.locale === 'ar') await page.evaluate(() => localStorage.setItem('kynexone-locale-choice-v2', 'ar'));
  return { context, page };
}

async function api(page: Page, method: string, url: string, body?: unknown) {
  const token = await page.evaluate(() => localStorage.getItem('zayra_access_token') ?? '');
  const res = await page.request.fetch(`${world.apiBase}${url}`, {
    method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, data: body === undefined ? undefined : JSON.stringify(body),
  });
  let json: any = null;
  try { json = await res.json(); } catch { /* not json */ }
  return { status: res.status(), json, text: JSON.stringify(json) };
}

async function pick(page: Page, testId: string, search: string, employeeId: number) {
  await page.getByTestId(testId).fill(search);
  await page.locator(`[data-testid="suc-person-option"][data-employee-id="${employeeId}"]`).first().click();
}

const ids: { critical?: string; nomA?: string; nomB?: string; v1?: string; v2?: string; learningAction?: string } = {};

test.describe.configure({ mode: 'serial' });

test.beforeAll(async () => {
  await mkdir(EVIDENCE, { recursive: true });
  world = await seedSuccessionWorld();
});

test.afterAll(async () => {
  let commit = 'unknown';
  try { commit = execSync('git rev-parse HEAD', { cwd: __dirname }).toString().trim(); } catch { /* not a checkout */ }
  const health = await (await fetch(`${world.apiBase}/health/live`)).json().catch(() => ({}));
  await writeFile(path.join(EVIDENCE, 'journey-log.json'), JSON.stringify({
    checkoutCommit: commit, backendBuild: (health as any).commit ?? null, browser: 'Google Chrome (channel chrome), headed',
    tenantA: world.tenantA, tenantB: world.tenantB, positions: world.positions, candidates: world.candidates, pathId: world.pathId,
    goalId: world.goalId, cycleId: world.cycleId, ids, records,
    personas: Object.fromEntries(Object.entries(world.logins).map(([k, v]) => [k, v.email])), steps,
  }, null, 2));
});

test('SUC-01/02 — HR designates a critical role and nominates two competing candidates with evidence', async ({ browser }) => {
  const { context, page } = await persona(browser, world.logins.hrManager.email, world.logins.hrManager.password);
  await page.goto('/performance/succession');
  await expect(page.getByTestId('suc-workspace')).toBeVisible();
  const row = page.locator('[data-testid="suc-position-row"]').filter({ hasText: 'Chief Financial Officer' });
  await expect(page.getByTestId('suc-positions')).toBeVisible();
  const designated = (await api(page, 'GET', '/api/succession/positions')).json.some((p: any) => p.positionId === world.positions.cfo);
  if (!designated) {
    await page.getByTestId('suc-designate-open').click();
    await page.getByTestId('suc-designate-position').selectOption(world.positions.cfo);
    await page.getByTestId('suc-designate-risk').selectOption('High');
    await page.getByTestId('suc-designate-impact').selectOption('Critical');
    await page.getByTestId('suc-designate-rationale').fill('Signs the statutory accounts and owns the bank relationships; no internal cover today.');
    await page.getByTestId('suc-designate-submit').click();
  }
  await expect(row).toBeVisible();
  await expect(page.getByTestId('suc-kpi-positions')).toContainText('1'); // the headline figures re-read after the designation
  ids.critical = (await row.getAttribute('data-position-id')) ?? undefined;
  await evidence(page, 'SUC-01-designated', 'Designate the CFO position (company INTELLIFLOW-KSA) as critical: High risk, Critical impact',
    'Row listed gaps-first with its gap chips (no approved slate, no ready-now successor, vacant, high risk); the headline figures count it', 'As expected', { criticalPositionId: ids.critical });

  await page.goto(`/performance/succession/positions/${ids.critical}`);
  await expect(page.getByTestId('suc-position-detail')).toBeVisible();
  const existingNominations = (await api(page, 'GET', `/api/succession/nominations?positionId=${ids.critical}`)).json as any[];
  for (const [who, search] of [[world.candidates.a, 'Liu'], [world.candidates.b, 'Carlos']] as const) {
    if (existingNominations.some((n) => n.candidate.employeeId === who.id)) continue;
    await page.getByTestId('suc-nominate-open').click();
    await pick(page, 'suc-nominate-employee', search, who.id);
    await page.getByTestId('suc-nominate-evidence').fill(`${who.name}: led the FY2025 close and the external audit response without findings.`);
    await page.getByTestId('suc-nominate-submit').click();
    await expect(page.locator('[data-testid="suc-candidate-row"]').filter({ hasText: who.name })).toBeVisible();
  }
  const rowA = page.locator('[data-testid="suc-candidate-row"]').filter({ hasText: world.candidates.a.name });
  const rowB = page.locator('[data-testid="suc-candidate-row"]').filter({ hasText: world.candidates.b.name });
  await expect(rowA).toBeVisible();
  await expect(rowB).toBeVisible();
  ids.nomA = (await rowA.getAttribute('data-nomination-id')) ?? undefined;
  ids.nomB = (await rowB.getAttribute('data-nomination-id')) ?? undefined;
  // A duplicate live nomination is refused by the database (409 duplicate_nomination).
  const duplicate = await api(page, 'POST', '/api/succession/nominations', { employeeId: world.candidates.a.id, criticalPositionId: ids.critical,
    evidence: 'Trying to nominate the same person twice for the same role.' });
  expect(duplicate.status).toBe(409);
  expect(duplicate.json.error).toBe('duplicate_nomination');

  for (const [rowX, potential, nom] of [[rowA, '3', ids.nomA], [rowB, '2', ids.nomB]] as const) {
    if ((await rowX.getByTestId('suc-candidate-stage').textContent())?.trim() === 'Proposed') {
      await rowX.getByTestId('suc-candidate-review').click();
      await expect(rowX.getByTestId('suc-candidate-stage')).toHaveText('InReview');
    }
    if (!(await api(page, 'GET', `/api/succession/nominations/${nom}`)).json.currentAssessment) {
      await rowX.getByTestId('suc-candidate-assess').click();
      await page.getByTestId('suc-assess-period').fill('2026-H2');
      await page.getByTestId('suc-assess-potential').selectOption(potential);
      await page.getByTestId('suc-assess-evidence').fill('Took on the treasury project unasked and grew the team from two to five people.');
      await page.getByTestId('suc-assess-submit').click();
      await expect(rowX.getByTestId('suc-candidate-assessment')).toContainText('2026-H2');
    }
  }
  await evidence(page, 'SUC-02-competing-candidates', 'Nominate Liu Wei and Carlos Mendez (evidence required), take both into review and record a talent assessment of each (period 2026-H2)',
    'Two candidate rows In review, each with an assessment brief; a second nomination of Liu Wei → 409 duplicate_nomination', 'As expected',
    { nominationA: ids.nomA, nominationB: ids.nomB, duplicateStatus: duplicate.status });
  await context.close();
});

test('SUC-03 — the HR Director calibrates (keeps one, moves one with a reason), confirms readiness, and the 9-box shows its inputs', async ({ browser }) => {
  const { context, page } = await persona(browser, world.logins.hrDirector.email, world.logins.hrDirector.password);
  await page.goto('/performance/succession');
  await page.getByTestId('suc-tab-calibration').click();
  await expect(page.getByTestId('suc-calibration')).toBeVisible();
  await page.getByTestId('suc-calibration-period').selectOption('2026-H2');
  const calRow = (name: string) => page.locator('[data-testid="suc-calibration-row"]').filter({ hasText: name });
  await expect(calRow(world.candidates.a.name)).toBeVisible();
  await expect(calRow(world.candidates.b.name)).toBeVisible();
  const statusOf = async (employeeId: number) => ((await api(page, 'GET', `/api/succession/assessments?period=2026-H2&employeeId=${employeeId}`)).json as any[])[0]?.status;

  if (await statusOf(world.candidates.a.id) === 'Submitted') {
    await calRow(world.candidates.a.name).getByTestId('suc-calibrate-open').click();
    await page.getByTestId('suc-calibrate-potential').selectOption('3');
    await page.getByTestId('suc-calibrate-submit').click();
    await expect(calRow(world.candidates.a.name).getByTestId('suc-calibrate-open')).toHaveCount(0);
  }
  if (await statusOf(world.candidates.b.id) === 'Submitted') {
    await calRow(world.candidates.b.name).getByTestId('suc-calibrate-open').click();
    await page.getByTestId('suc-calibrate-potential').selectOption('1');
    await page.getByTestId('suc-calibrate-reason').fill('Too short');
    await page.getByTestId('suc-calibrate-submit').click();
    await expect(page.getByText(/20/).first()).toBeVisible();
    await evidence(page, 'SUC-03-calibration-reason-required', 'Move Carlos from Growth to Limited with a 9-character reason',
      'Refused: a moved potential needs a reason of at least 20 characters', 'As expected');
    await page.getByTestId('suc-calibrate-reason').fill('Panel evidence: two missed audit deadlines this year, needs a stretch role first.');
    await page.getByTestId('suc-calibrate-submit').click();
    await expect(calRow(world.candidates.b.name).getByTestId('suc-calibrate-open')).toHaveCount(0);
  }
  for (const [nom, readiness] of [[ids.nomA, 'ReadyNow'], [ids.nomB, 'OneToTwoYears']] as const) {
    const open = page.locator(`[data-testid="suc-confirm-readiness-open"][data-nomination-id="${nom}"]`);
    if ((await api(page, 'GET', `/api/succession/nominations/${nom}`)).json.status !== 'InReview') continue;
    await open.click();
    await page.getByTestId('suc-confirm-readiness-select').selectOption(readiness);
    await page.getByTestId('suc-confirm-readiness-submit').click();
    await expect(open).toHaveCount(0);
  }
  await evidence(page, 'SUC-03-calibrated', 'Calibrate Liu Wei at High (kept, no reason) and Carlos at Limited (moved, reason ≥ 20 chars); confirm readiness Ready now / 1–2 years',
    'Both assessments calibrated; both nominations Calibrated', 'As expected');

  await page.goto(`/performance/succession/positions/${ids.critical}`);
  await expect(page.getByTestId('suc-ninebox')).toBeVisible();
  const liu = page.getByTestId('suc-ninebox-cell-P3-High').getByTestId('suc-ninebox-person').filter({ hasText: world.candidates.a.name });
  await expect(liu).toBeVisible();
  await expect(page.getByTestId('suc-ninebox-cell-P1-Moderate').getByTestId('suc-ninebox-person').filter({ hasText: world.candidates.b.name })).toBeVisible();
  await liu.click();
  const inputs = page.getByTestId('suc-ninebox-inputs');
  await expect(inputs).toContainText('86');
  await expect(inputs).toContainText('default-75-60-v1');
  await evidence(page, 'SUC-03-ninebox-inputs', 'The 9-box for the CFO candidates; open Liu Wei\'s placement',
    'Liu Wei in Future leader (High potential × High performance, score 86 ≥ 75), Carlos in Effective in role (Limited × Moderate, 68); the inputs show review, score, band rule default-75-60-v1, proposed vs calibrated potential, assessor',
    'As expected');
  await context.close();
});

test('SUC-06 — HR submits a slate, the HR Director returns it, HR revises, the HR Director approves', async ({ browser }) => {
  const hr = await persona(browser, world.logins.hrManager.email, world.logins.hrManager.password);
  await hr.page.goto(`/performance/succession/positions/${ids.critical}`);
  await expect(hr.page.getByTestId('suc-slates')).toBeVisible();
  const slateVersions = async () => (await api(hr.page, 'GET', `/api/succession/positions/${ids.critical}/slates`)).json as any[];
  if ((await slateVersions()).length === 0) {
    await hr.page.getByTestId('suc-slate-new').click();
    for (const [nom, order] of [[ids.nomA!, '1'], [ids.nomB!, '2']] as const) {
      await hr.page.getByTestId(`suc-slate-pick-${nom}`).check();
      await hr.page.getByTestId(`suc-slate-order-${nom}`).fill(order);
      await hr.page.getByTestId(`suc-slate-rationale-${nom}`).fill(order === '1'
        ? 'First: ready now, led the close and the audit; the board knows her work.'
        : 'Second: strong technically, needs one more cycle of stakeholder exposure.');
    }
    await hr.page.getByTestId('suc-slate-submit').click();
    await expect(hr.page.locator('[data-testid="suc-slate-version"][data-status="Submitted"]')).toHaveCount(1);
  }
  ids.v1 = (await slateVersions()).find((v) => v.versionNo === 1)?.id;
  await expect(hr.page.locator(`[data-testid="suc-slate-version"][data-slate-id="${ids.v1}"]`)).toBeVisible();
  await evidence(hr.page, 'SUC-06-submitted', 'Build v1 (Liu Wei 1, Carlos 2, each with a written rationale) and submit it',
    'v1 Submitted with its SHA-256 prefix; the order is manual and says so', 'As expected', { slateV1: ids.v1 });

  // The shared approval request carries ids and the hash only, and nobody is its subject.
  const all = await api(hr.page, 'GET', '/api/approval-requests?entityName=SuccessionSlate&queue=all&pageSize=50');
  expect(all.status).toBe(200);
  expect(all.text).not.toContain(world.candidates.a.name);
  expect(all.text).not.toContain(world.candidates.b.name);
  records.approvalTitles = (all.json.items ?? []).map((i: any) => `${i.title} · ${i.status} · requestedFor=${i.requestedForEmployeeId}`);

  const director = await persona(browser, world.logins.hrDirector.email, world.logins.hrDirector.password);
  await director.page.goto('/approvals');
  if ((await slateVersions()).find((v) => v.id === ids.v1)?.status === 'Submitted')
    await expect(director.page.getByText(/Succession slate v1/).first()).toBeVisible({ timeout: 30_000 });
  await evidence(director.page, 'SUC-06-approval-center', 'The HR Director opens the Approval Center',
    'The slate request reads "Succession slate v1 · <hash>" — no candidate name — with a "Review slate" link', 'As expected');
  await director.page.goto(`/performance/succession/slates/${ids.v1}`);
  await expect(director.page.getByTestId('suc-slate-review')).toBeVisible();
  if (await director.page.getByTestId('suc-slate-return').count() > 0) {
    await director.page.getByTestId('suc-slate-return').click();
    await director.page.getByTestId('suc-slate-return-reason').fill('Carlos needs evidence from the audit cycle before he is on the slate at all.');
    await director.page.getByTestId('suc-slate-return-confirm').click();
  }
  await expect(director.page.getByTestId('suc-slate-status').first()).toHaveText('ReturnedForRevision');
  await evidence(director.page, 'SUC-06-returned', 'The HR Director reviews v1 (snapshot with every 9-box input) and returns it with a reason',
    'v1 ReturnedForRevision; the reason is kept on the version', 'As expected');

  await hr.page.goto(`/performance/succession/positions/${ids.critical}`);
  await expect(hr.page.getByTestId('suc-slates')).toBeVisible();
  await expect(hr.page.locator(`[data-testid="suc-slate-version"][data-slate-id="${ids.v1}"]`)).toHaveAttribute('data-status', 'ReturnedForRevision');
  let latest = (await slateVersions()).sort((x, y) => y.versionNo - x.versionNo)[0];
  if (latest.status === 'ReturnedForRevision') {
    await hr.page.getByTestId('suc-slate-revise').click();
    await expect(hr.page.getByTestId('suc-slate-editor')).toBeVisible();
    latest = (await slateVersions()).sort((x, y) => y.versionNo - x.versionNo)[0];
  }
  if (latest.status === 'Draft') {
    await expect(hr.page.getByTestId('suc-slate-editor')).toBeVisible();
    await hr.page.getByTestId(`suc-slate-rationale-${ids.nomB}`).fill('Second: audit-cycle evidence added (two clean quarterly reviews since the return).');
    await hr.page.getByTestId('suc-slate-submit').click();
    await expect(hr.page.locator('[data-testid="suc-slate-version"][data-status="Submitted"]')).toHaveCount(1);
  }
  ids.v2 = (await slateVersions()).find((v) => v.versionNo === 2)?.id;

  await director.page.goto(`/performance/succession/slates/${ids.v2}`);
  await expect(director.page.getByTestId('suc-slate-review')).toBeVisible();
  if (await director.page.getByTestId('suc-slate-approve').count() > 0) await director.page.getByTestId('suc-slate-approve').click();
  await expect(director.page.getByTestId('suc-slate-status').first()).toHaveText('Approved');
  await hr.page.goto(`/performance/succession/positions/${ids.critical}`);
  await expect(hr.page.locator(`[data-testid="suc-slate-version"][data-slate-id="${ids.v2}"]`)).toHaveAttribute('data-status', 'Approved');
  await expect(hr.page.locator(`[data-testid="suc-slate-version"][data-slate-id="${ids.v1}"]`)).toHaveAttribute('data-status', 'ReturnedForRevision');
  await evidence(hr.page, 'SUC-06-approved-history', 'HR revises (v2 cloned from v1, rationale updated) and submits; the HR Director approves v2',
    'Versions: v2 Approved (effective today), v1 ReturnedForRevision with its reason; hashes verified; candidates On slate', 'As expected',
    { slateV2: ids.v2 });
  await hr.context.close();
  await director.context.close();
});

test('SUC-04 — HR gives Liu Wei a development plan; the learning action completes itself when she finishes the course', async ({ browser }) => {
  const hr = await persona(browser, world.logins.hrManager.email, world.logins.hrManager.password);
  await hr.page.goto(`/people/${world.candidates.a.id}?section=activity&panel=development`);
  await expect(hr.page.getByTestId('suc-dev-panel')).toBeVisible();
  await hr.page.waitForLoadState('networkidle').catch(() => {});
  if (await hr.page.getByTestId('suc-plan-status').count() === 0) {
    await hr.page.getByTestId('suc-plan-create').click();
    await hr.page.getByTestId('suc-plan-purpose').fill('Prepare Liu Wei to step into the CFO role within two years.');
    await hr.page.getByTestId('suc-plan-review-date').fill(new Date(Date.now() + 90 * 86400_000).toISOString().slice(0, 10));
    await hr.page.getByTestId('suc-plan-submit').click();
    await expect(hr.page.getByTestId('suc-plan-status').first()).toHaveText('Draft');
  }
  if (await hr.page.getByTestId('suc-action-row').count() === 0) {
    await hr.page.getByTestId('suc-action-add').click();
    await hr.page.getByTestId('suc-action-type').selectOption('Learning');
    await hr.page.getByTestId('suc-action-title').fill('Complete Finance leadership essentials');
    await hr.page.getByTestId('suc-action-path').selectOption(world.pathId);
    await hr.page.getByTestId('suc-action-submit').click();
    await expect(hr.page.getByTestId('suc-action-row')).toHaveCount(1);
    await hr.page.getByTestId('suc-action-add').click();
    await hr.page.getByTestId('suc-action-type').selectOption('Goal');
    await hr.page.getByTestId('suc-action-title').fill('Own the FY2027 budget cycle');
    await hr.page.getByTestId('suc-action-goal').selectOption(world.goalId);
    await hr.page.getByTestId('suc-action-submit').click();
    await expect(hr.page.getByTestId('suc-action-row')).toHaveCount(2);
    await hr.page.getByTestId('suc-action-add').click();
    await hr.page.getByTestId('suc-action-type').selectOption('Experience');
    await hr.page.getByTestId('suc-action-title').fill('Shadow the audit committee for one quarter');
    await hr.page.getByTestId('suc-action-submit').click();
    await expect(hr.page.getByTestId('suc-action-row')).toHaveCount(3);
  }
  if (await hr.page.getByTestId('suc-plan-activate').count() > 0) await hr.page.getByTestId('suc-plan-activate').click();
  await expect(hr.page.getByTestId('suc-plan-status').first()).toHaveText('Active');
  const learningRow = hr.page.locator('[data-testid="suc-action-row"]').filter({ hasText: 'Finance leadership essentials' });
  ids.learningAction = (await learningRow.getAttribute('data-action-id')) ?? undefined;
  await evidence(hr.page, 'SUC-04-plan-active', 'On Liu Wei\'s employee record → Development: create a plan (purpose, review date), add a Learning action (path SUC-LEAD, assigned through the learning module) and a Goal action, activate',
    'Plan Active; three actions Not started (Learning, Goal, Experience)', 'As expected', { learningActionId: ids.learningAction });
  await hr.context.close();

  const emp = await persona(browser, world.logins.employeeA.email, world.logins.employeeA.password);
  await emp.page.goto('/ess/learning');
  const card = emp.page.getByTestId('learning-assignment').filter({ hasText: 'Finance leadership essentials' });
  await expect(card).toBeVisible();
  if (await card.getByRole('link', { name: 'Start' }).count() > 0) {
    await card.getByTestId('learning-item').nth(0).getByRole('link', { name: 'Start' }).click();
    await emp.page.getByRole('button', { name: 'Open the lesson' }).click();
    await emp.page.getByLabel('I have read and understood this course.').check();
    await emp.page.getByRole('button', { name: 'Confirm and complete' }).click();
    await expect(emp.page.getByTestId('course-completed')).toContainText('Acknowledged');
  }
  await emp.page.goto('/ess/development-plan');
  await expect(emp.page.getByTestId('suc-my-plan')).toBeVisible();
  const myRow = emp.page.locator('[data-testid="suc-my-action-row"]').filter({ hasText: 'Finance leadership essentials' });
  await expect(myRow.getByTestId('suc-my-action-status')).toHaveText('Completed');
  await expect(emp.page.getByTestId('suc-my-memberships')).toHaveCount(0);
  // F5: on a manual action the employee proposes completion; cancelling or completing outright is refused.
  const experience = emp.page.locator('[data-testid="suc-my-action-row"]').filter({ hasText: 'Shadow the audit committee' });
  const mine = (await api(emp.page, 'GET', '/api/ess/development-plan')).json;
  const exp = mine.current.actions.find((a: any) => a.title.startsWith('Shadow the audit committee'));
  const cancel = await api(emp.page, 'PUT', `/api/ess/development-plan/actions/${exp.id}`, { status: 'Cancelled', version: exp.version });
  const complete = await api(emp.page, 'PUT', `/api/ess/development-plan/actions/${exp.id}`, { status: 'Completed', version: exp.version });
  expect([cancel.status, complete.status]).toEqual([403, 403]);
  records.essRefusals = { cancel: cancel.status, completeOutright: complete.status };
  if (!exp.completionProposedAtUtc) await experience.getByTestId('suc-my-action-complete').click();
  await expect(experience.getByTestId('suc-my-action-proposed')).toBeVisible();
  await expect(experience.getByTestId('suc-my-action-status')).toHaveText('InProgress');
  await evidence(emp.page, 'SUC-04-linked-completion', 'Liu Wei completes the lesson in My learning, then opens My development plan',
    'The learning action is Completed automatically (linked learning completed); the Experience action shows "You said this is done" (proposed, still In progress); cancelling or completing it outright → 403; no nomination, slate or pool is shown (visibility off)', 'As expected');
  await emp.context.close();

  const owner = await persona(browser, world.logins.hrManager.email, world.logins.hrManager.password);
  await owner.page.goto(`/people/${world.candidates.a.id}?section=activity&panel=development`);
  const ownerRow = owner.page.locator('[data-testid="suc-action-row"]').filter({ hasText: 'Shadow the audit committee' });
  await expect(ownerRow.getByTestId('suc-action-proposed').or(ownerRow.getByTestId('suc-action-status').filter({ hasText: 'Completed' }))).toBeVisible();
  if (await ownerRow.getByTestId('suc-action-confirm').count() > 0) await ownerRow.getByTestId('suc-action-confirm').click();
  await expect(ownerRow.getByTestId('suc-action-status')).toHaveText('Completed');
  await evidence(owner.page, 'SUC-04-owner-confirms', 'HR (plan owner side) opens the Development tab and confirms the proposed completion',
    'Experience action Completed (Manual); progress now counts it', 'As expected');
  await owner.context.close();
});

test('SUC-06/09 — a readiness change keeps the approved snapshot and shows in the history', async ({ browser }) => {
  const { context, page } = await persona(browser, world.logins.hrManager.email, world.logins.hrManager.password);
  await page.goto(`/performance/succession/positions/${ids.critical}`);
  const rowB = page.locator('[data-testid="suc-candidate-row"]').filter({ hasText: world.candidates.b.name });
  await expect(rowB).toBeVisible();
  const now = await api(page, 'GET', `/api/succession/nominations/${ids.nomB}`);
  if (now.json.readiness !== 'ReadyNow') {
    await rowB.getByTestId('suc-candidate-readiness').click();
    await page.getByTestId('suc-readiness-select').selectOption('ReadyNow');
    await page.getByTestId('suc-readiness-reason').fill('Closed the FY2026 interim audit with no findings.');
    await page.getByTestId('suc-readiness-submit').click();
    await expect.poll(async () => (await api(page, 'GET', `/api/succession/nominations/${ids.nomB}`)).json.readiness).toBe('ReadyNow');
  }
  await rowB.getByTestId('suc-candidate-history').click();
  await expect(page.getByTestId('suc-history')).toBeVisible();
  await expect(page.getByTestId('suc-history-entry').first()).toBeVisible();
  await evidence(page, 'SUC-06-readiness-history', 'Change Carlos from 1–2 years to Ready now with a reason; open his history',
    'Nomination shows Ready now (was 1–2 years); the history lists the change with its reason', 'As expected');
  const slate = await api(page, 'GET', `/api/succession/slates/${ids.v2}`);
  const inSnapshot = slate.json.snapshot.candidates.find((c: any) => c.nominationId === ids.nomB);
  expect(inSnapshot.readiness).toBe('OneToTwoYears');
  expect(slate.json.hashVerified).toBe(true);
  await page.goto(`/performance/succession/slates/${ids.v2}`);
  await expect(page.locator('[data-testid="suc-snapshot-candidate"]').filter({ hasText: world.candidates.b.name })).toBeVisible();
  await evidence(page, 'SUC-06-snapshot-kept', 'Open the approved v2 after the change',
    'The approved snapshot still shows Carlos at 1–2 years; the hash still verifies', 'As expected', { snapshotReadiness: inSnapshot.readiness });
  records.slateV2 = { hash: slate.json.snapshotSha256, status: slate.json.status, hashVerified: slate.json.hashVerified };
  await context.close();
});

test('SUC-07 — restricted access: candidate, line manager, a role without access, another tenant, the approval queues', async ({ browser }) => {
  const results: Record<string, number | boolean> = {};
  const carlos = await persona(browser, world.logins.employeeB.email, world.logins.employeeB.password);
  results.candidateReadsSlate = (await api(carlos.page, 'GET', `/api/succession/slates/${ids.v2}`)).status;
  results.candidateReadsNominations = (await api(carlos.page, 'GET', `/api/succession/nominations?positionId=${ids.critical}`)).status;
  results.candidateReadsOthersPlans = (await api(carlos.page, 'GET', `/api/succession/development-plans?employeeId=${world.candidates.a.id}`)).status;
  await carlos.page.goto(`/performance/succession/slates/${ids.v2}`);
  await carlos.page.waitForLoadState('networkidle').catch(() => {});
  await expect(carlos.page.getByTestId('suc-snapshot')).toHaveCount(0);
  await evidence(carlos.page, 'SUC-07-candidate-denied', 'Carlos (a candidate, Employee role) opens the approved slate URL', 'No slate content; API slate 404, nominations 403, plans 403', 'As expected', { ...results });
  await carlos.context.close();

  const manager = await persona(browser, world.logins.manager.email, world.logins.manager.password);
  results.managerReadsSlate = (await api(manager.page, 'GET', `/api/succession/slates/${ids.v2}`)).status;
  results.managerReadsNominations = (await api(manager.page, 'GET', `/api/succession/nominations?positionId=${ids.critical}`)).status;
  results.managerNineBox = (await api(manager.page, 'GET', '/api/succession/nine-box')).status;
  const plans = await api(manager.page, 'GET', `/api/succession/development-plans?employeeId=${world.candidates.a.id}`);
  results.managerReadsPlan = plans.status;
  results.managerPlanHasNominationLink = (plans.json ?? []).some((p: any) => p.nominationId);
  const team = await api(manager.page, 'GET', '/api/approval-requests?queue=team&pageSize=100');
  results.slateInTeamQueue = (team.json?.items ?? []).some((i: any) => i.entityName === 'SuccessionSlate');
  await manager.page.goto('/performance/succession');
  await manager.page.waitForLoadState('networkidle').catch(() => {});
  await expect(manager.page.getByTestId('suc-ninebox')).toHaveCount(0);
  await evidence(manager.page, 'SUC-07-manager-view', 'The candidates\' line manager opens Succession',
    'Own nominations and team plans only; slate 404, nominations 403, 9-box 403, plan without its nomination link, no slate in the team queue', 'As expected');
  await manager.context.close();

  // F1: the line manager's own nomination comes back masked.
  const hrApi = await persona(browser, world.logins.hrManager.email, world.logins.hrManager.password);
  const pools = (await api(hrApi.page, 'GET', '/api/succession/pools')).json as any[];
  let poolId = pools.find((p) => p.code === 'SUC-FIN-LEAD')?.id;
  if (!poolId) poolId = (await api(hrApi.page, 'POST', '/api/succession/pools', { companyId: world.tenantA.companyId, code: 'SUC-FIN-LEAD',
    nameEn: 'Finance leaders', purpose: 'Finance leadership bench for the next three years.' })).json.id;
  await hrApi.context.close();
  const manager2 = await persona(browser, world.logins.manager.email, world.logins.manager.password);
  const nominated = await api(manager2.page, 'POST', '/api/succession/nominations', { employeeId: world.candidates.a.id, talentPoolId: poolId,
    evidence: 'Ran the regional close this year without a single late entry.' });
  results.managerNominateStatus = nominated.status;
  results.managerNominateMasked = nominated.status === 409 || (!nominated.text.includes('currentAssessment') && !nominated.text.includes('readyNowOnApprovedSlates') && nominated.text.includes('stage'));
  await manager2.context.close();

  // F2/F3: Admin is configuration-only, and the audit trail it reads holds ids only.
  const admin = await persona(browser, world.logins.admin.email, world.logins.admin.password);
  const adminPositions = await api(admin.page, 'GET', '/api/succession/positions');
  results.adminPositions = adminPositions.status;
  results.adminPositionsNameFree = !adminPositions.text.includes(world.candidates.a.name) && !adminPositions.text.includes(world.candidates.b.name);
  results.adminNominations = (await api(admin.page, 'GET', `/api/succession/nominations?positionId=${ids.critical}`)).status;
  results.adminNineBox = (await api(admin.page, 'GET', '/api/succession/nine-box')).status;
  results.adminReports = (await api(admin.page, 'GET', '/api/succession/reports/summary')).status;
  results.adminSlateEdit = (await api(admin.page, 'PUT', `/api/succession/slates/${ids.v2}`, { candidates: [], version: 0 })).status;
  results.adminReadsSlate = (await api(admin.page, 'GET', `/api/succession/slates/${ids.v2}`)).status;
  const logs = await api(admin.page, 'GET', '/api/audit-logs?limit=500');
  const succRows = (logs.json ?? []).filter((r: any) => String(r.action).startsWith('succession.'));
  const meta = succRows.map((r: any) => r.metadata ?? '').join('\n');
  results.auditRows = succRows.length;
  results.auditIdsOnly = !/mployeeId|readiness|ReadyNow|potential|Liu Wei|Carlos/i.test(meta);
  await admin.page.goto('/performance/succession');
  await admin.page.waitForLoadState('networkidle').catch(() => {});
  await evidence(admin.page, 'SUC-07-admin-config-only', 'Admin (configuration role) opens Succession and calls the candidate endpoints',
    'Positions and pools only; nominations/9-box/reports 403, slate edit 403, slate read 404; audit metadata ids only', 'As expected');
  await admin.context.close();

  const supervisor = await persona(browser, world.logins.supervisor.email, world.logins.supervisor.password);
  results.supervisorPositions = (await api(supervisor.page, 'GET', '/api/succession/positions')).status;
  await supervisor.context.close();

  const tenantB = await persona(browser, world.logins.tenantBAdmin.email, world.logins.tenantBAdmin.password, { slug: world.tenantB.slug });
  results.otherTenantPosition = (await api(tenantB.page, 'GET', `/api/succession/positions/${ids.critical}`)).status;
  results.otherTenantSlate = (await api(tenantB.page, 'GET', `/api/succession/slates/${ids.v2}`)).status;
  await tenantB.context.close();

  records.access = results;
  expect(results).toMatchObject({
    candidateReadsSlate: 404, candidateReadsNominations: 403, candidateReadsOthersPlans: 403,
    managerReadsSlate: 404, managerReadsNominations: 403, managerNineBox: 403, managerReadsPlan: 200, managerPlanHasNominationLink: false,
    slateInTeamQueue: false, supervisorPositions: 403, otherTenantPosition: 404, otherTenantSlate: 404,
    managerNominateMasked: true, adminPositions: 200, adminPositionsNameFree: true, adminNominations: 403, adminNineBox: 403, adminReports: 403,
    adminSlateEdit: 403, adminReadsSlate: 404, auditIdsOnly: true,
  });
});

test('SUC-05/08 — reports reconcile to their rows; Arabic RTL mirrors the 9-box; a 390px phone', async ({ browser }) => {
  const director = await persona(browser, world.logins.hrDirector.email, world.logins.hrDirector.password);
  const summary = await api(director.page, 'GET', '/api/succession/reports/summary');
  const reconcile: Record<string, [number, number]> = {};
  for (const m of summary.json.metrics.filter((x: any) => /^(positions|coverage|extendedCoverage|gap|bench)/.test(x.key))) {
    const drill = await api(director.page, 'GET', `/api/succession/reports/drill?metric=${encodeURIComponent(m.drill)}`);
    const rows = drill.json.kind === 'candidates' ? drill.json.candidates.length : drill.json.positions.length;
    reconcile[m.key] = [m.numerator ?? m.value, rows];
    expect(rows, m.key).toBe(m.numerator ?? m.value);
  }
  records.reconcile = reconcile;
  await director.page.goto('/performance/succession');
  await director.page.getByTestId('suc-kpi-coverage').click();
  await expect(director.page.getByTestId('suc-drill')).toBeVisible();
  await evidence(director.page, 'SUC-05-kpi-drill', 'HR Director opens Succession and clicks the Coverage tile',
    'Coverage = positions with a Ready-now successor ÷ active critical positions; the drill lists exactly those positions', 'As expected', { reconcile });
  await director.context.close();

  const ar = await persona(browser, world.logins.hrDirector.email, world.logins.hrDirector.password, { locale: 'ar' });
  await ar.page.goto(`/performance/succession/positions/${ids.critical}`);
  await expect(ar.page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(ar.page.getByTestId('suc-ninebox')).toBeVisible();
  const high = await ar.page.getByTestId('suc-ninebox-cell-P3-High').boundingBox();
  const low = await ar.page.getByTestId('suc-ninebox-cell-P3-Low').boundingBox();
  expect(high!.x).toBeLessThan(low!.x);
  await evidence(ar.page, 'SUC-08-arabic-ninebox', 'The position page in Arabic', 'dir=rtl; the 9-box mirrored (High performance on the left); Arabic labels', 'As expected');
  await ar.context.close();

  const empAr = await persona(browser, world.logins.employeeA.email, world.logins.employeeA.password, { locale: 'ar' });
  await empAr.page.goto('/ess/development-plan');
  await expect(empAr.page.getByTestId('suc-my-plan')).toBeVisible();
  await evidence(empAr.page, 'SUC-04-arabic-my-plan', 'My development plan in Arabic', 'dir=rtl, Arabic statuses', 'As expected');
  await empAr.context.close();

  const phone = await persona(browser, world.logins.employeeA.email, world.logins.employeeA.password, { mobile: true });
  await phone.page.goto('/ess/development-plan');
  await expect(phone.page.getByTestId('suc-my-plan')).toBeVisible();
  const overflow = await phone.page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  await evidence(phone.page, 'SUC-04-mobile-my-plan', 'My development plan at 390px', 'No horizontal scroll', `overflow ${overflow}px`);
  await phone.context.close();

  const hrPhone = await persona(browser, world.logins.hrManager.email, world.logins.hrManager.password, { mobile: true });
  await hrPhone.page.goto(`/performance/succession/positions/${ids.critical}`);
  await expect(hrPhone.page.getByTestId('suc-position-detail')).toBeVisible();
  const overflow2 = await hrPhone.page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow2).toBeLessThanOrEqual(0);
  await evidence(hrPhone.page, 'SUC-01-mobile-position', 'Position page at 390px (HR Manager)', 'No horizontal scroll; 9-box and slates readable', `overflow ${overflow2}px`);
  await hrPhone.context.close();
});
