import { test, expect } from '@playwright/test';
import { API_MAX_PAGE_SIZE, collectAllPages, pageItems, pageTotal, type ListPage } from '../e2e/paging';

// A fake list endpoint that behaves like /api/employees: pageSize is clamped to 1..100, and the
// response carries the full `total` whatever page size was asked for.
function cappedEndpoint(rowCount: number, { withTotal = true } = {}) {
  const rows = Array.from({ length: rowCount }, (_, i) => `E${i + 1}`);
  const calls: Array<{ page: number; pageSize: number }> = [];
  const fetchPage = async (page: number, requested: number): Promise<ListPage<string>> => {
    calls.push({ page, pageSize: requested });
    const pageSize = Math.min(Math.max(requested, 1), 100);
    const items = rows.slice((page - 1) * pageSize, page * pageSize);
    return { items, total: withTotal ? rows.length : null };
  };
  return { rows, calls, fetchPage };
}

test('the defect: one pageSize=200 request against a 250-row list returns only 100', async () => {
  const { fetchPage } = cappedEndpoint(250);
  const { items, total } = await fetchPage(1, 200);
  expect(items).toHaveLength(100);
  expect(total).toBe(250);
});

test('collects all 250 rows, in order, with no duplicates, in three requests of 100', async () => {
  const { rows, calls, fetchPage } = cappedEndpoint(250);
  const all = await collectAllPages(fetchPage);
  expect(all).toEqual(rows);
  expect(new Set(all).size).toBe(250);
  expect(calls).toEqual([
    { page: 1, pageSize: API_MAX_PAGE_SIZE },
    { page: 2, pageSize: API_MAX_PAGE_SIZE },
    { page: 3, pageSize: API_MAX_PAGE_SIZE },
  ]);
});

test('an exact multiple of the page size stops on the total, not on an extra empty request', async () => {
  const { calls, fetchPage } = cappedEndpoint(200);
  expect(await collectAllPages(fetchPage)).toHaveLength(200);
  expect(calls).toHaveLength(2);
});

test('an empty list is one request and no rows', async () => {
  const { calls, fetchPage } = cappedEndpoint(0);
  expect(await collectAllPages(fetchPage)).toEqual([]);
  expect(calls).toHaveLength(1);
});

test('without a total it pages until a short page', async () => {
  const { calls, fetchPage } = cappedEndpoint(250, { withTotal: false });
  expect(await collectAllPages(fetchPage)).toHaveLength(250);
  expect(calls).toHaveLength(3);
});

test('without a total, an exact multiple ends on the empty page after it', async () => {
  const { calls, fetchPage } = cappedEndpoint(200, { withTotal: false });
  expect(await collectAllPages(fetchPage)).toHaveLength(200);
  expect(calls).toHaveLength(3);
});

test('an endpoint that ignores `page` fails loudly instead of looping forever', async () => {
  const stuck = async (): Promise<ListPage<number>> => ({ items: [1, 2, 3], total: null });
  await expect(collectAllPages(stuck, { pageSize: 3, maxPages: 5 })).rejects.toThrow(/after 5 pages/);
});

test('pageItems and pageTotal read every response shape the API uses', () => {
  expect(pageItems([1, 2])).toEqual([1, 2]);
  expect(pageItems({ items: [1] })).toEqual([1]);
  expect(pageItems({ Items: [2] })).toEqual([2]);
  expect(pageItems(null)).toEqual([]);
  expect(pageTotal({ total: 250 })).toBe(250);
  expect(pageTotal({ Total: 7 })).toBe(7);
  expect(pageTotal({ items: [] })).toBeNull();
  expect(pageTotal({ total: '250' })).toBeNull();
});
