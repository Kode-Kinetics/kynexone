'use client';

import { useEffect, useRef } from 'react';

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
    ? 'kx-tabs-pill'
    : 'kx-tabs-underline';

  return (
    <div className="overflow-x-auto pb-1" data-roving-tabs={idPrefix}>
      <div className={`flex w-max min-w-full items-center gap-1 ${shell}`} role="tablist" aria-label={label}>
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
