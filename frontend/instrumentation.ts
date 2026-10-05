/**
 * Next.js instrumentation hook (runs once per server process, before any request).
 *
 * OpenTelemetry is registered ONLY when OTEL_EXPORTER_OTLP_ENDPOINT is set — the same switch the API
 * uses. Unset (today, on Vercel and in the compose image) this returns before importing anything, so
 * there is no SDK, no listener and no export: behaviour is unchanged. Set, the standard OTEL_* variables
 * (OTEL_EXPORTER_OTLP_HEADERS, OTEL_EXPORTER_OTLP_PROTOCOL, OTEL_SERVICE_NAME, …) configure the exporter.
 *
 * Node runtime only: middleware runs on the edge runtime, which this does not instrument.
 */
export async function register(): Promise<void> {
  if (process.env.NEXT_RUNTIME !== 'nodejs') return;
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
