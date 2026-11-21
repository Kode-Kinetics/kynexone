import type { MessageParams } from '../i18n/message';

type T = (key: string, params?: MessageParams) => string;

/**
 * A GL account's name as the ledger stores it is English. The names the chart is seeded with are in the dictionary, so a
 * Finance reader sees them in Arabic; a name the company typed itself is not a key and is shown unchanged.
 */
export function glAccountName(t: T, name: string): string {
  return name ? t(name) : name;
}
