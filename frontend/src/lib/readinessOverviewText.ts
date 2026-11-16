import type { MessageParams } from '../i18n/message';
import { reviewItemLabel } from './hire/reviewLabels';

type T = (key: string, params?: MessageParams) => string;

/** The readiness item's name, from its key (known keys read in the viewer's language; an unknown one keeps the server's label). */
/** The employee record serves every country, so a key whose hire-wizard label is Saudi-specific reads neutrally here. */
const OVERVIEW_LABEL: Record<string, string> = {
  IdNumber: 'Government ID number',
};

export function readinessItemText(t: T, item: { key: string; label: string }): string {
  const neutral = OVERVIEW_LABEL[item.key];
  if (neutral) return t(neutral);
  return reviewItemLabel({ key: item.key, label: item.label }, t as Parameters<typeof reviewItemLabel>[1]);
}

/** Why an item is open. The API sends a short token ("statutory", "approval"); a server sentence is looked up as written. */
const REASON_SENTENCE: Record<string, string> = {
  statutory: 'Required by regulation',
  approval: 'Needs approval from a different person',
  recommended: 'Recommended, not required',
  expired: 'Expired',
  invalid: 'Not valid',
  provisional: 'On an interim number',
  not_evaluable: 'Cannot be checked yet',
};

export function readinessReasonText(t: T, reason: string | null | undefined): string | null {
  if (!reason) return null;
  const sentence = REASON_SENTENCE[reason];
  return t(sentence ?? reason);
}
