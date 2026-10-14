'use client';

import { useCallback, useEffect, useRef } from 'react';
import { useCompanyChangeGuard } from './useCompanyChangeGuard';
import { useT } from './useT';

/** One guard for Setup's buttons, sidebar links, company selection and history. */
export function useSetupNavigationGuard(dirty: boolean, clear: () => void, pendingFiles = false, companyId: string | null = null) {
  const t = useT();
  const approvedCompany = useRef<{ id: string | null } | null>(null);
  const confirmLeave = useCallback(() => {
    if (!dirty) return true;
    if (!window.confirm(t('Leave this form and discard unsaved changes?'))) return false;
    clear();
    return true;
  }, [dirty, clear, t]);
  const confirmDeparture = useCallback(() => {
    if (!pendingFiles) return confirmLeave();
    if (!window.confirm(t('Selected files have not been uploaded. Leave and discard these files and any unsaved changes?'))) return false;
    clear();
    return true;
  }, [pendingFiles, confirmLeave, clear, t]);
  const confirmDestination = useCallback((href: string) => {
    const target = new URL(href, location.href);
    const targetCompany = target.searchParams.get('companyId');
    if (target.pathname === '/setup' && targetCompany === companyId) return confirmLeave();
    const accepted = confirmDeparture();
    if (accepted && target.pathname === '/setup') approvedCompany.current = { id: targetCompany };
    return accepted;
  }, [companyId, confirmLeave, confirmDeparture]);
  useCompanyChangeGuard(useCallback((targetCompany?: string | null) => {
    const approved = approvedCompany.current;
    approvedCompany.current = null;
    // A history/link decision already authorized this exact URL's scope change.
    // A later unrelated top-bar choice must never reuse that decision.
    if (approved && approved.id === targetCompany && location.pathname === '/setup'
      && new URLSearchParams(location.search).get('companyId') === targetCompany) return true;
    return confirmDeparture();
  }, [confirmDeparture]));
  const current = useRef({ dirty: dirty || pendingFiles, confirmDestination });
  current.current = { dirty: dirty || pendingFiles, confirmDestination };
  useEffect(() => {
    const history = window.history;
    const key = '__kynexSetupPosition';
    let position: number | null = typeof history.state?.[key] === 'number' ? history.state[key] : null;
    let restoring = false;
    let traverseConfirmed = false;
    const historyWritten = (event: Event) => { position = (event as CustomEvent<number>).detail; };
    const pop = (event: PopStateEvent) => {
      if (restoring) { restoring = false; return false; }
      const next = typeof event.state?.[key] === 'number' ? event.state[key] : null;
      // The bootstrap tags every same-document entry, including before Setup.
      // Never guess a delta for an entry created outside that bookkeeping.
      // Cross-document departures use the native beforeunload guard instead.
      if (next === null || position === null) { position = next; traverseConfirmed = false; return true; }
      if (next !== position && !traverseConfirmed && !current.current.confirmDestination(location.href)) {
        restoring = true;
        history.go(position - next);
        return false;
      }
      traverseConfirmed = false;
      position = next;
      return true;
    };
    const beforeHistory = (event: Event) => { if (!pop((event as CustomEvent<PopStateEvent>).detail)) event.preventDefault(); };
    // Where available, cancel a history traversal before the browser changes URL
    // or restores page state. popstate remains the fallback for older browsers.
    const navigation = (window as Window & { navigation?: EventTarget }).navigation;
    const beforeTraverse = (event: Event) => {
      const traversal = event as Event & { navigationType?: string; destination?: { url: string } };
      if (traversal.navigationType !== 'traverse' || !event.cancelable || !traversal.destination) return;
      if (!current.current.confirmDestination(traversal.destination.url)) event.preventDefault();
      else traverseConfirmed = true;
    };
    const beforeNavigation = (event: Event) => { if (!current.current.confirmDestination((event as CustomEvent<{ path: string }>).detail.path)) event.preventDefault(); };
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (current.current.dirty) { event.preventDefault(); event.returnValue = ''; }
    };
    const beforeLink = (event: MouseEvent) => {
      const link = (event.target as Element)?.closest?.('a[href]') as HTMLAnchorElement | null;
      if (!link || link.target === '_blank' || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || link.href === location.href) return;
      if (!current.current.confirmDestination(link.href)) { event.preventDefault(); event.stopPropagation(); }
    };
    window.addEventListener('kynexone:setup-history', beforeHistory);
    window.addEventListener('kynexone:history-write', historyWritten);
    navigation?.addEventListener('navigate', beforeTraverse);
    window.addEventListener('kynexone:before-navigation', beforeNavigation);
    window.addEventListener('beforeunload', beforeUnload);
    document.addEventListener('click', beforeLink, true);
    return () => {
      window.removeEventListener('kynexone:setup-history', beforeHistory);
      window.removeEventListener('kynexone:history-write', historyWritten);
      navigation?.removeEventListener('navigate', beforeTraverse);
      window.removeEventListener('kynexone:before-navigation', beforeNavigation);
      window.removeEventListener('beforeunload', beforeUnload);
      document.removeEventListener('click', beforeLink, true);
    };
  }, []);
  return confirmLeave;
}
