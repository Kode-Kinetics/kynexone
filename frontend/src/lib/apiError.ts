/**
 * Turn a failed API call into one sentence a user can act on, in their language.
 *
 * WHY. Error toasts printed `${status}: ${JSON.stringify(data)}` — raw JSON, field names and
 * stack-ish detail in front of an HR clerk, untranslatable and occasionally leaking internals.
 *
 * The API's error body is `{ code, message, traceId }` (Program.cs global handler and the
 * controllers' typed refusals) or ASP.NET's validation ProblemDetails `{ title, errors }`.
 * Resolution order:
 *   1. a SPECIFIC backend code with a dictionary entry → `error.<code>` (translated);
 *   2. the server's own human `message` / `title` (specific, though English today);
 *   3. validation `errors` → their messages, joined;
 *   4. the HTTP status → a generic translated sentence;
 *   5. `error.unknown`.
 * A 5xx appends the trace reference so support can find the log line. Never raw JSON.
 */

import { translate, type MessageParams } from '../i18n/translations';

type Translator = (key: string, params?: MessageParams) => string;
const EN: Translator = (key, params) => translate('en', key, params);

/**
 * Codes too generic to replace the server's sentence: `bad_request` wraps whatever the domain
 * rule said ("Loan amount exceeds the policy limit"), and that detail is the useful part.
 */
const GENERIC_CODES = new Set(['bad_request', 'invalid_request', 'conflict', 'decision_refused']);

interface ErrorBody {
  code?: unknown;
  message?: unknown;
  title?: unknown;
  traceId?: unknown;
  errors?: unknown;
}

function text(v: unknown): string | null {
  return typeof v === 'string' && v.trim() ? v.trim() : null;
}

function validationDetails(errors: unknown): string | null {
  if (!errors || typeof errors !== 'object') return null;
  const msgs = Object.values(errors as Record<string, unknown>)
    .flatMap((v) => (Array.isArray(v) ? v : [v]))
    .map(text)
    .filter((m): m is string => !!m);
  return msgs.length ? [...new Set(msgs)].slice(0, 3).join(' ') : null;
}

function byStatus(status: number | undefined, t: Translator): string {
  if (status === 401) return t('error.unauthorized');
  if (status === 403) return t('error.forbidden');
  if (status === 404) return t('error.not_found');
  if (status === 409) return t('error.conflict');
  if (status === 429) return t('error.rate_limited');
  if (status === 502 || status === 503 || status === 504) return t('error.server_unavailable');
  if (status != null && status >= 500) return t('error.internal_error');
  if (status != null) return t('error.http', { status: String(status) });
  return t('error.unknown');
}

export function describeApiError(err: unknown, t: Translator = EN): string {
  const ax = err as { isAxiosError?: boolean; response?: { status?: number; data?: unknown }; message?: unknown } | null;
  const status = ax?.response?.status;

  if (ax?.isAxiosError && !ax.response) return t('error.network');

  const raw = ax?.response?.data;
  if (raw != null) {
    if (typeof raw === 'string') {
      // An HTML error page from a proxy is not a sentence.
      return raw.trim() && !/^\s*</.test(raw) && raw.length < 300 ? raw.trim() : byStatus(status, t);
    }
    const body = raw as ErrorBody;
    const code = text(body.code);
    const trace = text(body.traceId);
    const withRef = (s: string) => (trace && status != null && status >= 500 ? `${s} ${t('error.reference', { traceId: trace })}` : s);

    if (code && !GENERIC_CODES.has(code)) {
      const key = `error.${code}`;
      const translated = t(key);
      if (translated !== key) return withRef(translated);
    }
    const message = text(body.message) ?? validationDetails(body.errors) ?? text(body.title);
    if (message && !(status != null && status >= 500)) return message;
    return withRef(byStatus(status, t));
  }

  if (status != null) return byStatus(status, t);
  if (err instanceof Error && err.message && !/^Request failed with status code/.test(err.message)) return err.message;
  return t('error.unknown');
}
