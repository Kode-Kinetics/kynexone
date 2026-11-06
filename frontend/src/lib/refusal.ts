import { describeApiError } from './apiError';
import type { MessageParams } from '../i18n/translations';

/**
 * The refusal pattern (Screen Standard section 3e): *what happened, why, what to do*, in one or two sentences, with
 * the fix as a button or link where one exists. "Salary wasn't saved: the structure is no longer valid for this
 * employee. Choose another structure." Never "Failed to save", "Invalid", "Error 422" or "Something went wrong" alone.
 *
 * `refusalText` only formats; the caller passes the three pieces already translated. `refusalFromError` builds the
 * `why` from the API error the app already uses:
 *   { code, message, messageAr, traceId } (controllers' typed refusals and the global handler) or ASP.NET's
 *   ProblemDetails { title, errors }, resolved by lib/apiError.ts (`error.<code>` first, then the server's own
 *   sentence, then the status). In Arabic a body that ships `messageAr` shows it.
 * A 403 names who can grant the access, because "no permission" alone is a dead end.
 */
type Translator = (key: string, params?: MessageParams) => string;

export interface RefusalParts {
  /** What did not happen: "Salary was not saved". No trailing punctuation. */
  what: string;
  /** Why: one sentence. */
  why?: string;
  /** What to do next: one sentence, an imperative ("Choose another structure."). */
  fix?: string;
}

const clean = (s: string) => s.trim().replace(/[\s:;,]+$/u, '');
const sentence = (s: string) => {
  const c = s.trim();
  return /[.!?؟…]$/u.test(c) ? c : `${c}.`;
};

/** "{what}: {why} {fix}". Missing parts are skipped, so a caller with no `why` still gets a usable sentence. */
export function refusalText({ what, why, fix }: RefusalParts): string {
  const head = clean(what);
  const reason = why?.trim() ? sentence(why) : '';
  const action = fix?.trim() ? sentence(fix) : '';
  if (!reason) return action ? `${sentence(head)} ${action}` : sentence(head);
  return [`${head}: ${reason}`, action].filter(Boolean).join(' ');
}

interface ErrorLike {
  isAxiosError?: boolean;
  response?: { status?: number; data?: unknown };
}

const text = (v: unknown): string | null => (typeof v === 'string' && v.trim() && !/^\s*</.test(v) && v.length < 500 ? v.trim() : null);

/** The `why` of a failed request, in the viewer's language. */
export function refusalWhy(error: unknown, t: Translator, locale: string): string {
  const response = (error as ErrorLike | null)?.response;
  const data = response?.data;
  if (locale === 'ar' && data && typeof data === 'object') {
    const ar = text((data as { messageAr?: unknown }).messageAr);
    if (ar) return ar;
  }
  return describeApiError(error, t);
}

/**
 * A complete refusal from a caught error. `fix` defaults by cause: a 403 names who can grant access, an unreachable
 * server says to retry, anything else keeps the caller's own `fix` (or none).
 */
export function refusalFromError(
  error: unknown,
  parts: { what: string; fix?: string },
  t: Translator,
  locale: string,
): string {
  const e = error as ErrorLike | null;
  const status = e?.response?.status;
  const unreachable = !!e?.isAxiosError && !e.response;
  const fix = parts.fix
    ?? (status === 403 ? t('Ask your HR administrator to grant you access.')
      : unreachable || (status != null && status >= 500) ? t('Check your connection and try again in a moment.')
        : undefined);
  const why = refusalWhy(error, t, locale);
  // A 403 and a 5xx keep their own wording; when the why already says "try again" the generic fix would repeat it.
  return refusalText({ what: parts.what, why, fix: unreachable ? undefined : fix });
}

/**
 * Show a refusal as a toast (the app-wide `zayra:error` event). A 401/402/403 is already explained by the
 * interceptor with the server's own reason, so it is skipped here to avoid two toasts.
 */
export function notifyRefusal(
  error: unknown,
  parts: { what: string; fix?: string },
  t: Translator,
  locale: string,
): void {
  const status = (error as ErrorLike | null)?.response?.status;
  if (status === 401 || status === 402 || status === 403) return;
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new CustomEvent('zayra:error', { detail: refusalFromError(error, parts, t, locale) }));
}
