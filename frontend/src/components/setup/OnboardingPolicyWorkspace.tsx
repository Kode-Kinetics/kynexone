'use client';

import { useCallback, useEffect, useLayoutEffect, useMemo, useState } from 'react';
import dynamic from 'next/dynamic';
import { useSearchParams } from 'next/navigation';
import type { CompanyDto } from '../../api/organization';
import type { CompanyProfile, SetupConfiguration } from '../../api/setupAssistant';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { useT } from '../../hooks/useT';
import { SetupPolicySource } from '../SetupPolicySource';
import { SetupPendingFilesState } from './SetupEditorState';
import { usePolicyIntake } from '../../lib/policyIntake';

const OnboardingTab = dynamic(() => import('../../views/RecruitmentPage').then(module => module.OnboardingTab));

export function OnboardingPolicyWorkspace({ companies, onDirtyChange }: {
  companies: CompanyDto[]; onDirtyChange: (dirty: boolean) => void;
}) {
  const { user } = useAuth();
  const { companies: authorized, selectedCompanyId, accountType } = useCompany();
  const active = authorized.filter(company => company.isActive);
  const target = active.find(company => company.id === selectedCompanyId)
    ?? (accountType === 'SingleCompany' && active.length === 1 ? active[0] : undefined);
  const company = companies.find(company => company.id === target?.id && company.isActive);
  const scope = `${user?.tenantId ?? ''}:${user?.id ?? ''}:${target?.id ?? ''}`;
  return <OnboardingPolicyEditor key={scope} scope={scope} company={company} onDirtyChange={onDirtyChange} />;
}

function OnboardingPolicyEditor({ scope, company, onDirtyChange }: {
  scope: string; company?: CompanyDto; onDirtyChange: (dirty: boolean) => void;
}) {
  const t = useT();
  const { hasPermission, hasRole } = useAuth();
  // Policy intake is off by the owner's decision (only manual setup is enabled); while it is off this is the
  // manual task editor alone, with no entry-method switch and no upload.
  const policyIntake = usePolicyIntake();
  const canIntake = policyIntake && !!company && hasPermission('organization.write')
    && (hasRole('Admin') || hasRole('HR Manager') || hasRole('HR Officer'));
  const storageKey = `kynexone-onboarding-intake:v1:${scope}`;
  const [configuration, setConfiguration] = useState<SetupConfiguration | null>(null);
  const [storageError, setStorageError] = useState(false);
  const [mode, setMode] = useState<'manual' | 'policy' | 'review'>('manual');
  const [editorDirty, setEditorDirty] = useState(false);
  const [pendingFiles, setPendingFiles] = useState(false);
  const [revision, setRevision] = useState(0);
  const [policyDraft, setPolicyDraft] = useState(false);
  // /setup?tab=onboardingChecklists&policyDraft=1 is where an accepted policy extraction lands: open the draft it saved.
  const draftRequested = useSearchParams().get('policyDraft') === '1';
  useEffect(() => {
    if (!policyIntake || !draftRequested) return;
    setPolicyDraft(true); setRevision(value => value + 1);
  }, [policyIntake, draftRequested]);
  useEffect(() => {
    try {
      const stored = JSON.parse(localStorage.getItem(storageKey) ?? 'null');
      setConfiguration(stored?.version === 1 && stored.configuration && typeof stored.configuration === 'object' ? stored.configuration : {});
    } catch { setConfiguration({}); setStorageError(true); }
  }, [storageKey]);
  const updateConfiguration = useCallback((next: SetupConfiguration) => {
    setConfiguration(next);
    try { localStorage.setItem(storageKey, JSON.stringify({ version: 1, configuration: next })); setStorageError(false); }
    catch { setStorageError(true); }
  }, [storageKey]);
  useLayoutEffect(() => {
    // A policy draft left in this browser from before the switch must not hold the manual editor dirty.
    onDirtyChange(editorDirty || (policyIntake && (pendingFiles || storageError || !!configuration?.policyIntakeText)));
    return () => onDirtyChange(false);
  }, [editorDirty, pendingFiles, storageError, configuration?.policyIntakeText, onDirtyChange, policyIntake]);
  const profile = useMemo<CompanyProfile>(() => ({
    companyId: company?.id, legalEntityName: company?.legalNameEn,
    countryCode: company?.countryCode ?? '', currencyCode: company?.defaultCurrency ?? '',
    industry: '', companySize: '',
    sections: { org: false, leave: false, shifts: false, payroll: false, entity: false, governance: false,
      leavePolicies: false, holidays: false, attendance: false, localization: false, benefits: false },
  }), [company?.id, company?.legalNameEn, company?.countryCode, company?.defaultCurrency]);
  const confirmEditorReset = () => !editorDirty || window.confirm(t('Replace the unsaved values in the current task form with this policy draft?'));
  const review = () => {
    if (!confirmEditorReset()) return;
    setPolicyDraft(true); setRevision(value => value + 1); setMode('review');
  };
  if (!policyIntake) return <div className="min-w-0 space-y-4">
    <OnboardingTab configurationOnly policyDraft={false} policyDraftRevision={0} onDirtyChange={setEditorDirty} />
  </div>;
  return <div className="min-w-0 space-y-4">
    <div className="flex flex-wrap gap-2" aria-label={t('Onboarding entry method')}>
      <button type="button" className={mode === 'manual' ? 'btn-primary' : 'btn-secondary'} aria-pressed={mode === 'manual'} onClick={() => {
        if (mode === 'manual') return;
        if (policyDraft && !confirmEditorReset()) return;
        setPolicyDraft(false); setMode('manual');
      }}>{t('Enter manually')}</button>
      <button type="button" className={mode === 'policy' ? 'btn-primary' : 'btn-secondary'} aria-pressed={mode === 'policy'} disabled={!canIntake} onClick={() => setMode('policy')}>{t('Fill from policy')}</button>
    </div>
    <p className="text-sm text-slate-600 dark:text-slate-300">{t('Enter tasks manually or upload a policy to prepare this same form. Review every task, then save explicitly. Uploads remain private drafts; nothing is published or assigned to employees.')}</p>
    {!company && <p role="status" className="text-sm text-amber-800 dark:text-amber-200">{t('Select an active company to prepare policy form values. Manual templates remain tenant-wide.')}</p>}
    {storageError && <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{t('The local policy draft could not be saved. Keep this page open and retry.')}</p>}
    {configuration && canIntake && <div hidden={mode !== 'policy'}>
      <SetupPendingFilesState.Provider value={setPendingFiles}>
        <SetupPolicySource value={configuration} onChange={updateConfiguration} profile={profile} onProfileChange={() => {}} onReviewOnboardingDraft={review} />
      </SetupPendingFilesState.Provider>
    </div>}
    <div hidden={mode === 'policy'}>
      <OnboardingTab configurationOnly policyDraft={policyDraft} policyDraftRevision={revision} onDirtyChange={setEditorDirty} />
    </div>
  </div>;
}
