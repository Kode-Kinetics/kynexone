'use client';

import { SignInSecurityCard } from '../components/account/SignInSecurityCard';
import { useLocale } from '../contexts/LocaleContext';

/**
 * The signed-in person's own sign-in settings: two-step sign-in and trusted browsers.
 *
 * Start-aligned like every other page (no mx-auto), with a reading width. The H1 is the app's page
 * title (text-2xl extrabold, as Leave, Payroll, Employees…); SectionHeader's larger sm:text-3xl is the
 * wizard's. The eyebrow drops capitals and tracking in Arabic, at 14px (12px × ~1.15).
 */
export function SignInSecurityPage() {
  const { t } = useLocale();
  return (
    <div className="max-w-3xl space-y-5">
      <div className="min-w-0">
        <p className="text-xs font-bold uppercase tracking-[0.18em] text-sapphire rtl:text-sm rtl:font-semibold rtl:normal-case rtl:tracking-normal dark:text-cyanAccent">
          {t('My account')}
        </p>
        <h1 className="mt-1 text-2xl font-extrabold text-slate-950 dark:text-white">{t('Sign-in security')}</h1>
        <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-600 dark:text-slate-400">
          {t('How you sign in to KynexOne, and the browsers that skip the code.')}
        </p>
      </div>
      <SignInSecurityCard />
    </div>
  );
}
