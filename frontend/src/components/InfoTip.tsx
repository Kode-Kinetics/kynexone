'use client';

import { Info } from 'lucide-react';
import { useHelpTexts } from '../contexts/HelpTextContext';

/**
 * Small "i" info icon with a hover/focus tooltip, used next to form field
 * labels to explain what the field is for and what input is expected.
 *
 * `text` is the built-in default. When a `fieldKey` is given and the tenant
 * admin has saved a custom help text for that key (Tenant Admin → Help Text),
 * the custom text replaces the default — letting each company describe the
 * field per its own policies.
 *
 * Usage: <label>Joining date <InfoTip fieldKey="employees.joining_date" text="First working day…" /></label>
 */
export function InfoTip({ text, fieldKey, className = '' }: { text: string; fieldKey?: string; className?: string }) {
  const { overrides } = useHelpTexts();
  const shown = (fieldKey && overrides[fieldKey]) || text;

  return (
    <span className={`group relative inline-flex align-middle ${className}`}>
      <button
        type="button"
        tabIndex={0}
        aria-label={`Field info: ${shown}`}
        className="inline-flex cursor-help items-center justify-center rounded-full text-slate-400 hover:text-sapphire focus:text-sapphire focus:outline-none dark:text-slate-500 dark:hover:text-cyanAccent"
        onClick={e => e.preventDefault()}
      >
        <Info className="h-3.5 w-3.5" />
      </button>
      <span
        role="tooltip"
        className="pointer-events-none invisible absolute bottom-full left-1/2 z-50 mb-1.5 w-56 -translate-x-1/2 rounded-lg bg-slate-900 px-3 py-2 text-start text-xs font-normal normal-case leading-snug tracking-normal text-white opacity-0 shadow-lg transition-opacity duration-150 group-hover:visible group-hover:opacity-100 group-focus-within:visible group-focus-within:opacity-100 dark:bg-slate-800 dark:ring-1 dark:ring-white/10"
      >
        {shown}
        {fieldKey && (
          <span className="mt-1 block font-mono text-[10px] text-slate-400">key: {fieldKey}</span>
        )}
        <span className="absolute left-1/2 top-full -translate-x-1/2 border-4 border-transparent border-t-slate-900 dark:border-t-slate-800" />
      </span>
    </span>
  );
}
