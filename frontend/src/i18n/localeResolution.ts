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
 * The legacy 'kynexone-locale' key is deliberately not read (see localeBoot.ts).
 */

import { LOCALE_DICTS, LOCALE_METADATA, type LocaleCode } from './translations';
import { LOCALE_CHOICE_KEY, TENANT_LOCALE_KEY } from './localeBoot';

export interface LocaleStore {
  get(key: string): string | null;
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

export function resolveLocale(store: LocaleStore, tenant: TenantLanguage): LocaleCode {
  return asLocale(store.get(LOCALE_CHOICE_KEY))
    ?? tenantDefaultLocale(tenant)
    ?? asLocale(store.get(TENANT_LOCALE_KEY))
    ?? 'en';
}
