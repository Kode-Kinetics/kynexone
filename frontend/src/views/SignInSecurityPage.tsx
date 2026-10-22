'use client';

import { SectionHeader } from '../components/SectionHeader';
import { SignInSecurityCard } from '../components/account/SignInSecurityCard';
import { useLocale } from '../contexts/LocaleContext';

/** The signed-in person's own sign-in settings: two-step sign-in and trusted browsers. */
export function SignInSecurityPage() {
  const { t } = useLocale();
  return (
    <div className="mx-auto max-w-3xl space-y-5">
      <SectionHeader
        eyebrow={t('My account')}
        title={t('Sign-in security')}
        description={t('How you sign in to KynexOne, and the browsers that skip the code.')}
      />
      <SignInSecurityCard />
    </div>
  );
}
