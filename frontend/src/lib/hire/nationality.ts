import { getCountries } from 'libphonenumber-js/min';

export type IdType = 'nationalId' | 'iqama' | 'border' | 'gccId' | 'passport';
export type NationalityClass = 'saudi' | 'gcc' | 'expat' | 'unknown';

export interface CountryOption { code: string; nameEn: string; nameAr: string }

export const SAUDI = 'SA';
export const GCC_CODES = ['AE', 'BH', 'KW', 'OM', 'QA'] as const;
/** Regions the phone library lists that are not nationalities. */
const NOT_NATIONALITIES = new Set(['AC', 'TA']);

/** The ISO 3166 alpha-2 list with EN and AR names from the browser's own display names. Offline, no request. */
export function buildCountryOptions(codes?: readonly string[]): CountryOption[] {
  const en = new Intl.DisplayNames(['en'], { type: 'region' });
  const ar = new Intl.DisplayNames(['ar'], { type: 'region' });
  const list = (codes ?? getCountries()).filter(c => /^[A-Z]{2}$/.test(c) && !NOT_NATIONALITIES.has(c));
  return list
    .map(code => ({ code, nameEn: en.of(code) ?? code, nameAr: ar.of(code) ?? code }))
    .filter(o => o.nameEn !== o.code);
}

/** Case, diacritics, tatweel and alef/ya/ta-marbuta variants folded so any spelling of an Arabic name matches. */
export function foldText(text: string): string {
  return (text ?? '').normalize('NFKD')
    .replace(/[\u0300-\u036F\u064B-\u065F\u0670\u00AD\u0640]/g, '')
    .replace(/[إأآٱ]/g, 'ا').replace(/ى/g, 'ي').replace(/ة/g, 'ه').replace(/ؤ/g, 'و').replace(/ئ/g, 'ي')
    .toLowerCase().trim();
}

/** Saudi Arabia pinned first, then the GCC, then A-Z. A query keeps that order inside its matches (name matches that start the query rank above those that merely contain it). */
export function searchCountries(options: readonly CountryOption[], query: string, locale: string): CountryOption[] {
  const q = foldText(query);
  const byName = (a: CountryOption, b: CountryOption) => locale === 'ar' ? a.nameAr.localeCompare(b.nameAr, 'ar') : a.nameEn.localeCompare(b.nameEn, 'en');
  const rank = (o: CountryOption) => o.code === SAUDI ? 0 : (GCC_CODES as readonly string[]).includes(o.code) ? 1 : 2;
  const score = (o: CountryOption) => {
    if (!q) return 1;
    const names = [foldText(o.nameEn), foldText(o.nameAr)];
    if (names.some(n => n.startsWith(q) || n.split(/\s+/).some(w => w.startsWith(q)))) return 0;
    if (names.some(n => n.includes(q))) return 1;
    return o.code.toLowerCase() === q ? 0 : -1;
  };
  return options
    .map(o => ({ o, s: score(o) }))
    .filter(x => x.s >= 0)
    .sort((a, b) => rank(a.o) - rank(b.o) || (q ? a.s - b.s : 0) || byName(a.o, b.o))
    .map(x => x.o);
}

export function nationalityClass(code: string): NationalityClass {
  if (!code) return 'unknown';
  if (code === SAUDI) return 'saudi';
  return (GCC_CODES as readonly string[]).includes(code) ? 'gcc' : 'expat';
}

/** The identity document that fits the nationality; null until a nationality is chosen. */
export function defaultIdType(code: string, residentInKsa = true): IdType | null {
  switch (nationalityClass(code)) {
    case 'saudi': return 'nationalId';
    case 'gcc': return 'gccId';
    case 'expat': return residentInKsa ? 'iqama' : 'passport';
    default: return null;
  }
}
