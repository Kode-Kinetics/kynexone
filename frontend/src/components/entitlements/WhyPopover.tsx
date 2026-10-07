'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { HelpCircle } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';

/**
 * "Why this value?" — a labelled button that opens a short explanation of where a package value comes from (the grade
 * cell, the frozen contract row, the salary row). Text button, not an icon-only control; opens on click or keyboard,
 * closes on Escape, on a click outside, or on the button again. Logical positioning (start/end) so it mirrors in RTL,
 * and capped to the viewport width so it never causes a horizontal scroll on a phone.
 */
export function WhyPopover({ lines, title }: { lines: string[]; title: string }) {
  const { t } = useLocale();
  const [open, setOpen] = useState(false);
  const id = useId();
  const root = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    const onClick = (e: MouseEvent) => { if (root.current && !root.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onClick);
    return () => { document.removeEventListener('keydown', onKey); document.removeEventListener('mousedown', onClick); };
  }, [open]);

  if (lines.length === 0) return null;
  return (
    <span ref={root} className="relative inline-flex">
      <button
        type="button"
        aria-expanded={open}
        aria-controls={id}
        onClick={() => setOpen((v) => !v)}
        className="inline-flex shrink-0 items-center gap-1 whitespace-nowrap rounded-md px-1.5 py-0.5 text-xs font-medium text-sapphire hover:bg-sapphire/10 focus:outline-none focus-visible:ring-2 focus-visible:ring-sapphire dark:text-cyanAccent dark:hover:bg-cyanAccent/10"
      >
        <HelpCircle className="h-3.5 w-3.5" aria-hidden="true" />
        {t('Why this value?')}
      </button>
      {open && (
        <span
          id={id}
          role="note"
          aria-label={title}
          className="absolute end-0 top-full z-40 mt-1 block w-72 max-w-[calc(100vw-2rem)] rounded-xl border border-slate-200 bg-white p-3 text-start text-xs leading-relaxed text-slate-700 shadow-lg dark:border-white/10 dark:bg-slate-900 dark:text-slate-200"
        >
          <span className="mb-1 block font-semibold text-slate-900 dark:text-slate-50">{title}</span>
          {lines.map((line, i) => <span key={i} className="block">{line}</span>)}
        </span>
      )}
    </span>
  );
}
