import type { HTMLAttributes } from 'react';

export type BadgeTone = 'neutral' | 'primary' | 'success' | 'warning' | 'danger' | 'info';

/** Pill-shaped status label. Colour is never the only signal: always pass text. */
export function Badge({ tone = 'neutral', className = '', ...rest }: HTMLAttributes<HTMLSpanElement> & { tone?: BadgeTone }) {
  return <span className={`badge badge-${tone} ${className}`.trim()} {...rest} />;
}
