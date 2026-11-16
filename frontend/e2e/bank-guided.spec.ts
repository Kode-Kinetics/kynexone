import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import { boot, HANDLED, type Handler, type Locale } from './demo-polish.fixture';
import { BATCH, BATCH_ID, RECORDS, RUN_ID, bankHandler, type Opts } from './bank-wps.fixture';

/**
 * Payroll → Bank / WPS Files: one stage for the whole page (lib/payOut.ts). Every API request is intercepted: this proves the UI contract,
 * not authorisation or the real payloads.
 *
 * For every stage (setup gate, 1, 2 clean, 2 blocked, 3, 4, 5 waiting, 5 all confirmed), in English and Arabic: the run bar's chip, the run
 * picker's option, the step strip, the open panel and the primary button say the same thing; the panel's heading and its one primary are on
 * screen without scrolling at 1440x900 and 1366x768. Then the shell: the Payroll section nav keeps ?tab= deep links, every section renders,
 * the app menu folds inside Payroll, and a phone has no sideways scroll.
 *
 * Run (dev server on its own port and dist dir):  DEMO_POLISH_PORT=5191 npx playwright test -c e2e/playwright.demo-polish.config.ts bank-guided
 * BANK_GUIDED_SHOTS=<dir> also saves the walk-through and the mockup-matching screenshots.
 */

const LOCALES: Locale[] = ['en', 'ar'];
const url = `/payroll?tab=bank-wps&run=${RUN_ID}`;
const bp = '/api/payroll/bank-exports';
const say = (locale: Locale, key: string, params?: Record<string, string | number>) => translate(locale, key, params);
const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

const EMPTY_SETUP = { formatId: '', molEstablishmentId: '', mainAccountNumber: '', organizationName: '', organizationAddress1: '', organizationAddress2: '', organizationAddress3: '', companyName: '', narrative: '', batchType: '', autoWpsUpload: false, nationalUnifiedNo: '', debitAccountIban: '' };
const FILE = { id: '44444444-4444-4444-8444-444444444444', formatId: 'anb-connect-csv-v1', files: [{ name: 'header.csv', sha256: 'a'.repeat(64) }, { name: 'body.csv', sha256: 'b'.repeat(64) }], batchReference: '202611220353', paymentDate: '2026-11-30', employeeCount: 9, totalAmount: 133588.9 };
const GENERATED = { ...BATCH, status: 'FileGenerated', wpsStatus: 'Generated', effectiveWpsStatus: 'Generated', wpsStatusLabel: 'Generated', bankFileAvailable: true, bankFileGenerated: true, allowedNextStatuses: ['Downloaded', 'Submitted'] };
const SUBMITTED = { ...GENERATED, wpsStatus: 'Submitted', effectiveWpsStatus: 'Submitted', wpsStatusLabel: 'Submitted', wpsSubmissionReference: 'ANB-55821-0924', wpsStatusChangedAtUtc: '2026-11-16T11:05:00Z', allowedNextStatuses: ['Accepted', 'Rejected'] };
const ACCEPTED = { ...SUBMITTED, wpsStatus: 'Accepted', effectiveWpsStatus: 'Accepted', wpsStatusLabel: 'Accepted', allowedNextStatuses: ['Paid'] };
const withStatus = (paid: number, returned: number) => RECORDS.map((r, i) => ({ ...r, status: i < paid ? 'Paid' : i < paid + returned ? 'Returned' : 'Pending' }));
const BLOCKER = { canExport: false, errors: [{ code: 'bic_missing', message: 'BIC is missing for this employee.', employeeId: 203 }] };

interface Want { stage: string; chip: string; chipText: string; strip: string | null; primary: string | null; next?: string }

/** The run bar's chip, the run option, the strip, the panel and the primary all say the same thing. */
async function expectStage(page: Page, w: Want) {
  await expect(page.getByTestId('bank-guide')).toHaveAttribute('data-stage', w.stage, { timeout: 90_000 });
  const chip = page.getByTestId('bank-guide-chip');
  await expect(chip).toHaveAttribute('data-chip', w.chip);
  await expect(chip).toHaveText(w.chipText);
  // The run picker never says something the page does not: the open run's option ends with the chip's words.
  await expect(page.locator('#pipeline-run-picker option:checked')).toHaveText(new RegExp(`— ${escape(w.chipText)}$`));
  const current = page.locator('[data-step-strip="bank-file"] [aria-current="step"]');
  if (w.strip === null) await expect(current).toHaveCount(0);
  else { await expect(current).toHaveCount(1); await expect(current).toHaveAttribute('data-step', w.strip); }
  await expect(page.locator('[data-step-panel]')).toHaveAttribute('data-step-panel', w.stage);
  const primary = page.getByTestId('bank-guide-primary');
  if (w.primary === null) await expect(primary).toHaveCount(0); else await expect(primary).toHaveText(w.primary);
  // Exactly one primary on the page (none in the run bar or the header).
  await expect(page.locator('main .btn-primary:visible')).toHaveCount(w.primary === null ? 0 : 1);
  await expect(page.getByTestId('run-pipeline').locator('.btn-primary')).toHaveCount(0);
  if (w.next) await expect(page.getByTestId('bank-guide-next')).toContainText(w.next);
}

/** The step's heading and its one action are on screen without scrolling. */
async function expectInView(page: Page) {
  await page.evaluate(() => window.scrollTo(0, 0));
  const vh = page.viewportSize()!.height;
  const heading = await page.locator('[data-step-panel] [data-step-heading]').boundingBox();
  expect(heading!.y, 'step heading top').toBeLessThanOrEqual(480);
  const action = page.locator('[data-primary-action]');
  if (await action.count()) {
    const box = await action.boundingBox();
    expect(box!.y + box!.height, 'primary bottom').toBeLessThanOrEqual(vh);
    await expect(action).toBeInViewport();
  }
}

type Case = { name: string; opts: Opts; act?: (page: Page, locale: Locale) => Promise<void>; want: (l: Locale) => Want };
const CASES: Case[] = [
  {
    name: 'setup gate', opts: { settings: EMPTY_SETUP },
    want: l => ({ stage: 'setup', chip: 'setup', chipText: say(l, 'Bank setup needed'), strip: 'setup', primary: say(l, 'Complete bank setup'), next: say(l, 'Check employees') }),
  },
  {
    name: '1 no payment batch', opts: { batches: [] },
    want: l => ({ stage: 'batch', chip: 'no_batch', chipText: say(l, 'No payment batch yet'), strip: 'batch', primary: say(l, 'Create payment batch'), next: say(l, 'Next: Check employees.') }),
  },
  {
    name: '2 clean', opts: {},
    want: l => ({ stage: 'check', chip: 'no_file', chipText: say(l, 'No bank file yet'), strip: 'check', primary: say(l, 'Check employees'), next: say(l, 'File details') }),
  },
  {
    name: '2 blocked', opts: { validation: BLOCKER },
    act: async page => { await page.getByTestId('bank-guide-primary').click(); },
    want: l => ({ stage: 'check', chip: 'attention', chipText: say(l, 'Needs a fix'), strip: 'check', primary: say(l, 'Check again') }),
  },
  {
    name: '3 file details', opts: {},
    act: async page => { await page.getByTestId('bank-guide-primary').click(); },
    want: l => ({ stage: 'create', chip: 'no_file', chipText: say(l, 'No bank file yet'), strip: 'create', primary: say(l, 'Create bank file'), next: say(l, 'Send to bank') }),
  },
  {
    name: '4 send to bank', opts: { batches: [GENERATED], existingExport: FILE },
    want: l => ({ stage: 'send', chip: 'created', chipText: say(l, 'File created — not sent'), strip: 'send', primary: say(l, 'Download bank file'), next: say(l, 'Next: Record it as submitted once the bank has it.') }),
  },
  {
    name: '5 waiting for the bank', opts: { batches: [SUBMITTED], existingExport: FILE, records: withStatus(6, 0) },
    want: l => ({ stage: 'response', chip: 'submitted', chipText: say(l, 'Submitted — waiting for the bank ({answered} of {total})', { answered: 6, total: 9 }), strip: 'response', primary: say(l, 'Record the bank’s response'), next: say(l, 'Next: Finance settles the payments after the bank confirms.') }),
  },
  {
    name: '5 all confirmed', opts: { batches: [ACCEPTED], existingExport: FILE, records: withStatus(9, 0) },
    want: l => ({ stage: 'finished', chip: 'confirmed', chipText: say(l, 'Bank confirmed {total} of {total}', { total: 9 }), strip: null, primary: say(l, 'Settle payments') }),
  },
];

for (const locale of LOCALES) {
  for (const viewport of [{ width: 1440, height: 900 }, { width: 1366, height: 768 }]) {
    test.describe(`one stage, said once (${locale}, ${viewport.width}x${viewport.height})`, () => {
      test.use({ viewport });
      for (const c of CASES) {
        test(c.name, async ({ page }) => {
          const { errors } = await boot(page, { locale, url, routes: bankHandler(c.opts), waitMs: 2500, stages: 'default' });
          await expect(page.getByTestId('bank-guide')).toBeVisible({ timeout: 90_000 });
          if (c.act) { await expect(page.getByTestId('bank-guide-primary')).toBeEnabled({ timeout: 60_000 }); await c.act(page, locale); }
          await expectStage(page, c.want(locale));
          await expectInView(page);
          expect(errors).toEqual([]);
        });
      }
    });
  }
}

// ── The behaviour behind the stages ────────────────────────────────────────────────────────────────

for (const locale of LOCALES) {
  test.describe(`the flow (${locale})`, () => {
    test.use({ viewport: { width: 1440, height: 900 } });

    test('a blocked check: the summary takes focus, Fix opens THAT employee, and a clean check after it moves to step 3 with a confirmation', async ({ page }) => {
      let fixed = false;
      const extra: Handler = (path, _s, method) => {
        if (path === `${bp}/batches/${BATCH_ID}/validate` && method === 'POST') {
          return fixed ? { canExport: true, errors: [], warnings: [], employeeCount: 9, totalAmount: 133588.9, currency: 'SAR', formatId: 'anb-connect-csv-v1' }
            : { ...BLOCKER, warnings: [], employeeCount: 9, totalAmount: 133588.9, currency: 'SAR', formatId: 'anb-connect-csv-v1' };
        }
        if (path === '/api/employees/203' && method === 'GET') return { id: 203, employeeCode: 'EVT2-0004', fullName: 'Nora Alharbi', wpsBankDetails: '' };
        if (path === '/api/employees/203' && method === 'PUT') { fixed = true; return { id: 203 }; }
        return undefined;
      };
      await boot(page, { locale, url, routes: bankHandler({ extra }), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('bank-guide-primary')).toBeEnabled({ timeout: 90_000 });
      await page.getByTestId('bank-guide-primary').click();
      const summary = page.getByTestId('bank-error-summary');
      await expect(summary).toHaveAttribute('role', 'alert');
      await expect(summary).toContainText(say(locale, 'The bank code (BIC) is missing for this employee.'));
      await expect(page.getByTestId('bank-guide-live')).toBeFocused();
      await summary.getByTestId('bank-blocker-fix').click();
      const drawer = page.getByRole('dialog');
      await expect(drawer.getByLabel(locale === 'ar' ? 'الموظف (من هذه الدفعة)' : 'Employee (from this batch)')).toHaveValue('203', { timeout: 15_000 });
      await drawer.locator('input[dir="ltr"]').nth(0).fill('ARNBSARI');
      await drawer.locator('input[dir="ltr"]').nth(1).fill('Riyadh');
      await drawer.locator('input[dir="ltr"]').nth(2).fill('Olaya');
      await drawer.locator('input[dir="ltr"]').nth(3).fill('Building 10');
      await drawer.locator('form button[type="submit"]').click();
      await expect(drawer.locator('form button[type="submit"]')).toBeDisabled({ timeout: 15_000 });
      await page.keyboard.press('Escape');
      await expect(drawer).toHaveCount(0);
      await page.getByTestId('bank-guide-primary').click();
      await expectStage(page, { stage: 'create', chip: 'no_file', chipText: say(locale, 'No bank file yet'), strip: 'create', primary: say(locale, 'Create bank file') });
      await expect(page.getByTestId('bank-guide-confirmation')).toContainText(say(locale, 'Checked {n} employees — no blockers.', { n: 9 }));
      await expect(page.locator('[data-step-panel] [data-step-heading]')).toBeFocused();
    });

    test('an account-number blocker is fixed in People, not in a drawer that cannot change it', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ validation: { canExport: false, errors: [{ code: 'account_missing', message: 'No account number.', employeeId: 204 }] } }), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('bank-guide-primary')).toBeEnabled({ timeout: 90_000 });
      await page.getByTestId('bank-guide-primary').click();
      await expect(page.getByTestId('bank-blocker-fix')).toHaveAttribute('href', '/people/204');
    });

    test('step 3: one labelled date with the next working day, Fridays and Saturdays off; Create checks again and makes the file', async ({ page }) => {
      const posts: string[] = [];
      await boot(page, { locale, url, routes: bankHandler({ posts }), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('bank-guide-primary')).toBeEnabled({ timeout: 90_000 });
      await page.getByTestId('bank-guide-primary').click();
      await expect(page.getByTestId('bank-guide')).toHaveAttribute('data-stage', 'create');
      const field = page.getByTestId('bank-value-date').locator('input');
      await expect(field).toHaveCount(1);
      // One labelled control: its accessible name is the label.
      await expect(page.getByRole('combobox', { name: new RegExp(`^${escape(say(locale, 'Salary credit date (value date)'))}`) })).toHaveCount(1);
      const shown = await field.inputValue();
      expect(shown).toMatch(/\d{4}/);
      expect(shown).not.toMatch(/(Fri|Sat|الجمعة|السبت)/);
      await field.click();
      const dialog = page.getByRole('dialog', { name: say(locale, 'Choose a date') });
      await expect(dialog.getByRole('grid')).toBeVisible();
      // A Friday cannot be chosen by mouse.
      const friday = dialog.locator('[data-day][aria-disabled="true"]').first();
      const before = await field.inputValue();
      await friday.click({ force: true });
      await expect(field).toHaveValue(before);
      // Escape closes and returns focus to the field.
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(field).toBeFocused();
      // The recap echoes the date.
      await expect(page.getByTestId('bank-create-recap')).toContainText(before);
      await page.getByTestId('bank-guide-primary').click();
      await expectStage(page, { stage: 'send', chip: 'created', chipText: say(locale, 'File created — not sent'), strip: 'send', primary: say(locale, 'Download bank file') });
      expect(posts.filter(p => p.endsWith('/generate'))).toHaveLength(1);
      await expect(page.locator('#bank-wps-generated-export')).toContainText('header.csv');
    });

    test('download, then Record as submitted (bank reference required, sent once) hands over to the bank\'s response', async ({ page }) => {
      let submitted = false;
      const posts: string[] = [];
      const extra: Handler = (path, _s, method) => {
        if (path === '/api/payroll/payment-batches' && method === 'GET') return [submitted ? SUBMITTED : GENERATED];
        if (path === `/api/payroll/payment-batches/${BATCH_ID}/wps-status` && method === 'POST') { submitted = true; return { batchId: BATCH_ID, wpsStatus: 'Submitted' }; }
        return undefined;
      };
      await boot(page, { locale, url, routes: bankHandler({ existingExport: FILE, extra, posts }), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('bank-guide-primary')).toHaveText(say(locale, 'Download bank file'), { timeout: 90_000 });
      const download = page.waitForEvent('download');
      await page.getByTestId('bank-guide-primary').click();
      await download;
      await expectStage(page, { stage: 'send', chip: 'downloaded', chipText: say(locale, 'Downloaded — not yet submitted'), strip: 'send', primary: say(locale, 'Record as submitted') });
      await page.getByTestId('bank-guide-primary').click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByTestId('bank-submit-dialog')).toBeVisible();
      // The reference is required; nothing is sent without it.
      await dialog.getByRole('button', { name: say(locale, 'Record as submitted') }).click();
      await expect(dialog.getByRole('alert')).toContainText(say(locale, 'Enter the bank submission reference.'));
      expect(posts.filter(p => p.endsWith('/wps-status'))).toHaveLength(0);
      await dialog.getByLabel(say(locale, 'Bank submission reference')).fill('ANB-55821-0924');
      await dialog.getByRole('button', { name: say(locale, 'Record as submitted') }).click();
      await expect(dialog).toHaveCount(0);
      await expectStage(page, { stage: 'response', chip: 'submitted', chipText: say(locale, 'Submitted — waiting for the bank ({answered} of {total})', { answered: 0, total: 9 }), strip: 'response', primary: say(locale, 'Record the bank’s response') });
      expect(posts.filter(p => p.endsWith('/wps-status'))).toHaveLength(1);
      await expect(page.getByTestId('bank-guide-confirmation')).toContainText('ANB-55821-0924');
    });

    test('a Saudi batch whose WPS says Generated but has no bank file is "No bank file yet" everywhere', async ({ page }) => {
      const REGISTER_ONLY = { ...BATCH, status: 'Generated', wpsStatus: 'Generated', effectiveWpsStatus: 'Generated', wpsStatusLabel: 'Generated (file ready for the bank)', bankFileAvailable: true, bankFileGenerated: false };
      await boot(page, { locale, url, routes: bankHandler({ batches: [REGISTER_ONLY] }), waitMs: 2500, stages: 'default' });
      await expectStage(page, { stage: 'check', chip: 'no_file', chipText: say(locale, 'No bank file yet'), strip: 'check', primary: say(locale, 'Check employees') });
      await expect(page.locator('main')).not.toContainText(locale === 'ar' ? 'تم إنشاء ملف البنك' : 'bank file generated');
      await expect(page.locator('main')).not.toContainText(say(locale, 'Paid / Exported'));
    });

    test('several batches and none open: step 1 lists them; Open batch is off until one is chosen', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ batches: [BATCH, { ...BATCH, id: 'b2', batchNumber: 'PAY-202611-220354' }] }), waitMs: 2500, stages: 'default' });
      await expectStage(page, { stage: 'batch', chip: 'no_file', chipText: say(locale, 'No bank file yet'), strip: 'batch', primary: say(locale, 'Open batch') });
      await expect(page.getByTestId('bank-guide-primary')).toBeDisabled();
      await expect(page.getByTestId('bank-guide-primary-reason')).toHaveText(say(locale, 'Choose the payment batch, then open it.'));
      await page.getByRole('radio').first().check();
      await page.getByTestId('bank-guide-primary').click();
      await expect(page.getByTestId('bank-guide')).toHaveAttribute('data-stage', 'check', { timeout: 30_000 });
    });

    test('the run is not locked: step 1 is blocked and the primary is off, with the reason in the Next slot', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ batches: [], runStatus: 'Approved' }), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('bank-guide')).toHaveAttribute('data-stage', 'batch', { timeout: 90_000 });
      await expect(page.getByTestId('bank-guide-primary')).toBeDisabled();
      await expect(page.getByTestId('bank-guide-primary')).toHaveAttribute('aria-describedby', /.+/);
      await expect(page.getByTestId('bank-guide-primary-reason')).toHaveText(say(locale, 'Lock the run first: a payment batch can only be created once the run is Locked.'));
    });

    test('the setup form, the template editor and the beneficiary editor are not in the page until opened', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler(), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('bank-guide')).toHaveAttribute('data-stage', 'check', { timeout: 90_000 });
      await expect(page.locator('input[name="molEstablishmentId"]')).toHaveCount(0);
      await expect(page.getByText(/Custom bank salary-file templates|قوالب ملفات الرواتب/)).toHaveCount(0);
      await page.getByTestId('bank-more').click();
      await page.getByRole('menuitem', { name: say(locale, 'Bank setup') }).click();
      await expect(page.getByRole('dialog').locator('input[name="molEstablishmentId"]')).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page.getByTestId('bank-more')).toBeFocused();
    });

    test('without any payment permission the tab says who can give it, and shows no flow', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ perms: ['dashboard.read', 'payroll.read', 'payroll.write', 'payroll.approve'] }), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('bank-restricted')).toContainText(say(locale, 'You can’t prepare bank files'), { timeout: 90_000 });
      await expect(page.getByTestId('bank-guide')).toHaveCount(0);
    });
  });
}

// ── The Payroll shell ───────────────────────────────────────────────────────────────────────────────

for (const locale of LOCALES) {
  test.describe(`the Payroll shell (${locale})`, () => {
    test.use({ viewport: { width: 1440, height: 900 } });

    test('the section nav keeps ?tab= links, marks the open section, and every section renders', async ({ page }) => {
      const { errors } = await boot(page, { locale, url, routes: bankHandler(), waitMs: 2500, stages: 'default' });
      const nav = page.getByTestId('payroll-section-nav');
      await expect(nav.locator('[aria-current="page"]')).toHaveAttribute('id', 'payroll-tab-bank-wps', { timeout: 90_000 });
      const links = nav.locator('a[id^="payroll-tab-"]');
      const count = await links.count();
      expect(count).toBeGreaterThanOrEqual(14);
      for (let i = 0; i < count; i++) {
        const link = links.nth(i);
        const id = (await link.getAttribute('id'))!.replace('payroll-tab-', '');
        await expect(link).toHaveAttribute('href', new RegExp(`tab=${id}.*run=${RUN_ID}|run=${RUN_ID}.*tab=${id}|^/payroll\\?run=${RUN_ID}$`));
        const label = (await link.innerText()).trim();
        await link.click();
        await expect(page).toHaveURL(new RegExp(id === 'dashboard' ? `run=${RUN_ID}` : `tab=${id}`));
        await expect(nav.locator('[aria-current="page"]')).toHaveAttribute('id', `payroll-tab-${id}`);
        await expect(page.locator('h1')).toHaveText(label);
        await expect(page.getByTestId('payroll-section-panel')).toBeVisible();
      }
      expect(errors).toEqual([]);
    });

    test('inside Payroll the app menu folds to icons; expanding it is remembered there, and other pages keep their own setting', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler(), waitMs: 2500, stages: 'default' });
      const expand = page.getByRole('button', { name: 'Expand sidebar' });
      await expect(expand).toBeVisible({ timeout: 90_000 });
      await expand.click();
      await expect(page.getByRole('button', { name: 'Collapse sidebar' })).toBeVisible();
      await page.reload();
      await expect(page.getByRole('button', { name: 'Collapse sidebar' })).toBeVisible({ timeout: 90_000 });
      expect(await page.evaluate(() => localStorage.getItem('sidebar-collapsed'))).toBeNull();
    });

    test('from 1680px the facts become the Release summary rail beside the steps', async ({ page }) => {
      await page.setViewportSize({ width: 1920, height: 1080 });
      await boot(page, { locale, url, routes: bankHandler(), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('bank-release-summary')).toBeVisible({ timeout: 90_000 });
      await expect(page.getByTestId('bank-file-facts')).toBeHidden();
      await expect(page.getByTestId('bank-release-summary')).toContainText('PAY-202611-220353');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    });

    test('a phone has no sideways scroll, the section switcher opens the list, and the primary is fixed at the bottom', async ({ page }) => {
      await page.setViewportSize({ width: 390, height: 844 });
      await boot(page, { locale, url, routes: bankHandler({ settings: EMPTY_SETUP }), waitMs: 2500, stages: 'default' });
      await expect(page.getByTestId('bank-guide')).toHaveAttribute('data-stage', 'setup', { timeout: 90_000 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      const bar = page.locator('[data-action-bar]');
      expect(await bar.evaluate(el => getComputedStyle(el).position)).toBe('fixed');
      await expect(page.getByTestId('bank-guide-primary')).toBeInViewport();
      await page.getByTestId('payroll-section-toggle').click();
      const sheet = page.getByRole('dialog', { name: say(locale, 'Payroll sections') });
      await expect(sheet).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(sheet).toBeHidden();
      await expect(page.getByTestId('payroll-section-toggle')).toBeFocused();
    });
  });
}

// ── The walk-through and the mockup-matching screenshots ───────────────────────────────────────────

const SHOTS = process.env.BANK_GUIDED_SHOTS;
const snap = async (page: Page, name: string) => {
  fs.mkdirSync(SHOTS!, { recursive: true });
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/${name}.png` });
};

test.describe('walk-through as a payroll officer (EN, 1440)', () => {
  test.skip(!SHOTS, 'BANK_GUIDED_SHOTS is not set');
  test.use({ viewport: { width: 1440, height: 900 } });

  test('choose batch → setup → check (blocker → Fix → check again) → date → create → download → record submitted → waiting', async ({ page }) => {
    let setup: Record<string, unknown> = EMPTY_SETUP;
    let fixed = false;
    let exported: unknown = null;
    let submitted = false;
    const B1 = { ...BATCH, paymentExclusions: [{ employeeId: 300, employeeCode: 'EVT2-0010', amount: 4150, reasonCode: 'paid_outside_bank_file', reason: 'Paid by cash' }], excludedTotal: 4150, totalAmount: 129438.9 };
    const extra: Handler = (path, _s, method, route) => {
      if (path === `${bp}/companies/c1/settings` && method === 'PUT') { setup = JSON.parse(route.request().postData() ?? '{}'); return setup; }
      if (path === `${bp}/companies/c1/settings`) return setup;
      if (path === '/api/payroll/payment-batches' && method === 'GET') {
        const b1 = submitted ? { ...B1, ...SUBMITTED, totalAmount: 129438.9, paymentExclusions: B1.paymentExclusions, excludedTotal: 4150 } : exported ? { ...B1, ...GENERATED, totalAmount: 129438.9, paymentExclusions: B1.paymentExclusions, excludedTotal: 4150 } : B1;
        return [b1, { ...BATCH, id: 'b2', batchNumber: 'PAY-202611-220354', paymentMethod: 'BankTransfer' }];
      }
      if (path === `${bp}/batches/${BATCH_ID}/context`) return { companyId: 'c1', companyName: 'Acme Arabia', countryCode: 'SA', runStatus: 'Locked', existingExport: exported };
      if (path === `${bp}/batches/${BATCH_ID}/validate` && method === 'POST') {
        return fixed ? { canExport: true, errors: [], warnings: [], employeeCount: 9, totalAmount: 129438.9, currency: 'SAR', formatId: 'anb-connect-csv-v1' }
          : { ...BLOCKER, warnings: [], employeeCount: 9, totalAmount: 129438.9, currency: 'SAR', formatId: 'anb-connect-csv-v1' };
      }
      if (path === `${bp}/batches/${BATCH_ID}/generate` && method === 'POST') { const body = JSON.parse(route.request().postData() ?? '{}'); exported = { ...FILE, ...body, totalAmount: 129438.9 }; return exported; }
      if (path === `/api/payroll/payment-batches/${BATCH_ID}/wps-status` && method === 'POST') { submitted = true; return { batchId: BATCH_ID, wpsStatus: 'Submitted' }; }
      if (path === '/api/employees/203' && method === 'GET') return { id: 203, employeeCode: 'EVT2-0004', fullName: 'Nora Alharbi', wpsBankDetails: '' };
      if (path === '/api/employees/203' && method === 'PUT') { fixed = true; return { id: 203 }; }
      return undefined;
    };
    await boot(page, { locale: 'en', url, routes: bankHandler({ extra }), waitMs: 2500, stages: 'default' });

    await expectStage(page, { stage: 'batch', chip: 'no_file', chipText: 'No bank file yet', strip: 'batch', primary: 'Open batch' });
    await snap(page, 'walk-01-choose-batch');
    await page.getByRole('radio').first().check();
    await page.getByTestId('bank-guide-primary').click();

    await expectStage(page, { stage: 'setup', chip: 'setup', chipText: 'Bank setup needed', strip: 'setup', primary: 'Complete bank setup' });
    await snap(page, 'walk-02-setup-gate');
    await page.getByTestId('bank-guide-primary').click();
    const drawer = page.getByRole('dialog');
    for (const [name, value] of [['molEstablishmentId', '7-1234567'], ['mainAccountNumber', '0108061198800026'], ['organizationName', 'Acme Arabia'], ['companyName', 'Acme Arabia LLC'], ['organizationAddress1', 'Riyadh'], ['organizationAddress2', 'Olaya'], ['organizationAddress3', 'Building 10'], ['narrative', 'November payroll']]) {
      await drawer.locator(`input[name="${name}"]`).fill(value);
    }
    await drawer.locator('select').filter({ has: page.locator('option[value="PAYROLL"]') }).selectOption('PAYROLL');
    await snap(page, 'walk-03-setup-drawer');
    await drawer.locator('form button[type="submit"]').click();

    await expectStage(page, { stage: 'check', chip: 'no_file', chipText: 'No bank file yet', strip: 'check', primary: 'Check employees' });
    await expect(page.getByTestId('bank-guide-confirmation')).toContainText('Bank setup saved.');
    await snap(page, 'walk-04-check');
    await page.getByTestId('bank-guide-primary').click();

    await expectStage(page, { stage: 'check', chip: 'attention', chipText: 'Needs a fix', strip: 'check', primary: 'Check again' });
    await snap(page, 'walk-05-check-blocked');
    await page.getByTestId('bank-blocker-fix').click();
    await expect(drawer.getByLabel('Employee (from this batch)')).toHaveValue('203', { timeout: 15_000 });
    await drawer.locator('input[dir="ltr"]').nth(0).fill('ARNBSARI');
    await drawer.locator('input[dir="ltr"]').nth(1).fill('Riyadh');
    await drawer.locator('input[dir="ltr"]').nth(2).fill('Olaya');
    await drawer.locator('input[dir="ltr"]').nth(3).fill('Building 10');
    await snap(page, 'walk-06-fix-drawer');
    await drawer.locator('form button[type="submit"]').click();
    await expect(drawer.locator('form button[type="submit"]')).toBeDisabled({ timeout: 15_000 });
    await page.keyboard.press('Escape');
    await page.getByTestId('bank-guide-primary').click();

    await expectStage(page, { stage: 'create', chip: 'no_file', chipText: 'No bank file yet', strip: 'create', primary: 'Create bank file' });
    await snap(page, 'walk-07-file-details');
    await page.getByTestId('bank-value-date').locator('input').click();
    await snap(page, 'walk-08-date-picker');
    await page.keyboard.press('Escape');
    await page.getByTestId('bank-guide-primary').click();

    await expectStage(page, { stage: 'send', chip: 'created', chipText: 'File created — not sent', strip: 'send', primary: 'Download bank file' });
    await snap(page, 'walk-09-send');
    const download = page.waitForEvent('download');
    await page.getByTestId('bank-guide-primary').click();
    await download;
    await expectStage(page, { stage: 'send', chip: 'downloaded', chipText: 'Downloaded — not yet submitted', strip: 'send', primary: 'Record as submitted' });
    await snap(page, 'walk-10-downloaded');
    await page.getByTestId('bank-guide-primary').click();
    await page.getByRole('dialog').getByLabel('Bank submission reference').fill('ANB-55821-0924');
    await snap(page, 'walk-11-record-submitted');
    await page.getByRole('dialog').getByRole('button', { name: 'Record as submitted' }).click();

    await expectStage(page, { stage: 'response', chip: 'submitted', chipText: 'Submitted — waiting for the bank (0 of 9)', strip: 'response', primary: 'Record the bank’s response' });
    await snap(page, 'walk-12-waiting');
  });
});

test.describe('mockup-matching screenshots', () => {
  test.skip(!SHOTS, 'BANK_GUIDED_SHOTS is not set');
  const B1 = { ...BATCH, paymentExclusions: [{ employeeId: 300, employeeCode: 'EVT2-0010', amount: 4150, reasonCode: 'paid_outside_bank_file', reason: 'Paid by cash' }], excludedTotal: 4150, totalAmount: 129438.9 };
  // The check answers for the same batch (its total is the batch's, after the one left out).
  const sameTotal: Handler = (path, _s, method) => (path === `${bp}/batches/${BATCH_ID}/validate` && method === 'POST'
    ? { canExport: true, errors: [], warnings: [], employeeCount: 9, totalAmount: 129438.9, currency: 'SAR', formatId: 'anb-connect-csv-v1' } : undefined);

  test('1: desktop EN, step 2', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await boot(page, { locale: 'en', url, routes: bankHandler({ batches: [B1] }), waitMs: 2500, stages: 'default' });
    await expect(page.getByTestId('bank-guide')).toHaveAttribute('data-stage', 'check', { timeout: 90_000 });
    await snap(page, 'mockup-1-step2-en-1440');
  });
  test('2: desktop AR, step 3 with the date picker', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await boot(page, { locale: 'ar', url, routes: bankHandler({ batches: [B1], extra: sameTotal }), waitMs: 2500, stages: 'default' });
    await expect(page.getByTestId('bank-guide-primary')).toBeEnabled({ timeout: 90_000 });
    await page.getByTestId('bank-guide-primary').click();
    await expect(page.getByTestId('bank-guide')).toHaveAttribute('data-stage', 'create');
    await page.getByTestId('bank-value-date').locator('input').click();
    await snap(page, 'mockup-2-step3-datepicker-ar-1440');
  });
  test('3: phone, setup gate', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await boot(page, { locale: 'en', url, routes: bankHandler({ batches: [B1], settings: EMPTY_SETUP }), waitMs: 2500, stages: 'default' });
    await expect(page.getByTestId('bank-guide')).toHaveAttribute('data-stage', 'setup', { timeout: 90_000 });
    await snap(page, 'mockup-3-setup-gate-en-390');
  });
  test('4: end state, waiting for the bank', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await boot(page, { locale: 'en', url, routes: bankHandler({ batches: [{ ...B1, ...SUBMITTED, totalAmount: 129438.9, paymentExclusions: B1.paymentExclusions, excludedTotal: 4150 }], existingExport: FILE, records: withStatus(6, 1) }), waitMs: 2500, stages: 'default' });
    await expect(page.getByTestId('bank-guide')).toHaveAttribute('data-stage', 'response', { timeout: 90_000 });
    await snap(page, 'mockup-4-waiting-en-1440');
  });
  for (const tab of ['runs', 'validation', 'approvals']) {
    for (const [locale, w, h] of [['en', 1440, 900], ['ar', 1440, 900], ['en', 390, 844]] as const) {
      test(`shell: ${tab} (${locale} ${w})`, async ({ page }) => {
        await page.setViewportSize({ width: w, height: h });
        await boot(page, { locale, url: `/payroll?tab=${tab}&run=${RUN_ID}`, routes: bankHandler({ batches: [B1] }), waitMs: 4000, stages: 'default' });
        await expect(page.getByTestId('payroll-section-panel')).toBeVisible({ timeout: 90_000 });
        await snap(page, `shell-${tab}-${locale}-${w}`);
      });
    }
  }
});

void HANDLED;
