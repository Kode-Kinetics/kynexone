/**
 * The currency an amount should be shown in: its own when it has one, otherwise the company's. Never a guess: with
 * neither known the formatter is handed null and shows the bare number.
 */
export function currencyForAmount(own: string | null | undefined, tenant: string | null | undefined): string | null {
  return own || tenant || null;
}
