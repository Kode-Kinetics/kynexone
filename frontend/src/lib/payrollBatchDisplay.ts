import type { MessageParams } from '../i18n/translations';

type Translator = (key: string, params?: MessageParams) => string;

/** Statuses whose API label only elaborates the status already shown on the badge. */
const REPEATS_BADGE = new Set(['Generated', 'Submitted']);

/**
 * The extra line under a payment batch's status badge. The API's label for Generated is
 * "Generated (file ready for the bank)"; printing it beneath a "Generated" badge showed the status
 * twice. A label that only repeats the badge is dropped; one that adds a caveat (an accepted batch's
 * evidence note) is kept, translated.
 */
export function wpsStatusNote(t: Translator, label: string | null | undefined, status: string): string | null {
  const text = (label ?? '').trim();
  if (!text || text === status) return null;
  if (REPEATS_BADGE.has(status) && text.startsWith(status)) return null;
  return t(text);
}
