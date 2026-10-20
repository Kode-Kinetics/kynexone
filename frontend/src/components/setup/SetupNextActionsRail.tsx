'use client';

import { ArrowUpRight, ChevronRight } from 'lucide-react';
import { useT } from '../../hooks/useT';
import type { SetupAreaDefinition, SetupDestination } from '../../lib/setupAreas';
import type { SetupBlueprintDestination } from './SetupAreaBlueprint';

export function SetupNextActionsRail({ area, destinations, selectedDestination, onOpenDestination }: {
  area?: SetupAreaDefinition;
  destinations: readonly SetupBlueprintDestination[];
  selectedDestination?: SetupDestination;
  onOpenDestination: (destination: SetupDestination) => void;
}) {
  const t = useT();
  const available = destinations
    .filter(item => item.availability === 'available')
    .sort((a, b) => a.destination.groupOrder - b.destination.groupOrder || a.destination.destinationOrder - b.destination.destinationOrder);
  const selectedIndex = selectedDestination ? available.findIndex(item => item.destination.id === selectedDestination.id) : -1;
  const next = selectedIndex >= 0 ? available[selectedIndex + 1] ?? available[0] : available[0];
  const actions = (selectedIndex >= 0
    ? [...available.slice(selectedIndex + 1), ...available.slice(0, selectedIndex + 1)]
    : available).slice(0, 3);

  const actionContent = (item: SetupBlueprintDestination, compact = false) => (
    <>
      <span className="min-w-0 flex-1">
        <span className="block font-semibold text-slate-950 dark:text-white">{t(item.destination.label)}</span>
        {!compact && <span className="mt-1 block text-xs leading-5 text-slate-500 dark:text-slate-400">
          {t(item.destination.presentation === 'embedded' ? 'Manual setting' : 'Related workspace')}
        </span>}
      </span>
      {item.destination.presentation === 'route'
        ? <ArrowUpRight aria-hidden="true" className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />
        : <ChevronRight aria-hidden="true" className="h-4 w-4 shrink-0 text-sapphire rtl:rotate-180 dark:text-cyanAccent" />}
    </>
  );

  const action = (item: SetupBlueprintDestination, className: string, compact = false) => (
    <button key={item.destination.id} type="button" onClick={() => onOpenDestination(item.destination)} className={className}>
      {actionContent(item, compact)}
    </button>
  );

  return (
    <aside aria-label={t('Next actions')} className="sticky top-4 max-h-[calc(100vh-7rem)] overflow-y-auto rounded-xl border border-slate-200 bg-white p-4 dark:border-white/10 dark:bg-slate-900">
      <h2 className="text-sm font-semibold text-slate-950 dark:text-white">{t('Next actions')}</h2>
      <p className="mt-3 border-s border-sapphire bg-sapphire/[0.06] px-3 py-2 text-xs leading-5 text-slate-600 dark:border-cyanAccent dark:bg-cyanAccent/[0.07] dark:text-slate-300">
        {t('Manual configuration only. Each setting uses its existing form and nothing is saved automatically.')}
      </p>

      {area ? (
        <div className="mt-3 divide-y divide-slate-200 dark:divide-white/10">
          {actions.map(item => action(item, 'flex w-full items-start gap-2 py-3 text-start text-sm hover:text-sapphire focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire dark:hover:text-cyanAccent'))}
          {actions.length === 0 && (
            <p className="py-3 text-xs leading-5 text-slate-500 dark:text-slate-400">{t('No available actions for your current access.')}</p>
          )}
        </div>
      ) : (
        <p className="mt-3 text-xs leading-5 text-slate-500 dark:text-slate-400">{t('Choose a setup area to see its available actions.')}</p>
      )}

      {next && (
        <button
          type="button"
          aria-label={t('Continue next action')}
          onClick={() => onOpenDestination(next.destination)}
          className="mt-3 flex min-h-11 w-full items-center justify-between gap-2 rounded-lg bg-sapphire px-3 py-2.5 text-start text-sm font-semibold text-white shadow-sm hover:bg-sapphire/90 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sapphire dark:bg-cyanAccent dark:text-slate-950"
        >
          {t('Continue next action')}
          <ChevronRight aria-hidden="true" className="h-4 w-4 shrink-0 rtl:rotate-180" />
        </button>
      )}
    </aside>
  );
}
