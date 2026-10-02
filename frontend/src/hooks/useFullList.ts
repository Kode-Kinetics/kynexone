'use client';

/**
 * Every row of a list the user acts on (an expiry register, a renewal queue), read page by page
 * through an `fetchAllPages`-based loader. The sibling of usePagedList, for lists where a window
 * would hide work: a visa that expires next week must not sit on page 2.
 *
 * `fetchAll` is read from a ref, so `reload` always runs the current filters. Responses go through
 * the latest-request gate: a slow load for an old filter cannot land after, and replace, the load
 * for the filter now selected. A failure clears the rows and keeps the error, so the screen says
 * the list could not be loaded instead of showing an empty or partial one.
 */

import { useCallback, useMemo, useRef, useState } from 'react';
import { createLatestRequestGate, runLatest } from '../lib/latestRequest';

export interface FullList<T> {
  items: T[];
  loading: boolean;
  error: unknown;
  reload: () => Promise<unknown>;
}

export function useFullList<T>(fetchAll: () => Promise<T[]>): FullList<T> {
  const [items, setItems] = useState<T[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const fetchRef = useRef(fetchAll);
  fetchRef.current = fetchAll;
  const gate = useMemo(() => createLatestRequestGate(), []);

  const reload = useCallback(() => {
    setLoading(true);
    setError(null);
    return runLatest(gate, () => fetchRef.current(), {
      onResult: (rows) => setItems(rows),
      onError: (err) => { setItems([]); setError(err); },
      onSettled: () => setLoading(false),
    });
  }, [gate]);

  return { items, loading, error, reload };
}
