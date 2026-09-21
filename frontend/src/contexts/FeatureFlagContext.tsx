'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { useAuth } from './AuthContext';
import { featuresApi, type ModuleNav } from '../api/intelligence';

type FlagState =
  | { status: 'loading' }
  | { status: 'ready'; disabledKeys: Set<string>; modules: ModuleNav[] }
  | { status: 'error' };

/** What the route guard needs to know about the path it is protecting. */
export interface PathModuleVerdict {
  /** False only when the path belongs to a module that is known to be off. */
  allowed: boolean;
  /** The owning module, when one claims this path. */
  moduleKey?: string;
  moduleLabel?: string;
}

interface FeatureFlagContextValue {
  /**
   * Returns whether a feature is enabled for the current tenant.
   *
   * - While flags are loading: returns false (fail-closed — hide keyed nav items until known).
   * - On fetch error: returns false (fail-closed — backend API guards remain the real gate).
   * - Once loaded: absent key = enabled by default; key in disabled set = false.
   * - No requiredFeatureKey: caller should short-circuit before calling this.
   */
  isFeatureEnabled: (featureKey: string) => boolean;
  /**
   * Resolves a frontend route to its owning module and says whether it may be shown.
   *
   * Path ownership comes from the backend module catalog, so a page cannot be reachable in the UI
   * while its API refuses it — the state that used to render an empty, silently-403ing screen.
   *
   * Unlike {@link isFeatureEnabled}, this is fail-OPEN while loading: the alternative is flashing
   * a "module disabled" screen on every navigation before the fetch resolves. The API remains the
   * real gate, and `isLoading` is exposed so callers can render a spinner instead.
   */
  verdictForPath: (pathname: string) => PathModuleVerdict;
  isLoading: boolean;
  /** Re-fetch flags from the API. Call after toggling a module so the nav updates without a reload. */
  refresh: () => Promise<void>;
}

const FeatureFlagContext = createContext<FeatureFlagContextValue>({
  isFeatureEnabled: () => false,
  verdictForPath: () => ({ allowed: true }),
  isLoading: true,
  refresh: async () => {},
});

export function FeatureFlagProvider({ children }: { children: React.ReactNode }) {
  const { user } = useAuth();
  const [state, setState] = useState<FlagState>({ status: 'loading' });

  const refresh = useCallback(async () => {
    if (!user) {
      setState({ status: 'loading' });
      return;
    }
    try {
      const [keys, modules] = await Promise.all([
        featuresApi.getDisabledKeys(),
        featuresApi.getModules(),
      ]);
      setState({ status: 'ready', disabledKeys: new Set(keys), modules });
    } catch {
      setState({ status: 'error' });
    }
  }, [user]);

  useEffect(() => {
    refresh();
  }, [refresh]);

  const isFeatureEnabled = useCallback(
    (featureKey: string): boolean => {
      if (state.status !== 'ready') return false; // fail-closed during load or error
      return !state.disabledKeys.has(featureKey);  // absent = enabled by default
    },
    [state],
  );

  // Longest path first, mirroring the backend's longest-prefix resolution, so a module that owns
  // a sub-route of another module's path (/ess/benefits under /ess) wins.
  const orderedModules = useMemo(() => {
    if (state.status !== 'ready') return [];
    return state.modules
      .flatMap(m => m.navPaths.map(path => ({ path, module: m })))
      .sort((a, b) => b.path.length - a.path.length);
  }, [state]);

  const verdictForPath = useCallback(
    (pathname: string): PathModuleVerdict => {
      if (state.status !== 'ready') return { allowed: true }; // fail-open while unknown
      const match = orderedModules.find(
        ({ path }) => pathname === path || pathname.startsWith(`${path}/`),
      );
      if (!match) return { allowed: true }; // unowned route — never gated
      return {
        allowed: match.module.enabled,
        moduleKey: match.module.key,
        moduleLabel: match.module.labelEn,
      };
    },
    [state, orderedModules],
  );

  return (
    <FeatureFlagContext.Provider
      value={{ isFeatureEnabled, verdictForPath, isLoading: state.status === 'loading', refresh }}
    >
      {children}
    </FeatureFlagContext.Provider>
  );
}

export function useFeatureFlags() {
  return useContext(FeatureFlagContext);
}
