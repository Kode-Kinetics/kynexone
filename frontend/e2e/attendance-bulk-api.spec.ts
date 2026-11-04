/**
 * ATTENDANCE BULK OPERATIONS — the API contract, end to end on a real stack (API + Postgres).
 *
 *   GET  /api/attendance/events/import-template      the CSV template
 *   POST /api/attendance/events/import-preview       a dry run: judges every row, writes NOTHING
 *   POST /api/attendance/events/import               commits the valid rows, skips duplicates, reports the rest
 *   POST /api/attendance/events/bulk-entry           the same In/Out for a SELECTION of employees over a date range
 *   POST /api/attendance/process/async  + GET /api/jobs/{id}   "All employees" as a background job
 *
 * Acting as the IntelliFlow HR Manager (a persona the bootstrap provisions). Each invariant of the feature has a test:
 *   I1 nobody records their own attendance in bulk      (needs an HR login that IS an employee: see linkedHrSession)
 *   I2 an out-of-scope row reads exactly like an unknown code; another tenant's employee can never be written or processed
 *   I5/I6 no future punch, no implausible date, no punch in a payroll-locked period (the window finder never picks one)
 *   I7 sending a file or a request again creates nothing new
 *   I9 permissions: import needs attendance.bulk_import; mass entry needs attendance.write AND attendance.bulk_import;
 *      processing needs an HR role. A 403 for everyone else, and NOT for the HR Manager (the owner's "All employees" 403).
 *   I11 size caps answer 400 with a plain explanation.
 *
 * DATA. IntelliFlow (tenant A) and Ras Al-Manar (tenant B) as provisioned by e2e/bootstrap/provision.ts. Punches are written on
 * past days that are free and unlocked, found with the product's own dry run (see attendance-bulk-support.ts), so the spec can
 * run again on the same database. Nothing is deleted; the punches it writes stay, on days nothing else reads.
 *
 * Runs serially (the write paths take a per-company lock; a 409 is retried once by `send`).
 */
import { test, expect } from '@playwright/test';
import type { APIRequestContext } from '@playwright/test';
import {
  apiLogin, apiLoginLive,
  INTELLIFLOW_ADMIN, INTELLIFLOW_AUDITOR, INTELLIFLOW_FINANCE, INTELLIFLOW_HR_MGR, INTELLIFLOW_SLUG, INTELLIFLOW_SUPERVISOR,
  RASALMANAR_ADMIN, RASALMANAR_SLUG,
} from './helpers';
import {
  UNKNOWN_CODE_MESSAGE_START, TEMPLATE_HEADER, activeEmployees, addDays, csvFile, csvRow, dayBack, findFreeWindow, linkedHrSession,
  newApi, planPools, rawCount, rawPunches, runClock, send, waitForJob,
  type Emp, type Pools,
} from './attendance-bulk-support';

interface ImportResult {
  batchId: string | null; status: string | null; fileName: string;
  totalRows: number; validRows: number; importedRows: number; duplicateRows: number; failedRows: number;
  timeZone: string; errors: Array<{ rowNumber: number; column: string | null; message: string; rawRow: string }>;
}
interface BulkResult {
  requested: number; created: number; duplicates: number; failed: number;
  errors: Array<{ employeeCode: string; date: string; message: string }>;
}
interface DailyPage { items: Array<{ employeeId: number; workDate: string; firstInUtc: string | null }>; total: number }

const { mm, ss } = runClock();

test.describe.configure({ mode: 'serial', timeout: 240_000 });

test.describe('Attendance bulk operations — API', () => {
  let api: APIRequestContext;
  let hr: string;
  let admin: string;
  let tenantB: string;
  let pools: Pools;
  let other: Emp; // an employee of tenant B (Ras Al-Manar)

  // The scenarios' days and employees, decided once.
  let importDay: string;
  let importTimes: { in: string; out: string };
  let bulkDays: string[];
  let bulkTimes: { in: string; out: string };
  let importFile: string;
  const unknownCode = `NOPE-${Date.now()}`;
  let futureDay: string;

  test.beforeAll(async () => {
    api = await newApi();
    hr = await apiLogin(api, INTELLIFLOW_HR_MGR.email, INTELLIFLOW_HR_MGR.password, INTELLIFLOW_SLUG);
    admin = await apiLogin(api, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    tenantB = await apiLogin(api, RASALMANAR_ADMIN.email, RASALMANAR_ADMIN.password, RASALMANAR_SLUG);

    pools = planPools(await activeEmployees(api, admin));
    const bEmployees = await activeEmployees(api, tenantB);
    expect(bEmployees.length, 'Ras Al-Manar must have an Active employee to act as "another tenant"').toBeGreaterThan(0);
    other = bEmployees[0];
    expect(pools.apiImport[0].code, 'the two tenants must not share employee codes, or the cross-tenant rows prove nothing').not.toBe(other.code);

    const [a1] = pools.apiImport;
    const [b1, b2] = pools.apiBulk;
    importTimes = { in: `10:${mm}:${ss}`, out: `15:${mm}:${ss}` };
    bulkTimes = { in: `11:${mm}`, out: `16:${mm}` };
    futureDay = addDays(dayBack(0), 2);

    [importDay] = await findFreeWindow(api, hr, 1, (day) => [
      csvRow(a1.code, day, importTimes.in, 'In'), csvRow(a1.code, day, importTimes.out, 'Out'),
    ]);
    bulkDays = await findFreeWindow(api, hr, 2, (day) => [b1, b2].flatMap((e) => [
      csvRow(e.code, day, `${bulkTimes.in}:00`, 'In'), csvRow(e.code, day, `${bulkTimes.out}:00`, 'Out'),
    ]));

    // The mixed file. Line numbers are FILE lines (the header is line 1):
    //   2 valid In, 3 valid Out, 4 unknown code, 5 another tenant's code, 6 future time, 7 bad direction, 8 repeat of line 2.
    importFile = csvFile(
      csvRow(a1.code, importDay, importTimes.in, 'In'),
      csvRow(a1.code, importDay, importTimes.out, 'Out'),
      csvRow(unknownCode, importDay, importTimes.in, 'In'),
      csvRow(other.code, importDay, importTimes.in, 'In'),
      csvRow(a1.code, futureDay, '09:00:00', 'In'),
      csvRow(a1.code, importDay, `11:${mm}:${ss}`, 'Sideways'),
      csvRow(a1.code, importDay, importTimes.in, 'In'),
    );
  });

  test.afterAll(async () => { await api?.dispose(); });

  // ── the template ───────────────────────────────────────────────────────────────────────────────

  test('the template is a text/csv file with the five columns and a worked example', async () => {
    const response = await send(api, 'GET', '/api/attendance/events/import-template', hr);
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toContain('text/csv');
    const lines = (await response.text()).replace(/^\uFEFF/, '').split(/\r?\n/).filter((l) => l.trim());
    expect(lines[0]).toBe(TEMPLATE_HEADER);
    expect(lines.length, 'a header and at least one example row').toBeGreaterThanOrEqual(2);
    expect(lines[1].split(',').length, 'the example fills every column').toBe(5);
  });

  test('an empty file and a header-only file are refused with an explanation, by the dry run and the import alike', async () => {
    for (const url of ['/api/attendance/events/import-preview', '/api/attendance/events/import']) {
      for (const csvContent of ['', '   \n', `${TEMPLATE_HEADER}\n`]) {
        const response = await send(api, 'POST', url, hr, { fileName: 'empty.csv', csvContent });
        expect(response.status(), `${url} with ${JSON.stringify(csvContent)}`).toBe(400);
        expect(String(((await response.json()) as { message?: string }).message ?? '').length, 'a plain explanation').toBeGreaterThan(10);
      }
    }
  });

  test('implausible dates (before the year 2000, more than 400 days back) are row errors, not silently imported', async () => {
    const [a1] = pools.apiImport;
    const response = await send(api, 'POST', '/api/attendance/events/import-preview', hr, {
      fileName: 'old.csv',
      csvContent: csvFile(csvRow(a1.code, '1999-12-31', '09:00:00', 'In'), csvRow(a1.code, dayBack(700), '09:00:00', 'In')),
    });
    expect(response.status()).toBe(200);
    const body = await response.json() as ImportResult;
    expect(body.totalRows).toBe(2);
    expect(body.validRows).toBe(0);
    expect(body.failedRows).toBe(2);
    expect(body.errors.map((e) => e.rowNumber)).toEqual([2, 3]);
  });

  // ── preview → import → re-import ───────────────────────────────────────────────────────────────

  test('the dry run judges a mixed file row by row and writes NOTHING', async () => {
    const [a1] = pools.apiImport;
    const beforeA = await rawCount(api, admin, [a1.id], [importDay]);
    const beforeB = (await rawPunches(api, tenantB, other.id, importDay)).length;

    const response = await send(api, 'POST', '/api/attendance/events/import-preview', hr, { fileName: 'mixed.csv', csvContent: importFile });
    expect(response.status(), await response.text()).toBe(200);
    const body = await response.json() as ImportResult;

    expect(body.totalRows).toBe(7);
    expect(body.validRows).toBe(2);
    expect(body.duplicateRows, 'line 8 repeats line 2 — counted, not an error').toBe(1);
    expect(body.failedRows).toBe(4);
    expect(body.totalRows, 'total = valid + duplicate + failed').toBe(body.validRows + body.duplicateRows + body.failedRows);
    expect(body.importedRows, 'a dry run imports nothing').toBe(0);
    expect(body.batchId, 'a dry run opens no batch').toBeNull();
    expect(body.timeZone.length).toBeGreaterThan(0);

    // Real FILE line numbers, the column at fault, and a plain reason.
    expect(body.errors.map((e) => e.rowNumber)).toEqual([4, 5, 6, 7]);
    expect(body.errors.map((e) => e.column)).toEqual(['employeeCode', 'employeeCode', 'punchTimestamp', 'punchDirection']);
    expect(body.errors[0].message).toContain(UNKNOWN_CODE_MESSAGE_START);
    expect(body.errors[1].message, 'another tenant\'s employee reads EXACTLY like an unknown code — it must not reveal that they exist').toBe(body.errors[0].message);
    expect(body.errors[2].message).toMatch(/future/i);
    expect(body.errors[3].message).toMatch(/Sideways/);
    expect(body.errors[3].message).toMatch(/In, Out, BreakIn or BreakOut/);

    // NOTHING was written, in either tenant.
    expect(await rawCount(api, admin, [a1.id], [importDay]), 'the dry run wrote punches').toBe(beforeA);
    expect((await rawPunches(api, tenantB, other.id, importDay)).length).toBe(beforeB);
  });

  test('the import writes exactly the valid rows, as "CSV import" punches, and never the refused ones', async () => {
    const [a1] = pools.apiImport;
    const before = await rawPunches(api, admin, a1.id, importDay);
    const beforeB = (await rawPunches(api, tenantB, other.id, importDay)).length;

    const response = await send(api, 'POST', '/api/attendance/events/import', hr, { fileName: 'mixed.csv', csvContent: importFile });
    expect(response.status(), await response.text()).toBe(200);
    const body = await response.json() as ImportResult;

    expect(body.totalRows).toBe(7);
    expect(body.validRows).toBe(2);
    expect(body.importedRows, 'after an import, valid = imported').toBe(2);
    expect(body.duplicateRows).toBe(1);
    expect(body.failedRows).toBe(4);
    expect(body.totalRows).toBe(body.validRows + body.duplicateRows + body.failedRows);
    expect(body.batchId).toBeTruthy();
    expect(body.status).toBe('CompletedWithErrors');
    expect(body.errors.map((e) => e.rowNumber)).toEqual([4, 5, 6, 7]);

    const after = await rawPunches(api, admin, a1.id, importDay);
    expect(after.length - before.length, 'exactly the two valid rows were stored').toBe(2);
    const fresh = after.filter((p) => !before.some((b) => b.punchTimestampUtc === p.punchTimestampUtc && b.punchDirection === p.punchDirection));
    expect(fresh.map((p) => p.punchDirection).sort()).toEqual(['In', 'Out']);
    expect(fresh.every((p) => p.source === 'CSV import')).toBe(true);
    expect(fresh.every((p) => p.employeeCode === a1.code)).toBe(true);
    expect((await rawPunches(api, tenantB, other.id, importDay)).length, 'the other tenant\'s employee got nothing').toBe(beforeB);
  });

  test('sending the same file again imports nothing: every valid row is now a duplicate', async () => {
    const [a1] = pools.apiImport;
    const before = await rawCount(api, admin, [a1.id], [importDay]);

    const response = await send(api, 'POST', '/api/attendance/events/import', hr, { fileName: 'mixed.csv', csvContent: importFile });
    expect(response.status()).toBe(200);
    const body = await response.json() as ImportResult;

    expect(body.importedRows).toBe(0);
    expect(body.validRows).toBe(0);
    expect(body.duplicateRows, 'lines 2, 3 and 8 are all on record now').toBe(3);
    expect(body.failedRows).toBe(4);
    expect(body.totalRows).toBe(body.validRows + body.duplicateRows + body.failedRows);
    expect(await rawCount(api, admin, [a1.id], [importDay]), 're-sending created punches').toBe(before);
  });

  // ── mass entry ─────────────────────────────────────────────────────────────────────────────────

  test('mass entry needs a reason, at least one employee, and stays inside the size caps', async () => {
    const [b1, b2] = pools.apiBulk;
    const base = { employeeIds: [b1.id, b2.id], fromDate: bulkDays[0], toDate: bulkDays[1], inTime: `${bulkTimes.in}`, outTime: `${bulkTimes.out}` };

    for (const reason of [undefined, '', '   ']) {
      const response = await send(api, 'POST', '/api/attendance/events/bulk-entry', hr, { ...base, reason });
      expect(response.status(), `reason ${JSON.stringify(reason)}`).toBe(400);
      expect(String(((await response.json()) as { message?: string }).message ?? '')).toMatch(/reason/i);
    }

    const none = await send(api, 'POST', '/api/attendance/events/bulk-entry', hr, { ...base, employeeIds: [], reason: 'e2e' });
    expect(none.status()).toBe(400);

    // 500 employees x 6 days x (In + Out) = 6,000 punches, over the 5,000-punch cap of one mass entry.
    // Employee ids that do not exist are enough: the cap is checked before any employee is looked up.
    const tooMany = await send(api, 'POST', '/api/attendance/events/bulk-entry', hr, {
      employeeIds: Array.from({ length: 500 }, (_, i) => 2_000_000_001 + i),
      fromDate: dayBack(10), toDate: dayBack(5), inTime: '09:00', outTime: '17:00', reason: 'e2e cap check',
    });
    expect(tooMany.status(), 'over the 5,000-punch cap must be a 400 with an explanation (a 403 here means the scope check ran first)').toBe(400);
    expect(String(((await tooMany.json()) as { message?: string }).message ?? '')).toMatch(/5,?000|punch/i);
  });

  test('mass entry for 2 employees x 2 days with In and Out creates 8 punches; sending it again creates none', async () => {
    const [b1, b2] = pools.apiBulk;
    const request = {
      employeeIds: [b1.id, b2.id], fromDate: bulkDays[0], toDate: bulkDays[1],
      inTime: bulkTimes.in, outTime: bulkTimes.out, reason: `e2e mass entry ${Date.now()}`,
    };
    const before = await rawCount(api, admin, [b1.id, b2.id], bulkDays);

    const first = await send(api, 'POST', '/api/attendance/events/bulk-entry', hr, request);
    expect(first.status(), await first.text()).toBe(200);
    const created = await first.json() as BulkResult;
    expect(created.requested, 'counts are PUNCHES: 2 employees x 2 days x (In + Out)').toBe(8);
    expect(created.created).toBe(8);
    expect(created.duplicates).toBe(0);
    expect(created.failed).toBe(0);
    expect(created.errors).toEqual([]);

    const afterFirst = await rawCount(api, admin, [b1.id, b2.id], bulkDays);
    expect(afterFirst - before).toBe(8);
    const sample = (await rawPunches(api, admin, b1.id, bulkDays[0])).filter((p) => p.source === 'Manual (bulk)');
    expect(sample.length, 'each employee-day carries an In and an Out labelled "Manual (bulk)"').toBeGreaterThanOrEqual(2);

    const again = await send(api, 'POST', '/api/attendance/events/bulk-entry', hr, request);
    expect(again.status()).toBe(200);
    const repeat = await again.json() as BulkResult;
    expect(repeat.requested).toBe(8);
    expect(repeat.created, 'a repeat creates nothing').toBe(0);
    expect(repeat.duplicates).toBe(8);
    expect(repeat.failed).toBe(0);
    expect(await rawCount(api, admin, [b1.id, b2.id], bulkDays)).toBe(afterFirst);
  });

  test('a mass entry for a day in the future is refused per item and writes nothing', async () => {
    const [b1] = pools.apiBulk;
    const before = await rawCount(api, admin, [b1.id], [futureDay]);
    const response = await send(api, 'POST', '/api/attendance/events/bulk-entry', hr, {
      employeeIds: [b1.id], fromDate: futureDay, toDate: futureDay, inTime: '09:00', outTime: '17:00', reason: 'e2e future check',
    });
    expect(response.status()).toBe(200);
    const body = await response.json() as BulkResult;
    expect(body.requested).toBe(2);
    expect(body.created).toBe(0);
    expect(body.failed).toBe(2);
    expect(body.errors).toHaveLength(1);
    expect(body.errors[0].employeeCode).toBe(b1.code);
    expect(body.errors[0].message).toMatch(/future/i);
    expect(await rawCount(api, admin, [b1.id], [futureDay])).toBe(before);
  });

  // ── processing ─────────────────────────────────────────────────────────────────────────────────

  test('"All employees" starts a background job (NOT a 403), the job finishes, and the daily records are there', async () => {
    const [b1, b2] = pools.apiBulk;
    const response = await send(api, 'POST', '/api/attendance/process/async', hr, { fromDate: bulkDays[0], toDate: bulkDays[1] });
    // The owner's report: this used to be a 403 for an HR Manager whose scope is a company, not "everything".
    expect(response.status(), 'processing all employees must not be forbidden for an HR Manager').not.toBe(403);
    expect(response.status(), await response.text()).toBe(202);
    const queued = await response.json() as { jobId: string; status: string; statusUrl?: string };
    expect(queued.jobId).toBeTruthy();

    const job = await waitForJob(api, hr, queued.jobId);
    expect(job.status, job.lastError ?? '').toBe('Succeeded');
    expect(job.result?.employees ?? 0, 'everyone in scope was processed').toBeGreaterThanOrEqual(2);

    const daily = await send(api, 'GET', `/api/attendance/daily?from=${bulkDays[0]}&to=${bulkDays[1]}&pageSize=100`, hr);
    expect(daily.status()).toBe(200);
    const page = await daily.json() as DailyPage;
    for (const employee of [b1, b2]) {
      for (const day of bulkDays) {
        const record = page.items.find((r) => r.employeeId === employee.id && r.workDate.slice(0, 10) === day);
        expect(record, `a daily record for ${employee.code} on ${day}`).toBeTruthy();
        expect(record!.firstInUtc, `${employee.code} on ${day} has the punched In`).toBeTruthy();
      }
    }
  });

  test('a selection processes exactly those people; employeeId and employeeIds are merged; an empty list is a 400', async () => {
    const [b1, b2] = pools.apiBulk;
    const range = { fromDate: bulkDays[0], toDate: bulkDays[1] };

    const one = await send(api, 'POST', '/api/attendance/process/async', hr, { ...range, employeeIds: [b1.id] });
    expect(one.status(), await one.text()).toBe(202);
    const oneJob = await waitForJob(api, hr, ((await one.json()) as { jobId: string }).jobId);
    expect(oneJob.status, oneJob.lastError ?? '').toBe('Succeeded');
    expect(oneJob.result?.employees).toBe(1);

    const merged = await send(api, 'POST', '/api/attendance/process/async', hr, { ...range, employeeId: b1.id, employeeIds: [b2.id] });
    expect(merged.status(), await merged.text()).toBe(202);
    const mergedJob = await waitForJob(api, hr, ((await merged.json()) as { jobId: string }).jobId);
    expect(mergedJob.status, mergedJob.lastError ?? '').toBe('Succeeded');
    expect(mergedJob.result?.employees, 'employeeId and employeeIds are both honoured').toBe(2);

    const empty = await send(api, 'POST', '/api/attendance/process/async', hr, { ...range, employeeIds: [] });
    expect(empty.status(), 'an empty selection must never mean "everyone"').toBe(400);

    const sync = await send(api, 'POST', '/api/attendance/process', hr, { ...range, employeeIds: [b1.id] });
    expect(sync.status(), await sync.text()).toBe(200);
    expect(typeof (await sync.json())).toBe('number');
  });

  // ── permissions ────────────────────────────────────────────────────────────────────────────────

  test('people without the permissions get a 403 on every bulk endpoint', async () => {
    const [b1] = pools.apiBulk;
    const day = bulkDays[0];
    const punchesBefore = await rawCount(api, admin, [b1.id], [day]);
    const personas = [
      { name: 'Finance Approver (no attendance access at all)', token: await apiLogin(api, INTELLIFLOW_FINANCE.email, INTELLIFLOW_FINANCE.password, INTELLIFLOW_SLUG) },
      { name: 'Auditor (attendance.read only)', token: await apiLoginLive(api, INTELLIFLOW_AUDITOR.email, INTELLIFLOW_AUDITOR.password, INTELLIFLOW_SLUG) },
      // Holds attendance.write but NOT attendance.bulk_import: mass entry needs both.
      { name: 'Supervisor (attendance.write without attendance.bulk_import)', token: await apiLoginLive(api, INTELLIFLOW_SUPERVISOR.email, INTELLIFLOW_SUPERVISOR.password, INTELLIFLOW_SLUG) },
    ];
    for (const { name, token } of personas) {
      const file = { fileName: 'forbidden.csv', csvContent: csvFile(csvRow(b1.code, day, `05:${mm}:${ss}`, 'In')) };
      const attempts: Array<[string, number]> = [
        ['GET import-template', (await send(api, 'GET', '/api/attendance/events/import-template', token)).status()],
        ['POST import-preview', (await send(api, 'POST', '/api/attendance/events/import-preview', token, file)).status()],
        ['POST import', (await send(api, 'POST', '/api/attendance/events/import', token, file)).status()],
        ['POST bulk-entry', (await send(api, 'POST', '/api/attendance/events/bulk-entry', token, {
          employeeIds: [b1.id], fromDate: day, toDate: day, inTime: `05:${mm}`, reason: 'e2e permission check',
        })).status()],
        ['POST process', (await send(api, 'POST', '/api/attendance/process', token, { fromDate: day, toDate: day })).status()],
        ['POST process/async', (await send(api, 'POST', '/api/attendance/process/async', token, { fromDate: day, toDate: day })).status()],
      ];
      for (const [what, status] of attempts) expect(status, `${name}: ${what}`).toBe(403);
    }
    expect(await rawCount(api, admin, [b1.id], [day]), 'a refused request still wrote punches').toBe(punchesBefore);
  });

  // ── tenant isolation ───────────────────────────────────────────────────────────────────────────

  test('an HR login of tenant A cannot import, mass-enter or process an employee of tenant B (and the reverse)', async () => {
    const [a1] = pools.apiImport;
    const [b1] = pools.apiBulk;
    const day = bulkDays[0];
    const beforePunches = (await rawPunches(api, tenantB, other.id, day)).length;
    const bDaily = async () => ((await (await send(api, 'GET', `/api/attendance/daily?from=${bulkDays[0]}&to=${bulkDays[1]}&pageSize=100`, tenantB)).json()) as DailyPage).total;
    const beforeDaily = await bDaily();

    // Import by code: a row error that reads like an unknown code, in the dry run and for real.
    const file = { fileName: 'cross-tenant.csv', csvContent: csvFile(csvRow(other.code, day, `07:${mm}:${ss}`, 'In')) };
    for (const url of ['/api/attendance/events/import-preview', '/api/attendance/events/import']) {
      const response = await send(api, 'POST', url, hr, file);
      expect(response.status(), url).toBe(200);
      const body = await response.json() as ImportResult;
      expect(body.validRows, url).toBe(0);
      expect(body.failedRows, url).toBe(1);
      expect(body.errors[0].message, url).toContain(UNKNOWN_CODE_MESSAGE_START);
    }

    // Mass entry by id: refused outright (403) or refused per item (nothing created). Never written.
    const bulk = await send(api, 'POST', '/api/attendance/events/bulk-entry', hr, {
      employeeIds: [other.id], fromDate: day, toDate: day, inTime: `07:${mm}`, outTime: `08:${mm}`, reason: 'e2e cross-tenant check',
    });
    if (bulk.status() !== 403) {
      expect(bulk.status()).toBe(200);
      const body = await bulk.json() as BulkResult;
      expect(body.created, 'a punch was written for another tenant\'s employee').toBe(0);
      expect(body.duplicates).toBe(0);
      expect(body.failed).toBe(body.requested);
    }

    // Processing by id, synchronously and as a job: refused, or a no-op.
    const sync = await send(api, 'POST', '/api/attendance/process', hr, { fromDate: bulkDays[0], toDate: bulkDays[1], employeeIds: [other.id] });
    if (sync.status() !== 403) {
      expect(sync.status()).toBe(200);
      expect(await sync.json(), 'processed another tenant\'s employee').toBe(0);
    }
    const queued = await send(api, 'POST', '/api/attendance/process/async', hr, { fromDate: bulkDays[0], toDate: bulkDays[1], employeeIds: [other.id] });
    if (queued.status() !== 403) {
      expect(queued.status()).toBe(202);
      const job = await waitForJob(api, hr, ((await queued.json()) as { jobId: string }).jobId);
      expect(job.result?.employees ?? 0, 'the job processed another tenant\'s employee').toBe(0);
    }

    expect((await rawPunches(api, tenantB, other.id, day)).length, 'tenant B got a punch from tenant A').toBe(beforePunches);
    expect(await bDaily(), 'tenant B got daily records from tenant A\'s processing').toBe(beforeDaily);

    // The reverse: tenant B's admin naming tenant A's employee.
    const beforeA = (await rawPunches(api, admin, b1.id, day)).length;
    const reverse = await send(api, 'POST', '/api/attendance/events/import', tenantB, {
      fileName: 'reverse.csv', csvContent: csvFile(csvRow(a1.code, day, `07:${mm}:${ss}`, 'In')),
    });
    expect(reverse.status()).toBe(200);
    const reverseBody = await reverse.json() as ImportResult;
    expect(reverseBody.validRows).toBe(0);
    expect(reverseBody.errors[0].message).toContain(UNKNOWN_CODE_MESSAGE_START);
    const reverseBulk = await send(api, 'POST', '/api/attendance/events/bulk-entry', tenantB, {
      employeeIds: [b1.id], fromDate: day, toDate: day, inTime: `07:${mm}`, reason: 'e2e cross-tenant check',
    });
    if (reverseBulk.status() !== 403) expect((await reverseBulk.json() as BulkResult).created).toBe(0);
    expect((await rawPunches(api, admin, b1.id, day)).length).toBe(beforeA);
  });

  // ── no self-attendance (I1) ────────────────────────────────────────────────────────────────────

  test('an HR login that is an employee cannot record its OWN attendance by file or by mass entry; the rest proceeds', async () => {
    const a2 = pools.apiImport[1];
    const [, b2] = pools.apiBulk;
    const me = await linkedHrSession(api, pools.linkCandidate);
    expect(me.employeeId).toBe(pools.linkCandidate.id);

    const times = { import: `12:${mm}:${ss}`, in: `13:${mm}`, out: `17:${mm}` };
    const [day] = await findFreeWindow(api, hr, 1, (d) => [
      csvRow(a2.code, d, times.import, 'In'), csvRow(b2.code, d, `${times.in}:00`, 'In'), csvRow(b2.code, d, `${times.out}:00`, 'Out'),
    ]);
    const file = { fileName: 'self.csv', csvContent: csvFile(csvRow(pools.linkCandidate.code, day, times.import, 'In'), csvRow(a2.code, day, times.import, 'In')) };
    const selfBefore = await rawCount(api, admin, [me.employeeId], [day]);
    const a2Before = await rawCount(api, admin, [a2.id], [day]);

    const preview = await send(api, 'POST', '/api/attendance/events/import-preview', me.token, file);
    expect(preview.status(), 'a row for yourself is a ROW error, not a whole-file 403').toBe(200);
    const judged = await preview.json() as ImportResult;
    expect(judged.totalRows).toBe(2);
    expect(judged.validRows).toBe(1);
    expect(judged.failedRows).toBe(1);
    expect(judged.errors[0].rowNumber).toBe(2);
    expect(judged.errors[0].message).toMatch(/own employee record/i);

    const committed = await send(api, 'POST', '/api/attendance/events/import', me.token, file);
    expect(committed.status()).toBe(200);
    expect((await committed.json() as ImportResult).importedRows).toBe(1);
    expect(await rawCount(api, admin, [me.employeeId], [day]), 'a punch was written for the caller\'s own record').toBe(selfBefore);
    expect(await rawCount(api, admin, [a2.id], [day])).toBe(a2Before + 1);

    const bulk = await send(api, 'POST', '/api/attendance/events/bulk-entry', me.token, {
      employeeIds: [me.employeeId, b2.id], fromDate: day, toDate: day, inTime: times.in, outTime: times.out, reason: 'e2e self-attendance check',
    });
    expect(bulk.status(), await bulk.text()).toBe(200);
    const body = await bulk.json() as BulkResult;
    expect(body.requested).toBe(4);
    expect(body.created, 'only the colleague\'s In and Out').toBe(2);
    expect(body.failed, 'the caller\'s own In and Out are refused').toBe(2);
    expect(body.errors).toHaveLength(1);
    expect(body.errors[0].employeeCode).toBe(pools.linkCandidate.code);
    expect(body.errors[0].message).toMatch(/own employee record/i);
    expect(await rawCount(api, admin, [me.employeeId], [day]), 'a punch was written for the caller\'s own record').toBe(selfBefore);
  });
});
