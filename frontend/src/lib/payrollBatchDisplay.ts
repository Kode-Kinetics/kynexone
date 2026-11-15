import type { MessageParams } from '../i18n/translations';
import { isCustomFormatId } from '../api/saudiBankExports';

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

const ACCEPTANCE_KEYS: Record<string, string> = {
  'specification-implemented; bank-acceptance-not-verified': 'enum.BankAcceptance.SpecificationImplemented',
  'customer-configured-layout; bank-acceptance-not-verified': 'enum.BankAcceptance.CustomerConfigured',
};

/** The honest acceptance caveat of a bank format, in the viewer's language (the API sends a code-like English string). */
export function bankAcceptanceLabel(t: Translator, status: string | null | undefined): string {
  const text = (status ?? '').trim();
  const key = ACCEPTANCE_KEYS[text];
  return key ? t(key) : text;
}

/**
 * A bank format's display name for a stored format id (the generated export records only the id). It uses the same name source
 * as the Format select (the API's format list, translated; a customer template carries the "custom template" suffix) and
 * falls back to the id when the format is not in the list.
 */
export function bankFormatLabel(t: Translator, formats: ReadonlyArray<{ id: string; name: string }>, id: string | null | undefined, customSuffix: string): string {
  const formatId = (id ?? '').trim();
  const format = formats.find((f) => f.id === formatId);
  if (!format) return formatId;
  return isCustomFormatId(formatId) ? `${t(format.name)} (${customSuffix})` : t(format.name);
}
