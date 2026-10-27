import fs from 'node:fs';
import path from 'node:path';
import { randomBytes, randomInt } from 'node:crypto';
import { expect, test, type Page, type TestInfo } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import { resolveTarget } from './identity/env';
import { stampActor } from './identity/actor';
import { assertDisposableHost } from './disposable-host.guard';
import { PLATFORM_EMAIL, PLATFORM_PASSWORD } from './world';

/**
 * Create-company wizard against the REAL /api/company-setup — no request is intercepted. The mocked
 * sibling (company-setup-wizard.spec.ts) proves the UI against the contract; this one proves the
 * product: the planner's conflicts, the commit's idempotency, the super admin's scope and the
 * group-scope gate, all through the running API and its database.
 *
 * ── Its own tenant ────────────────────────────────────────────────────────────────────────────
 * Every run creates a fresh Group tenant (`wizard-e2e-<hex>`) through the platform-admin API, so the
 * shared fixture tenants keep their company counts (group-admin.spec.ts asserts Almarai has exactly
 * five). Tenant deletion is disabled in this build, so the tenant stays in the disposable database;
 * the host guard refuses to run anywhere else.
 *
 * Collected by the Browser Pilot `chromium` project. Locally (never :5173, a prebuilt image):
 *   npx next dev -p 5174   with the API on E2E_API_BASE_URL and the world bootstrapped, then
 *   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5174 npx playwright test --project=chromium e2e/company-setup-wizard.real.spec.ts
 * WIZARD_REAL_SHOTS_DIR=<dir> saves a viewport screenshot of every step in EN and AR.
 */

type Locale = 'en' | 'ar';
const T = (locale: Locale) => (key: string, params?: Record<string, string | number>) => translate(locale, key, params);
const target = resolveTarget();
const API = target.apiBaseUrl;

const RUN = randomBytes(3).toString('hex');
const SLUG = `wizard-e2e-${RUN}`;
const PASSWORD = `Wz${randomBytes(8).toString('hex')}!aA9`;
const OWNER_DOMAIN = `wizard-${RUN}.test`;
const OWNER = { email: `owner@${OWNER_DOMAIN}`, fullName: 'Wizard Group Owner' };
/** Admin role, confined to the starter company: eligible as an existing super admin, refused the wizard. */
const CO_ADMIN = { email: `coadmin@${OWNER_DOMAIN}`, fullName: 'Starter Company Admin' };
/** Group HR Manager without Admin: never offered as an existing super admin. */
const GROUP_HR = { email: `hr@${OWNER_DOMAIN}`, fullName: 'Group HR Manager' };

const digits = (n: number) => Array.from({ length: n }, () => randomInt(0, 10)).join('');
const crNumber = () => `10${digits(8)}`;
const molNumber = () => `7-${randomInt(1, 10)}${digits(6)}`;

interface NewCompany { legalNameEn: string; legalNameAr: string; cr: string; mol: string; domain: string }
const company = (label: string, ar: string): NewCompany => ({
  legalNameEn: `${label} ${RUN}`, legalNameAr: `${ar} ${RUN}`, cr: crNumber(), mol: molNumber(), domain: `${label.toLowerCase().replace(/[^a-z]+/g, '')}-${RUN}.sa`,
});

const EN_CO = company('Riyadh Trading', 'شركة الرياض للتجارة');
const AR_CO = company('Jeddah Logistics', 'شركة جدة للخدمات اللوجستية');
const DOUBLE_CO = company('Dammam Services', 'شركة الدمام للخدمات');
const INVITED = { email: `sara@${EN_CO.domain}`, fullName: 'Sara Al-Qahtani' };

// ── direct API (the assertions' source of truth) ──────────────────────────────────────────────

async function call<T = any>(method: string, p: string, opts: { token?: string; body?: unknown; stepUp?: string } = {}): Promise<{ status: number; body: T; text: string }> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
  // "Confirm it's you" (POST /api/auth/step-up): actions against an Admin carry it in X-Step-Up.
  if (opts.stepUp) headers['X-Step-Up'] = opts.stepUp;
  const res = await fetch(`${API}${p}`, { method, headers, body: opts.body === undefined ? undefined : JSON.stringify(opts.body) });
  const text = await res.text();
  let body: any = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* keep text */ }
  return { status: res.status, body, text };
}

function ok<T>(res: { status: number; body: T; text: string }, what: string, accept = [200, 201, 204]): T {
  if (!accept.includes(res.status)) throw new Error(`[wizard-real] ${what}: HTTP ${res.status} ${res.text.slice(0, 500)}`);
  return res.body;
}

const items = (body: any): any[] => Array.isArray(body) ? body : body?.items ?? body?.Items ?? [];

/** A five-minute "Confirm it's you" token for the signed-in caller (StepUpService). */
async function stepUpToken(token: string, password = PASSWORD): Promise<string> {
  return (ok(await call('POST', '/api/auth/step-up', { token, body: { password } }), 'confirm it is you') as any).stepUpToken;
}

/** The web app's "Confirm it's you" dialog: an action against an Admin asks the signed-in user for their password once. */
async function confirmItsYou(page: Page, password = PASSWORD) {
  const dialog = page.getByTestId('step-up-dialog');
  await expect(dialog).toBeVisible({ timeout: 20_000 });
  await dialog.getByTestId('step-up-password').fill(password);
  await page.getByTestId('step-up-confirm').click();
  await expect(dialog).toHaveCount(0, { timeout: 20_000 });
}

/** As above when the action might not need it (an admin choice that makes nobody an Admin, or a confirmation still fresh). */
async function confirmItsYouIfAsked(page: Page, password = PASSWORD) {
  const dialog = page.getByTestId('step-up-dialog');
  if (await dialog.waitFor({ state: 'visible', timeout: 5_000 }).then(() => true, () => false)) await confirmItsYou(page, password);
}

async function tenantToken(email: string, password = PASSWORD): Promise<string> {
  const body = ok(await call('POST', '/api/auth/login', { body: { email, password, tenantSlug: SLUG } }), `login ${email}`);
  return (body as any).accessToken ?? (body as any).token;
}

async function companiesWithCr(token: string, cr: string): Promise<any[]> {
  const list = items(ok(await call('GET', '/api/companies?page=1&pageSize=100', { token }), 'list companies'));
  return list.filter(c => String(c.registrationNumber ?? '').trim() === cr);
}

async function companiesUsed(token: string): Promise<number> {
  return (ok(await call('GET', '/api/company-setup/context', { token }), 'context') as any).companiesUsed as number;
}

let starterCompany: { id: string; legalNameEn: string };
let coAdminId: string;
/** The single-use sign-in link the EN journey's "Send sign-in link" handed back (no SMTP on a disposable stack). */
let invitedSignInLink: string | undefined;

// Each case drives the real API through several sign-ins and full journeys; CI runners need more than the
// default 30 s per test (the resume case measured ~37 s there).
test.describe.configure({ mode: 'serial', timeout: 120_000 });
test.use({ storageState: { cookies: [], origins: [] }, viewport: { width: 1440, height: 1000 } });

test.beforeAll(async () => {
  assertDisposableHost(target.baseUrl, 'create a per-run wizard tenant');
  const platform = ok(await call('POST', '/api/platform/auth/login', { body: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD } }), 'platform login') as any;
  const pt = platform.token ?? platform.accessToken;
  const created = ok(await call('POST', '/api/platform/tenants', {
    token: pt,
    body: {
      name: `Wizard E2E ${RUN}`, slug: SLUG, adminEmail: OWNER.email, adminFullName: OWNER.fullName, adminPassword: PASSWORD,
      accountType: 'Group', plan: 'Enterprise', maxUsers: 50, maxEmployees: 100, maxCompanies: 10,
      billingEmail: `billing@${SLUG}.test`, billingCycle: 'Monthly', monthlyAmount: 0, currencyCode: 'SAR', homeCountryCode: 'SA',
    },
  }), 'create wizard tenant', [201]) as any;
  const tenantId = created.tenantId as string;
  const owner = await tenantToken(OWNER.email);
  const [starter] = items(ok(await call('GET', '/api/companies?page=1&pageSize=100', { token: owner }), 'starter company'));
  starterCompany = { id: starter.id, legalNameEn: starter.legalNameEn };
  const coAdmin = ok(await call('POST', `/api/platform/tenants/${tenantId}/users`, {
    token: pt,
    body: { email: CO_ADMIN.email, password: PASSWORD, fullName: CO_ADMIN.fullName, roleName: 'Admin', entityScope: 'companies', companyIds: [starter.id] },
  }), 'create company-scoped admin') as any;
  coAdminId = coAdmin.id ?? coAdmin.Id;
  ok(await call('POST', `/api/platform/tenants/${tenantId}/users`, {
    token: pt,
    body: { email: GROUP_HR.email, password: PASSWORD, fullName: GROUP_HR.fullName, roleName: 'HR Manager', entityScope: 'group' },
  }), 'create group HR manager');
});

// ── browser helpers ───────────────────────────────────────────────────────────────────────────

async function signIn(page: Page, email: string, locale: Locale = 'en', password = PASSWORD) {
  stampActor({ email, tenantSlug: SLUG, via: 'wizard lane fixture (per-run tenant)' });
  await page.addInitScript(l => localStorage.setItem('kynexone-locale-choice-v2', l), locale);
  await page.goto(`/login?workspace=${encodeURIComponent(SLUG)}`);
  await page.locator('#li-em').fill(email);
  await page.locator('#li-pw').fill(password);
  const workspace = page.locator('#li-ws');
  if (await workspace.count()) await workspace.fill(SLUG);
  await page.locator('#li-pw').press('Enter');
  await page.waitForURL(url => !url.pathname.startsWith('/login'), { timeout: 20_000 });
}

async function shot(page: Page, info: TestInfo, name: string) {
  const dir = process.env.WIZARD_REAL_SHOTS_DIR;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: path.join(dir, `${name}.png`), animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
}

async function expectStep(page: Page, t: ReturnType<typeof T>, heading: string) {
  await expect(page.getByRole('heading', { level: 2, name: t(heading), exact: true })).toBeVisible({ timeout: 20_000 });
}

/** The footer's primary action: "Next", "Next: Departments", "Save cost centres & next"… */
const next = (page: Page, _t?: ReturnType<typeof T>) => page.getByTestId('wizard-next').click();

/** Click the primary action until the given h2 (a step heading or a part name) is on screen. */
async function nextUntil(page: Page, t: ReturnType<typeof T>, heading: string, max = 10) {
  const target = page.getByRole('heading', { level: 2, name: t(heading), exact: true });
  for (let i = 0; i < max; i++) {
    if (await target.isVisible()) return;
    const position = await page.getByTestId('wizard-position').getAttribute('data-position');
    await next(page, t);
    await expect(page.getByTestId('wizard-position')).not.toHaveAttribute('data-position', position ?? '', { timeout: 15_000 }).catch(() => undefined);
  }
  await expect(target).toBeVisible({ timeout: 20_000 });
}

async function openWizard(page: Page, t: ReturnType<typeof T>) {
  await page.goto('/setup?tab=companies');
  await page.getByRole('button', { name: t('Create company'), exact: true }).click();
  await expect(page).toHaveURL(/\/setup\/new-company/);
  await expectStep(page, t, 'Create a company');
}

async function fillCompany(page: Page, t: ReturnType<typeof T>, co: NewCompany) {
  await page.getByRole('button', { name: t('Start'), exact: true }).click();
  await expectStep(page, t, 'Company details');
  await page.getByLabel(t('Legal name (English)')).fill(co.legalNameEn);
  await page.getByLabel(t('Legal name (Arabic)')).fill(co.legalNameAr);
  await page.getByLabel(t('Commercial registration (CR) number')).fill(co.cr);
  await page.getByLabel(t('MOL establishment number')).fill(co.mol);
  await page.getByLabel(t('Work email domain (optional)')).fill(co.domain);
  await expect(page.getByLabel(t('Salary currency'))).toHaveValue('SAR');
}

/** Steps 3-5 with real rows: one location, one cost centre, one new department, a numbering prefix. */
async function fillMiddleSteps(page: Page, t: ReturnType<typeof T>, info: TestInfo, locale: Locale, tag: string) {
  await next(page, t);
  await expectStep(page, t, 'Branches & locations');
  const hq = page.getByTestId('branches-row').first();
  await hq.locator('summary').click();
  await hq.getByRole('button', { name: t('Add location'), exact: true }).click();
  const location = page.getByTestId('branches.0.locations-row').first();
  await location.getByLabel(t('Code')).fill(`G${tag}`);
  await location.getByLabel(t('Name (English)')).fill('Main gate');
  await shot(page, info, `${locale}-3-branches`);
  await next(page, t);

  // Step 4 goes one part at a time (Addendum B4): cost centres, then departments, grades, designations.
  await expectStep(page, t, 'Cost centres');
  await page.getByRole('button', { name: t('Add cost centre'), exact: true }).click();
  const cc = page.getByTestId('costCentres-row').last();
  await cc.getByLabel(t('Code')).fill(`CC${tag}`);
  await cc.getByLabel(t('Name (English)')).fill('Administration');
  await next(page, t);
  await expectStep(page, t, 'Departments');
  await page.getByRole('button', { name: t('Add department'), exact: true }).click();
  const dept = page.getByTestId('departments-row').last();
  await dept.getByLabel(t('Code')).fill(`OPS${tag}`);
  await dept.getByLabel(t('Name (English)')).fill('Operations');
  // Its links sit on the row's second line, behind "Edit details".
  const index = (await page.getByTestId('departments-row').count()) - 1;
  await page.getByRole('button', { name: new RegExp(`^${t('Edit details')}`) }).last().click();
  await page.locator(`#ccw-departments-${index}-costCentreCode`).selectOption(`CC${tag}`);
  await shot(page, info, `${locale}-4-organisation`);
  await nextUntil(page, t, 'Work rules');
  await page.getByLabel(t('Prefix')).fill(`W${tag}`);
  await expect(page.getByTestId('numbering-preview')).toContainText(`W${tag}-`);
  await shot(page, info, `${locale}-5-rules`);
  await nextUntil(page, t, 'Company super admin');
}

async function reachReview(page: Page, t: ReturnType<typeof T>) {
  await next(page, t);
  await expectStep(page, t, 'Review & create');
  await expect(page.getByTestId('review-plan')).toBeVisible({ timeout: 20_000 });
}

const isCommit = (method: string, url: string) => method === 'POST' && /\/api\/company-setup\/?$/.test(new URL(url).pathname);

/** Every commit request the page sent (its Idempotency-Key) and every answer it got (status, replayed). */
function watchCommits(page: Page) {
  const commits: string[] = [];
  const answers: { status: number; replayed?: boolean }[] = [];
  page.on('request', r => { if (isCommit(r.method(), r.url())) commits.push(r.headers()['idempotency-key'] ?? ''); });
  page.on('response', async r => {
    if (!isCommit(r.request().method(), r.url())) return;
    const body = await r.json().catch(() => null);
    answers.push({ status: r.status(), replayed: body?.replayed });
  });
  return Object.assign(commits, { answers });
}

async function expectCreated(page: Page, t: ReturnType<typeof T>, co: NewCompany, locale: Locale = 'en') {
  await expect(page.getByTestId('create-company-success')).toBeVisible({ timeout: 30_000 });
  // The company is named in the reader's language.
  await expect(page.getByRole('heading', { name: t('{name} is created', { name: locale === 'ar' ? co.legalNameAr : co.legalNameEn }) })).toBeVisible();
  const readiness = page.getByTestId('company-readiness');
  await expect(readiness).toBeVisible();
  await expect(readiness.locator('[data-testid^="readiness-"]').first()).toBeVisible();
}

// ── the cases ─────────────────────────────────────────────────────────────────────────────────

test('EN: the seven steps create the company, its setup and an invited super admin', async ({ page }, info) => {
  const t = T('en');
  const pageErrors: string[] = [];
  page.on('pageerror', e => pageErrors.push(e.message));
  const commits = watchCommits(page);
  await signIn(page, OWNER.email);
  const owner = await tenantToken(OWNER.email);
  const before = await companiesUsed(owner);

  await openWizard(page, t);
  await expect(page.getByTestId('plan-usage')).toContainText(t('{used} of {allowed} companies on your plan', { used: before, allowed: 10 }));
  await shot(page, info, 'en-1-start');
  await fillCompany(page, t, EN_CO);
  await expect(page.getByLabel(t('Qiwa establishment ID (optional)'))).toHaveValue(EN_CO.mol);
  await shot(page, info, 'en-2-company');
  await fillMiddleSteps(page, t, info, 'en', 'E');

  await page.getByLabel(t('Invite a new company super admin')).check();
  await page.getByLabel(t('Full name (English)')).fill(INVITED.fullName);
  await page.getByLabel(t('Official work email')).fill(INVITED.email);
  await shot(page, info, 'en-6-admin');
  await reachReview(page, t);
  await expect(page.getByTestId('plan-group-6')).toContainText(INVITED.fullName);
  const create = page.getByRole('button', { name: t('Create company'), exact: true });
  await expect(create).toBeEnabled();
  await shot(page, info, 'en-7-review');
  await create.click();
  // Inviting a company super admin makes someone an Admin: the signed-in owner confirms it's them first.
  await confirmItsYou(page);
  await expectCreated(page, t, EN_CO);
  const adminRow = page.getByTestId('readiness-companyAdmin');
  await expect(adminRow).toContainText(`Send ${INVITED.fullName} their sign-in link`);
  await shot(page, info, 'en-8-created');
  // One click sends the invited super admin their sign-in link. This stack has no SMTP, so the server hands the link
  // back once, here, to be passed on through the company's own channel.
  await adminRow.getByRole('button', { name: t('Send sign-in link'), exact: true }).click();
  // The invited super admin is an Admin: the confirmation given at "Create company" is still fresh (five minutes).
  await confirmItsYouIfAsked(page);
  const linkOutcome = adminRow.getByTestId('sign-in-link-result');
  await expect(linkOutcome).toBeVisible({ timeout: 20_000 });
  const emailed = (await linkOutcome.getAttribute('data-emailed')) === 'true';
  if (emailed) {
    await expect(linkOutcome).toContainText(t('Emailed to {email}. The link works once and expires in 1 hour.', { email: INVITED.email }));
  } else {
    await expect(linkOutcome).toContainText(t('Nothing was emailed. Copy this sign-in link and send it to {email} through the company’s own channel. It works once and expires in 1 hour.', { email: INVITED.email }));
    invitedSignInLink = await adminRow.getByTestId('sign-in-link-url').inputValue();
    expect(invitedSignInLink).toMatch(/#token=/);
  }
  await shot(page, info, 'en-8-sign-in-link');

  // The database agrees with the screen: one company with this CR, the plan usage moved by one.
  const rows = await companiesWithCr(owner, EN_CO.cr);
  expect(rows.map(r => r.legalNameEn)).toEqual([EN_CO.legalNameEn]);
  expect(rows[0].wpsEmployerId).toBe(EN_CO.mol);
  expect(await companiesUsed(owner)).toBe(before + 1);
  // One click: refused once for "Confirm it's you" (403, nothing written), then the same request — same idempotency
  // key — repeated once after the owner confirmed.
  expect(new Set(commits).size).toBe(1);
  expect(commits.answers.map(a => a.status)).toEqual([403, 201]);
  const readiness = ok(await call('GET', `/api/company-setup/readiness/${rows[0].id}`, { token: owner }), 'readiness') as any[];
  expect(readiness.length).toBeGreaterThan(0);
  expect(pageErrors).toEqual([]);
});

test('AR: the same journey in Arabic, with an existing company admin as the super admin', async ({ page }, info) => {
  const t = T('ar');
  const pageErrors: string[] = [];
  page.on('pageerror', e => pageErrors.push(e.message));
  await signIn(page, OWNER.email, 'ar');
  await openWizard(page, t);
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await shot(page, info, 'ar-1-start');
  await fillCompany(page, t, AR_CO);
  await expect(page.getByLabel(t('Legal name (Arabic)'))).toHaveAttribute('dir', 'rtl');
  await expect(page.getByTestId('structure-tree')).toContainText(AR_CO.legalNameAr);
  // A long English row name in the Arabic tree is clipped at its END, so its start stays readable. The tree's groups
  // are collapsed with counts now, so names may not be long enough to clip here: every row name is a truncating span
  // with dir="auto" (English resolves to ltr), and any name that does clip, clips at its end.
  const rowNames = page.getByTestId('structure-tree').locator('span.truncate[dir="auto"]');
  expect(await rowNames.count(), 'row names are truncating, direction-aware spans').toBeGreaterThan(0);
  const clipped = await page.getByTestId('structure-tree').locator('span.truncate').evaluateAll(spans => spans
    .filter(s => s.scrollWidth > s.clientWidth && /^[A-Za-z]/.test(s.textContent ?? ''))
    .map(s => getComputedStyle(s).direction));
  expect(clipped.every(d => d === 'ltr'), `clipped English names must clip at their end: ${clipped}`).toBe(true);
  await shot(page, info, 'ar-2-company');
  await fillMiddleSteps(page, t, info, 'ar', 'A');

  // Step 6: only users who already hold Admin are offered, and the screen says why.
  await page.getByLabel(t('Make an existing user the company super admin')).check();
  const picker = page.locator('#ccw-companyAdmin-existingUserId');
  await expect(picker).toBeEnabled({ timeout: 15_000 });
  const offered = await picker.locator('option').allTextContents();
  expect(offered.some(o => o.includes(CO_ADMIN.email))).toBe(true);
  expect(offered.some(o => o.includes(GROUP_HR.email)), 'a group HR Manager without Admin is never offered').toBe(false);
  await expect(page.getByText(t('Only users who already hold the Admin role are listed, because a role applies to every company a person can access.'))).toBeVisible();
  await picker.selectOption(coAdminId);
  await shot(page, info, 'ar-6-admin');
  await reachReview(page, t);
  await expect(page.getByTestId('plan-group-6')).toContainText(CO_ADMIN.fullName);
  const create = page.getByRole('button', { name: t('Create company'), exact: true });
  await expect(create).toBeEnabled();
  await shot(page, info, 'ar-7-review');
  await create.click();
  // Giving an existing user the Admin role: the signed-in owner confirms it's them first.
  await confirmItsYou(page);
  await expectCreated(page, t, AR_CO, 'ar');
  await shot(page, info, 'ar-8-created');

  const owner = await tenantToken(OWNER.email);
  const [row] = await companiesWithCr(owner, AR_CO.cr);
  expect(row?.legalNameAr).toBe(AR_CO.legalNameAr);
  // The existing admin gained this company and kept the one they had.
  const me = ok(await call('GET', '/api/auth/me', { token: await tenantToken(CO_ADMIN.email) }), 'co-admin me') as any;
  expect((me.companies ?? []).map((c: any) => c.id).sort()).toEqual([starterCompany.id, row.id].sort());
  expect(me.isGroupScope).toBe(false);
  expect(pageErrors).toEqual([]);
});

test('save draft & exit, sign out, sign in: the draft resumes at the same step with the same data', async ({ page }) => {
  const t = T('en');
  const co = company('Khobar Retail', 'شركة الخبر للتجزئة');
  await signIn(page, OWNER.email);
  await openWizard(page, t);
  await fillCompany(page, t, co);
  await page.getByLabel(t('Trade name (optional)')).fill(`Khobar ${RUN}`);
  await expect(page.getByTestId('autosave-status')).toHaveAttribute('data-state', 'saved', { timeout: 15_000 });
  await next(page, t);
  await expectStep(page, t, 'Branches & locations');
  await page.getByRole('button', { name: t('Save draft & exit'), exact: true }).click();
  // Save draft & exit returns to Guided setup, where drafts are resumed.
  await expect(page).toHaveURL(/\/setup\?tab=aiSetup&path=guided/);

  // Persisted server-side, not in the browser.
  const owner = await tenantToken(OWNER.email);
  const drafts = ok(await call('GET', '/api/company-setup/drafts', { token: owner }), 'drafts') as any[];
  const summary = drafts.find(d => d.legalNameEn === co.legalNameEn);
  expect(summary?.step).toBe(3);

  await page.getByRole('button', { name: 'Open user menu' }).click();
  await page.locator('header').getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(page).toHaveURL(/\/login/);
  expect(await page.evaluate(() => localStorage.getItem('zayra_access_token'))).toBeFalsy();

  await signIn(page, OWNER.email);
  await page.goto('/setup?tab=companies');
  const card = page.getByTestId('company-drafts-card');
  await expect(card).toContainText(co.legalNameEn);
  await expect(card).toContainText(t('Stopped at step {step}: {title}', { step: 3, title: t('Branches & locations') }));
  await card.getByRole('link', { name: new RegExp(`${t('Resume')}.*${co.legalNameEn}`) }).click();
  await expectStep(page, t, 'Branches & locations');
  await page.getByTestId('wizard-back').click();
  await expectStep(page, t, 'Company details');
  await expect(page.getByLabel(t('Legal name (English)'))).toHaveValue(co.legalNameEn);
  await expect(page.getByLabel(t('Legal name (Arabic)'))).toHaveValue(co.legalNameAr);
  await expect(page.getByLabel(t('Commercial registration (CR) number'))).toHaveValue(co.cr);
  await expect(page.getByLabel(t('MOL establishment number'))).toHaveValue(co.mol);
  await expect(page.getByLabel(t('Trade name (optional)'))).toHaveValue(`Khobar ${RUN}`);
});

test('a CR number that already belongs to a company is blocked at review', async ({ page }, info) => {
  const t = T('en');
  const dup = { ...company('Duplicate CR', 'سجل مكرر'), cr: EN_CO.cr };
  await signIn(page, OWNER.email);
  const owner = await tenantToken(OWNER.email);
  const before = await companiesUsed(owner);
  await openWizard(page, t);
  await fillCompany(page, t, dup);
  await nextUntil(page, t, 'Company super admin', 14);
  await reachReview(page, t);
  await expect(page.getByTestId('review-plan')).toContainText(`CR number ${EN_CO.cr} already belongs to a company in this workspace.`);
  await expect(page.getByRole('button', { name: t('Create company'), exact: true })).toBeDisabled();
  await expect(page.getByText(t('Create is available once the items above are fixed.'))).toBeVisible();
  await shot(page, info, 'en-7-review-duplicate-cr');
  // And the server refuses the commit even if asked directly (once autosave has the duplicate on the server).
  await expect(page.getByTestId('autosave-status')).toHaveAttribute('data-state', 'saved', { timeout: 15_000 });
  const drafts = ok(await call('GET', '/api/company-setup/drafts', { token: owner }), 'drafts') as any[];
  const draft = ok(await call('GET', `/api/company-setup/drafts/${drafts.find(d => d.legalNameEn === dup.legalNameEn).id}`, { token: owner }), 'draft') as any;
  const res = await fetch(`${API}/api/company-setup`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${owner}`, 'Idempotency-Key': draft.id },
    body: JSON.stringify({ draftId: draft.id, version: draft.version }),
  });
  expect(res.status).toBe(409);
  expect(await companiesWithCr(owner, EN_CO.cr)).toHaveLength(1);
  expect(await companiesUsed(owner)).toBe(before);
});

test('a double-click on Create makes exactly one company', async ({ page }) => {
  const t = T('en');
  const commits = watchCommits(page);
  await signIn(page, OWNER.email);
  const owner = await tenantToken(OWNER.email);
  const before = await companiesUsed(owner);
  await openWizard(page, t);
  await fillCompany(page, t, DOUBLE_CO);
  await nextUntil(page, t, 'Company super admin', 14);
  await reachReview(page, t);
  const create = page.getByRole('button', { name: t('Create company'), exact: true });
  await expect(create).toBeEnabled();
  await create.dblclick();
  await confirmItsYouIfAsked(page);
  await expectCreated(page, t, DOUBLE_CO);

  expect(await companiesWithCr(owner, DOUBLE_CO.cr)).toHaveLength(1);
  expect(await companiesUsed(owner)).toBe(before + 1);
  // Whatever reached the server carried the same key (the draft id); exactly one answer created, any other replayed.
  expect(commits.length).toBeGreaterThanOrEqual(1);
  expect(new Set(commits).size).toBe(1);
  await expect.poll(() => commits.answers.length).toBe(commits.length);
  expect(commits.answers.filter(a => a.status === 201 && a.replayed === false)).toHaveLength(1);
  expect(commits.answers.filter(a => !(a.status === 201 && a.replayed === false)).every(a => a.status === 200 && a.replayed === true)).toBe(true);
  test.info().annotations.push({ type: 'commit-requests', description: JSON.stringify(commits.answers) });
  console.log(`[wizard-real] double-click sent ${commits.length} commit request(s): ${JSON.stringify(commits.answers)}`);

  // The button may swallow the second click, so race the server directly too: two simultaneous commits of one
  // fresh draft, same key. One creates, the other waits on the tenant setup lock and replays the first result.
  const race = company('Tabuk Race', 'شركة تبوك');
  const defaults = ok(await call('GET', '/api/company-setup/defaults?country=SA', { token: owner }), 'defaults') as any;
  const body = { ...defaults, company: { ...defaults.company, legalNameEn: race.legalNameEn, legalNameAr: race.legalNameAr, registrationNumber: race.cr, molEstablishmentNumber: race.mol } };
  const draft = ok(await call('POST', '/api/company-setup/drafts', { token: owner, body }), 'race draft', [200, 201]) as any;
  const commit = () => fetch(`${API}/api/company-setup`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${owner}`, 'Idempotency-Key': draft.id },
    body: JSON.stringify({ draftId: draft.id, version: draft.version }),
  }).then(async r => ({ status: r.status, body: await r.json() }));
  const raced = await Promise.all([commit(), commit()]);
  expect(raced.map(r => r.status).sort()).toEqual([200, 201]);
  expect(raced.map(r => r.body.replayed).sort()).toEqual([false, true]);
  expect(new Set(raced.map(r => r.body.companyId)).size).toBe(1);
  expect(await companiesWithCr(owner, race.cr)).toHaveLength(1);
  expect(await companiesUsed(owner)).toBe(before + 2);
});

test('the invited company super admin signs in and sees only their company', async ({ page }) => {
  const owner = await tenantToken(OWNER.email);
  const [newCo] = await companiesWithCr(owner, EN_CO.cr);
  const users = items(ok(await call('GET', `/api/access/users?search=${encodeURIComponent(INVITED.email)}`, { token: owner }), 'find invited admin'));
  const invited = users.find(u => String(u.email).toLowerCase() === INVITED.email);
  expect(invited?.roles).toContain('Admin');
  // The product's own hand-over: the single-use sign-in link the group admin sent from the success screen (no
  // password typed by them). Only when that one was emailed is a fresh one issued here.
  const resetUrl = invitedSignInLink
    ?? (ok(await call('POST', `/api/access/users/${invited.id}/password-reset-link`, { token: owner, stepUp: await stepUpToken(owner) }), 'reset link') as any).resetUrl;
  const token = decodeURIComponent(new URL(resetUrl).hash.replace(/^#token=/, ''));
  const newPassword = `Sa${randomBytes(8).toString('hex')}!aA9`;
  ok(await call('POST', '/api/auth/reset-password', { body: { resetToken: token, newPassword, tenantSlug: SLUG } }), 'set password');

  await signIn(page, INVITED.email, 'en', newPassword);
  const me = await page.evaluate(async () => {
    const r = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${localStorage.getItem('zayra_access_token')}` } });
    return r.json();
  });
  expect(me.isGroupScope).toBe(false);
  expect((me.companies ?? []).map((c: any) => c.id)).toEqual([newCo.id]);
  const visible = await page.evaluate(async () => {
    const r = await fetch('/api/companies?page=1&pageSize=100', { headers: { Authorization: `Bearer ${localStorage.getItem('zayra_access_token')}` } });
    const b = await r.json();
    return (Array.isArray(b) ? b : b.items).map((c: any) => c.legalNameEn);
  });
  expect(visible).toEqual([EN_CO.legalNameEn]);
  await page.goto('/setup?tab=companies');
  // The setup rail also carries a screen-reader-only status line naming the company ("… 6 of 12 setup items done").
  // Depending on load order it can be the first match, and it is hidden by design, so look at visible matches only.
  await expect(page.getByText(EN_CO.legalNameEn).filter({ visible: true }).first()).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText(starterCompany.legalNameEn, { exact: true })).toHaveCount(0);
  await expect(page.getByText(AR_CO.legalNameEn)).toHaveCount(0);
});

test('a company-scoped admin is refused the wizard, on screen and by the API', async ({ page }) => {
  const t = T('en');
  await signIn(page, CO_ADMIN.email);
  await page.goto('/setup/new-company');
  await expectStep(page, t, 'Create a company');
  await expect(page.getByText(t('A new company cannot be created right now.'))).toBeVisible();
  await expect(page.getByText(t('Only a group administrator can create a company. Ask your group super admin.'))).toBeVisible();
  await expect(page.getByRole('button', { name: t('Start'), exact: true })).toBeDisabled();

  const token = await tenantToken(CO_ADMIN.email);
  const context = ok(await call('GET', '/api/company-setup/context', { token }), 'context') as any;
  expect(context).toMatchObject({ canCreate: false, refusalCode: 'not_group_scope', isGroupScope: false });
  // A well-formed body (the group owner's own defaults), so the refusal is the scope gate, not model validation.
  const body = ok(await call('GET', '/api/company-setup/defaults?country=SA', { token: await tenantToken(OWNER.email) }), 'defaults');
  // Addendum A5: a company super admin may list drafts, but only their own company's existing-mode ones; creating stays group-only.
  const drafts = await call('GET', '/api/company-setup/drafts', { token });
  expect(drafts.status, 'GET drafts').toBe(200);
  expect((drafts.body as any[]).every(d => d.existingCompanyId), 'only existing-mode drafts of their own company').toBe(true);
  for (const [method, p, sent] of [
    ['GET', '/api/company-setup/defaults?country=SA', undefined],
    ['POST', '/api/company-setup/drafts', body], ['POST', '/api/company-setup/dry-run', body],
  ] as const) {
    const res = await call(method, p, { token, body: sent });
    expect(res.status, `${method} ${p}`).toBe(403);
    expect(res.body?.code, `${method} ${p}`).toBe('not_group_scope');
  }
});
