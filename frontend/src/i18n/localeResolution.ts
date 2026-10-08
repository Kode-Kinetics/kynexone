/**
 * Which language the UI starts in. Pure: no React, no DOM — LocaleContext supplies storage and
 * the tenant's settings, and unit/localeResolution.spec.ts proves the order.
 *
 *   1. the user's explicit choice (LOCALE_CHOICE_KEY), written only by the language switcher;
 *   2. the tenant's default language, but only once the tenant's settings have LOADED — before
 *      that, the settings are placeholders ('en'), and acting on them flipped an Arabic tenant
 *      RTL → LTR → RTL and overwrote the cached tenant language with 'en';
 *   3. the tenant default as last cached (TENANT_LOCALE_KEY);
 *   4. English.
 *
 * The legacy 'kynexone-locale' key: older builds wrote 'en' there on every mount, so 'en' says
 * nothing about the user. Any OTHER supported value was a real pick in the old switcher, and
 * `migrateLegacyChoice` carries it over to LOCALE_CHOICE_KEY once (see localeBoot.ts).
 */

import { LOCALE_DICTS, LOCALE_METADATA, type LocaleCode } from './translations';
import { LEGACY_LOCALE_KEY, LOCALE_CHOICE_KEY, TENANT_LOCALE_KEY } from './localeBoot';

export interface LocaleStore {
  get(key: string): string | null;
  set(key: string, value: string): void;
}

export interface TenantLanguage {
  /** True only after a successful localization fetch (TenantSettingsContext). */
  loaded: boolean;
  defaultLanguage?: string | null;
}

export function asLocale(v: string | null | undefined): LocaleCode | null {
  if (!v) return null;
  const two = v.slice(0, 2).toLowerCase();
  return two in LOCALE_DICTS ? (two as LocaleCode) : null;
}

/** The tenant's default language when it is known and offered in the switcher; otherwise null. */
export function tenantDefaultLocale(tenant: TenantLanguage): LocaleCode | null {
  if (!tenant.loaded) return null;
  const code = asLocale(tenant.defaultLanguage);
  return code && LOCALE_METADATA[code].selectable ? code : null;
}

/**
 * Old builds only ever wrote 'en' by themselves, so a legacy value that is a supported locale other
 * than 'en' was the user's own choice. Copy it to the versioned key once, when that key is absent.
 */
export function migrateLegacyChoice(store: LocaleStore): void {
  if (store.get(LOCALE_CHOICE_KEY) != null) return;
  const legacy = asLocale(store.get(LEGACY_LOCALE_KEY));
  if (legacy && legacy !== 'en') store.set(LOCALE_CHOICE_KEY, legacy);
}

/**
 * The device's own language (navigator.languages), when it is one the switcher offers. Only the
 * FIRST preference counts: an ar-SA phone is Arabic, an en-GB phone that also lists Arabic is not.
 */
export function deviceLocale(languages: readonly string[] | null | undefined): LocaleCode | null {
  const code = asLocale(languages?.[0]);
  return code && LOCALE_METADATA[code].selectable ? code : null;
}

/**
 * `device` is passed only by the public sign-in surfaces (/login, /welcome): there nobody has
 * signed in, so the tenant's language may be unknown on this phone, and someone scanning a slip on
 * an Arabic phone should be greeted in Arabic. It ranks after the explicit choice, before tenant
 * defaults.
 */
export function resolveLocale(store: LocaleStore, tenant: TenantLanguage, device?: readonly string[] | null): LocaleCode {
  migrateLegacyChoice(store);
  return asLocale(store.get(LOCALE_CHOICE_KEY))
    ?? (device ? deviceLocale(device) : null)
    ?? tenantDefaultLocale(tenant)
    ?? asLocale(store.get(TENANT_LOCALE_KEY))
    ?? 'en';
}
