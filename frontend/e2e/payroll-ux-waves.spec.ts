import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { payrollUx } from '../src/i18n/payrollUx';
import { bankFileGuide } from '../src/i18n/bankFileGuide';
import { boot, HANDLED, PERMS, USER, type Handler, type Locale } from './demo-polish.fixture';

/**
 * Payroll → Bank / WPS Files and Payment Tracking, the UX waves (W2 reasons, W4 page size, W5 bank-file steps, W6 confirm before a
 * GL post, W7 names, W8 lock wording). Every API request is intercepted: this proves the UI contract (states, wiring, wording, EN and
 * AR), not authorisation or the real payloads.
 * Run (dev server on its own port and dist dir):  DEMO_POLISH_PORT=5174 npx playwright test -c e2e/playwright.demo-polish.config.ts payroll-ux-waves
 * SHOT_DIR=<dir> SHOT_PREFIX=before|after also saves the review screenshots of the Bank / WPS tab.
 */

const LOCALES: Locale[] = ['en', 'ar'];
import { RUN_ID, BATCH_ID, RUN, BATCH, RECORDS, bankHandler } from './bank-wps.fixture';

const say = (locale: Locale, key: string) => (locale === 'ar' ? payrollUx.ar[key] ?? bankFileGuide.ar[key] ?? `MISSING ${key}` : key);
/** The records drawer: the payment records are one click from step 2, not a table in the page. */
const openRecords = async (page: Page) => { await page.getByTestId('bank-records-open').click(); await expect(page.getByRole('dialog')).toBeVisible(); };
const url = (tab: string) => `/payroll?tab=${tab}&run=${RUN_ID}`;
const BANK_WORDS: Record<Locale, { tab: string }> = { en: { tab: 'Bank / WPS Files' }, ar: { tab: 'ملفات البنك' } };
void BANK_WORDS;

async function shot(page: Page, name: string, locale: Locale, form: string) {
  const dir = process.env.SHOT_DIR;
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: `${dir}/${process.env.SHOT_PREFIX ?? 'after'}-${name}-${locale}-${form}.png`, fullPage: true });
}

// ── Review screenshots: the Bank / WPS tab as a finance user meets it ──────────────────────────────

for (const locale of LOCALES) {
  for (const [form, viewport] of [['desktop', { width: 1366, height: 768 }], ['phone', { width: 390, height: 844 }]] as const) {
    test.describe(`shots: Bank / WPS tab (${locale}, ${form})`, () => {
      test.use({ viewport });
      test('the tab with a Saudi batch open', async ({ page }) => {
        await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500, stages: process.env.SHOT_PREFIX === 'before' ? 'open' : 'default' });
        await expect(page.getByTestId('bank-guide-sentence')).toBeVisible({ timeout: 90_000 });
        await page.waitForTimeout(2500);
        await shot(page, 'bank-wps', locale, form);
      });
    });
  }
}

// ── W2: say why something is missing ───────────────────────────────────────────────────────────────

const FINANCE_PERMS = PERMS.filter(p => !p.startsWith('payroll.export'));

for (const locale of LOCALES) {
  test.describe(`W2: reasons instead of nothing (${locale})`, () => {
    test('without the export permission the bank file panel says so and what to do', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ perms: FINANCE_PERMS, roles: ['Finance Approver'], records: 'forbidden' }), waitMs: 2500 });
      const note = page.getByTestId('bank-file-no-permission');
      await expect(note).toBeVisible({ timeout: 90_000 });
      await expect(note).toContainText(say(locale, 'You cannot prepare the bank file'));
      await expect(note).toContainText(say(locale, 'Preparing the Saudi bank file needs the Payroll export permission. Ask an administrator to give it to your role, then come back to this batch.'));
      await expect(page.getByTestId('bank-setup-summary')).toHaveCount(0);
      // The records the user may not read are not "No records."
      await expect(page.getByTestId('bank-records-forbidden')).toContainText(say(locale, 'The payment records need the Payroll export permission. Ask an administrator to give it to your role.'));
      await expect(page.getByText(/^(No records\.|لا توجد سجلات.?)$/)).toHaveCount(0);
    });

    test('with the switch off the panel names the switch and who turns it on', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ availability: { enabled: false } }), waitMs: 2500 });
      const note = page.getByTestId('bank-file-off');
      await expect(note).toBeVisible({ timeout: 90_000 });
      await expect(note).toContainText(say(locale, 'Bank file export is switched off for this company'));
      await expect(note).toContainText(say(locale, 'The platform team switches bank file export on for each company. Ask them to turn it on, then reload this batch.'));
    });

    test('a failed availability check says so, and Retry brings the panel', async ({ page }) => {
      let calls = 0;
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ availability: () => (++calls === 1 ? 'fail' : { enabled: true }) }), waitMs: 2500 });
      const note = page.getByTestId('bank-file-check-failed');
      await expect(note).toBeVisible({ timeout: 90_000 });
      await expect(note).toContainText(say(locale, 'Could not check whether the bank file can be prepared'));
      await note.getByRole('button').click();
      await expect(page.getByTestId('bank-setup-summary')).toBeVisible({ timeout: 30_000 });
      await expect(note).toHaveCount(0);
    });

    test('while the batches load there is a skeleton, not "No batches yet."', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ slowBatches: 4000 }), waitMs: 1500 });
      const panel = page.getByRole('tabpanel');
      await expect(panel.locator('.kx-skeleton').first()).toBeVisible({ timeout: 90_000 });
      await expect(panel.getByText(/^(No batches yet\.|لا توجد دفعات بعد.?)$/)).toHaveCount(0);
      await expect(step(page, 'batch')).toContainText('PAY-202611-220353', { timeout: 30_000 });
    });

    test('a failed batch list says it failed, never "No batches yet."', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ batches: 'fail' }), waitMs: 2500 });
      await expect(page.getByRole('tabpanel').getByTestId('list-load-failed')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByText(/^(No batches yet\.|لا توجد دفعات بعد.?)$/)).toHaveCount(0);
    });

    test('a failed records read says it failed, never "No records."', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ records: 'fail' }), waitMs: 2500 });
      await expect(page.getByRole('tabpanel').getByTestId('list-load-failed')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByText(/^(No records\.|لا توجد سجلات.?)$/)).toHaveCount(0);
    });

    test('Payment Tracking: a failed list is not "No payment batches yet"', async ({ page }) => {
      await boot(page, { locale, url: url('payment-tracking'), routes: bankHandler({ batches: 'fail' }), waitMs: 2500 });
      await expect(page.getByRole('tabpanel').getByTestId('list-load-failed')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByText(/^(No payment batches yet|لا توجد دفعات دفع بعد)$/)).toHaveCount(0);
    });
  });
}

// ── W4: shrink the page ───────────────────────────────────────────────────────────────────────────

const MANY = Array.from({ length: 40 }, (_, i) => ({
  id: `m${i}`, paymentBatchId: BATCH_ID, employeeId: 300 + i, employeeName: `Employee Number ${i + 1}`, employeeCode: `EVT2-${String(100 + i)}`, amount: 9000 + i,
  iban: `SA03800000006080101675${i}`, wpsReference: `REF-M${i}`, status: 'Pending',
}));

for (const locale of LOCALES) {
  test.describe(`W4: a smaller page (${locale})`, () => {
    test.use({ viewport: { width: 1366, height: 768 } });

    test('the stage cards start folded once a run is chosen, the next step stays, and the choice is remembered', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500, stages: 'default' });
      const toggle = page.getByTestId('pipeline-stages-toggle');
      await expect(toggle).toBeVisible({ timeout: 90_000 });
      await expect(toggle).toHaveAttribute('aria-expanded', 'false');
      await expect(page.getByTestId('pipeline-stages')).toBeHidden();
      await expect(page.getByTestId('pipeline-next-action')).toBeVisible();
      await expect(page.getByTestId('pipeline-folded-stage')).toBeVisible();
      await toggle.click();
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await expect(page.getByTestId('pipeline-stages')).toBeVisible();
      await page.reload();
      await expect(page.getByTestId('pipeline-stages-toggle')).toHaveAttribute('aria-expanded', 'true', { timeout: 90_000 });
      await page.getByTestId('pipeline-stages-toggle').click();
      await page.reload();
      await expect(page.getByTestId('pipeline-stages-toggle')).toHaveAttribute('aria-expanded', 'false', { timeout: 90_000 });
    });

    test('with no run chosen the pipeline stays open (runs needing attention are the content)', async ({ page }) => {
      await boot(page, { locale, url: '/payroll?tab=dashboard', routes: bankHandler(), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('pipeline-attention')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByTestId('pipeline-stages-toggle')).toHaveCount(0);
    });

    test('the sixteen tabs are five labelled groups and every tab is reachable without scrolling the strip', async ({ page }) => {
      await boot(page, { locale, url: url('dashboard'), routes: bankHandler(), waitMs: 2500 });
      const tabs = page.getByRole('tab');
      await expect(tabs).toHaveCount(16, { timeout: 90_000 });
      await expect(page.locator('[data-tab-group]')).toHaveCount(5);
      const boxes = await tabs.evaluateAll(els => els.map(e => { const r = e.getBoundingClientRect(); return { l: r.left, r: r.right, w: r.width }; }));
      for (const b of boxes) { expect(b.w).toBeGreaterThan(0); expect(b.l).toBeGreaterThanOrEqual(0); expect(b.r).toBeLessThanOrEqual(1366); }
      const strip = page.locator('[data-roving-tabs="payroll"]');
      expect(await strip.evaluate(el => el.scrollWidth - el.clientWidth)).toBeLessThanOrEqual(1);
      // Arrow keys still walk the tabs in order.
      // (From Bank / WPS to Payment Tracking: the next tab in reading order, which in Arabic is the one on the left.)
      await page.locator('#payroll-tab-bank-wps').focus();
      await page.keyboard.press(locale === 'ar' ? 'ArrowLeft' : 'ArrowRight');
      await expect(page.locator('#payroll-tab-payment-tracking')).toHaveAttribute('aria-selected', 'true');
    });

    test('changing tab does not move the page', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500 });
      await expect(page.locator('#payroll-tab-payment-tracking')).toBeVisible({ timeout: 90_000 });
      expect(await page.evaluate(() => window.scrollY)).toBe(0);
      await page.evaluate(() => window.scrollTo(0, 120));
      const before = await page.evaluate(() => window.scrollY);
      await page.locator('#payroll-tab-payment-tracking').click();
      await page.waitForTimeout(400);
      expect(Math.abs((await page.evaluate(() => window.scrollY)) - before)).toBeLessThanOrEqual(2);
    });

    test('the top bar does not cover an anchor, the assistant is the compact circle, and a long batch scrolls inside its own region', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ records: MANY }), waitMs: 2500 });
      await expect(page.getByTestId('bank-records-open')).toBeVisible({ timeout: 90_000 });
      await openRecords(page);
      const region = page.getByTestId('bank-records-scroll');
      await expect(region).toBeVisible();
      await expect(region).toHaveAttribute('role', 'region');
      const dims = await region.evaluate(el => ({ sh: el.scrollHeight, ch: el.clientHeight }));
      expect(dims.sh).toBeGreaterThan(dims.ch);
      expect(await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).scrollPaddingTop))).toBeGreaterThanOrEqual(60);
      await expect(page.getByRole('button', { name: /Kody/ })).toHaveAttribute('data-compact', 'true');
    });
  });

  test.describe(`W4: on a phone (${locale})`, () => {
    test.use({ viewport: { width: 390, height: 844 } });

    test('a deep link does not scroll the page, and a wide table scrolls inside its card', async ({ page }) => {
      await boot(page, { locale, url: url('payment-tracking'), routes: bankHandler(), waitMs: 2500 });
      await expect(page.locator('#payroll-tab-payment-tracking')).toBeVisible({ timeout: 90_000 });
      await page.waitForTimeout(500);
      expect(await page.evaluate(() => window.scrollY)).toBe(0);
      const card = page.getByRole('tabpanel').locator('.surface').filter({ has: page.locator('table') }).first();
      await expect(card).toBeVisible();
      const m = await card.evaluate(el => ({ ox: getComputedStyle(el).overflowX, sw: el.scrollWidth, cw: el.clientWidth }));
      expect(m.ox).toBe('auto');
      expect(m.sw).toBeGreaterThan(m.cw);
      // The page itself has no sideways scroll.
      expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(1);
    });
  });
}

// ── W5: the bank file as four steps ───────────────────────────────────────────────────────────────
// (The stage tests of the guided flow, one per place a batch can be, are in bank-guided.spec.ts.)

const step = (page: Page, key: string) => page.locator(`[data-testid="bank-file-steps"] [data-step="${key}"]`);
async function pickDate(page: Page, locale: Locale) {
  const group = page.getByRole('group', { name: locale === 'ar' ? 'تاريخ قيمة الإيداع' : 'Credit value date' });
  const selects = group.getByRole('combobox');
  await expect(selects).toHaveCount(3);
  for (let i = 0; i < 3; i++) {
    const sel = selects.nth(i);
    const values = await sel.locator('option').evaluateAll(els => els.map(e => (e as HTMLOptionElement).value));
    await sel.selectOption(values.includes('2026') ? '2026' : values.includes('31') ? '30' : '11');
  }
}

for (const locale of LOCALES) {
  test.describe(`W5: bank file steps (${locale})`, () => {
    test.use({ viewport: { width: 1366, height: 768 } });

    test('the steps follow the batch: check is current, then done once validated, then the file is made and the download step offers the next move', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500 });
      await expect(step(page, 'batch')).toHaveAttribute('data-state', 'done', { timeout: 90_000 });
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'current');
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'upcoming');
      await expect(step(page, 'create').getByTestId('bank-step-reason-create')).toContainText(say(locale, 'Waits for a successful check.'));
      await expect(step(page, 'download')).toHaveAttribute('data-state', 'upcoming');
      await expect(page.getByTestId('bank-file-steps').locator('[aria-current="step"]')).toHaveCount(1);

      await pickDate(page, locale);
      await page.getByRole('button', { name: locale === 'ar' ? 'تحقق' : 'Validate', exact: true }).click();
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'done');
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'current');
      await page.getByRole('button', { name: locale === 'ar' ? 'إنشاء الملفات' : 'Generate files', exact: true }).click();
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'done');
      await expect(step(page, 'download')).toHaveAttribute('data-state', 'current');
      await step(page, 'download').getByRole('button', { name: say(locale, 'Open Payment Tracking') }).click();
      await expect(page.locator('#payroll-tab-payment-tracking')).toHaveAttribute('aria-selected', 'true');
    });

    test('validation blockers block the check step and say where to fix them', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ validation: { canExport: false, errors: [{ code: 'iban_missing', message: 'IBAN is missing', employeeId: 203 }] } }), waitMs: 2500 });
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'current', { timeout: 90_000 });
      await pickDate(page, locale);
      await page.getByRole('button', { name: locale === 'ar' ? 'تحقق' : 'Validate', exact: true }).click();
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'blocked');
      // The step that is blocked is the stage, so its reason is the header's sentence (one place, not two).
      await expect(page.getByTestId('bank-guide-sentence')).toContainText(say(locale, 'The check found blockers. Fix each one below, then check again.'));
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'upcoming');
    });

    test('a closed gate blocks step 2 with its reason (switch off, no permission)', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ availability: { enabled: false } }), waitMs: 2500 });
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'blocked', { timeout: 90_000 });
      await expect(page.getByTestId('bank-guide-sentence')).toContainText(say(locale, 'Bank file export is switched off for this company. Ask the platform team.'));
    });

    test('a run that is not locked and has no batch: step 1 is blocked, with the reason', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ batches: [], runStatus: 'Approved' }), waitMs: 2500 });
      await expect(step(page, 'batch')).toHaveAttribute('data-state', 'blocked', { timeout: 90_000 });
      await expect(page.getByTestId('bank-guide-sentence')).toContainText(locale === 'ar' ? /قفل/ : /Locked/);
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'upcoming');
    });

    test('the bank batch number is suggested from the payment batch number, is shown as read-only with Change, stays editable, and says where it came from', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500 });
      const suggested = locale === 'ar' ? 'مقترح من رقم دفعة الدفع' : 'Suggested from the payment batch number';
      const readonly = page.getByTestId('bank-batch-reference-readonly');
      await expect(readonly).toContainText('202611220353', { timeout: 90_000 });
      await expect(page.getByText(suggested, { exact: false })).toBeVisible();
      await readonly.getByRole('button', { name: locale === 'ar' ? 'تغيير' : 'Change' }).click();
      const input = page.getByLabel(locale === 'ar' ? 'رقم الدفعة' : 'Batch number', { exact: true });
      await expect(input).toHaveValue('202611220353');
      await expect(input).toBeFocused();
      await expect(page.getByText(suggested, { exact: false })).toBeVisible();
      await input.fill('202611999');
      await expect(input).toHaveValue('202611999');
      await expect(page.getByText(suggested, { exact: false })).toHaveCount(0);
      // The suggestion is only a starting value: the request carries what is in the box.
      await pickDate(page, locale);
      const validate = page.waitForRequest(r => r.url().endsWith('/validate'));
      await page.getByRole('button', { name: locale === 'ar' ? 'تحقق' : 'Validate', exact: true }).click();
      expect((await validate).postDataJSON()).toMatchObject({ batchReference: '202611999' });
    });

    test('the hedging sentence "if it does not appear" is gone: the reason is named where the panel would be', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ perms: FINANCE_PERMS, roles: ['Finance Approver'] }), waitMs: 2500 });
      await expect(page.getByTestId('bank-file-no-permission')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByText(/If it does not, you need|وإن لم يظهر/)).toHaveCount(0);
    });
  });
}

// ── W6: say what will post to the GL before it posts ──────────────────────────────────────────────

const ACCEPTED = { ...BATCH, status: 'FileGenerated', wpsStatus: 'Accepted', effectiveWpsStatus: 'Accepted', wpsStatusLabel: 'Accepted', allowedNextStatuses: [] };
const PAID = { ...ACCEPTED, wpsStatus: 'Paid', effectiveWpsStatus: 'Paid', wpsStatusLabel: 'Paid' };

for (const locale of LOCALES) {
  test.describe(`W6: confirm before a GL posting (${locale})`, () => {
    test.use({ viewport: { width: 1440, height: 900 } });
    const dialog = (page: Page) => page.getByRole('dialog');

    test('Settle net pay names the batch, journal, amount and date; Cancel posts nothing; confirming posts once', async ({ page }) => {
      const posts: string[] = [];
      await boot(page, { locale, url: url('payment-tracking'), routes: bankHandler({ batches: [ACCEPTED], posts, extra: (p, _s, m) => (m === 'POST' && p.endsWith('/settle') ? { batchId: BATCH_ID, runId: RUN_ID, wpsStatus: 'Paid', settled: 9 } : undefined) }), waitMs: 2500 });
      const settle = page.getByRole('button', { name: locale === 'ar' ? /تسوية صافي الرواتب في دفتر الأستاذ/ : 'Settle net pay to GL' }).first();
      await expect(settle).toBeVisible({ timeout: 90_000 });
      await settle.click();
      const d = dialog(page);
      await expect(d).toBeVisible();
      await expect(d).toContainText(say(locale, 'Post the net pay settlement to the general ledger?'));
      const facts = d.getByTestId('gl-post-confirm');
      await expect(facts).toContainText('PAY-202611-220353');
      await expect(facts).toContainText('133,588.90');
      await expect(facts).toContainText(say(locale, 'Payment batch'));
      await expect(facts).toContainText(say(locale, 'Journal'));
      expect(posts.filter(x => x.endsWith('/settle'))).toHaveLength(0);
      await d.getByRole('button', { name: locale === 'ar' ? 'إلغاء' : 'Cancel' }).click();
      await expect(d).toHaveCount(0);
      expect(posts.filter(x => x.endsWith('/settle'))).toHaveLength(0);
      await settle.click();
      await d.getByRole('button', { name: say(locale, 'Post settlement') }).click();
      await expect.poll(() => posts.filter(x => x.endsWith('/settle')).length).toBe(1);
    });

    test('Remit names the group and says the amounts appear on the GL Journal tab', async ({ page }) => {
      const posts: string[] = [];
      await boot(page, { locale, url: url('payment-tracking'), routes: bankHandler({ batches: [PAID], posts }), waitMs: 2500 });
      const remit = page.getByRole('button', { name: locale === 'ar' ? 'تحويل' : 'Remit', exact: true }).first();
      await expect(remit).toBeVisible({ timeout: 90_000 });
      await remit.click();
      const d = dialog(page);
      await expect(d).toContainText(say(locale, 'Post the statutory remittance to the general ledger?'));
      await expect(d.getByTestId('gl-post-confirm')).toContainText(say(locale, 'GOSI, tax and loan remittances'));
      expect(posts.filter(x => x.endsWith('/remit'))).toHaveLength(0);
      await d.getByRole('button', { name: locale === 'ar' ? 'إلغاء' : 'Cancel' }).click();
      expect(posts.filter(x => x.endsWith('/remit'))).toHaveLength(0);
    });
  });
}

// ── W7: names, and no English left in Arabic on these tabs ───────────────────────────────────────

/** Latin words of four or more letters: what an Arabic reader would meet as English. Mock data (names, codes) and product/bank terms are not chrome. */
const latinWords = (text: string) => (text.replace(/https?:\/\/\S+/g, ' ').replace(/[A-Za-z]+(?=EVT)|EVT2?-\d+/g, ' ').match(/[A-Za-z]{4,}/g) ?? []).filter(w => !/^(ANB|Connect|CSV|IBAN|SHA|SAR|Mudad|header|body|zip|REF|EVT|PAY|Acme|Arabia|Maha|Almutairi|Layla|Harbi|Fahad|Alharbi|Nora|Khalid|Alotaibi|Sara|Alzahrani|Omar|Aldosari|Huda|Alqahtani|Yousef|Alshehri|Employee|Number|Kody|Riyadh|Olaya|Building|Test|Account|Admin|BIC|SWIFT|WPS|MOL|Qiwa|GOSI|WPY|Arab|National|Bank|payroll|payment|instruction|file)$/i.test(w));

for (const locale of LOCALES) {
  test.describe(`W7: names and language (${locale})`, () => {
    test.use({ viewport: { width: 1440, height: 900 } });

    test('a record from an older server with no name shows the name the run knows, not Emp #id', async ({ page }) => {
      const bare = RECORDS.map(({ employeeName: _n, employeeCode: _c, ...rest }) => rest);
      const population = { runId: RUN_ID, runType: 'Regular', includesRecurringPay: true, populationMode: 'All', eligibleCount: 9, includedCount: 9, excludedCount: 0, notEligibleCount: 0,
        included: RECORDS.map(r => ({ employeeId: r.employeeId, code: r.employeeCode, name: r.employeeName })), excluded: [], notEligible: [] };
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ records: bare, extra: p => (/\/population$/.test(p) ? population : undefined) }), waitMs: 2500 });
      await expect(page.getByTestId('bank-records-open')).toBeVisible({ timeout: 90_000 });
      await openRecords(page);
      const row = page.getByRole('row', { name: /REF-1\b/ });
      await expect(row).toContainText('Maha Almutairi');
      await expect(row).not.toContainText(/Emp #|#200/);
    });

    test('an employee code stays on one line in the records table; the name is what wraps', async ({ page }) => {
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 2500 });
      await expect(page.getByTestId('bank-records-open')).toBeVisible({ timeout: 90_000 });
      await openRecords(page);
      const code = page.getByRole('row', { name: /REF-1\b/ }).locator('bdi', { hasText: 'EVT2-0001' });
      await expect(code).toBeVisible();
      const box = await code.boundingBox();
      expect(box!.height, 'a code that broke at its hyphen would be two lines tall').toBeLessThan(20);
      await expect(code.locator('xpath=..')).toHaveClass(/whitespace-nowrap/);
    });

    test('validation findings name the employee', async ({ page }) => {
      const population = { runId: RUN_ID, runType: 'Regular', includesRecurringPay: true, populationMode: 'All', eligibleCount: 1, includedCount: 1, excludedCount: 0, notEligibleCount: 0,
        included: [{ employeeId: 226, code: 'EVT2-0226', name: 'Layla Al-Harbi' }], excluded: [], notEligible: [] };
      const finding = { id: 'v1', employeeId: 226, severity: 'Error', code: 'MISSING_IBAN', message: 'IBAN is missing', isResolved: false };
      await boot(page, { locale, url: url('validation'), routes: bankHandler({ extra: p => (/\/population$/.test(p) ? population : /\/runs\/[^/]+\/validate$/.test(p) ? [finding] : undefined) }), waitMs: 2500 });
      await expect(page.getByRole('tabpanel')).toContainText('Layla Al-Harbi', { timeout: 90_000 });
      await expect(page.getByRole('tabpanel')).not.toContainText('Emp #226');
    });

    test('the Bank / WPS and Payment Tracking tabs leave no English in Arabic', async ({ page }) => {
      test.skip(locale === 'en', 'Arabic only');
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler({ batches: [BATCH] }), waitMs: 3000 });
      await expect(page.getByTestId('bank-records-open')).toBeVisible({ timeout: 90_000 });
      await page.waitForTimeout(1500);
      // The guided flow, its footer notice opened, and the records drawer: all of it, not just the first screen.
      await page.getByTestId('bank-pays-nobody').locator('xpath=..').getByRole('button').click();
      await expect(page.getByTestId('bank-notice-details')).toBeVisible();
      const bank = await page.locator('main').innerText();
      expect(latinWords(bank), `Bank / WPS: ${[...new Set(latinWords(bank))].join(', ')}`).toEqual([]);
      await openRecords(page);
      const drawer = await page.getByRole('dialog').innerText();
      expect(latinWords(drawer), `Bank / WPS records: ${[...new Set(latinWords(drawer))].join(', ')}`).toEqual([]);
      await page.keyboard.press('Escape');
      await page.locator('#payroll-tab-payment-tracking').click();
      await expect(page.getByRole('tabpanel').getByRole('table')).toBeVisible({ timeout: 30_000 });
      const tracking = await page.getByRole('tabpanel').innerText();
      expect(latinWords(tracking), `Payment Tracking: ${[...new Set(latinWords(tracking))].join(', ')}`).toEqual([]);
    });

    test('Payment Tracking on an accepted batch leaves no English in Arabic', async ({ page }) => {
      test.skip(locale === 'en', 'Arabic only');
      await boot(page, { locale, url: url('payment-tracking'), routes: bankHandler({ batches: [{ ...ACCEPTED, paymentExclusions: [{ employeeId: 204, employeeCode: 'EVT2-0005', amount: 500, reasonCode: 'CASH', reason: 'Paid in cash', canRecordOutsidePayment: true }], excludedTotal: 500 }] }), waitMs: 3000 });
      await expect(page.getByRole('tabpanel').getByRole('table')).toBeVisible({ timeout: 90_000 });
      const text = await page.getByRole('tabpanel').innerText();
      expect(latinWords(text.replace(/Paid in cash/g, '')), `Payment Tracking (accepted): ${[...new Set(latinWords(text))].join(', ')}`).toEqual([]);
    });

    test('page header, tab groups and pipeline are Arabic', async ({ page }) => {
      test.skip(locale === 'en', 'Arabic only');
      await boot(page, { locale, url: url('bank-wps'), routes: bankHandler(), waitMs: 3000 });
      await expect(page.getByTestId('payroll-tabs-anchor')).toBeVisible({ timeout: 90_000 });
      const head = await page.locator('main').first().evaluate(el => (el.querySelector('h1')?.parentElement?.innerText ?? '') + ' ' + (document.querySelector('[data-testid="payroll-tabs-anchor"]') as HTMLElement).innerText);
      expect(latinWords(head), head).toEqual([]);
    });
  });
}
