'use client';

import { useCallback, useEffect, useRef } from 'react';
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
  const activationTarget = useRef<HTMLElement | null>(null);
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

  useEffect(() => {
    const capture = (event: Event) => {
      const element = event.target instanceof Element
        ? event.target.closest<HTMLElement>('button:not([disabled]), a[href], input[type="button"]:not([disabled]), input[type="submit"]:not([disabled]), [role="button"]')
        : null;
      if (!element || !element.isConnected || element === document.body) return;
      activationTarget.current = element;
      window.setTimeout(() => {
        if (activationTarget.current === element) activationTarget.current = null;
      }, 0);
    };
    document.addEventListener('pointerdown', capture, true);
    document.addEventListener('click', capture, true);
    return () => {
      document.removeEventListener('pointerdown', capture, true);
      document.removeEventListener('click', capture, true);
    };
  }, []);

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

    // Temporary UI-only compatibility for existing `if (!confirmLeave()) return`
    // callers. The real pointer/click activation is captured during event dispatch;
    // programmatic no-argument calls intentionally have no resumable target.
    const trigger = activationTarget.current;
    activationTarget.current = null;
    navigationGuard.run(`setup-action:${companyId ?? 'tenant'}`, () => {
      if (!trigger?.isConnected || trigger === document.body) throw new Error(t('Navigation could not be resumed. Try the action again.'));
      replayApproved.current = true;
      trigger.click();
    }, trigger);
    return false;
  }, [blocked, companyId, navigationGuard, t]);
}
