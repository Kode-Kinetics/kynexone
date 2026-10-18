'use client';

import { useCallback, useMemo } from 'react';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import {
  buildPeopleReturnTo,
  parsePeopleDirectoryState,
  serializePeopleDirectoryState,
  type PeopleDirectoryState,
} from '../lib/peopleRoutes';

interface DirectoryUpdateOptions {
  history?: 'push' | 'replace';
}

export interface PeopleDirectoryQuery {
  state: PeopleDirectoryState;
  returnTo: string;
  update(patch: Partial<PeopleDirectoryState>, options?: DirectoryUpdateOptions): void;
}

export function usePeopleDirectoryQuery(): PeopleDirectoryQuery {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const serialized = searchParams.toString();
  const state = useMemo(
    () => parsePeopleDirectoryState(new URLSearchParams(serialized)),
    [serialized],
  );

  const update = useCallback((patch: Partial<PeopleDirectoryState>, options: DirectoryUpdateOptions = {}) => {
    const next: PeopleDirectoryState = { ...state, ...patch };
    for (const [key, value] of Object.entries(next)) {
      if (value === undefined || value === '' || (key === 'page' && value === 1) || (key === 'view' && value === 'current')) {
        delete next[key as keyof PeopleDirectoryState];
      }
    }
    const query = serializePeopleDirectoryState(next).toString();
    const href = query ? `${pathname}?${query}` : pathname;
    if (options.history === 'push') router.push(href, { scroll: false });
    else router.replace(href, { scroll: false });
  }, [pathname, router, state]);

  return {
    state,
    returnTo: buildPeopleReturnTo(state),
    update,
  };
}
