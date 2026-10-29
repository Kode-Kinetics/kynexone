import { create } from 'zustand';

/** Offline = the OS says there is no connection, or a connection with no internet behind it. */
export function isOfflineState(state: { isConnected?: boolean | null; isInternetReachable?: boolean | null } | null | undefined): boolean {
  if (!state) return false;
  if (state.isConnected === false) return true;
  return state.isInternetReachable === false;
}

interface ConnectivityState {
  offline: boolean;
  setOffline: (offline: boolean) => void;
}

export const useConnectivityStore = create<ConnectivityState>((set) => ({
  offline: false,
  setOffline: (offline) => set({ offline }),
}));

/** Starts listening; returns the unsubscribe. NetInfo is required lazily so a missing native module never blocks launch. */
export function startConnectivityMonitor(): () => void {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const NetInfo = require('@react-native-community/netinfo').default;
    return NetInfo.addEventListener((state: Parameters<typeof isOfflineState>[0]) => {
      useConnectivityStore.getState().setOffline(isOfflineState(state));
    });
  } catch {
    return () => undefined;
  }
}
