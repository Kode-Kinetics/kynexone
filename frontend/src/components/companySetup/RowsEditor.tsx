'use client';

import type { ReactNode } from 'react';
import { ExternalLink, Plus } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { msg } from '../../i18n/translations';
import type { ExistingRow } from '../../api/companySetup';
import { editRow, rowChanged } from '../../lib/companySetupWizard';
import { TagPill, WizardField, useWizardForm } from './wizardForm';
import { ArabicSuggest, FillMissingArabic } from './ArabicSuggest';

export interface RowColumn<T> {
  key: keyof T & string;
  /** Dictionary key; translated here. */
  label: string;
  required?: boolean;
  kind?: 'text' | 'number' | 'select' | 'checkbox';
  /** Arabic inputs are right-to-left whatever the page direction, and offer "Suggest Arabic" from the English name. */
  arabic?: boolean;
  /** The English column an Arabic column is suggested from; defaults to the same key ending in En (nameAr → nameEn). */
  arabicSource?: keyof T & string;
  options?: { value: string; label: string }[];
  /** Tailwind width hint for the field inside a row. */
  width?: string;
  inputMode?: 'numeric' | 'decimal' | 'text';
  /** Dictionary key for the empty choice of a select; "None" when absent. */
  emptyLabel?: string;
}

/** What the empty choice of a select means, when it is not simply "none". */
const EMPTY_CHOICE: Record<string, string> = { timeZoneId: msg('Country default') };

/**
 * Inline add / edit / remove rows (branches and locations). Each row is a labelled group of fields that wraps,
 * so a row never forces horizontal scrolling at phone width. Organisation rows use OrgRowsEditor.
 *
 * Existing mode (Addendum A3): a row the company already has (`existingId`) is edited in place, keeps its
 * code, and is never removed here — removal happens in its own Setup tab, linked from the row.
 */
export function RowsEditor<T extends { code: string } & ExistingRow>({
  path, rows, columns, onChange, newRow, addLabel, emptyText, rowLabel, footer, rowExtra, manage, baselineRows, canAdd = true,
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
  footer?: ReactNode;
  rowExtra?: (row: T, index: number) => ReactNode;
  /** Where an existing row is removed instead: a Setup tab and its (dictionary key) name. */
  manage?: { href: string; label: string };
  /** Existing mode: the rows as the company had them, so an edited existing row says "Changed". */
  baselineRows?: readonly T[];
  /** False hides "Add". */
  canAdd?: boolean;
}) {
  const t = useT();
  const { readOnly, errorFor } = useWizardForm();
  const fields = columns.map(c => c.key);
  const manageLink = (label: string) => manage
    ? <a href={manage.href} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs font-semibold text-sapphire hover:underline dark:text-cyanAccent" data-testid="manage-in-setup">
        {t('Manage in Setup → {tab}', { tab: t(manage.label) })}<ExternalLink className="h-3 w-3 rtl:-scale-x-100" aria-hidden="true" />
        <span className="sr-only"> {label} {t('(opens in a new tab)')}</span>
      </a>
    : null;
  const set = (index: number, key: keyof T, value: unknown) => onChange(editRow(rows, index, key, value));
  const arabicColumns = columns.filter(c => c.arabic);
  const englishOf = (col: RowColumn<T>) => (row: T) => String(row[(col.arabicSource ?? col.key.replace(/Ar$/, 'En')) as keyof T] ?? '');
  return (
    <div className="space-y-2">
      {rows.length === 0 && <p className="rounded-lg border border-dashed border-slate-300 px-4 py-3 text-sm text-slate-600 dark:border-white/15 dark:text-slate-400">{emptyText}</p>}
      {rows.map((row, index) => {
        const existing = !!row.existingId;
        const changed = existing && rowChanged(row, baselineRows, fields);
        return (
          <fieldset key={index}
            className="min-w-0 rounded-lg border border-slate-200 bg-white p-3 dark:border-white/10 dark:bg-white/[0.02]" data-testid={`${path}-row`}
            data-existing={existing ? 'true' : undefined} data-changed={changed ? 'true' : undefined}>
            <legend className="sr-only">{rowLabel(index)}</legend>
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <div className="flex min-w-0 items-center gap-2">
                <span className="text-xs font-semibold text-slate-500 dark:text-slate-400">{rowLabel(index)}</span>
                <TagPill tag={existing ? 'existing' : 'new'} />
                {changed && <TagPill tag="changed" />}
              </div>
              {existing ? manageLink(rowLabel(index)) : !readOnly && <button type="button" className="rounded-md px-2 py-1 text-xs font-semibold text-rose-700 hover:bg-rose-50 dark:text-rose-300 dark:hover:bg-rose-500/10" onClick={() => onChange(rows.filter((_, i) => i !== index))}>
                {t('Remove')}<span className="sr-only"> {rowLabel(index)}</span>
              </button>}
            </div>
            {existing && <p className="mb-2 text-xs leading-5 text-slate-600 dark:text-slate-400">{t('Already set up for this company. Edit it here; its code stays the same.')}</p>}
            <div className="flex flex-wrap gap-x-3 gap-y-2">
              {columns.map(col => {
                const field = `${path}.${index}.${col.key}`;
                const value = row[col.key] as unknown;
                const locked = readOnly;
                // An existing row keeps its code: other records (employees, numbering, files) refer to it.
                const codeLocked = readOnly || (existing && col.key === 'code');
                const shown = String(value ?? '');
                if (col.kind === 'checkbox') return (
                  <label key={col.key} className="flex min-h-[2.5rem] items-center gap-2 self-end text-sm text-slate-700 dark:text-slate-300">
                    <input type="checkbox" data-field={field} checked={!!value} disabled={readOnly} onChange={e => set(index, col.key, e.target.checked)} className="h-4 w-4 rounded border-slate-300"
                      aria-describedby={col.key === 'isHeadOffice' && errorFor('branches') ? 'ccw-branches-error' : undefined} />
                    {t(col.label)}
                  </label>
                );
                return (
                  <WizardField key={col.key} field={field} label={t(col.label)} required={col.required} className={col.width ?? 'w-full sm:w-40'}>
                    {control => col.kind === 'select'
                      ? <select {...control} className="select w-full" value={String(value ?? '')} disabled={locked || codeLocked} onChange={e => set(index, col.key, e.target.value || undefined)}>
                          <option value="">{t(col.emptyLabel ?? EMPTY_CHOICE[col.key] ?? msg('None'))}</option>
                          {(col.options ?? []).map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                        </select>
                      : <>
                          <input {...control} className="input w-full" dir={col.arabic ? 'rtl' : undefined} lang={col.arabic ? 'ar' : undefined}
                            type={col.kind === 'number' ? 'number' : 'text'} inputMode={col.inputMode}
                            readOnly={locked || codeLocked}
                            value={shown}
                            onChange={e => set(index, col.key, col.kind === 'number' ? (e.target.value === '' ? null : Number(e.target.value)) : col.key === 'code' ? e.target.value.toUpperCase() : e.target.value)} />
                          {col.arabic && <ArabicSuggest source={englishOf(col)(row)} value={shown} inputId={control.id} readOnly={locked}
                            context={rowLabel(index)} onAccept={arabic => set(index, col.key, arabic)} />}
                        </>}
                  </WizardField>
                );
              })}
            </div>
            {rowExtra?.(row, index)}
          </fieldset>
        );
      })}
      {!readOnly && (canAdd || footer || arabicColumns.length > 0) && (
        <div className="flex flex-wrap items-center gap-2 pt-1">
          {canAdd && <button type="button" className="btn-secondary" onClick={() => onChange([...rows, newRow()])}><Plus className="h-4 w-4" aria-hidden="true" />{addLabel}</button>}
          {arabicColumns.map(col => (
            <FillMissingArabic<T> key={col.key} rows={rows} arabicKey={col.key} english={englishOf(col)} onFill={onChange}
              eligible={() => true} label={(_, i) => rowLabel(i)} />
          ))}
          {footer}
        </div>
      )}
    </div>
  );
}
