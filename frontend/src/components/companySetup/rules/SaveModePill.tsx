'use client';

import { useEffect, useRef, useState } from 'react';
import { Check } from 'lucide-react';
import { useLocale } from '../../../contexts/LocaleContext';
import type { LeaveEditorChange } from '../../../views/LeavePage';
import styles from './motion.module.css';

export type SaveMode = 'live' | 'create' | 'update' | 'elsewhere';
export interface SaveTally { added: number; updated: number; removed: number; recent: boolean }
const EMPTY: SaveTally = { added: 0, updated: 0, removed: 0, recent: false };

/** Counts live saves in this part; `recent` stays true for 4 s after each save (motion spec M7, audit C5). */
export function useSaveTally(): [SaveTally, (change: LeaveEditorChange) => void] {
  const [tally, setTally] = useState<SaveTally>(EMPTY);
  const timer = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearTimeout(timer.current), []);
  const record = useRef((change: LeaveEditorChange) => {
    setTally(previous => ({ ...previous, [change]: previous[change] + 1, recent: true }));
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setTally(previous => ({ ...previous, recent: false })), 4000);
  }).current;
  return [tally, record];
}

/**
 * Says how a block saves, because step 5 mixes three models (audit A10): draft data saved by the one Create /
 * Update, embedded editors that save as you edit, and settings that save on their own screen. A live block's
 * chip also reports what was saved — inline, never a toast.
 */
export function SaveModePill({ mode, tally }: { mode: SaveMode; tally?: SaveTally }) {
  const { t, locale } = useLocale();
  const saved = mode === 'live' && tally && tally.added + tally.updated + tally.removed > 0;
  const parts = saved ? [
    tally.added ? t('{count} added', { count: tally.added }) : '',
    tally.updated ? t('{count} updated', { count: tally.updated }) : '',
    tally.removed ? t('{count} removed', { count: tally.removed }) : '',
  ].filter(Boolean) : [];
  const summary = (() => {
    try { return new Intl.ListFormat(locale, { type: 'unit', style: 'short' }).format(parts); } catch { return parts.join(', '); }
  })();
  const label = saved
    ? (tally.recent ? t('Saved · {summary}', { summary }) : t('Saved'))
    : mode === 'live' ? t('Saves as you edit')
    : mode === 'update' ? t('Saved when you press Save all changes')
    : mode === 'create' ? t('Saved when you press Create company')
    : t('Each setting saves on its own screen');
  const live = mode === 'live';
  return (
    <span data-testid={`save-mode-${mode}`} role={live ? 'status' : undefined} title={saved ? t('Saved · {summary}', { summary }) : undefined}
      className={`inline-flex h-6 max-w-full shrink-0 items-center gap-1.5 rounded-full px-2.5 text-xs font-medium tabular-nums ${live
        ? 'bg-emerald-50 text-emerald-800 ring-1 ring-inset ring-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-200 dark:ring-emerald-400/20'
        : 'bg-slate-100 text-slate-700 ring-1 ring-inset ring-slate-200 dark:bg-white/[0.06] dark:text-slate-300 dark:ring-white/10'}`}>
      {saved
        ? <Check className="h-3 w-3 shrink-0" aria-hidden="true" />
        : <span aria-hidden="true" className={`h-1.5 w-1.5 shrink-0 rounded-full ${live ? 'bg-emerald-500' : 'bg-slate-400'}`} />}
      <span key={label} className={`truncate ${saved ? styles.tick : ''}`}>{label}</span>
    </span>
  );
}
