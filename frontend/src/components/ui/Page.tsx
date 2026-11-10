import type { ReactNode } from 'react';

/**
 * The header every screen opens with (Screen Standard section 1): the title, one purpose line under it, and at the
 * inline-end the screen's single primary action, with at most two secondary controls beside it. The hooks
 * `data-page-header`, `data-purpose` and `data-primary-action` are what the screen contracts (e2e/screen-contract) read,
 * so a screen built from this header passes them by construction. Logical layout only: in Arabic it mirrors on its own.
 *
 * `primary` is the one `btn-primary` of the view (list, record and dashboard screens keep it here; forms and Setup keep it
 * in the sticky footer instead). `actions` are the secondary controls (a settings link, an Export menu).
 */
export function PageHeader({ title, purpose, primary, actions, className = '' }: {
  title: ReactNode;
  /** One sentence, verb first: what the user does here and what it affects. 20-140 characters in English. */
  purpose: ReactNode;
  primary?: ReactNode;
  actions?: ReactNode;
  className?: string;
}) {
  return (
    <header data-page-header className={`flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between ${className}`.trim()}>
      <div className="min-w-0">
        <h1 className="text-h1 text-fg">{title}</h1>
        <p data-purpose className="mt-1 max-w-[70ch] text-body text-fg-muted">{purpose}</p>
      </div>
      {(actions || primary) && (
        <div className="flex flex-wrap items-center gap-2 sm:shrink-0">
          {actions}
          {primary && <div data-primary-action className="inline-flex">{primary}</div>}
        </div>
      )}
    </header>
  );
}
