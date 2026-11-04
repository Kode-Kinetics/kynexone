import { describeApiError } from './apiError';
import type { MessageParams } from '../i18n/translations';

type Translator = (key: string, params?: MessageParams) => string;

/**
 * The sentence shown when a bulk attendance call is refused. The server's own `message` always wins (it names
 * the actual rule: scope, locked period, size cap, missing reason). A bare 403 has no body, so it gets the
 * caller's plain-language `forbiddenFallback` instead of a generic "not allowed".
 */
export function describeBulkError(err: unknown, t: Translator, forbiddenFallback?: string): string {
  const ax = err as { response?: { status?: number; data?: unknown } } | null;
  if (ax?.response?.status === 403 && forbiddenFallback) {
    const data = ax.response.data as { message?: unknown } | string | undefined | null;
    const serverMessage = data && typeof data === 'object' && typeof data.message === 'string' && data.message.trim() ? data.message.trim() : null;
    return serverMessage ?? forbiddenFallback;
  }
  return describeApiError(err, t);
}
