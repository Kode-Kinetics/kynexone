// Installs the Sentry reporter when (and only when) a DSN is configured. No DSN = no-op, nothing
// leaves the device. The owner creates the Sentry project and sets EXPO_PUBLIC_SENTRY_DSN for the
// build profile (docs/CRASH_REPORTING.md). No DSN is committed to this repo.
import Constants from 'expo-constants';
import * as Updates from 'expo-updates';
import { createSentryReporter, scrubSentryBreadcrumb, scrubSentryEvent, setErrorReporter } from './reportError';

export function configuredSentryDsn(): string | undefined {
  const dsn = Constants.expoConfig?.extra?.sentryDsn;
  return typeof dsn === 'string' && dsn.trim() ? dsn.trim() : undefined;
}

/** EXPO_PUBLIC_SENTRY_TRACES_SAMPLE_RATE as 0..1; unset, malformed or out of range means 0 (tracing off). */
export function parseTracesSampleRate(raw: unknown): number {
  const n = typeof raw === 'string' || typeof raw === 'number' ? Number(raw) : NaN;
  return raw !== '' && Number.isFinite(n) && n >= 0 && n <= 1 ? n : 0;
}

export function initErrorReporting(): void {
  const dsn = configuredSentryDsn();
  if (!dsn) return;
  try {
    // Required lazily so a build without the native module still starts; the package is a declared
    // dependency and is linked by the store build.
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const Sentry = require('@sentry/react-native');
    Sentry.init({
      dsn,
      environment: String(Constants.expoConfig?.extra?.appEnvironment ?? 'development'),
      release: String(Constants.expoConfig?.version ?? '0'),
      sendDefaultPii: false,
      tracesSampleRate: parseTracesSampleRate(Constants.expoConfig?.extra?.sentryTracesSampleRate),
      enableCaptureFailedRequests: false, // failed-request events carry the URL and query string
      maxBreadcrumbs: 20,
      attachScreenshot: false, // screens show payslips and IDs
      attachViewHierarchy: false,
      enableAutoSessionTracking: true,
      beforeSend: scrubSentryEvent,
      beforeSendTransaction: scrubSentryEvent,
      beforeBreadcrumb: scrubSentryBreadcrumb,
    });
    // Tag every event with the exact OTA bundle so a bad update is traceable to its update id.
    Sentry.setTag('runtimeVersion', String(Updates.runtimeVersion ?? 'unknown'));
    Sentry.setTag('updateId', String(Updates.updateId ?? 'embedded'));
    Sentry.setTag('isEmbeddedLaunch', String(Updates.isEmbeddedLaunch));
    setErrorReporter(createSentryReporter(Sentry));
  } catch (error) {
    console.warn('[observability] Crash reporting unavailable:', (error as Error)?.message);
  }
}
