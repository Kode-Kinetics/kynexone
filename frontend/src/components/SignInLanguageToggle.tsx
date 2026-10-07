'use client';

import { useLocale } from '../contexts/LocaleContext';
import { LOCALE_METADATA } from '../i18n/translations';

/**
 * "English / العربية" on the sign-in card and the first-sign-in screen.
 *
 * It writes the SAME choice the in-app switcher writes (LocaleContext.setLocale → localStorage), so
 * the language picked here is the language the employee lands in after signing in, and the root
 * layout's boot script applies it before first paint next time. Each option is labelled in its own
 * language, so it can be found by someone who cannot read the other one.
 */
export function SignInLanguageToggle() {
  const { locale, setLocale, t } = useLocale();
  const arabic = locale === 'ar';
  return (
    <div className="lx-langbar" role="group" aria-label={t('Language')}>
      <button type="button" className="lx-lang-opt" lang="en" dir="ltr" aria-pressed={!arabic}
        onClick={() => setLocale('en')} data-testid="signin-lang-en">
        {LOCALE_METADATA.en.native}
      </button>
      <span className="lx-lang-sep" aria-hidden>/</span>
      <button type="button" className="lx-lang-opt" lang="ar" dir="rtl" aria-pressed={arabic}
        onClick={() => setLocale('ar')} data-testid="signin-lang-ar">
        {LOCALE_METADATA.ar.native}
      </button>
    </div>
  );
}
