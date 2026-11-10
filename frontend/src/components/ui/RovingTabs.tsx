'use client';

import { useEffect, useRef, useState } from 'react';
import { ChevronRight } from 'lucide-react';

export interface RovingTabItem<T extends string> {
  id: T;
  label: string;
  icon?: React.ElementType<{ className?: string }>;
  /** Optional data-testid for the tab button. */
  testId?: string;
}

interface RovingTabListProps<T extends string> {
  items: RovingTabItem<T>[];
  activeId: T;
  onChange: (id: T) => void;
  idPrefix: string;
  label: string;
  variant?: 'pill' | 'underline';
  compact?: boolean;
  /** Opt in: a chevron at each clipped edge of a strip wider than its container. Off by default, so no screen changes unless it asks. */
  scrollCue?: boolean;
}

/**
 * Responsive, keyboard-operable tabs. Arrow keys move focus and selection,
 * Home/End jump to the first/last tab, and the selected tab is scrolled into
 * view on narrow screens.
 */
export function RovingTabList<T extends string>({
  items,
  activeId,
  onChange,
  idPrefix,
  label,
  variant = 'pill',
  compact = false,
  scrollCue = false,
}: RovingTabListProps<T>) {
  const refs = useRef<Array<HTMLButtonElement | null>>([]);
  const activeIndex = items.findIndex((item) => item.id === activeId);

  useEffect(() => {
    refs.current[activeIndex]?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }, [activeId, activeIndex]);

  // Scroll cue: when the strip is wider than the screen, a chevron at each clipped edge says there is more.
  // Distances are measured from the inline start, so the same arithmetic holds in right-to-left.
  const scroller = useRef<HTMLDivElement>(null);
  const [cue, setCue] = useState({ start: false, end: false, overflow: false });
  const tablist = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = scroller.current;
    if (!scrollCue || !el) return;
    const measure = () => {
      const max = el.scrollWidth - el.clientWidth;
      const pos = Math.abs(el.scrollLeft);
      setCue((prev) => {
        const next = { start: max > 1 && pos > 1, end: max > 1 && pos < max - 1, overflow: max > 1 };
        return prev.start === next.start && prev.end === next.end && prev.overflow === next.overflow ? prev : next;
      });
    };
    measure();
    el.addEventListener('scroll', measure, { passive: true });
    // Observe the container and the tab list itself: a language switch or a late font changes the tabs' widths, not the container's.
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    observer?.observe(el);
    if (tablist.current) observer?.observe(tablist.current);
    return () => { el.removeEventListener('scroll', measure); observer?.disconnect(); };
  }, [scrollCue, items.length]);

  const move = (index: number, event: React.KeyboardEvent<HTMLButtonElement>) => {
    let next = index;
    if (event.key === 'ArrowRight' || event.key === 'ArrowDown') next = (index + 1) % items.length;
    else if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') next = (index - 1 + items.length) % items.length;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = items.length - 1;
    else return;
    event.preventDefault();
    onChange(items[next].id);
    refs.current[next]?.focus();
  };

  const shell = variant === 'pill'
    ? 'kx-tabs-pill'
    : 'kx-tabs-underline';

  const strip = (
    <div ref={scroller} className="overflow-x-auto pb-1" data-roving-tabs={idPrefix}>
      <div ref={tablist} className={`flex w-max min-w-full items-center gap-1 ${shell}`} role="tablist" aria-label={label}>
        {items.map((item, index) => {
          const selected = item.id === activeId;
          const Icon = item.icon;
          const selectedClass = variant === 'pill'
            ? 'bg-surface text-primary-fg shadow-e1'
            : 'border-primary-fg text-primary-fg';
          const idleClass = variant === 'pill'
            ? 'text-fg-muted hover:text-fg'
            : 'border-transparent text-fg-muted hover:text-fg';
          return (
            <button
              key={item.id}
              ref={(node) => { refs.current[index] = node; }}
              id={`${idPrefix}-tab-${item.id}`}
              data-testid={item.testId}
              type="button"
              role="tab"
              aria-selected={selected}
              aria-controls={`${idPrefix}-panel-${item.id}`}
              tabIndex={selected ? 0 : -1}
              onClick={() => onChange(item.id)}
              onKeyDown={(event) => move(index, event)}
              className={`flex shrink-0 items-center gap-1.5 whitespace-nowrap font-semibold transition-colors ${
                variant === 'underline' ? 'border-b-2 px-4 py-2.5' : `rounded-lg px-3 py-1.5 ${compact ? 'text-meta' : 'text-body'}`
              } ${selected ? selectedClass : idleClass}`}
            >
              {Icon && <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />}
              {item.label}
            </button>
          );
        })}
      </div>
    </div>
  );
  if (!scrollCue) return strip;
  // When the strip scrolls, a gutter at each side holds the chevron so it never sits on top of a tab label.
  // The gutter stays for as long as the strip overflows, so the tabs do not jump as you scroll.
  return (
    <div className={`relative ${cue.overflow ? 'px-7' : ''}`}>
      {cue.start && <TabsCue edge="start" />}
      {cue.end && <TabsCue edge="end" />}
      {strip}
    </div>
  );
}

export function TabPanel({
  idPrefix,
  tabId,
  children,
  className = '',
}: {
  idPrefix: string;
  tabId: string;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <div
      id={`${idPrefix}-panel-${tabId}`}
      role="tabpanel"
      aria-labelledby={`${idPrefix}-tab-${tabId}`}
      tabIndex={0}
      className={`focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus ${className}`}
    >
      {children}
    </div>
  );
}

/** A non-interactive chevron on the clipped edge of a scrolling tab strip (it points the way the strip scrolls). */
function TabsCue({ edge }: { edge: 'start' | 'end' }) {
  const side = edge === 'end' ? 'end-0 rtl:rotate-180' : 'start-0 rotate-180 rtl:rotate-0';
  return (
    <span data-tabs-cue={edge} aria-hidden="true"
      className={`pointer-events-none absolute top-1/2 z-10 grid h-6 w-6 -translate-y-1/2 place-items-center rounded-full border border-line bg-surface text-fg-muted shadow-e1 ${side}`}>
      <ChevronRight className="h-4 w-4" />
    </span>
  );
}
