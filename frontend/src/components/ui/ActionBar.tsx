'use client';

import type { ReactNode } from 'react';
import { ChevronLeft } from 'lucide-react';

/**
 * The footer of a step (Screen Standard 1, Form / Wizard): "Back" at the inline-start, one line saying what comes next, and the ONE primary
 * at the inline-end. Sticky at the bottom of its panel, so the action is on screen without scrolling; on a phone it is fixed above the app's
 * bottom bar with the primary full width. A disabled primary keeps its label; the Next slot then says why (`nextId` for aria-describedby).
 */
export function ActionBar({ back, next, nextId, nextTestId, primary }: {
  back?: { label: string; onClick: () => void } | null;
  next?: ReactNode;
  nextId?: string;
  nextTestId?: string;
  primary?: ReactNode;
}) {
  // The inline-end padding keeps the primary clear of the floating assistant button (bottom, inline-end) at every width.
  return (
    <div
      data-action-bar=""
      className="z-20 flex flex-col gap-2 border-t border-line bg-surface px-4 py-3
        max-sm:fixed max-sm:inset-x-0 max-sm:bottom-[calc(4rem+env(safe-area-inset-bottom))] max-sm:pe-[4.75rem]
        sm:sticky sm:bottom-[calc(4rem+env(safe-area-inset-bottom))] sm:flex-row sm:items-center sm:gap-4 sm:rounded-b-xl lg:bottom-0 lg:pe-16"
    >
      {back && (
        <button type="button" className="btn-ghost btn-sm hidden shrink-0 sm:inline-flex" onClick={back.onClick}>
          <ChevronLeft className="h-4 w-4" aria-hidden="true" />{back.label}
        </button>
      )}
      {next && <p id={nextId} data-testid={nextTestId} className="min-w-0 text-body-sm text-fg-muted sm:ms-auto sm:text-end">{next}</p>}
      {primary && <div className="flex shrink-0 max-sm:w-full max-sm:[&>*]:w-full max-sm:[&_button]:min-h-11 max-sm:[&_button]:w-full max-sm:[&_button]:justify-center">{primary}</div>}
    </div>
  );
}
