/**
 * ATTENDANCE BULK OPERATIONS — the screen, driven as the IntelliFlow HR Manager against the real API.
 *
 *   /attendance → Raw Punch Logs:  Download template · paste a CSV · Validate · "Import N valid rows (skip M)" · Mass entry
 *   /attendance → Processing:      All employees → Process Records → progress → completion (a background job)
 *   …and one Arabic / right-to-left smoke over all three panels.
 *
 * Nothing here is mocked: every number on screen is compared with what the API holds. The strings come from the app's own
 * dictionaries (`translate`), so a reworded label fails here the same moment it changes on screen.
 *
 * DATA. Past, free, unlocked days found with the import dry run (e2e/attendance-bulk-support.ts), on employees the API
 * spec does not use, so this spec can run again on the same database and its row counts stay exact.
 *
 * Runs serially: the three flows share one set of employees and days, and the write paths take a per-company lock.
 */
import { test, expect } from '@playwright/test';
import type { APIRequestContext, Page, Locator, Request } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { INTELLIFLOW_ADMIN, INTELLIFLOW_HR_MGR, INTELLIFLOW_SLUG, apiLogin, tenantLogin } from './helpers';
import { translate } from '../src/i18n/translations';
import { LOCALE_CHOICE_KEY } from '../src/i18n/localeBoot';
import {
  UNKNOWN_CODE_MESSAGE_START, TEMPLATE_HEADER, activeEmployees, csvFile, csvRow, findFreeWindow, newApi, planPools, rawPunches, runClock,
  type Pools,
} from './attendance-bulk-support';

const en = (key: string, params?: Record<string, string | number>): string => translate('en', key, params);
const ar = (key: string, params?: Record<string, string | number>): string => translate('ar', key, params);

const SUBMIT_KEY = 'Record attendance for {employees, plural, one {# employee} other {# employees}} × {days, plural, one {# day} other {# days}}';
const SUMMARY_KEY = '{employees, plural, one {# employee} other {# employees}} × {days, plural, one {# day} other {# days}}';
const PARTIAL_KEY = 'Import {count, plural, one {# valid row} other {# valid rows}} (skip {skipped})';
const IMPORTED_KEY = 'Imported {imported, plural, one {# row} other {# rows}}. {duplicates} already recorded, {failed} skipped.';
const SELECTED_KEY = '{count, plural, one {# employee selected} other {# employees selected}}';

const { mm, ss } = runClock();
const main = (page: Page): Locator => page.locator('main');
const codePattern = (code: string): RegExp => new RegExp(`${code.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?!\\d)`);

async function openAttendance(page: Page, tab: string): Promise<void> {
  await tenantLogin(page, INTELLIFLOW_HR_MGR.email, INTELLIFLOW_HR_MGR.password, INTELLIFLOW_SLUG);
  await page.goto('/attendance', { waitUntil: 'domcontentloaded' });
  const target = main(page).getByRole('tab', { name: tab, exact: true });
  await expect(target).toBeVisible({ timeout: 60_000 });
  await target.click();
  await expect(target).toHaveAttribute('aria-selected', 'true');
}

/** The raw log is for one date; point it at the day the scenario wrote to. */
async function showRawLogFor(page: Page, day: string): Promise<void> {
  await main(page).getByLabel(en('Attendance date')).fill(day);
}

test.describe.configure({ mode: 'serial', timeout: 240_000 });

test.describe('Attendance bulk operations — screen', () => {
  let api: APIRequestContext;
  let admin: string;
  let hr: string;
  let pools: Pools;
  let importDay: string;
  let massDays: string[];
  const unknownCode = `NOPE-UI-${Date.now()}`;

  test.beforeAll(async () => {
    api = await newApi();
    admin = await apiLogin(api, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    hr = await apiLogin(api, INTELLIFLOW_HR_MGR.email, INTELLIFLOW_HR_MGR.password, INTELLIFLOW_SLUG);
    pools = planPools(await activeEmployees(api, admin));
    const u1 = pools.uiImport;
    [importDay] = await findFreeWindow(api, hr, 1, (day) => [csvRow(u1.code, day, `12:${mm}:${ss}`, 'In'), csvRow(u1.code, day, `13:${mm}:${ss}`, 'Out')]);
    massDays = await findFreeWindow(api, hr, 2, (day) => pools.uiMass.flatMap((e) => [
      csvRow(e.code, day, `09:${mm}:00`, 'In'), csvRow(e.code, day, `14:${mm}:00`, 'Out'),
    ]));
  });

  test.afterAll(async () => { await api?.dispose(); });

  test('Raw Punch Logs: download the template, validate a pasted file, import the valid rows, see them in the log', async ({ page }) => {
    const u1 = pools.uiImport;
    const imports: string[] = [];
    page.on('request', (r: Request) => { if (r.method() === 'POST' && /\/api\/attendance\/events\/import$/.test(new URL(r.url()).pathname)) imports.push(r.url()); });

    await openAttendance(page, en('Raw Punch Logs'));
    const m = main(page);
    await expect(m.getByText(en('CSV Attendance Import'), { exact: true })).toBeVisible();

    // 1. The template downloads, with the server's header.
    const [template] = await Promise.all([
      page.waitForEvent('download'),
      m.getByRole('button', { name: en('Download template') }).click(),
    ]);
    expect(template.suggestedFilename()).toBe('attendance-import-template.csv');
    const templateText = (await readFile((await template.path())!, 'utf8')).replace(/^\uFEFF/, '');
    expect(templateText.split(/\r?\n/)[0]).toBe(TEMPLATE_HEADER);

    // 2. Paste a file: two good rows and one unknown code (file line 4).
    const csv = csvFile(
      csvRow(u1.code, importDay, `12:${mm}:${ss}`, 'In'),
      csvRow(u1.code, importDay, `13:${mm}:${ss}`, 'Out'),
      csvRow(unknownCode, importDay, `12:${mm}:${ss}`, 'In'),
    );
    const importButton = m.getByRole('button', { name: en(PARTIAL_KEY, { count: 2, skipped: 1 }) });
    await m.getByLabel(en('CSV content')).fill(csv);
    await expect(importButton, 'importing is offered only after a Validate of exactly this content').toHaveCount(0);

    // 3. Validate: counts and the row that needs fixing; nothing is saved.
    const before = (await rawPunches(api, admin, u1.id, importDay)).length;
    await m.getByRole('button', { name: en('Validate file'), exact: true }).click();
    await expect(m.getByText(en('Rows in file: {count}', { count: 3 }), { exact: true })).toBeVisible();
    await expect(m.getByText(en('Valid: {count}', { count: 2 }), { exact: true })).toBeVisible();
    await expect(m.getByText(en('Already recorded: {count}', { count: 0 }), { exact: true })).toBeVisible();
    await expect(m.getByText(en('Need fixing: {count}', { count: 1 }), { exact: true })).toBeVisible();
    await expect(m.getByText(en('Nothing has been saved yet. Review the result, then import.'))).toBeVisible();
    const problem = m.getByRole('table', { name: en('Rows that need fixing') }).getByRole('row').filter({ hasText: UNKNOWN_CODE_MESSAGE_START });
    await expect(problem).toHaveCount(1);
    await expect(problem.getByRole('cell').first(), 'the REAL file line number').toHaveText('4');
    expect(imports, 'Validate must not call the import endpoint').toEqual([]);
    expect((await rawPunches(api, admin, u1.id, importDay)).length, 'Validate wrote punches').toBe(before);

    // The error report is a real download that lists the bad row.
    const [report] = await Promise.all([
      page.waitForEvent('download'),
      m.getByRole('button', { name: en('Download error report') }).click(),
    ]);
    expect(report.suggestedFilename()).toBe('attendance-import-errors.csv');
    expect(await readFile((await report.path())!, 'utf8')).toContain(unknownCode);

    // 4. Import the valid rows ("skip 1").
    await expect(importButton).toBeEnabled();
    await importButton.click();
    await expect(m.getByRole('status').filter({ hasText: en(IMPORTED_KEY, { imported: 2, duplicates: 0, failed: 1 }) })).toBeVisible({ timeout: 30_000 });
    expect(imports).toHaveLength(1);

    // 5. The API holds exactly the two new punches, and the raw log shows what the API holds for that day.
    const after = await rawPunches(api, admin, u1.id, importDay);
    expect(after.length - before).toBe(2);
    await showRawLogFor(page, importDay);
    const expectedRows = after.filter((p) => p.punchTimestampUtc.slice(0, 10) === importDay).length;
    const rows = m.getByRole('row').filter({ hasText: codePattern(u1.code) });
    await expect(rows).toHaveCount(expectedRows);
    await expect(rows.filter({ hasText: 'CSV import' }).filter({ hasText: en('Punch in') }).first()).toBeVisible();
    await expect(rows.filter({ hasText: 'CSV import' }).filter({ hasText: en('Punch out') }).first()).toBeVisible();

    // 6. Importing the same text again is safe: the button now offers nothing until it is validated, and a re-validate says all recorded.
    await m.getByLabel(en('CSV content')).fill(`${csv}\n`);
    await m.getByRole('button', { name: en('Validate file'), exact: true }).click();
    await expect(m.getByText(en('Already recorded: {count}', { count: 2 }), { exact: true })).toBeVisible();
    await expect(m.getByText(en('Valid: {count}', { count: 0 }), { exact: true })).toBeVisible();
  });

  test('Mass entry: Enter in the search box sends nothing; two employees x two days records 8 punches and says so', async ({ page }) => {
    const [e1, e2] = pools.uiMass;
    const bulkRequests: string[] = [];
    page.on('request', (r: Request) => { if (r.method() === 'POST' && r.url().includes('/api/attendance/events/bulk-entry')) bulkRequests.push(r.url()); });

    await openAttendance(page, en('Raw Punch Logs'));
    const m = main(page);
    await m.getByRole('button', { name: en('Open mass entry') }).click();

    // Search, then press Enter in the search box: this must never submit anything.
    const search = m.getByLabel(en('Find employees'), { exact: true });
    await search.fill(e1.code);
    const first = m.getByRole('checkbox', { name: codePattern(e1.code) });
    await expect(first).toBeVisible();
    await search.press('Enter');
    await page.waitForTimeout(1_000);
    expect(bulkRequests, 'Enter in the search box sent a mass entry').toEqual([]);
    await expect(m.getByText(/^Requested \d+:/)).toHaveCount(0);

    // Select two employees across two searches (the selection survives a new search).
    await first.check();
    await search.fill(e2.code);
    const second = m.getByRole('checkbox', { name: codePattern(e2.code) });
    await expect(second).toBeVisible();
    await second.check();
    await expect(m.getByRole('status').filter({ hasText: en(SELECTED_KEY, { count: 2 }) })).toBeVisible();

    // Fill the form: two past days, In and Out, a reason.
    await m.getByLabel(en('From date')).fill(massDays[0]);
    await m.getByLabel(en('To date')).fill(massDays[1]);
    await m.getByLabel(en('In time')).fill(`09:${mm}`);
    await m.getByLabel(en('Out time (optional)')).fill(`14:${mm}`);
    const submit = m.getByRole('button', { name: en(SUBMIT_KEY, { employees: 2, days: 2 }) });
    await expect(submit, 'a reason is required before anything can be recorded').toBeDisabled();
    await m.getByLabel(en('Reason (required)')).fill(`E2E mass entry ${Date.now()}`);
    await expect(m.getByText(en(SUMMARY_KEY, { employees: 2, days: 2 }), { exact: true })).toBeVisible();
    await expect(submit).toBeEnabled();
    expect(bulkRequests).toEqual([]);

    const before = (await Promise.all([e1, e2].map((e) => rawPunches(api, admin, e.id, massDays[0])))).flat().length;
    await submit.click();
    await expect(m.getByText(en('Requested {requested}: {created} recorded, {duplicates} already recorded, {failed} refused.', { requested: 8, created: 8, duplicates: 0, failed: 0 }))).toBeVisible({ timeout: 30_000 });
    expect(bulkRequests).toHaveLength(1);

    // The API holds the 8 new punches (4 on each of the two days), and the raw log shows the first day's.
    for (const e of [e1, e2]) {
      for (const day of massDays) {
        const bulk = (await rawPunches(api, admin, e.id, day)).filter((p) => p.source === 'Manual (bulk)' && p.punchTimestampUtc.slice(0, 10) === day);
        expect(bulk.map((p) => p.punchDirection).sort(), `${e.code} on ${day}`).toEqual(['In', 'Out']);
      }
    }
    expect((await Promise.all([e1, e2].map((e) => rawPunches(api, admin, e.id, massDays[0])))).flat().length - before).toBe(4);
    await showRawLogFor(page, massDays[0]);
    for (const e of [e1, e2]) {
      await expect(m.getByRole('row').filter({ hasText: codePattern(e.code) }).filter({ hasText: 'Manual (bulk)' })).toHaveCount(2);
    }
  });

  test('Processing: All employees runs as a job with progress, completes, and is never a 403', async ({ page }) => {
    const forbidden: string[] = [];
    page.on('response', (r) => {
      const url = r.url();
      if (r.status() === 403 && (url.includes('/api/attendance/process') || url.includes('/api/jobs/'))) forbidden.push(`${r.status()} ${url}`);
    });
    const started: string[] = [];
    page.on('request', (r: Request) => { if (r.method() === 'POST' && r.url().includes('/api/attendance/process/async')) started.push(r.postData() ?? ''); });

    await openAttendance(page, en('Processing'));
    const m = main(page);
    await m.getByLabel(en('From date')).fill(massDays[0]);
    await m.getByLabel(en('To date')).fill(massDays[1]);
    await m.getByRole('radio', { name: en('All employees') }).check();

    await m.getByRole('button', { name: en('Process Records'), exact: true }).click();
    await expect(m.getByRole('progressbar', { name: en('Processing progress') })).toBeVisible({ timeout: 15_000 });
    await expect(
      m.getByRole('status').filter({ hasText: /Attendance processed for \d+ employees?\.|Attendance processed into daily records and payroll impacts\./ }),
    ).toBeVisible({ timeout: 150_000 });

    expect(started, 'one background job was started').toHaveLength(1);
    expect(JSON.parse(started[0]), '"All employees" sends no employee list').toEqual({ fromDate: massDays[0], toDate: massDays[1] });
    expect(forbidden, 'the owner\'s 403').toEqual([]);
    const panel = m.getByRole('region', { name: en('Process Attendance') });
    expect(await panel.innerText()).not.toMatch(/\b403\b|access denied|forbidden/i);
    await expect(panel.getByRole('alert')).toHaveCount(0);

    // The job did its work: the API has daily records for the employees mass entry just punched.
    const daily = await (await api.get(`/api/attendance/daily?from=${massDays[0]}&to=${massDays[1]}&pageSize=100`, { headers: { Authorization: `Bearer ${hr}` } })).json() as
      { items: Array<{ employeeId: number; workDate: string }> };
    for (const e of pools.uiMass) {
      for (const day of massDays) {
        expect(daily.items.some((r) => r.employeeId === e.id && r.workDate.slice(0, 10) === day), `${e.code} on ${day}`).toBe(true);
      }
    }
  });

  test('Arabic: the three panels render right-to-left with translated titles and no English fallbacks', async ({ page }) => {
    await tenantLogin(page, INTELLIFLOW_HR_MGR.email, INTELLIFLOW_HR_MGR.password, INTELLIFLOW_SLUG);
    await page.evaluate((key) => localStorage.setItem(key, 'ar'), LOCALE_CHOICE_KEY);
    await page.goto('/attendance', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl', { timeout: 60_000 });
    const m = main(page);

    // The titles the feature added, in each language: present in Arabic, absent in English.
    const newTitles = ['CSV Attendance Import', 'Mass entry', 'Open mass entry', 'Validate file', 'Download template'];
    const rawTab = m.getByRole('tab', { name: ar('Raw Punch Logs'), exact: true });
    await expect(rawTab).toBeVisible({ timeout: 60_000 });
    await rawTab.click();

    await expect(m.getByText(ar('CSV Attendance Import'), { exact: true })).toBeVisible();
    await expect(m.getByRole('button', { name: ar('Download template') })).toBeVisible();
    await expect(m.getByRole('button', { name: ar('Validate file'), exact: true })).toBeVisible();
    await expect(m.getByLabel(ar('CSV content'))).toBeVisible();
    await expect(m.getByText(ar('Mass entry'), { exact: true }).first()).toBeVisible();
    await expect(m.getByRole('button', { name: ar('Open mass entry') })).toBeVisible();
    for (const title of newTitles) {
      expect(ar(title), `the Arabic dictionary has "${title}"`).not.toBe(title);
      await expect(m.getByText(title, { exact: true }), `English "${title}" is still on screen`).toHaveCount(0);
    }

    // Opened, the mass-entry form and its multi-select are translated too.
    await m.getByRole('button', { name: ar('Open mass entry') }).click();
    await expect(m.getByText(ar('Employees to include'), { exact: true })).toBeVisible();
    await expect(m.getByLabel(ar('Find employees'), { exact: true })).toBeVisible();
    await expect(m.getByRole('button', { name: ar('Select all shown') })).toBeVisible();
    await expect(m.getByLabel(ar('Reason (required)'))).toBeVisible();
    await expect(m.getByText(ar('In time'), { exact: true })).toBeVisible();
    for (const english of ['Employees to include', 'Find employees', 'Select all shown', 'Reason (required)', 'Out time (optional)']) {
      await expect(m.getByText(english, { exact: true }), `English "${english}" is still on screen`).toHaveCount(0);
    }

    // The Processing panel.
    const processingTab = m.getByRole('tab', { name: ar('Processing'), exact: true });
    await processingTab.click();
    await expect(m.getByText(ar('Who to process'), { exact: true })).toBeVisible();
    await expect(m.getByRole('radio', { name: ar('All employees') })).toBeVisible();
    await expect(m.getByRole('radio', { name: ar('Selected employees') })).toBeVisible();
    await expect(m.getByRole('button', { name: ar('Process Records'), exact: true })).toBeVisible();
    for (const english of ['Who to process', 'Selected employees', 'Process Records']) {
      await expect(m.getByText(english, { exact: true }), `English "${english}" is still on screen`).toHaveCount(0);
    }
  });
});
