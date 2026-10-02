'use client';

import { AlertCircle, AlertTriangle, CheckCircle2, Clock, HelpCircle } from 'lucide-react';

// Four tones, one per distinct commercial meaning. Green is reserved for "nothing left to do":
//   red    = Blocked                       → "can't activate yet"       → "Incomplete · N"
//   amber  = NeedsAttention                → "activatable, gaps remain" → "Needs attention · N"
//   amber  = required doc/ID expiring/expired (time)                    → "Renewal due"
//   green  = Ready — and only the literal "Ready" state                 → "Ready"
//   slate  = an unrecognised / missing state we cannot evaluate         → "Not evaluated"
// NeedsAttention must NEVER read as green: those employees can be activated but are not
// necessarily payable (e.g. a statutory gap enforced at the pay gate rather than the
// activation gate), so a green badge would be a false all-clear. Likewise an unknown state
// falls through to slate, never to green — we do not guess readiness we were not told.
// Every tone pairs an icon with text — never colour alone (WCAG). The badge is the
// click-target into the activation checklist.

type Tone = 'emerald' | 'amber' | 'rose' | 'slate';

const toneClasses: Record<Tone, string> = {
  emerald: 'bg-emeraldZ/10 text-emerald-700 ring-emeraldZ/20 dark:bg-emeraldZ/10 dark:text-emerald-300 dark:ring-emeraldZ/20',
  amber: 'bg-amber-400/15 text-amber-700 ring-amber-400/25 dark:bg-amber-400/10 dark:text-amber-300 dark:ring-amber-400/20',
  rose: 'bg-rose-500/10 text-rose-700 ring-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300 dark:ring-rose-500/20',
  slate: 'bg-slate-100 text-slate-600 ring-slate-200 dark:bg-white/10 dark:text-slate-300 dark:ring-white/10',
};

export interface ReadinessBadgeProps {
  /** Server readiness state: "Ready" | "NeedsAttention" | "Blocked". Anything else — including
   *  a new server state or a blank string — resolves to the neutral "Not evaluated" badge. */
  state: string;
  /** Count of outstanding gaps (drives the "· N" sub-count on red and amber). */
  blockersCount: number;
  /** True when a required statutory ID/doc is expiring soon or expired — reallocates to amber. */
  expiring?: boolean;
  /** When provided, the badge becomes a button that opens the checklist. */
  onClick?: () => void;
  /** Optional extra context for the tooltip (e.g. the completeness %). */
  title?: string;
}

function resolve(state: string, blockersCount: number, expiring?: boolean): {
  tone: Tone;
  Icon: typeof CheckCircle2;
  label: string;
  hint: string;
} {
  if (state === 'Blocked') {
    return {
      tone: 'rose',
      Icon: AlertTriangle,
      label: blockersCount > 0 ? `Incomplete · ${blockersCount}` : 'Incomplete',
      hint: 'Cannot be activated yet — open the checklist to see what is missing',
    };
  }
  if (state === 'NeedsAttention') {
    // Activatable, but gaps remain — some of them only bite later (e.g. at the pay gate).
    // Deliberately amber, never green: a green badge here is a false all-clear.
    return {
      tone: 'amber',
      Icon: AlertCircle,
      label: blockersCount > 0 ? `Needs attention · ${blockersCount}` : 'Needs attention',
      hint: 'Can be activated, but items are still outstanding — open the checklist',
    };
  }
  if (expiring) {
    return {
      tone: 'amber',
      Icon: Clock,
      label: 'Renewal due',
      hint: 'A required ID or document is expiring soon or has expired',
    };
  }
  if (state === 'Ready') {
    return { tone: 'emerald', Icon: CheckCircle2, label: 'Ready', hint: 'Nothing outstanding' };
  }
  // Unknown, new or missing state — we were not told this employee is ready, so we never say
  // they are. Neutral slate + its own wording, so a new server state degrades visibly rather
  // than silently masquerading as green.
  return {
    tone: 'slate',
    Icon: HelpCircle,
    label: 'Not evaluated',
    hint: 'Readiness could not be determined for this employee — open the checklist',
  };
}

export function ReadinessBadge({ state, blockersCount, expiring, onClick, title }: ReadinessBadgeProps) {
  const { tone, Icon, label, hint } = resolve(state, blockersCount, expiring);
  const base = `inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-semibold ring-1 ${toneClasses[tone]}`;

  if (onClick) {
    return (
      <button
        type="button"
        onClick={(e) => { e.stopPropagation(); onClick(); }}
        title={title ?? hint}
        className={`${base} transition hover:brightness-95 focus:outline-none focus-visible:ring-2 focus-visible:ring-offset-1`}
      >
        <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        {label}
      </button>
    );
  }

  return (
    <span className={base} title={title ?? hint}>
      <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
      {label}
    </span>
  );
}

/**
 * True when a visa or passport expiry date is within `withinDays` or already past — the
 * time-based signal that flips an otherwise-green badge to amber on the list. Uses the two
 * expiry dates the list DTO already carries, so no extra request is needed.
 */
export function hasExpiringId(dates: Array<string | undefined>, withinDays = 60): boolean {
  const now = Date.now();
  const horizon = now + withinDays * 24 * 60 * 60 * 1000;
  return dates.some((d) => {
    if (!d) return false;
    const t = new Date(d).getTime();
    return Number.isFinite(t) && t <= horizon;
  });
}
