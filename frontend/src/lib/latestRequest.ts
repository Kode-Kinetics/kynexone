/**
 * Latest-request gate for search boxes and list loads.
 *
 * A debounce only spaces requests out; it does not order their responses. When an earlier search
 * is slower than a later one, its response lands last and — without a gate — replaces the newer
 * result. In a picker the late response also reopened a dropdown the user had already closed by
 * choosing someone, so the next click silently swapped the selected employee.
 *
 * Every request takes a token from `begin()`. Only the most recent token may write state, and
 * `invalidate()` retires every in-flight request (on select, clear, or when the query is emptied).
 */
export interface LatestRequestGate {
  /** Start a request. Every token handed out before this one becomes stale. */
  begin(): number;
  /** True only for the most recently begun request, and only until the gate is invalidated. */
  isLatest(token: number): boolean;
  /** Retire every in-flight request without starting a new one. */
  invalidate(): void;
}

export function createLatestRequestGate(): LatestRequestGate {
  let current = 0;
  return {
    begin: () => ++current,
    isLatest: (token) => token === current,
    invalidate: () => { current += 1; },
  };
}

export type LatestRequestOutcome = 'applied' | 'failed' | 'stale';

/**
 * Run `request` under the gate. `onResult`/`onError` fire only while the request is still the
 * latest; `onSettled` (e.g. clearing a spinner) likewise, so a stale request can neither overwrite
 * newer results nor switch off the loading state of the request that superseded it.
 */
export async function runLatest<T>(
  gate: LatestRequestGate,
  request: () => Promise<T>,
  handlers: {
    onResult: (result: T) => void;
    onError?: (error: unknown) => void;
    onSettled?: () => void;
  },
): Promise<LatestRequestOutcome> {
  const token = gate.begin();
  let result: T;
  try {
    result = await request();
  } catch (error) {
    if (!gate.isLatest(token)) return 'stale';
    handlers.onError?.(error);
    handlers.onSettled?.();
    return 'failed';
  }
  if (!gate.isLatest(token)) return 'stale';
  handlers.onResult(result);
  handlers.onSettled?.();
  return 'applied';
}
