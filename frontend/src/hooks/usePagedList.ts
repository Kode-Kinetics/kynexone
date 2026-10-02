'use client';

/**
 * One page at a time for a history list that can grow without bound (overtime requests,
 * timesheets): loading every row up front would be slow, and loading one page and calling it the
 * list is the silent truncation this replaces. The hook keeps the server's `total` so the screen
 * can say "Showing 100 of 1,234" and offer the next page (see ListWindowFooter).
 *
 * `fetchPage` is read from a ref, so `reload` always runs the current filters. Responses go
 * through the latest-request gate: a reload started after a slow "load more" wins, and the late
 * page is dropped instead of being appended to the new list.
 */

import { useCallback, useMemo, useRef, useState } from 'react';
import { createLatestRequestGate, runLatest } from '../lib/latestRequest';
import { API_MAX_PAGE_SIZE } from '../lib/paging';

export interface PagedListPage<T> {
  items: T[];
  total: number;
}

export interface PagedList<T> {
  items: T[];
  /** Server row count across all pages; null until the first page arrives. */
  total: number | null;
  loading: boolean;
  loadingMore: boolean;
  error: unknown;
  reload: () => Promise<unknown>;
  loadMore: () => Promise<unknown>;
}

export function usePagedList<T>(
  fetchPage: (page: number, pageSize: number) => Promise<PagedListPage<T>>,
  pageSize = API_MAX_PAGE_SIZE,
): PagedList<T> {
  const [items, setItems] = useState<T[]>([]);
  const [total, setTotal] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const pageRef = useRef(0);
  const fetchRef = useRef(fetchPage);
  fetchRef.current = fetchPage;
  const gate = useMemo(() => createLatestRequestGate(), []);

  const reload = useCallback(() => {
    setLoading(true);
    setLoadingMore(false);
    setError(null);
    return runLatest(gate, () => fetchRef.current(1, pageSize), {
      onResult: (page) => { pageRef.current = 1; setItems(page.items); setTotal(page.total); },
      onError: (err) => { setItems([]); setTotal(null); setError(err); },
      onSettled: () => setLoading(false),
    });
  }, [gate, pageSize]);

  const loadMore = useCallback(() => {
    const next = pageRef.current + 1;
    setLoadingMore(true);
    setError(null);
    return runLatest(gate, () => fetchRef.current(next, pageSize), {
      onResult: (page) => { pageRef.current = next; setItems((prev) => [...prev, ...page.items]); setTotal(page.total); },
      onError: (err) => setError(err),
      onSettled: () => setLoadingMore(false),
    });
  }, [gate, pageSize]);

  return { items, total, loading, loadingMore, error, reload, loadMore };
}
