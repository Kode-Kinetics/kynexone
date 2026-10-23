'use client';

import Link from 'next/link';
import { ArrowUpRight, ChevronRight } from 'lucide-react';
import { useT } from '../../hooks/useT';
import type { SetupAreaDefinition, SetupDestination } from '../../lib/setupAreas';
import type { AreaProgressItem } from './setupProgress';

export type SetupDestinationAvailability = 'available' | 'restricted' | 'unavailable' | 'checking' | 'unknown';

export interface SetupBlueprintDestination {
  destination: SetupDestination;
  availability: SetupDestinationAvailability;
  href?: string;
  scopeLabel: string;
}

const availabilityLabels: Record<Exclude<SetupDestinationAvailability, 'available'>, string> = {
  restricted: 'Your role does not have access',
  unavailable: 'Not enabled for this tenant',
  checking: 'Checking availability…',
  unknown: 'Availability could not be checked',
};

export function SetupAreaBlueprint({ area, destinations, onOpenDestination, progress = [] }: {
  area: SetupAreaDefinition;
  destinations: readonly SetupBlueprintDestination[];
  onOpenDestination: (destination: SetupDestination) => void;
  /** The area's measurable items: each one's row says Done or Not started (the area's status drills down to these). */
  progress?: readonly AreaProgressItem[];
}) {
  const t = useT();
  const statusOf = (id: string) => progress.find(item => item.destinationId === id);
  const statusId = (id: string) => `setup-destination-status-${area.id}-${id}`;
  const groups = [...new Map(
    [...destinations]
      .sort((a, b) => a.destination.groupOrder - b.destination.groupOrder || a.destination.destinationOrder - b.destination.destinationOrder)
      .map(item => [item.destination.businessGroup.id, item.destination.businessGroup]),
  ).values()];

  const content = (item: SetupBlueprintDestination, trailing: React.ReactNode) => (
    <>
      <span className="min-w-0 flex-1">
        <span className="block font-semibold text-slate-950 dark:text-white">{t(item.destination.label)}</span>
        <span className="mt-1 block text-xs leading-5 text-slate-600 dark:text-slate-300">{t(item.scopeLabel)}</span>
        {item.availability !== 'available' && (
          <span className="mt-1 block text-sm leading-5 text-slate-600 dark:text-slate-300">{t(availabilityLabels[item.availability])}</span>
        )}
      </span>
      {(() => {
        const status = statusOf(item.destination.id);
        return status ? (
          <span id={statusId(item.destination.id)} data-testid="setup-destination-status" data-done={status.done ? 'true' : 'false'}
            className={`shrink-0 text-xs font-medium ${status.done ? 'text-emerald-700 dark:text-emerald-400' : 'text-slate-600 dark:text-slate-300'}`}>
            {status.done ? t('Done') : t('Not started')}
          </span>
        ) : null;
      })()}
      {trailing}
    </>
  );

  return (
    <div data-testid="setup-area-blueprint" className="mt-6 space-y-6">
      {groups.map(group => (
        <section key={group.id} aria-labelledby={`setup-group-${area.id}-${group.id}`}>
          <h3 id={`setup-group-${area.id}-${group.id}`} className="text-xs font-semibold uppercase tracking-wide text-slate-500 rtl:normal-case rtl:tracking-normal rtl:text-[13px] dark:text-slate-400">
            {t(group.label)}
          </h3>
          <ul className="surface mt-2 overflow-hidden">
            {destinations
              .filter(item => item.destination.businessGroup.id === group.id)
              .sort((a, b) => a.destination.destinationOrder - b.destination.destinationOrder)
              .map(item => {
                const destination = item.destination;
                const rowClass = 'flex w-full items-center gap-4 border-t border-slate-200 px-4 py-4 text-start first:border-t-0 focus-visible:relative focus-visible:z-10 focus-visible:outline focus-visible:outline-2 focus-visible:outline-sapphire dark:border-white/10';
                if (item.availability !== 'available') {
                  return <li key={destination.id}><div className={`${rowClass} cursor-not-allowed bg-slate-50/70 dark:bg-white/[0.03]`} aria-disabled="true">{content(item, null)}</div></li>;
                }
                if (destination.presentation === 'route') {
                  return (
                    <li key={destination.id}>
                      <Link data-setup-destination={destination.id} aria-label={t(destination.label)} aria-describedby={statusOf(destination.id) ? statusId(destination.id) : undefined} href={item.href!} className={`${rowClass} hover:bg-sapphire/[0.04] dark:hover:bg-cyanAccent/[0.05]`}>
                        {content(item, <ArrowUpRight aria-hidden="true" className="h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" />)}
                      </Link>
                    </li>
                  );
                }
                return (
                  <li key={destination.id}>
                    <button data-setup-destination={destination.id} aria-label={t(destination.label)} aria-describedby={statusOf(destination.id) ? statusId(destination.id) : undefined} type="button" onClick={() => onOpenDestination(destination)} className={`${rowClass} hover:bg-sapphire/[0.04] dark:hover:bg-cyanAccent/[0.05]`}>
                      {content(item, <ChevronRight aria-hidden="true" className="h-5 w-5 shrink-0 text-sapphire rtl:rotate-180 dark:text-cyanAccent" />)}
                    </button>
                  </li>
                );
              })}
          </ul>
        </section>
      ))}
    </div>
  );
}
