import { getCountries, getCountryCallingCode, parsePhoneNumberFromString, type CountryCode } from 'libphonenumber-js/min';
import { normalizeDigits } from './saudiId';

export interface MobileCountry { code: string; dial: string }

export function dialCodeFor(country: string): string {
  try { return `+${getCountryCallingCode(country as CountryCode)}`; } catch { return ''; }
}

/** Dial-code picker data: every region the phone library knows. */
export function mobileCountries(): MobileCountry[] {
  return getCountries().map(code => ({ code, dial: dialCodeFor(code) })).filter(c => c.dial);
}

/** National-format examples (the "min" metadata ships none). Others fall back to a neutral pattern. */
const EXAMPLES: Record<string, string> = {
  SA: '0512345678', AE: '0501234567', BH: '36001234', KW: '50012345', OM: '92123456', QA: '33123456',
  EG: '01012345678', JO: '0791234567', PK: '03012345678', IN: '09123456789', PH: '09171234567', BD: '01712345678', LK: '0712345678', NP: '9841234567',
  GB: '07400123456', US: '2015550123',
};
export function exampleMobile(country: string): string {
  return EXAMPLES[country] ?? '';
}

const SA_MOBILE = /^(?:\+?966|00966|0)?(5\d{8})$/;

/** Parses typed input for a selected country. Returns null when it is not a valid mobile number. */
export function parseMobile(input: string, country: string): { e164: string } | null {
  const text = normalizeDigits(input ?? '').replace(/[()-]/g, '');
  if (!text) return null;
  const international = text.startsWith('+') || text.startsWith('00');
  if (country === 'SA' && !international) {
    const m = SA_MOBILE.exec(text);
    return m ? { e164: `+966${m[1]}` } : null;
  }
  const normalized = text.startsWith('00') ? `+${text.slice(2)}` : text;
  const parsed = parsePhoneNumberFromString(normalized, international ? undefined : (country as CountryCode));
  if (!parsed || !parsed.isValid()) return null;
  if (parsed.countryCallingCode === '966' && !SA_MOBILE.test(parsed.number)) return null;
  return { e164: parsed.number };
}

export function formatNational(e164: string, country: string): string {
  const parsed = parsePhoneNumberFromString(e164, country as CountryCode);
  return parsed ? parsed.formatNational() : e164;
}
