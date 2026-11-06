'use client';

import { useCallback, useRef, useState } from 'react';
import { createInFlight } from '../lib/inFlight';

/**
 * Per-row busy state for async actions: `run('approve:42', () => api.approve(42))` ignores a second call for the
 * same key while the first is in flight, and `isBusy('approve:42')` disables the button and sets aria-busy.
 */
export function useBusyKeys() {
  const guard = useRef(createInFlight());
  const [busy, setBusy] = useState<ReadonlySet<string>>(() => new Set());
  const sync = useCallback(() => setBusy(new Set(guard.current.keys())), []);
  const run = useCallback(<T,>(key: string, action: () => Promise<T>) => guard.current.run(key, action, sync), [sync]);
  const isBusy = useCallback((...keys: string[]) => keys.some(key => busy.has(key)), [busy]);
  return { run, isBusy, anyBusy: busy.size > 0 };
}
