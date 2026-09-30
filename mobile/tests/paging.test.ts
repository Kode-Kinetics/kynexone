import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { API_MAX_PAGE_SIZE, fetchAllPages } from '../src/api/paging.ts';

// services.ts imports React Native modules, so its request URLs are checked as source.
const services = fs.readFileSync(new URL('../src/api/services.ts', import.meta.url), 'utf8');

/** Clamps pageSize like EmployeeManagementService and echoes page like PagedResult. */
function clampingServer(rowCount: number, cap = 100) {
  const rows = Array.from({ length: rowCount }, (_, i) => ({ id: i + 1 }));
  const calls: number[] = [];
  const fetchPage = async (page: number, pageSize: number) => {
    calls.push(page);
    const size = Math.min(pageSize, cap);
    return { items: rows.slice((page - 1) * size, page * size), total: rows.length, page };
  };
  return { rows, calls, fetchPage };
}

test('the bug this replaces: one pageSize=200 request to a clamping endpoint returns 100 rows', async () => {
  const server = clampingServer(250);
  const first = await server.fetchPage(1, 200);
  assert.equal(first.items.length, 100);
  assert.equal(first.total, 250);
});

test('every row of a 250-row list is read, in 100-row pages', async () => {
  const server = clampingServer(250);
  assert.deepEqual(await fetchAllPages(server.fetchPage), server.rows);
  assert.deepEqual(server.calls, [1, 2, 3]);
  assert.equal(API_MAX_PAGE_SIZE, 100);
});

test('rows under a data key, and a list without a total, are read to the end', async () => {
  const rows = Array.from({ length: 130 }, (_, i) => i);
  const all = await fetchAllPages(async (page, pageSize) => ({ data: rows.slice((page - 1) * pageSize, page * pageSize) }));
  assert.deepEqual(all, rows);
});

test('an endpoint that ignores page, or a list past the page limit, fails instead of returning part of it', async () => {
  const ignoresPage = async () => ({ items: Array.from({ length: 100 }, (_, i) => i), total: 250, page: 1 });
  await assert.rejects(fetchAllPages(ignoresPage), /page 1 when page 2/);
  await assert.rejects(fetchAllPages(clampingServer(500).fetchPage, { maxPages: 3 }), /more than 300 rows/);
});

test('a newest-first list stops once a page reaches rows the caller does not need', async () => {
  // 150 requests this month, then older history.
  const rows = [
    ...Array.from({ length: 150 }, () => ({ workDate: '2026-09-15' })),
    ...Array.from({ length: 400 }, () => ({ workDate: '2026-08-10' })),
  ];
  const calls: number[] = [];
  const all = await fetchAllPages(
    async (page, pageSize) => {
      calls.push(page);
      return { items: rows.slice((page - 1) * pageSize, page * pageSize), total: rows.length, page };
    },
    { enough: (items) => String(items[items.length - 1]?.workDate ?? '') < '2026-09' }
  );
  assert.deepEqual(calls, [1, 2]);
  assert.equal(all.filter((r) => r.workDate.startsWith('2026-09')).length, 150);
});

test('a request that reads one fixed-size page is for a list known to fit in it', () => {
  // A literal pageSize reads that page and nothing more. Each one must be a list bounded by what it
  // asks for; anything else pages through fetchAllPages. My overtime history used to be one of
  // these (`page=1&pageSize=50`) and stopped at its 50th request.
  const singlePage = [...services.matchAll(/`([^`]*pageSize=\d+[^`]*)`/g)].map((m) => m[1]);
  assert.deepEqual(singlePage, [
    // One employee's raw punches for one day.
    '/attendance/events/raw?from=${today}&to=${today}&employeeId=${employeeId}&pageSize=100',
    // One employee's attendance days for one calendar month (at most 31 rows).
    '/attendance/daily?from=${from}&to=${to}&employeeId=${employeeId}&page=1&pageSize=62',
  ]);
});

test('my overtime history reads every page of my requests', () => {
  const start = services.indexOf('async getMyOTRequests');
  assert.ok(start > 0);
  const body = services.slice(start, services.indexOf('\n  },', start));
  assert.match(body, /fetchAllPages<any>\(\(page, pageSize\) =>/);
  assert.match(body, /\/overtime\/requests\?employeeId=\$\{employeeId\}&page=\$\{page\}&pageSize=\$\{pageSize\}/);
});

test('an employee with 130 overtime requests sees all 130, not the first page', async () => {
  // The shape getMyOTRequests now reads: 100-row pages until the server total is reached.
  const server = clampingServer(130);
  assert.equal((await server.fetchPage(1, 50)).items.length, 50);
  assert.equal((await fetchAllPages(server.fetchPage)).length, 130);
});
