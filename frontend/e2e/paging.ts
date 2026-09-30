/**
 * Read a whole paged list, one API-sized page at a time.
 *
 * The API caps list pages at 100: `EmployeesController` / `EmployeeManagementService` clamp
 * `pageSize` to 1..100. A request for `pageSize=200` does not fail. It returns 100 rows and a
 * `total` above 100. A fixture or assertion that assumed one big page therefore stopped at row 100
 * with nothing to say so: employees past 100 were never activated or given salaries, and an
 * isolation check never looked at them.
 *
 * Every "give me the whole list" call in the e2e tree goes through `collectAllPages`.
 */

export const API_MAX_PAGE_SIZE = 100;

export interface ListPage<T> {
  items: T[];
  /** The server's total row count, or null if the response did not carry one. */
  total: number | null;
}

/** Rows from a list response: a bare array, `{ items }`, or `{ Items }`. */
export function pageItems(body: any): any[] {
  return Array.isArray(body) ? body
    : Array.isArray(body?.items) ? body.items
      : Array.isArray(body?.Items) ? body.Items : [];
}

/** `total` from a `PagedResult`, or null if the response did not carry a number. */
export function pageTotal(body: any): number | null {
  const t = body?.total ?? body?.Total;
  return typeof t === 'number' && Number.isFinite(t) ? t : null;
}

/**
 * Fetch pages 1, 2, 3, … until the list is exhausted. The loop stops when:
 *  - a page comes back empty;
 *  - `total` is known and that many rows have been collected; or
 *  - `total` is unknown and a page comes back short.
 * If rows are still arriving after `maxPages`, it throws instead of looping. An endpoint that
 * ignores `page` would otherwise repeat page 1 forever.
 */
export async function collectAllPages<T>(
  fetchPage: (page: number, pageSize: number) => Promise<ListPage<T>>,
  { pageSize = API_MAX_PAGE_SIZE, maxPages = 1000 }: { pageSize?: number; maxPages?: number } = {},
): Promise<T[]> {
  const all: T[] = [];
  for (let page = 1; page <= maxPages; page++) {
    const { items, total } = await fetchPage(page, pageSize);
    all.push(...items);
    if (items.length === 0) return all;
    if (total !== null ? all.length >= total : items.length < pageSize) return all;
  }
  throw new Error(
    `collectAllPages: still receiving rows after ${maxPages} pages of ${pageSize}. `
    + 'The endpoint is probably ignoring the page parameter.',
  );
}
