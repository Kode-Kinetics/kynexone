'use client';

interface StatusChipProps {
  label: string;
  tone?: 'blue' | 'cyan' | 'emerald' | 'amber' | 'rose' | 'slate';
  dot?: boolean;
}

const toneClasses: Record<NonNullable<StatusChipProps['tone']>, string> = {
  blue: 'bg-sapphire/10 text-sapphire ring-sapphire/20 dark:bg-sapphire/20 dark:text-blue-200 dark:ring-sapphire/30',
  cyan: 'bg-cyanAccent/15 text-cyan-700 ring-cyanAccent/25 dark:bg-cyanAccent/10 dark:text-cyanAccent dark:ring-cyanAccent/20',
  emerald: 'bg-emeraldZ/10 text-emerald-700 ring-emeraldZ/20 dark:bg-emeraldZ/10 dark:text-emerald-300 dark:ring-emeraldZ/20',
  amber: 'bg-amber-400/15 text-amber-800 ring-amber-400/25 dark:bg-amber-400/10 dark:text-amber-300 dark:ring-amber-400/20',
  rose: 'bg-rose-500/10 text-rose-700 ring-rose-500/20 dark:bg-rose-500/10 dark:text-rose-300 dark:ring-rose-500/20',
  slate: 'bg-slate-100 text-slate-600 ring-slate-200 dark:bg-white/10 dark:text-slate-300 dark:ring-white/10',
};

const dotColors: Record<NonNullable<StatusChipProps['tone']>, string> = {
  blue: 'bg-sapphire',
  cyan: 'bg-cyanAccent',
  emerald: 'bg-emeraldZ',
  amber: 'bg-amber-400',
  rose: 'bg-rose-500',
  slate: 'bg-slate-400',
};

export function StatusChip({ label, tone = 'slate', dot }: StatusChipProps) {
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-semibold ring-1 ${toneClasses[tone]}`}
    >
      {dot && (
        <span className={`h-1.5 w-1.5 shrink-0 rounded-full ${dotColors[tone]}`} aria-hidden="true" />
      )}
      {label}
    </span>
  );
}
