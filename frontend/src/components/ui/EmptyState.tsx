'use client';

import Link from 'next/link';
import type { ElementType, ReactNode } from 'react';
import { useT } from '../../hooks/useT';

/**
 * Nothing to show (Screen Standard 3c). Three kinds, three causes, and a next step for each:
 *
 *   first-use   nothing exists yet. Title says what it is ("No grades yet"), the description why it is empty, the
 *               action is the same verb as the page header ("Add the first grade").
 *   filtered    filters hide everything. The description names the active filters; the action clears them
 *               (label defaults to "Clear filters").
 *   restricted  the viewer may not see this. No action: the description names who can grant access.
 *
 * Exactly one action for first-use and filtered; it is a button (`onClick`) or a link (`href`). A failed load is not
 * empty (LoadFailedRow); a hand-rolled border-dashed block is a defect. `data-empty-state` is the hook the screen
 * contracts use.
 */
export interface EmptyAction {
  label?: string;
  onClick?: () => void;
  href?: string;
}

type Common = {
  icon?: ElementType<{ className?: string; 'aria-hidden'?: boolean }>;
  title: string;
  description?: ReactNode;
  className?: string;
};
export type EmptyStateProps =
  | (Common & { kind: 'first-use'; action: EmptyAction })
  | (Common & { kind: 'filtered'; action: EmptyAction })
  | (Common & { kind: 'restricted'; action?: undefined });

export function EmptyState(props: EmptyStateProps) {
  const t = useT();
  const { kind, icon: Icon, title, description, className = '' } = props;
  const action = props.kind === 'restricted' ? undefined : props.action;
  if (kind !== 'restricted' && !(action && (action.onClick || action.href))) {
    throw new Error(`EmptyState kind="${kind}" needs one action (onClick or href): an empty state always says what to do next.`);
  }
  const label = action ? action.label ?? (kind === 'filtered' ? t('Clear filters') : undefined) : undefined;
  if (action && !label) throw new Error('EmptyState first-use needs one action with a label, the same verb as the page header.');
  const body = description ?? (kind === 'restricted' ? t('Access is given by your HR administrator.') : undefined);
  const actionClass = kind === 'first-use' ? 'btn-primary' : 'btn-secondary';
  return (
    <div data-empty-state={kind} className={`flex flex-col items-center gap-2 px-6 py-10 text-center ${className}`.trim()}>
      {Icon && <Icon className="mb-1 h-5 w-5 text-fg-subtle" aria-hidden />}
      <p className="text-body font-semibold text-fg">{title}</p>
      {body && <p className="max-w-[65ch] text-body-sm text-fg-muted">{body}</p>}
      {action && label && (
        <div className="mt-2">
          {action.href
            ? <Link href={action.href} className={actionClass} onClick={action.onClick}>{label}</Link>
            : <button type="button" className={actionClass} onClick={action.onClick}>{label}</button>}
        </div>
      )}
    </div>
  );
}
