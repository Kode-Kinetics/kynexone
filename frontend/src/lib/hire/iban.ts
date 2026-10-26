export type IbanCheck = 'ok' | 'saudi_structure' | 'checksum';

export function normalizeIban(raw: string): string {
  return (raw ?? '').replace(/[\s ‎‏-]/g, '').toUpperCase();
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

/** Saudi IBAN (the server's rule, shared vectors): SA + 2 check + 2 bank + 18 alphanumeric = 24, mod-97 = 1. Any other prefix fails the structure check. */
export function validateIban(raw: string): IbanCheck {
  const iban = normalizeIban(raw);
  if (!/^SA\d{2}[0-9A-Z]{20}$/.test(iban)) return 'saudi_structure';
  return mod97(iban) === 1 ? 'ok' : 'checksum';
}
