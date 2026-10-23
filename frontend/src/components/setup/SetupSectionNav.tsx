'use client';

import { useEffect, useRef, useState, type ElementType, type MouseEvent as ReactMouseEvent, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, X } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { useMediaQuery } from '../../hooks/useMediaQuery';

export interface SetupSectionNavItem<Id extends string> {
  id: Id;
  /** Already translated. Also the link's accessible name, which tests and assistive tech rely on. */
  label: string;
  icon: ElementType;
  /** The section's own address, so a Cmd/Ctrl/middle click opens it in a new tab. */
  href: string;
}

export interface SetupSectionNavGroup<Id extends string> {
  id: string;
  /** Already translated. */
  label: string;
  items: SetupSectionNavItem<Id>[];
}

/** The list's element id: the "All sections" toggle in the section header controls it. */
export const SETUP_SECTION_LIST_ID = 'setup-section-list';
/** The selected section's h2. The skip link moves focus here; the heading carries tabIndex={-1}. */
export const SETUP_SECTION_HEADING_ID = 'setup-section-heading';

const SHEET_QUERY = '(max-width: 639.98px)';
const FOCUSABLE = 'a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** A plain primary click stays in the page (so the unsaved-changes guard can run); any modified click is the browser's. */
function isPlainPrimaryClick(event: ReactMouseEvent) {
  return event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;
}

/**
 * The single navigation of Setup & Administration. From 1280px it is a sticky, grouped column beside the selected
 * section, so every permitted section is visible at once. Below that the list folds away and the section header's
 * "All sections" button (SetupSectionsToggle) opens it: inline and capped from 640px, as a bottom sheet on a phone.
 *
 * A focused task that brings its own step navigation (the Policy starter) passes `collapsed`, so the list is folded at
 * every width and the task keeps the full width.
 *
 * Entries are real links. A plain click is handled in the page, which keeps the unsaved-changes guard in charge; a
 * modified click opens the section in a new tab. The list stays mounted while closed: the guard resumes a navigation
 * by clicking the original entry again once the user chooses "Discard and leave", so the entry must stay connected.
 */
export function SetupSectionNav<Id extends string>({ groups, selectedId, currentLabel, collapsed = false, open, onOpenChange, toggleRef, onSelect }: {
  groups: SetupSectionNavGroup<Id>[];
  /** The entry for the section on screen, or null when the section has no entry of its own. */
  selectedId: Id | null;
  /** The section on screen, for the skip link and the announcement. */
  currentLabel: string;
  /** Fold the list away at every width. */
  collapsed?: boolean;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** The "All sections" toggle, where focus returns when the folded list closes. */
  toggleRef: RefObject<HTMLButtonElement | null>;
  onSelect: (id: Id) => void;
}) {
  const t = useT();
  const list = useRef<HTMLDivElement>(null);
  const isPhone = useMediaQuery(SHEET_QUERY);
  const [announcement, setAnnouncement] = useState('');
  const previousSelected = useRef(selectedId);
  const previousLabel = useRef(currentLabel);
  // A bottom sheet only while folded (always folded on a phone, below the xl column breakpoint).
  const sheet = isPhone;
  const sheetOpen = sheet && open;

  // The list closes once the chosen section is actually shown. A guarded switch that the user cancels leaves both
  // the section and the open list as they were. Closing hides the list, so focus on the chosen entry moves to the
  // toggle first (only while the list is folded, not in the wide column), and the change is announced.
  useEffect(() => {
    if (previousSelected.current === selectedId && previousLabel.current === currentLabel) return;
    previousSelected.current = selectedId;
    previousLabel.current = currentLabel;
    const toggle = toggleRef.current;
    if (open && toggle && toggle.getClientRects().length > 0 && list.current?.contains(document.activeElement)) toggle.focus();
    onOpenChange(false);
    setAnnouncement(t('{label} shown', { label: currentLabel }));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- runs on a section change only
  }, [selectedId, currentLabel]);

  // The wide column is capped at the window height less the page title above it (10rem), so even before it sticks its
  // last entry is never under the window's bottom edge.
  // Keep the current entry in view inside the list's own scroll area: on a deep link low in the wide column (also once
  // the permitted entries arrive), and each time the folded list opens. The page itself does not move.
  const entryKey = groups.map(group => group.items.map(item => item.id).join(',')).join('|');
  useEffect(() => {
    const container = list.current;
    const active = container?.querySelector<HTMLElement>('[aria-current="page"]');
    if (!container || !active || container.scrollHeight <= container.clientHeight) return;
    const box = container.getBoundingClientRect();
    // Before the column sticks it starts below the page title, so part of it can sit under the window's bottom edge:
    // only the part inside the window counts as "in view".
    const visible = Math.max(0, Math.min(box.bottom, window.innerHeight) - Math.max(box.top, 0)) || container.clientHeight;
    const top = active.getBoundingClientRect().top - box.top + container.scrollTop;
    if (top < container.scrollTop || top + active.offsetHeight > container.scrollTop + visible) {
      container.scrollTop = Math.max(0, top - visible / 2);
    }
  }, [selectedId, open, entryKey]);

  // The sheet is a modal dialog: focus starts on the current entry (or the first one) and stays inside until it closes.
  useEffect(() => {
    if (!sheetOpen) return;
    const frame = window.requestAnimationFrame(() => {
      const container = list.current;
      // A plain focus scrolls the entry only just into view, under the sheet's sticky header; centre it instead.
      const entry = container?.querySelector<HTMLElement>('[aria-current="page"]') ?? container?.querySelector<HTMLElement>('a[href]');
      entry?.focus({ preventScroll: true });
      if (container && entry) {
        // Only the sheet's own list scrolls; the page behind the scrim stays where it was.
        const offset = entry.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop;
        container.scrollTop = Math.max(0, offset - (container.clientHeight - entry.offsetHeight) / 2);
      }
    });
    return () => window.cancelAnimationFrame(frame);
  }, [sheetOpen]);

  const close = () => {
    onOpenChange(false);
    toggleRef.current?.focus();
  };

  // Escape closes the open list from its entries or from the "All sections" button, which sits in the section
  // header outside the <nav>.
  const nav = useRef<HTMLElement>(null);
  useEffect(() => {
    if (!open) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || !(event.target instanceof Node)) return;
      const target = event.target;
      if (!(nav.current?.contains(target) || list.current?.contains(target) || toggleRef.current?.contains(target))) return;
      event.stopPropagation();
      onOpenChange(false);
      toggleRef.current?.focus();
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [open, onOpenChange, toggleRef]);

  const skipToSection = (event: ReactMouseEvent<HTMLAnchorElement>) => {
    event.preventDefault();
    const heading = document.getElementById(SETUP_SECTION_HEADING_ID);
    if (!heading) return;
    heading.scrollIntoView({ block: 'start' });
    heading.focus({ preventScroll: true });
  };

  const listClass = sheet
    ? `${open ? 'flex' : 'hidden'} setup-section-sheet fixed inset-x-0 bottom-0 z-40 max-h-[85dvh] flex-col overflow-y-auto overscroll-contain rounded-t-2xl border-t border-slate-200 bg-white pb-[max(0.75rem,env(safe-area-inset-bottom))] shadow-2xl dark:border-white/10 dark:bg-[#0d1225]`
    : `${open ? 'mt-2 grid' : 'hidden'} setup-section-list surface max-h-[60dvh] gap-x-4 gap-y-1 overflow-y-auto overscroll-contain p-2 sm:grid-cols-2 ${collapsed ? 'lg:grid-cols-3' : 'lg:grid-cols-3 xl:mt-0 xl:block xl:max-h-[calc(100vh-10rem)] xl:rounded-none xl:border-0 xl:border-e xl:border-slate-200 xl:bg-transparent xl:p-0 xl:pe-4 xl:shadow-none xl:dark:border-white/10 xl:dark:bg-transparent'}`;

  // The sheet is portalled to <body>: <main> keeps a stacking context from its entry animation, which held a fixed
  // sheet under the app's bottom bar and assistant button (z-30). It stays under dialogs (z-50), so the
  // unsaved-changes question asked by an entry is shown above it. React still bubbles its key events to the <nav> (Escape,
  // the focus trap), and the entries stay mounted for the unsaved-changes guard.
  const sheetLayer = (
    <>
      {sheetOpen && <div aria-hidden="true" data-testid="setup-section-scrim" className="fixed inset-0 z-[39] bg-slate-950/40" onClick={close} />}
      <div
        id={SETUP_SECTION_LIST_ID}
        ref={list}
        // A modal dialog only while it is open: a closed sheet must not count as an open modal (Modal restores focus
        // to its opener only when no other modal dialog is open).
        role={sheetOpen ? 'dialog' : undefined}
        aria-modal={sheetOpen ? true : undefined}
        aria-label={sheetOpen ? t('Setup sections') : undefined}
        className={listClass}
      >
        {sheet && (
          <div className="sticky top-0 z-10 flex items-center justify-between gap-3 border-b border-slate-200 bg-white px-4 py-2 dark:border-white/10 dark:bg-[#0d1225]">
            <p className="text-base font-semibold text-slate-950 dark:text-white">{t('Setup sections')}</p>
            <button type="button" onClick={close} className="btn-secondary h-11 min-w-11">
              <X aria-hidden="true" className="h-4 w-4" />
              {t('Close')}
            </button>
          </div>
        )}
        <div className={sheet ? 'px-2 pt-1' : 'contents'}>
          {groups.map((group, index) => (
            <div key={group.id} className={`setup-section-group min-w-0 py-1 ${collapsed ? '' : 'xl:py-0.5'} ${index > 0 && !collapsed ? 'xl:mt-1 xl:border-t xl:border-slate-200/70 xl:pt-1 xl:dark:border-white/[0.06]' : ''}`}>
              <p id={`${SETUP_SECTION_LIST_ID}-${group.id}`} className={`px-3 pb-1 pt-1.5 ${collapsed ? '' : 'xl:pb-0.5 xl:pt-1'} text-[11px] font-semibold uppercase leading-4 tracking-[0.08em] text-slate-500 rtl:normal-case rtl:text-[13px] rtl:tracking-normal dark:text-slate-400`}>{group.label}</p>
              <ul aria-labelledby={`${SETUP_SECTION_LIST_ID}-${group.id}`} className={`space-y-0.5 ${collapsed ? '' : 'xl:space-y-0'}`}>
                {group.items.map(({ id, label, icon: Icon, href }) => {
                  const active = id === selectedId;
                  return (
                    <li key={id}>
                      <a
                        href={href}
                        aria-current={active ? 'page' : undefined}
                        onClick={event => {
                          if (event.defaultPrevented || !isPlainPrimaryClick(event)) return;
                          event.preventDefault();
                          onSelect(id);
                        }}
                        className={`relative flex min-h-11 w-full items-center gap-2.5 rounded-lg px-3 py-2 text-start text-sm leading-5 focus-visible:outline focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-sapphire ${collapsed ? '' : 'xl:min-h-7 xl:py-1'} ${
                          active
                            ? 'bg-sapphire/10 font-semibold text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent'
                            : 'text-slate-700 hover:bg-slate-100 hover:text-slate-950 dark:text-slate-300 dark:hover:bg-white/[0.06] dark:hover:text-white'
                        }`}
                      >
                        {active && <span aria-hidden="true" className="absolute inset-y-1.5 start-0 w-[3px] rounded-full bg-sapphire dark:bg-cyanAccent" />}
                        <Icon aria-hidden="true" className={`h-4 w-4 shrink-0 ${active ? '' : 'text-slate-400 dark:text-slate-500'}`} />
                        <span className="min-w-0 flex-1">{label}</span>
                      </a>
                    </li>
                  );
                })}
              </ul>
            </div>
          ))}
        </div>
      </div>
    </>
  );

  return (
    <nav
      ref={nav}
      aria-label={t('Setup sections')}
      className={`setup-page-nav relative min-w-0 ${collapsed ? '' : 'xl:sticky xl:top-[4.75rem] xl:self-start'}`}
      onKeyDown={event => {
        if (event.key !== 'Tab' || !sheetOpen || !list.current) return;
        const focusable = Array.from(list.current.querySelectorAll<HTMLElement>(FOCUSABLE));
        if (focusable.length === 0) return;
        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
      }}
    >
      <a
        href={`#${SETUP_SECTION_HEADING_ID}`}
        onClick={skipToSection}
        className="sr-only focus:not-sr-only focus:absolute focus:start-0 focus:top-0 focus:z-[61] focus:rounded-lg focus:bg-white focus:px-3 focus:py-2 focus:text-sm focus:font-semibold focus:text-sapphire focus:shadow-lg focus:outline focus:outline-2 focus:outline-sapphire dark:focus:bg-slate-900 dark:focus:text-cyanAccent"
      >
        {t('Skip to {section}', { section: currentLabel })}
      </a>
      <p className="sr-only" aria-live="polite" aria-atomic="true" data-setup-section-announcement="">{announcement}</p>

      {sheet && typeof document !== 'undefined' ? createPortal(sheetLayer, document.body) : sheetLayer}
    </nav>
  );
}



/**
 * "All sections ⌄", at the end of the section header row while the list is folded (below 1280px, or at every width
 * for a focused task). "Setup section" stays in its accessible name for assistive tech.
 */
export function SetupSectionsToggle({ open, onToggle, collapsed = false, toggleRef }: {
  open: boolean;
  onToggle: () => void;
  collapsed?: boolean;
  toggleRef: RefObject<HTMLButtonElement | null>;
}) {
  const t = useT();
  return (
    <button
      ref={toggleRef}
      type="button"
      data-setup-sections-toggle=""
      aria-expanded={open}
      aria-controls={SETUP_SECTION_LIST_ID}
      onClick={onToggle}
      className={`setup-sections-toggle btn-secondary h-9 shrink-0 ${collapsed ? '' : 'xl:hidden'}`}
    >
      <span className="sr-only">{t('Setup section')}</span>
      {t('All sections')}
      <ChevronDown aria-hidden="true" className={`h-4 w-4 shrink-0 transition-transform motion-reduce:transition-none ${open ? 'rotate-180' : ''}`} />
    </button>
  );
}
