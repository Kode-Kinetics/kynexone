'use client';

import { useEffect, useRef, type ReactNode } from 'react';
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

interface ReturnOrigin {
  destinationId: string;
  scrollY: number;
}

export function SetupStudioWorkspace({ companies, renderSettings, onOpenLibrary, onOpenLegacyDraft, onOpenAdministration, confirmLeave, onDirtyChange }: {
  companies: CompanyDto[];
  renderSettings: (id: string, onDirtyChange: (dirty: boolean) => void) => ReactNode;
  onOpenLibrary: () => void;
  onOpenLegacyDraft: () => void;
  onOpenAdministration: () => void;
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
  const canManagePolicies = hasPermission('organization.write') && (hasRole('Admin') || hasRole('HR Manager') || hasRole('HR Officer'));

  const navigate = (areaId: SetupAreaId | 'overview', setting?: string, afterAccepted?: () => void) => {
    if (areaId === activeArea && (setting ?? null) === params?.get('setting')) {
      afterAccepted?.();
      return;
    }
    const next = new URLSearchParams(params?.toString());
    next.set('tab', 'aiSetup');
    next.set('area', areaId);
    next.delete('mode');
    if (authorizedCompany) next.set('companyId', authorizedCompany); else next.delete('companyId');
    if (setting) next.set('setting', setting); else next.delete('setting');
    const href = `/setup?${next.toString()}`;
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
    if (!focusSelectedArea.current || selectedDestination) return;
    focusSelectedArea.current = false;
    const frame = window.requestAnimationFrame(() => {
      areaHeading.current?.scrollIntoView({ block: 'start' });
      areaHeading.current?.focus({ preventScroll: true });
    });
    return () => window.cancelAnimationFrame(frame);
  }, [activeArea, selectedDestination]);

  useEffect(() => {
    if (!restoreOrigin.current || selectedDestination) return;
    restoreOrigin.current = false;
    const origin = returnOrigin.current;
    const frame = window.requestAnimationFrame(() => {
      if (origin) {
        window.scrollTo({ top: origin.scrollY, behavior: 'auto' });
        document.querySelector<HTMLElement>(`[data-setup-destination="${CSS.escape(origin.destinationId)}"]`)?.focus({ preventScroll: true });
      } else {
        areaHeading.current?.focus({ preventScroll: true });
      }
    });
    return () => window.cancelAnimationFrame(frame);
  }, [selectedDestination]);

  const openDestination = (destination: SetupDestination) => {
    returnOrigin.current = { destinationId: destination.id, scrollY: window.scrollY };
    navigate(destination.areaId, destination.id);
  };
  const backToArea = () => navigate(activeArea, undefined, () => { restoreOrigin.current = true; });
  const moveTo = (destination: SetupDestination) => navigate(destination.areaId, destination.id);
  const chooseArea = (areaId: SetupAreaId | 'overview', afterAccepted: () => void) => {
    navigate(areaId, undefined, () => {
      focusSelectedArea.current = true;
      afterAccepted();
      if (areaId === activeArea && !selectedDestination) {
        window.requestAnimationFrame(() => areaHeading.current?.focus());
      }
    });
  };

  return (
    <div data-testid="setup-studio-workspace" className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-4 border-b border-slate-200 pb-4 dark:border-white/10">
        <div className="min-w-0">
          <p className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{t('Selected scope')}</p>
          <p className="mt-1 font-semibold text-slate-950 dark:text-white">{companyName ?? t(isGroupScope ? 'All authorized companies' : 'No company selected')}</p>
          <p className="mt-1 max-w-2xl text-xs leading-5 text-slate-600 dark:text-slate-400">{t('Area navigation does not save changes. Starter drafts are stored in this browser; settings use each form’s save action.')}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          {canManagePolicies && <button type="button" onClick={onOpenLibrary} className="btn-secondary text-sm">{t('Policy library')}</button>}
          <button type="button" onClick={onOpenLegacyDraft} className="btn-secondary text-sm">{t('Starter configuration draft')}</button>
          <button type="button" onClick={onOpenAdministration} className="btn-secondary text-sm">{t('Administration settings')}</button>
        </div>
      </div>

      {selectedDestination && area ? (
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
      ) : (
        <section data-testid="setup-area-content" className="min-w-0 rounded-xl border border-slate-200 bg-white px-4 py-5 dark:border-white/10 dark:bg-slate-900 sm:px-6" aria-labelledby="setup-area-title">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div className="min-w-0 flex-1">
              <h2 ref={areaHeading} tabIndex={-1} id="setup-area-title" className="scroll-mt-4 text-xl font-semibold tracking-tight text-slate-950 outline-none dark:text-white">{t(area?.label ?? 'Workspace overview')}</h2>
              <p className="mt-2 text-sm leading-6 text-slate-600 dark:text-slate-300">{t(area?.description ?? 'Choose an area to review settings, templates and related operations. You can start anywhere.')}</p>
            </div>
            <SetupAreaNavigator activeArea={activeArea} onSelect={chooseArea} />
          </div>
          {!area ? (
            <>
              <p className="mt-4 rounded-lg bg-slate-50 p-3 text-sm leading-6 text-slate-600 dark:bg-white/5 dark:text-slate-300">{t('Configuration status is not assessed here. Open an area to review its saved settings.')}</p>
              <ul className="mt-3 divide-y divide-slate-200 dark:divide-white/10">
                {SETUP_AREAS.map(item => (
                  <li key={item.id}>
                    <button type="button" onClick={() => navigate(item.id, undefined, () => { focusSelectedArea.current = true; })} className="flex w-full items-center justify-between gap-3 py-3 text-start text-sm hover:text-sapphire dark:hover:text-cyanAccent">
                      <span className="font-medium text-slate-900 dark:text-white">{t(item.label)}</span>
                      <ChevronRight aria-hidden="true" className="h-4 w-4 shrink-0 rtl:rotate-180" />
                    </button>
                  </li>
                ))}
              </ul>
            </>
          ) : (
            <>
              <p className="mt-4 text-sm text-slate-600 dark:text-slate-300">{t(area.id === 'onboarding'
                ? 'Onboarding task suggestions can be prepared in the starter policy guide, then reviewed in the existing onboarding form. Templates are tenant-wide; nothing is saved automatically.'
                : 'Document filling is not connected for this area. Edit its existing settings manually; the starter guide does not apply changes only to this area.')}</p>
              {selectedItem && selectedItem.availability !== 'available' && (
                <p role="status" className="mt-4 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-100">
                  {t(selectedItem.availability === 'restricted' ? 'Your role does not have access' : selectedItem.availability === 'unavailable' ? 'Not enabled for this tenant' : selectedItem.availability === 'checking' ? 'Checking availability…' : 'Availability could not be checked')}
                </p>
              )}
              <SetupAreaBlueprint area={area} destinations={destinations} onOpenDestination={openDestination} />
            </>
          )}
        </section>
      )}
    </div>
  );
}
