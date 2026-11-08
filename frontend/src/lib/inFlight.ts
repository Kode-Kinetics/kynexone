/**
 * A per-key single-flight guard. A second click on "Approve" while the first request is still running used to
 * send a second request (register: Leave, Compliance, Learning, Benefits rows). The guard answers on the same
 * tick, before React has re-rendered the disabled button, so a double-click cannot slip through.
 */
export function createInFlight() {
  const active = new Set<string>();
  return {
    has: (key: string) => active.has(key),
    get size() { return active.size; },
    keys: () => [...active],
    /** Runs `action` unless `key` is already running; resolves `undefined` when it was skipped. */
    async run<T>(key: string, action: () => Promise<T>, onChange?: () => void): Promise<T | undefined> {
      if (active.has(key)) return undefined;
      active.add(key);
      onChange?.();
      try {
        return await action();
      } finally {
        active.delete(key);
        onChange?.();
      }
    },
  };
}
