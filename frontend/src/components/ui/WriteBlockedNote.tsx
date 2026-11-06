'use client';

import { useT } from '../../hooks/useT';

/**
 * Why a write control is switched off while a panel is not ready (Screen Standard rule 1: a disabled primary says
 * why in adjacent text, tied to the control with aria-describedby). It was a dead-looking button with no reason:
 * nothing said "still loading" or "this did not load". Renders nothing when the panel is ready.
 * Permission is a separate, existing line ("Read-only. You do not have permission ...").
 */
export function WriteBlockedNote({ id, ready, className = '' }: { id: string; ready: boolean; className?: string }) {
  const t = useT();
  if (ready) return null;
  return (
    <p id={id} data-write-blocked className={`text-meta text-fg-muted ${className}`.trim()}>
      {t('Changes are turned off until this has loaded. If it did not load, use Retry below.')}
    </p>
  );
}
