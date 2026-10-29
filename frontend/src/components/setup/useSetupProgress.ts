'use client';

import { useEffect, useState } from 'react';
import { companySetupApi, type ManageableCompany, type ReadinessItem } from '../../api/companySetup';
import { branchesApi, companiesApi, costCentersApi, departmentsApi, designationsApi, gradesApi } from '../../api/organization';
import { fiscalYearsApi, gccSettingsApi, locationsApi, masterDataApi, notificationTemplatesApi } from '../../api/setup';
import { activeCount, type RecordCheckedSection, type RecordCounts } from './setupProgress';

export type ProgressLoad = 'idle' | 'loading' | 'ready' | 'unavailable';

/**
 * Guided setup's per-company progress, from the same endpoint and fields its chooser reads
 * (GET /api/company-setup/context → manageableCompanies[].readinessDone/readinessTotal). `companies` is null when the
 * server does not offer it or the caller may not see it (a 403 is the normal answer for a reader).
 */
export function useGuidedSetupCompanies(enabled: boolean, refreshKey: string): { status: ProgressLoad; companies: ManageableCompany[] | null } {
  const [state, setState] = useState<{ status: ProgressLoad; companies: ManageableCompany[] | null }>({ status: 'idle', companies: null });
  useEffect(() => {
    if (!enabled) { setState({ status: 'idle', companies: null }); return; }
    let active = true;
    setState(current => ({ status: 'loading', companies: current.companies }));
    companySetupApi.context()
      .then(context => {
        if (!active) return;
        const companies = Array.isArray(context.manageableCompanies) ? context.manageableCompanies : null;
        setState({ status: companies ? 'ready' : 'unavailable', companies });
      })
      .catch(() => { if (active) setState({ status: 'unavailable', companies: null }); });
    return () => { active = false; };
  }, [enabled, refreshKey]);
  return state;
}

/**
 * The checklist behind one company's "N of M" (GET /api/company-setup/readiness/{id}); null until read or on failure.
 * While it is re-read for the same company the last list stays (status 'loading'), so a headline counted from it never
 * falls back to an older number in between; another company's list is never shown.
 */
export function useReadinessItems(companyId: string | null, refreshKey: string): { status: ProgressLoad; items: ReadinessItem[] | null } {
  const [state, setState] = useState<{ companyId: string | null; status: ProgressLoad; items: ReadinessItem[] | null }>({ companyId: null, status: 'idle', items: null });
  useEffect(() => {
    if (!companyId) { setState({ companyId: null, status: 'idle', items: null }); return; }
    let active = true;
    setState(current => ({ companyId, status: 'loading', items: current.companyId === companyId ? current.items : null }));
    companySetupApi.readiness(companyId)
      .then(items => { if (active) setState(Array.isArray(items) ? { companyId, status: 'ready', items } : { companyId, status: 'unavailable', items: null }); })
      .catch(() => { if (active) setState({ companyId, status: 'unavailable', items: null }); });
    return () => { active = false; };
  }, [companyId, refreshKey]);
  return state.companyId === companyId ? { status: state.status, items: state.items } : { status: companyId ? 'loading' : 'idle', items: null };
}

type Row = Record<string, unknown>;
const RECORD_CHECKS: Record<RecordCheckedSection, () => Promise<number | null>> = {
  // A company counts once it is active; a draft or one waiting for activation is not set up yet.
  companies: () => companiesApi.listAll().then(rows => activeCount(rows, (row: Row) => row.isActive !== false && (row.approvalStatus == null || row.approvalStatus === 'Active'))),
  branches: () => branchesApi.listAll().then(rows => activeCount(rows)),
  departments: () => departmentsApi.listAll().then(rows => activeCount(rows)),
  designations: () => designationsApi.listAll().then(rows => activeCount(rows)),
  grades: () => gradesApi.listAll().then(rows => activeCount(rows)),
  costCenters: () => costCentersApi.listAll().then(rows => activeCount(rows)),
  locations: () => locationsApi.list().then(rows => activeCount(rows)),
  masterData: () => masterDataApi.listTypes(false).then(rows => activeCount(rows)),
  // Fiscal years and country rules have no active flag: every saved one counts.
  fiscalYears: () => fiscalYearsApi.list().then(rows => activeCount(rows, () => true)),
  notificationTemplates: () => notificationTemplatesApi.list().then(rows => activeCount(rows)),
  gccSettings: () => gccSettingsApi.list().then(rows => activeCount(rows, (row: Row) => row.isActive !== false)),
};

/**
 * Active-record counts for the given sections, read from the list endpoints Setup's own sections use. A failed or
 * unreadable list is null, so its item is left out of every status instead of being counted as not started.
 */
export function useRecordCounts(sections: readonly RecordCheckedSection[], scopeKey: string, refreshKey: string): RecordCounts {
  const [state, setState] = useState<{ scopeKey: string; counts: RecordCounts }>({ scopeKey, counts: {} });
  const key = sections.join(',');
  useEffect(() => {
    let active = true;
    if (!key) { setState({ scopeKey, counts: {} }); return; }
    const wanted = key.split(',') as RecordCheckedSection[];
    Promise.all(wanted.map(section => RECORD_CHECKS[section]().catch(() => null).then(count => [section, count] as const)))
      .then(entries => { if (active) setState({ scopeKey, counts: Object.fromEntries(entries) }); });
    return () => { active = false; };
  }, [key, scopeKey, refreshKey]);
  // Counts read for another company are never shown for this one, not even while the new ones load.
  return state.scopeKey === scopeKey ? state.counts : {};
}
