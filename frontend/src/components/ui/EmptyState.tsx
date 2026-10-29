import type { ElementType, ReactNode } from 'react';

/**
 * Nothing to show yet. Plain, left-aligned-to-reading-edge copy with one next action; no dashed
 * box, no illustration. Say what is missing and what to do about it.
 */
export function EmptyState({ icon: Icon, title, description, action, className = '' }: {
  icon?: ElementType<{ className?: string; 'aria-hidden'?: boolean }>;
  title: string;
  description?: ReactNode;
  action?: ReactNode;
  className?: string;
}) {
  return (
    <div className={`flex flex-col items-center gap-2 px-6 py-10 text-center ${className}`.trim()}>
      {Icon && <Icon className="mb-1 h-5 w-5 text-fg-subtle" aria-hidden />}
      <p className="text-body font-semibold text-fg">{title}</p>
      {description && <p className="max-w-md text-body-sm text-fg-muted">{description}</p>}
      {action && <div className="mt-2">{action}</div>}
    </div>
  );
}
