// ============================================================
// KynexOne Mobile — error reporting abstraction
// ============================================================
//
// Screens, the API client and the ErrorBoundary call `reportError`. By default it does nothing
// (no network, no storage). A reporter is installed only when a DSN is configured
// (see initErrorReporting.ts and docs/CRASH_REPORTING.md). Import-free so it runs under node --test.
//
// Privacy: this app shows payslips, IBANs and IDs. Messages, breadcrumbs and context pass through
// `scrub` before they leave the device, and the reporter is never given request/response bodies.

export interface ErrorContext {
  /** Short, non-PII label for where it happened, e.g. 'api.schema' or 'boundary'. */
  source?: string;
  /** Extra tags. Values are scrubbed; keep them to ids of routes, never user data. */
  tags?: Record<string, string | number | boolean | undefined>;
}

export interface ErrorReporter {
  captureException(error: Error, context?: ErrorContext): void;
  addBreadcrumb(message: string): void;
  /** Opaque tenant GUID only (never a name); null on sign-out. */
  setTenant?(tenantId: string | null): void;
}

export const noopReporter: ErrorReporter = {
  captureException() {},
  addBreadcrumb() {},
};

let active: ErrorReporter = noopReporter;

export function setErrorReporter(reporter: ErrorReporter | null): void {
  active = reporter ?? noopReporter;
}

export function isReportingEnabled(): boolean {
  return active !== noopReporter;
}

const EMAIL = /[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi;
const BEARER = /\bBearer\s+[A-Za-z0-9._~+/=-]+/gi;
const JWT = /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*/g;
const LONG_DIGITS = /\b\d{6,}\b/g; // IBAN tails, national ids, phone numbers, amounts with many digits
const IBAN = /\b[A-Z]{2}\d{2}[A-Z0-9]{10,30}\b/g;
// A '?' glued to a preceding token and followed by non-space: "/api/payroll?salary=1", not "Why? Because".
const QUERY = /(?<=\S)\?[^\s"'<>#]+/g;
// Money: any amount with decimals ("15750.50", "6,900.00"), and a whole number after a pay word ("salary 15750").
const DECIMAL_AMOUNT = /(?<![\w.-])\d{1,3}(?:[,\d]*\d)?[.,]\d{1,3}(?![\w-])/g;
const PAY_WORD_AMOUNT = /(?<![\p{L}\p{N}_])(salary|basic|housing|allowance|deduction|gross|net|pay|wage|amount|bonus|loan|راتب|الراتب|بدل|خصم)(\s*[:=]?\s*)[\d,]+/giu;
const GUID = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;
const SENSITIVE_KEY = /(salary|wage|iban|iqama|national_?id|email|phone|password|passwd|token|secret|authorization|cookie)/i;

/** Removes the obvious personal and credential shapes from free text. */
export function scrub(text: string): string {
  return text
    .replace(BEARER, 'Bearer [redacted]')
    .replace(JWT, '[token]')
    .replace(EMAIL, '[email]')
    .replace(QUERY, '?[redacted]')
    .replace(IBAN, '[iban]')
    .replace(PAY_WORD_AMOUNT, '$1$2[amount]')
    .replace(DECIMAL_AMOUNT, '[amount]')
    .replace(LONG_DIGITS, '[number]');
}

function scrubContext(context?: ErrorContext): ErrorContext | undefined {
  if (!context) return undefined;
  const tags: Record<string, string | number | boolean | undefined> = {};
  for (const [key, value] of Object.entries(context.tags ?? {})) {
    tags[key] = typeof value === 'string' ? scrub(value) : value;
  }
  return { source: context.source ? scrub(context.source) : undefined, tags };
}

function scrubError(error: unknown): Error {
  const source = error instanceof Error ? error : new Error(typeof error === 'string' ? error : 'Unknown error');
  const clean = new Error(scrub(source.message));
  clean.name = source.name;
  clean.stack = source.stack ? scrub(source.stack) : undefined;
  return clean;
}

/** Never throws: a failing reporter must not take the app down with it. */
export function reportError(error: unknown, context?: ErrorContext): void {
  try {
    active.captureException(scrubError(error), scrubContext(context));
  } catch {
    // swallow
  }
}

/** Tags later events with the opaque tenant GUID. Anything that is not a GUID (a slug, a name) is refused. */
export function setErrorTrackingTenant(tenantId: string | null | undefined): void {
  try {
    active.setTenant?.(typeof tenantId === 'string' && GUID.test(tenantId) ? tenantId : null);
  } catch {
    // swallow
  }
}

export function reportBreadcrumb(message: string): void {
  try {
    active.addBreadcrumb(scrub(message));
  } catch {
    // swallow
  }
}

// ---- Sentry adapter (kept here so this file stays import-free for node --test) ----

export interface SentryLike {
  captureException(error: Error, hint?: { tags?: Record<string, string>; level?: string }): unknown;
  addBreadcrumb(breadcrumb: { message: string; category?: string; level?: string }): void;
  setTag?(key: string, value: string | undefined): void;
}

export function createSentryReporter(sentry: SentryLike): ErrorReporter {
  return {
    captureException(error: Error, context?: ErrorContext) {
      const tags: Record<string, string> = {};
      if (context?.source) tags.source = context.source;
      for (const [key, value] of Object.entries(context?.tags ?? {})) {
        if (value !== undefined) tags[key] = String(value);
      }
      sentry.captureException(error, { tags });
    },
    addBreadcrumb(message: string) {
      sentry.addBreadcrumb({ message, category: 'app', level: 'info' });
    },
    setTenant(tenantId: string | null) {
      sentry.setTag?.('tenant_id', tenantId ?? undefined);
    },
  };
}

function isDirty(value: unknown): boolean {
  return typeof value === 'string' && scrub(value) !== value;
}

function deepScrub(value: unknown, depth = 0): unknown {
  if (typeof value === 'string') return scrub(value);
  if (value === null || typeof value !== 'object' || depth > 8) return value;
  if (Array.isArray(value)) return value.map((v) => deepScrub(v, depth + 1));
  const out: Record<string, unknown> = {};
  for (const [key, v] of Object.entries(value as Record<string, unknown>)) {
    out[key] = SENSITIVE_KEY.test(key) ? '[redacted]' : deepScrub(v, depth + 1);
  }
  return out;
}

function breadcrumbIsDirty(crumb: Record<string, any>): boolean {
  if (isDirty(crumb.message)) return true;
  const data = crumb.data;
  return !!data && typeof data === 'object' && Object.values(data).some(isDirty);
}

/** beforeBreadcrumb hook: null drops a breadcrumb that carries a query string or a personal-data shape. */
export function scrubSentryBreadcrumb<B extends Record<string, any>>(crumb: B): B | null {
  if (crumb.category === 'console') return null;
  return breadcrumbIsDirty(crumb) ? null : crumb;
}

/**
 * beforeSend hook for Sentry.init, the single scrub point: drops request data (url, query, cookies, headers,
 * body) and the user, filters breadcrumbs, scrubs every remaining string, keeps only an opaque GUID tenant tag.
 */
export function scrubSentryEvent<E extends Record<string, any>>(event: E): E {
  const next: Record<string, any> = { ...event };
  delete next.request;
  delete next.user;
  delete next.server_name;
  if (typeof next.message === 'string') next.message = scrub(next.message);
  if (typeof next.transaction === 'string') next.transaction = scrub(next.transaction);
  for (const key of ['exception', 'logentry', 'extra', 'contexts', 'spans']) {
    if (next[key] !== undefined) next[key] = deepScrub(next[key]);
  }
  if (Array.isArray(next.breadcrumbs)) {
    next.breadcrumbs = next.breadcrumbs.filter((c: Record<string, any>) => c.category !== 'console' && !breadcrumbIsDirty(c));
  }
  if (next.tags && typeof next.tags === 'object') {
    const tags: Record<string, unknown> = {};
    for (const [key, v] of Object.entries(next.tags as Record<string, unknown>)) {
      if (key === 'tenant_id') {
        if (typeof v === 'string' && GUID.test(v)) tags[key] = v;
      } else tags[key] = deepScrub(v);
    }
    next.tags = tags;
  }
  return next as E;
}
