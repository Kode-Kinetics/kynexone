'use client';

import { AlertTriangle, CheckCircle2, Clock } from 'lucide-react';

// Two gate tones + a semantically distinct amber, per the design (§2.2):
//   red   = Blocked         → "can't activate yet"        → "Incomplete · N"
//   amber = required doc/ID expiring or expired (time)    → "Renewal due"
//   green = activatable (Ready or NeedsAttention)         → "Ready"
// Every tone pairs an icon with text — never colour alone (WCAG). The badge is the
// click-target into the activation checklist.

type Tone = 'emerald' | 'amber' | 'rose';

const toneClasses: Record<Tone, string> = {
  emerald: 'bg-emeraldZ/10 text-emerald-700 ring-emeraldZ/20 dark:bg-emeraldZ/10 dark:text-emerald-300 dark:ring-emeraldZ/20',
  amber: 'bg-amber-400/15 text-amber-700 ring-amber-400/25 dark:bg-amber-400/10 dark:text-amber-300 dark:ring-amber-400/20',
  rose: 'bg-rose-500/10 text-rose-700 ring-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300 dark:ring-rose-500/20',
};

export interface ReadinessBadgeProps {
  /** Server readiness state: "Ready" | "NeedsAttention" | "Blocked". */
  state: string;
  /** Count of activation blockers (drives the red "· N" sub-count). */
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
} {
  if (state === 'Blocked') {
    return {
      tone: 'rose',
      Icon: AlertTriangle,
      label: blockersCount > 0 ? `Incomplete · ${blockersCount}` : 'Incomplete',
    };
  }
  if (expiring) {
    return { tone: 'amber', Icon: Clock, label: 'Renewal due' };
  }
  // Ready and NeedsAttention both read as success — an activatable employee is not an amber alarm.
  return { tone: 'emerald', Icon: CheckCircle2, label: 'Ready' };
}

export function ReadinessBadge({ state, blockersCount, expiring, onClick, title }: ReadinessBadgeProps) {
  const { tone, Icon, label } = resolve(state, blockersCount, expiring);
  const base = `inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-semibold ring-1 ${toneClasses[tone]}`;

  if (onClick) {
    return (
      <button
        type="button"
        onClick={(e) => { e.stopPropagation(); onClick(); }}
        title={title ?? 'View the activation checklist'}
        className={`${base} transition hover:brightness-95 focus:outline-none focus-visible:ring-2 focus-visible:ring-offset-1`}
      >
        <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        {label}
      </button>
    );
  }

  return (
    <span className={base} title={title}>
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
