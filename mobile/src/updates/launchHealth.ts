// Launch-health bookkeeping for OTA updates (pure, import-free for node --test).
//
// expo-updates already rolls back by itself when a new update throws a JS error very early in
// launch (its native error recovery). It cannot see an update that starts fine and then crash-loops
// in render, or that is stuck behind the ErrorBoundary. This record covers that gap: every launch
// of an update that was never marked healthy counts against it; at the threshold the app stops
// waiting for "next launch" and pulls whatever the server serves now (a fix, or the rollback-to-
// embedded directive from `eas update:rollback`) and reloads straight into it.

/** A render failure is terminal for this JS launch, even when startup I/O began later. */
export class LaunchFailureLatch {
  private failed = false;
  noteFailure(): void { this.failed = true; }
  get hasFailed(): boolean { return this.failed; }
}

export const EMBEDDED = 'embedded';
/** Failed launches of the same update before the app acts. Two: one crash can be a fluke. */
export const CRASH_LOOP_THRESHOLD = 2;
/** The app counts as healthy once it has stayed up this long after the navigator first rendered. */
export const HEALTHY_AFTER_MS = 8_000;

export interface LaunchRecord {
  updateId: string;
  /** Launches of this update that never reached healthy. */
  failedLaunches: number;
  healthy: boolean;
}

export interface LaunchDecision {
  record: LaunchRecord;
  /** True when this OTA update has failed to become healthy too many times in a row. */
  crashLoop: boolean;
}

export function beginLaunch(previous: LaunchRecord | null, updateId: string | null | undefined): LaunchDecision {
  const id = updateId || EMBEDDED;
  const sameUpdateNeverHealthy = previous !== null && previous.updateId === id && !previous.healthy;
  const record: LaunchRecord = {
    updateId: id,
    failedLaunches: sameUpdateNeverHealthy ? previous.failedLaunches + 1 : 0,
    healthy: false,
  };
  // The embedded bundle is the safe floor: there is nothing to fall back to, so never "recover" from it.
  const crashLoop = id !== EMBEDDED && record.failedLaunches >= CRASH_LOOP_THRESHOLD;
  return { record, crashLoop };
}

export function markHealthy(record: LaunchRecord): LaunchRecord {
  return { ...record, failedLaunches: 0, healthy: true };
}

export function parseLaunchRecord(raw: unknown): LaunchRecord | null {
  if (!raw || typeof raw !== 'object') return null;
  const { updateId, failedLaunches, healthy } = raw as Record<string, unknown>;
  if (typeof updateId !== 'string' || typeof failedLaunches !== 'number' || typeof healthy !== 'boolean') return null;
  return { updateId, failedLaunches: Math.max(0, Math.floor(failedLaunches)), healthy };
}
