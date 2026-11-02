/**
 * Next.js instrumentation hook (runs once per server process, before any request).
 *
 * OpenTelemetry is registered ONLY when OTEL_EXPORTER_OTLP_ENDPOINT is set — the same switch the API
 * uses. Unset (today, on Vercel and in the compose image) this returns before importing anything, so
 * there is no SDK, no listener and no export: behaviour is unchanged. Set, the standard OTEL_* variables
 * (OTEL_EXPORTER_OTLP_HEADERS, OTEL_EXPORTER_OTLP_PROTOCOL, OTEL_SERVICE_NAME, …) configure the exporter.
 *
 * Error tracking (Sentry) is a separate opt-in: it starts ONLY when SENTRY_DSN is set (see
 * src/lib/errorTracking and docs/runbooks/observability.md). Unset, it is never imported. When both are on,
 * Sentry leaves OpenTelemetry setup to @vercel/otel (skipOpenTelemetrySetup) so the two do not fight over the
 * global tracer provider.
 *
 * Node runtime only: middleware runs on the edge runtime, which this does not instrument.
 */
export async function register(): Promise<void> {
  if (process.env.NEXT_RUNTIME !== 'nodejs') return;
  await registerErrorTracking();
  if (!process.env.OTEL_EXPORTER_OTLP_ENDPOINT?.trim()) return;

  const { registerOTel } = await import('@vercel/otel');
  registerOTel({
    serviceName: process.env.OTEL_SERVICE_NAME?.trim() || 'kynexone-web',
    attributes: {
      'service.version':
        process.env.BUILD_COMMIT?.trim() || process.env.VERCEL_GIT_COMMIT_SHA?.trim() || 'unknown',
    },
  });
}

async function registerErrorTracking(): Promise<void> {
  const dsn = process.env.SENTRY_DSN?.trim();
  if (!dsn) return;
  const [Sentry, { buildSentryOptions }] = await Promise.all([
    import('@sentry/nextjs'),
    import('./src/lib/errorTracking/options'),
  ]);
  Sentry.init({
    ...buildSentryOptions({
      dsn,
      environment: process.env.SENTRY_ENVIRONMENT,
      release: process.env.BUILD_COMMIT?.trim() || process.env.VERCEL_GIT_COMMIT_SHA?.trim(),
      tracesSampleRate: process.env.SENTRY_TRACES_SAMPLE_RATE,
    }),
    skipOpenTelemetrySetup: Boolean(process.env.OTEL_EXPORTER_OTLP_ENDPOINT?.trim()),
  });
}

/** Server-side render and route errors. A no-op without SENTRY_DSN; the scrub in beforeSend applies. */
export async function onRequestError(
  ...args: Parameters<typeof import('@sentry/nextjs').captureRequestError>
): Promise<void> {
  if (process.env.NEXT_RUNTIME !== 'nodejs' || !process.env.SENTRY_DSN?.trim()) return;
  const Sentry = await import('@sentry/nextjs');
  Sentry.captureRequestError(...args);
}
