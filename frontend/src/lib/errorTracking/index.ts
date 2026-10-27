import { isOpaqueTenantId } from './scrub';

/**
 * Error tracking is OFF unless NEXT_PUBLIC_SENTRY_DSN (browser; inlined at build time) or SENTRY_DSN (server) is
 * set. These helpers import the SDK lazily, so with no DSN the SDK is never downloaded or evaluated and the
 * helpers do nothing.
 */
export function isErrorTrackingEnabled(): boolean {
  return Boolean(process.env.NEXT_PUBLIC_SENTRY_DSN?.trim());
}

/** Tags later events with the opaque tenant GUID (never a name). Pass null on sign-out. */
export function tagErrorTrackingTenant(tenantId: string | null | undefined): void {
  if (!isErrorTrackingEnabled()) return;
  const value = isOpaqueTenantId(tenantId) ? tenantId : undefined;
  void import('@sentry/nextjs').then((Sentry) => Sentry.setTag('tenant_id', value)).catch(() => {});
}

/** Reports an error caught by a render error boundary. A no-op without a DSN. */
export function captureRenderError(error: unknown): void {
  if (!isErrorTrackingEnabled()) return;
  void import('@sentry/nextjs').then((Sentry) => Sentry.captureException(error)).catch(() => {});
}
