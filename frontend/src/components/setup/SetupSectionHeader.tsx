'use client';

import type { ElementType, ReactNode } from 'react';
import { SETUP_SECTION_HEADING_ID } from './SetupSectionNav';

/**
 * The selected section's header row: its name (the same label as its navigation entry), an optional one-line
 * description, and, while the section list is folded, the "All sections" toggle at the end of the row. The h2 is the
 * skip link's target, so it takes focus without joining the tab order.
 */
export function SetupSectionHeader({ icon: Icon, title, description, divider = false, action }: {
  icon?: ElementType;
  title: string;
  description?: ReactNode;
  /** A rule under the header, for a settings section whose content starts straight below it. */
  divider?: boolean;
  action?: ReactNode;
}) {
  return (
    <header className={`setup-section-header flex min-w-0 items-start gap-3 ${divider ? 'border-b border-slate-200 pb-3 dark:border-white/10' : ''}`}>
      {Icon && <Icon aria-hidden="true" className="mt-1 h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" />}
      <div className="min-w-0 flex-1">
        <h2 id={SETUP_SECTION_HEADING_ID} tabIndex={-1} className="scroll-mt-20 text-lg font-semibold text-slate-950 outline-none focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sapphire dark:text-white">{title}</h2>
        {description && <p className="mt-0.5 text-sm text-slate-600 dark:text-slate-300">{description}</p>}
      </div>
      {action}
    </header>
  );
}
