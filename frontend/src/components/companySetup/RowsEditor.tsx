'use client';

import type { ReactNode } from 'react';
import { Plus } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { TagPill, WizardField, useWizardForm } from './wizardForm';

export interface RowColumn<T> {
  key: keyof T & string;
  /** Dictionary key; translated here. */
  label: string;
  required?: boolean;
  kind?: 'text' | 'number' | 'select' | 'checkbox';
  /** Arabic inputs are right-to-left whatever the page direction. */
  arabic?: boolean;
  options?: { value: string; label: string }[];
  /** Tailwind width hint for the field inside a row. */
  width?: string;
  inputMode?: 'numeric' | 'decimal' | 'text';
}

/**
 * Inline add / edit / remove rows. Each row is a labelled group of fields that wraps, so a row never
 * forces horizontal scrolling at phone width. A row the shared group catalog already holds is shown
 * as Reused and read-only (only Remove applies).
 */
export function RowsEditor<T extends { code: string }>({
  path, rows, columns, onChange, newRow, addLabel, emptyText, rowLabel, reused, footer, rowExtra,
}: {
  path: string;
  rows: T[];
  columns: RowColumn<T>[];
  onChange: (rows: T[]) => void;
  newRow: () => T;
  addLabel: string;
  emptyText: string;
  /** Accessible name of a row, e.g. "Branch 2". */
  rowLabel: (index: number) => string;
  /** The catalog name when this row's code is already shared, so it is reused rather than created. */
  reused?: (row: T) => { nameEn: string; nameAr?: string | null } | undefined;
  footer?: ReactNode;
  rowExtra?: (row: T, index: number) => ReactNode;
}) {
  const t = useT();
  const { readOnly } = useWizardForm();
  const set = (index: number, key: keyof T, value: unknown) => onChange(rows.map((row, i) => (i === index ? { ...row, [key]: value } : row)));
  return (
    <div className="space-y-2">
      {rows.length === 0 && <p className="rounded-lg border border-dashed border-slate-300 px-4 py-3 text-sm text-slate-600 dark:border-white/15 dark:text-slate-400">{emptyText}</p>}
      {rows.map((row, index) => {
        const shared = reused?.(row);
        return (
          <fieldset key={index} className="min-w-0 rounded-lg border border-slate-200 bg-white p-3 dark:border-white/10 dark:bg-white/[0.02]" data-testid={`${path}-row`}>
            <legend className="sr-only">{rowLabel(index)}</legend>
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <div className="flex min-w-0 items-center gap-2">
                <span className="text-xs font-semibold text-slate-500 dark:text-slate-400">{rowLabel(index)}</span>
                <TagPill tag={shared ? 'reused' : 'new'} />
              </div>
              {!readOnly && <button type="button" className="rounded-md px-2 py-1 text-xs font-semibold text-rose-700 hover:bg-rose-50 dark:text-rose-300 dark:hover:bg-rose-500/10" onClick={() => onChange(rows.filter((_, i) => i !== index))}>
                {t('Remove')}<span className="sr-only"> {rowLabel(index)}</span>
              </button>}
            </div>
            {shared && <p className="mb-2 text-xs leading-5 text-slate-600 dark:text-slate-400">{t('Already in the shared group catalog as “{name}”. It is reused as it is; nothing new is created.', { name: shared.nameEn })}</p>}
            <div className="flex flex-wrap gap-x-3 gap-y-2">
              {columns.map(col => {
                const field = `${path}.${index}.${col.key}`;
                const value = row[col.key] as unknown;
                // A reused row keeps its catalog names; only its code is shown, read-only.
                const isName = col.key.startsWith('name') || col.key.startsWith('title');
                const locked = readOnly || (!!shared && col.key !== 'code');
                const codeLocked = readOnly || (!!shared && col.key === 'code');
                const shown = shared && isName ? (col.arabic ? (shared.nameAr ?? '') : shared.nameEn) : String(value ?? '');
                if (col.kind === 'checkbox') return (
                  <label key={col.key} className="flex min-h-[2.5rem] items-center gap-2 self-end text-sm text-slate-700 dark:text-slate-300">
                    <input type="checkbox" data-field={field} checked={!!value} disabled={readOnly} onChange={e => set(index, col.key, e.target.checked)} className="h-4 w-4 rounded border-slate-300" />
                    {t(col.label)}
                  </label>
                );
                return (
                  <WizardField key={col.key} field={field} label={t(col.label)} required={col.required && !shared} className={col.width ?? 'w-full sm:w-40'}>
                    {control => col.kind === 'select'
                      ? <select {...control} className="select w-full" value={String(value ?? '')} disabled={locked || codeLocked} onChange={e => set(index, col.key, e.target.value || undefined)}>
                          <option value="">{t('None')}</option>
                          {(col.options ?? []).map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                        </select>
                      : <input {...control} className="input w-full" dir={col.arabic ? 'rtl' : undefined} lang={col.arabic ? 'ar' : undefined}
                          type={col.kind === 'number' ? 'number' : 'text'} inputMode={col.inputMode}
                          readOnly={locked || codeLocked}
                          value={shown}
                          onChange={e => set(index, col.key, col.kind === 'number' ? (e.target.value === '' ? 0 : Number(e.target.value)) : col.key === 'code' ? e.target.value.toUpperCase() : e.target.value)} />}
                  </WizardField>
                );
              })}
            </div>
            {rowExtra?.(row, index)}
          </fieldset>
        );
      })}
      {!readOnly && (
        <div className="flex flex-wrap items-center gap-2 pt-1">
          <button type="button" className="btn-secondary" onClick={() => onChange([...rows, newRow()])}><Plus className="h-4 w-4" aria-hidden="true" />{addLabel}</button>
          {footer}
        </div>
      )}
    </div>
  );
}
