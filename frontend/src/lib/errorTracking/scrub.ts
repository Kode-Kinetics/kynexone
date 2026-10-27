/**
 * The single scrub point for error tracking (Sentry) on the web app. Pure: imports nothing, so it is unit-tested
 * (unit/errorTrackingScrub.spec.ts) and shared by the browser and the Node server.
 *
 * Nothing leaves the app unless it passes through here. Dropped outright: request bodies, cookies, headers, query
 * strings, the user object, breadcrumbs that carry a query string or a personal-data shape. Redacted in every
 * remaining string: bearer tokens, JWTs, emails, IBANs, Saudi national / iqama ids, 6+ digit runs. The only tag
 * kept about a tenant is an opaque GUID.
 */

type Bag = Record<string, unknown>;

const BEARER = /\bBearer\s+[A-Za-z0-9._~+/=-]+/gi;
const JWT = /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*/g;
const EMAIL = /[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi;
const IBAN = /\b[A-Z]{2}\d{2}[A-Z0-9]{10,30}\b/g;
const NATIONAL_ID = /(?<![\w.-])[12]\d{9}(?![\w-])/g;
const LONG_DIGITS = /(?<![\w.-])\d{6,}(?![\w-])/g;
// A '?' glued to a preceding token and followed by non-space: "/api/payroll?salary=1", not "Why? Because".
const QUERY = /(?<=\S)\?[^\s"'<>#]+/g;
// Money: any amount with decimals ("15750.50", "6,900.00"), and a whole number after a pay word ("salary 15750").
const DECIMAL_AMOUNT = /(?<![\w.-])\d{1,3}(?:[,\d]*\d)?[.,]\d{1,3}(?![\w-])/g;
const PAY_WORD_AMOUNT = /(?<![\p{L}\p{N}_])(salary|basic|housing|allowance|deduction|gross|net|pay|wage|amount|bonus|loan|راتب|الراتب|بدل|خصم)(\s*[:=]?\s*)[\d,]+/giu;
const GUID = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;
const SENSITIVE_KEY = /(salary|wage|iban|iqama|national_?id|email|phone|password|passwd|token|secret|authorization|cookie)/i;

export function scrubText(text: string): string {
  return text
    .replace(BEARER, 'Bearer [redacted]')
    .replace(JWT, '[token]')
    .replace(EMAIL, '[email]')
    .replace(QUERY, '?[redacted]')
    .replace(IBAN, '[iban]')
    .replace(NATIONAL_ID, '[national-id]')
    .replace(PAY_WORD_AMOUNT, '$1$2[amount]')
    .replace(DECIMAL_AMOUNT, '[amount]')
    .replace(LONG_DIGITS, '[number]');
}

export function isDirty(text: unknown): boolean {
  return typeof text === 'string' && scrubText(text) !== text;
}

/** The URL without query string or fragment, then text-scrubbed. */
export function stripQuery(url: string): string {
  const cut = url.search(/[?#]/);
  return scrubText(cut >= 0 ? url.slice(0, cut) : url);
}

export function isOpaqueTenantId(value: unknown): value is string {
  return typeof value === 'string' && GUID.test(value);
}

/** A sample rate from env as 0..1; unset, malformed or out of range means 0 (tracing off). */
export function parseSampleRate(raw: string | undefined): number {
  const n = Number(raw);
  return raw !== undefined && raw.trim() !== '' && Number.isFinite(n) && n >= 0 && n <= 1 ? n : 0;
}

function deepScrub(value: unknown, depth = 0): unknown {
  if (typeof value === 'string') return scrubText(value);
  if (value === null || typeof value !== 'object' || depth > 8) return value;
  if (Array.isArray(value)) return value.map((v) => deepScrub(v, depth + 1));
  const out: Record<string, unknown> = {};
  for (const [key, v] of Object.entries(value as Record<string, unknown>)) {
    out[key] = SENSITIVE_KEY.test(key) ? '[redacted]' : deepScrub(v, depth + 1);
  }
  return out;
}

/** True when a breadcrumb names a URL with a query string or any personal-data shape. */
function breadcrumbIsDirty(crumb: Bag): boolean {
  if (isDirty(crumb.message)) return true;
  const data = crumb.data as Bag | undefined;
  return !!data && typeof data === 'object' && Object.values(data).some((v) => isDirty(v));
}

/** beforeBreadcrumb: null drops it. Console output is dropped too, since it can echo any value. */
export function scrubBreadcrumb<B extends object>(crumb: B): B | null {
  const bag = crumb as unknown as Bag;
  if (bag.category === 'console') return null;
  return breadcrumbIsDirty(bag) ? null : crumb;
}

/** beforeSend / beforeSendTransaction. Always returns the event; there is nothing here worth dropping it for. */
export function scrubEvent<E extends object>(event: E): E {
  const next: Bag = { ...(event as unknown as Bag) };

  if (next.request) {
    const request: Bag = { ...(next.request as Bag) };
    delete request.data;
    delete request.cookies;
    delete request.headers;
    delete request.query_string;
    delete request.env;
    if (typeof request.url === 'string') request.url = stripQuery(request.url);
    next.request = request;
  }
  delete next.user;
  delete next.server_name;

  if (typeof next.message === 'string') next.message = scrubText(next.message);
  if (typeof next.transaction === 'string') next.transaction = stripQuery(next.transaction);
  for (const key of ['exception', 'logentry', 'extra', 'contexts', 'spans'] as const) {
    if (next[key] !== undefined) next[key] = deepScrub(next[key]);
  }
  if (Array.isArray(next.breadcrumbs)) {
    next.breadcrumbs = (next.breadcrumbs as Bag[]).filter((c) => !breadcrumbIsDirty(c) && c.category !== 'console');
  }

  // Tags: keep the opaque tenant id, scrub the rest.
  if (next.tags && typeof next.tags === 'object') {
    const tags: Record<string, unknown> = {};
    for (const [key, v] of Object.entries(next.tags as Record<string, unknown>)) {
      if (key === 'tenant_id') {
        if (isOpaqueTenantId(v)) tags[key] = v;
      } else tags[key] = deepScrub(v);
    }
    next.tags = tags;
  }
  return next as unknown as E;
}
