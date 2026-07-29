import client from './client';
import type { LocaleCode } from '../i18n/translations';

export interface TransliterateResult {
  suggestion: string;
}

/**
 * P0-6: sanctioned, in-house replacement for the removed MyMemory keystroke call.
 *
 * Employee and company legal names are NEVER sent to any third party. This posts to our
 * own authenticated, tenant-scoped backend (`POST /api/localization/transliterate`), which
 * runs a fully offline transliteration service — no external network egress, no PDPL/GDPR
 * cross-border transfer. Called only on an explicit user action (a "Suggest (AR)" button).
 */
export const localizationApi = {
  transliterate: (text: string, target: LocaleCode = 'ar') =>
    client
      .post<TransliterateResult>('/api/localization/transliterate', { text, target })
      .then((r) => r.data),
};
