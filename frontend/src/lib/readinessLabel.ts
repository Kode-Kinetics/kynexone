/**
 * An activation-readiness item's label, in the viewer's language.
 *
 * WHY. The API names a missing document "{type} document" in English ("Contract document"). Only
 * a few of those sentences had Arabic keys, so the rest stayed English under Arabic. A known
 * sentence is used as written; any other "{type} document" is built from the translated type.
 */
import type { MessageParams } from '../i18n/translations';

type Translator = (key: string, params?: MessageParams) => string;

const DOCUMENT_LABEL = /^(.+) document$/;

export function readinessLabel(t: Translator, label: string): string {
  const known = t(label);
  if (known !== label) return known;
  const m = DOCUMENT_LABEL.exec(label);
  if (m) return t('{type} document', { type: t(m[1]) });
  return label;
}
