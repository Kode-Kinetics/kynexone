import type { MessageParams } from '../i18n/translations';
import { refusalFromError, refusalText } from './refusal';

type Translator = (key: string, params?: MessageParams) => string;

/**
 * Merging a duplicate removes one of the two records, so the backend (resolve-duplicate with `merge`) requires
 * `employees.delete`, not just `employees.write`. The screen hides the Merge button without it and says why with this
 * sentence (an i18n key; pass it through `t`). A 403 on the call shows the same sentence.
 */
export const MERGE_NEEDS_DELETE_REASON = 'Merging removes the duplicate record, so it needs the Delete employees permission.';

const hasSentence = (data: unknown): boolean => {
  if (typeof data === 'string') return data.trim().length > 0 && !/^\s*</.test(data);
  if (!data || typeof data !== 'object') return false;
  const b = data as { message?: unknown; title?: unknown; errors?: unknown };
  return [b.message, b.title].some((v) => typeof v === 'string' && v.trim()) || (!!b.errors && typeof b.errors === 'object' && Object.keys(b.errors).length > 0);
};

/**
 * The text shown when merging fails. A 403 is the missing permission: it states the reason even when the response body
 * is empty (the generic "no permission" says nothing about what to ask for). Everything else goes through
 * `refusalFromError` (the server's own sentence, or the connection/5xx wording).
 */
export function mergeRefusal(error: unknown, t: Translator, locale: string): string {
  const what = t('The records were not merged');
  const response = (error as { response?: { status?: number; data?: unknown } } | null)?.response;
  if (response?.status === 403) {
    const reason = t(MERGE_NEEDS_DELETE_REASON);
    if (hasSentence(response.data)) {
      const full = refusalFromError(error, { what }, t, locale);
      return full.includes(reason) ? full : `${full} ${reason}`;
    }
    return refusalText({ what, why: reason, fix: t('Ask your HR administrator to grant you access.') });
  }
  return refusalFromError(error, { what }, t, locale);
}
