import { parseSampleRate, scrubBreadcrumb, scrubEvent } from './scrub';

/**
 * Sentry.init options shared by the browser and the Node server. Privacy settings live here and nowhere else:
 * no default PII, no replay, no trace propagation to other origins, everything through the scrub in ./scrub.
 * (No Sentry import: this file is loaded by unit tests.)
 */
export interface ErrorTrackingEnv {
  dsn: string;
  environment?: string;
  release?: string;
  tracesSampleRate?: string;
}

export function buildSentryOptions(env: ErrorTrackingEnv) {
  return {
    dsn: env.dsn,
    environment: env.environment?.trim() || 'production',
    ...(env.release?.trim() ? { release: env.release.trim() } : {}),
    sendDefaultPii: false,
    tracesSampleRate: parseSampleRate(env.tracesSampleRate),
    // Never attach trace headers to requests (the API is on another origin); never record sessions.
    tracePropagationTargets: [] as string[],
    replaysSessionSampleRate: 0,
    replaysOnErrorSampleRate: 0,
    maxBreadcrumbs: 20,
    beforeSend: scrubEvent,
    beforeSendTransaction: scrubEvent,
    beforeBreadcrumb: scrubBreadcrumb,
  };
}
