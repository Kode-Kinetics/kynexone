import { createFormatter } from '../format';

/** Amounts through the app formatter (A12): the viewer's language, Latin digits, the company currency. */
export function formatAmount(value: number, currency: string, locale: string): string {
  return createFormatter({ locale, currency }).money(value, currency, { decimals: 2 });
}
