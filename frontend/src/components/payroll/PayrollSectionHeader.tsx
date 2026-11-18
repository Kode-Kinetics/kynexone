'use client';

import type { ReactNode } from 'react';
import { ChevronRight } from 'lucide-react';
import { useT } from '../../hooks/useT';

/** The section heading the Payroll nav's skip link moves focus to. */
export const PAYROLL_SECTION_HEADING_ID = 'payroll-section-heading';

/**
 * The header of one Payroll section (Screen Standard 1): the crumb "Payroll › {group}", the section's name as the page's h1, one purpose
 * line, and at the inline-end at most a secondary control (Bank / WPS's "More"). No primary here: a section that has one keeps it where its
 * work is (a list's toolbar, a flow's sticky footer).
 */
export function PayrollSectionHeader({ group, title, purpose, actions }: {
  /** Already translated. */
  group: string;
  title: string;
  purpose: string;
  actions?: ReactNode;
}) {
  const t = useT();
  return (
    <header data-page-header className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
      <div className="min-w-0">
        <nav aria-label={t('Breadcrumb')} className="mb-1">
          <ol className="flex flex-wrap items-center gap-1 text-meta text-fg-muted">
            <li>{t('Payroll')}</li>
            <li aria-hidden="true"><ChevronRight className="h-3 w-3" /></li>
            <li>{group}</li>
          </ol>
        </nav>
        <h1 id={PAYROLL_SECTION_HEADING_ID} tabIndex={-1} className="text-h1 text-fg outline-none">{title}</h1>
        <p data-purpose className="mt-1 max-w-[70ch] text-body text-fg-muted">{purpose}</p>
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2 sm:shrink-0">{actions}</div>}
    </header>
  );
}
