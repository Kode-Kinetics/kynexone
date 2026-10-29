import type { HTMLAttributes } from 'react';

/** The one card: 12px radius, 1px line, elevation e1 (`raised` is e2). Padding 16 compact / 20 default. */
export function Card({ raised = false, compact = false, className = '', ...rest }: HTMLAttributes<HTMLDivElement> & { raised?: boolean; compact?: boolean }) {
  return <div className={`${raised ? 'surface-raised' : 'surface'} ${compact ? 'p-4' : 'p-5'} ${className}`.trim()} {...rest} />;
}
