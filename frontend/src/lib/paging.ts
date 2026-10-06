/**
 * Reading paged API lists without silently dropping rows.
 *
 * List endpoints clamp `pageSize` to 1..100 (a few history lists to 1..200) and answer a larger
 * request with 100 rows and a `total` above 100, not an error. Payroll slips and payslips and
 * overtime requests used to apply no cap, so a screen asking for 200 simply stopped at 200. Either
 * way a screen that took one page as "the list" lost every row past it with nothing on screen to
 * say so: a payroll register missing its 201st employee, a lookup without its 101st company.
 *
 * A screen that needs the whole list pages through it with `fetchAllPages`. A history list that
 * can grow without bound shows one page at a time and says how many rows exist
 * (`pageWindowText`), so it is never mistaken for the whole list.
 */

import { requirePage } from './listResponse';

export const API_MAX_PAGE_SIZE = 100;

export interface PageOf<T> {
  items: readonly T[] | T[];
  /** The server's row count across all pages, when it sends one. */
  total?: number | null;
  /** The page the server says it returned, when it echoes one. */
  page?: number | null;
}

/**
 * Fetch pages 1, 2, 3, … until the list is exhausted. The loop stops when a page comes back
 * empty, when `total` is known and that many rows have arrived, or (with no `total`) when a page
 * comes back short. It throws rather than return a partial list when the endpoint answers with a
 * different page than was asked for (it is ignoring `page`, so every "page" would repeat the
 * first) or when rows are still arriving after `maxPages`.
 */
export async function fetchAllPages<T>(
  fetchPage: (page: number, pageSize: number) => Promise<PageOf<T>>,
  { pageSize = API_MAX_PAGE_SIZE, maxPages = 200 }: { pageSize?: number; maxPages?: number } = {},
): Promise<T[]> {
  const all: T[] = [];
  for (let page = 1; page <= maxPages; page++) {
    // A reply that is not `{ items: [...] }` (a proxy's HTML page, null) is a failed load, never
    // "the list is empty" — `result.items ?? []` used to end the loop with zero rows.
    const result = requirePage<PageOf<T>>(await fetchPage(page, pageSize));
    if (typeof result.page === 'number' && result.page !== page) {
      throw new Error(`The list endpoint returned page ${result.page} when page ${page} was requested.`);
    }
    const items = result.items;
    all.push(...items);
    if (items.length === 0) return all;
    const total = typeof result.total === 'number' && Number.isFinite(result.total) ? result.total : null;
    if (total !== null ? all.length >= total : items.length < pageSize) return all;
  }
  throw new Error(`The list has more than ${maxPages * pageSize} rows; it was not loaded rather than shown in part.`);
}

/**
 * "Showing 100 of 1,234 requests" when a list on screen is one page of a longer one; null when
 * everything is shown. The count comes from the server's `total`, never from the rows loaded.
 */
export function pageWindowText(shown: number, total: number | null | undefined, noun: string): string | null {
  if (typeof total !== 'number' || !Number.isFinite(total) || shown >= total) return null;
  return `Showing ${shown.toLocaleString('en-US')} of ${total.toLocaleString('en-US')} ${noun}`;
}
