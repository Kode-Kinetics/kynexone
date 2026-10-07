'use client';

import { useState } from 'react';
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

/** Hide the notice for the rest of this browser session (the sign-in page already showed it). */
export function markResetNoticeSeen(issued: string): void {
  try { sessionStorage.setItem(dismissKey(issued), '1'); } catch { /* storage unavailable */ }
}

export function ResetCodeNotice({ className = '' }: { className?: string }) {
  const { user } = useAuth();
  const { t } = useLocale();
  const format = useFormat();
  const issued = user?.pendingResetNotice?.date ?? '';
  const key = dismissKey(issued);
  const [dismissed, setDismissed] = useState<string | null>(null);

  if (!issued || dismissed === issued) return null;
  try { if (sessionStorage.getItem(key) === '1') return null; } catch { /* storage unavailable: show it */ }

  const dismiss = () => {
    try { sessionStorage.setItem(key, '1'); } catch { /* storage unavailable: hidden until reload */ }
    setDismissed(issued);
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
}
