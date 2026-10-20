'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { setupAssistantApi, type PolicyDraftSource, type PolicyReviewDecision, type PolicyReviewDisposition, type PolicyReviewItem, type PolicyReviewRecord, type SetupConfiguration } from '../api/setupAssistant';
import { apiErrorReason } from '../api/client';
import { useT } from '../hooks/useT';
import { currentReviewDecisions, reviewDispositions, validReviewDecision } from '../lib/policyReview';

export const dispositionLabels: Record<PolicyReviewDisposition, string> = {
  Reviewed: 'Reviewed', NeedsClarification: 'Needs clarification', Deferred: 'Defer', ManualProcess: 'Manual process',
  ProductGap: 'Product gap', NotApplicable: 'Not applicable to this policy', Answered: 'Approved answer recorded',
};
type ReviewStore = NonNullable<SetupConfiguration['policyReview']>;

export function PolicyReviewDecisionForm({ item, companyId, value, history, onChange, onSaved, supported = false }: {
  item: PolicyReviewItem; companyId?: string; value?: PolicyReviewDecision; history: PolicyReviewRecord[];
  onChange: (decision: PolicyReviewDecision) => void; onSaved: (record: PolicyReviewRecord) => void; supported?: boolean;
}) {
  const t = useT(); const id = useId();
  const [busy, setBusy] = useState(false); const [error, setError] = useState(''); const [notice, setNotice] = useState('');
  const request = useRef<AbortController | null>(null);
  useEffect(() => () => request.current?.abort(), [item.id, companyId]);
  const previous = history.find(record => record.itemKey === item.id && record.companyId === companyId);
  const decision = value ?? previous ?? { disposition: supported ? 'Reviewed' : '', comment: '', owner: '', approvalReference: '' };
  const update = (patch: Partial<PolicyReviewDecision>) => { setError(''); setNotice(''); onChange({ ...decision, ...patch }); };
  const save = async () => {
    const validation = validReviewDecision(item, decision);
    if (!validation.valid) { setError(t(validation.message!)); return; }
    if (!companyId) { setError(t('Select an existing company before saving a review decision.')); return; }
    const source = item.sources[0];
    const controller = new AbortController(); request.current = controller; setBusy(true); setError(''); setNotice('');
    try {
      const record = await setupAssistantApi.savePolicyReview({ companyId, documentId: source.documentId, sourceHash: source.contentSha256, itemType: item.itemType, itemKey: item.id, summary: item.summary,
        target: item.target, field: item.field, sourceQuote: source.sourceQuote || undefined, sourceStart: source.sourceStart, sourceLength: source.sourceLength,
        disposition: decision.disposition as PolicyReviewDisposition, comment: decision.comment.trim(), owner: decision.owner?.trim() || undefined, approvalReference: decision.approvalReference?.trim() || undefined }, controller.signal);
      if (controller.signal.aborted) return;
      onSaved(record); setNotice(t('Review decision saved. No company setting or workflow was activated.'));
    } catch (cause) { if (!controller.signal.aborted) setError(apiErrorReason(cause, t('The review decision was not saved. Your draft comment is kept; retry.'))); }
    finally { if (!controller.signal.aborted) setBusy(false); }
  };
  return <div className="mt-3 space-y-3">
    {!supported && <label className="block text-sm"><span className="mb-1 block font-medium">{t('Review disposition')}</span><select required className="input w-full" value={decision.disposition} disabled={busy} aria-describedby={error ? `${id}-error` : undefined} onChange={event => update({ disposition: event.target.value as PolicyReviewDecision['disposition'] })}>
      <option value="">{t('Choose a disposition')}</option>{reviewDispositions(item).map(option => <option key={option} value={option}>{t(dispositionLabels[option])}</option>)}
    </select></label>}
    <label className="block text-sm"><span className="mb-1 block font-medium">{t(supported ? 'Proposal comment (optional)' : 'Review comment')}</span><textarea required={!supported && decision.disposition !== 'Reviewed'} className="input min-h-20 w-full" maxLength={4000} value={decision.comment} disabled={busy} aria-describedby={error ? `${id}-error` : undefined} onChange={event => update({ comment: event.target.value })} /></label>
    {!supported && <div className="grid gap-3 sm:grid-cols-2"><label className="block text-sm"><span className="mb-1 block font-medium">{t('Follow-up owner')}</span><input className="input w-full" maxLength={200} value={decision.owner ?? ''} disabled={busy} onChange={event => update({ owner: event.target.value })} /></label>{decision.disposition === 'Answered' && <label className="block text-sm"><span className="mb-1 block font-medium">{t('Approval reference')}</span><input className="input w-full" maxLength={1000} value={decision.approvalReference ?? ''} disabled={busy} onChange={event => update({ approvalReference: event.target.value })} /></label>}</div>}
    {decision.disposition === 'Answered' && <p className="text-xs text-amber-800 dark:text-amber-200">{t('Recording an answer does not change the proposed value. Update the corrected source and extract again before applying an affected setting.')}</p>}
    <div className="flex flex-wrap items-center gap-2"><button type="button" className="btn-secondary" disabled={busy || !validReviewDecision(item, decision).valid || !companyId} onClick={() => void save()}>{t(busy ? 'Saving review…' : 'Save review decision')}</button><span className="text-xs text-slate-600 dark:text-slate-300">{t(companyId ? 'Draft comments are kept locally until you save the decision.' : 'Select an existing company before saving a review decision.')}</span></div>
    {error && <p id={`${id}-error`} role="alert" className="text-sm text-rose-700 dark:text-rose-300">{error}</p>}{notice && <p role="status" className="text-sm text-emerald-800 dark:text-emerald-200">{notice}</p>}
    {previous && <p className="text-xs text-slate-600 dark:text-slate-300">{t('Last saved decision')}: {t(dispositionLabels[previous.disposition])} · {previous.reviewedAtUtc} · {previous.reviewedByUserId}</p>}
  </div>;
}

export function PolicyReviewPanel({ items, companyId, sources, value, onChange }: { items: PolicyReviewItem[]; companyId?: string; sources: PolicyDraftSource[]; value?: SetupConfiguration['policyReview']; onChange: (value: ReviewStore) => void }) {
  const t = useT(); const [page, setPage] = useState(0); const [historyError, setHistoryError] = useState(''); const [historyPage, setHistoryPage] = useState(0);
  const latest = useRef({ value, onChange }); latest.current = { value, onChange };
  const signature = JSON.stringify([companyId, sources]);
  useEffect(() => {
    setPage(0); setHistoryError('');
    if (!companyId) return;
    const controller = new AbortController();
    void Promise.all(sources.map(source => setupAssistantApi.policyReviewHistory(companyId, source.documentId, controller.signal))).then(results => {
      if (controller.signal.aborted) return;
      const store = latest.current.value ?? { drafts: {}, history: [] };
      const history = new Map(store.history.map(record => [record.id, record]));
      for (const result of results) if (Array.isArray(result)) for (const record of result) history.set(record.id, record);
      latest.current.onChange({ ...store, history: [...history.values()].sort((a, b) => b.reviewedAtUtc.localeCompare(a.reviewedAtUtc)) });
    }).catch(cause => { if (!controller.signal.aborted) setHistoryError(apiErrorReason(cause, t('Saved review history could not be loaded. Local draft comments are still available.'))); });
    return () => controller.abort();
  }, [signature]);
  const store = value ?? { drafts: {}, history: [] };
  const current = currentReviewDecisions(store.history, sources, companyId ?? '');
  const currentPage = Math.min(page, Math.max(0, Math.ceil(items.length / 5) - 1));
  const history = store.history.filter(record => record.companyId === companyId);
  return <section aria-label={t('Policy review decisions')} className="space-y-3">
    <h4 className="font-semibold">{t('Policy review decisions')} ({items.length})</h4>
    <p className="text-sm text-slate-600 dark:text-slate-300">{t('Record a decision, comment and follow-up owner for each policy question. This review does not activate unsupported workflows.')}</p>
    {historyError && <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{historyError}</p>}
    {items.slice(currentPage * 5, (currentPage + 1) * 5).map(item => <article key={item.id} aria-label={item.summary} className="rounded-lg border border-amber-300 bg-white p-3 dark:border-amber-600 dark:bg-slate-950">
      <h5 className="font-semibold">{item.summary}</h5><p className="mt-1 text-xs">{item.section} · {t(item.kind === 'conflict' ? 'Conflicting clauses' : item.kind === 'incomplete' ? 'Missing policy information' : item.kind === 'outOfScope' ? 'Outside this policy chapter' : 'Documented; not operationally configured')}</p>
      {item.sourceClassification && item.sourceClassification !== 'Policy' && <p className="mt-1 text-xs text-amber-800 dark:text-amber-200">{item.sourceClassification} · {t('This passage is proposed or awaiting confirmation.')}</p>}
      {item.sources.map((source, index) => <div key={index} className="mt-2"><p className="text-xs text-slate-600 dark:text-slate-300">{source.sourceName} · {t('Characters')} {source.sourceStart + 1}–{source.sourceStart + source.sourceLength} · {t('Version')} {source.contentSha256.slice(0, 12)}</p><blockquote className="mt-1 border-s-2 border-sapphire/30 ps-3 text-sm">{source.sourceQuote}</blockquote></div>)}
      {item.target && ['profile', 'configuration'].includes(item.target) && item.field && <p className="mt-2 text-xs text-amber-800 dark:text-amber-200">{t('Only the affected setting is held for clarification')}: {item.field}</p>}
      <PolicyReviewDecisionForm item={item} companyId={companyId} value={store.drafts[item.id] ?? current[item.id]} history={history} onChange={decision => { const active = latest.current.value ?? { drafts: {}, history: [] }; latest.current.onChange({ ...active, drafts: { ...active.drafts, [item.id]: decision } }); }} onSaved={record => { const active = latest.current.value ?? { drafts: {}, history: [] }; latest.current.onChange({ ...active, history: [record, ...active.history.filter(old => old.id !== record.id)] }); }} />
    </article>)}
    {items.length > 5 && <nav aria-label={t('Policy review pages')} className="flex flex-wrap items-center gap-2"><button type="button" className="btn-secondary" disabled={currentPage === 0} onClick={() => setPage(currentPage - 1)}>{t('Previous reviews')}</button><span className="text-xs">{currentPage + 1} / {Math.ceil(items.length / 5)}</span><button type="button" className="btn-secondary" disabled={(currentPage + 1) * 5 >= items.length} onClick={() => setPage(currentPage + 1)}>{t('Next reviews')}</button></nav>}
    {!!history.length && <details className="rounded-lg border p-3 dark:border-white/15"><summary className="cursor-pointer text-sm font-medium">{t('Review history')} ({history.length})</summary><ul className="mt-2 space-y-2 text-xs">{history.slice(historyPage * 10, (historyPage + 1) * 10).map(record => <li key={record.id} className="border-b pb-2 dark:border-white/15"><p className="font-medium">{record.summary} · {t(dispositionLabels[record.disposition])}</p><p>{record.comment}</p><p>{record.reviewedAtUtc} · {record.reviewedByUserId} · {record.owner}</p>{!sources.some(source => source.documentId === record.documentId && source.contentSha256 === record.sourceHash) && <p>{t('Earlier source version; retained for history only.')}</p>}</li>)}</ul>{history.length > 10 && <div className="mt-2 flex gap-2"><button type="button" className="btn-secondary" disabled={historyPage === 0} onClick={() => setHistoryPage(historyPage - 1)}>{t('Previous history')}</button><button type="button" className="btn-secondary" disabled={(historyPage + 1) * 10 >= history.length} onClick={() => setHistoryPage(historyPage + 1)}>{t('Next history')}</button></div>}</details>}
  </section>;
}
