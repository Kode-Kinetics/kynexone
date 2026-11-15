import type { MessageParams } from '../i18n/translations';
import { refusalFromError } from './refusal';

type Translator = (key: string, params?: MessageParams) => string;

/**
 * The text shown when merging fails: what did not happen, then the server's own sentence in the viewer's language (or the
 * connection/5xx wording), through the shared refusal pattern. Merge and unmerge need `employees.write`, the same as
 * resolving a duplicate any other way, so a 403 is the generic "ask your HR administrator" refusal.
 */
export function mergeRefusal(error: unknown, t: Translator, locale: string): string {
  return refusalFromError(error, { what: t('The records were not merged') }, t, locale);
}
