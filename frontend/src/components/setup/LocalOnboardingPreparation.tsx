'use client';

import { useEffect, useRef, useState } from 'react';
import { setupAssistantApi, type CompanyProfile, type PolicyExtractionResult, type SetupConfiguration } from '../../api/setupAssistant';
import { apiErrorReason } from '../../api/client';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { useT } from '../../hooks/useT';
import { policySources } from '../../lib/policyBinder';
import { savePolicyFormDraft } from '../../lib/policyFormDrafts';
import { SetupPolicyExtraction } from '../SetupPolicyExtraction';

/** Local deterministic preparation is independent from optional AI and its checkpoints. */
export function LocalOnboardingPreparation({ value, onChange, profile, onProfileChange, onReviewOnboardingDraft }: {
  value: SetupConfiguration; onChange: (value: SetupConfiguration) => void;
  profile: CompanyProfile; onProfileChange: (patch: Partial<CompanyProfile>) => void;
  onReviewOnboardingDraft: () => void;
}) {
  const t = useT();
  const { user, hasPermission } = useAuth();
  const { companies, accountType } = useCompany();
  const sources = policySources(value);
  const signature = JSON.stringify(sources);
  const latest = useRef({ value, onChange, signature }); latest.current = { value, onChange, signature };
  const [results, setResults] = useState<PolicyExtractionResult[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  const [showAi, setShowAi] = useState(false);
  const [readySignature, setReadySignature] = useState('');
  useEffect(() => {
    const controller = new AbortController();
    setResults([]); setReadySignature(''); setError(''); setShowAi(false);
    const saved = latest.current.value.localOnboardingPreparation;
    const currentSources = policySources(latest.current.value);
    if (!currentSources.length) { setBusy(false); return; }
    if (!retry && saved?.sourceSignature === signature && saved.results.length === currentSources.length) {
      setResults(saved.results); setReadySignature(signature); setBusy(false); return;
    }
    setBusy(true);
    void (async () => {
      try {
        const prepared: PolicyExtractionResult[] = [];
        for (const source of currentSources) {
          const result = await setupAssistantApi.prepareOnboarding(source.documentId, profile, controller.signal);
          if (controller.signal.aborted || latest.current.signature !== signature) return;
          if (result.documentId !== source.documentId || result.sourceHash !== source.contentSha256) throw new Error('source_changed');
          prepared.push(result);
        }
        if (prepared.reduce((count, result) => count + (result.onboardingTaskDrafts?.length ?? 0), 0) > 1000) throw new Error('too_many_tasks');
        setResults(prepared); setReadySignature(signature);
        latest.current.onChange({ ...latest.current.value, localOnboardingPreparation: { sourceSignature: signature, results: prepared } });
      } catch (cause) {
        if (!controller.signal.aborted) setError(cause instanceof Error && cause.message === 'source_changed'
          ? t('The policy version changed. Upload or select the current document again.')
          : cause instanceof Error && cause.message === 'too_many_tasks'
            ? t('Too many task drafts. Prepare fewer documents together; no tasks have been saved.')
            : apiErrorReason(cause, t('Local preparation failed. Your document is retained. Retry or enter tasks manually.')));
      } finally { if (!controller.signal.aborted) setBusy(false); }
    })();
    return () => controller.abort();
  }, [signature, retry, profile.companyId]);
  const tasks = results.flatMap(result => {
    const source = sources.find(source => source.documentId === result.documentId && source.contentSha256 === result.sourceHash);
    return source ? (result.onboardingTaskDrafts ?? []).map(task => ({ ...task, documentId: source.documentId, contentSha256: source.contentSha256, sourceName: source.name })) : [];
  });
  const review = () => {
    const company = companies.find(company => company.id === profile.companyId && company.isActive);
    if (!user || !company || busy || readySignature !== signature || !tasks.length) return;
    const bundle = { tenantId: user.tenantId, userId: user.id, companyId: company.id, companyName: company.name, accountType, sources, tasks };
    try {
      try { savePolicyFormDraft(localStorage, bundle); }
      catch (cause) {
        if (!(cause instanceof Error) || !cause.message.startsWith('Existing policy form drafts')) throw cause;
        if (!window.confirm(t('Your existing policy form draft has unsaved edits. Replace it with the newly extracted values? Saved checklist tasks will not change.'))) return;
        savePolicyFormDraft(localStorage, bundle, { replace: true });
      }
      onReviewOnboardingDraft();
    } catch { setError(t('The form draft could not be saved in this browser. Your setup values are unchanged.')); }
  };
  return <div className="space-y-3">
    <p className="text-sm">{t('Document text is read on the application server. Supported onboarding fields are prepared without AI; nothing is sent to an AI provider.')}</p>
    {busy && <p role="status" className="text-sm">{t('Preparing form values locally…')}</p>}
    {error && <div role="alert" className="text-sm text-rose-700 dark:text-rose-300">{error}<button className="btn-secondary ms-2" type="button" onClick={() => setRetry(count => count + 1)}>{t('Retry local preparation')}</button></div>}
    {!busy && readySignature === signature && <section aria-label={t('Locally prepared form values')} className="space-y-3 rounded-lg border border-blue-200 bg-white p-4 dark:border-blue-800 dark:bg-slate-950">
      <h3 className="font-semibold">{t('Document parsed successfully')}</h3>
      <p className="text-sm">{tasks.length ? t('{count} local task drafts are ready for review.', { count: tasks.length }) : t('No supported onboarding tasks were mapped locally. Enter tasks manually or optionally use AI; no policy values have been guessed.')}</p>
      <p className="text-xs text-slate-600 dark:text-slate-300">{t('Local mapping is limited to supported clauses. Review every value and its source; unclear deadlines and requirements stay blank.')}</p>
      {tasks.length > 0 && <button type="button" className="btn-primary" onClick={review} disabled={!hasPermission('recruitment.read') && !hasPermission('recruitment.write')}>{t('Review local form values')}</button>}
      {results.some(result => result.issues.length) && <details><summary className="cursor-pointer text-sm">{t('Local mapping notes')}</summary><ul className="list-disc ps-5 text-sm">{results.flatMap(result => result.issues.map((issue, index) => <li key={`${result.documentId}:${index}`}>{issue.message}</li>))}</ul></details>}
    </section>}
    <button type="button" className="btn-secondary" disabled={busy} aria-expanded={showAi} onClick={() => setShowAi(open => !open)}>{t('Improve with AI (optional)')}</button>
    {showAi && <div className="space-y-3 rounded-lg border border-slate-200 p-3 dark:border-white/15">
      <p className="text-sm">{t('AI can suggest additional mappings. Local results remain available if AI fails. Consent is required before sending document text.')}</p>
      <SetupPolicyExtraction value={value} onChange={onChange} profile={profile} onProfileChange={onProfileChange} onReviewOnboardingDraft={onReviewOnboardingDraft} />
    </div>}
  </div>;
}
