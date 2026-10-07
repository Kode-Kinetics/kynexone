'use client';

import { useEffect, useState } from 'react';
import { ShieldCheck } from 'lucide-react';

/**
 * Grace-period prompt for mandatory two-step sign-in (backend: PrivilegedMfaPolicy).
 *
 * Shown to a signed-in user whose role requires MFA but who has no factor yet, BEFORE the
 * enforcement date. It never blocks the page: from the date, sign-in itself asks for enrolment.
 * "Set up" hands off to the sign-in page's existing enrolment step (onStart ends this session).
 * "Remind me later" hides it for this browser session only.
 *
 * The primary button is blue-600, not the brand sapphire: white on #2F6BFF is 4.49:1, just under
 * WCAG AA's 4.5:1 for 14px text (axe colour-contrast, e2e/accessibility.spec.ts). blue-600 is 5.17:1.
 */
export interface MfaPromptStatus {
  promptToEnroll: boolean;
  enforceFromUtc: string | null;
}

interface Props {
  loadStatus: () => Promise<MfaPromptStatus>;
  onStart: () => Promise<void>;
  /** sessionStorage key for "remind me later"; differs for tenant and platform consoles. */
  dismissKey: string;
  tone?: 'light' | 'dark';
  /** Outer spacing, so the host layout keeps its own gutters. */
  className?: string;
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime())
    ? iso
    : d.toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' });
}

export function MfaEnrollmentPrompt({ loadStatus, onStart, dismissKey, tone = 'light', className = '' }: Props) {
  const [status, setStatus] = useState<MfaPromptStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    let dismissed = false;
    try { dismissed = sessionStorage.getItem(dismissKey) === '1'; } catch { /* storage unavailable */ }
    if (dismissed) return;
    let cancelled = false;
    loadStatus()
      .then((s) => { if (!cancelled) setStatus(s); })
      .catch(() => { /* the prompt is advisory; never surface its own failure */ });
    return () => { cancelled = true; };
  }, [loadStatus, dismissKey]);

  if (!status?.promptToEnroll) return null;

  const when = status.enforceFromUtc
    ? `From ${formatDate(status.enforceFromUtc)} you will need a code from an authenticator app to sign in.`
    : 'Soon you will need a code from an authenticator app to sign in.';

  const start = async () => {
    setBusy(true); setError('');
    try { await onStart(); }
    catch { setError('Could not start setup. Please try again.'); setBusy(false); }
  };

  const later = () => {
    try { sessionStorage.setItem(dismissKey, '1'); } catch { /* storage unavailable */ }
    setStatus(null);
  };

  const palette = tone === 'dark'
    ? 'border-amber-400/30 bg-amber-400/10 text-amber-100'
    : 'border-amber-200 bg-amber-50 text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200';
  // The dark console does not set the `dark` class, so its error colour is chosen by tone.
  const errorColour = tone === 'dark' ? 'text-red-300' : 'text-red-700 dark:text-red-400';

  return (
    <section aria-label="Two-step sign-in" className={`${className} flex flex-col gap-3 rounded-xl border px-4 py-3 sm:flex-row sm:items-center ${palette}`}>
      <ShieldCheck className="hidden h-5 w-5 shrink-0 sm:block" aria-hidden />
      <div className="min-w-0 flex-1 text-sm">
        <p className="font-semibold">Two-step sign-in is required for your role</p>
        <p className="mt-0.5 opacity-90">{when} Setting it up takes about a minute; you will sign in again afterwards.</p>
        {error && <p role="alert" className={`mt-1 font-medium ${errorColour}`}>{error}</p>}
      </div>
      <div className="flex shrink-0 gap-2">
        <button type="button" onClick={later} disabled={busy}
          className="rounded-lg px-3 py-2 text-sm font-medium opacity-80 hover:opacity-100 disabled:opacity-50">
          Remind me later
        </button>
        <button type="button" onClick={start} disabled={busy}
          className="rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-60">
          {busy ? 'Starting…' : 'Set up two-step sign-in'}
        </button>
      </div>
    </section>
  );
}
