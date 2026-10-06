'use client';

import { useState } from 'react';
import { WifiOff } from 'lucide-react';
import { LOCALE_DICTS, translate, type LocaleCode } from '@/src/i18n/translations';
import type { AuthLoadError } from '@/src/lib/authLoadState';

/**
 * Full-page "can't reach the server" state, shown instead of redirecting to /login when the
 * session check failed for a network or server reason (AuthContext.authError). The session is
 * kept; Retry re-runs the check.
 *
 * This renders OUTSIDE the tenant shell (before AppLayout mounts LocaleProvider), so it reads the
 * language from <html lang>, which the root layout's blocking boot script sets from the user's
 * stored choice before first paint.
 */
function documentLocale(): LocaleCode {
  if (typeof document === 'undefined') return 'en';
  const lang = document.documentElement.lang;
  return (lang && lang in LOCALE_DICTS ? lang : 'en') as LocaleCode;
}

export function ServerUnreachable({ reason, onRetry }: { reason: AuthLoadError; onRetry: () => Promise<void> }) {
  const [retrying, setRetrying] = useState(false);
  const locale = documentLocale();
  const t = (key: string) => translate(locale, key);

  const retry = async () => {
    setRetrying(true);
    try {
      await onRetry();
    } finally {
      setRetrying(false);
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-lightBg px-4 dark:bg-midnight">
      <div role="alert" className="surface w-full max-w-md p-8 text-center" data-testid="server-unreachable">
        <WifiOff className="mx-auto h-10 w-10 text-slate-400" aria-hidden="true" />
        <h1 className="mt-4 text-lg font-semibold text-slate-900 dark:text-white">{t("Can't reach the server right now.")}</h1>
        <p className="mt-2 text-sm text-slate-600 dark:text-slate-300">
          {reason === 'network'
            ? t('Your device could not connect to KynexOne. Check your internet connection, then retry.')
            : t('KynexOne is not responding normally. This usually clears within a minute. Retry shortly.')}
        </p>
        <p className="mt-2 text-sm text-slate-600 dark:text-slate-300">{t('You are still signed in. Nothing you saved has been lost.')}</p>
        <button type="button" className="btn-primary mt-6" onClick={retry} disabled={retrying}>
          {retrying ? t('Retrying…') : t('Retry')}
        </button>
      </div>
    </div>
  );
}
