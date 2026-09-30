/**
 * Reading a paged API list without silently dropping rows.
 *
 * The employee list clamps pageSize to 100, so `/employees?pageSize=200` returned the first 100
 * employees and the team screen's manager filter never saw anyone past them. Overtime requests
 * and daily attendance apply no cap, so a request for 200 simply stopped at 200. Callers that need
 * the whole list page through it here; the loop throws rather than return part of a list.
 *
 * Kept free of imports so the node test runner can load it directly (tests/paging.test.ts).
 */

export const API_MAX_PAGE_SIZE = 100;

export type PageOf<T> = { items?: T[]; data?: T[]; total?: number | null; page?: number | null };

export type FetchAllOptions<T> = {
  pageSize?: number;
  maxPages?: number;
  /**
   * Stop early once a page shows nothing further is needed, e.g. a list ordered newest first has
   * reached rows older than the period being totalled. The page that satisfied it is kept.
   */
  enough?: (pageItems: T[]) => boolean;
};

export async function fetchAllPages<T>(
  fetchPage: (page: number, pageSize: number) => Promise<PageOf<T> | null | undefined>,
  { pageSize = API_MAX_PAGE_SIZE, maxPages = 200, enough }: FetchAllOptions<T> = {}
): Promise<T[]> {
  const all: T[] = [];
  for (let page = 1; page <= maxPages; page++) {
    const result = await fetchPage(page, pageSize);
    if (result && typeof result.page === 'number' && result.page !== page) {
      throw new Error(`The list endpoint returned page ${result.page} when page ${page} was requested.`);
    }
    const items = result?.items ?? result?.data ?? [];
    all.push(...items);
    if (items.length === 0) return all;
    const total = typeof result?.total === 'number' && Number.isFinite(result.total) ? result.total : null;
    if (total !== null ? all.length >= total : items.length < pageSize) return all;
    if (enough?.(items)) return all;
  }
  throw new Error(`The list has more than ${maxPages * pageSize} rows; it was not loaded rather than shown in part.`);
}
