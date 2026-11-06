/**
 * ATTENDANCE BULK OPERATIONS — the screen, driven as the IntelliFlow HR Manager against the real API.
 *
 *   /attendance → Raw Punch Logs:  Download template · paste a CSV · Validate · "Import N valid rows (skip M)"
 *   /attendance → Live Dashboard:  Record attendance (one list, no mode switch) → progress (% and who is recorded) → Process now → daily records
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

  test('Live Dashboard mass entry: Enter sends nothing; 2 employees x 2 days shows live progress, who is recorded, then Process now fills the daily records', async ({ page }) => {
    const [e1, e2] = pools.uiMass;
    const bulkRequests: string[] = [];
    page.on('request', (r: Request) => { if (r.method() === 'POST' && r.url().includes('/api/attendance/events/bulk-entry')) bulkRequests.push(r.postData() ?? ''); });
    const processRequests: string[] = [];
    page.on('request', (r: Request) => { if (r.method() === 'POST' && r.url().includes('/api/attendance/process/async')) processRequests.push(r.postData() ?? ''); });

    await openAttendance(page, en('Live Dashboard'));
    const m = main(page);
    const panel = m.getByRole('region', { name: en('Record attendance') });
    await expect(panel, 'record attendance is on the first screen').toBeVisible();
    // No mode to choose: one list serves one employee or many.
    await expect(panel.getByRole('button', { name: /^(One employee|Several employees)$/ })).toHaveCount(0);

    // Filters: department, branch and job title, folded behind one button so the list keeps the height.
    const filters = panel.getByRole('button', { name: en('Filters'), exact: true });
    await expect(filters).toHaveAttribute('aria-expanded', 'false');
    await filters.click();
    for (const label of ['Department', 'Branch', 'Job title']) await expect(panel.getByLabel(en(label), { exact: true })).toBeVisible();
    await filters.click();
    await expect(panel.getByLabel(en('Job title'), { exact: true })).toHaveCount(0);

    // 360 search: the hint names what an HR Manager (employees.sensitive) can search by.
    const search = panel.getByLabel(en('Find employees'), { exact: true });
    await expect(search).toHaveAttribute('placeholder', en('Name, code, email, phone, ID/Iqama, job title, department or branch'));
    await search.fill(e1.code.toLowerCase());
    const first = panel.getByRole('checkbox', { name: codePattern(e1.code) });
    await expect(first, 'search is case-insensitive').toBeVisible();
    await search.press('Enter');
    await page.waitForTimeout(1_000);
    expect(bulkRequests, 'Enter in the search box sent a mass entry').toEqual([]);

    // Select two employees across two searches (the selection survives a new search).
    await first.check();
    await search.fill(e2.code);
    const second = panel.getByRole('checkbox', { name: codePattern(e2.code) });
    await expect(second).toBeVisible();
    await second.check();
    await expect(panel.getByRole('status').filter({ hasText: en(SELECTED_KEY, { count: 2 }) })).toBeVisible();

    // Two past days, In and Out, a reason (Enter in the reason box sends nothing either).
    await panel.getByLabel(en('From date')).fill(massDays[0]);
    await panel.getByLabel(en('To date')).fill(massDays[1]);
    await panel.getByLabel(en('In time')).fill(`09:${mm}`);
    await panel.getByLabel(en('Out time'), { exact: true }).fill(`14:${mm}`);
    const submit = panel.getByRole('button', { name: en(SUBMIT_KEY, { employees: 2, days: 2 }) });
    await expect(submit, 'a reason is required before anything can be recorded').toBeDisabled();
    const reason = panel.getByLabel(en('Reason (required)'));
    await reason.fill(`E2E mass entry ${Date.now()}`);
    await reason.press('Enter');
    await page.waitForTimeout(500);
    expect(bulkRequests, 'Enter in the reason box sent a mass entry').toEqual([]);
    await expect(submit).toBeEnabled();

    const before = (await Promise.all([e1, e2].map((e) => rawPunches(api, admin, e.id, massDays[0])))).flat().length;
    await submit.click();

    // The form folds into a one-line summary while the progress view runs, so the daily records stay close.
    await expect(panel.getByLabel(en('Find employees'), { exact: true })).toHaveCount(0);
    await expect(panel.getByText(en('{employees, plural, one {# employee} other {# employees}} × {days, plural, one {# day} other {# days}}', { employees: 2, days: 2 }))).toBeVisible();

    // Progress: a bar with %, then the finished line, the counts, and each person marked Recorded.
    const progress = panel.getByRole('progressbar', { name: en('Mass entry progress') });
    await expect(progress).toBeVisible();
    await expect(progress).toHaveAttribute('aria-valuenow', '100', { timeout: 30_000 });
    await expect(panel.getByRole('status').filter({ hasText: en('Finished: {done} of {total} employees recorded.', { done: 2, total: 2 }) })).toBeVisible();
    await expect(panel.getByText(en('{created} punches recorded, {duplicates} already on file, {refused} refused.', { created: 8, duplicates: 0, refused: 0 }))).toBeVisible();
    const people = panel.getByRole('list', { name: en('Employees in this entry') });
    for (const e of [e1, e2]) {
      await expect(people.getByRole('listitem').filter({ hasText: codePattern(e.code) }).getByText(en('Recorded'), { exact: true })).toBeVisible();
    }
    expect(bulkRequests, 'two people go one request each, so the bar moves per person').toHaveLength(2);
    expect(bulkRequests.map((b) => (JSON.parse(b) as { employeeIds: number[] }).employeeIds).flat().sort((a, b) => a - b))
      .toEqual([e1.id, e2.id].sort((a, b) => a - b));

    // The API holds the 8 new punches (In and Out on each of the two days, for each person). Only THIS run's punches are
    // counted (its minute): an earlier run on the same database may have left bulk punches on these days at another minute.
    for (const e of [e1, e2]) {
      for (const day of massDays) {
        const bulk = (await rawPunches(api, admin, e.id, day)).filter((p) => p.source === 'Manual (bulk)' && p.punchTimestampUtc.slice(0, 10) === day
          && p.punchTimestampUtc.slice(14, 16) === mm);
        expect(bulk.map((p) => p.punchDirection).sort(), `${e.code} on ${day}`).toEqual(['In', 'Out']);
      }
    }
    expect((await Promise.all([e1, e2].map((e) => rawPunches(api, admin, e.id, massDays[0])))).flat().length - before).toBe(4);

    // Process now: one job for exactly these two people and days, with its own % progress, then the daily records show them.
    await panel.getByRole('button', { name: en('Process {count, plural, one {# employee} other {# employees}} now', { count: 2 }) }).click();
    await expect(panel.getByRole('status').filter({ hasText: en('Attendance processed for {employees, plural, one {# employee} other {# employees}}.', { employees: 2 }) }))
      .toBeVisible({ timeout: 150_000 });
    expect(processRequests).toHaveLength(1);
    const asked = JSON.parse(processRequests[0]) as { fromDate: string; toDate: string; employeeIds: number[] };
    expect({ ...asked, employeeIds: [...asked.employeeIds].sort((a, b) => a - b) })
      .toEqual({ fromDate: massDays[0], toDate: massDays[1], employeeIds: [e1.id, e2.id].sort((a, b) => a - b) });
    await m.getByLabel(en('Attendance date')).fill(massDays[0]);
    // The daily records table (same screen, right below the panel) names each person; it shows names, not codes.
    for (const e of [e1, e2]) {
      expect(e.name, `${e.code} has a name to look for`).toBeTruthy();
      await expect(m.getByRole('row').filter({ hasText: e.name! }).first(), `${e.code} in the dashboard's daily records`).toBeVisible({ timeout: 30_000 });
    }

    // New entry clears the progress view for the next one.
    await panel.getByRole('button', { name: en('New entry') }).click();
    await expect(progress).toHaveCount(0);

    // The everyday single-person fix in the same list: tick ONE employee, leave In empty, enter only the Out.
    await panel.getByRole('button', { name: en('Clear selection') }).click();
    await search.fill(e1.code);
    await panel.getByRole('checkbox', { name: codePattern(e1.code) }).check();
    await panel.getByLabel(en('To date')).fill(massDays[0]);
    await panel.getByLabel(en('In time')).fill('');
    await panel.getByLabel(en('Out time'), { exact: true }).fill(`18:${mm}`);
    await panel.getByLabel(en('Reason (required)')).fill(`E2E forgot to punch out ${Date.now()}`);
    await panel.getByRole('button', { name: en(SUBMIT_KEY, { employees: 1, days: 1 }) }).click();
    await expect(panel.getByRole('status').filter({ hasText: en('Finished: {done} of {total} employees recorded.', { done: 1, total: 1 }) })).toBeVisible({ timeout: 30_000 });
    await expect(panel.getByText(en('Out at {time}', { time: `18:${mm}` }))).toBeVisible();
    const outOnly = (await rawPunches(api, admin, e1.id, massDays[0])).filter((p) => p.source === 'Manual (bulk)' && p.punchTimestampUtc.slice(14, 16) === mm);
    expect(outOnly.map((p) => p.punchDirection).sort(), 'the earlier In and Out, plus the new Out only').toEqual(['In', 'Out', 'Out']);
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
    const newTitles = ['CSV Attendance Import', 'Validate file', 'Download template'];
    const rawTab = m.getByRole('tab', { name: ar('Raw Punch Logs'), exact: true });
    await expect(rawTab).toBeVisible({ timeout: 60_000 });
    await rawTab.click();

    await expect(m.getByText(ar('CSV Attendance Import'), { exact: true })).toBeVisible();
    await expect(m.getByRole('button', { name: ar('Download template') })).toBeVisible();
    await expect(m.getByRole('button', { name: ar('Validate file'), exact: true })).toBeVisible();
    await expect(m.getByLabel(ar('CSV content'))).toBeVisible();
    for (const title of newTitles) {
      expect(ar(title), `the Arabic dictionary has "${title}"`).not.toBe(title);
      await expect(m.getByText(title, { exact: true }), `English "${title}" is still on screen`).toHaveCount(0);
    }

    // The Live Dashboard's record panel, switched to several employees: the form and its multi-select are translated too.
    const dashboardTab = m.getByRole('tab', { name: ar('Live Dashboard'), exact: true });
    await dashboardTab.click();
    await expect(m.getByRole('region', { name: ar('Record attendance') })).toBeVisible();
    for (const title of ['Record attendance', 'Filters', 'Out time']) {
      expect(ar(title), `the Arabic dictionary has "${title}"`).not.toBe(title);
      await expect(m.getByText(title, { exact: true }), `English "${title}" is still on screen`).toHaveCount(0);
    }
    await expect(m.getByLabel(ar('Find employees'), { exact: true })).toBeVisible();
    await expect(m.getByRole('button', { name: ar('Select all shown') })).toBeVisible();
    await expect(m.getByLabel(ar('Reason (required)'))).toBeVisible();
    await expect(m.getByText(ar('In time'), { exact: true })).toBeVisible();
    for (const english of ['Find employees', 'Select all shown', 'Reason (required)', 'Out time', 'Filters']) {
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
