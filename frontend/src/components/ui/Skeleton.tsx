import type { CSSProperties } from 'react';

/** A loading placeholder shaped like its content. Decorative: hide from assistive tech and announce loading elsewhere. */
export function Skeleton({ className = 'h-4 w-full', style }: { className?: string; style?: CSSProperties }) {
  return <div className={`kx-skeleton ${className}`} style={style} aria-hidden="true" />;
}

/** `rows` skeleton table rows at the default density, for use in place of a table body. */
export function SkeletonRows({ rows = 5, className = '' }: { rows?: number; className?: string }) {
  return (
    <div className={`space-y-2 ${className}`.trim()} role="status" aria-busy="true">
      {Array.from({ length: rows }, (_, i) => <Skeleton key={i} className="h-10 w-full" />)}
    </div>
  );
}
