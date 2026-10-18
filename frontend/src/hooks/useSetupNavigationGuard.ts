'use client';

import { useCallback, useRef } from 'react';
import { useNavigationGuard } from '../contexts/NavigationGuardContext';
import { useNavigationBlocker } from './useNavigationBlocker';
import { useT } from './useT';

export interface SetupNavigationGuard {
  (): boolean;
  (destination: string, action: () => void | Promise<void>): boolean;
}

/** Setup contributes draft state to the application guard; it does not intercept global navigation. */
export function useSetupNavigationGuard(dirty: boolean, clear: () => void, pendingFiles = false, companyId: string | null = null): SetupNavigationGuard {
  const t = useT();
  const navigationGuard = useNavigationGuard();
  const replayApproved = useRef(false);
  const blocked = dirty || pendingFiles;
  const message = pendingFiles
    ? t('Selected files have not been uploaded. Leave and discard these files and any unsaved changes?')
    : t('Leave this form and discard unsaved changes?');

  useNavigationBlocker({
    id: `setup:${companyId ?? 'tenant'}`,
    when: blocked,
    message,
    onDiscard: clear,
  });

  return useCallback((destination?: string, action?: () => void | Promise<void>) => {
    if (!blocked) {
      if (action) void action();
      return true;
    }
    if (replayApproved.current) {
      replayApproved.current = false;
      return true;
    }
    if (destination && action) {
      navigationGuard.run(destination, action);
      return false;
    }

    // Compatibility for the existing `if (!confirmLeave()) return` Setup callers.
    // Their initiating button is replayed once after async discard; the ref lets
    // that replay pass through synchronously without a second prompt.
    const trigger = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    navigationGuard.run(`setup-action:${companyId ?? 'tenant'}`, () => {
      if (!trigger) throw new Error(t('Navigation could not be resumed. Try the action again.'));
      replayApproved.current = true;
      trigger.click();
    });
    return false;
  }, [blocked, companyId, navigationGuard, t]);
}
