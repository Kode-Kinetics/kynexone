import { expect, test, type Browser, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { ACME, OTHER, PEOPLE, SEED_PASSWORD, login, seedEngagementWorld, type EngagementWorld } from './bootstrap/engagement-seed';

/**
 * ENG — the real-backend journey, headed Chrome, on a disposable stack (see playwright.engagement-journey.config.ts).
 * HR builds a template and launches an anonymous survey → employees respond (a draft then a submit, and a double-submit
 * attempt) → HR reads results with a suppressed small group and a blocked intersecting filter → export → an action is
 * created and completed by its owner → reload → Arabic RTL → a phone-sized respond flow. Every decision point is a
 * screenshot in docs/acceptance/capability-closure/evidence/eng/, and journey.json records ids and timestamps.
 */

const API = (process.env.E2E_API_BASE_URL ?? 'http://127.0.0.1:5212').replace(/\/$/, '');
const EVIDENCE = path.resolve(process.cwd(), '../docs/acceptance/capability-closure/evidence/eng');
const LOG: Record<string, unknown>[] = [];
let world: EngagementWorld;
let surveyId = '';

const record = (id: string, data: Record<string, unknown>) => LOG.push({ id, atUtc: new Date().toISOString(), ...data });

async function shot(page: Page, name: string) {
  fs.mkdirSync(EVIDENCE, { recursive: true });
  await page.screenshot({ path: path.join(EVIDENCE, `${name}.png`), fullPage: true });
  record('screenshot', { file: `${name}.png`, url: page.url(), viewport: page.viewportSize() });
}

async function api(method: string, p: string, token: string, body?: unknown) {
  const res = await fetch(`${API}${p}`, {
    method, headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}

async function uiLogin(page: Page, email: string, slug: string, locale: 'en' | 'ar' = 'en') {
  await page.addInitScript((l) => localStorage.setItem('kynexone-locale-choice-v2', l), locale);
  await page.goto(`/login?workspace=${encodeURIComponent(slug)}`);
  await page.locator('#li-em, input[type="email"]').first().fill(email);
  await page.locator('#li-pw, input[type="password"]').first().fill(SEED_PASSWORD);
  const ws = page.locator('#li-ws, input[autocomplete="organization"]').first();
  if (await ws.isVisible().catch(() => false)) await ws.fill(slug);
  await page.locator('form button[type="submit"]').first().click();
  await page.waitForURL(/\/(dashboard|app|ess)/, { timeout: 60_000 });
  // A privileged account may be offered two-step enrolment; the journey does not depend on it.
  const later = page.getByRole('button', { name: /later|not now|skip/i }).first();
  if (await later.isVisible({ timeout: 2_000 }).catch(() => false)) await later.click();
}

async function newPage(browser: Browser, opts: { width?: number; height?: number; locale?: 'en' | 'ar' } = {}) {
  const context = await browser.newContext({ viewport: { width: opts.width ?? 1440, height: opts.height ?? 1000 }, acceptDownloads: true });
  return context.newPage();
}

/** Deterministic answers for respondent i of the starter template (q1, q2 5-point; q3 eNPS; q4 comment). */
function answers(i: number) {
  const list: Record<string, unknown>[] = [
    { questionKey: 'q1', value: [5, 4, 4, 3, 2, 4, 5][i % 7] },
    { questionKey: 'q2', value: [4, 4, 5, 3, 3, 2, 4][i % 7] },
    { questionKey: 'q3', value: [9, 10, 8, 6, 7, 9, 3][i % 7] },
  ];
  if (i % 3 === 0) list.push({ questionKey: 'q4', text: `Seed comment ${i}: more cross-team planning would help.` });
  return list;
}

/**
 * The evidence file holds only an allow-listed, typed subset of what the API returned: finite numbers, booleans and
 * short identifier-like strings (ids, codes, statuses, file names), under identifier-like keys. Anything else — free
 * text, nested shapes deeper than the log needs, unexpected characters — is dropped, never written to disk.
 */
const SAFE_KEY = /^[A-Za-z][A-Za-z0-9_-]{0,40}$/;
const SAFE_TEXT = /^[A-Za-z0-9 _.:/@()-]{0,120}$/;
type Evidence = number | boolean | string | null | Evidence[] | { [key: string]: Evidence };
function evidenceOf(value: unknown, depth = 0): Evidence {
  if (value === null || value === undefined) return null;
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value === 'boolean') return value;
  if (typeof value === 'string') return SAFE_TEXT.test(value) ? value : null;
  if (depth >= 5) return null;
  if (Array.isArray(value)) return value.slice(0, 200).map((v) => evidenceOf(v, depth + 1));
  if (typeof value === 'object') {
    const out: { [key: string]: Evidence } = {};
    for (const [key, v] of Object.entries(value as Record<string, unknown>)) if (SAFE_KEY.test(key)) out[key] = evidenceOf(v, depth + 1);
    return out;
  }
  return null;
}

test.describe.serial('ENG real-backend journey', () => {
  test.afterAll(() => {
    fs.mkdirSync(EVIDENCE, { recursive: true });
    const evidence = evidenceOf({ api: API, surveyId, world, log: LOG });
    fs.writeFileSync(path.join(EVIDENCE, 'journey.json'), JSON.stringify(evidence, null, 2));
  });

  test('J0 seed the disposable world through the API only', async () => {
    world = await seedEngagementWorld(test.info().project.use.baseURL!);
    record('J0', { tenant: ACME.slug, otherTenant: OTHER.slug, employees: Object.keys(world.employees).length, companyId: world.companyId });
    expect(Object.keys(world.employees).length).toBeGreaterThanOrEqual(PEOPLE.length);
  });

  test('J1 HR builds and publishes a template, then launches an anonymous survey', async ({ browser }) => {
    const page = await newPage(browser);
    await uiLogin(page, ACME.admin, ACME.slug);
    await page.goto('/engagement');
    await expect(page.getByRole('heading', { name: 'Engagement surveys' })).toBeVisible({ timeout: 60_000 });
    await shot(page, 'J1-01-engagement-home');

    await page.getByRole('tab', { name: 'Templates' }).click();
    await page.getByTestId('template-new').click();
    await page.getByTestId('template-name-en').fill('Quarterly engagement pulse');
    await page.getByTestId('template-name-ar').fill('نبض المشاركة الربع سنوي');
    await shot(page, 'J1-02-template-builder');
    await page.getByTestId('template-save').click();
    await expect(page.getByText('Template draft saved.', { exact: false })).toBeVisible();
    await page.locator('[data-testid^="template-publish-"]').first().click();
    await expect(page.getByText('Template published.', { exact: false })).toBeVisible();
    await shot(page, 'J1-03-template-published');

    await page.getByRole('tab', { name: 'Surveys' }).click();
    await page.getByTestId('survey-new').click();
    await page.getByTestId('survey-title-en').fill('Q4 engagement pulse');
    await page.getByTestId('survey-title-ar').fill('نبض المشاركة للربع الرابع');
    await page.getByTestId('survey-mode-Anonymous').check();
    for (const name of ['Operations', 'Finance', 'Legal']) await page.getByTestId('survey-departments').getByText(name, { exact: true }).click();
    await page.getByTestId('survey-preview').click();
    await expect(page.getByTestId('survey-eligible')).toHaveText('36 people');
    await expect(page.getByTestId('survey-mode-notice')).toContainText('HR sees only how many people have responded, not who');
    await shot(page, 'J1-04-survey-builder-anonymous-notice');
    await page.getByTestId('survey-save').click();
    await expect(page.getByText('Survey draft saved.', { exact: false })).toBeVisible();
    await page.locator('[data-testid^="survey-launch-"]').first().click();
    await page.getByTestId('confirm-pending').click();
    await expect(page.getByText('36 people were invited', { exact: false })).toBeVisible();
    await shot(page, 'J1-05-survey-launched');

    const hr = await login(ACME.admin, ACME.slug);
    const surveys = (await api('GET', '/api/engagement/surveys', hr)).body as { id: string; titleEn: string; status: string; invitationCount: number }[];
    const launched = surveys.find((s) => s.titleEn === 'Q4 engagement pulse')!;
    surveyId = launched.id;
    record('J1', { surveyId, status: launched.status, invited: launched.invitationCount });
    expect(launched.status).toBe('Open');
    expect(launched.invitationCount).toBe(36);
    await page.context().close();
  });

  test('J2 an employee saves a draft, comes back, submits once; a second submit is refused', async ({ browser }) => {
    const page = await newPage(browser);
    const person = PEOPLE.find((p) => p.code === 'ACM-001')!;
    await uiLogin(page, person.email, ACME.slug);
    await page.goto('/ess/surveys');
    await expect(page.getByTestId(`my-survey-${surveyId}`)).toBeVisible({ timeout: 60_000 });
    await shot(page, 'J2-01-my-surveys');
    await page.getByTestId(`my-survey-open-${surveyId}`).click();
    await expect(page.getByTestId('privacy-notice')).toContainText('stored without your name, your employee number or the date you answered');
    await page.getByTestId('question-q1').getByText('Agree', { exact: true }).click();
    await page.getByTestId('respond-save-draft').click();
    await expect(page.getByText('Draft saved. Only you can see it', { exact: false })).toBeVisible();
    await shot(page, 'J2-02-draft-saved');
    await page.getByText('Back to my surveys').click();
    await expect(page.getByTestId(`my-survey-${surveyId}`)).toContainText('Draft saved');
    await page.getByTestId(`my-survey-open-${surveyId}`).click();
    await expect(page.getByText('Your saved draft was restored.')).toBeVisible();
    await page.getByTestId('question-q2').getByText('Strongly agree', { exact: true }).click();
    await page.getByTestId('question-q3').getByText('9', { exact: true }).click();
    await page.getByTestId('answer-q4').fill('Clearer priorities each quarter.');
    await shot(page, 'J2-03-ready-to-submit');
    await page.getByTestId('respond-submit').click();
    await expect(page.getByTestId('survey-thanks')).toBeVisible();
    await shot(page, 'J2-04-submitted');

    const token = await login(person.email, ACME.slug);
    const reopen = await api('POST', '/api/ess/surveys/open', token, { surveyId });
    const resubmit = await api('POST', '/api/ess/surveys/submit', token, { surveyId, token: 'stale', answers: answers(1) });
    record('J2', { employee: person.code, reopenStatus: reopen.status, reopenError: reopen.body?.error, resubmitStatus: resubmit.status, resubmitError: resubmit.body?.error });
    expect(reopen.status).toBe(409);
    expect(reopen.body.error).toBe('survey_already_submitted');
    expect(resubmit.status).toBe(409);
    await page.context().close();
  });

  test('J3 more employees respond through the API (deterministic answers)', async () => {
    // Everyone in Operations and Legal, and 8 of the 10 in Finance. ACM-001 already answered in J2.
    const respondents = PEOPLE.filter((p) => p.dept !== 'MGT' && p.code !== 'ACM-001')
      .filter((p) => !(p.dept === 'FIN' && ['ACM-030', 'ACM-031'].includes(p.code)));
    let ok = 0;
    for (const [i, p] of respondents.entries()) {
      const token = await login(p.email, ACME.slug);
      const session = await api('POST', '/api/ess/surveys/open', token, { surveyId });
      expect(session.status, p.code).toBe(200);
      const submitted = await api('POST', '/api/ess/surveys/submit', token, { surveyId, token: session.body.token, answers: answers(i) });
      expect(submitted.status, `${p.code}: ${JSON.stringify(submitted.body)}`).toBe(200);
      ok++;
    }
    record('J3', { submittedViaApi: ok, totalRespondents: ok + 1 });
    expect(ok + 1).toBe(34);
  });

  test('J3b a phone-sized respond flow in Arabic (390×844)', async ({ browser }) => {
    const page = await newPage(browser, { width: 390, height: 844 });
    const person = PEOPLE.find((p) => p.code === 'ACM-030')!;
    await uiLogin(page, person.email, ACME.slug, 'ar');
    await page.goto('/ess/surveys');
    await expect(page.getByTestId(`my-survey-${surveyId}`)).toBeVisible({ timeout: 60_000 });
    await shot(page, 'J3b-01-mobile-my-surveys-ar');
    await page.getByTestId(`my-survey-open-${surveyId}`).click();
    await expect(page.getByTestId('privacy-notice')).toContainText('مجهول الهوية');
    await page.getByTestId('question-q1').getByText('أوافق', { exact: true }).click();
    await page.getByTestId('question-q2').getByText('محايد', { exact: true }).click();
    await page.getByTestId('question-q3').getByText('8', { exact: true }).click();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    await shot(page, 'J3b-02-mobile-respond-ar');
    await page.getByTestId('respond-submit').click();
    await expect(page.getByTestId('survey-thanks')).toBeVisible();
    await shot(page, 'J3b-03-mobile-submitted-ar');
    record('J3b', { employee: person.code, horizontalOverflowPx: overflow });
    expect(overflow).toBeLessThanOrEqual(1);
    await page.context().close();
  });

  test('J3c while open, an anonymous survey shows only its response rate; HR closes it', async ({ browser }) => {
    const page = await newPage(browser);
    await uiLogin(page, ACME.admin, ACME.slug);
    await page.goto('/engagement');
    await page.getByRole('tab', { name: 'Results' }).click();
    await expect(page.getByTestId('results-after-close')).toContainText('Results appear when the survey closes on', { timeout: 60_000 });
    await expect(page.getByTestId('kpi-response-rate')).toContainText('35 of 36 invited responded');
    await expect(page.getByTestId('kpi-favourable')).toHaveCount(0);
    await shot(page, 'J3c-01-results-wait-for-close');
    const hr = await login(ACME.admin, ACME.slug);
    const live = await api('GET', `/api/engagement/surveys/${surveyId}/results`, hr);
    const comments = await api('GET', `/api/engagement/surveys/${surveyId}/comments?question=q4`, hr);
    await page.getByRole('tab', { name: 'Surveys' }).click();
    await page.getByTestId(`survey-close-${surveyId}`).click();
    await page.getByTestId('confirm-pending').click();
    await expect(page.getByText('The survey is closed.', { exact: false })).toBeVisible();
    await shot(page, 'J3c-02-survey-closed');
    record('J3c', { resultsAvailableWhileOpen: live.body.resultsAvailable, commentsWhileOpen: comments.status, submittedLive: live.body.participation.submitted });
    expect(live.body.resultsAvailable).toBe(false);
    expect(comments.status).toBe(409);
    await page.context().close();
  });

  test('J4 HR reads results: small groups suppressed, an intersecting filter blocked, export matches', async ({ browser }) => {
    const page = await newPage(browser);
    await uiLogin(page, ACME.admin, ACME.slug);
    await page.goto('/engagement');
    await page.getByRole('tab', { name: 'Results' }).click();
    await expect(page.getByTestId('kpi-response-rate')).toContainText('35 of 36 invited responded', { timeout: 60_000 });
    await shot(page, 'J4-01-results-overview');

    await page.getByTestId('filter-breakdown').selectOption('department');
    await expect(page.getByTestId('breakdown-table')).toBeVisible();
    const legal = page.getByTestId('breakdown-table').locator('tr', { hasText: 'Legal' });
    const finance = page.getByTestId('breakdown-table').locator('tr', { hasText: 'Finance' });
    await expect(legal).toContainText('Not enough responses to protect confidentiality (minimum 5).');
    await expect(finance).toContainText('Not enough responses to protect confidentiality (minimum 5).');
    await expect(page.getByTestId('breakdown-table').locator('tr', { hasText: 'Operations' })).toContainText('23');
    await shot(page, 'J4-02-breakdown-small-group-suppressed');

    await page.getByTestId('filter-breakdown').selectOption('');
    await page.getByTestId('filter-department').selectOption({ label: 'Operations' });
    await page.getByTestId('filter-designation').selectOption({ label: 'Engineer' });
    await expect(page.getByTestId('results-suppressed')).toContainText('Not enough responses to protect confidentiality (minimum 5).');
    await shot(page, 'J4-03-intersecting-filter-blocked');

    const download = page.waitForEvent('download');
    await page.getByTestId('filter-department').selectOption('');
    await page.getByTestId('filter-designation').selectOption('');
    await page.getByTestId('filter-breakdown').selectOption('department');
    await expect(page.getByTestId('breakdown-table')).toBeVisible();
    await page.getByTestId('export-csv').click();
    const file = await download;
    const target = path.join(EVIDENCE, 'J4-export.csv');
    await file.saveAs(target);
    const csv = fs.readFileSync(target, 'utf8').replace(/"/g, '');
    expect(csv).toContain('department,Legal,Suppressed');
    expect(csv).toContain('department,Finance,Suppressed');
    expect(csv).toContain('Submitted,35');
    expect(csv).not.toContain('Seed comment');

    const hr = await login(ACME.admin, ACME.slug);
    const results = (await api('GET', `/api/engagement/surveys/${surveyId}/results?breakdown=department`, hr)).body;
    record('J4', {
      responseRatePct: results.participation.responseRatePct, favourablePct: results.favourablePct, enps: results.enps?.score,
      cells: results.breakdown.cells.map((c: { label: string; visible: boolean; suppressionReason: string | null }) => ({ label: c.label, visible: c.visible, reason: c.suppressionReason })),
      exportFile: 'J4-export.csv',
    });
    expect(csv).toContain(`Favourable %,${results.favourablePct}`);
    await page.context().close();
  });

  test('J5 HR creates an action; its owner completes it in Self-Service; it survives a reload', async ({ browser }) => {
    const page = await newPage(browser);
    await uiLogin(page, ACME.admin, ACME.slug);
    await page.goto('/engagement');
    await page.getByRole('tab', { name: 'Results' }).click();
    await page.getByRole('button', { name: 'Create action' }).first().click();
    await expect(page.getByTestId('action-title')).toBeVisible();
    await page.getByTestId('action-owner-search').fill('Mona');
    await page.getByRole('option', { name: /Mona Al-Harbi/ }).click();
    await shot(page, 'J5-01-action-editor');
    await page.getByTestId('action-save').click();
    await expect(page.getByTestId('engagement-actions')).toContainText('Mona Al-Harbi');
    await shot(page, 'J5-02-action-created');
    await page.context().close();

    const owner = await newPage(browser);
    await uiLogin(owner, PEOPLE.find((p) => p.code === 'MGR-ENG')!.email, ACME.slug);
    await owner.goto('/ess/surveys');
    await expect(owner.getByTestId('my-actions')).toBeVisible({ timeout: 60_000 });
    await owner.locator('[data-testid^="my-action-progress-"]').first().click();
    await owner.getByTestId('progress-status').selectOption('InProgress');
    await owner.getByTestId('progress-note').fill('Booked the first recognition round.');
    await owner.getByTestId('progress-save').click();
    await owner.locator('[data-testid^="my-action-progress-"]').first().click();
    await owner.getByTestId('progress-status').selectOption('Done');
    await owner.getByTestId('progress-note').fill('Held and recorded.');
    await owner.getByTestId('progress-save').click();
    await expect(owner.getByTestId('my-actions')).toContainText('Done');
    await shot(owner, 'J5-03-owner-completed-action');
    await owner.context().close();

    const reload = await newPage(browser);
    await uiLogin(reload, ACME.admin, ACME.slug);
    await reload.goto('/engagement');
    await reload.getByRole('tab', { name: 'Actions' }).click();
    await expect(reload.getByTestId('engagement-actions')).toContainText('Done');
    await reload.reload();
    await reload.getByRole('tab', { name: 'Actions' }).click();
    await expect(reload.getByTestId('engagement-actions')).toContainText('Done');
    await shot(reload, 'J5-04-action-done-after-reload');
    const hr = await login(ACME.admin, ACME.slug);
    const actions = (await api('GET', `/api/engagement/actions?surveyId=${surveyId}`, hr)).body as { id: string; status: string; progress: unknown[] }[];
    record('J5', { actionId: actions[0]?.id, status: actions[0]?.status, progressEntries: actions[0]?.progress.length });
    expect(actions[0].status).toBe('Done');
    await reload.context().close();
  });

  test('J6 Arabic RTL results page', async ({ browser }) => {
    const page = await newPage(browser);
    await uiLogin(page, ACME.admin, ACME.slug, 'ar');
    await page.goto('/engagement');
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl', { timeout: 60_000 });
    await page.getByRole('tab', { name: 'النتائج' }).click();
    await page.getByTestId('filter-breakdown').selectOption('department');
    await expect(page.getByTestId('breakdown-table')).toContainText('لا توجد إجابات كافية لحماية السرية');
    await shot(page, 'J6-01-results-arabic-rtl');
    record('J6', { dir: await page.locator('html').getAttribute('dir') });
    await page.context().close();
  });

  test('J8 isolation: the other tenant, and an employee, cannot read these results', async () => {
    const other = await login(OTHER.admin, OTHER.slug);
    const cross = await api('GET', `/api/engagement/surveys/${surveyId}/results`, other);
    const employee = await login(PEOPLE.find((p) => p.code === 'ACM-002')!.email, ACME.slug);
    const asEmployee = await api('GET', `/api/engagement/surveys/${surveyId}/results`, employee);
    record('J8', { otherTenantStatus: cross.status, employeeStatus: asEmployee.status });
    expect(cross.status).toBe(404);
    expect(asEmployee.status).toBe(403);
  });
});
