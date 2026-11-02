// Startup health check for OTA updates. See launchHealth.ts for the rule; this file does the I/O.
//
// What the SDK already does: if a new update throws a JS error very early in launch, expo-updates
// falls back to the previous or embedded bundle on its own. What it cannot do is notice an update
// that renders into the ErrorBoundary or crash-loops after that window. This check marks a launch
// healthy only after the app has stayed up and no boundary has fired; an update that fails to get
// there twice is treated as broken and the app immediately fetches what the server serves now (a
// fix, or the rollback-to-embedded directive from `eas update:rollback`) and reloads into it.
import * as Updates from 'expo-updates';
import { appStorage } from '@/storage';
import { reportError } from '@/observability/reportError';
import {
  HEALTHY_AFTER_MS,
  beginLaunch,
  markHealthy,
  parseLaunchRecord,
  type LaunchRecord,
} from './launchHealth';

const KEY = 'kynexone_launch_health';

let current: LaunchRecord | null = null;
let broken = false;
let timer: ReturnType<typeof setTimeout> | null = null;

/** The ErrorBoundary calls this: a launch that hit a render failure must not be marked healthy. */
export function noteLaunchFailure(): void {
  broken = true;
  if (timer) clearTimeout(timer);
  timer = null;
}

async function pullServerVerdict(): Promise<void> {
  try {
    const check = await Updates.checkForUpdateAsync();
    if (check.isAvailable || (check as { isRollBackToEmbedded?: boolean }).isRollBackToEmbedded) {
      await Updates.fetchUpdateAsync();
      await Updates.reloadAsync();
    }
  } catch {
    // Offline or the server has nothing newer: the next launch tries again.
  }
}

export async function startLaunchHealthCheck(): Promise<void> {
  // Development builds and Expo Go have no OTA layer to judge.
  if (!Updates.isEnabled) return;
  const previous = parseLaunchRecord(await appStorage.get<unknown>(KEY));
  const decision = beginLaunch(previous, Updates.isEmbeddedLaunch ? null : Updates.updateId);
  current = decision.record;
  broken = false;
  await appStorage.set(KEY, current);

  timer = setTimeout(() => {
    timer = null;
    if (broken || !current) return;
    current = markHealthy(current);
    void appStorage.set(KEY, current);
  }, HEALTHY_AFTER_MS);

  if (decision.crashLoop) {
    reportError(new Error('OTA update failed to start healthily'), {
      source: 'updates.crashLoop',
      tags: { updateId: current.updateId, failedLaunches: current.failedLaunches },
    });
    await pullServerVerdict();
  }
}
