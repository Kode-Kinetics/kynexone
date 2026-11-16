'use client';

/**
 * The dashboard's section switch on desktop. The page is one screen tall; these tabs move
 * between full screens instead of scrolling down a long one. A real tablist (roving tabindex,
 * arrows / Home / End, mirrored in RTL), so nothing depends on a mouse. A gradient indicator
 * slides under the selected tab; arrow-key switches report `via: 'keyboard'` so the page can
 * swap instantly instead of animating.
 */

import { useLayoutEffect, useRef, useState } from 'react';
import { useT } from '../../hooks/useT';

export type StageViewId = 'overview' | 'departments' | 'compliance';
export type StageSwitchVia = 'pointer' | 'keyboard';

export const STAGE_VIEWS: Array<{ id: StageViewId; label: string }> = [
  { id: 'overview', label: 'Overview' },
  { id: 'departments', label: 'Departments' },
  { id: 'compliance', label: 'Compliance' },
];

export const stageTabId = (id: StageViewId) => `stage-tab-${id}`;
export const STAGE_PANEL_ID = 'stage-panel';

/** `badges`: a count shown on a tab (e.g. critical compliance items), as text in the tab's name, never colour alone. */
export function StageTabs({ active, onChange, badges }: { active: StageViewId; onChange: (id: StageViewId, via: StageSwitchVia) => void; badges?: Partial<Record<StageViewId, number>> }) {
  const t = useT();
  const listRef = useRef<HTMLDivElement>(null);
  const [ind, setInd] = useState<{ x: number; w: number } | null>(null);

  // Measured, not computed: tab widths depend on the language and the font.
  useLayoutEffect(() => {
    const measure = () => {
      const btn = document.getElementById(stageTabId(active));
      const list = listRef.current;
      // Distance from the inline-start edge, so the indicator follows the tab in either direction.
      if (btn && list) setInd({ x: list.dir === 'rtl' || getComputedStyle(list).direction === 'rtl' ? list.clientWidth - btn.offsetLeft - btn.offsetWidth : btn.offsetLeft, w: btn.offsetWidth });
    };
    measure();
    window.addEventListener('resize', measure);
    document.fonts?.ready.then(measure).catch(() => {});
    return () => window.removeEventListener('resize', measure);
  }, [active, t, badges]);

  const onKeyDown = (e: React.KeyboardEvent, index: number) => {
    const rtl = document.documentElement.dir === 'rtl';
    const fwd = rtl ? 'ArrowLeft' : 'ArrowRight';
    const back = rtl ? 'ArrowRight' : 'ArrowLeft';
    let next = index;
    if (e.key === fwd) next = (index + 1) % STAGE_VIEWS.length;
    else if (e.key === back) next = (index - 1 + STAGE_VIEWS.length) % STAGE_VIEWS.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = STAGE_VIEWS.length - 1;
    else return;
    e.preventDefault();
    const id = STAGE_VIEWS[next].id;
    onChange(id, 'keyboard');
    document.getElementById(stageTabId(id))?.focus();
  };

  return (
    <div ref={listRef} role="tablist" aria-label={t('Dashboard sections')}
      className="relative inline-flex w-fit shrink-0 gap-1 rounded-xl border border-[color:var(--wg-line-strong)] bg-[color:var(--wg-surface)] p-1">
      {ind && (
        <span aria-hidden
          className="pointer-events-none absolute bottom-1 top-1 rounded-lg bg-gradient-to-br from-[color:var(--wg-hero-light)] to-[color:var(--wg-hero-base)] shadow-[0_6px_14px_-6px_var(--wg-hero-shadow)] transition-[inset-inline-start,width] duration-base ease-standard motion-reduce:transition-none"
          style={{ width: ind.w, insetInlineStart: ind.x }} />
      )}
      {STAGE_VIEWS.map((v, i) => {
        const selected = active === v.id;
        return (
          <button key={v.id} id={stageTabId(v.id)} type="button" role="tab"
            aria-selected={selected} aria-controls={STAGE_PANEL_ID} tabIndex={selected ? 0 : -1}
            onClick={() => onChange(v.id, 'pointer')} onKeyDown={(e) => onKeyDown(e, i)}
            className={`relative z-10 h-8 rounded-lg px-4 text-body-sm font-semibold outline-none transition-colors duration-fast focus-visible:ring-2 focus-visible:ring-sapphire ${
              selected ? 'text-white' : 'text-slate-700 hover:text-slate-950 dark:text-slate-300 dark:hover:text-white'}`}>
            {t(v.label)}
            {!!badges?.[v.id] && (
              <span className={`ms-1.5 inline-grid min-w-[1.25rem] place-items-center rounded-full px-1.5 text-caption font-semibold tabular-nums leading-5 ${selected ? 'bg-white text-[color:var(--wg-hero-base)]' : 'bg-rose-100 text-rose-800 dark:bg-rose-500/20 dark:text-rose-200'}`}>{badges[v.id]}</span>
            )}
            {!!badges?.[v.id] && (
              <span className="sr-only">{t('Critical items')}</span>
            )}
          </button>
        );
      })}
    </div>
  );
}
