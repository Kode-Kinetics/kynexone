'use client';

import { useT } from '../../hooks/useT';

/** The short note shown in place of a form the server cannot save yet ("coming soon" / "not available"). */
export function CapabilityNote({ title, children }: { title: string; children?: string }) {
  const t = useT();
  return (
    <p role="note" data-testid="capability-note" className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-300">
      <span className="font-semibold">{t(title)}</span>{children ? <> {t(children)}</> : null}
    </p>
  );
}
