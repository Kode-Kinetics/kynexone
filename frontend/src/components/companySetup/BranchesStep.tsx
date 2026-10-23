'use client';

import { useT } from '../../hooks/useT';
import { msg } from '../../i18n/translations';
import type { CompanySetupBranch, CompanySetupDraftBody, CompanySetupLocation } from '../../api/companySetup';
import { MANAGE_TAB, TIME_ZONES, stepWarnings, withOneHeadOffice } from '../../lib/companySetupWizard';
import { RowsEditor, type RowColumn } from './RowsEditor';
import { Notice, SectionHeader, useWizardForm } from './wizardForm';
import { UseDefaultsButton } from './UseDefaultsButton';
import { LocationPinPanel } from './LocationPinPanel';

const PACK_TIME_ZONE: Record<string, string> = { SA: 'Asia/Riyadh', AE: 'Asia/Dubai', QA: 'Asia/Qatar', KW: 'Asia/Kuwait', BH: 'Asia/Bahrain', OM: 'Asia/Muscat', EG: 'Africa/Cairo', IN: 'Asia/Kolkata', PK: 'Asia/Karachi', GB: 'Europe/London', US: 'America/New_York' };

/** Step 3: branches (exactly one head office) and, optionally, the locations people punch in at. */
export function BranchesStep({ defaults }: { defaults: CompanySetupDraftBody | null }) {
  const t = useT();
  const { body, update, errorFor, context, readOnly, baseline } = useWizardForm();
  // Existing mode: "defaults" are what the company has today, so the section reset is not offered; rows the company
  // already has are edited in place and removed only in Setup (Addendum A3).
  const existing = !!body.existingCompanyId;
  const manage = (key: keyof typeof MANAGE_TAB) => existing ? { href: `/setup?tab=${MANAGE_TAB[key].tab}&companyId=${encodeURIComponent(body.existingCompanyId!)}`, label: MANAGE_TAB[key].label } : undefined;
  const timeZone = defaults?.branches[0]?.timeZoneId || PACK_TIME_ZONE[body.company.countryCode] || 'Asia/Riyadh';
  // Exactly one head office: ticking one unticks the others; a lone branch is the head office.
  const setBranches = (branches: CompanySetupBranch[]) => update(b => ({ ...b, branches: withOneHeadOffice(b.branches, branches) }));
  const setLocations = (index: number, locations: CompanySetupLocation[]) =>
    update(b => ({ ...b, branches: b.branches.map((row, i) => (i === index ? { ...row, locations } : row)) }));
  const branchColumns: RowColumn<CompanySetupBranch>[] = [
    { key: 'code', label: msg('Code'), required: true, width: 'w-full sm:w-28' },
    { key: 'nameEn', label: msg('Name (English)'), required: true, width: 'w-full sm:w-48' },
    { key: 'nameAr', label: msg('Name (Arabic)'), arabic: true, width: 'w-full sm:w-48' },
    { key: 'city', label: msg('City'), required: true, width: 'w-full sm:w-36' },
    { key: 'timeZoneId', label: msg('Time zone'), kind: 'select', options: TIME_ZONES.map(z => ({ value: z, label: z })), width: 'w-full sm:w-44' },
    { key: 'isHeadOffice', label: msg('Head office'), kind: 'checkbox' },
  ];
  const locationColumns: RowColumn<CompanySetupLocation>[] = [
    { key: 'code', label: msg('Code'), required: true, width: 'w-full sm:w-28' },
    { key: 'nameEn', label: msg('Name (English)'), required: true, width: 'w-full sm:w-48' },
    { key: 'nameAr', label: msg('Name (Arabic)'), arabic: true, width: 'w-full sm:w-48' },
  ];
  // Latitude, longitude and radius live in the location's "Pin this location" panel.
  const editLocation = (branchIndex: number, locations: CompanySetupLocation[], locationIndex: number, patch: Partial<CompanySetupLocation>) =>
    setLocations(branchIndex, locations.map((l, j) => (j === locationIndex ? { ...l, ...patch } : l)));
  const branchError = errorFor('branches');
  const warnings = stepWarnings(3, { body, context });
  return (
    <div className="space-y-4">
      <SectionHeader
        title={t('Branches')}
        description={t('Every company needs one head office. Add the other branches people are assigned to.')}
        actions={!readOnly && !existing && defaults && defaults.branches.length > 0
          ? <UseDefaultsButton current={body.branches} defaults={defaults.branches} section={t('Branches')}
              onApply={() => update(b => ({ ...b, branches: defaults.branches.map(row => ({ ...row, locations: row.locations ?? [] })) }))} />
          : undefined}
      />
      {branchError && <p id="ccw-branches-error" role="alert" className="text-sm font-medium text-rose-700 dark:text-rose-300">{branchError}</p>}
      <RowsEditor<CompanySetupBranch>
        path="branches"
        rows={body.branches}
        columns={branchColumns}
        onChange={setBranches}
        manage={manage('branches')}
        baselineRows={baseline?.branches}
        newRow={() => ({ code: '', nameEn: '', nameAr: '', city: '', timeZoneId: timeZone, isHeadOffice: body.branches.length === 0, locations: [] })}
        addLabel={t('Add branch')}
        emptyText={t('No branches yet. Add the head office first.')}
        rowLabel={i => t('Branch {number}', { number: i + 1 })}
        rowExtra={(row, index) => (
          <details className="mt-3 rounded-lg bg-slate-50 p-3 dark:bg-white/[0.03]" open={row.locations.length > 0}>
            <summary className="cursor-pointer text-sm font-semibold text-slate-800 dark:text-slate-200">{t('Locations for this branch ({count})', { count: row.locations.length })}</summary>
            <p className="mt-1 text-xs text-slate-600 dark:text-slate-400">{t('Optional. A location is a site people punch in at. Pin it on the map to set its geofence.')}</p>
            <div className="mt-2">
              <RowsEditor<CompanySetupLocation>
                path={`branches.${index}.locations`}
                rows={row.locations}
                columns={locationColumns}
                onChange={locations => setLocations(index, locations)}
                manage={manage('locations')}
                baselineRows={baseline?.branches.find(b => row.existingId && b.existingId === row.existingId)?.locations}
                newRow={() => ({ code: '', nameEn: '', nameAr: '', latitude: null, longitude: null, radiusMeters: 150 })}
                addLabel={t('Add location')}
                emptyText={t('No locations for this branch.')}
                rowLabel={i => t('Location {number}', { number: i + 1 })}
                rowExtra={(location, j) => (
                  <LocationPinPanel path={`branches.${index}.locations.${j}`} location={location} countryCode={body.company.countryCode}
                    name={location.nameEn?.trim() || t('Location {number}', { number: j + 1 })}
                    onChange={patch => editLocation(index, row.locations, j, patch)} />
                )}
              />
            </div>
          </details>
        )}
      />
      {warnings.map(w => <Notice key={w.field} tone="warning" role="status">{t(w.message, w.params)}</Notice>)}
    </div>
  );
}
