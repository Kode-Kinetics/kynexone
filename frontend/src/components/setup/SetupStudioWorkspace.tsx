'use client';

import { useEffect, useRef, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react';
import { ChevronRight } from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import type { CompanyDto } from '../../api/organization';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import type { SetupNavigationGuard } from '../../hooks/useSetupNavigationGuard';
import { useT } from '../../hooks/useT';
import { SETUP_AREAS, destinationAvailability, resolveSetupArea, type SetupAreaId, type SetupDestination } from '../../lib/setupAreas';
import { SetupAreaBlueprint, type SetupBlueprintDestination } from './SetupAreaBlueprint';
import { SetupAreaNavigator } from './SetupAreaNavigator';
import { SetupFocusedWorkspace } from './SetupFocusedWorkspace';
import { SetupNextActionsRail } from './SetupNextActionsRail';
import { SETUP_SECTION_HEADING_ID } from './SetupSectionNav';

interface ReturnOrigin {
  destinationId: string;
  scrollY: number;
}

/**
 * All setup areas: the twelve business areas, each area's settings and related workspaces, and an embedded setting
 * opened in place. The page's section header names it; this component starts at the selected scope.
 */
export function SetupStudioWorkspace({ companies, renderSettings, confirmLeave, onDirtyChange }: {
  companies: CompanyDto[];
  renderSettings: (id: string, onDirtyChange: (dirty: boolean) => void) => ReactNode;
  confirmLeave: SetupNavigationGuard;
  onDirtyChange: (dirty: boolean) => void;
}) {
  const t = useT();
  const router = useRouter();
  const params = useSearchParams();
  const { hasPermission, hasRole } = useAuth();
  const { companies: permittedCompanies, selectedCompanyId, accountType, isGroupScope, companyVersion } = useCompany();
  const flags = useFeatureFlags();
  const areaHeading = useRef<HTMLHeadingElement>(null);
  const returnOrigin = useRef<ReturnOrigin | null>(null);
  const restoreOrigin = useRef(false);
  const focusSelectedArea = useRef(false);
  const previousSelectedDestination = useRef<string | null>(null);

  const authorizedCompany = selectedCompanyId && permittedCompanies.some(company => company.id === selectedCompanyId) ? selectedCompanyId : null;
  const companyName = companies.find(company => company.id === authorizedCompany)?.legalNameEn
    ?? permittedCompanies.find(company => company.id === authorizedCompany)?.name;
  const activeArea = resolveSetupArea(params?.get('area') ?? null);
  const area = SETUP_AREAS.find(item => item.id === activeArea);
  const access = {
    hasPermission,
    hasRole,
    isFeatureEnabled: flags.isFeatureEnabled,
    verdictForPath: flags.verdictForPath,
    flagStatus: flags.status,
    isCompanyScopedGroup: accountType === 'Group' && !isGroupScope,
  };
  const areaHref = (areaId: SetupAreaId | 'overview', setting?: string) => {
    const next = new URLSearchParams(params?.toString());
    next.set('tab', 'aiSetup');
    next.set('area', areaId);
    next.delete('mode');
    if (authorizedCompany) next.set('companyId', authorizedCompany); else next.delete('companyId');
    if (setting) next.set('setting', setting); else next.delete('setting');
    return `/setup?${next.toString()}`;
  };

  const navigate = (areaId: SetupAreaId | 'overview', setting?: string, afterAccepted?: () => void) => {
    if (areaId === activeArea && (setting ?? null) === params?.get('setting')) {
      afterAccepted?.();
      return;
    }
    const href = areaHref(areaId, setting);
    confirmLeave(href, () => {
      onDirtyChange(false);
      afterAccepted?.();
      router.push(href, { scroll: false });
    });
  };

  const destinationHref = (destination: SetupDestination) => {
    const [pathname, query] = destination.path.split('?');
    const target = new URLSearchParams(query);
    if (authorizedCompany) target.set('companyId', authorizedCompany);
    target.set('setupArea', destination.areaId);
    return `${pathname}${target.size ? `?${target.toString()}` : ''}`;
  };

  const scopeLabel = (destination: SetupDestination) => {
    if (destination.effectiveTargetScope === 'tenant') return 'Applies to all companies';
    if (destination.effectiveTargetScope === 'group') return accountType === 'Group' ? 'Group company register' : 'Company register';
    if (destination.effectiveTargetScope === 'company') {
      return companyName ? t('Applies to {company}', { company: companyName }) : 'Requires a selected company';
    }
    return 'Scope depends on the selected record';
  };

  const destinations: SetupBlueprintDestination[] = area?.destinations.map(destination => ({
    destination,
    availability: destinationAvailability(destination, access),
    href: destination.presentation === 'route' ? destinationHref(destination) : undefined,
    scopeLabel: scopeLabel(destination),
  })) ?? [];

  const requestedSetting = params?.get('setting');
  const selectedItem = destinations.find(item => item.destination.id === requestedSetting && item.destination.presentation === 'embedded');
  const selectedDestination = selectedItem?.availability === 'available' ? selectedItem.destination : undefined;
  const orderedEmbedded = destinations
    .filter(item => item.availability === 'available' && item.destination.presentation === 'embedded')
    .sort((a, b) => a.destination.groupOrder - b.destination.groupOrder || a.destination.destinationOrder - b.destination.destinationOrder)
    .map(item => item.destination);
  const selectedIndex = selectedDestination ? orderedEmbedded.findIndex(destination => destination.id === selectedDestination.id) : -1;
  const previousDestination = selectedIndex > 0 ? orderedEmbedded[selectedIndex - 1] : undefined;
  const nextDestination = selectedIndex >= 0 && selectedIndex < orderedEmbedded.length - 1 ? orderedEmbedded[selectedIndex + 1] : undefined;

  useEffect(() => {
    const currentDestination = selectedDestination?.id ?? null;
    const previousDestination = previousSelectedDestination.current;
    previousSelectedDestination.current = currentDestination;

    if (currentDestination && previousDestination && currentDestination !== previousDestination && returnOrigin.current) {
      returnOrigin.current = { ...returnOrigin.current, destinationId: currentDestination };
    }
    if (!currentDestination && previousDestination && !focusSelectedArea.current) restoreOrigin.current = true;
    if (!restoreOrigin.current || currentDestination) return;
    restoreOrigin.current = false;
    const origin = returnOrigin.current;
    const frame = window.requestAnimationFrame(() => {
      if (origin) {
        window.scrollTo({ top: origin.scrollY, behavior: 'auto' });
        document.querySelector<HTMLElement>(`[data-setup-destination="${CSS.escape(origin.destinationId)}"]`)?.focus({ preventScroll: true });
      } else {
        (areaHeading.current ?? document.getElementById(SETUP_SECTION_HEADING_ID))?.focus({ preventScroll: true });
      }
    });
    return () => window.cancelAnimationFrame(frame);
  }, [selectedDestination?.id]);

  useEffect(() => {
    if (!focusSelectedArea.current || selectedDestination) return;
    focusSelectedArea.current = false;
    const frame = window.requestAnimationFrame(() => {
      // The overview has no area heading of its own: the page's section header ("All setup areas") names it.
      const heading = areaHeading.current ?? document.getElementById(SETUP_SECTION_HEADING_ID);
      heading?.scrollIntoView({ block: 'start' });
      heading?.focus({ preventScroll: true });
    });
    return () => window.cancelAnimationFrame(frame);
  }, [activeArea, selectedDestination]);

  const openDestination = (destination: SetupDestination) => {
    returnOrigin.current = { destinationId: destination.id, scrollY: window.scrollY };
    navigate(destination.areaId, destination.id);
  };
  const activateDestination = (destination: SetupDestination) => {
    if (destination.presentation === 'embedded') {
      openDestination(destination);
      return;
    }
    const href = destinationHref(destination);
    confirmLeave(href, () => {
      onDirtyChange(false);
      router.push(href, { scroll: false });
    });
  };
  const backToArea = () => navigate(activeArea, undefined, () => { restoreOrigin.current = true; });
  const moveTo = (destination: SetupDestination) => {
    if (returnOrigin.current) returnOrigin.current = { ...returnOrigin.current, destinationId: destination.id };
    navigate(destination.areaId, destination.id);
  };
  const chooseArea = (areaId: SetupAreaId | 'overview', afterAccepted: () => void) => {
    navigate(areaId, undefined, () => {
      focusSelectedArea.current = true;
      afterAccepted();
      if (areaId === activeArea && !selectedDestination) {
        window.requestAnimationFrame(() => areaHeading.current?.focus());
      }
    });
  };

  // A real link (Cmd/Ctrl-click opens a new tab); a plain click goes through the unsaved-changes guard.
  const guardedLink = (areaId: SetupAreaId | 'overview', afterAccepted?: () => void) => (event: ReactMouseEvent<HTMLAnchorElement>) => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    navigate(areaId, undefined, afterAccepted);
  };
  const crumbLink = 'rounded font-medium text-sapphire underline-offset-2 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sapphire dark:text-cyanAccent';
  const breadcrumb = area && (
    <nav aria-label={t('Breadcrumb')} className="min-w-0 text-sm">
      <ol className="flex flex-wrap items-center gap-x-2 gap-y-1">
        <li><a href={areaHref('overview')} onClick={guardedLink('overview', () => { focusSelectedArea.current = true; })} className={crumbLink}>{t('All setup areas')}</a></li>
        <li aria-hidden="true" className="text-slate-400 dark:text-slate-500">/</li>
        <li className="min-w-0">
          {selectedDestination
            ? <a href={areaHref(area.id)} onClick={event => { if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return; event.preventDefault(); backToArea(); }} className={crumbLink}>{t(area.label)}</a>
            : <span aria-current="page" className="font-medium text-slate-700 dark:text-slate-200">{t(area.label)}</span>}
        </li>
      </ol>
    </nav>
  );

  return (
    <div data-testid="setup-studio-workspace" className="space-y-4">
      <div className="min-w-0 border-b border-slate-200 pb-4 dark:border-white/10">
        <p className="text-xs font-semibold uppercase tracking-wide text-slate-500 rtl:normal-case rtl:tracking-normal rtl:text-[13px] dark:text-slate-400">{t('Selected scope')}</p>
        <p className="mt-1 font-semibold text-slate-950 dark:text-white">{companyName ?? t(isGroupScope ? 'All authorized companies' : 'No company selected')}</p>
        <p className="mt-1 max-w-2xl text-xs leading-5 text-slate-600 dark:text-slate-400">{t('Area navigation does not save changes. Starter drafts are stored in this browser; settings use each form’s save action.')}</p>
      </div>

      {/* The page's Setup sections column is the left-hand navigation. Areas are chosen here: from the overview list,
          or on an area page from the breadcrumb or "Browse all areas". The overview has no "In this area" rail. */}
      <div className={`grid min-w-0 gap-4 ${area ? 'xl:grid-cols-[minmax(0,1fr)_16rem]' : ''}`}>
        <div className="min-w-0">
          {selectedDestination && area ? (
            <div className="space-y-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                {breadcrumb}
                <SetupAreaNavigator activeArea={activeArea} onSelect={chooseArea} />
              </div>
              <SetupFocusedWorkspace
                destination={selectedDestination}
                areaLabel={area.label}
                scopeLabel={scopeLabel(selectedDestination)}
                onBack={backToArea}
                previousDestination={previousDestination}
                nextDestination={nextDestination}
                onPrevious={previousDestination ? () => moveTo(previousDestination) : undefined}
                onNext={nextDestination ? () => moveTo(nextDestination) : undefined}
              >
                <div key={`${selectedDestination.id}:${companyVersion}`}>
                  {renderSettings(selectedDestination.id, onDirtyChange)}
                </div>
              </SetupFocusedWorkspace>
            </div>
          ) : area ? (
            <section data-testid="setup-area-content" className="surface min-w-0 px-4 py-5 sm:px-6" aria-labelledby="setup-area-title">
              {breadcrumb}
              <div className="mt-3 flex flex-wrap items-start justify-between gap-4">
                <div className="min-w-0 flex-1">
                  <h2 ref={areaHeading} tabIndex={-1} id="setup-area-title" className="scroll-mt-4 text-xl font-semibold tracking-tight text-slate-950 outline-none dark:text-white">{t(area.label)}</h2>
                  <p className="mt-2 text-sm leading-6 text-slate-600 dark:text-slate-300">{t(area.description)}</p>
                </div>
                <SetupAreaNavigator activeArea={activeArea} onSelect={chooseArea} />
              </div>
              <p className="mt-4 text-sm text-slate-600 dark:text-slate-300">{t('Enter and maintain this area manually. Nothing is saved until you use the selected form’s Save action.')}</p>
              {selectedItem && selectedItem.availability !== 'available' && (
                <p role="status" className="mt-4 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-100">
                  {t(selectedItem.availability === 'restricted' ? 'Your role does not have access' : selectedItem.availability === 'unavailable' ? 'Not enabled for this tenant' : selectedItem.availability === 'checking' ? 'Checking availability…' : 'Availability could not be checked')}
                </p>
              )}
              <SetupAreaBlueprint area={area} destinations={destinations} onOpenDestination={openDestination} />
            </section>
          ) : (
            <section data-testid="setup-area-content" className="surface min-w-0 px-2 py-1 sm:px-3" aria-labelledby={SETUP_SECTION_HEADING_ID}>
              <ul className="divide-y divide-slate-200 dark:divide-white/10">
                {SETUP_AREAS.map(item => (
                  <li key={item.id}>
                    <a
                      href={areaHref(item.id)}
                      onClick={guardedLink(item.id, () => { focusSelectedArea.current = true; })}
                      className="group flex w-full items-center justify-between gap-3 rounded-lg px-2 py-3 text-start text-sm hover:bg-sapphire/[0.04] focus-visible:outline focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-sapphire dark:hover:bg-cyanAccent/[0.05] sm:px-3"
                    >
                      <span className="min-w-0 flex-1">
                        <span className="block font-medium text-slate-900 group-hover:text-sapphire dark:text-white dark:group-hover:text-cyanAccent">{t(item.label)}</span>
                        <span className="mt-0.5 block text-xs leading-5 text-slate-600 dark:text-slate-400">{t(item.description)}</span>
                      </span>
                      <ChevronRight aria-hidden="true" className="h-4 w-4 shrink-0 text-slate-400 rtl:rotate-180 dark:text-slate-500" />
                    </a>
                  </li>
                ))}
              </ul>
            </section>
          )}
        </div>

        {area && (
          <div className="hidden min-w-0 xl:block">
            <SetupNextActionsRail destinations={destinations} selectedDestination={selectedDestination} onOpenDestination={activateDestination} />
          </div>
        )}
      </div>
    </div>
  );
}
