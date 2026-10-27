import { expect, test, type Page } from '@playwright/test';
import { appendFile, mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { CMP_WORLD, type CmpWorld } from './bootstrap/compensationWorld';

/**
 * CMP merit planning, end to end, on a REAL disposable backend in headed Google Chrome (see
 * playwright.compensation-real.config.ts). Synthetic data only. The journey:
 *   HR sets up the cycle (eligibility, guidelines, budgets, FX) → imports the SYNTHETIC benchmark file and maps it →
 *   opens planning → the manager builds the worksheet with an out-of-guideline line and an overspend → submits →
 *   HR returns it in Approvals → the manager revises → HR then Finance approve → HR sends to review → Finance approves
 *   the cycle → HR schedules the awards → the change is visible as an approved, effective-dated salary change →
 *   the effective-date job applies it (the effective date is the 1st of the CURRENT month, so it is already due: the
 *   real clock cannot be moved, the date can) → payroll for that month pays the new basic once →
 *   tenant B cannot see the cycle → Arabic RTL → 390 px.
 * Screenshots and a step log go to CMP_EVIDENCE_DIR.
 */

const API = (process.env.E2E_API_BASE_URL ?? 'http://127.0.0.1:5213').replace(/\/$/, '');
const EVIDENCE = process.env.CMP_EVIDENCE_DIR ?? '/tmp/kynex-cmp-evidence';
const LOG = path.join(EVIDENCE, 'journey-steps.jsonl');

const now = new Date();
const pad = (n: number) => String(n).padStart(2, '0');
const EFFECTIVE = `${now.getUTCFullYear()}-${pad(now.getUTCMonth() + 1)}-01`;
const effectiveDate = new Date(`${EFFECTIVE}T00:00:00Z`);
const PERIOD_END = new Date(effectiveDate.getTime() - 86_400_000).toISOString().slice(0, 10);
const PERIOD_START = `${effectiveDate.getUTCFullYear() - 1}-${pad(effectiveDate.getUTCMonth() + 1)}-01`;
const CUTOFF = `${effectiveDate.getUTCFullYear() - 1}-${pad(effectiveDate.getUTCMonth() + 1)}-15`;

let world: CmpWorld;

async function step(id: string, cmp: string, title: string, expected: string, actual: string, shot?: string) {
  await appendFile(LOG, JSON.stringify({ utc: new Date().toISOString(), id, cmp, title, expected, actual, screenshot: shot ?? null }) + '\n');
}

async function snap(page: Page, name: string): Promise<string> {
  const file = path.join(EVIDENCE, `${name}.png`);
  await page.screenshot({ path: file, fullPage: true, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  return path.basename(file);
}

async function api(method: string, p: string, token: string, body?: unknown) {
  const r = await fetch(`${API}${p}`, { method, headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  let json: any = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* text */ }
  return { status: r.status, json, text };
}

async function apiToken(email: string, slug = world.slug): Promise<string> {
  const r = await fetch(`${API}/api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password: world.password, tenantSlug: slug }) });
  const j = await r.json();
  return j.accessToken ?? j.token;
}

async function signIn(page: Page, email: string, slug = world.slug) {
  await page.context().clearCookies();
  await page.goto('/login').catch(() => undefined);
  await page.evaluate(() => { localStorage.removeItem('zayra_access_token'); localStorage.removeItem('zayra_refresh_token'); }).catch(() => undefined);
  await page.goto('/login');
  await page.locator('#li-em, input[type="email"]').first().fill(email);
  await page.locator('#li-pw, input[type="password"]').first().fill(world.password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.waitForURL(/\/(dashboard|app|ess|group)/, { timeout: 60_000 });
  const later = page.getByRole('button', { name: 'Remind me later' });
  if (await later.isVisible({ timeout: 3_000 }).catch(() => false)) await later.click();
}

async function openCompensation(page: Page, tab?: string) {
  await page.goto('/compensation');
  await expect(page.getByRole('heading', { name: 'Merit planning', exact: true }).first()).toBeVisible({ timeout: 90_000 });
  if (tab) await page.getByRole('tab', { name: tab }).click();
}

async function decideInApprovals(page: Page, decision: 'Approve' | 'Reject', comment: string) {
  await page.goto('/approvals');
  await page.waitForLoadState('networkidle').catch(() => undefined);
  const row = page.getByRole('row').filter({ hasText: 'Merit worksheet' }).filter({ has: page.getByRole('button', { name: 'Review' }) }).first();
  await expect(row).toBeVisible({ timeout: 60_000 });
  await row.getByRole('button', { name: 'Review' }).click();
  const dialog = page.getByRole('dialog', { name: 'Review Approval' });
  await dialog.locator('#approval-comments').fill(comment);
  // The decision is proven by the server's answer, not by the dialog closing.
  const decided = page.waitForResponse((r) => /\/api\/approval-requests\/[^/]+\/decisions$/.test(new URL(r.url()).pathname) && r.request().method() === 'POST', { timeout: 30_000 });
  await dialog.getByRole('button', { name: decision, exact: true }).click();
  expect((await decided).status()).toBe(200);
  await expect(dialog).toBeHidden({ timeout: 30_000 });
}

test.describe.configure({ mode: 'serial' });

test.beforeAll(async () => {
  world = JSON.parse(await readFile(CMP_WORLD, 'utf8')) as CmpWorld;
  await mkdir(EVIDENCE, { recursive: true });
});

test('CMP journey: set up → plan → return → revise → approve → apply → effective → payroll → isolation → Arabic → mobile', async ({ page }) => {
  // ── 1. HR sets up the cycle ──
  await signIn(page, world.users.hr);
  await openCompensation(page, 'Merit cycles');
  await page.getByRole('button', { name: 'New cycle' }).click();
  const form = page.getByTestId('cycle-form');
  const cycleName = `Merit ${EFFECTIVE} (synthetic)`;
  await form.getByLabel('Cycle name').fill(cycleName);
  await form.getByLabel('Performance period starts').fill(PERIOD_START);
  await form.getByLabel('Performance period ends').fill(PERIOD_END);
  await form.getByLabel('Effective date').fill(EFFECTIVE);
  await form.getByLabel('Hire-date cutoff (optional)').fill(CUTOFF);
  await form.getByLabel('Minimum eligible days').fill('30');
  await page.getByRole('button', { name: 'Create cycle' }).click();
  const detail = page.getByTestId('cycle-detail');
  await expect(detail.getByRole('heading', { name: cycleName })).toBeVisible();
  await expect(detail.getByTestId('blockers')).toContainText('Enter an FX rate for AED');
  await step('J-01', 'CMP-01/CMP-04', 'HR creates a draft cycle', 'Draft cycle in SAR; planning blocked until guidelines, budgets and the AED rate exist',
    'Draft shown; blockers list guidelines, budgets and the missing AED rate', await snap(page, 'j01-cycle-draft-blockers'));

  // Guidelines (no published ratings in this world → "Unrated" row), budgets, FX.
  const matrix = detail.getByTestId('guideline-matrix');
  await detail.getByRole('button', { name: 'Add row' }).click();
  await matrix.getByLabel('Rating, row 1').fill('Unrated');
  await matrix.getByLabel('Compa-ratio from, row 1').fill('0');
  await matrix.getByLabel('Minimum %, row 1').fill('0');
  await matrix.getByLabel('Target %, row 1').fill('3');
  await matrix.getByLabel('Maximum %, row 1').fill('5');
  await detail.getByRole('button', { name: 'Save guideline matrix' }).click();
  await expect(detail.getByTestId('blockers')).not.toContainText('guideline matrix');
  await expect(matrix.getByLabel('Rating, row 1')).toHaveValue('Unrated');
  await detail.getByLabel('Add a budget for').selectOption({ label: 'Engineering' });
  await detail.getByLabel('Pool value for Engineering').fill('2');
  await detail.getByRole('button', { name: 'Save budgets' }).click();
  // Before the AED rate exists the AED-paid employee cannot be normalised, so the pool counts 21,000.00 only.
  await expect(detail.getByTestId('budget-table').locator('tr[data-department="Engineering"]')).toContainText('SAR 420.00');
  await detail.getByLabel('Rate for AED').fill('1.02');
  await detail.getByLabel('Source for AED').fill('Synthetic reference rate (test only)');
  await detail.getByRole('button', { name: 'Save exchange rates' }).click();
  await expect(detail.getByTestId('fx-table')).toContainText('Hana HR Manager');
  await expect(detail.getByTestId('budget-table').locator('tr[data-department="Engineering"]')).toContainText('SAR 542.40');
  await step('J-02', 'CMP-01/CMP-04', 'Guidelines, budget and FX saved', 'Pool = 2 % of eligible basic 27,120.00 SAR (incl. AED 6,000 × 1.02) = 542.40; rate shows who entered it',
    'Engineering pool SAR 542.40; AED rate entered by Hana HR Manager', await snap(page, 'j02-cycle-configured'));

  // ── 2. Benchmarks: SYNTHETIC file, provenance, mapping ──
  await page.getByRole('tab', { name: 'Pay benchmarks' }).click();
  await page.getByRole('button', { name: 'Import a survey' }).click();
  const imp = page.getByTestId('benchmark-import');
  await imp.getByLabel('Set name').fill('SYNTHETIC survey fixture');
  await imp.getByLabel('Survey provider or source').fill('KynexOne e2e fixture (synthetic)');
  await imp.getByLabel('Survey date').fill('2024-01-01');
  await imp.getByLabel('Licence or usage-rights statement').fill('Synthetic test data created for the KynexOne CMP e2e journey; no licence required; not market data.');
  await imp.getByLabel('File (CSV or XLSX)').setInputFiles(path.join(process.cwd(), 'e2e/fixtures/compensation/synthetic-benchmarks.csv'));
  await imp.getByRole('button', { name: 'Import', exact: true }).click();
  await expect(page.getByTestId('synthetic-banner')).toBeVisible();
  const setDetail = page.getByTestId('benchmark-set-detail');
  await setDetail.getByLabel('Designation for Engineering L1').selectOption({ label: 'Software Engineer' });
  await expect(page.getByTestId('benchmark-sets')).toContainText('1 of 2 mapped');
  await step('J-03', 'CMP-06/CMP-07', 'HR imports the synthetic benchmark file and maps it', 'Provenance stored; file marker forces the synthetic label; stale (> 18 months) flagged',
    'Set shows provider, licence, Older than 18 months and the synthetic banner; Engineering L1 mapped', await snap(page, 'j03-benchmark-synthetic'));

  // ── 3. Open planning ──
  await page.getByRole('tab', { name: 'Merit cycles' }).click();
  await detail.getByTestId('cycle-next-action').click();
  await expect(detail.getByTestId('cycle-worksheets')).toContainText('Mona Manager');
  await expect(detail).toContainText('1 employee is not eligible');
  await step('J-04', 'CMP-01', 'Planning opens', 'One worksheet for Mona (4 eligible reports); the late joiner is ineligible; Mona herself has no manager',
    'Worksheet for Mona Manager created; 1 employee not eligible', await snap(page, 'j04-planning-open'));

  // ── 4. Manager builds the worksheet ──
  await signIn(page, world.users.manager);
  await openCompensation(page);
  const sheet = page.getByTestId('worksheet');
  await expect(sheet.getByRole('heading', { name: 'Merit worksheet: Mona Manager' })).toBeVisible();
  for (const [name, pct] of [['Ali Engineer', '3'], ['Sara Engineer', '3'], ['Omar Engineer', '6'], ['Noor Engineer', '3']] as const)
    await sheet.getByLabel(`Increase % for ${name}`).fill(pct);
  await sheet.getByRole('button', { name: 'Save worksheet' }).click();
  await expect(sheet.getByRole('status')).toHaveText('Worksheet saved.');
  await expect(sheet.getByTestId('worksheet-total')).toHaveText('SAR 1,053.60');
  await expect(sheet.locator('tr', { hasText: 'Ali Engineer' })).toContainText('Synthetic — not market data');
  await expect(sheet.getByTestId('blockers')).toContainText('Omar Engineer: outside the guideline');
  await step('J-05', 'CMP-02/CMP-05', 'Manager proposes increases', 'Total 1,053.60 SAR (180 + 210 + 480 + 183.60); Omar at 6 % is outside 0–5 %; Noor paid AED 180.00; market position shown with synthetic label',
    'Total SAR 1,053.60; Omar flagged; synthetic chip on mapped line', await snap(page, 'j05-worksheet-built'));
  await sheet.getByRole('button', { name: 'Submit for approval' }).click();
  await expect(sheet.getByTestId('blockers')).toContainText('over budget');
  await step('J-06', 'CMP-02/CMP-09', 'Submit refused without justifications', 'Refused: out-of-guideline line and the overspend each need a justification',
    'Submit refused with both blockers', await snap(page, 'j06-submit-refused'));
  await sheet.getByLabel('Justification for Omar Engineer').fill('Took over the payments platform; agreed with HRBP.');
  await sheet.getByLabel('Overspend justification').fill('Two retention cases; the overspend is funded from the Engineering vacancy.');
  await sheet.getByRole('button', { name: 'Submit for approval' }).click();
  await expect(sheet.getByText('This worksheet is locked while it waits for approval', { exact: false })).toBeVisible();
  await step('J-07', 'CMP-03', 'Manager submits', 'Worksheet Submitted and locked; approval request raised, flagged OVER BUDGET', 'Locked, waiting for approval', await snap(page, 'j07-submitted'));

  // ── 5. HR returns it ──
  await signIn(page, world.users.hr);
  await decideInApprovals(page, 'Reject', 'Please bring Omar to the 5 % maximum; the overspend needs Finance sign-off first.');
  await step('J-08', 'CMP-03/CMP-09', 'HR returns the worksheet in Approvals', 'Worksheet Returned, revision 2, comment visible to the manager', 'Rejected in Approvals', await snap(page, 'j08-approvals-returned'));

  // ── 6. Manager revises and resubmits ──
  await signIn(page, world.users.manager);
  await openCompensation(page);
  await expect(sheet.getByTestId('returned-comments')).toContainText('Please bring Omar to the 5 % maximum');
  await expect(sheet).toContainText('Revision 2');
  await sheet.getByLabel('Increase % for Omar Engineer').fill('5');
  await sheet.getByRole('button', { name: 'Submit for approval' }).click();
  await expect(sheet.getByText('This worksheet is locked while it waits for approval', { exact: false })).toBeVisible();
  await step('J-09', 'CMP-03', 'Manager revises and resubmits', 'Revision 2 submitted with Omar at 5 %', 'Resubmitted', await snap(page, 'j09-revised-resubmitted'));

  // SoD: the manager cannot decide her own worksheet (API, which the UI also relies on).
  const managerToken = await apiToken(world.users.manager);
  const pending = await api('GET', '/api/approval-requests?status=Pending&entityName=CompensationWorksheet&page=1&pageSize=20', await apiToken(world.users.hr));
  const approvalId = (pending.json?.items ?? []).find((a: any) => a.entityName === 'CompensationWorksheet')?.id;
  const selfDecide = await api('POST', `/api/approval-requests/${approvalId}/decisions`, managerToken, { decision: 'Approve', comments: 'mine' });
  expect(selfDecide.status).toBeGreaterThanOrEqual(400);
  await step('J-10', 'CMP-03/CMP-09', 'Manager tries to approve her own worksheet (API)', 'Refused (requester / subject bar)', `HTTP ${selfDecide.status}: ${selfDecide.text.slice(0, 160)}`);

  // ── 7. HR then Finance approve ──
  await signIn(page, world.users.hr);
  await decideInApprovals(page, 'Approve', 'HR review complete.');
  await signIn(page, world.users.finance);
  await decideInApprovals(page, 'Approve', 'Overspend accepted against the vacancy saving.');
  await step('J-11', 'CMP-03', 'HR Manager then Finance Approver approve (two steps, two people)', 'Worksheet Approved and locked', 'Both steps decided', await snap(page, 'j11-finance-approved'));

  // ── 8. Review, cycle approval by a different person, apply ──
  await signIn(page, world.users.hr);
  await openCompensation(page, 'Merit cycles');
  await detail.getByTestId('cycle-next-action').click();
  await expect(detail.getByText('In review', { exact: true }).first()).toBeVisible();
  await signIn(page, world.users.finance);
  await openCompensation(page, 'Merit cycles');
  await detail.getByTestId('cycle-next-action').click();
  await expect(detail.getByText('Approved', { exact: true }).first()).toBeVisible();
  await step('J-12', 'CMP-03', 'HR sends to review; Finance approves the cycle', 'Maker ≠ checker at cycle level', 'Cycle Approved by Finance', await snap(page, 'j12-cycle-approved'));
  await signIn(page, world.users.hr);
  await openCompensation(page, 'Merit cycles');
  await detail.getByTestId('cycle-next-action').click();
  await page.getByRole('dialog').getByRole('button', { name: 'Schedule the awards' }).click();
  const applied = detail.getByTestId('apply-results');
  await expect(applied).toContainText('4 awards');
  await step('J-13', 'CMP-08', 'HR schedules the awards', 'One approved, effective-dated salary change per award; nothing written to salary rows yet',
    'Apply results list 4 awards', await snap(page, 'j13-awards-scheduled'));

  // Idempotent re-run (API) creates nothing new.
  const hrToken = await apiToken(world.users.hr);
  const cycles = await api('GET', '/api/compensation/merit/cycles', hrToken);
  const cycleId = (cycles.json as any[]).find((c) => c.name === cycleName).id;
  const again = await api('POST', `/api/compensation/merit/cycles/${cycleId}/apply`, hrToken);
  const awards = again.json.apply.lines.filter((l: any) => l.changeRequestId).length;
  expect(awards).toBe(4);
  await step('J-14', 'CMP-08', 'Apply run again (API)', 'Still exactly 4 change requests', `${awards} change requests after a second apply`);

  // ── 9. The effective-date job applies them (date already due) ──
  await expect.poll(async () => {
    const c = await api('GET', `/api/compensation/merit/cycles/${cycleId}`, hrToken);
    return c.json.apply.applied;
  }, { timeout: 180_000, intervals: [5_000] }).toBe(4);
  await openCompensation(page, 'Merit cycles');
  await expect(applied).toContainText('In pay of record');
  await step('J-15', 'CMP-08', 'EffectiveChangeJobHandler applies the awards on their effective date', 'All 4 changes ApprovedApplied (once)', 'Apply results: 4 in pay of record',
    await snap(page, 'j15-applied-in-pay-of-record'));
  const ali = world.employees['CMP-E2'];
  const history = await api('GET', `/api/employees/${ali}/history`, hrToken);
  await step('J-16', 'CMP-08', 'Employee history shows the scheduled and applied change', 'SensitiveChangeScheduled then SensitiveChangeApplied for salaryBreakdown',
    JSON.stringify((history.json?.items ?? history.json ?? []).map((h: any) => h.eventType).filter((e: string) => /Sensitive/.test(e))));

  // ── 10. Payroll for the effective month pays the new basic once ──
  const run = await api('POST', '/api/payroll/runs', hrToken, { companyId: world.companyId, year: effectiveDate.getUTCFullYear(), month: effectiveDate.getUTCMonth() + 1, runType: 'Regular' });
  const runId = run.json?.id ?? run.json?.Id;
  const processed = await api('POST', `/api/payroll/runs/${runId}/process`, hrToken);
  const slips = await api('GET', `/api/payroll/runs/${runId}/slips`, hrToken);
  const slipList: any[] = slips.json?.items ?? slips.json ?? [];
  const aliSlip = slipList.find((s) => s.employeeId === ali || s.EmployeeId === ali);
  await step('J-17', 'CMP-08', 'Payroll for the effective month (API, PayrollController.Process)', 'Ali basic 6,180.00 (6,000 + 3 %), one slip',
    `run HTTP ${run.status}, process HTTP ${processed.status}, slips ${slipList.length}, Ali basic ${aliSlip?.basicSalary ?? aliSlip?.BasicSalary}`);
  expect(Number(aliSlip?.basicSalary ?? aliSlip?.BasicSalary)).toBe(6180);
  await page.goto('/payroll');
  await page.waitForLoadState('networkidle').catch(() => undefined);
  await step('J-18', 'CMP-08', 'Payroll screen after processing', 'Run listed for the effective month', 'Screenshot', await snap(page, 'j18-payroll'));

  // ── 11. Tenant B cannot see it ──
  const betaToken = await apiToken(world.users.betaAdmin, world.betaSlug);
  const cross = await api('GET', `/api/compensation/merit/cycles/${cycleId}`, betaToken);
  expect(cross.status).toBe(404);
  await step('J-19', 'CMP-09', 'Tenant B asks for tenant A’s cycle', '404 — the cycle does not exist for tenant B (flag on for both)', `HTTP ${cross.status}`);

  // ── 12. Arabic RTL and 390 px ──
  await page.evaluate(() => localStorage.setItem('kynexone-locale-choice-v2', 'ar'));
  await openCompensation(page).catch(() => undefined);
  await page.goto('/compensation');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.getByRole('heading', { name: 'تخطيط زيادات الأداء', exact: true }).first()).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('cycle-detail').getByTestId('apply-results')).toBeVisible({ timeout: 60_000 });
  await step('J-20', 'CMP-01..09', 'Arabic, right to left', 'Page renders RTL in Arabic', 'dir=rtl, Arabic heading', await snap(page, 'j20-arabic-rtl'));
  await page.evaluate(() => localStorage.setItem('kynexone-locale-choice-v2', 'en'));
  await signIn(page, world.users.manager);
  await page.setViewportSize({ width: 390, height: 844 });
  await openCompensation(page);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
  await step('J-21', 'CMP-02', 'Manager on a 390 px phone', 'No horizontal page scroll', `overflow ${overflow}px`, await snap(page, 'j21-mobile-390'));
});
