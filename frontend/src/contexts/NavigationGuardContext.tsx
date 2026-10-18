'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import { ConfirmDialog, type ConfirmState } from '../components/ui/ConfirmDialog';
import { useLocale } from './LocaleContext';
import {
  cancelIntent,
  confirmIntent,
  createNavigationGuardState,
  queueIntent,
  type NavigationGuardSnapshot,
  type NavigationIntent,
} from '../lib/navigationGuardState';

interface NavigationBlocker {
  id: string;
  when: boolean;
  message: string;
  onDiscard: () => void | Promise<void>;
}

interface NavigationGuardValue {
  request(intent: NavigationIntent): void;
  push(href: string, restoreFocus?: HTMLElement | null): void;
  replace(href: string, restoreFocus?: HTMLElement | null): void;
  run(destination: string, action: () => void | Promise<void>, restoreFocus?: HTMLElement | null): void;
  register(blocker: NavigationBlocker): () => void;
}

const NavigationGuardContext = createContext<NavigationGuardValue | null>(null);
const HISTORY_POSITION = '__kynexNavigationPosition';

interface BrowserNavigation extends EventTarget {
  traverseTo(key: string): { finished: Promise<unknown> };
}

function isPlainLeftClick(event: MouseEvent) {
  return event.button === 0 && !event.defaultPrevented && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;
}

const guardCopy = {
  en: {
    title: 'Discard changes?', cancel: 'Cancel', confirm: 'Discard and leave', busy: 'Working…', close: 'Close',
    message: 'Leave this page and discard unsaved changes?', actionError: 'The action could not be completed.',
    navigationError: 'Navigation could not be completed. Try again.',
  },
  ar: {
    title: 'تجاهل التغييرات؟', cancel: 'إلغاء', confirm: 'تجاهل ومغادرة', busy: 'جارٍ التنفيذ…', close: 'إغلاق',
    message: 'هل تريد مغادرة هذه الصفحة وتجاهل التغييرات غير المحفوظة؟', actionError: 'تعذر إكمال الإجراء.',
    navigationError: 'تعذر إكمال الانتقال. حاول مرة أخرى.',
  },
} as const;

export function NavigationGuardProvider({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const { locale } = useLocale();
  const copy = locale === 'ar' ? guardCopy.ar : guardCopy.en;
  const machine = useRef(createNavigationGuardState()).current;
  const blockers = useRef(new Map<string, NavigationBlocker>());
  const [, setBlockerVersion] = useState(0);
  const [snapshot, setSnapshot] = useState<NavigationGuardSnapshot>(() => machine.snapshot());
  const syncSnapshot = useCallback(() => setSnapshot(machine.snapshot()), [machine]);

  useEffect(() => machine.subscribe(syncSnapshot), [machine, syncSnapshot]);

  const activeBlockers = useCallback(
    () => [...blockers.current.values()].filter((blocker) => blocker.when),
    [],
  );

  const request = useCallback((intent: NavigationIntent) => {
    if (machine.bypassDestination === intent.destination) {
      queueIntent(machine, intent);
      void intent.run();
      return;
    }
    if (activeBlockers().length === 0) {
      void intent.run();
      return;
    }
    queueIntent(machine, {
      ...intent,
      restoreFocus: intent.restoreFocus ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null),
    });
    syncSnapshot();
  }, [activeBlockers, machine, syncSnapshot]);

  const push = useCallback((href: string, restoreFocus?: HTMLElement | null) => {
    request({ destination: new URL(href, window.location.href).href, run: () => router.push(href), restoreFocus });
  }, [request, router]);

  const replace = useCallback((href: string, restoreFocus?: HTMLElement | null) => {
    request({ destination: new URL(href, window.location.href).href, run: () => router.replace(href), restoreFocus });
  }, [request, router]);

  const run = useCallback((destination: string, action: () => void | Promise<void>, restoreFocus?: HTMLElement | null) => {
    request({ destination, run: action, restoreFocus });
  }, [request]);

  const register = useCallback((blocker: NavigationBlocker) => {
    blockers.current.set(blocker.id, blocker);
    setBlockerVersion((version) => version + 1);
    return () => {
      if (blockers.current.get(blocker.id) === blocker) blockers.current.delete(blocker.id);
      setBlockerVersion((version) => version + 1);
    };
  }, []);

  useEffect(() => {
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (activeBlockers().length === 0) return;
      event.preventDefault();
      event.returnValue = '';
    };
    const click = (event: MouseEvent) => {
      if (!isPlainLeftClick(event)) return;
      const target = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>('a[href]') : null;
      if (!target || target.target === '_blank' || target.hasAttribute('download')) return;
      const url = new URL(target.href, window.location.href);
      if (url.origin !== window.location.origin || url.href === window.location.href) return;
      if (activeBlockers().length === 0) return;
      event.preventDefault();
      event.stopPropagation();
      request({ destination: url.href, run: () => router.push(`${url.pathname}${url.search}${url.hash}`), restoreFocus: target });
    };
    const adapter = (event: Event) => {
      const navigationEvent = event as CustomEvent<{ path: string }>;
      if (activeBlockers().length === 0) return;
      event.preventDefault();
      push(navigationEvent.detail.path);
    };
    window.addEventListener('beforeunload', beforeUnload);
    document.addEventListener('click', click, true);
    window.addEventListener('kynexone:before-navigation', adapter);
    return () => {
      window.removeEventListener('beforeunload', beforeUnload);
      document.removeEventListener('click', click, true);
      window.removeEventListener('kynexone:before-navigation', adapter);
    };
  }, [activeBlockers, push, request, router]);

  useEffect(() => {
    const history = window.history;
    const originalPush = history.pushState.bind(history);
    const originalReplace = history.replaceState.bind(history);
    let position = typeof history.state?.[HISTORY_POSITION] === 'number' ? history.state[HISTORY_POSITION] : 0;
    let restoringTo: number | null = null;
    let confirmedTraversal: { destination: string; resolve: () => void } | null = null;
    let restoredIntent: { destination: string; delta: number } | null = null;

    originalReplace({ ...history.state, [HISTORY_POSITION]: position }, '', window.location.href);
    history.pushState = (state, unused, url) => {
      position += 1;
      originalPush({ ...state, [HISTORY_POSITION]: position }, unused, url);
    };
    history.replaceState = (state, unused, url) => {
      originalReplace({ ...state, [HISTORY_POSITION]: position }, unused, url);
    };

    const navigation = (window as Window & { navigation?: BrowserNavigation }).navigation;
    const beforeTraverse = (event: Event) => {
      if (!navigation) return;
      const navigationApi: BrowserNavigation = navigation;
      const traversal = event as Event & { navigationType?: string; destination?: { key?: string; url?: string } };
      if (traversal.navigationType !== 'traverse' || !event.cancelable || !traversal.destination?.key || !traversal.destination.url) return;
      if (activeBlockers().length === 0 || machine.bypassDestination === traversal.destination.url) return;
      event.preventDefault();
      const destinationUrl = traversal.destination.url as string;
      const destinationKey = traversal.destination.key as string;
      request({ destination: destinationUrl, run: () => navigationApi.traverseTo(destinationKey).finished.then(() => {}) });
    };

    const pop = (event: PopStateEvent) => {
      const nextPosition = typeof event.state?.[HISTORY_POSITION] === 'number' ? event.state[HISTORY_POSITION] : null;
      if (nextPosition === null) return;
      if (restoringTo === nextPosition) {
        restoringTo = null;
        position = nextPosition;
        const pending = restoredIntent;
        restoredIntent = null;
        if (pending) request({
          destination: pending.destination,
          run: () => new Promise<void>((resolve) => {
            confirmedTraversal = { destination: pending.destination, resolve };
            history.go(pending.delta);
          }),
        });
        return;
      }
      const delta = nextPosition - position;
      if (delta === 0 || activeBlockers().length === 0 || machine.bypassDestination === window.location.href) {
        if (machine.bypassDestination === window.location.href) queueIntent(machine, { destination: window.location.href, run: () => {} });
        position = nextPosition;
        if (confirmedTraversal?.destination === window.location.href) {
          confirmedTraversal.resolve();
          confirmedTraversal = null;
        }
        return;
      }

      const destination = window.location.href;
      restoringTo = position;
      restoredIntent = { destination, delta };
      // Next may replace the newly reached entry while handling this same popstate.
      // Stamp that entry with its own position, then restore the previous one.
      position = nextPosition;
      history.go(-delta);
    };

    navigation?.addEventListener('navigate', beforeTraverse);
    window.addEventListener('popstate', pop);
    return () => {
      navigation?.removeEventListener('navigate', beforeTraverse);
      window.removeEventListener('popstate', pop);
      history.pushState = originalPush;
      history.replaceState = originalReplace;
    };
  }, [activeBlockers, machine, request]);

  const value = useMemo<NavigationGuardValue>(() => ({ request, push, replace, run, register }), [push, register, replace, request, run]);
  const displayedBlockers = activeBlockers();
  const dialogState: ConfirmState | null = snapshot.intent ? {
    title: copy.title,
    message: displayedBlockers[0]?.message ?? copy.message,
    cancelLabel: copy.cancel,
    confirmLabel: copy.confirm,
    busyLabel: copy.busy,
    closeLabel: copy.close,
    fallbackError: copy.actionError,
    busy: snapshot.busy,
    error: snapshot.error,
    resetKey: snapshot.sequence,
    closeOnConfirm: false,
    onConfirm: async () => {
      const confirmation = confirmIntent(
        machine,
        activeBlockers().map((blocker) => blocker.onDiscard),
        copy.navigationError,
        locale === 'ar' ? copy.navigationError : undefined,
      );
      syncSnapshot();
      try { await confirmation; } finally { syncSnapshot(); }
    },
  } : null;

  return (
    <NavigationGuardContext.Provider value={value}>
      {children}
      <ConfirmDialog state={dialogState} onCancel={() => {
        const restoreFocus = machine.pending?.restoreFocus;
        cancelIntent(machine);
        syncSnapshot();
        requestAnimationFrame(() => { if (restoreFocus?.isConnected) restoreFocus.focus(); });
      }} />
    </NavigationGuardContext.Provider>
  );
}

export function useNavigationGuard() {
  const value = useContext(NavigationGuardContext);
  if (!value) throw new Error('useNavigationGuard must be used inside NavigationGuardProvider.');
  return value;
}

export type { NavigationBlocker, NavigationGuardValue };
