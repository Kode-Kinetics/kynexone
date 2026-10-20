'use client';

import { ChevronRight } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { SETUP_AREAS, type SetupAreaId } from '../../lib/setupAreas';

export function SetupAreaRail({ activeArea, onSelect }: {
  activeArea: SetupAreaId | 'overview';
  onSelect: (area: SetupAreaId) => void;
}) {
  const t = useT();

  return (
    <nav aria-label={t('Setup areas')} className="sticky top-4 max-h-[calc(100vh-7rem)] overflow-y-auto rounded-xl border border-slate-200 bg-white p-3 dark:border-white/10 dark:bg-slate-900">
      <p className="px-2 pb-3 text-sm font-semibold text-slate-950 dark:text-white">{t('Workspace overview')}</p>
      <ul className="space-y-1">
        {SETUP_AREAS.map(area => {
          const active = area.id === activeArea;
          return (
            <li key={area.id}>
              <button
                type="button"
                aria-label={t(area.label)}
                aria-current={active ? 'page' : undefined}
                onClick={() => onSelect(area.id)}
                className={`group flex min-h-11 w-full items-center justify-between gap-2 rounded-lg px-3 py-2.5 text-start text-sm leading-5 transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire ${
                  active
                    ? 'bg-sapphire/10 font-semibold text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent'
                    : 'text-slate-700 hover:bg-slate-100 hover:text-slate-950 dark:text-slate-300 dark:hover:bg-white/[0.06] dark:hover:text-white'
                }`}
              >
                <span>{t(area.label)}</span>
                <ChevronRight aria-hidden="true" className="h-4 w-4 shrink-0 rtl:rotate-180" />
              </button>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
