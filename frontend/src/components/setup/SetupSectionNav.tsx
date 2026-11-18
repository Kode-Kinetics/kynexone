'use client';

import type { ComponentProps, RefObject } from 'react';
import { ChevronDown } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { SectionNav, type SectionNavGroup, type SectionNavItem } from '../ui/SectionNav';

export type SetupSectionNavItem<Id extends string> = SectionNavItem<Id>;
export type SetupSectionNavGroup<Id extends string> = SectionNavGroup<Id>;

/** The list's element id: the "All sections" toggle in the section header controls it. */
export const SETUP_SECTION_LIST_ID = 'setup-section-list';
/** The selected section's h2. The skip link moves focus here; the heading carries tabIndex={-1}. */
export const SETUP_SECTION_HEADING_ID = 'setup-section-heading';

/**
 * The single navigation of Setup & Administration (see ui/SectionNav): Setup's names, ids and styling hooks.
 */
export function SetupSectionNav<Id extends string>(props: Omit<ComponentProps<typeof SectionNav<Id>>, 'ariaLabel' | 'sheetTitle' | 'listId' | 'headingId' | 'classPrefix' | 'stickyClass'>) {
  const t = useT();
  return <SectionNav {...props} ariaLabel={t('Setup sections')} sheetTitle={t('Setup sections')} listId={SETUP_SECTION_LIST_ID} headingId={SETUP_SECTION_HEADING_ID} classPrefix="setup" stickyClass="xl:sticky xl:top-[4.75rem]" />;
}

/**
 * "All sections ⌄", at the end of the section header row while the list is folded (below 1280px, or at every width
 * for a focused task). "Setup section" stays in its accessible name for assistive tech.
 */
export function SetupSectionsToggle({ open, onToggle, toggleRef }: {
  open: boolean;
  onToggle: () => void;
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
      className={`setup-sections-toggle btn-secondary h-9 shrink-0 xl:hidden`}
    >
      <span className="sr-only">{t('Setup section')}</span>
      {t('All sections')}
      <ChevronDown aria-hidden="true" className={`h-4 w-4 shrink-0 transition-transform motion-reduce:transition-none ${open ? 'rotate-180' : ''}`} />
    </button>
  );
}
