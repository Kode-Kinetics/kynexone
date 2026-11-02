'use client';

import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { ChevronDown, ExternalLink, Lock, Plus, RotateCcw, Trash2 } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import type { CompanySetupRowState, ExistingRow, OrgSection } from '../../api/companySetup';
import { editRow, lookAlikeIndex, rowChanged, suggestCode, type FieldError } from '../../lib/companySetupWizard';
import { fieldId, useWizardForm } from './wizardForm';
import { usageSentence } from '../../lib/orgUsage';
import { ArabicSuggest, FillMissingArabic } from './ArabicSuggest';

type Row = { code: string } & ExistingRow;

/** A field on the row's second line ("links"): a reference to another section, or a number of a grade's band. */
export interface OrgDetailField<T> {
  key: keyof T & string;
  /** Dictionary key. */
  label: string;
  kind: 'select' | 'number';
  options?: { value: string; label: string }[];
  /** Shown in amber in the summary when empty: worth filling, though not required. */
  wanted?: boolean;
  /** Dictionary key for the summary when empty, e.g. "No cost centre". */
  emptyLabel?: string;
  /** Amount fields: an empty input shows nothing (not 0) and the summary uses tabular figures. */
  amount?: boolean;
  inputMode?: 'numeric' | 'decimal';
}

/** The table switches to a stacked list below this content width (Tailwind 3.4 has no container queries here). */
const TABLE_MIN_WIDTH = 640;
/** How long "… removed · Undo" stays. */
const UNDO_MS = 8000;
/** Exit fade before a removed row leaves the list (0 under reduced motion). */
const EXIT_MS = 140;

/** Rows rise in over 180 ms; nothing moves under reduced motion. */
const ROW_ENTER = 'motion-safe:animate-[fade-in_180ms_cubic-bezier(0.2,0,0,1)_both]';

const reducedMotion = () => typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
const isBlank = (v: unknown) => !String(v ?? '').trim();

/**
 * The editable rows of one Organisation section (Addendum B): every row is this company's own.
 * - A compact table when the content column is at least 640px wide, a stacked list below that.
 * - Code | Name (English) | Name (Arabic) (Arabic first in Arabic) | Status (exceptions only) | Remove (new rows only).
 * - A row's references and band sit on a second line with "Edit details"; an empty reference worth filling is amber.
 * - Every row can be removed. A new row leaves at once (Undo); a saved row is marked "Will be removed when you save" (Undo), and the
 *   server decides on save: nothing uses it = deleted, in use = turned off (it shows as Off with Restore afterwards).
 * - A saved row's code is editable until something uses it, then locked with the reason shown on the row.
 * Deterministic helpers only: a suggested code from the name (never filled in silently) and a look-alike check.
 */
export function OrgRowsEditor<T extends Row>({
  section, rows, onChange, nameKey, arKey, newRow, addLabel, emptyText, rowLabel, details = [], detailNote, rowWarnings,
  baselineRows, order, visible, codePrefix = '', onWalkArabic, rowStates, removedIds = [], onRemovedIdsChange, onRestore, manage,
}: {
  section: OrgSection;
  rows: T[];
  onChange: (rows: T[]) => void;
  nameKey: keyof T & string;
  arKey: keyof T & string;
  newRow: () => T;
  addLabel: string;
  emptyText: string;
  /** Accessible name of a row, e.g. "Department 2" (already translated). */
  rowLabel: (index: number) => string;
  details?: OrgDetailField<T>[];
  /** Extra line under the details: a suggestion the user confirms, a "Use the middle" button. */
  detailNote?: (row: T, index: number) => ReactNode;
  /** Non-blocking advice by field path, shown in amber in its cell. */
  rowWarnings?: ReadonlyMap<string, FieldError>;
  /** Existing mode: the rows as the company had them, so an edited row says "Changed" and what it was. */
  baselineRows?: readonly T[];
  /** Display order as indexes into `rows` (the draft keeps its own order). */
  order?: readonly number[];
  /** Show only these rows (a filter); the rest stay in the draft. */
  visible?: (row: T, index: number) => boolean;
  codePrefix?: string;
  /** Registers the "Fill Arabic names" walker: focuses the next empty Arabic cell after the last one it visited. */
  onWalkArabic?: (walk: () => boolean) => void;
  /** Existing mode: per saved row (by existingId) on/off and in-use, as the server read it. */
  rowStates?: Record<string, CompanySetupRowState>;
  /** Existing mode: ids of saved rows marked for removal in this draft. */
  removedIds?: readonly string[];
  onRemovedIdsChange?: (ids: string[]) => void;
  /** Existing mode: turn a turned-off row back on now. */
  onRestore?: (id: string) => Promise<void>;
  /** Where the row can also be managed: its Setup tab. */
  manage?: { href: string; label: string };
}) {
  const { t, locale } = useLocale();
  const fmt = useFormat();
  const { errorFor, readOnly } = useWizardForm();
  const existingMode = !!baselineRows;
  const path = section;
  const stateOf = (row: T): CompanySetupRowState | undefined => (row.existingId ? rowStates?.[row.existingId] : undefined);
  const isMarked = (row: T) => !!row.existingId && removedIds.includes(row.existingId);
  const isOff = (row: T) => stateOf(row)?.isActive === false;
  const mark = (row: T) => { if (row.existingId && onRemovedIdsChange) onRemovedIdsChange([...removedIds, row.existingId]); };
  const unmark = (row: T) => { if (onRemovedIdsChange) onRemovedIdsChange(removedIds.filter(id => id !== row.existingId)); };
  /** A saved row's code is locked only while something uses it. */
  const codeLocked = (row: T) => !!row.existingId && !!stateOf(row)?.inUse;
  const codeLockReason = (row: T) => { const st = stateOf(row); return codeLocked(row) ? `${t('Code locked: it is in use.')} ${usageSentence(st?.usage, t, st?.usageSummary)}`.trim() : undefined; };
  const [restoring, setRestoring] = useState<string | null>(null);
  const [restoreFailed, setRestoreFailed] = useState<string | null>(null);
  const restore = async (row: T) => {
    if (!row.existingId || !onRestore) return;
    setRestoring(row.existingId); setRestoreFailed(null);
    try { await onRestore(row.existingId); } catch { setRestoreFailed(row.existingId); } finally { setRestoring(null); }
  };

  // ── Layout: table or stacked, by the width the content column actually has ──
  const box = useRef<HTMLDivElement>(null);
  const [wide, setWide] = useState(false);
  useLayoutEffect(() => {
    const el = box.current;
    if (!el) return;
    const measure = () => setWide(el.getBoundingClientRect().width >= TABLE_MIN_WIDTH);
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  // ── Stable row keys, so rows animate in and out as themselves (not by index) ──
  const seq = useRef(0);
  const keys = useRef<string[]>([]);
  while (keys.current.length < rows.length) keys.current.push(`${section}-${seq.current++}`);
  if (keys.current.length > rows.length) keys.current = keys.current.slice(0, rows.length);
  const mounted = useRef(false);
  const entering = useRef(new Set<string>());
  useEffect(() => { mounted.current = true; }, []);

  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const [leaving, setLeaving] = useState<string | null>(null);
  const [removed, setRemoved] = useState<{ row: T; index: number; key: string; label: string; after: readonly T[] } | null>(null);
  const [lookAlike, setLookAlike] = useState<Map<string, number>>(() => new Map());
  const [dismissed, setDismissed] = useState<Set<string>>(() => new Set());
  const focusNext = useRef<string | null>(null);
  const undoRef = useRef<HTMLButtonElement>(null);
  const addRef = useRef<HTMLButtonElement>(null);
  const undoTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (!focusNext.current) return;
    const el = document.getElementById(focusNext.current);
    focusNext.current = null;
    el?.focus();
  });
  useEffect(() => () => { if (undoTimer.current) clearTimeout(undoTimer.current); }, []);

  // Latest rows and handler, for the removal that lands after the exit fade.
  const latest = useRef({ rows, onChange });
  latest.current = { rows, onChange };
  const set = (index: number, key: keyof T, value: unknown) => onChange(editRow(rows, index, key, value));

  const add = () => {
    const key = `${section}-${seq.current++}`;
    keys.current = [...keys.current, key];
    if (mounted.current) entering.current.add(key);
    // A new row's references and band are part of filling it in: open them.
    if (details.length > 0) setOpen(prev => new Set([...prev, key]));
    focusNext.current = fieldId(`${path}.${rows.length}.code`);
    onChange([...rows, newRow()]);
  };

  /** Removes a new row (with Undo); focus goes to Undo, or to `focusAfter` when the reader chose where to go. */
  const remove = (index: number, focusAfter?: string) => {
    const key = keys.current[index];
    const row = rows[index];
    const label = String(row.code || row[nameKey] || rowLabel(index));
    const finish = () => {
      const now = latest.current.rows;
      const at = keys.current.indexOf(key);
      if (at < 0) { setLeaving(null); return; }
      keys.current = keys.current.filter(k => k !== key);
      setLeaving(null);
      const after = now.filter((_, i) => i !== at);
      latest.current.onChange(after);
      setRemoved({ row: now[at] ?? row, index: at, key, label, after });
      focusNext.current = focusAfter ?? `ccw-undo-${section}`;
      if (undoTimer.current) clearTimeout(undoTimer.current);
      undoTimer.current = setTimeout(() => {
        // The offer ends; focus that was on Undo moves to the next row, or to Add — never to the page body.
        if (document.activeElement === undoRef.current) {
          const nextRow = document.getElementById(fieldId(`${path}.${at}.code`));
          (nextRow ?? addRef.current)?.focus();
        }
        setRemoved(null);
      }, UNDO_MS);
    };
    if (reducedMotion()) finish();
    else { setLeaving(key); setTimeout(finish, EXIT_MS); }
  };

  const undo = () => {
    if (!removed) return;
    if (undoTimer.current) clearTimeout(undoTimer.current);
    const index = Math.min(removed.index, rows.length);
    keys.current = [...keys.current.slice(0, index), removed.key, ...keys.current.slice(index)];
    entering.current.add(removed.key);
    focusNext.current = fieldId(`${path}.${index}.code`);
    onChange([...rows.slice(0, index), removed.row, ...rows.slice(index)]);
    setRemoved(null);
  };

  // The placeholder stays until Undo, 8 s, or the next edit of this list.
  useEffect(() => {
    if (removed && rows !== removed.after) { if (undoTimer.current) clearTimeout(undoTimer.current); setRemoved(null); }
  }, [rows, removed]);

  const shown = useMemo(() => {
    const base = order ? order.filter(i => i < rows.length) : rows.map((_, i) => i);
    const missing = rows.map((_, i) => i).filter(i => !base.includes(i));
    const all = [...base, ...missing];
    return visible ? all.filter(i => visible(rows[i], i)) : all;
  }, [order, rows, visible]);

  const takenCodes = rows.map(r => r.code);
  const codeSuggestion = (row: T) => (isBlank(row.code) && !isBlank(row[nameKey]) ? suggestCode(String(row[nameKey]), takenCodes, codePrefix) : '');

  /** A new row's English name, once left: does it look like a row already in this list? */
  const checkLookAlike = (index: number) => {
    const row = rows[index];
    const key = keys.current[index];
    if (row.existingId || dismissed.has(key)) return;
    const other = lookAlikeIndex(rows, index, r => String(r[nameKey] ?? ''));
    setLookAlike(prev => {
      const next = new Map(prev);
      if (other >= 0) next.set(key, other); else next.delete(key);
      return next;
    });
  };

  // "Fill Arabic names": walk focus through the empty Arabic cells, in the order shown.
  const walkFrom = useRef(-1);
  const walk = useCallback(() => {
    const empty = shown.filter(i => isBlank(rows[i][arKey]));
    if (empty.length === 0) return false;
    const nextIndex = empty.find(i => shown.indexOf(i) > shown.indexOf(walkFrom.current)) ?? empty[0];
    walkFrom.current = nextIndex;
    document.getElementById(fieldId(`${path}.${nextIndex}.${arKey}`))?.focus();
    return true;
  }, [shown, rows, arKey, path]);
  useEffect(() => { onWalkArabic?.(walk); }, [onWalkArabic, walk]);

  const arabicFirst = locale === 'ar';
  const columns: { key: keyof T & string; label: string; arabic?: boolean; required?: boolean }[] = [
    { key: 'code' as keyof T & string, label: t('Code'), required: true },
    ...(arabicFirst
      ? [{ key: arKey, label: t('Name (Arabic)'), arabic: true }, { key: nameKey, label: t('Name (English)'), required: true }]
      : [{ key: nameKey, label: t('Name (English)'), required: true }, { key: arKey, label: t('Name (Arabic)'), arabic: true }]),
  ];
  const lastColumn = columns[columns.length - 1].key;
  const colId = (key: string) => `ccw-${section}-col-${key}`;

  const rowValid = (row: T) => !isBlank(row.code) && !isBlank(row[nameKey]);
  /** Enter in the last cell of a valid last row adds a row. Enter never reaches Next. */
  const onCellKeyDown = (event: KeyboardEvent<HTMLElement>, index: number, key: string) => {
    if (event.key !== 'Enter' || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    if ((event.target as HTMLElement).tagName === 'SELECT') return;
    event.preventDefault();
    if (key === lastColumn && index === shown[shown.length - 1] && rowValid(rows[index]) && !readOnly) add();
  };

  const formatAmount = (value: unknown) => (Number(value) > 0 ? fmt.plain.integer(Number(value)) : '—');
  const optionLabel = (field: OrgDetailField<T>, value: unknown) => field.options?.find(o => o.value === value)?.label ?? String(value ?? '');

  /** One editable input; `stacked` puts a visible label above it, the table names it by column header + row. */
  const cell = (row: T, index: number, key: keyof T & string, opts: { label: string; arabic?: boolean; required?: boolean; stacked: boolean; rowLabelId: string; locked?: boolean; lockReason?: string; placeholder?: string; kind?: 'text' | 'number' | 'select'; options?: { value: string; label: string }[]; amount?: boolean; inputMode?: 'numeric' | 'decimal' }) => {
    const field = `${path}.${index}.${key}`;
    const id = fieldId(field);
    const error = errorFor(field);
    const warning = !error ? rowWarnings?.get(field) : undefined;
    const errorId = error ? `${id}-error` : warning ? `${id}-warning` : undefined;
    const value = row[key];
    const naming = opts.stacked ? {} : { 'aria-labelledby': `${colId(key)} ${opts.rowLabelId}` };
    const common = {
      id, 'data-field': field, ...naming,
      ...(error ? { 'aria-invalid': true as const } : {}),
      ...(errorId ? { 'aria-describedby': errorId } : {}),
      ...(opts.required ? { 'aria-required': true as const } : {}),
      onKeyDown: (e: KeyboardEvent<HTMLElement>) => onCellKeyDown(e, index, key),
    };
    const lockedCode = !!opts.locked;
    const control = opts.kind === 'select'
      ? <select {...common} className="select w-full min-w-0" value={String(value ?? '')} disabled={readOnly} onChange={e => set(index, key, e.target.value || undefined)}>
          <option value="">{t('None')}</option>
          {(opts.options ?? []).map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
        </select>
      : <input {...common}
          className={`input w-full min-w-0 ${key === 'code' ? 'field-ltr' : ''} ${opts.kind === 'number' ? 'tabular-nums' : ''} ${lockedCode ? 'cursor-default border-transparent bg-slate-50 pe-7 text-slate-600 dark:bg-white/[0.03] dark:text-slate-300' : ''} ${error ? 'border-rose-400 dark:border-rose-400/60' : ''}`}
          dir={opts.arabic ? 'rtl' : key === 'code' ? 'ltr' : undefined} lang={opts.arabic ? 'ar' : undefined}
          type={opts.kind === 'number' ? 'number' : 'text'} inputMode={opts.inputMode}
          readOnly={readOnly || lockedCode || isMarked(row)}
          aria-readonly={lockedCode || undefined}
          placeholder={opts.placeholder}
          aria-describedby={lockedCode ? `${id}-lock` : errorId}
          value={opts.amount && !(Number(value) > 0) ? '' : String(value ?? '')}
          onChange={e => set(index, key, opts.kind === 'number' ? (e.target.value === '' ? 0 : Number(e.target.value)) : key === 'code' ? e.target.value.toUpperCase() : e.target.value)}
          onBlur={key === nameKey ? () => checkLookAlike(index) : undefined} />;
    return (
      <div className="min-w-0">
        {opts.stacked && (
          <label htmlFor={id} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">
            {opts.label}{opts.required && <span className="text-rose-600 dark:text-rose-400" aria-hidden="true"> *</span>}
          </label>
        )}
        <div className="relative">
          {control}
          {lockedCode && <Lock className="pointer-events-none absolute end-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" aria-hidden="true" />}
        </div>
        {opts.arabic && !lockedCode && <ArabicSuggest source={String(row[nameKey] ?? '')} value={String(value ?? '')} inputId={id} readOnly={readOnly}
          context={rowLabel(index)} onAccept={arabic => set(index, key, arabic)} />}
        {lockedCode && opts.lockReason && <p id={`${id}-lock`} className="mt-1 text-xs leading-5 text-slate-600 dark:text-slate-400" data-testid="code-lock-reason">{opts.lockReason}</p>}
        {error && <p id={errorId} className="mt-1 text-xs font-medium leading-5 text-rose-700 dark:text-rose-300">{error}</p>}
        {warning && <p id={errorId} className="mt-1 text-xs leading-5 text-amber-800 dark:text-amber-200">{t(warning.message, warning.params)}</p>}
      </div>
    );
  };

  const statusOf = (row: T) => {
    if (!existingMode) return null;
    if (isMarked(row)) return <span className="inline-flex h-5 items-center rounded-full bg-amber-50 px-2 text-caption font-semibold leading-none rtl:text-xs text-amber-900 dark:bg-amber-500/15 dark:text-amber-100" data-testid="will-be-removed">{t('Will be removed')}</span>;
    if (isOff(row)) return <span className="inline-flex h-5 items-center rounded-full bg-slate-200 px-2 text-caption font-semibold leading-none rtl:text-xs text-slate-700 dark:bg-white/15 dark:text-slate-200" data-testid="row-off">{t('Off')}</span>;
    if (!row.existingId) return <span className="inline-flex h-5 items-center rounded-full bg-emerald-50 px-2 text-caption font-semibold leading-none rtl:text-xs text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-200">{t('New')}</span>;
    const fields = ['code', nameKey, arKey, ...details.map(d => d.key)] as (keyof T)[];
    if (!rowChanged(row, baselineRows, fields)) return null;
    const before = baselineRows?.find(b => b.existingId === row.existingId);
    const wasName = before && String(before[nameKey] ?? '') !== String(row[nameKey] ?? '') ? String(before[nameKey] ?? '') : '';
    return (
      <span className="flex min-w-0 flex-col items-start gap-0.5">
        <span className="inline-flex h-5 items-center rounded-full bg-sky-50 px-2 text-caption font-semibold leading-none rtl:text-xs text-sky-800 dark:bg-sky-500/15 dark:text-sky-200">{t('Changed')}</span>
        {wasName && <span className="max-w-[10rem] truncate text-caption text-slate-600 rtl:text-xs dark:text-slate-400" title={wasName}><bdi>{t('Was: {value}', { value: wasName })}</bdi></span>}
      </span>
    );
  };

  const RM_BTN = 'inline-flex h-8 items-center gap-1 rounded-lg px-2 text-xs font-medium text-slate-600 transition-colors duration-150 hover:bg-rose-50 hover:text-rose-700 focus-visible:text-rose-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-rose-500/50 motion-safe:active:scale-[0.98] dark:text-slate-400 dark:hover:bg-rose-500/10 dark:hover:text-rose-300';
  /** Remove on EVERY row. A new row leaves at once (Undo); a saved row is marked and the server decides on save. */
  const removeButton = (row: T, index: number) => {
    if (readOnly) return null;
    const saved = !!row.existingId;
    if (saved && !onRemovedIdsChange) return null;
    if (isMarked(row)) return (
      <button type="button" onMouseDown={e => e.preventDefault()} onClick={() => unmark(row)} data-testid="undo-remove"
        className="inline-flex h-8 items-center gap-1 rounded-lg px-2 text-xs font-semibold text-blue-700 hover:bg-sapphire/[0.06] dark:text-primary-fg">
        {t('Undo')}<span className="sr-only"> {t('removing')} {rowLabel(index)}</span>
      </button>
    );
    return (
      <span className="inline-flex items-center gap-0.5">
        {saved && isOff(row) && onRestore && (
          <button type="button" onMouseDown={e => e.preventDefault()} onClick={() => void restore(row)} disabled={restoring === row.existingId} data-testid="restore-row"
            className="inline-flex h-8 items-center gap-1 rounded-lg px-2 text-xs font-semibold text-blue-700 hover:bg-sapphire/[0.06] disabled:opacity-50 dark:text-primary-fg">
            <RotateCcw className="h-3.5 w-3.5" aria-hidden="true" />{t('Restore')}<span className="sr-only"> {rowLabel(index)}</span>
          </button>
        )}
        <button type="button" onMouseDown={e => e.preventDefault()} onClick={() => (saved ? mark(row) : remove(index))} data-testid="remove-row" className={RM_BTN}>
          <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />{t('Remove')}<span className="sr-only"> {rowLabel(index)}</span>
        </button>
      </span>
    );
  };

  /** The line under a saved row that says what will happen to it, or that it is off. Null when there is nothing to say. */
  const rowNote = (row: T) => {
    if (!row.existingId) return null;
    const state = stateOf(row);
    return (
      <>
        {isMarked(row) && (
          <p className="text-xs leading-5 text-amber-900 dark:text-amber-100" data-testid="removal-note">
            {t('Will be removed when you save. If anything uses it, it is turned off instead, stays in history, and can be restored.')}
            {state?.inUse && state.usageSummary ? <> <bdi>{usageSentence(state.usage, t, state.usageSummary)}.</bdi></> : null}
          </p>
        )}
        {!isMarked(row) && isOff(row) && (
          <p className="text-xs leading-5 text-slate-600 dark:text-slate-400" data-testid="off-note">
            {t('Turned off: it stays in history but is not offered for new records.')}
            {state?.inUse && state.usageSummary ? <> <bdi>{usageSentence(state.usage, t, state.usageSummary)}.</bdi></> : null}
          </p>
        )}
        {restoreFailed === row.existingId && <p role="alert" className="text-xs font-medium text-rose-700 dark:text-rose-300">{t('It could not be turned back on. Try again.')}</p>}
      </>
    );
  };

  /** The second line: references and band, summarised; "Edit details" opens the inputs. */
  const detailsLine = (row: T, index: number, key: string, stacked: boolean, rowLabelId: string) => {
    const lookAlikeOther = lookAlike.get(key);
    const hasError = details.some(d => !!errorFor(`${path}.${index}.${d.key}`));
    const isOpen = open.has(key) || hasError;
    const toggle = () => setOpen(prev => { const next = new Set(prev); if (next.has(key)) next.delete(key); else next.add(key); return next; });
    const note = detailNote?.(row, index);
    const suggestion = codeSuggestion(row);
    const status = rowNote(row);
    if (details.length === 0 && lookAlikeOther === undefined && !note && !suggestion && !status) return null;
    const detailsId = `${fieldId(`${path}.${index}`)}-details`;
    return (
      <div className="space-y-2">
        {status}
        {suggestion && !readOnly && (
          <p className="flex flex-wrap items-center gap-2 text-xs text-slate-600 dark:text-slate-400">
            <span>{t('Suggested code: {code}', { code: suggestion })}</span>
            <button type="button" onMouseDown={e => e.preventDefault()} className="font-semibold text-blue-700 hover:underline dark:text-primary-fg" onClick={() => { set(index, 'code' as keyof T, suggestion); focusNext.current = fieldId(`${path}.${index}.code`); }}>
              {t('Use {code}', { code: suggestion })}<span className="sr-only"> {rowLabel(index)}</span>
            </button>
          </p>
        )}
        {lookAlikeOther !== undefined && rows[lookAlikeOther] && (
          <div role="status" className="flex flex-wrap items-center gap-2 rounded-lg border border-amber-200 bg-amber-50/70 px-3 py-2 text-xs text-amber-950 motion-safe:animate-fade-in-fast dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100" data-testid="look-alike">
            <span>{t('This looks like {name} ({code}), already in this list.', { name: String(rows[lookAlikeOther][nameKey] || '—'), code: rows[lookAlikeOther].code || '—' })}</span>
            <button type="button" onMouseDown={e => e.preventDefault()} className="font-semibold underline" onClick={() => {
              const target = lookAlikeOther > index ? lookAlikeOther - 1 : lookAlikeOther;
              setLookAlike(prev => { const next = new Map(prev); next.delete(key); return next; });
              remove(index, fieldId(`${path}.${target}.${String(nameKey)}`));
            }}>{t('Use existing')}</button>
            <button type="button" className="font-semibold underline" onClick={() => {
              setDismissed(prev => new Set([...prev, key]));
              setLookAlike(prev => { const next = new Map(prev); next.delete(key); return next; });
            }}>{t('Create new anyway')}</button>
          </div>
        )}
        {details.length > 0 && (
          <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-xs">
            {!isOpen && details.map(d => {
              const value = row[d.key];
              const empty = d.kind === 'number' ? !(Number(value) > 0) : isBlank(value);
              const text = d.kind === 'number'
                ? (d.amount ? formatAmount(value) : String(value ?? '—'))
                : empty ? (d.emptyLabel ? t(d.emptyLabel) : t('None')) : optionLabel(d, value);
              return (
                <span key={d.key} className={`min-w-0 truncate ${empty && d.wanted ? 'font-medium text-amber-800 dark:text-amber-200' : 'text-slate-600 dark:text-slate-400'}`} title={`${t(d.label)}: ${text}`}>
                  <span className="text-slate-500 dark:text-slate-500">{t(d.label)}</span>{' '}<bdi className={d.kind === 'number' ? 'tabular-nums' : ''}>{text}</bdi>
                </span>
              );
            })}
            {!readOnly && !hasError && (
              <button type="button" onMouseDown={e => e.preventDefault()} onClick={toggle} aria-expanded={isOpen} aria-controls={detailsId}
                className="inline-flex items-center gap-1 rounded-lg px-1.5 py-0.5 font-semibold text-blue-700 transition-colors duration-150 hover:bg-sapphire/[0.06] dark:text-primary-fg">
                {isOpen ? t('Hide details') : t('Edit details')}<span className="sr-only"> {rowLabel(index)}</span>
                <ChevronDown className={`h-3 w-3 transition-transform duration-150 motion-reduce:transition-none ${isOpen ? 'rotate-180' : ''}`} aria-hidden="true" />
              </button>
            )}
          </div>
        )}
        {details.length > 0 && isOpen && (
          <div id={detailsId} className={`grid gap-x-3 gap-y-2 motion-safe:animate-fade-in-fast ${stacked ? 'grid-cols-2' : 'grid-cols-[repeat(auto-fit,minmax(9rem,1fr))]'}`}>
            {details.map(d => (
              <div key={d.key} className={stacked && d.kind === 'select' ? 'col-span-2' : ''}>
                {cell(row, index, d.key, { label: t(d.label), stacked: true, rowLabelId, kind: d.kind, options: d.options, amount: d.amount, inputMode: d.inputMode })}
              </div>
            ))}
          </div>
        )}
        {note}
      </div>
    );
  };

  const rowInvalid = (index: number) => ['code', nameKey, arKey, ...details.map(d => d.key)].some(k => !!errorFor(`${path}.${index}.${k}`));
  const motionClass = (key: string) => [
    entering.current.has(key) ? ROW_ENTER : '',
    leaving === key ? 'opacity-0 -translate-y-1 transition duration-150 ease-out motion-reduce:transition-none' : '',
  ].join(' ');

  /** Same height as a table row, where the row was: "{row} removed. Undo". */
  const placeholder = removed && (
    <div className="flex h-11 min-w-0 items-center justify-between gap-2 rounded-lg border border-dashed border-slate-300 px-3 text-sm text-slate-600 motion-safe:animate-fade-in-fast dark:border-white/15 dark:text-slate-400" role="status" data-testid="row-removed">
      <span className="min-w-0 truncate"><bdi>{t('{row} removed.', { row: removed.label })}</bdi></span>
      <button ref={undoRef} id={`ccw-undo-${section}`} type="button" onClick={undo}
        className="shrink-0 rounded px-1.5 font-semibold text-blue-700 underline-offset-2 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:text-primary-fg">
        {t('Undo')}<span className="sr-only"> {removed.label}</span>
      </button>
    </div>
  );
  /** Where the placeholder sits among the shown rows: before the first row now at or after the removed index. */
  const placeholderAt = removed ? (() => { const at = shown.findIndex(i => i >= removed.index); return at < 0 ? shown.length : at; })() : -1;
  const colCount = columns.length + (existingMode ? 2 : 1);

  const addBar = !readOnly && (
    <div className="flex flex-wrap items-center gap-2 pt-1">
      <button ref={addRef} type="button" className="btn-secondary" onClick={add}><Plus className="h-4 w-4" aria-hidden="true" />{addLabel}</button>
      {manage && (
        <a href={manage.href} target="_blank" rel="noopener noreferrer" data-testid="manage-in-setup"
          className="inline-flex items-center gap-1 rounded-lg px-2 py-1 text-xs font-semibold text-sapphire hover:underline dark:text-cyanAccent">
          {t('Manage in Setup → {tab}', { tab: t(manage.label) })}<ExternalLink className="h-3 w-3 rtl:-scale-x-100" aria-hidden="true" /><span className="sr-only"> {t('(opens in a new tab)')}</span>
        </a>
      )}
      <FillMissingArabic<T> rows={rows} arabicKey={arKey} english={row => String(row[nameKey] ?? '')} onFill={onChange}
        eligible={row => !row.existingId} label={(_, i) => rowLabel(i)} />
    </div>
  );

  return (
    <div ref={box} className="min-w-0 space-y-2" data-testid={`${section}-editor`} data-layout={wide ? 'table' : 'stacked'}>
      {rows.length === 0 && <p className="rounded-lg border border-dashed border-slate-300 px-4 py-3 text-sm text-slate-600 dark:border-white/15 dark:text-slate-400">{emptyText}</p>}
      {rows.length > 0 && wide && (
        <div className="overflow-hidden rounded-xl border border-slate-200 dark:border-white/10">
          <table className="w-full table-fixed border-collapse text-sm">
            <thead className="bg-slate-50/80 text-xs font-semibold text-slate-600 dark:bg-white/[0.03] dark:text-slate-400">
              <tr>
                {columns.map(c => (
                  <th key={c.key} id={colId(c.key)} scope="col" className={`px-3 py-2 text-start ${c.key === 'code' ? 'w-28' : ''}`}>
                    {c.label}{c.required && <span className="text-rose-600 dark:text-rose-400" aria-hidden="true"> *</span>}
                  </th>
                ))}
                {existingMode && <th scope="col" className="w-28 px-3 py-2 text-start">{t('Status')}</th>}
                <th scope="col" className="w-44 px-3 py-2 text-end"><span className="sr-only">{t('Actions')}</span></th>
              </tr>
            </thead>
            {shown.map((index, at) => {
              const row = rows[index];
              const key = keys.current[index];
              const rowLabelId = `${fieldId(`${path}.${index}`)}-label`;
              const line = detailsLine(row, index, key, false, rowLabelId);
              return (
                <Fragment key={key}>
                {at === placeholderAt && <tbody><tr><td colSpan={colCount} className="px-2 py-0">{placeholder}</td></tr></tbody>}
                <tbody className={`border-t border-slate-100 align-top first:border-t-0 dark:border-white/[0.06] ${rowInvalid(index) ? 'bg-rose-50/40 dark:bg-rose-500/[0.04]' : ''} ${motionClass(key)}`}
                  data-testid={`${section}-row`} data-existing={row.existingId ? 'true' : undefined} data-removed={isMarked(row) ? 'true' : undefined}>
                  <tr className={isMarked(row) || isOff(row) ? 'opacity-70' : undefined}>
                    {columns.map((c, ci) => (
                      <td key={c.key} className="px-3 py-1">
                        {ci === 0 && <span id={rowLabelId} className="sr-only">{rowLabel(index)}</span>}
                        {cell(row, index, c.key, { label: c.label, arabic: c.arabic, required: c.required, stacked: false, rowLabelId,
                          locked: c.key === 'code' && codeLocked(row), lockReason: codeLockReason(row), placeholder: c.key === 'code' ? (codeSuggestion(row) ? t('Suggested: {code}', { code: codeSuggestion(row) }) : undefined) : undefined })}
                      </td>
                    ))}
                    {existingMode && <td className="px-3 py-1"><div className="flex min-h-9 items-center">{statusOf(row)}</div></td>}
                    <td className="px-3 py-1 text-end"><div className="flex min-h-9 items-center justify-end">{removeButton(row, index)}</div></td>
                  </tr>
                  {line && <tr><td colSpan={colCount} className="px-3 pb-2 pt-0">{line}</td></tr>}
                </tbody>
                </Fragment>
              );
            })}
            {placeholderAt === shown.length && <tbody><tr><td colSpan={colCount} className="px-2 py-0">{placeholder}</td></tr></tbody>}
          </table>
        </div>
      )}
      {rows.length > 0 && !wide && (
        <ul className="divide-y divide-slate-100 overflow-hidden rounded-xl border border-slate-200 dark:divide-white/[0.06] dark:border-white/10">
          {shown.map((index, at) => {
            const row = rows[index];
            const key = keys.current[index];
            const rowLabelId = `${fieldId(`${path}.${index}`)}-label`;
            const status = statusOf(row);
            return (
              <Fragment key={key}>
              {at === placeholderAt && <li className="p-2">{placeholder}</li>}
              <li className={`space-y-2 p-3 ${rowInvalid(index) ? 'bg-rose-50/40 dark:bg-rose-500/[0.04]' : ''} ${motionClass(key)}`}
                data-testid={`${section}-row`} data-existing={row.existingId ? 'true' : undefined} data-removed={isMarked(row) ? 'true' : undefined}>
                <div className="flex min-w-0 items-center justify-between gap-2">
                  <span id={rowLabelId} className="flex min-w-0 items-center gap-2 text-xs font-semibold text-slate-600 dark:text-slate-400">{rowLabel(index)}{status}</span>
                  {removeButton(row, index)}
                </div>
                <div className="grid grid-cols-1 gap-2 min-[420px]:grid-cols-[7rem_minmax(0,1fr)]">
                  {cell(row, index, 'code' as keyof T & string, { label: t('Code'), required: true, stacked: true, rowLabelId, locked: codeLocked(row), lockReason: codeLockReason(row),
                    placeholder: codeSuggestion(row) ? t('Suggested: {code}', { code: codeSuggestion(row) }) : undefined })}
                  {cell(row, index, columns[1].key, { label: columns[1].label, arabic: columns[1].arabic, required: columns[1].required, stacked: true, rowLabelId })}
                </div>
                {cell(row, index, columns[2].key, { label: columns[2].label, arabic: columns[2].arabic, required: columns[2].required, stacked: true, rowLabelId })}
                {detailsLine(row, index, key, true, rowLabelId)}
              </li>
              </Fragment>
            );
          })}
          {placeholderAt === shown.length && <li className="p-2">{placeholder}</li>}
        </ul>
      )}
      {rows.length === 0 && placeholder}
      {addBar}
    </div>
  );
}
