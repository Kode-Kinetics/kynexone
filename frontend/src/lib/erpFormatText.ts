import type { MessageParams } from '../i18n/message';

type T = (key: string, params?: MessageParams) => string;

/**
 * The ERP journal formats are described by the API in English (JournalExportFormatters). A known sentence is shown from the
 * dictionary; a template format keeps its product name and has only the template note translated; anything else is shown as sent.
 * Column names inside the generic format's note are the file's own headers and stay Latin.
 */
const TEMPLATE_SUFFIX = ' (template – validate in your ERP test instance)';

export function erpFormatName(t: T, displayName: string): string {
  const whole = t(displayName);
  if (whole !== displayName) return whole;
  if (displayName.endsWith(TEMPLATE_SUFFIX)) {
    const note = t(TEMPLATE_SUFFIX.trim());
    if (note === TEMPLATE_SUFFIX.trim()) return displayName;
    // The product name stays Latin, so it is isolated from the surrounding right-to-left text.
    return `\u2066${displayName.slice(0, -TEMPLATE_SUFFIX.length)}\u2069 ${note}`;
  }
  return displayName;
}

export function erpFormatDisclaimer(t: T, text: string): string {
  return t(text);
}

/** API sentences that start in lower case are keyed by a capitalised sentence (a dictionary key may not look like a fragment). */
const OPTION_KEYS: Record<string, string> = {
  'true adds a UTF-8 byte-order mark so Excel opens Arabic text correctly. Leave it off (false) for an ERP import.': 'Set to true to add a UTF-8 byte-order mark so Excel opens Arabic text correctly. Leave it off (false) for an ERP import.',
};

/** An ERP setting's label or description. */
export function erpOptionText(t: T, text: string): string {
  return t(OPTION_KEYS[text] ?? text);
}
