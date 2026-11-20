'use client';

import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';

/**
 * A genuinely wide strip (the hiring pipeline's stage columns) that scrolls sideways. It is a labelled, focusable region, so the keyboard
 * reaches the columns past the edge (arrow keys, Home, End), and it fades at whichever edge still has more, so the columns past the edge
 * are not invisible. The same cue the benefits grid uses. Phones snap to a column.
 */
export function HScrollRegion({ label, children, className = '', earlierLabel, laterLabel }: { label: string; children: ReactNode; className?: string; earlierLabel?: string; laterLabel?: string }) {
  const scroller = useRef<HTMLDivElement>(null);
  const [view, setView] = useState({ scrolls: false, atStart: true, atEnd: true });

  const measure = useCallback(() => {
    const el = scroller.current;
    if (!el) return;
    const max = el.scrollWidth - el.clientWidth;
    // scrollLeft is 0 at the start and negative going on in right-to-left: the distance travelled is its absolute value.
    const travelled = Math.abs(el.scrollLeft);
    const next = { scrolls: max > 1, atStart: travelled <= 1, atEnd: travelled >= max - 1 };
    setView(prev => (prev.scrolls === next.scrolls && prev.atStart === next.atStart && prev.atEnd === next.atEnd ? prev : next));
  }, []);

  // Re-measured after every render (the columns change with the data) and whenever the box is resized.
  useEffect(() => { measure(); });
  useEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    observer?.observe(el);
    window.addEventListener('resize', measure);
    return () => { observer?.disconnect(); window.removeEventListener('resize', measure); };
  }, [measure]);

  // Explicit controls for a wide strip on a pointer screen (scrollbars are hidden or overlaid on many systems): one step is most of a screen.
  const step = (direction: 1 | -1) => {
    const el = scroller.current;
    if (!el) return;
    const rtl = getComputedStyle(el).direction === 'rtl';
    el.scrollBy({ left: direction * (rtl ? -1 : 1) * el.clientWidth * 0.8, behavior: 'smooth' });
  };
  const controls = view.scrolls && earlierLabel && laterLabel;

  return (
    <div>
      {controls && (
        <div className="mb-2 hidden items-center justify-end gap-1 sm:flex" data-testid="scroll-controls">
          <button type="button" aria-label={earlierLabel} disabled={view.atStart} onClick={() => step(-1)}
            className="grid h-control-sm w-control-sm place-items-center rounded-lg border border-line text-fg-muted hover:bg-surface-sunken disabled:opacity-40">
            <ChevronLeft className="h-4 w-4 rtl:rotate-180" aria-hidden="true" />
          </button>
          <button type="button" aria-label={laterLabel} disabled={view.atEnd} onClick={() => step(1)}
            className="grid h-control-sm w-control-sm place-items-center rounded-lg border border-line text-fg-muted hover:bg-surface-sunken disabled:opacity-40">
            <ChevronRight className="h-4 w-4 rtl:rotate-180" aria-hidden="true" />
          </button>
        </div>
      )}
      <div className="relative">
      <div
        ref={scroller}
        role="region"
        tabIndex={0}
        aria-label={label}
        onScroll={measure}
        onKeyDown={e => {
          // Arrow keys scroll a sideways region natively; Home and End jump to its first and last column (the browser's own Home and End scroll up and down).
          if (e.target !== e.currentTarget || (e.key !== 'Home' && e.key !== 'End')) return;
          e.preventDefault();
          const el = e.currentTarget;
          const rtl = getComputedStyle(el).direction === 'rtl';
          const last = (el.scrollWidth - el.clientWidth) * (rtl ? -1 : 1);
          el.scrollTo({ left: e.key === 'End' ? last : 0 });
        }}
        className={`overflow-x-auto focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary max-sm:snap-x max-sm:snap-mandatory ${className}`}
      >
        {children}
      </div>
      {view.scrolls && !view.atStart && (
        <div data-testid="scroll-fade-start" aria-hidden="true"
          className="pointer-events-none absolute inset-y-0 start-0 w-10 bg-gradient-to-r from-canvas to-transparent rtl:bg-gradient-to-l" />
      )}
      {view.scrolls && !view.atEnd && (
        <div data-testid="scroll-fade-end" aria-hidden="true"
          className="pointer-events-none absolute inset-y-0 end-0 w-10 bg-gradient-to-l from-canvas to-transparent rtl:bg-gradient-to-r" />
      )}
      </div>
    </div>
  );
}
