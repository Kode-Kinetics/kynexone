/**
 * Which of a tenant's companies are Saudi establishments. GOSI and WPS employer IDs, and the
 * Nitaqat band, belong to a company registered in the Kingdom, so the screens that edit or read
 * them choose among these rather than taking whichever company the API listed first.
 */

const SAUDI_COUNTRY_CODES = new Set(['SA', 'SAU', 'KSA']);

export function isSaudiCompany(company: { countryCode?: string | null }): boolean {
  return SAUDI_COUNTRY_CODES.has((company.countryCode ?? '').trim().toUpperCase());
}

/**
 * The company a Saudi settings screen should show: the one already selected while it is still in
 * the list, otherwise the first Saudi company, otherwise none ('' — the screen then says there is
 * no Saudi company instead of editing a company from another country).
 */
export function selectSaudiCompany(saudi: readonly { id: string }[], currentId: string): string {
  if (currentId && saudi.some((c) => c.id === currentId)) return currentId;
  return saudi[0]?.id ?? '';
}
