'use client';

import { useEffect, useRef } from 'react';

export interface RovingTabItem<T extends string> {
  id: T;
  label: string;
  icon?: React.ElementType<{ className?: string }>;
}

interface RovingTabListProps<T extends string> {
  items: RovingTabItem<T>[];
  activeId: T;
  onChange: (id: T) => void;
  idPrefix: string;
  label: string;
  variant?: 'pill' | 'underline';
  compact?: boolean;
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
}: RovingTabListProps<T>) {
  const refs = useRef<Array<HTMLButtonElement | null>>([]);
  const activeIndex = items.findIndex((item) => item.id === activeId);

  useEffect(() => {
    refs.current[activeIndex]?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }, [activeId, activeIndex]);

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
    ? 'rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-white/10 dark:bg-white/[0.03]'
    : 'border-b border-slate-200 dark:border-white/[0.08]';

  return (
    <div className="overflow-x-auto pb-1" data-roving-tabs={idPrefix}>
      <div className={`flex w-max min-w-full items-center gap-1 ${shell}`} role="tablist" aria-label={label}>
        {items.map((item, index) => {
          const selected = item.id === activeId;
          const Icon = item.icon;
          const selectedClass = variant === 'pill'
            ? 'bg-white text-sapphire shadow-sm dark:bg-slate-800 dark:text-cyanAccent'
            : 'border-sapphire text-sapphire';
          const idleClass = variant === 'pill'
            ? 'text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'
            : 'border-transparent text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200';
          return (
            <button
              key={item.id}
              ref={(node) => { refs.current[index] = node; }}
              id={`${idPrefix}-tab-${item.id}`}
              type="button"
              role="tab"
              aria-selected={selected}
              aria-controls={`${idPrefix}-panel-${item.id}`}
              tabIndex={selected ? 0 : -1}
              onClick={() => onChange(item.id)}
              onKeyDown={(event) => move(index, event)}
              className={`flex shrink-0 items-center gap-1.5 whitespace-nowrap font-semibold transition ${
                variant === 'underline' ? 'border-b-2 px-4 py-2.5' : `rounded-lg px-3 py-1.5 ${compact ? 'text-xs' : 'text-sm'}`
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
      className={`outline-none focus-visible:ring-2 focus-visible:ring-sapphire/50 ${className}`}
    >
      {children}
    </div>
  );
}
