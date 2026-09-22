'use client';

/**
 * Today / Actions / Insights.
 *
 * Below lg (phones and portrait tablets) the three views sit side by side in a scroll-snap
 * rail: swipe follows the finger natively, and a visible segmented control above it does the
 * same thing by tap or keyboard, so swipe is never the only way to move. The chosen view is
 * kept in sessionStorage so an interruption (a call, a trip to another page) returns to it.
 *
 * At lg and up there is no rail and no tab bar: the same three panels stack in reading
 * order, so desktop never needs a swipe and nothing is hidden behind one.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import { useMediaQuery } from '../../hooks/useMediaQuery';
import { useT } from '../../hooks/useT';

export type DashboardViewId = 'today' | 'actions' | 'insights';

const VIEWS: Array<{ id: DashboardViewId; label: string }> = [
  { id: 'today', label: 'Today' },
  { id: 'actions', label: 'To do' },
  { id: 'insights', label: 'Insights' },
];

const STORAGE_KEY = 'kx-dashboard-view';

function readStored(): DashboardViewId {
  try {
    const v = sessionStorage.getItem(STORAGE_KEY);
    return v === 'actions' || v === 'insights' ? v : 'today';
  } catch {
    return 'today';
  }
}

export function DashboardViews({
  today,
  actions,
  insights,
  badges,
}: {
  today: React.ReactNode;
  actions: React.ReactNode;
  insights: React.ReactNode;
  /** Small counts shown on the tabs (e.g. open actions), so a hidden view still signals work. */
  badges?: Partial<Record<DashboardViewId, number>>;
}) {
  const t = useT();
  const compact = useMediaQuery('(max-width: 1023.98px)');
  const reduceMotion = useMediaQuery('(prefers-reduced-motion: reduce)');
  const [active, setActive] = useState<DashboardViewId>('today');
  const railRef = useRef<HTMLDivElement>(null);
  const panelRefs = useRef<Record<DashboardViewId, HTMLElement | null>>({ today: null, actions: null, insights: null });
  const tabRefs = useRef<Record<DashboardViewId, HTMLButtonElement | null>>({ today: null, actions: null, insights: null });
  const programmatic = useRef(false);

  // Restore the last view without animating to it. Nothing is written back until the stored
  // value has been read: during hydration `compact` is briefly false (server snapshot), and an
  // eager write would overwrite the saved choice with the default before it could be restored.
  const restored = useRef(false);
  useEffect(() => {
    if (!compact || restored.current) return;
    restored.current = true;
    const stored = readStored();
    setActive(stored);
    if (stored === 'today') return;
    programmatic.current = true;
    requestAnimationFrame(() => {
      panelRefs.current[stored]?.scrollIntoView({ block: 'nearest', inline: 'start', behavior: 'auto' });
      window.setTimeout(() => { programmatic.current = false; }, 150);
    });
  }, [compact]);

  useEffect(() => {
    if (!restored.current) return;
    try { sessionStorage.setItem(STORAGE_KEY, active); } catch { /* storage unavailable */ }
  }, [active]);

  // Swipes update the active tab. IntersectionObserver rather than scrollLeft maths, because
  // scrollLeft is negative in RTL.
  useEffect(() => {
    const rail = railRef.current;
    if (!compact || !rail || typeof IntersectionObserver === 'undefined') return;
    const io = new IntersectionObserver(
      (entries) => {
        if (programmatic.current) return;
        for (const e of entries) {
          if (e.isIntersecting && e.intersectionRatio >= 0.55) {
            setActive((e.target as HTMLElement).dataset.view as DashboardViewId);
          }
        }
      },
      { root: rail, threshold: [0.55] },
    );
    Object.values(panelRefs.current).forEach((el) => el && io.observe(el));
    return () => io.disconnect();
  }, [compact]);

  const go = useCallback((id: DashboardViewId, focusTab = false) => {
    setActive(id);
    const panel = panelRefs.current[id];
    const rail = railRef.current;
    if (!panel || !rail) return;
    programmatic.current = true;
    panel.scrollIntoView({ block: 'nearest', inline: 'start', behavior: reduceMotion ? 'auto' : 'smooth' });
    // If the reader had scrolled down a long view, bring the new view's top into sight.
    const top = rail.getBoundingClientRect().top;
    if (top < 0) window.scrollBy({ top: top - 120, behavior: reduceMotion ? 'auto' : 'smooth' });
    window.setTimeout(() => { programmatic.current = false; }, reduceMotion ? 50 : 420);
    if (focusTab) tabRefs.current[id]?.focus();
  }, [reduceMotion]);

  const onKey = (e: React.KeyboardEvent, index: number) => {
    const rtl = document.documentElement.dir === 'rtl';
    const fwd = rtl ? 'ArrowLeft' : 'ArrowRight';
    const back = rtl ? 'ArrowRight' : 'ArrowLeft';
    let next = index;
    if (e.key === fwd) next = (index + 1) % VIEWS.length;
    else if (e.key === back) next = (index - 1 + VIEWS.length) % VIEWS.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = VIEWS.length - 1;
    else return;
    e.preventDefault();
    go(VIEWS[next].id, true);
  };

  const panels: Record<DashboardViewId, React.ReactNode> = { today, actions, insights };

  return (
    <div className="min-w-0">
      {/* Segmented control — glass, because it floats over scrolling content. */}
      <div className="sticky top-[60px] z-10 -mx-4 mb-4 px-4 py-2 sm:-mx-6 sm:px-6 lg:hidden">
        <div
          role="tablist"
          aria-label={t('Dashboard views')}
          className="wg-glass mx-auto grid max-w-md grid-cols-3 gap-1 rounded-xl border p-1"
        >
          {VIEWS.map((v, i) => {
            const selected = active === v.id;
            const badge = badges?.[v.id];
            return (
              <button
                key={v.id}
                ref={(el) => { tabRefs.current[v.id] = el; }}
                id={`dash-tab-${v.id}`}
                type="button"
                role="tab"
                aria-selected={selected}
                aria-controls={`dash-panel-${v.id}`}
                tabIndex={selected ? 0 : -1}
                onClick={() => go(v.id)}
                onKeyDown={(e) => onKey(e, i)}
                className={`wg-press flex h-9 items-center justify-center gap-1.5 rounded-lg text-sm font-semibold ${
                  selected
                    ? 'bg-[color:var(--wg-surface)] text-sapphire shadow-sm dark:text-blue-300'
                    : 'text-slate-600 hover:text-slate-900 dark:text-slate-300 dark:hover:text-white'
                }`}
              >
                {t(v.label)}
                {badge ? (
                  <span className={`min-w-[1.25rem] rounded-full px-1.5 text-[11px] font-bold tabular-nums leading-5 ${selected ? 'bg-sapphire text-white dark:bg-blue-600' : 'bg-slate-200 text-slate-700 dark:bg-white/10 dark:text-slate-200'}`}>
                    {badge}
                  </span>
                ) : null}
              </button>
            );
          })}
        </div>
      </div>

      <div
        ref={railRef}
        className="wg-snap-x -mx-4 items-start sm:-mx-6 lg:mx-0 lg:block lg:overflow-visible"
      >
        {VIEWS.map((v) => {
          const selected = active === v.id;
          return (
            <section
              key={v.id}
              ref={(el) => { panelRefs.current[v.id] = el; }}
              data-view={v.id}
              id={`dash-panel-${v.id}`}
              role={compact ? 'tabpanel' : undefined}
              aria-labelledby={compact ? `dash-tab-${v.id}` : undefined}
              aria-label={compact ? undefined : t(v.label)}
              // Off-screen views stay in the DOM (swipe needs them painted) but out of the tab
              // order and the accessibility tree until selected.
              inert={compact && !selected ? true : undefined}
              className={`w-full min-w-0 shrink-0 px-4 sm:px-6 lg:mb-6 lg:px-0 lg:last:mb-0 ${
                compact && !selected ? 'max-h-[80svh] overflow-hidden' : ''
              }`}
            >
              {panels[v.id]}
            </section>
          );
        })}
      </div>
    </div>
  );
}
