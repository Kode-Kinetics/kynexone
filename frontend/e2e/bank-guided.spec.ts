import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { payrollUx } from '../src/i18n/payrollUx';
import { payrollPipeline } from '../src/i18n/payrollPipeline';
import { bankFileGuide } from '../src/i18n/bankFileGuide';
import { boot, HANDLED, type Handler, type Locale } from './demo-polish.fixture';
import { BATCH, BATCH_ID, RECORDS, RUN_ID, SETTINGS, bankHandler } from './bank-wps.fixture';

/**
 * Payroll → Bank / WPS Files as one guided flow. Every API request is intercepted: this proves the UI contract (which step the screen is in,
 * what it says to do, the one primary button, what comes next, EN and AR), not authorisation or the real payloads.
 *
 * For every place a batch can be, the header, the step list, the primary button and the status chip must all say the same thing; and the one
 * action for the step must be on screen on a laptop without scrolling.
 *
 * Run (dev server on its own port and dist dir):  DEMO_POLISH_PORT=5174 npx playwright test -c e2e/playwright.demo-polish.config.ts bank-guided
 * BANK_GUIDED_SHOTS=<dir> also saves the walk-through screenshots (desktop and phone, EN and AR).
 */

const LOCALES: Locale[] = ['en', 'ar'];
const url = `/payroll?tab=bank-wps&run=${RUN_ID}`;
const bp = '/api/payroll/bank-exports';

const say = (locale: Locale, key: string) => (locale === 'ar' ? payrollUx.ar[key] ?? bankFileGuide.ar[key] ?? payrollPipeline.ar[key] ?? `MISSING ${key}` : key);
const W: Record<Locale, Record<string, string>> = {
  en: { validate: 'Validate', generate: 'Generate files', download: 'Download zip', saveSetup: 'Save employer setup', createBatch: 'Create Payment Batch', tracking: 'Open Payment Tracking', creditDate: 'Credit value date', change: 'Change', edit: 'Edit', fix: 'Fix', employee: 'Employee (from this batch)' },
  ar: { validate: 'تحقق', generate: 'إنشاء الملفات', download: 'تنزيل الملف المضغوط', saveSetup: 'حفظ إعدادات صاحب العمل', createBatch: 'إنشاء دفعة دفع', tracking: 'فتح تتبع المدفوعات', creditDate: 'تاريخ قيمة الإيداع', change: 'تغيير', edit: 'تعديل', fix: 'معالجة', employee: 'الموظف (من هذه الدفعة)' },
};

const step = (page: Page, key: string) => page.locator(`[data-testid="bank-file-steps"] [data-step="${key}"]`);
const guide = (page: Page) => ({
  root: page.getByTestId('bank-guide'),
  position: page.getByTestId('bank-guide-position'),
  sentence: page.getByTestId('bank-guide-sentence'),
  next: page.getByTestId('bank-guide-next'),
  primary: page.getByTestId('bank-guide-primary'),
  chip: page.getByTestId('bank-guide-chip'),
});

/** Header, step list, primary button and chip agree on one stage, and only that step is open. */
async function expectStage(page: Page, locale: Locale, want: { stage: string; position?: number; name?: string; primary?: string | null; chip: string; state?: string }) {
  const g = guide(page);
  await expect(g.root).toHaveAttribute('data-stage', want.stage, { timeout: 90_000 });
  // The list has exactly one current step, and it is the one the header names.
  if (want.stage !== 'finished') {
    const current = page.locator('[data-testid="bank-file-steps"] [aria-current="step"]');
    await expect(current).toHaveCount(1);
    await expect(current).toHaveAttribute('data-step', want.stage);
    if (want.state) await expect(current).toHaveAttribute('data-state', want.state);
  } else {
    await expect(page.locator('[data-testid="bank-file-steps"] [aria-current="step"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="bank-file-steps"] [data-state="done"]')).toHaveCount(4);
  }
  if (want.position !== undefined && want.name) {
    const text = locale === 'ar' ? `الخطوة ${want.position} من 4 · ${say(locale, want.name)}` : `Step ${want.position} of 4 · ${want.name}`;
    await expect(g.position).toHaveText(text);
  }
  await expect(g.chip).toHaveAttribute('data-chip', want.chip);
  // The track names the same stage (at the end every segment is done).
  if (want.stage === 'finished') await expect(page.locator('[data-testid="bank-guide-progress"] [data-state="done"]')).toHaveCount(4);
  else await expect(page.locator(`[data-testid="bank-guide-progress"] [data-track="${want.stage}"]`)).not.toHaveAttribute('data-state', 'upcoming');
  // One primary button (or none), the right one, and nothing else in the guide dressed as primary.
  if (want.primary === null) await expect(g.primary).toHaveCount(0);
  else if (want.primary !== undefined) await expect(g.primary).toHaveText(want.primary);
  await expect(g.root.locator('.btn-primary')).toHaveCount(want.primary === null ? 0 : 1);
  // What comes next is always said (except at the very end, which has its one action).
  if (want.stage === 'finished') await expect(g.next).toHaveCount(0); else await expect(g.next).toBeVisible();
  // Only the current step is open (the finished steps are one line each, the ones ahead say what they wait for).
  const bodies = page.locator('[data-testid^="bank-step-body-"]');
  if (want.stage === 'finished') await expect(bodies).toHaveCount(0); else await expect(bodies).toHaveCount(1);
  if (want.stage !== 'finished') await expect(step(page, want.stage).locator('[data-testid^="bank-step-body-"]')).toHaveCount(1);
}

async function pickDate(page: Page, locale: Locale) {
  const group = page.getByRole('group', { name: W[locale].creditDate });
  const selects = group.getByRole('combobox');
  await expect(selects).toHaveCount(3);
  for (let i = 0; i < 3; i++) {
    const values = await selects.nth(i).locator('option').evaluateAll(els => els.map(e => (e as HTMLOptionElement).value));
    await selects.nth(i).selectOption(values.includes('2026') ? '2026' : values.includes('31') ? '30' : '11');
  }
}

const EMPTY_SETUP = { formatId: '', molEstablishmentId: '', mainAccountNumber: '', organizationName: '', organizationAddress1: '', organizationAddress2: '', organizationAddress3: '', companyName: '', narrative: '', batchType: '', autoWpsUpload: false, nationalUnifiedNo: '', debitAccountIban: '' };
const FILE = { id: '44444444-4444-4444-8444-444444444444', formatId: 'anb-connect-csv-v1', files: [{ name: 'header.csv', sha256: 'a'.repeat(64) }, { name: 'body.csv', sha256: 'b'.repeat(64) }], batchReference: '202611220353', paymentDate: '2026-11-30', employeeCount: 9, totalAmount: 133588.9 };
const SUBMITTED = { ...BATCH, status: 'FileGenerated', wpsStatus: 'Submitted', effectiveWpsStatus: 'Submitted', wpsStatusLabel: 'Submitted' };

for (const locale of LOCALES) {
  test.describe(`bank file guide: every stage says one thing (${locale})`, () => {
    test.use({ viewport: { width: 1366, height: 768 } });

    test('no batch on a locked run: step 1, create it', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ batches: [] }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'batch', position: 1, name: 'Choose the payment batch', primary: W[locale].createBatch, chip: 'waiting', state: 'current' });
      await expect(guide(page).primary).toBeEnabled();
    });

    test('no batch on a run that is not locked: step 1 is blocked and the button is off, with the reason beside it', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ batches: [], runStatus: 'Approved' }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'batch', position: 1, name: 'Choose the payment batch', primary: W[locale].createBatch, chip: 'attention', state: 'blocked' });
      await expect(guide(page).primary).toBeDisabled();
      await expect(guide(page).sentence).toContainText(locale === 'ar' ? /قفل/ : /Locked/);
    });

    test('two batches and none chosen: step 1, choose one from the list; no button until one is', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ batches: [BATCH, { ...BATCH, id: 'b2', batchNumber: 'PAY-202611-220354' }] }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'batch', position: 1, name: 'Choose the payment batch', primary: null, chip: 'waiting', state: 'current' });
      await expect(page.getByRole('button', { name: /PAY-202611-220354/ })).toBeVisible();
      await page.getByRole('button', { name: /PAY-202611-220354/ }).click();
      await expect(step(page, 'batch')).toHaveAttribute('data-state', 'done');
    });

    test('bank setup incomplete: the setup is the stage (before step 2), its form is open, Save is the one button, everything after waits', async ({ page }) => {
      const posts: string[] = [];
      await boot(page, { locale, url, routes: bankHandler({ settings: EMPTY_SETUP, posts }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'setup', primary: W[locale].saveSetup, chip: 'setup', state: 'current' });
      await expect(guide(page).position).toHaveText(`${say(locale, 'Before you start')} · ${say(locale, 'Bank setup')}`);
      await expect(page.getByTestId('bank-setup-summary')).toHaveCount(0);
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'upcoming');
      await expect(step(page, 'check').getByTestId('bank-step-reason-check')).toContainText(say(locale, 'Waits for the bank setup.'));
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'upcoming');
      // The form has no button of its own: the header's is the only one, and it submits the form.
      await expect(step(page, 'setup').locator('form button[type="submit"]')).toHaveCount(0);
      await expect(guide(page).primary).toHaveAttribute('type', 'submit');
      // Saving an empty setup is refused in place and nothing is sent.
      await guide(page).primary.click();
      await expect(step(page, 'setup').locator('[aria-invalid="true"]').first()).toBeVisible();
      expect(posts.filter(p => p.startsWith('PUT'))).toEqual([]);
    });

    test('step 2: a clean batch is checked from the header; the date is one labelled field with Day, Month and Year placeholders', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler(), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'check', position: 2, name: 'Check the batch', primary: W[locale].validate, chip: 'waiting', state: 'current' });
      await expect(step(page, 'batch')).toHaveAttribute('data-state', 'done');
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'upcoming');
      await expect(step(page, 'download')).toHaveAttribute('data-state', 'upcoming');
      // The date: a visible label, a group with the same name, three selects that name themselves while empty.
      const group = page.getByRole('group', { name: W[locale].creditDate });
      await expect(step(page, 'check').getByText(W[locale].creditDate, { exact: true }).first()).toBeVisible();
      for (const [i, word] of [[0, 'Day'], [1, 'Month'], [2, 'Year']] as const) {
        const select = group.getByRole('combobox').nth(i);
        await expect(select).toHaveValue('');
        await expect(select.locator('option').first()).toHaveText(locale === 'ar' ? { Day: 'اليوم', Month: 'الشهر', Year: 'السنة' }[word] : word);
      }
      // The footer: the bank setup in one line, the notice in one line.
      await expect(page.getByTestId('bank-setup-summary')).toContainText('••0026');
      await expect(page.getByTestId('bank-setup-summary').getByRole('button', { name: W[locale].edit })).toBeVisible();
      await expect(page.getByTestId('bank-pays-nobody')).toBeVisible();
      await expect(page.getByTestId('bank-notice-details')).toHaveCount(0);
    });

    test('step 2 → 3 → 4: each clean step hands over to the next, which takes focus, with a brief confirmation', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler(), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'check', position: 2, name: 'Check the batch', primary: W[locale].validate, chip: 'waiting' });
      await pickDate(page, locale);
      await guide(page).primary.click();
      await expectStage(page, locale, { stage: 'create', position: 3, name: 'Create the bank file', primary: W[locale].generate, chip: 'waiting', state: 'current' });
      await expect(page.getByTestId('bank-guide-confirmation')).toContainText(say(locale, 'Batch checked — no blockers.'));
      await expect(step(page, 'check')).toContainText(locale === 'ar' ? 'نجح التحقق' : 'Passed');
      await expect(step(page, 'create').locator('[data-step-heading]')).toBeFocused();
      await expect(page.getByTestId('bank-create-recap')).toContainText('2026');
      await guide(page).primary.click();
      await expectStage(page, locale, { stage: 'download', position: 4, name: 'Download and mark submitted', primary: W[locale].download, chip: 'ready', state: 'current' });
      await expect(page.getByTestId('bank-guide-confirmation')).toContainText(say(locale, 'Bank file created.'));
      await expect(step(page, 'download').locator('[data-step-heading]')).toBeFocused();
      await expect(step(page, 'download').getByRole('button', { name: W[locale].tracking })).toBeVisible();
      await expect(page.locator('#bank-wps-generated-export')).toContainText('header.csv');
    });

    test('a blocker has a Fix that opens the beneficiary editor on THAT employee; a clean check after it carries on', async ({ page }) => {
      let fixed = false;
      const extra: Handler = (path, _s, method) => {
        if (path === `${bp}/batches/${BATCH_ID}/validate` && method === 'POST') {
          return fixed ? { canExport: true, errors: [], warnings: [], employeeCount: 9, totalAmount: 133588.9, currency: 'SAR', formatId: 'anb-connect-csv-v1' }
            : { canExport: false, errors: [{ code: 'bic_missing', message: 'BIC is missing for this employee.', employeeId: 203 }], warnings: [], employeeCount: 9, totalAmount: 133588.9, currency: 'SAR', formatId: 'anb-connect-csv-v1' };
        }
        if (path === '/api/employees/203' && method === 'GET') return { id: 203, employeeCode: 'EVT2-0004', fullName: 'Nora Alharbi', wpsBankDetails: '' };
        if (path === '/api/employees/203' && method === 'PUT') { fixed = true; return { id: 203 }; }
        return undefined;
      };
      await boot(page, { locale, url, routes: bankHandler({ extra }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'check', position: 2, name: 'Check the batch', primary: W[locale].validate, chip: 'waiting' });
      await pickDate(page, locale);
      await guide(page).primary.click();
      await expectStage(page, locale, { stage: 'check', position: 2, name: 'Check the batch', primary: W[locale].validate, chip: 'attention', state: 'blocked' });
      await expect(guide(page).sentence).toHaveText(say(locale, 'The check found blockers. Fix each one below, then check again.'));
      await expect(guide(page).next).toHaveText(say(locale, 'Next: check again, then create the bank file.'));
      const blockers = page.getByTestId('bank-blockers');
      await expect(blockers).toContainText('BIC is missing for this employee.');
      await blockers.getByTestId('bank-blocker-fix').click();
      const drawer = page.getByRole('dialog');
      await expect(drawer).toBeVisible();
      await expect(drawer.getByLabel(W[locale].employee)).toHaveValue('203', { timeout: 15_000 });
      await drawer.locator('input[dir="ltr"]').nth(0).fill('ARNBSARI');
      await drawer.locator('input[dir="ltr"]').nth(1).fill('Riyadh');
      await drawer.locator('input[dir="ltr"]').nth(2).fill('Olaya');
      await drawer.locator('input[dir="ltr"]').nth(3).fill('Building 10');
      await drawer.locator('form button[type="submit"]').click();
      await expect(drawer.locator('form button[type="submit"]')).toBeDisabled({ timeout: 15_000 });
      await page.keyboard.press('Escape');
      await expect(drawer).toHaveCount(0);
      // The change discarded the old result: the check is to be run again, and then the stage moves on.
      await guide(page).primary.click();
      await expectStage(page, locale, { stage: 'create', position: 3, name: 'Create the bank file', primary: W[locale].generate, chip: 'waiting', state: 'current' });
    });

    test('a blocker on an account number is fixed in People, not in a drawer that cannot change it', async ({ page }) => {
      const validation = { canExport: false, errors: [{ code: 'account_missing', message: 'No account number.', employeeId: 204 }] };
      await boot(page, { locale, url, routes: bankHandler({ validation }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'check', position: 2, name: 'Check the batch', primary: W[locale].validate, chip: 'waiting' });
      await pickDate(page, locale);
      await guide(page).primary.click();
      const fix = page.getByTestId('bank-blockers').getByTestId('bank-blocker-fix');
      await expect(fix).toHaveAttribute('href', '/people/204');
    });

    test('the secondary links: bank formats and any employee\'s bank details open in drawers', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler(), waitMs: 2500 });
      await expect(guide(page).root).toBeVisible({ timeout: 90_000 });
      await page.getByRole('button', { name: locale === 'ar' ? 'صيغ ملفات البنك' : 'Bank formats' }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await page.keyboard.press('Escape');
      await page.getByRole('button', { name: locale === 'ar' ? 'تعديل بيانات موظف البنكية' : 'Edit an employee’s bank details' }).click();
      await expect(page.getByRole('dialog').getByLabel(W[locale].employee)).toHaveValue('');
      await page.keyboard.press('Escape');
      await expect(page.getByRole('dialog')).toHaveCount(0);
      // The templates are no longer a section of the page, and neither are the beneficiary details.
      await expect(page.getByRole('heading', { name: /Custom bank salary-file templates|قوالب ملفات الرواتب/ })).toHaveCount(0);
    });

    test('Edit on the setup row opens it in a drawer and a clean save closes it', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ extra: (path, _s, method, route) => (method === 'PUT' && path === `${bp}/companies/c1/settings` ? JSON.parse(route.request().postData() ?? '{}') : undefined) }), waitMs: 2500 });
      await expect(page.getByTestId('bank-setup-summary')).toBeVisible({ timeout: 90_000 });
      await page.getByTestId('bank-setup-summary').getByRole('button', { name: W[locale].edit }).click();
      const drawer = page.getByRole('dialog');
      await expect(drawer.locator('input[name="molEstablishmentId"]')).toHaveValue(SETTINGS.molEstablishmentId);
      await drawer.locator('input[name="narrative"]').fill('Edited narrative');
      await drawer.locator('form button[type="submit"]').click();
      await expect(drawer).toHaveCount(0, { timeout: 15_000 });
      await expect(page.getByTestId('bank-guide-live')).toContainText(locale === 'ar' ? 'تم حفظ إعدادات صاحب العمل' : 'Employer setup saved');
    });

    test('step 4: the file exists; download is the one button, Payment Tracking is next, and the file is shown', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ existingExport: FILE }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'download', position: 4, name: 'Download and mark submitted', primary: W[locale].download, chip: 'ready', state: 'current' });
      await expect(guide(page).primary).toBeEnabled();
      await expect(step(page, 'check')).toHaveAttribute('data-state', 'done');
      await expect(step(page, 'create')).toHaveAttribute('data-state', 'done');
      await expect(step(page, 'create')).toContainText('202611220353');
      await expect(guide(page).next).toHaveText(say(locale, 'Next: record the batch as submitted in Payment Tracking.'));
    });

    test('the end: submitted, nothing else to do here; waiting for the bank, with Payment Tracking as the one action', async ({ page }) => {
      const batch = { ...SUBMITTED, bankFileAvailable: true, bankFileGenerated: true, unconfirmedPayments: { count: 3, total: 3000 }, confirmationCoverage: 0.4 };
      await boot(page, { locale, url, routes: bankHandler({ batches: [batch], existingExport: FILE }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'finished', primary: W[locale].tracking, chip: 'submitted' });
      await expect(guide(page).sentence).toContainText(locale === 'ar' ? 'أُرسل ملف البنك — بانتظار تأكيد البنك (2 من 5)' : 'Bank file submitted — waiting for the bank’s confirmation (2 of 5)');
      await guide(page).primary.click();
      await expect(page.locator('#payroll-tab-payment-tracking')).toHaveAttribute('aria-selected', 'true');
    });

    test('a closed gate: step 2 is blocked with its reason and no button (Retry lives with the reason)', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ availability: { enabled: false } }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'check', position: 2, name: 'Check the batch', primary: null, chip: 'attention', state: 'blocked' });
      await expect(guide(page).sentence).toHaveText(say(locale, 'Bank file export is switched off for this company. Ask the platform team.'));
    });
  });
}

// ── The page tells one story ──────────────────────────────────────────────────────────────────────

for (const locale of LOCALES) {
  test.describe(`bank file guide: the page's run list, its next-step card and this tab agree (${locale})`, () => {
    test.use({ viewport: { width: 1366, height: 768 } });

    // The owner's case: the internal payroll register was generated (the batch says Generated) but the bank file was not made.
    const REGISTER_ONLY = { ...BATCH, status: 'Generated', wpsStatus: 'Generated', effectiveWpsStatus: 'Generated', wpsStatusLabel: 'Generated (file ready for the bank)', bankFileAvailable: true, bankFileGenerated: false };

    test('a generated internal register is not a bank file: no tab, card or list says it is', async ({ page }) => {
      await boot(page, { locale, url, routes: bankHandler({ batches: [REGISTER_ONLY] }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'check', position: 2, name: 'Check the batch', primary: W[locale].validate, chip: 'waiting' });
      // The page's run list says what the chip says; the next-step card names the stage this tab is in.
      await expect(page.locator('#pipeline-run-picker option:checked')).toContainText(say(locale, 'Waiting for the bank file'));
      await expect(page.getByTestId('pipeline-next-title')).toHaveText(say(locale, 'Create the bank file'));
      await expect(guide(page).chip).toHaveText(say(locale, 'Waiting for the bank file'));
      // The old status line ("bank file generated") is gone for good.
      await expect(page.getByTestId('bank-batch-status')).toHaveCount(0);
      await expect(page.locator('main')).not.toContainText(locale === 'ar' ? 'تم إنشاء ملف البنك' : 'bank file generated');
    });

    test('making the bank file here updates the page\'s run list and card without a reload', async ({ page }) => {
      let made = false;
      const extra: Handler = (path, _s, method) => {
        if (path === `${bp}/batches/${BATCH_ID}/generate` && method === 'POST') made = true;
        if (path === '/api/payroll/payment-batches' && method === 'GET') return [{ ...REGISTER_ONLY, bankFileGenerated: made }];
        return undefined;
      };
      await boot(page, { locale, url, routes: bankHandler({ extra }), waitMs: 2500 });
      await expectStage(page, locale, { stage: 'check', position: 2, name: 'Check the batch', primary: W[locale].validate, chip: 'waiting' });
      await expect(page.locator('#pipeline-run-picker option:checked')).toContainText(say(locale, 'Waiting for the bank file'));
      await pickDate(page, locale);
      await guide(page).primary.click();
      await expectStage(page, locale, { stage: 'create', position: 3, name: 'Create the bank file', primary: W[locale].generate, chip: 'waiting' });
      await guide(page).primary.click();
      await expectStage(page, locale, { stage: 'download', position: 4, name: 'Download and mark submitted', primary: W[locale].download, chip: 'ready' });
      await expect(page.locator('#pipeline-run-picker option:checked')).not.toContainText(say(locale, 'Waiting for the bank file'), { timeout: 15_000 });
    });
  });
}

// ── The walk-through: every step as a payroll officer who has never seen the screen meets it ──────────

const SHOTS = process.env.BANK_GUIDED_SHOTS;
for (const locale of LOCALES) {
  for (const [form, viewport] of [['desktop', { width: 1366, height: 768 }], ['phone', { width: 390, height: 844 }]] as const) {
    test.describe(`walk-through (${locale}, ${form})`, () => {
      test.skip(!SHOTS, 'BANK_GUIDED_SHOTS is not set');
      test.use({ viewport });

      const snap = async (page: Page, name: string) => {
        fs.mkdirSync(SHOTS!, { recursive: true });
        await page.waitForTimeout(700);
        await page.screenshot({ path: `${SHOTS}/${name}-${locale}-${form}.png` });
        await page.screenshot({ path: `${SHOTS}/${name}-${locale}-${form}-full.png`, fullPage: true });
      };

      test('setup → choose → check → fix → create → download → submitted', async ({ page }) => {
        let setup: Record<string, unknown> = EMPTY_SETUP;
        let fixed = false;
        let batches: unknown[] = [BATCH, { ...BATCH, id: 'b2', batchNumber: 'PAY-202611-220354' }];
        let exported: unknown = null;
        const extra: Handler = (path, _s, method, route) => {
          if (path === `${bp}/companies/c1/settings` && method === 'PUT') { setup = JSON.parse(route.request().postData() ?? '{}'); return setup; }
          if (path === `${bp}/companies/c1/settings`) return setup;
          if (path === '/api/payroll/payment-batches') return batches;
          if (path === `${bp}/batches/${BATCH_ID}/context`) return { companyId: 'c1', companyName: 'Acme Arabia', countryCode: 'SA', runStatus: 'Locked', existingExport: exported };
          if (path === `${bp}/batches/${BATCH_ID}/validate` && method === 'POST') {
            return fixed ? { canExport: true, errors: [], warnings: [], employeeCount: 9, totalAmount: 133588.9, currency: 'SAR', formatId: 'anb-connect-csv-v1' }
              : { canExport: false, errors: [{ code: 'bic_missing', message: 'BIC is missing for this employee.', employeeId: 203 }], warnings: [], employeeCount: 9, totalAmount: 133588.9, currency: 'SAR', formatId: 'anb-connect-csv-v1' };
          }
          if (path === `${bp}/batches/${BATCH_ID}/generate` && method === 'POST') { exported = FILE; return FILE; }
          if (path === `${bp}/batches/${BATCH_ID}/download`) { void route.fulfill({ status: 200, headers: { 'content-type': 'application/zip', 'content-disposition': 'attachment; filename="bank-file.zip"' }, body: 'PK' }); return HANDLED; }
          if (path === '/api/employees/203' && method === 'GET') return { id: 203, employeeCode: 'EVT2-0004', fullName: 'Nora Alharbi', wpsBankDetails: '' };
          if (path === '/api/employees/203' && method === 'PUT') { fixed = true; return { id: 203 }; }
          return undefined;
        };
        await boot(page, { locale, url, routes: bankHandler({ extra }), waitMs: 2500 });

        // 0. two batches and none chosen
        await expectStage(page, locale, { stage: 'batch', position: 1, name: 'Choose the payment batch', primary: null, chip: 'waiting' });
        await snap(page, '00-choose-batch');
        await page.getByRole('button', { name: /PAY-202611-220353/ }).click();

        // 1. setup incomplete
        await expectStage(page, locale, { stage: 'setup', primary: W[locale].saveSetup, chip: 'setup' });
        await snap(page, '01-setup-incomplete');
        const inline = step(page, 'setup');
        await inline.locator('input[name="molEstablishmentId"]').fill('7-1234567');
        await inline.locator('input[name="mainAccountNumber"]').fill('0108061198800026');
        await inline.locator('input[name="organizationName"]').fill('Acme Arabia');
        await inline.locator('input[name="companyName"]').fill('Acme Arabia LLC');
        await inline.locator('input[name="organizationAddress1"]').fill('Riyadh');
        await inline.locator('input[name="organizationAddress2"]').fill('Olaya');
        await inline.locator('input[name="organizationAddress3"]').fill('Building 10');
        await inline.locator('input[name="narrative"]').fill('November payroll');
        await inline.locator('select').filter({ has: page.locator('option[value="PAYROLL"]') }).selectOption('PAYROLL');
        await guide(page).primary.click();

        // 2. check (the setup's hand-over)
        await expectStage(page, locale, { stage: 'check', position: 2, name: 'Check the batch', primary: W[locale].validate, chip: 'waiting' });
        await expect(page.getByTestId('bank-guide-confirmation')).toContainText(say(locale, 'Bank setup saved.'));
        await snap(page, '02-step2-check-after-setup');
        await pickDate(page, locale);
        await snap(page, '03-step2-check-date-chosen');
        await guide(page).primary.click();

        // 3. a blocker, and its Fix
        await expectStage(page, locale, { stage: 'check', position: 2, name: 'Check the batch', primary: W[locale].validate, chip: 'attention', state: 'blocked' });
        await snap(page, '04-step2-blocked');
        await page.getByTestId('bank-blocker-fix').click();
        const drawer = page.getByRole('dialog');
        await expect(drawer.getByLabel(W[locale].employee)).toHaveValue('203', { timeout: 15_000 });
        await snap(page, '05-fix-drawer');
        await drawer.locator('input[dir="ltr"]').nth(0).fill('ARNBSARI');
        await drawer.locator('input[dir="ltr"]').nth(1).fill('Riyadh');
        await drawer.locator('input[dir="ltr"]').nth(2).fill('Olaya');
        await drawer.locator('input[dir="ltr"]').nth(3).fill('Building 10');
        await drawer.locator('form button[type="submit"]').click();
        await expect(drawer.locator('form button[type="submit"]')).toBeDisabled({ timeout: 15_000 });
        await page.keyboard.press('Escape');
        await guide(page).primary.click();

        // 4. create (the check's hand-over): the screenshot (c)
        await expectStage(page, locale, { stage: 'create', position: 3, name: 'Create the bank file', primary: W[locale].generate, chip: 'waiting' });
        await expect(page.getByTestId('bank-guide-confirmation')).toContainText(say(locale, 'Batch checked — no blockers.'));
        await snap(page, '06-handover-step3-create');
        await guide(page).primary.click();

        // 5. download (the file's hand-over)
        await expectStage(page, locale, { stage: 'download', position: 4, name: 'Download and mark submitted', primary: W[locale].download, chip: 'ready' });
        await snap(page, '07-step4-download');
        const download = page.waitForEvent('download');
        await guide(page).primary.click();
        await download;

        // 6. submitted: the end state (d)
        batches = [{ ...SUBMITTED, bankFileAvailable: true, bankFileGenerated: true, unconfirmedPayments: { count: 3, total: 3000 }, confirmationCoverage: 0.4 }];
        await page.reload();
        await expectStage(page, locale, { stage: 'finished', primary: W[locale].tracking, chip: 'submitted' });
        await snap(page, '08-end-submitted');
      });
    });
  }
}
