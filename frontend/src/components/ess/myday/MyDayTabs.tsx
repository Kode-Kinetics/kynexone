'use client';

/** Today / My records. A real tablist: roving tabindex, arrows, Home and End, mirrored in RTL. */

import { useT } from '../../../hooks/useT';

export type MyDayView = 'today' | 'records';
export const MYDAY_VIEWS: Array<{ id: MyDayView; label: string }> = [
  { id: 'today', label: 'Today' },
  { id: 'records', label: 'My records' },
];

export const myDayTabId = (id: MyDayView) => `myday-tab-${id}`;
export const myDayPanelId = (id: MyDayView) => `myday-panel-${id}`;

export function MyDayTabs({ active, onChange }: { active: MyDayView; onChange: (id: MyDayView) => void }) {
  const t = useT();
  const onKeyDown = (e: React.KeyboardEvent, index: number) => {
    const rtl = document.documentElement.dir === 'rtl';
    const fwd = rtl ? 'ArrowLeft' : 'ArrowRight';
    const back = rtl ? 'ArrowRight' : 'ArrowLeft';
    let next = index;
    if (e.key === fwd) next = (index + 1) % MYDAY_VIEWS.length;
    else if (e.key === back) next = (index - 1 + MYDAY_VIEWS.length) % MYDAY_VIEWS.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = MYDAY_VIEWS.length - 1;
    else return;
    e.preventDefault();
    const id = MYDAY_VIEWS[next].id;
    onChange(id);
    document.getElementById(myDayTabId(id))?.focus();
  };

  return (
    <div role="tablist" aria-label={t('My day views')} data-testid="ess-myday-tabs"
      className="inline-flex w-fit shrink-0 gap-1 rounded-xl border border-[color:var(--wg-line-strong)] bg-[color:var(--wg-surface)] p-1 max-sm:flex-1">
      {MYDAY_VIEWS.map((v, i) => {
        const selected = active === v.id;
        return (
          <button key={v.id} id={myDayTabId(v.id)} type="button" role="tab" aria-selected={selected} aria-controls={myDayPanelId(v.id)}
            tabIndex={selected ? 0 : -1} onClick={() => onChange(v.id)} onKeyDown={(e) => onKeyDown(e, i)}
            className={`h-8 rounded-lg px-4 text-body-sm font-semibold outline-none transition-colors duration-fast focus-visible:ring-2 focus-visible:ring-sapphire max-sm:h-10 max-sm:flex-1 ${
              selected
                ? 'bg-gradient-to-br from-[color:var(--wg-hero-light)] to-[color:var(--wg-hero-base)] text-white'
                : 'text-slate-700 hover:text-slate-950 dark:text-slate-300 dark:hover:text-white'}`}>
            {t(v.label)}
          </button>
        );
      })}
    </div>
  );
}
