'use client';

import type { ReactNode } from 'react';
import { useLocale } from '../../contexts/LocaleContext';

/**
 * Read-only fields of the employee record, one look everywhere (Screen Standard): a small muted label above the value, no box, no
 * border. Boxes and inputs belong to edit mode only. The grid is 1 column on a phone, 2 from 768px and 3 from 1280px.
 *
 * A long value wraps inside its own cell (`min-w-0`, `break-words`); a code or an identifier (`code`) is broken anywhere and set
 * in monospace, isolated so an Arabic page does not reorder it, with the full text in a tooltip.
 */
const COLUMNS = {
  default: 'md:grid-cols-2 xl:grid-cols-3',
  /** A narrow card beside another: always one column. */
  one: '',
  /** A short strip of four. */
  four: 'md:grid-cols-2 xl:grid-cols-4',
} as const;

export function RecordFieldGrid({ children, columns = 'default' }: { children: ReactNode; columns?: keyof typeof COLUMNS }) {
  return <dl className={`grid grid-cols-1 gap-x-6 gap-y-3 ${COLUMNS[columns]}`}>{children}</dl>;
}

export function RecordField({ label, value, code = false, caption, hint, children }: {
  label: ReactNode;
  value?: string | number | null;
  /** An identifier, IBAN, e-mail address or number: monospace, left-to-right, broken anywhere. */
  code?: boolean;
  /** One muted line under the value, only where it adds meaning (an expiry date, a status). */
  caption?: ReactNode;
  /** A longer explanation, as a tooltip. */
  hint?: string;
  /** Replaces the value (a link, a chip). */
  children?: ReactNode;
}) {
  const { t } = useLocale();
  const empty = children == null && (value == null || value === '');
  const text = empty ? '' : String(value);
  return (
    <div className="min-w-0" title={hint}>
      <dt className="text-xs text-slate-500 dark:text-slate-400">{label}</dt>
      <dd data-record-value className={`mt-0.5 min-w-0 text-sm font-medium ${empty ? 'text-slate-400 dark:text-slate-500' : 'text-slate-900 dark:text-white'} ${code ? 'break-all' : 'break-words'}`} title={code && !empty ? text : undefined}>
        {empty ? t('Not provided') : children ?? (code ? <bdi dir="ltr" className="font-mono text-xs">{text}</bdi> : <bdi>{text}</bdi>)}
        {caption && <span className="block text-xs font-normal text-slate-500 dark:text-slate-400">{caption}</span>}
      </dd>
    </div>
  );
}
