/**
 * Locale-aware pluralisation for user-facing copy.
 *
 * The app ships English and Arabic, and the two do not agree on how many plural forms a sentence
 * needs: English has 2 (one / other), Arabic has 6 (zero / one / two / few / many / other). A
 * hand-rolled `n !== 1 ? 's' : ''` is therefore wrong the moment a string is translated, and
 * "1 urgent action require attention" shows it is already wrong in English. `Intl.PluralRules`
 * is the only correct source for which form a locale wants, and every browser this app supports
 * has had it since 2018.
 *
 * Callers supply the whole clause per form, not a bare suffix, so verb agreement travels with the
 * noun ("1 urgent action requires" vs "2 urgent actions require"). Use `{count}` as the placeholder
 * — it is substituted with the count formatted for the locale (Arabic-Indic digits under `ar`).
 */

export type PluralCategory = 'zero' | 'one' | 'two' | 'few' | 'many' | 'other';

/** `other` is required: it is the form every locale falls back to. */
export type PluralForms = Partial<Record<PluralCategory, string>> & { other: string };

/**
 * Where to look when a locale asks for a form the caller did not supply. English callers supply
 * only `one` and `other`; under Arabic this keeps them on a sensible form instead of throwing.
 */
const FALLBACK: Record<PluralCategory, PluralCategory[]> = {
  zero:  ['other'],
  one:   ['other'],
  two:   ['few', 'many', 'other'],
  few:   ['many', 'other'],
  many:  ['few', 'other'],
  other: [],
};

/** Which plural form `locale` wants for `count`. Falls back to English rules if Intl is missing. */
export function pluralCategory(count: number, locale = 'en'): PluralCategory {
  try {
    return new Intl.PluralRules(locale).select(count) as PluralCategory;
  } catch {
    return count === 1 ? 'one' : 'other';
  }
}

/** Formats `count` for the locale — grouping separators, and Arabic-Indic digits under `ar`. */
export function formatCount(count: number, locale = 'en'): string {
  try {
    return count.toLocaleString(locale);
  } catch {
    return String(count);
  }
}

/**
 * Picks the right form for `count` and substitutes `{count}`.
 *
 *   pluralize(1, { one: '{count} urgent action requires attention',
 *                  other: '{count} urgent actions require attention' })
 *   // → "1 urgent action requires attention"
 */
export function pluralize(count: number, forms: PluralForms, locale = 'en'): string {
  const category = pluralCategory(count, locale);
  const chain: PluralCategory[] = [category, ...FALLBACK[category]];
  const template = chain.map(c => forms[c]).find(v => v !== undefined) ?? forms.other;
  return template.replace(/\{count\}/g, formatCount(count, locale));
}

/** Shorthand for the common "N thing / N things" case. */
export function countOf(count: number, one: string, other: string, locale = 'en'): string {
  return pluralize(count, { one: `{count} ${one}`, other: `{count} ${other}` }, locale);
}
