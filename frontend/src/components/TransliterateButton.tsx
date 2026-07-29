'use client';

import type { LocaleCode } from '../i18n/translations';
import { useTransliterate } from '../hooks/useTransliterate';

/**
 * P0-6 — opt-in "Suggest (AR)" control that sits beside a manual Arabic-name field.
 *
 * Replaces the old keystroke auto-translate: it does nothing until the user clicks it, then
 * asks our own offline backend for a transliteration and hands the result to `onSuggest` so
 * the user can accept or edit it (preview -> approve). No employee/company PII ever leaves to
 * a third party. The manual field beside it is unchanged and always usable.
 */
export function TransliterateButton({
  source,
  target = 'ar',
  onSuggest,
  className,
  label,
}: {
  /** English source text to transliterate (e.g. the "Name (EN)" field value). */
  source: string;
  /** Target locale for the suggestion. Defaults to Arabic. */
  target?: LocaleCode;
  /** Receives the sanitized suggestion; the caller writes it into the Arabic field. */
  onSuggest: (suggestion: string) => void;
  className?: string;
  label?: string;
}) {
  const { suggest, isTranslating } = useTransliterate();
  const canSuggest = source.trim().length >= 2 && !isTranslating;

  const handleClick = async () => {
    const suggestion = await suggest(source, target);
    if (suggestion) onSuggest(suggestion);
  };

  return (
    <button
      type="button"
      disabled={!canSuggest}
      onClick={handleClick}
      title="Suggest an Arabic transliteration from the English text. You can edit it afterwards."
      className={
        className ??
        'shrink-0 whitespace-nowrap rounded-lg border border-slate-200 px-2.5 py-2 text-xs font-medium text-slate-600 transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5'
      }
    >
      {isTranslating ? 'Suggesting…' : (label ?? 'Suggest (AR)')}
    </button>
  );
}
