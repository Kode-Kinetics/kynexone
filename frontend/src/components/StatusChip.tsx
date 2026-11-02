'use client';

interface StatusChipProps {
  label: string;
  tone?: 'blue' | 'cyan' | 'emerald' | 'amber' | 'rose' | 'slate';
  dot?: boolean;
}

// Legacy tone names kept for the ~45 call sites; each maps onto a semantic badge role.
const toneClasses: Record<NonNullable<StatusChipProps['tone']>, string> = {
  blue: 'badge-primary',
  cyan: 'badge-info',
  emerald: 'badge-success',
  amber: 'badge-warning',
  rose: 'badge-danger',
  slate: 'badge-neutral',
};

const dotColors: Record<NonNullable<StatusChipProps['tone']>, string> = {
  blue: 'bg-primary',
  cyan: 'bg-info',
  emerald: 'bg-success',
  amber: 'bg-warning',
  rose: 'bg-danger',
  slate: 'bg-fg-subtle',
};

export function StatusChip({ label, tone = 'slate', dot }: StatusChipProps) {
  return (
    <span className={`badge ${toneClasses[tone]}`}>
      {dot && (
        <span className={`h-1.5 w-1.5 shrink-0 rounded-full ${dotColors[tone]}`} aria-hidden="true" />
      )}
      {label}
    </span>
  );
}
