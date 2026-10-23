'use client';

import { ArrowUpRight, CheckCircle2, ChevronRight } from 'lucide-react';
import { useT } from '../../hooks/useT';
import type { SetupDestination } from '../../lib/setupAreas';
import type { SetupBlueprintDestination } from './SetupAreaBlueprint';
import { NEXT_ACTION_LABEL, type AreaProgressItem } from './setupProgress';

/**
 * "In this area". It starts with the area's first measurable item that is not done yet, named as its first step
 * ("Add your first grade") with the rail's one primary button; then the area's other destinations as shortcuts. When
 * every measurable item is done it says so. An area with nothing measurable offers the shortcuts only and claims nothing.
 */
export function SetupNextActionsRail({ destinations, selectedDestination, onOpenDestination, progress = [] }: {
  destinations: readonly SetupBlueprintDestination[];
  selectedDestination?: SetupDestination;
  onOpenDestination: (destination: SetupDestination) => void;
  /** The area's measurable items, in destination order (setupProgress.areaProgressItems). */
  progress?: readonly AreaProgressItem[];
}) {
  const t = useT();
  const available = destinations
    .filter(item => item.availability === 'available')
    .sort((a, b) => a.destination.groupOrder - b.destination.groupOrder || a.destination.destinationOrder - b.destination.destinationOrder);
  // The next step is never the setting already on screen: the user is doing it.
  const incomplete = progress.filter(item => !item.done && item.destinationId !== selectedDestination?.id);
  const next = incomplete.length > 0 ? available.find(item => item.destination.id === incomplete[0].destinationId) : undefined;
  const allDone = progress.length > 0 && progress.every(item => item.done);
  const selectedIndex = selectedDestination ? available.findIndex(item => item.destination.id === selectedDestination.id) : -1;
  const actions = (selectedIndex >= 0
    ? [...available.slice(selectedIndex + 1), ...available.slice(0, selectedIndex + 1)]
    : available).filter(item => item.destination.id !== next?.destination.id).slice(0, 3);
  const doneIds = new Set(progress.filter(item => item.done).map(item => item.destinationId));
  const nextLabel = next ? NEXT_ACTION_LABEL[next.destination.id] : undefined;

  return (
    <aside aria-label={t('In this area')} className="surface sticky top-4 max-h-[calc(100vh-7rem)] overflow-y-auto p-4">
      <h2 className="text-sm font-semibold text-slate-950 dark:text-white">{t('In this area')}</h2>
      {next ? (
        <div className="mt-3 rounded-lg border border-sapphire/25 bg-sapphire/[0.05] p-3 dark:border-cyanAccent/25 dark:bg-cyanAccent/[0.06]" data-testid="setup-next-action">
          <p className="text-xs font-medium text-slate-600 dark:text-slate-300">{t('Next step')}</p>
          <p className="mt-0.5 text-sm font-semibold text-slate-950 dark:text-white">{t(next.destination.label)}</p>
          <p className="mt-0.5 text-xs leading-5 text-slate-600 dark:text-slate-400">{t('Not set up yet.')}</p>
          <button type="button" onClick={() => onOpenDestination(next.destination)} className="btn-primary mt-3 h-auto min-h-9 w-full justify-center py-2 text-center leading-5">
            {nextLabel ? t(nextLabel) : t('Open {label}', { label: t(next.destination.label) })}
          </button>
        </div>
      ) : allDone ? (
        <p className="mt-3 flex items-start gap-2 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm font-medium text-emerald-900 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-200" data-testid="setup-area-all-done">
          <CheckCircle2 aria-hidden="true" className="mt-0.5 h-4 w-4 shrink-0" />
          {t('Everything here is set up')}
        </p>
      ) : null}
      <p className="mt-3 border-s border-sapphire bg-sapphire/[0.06] px-3 py-2 text-xs leading-5 text-slate-600 dark:border-cyanAccent dark:bg-cyanAccent/[0.07] dark:text-slate-300">
        {t('Manual configuration only. Each setting uses its existing form and nothing is saved automatically.')}
      </p>
      <div className="mt-3 divide-y divide-slate-200 dark:divide-white/10">
        {actions.map(item => (
          <button key={item.destination.id} type="button" onClick={() => onOpenDestination(item.destination)} className="flex w-full items-start gap-2 py-3 text-start text-sm hover:text-sapphire focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire dark:hover:text-cyanAccent">
            <span className="min-w-0 flex-1">
              <span className="block font-semibold text-slate-950 dark:text-white">{t(item.destination.label)}</span>
              <span className="mt-1 block text-xs leading-5 text-slate-500 dark:text-slate-400">
                {doneIds.has(item.destination.id)
                  ? t(item.destination.presentation === 'embedded' ? 'Manual setting · Done' : 'Related workspace · Done')
                  : t(item.destination.presentation === 'embedded' ? 'Manual setting' : 'Related workspace')}
              </span>
            </span>
            {item.destination.presentation === 'route'
              ? <ArrowUpRight aria-hidden="true" className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />
              : <ChevronRight aria-hidden="true" className="h-4 w-4 shrink-0 text-sapphire rtl:rotate-180 dark:text-cyanAccent" />}
          </button>
        ))}
        {actions.length === 0 && !next && (
          <p className="py-3 text-xs leading-5 text-slate-500 dark:text-slate-400">{t('No available actions for your current access.')}</p>
        )}
      </div>
    </aside>
  );
}
