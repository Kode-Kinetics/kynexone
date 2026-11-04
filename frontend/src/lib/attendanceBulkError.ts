import { describeApiError } from './apiError';
import type { MessageParams } from '../i18n/translations';

type Translator = (key: string, params?: MessageParams) => string;

const serverMessageOf = (err: unknown): string | null => {
  const data = (err as { response?: { data?: unknown } } | null)?.response?.data as { message?: unknown } | string | undefined | null;
  return data && typeof data === 'object' && typeof data.message === 'string' && data.message.trim() ? data.message.trim() : null;
};

/**
 * The sentence shown when a bulk attendance call is refused. The server's own `message` always wins (it names
 * the actual rule: scope, locked period, size cap, missing reason). Without one, the status gets plain words:
 * a bare 403 has no body so it gets the caller's `forbiddenFallback`; 409 means another bulk attendance
 * operation holds the company; 429 uses `tooManyFallback` (the caller knows what was too many).
 */
export function describeBulkError(err: unknown, t: Translator, forbiddenFallback?: string, tooManyFallback?: string): string {
  const status = (err as { response?: { status?: number } } | null)?.response?.status;
  const server = serverMessageOf(err);
  if (status === 403 && forbiddenFallback) return server ?? forbiddenFallback;
  if (status === 409 && !server) return t('Another bulk attendance operation is already running for this company. Wait for it to finish, then try again.');
  if (status === 429 && !server && tooManyFallback) return tooManyFallback;
  return describeApiError(err, t);
}
