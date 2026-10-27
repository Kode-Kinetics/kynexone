export type IbanCheck = 'ok' | 'saudi_structure' | 'format' | 'checksum';

export interface SaBank { code: string; nameEn: string; nameAr: string }

export function normalizeIban(raw: string): string {
  return (raw ?? '').replace(/[\s ‎‏-]/g, '').toUpperCase();
}

function mod97(iban: string): number {
  const rearranged = iban.slice(4) + iban.slice(0, 4);
  let remainder = 0;
  for (const ch of rearranged) {
    const v = /[A-Z]/.test(ch) ? String(ch.charCodeAt(0) - 55) : ch;
    for (const digit of v) remainder = (remainder * 10 + Number(digit)) % 97;
  }
  return remainder;
}

/** IBAN length by country (ISO 13616 registry) for the countries a GCC employer hires from or banks in. Others only get the generic 15 to 34 check. */
const IBAN_LENGTH: Record<string, number> = {
  SA: 24, AE: 23, BH: 22, KW: 30, OM: 23, QA: 29, JO: 30, LB: 28, EG: 29, IQ: 23, TR: 26, PK: 24, GB: 22, DE: 22, FR: 27, ES: 24, IT: 27, NL: 18, IE: 22, CH: 21, BE: 16, AT: 20, PT: 25, SE: 24, DK: 18, NO: 15,
};

/**
 * CD29. The Saudi rule (SA + 2 check + 2 bank + 18 alphanumeric = 24, mod-97 = 1) applies only to a Saudi company (the server's rule,
 * shared vectors). For any other company country: generic shape, the country's own length when known, and mod-97.
 * 'saudi_structure' = wrong shape for a Saudi company; 'format' = wrong shape elsewhere; 'checksum' = right shape, wrong check digits.
 */
export function validateIban(raw: string, companyCountry = 'SA'): IbanCheck {
  const iban = normalizeIban(raw);
  if (companyCountry === 'SA') {
    if (!/^SA\d{2}[0-9A-Z]{20}$/.test(iban)) return 'saudi_structure';
    return mod97(iban) === 1 ? 'ok' : 'checksum';
  }
  if (!/^[A-Z]{2}\d{2}[0-9A-Z]{11,30}$/.test(iban)) return 'format';
  const expected = IBAN_LENGTH[iban.slice(0, 2)];
  if (expected !== undefined && iban.length !== expected) return 'format';
  return mod97(iban) === 1 ? 'ok' : 'checksum';
}

/** The two-digit SAMA bank code (IBAN characters 5 and 6) of a Saudi-shaped IBAN, or '' for anything else. */
export function ibanBankCode(raw: string): string {
  const iban = normalizeIban(raw);
  return /^SA\d{2}[0-9A-Z]{2}/.test(iban) ? iban.slice(4, 6) : '';
}

/** The bank's name in the viewer's language, or '' when the code or the list is unknown. */
export function bankNameFor(raw: string, banks: readonly SaBank[] | null | undefined, locale: string): string {
  const code = ibanBankCode(raw);
  if (!code || !banks) return '';
  const bank = banks.find(b => b.code === code);
  return bank ? (locale === 'ar' && bank.nameAr ? bank.nameAr : bank.nameEn) : '';
}
