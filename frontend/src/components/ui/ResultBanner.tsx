'use client';

import Link from 'next/link';
import { useEffect, useRef, type ReactNode } from 'react';
import { CheckCircle2, Clock, X } from 'lucide-react';
import { useT } from '../../hooks/useT';

/**
 * What the user lands on after a save (Screen Standard section 3d): exactly three parts, kept until dismissed.
 *   What changed    "3 changes saved: Grade, Housing, Probation end"
 *   Where you are   the page you stayed on or reached
 *   One next action a button or link: "Review who is affected"
 * A pending state is stated, not implied: "The salary change goes to Finance and applies on 1 Dec after approval."
 * It is a polite live region (role="status") and takes focus when it appears, so a screen-reader user hears the
 * outcome and a keyboard user is next to the action. Toasts are only for inline row edits that do not navigate.
 */
export interface ResultNext {
  label: string;
  href?: string;
  onClick?: () => void;
}

export function ResultBanner({ changed, where, pending, next, onDismiss, className = '' }: {
  changed: ReactNode;
  where?: ReactNode;
  pending?: ReactNode;
  next?: ResultNext;
  onDismiss?: () => void;
  className?: string;
}) {
  const t = useT();
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => { ref.current?.focus(); }, []);
  const nextClass = 'btn-secondary btn-sm';
  return (
    <div
      ref={ref}
      role="status"
      tabIndex={-1}
      data-result
      className={`flex items-start gap-3 rounded-lg border border-success/30 bg-success-soft px-4 py-3 text-body text-fg outline-none focus-visible:ring-2 focus-visible:ring-primary ${className}`.trim()}
    >
      <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-success-fg" aria-hidden />
      <div className="min-w-0 flex-1 space-y-1">
        <p className="font-semibold" data-result-changed>{changed}</p>
        {where && <p className="text-fg-muted" data-result-where>{where}</p>}
        {pending && (
          <p className="flex items-start gap-1.5 text-fg-muted" data-result-pending>
            <Clock className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
            <span>{pending}</span>
          </p>
        )}
        {next && (
          <div className="pt-1" data-result-next>
            {next.href
              ? <Link href={next.href} className={nextClass} onClick={next.onClick}>{next.label}</Link>
              : <button type="button" className={nextClass} onClick={next.onClick}>{next.label}</button>}
          </div>
        )}
      </div>
      {onDismiss && (
        <button type="button" onClick={onDismiss} aria-label={t('Dismiss')}
          className="grid h-control-sm w-control-sm shrink-0 place-items-center rounded-lg text-fg-muted transition-colors hover:bg-surface-sunken hover:text-fg">
          <X className="h-4 w-4" aria-hidden />
        </button>
      )}
    </div>
  );
}
