export type IdKind = 'nationalId' | 'iqama' | 'border';
export type IdCheck = 'ok' | 'format' | 'checksum';

const ARABIC_INDIC = '٠١٢٣٤٥٦٧٨٩';
const PERSIAN = '۰۱۲۳۴۵۶۷۸۹';

/** Arabic-Indic and Persian digits become ASCII; spaces and direction marks are dropped. */
export function normalizeDigits(raw: string): string {
  return (raw ?? '')
    .replace(/[٠-٩]/g, c => String(ARABIC_INDIC.indexOf(c)))
    .replace(/[۰-۹]/g, c => String(PERSIAN.indexOf(c)))
    .replace(/[\s‎‏‪-‮]/g, '');
}

/** Luhn variant used for Saudi National ID and Iqama (10-KSA section 1). */
export function saudiIdChecksumValid(digits: string): boolean {
  if (!/^\d{10}$/.test(digits)) return false;
  let sum = 0;
  for (let i = 0; i < 10; i++) {
    let d = Number(digits[i]);
    if (i % 2 === 0) { d *= 2; if (d > 9) d -= 9; }
    sum += d;
  }
  return sum % 10 === 0;
}

const PREFIX: Record<IdKind, string> = { nationalId: '1', iqama: '2', border: '3' };

export function validateIdNumber(kind: IdKind, raw: string): IdCheck {
  const digits = normalizeDigits(raw);
  if (!/^\d{10}$/.test(digits) || digits[0] !== PREFIX[kind]) return 'format';
  if (kind === 'border') return 'ok'; // checksum unverified for Border Numbers (10-KSA: warning only)
  return saudiIdChecksumValid(digits) ? 'ok' : 'checksum';
}
