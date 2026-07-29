'use client';

import type { ReactNode } from 'react';

// Small presentational helpers shared across the GL / rates configuration panels.
// Styling reuses the app's global utility classes (input, select, btn-*, surface).

export function Field({
  label,
  required,
  hint,
  children,
}: {
  label: string;
  required?: boolean;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <div>
      <label className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">
        {label} {required && <span className="text-rose-500">*</span>}
      </label>
      {children}
      {hint && <p className="mt-1 text-[11px] leading-snug text-slate-400 dark:text-slate-500">{hint}</p>}
    </div>
  );
}

export function FieldError({ error }: { error: string }) {
  if (!error) return null;
  return (
    <p className="mb-3 rounded-lg bg-rose-50 px-3 py-2.5 text-sm text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">
      {error}
    </p>
  );
}

type Tone = 'slate' | 'sapphire' | 'emerald' | 'amber' | 'rose' | 'violet';

const toneClass: Record<Tone, string> = {
  slate: 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400',
  sapphire: 'bg-sapphire/10 text-sapphire dark:bg-sapphire/20 dark:text-cyanAccent',
  emerald: 'bg-emeraldZ/10 text-emeraldZ dark:bg-emeraldZ/20',
  amber: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400',
  rose: 'bg-rose-100 text-rose-600 dark:bg-rose-500/15 dark:text-rose-400',
  violet: 'bg-violet-100 text-violet-700 dark:bg-violet-500/15 dark:text-violet-300',
};

export function Badge({
  tone = 'slate',
  children,
  title,
}: {
  tone?: Tone;
  children: ReactNode;
  title?: string;
}) {
  return (
    <span
      title={title}
      className={`inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-semibold ${toneClass[tone]}`}
    >
      {children}
    </span>
  );
}

export function InheritedBadge({ scopeLabel = 'group' }: { scopeLabel?: string }) {
  return (
    <Badge tone="slate" title={`Inherited from the ${scopeLabel} default — not overridden for this company.`}>
      Inherited
    </Badge>
  );
}

export function SystemBadge() {
  return (
    <Badge tone="sapphire" title="Built-in system driver. Routing fields are read-only; it cannot be deleted.">
      System
    </Badge>
  );
}

export function StatusBadge({ status }: { status: string }) {
  const tone: Tone =
    status === 'Active' ? 'emerald' : status === 'PendingApproval' ? 'amber' : 'slate';
  const label = status === 'PendingApproval' ? 'Pending approval' : status;
  return <Badge tone={tone}>{label}</Badge>;
}

/** Empty / loading / content wrapper for a card body. */
export function PanelState({
  loading,
  empty,
  emptyLabel,
  children,
}: {
  loading: boolean;
  empty: boolean;
  emptyLabel: string;
  children: ReactNode;
}) {
  if (loading)
    return (
      <div className="grid place-items-center py-12">
        <div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
      </div>
    );
  if (empty) return <p className="py-10 text-center text-sm text-slate-400 dark:text-slate-500">{emptyLabel}</p>;
  return <>{children}</>;
}

/** yyyy-MM-dd (or ISO) → short locale date; blank stays a dash. */
export function fmtDate(d: string | null | undefined): string {
  if (!d) return '—';
  const parsed = new Date(d.length <= 10 ? `${d}T00:00:00` : d);
  if (Number.isNaN(parsed.getTime())) return d;
  return parsed.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

/** Today as yyyy-MM-dd, for date-input defaults. */
export function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}
