interface SnapshotProtectionApi {
  enable: () => Promise<void>;
  disable: () => Promise<void>;
  onError?: (error: unknown) => void;
}

/** Serialize the global native setting; each sensitive surface owns one lease. */
export function createSnapshotProtection(api: SnapshotProtectionApi) {
  const owners = new Set<symbol>();
  let enabled = false;
  let tail = Promise.resolve();

  function reconcile() {
    tail = tail.then(async () => {
      const needed = owners.size > 0;
      if (needed === enabled) return;
      if (needed) await api.enable();
      else await api.disable();
      enabled = needed;
    }).catch((error: unknown) => { api.onError?.(error); });
  }

  return {
    acquire(): () => void {
      const owner = Symbol();
      owners.add(owner);
      reconcile();
      return () => {
        if (owners.delete(owner)) reconcile();
      };
    },
  };
}

interface NativeScreenProtection {
  enableAppSwitcherProtectionAsync: (intensity: number) => Promise<void>;
  disableAppSwitcherProtectionAsync: () => Promise<void>;
  preventScreenCaptureAsync: (key: string) => Promise<void>;
  allowScreenCaptureAsync: (key: string) => Promise<void>;
}

/** Foreground capture and background snapshots have separate native lifetimes. */
export function createScreenProtection(native: NativeScreenProtection, platform: string) {
  const reportError = () => console.warn('[Privacy] Screen protection update failed');
  const snapshot = createSnapshotProtection({
    enable: async () => {
      if (platform === 'ios') await native.enableAppSwitcherProtectionAsync(1);
      else if (platform === 'android') await native.preventScreenCaptureAsync('sensitive-snapshot');
    },
    disable: async () => {
      if (platform === 'ios') await native.disableAppSwitcherProtectionAsync();
      else if (platform === 'android') await native.allowScreenCaptureAsync('sensitive-snapshot');
    },
    onError: reportError,
  });
  const capture = createSnapshotProtection({
    enable: async () => {
      if (platform === 'ios' || platform === 'android') await native.preventScreenCaptureAsync('sensitive-content');
    },
    disable: async () => {
      if (platform === 'ios' || platform === 'android') await native.allowScreenCaptureAsync('sensitive-content');
    },
    onError: reportError,
  });
  return {
    acquireSnapshot: snapshot.acquire,
    acquireSensitive() {
      const releaseSnapshot = snapshot.acquire();
      const releaseCapture = capture.acquire();
      return () => { releaseCapture(); releaseSnapshot(); };
    },
  };
}
