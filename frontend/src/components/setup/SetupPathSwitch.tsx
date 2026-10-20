'use client';

import { useT } from '../../hooks/useT';
import type { SetupStudioPath } from '../../lib/setupPathState';

export function SetupPathSwitch({ active, onSelect }: { active: SetupStudioPath; onSelect: (path: SetupStudioPath) => void }) {
  const t = useT();
  const options: readonly { id: SetupStudioPath; label: string }[] = [
    { id: 'guided', label: 'Guided Setup' },
    { id: 'configuration', label: 'Configuration Center' },
  ];

  return (
    <nav aria-label={t('Setup paths')} className="flex w-fit max-w-full gap-1 rounded-lg border border-slate-200 bg-white p-1 shadow-sm dark:border-white/10 dark:bg-[#0d1225]">
      {options.map(option => (
        <button
          key={option.id}
          type="button"
          aria-pressed={active === option.id}
          onClick={() => onSelect(option.id)}
          className={`rounded-md px-3 py-1.5 text-sm font-semibold transition-colors ${
            active === option.id
              ? 'bg-sapphire text-white dark:bg-cyanAccent dark:text-slate-950'
              : 'text-slate-600 hover:bg-slate-100 hover:text-slate-950 dark:text-slate-300 dark:hover:bg-white/[0.08] dark:hover:text-white'
          }`}
        >
          {t(option.label)}
        </button>
      ))}
    </nav>
  );
}
