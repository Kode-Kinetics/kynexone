'use client';

import { memo, useState } from 'react';
import { ShieldAlert, X } from 'lucide-react';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';

/**
 * "HR gave you a new sign-in code on {date}. If you didn't ask for it, tell HR."
 *
 * Contract Amendment 3 (F1): when HR resets an ACTIVE login, the old password keeps working until
 * the code is redeemed, so the person already signed in is the one who can say "that wasn't me".
 * The sign-in page shows this at a fresh sign-in; this banner covers a session that was already
 * open. It reads `pendingResetNotice` from the user the auth context loaded from /api/auth/me (no
 * extra request). Dismissing hides it for this browser session only, per issued code; there is
 * deliberately no "cancel" — only HR can act on it.
 */
const dismissKey = (issued: string) => `kynexone-reset-notice-dismissed:${issued}`;

/**
 * Dismissals live in module memory as well as sessionStorage, so a re-render, a remount of the
 * shell or a refreshed /auth/me never brings back a notice the person already closed, and a
 * browser with storage blocked still keeps it closed until reload.
 */
const dismissedThisSession = new Set<string>();

function isDismissed(issued: string): boolean {
  if (dismissedThisSession.has(issued)) return true;
  try { return sessionStorage.getItem(dismissKey(issued)) === '1'; } catch { return false; }
}

/** Hide the notice for the rest of this browser session (the sign-in page already showed it). */
export function markResetNoticeSeen(issued: string): void {
  dismissedThisSession.add(issued);
  try { sessionStorage.setItem(dismissKey(issued), '1'); } catch { /* storage unavailable */ }
}

/**
 * Memoised on its only input (the issued date, a string): a new user object from a refreshed
 * /auth/me with the same notice re-renders nothing, and the element keeps its identity.
 */
export function ResetCodeNotice({ className = '' }: { className?: string }) {
  const { user } = useAuth();
  return <ResetCodeNoticeView issued={user?.pendingResetNotice?.date ?? ''} className={className} />;
}

const ResetCodeNoticeView = memo(function ResetCodeNoticeView({ issued, className }: { issued: string; className: string }) {
  const { t } = useLocale();
  const format = useFormat();
  const [, setClosed] = useState(0);

  if (!issued || isDismissed(issued)) return null;

  const dismiss = () => {
    markResetNoticeSeen(issued);
    setClosed((n) => n + 1);
  };

  return (
    <section role="alert" data-testid="reset-code-notice"
      className={`${className} flex items-start gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200`}>
      <ShieldAlert className="mt-0.5 h-5 w-5 shrink-0" aria-hidden />
      <p className="min-w-0 flex-1 text-sm font-medium">
        {t("HR gave you a new sign-in code on {date}. If you didn't ask for it, tell HR.", { date: format.date(issued, 'long') })}
      </p>
      <button type="button" onClick={dismiss} aria-label={t('Hide this message')}
        className="grid h-8 w-8 shrink-0 place-items-center rounded-lg opacity-80 hover:opacity-100">
        <X className="h-4 w-4" aria-hidden />
      </button>
    </section>
  );
});
