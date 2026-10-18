'use client';

import { type ReactNode } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { ArrowUpRight, ChevronRight } from 'lucide-react';
import type { CompanyDto } from '../../api/organization';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { useFeatureFlags } from '../../contexts/FeatureFlagContext';
import { useT } from '../../hooks/useT';
import { SETUP_AREAS, destinationAvailability, resolveSetupArea, type SetupAreaId, type SetupDestination } from '../../lib/setupAreas';
import { SetupAreaNavigator } from './SetupAreaNavigator';
import type { SetupNavigationGuard } from '../../hooks/useSetupNavigationGuard';

const availabilityLabels = {
  available: 'Available to open', restricted: 'Your role does not have access',
  unavailable: 'Not enabled for this tenant', checking: 'Checking availability…', unknown: 'Availability could not be checked',
};
const scopeLabels = { company: 'Company scope', tenant: 'Tenant-wide controls', mixed: 'Scope varies by record' };

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
  const authorizedCompany = selectedCompanyId && permittedCompanies.some(company => company.id === selectedCompanyId) ? selectedCompanyId : null;
  const companyName = companies.find(company => company.id === authorizedCompany)?.legalNameEn
    ?? permittedCompanies.find(company => company.id === authorizedCompany)?.name;
  const activeArea = resolveSetupArea(params?.get('area') ?? null);
  const area = SETUP_AREAS.find(item => item.id === activeArea);
  const access = {
    hasPermission, hasRole, isFeatureEnabled: flags.isFeatureEnabled, verdictForPath: flags.verdictForPath,
    flagStatus: flags.status, isCompanyScopedGroup: accountType === 'Group' && !isGroupScope,
  };
  const canManagePolicies = hasPermission('organization.write') && (hasRole('Admin') || hasRole('HR Manager') || hasRole('HR Officer'));
  const navigate = (areaId: SetupAreaId | 'overview', setting?: string) => {
    if (areaId === activeArea && (setting ?? null) === params?.get('setting')) return;
    const next = new URLSearchParams(params?.toString());
    next.set('tab', 'aiSetup'); next.set('area', areaId); next.delete('mode');
    if (authorizedCompany) next.set('companyId', authorizedCompany); else next.delete('companyId');
    if (setting) next.set('setting', setting); else next.delete('setting');
    const href = `/setup?${next.toString()}`;
    confirmLeave(href, () => {
      onDirtyChange(false);
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
  const selectedDestination = area?.destinations.find(destination => destination.id === params?.get('setting') && destination.presentation === 'embedded');
  const selectedAvailable = selectedDestination && destinationAvailability(selectedDestination, access) === 'available';
  const editor = selectedAvailable ? renderSettings(selectedDestination.id, onDirtyChange) : null;
  const destinationRow = (destination: SetupDestination) => {
    const availability = destinationAvailability(destination, access);
    return (
      <li key={destination.id} className="flex flex-wrap items-center justify-between gap-3 border-t border-slate-200 py-4 first:border-t-0 dark:border-white/10">
        <div className="min-w-0 flex-1 basis-48">
          <h4 className="font-medium text-slate-900 dark:text-white">{t(destination.label)}</h4>
          <p className="mt-1 text-xs text-slate-600 dark:text-slate-400">{t(scopeLabels[destination.scope])}</p>
          {availability !== 'available' && <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{t(availabilityLabels[availability])}</p>}
        </div>
        {availability === 'available' && (destination.presentation === 'embedded'
          ? <button type="button" onClick={() => navigate(area!.id, destination.id)} className="btn-secondary text-sm">{t('Open settings')}<ChevronRight aria-hidden="true" className="ms-1 inline h-4 w-4 rtl:rotate-180" /></button>
          : <Link href={destinationHref(destination)} className="inline-flex items-center gap-1 rounded-lg px-3 py-2 text-sm font-medium text-sapphire hover:bg-sapphire/5 dark:text-cyanAccent">{t(destination.kind === 'operations' ? 'Open operations' : 'Open workspace')}<ArrowUpRight aria-hidden="true" className="h-4 w-4" /></Link>)}
      </li>
    );
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
      <div className="flex min-w-0 flex-col gap-5 lg:flex-row lg:items-start">
        <SetupAreaNavigator activeArea={activeArea} onSelect={id => navigate(id)} />
        <section data-testid="setup-area-content" className="min-w-0 flex-1 rounded-xl border border-slate-200 bg-white px-4 py-5 dark:border-white/10 dark:bg-slate-900 sm:px-6" aria-labelledby="setup-area-title">
          <h2 id="setup-area-title" className="text-xl font-semibold tracking-tight text-slate-950 dark:text-white">{t(area?.label ?? 'Workspace overview')}</h2>
          <p className="mt-2 text-sm leading-6 text-slate-600 dark:text-slate-300">{t(area?.description ?? 'Choose an area to review settings, templates and related operations. You can start anywhere.')}</p>
          {!area ? <>
            <p className="mt-4 rounded-lg bg-slate-50 p-3 text-sm leading-6 text-slate-600 dark:bg-white/5 dark:text-slate-300">{t('Configuration status is not assessed here. Open an area to review its saved settings.')}</p>
            <ul className="mt-3 divide-y divide-slate-200 dark:divide-white/10">
              {SETUP_AREAS.map(item => <li key={item.id}><button type="button" onClick={() => navigate(item.id)} className="flex w-full items-center justify-between gap-3 py-3 text-start text-sm hover:text-sapphire dark:hover:text-cyanAccent"><span className="font-medium text-slate-900 dark:text-white">{t(item.label)}</span><ChevronRight aria-hidden="true" className="h-4 w-4 shrink-0 rtl:rotate-180" /></button></li>)}
            </ul>
          </> : <>
            <p className="mt-4 text-sm text-slate-600 dark:text-slate-300">{t(area.id === 'onboarding'
              ? 'Onboarding task suggestions can be prepared in the starter policy guide, then reviewed in the existing onboarding form. Templates are tenant-wide; nothing is saved automatically.'
              : 'Document filling is not connected for this area. Edit its existing settings manually; the starter guide does not apply changes only to this area.')}</p>
            {selectedAvailable && <div className="mt-5 border-t border-slate-200 pt-4 dark:border-white/10">
              <p className="mb-3 text-sm font-medium text-slate-600 dark:text-slate-300">{t(selectedDestination.label)} · {t(scopeLabels[selectedDestination.scope])}</p>
              <div key={`${selectedDestination.id}:${companyVersion}`}>{editor ?? <Link href={destinationHref(selectedDestination)} className="btn-secondary inline-flex text-sm">{t('Continue to existing settings')}</Link>}</div>
            </div>}
            {(['settings', 'operations'] as const).map(kind => {
              const destinations = area.destinations.filter(destination => kind === 'operations' ? destination.kind === 'operations' : destination.kind !== 'operations');
              return destinations.length ? <div key={kind} className="mt-6"><h3 className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{t(kind === 'operations' ? 'Related operations' : 'Settings and templates')}</h3><ul className="mt-2">{destinations.map(destinationRow)}</ul></div> : null;
            })}
          </>}
        </section>
      </div>
    </div>
  );
}
