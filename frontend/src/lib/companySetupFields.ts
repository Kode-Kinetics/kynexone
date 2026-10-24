import { msg } from '../i18n/translations';

/**
 * Field-level helpers for the data-entry steps of the company setup wizard (steps 2, 3 and 6): input masks,
 * deterministic clean-ups and suggestions. Pure functions, no AI, unit-tested in unit/companySetupFields.spec.ts.
 * The rules that block Next stay in companySetupWizard.ts (validateStep); these only shape what is typed and
 * offer non-blocking hints.
 */

// ── Digits ───────────────────────────────────────────────────────────────────

/** Arabic-Indic (٠-٩) and Persian (۰-۹) digits as ASCII, so a number typed on an Arabic keyboard is the same number. */
export function toAsciiDigits(value: string): string {
  return value.replace(/[٠-٩۰-۹]/g, ch => {
    const code = ch.charCodeAt(0);
    return String(code >= 0x06f0 ? code - 0x06f0 : code - 0x0660);
  });
}

/** Digits only (any script), at most `max` of them. Spaces and dashes typed from a certificate are dropped. */
export function digitsOnly(value: string, max?: number): string {
  const digits = toAsciiDigits(value).replace(/[^0-9]/g, '');
  return max ? digits.slice(0, max) : digits;
}

/**
 * MOL establishment number as it is typed: digits, joined by at most one hyphen ("7-1234567"). Any separator a
 * keyboard offers — a dot or comma on a phone's number pad, a space, a slash, an en dash — becomes that hyphen.
 * A hyphen at the end is kept while the second part is still being typed.
 */
export function maskMol(value: string): string {
  const ascii = toAsciiDigits(value).replace(/[^0-9]+/g, '-').replace(/^-+/, '');
  const dash = ascii.indexOf('-');
  const joined = dash < 0 ? ascii : `${ascii.slice(0, dash + 1)}${ascii.slice(dash + 1).replace(/-/g, '')}`;
  return joined.slice(0, 15);
}

/** Saudi CR is 10 digits; elsewhere a registration number may hold letters ("CN-1234567"), so only digits are normalised. */
export function maskRegistration(value: string, saudi: boolean): string {
  return saudi ? digitsOnly(value, 10) : toAsciiDigits(value);
}

/** Saudi VAT registration number (ZATCA): 15 digits, starting and ending with 3. */
export const isSaudiVatNumber = (value: string) => /^3[0-9]{13}3$/.test(value.trim());

// ── Names ────────────────────────────────────────────────────────────────────

export const hasArabicLetters = (value: string | null | undefined) => /[؀-ۿݐ-ݿﭐ-﷿ﹰ-﻿]/.test(value ?? '');

// ── Email ────────────────────────────────────────────────────────────────────

/** Mirrors backend CompanyEmailDomainRules.PublicDomains: never a company's sign-in domain (owner rule: no mailboxes). */
export const PUBLIC_EMAIL_DOMAINS: ReadonlySet<string> = new Set([
  'gmail.com', 'googlemail.com', 'outlook.com', 'outlook.sa', 'hotmail.com', 'hotmail.co.uk', 'live.com', 'msn.com',
  'yahoo.com', 'yahoo.co.uk', 'ymail.com', 'rocketmail.com', 'icloud.com', 'me.com', 'mac.com', 'aol.com',
  'proton.me', 'protonmail.com', 'pm.me', 'gmx.com', 'gmx.net', 'mail.com', 'zoho.com', 'yandex.com', 'yandex.ru',
  'tutanota.com', 'tuta.io', 'fastmail.com', 'hey.com', 'qq.com', '163.com', '126.com',
]);

export const isPublicEmailDomain = (domain: string | null | undefined) => PUBLIC_EMAIL_DOMAINS.has((domain ?? '').trim().toLowerCase());

/** The domain after "@" of an email, lower-cased; '' when there is none. */
export function emailHost(email: string | null | undefined): string {
  const v = (email ?? '').trim().toLowerCase();
  const at = v.lastIndexOf('@');
  return at >= 0 ? v.slice(at + 1) : '';
}

/**
 * What people paste into "Work email domain" — "@acme.sa", "https://www.acme.sa/about", "HR@Acme.SA" — as the bare
 * domain the field needs ("acme.sa"). Anything that is not recognisably a domain is returned trimmed, unchanged, so
 * the field's own message still explains it.
 */
export function cleanEmailDomain(raw: string | null | undefined): string {
  let v = (raw ?? '').trim().toLowerCase();
  if (!v) return '';
  v = v.replace(/^mailto:/, '').replace(/^[a-z][a-z0-9+.-]*:\/\//, '');
  if (v.includes('@')) v = v.slice(v.lastIndexOf('@') + 1);
  v = v.split(/[/?#]/)[0].replace(/:\d+$/, '').replace(/^www\./, '').replace(/\.+$/, '');
  return v;
}

/** "sara" with a company domain becomes "sara@acme.sa"; "sara@" too. Anything else is left as typed. */
export function completeEmail(value: string, domain: string | null | undefined): string {
  const v = value.trim();
  const d = (domain ?? '').trim().toLowerCase();
  if (!v || !d) return v;
  if (!v.includes('@')) return /^[^\s@]+$/.test(v) ? `${v}@${d}` : v;
  return v.endsWith('@') && v.indexOf('@') === v.length - 1 ? `${v}${d}` : v;
}

// ── Months, currencies, time zones ───────────────────────────────────────────

/** "2026-11" → "November 2026" in the reader's language; '' when it is not a month. */
export function monthLabel(month: string | null | undefined, locale: string): string {
  const m = /^([0-9]{4})-(0[1-9]|1[0-2])$/.exec(month ?? '');
  if (!m) return '';
  try {
    return new Intl.DateTimeFormat(locale === 'ar' ? 'ar-SA-u-ca-gregory-nu-latn' : locale, { month: 'long', year: 'numeric', timeZone: 'UTC' })
      .format(new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, 1)));
  } catch { return month ?? ''; }
}

/** True when "YYYY-MM" is before the month of `today`. */
export function isPastMonth(month: string | null | undefined, today: Date = new Date()): boolean {
  const m = /^([0-9]{4})-(0[1-9]|1[0-2])$/.exec(month ?? '');
  if (!m) return false;
  return Number(m[1]) * 12 + Number(m[2]) < today.getFullYear() * 12 + today.getMonth() + 1;
}

/** "SAR — Saudi riyal" (the name in the reader's language); the bare code when the browser has no name for it. */
export function currencyLabel(code: string, locale: string): string {
  try {
    const name = new Intl.DisplayNames([locale], { type: 'currency' }).of(code);
    return name && name !== code ? `${code} — ${name}` : code;
  } catch { return code; }
}

/** The cities the wizard's time zones are named after (dictionary keys; the view translates them). */
export const TIME_ZONE_CITY: Record<string, string> = {
  'Asia/Riyadh': msg('Riyadh'), 'Asia/Dubai': msg('Dubai'), 'Asia/Qatar': msg('Doha'), 'Asia/Kuwait': msg('Kuwait City'),
  'Asia/Bahrain': msg('Manama'), 'Asia/Muscat': msg('Muscat'), 'Africa/Cairo': msg('Cairo'), 'Asia/Kolkata': msg('India (Kolkata)'),
  'Asia/Karachi': msg('Karachi'), 'Europe/London': msg('London'), 'America/New_York': msg('New York'), UTC: msg('UTC'),
};

/** "GMT+3" for a zone today, '' when the browser cannot say. Digits stay Latin so the offset reads the same in Arabic. */
export function timeZoneOffset(zone: string, at: Date = new Date()): string {
  if (zone === 'UTC') return '';
  try {
    const part = new Intl.DateTimeFormat('en', { timeZone: zone, timeZoneName: 'shortOffset' }).formatToParts(at).find(p => p.type === 'timeZoneName');
    return part?.value ?? '';
  } catch { return ''; }
}

// ── Branches and locations ───────────────────────────────────────────────────

/**
 * The city a Saudi CR was issued in, from its first four digits (the issuing office). Only offices that are
 * unambiguous are listed; anything else returns undefined and nothing is suggested.
 */
const CR_OFFICE_CITY: Record<string, string> = {
  '1010': msg('Riyadh'), '4030': msg('Jeddah'), '4031': msg('Makkah'), '4032': msg('Taif'), '4650': msg('Madinah'),
  '2050': msg('Dammam'), '2051': msg('Al Khobar'), '2055': msg('Jubail'), '5850': msg('Abha'), '5855': msg('Khamis Mushait'),
};
export function cityFromCr(cr: string | null | undefined): string | undefined {
  const digits = digitsOnly(cr ?? '');
  return digits.length === 10 ? CR_OFFICE_CITY[digits.slice(0, 4)] : undefined;
}

/** A free branch code: "HQ" for the first, then BR2, BR3… skipping any already used. */
export function suggestBranchCode(existing: readonly string[]): string {
  const used = new Set(existing.map(c => c.trim().toUpperCase()));
  if (existing.length === 0) return 'HQ';
  for (let n = existing.length + 1; ; n += 1) if (!used.has(`BR${n}`)) return `BR${n}`;
}
