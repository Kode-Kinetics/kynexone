import type { HTMLAttributes, ReactNode, TdHTMLAttributes, ThHTMLAttributes } from 'react';

export type TableDensity = 'compact' | 'default' | 'comfortable';

/**
 * The shared data table. Sticky header (inside a scroll container, so give it a `maxHeight`
 * for long lists), three row densities (32 / 40 / 48; 48 on touch), numeric columns end-aligned
 * with tabular figures, and logical alignment so Arabic mirrors without extra rules.
 * Provide `caption` (visually hidden) so the table has an accessible name.
 */
export function Table({
  caption,
  density = 'default',
  maxHeight,
  className = '',
  scrollClassName = '',
  children,
  ...rest
}: Omit<HTMLAttributes<HTMLTableElement>, 'children'> & {
  caption: string;
  density?: TableDensity;
  maxHeight?: string | number;
  scrollClassName?: string;
  children: ReactNode;
}) {
  return (
    <div className={`kx-table-scroll ${scrollClassName}`.trim()} style={maxHeight !== undefined ? { maxHeight } : undefined} tabIndex={maxHeight !== undefined ? 0 : undefined}>
      <table className={`kx-table ${className}`.trim()} data-density={density} {...rest}>
        <caption className="sr-only">{caption}</caption>
        {children}
      </table>
    </div>
  );
}

/** Header cell. `numeric` right-aligns (inline-end) to sit over its figures. */
export function Th({ numeric = false, className = '', scope = 'col', ...rest }: ThHTMLAttributes<HTMLTableCellElement> & { numeric?: boolean }) {
  return <th scope={scope} className={`${numeric ? 'kx-num' : ''} ${className}`.trim()} {...rest} />;
}

export function Td({ numeric = false, className = '', ...rest }: TdHTMLAttributes<HTMLTableCellElement> & { numeric?: boolean }) {
  return <td className={`${numeric ? 'kx-num' : ''} ${className}`.trim()} {...rest} />;
}
