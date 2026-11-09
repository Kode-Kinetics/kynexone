'use client';

import { Hourglass, Pencil, Plus, TriangleAlert } from 'lucide-react';
import type { CellView } from '../../lib/benefitsGrid';
import { useLocale } from '../../contexts/LocaleContext';

/**
 * One value in Benefits by grade. It is a full-width button whose look says what it is without relying on colour:
 *   a value            the value, with a pencil at the inline-end (can be changed)
 *   an empty value     "+ Set value" (can be filled)
 *   edited             the value, the pencil and the word "Edited" with a dot
 *   waiting / fixed    the value only, no pencil, not a control
 * The accessible name carries the value and its state; the pencil and the plus are decoration.
 */
export function MatrixCell({ view, label, summary, selected, edited, problem, override, note, onOpen }: {
  view: CellView; label: string; summary: string; selected: boolean;
  /** Changed here and not published yet. */
  edited: boolean;
  /** Why the edit cannot be published, as text. */
  problem?: string;
  /** In a tailored row: this value is the company's own, not the group's. */
  override: boolean;
  /** A line under the value (what a locked cell is waiting for). */
  note?: string;
  onOpen: () => void;
}) {
  const { t } = useLocale();
  const tone = view.kind === 'error' ? 'border-danger bg-danger-soft'
    : view.kind === 'edited' ? 'border-primary bg-primary-soft'
    : view.kind === 'unset' ? 'border-warning bg-warning-soft'
    : view.kind === 'starts' ? 'border-primary-fg/40 bg-surface'
    : view.kind === 'locked' || view.kind === 'readonly' ? 'border-line bg-surface-sunken'
    : 'border-line bg-surface';
  const hover = view.interactive ? 'hover:border-line-strong' : '';
  return <button type="button" data-cell disabled={!view.interactive} aria-pressed={selected} aria-label={label} onClick={onOpen} title={view.setCall ? undefined : summary}
    className={`flex min-h-10 w-full items-center justify-between gap-1 rounded-lg border px-1.5 py-0.5 text-start text-body-sm transition-colors duration-fast disabled:cursor-default ${tone} ${hover} ${selected ? 'ring-2 ring-primary' : ''}`}>
    <span className="min-w-0 flex-1">
      {view.setCall
        ? <span data-testid="cell-set-value" className="inline-flex items-center gap-1 font-semibold text-warning-fg">
          <Plus className="h-3.5 w-3.5 shrink-0" aria-hidden />{t('Set value')}</span>
        : <span className="line-clamp-2 break-words">
          <bdi className={view.kind === 'notOffered' || view.kind === 'unset' || view.kind === 'locked' ? 'text-fg-muted' : 'text-fg'}>
            {view.kind === 'locked' && <Hourglass className="me-1 inline h-3 w-3" aria-hidden />}{summary}</bdi></span>}
      {override && <span data-testid="cell-override" className="ms-1.5 inline-flex items-center align-middle">
        <span aria-hidden className="h-1.5 w-1.5 rounded-full bg-primary" /><span className="sr-only">{t('Set for this company')}</span></span>}
      {edited && view.kind !== 'error' && <span className="mt-0.5 flex items-center gap-1 text-caption font-semibold text-primary-fg">
        <span aria-hidden className="h-1.5 w-1.5 rounded-full bg-primary" />{t('Edited')}</span>}
      {note && <span className="mt-0.5 block text-caption text-fg-muted">{note}</span>}
      {problem && <span className="mt-0.5 flex items-start gap-1 text-caption text-danger-fg"><TriangleAlert className="mt-px h-3 w-3 shrink-0" aria-hidden />{problem}</span>}
    </span>
    {view.pencil && <Pencil data-testid="cell-pencil" className="h-3.5 w-3.5 shrink-0 text-fg-muted" aria-hidden />}
  </button>;
}
