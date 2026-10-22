'use client';

import { useEffect, useId, useRef, useState, type ElementType } from 'react';
import { ChevronDown } from 'lucide-react';
import { useT } from '../../hooks/useT';

export interface SetupSectionNavItem<Id extends string> {
  id: Id;
  /** Already translated. Also the button's accessible name, which tests and assistive tech rely on. */
  label: string;
  icon: ElementType;
  /** The region this section's content renders into (referenced while the section is shown). */
  controls: string;
}

export interface SetupSectionNavGroup<Id extends string> {
  id: string;
  /** Already translated. */
  label: string;
  items: SetupSectionNavItem<Id>[];
}

/**
 * The single navigation of Setup & Administration. On wide screens it is a sticky, grouped column beside the
 * selected section, so every permitted section is visible at once. Below that it collapses into a section picker
 * that shows the current section and expands to the same grouped list.
 *
 * A focused task that brings its own step navigation (the Policy starter) passes `collapsed`, so the picker is used
 * at every width and the task keeps the full width instead of sitting beside two columns of navigation.
 *
 * The list stays mounted while the picker is closed: the unsaved-changes guard resumes a navigation by clicking
 * the original button again once the user chooses "Discard and leave", so that button must still be connected.
 */
export function SetupSectionNav<Id extends string>({ groups, selectedId, currentLabel, collapsed = false, onSelect }: {
  groups: SetupSectionNavGroup<Id>[];
  selectedId: Id;
  /** The section on screen, for the picker. A deep-linked section that has no entry of its own still has a name. */
  currentLabel: string;
  /** Use the picker at every width. */
  collapsed?: boolean;
  onSelect: (id: Id) => void;
}) {
  const t = useT();
  const listId = useId();
  const [open, setOpen] = useState(false);
  const toggle = useRef<HTMLButtonElement>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const selected = groups.flatMap(group => group.items.map(item => ({ item, group }))).find(entry => entry.item.id === selectedId);
  const SelectedIcon = selected?.item.icon;

  // The picker closes once the chosen section is actually shown. A guarded switch that the user cancels leaves
  // both the section and the open picker as they were.
  useEffect(() => { setOpen(false); }, [selectedId]);

  // A deep link to a section low in the column keeps its entry in view inside the column's own scroll area,
  // without moving the page itself.
  useEffect(() => {
    const container = scroller.current;
    const active = container?.querySelector<HTMLElement>('[aria-current="page"]');
    if (!container || !active || container.scrollHeight <= container.clientHeight) return;
    const top = active.offsetTop - container.offsetTop;
    if (top < container.scrollTop || top + active.offsetHeight > container.scrollTop + container.clientHeight) {
      container.scrollTop = Math.max(0, top - container.clientHeight / 2);
    }
  }, [selectedId]);

  return (
    <nav
      aria-label={t('Setup sections')}
      className={`setup-page-nav min-w-0 ${collapsed ? '' : 'xl:sticky xl:top-[4.75rem] xl:self-start'}`}
      onKeyDown={event => {
        if (event.key !== 'Escape' || !open) return;
        event.stopPropagation();
        setOpen(false);
        toggle.current?.focus();
      }}
    >
      <button
        ref={toggle}
        type="button"
        aria-expanded={open}
        aria-controls={listId}
        onClick={() => setOpen(value => !value)}
        className={`setup-section-picker flex min-h-12 w-full items-center gap-3 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-start shadow-sm transition-colors hover:border-sapphire/40 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sapphire dark:border-white/10 dark:bg-slate-900 dark:hover:border-cyanAccent/40 ${collapsed ? '' : 'xl:hidden'}`}
      >
        {SelectedIcon && <SelectedIcon aria-hidden="true" className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />}
        <span className="min-w-0 flex-1">
          <span className="setup-section-picker-caption block text-xs font-medium text-slate-500 dark:text-slate-400">{t('Setup section')}</span>
          <span className="block truncate text-sm font-semibold text-slate-950 dark:text-white">{selected?.item.label ?? currentLabel}</span>
        </span>
        <span className="shrink-0 text-xs font-semibold text-sapphire dark:text-cyanAccent">{open ? t('Close') : t('All sections')}</span>
        <ChevronDown aria-hidden="true" className={`h-4 w-4 shrink-0 text-slate-500 transition-transform motion-reduce:transition-none dark:text-slate-400 ${open ? 'rotate-180' : ''}`} />
      </button>

      <div
        id={listId}
        ref={scroller}
        className={`${open ? 'mt-2 grid' : 'hidden'} setup-section-list gap-x-4 gap-y-1 rounded-xl border border-slate-200 bg-white p-2 dark:border-white/10 dark:bg-slate-900 sm:grid-cols-2 ${collapsed ? 'lg:grid-cols-4' : 'xl:mt-0 xl:block xl:max-h-[calc(100vh-6rem)] xl:overflow-y-auto xl:overscroll-contain'}`}
      >
        {groups.map((group, index) => (
          <div key={group.id} className={`setup-section-group min-w-0 py-1 ${index > 0 && !collapsed ? 'xl:mt-1 xl:border-t xl:border-slate-100 xl:pt-1.5 xl:dark:border-white/[0.06]' : ''}`}>
            <p id={`${listId}-${group.id}`} className="px-3 pb-1 pt-1.5 text-[11px] leading-4 font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">{group.label}</p>
            <ul aria-labelledby={`${listId}-${group.id}`} className="space-y-0.5">
              {group.items.map(({ id, label, icon: Icon, controls }) => {
                const active = id === selectedId;
                return (
                  <li key={id}>
                    <button
                      type="button"
                      aria-current={active ? 'page' : undefined}
                      // Only the shown section's region exists in the document; the others render on selection.
                      aria-controls={active ? controls : undefined}
                      onClick={() => onSelect(id)}
                      className={`relative flex min-h-10 w-full items-center gap-2.5 rounded-lg px-3 py-2 text-start text-sm leading-5 transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-sapphire xl:min-h-8 xl:py-1 ${
                        active
                          ? 'bg-sapphire/10 font-semibold text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent'
                          : 'text-slate-700 hover:bg-slate-100 hover:text-slate-950 dark:text-slate-300 dark:hover:bg-white/[0.06] dark:hover:text-white'
                      }`}
                    >
                      {active && <span aria-hidden="true" className="absolute inset-y-1.5 start-0 w-[3px] rounded-full bg-sapphire dark:bg-cyanAccent" />}
                      <Icon aria-hidden="true" className={`h-4 w-4 shrink-0 ${active ? '' : 'text-slate-400 dark:text-slate-500'}`} />
                      <span className="min-w-0 flex-1">{label}</span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </div>
        ))}
      </div>
    </nav>
  );
}
