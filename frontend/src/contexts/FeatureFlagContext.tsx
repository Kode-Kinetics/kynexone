'use client';

import { createContext, useCallback, useContext, useEffect, useState } from 'react';
import { useAuth } from './AuthContext';
import { featuresApi } from '../api/intelligence';

type FlagState =
  | { status: 'loading' }
  | { status: 'ready'; disabledKeys: Set<string> }
  | { status: 'error' };

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
  isLoading: boolean;
  /** Re-fetch flags from the API. Call after toggling a feature so the nav updates without a full page reload. */
  refresh: () => Promise<void>;
}

const FeatureFlagContext = createContext<FeatureFlagContextValue>({
  isFeatureEnabled: () => false,
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
      const keys = await featuresApi.getDisabledKeys();
      setState({ status: 'ready', disabledKeys: new Set(keys) });
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

  return (
    <FeatureFlagContext.Provider value={{ isFeatureEnabled, isLoading: state.status === 'loading', refresh }}>
      {children}
    </FeatureFlagContext.Provider>
  );
}

export function useFeatureFlags() {
  return useContext(FeatureFlagContext);
}
