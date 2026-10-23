'use client';

import { ArrowUpRight, ChevronRight } from 'lucide-react';
import { useT } from '../../hooks/useT';
import type { SetupDestination } from '../../lib/setupAreas';
import type { SetupBlueprintDestination } from './SetupAreaBlueprint';

/**
 * "In this area": the area's available destinations, starting after the one on screen. It is a shortcut list, not a
 * recommendation: readiness-driven next actions are not assessed here, so it offers no "continue" button.
 */
export function SetupNextActionsRail({ destinations, selectedDestination, onOpenDestination }: {
  destinations: readonly SetupBlueprintDestination[];
  selectedDestination?: SetupDestination;
  onOpenDestination: (destination: SetupDestination) => void;
}) {
  const t = useT();
  const available = destinations
    .filter(item => item.availability === 'available')
    .sort((a, b) => a.destination.groupOrder - b.destination.groupOrder || a.destination.destinationOrder - b.destination.destinationOrder);
  const selectedIndex = selectedDestination ? available.findIndex(item => item.destination.id === selectedDestination.id) : -1;
  const actions = (selectedIndex >= 0
    ? [...available.slice(selectedIndex + 1), ...available.slice(0, selectedIndex + 1)]
    : available).slice(0, 3);

  return (
    <aside aria-label={t('In this area')} className="surface sticky top-4 max-h-[calc(100vh-7rem)] overflow-y-auto p-4">
      <h2 className="text-sm font-semibold text-slate-950 dark:text-white">{t('In this area')}</h2>
      <p className="mt-3 border-s border-sapphire bg-sapphire/[0.06] px-3 py-2 text-xs leading-5 text-slate-600 dark:border-cyanAccent dark:bg-cyanAccent/[0.07] dark:text-slate-300">
        {t('Manual configuration only. Each setting uses its existing form and nothing is saved automatically.')}
      </p>
      <div className="mt-3 divide-y divide-slate-200 dark:divide-white/10">
        {actions.map(item => (
          <button key={item.destination.id} type="button" onClick={() => onOpenDestination(item.destination)} className="flex w-full items-start gap-2 py-3 text-start text-sm hover:text-sapphire focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire dark:hover:text-cyanAccent">
            <span className="min-w-0 flex-1">
              <span className="block font-semibold text-slate-950 dark:text-white">{t(item.destination.label)}</span>
              <span className="mt-1 block text-xs leading-5 text-slate-500 dark:text-slate-400">
                {t(item.destination.presentation === 'embedded' ? 'Manual setting' : 'Related workspace')}
              </span>
            </span>
            {item.destination.presentation === 'route'
              ? <ArrowUpRight aria-hidden="true" className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />
              : <ChevronRight aria-hidden="true" className="h-4 w-4 shrink-0 text-sapphire rtl:rotate-180 dark:text-cyanAccent" />}
          </button>
        ))}
        {actions.length === 0 && (
          <p className="py-3 text-xs leading-5 text-slate-500 dark:text-slate-400">{t('No available actions for your current access.')}</p>
        )}
      </div>
    </aside>
  );
}
