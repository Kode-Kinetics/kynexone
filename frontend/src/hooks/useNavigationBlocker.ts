'use client';

import { useEffect, useRef } from 'react';
import { useNavigationGuard, type NavigationBlocker } from '../contexts/NavigationGuardContext';

export function useNavigationBlocker(blocker: NavigationBlocker) {
  const guard = useNavigationGuard();
  const current = useRef(blocker);
  current.current = blocker;

  useEffect(() => guard.register({
    id: blocker.id,
    get when() { return current.current.when; },
    get message() { return current.current.message; },
    onDiscard: () => current.current.onDiscard(),
  }), [blocker.id, guard]);
}
