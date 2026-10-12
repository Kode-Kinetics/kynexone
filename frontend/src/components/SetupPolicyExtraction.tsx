'use client';

import { useEffect, useRef, useState } from 'react';
import { setupAssistantApi, type CompanyProfile, type SetupConfiguration, type PolicyExtractionProposal, type PolicyExtractionResult } from '../api/setupAssistant';
import { apiErrorReason } from '../api/client';
import { useT } from '../hooks/useT';
import { msg } from '../i18n/translations';

// A closed list keeps generated object paths out of assignment and prevents prototype writes.
const CONFIG_FIELDS = new Set(['attendanceMethods', 'attendancePolicy', 'overtimeModes', 'overtimePolicy', 'grades', 'leavePolicies', 'benefitPlans']);
const PROFILE_FIELDS = new Set(['countryCode', 'industry', 'companySize', 'currencyCode', 'legalEntityName', 'branchCity', 'operatingModel', 'approvalModel', 'workPattern', 'weekendPattern', 'probationMonths', 'noticePeriodDays', 'workforceMix', 'overtimeHandling', 'attendanceCapture', 'payCycle', 'timeZone', 'defaultLanguage', 'leaveYearBasis']);
const FIELD_NAMES: Record<string, string> = {
  attendanceMethods: msg('Time recording methods'), attendancePolicy: msg('Attendance policy'), overtimeModes: msg('Overtime options'), overtimePolicy: msg('Overtime policy'), grades: msg('Salary grades'), leavePolicies: msg('Leave policies'), hrConfig: msg('Approval and management preferences'), benefitPlans: msg('Benefits'), countryCode: msg('Country'), industry: msg('Industry'), companySize: msg('Company size'), currencyCode: msg('Currency'), legalEntityName: msg('Legal entity name'), branchCity: msg('Head office city'), operatingModel: msg('Management structure preference'), approvalModel: msg('Approval preference'), workPattern: msg('Working pattern'), weekendPattern: msg('Weekend'), probationMonths: msg('Probation (months)'), noticePeriodDays: msg('Notice (days)'), workforceMix: msg('Workforce'), overtimeHandling: msg('Overtime'), attendanceCapture: msg('Time recording'), payCycle: msg('Pay cycle'), timeZone: msg('Time zone'), defaultLanguage: msg('Working language'), leaveYearBasis: msg('Leave balance year'),
};
function allowed(proposal: PolicyExtractionProposal) {
  return (proposal.target === 'configuration' ? CONFIG_FIELDS : proposal.target === 'profile' ? PROFILE_FIELDS : new Set()).has(proposal.field);
}
const VALUE_NAMES: Record<string, string> = {
  WebCheckIn: msg('Web check-in'), BiometricDevice: msg('Biometric device'), MobileGeofence: msg('Mobile app with location'), Manual: msg('Entered by hand'),
  PaidOvertime: msg('Paid overtime'), CompensatoryOff: msg('Time off in lieu'), NotApplicable: msg('No overtime policy'),
  Calendar: msg('Calendar year (1 January)'), Monthly: msg('Monthly'), JoiningDate: msg('Joining date'),
  en: msg('English'), ar: msg('Arabic'), bilingual: msg('English and Arabic'),
};
function readableLabel(key: string) {
  const words = key.replace(/([a-z])([A-Z])/g, '$1 $2');
  return words.charAt(0).toUpperCase() + words.slice(1);
}
function PolicyValue({ value }: { value: unknown }) {
  const t = useT();
  if (value == null || value === '') return <span>—</span>;
  if (Array.isArray(value)) return value.length ? <ul className="space-y-2">{value.map((item, index) => <li key={index}><PolicyValue value={item} /></li>)}</ul> : <span>—</span>;
  if (typeof value === 'object') {
    const record = value as Record<string, unknown>;
    const title = [record.name ?? record.nameEn, record.code].filter(item => typeof item === 'string' && item).join(' · ');
    return <details className="rounded border border-slate-200 p-2 dark:border-white/15"><summary className="cursor-pointer font-medium">{title || t('View all settings')}</summary><dl className="mt-2 space-y-2">{Object.entries(record).map(([key, item]) => <div key={key} className="grid grid-cols-2 gap-2"><dt className="text-slate-500 dark:text-slate-400">{t(readableLabel(key))}</dt><dd className="min-w-0 break-words"><PolicyValue value={item} /></dd></div>)}</dl></details>;
  }
  if (typeof value === 'boolean') return <span>{t(value ? 'Yes' : 'No')}</span>;
  return <span>{typeof value === 'string' ? t(VALUE_NAMES[value] ?? value) : String(value)}</span>;
}

export function SetupPolicyExtraction({ value, onChange, profile, onProfileChange }: {
  value: SetupConfiguration; onChange: (value: SetupConfiguration) => void;
  profile: CompanyProfile; onProfileChange: (patch: Partial<CompanyProfile>) => void;
}) {
  const t = useT();
  const [result, setResult] = useState<PolicyExtractionResult | null>(null);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [busy, setBusy] = useState(false); const [error, setError] = useState(''); const [notice, setNotice] = useState('');
  const request = useRef<AbortController | null>(null);
  const snapshot = useRef('');
  const current = useRef({ value, profile }); current.current = { value, profile };
  useEffect(() => () => request.current?.abort(), []);
  useEffect(() => { request.current?.abort(); setResult(null); setSelected(new Set()); setBusy(false); setNotice(''); }, [value.policyDocumentId, value.policySourceHash]);
  const signature = () => JSON.stringify(current.current);
  const extract = async () => {
    if (!value.policyDocumentId || !value.usePolicySourceForAi || busy) return;
    const controller = new AbortController(); request.current = controller;
    setBusy(true); setError(''); setNotice(''); setResult(null); snapshot.current = signature();
    try {
      const response = await setupAssistantApi.extractPolicy(value.policyDocumentId, { ...profile, configuration: value }, controller.signal);
      if (controller.signal.aborted) return;
      if (signature() !== snapshot.current) { setError(t('Your setup changed during extraction. Extract again to review current suggestions.')); return; }
      if (response.documentId !== value.policyDocumentId || response.sourceHash !== value.policySourceHash) { setError(t('The policy version changed. Upload or select the current document again.')); return; }
      setResult(response); setSelected(new Set());
    } catch (cause) { if (!controller.signal.aborted) setError(apiErrorReason(cause, t('Policy extraction failed. Your entries are preserved. Check the document and try again.'))); }
    finally { if (!controller.signal.aborted) setBusy(false); }
  };
  const accept = () => {
    if (!result || !selected.size) return;
    if (signature() !== snapshot.current) { setError(t('Your setup changed after extraction. Extract again before accepting suggestions.')); return; }
    const configPatch: Record<string, unknown> = {}; const profilePatch: Record<string, unknown> = {};
    let fieldSources = [...(value.policyFieldSources ?? [])];
    for (const index of selected) {
      const proposal = result.proposals[index]; if (!proposal || !allowed(proposal)) continue;
      (proposal.target === 'configuration' ? configPatch : profilePatch)[proposal.field] = proposal.value;
      fieldSources = fieldSources.filter(source => source.target !== proposal.target || source.field !== proposal.field);
      fieldSources.push({ documentId: result.documentId, contentSha256: result.sourceHash, target: proposal.target, field: proposal.field, sourceStart: proposal.sourceStart, sourceLength: proposal.sourceLength });
    }
    onChange({ ...value, ...configPatch, policyFieldSources: fieldSources }); onProfileChange(profilePatch as Partial<CompanyProfile>);
    setResult(null); setSelected(new Set()); setNotice(t('Selected suggestions copied into setup. Review the following steps before generating your draft.'));
  };
  return <div className="space-y-3">
    <p className="text-xs text-slate-600 dark:text-slate-300">{t('Extract one policy section at a time. Large handbooks may exceed the configured AI context limit; upload a shorter section if needed.')}</p>
    <p className="text-sm text-slate-600 dark:text-slate-300">{t('AI proposes settings with supporting policy passages. Select the changes you want; existing values are shown before replacement.')}</p>
    <label className="flex items-start gap-2 text-sm"><input type="checkbox" className="mt-0.5 h-4 w-4 accent-sapphire" checked={value.usePolicySourceForAi ?? false} disabled={busy} onChange={event => { onChange({ ...value, usePolicySourceForAi: event.target.checked }); setResult(null); }} />{t('Allow the configured AI service to read this document and propose setup settings.')}</label>
    <div className="flex flex-wrap items-center gap-2"><button type="button" className="btn-primary" disabled={busy || !value.usePolicySourceForAi} onClick={() => void extract()}>{busy ? t('Extracting settings…') : t('Extract settings')}</button>{busy && <button type="button" className="btn-secondary" onClick={() => { request.current?.abort(); setBusy(false); }}>{t('Cancel')}</button>}<span className="text-xs text-slate-500">{t('Extraction does not publish the policy or activate company rules.')}</span></div>
    {busy && <p role="status" className="text-sm">{t('Extracting settings…')}</p>}{error && <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{error}</p>}{notice && <p role="status" className="text-sm text-emerald-700 dark:text-emerald-300">{notice}</p>}
    {result && <div className="space-y-3">
      <p className="text-sm">{result.message}</p>
      {!!result.coverage?.length && <div className="flex flex-wrap gap-2">{result.coverage.map(item => <span key={item.section} className="rounded border border-slate-300 px-2 py-1 text-xs dark:border-white/20">{t(item.section)}: {t(item.status)}</span>)}</div>}
      {!!result.issues?.length && <details className="rounded-lg border border-amber-300 p-3 dark:border-amber-500/40" open><summary className="cursor-pointer text-sm font-semibold">{t('Questions and unsupported rules')} ({result.issues.length})</summary><ul className="mt-2 space-y-1 text-sm">{result.issues.map((issue, index) => <li key={index}>{t(issue.section)} · {t(issue.kind === 'missing' ? 'Missing' : issue.kind === 'unsupported' ? 'Unsupported' : 'Review required')}: {issue.message}</li>)}</ul></details>}
      <div className="max-h-80 space-y-2 overflow-auto" tabIndex={0} role="region" aria-label={t('Extracted settings')}>
        {result.proposals.map((proposal, index) => <div key={`${proposal.target}-${proposal.field}-${index}`} className="rounded-lg border border-slate-200 bg-white p-3 dark:border-white/10 dark:bg-slate-950">
          <label className="flex items-start gap-2 text-sm font-semibold"><input type="checkbox" className="mt-0.5 h-4 w-4 accent-sapphire" disabled={!allowed(proposal)} checked={selected.has(index)} onChange={event => setSelected(previous => { const next = new Set(previous); if (event.target.checked) next.add(index); else next.delete(index); return next; })} />{t(FIELD_NAMES[proposal.field] ?? proposal.field)}</label>
          <div className="mt-2 grid gap-3 text-xs sm:grid-cols-2"><div><p className="font-medium text-slate-500">{t('Current setting')}</p><div className="mt-1 break-words"><PolicyValue value={(proposal.target === 'configuration' ? value : profile)[proposal.field as never]} /></div></div><div><p className="font-medium text-sapphire dark:text-blue-300">{t('Proposed setting')}</p><div className="mt-1 break-words"><PolicyValue value={proposal.value} /></div></div></div>
          <blockquote className="mt-2 border-s-2 border-sapphire/30 ps-3 text-xs leading-5">{proposal.sourceQuote}</blockquote>
          {!allowed(proposal) && <p className="mt-2 text-xs text-amber-800 dark:text-amber-300">{t('This setting needs manual review and cannot be copied automatically.')}</p>}
        </div>)}
      </div>
      <button type="button" className="btn-primary" disabled={!selected.size} onClick={accept}>{t('Use selected suggestions ({count})', { count: selected.size })}</button>
    </div>}
  </div>;
}
