/**
 * The audit action behind a "Recent activity" line, as a short sentence in the viewer's language.
 *
 * WHY. The home feed printed the stored action code split on dots and underscores ("Payroll erp
 * posting confirmed from export"), in English under Arabic too. Known codes now have a sentence in
 * both languages (dictionary keys "audit.<code>"); any other code is humanised so a new audit
 * action still reads, and the leave log's plain words ("Approved") pass through the translator.
 */
import { humaniseEnumValue } from '../i18n/enumLabel';
import type { MessageParams } from '../i18n/translations';

type Translator = (key: string, params?: MessageParams) => string;

export function auditActionLabel(t: Translator, action: string | null | undefined): string {
  const code = (action ?? '').trim();
  if (!code) return '—';
  const key = `audit.${code.toLowerCase()}`;
  const known = t(key);
  if (known !== key) return known;
  const plain = t(code);
  if (plain !== code) return plain;
  if (/^[A-Z][a-z]+$/.test(code)) return code;
  return humaniseEnumValue(code);
}
