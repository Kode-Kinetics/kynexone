'use client';

import type { ReactNode } from 'react';

/**
 * The one section card of the employee record: the same header size (`text-base font-semibold`), the same padding (`p-4`), the
 * section's own action (Edit section) at the inline end of the header. `bare` leaves the body to the caller.
 */
export function RecordSection({ title, description, actions, children, headingId, bare = false, className = '' }: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
  headingId?: string;
  bare?: boolean;
  className?: string;
}) {
  return (
    <section className={`surface overflow-hidden ${className}`} aria-labelledby={headingId}>
      <header className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2 border-b border-slate-200 px-4 py-3 dark:border-white/10">
        <div className="min-w-0">
          <h2 id={headingId} className="text-base font-semibold text-slate-950 dark:text-white">{title}</h2>
          {description && <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{description}</p>}
        </div>
        {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
      </header>
      {bare ? children : <div className="p-4">{children}</div>}
    </section>
  );
}

/** An info box inside a section: the heading is always `text-sm font-semibold`, never larger than the card's own title. */
export function RecordCallout({ title, icon, tone = 'blue', children }: { title: ReactNode; icon?: ReactNode; tone?: 'blue' | 'emerald' | 'amber'; children?: ReactNode }) {
  const palette = {
    blue: 'border-blue-200 bg-blue-50 dark:border-blue-500/30 dark:bg-blue-500/10',
    emerald: 'border-emerald-200 bg-emerald-50 dark:border-emerald-500/30 dark:bg-emerald-500/10',
    amber: 'border-amber-200 bg-amber-50 dark:border-amber-500/30 dark:bg-amber-500/10',
  }[tone];
  return (
    <div className={`rounded-lg border p-3 ${palette}`}>
      <div className="flex items-center gap-2">{icon}<h3 className="text-sm font-semibold text-slate-950 dark:text-white">{title}</h3></div>
      {children && <div className="mt-1 text-xs leading-5 text-slate-600 dark:text-slate-300">{children}</div>}
    </div>
  );
}
