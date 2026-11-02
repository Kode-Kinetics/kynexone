/**
 * Browser-side error tracking (Next.js runs this file once per page load, before the app).
 *
 * Starts ONLY when NEXT_PUBLIC_SENTRY_DSN was set at build time. Unset (today), the condition is a build-time
 * constant false: the SDK is not bundled, nothing initialises and nothing is sent. Privacy settings and the scrub
 * live in src/lib/errorTracking (no replay, no default PII, query strings and bodies dropped).
 */
import { buildSentryOptions } from './src/lib/errorTracking/options';

const dsn = process.env.NEXT_PUBLIC_SENTRY_DSN?.trim();

if (dsn) {
  void import('@sentry/nextjs').then((Sentry) => {
    Sentry.init(
      buildSentryOptions({
        dsn,
        environment: process.env.NEXT_PUBLIC_SENTRY_ENVIRONMENT,
        release: process.env.NEXT_PUBLIC_SENTRY_RELEASE,
        tracesSampleRate: process.env.NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE,
      }),
    );
  });
}
