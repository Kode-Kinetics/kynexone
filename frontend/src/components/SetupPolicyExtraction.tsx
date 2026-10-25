'use client';

import { useEffect, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import { useAuth } from '../contexts/AuthContext';
import { useCompany } from '../contexts/CompanyContext';
import { setupAssistantApi, type CompanyProfile, type SetupConfiguration, type PolicyExtractionProposal, type PolicyExtractionResult, type PolicyReviewItem } from '../api/setupAssistant';
import { apiErrorReason } from '../api/client';
import { useT } from '../hooks/useT';
import { msg } from '../i18n/translations';
import { appendOnboardingTaskDrafts, appendPolicyProposals, mergePolicyValue, policySources, proposalsConflict, type SourcedPolicyProposal } from '../lib/policyBinder';
import { savePolicyFormDraft, type PolicyOnboardingTaskDraft } from '../lib/policyFormDrafts';
import { matchesPolicyExtractionInputs } from '../lib/setupDraftLifecycle';
import { PartialPolicyReview } from './PartialPolicyReview';
import type { PartialReviewEntry } from '../lib/partialPolicy';
import { blockingReviewItems, checkpointMatchesReviewCompany, deduplicateCoverageNotes, extractionReviewItems, reviewItemKey } from '../lib/policyReview';

// A closed list keeps generated object paths out of assignment and prevents prototype writes.
const CONFIG_FIELDS = new Set(['attendanceMethods', 'attendancePolicy', 'overtimeModes', 'overtimePolicy', 'grades', 'leavePolicies', 'benefitPlans']);
const PROFILE_FIELDS = new Set(['countryCode', 'industry', 'companySize', 'currencyCode', 'legalEntityName', 'branchCity', 'operatingModel', 'approvalModel', 'workPattern', 'weekendPattern', 'probationMonths', 'noticePeriodDays', 'workforceMix', 'overtimeHandling', 'attendanceCapture', 'payCycle', 'timeZone', 'defaultLanguage', 'leaveYearBasis']);
const FIELD_NAMES: Record<string, string> = {
  attendanceMethods: msg('Time recording methods'), attendancePolicy: msg('Attendance policy'), overtimeModes: msg('Overtime options'), overtimePolicy: msg('Overtime policy'), grades: msg('Salary grades'), leavePolicies: msg('Leave policies'), hrConfig: msg('Approval and management preferences'), benefitPlans: msg('Benefits'), countryCode: msg('Country'), industry: msg('Industry'), companySize: msg('Company size'), currencyCode: msg('Currency'), legalEntityName: msg('Legal entity name'), branchCity: msg('Head office city'), operatingModel: msg('Management structure preference'), approvalModel: msg('Approval preference'), workPattern: msg('Working pattern'), weekendPattern: msg('Weekend'), probationMonths: msg('Probation (months)'), noticePeriodDays: msg('Notice (days)'), workforceMix: msg('Workforce'), overtimeHandling: msg('Overtime'), attendanceCapture: msg('Time recording'), payCycle: msg('Pay cycle'), timeZone: msg('Time zone'), defaultLanguage: msg('Working language'), leaveYearBasis: msg('Leave balance year'),
};
function allowed(proposal: PolicyExtractionProposal, companyId?: string) {
  if (companyId && proposal.target === 'profile' && ['legalEntityName', 'countryCode', 'currencyCode'].includes(proposal.field)) return false;
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
function proposalReview(proposal: SourcedPolicyProposal): PolicyReviewItem {
  return { id: proposal.reviewKey ?? '', itemType: 'proposal', kind: 'supported', section: 'Supported setting', summary: `${readableLabel(proposal.field)}: ${JSON.stringify(proposal.value).slice(0, 1800)}`, target: proposal.target, field: proposal.field,
    sources: [{ documentId: proposal.documentId, contentSha256: proposal.contentSha256, sourceName: proposal.sourceName, sourceQuote: proposal.sourceQuote, sourceStart: proposal.sourceStart, sourceLength: proposal.sourceLength }] };
}
function PolicyValue({ value }: { value: unknown }) {
  const t = useT();
  if (value == null || value === '') return <span>—</span>;
  if (Array.isArray(value)) return value.length ? <ul className="space-y-2">{value.map((item, index) => <li key={index}><PolicyValue value={item} /></li>)}</ul> : <span>—</span>;
  if (typeof value === 'object') {
    const record = value as Record<string, unknown>;
    const title = [record.name ?? record.nameEn, record.code].filter(item => typeof item === 'string' && item).join(' · ');
    return <details className="rounded border border-slate-200 p-2 dark:border-white/15"><summary className="cursor-pointer font-medium">{title || t('View all settings')}</summary><dl className="mt-2 space-y-2">{Object.entries(record).map(([key, item]) => <div key={key} className="grid grid-cols-2 gap-2"><dt className="text-slate-600 dark:text-slate-300">{t(readableLabel(key))}</dt><dd className="min-w-0 break-words"><PolicyValue value={item} /></dd></div>)}</dl></details>;
  }
  if (typeof value === 'boolean') return <span>{t(value ? 'Yes' : 'No')}</span>;
  return <span>{typeof value === 'string' ? t(VALUE_NAMES[value] ?? value) : String(value)}</span>;
}

export function SetupPolicyExtraction({ value, onChange, profile, onProfileChange, onReviewOnboardingDraft }: {
  value: SetupConfiguration; onChange: (value: SetupConfiguration) => void;
  profile: CompanyProfile; onProfileChange: (patch: Partial<CompanyProfile>) => void;
  onReviewOnboardingDraft?: () => void;
}) {
  const t = useT();
  const router = useRouter();
  const { user, hasPermission } = useAuth();
  const { accountType, companies } = useCompany();
  const [onboardingTaskDrafts, setOnboardingTaskDrafts] = useState<PolicyOnboardingTaskDraft[]>([]);
  const [proposals, setProposals] = useState<SourcedPolicyProposal[]>([]);
  const [issues, setIssues] = useState<PolicyExtractionResult['issues']>([]);
  const [partials, setPartials] = useState<PartialReviewEntry[]>([]);
  const [reviewItems, setReviewItems] = useState<PolicyReviewItem[]>([]);
  const [progress, setProgress] = useState('');
  const [complete, setComplete] = useState(false);
  const [preparedSetupCount, setPreparedSetupCount] = useState(0);
  const preparedCount = useRef(0);
  const [evidencePage, setEvidencePage] = useState(0);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [proposalPage, setProposalPage] = useState(0);
  const [issuePage, setIssuePage] = useState(0);
  const [busy, setBusy] = useState(false); const [error, setError] = useState(''); const [notice, setNotice] = useState('');
  const request = useRef<AbortController | null>(null);
  const snapshot = useRef('');
  const resume = useRef({ document: 0, chunk: 0 });
  const collected = useRef({ proposals: [] as SourcedPolicyProposal[], partials: [] as PartialReviewEntry[], issues: [] as PolicyExtractionResult['issues'], reviewItems: [] as PolicyReviewItem[], onboardingTaskDrafts: [] as PolicyOnboardingTaskDraft[] });
  const current = useRef({ value, profile }); current.current = { value, profile };
  useEffect(() => () => request.current?.abort(), []);
  const sources = policySources(value);
  const sourceSignature = JSON.stringify(sources);
  const currentProposalPage = Math.min(proposalPage, Math.max(0, Math.ceil(proposals.length / 25) - 1));
  const coverageNotes = deduplicateCoverageNotes(issues.filter(issue => !issue.sourceQuote || issue.kind === 'info'));
  const currentIssuePage = Math.min(issuePage, Math.max(0, Math.ceil(coverageNotes.length / 50) - 1));
  const currentEvidencePage = Math.min(evidencePage, Math.max(0, Math.ceil(reviewItems.length / 5) - 1));
  const availableSetupCount = proposals.filter(proposal => allowed(proposal, profile.companyId) && !blockingReviewItems(reviewItems, {}, proposal).length).length;
  const noFormValues = complete && preparedSetupCount === 0 && onboardingTaskDrafts.length === 0 && partials.length === 0;
  useEffect(() => {
    request.current?.abort(); setSelected(new Set()); setProposalPage(0); setIssuePage(0); setEvidencePage(0); setBusy(false); setNotice(''); setProgress('');
    const saved = current.current.value.policyExtraction;
    const companyMatches = saved && checkpointMatchesReviewCompany(saved.setupSignature, current.current.profile.companyId);
    if (saved && saved.sourceSignature === sourceSignature && saved.reviewVersion === 3 && companyMatches) {
      collected.current = { proposals: saved.proposals, partials: saved.partials, issues: saved.issues, reviewItems: saved.reviewItems ?? [], onboardingTaskDrafts: saved.onboardingTaskDrafts ?? [] };
      preparedCount.current = saved.preparedSetupCount ?? saved.proposals.filter(proposal => allowed(proposal, current.current.profile.companyId) && !blockingReviewItems(saved.reviewItems ?? [], {}, proposal).length).length;
      if (saved.preparedSetupCount == null && !preparedCount.current && (current.current.value.policyFieldSources ?? []).some(field => sources.some(source => source.documentId === field.documentId && source.contentSha256 === field.contentSha256))) preparedCount.current = 1;
      resume.current = { document: saved.documentIndex, chunk: saved.chunkIndex }; snapshot.current = saved.setupSignature; setComplete(saved.complete);
    } else {
      collected.current = { proposals: [], partials: [], issues: [], reviewItems: [], onboardingTaskDrafts: [] }; resume.current = { document: 0, chunk: 0 }; snapshot.current = ''; setComplete(false);
      preparedCount.current = 0;
      if (saved) setNotice(t(!companyMatches ? 'Your setup changed after extraction. Extract again before accepting suggestions.' : 'This saved extraction uses an earlier review format. Extract from policy again; your form values and review history are preserved.'));
    }
    setProposals(collected.current.proposals); setPartials(collected.current.partials); setIssues(collected.current.issues); setReviewItems(collected.current.reviewItems);
    setOnboardingTaskDrafts(collected.current.onboardingTaskDrafts);
    setPreparedSetupCount(preparedCount.current);
  }, [sourceSignature, profile.companyId]);
  const signature = () => JSON.stringify({ value: { ...current.current.value, policyExtraction: undefined, policyIntakeText: undefined, policyReview: undefined }, profile: { ...current.current.profile, configuration: undefined } });
  const inputsMatch = () => matchesPolicyExtractionInputs(snapshot.current, { ...current.current.profile, configuration: current.current.value });
  const saveCheckpoint = (finished: boolean) => {
    const next = { ...current.current.value, policyExtraction: { reviewVersion: 3, preparedSetupCount: preparedCount.current, sourceSignature, setupSignature: snapshot.current, documentIndex: resume.current.document, chunkIndex: resume.current.chunk, complete: finished, ...collected.current } };
    current.current = { ...current.current, value: next }; onChange(next);
  };
  const extract = async () => {
    if (!sources.length || !value.usePolicySourceForAi || busy) return;
    const controller = new AbortController(); request.current = controller;
    setBusy(true); setError(''); setNotice('');
    if (complete || (snapshot.current && !inputsMatch())) {
      resume.current = { document: 0, chunk: 0 }; collected.current = { proposals: [], partials: [], issues: [], reviewItems: [], onboardingTaskDrafts: [] }; setProposals([]); setPartials([]); setIssues([]); setReviewItems([]); setOnboardingTaskDrafts([]); setSelected(new Set()); setComplete(false);
      preparedCount.current = 0; setPreparedSetupCount(0);
    }
    snapshot.current = signature();
    try {
      while (resume.current.document < sources.length) {
        const source = sources[resume.current.document];
        const chunk = resume.current.chunk;
        setProgress(`${source.name} · ${t('Document')} ${resume.current.document + 1}/${sources.length} · ${t('Section')} ${chunk + 1}`);
        const response = await setupAssistantApi.extractPolicy(source.documentId, { ...profile, configuration: value }, controller.signal, chunk);
        if (controller.signal.aborted) return;
        if (!inputsMatch()) { setError(t('Your setup changed during extraction. Extract again to review current suggestions.')); return; }
        if (response.documentId !== source.documentId || response.sourceHash !== source.contentSha256) { setError(t('The policy version changed. Upload or select the current document again.')); return; }
        const next = response.progress;
        if (next && !next.isComplete && (next.nextChunkIndex == null || next.nextChunkIndex <= chunk)) {
          setError(response.message || t('This section could not be extracted. Retry to continue from this section.')); return;
        }
        const incomingPartials = (onReviewOnboardingDraft ? [] : response.partialProposals ?? []).map(proposal => ({ ...proposal, documentId: source.documentId, sourceHash: source.contentSha256, sourceName: source.name }));
        const incomingReviews = await extractionReviewItems(profile.companyId, source, response.issues, response.requirements ?? []);
        const mergedProposals = appendPolicyProposals(collected.current.proposals, (onReviewOnboardingDraft ? [] : response.proposals).map(proposal => ({ ...proposal, documentId: source.documentId, contentSha256: source.contentSha256, sourceName: source.name })));
        await Promise.all(mergedProposals.map(async proposal => { if (!proposal.reviewKey) proposal.reviewKey = await reviewItemKey(profile.companyId, proposalReview(proposal), proposal.value); }));
        if (controller.signal.aborted || !inputsMatch()) return;
        collected.current = {
          proposals: mergedProposals,
          partials: [...collected.current.partials, ...incomingPartials.filter(entry => !collected.current.partials.some(old => old.documentId === entry.documentId && old.field === entry.field && old.sourceStart === entry.sourceStart && old.sourceLength === entry.sourceLength))],
          issues: [...collected.current.issues, ...response.issues],
          reviewItems: [...collected.current.reviewItems, ...incomingReviews.filter(item => !collected.current.reviewItems.some(old => old.id === item.id))],
          onboardingTaskDrafts: appendOnboardingTaskDrafts(collected.current.onboardingTaskDrafts, (response.onboardingTaskDrafts ?? []).map(task => ({ ...task, documentId: source.documentId, contentSha256: source.contentSha256, sourceName: source.name }))),
        };
        setProposals(collected.current.proposals); setPartials(collected.current.partials); setIssues(collected.current.issues); setReviewItems(collected.current.reviewItems);
        setOnboardingTaskDrafts(collected.current.onboardingTaskDrafts);
        preparedCount.current = collected.current.proposals.filter(proposal => allowed(proposal, profile.companyId) && !blockingReviewItems(collected.current.reviewItems, {}, proposal).length).length;
        setPreparedSetupCount(preparedCount.current);
        if (!next || next.isComplete) resume.current = { document: resume.current.document + 1, chunk: 0 };
        else resume.current = { document: resume.current.document, chunk: next.nextChunkIndex! };
        saveCheckpoint(resume.current.document >= sources.length);
      }
      setComplete(true); setProgress('');
    } catch (cause) { if (!controller.signal.aborted) setError(apiErrorReason(cause, t('Policy extraction failed. Your entries are preserved. Check the document and try again.'))); }
    finally { if (!controller.signal.aborted) setBusy(false); }
  };
  const accept = () => {
    if (!complete || !selected.size) return;
    if (!inputsMatch()) { setError(t('Your setup changed after extraction. Extract again before accepting suggestions.')); return; }
    const configPatch: Record<string, unknown> = {}; const profilePatch: Record<string, unknown> = {};
    let fieldSources = [...(value.policyFieldSources ?? [])];
    for (const index of selected) {
      const proposal = proposals[index]; if (!proposal || !allowed(proposal, profile.companyId) || blockingReviewItems(reviewItems, {}, proposal).length) continue;
      const patch = proposal.target === 'configuration' ? configPatch : profilePatch;
      const original = (proposal.target === 'configuration' ? value : profile) as unknown as Record<string, unknown>;
      patch[proposal.field] = mergePolicyValue(proposal.field, patch[proposal.field] ?? original[proposal.field], proposal.value);
      const source = { documentId: proposal.documentId, contentSha256: proposal.contentSha256, target: proposal.target, field: proposal.field, sourceStart: proposal.sourceStart, sourceLength: proposal.sourceLength, ...(proposal.userCompleted ? { userCompleted: true } : {}) };
      const sourceIndex = fieldSources.findIndex(existing => existing.documentId === source.documentId && existing.contentSha256 === source.contentSha256 && existing.target === source.target && existing.field === source.field && existing.sourceStart === source.sourceStart && existing.sourceLength === source.sourceLength);
      if (sourceIndex < 0) fieldSources.push(source); else fieldSources[sourceIndex] = { ...fieldSources[sourceIndex], ...source };
    }
    const accepted = [...selected].map(index => proposals[index]);
    collected.current.proposals = proposals.filter((proposal, index) => !selected.has(index) && !accepted.some(other => proposalsConflict(proposal, other)));
    const nextValue = { ...value, ...configPatch, policyFieldSources: fieldSources, policyExtraction: undefined };
    const nextProfile = { ...profile, ...profilePatch } as CompanyProfile;
    current.current = { value: nextValue, profile: nextProfile };
    snapshot.current = signature();
    saveCheckpoint(true); onProfileChange(profilePatch as Partial<CompanyProfile>);
    setProposals(collected.current.proposals); setSelected(new Set()); setNotice(t('Selected suggestions copied into setup. Review the following steps before generating your draft.'));
  };
  const openOnboardingForm = () => {
    const company = companies.find(item => item.id === profile.companyId && item.isActive);
    if (!complete || busy || !user || !company || !hasPermission('recruitment.read') && !hasPermission('recruitment.write')) return;
    if (!inputsMatch()) { setError(t('Your setup changed after extraction. Extract again before accepting suggestions.')); return; }
    const bundle = { tenantId: user.tenantId, userId: user.id, companyId: company.id, companyName: company.name, accountType, sources, tasks: onboardingTaskDrafts };
    try {
      try { savePolicyFormDraft(localStorage, bundle); }
      catch (cause) {
        if (!(cause instanceof Error) || !cause.message.startsWith('Existing policy form drafts')) throw cause;
        if (!window.confirm(t('Your existing policy form draft has unsaved edits. Replace it with the newly extracted values? Saved checklist tasks will not change.'))) return;
        savePolicyFormDraft(localStorage, bundle, { replace: true });
      }
      if (onReviewOnboardingDraft) onReviewOnboardingDraft();
      else router.push(`/setup?tab=onboardingChecklists&policyDraft=1&companyId=${encodeURIComponent(company.id)}`);
    } catch (cause) { setError(apiErrorReason(cause, t('The form draft could not be saved in this browser. Your setup values are unchanged.'))); }
  };
  return <div className="space-y-3">
    <p className="text-xs text-slate-600 dark:text-slate-300">{t('Your complete binder is processed in sections automatically. Suggestions from all documents are combined for review; conflicting rules require your choice.')}</p>
    <p className="text-sm text-slate-600 dark:text-slate-300">{t('AI proposes settings with supporting policy passages. Select the changes you want; existing values are shown before replacement.')}</p>
    <details className="text-xs text-slate-600 dark:text-slate-300"><summary className="cursor-pointer font-medium">{t('Limits for reviewed changes')}</summary><p className="mt-2">{t('Document upload limits are separate from applying settings. Each reviewed setup submission supports up to 500 source references and 128 KiB of configuration data. If exceeded, review and apply smaller groups of settings.')}</p></details>
    <label className="flex items-start gap-2 text-sm"><input type="checkbox" className="mt-0.5 h-4 w-4 accent-sapphire" checked={value.usePolicySourceForAi ?? false} disabled={busy} onChange={event => { onChange({ ...value, usePolicySourceForAi: event.target.checked }); }} />{t('Allow the configured AI service to read this document and propose setup settings.')}</label>
    <div className="flex flex-wrap items-center gap-2"><button type="button" className={complete && !noFormValues && inputsMatch() ? "btn-secondary" : "btn-primary"} disabled={busy || !value.usePolicySourceForAi} onClick={() => void extract()}>{busy ? t('Reading policy…') : t(noFormValues ? 'Retry extraction' : complete ? 'Extract again' : 'Extract from policy')}</button>{busy && <button type="button" className="btn-secondary" onClick={() => { request.current?.abort(); setBusy(false); setProgress(t('Processing cancelled. Extract from policy again to resume from this section.')); }}>{t('Cancel')}</button>}<span className="text-xs text-slate-600 dark:text-slate-300">{t('Extraction does not publish the policy or activate company rules.')}</span></div>
    {progress && <p role="status" className="text-sm">{progress}</p>}{!busy && !complete && !!proposals.length && <p className="text-sm">{t('Processing paused. Extract from policy again to resume; completed sections are kept.')}</p>}{error && <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{error}</p>}{notice && <p role="status" className="text-sm text-emerald-700 dark:text-emerald-300">{notice}</p>}
    {noFormValues && <div role="status" className="rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-950 dark:border-amber-600 dark:bg-amber-950/30 dark:text-amber-100"><p className="font-semibold">{t('No form values prepared')}</p><p className="mt-1">{t('The document was read, but no usable form values were prepared. Your existing settings are unchanged. Retry extraction or inspect the source and gaps below; this is not a completed setup.')}</p></div>}
    {complete && !noFormValues && <p role="status" className="text-sm">{onboardingTaskDrafts.length
      ? t('Policy extraction finished: {tasks} onboarding task drafts and {settings} setup suggestions. Review them in the existing forms; nothing has been saved.', { tasks: onboardingTaskDrafts.length, settings: availableSetupCount })
      : availableSetupCount ? t('Policy extraction finished: {settings} setup suggestions are available. Nothing has been applied.', { settings: availableSetupCount })
        : partials.length ? t('{count} incomplete settings contain extracted values. Complete only the missing fields below; nothing has been applied.', { count: partials.length })
          : t('Reviewed policy values are already copied into your setup draft. Nothing has been applied.')}</p>}
    {(proposals.length > 0 || complete) && <div className="space-y-3">
      {complete && onboardingTaskDrafts.length > 0 && <section aria-label={t('Prepared form values')} className="rounded-lg border border-blue-200 bg-white p-4 dark:border-blue-800 dark:bg-slate-950">
        <h3 className="font-semibold">{t('Prepared form values')}</h3>
        <p className="mt-1 text-sm">{t('{count} task drafts are ready for the existing Onboarding → Checklist Templates form.', { count: onboardingTaskDrafts.length })}</p>
        <p className="mt-2 text-xs text-slate-600 dark:text-slate-300">{t('Review the extracted title, description, category and owner in the existing fields. Missing deadlines stay blank. Nothing is saved until you use the form’s Save action.')}</p>
        <p className="mt-2 text-xs text-slate-600 dark:text-slate-300">{t('Checklist templates are tenant-wide. Policy text does not activate conditional eligibility, probation calculations or employee tasks.')}</p>
        <button type="button" className="btn-primary mt-3" disabled={busy || !profile.companyId || !hasPermission('recruitment.read') && !hasPermission('recruitment.write')} onClick={openOnboardingForm}>{t('Review prefilled forms')}</button>
        {!profile.companyId && <p className="mt-2 text-sm text-amber-800">{t('Select an existing company before opening the form.')}</p>}
        {!hasPermission('recruitment.read') && !hasPermission('recruitment.write') && <p className="mt-2 text-sm">{t('Recruitment access is required to open this form.')}</p>}
      </section>}
      {complete && <PartialPolicyReview entries={partials} onEdit={entry => {
        collected.current.partials = collected.current.partials.map(item => item.documentId === entry.documentId && item.field === entry.field && item.sourceStart === entry.sourceStart ? entry : item);
        setPartials(collected.current.partials); saveCheckpoint(true);
      }} onComplete={async entry => {
        const normalized = entry.field === 'benefitPlans' && Array.isArray(entry.value) ? entry.value.map(item => ({ ...item, gradeCodes: item.gradeCodes ?? [] })) : entry.value;
        const nextProposals = appendPolicyProposals(collected.current.proposals, [{ ...entry, value: normalized, userCompleted: true, contentSha256: entry.sourceHash, sourceName: entry.sourceName ?? '', documentId: entry.documentId }]);
        await Promise.all(nextProposals.map(async proposal => { if (!proposal.reviewKey) proposal.reviewKey = await reviewItemKey(profile.companyId, proposalReview(proposal), proposal.value); }));
        if (profile.companyId !== current.current.profile.companyId || !policySources(current.current.value).some(source => source.documentId === entry.documentId && source.contentSha256 === entry.sourceHash)) return;
        preparedCount.current += nextProposals.filter(proposal => allowed(proposal, profile.companyId) && !blockingReviewItems(reviewItems, {}, proposal).length).length - collected.current.proposals.filter(proposal => allowed(proposal, profile.companyId) && !blockingReviewItems(reviewItems, {}, proposal).length).length;
        setPreparedSetupCount(preparedCount.current);
        collected.current.proposals = nextProposals;
        collected.current.partials = collected.current.partials.filter(item => !(item.documentId === entry.documentId && item.field === entry.field && item.sourceStart === entry.sourceStart));
        setProposals(collected.current.proposals); setPartials(collected.current.partials); saveCheckpoint(true);
      }} />}
      {complete && reviewItems.length > 0 && <details className="rounded-lg border border-slate-300 p-3 dark:border-white/20"><summary className="cursor-pointer text-sm font-semibold">{t('Policy source and gaps')} ({reviewItems.length})</summary><section aria-label={t('Policy source and gaps')} className="mt-2 space-y-2"><p className="text-xs text-slate-600 dark:text-slate-300">{t('Read-only evidence and capability gaps. These notes do not fill forms or require a review questionnaire.')}</p>
        {reviewItems.slice(currentEvidencePage * 5, (currentEvidencePage + 1) * 5).map(item => <details key={item.id} className="rounded border border-slate-200 p-2 text-sm dark:border-white/15"><summary className="cursor-pointer">{item.summary}</summary><p className="mt-2 text-xs text-slate-600 dark:text-slate-300">{item.section} · {t(item.kind === 'conflict' ? 'Conflicting clauses' : item.kind === 'incomplete' ? 'Missing policy information' : 'Documented; not operationally configured')}</p>{item.sources.map((source, index) => <div key={index} className="mt-2 text-xs"><p>{source.sourceName} · {t('Characters')} {source.sourceStart + 1}–{source.sourceStart + source.sourceLength}</p><blockquote className="mt-1 border-s-2 ps-2">{source.sourceQuote}</blockquote></div>)}{item.sourceClassification && item.sourceClassification !== 'Policy' && <p className="mt-2 text-xs text-amber-800 dark:text-amber-200">{item.sourceClassification} · {t('This passage is proposed or awaiting confirmation.')}</p>}</details>)}
        {reviewItems.length > 5 && <nav aria-label={t('Source evidence pages')} className="flex items-center gap-2"><button type="button" className="btn-secondary" disabled={currentEvidencePage === 0} onClick={() => setEvidencePage(currentEvidencePage - 1)}>{t('Previous evidence')}</button><span className="text-xs">{currentEvidencePage + 1} / {Math.ceil(reviewItems.length / 5)}</span><button type="button" className="btn-secondary" disabled={(currentEvidencePage + 1) * 5 >= reviewItems.length} onClick={() => setEvidencePage(currentEvidencePage + 1)}>{t('Next evidence')}</button></nav>}
      </section></details>}
      {!!coverageNotes.length && <details className="rounded-lg border border-slate-300 p-3 dark:border-white/20"><summary className="cursor-pointer text-sm font-semibold">{t('Coverage notes — not verified policy requirements')} ({coverageNotes.length})</summary><p className="mt-2 text-xs text-slate-600 dark:text-slate-300">{t('Repeated coverage messages are combined. Missing topics from other chapters do not block this policy or create configuration changes.')}</p><ul className="mt-2 space-y-1 text-sm">{coverageNotes.slice(currentIssuePage * 50, (currentIssuePage + 1) * 50).map((issue, index) => <li key={currentIssuePage * 50 + index}>{t(issue.section)}: {issue.message}{issue.occurrences > 1 && <span className="ms-2 text-xs">({t('{count} repeated notes', { count: issue.occurrences })})</span>}</li>)}</ul>{coverageNotes.length > 50 && <nav className="mt-3 flex flex-wrap items-center gap-2" aria-label={t('Policy questions pages')}><button type="button" className="btn-secondary" disabled={currentIssuePage === 0} onClick={() => setIssuePage(currentIssuePage - 1)}>{t('Previous questions')}</button><span className="text-xs">{currentIssuePage + 1} / {Math.ceil(coverageNotes.length / 50)}</span><button type="button" className="btn-secondary" disabled={(currentIssuePage + 1) * 50 >= coverageNotes.length} onClick={() => setIssuePage(currentIssuePage + 1)}>{t('Next questions')}</button></nav>}</details>}
      <div className="max-h-80 space-y-2 overflow-auto" tabIndex={0} role="region" aria-label={t('Extracted settings')}>
        {proposals.slice(currentProposalPage * 25, (currentProposalPage + 1) * 25).map((proposal, pageIndex) => { const index = currentProposalPage * 25 + pageIndex; return <div key={`${proposal.target}-${proposal.field}-${index}`} className="rounded-lg border border-slate-200 bg-white p-3 dark:border-white/10 dark:bg-slate-950">
          <label className="flex items-start gap-2 text-sm font-semibold"><input type="checkbox" className="mt-0.5 h-4 w-4 accent-sapphire" disabled={!complete || !allowed(proposal, profile.companyId) || blockingReviewItems(reviewItems, {}, proposal).length > 0} checked={selected.has(index)} onChange={event => setSelected(previous => { const next = new Set(previous); if (event.target.checked) { for (const old of next) if (proposalsConflict(proposals[old], proposal)) next.delete(old); next.add(index); } else next.delete(index); return next; })} />{t(FIELD_NAMES[proposal.field] ?? proposal.field)}</label>
          {proposals.some((other, otherIndex) => otherIndex !== index && proposalsConflict(other, proposal)) && <p className="mt-1 text-xs text-amber-800 dark:text-amber-300">{t('Conflicting policy rules: choose one version for this setting. Selecting this version clears the alternative.')}</p>}
          <div className="mt-2 grid gap-3 text-xs sm:grid-cols-2"><div><p className="font-medium text-slate-600 dark:text-slate-300">{t('Current setting')}</p><div className="mt-1 break-words"><PolicyValue value={(proposal.target === 'configuration' ? value : profile)[proposal.field as never]} /></div></div><div><p className="font-medium text-sapphire dark:text-blue-300">{t('Proposed setting')}</p><div className="mt-1 break-words"><PolicyValue value={proposal.value} /></div></div></div>
          <p className="mt-2 text-xs text-slate-600 dark:text-slate-300">{proposal.sourceName} · {t('Characters')} {proposal.sourceStart + 1}–{proposal.sourceStart + proposal.sourceLength}</p><blockquote className="mt-2 border-s-2 border-sapphire/30 ps-3 text-xs leading-5">{proposal.sourceQuote}</blockquote>
          {!allowed(proposal, profile.companyId) && <p className="mt-2 text-xs text-amber-800 dark:text-amber-300">{t(profile.companyId && proposal.target === 'profile' && ['legalEntityName', 'countryCode', 'currencyCode'].includes(proposal.field) ? 'The selected company identity is protected. Update its company record separately if these details have changed.' : 'This setting needs manual review and cannot be copied automatically.')}</p>}
          {proposal.userCompleted && <p className="mt-2 text-xs text-slate-600 dark:text-slate-300">{t('Completed by you. The source passage supports the extracted values; your additional answers are recorded as manual input.')}</p>}
          {blockingReviewItems(reviewItems, {}, proposal).length > 0 && <p className="mt-2 text-xs text-amber-800 dark:text-amber-200">{t('This setting is held because its policy evidence is unresolved. Correct the source and extract again; other supported settings remain available.')}</p>}
        </div>; })}
      </div>
      {proposals.length > 25 && <nav className="flex flex-wrap items-center gap-2" aria-label={t('Extracted settings pages')}><button type="button" className="btn-secondary" disabled={currentProposalPage === 0} onClick={() => setProposalPage(currentProposalPage - 1)}>{t('Previous settings')}</button><span className="text-xs">{t('{count} settings', { count: proposals.length })} · {currentProposalPage + 1} / {Math.ceil(proposals.length / 25)}</span><button type="button" className="btn-secondary" disabled={(currentProposalPage + 1) * 25 >= proposals.length} onClick={() => setProposalPage(currentProposalPage + 1)}>{t('Next settings')}</button></nav>}
      {proposals.length > 0 && <button type="button" className="btn-primary" disabled={!complete || !selected.size} onClick={accept}>{t('Use selected suggestions ({count})', { count: selected.size })}</button>}
    </div>}
  </div>;
}
