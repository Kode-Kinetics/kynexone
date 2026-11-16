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
  /**
   * Opt in: many tabs shown as a few labelled groups (the ids of a group are consecutive in `items`). From `md` up the groups wrap
   * onto rows so no tab is hidden off-screen; below it the strip scrolls and the labels scroll with it. The tablist stays one
   * list of tabs: arrow keys, Home and End move through all of them in order.
   */
  groups?: { label: string; ids: T[] }[];
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
  groups,
}: RovingTabListProps<T>) {
  const refs = useRef<Array<HTMLButtonElement | null>>([]);
  const activeIndex = items.findIndex((item) => item.id === activeId);

  // Keep the selected tab inside the strip by scrolling the STRIP only. scrollIntoView would also move the page to show the strip,
  // which jumped the whole screen on every tab change (and on first load of a deep link).
  const scrollerRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const strip = scrollerRef.current;
    const tab = refs.current[activeIndex];
    if (!strip || !tab) return;
    const s = strip.getBoundingClientRect();
    const r = tab.getBoundingClientRect();
    if (r.left < s.left) strip.scrollLeft -= s.left - r.left;
    else if (r.right > s.right) strip.scrollLeft += r.right - s.right;
  }, [activeId, activeIndex]);

  // Scroll cue: when the strip is wider than the screen, a chevron at each clipped edge says there is more.
  // Distances are measured from the inline start, so the same arithmetic holds in right-to-left.
  const [cue, setCue] = useState({ start: false, end: false, overflow: false });
  const tablist = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = scrollerRef.current;
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
    // In a right-to-left page the next tab is on the left: ArrowLeft goes forward, ArrowRight back.
    const rtl = getComputedStyle(event.currentTarget).direction === 'rtl';
    const forward = rtl ? 'ArrowLeft' : 'ArrowRight';
    const back = rtl ? 'ArrowRight' : 'ArrowLeft';
    if (event.key === forward || event.key === 'ArrowDown') next = (index + 1) % items.length;
    else if (event.key === back || event.key === 'ArrowUp') next = (index - 1 + items.length) % items.length;
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

  const renderTab = (item: RovingTabItem<T>) => {
    const index = items.indexOf(item);
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
  };

  const grouped = groups?.map((g) => ({ label: g.label, tabs: g.ids.flatMap((id) => items.filter((i) => i.id === id)) })).filter((g) => g.tabs.length > 0);
  const strip = (
    <div ref={scrollerRef} className={`overflow-x-auto pb-1 ${grouped ? 'md:overflow-visible md:pb-0' : ''}`} data-roving-tabs={idPrefix}>
      <div ref={tablist} className={`flex w-max min-w-full items-center gap-1 ${shell} ${grouped ? 'md:w-auto md:flex-wrap md:gap-x-4 md:gap-y-1' : ''}`} role="tablist" aria-label={label}>
        {grouped
          ? grouped.map((g) => (
            <div key={g.label} role="presentation" data-tab-group={g.label} className="flex shrink-0 items-center gap-1">
              <span aria-hidden="true" className="me-1 shrink-0 whitespace-nowrap text-caption font-semibold text-fg-subtle">{g.label}</span>
              {g.tabs.map(renderTab)}
            </div>
          ))
          : items.map(renderTab)}
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
