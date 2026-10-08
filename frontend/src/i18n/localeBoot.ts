/**
 * Where the UI language is stored, and the pre-paint script that applies it.
 *
 * No imports: app/layout.tsx (a server component) inlines LOCALE_BOOT into <head>, and the unit
 * tests evaluate it against a fake localStorage.
 *
 * KEYS
 *   LOCALE_CHOICE_KEY  the user's explicit choice. Written ONLY by the language switcher
 *                      (LocaleContext.setLocale). Versioned because the old key cannot be trusted.
 *   LEGACY_LOCALE_KEY  'kynexone-locale'. Builds up to Wave 0 wrote it on EVERY mount, so every
 *                      existing user (the Evostel pilot included) has 'en' there without ever
 *                      choosing it; 'en' there is ignored, or the tenant's default language would
 *                      never reach those users. Those builds never wrote anything else by
 *                      themselves, so any other supported value was the user's real choice: it is
 *                      copied to LOCALE_CHOICE_KEY once, when that key is absent (here and in
 *                      localeResolution.migrateLegacyChoice).
 *   TENANT_LOCALE_KEY  the tenant's default language as LocaleProvider last loaded it, so this
 *                      script can set `dir` before React hydrates. Never a user choice.
 */

export const LOCALE_CHOICE_KEY = 'kynexone-locale-choice-v2';
export const LEGACY_LOCALE_KEY = 'kynexone-locale';
export const TENANT_LOCALE_KEY = 'kynexone-tenant-locale';

/**
 * Apply the stored language's direction BEFORE first paint: the user's choice, else an Arabic
 * device language (localeResolution.deviceLocale), else the cached tenant default, else English. Keep the code list and the rtl map in step with LOCALE_METADATA in
 * src/i18n/translations.ts — today `ar` is the only right-to-left locale there.
 */
export const LOCALE_BOOT = `(function(){try{
var s=localStorage,c=s.getItem('${LOCALE_CHOICE_KEY}'),o=s.getItem('${LEGACY_LOCALE_KEY}');
if(c==null&&o&&/^(ar|fr|es)$/.test(o)){c=o;s.setItem('${LOCALE_CHOICE_KEY}',o);}
var n=typeof navigator!=='undefined'?((navigator.languages&&navigator.languages[0])||navigator.language||''):'';
var v=/^ar(-|$)/i.test(n)?'ar':null;
var l=c||v||s.getItem('${TENANT_LOCALE_KEY}')||'en';
if(!/^(en|ar|fr|es)$/.test(l))l='en';
var d={ar:'rtl'}[l]||'ltr';
document.documentElement.lang=l;document.documentElement.dir=d;
}catch(e){}})();`;
