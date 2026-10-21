'use client';

import { useT } from '../../hooks/useT';
import { msg } from '../../i18n/translations';
import type { CompanySetupBranch, CompanySetupDraftBody, CompanySetupLocation } from '../../api/companySetup';
import { TIME_ZONES, stepWarnings } from '../../lib/companySetupWizard';
import { RowsEditor, type RowColumn } from './RowsEditor';
import { Notice, SectionHeader, useWizardForm } from './wizardForm';

const PACK_TIME_ZONE: Record<string, string> = { SA: 'Asia/Riyadh', AE: 'Asia/Dubai', QA: 'Asia/Qatar', KW: 'Asia/Kuwait', BH: 'Asia/Bahrain', OM: 'Asia/Muscat', EG: 'Africa/Cairo', IN: 'Asia/Kolkata', PK: 'Asia/Karachi', GB: 'Europe/London', US: 'America/New_York' };

/** Step 3: branches (exactly one head office) and, optionally, the locations people punch in at. */
export function BranchesStep({ defaults }: { defaults: CompanySetupDraftBody | null }) {
  const t = useT();
  const { body, update, errorFor, context, readOnly } = useWizardForm();
  const timeZone = defaults?.branches[0]?.timeZoneId || PACK_TIME_ZONE[body.company.countryCode] || 'Asia/Riyadh';
  const setBranches = (branches: CompanySetupBranch[]) => update(b => {
    // Exactly one head office: ticking one unticks the others; a lone branch is the head office.
    const before = b.branches;
    const ticked = branches.findIndex((row, i) => row.isHeadOffice && !before[i]?.isHeadOffice);
    const next = ticked >= 0 ? branches.map((row, i) => ({ ...row, isHeadOffice: i === ticked })) : branches;
    if (next.length === 1 && !next[0].isHeadOffice) next[0] = { ...next[0], isHeadOffice: true };
    return { ...b, branches: next };
  });
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
    { key: 'latitude', label: msg('Latitude'), kind: 'number', inputMode: 'decimal', width: 'w-full sm:w-28' },
    { key: 'longitude', label: msg('Longitude'), kind: 'number', inputMode: 'decimal', width: 'w-full sm:w-28' },
    { key: 'radiusMeters', label: msg('Radius (m)'), kind: 'number', inputMode: 'numeric', width: 'w-full sm:w-24' },
  ];
  const branchError = errorFor('branches');
  const warnings = stepWarnings(3, { body, context });
  return (
    <div className="space-y-4">
      <SectionHeader
        title={t('Branches')}
        description={t('Every company needs one head office. Add the other branches people are assigned to.')}
        actions={!readOnly && defaults && defaults.branches.length > 0 ? <button type="button" className="btn-secondary" onClick={() => setBranches(defaults.branches.map(row => ({ ...row, locations: row.locations ?? [] })))}>{t('Use defaults')}</button> : undefined}
      />
      {branchError && <p id="ccw-branches-error" role="alert" className="text-sm font-medium text-rose-700 dark:text-rose-300">{branchError}</p>}
      <RowsEditor<CompanySetupBranch>
        path="branches"
        rows={body.branches}
        columns={branchColumns}
        onChange={setBranches}
        newRow={() => ({ code: '', nameEn: '', nameAr: '', city: '', timeZoneId: timeZone, isHeadOffice: body.branches.length === 0, locations: [] })}
        addLabel={t('Add branch')}
        emptyText={t('No branches yet. Add the head office first.')}
        rowLabel={i => t('Branch {number}', { number: i + 1 })}
        rowExtra={(row, index) => (
          <details className="mt-3 rounded-lg bg-slate-50 p-3 dark:bg-white/[0.03]" open={row.locations.length > 0}>
            <summary className="cursor-pointer text-sm font-semibold text-slate-800 dark:text-slate-200">{t('Locations for this branch ({count})', { count: row.locations.length })}</summary>
            <p className="mt-1 text-xs text-slate-600 dark:text-slate-400">{t('Optional. A location is a site people punch in at; latitude, longitude and radius set its geofence.')}</p>
            <div className="mt-2">
              <RowsEditor<CompanySetupLocation>
                path={`branches.${index}.locations`}
                rows={row.locations}
                columns={locationColumns}
                onChange={locations => setLocations(index, locations)}
                newRow={() => ({ code: '', nameEn: '', nameAr: '', latitude: null, longitude: null, radiusMeters: 150 })}
                addLabel={t('Add location')}
                emptyText={t('No locations for this branch.')}
                rowLabel={i => t('Location {number}', { number: i + 1 })}
              />
            </div>
          </details>
        )}
      />
      {warnings.map(w => <Notice key={w.field} tone="warning" role="status">{t(w.message, w.params)}</Notice>)}
    </div>
  );
}
