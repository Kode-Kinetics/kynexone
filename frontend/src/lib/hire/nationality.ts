import { NATIONALITY_CATALOG } from './nationalityCatalog';

export type IdType = 'nationalId' | 'iqama' | 'border' | 'gccId' | 'passport';
export type NationalityClass = 'saudi' | 'gcc' | 'expat' | 'unknown';

export interface CountryOption { code: string; nameEn: string; nameAr: string }

export const SAUDI = 'SA';
export const GCC_CODES = ['AE', 'BH', 'KW', 'OM', 'QA'] as const;
/**
 * The country list the picker falls back to: the backend catalog's own ISO 3166-1 rows, offline, no request. A code list
 * narrows it (used by tests).
 */
export function buildCountryOptions(codes?: readonly string[]): CountryOption[] {
  const keep = codes ? new Set(codes) : null;
  return NATIONALITY_CATALOG.filter(o => !keep || keep.has(o.code)).map(o => ({ ...o }));
}

/**
 * GET /api/reference/nationalities rows are { iso2, nameEn, nameAr, class }. A row without a two-letter code or a name is
 * dropped, never turned into an option whose code is undefined. An empty result means "use the built-in list".
 */
export function countryOptionsFromApi(rows: ReadonlyArray<Record<string, unknown>> | null | undefined): CountryOption[] {
  const seen = new Set<string>();
  const out: CountryOption[] = [];
  for (const r of rows ?? []) {
    const code = String(r.iso2 ?? r.code ?? '').toUpperCase();
    const nameEn = String(r.nameEn ?? '').trim();
    const nameAr = String(r.nameAr ?? '').trim();
    if (!/^[A-Z]{2}$/.test(code) || !nameEn || seen.has(code)) continue;
    seen.add(code);
    out.push({ code, nameEn, nameAr: nameAr || nameEn });
  }
  return out;
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
