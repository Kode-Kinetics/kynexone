'use client';

import type { ReactNode } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';

export interface ControlProps {
  id: string;
  'aria-invalid'?: true;
  'aria-describedby'?: string;
  'aria-required'?: true;
}

export const fieldId = (field: string) => `hire-${field}`;

/**
 * One labelled field: visible label, "(required)" as text (not only an asterisk), hint, and an error that is
 * linked with aria-describedby. Validation is shown on blur and on Next, never while typing (A2, A9).
 */
export function FieldShell({ field, label, required, hint, error, extra, children, className = '' }: {
  field: string;
  label: string;
  required?: boolean;
  hint?: string;
  error?: string;
  /** Rendered after the control, inside the described-by chain (e.g. the Hijri line). */
  extra?: { id: string; node: ReactNode };
  children: (props: ControlProps) => ReactNode;
  className?: string;
}) {
  const { t } = useLocale();
  const id = fieldId(field);
  const ids = [error ? `${id}-error` : '', hint ? `${id}-hint` : '', extra ? extra.id : ''].filter(Boolean).join(' ');
  return (
    <div className={`min-w-0 ${className}`}>
      <label htmlFor={id} className="block text-sm font-medium text-slate-700 dark:text-slate-300">
        {label}{required && <span className="ms-1 text-xs font-normal text-slate-500 dark:text-slate-400">{t('(required)')}</span>}
      </label>
      <div className="mt-1.5">
        {children({ id, ...(error ? { 'aria-invalid': true as const } : {}), ...(ids ? { 'aria-describedby': ids } : {}), ...(required ? { 'aria-required': true as const } : {}) })}
      </div>
      {hint && <p id={`${id}-hint`} className="mt-1 text-xs text-slate-500 dark:text-slate-400">{hint}</p>}
      {extra && <div id={extra.id} className="mt-1 text-xs text-slate-500 dark:text-slate-400">{extra.node}</div>}
      {error && <p id={`${id}-error`} className="mt-1 text-xs font-medium text-red-700 dark:text-red-300">{error}</p>}
    </div>
  );
}

export const inputClass = 'input w-full min-w-0 aria-[invalid=true]:border-red-500 aria-[invalid=true]:ring-1 aria-[invalid=true]:ring-red-500';
/** Latin-script values (IBAN, ID, phone, email) stay LTR and start-aligned inside an RTL form. */
export const ltrProps = { dir: 'ltr' as const, className: `${inputClass} text-start` };
