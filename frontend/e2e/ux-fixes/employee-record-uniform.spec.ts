import fs from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import { bootEmployeeWorkspaceFixture, employees } from '../employee-workspace.fixture';

/**
 * The employee record workspace speaks one visual language (Screen Standard): read-only fields are a label above a value
 * (no box), in a 1/2/3 column grid; every section card has the same header; dates, money and event names are formatted, never
 * raw. Measured at 1366x768 in English and Arabic. Every API call is intercepted; nothing is read or written.
 *
 *   Employment   at least 9 field values above y=768 (the tall input-style boxes showed 8). CI renders a wider fallback font, so 8 there.
 *   Activity     at least 8 events in view (one huge card per event showed 4); no raw system name, no raw timestamp.
 *   Every tab    no ISO string, no PascalCase event name, no sideways scroll, every field value inside its own cell.
 *   Pay          the sub-tabs are a slim strip, not a card of their own; the last row is never under the assistant button.
 *   Access       the three status blocks sit in one row from 1280px; Work email is a compact row.
 *   Overview     nothing to do: one card with one line, not two mostly empty cards.
 *
 * ERUI_SHOTS_DIR=<dir> (and ERUI_PHASE=before|after) also saves a screenshot per tab and language.
 */
const SHOTS = process.env.ERUI_SHOTS_DIR;
const PHASE = process.env.ERUI_PHASE ?? 'run';
const SLACK = process.env.CI ? 1 : 0; // CI draws a wider fallback font than a Mac: a row or two fewer in the same height.
const LOCALES = ['en', 'ar'] as const;
type Locale = (typeof LOCALES)[number];
const TABS = ['overview', 'personal', 'employment', 'pay-benefits', 'compliance-documents', 'access-onboarding', 'activity'] as const;
type Tab = (typeof TABS)[number];
const ISO = /\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/;
const PERMISSIONS = ['employees.read', 'employees.write', 'employees.sensitive', 'employees.documents', 'employees.approve', 'employees.access.issue', 'employees.access.reset', 'dashboard.read', 'ai.query'];

const KINDS = [
  { eventType: 'SensitiveChangeApproved', fieldName: 'bankName,accountNumber,iban', oldValue: '', newValue: '' },
  { eventType: 'StatusChanged', fieldName: 'status', oldValue: 'Draft', newValue: 'Active' },
  { eventType: 'SensitiveChangeApproved', fieldName: 'iqamaNumber,idNumber', oldValue: '', newValue: '' },
  { eventType: 'FieldChanged', fieldName: 'wpsBankDetails', oldValue: '', newValue: '' },
];
const HISTORY = Array.from({ length: 16 }, (_, i) => ({
  id: `h${i}`, ...KINDS[i % KINDS.length], effectiveDate: '2026-11-13T00:00:00Z', reason: 'A sensitive change was approved by a second HR reviewer and applied to the record.',
  createdAtUtc: `2026-11-13T22:${String(10 + i).padStart(2, '0')}:21.307121Z`,
}));
const DOCUMENTS = ['Passport', 'Contract', 'Offer letter'].map((documentType, i) => ({
  id: `doc${i}`, documentType, fileName: `${documentType.toLowerCase().replace(' ', '-')}.pdf`, approvalStatus: 'Approved', scanStatus: 'Clean', isProtected: false, isRequired: false, expiryDate: '2027-01-01T00:00:00Z',
}));
const RICH = {
  ...employees[901],
  joiningDate: '2026-10-31T00:00:00Z',
  salary: 18500,
  payrollProfileCode: 'KSA-MONTHLY',
  bankName: 'Al Rajhi Bank',
  payrollProfile: { bankName: 'Al Rajhi Bank', iban: employees[901].bankIban, salaryCurrency: 'SAR', payrollGroup: 'Monthly staff' },
  history: HISTORY,
  documents: DOCUMENTS,
};

async function boot(page: Page, locale: Locale, readinessEmpty = false) {
  const control = await bootEmployeeWorkspaceFixture(page, { locale, permissions: PERMISSIONS, disabledFeatures: ['release_a'] });
  // Registered after the fixture's own route, so it answers first: employee 901 with a long history, a real salary and documents.
  await page.route('**/api/employees/901', route => route.request().method() === 'GET' ? route.fulfill({ json: RICH }) : route.fallback());
  if (readinessEmpty) control.readinessBody.set(901, { employeeId: 901, state: 'Ready', score: 100, progress: { present: 5, requiredTotal: 5 }, policy: { countryCode: 'SAU', tier: 'Activation', sources: [] }, blocking: [], payBlocking: [], recommended: [], present: [], expiringSoon: [], disclaimer: '' });
  return control;
}

async function open(page: Page, tab: Tab) {
  await page.goto(`/people/901?section=${tab}`, { timeout: 90_000 });
  await expect(page.locator('#employee-workspace-heading')).toBeVisible({ timeout: 90_000 });
  await expect(page.getByRole('navigation', { name: /Employee record sections|أقسام سجل الموظف/ })).toBeVisible();
  // Each tab's own content: wait until it has real text beyond the shell.
  await expect(page.locator('[data-employee-workspace-scroll]')).toContainText(/\S{3}/, { timeout: 30_000 });
  await page.waitForTimeout(600);
}

async function shot(page: Page, tab: Tab, locale: Locale) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: `${SHOTS}/${PHASE}-${tab}-${locale}.png` });
}

/** How many of the elements are fully inside the first screenful. */
const inView = (page: Page, selector: string) => page.locator(selector).evaluateAll(els => els.filter((el) => {
  const r = el.getBoundingClientRect();
  return r.height > 0 && r.top >= 0 && r.bottom <= window.innerHeight;
}).length);

for (const locale of LOCALES) {
  test.describe(`employee record uniform (${locale} 1366x768)`, () => {
    test.use({ viewport: { width: 1366, height: 768 } });

    test('every tab: no raw ISO string, no PascalCase event name, no sideways scroll, every value inside its cell', async ({ page }) => {
      await boot(page, locale);
      for (const tab of TABS) {
        await open(page, tab);
        const rawTimestamps = await page.evaluate((source) => {
          const re = new RegExp(source);
          const found: string[] = [];
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            const text = node.nodeValue ?? '';
            if (re.test(text) && (node.parentElement?.offsetParent ?? null) !== null) found.push(text.trim());
          }
          return found;
        }, ISO.source);
        expect(rawTimestamps, `${tab}: raw timestamp on screen`).toEqual([]);
        expect(await page.locator('main').innerText(), `${tab}: PascalCase event name`).not.toMatch(/SensitiveChange|StatusChanged|FieldChanged/);
        const sideways = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        expect(sideways, `${tab}: page scrolls sideways`).toBeLessThanOrEqual(1);
        const overflowing = await page.locator('[data-employee-workspace-scroll] dd').evaluateAll(els => els
          .filter(el => el.scrollWidth > el.clientWidth + 1 && getComputedStyle(el).overflowX !== 'auto')
          .map(el => (el.textContent ?? '').slice(0, 50)));
        expect(overflowing, `${tab}: a value is wider than its cell`).toEqual([]);
        await shot(page, tab, locale);
      }
    });

    test('Employment: the joining date is a date and at least nine field values are in the first screenful', async ({ page }) => {
      await boot(page, locale);
      await open(page, 'employment');
      const main = page.locator('[data-employee-workspace-scroll]');
      if (locale === 'en') await expect(main).toContainText('31 Oct 2026');
      await expect(main).not.toContainText('T00:00:00');
      const values = await inView(page, '[data-employee-workspace-scroll] dd');
      expect(values, 'field values in view').toBeGreaterThanOrEqual(9 - SLACK);
      // A read-only field is a label above a value, not an input-style box.
      const boxed = await page.locator('[data-employee-workspace-scroll] dd span.block.rounded-lg').count();
      expect(boxed, 'boxed read-only values').toBe(0);
      // Section headings share one size: the card title and the callout title do not differ by more than the standard step.
      const sizes = await page.locator('[data-employee-workspace-scroll] h2, [data-employee-workspace-scroll] h3').evaluateAll(els => els.map(el => parseFloat(getComputedStyle(el).fontSize)));
      expect(Math.max(...sizes), `heading sizes ${sizes.join(',')}`).toBeLessThanOrEqual(16);
    });

    test('Activity: at least eight events in the first screenful, one line each, in words and formatted dates', async ({ page }) => {
      await boot(page, locale);
      await open(page, 'activity');
      const events = await inView(page, '[data-employee-workspace-scroll] ol > li');
      expect(events, 'events in view').toBeGreaterThanOrEqual(8 - SLACK);
      const text = await page.locator('[data-employee-workspace-scroll] ol').innerText();
      expect(text).not.toMatch(/[a-z][A-Z]/); // no camelCase or PascalCase name
      expect(text).not.toContain('→ —');
      expect(text).not.toMatch(/— → —/);
      if (locale === 'en') {
        expect(text).toContain('Sensitive change approved');
        expect(text).toContain('Bank name, account number, IBAN');
        expect(text).toContain('Details withheld');
        expect(text).toMatch(/\d{1,2} Nov 2026/);
      }
    });

    test('Pay & Benefits: the sub-tabs are a slim strip, and the last row is clear of the assistant button', async ({ page }) => {
      await boot(page, locale);
      await open(page, 'pay-benefits');
      const strip = page.getByRole('navigation', { name: /Pay & Benefits sections|أقسام الرواتب/ });
      await expect(strip).toBeVisible();
      expect(await strip.evaluate(el => !!el.closest('.surface')), 'the strip sits in a card of its own').toBe(false);
      // Scroll everything as far as it goes, then no part of the last row may be under the floating assistant.
      await page.evaluate(() => {
        for (const el of Array.from(document.querySelectorAll<HTMLElement>('*'))) if (el.scrollHeight > el.clientHeight && /auto|scroll/.test(getComputedStyle(el).overflowY)) el.scrollTop = el.scrollHeight;
        window.scrollTo(0, document.documentElement.scrollHeight);
      });
      await page.waitForTimeout(300);
      const launcher = page.getByRole('button', { name: /Kody/ }).first();
      await expect(launcher).toBeVisible();
      const clash = await page.evaluate(() => {
        const l = Array.from(document.querySelectorAll('button')).find(b => /Kody/.test(b.getAttribute('aria-label') ?? ''))!.getBoundingClientRect();
        const root = document.querySelector('[data-employee-workspace-scroll]')!;
        const leaves = Array.from(root.querySelectorAll('*')).filter(e => e.children.length === 0 && e.getBoundingClientRect().height > 0 && (e.textContent ?? '').trim() !== '');
        const last = leaves[leaves.length - 1].getBoundingClientRect();
        return !(last.right < l.left || last.left > l.right || last.bottom < l.top || last.top > l.bottom);
      });
      expect(clash, 'the last row is under the assistant button').toBe(false);
    });

    test('Access & Onboarding: three status blocks in one row, Work email is a compact row', async ({ page }) => {
      await boot(page, locale);
      await open(page, 'access-onboarding');
      const group = page.getByRole('group', { name: /Employment and sign-in status|حالة التوظيف وتسجيل الدخول/ });
      await expect(group).toBeVisible();
      const tops = await group.locator('dl > div').evaluateAll(els => els.map(el => Math.round(el.getBoundingClientRect().top)));
      expect(tops).toHaveLength(3);
      expect(Math.max(...tops) - Math.min(...tops), `status block tops ${tops.join(',')}`).toBeLessThanOrEqual(2);
      const mail = page.locator('[data-testid="work-email-row"]');
      await expect(mail).toBeVisible();
      expect((await mail.boundingBox())!.height, 'Work email row height').toBeLessThanOrEqual(64);
    });
  });

  test.describe(`employee record overview (${locale})`, () => {
    test.use({ viewport: { width: 1366, height: 768 } });

    test('nothing to do: one card with one line, not two mostly empty cards', async ({ page }) => {
      await boot(page, locale, true);
      await open(page, 'overview');
      const main = page.locator('[data-employee-workspace-scroll]');
      await expect(main.getByText(locale === 'ar' ? 'لا توجد معوقات جاهزية.' : 'No readiness blockers.', { exact: true })).toBeVisible();
      await expect(main.getByText(/Open requirements|المتطلبات المفتوحة/)).toHaveCount(0);
      expect(await main.locator('section.surface').count(), 'cards on the overview').toBe(1);
    });

    test('blockers present: the next action and the open requirements stay', async ({ page }) => {
      await boot(page, locale);
      await open(page, 'overview');
      const main = page.locator('[data-employee-workspace-scroll]');
      await expect(main.getByRole('heading', { name: locale === 'ar' ? 'الإجراء التالي الموصى به' : 'Recommended next action' })).toBeVisible();
      await expect(main.getByRole('heading', { name: locale === 'ar' ? 'المتطلبات المفتوحة' : 'Open requirements' })).toBeVisible();
    });
  });
}
