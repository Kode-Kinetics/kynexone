"use client";

import { useContext, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { SetupPendingFilesState } from './setup/SetupEditorState';
import { FileText } from 'lucide-react';
import { useT } from '../hooks/useT';
import { policyDocumentsApi, type PolicyDocument } from '../api/policyDocuments';
import { apiErrorReason } from '../api/client';
import { useAuth } from '../contexts/AuthContext';
import { SetupPolicyExtraction } from './SetupPolicyExtraction';
import { LocalOnboardingPreparation } from './setup/LocalOnboardingPreparation';
import type { CompanyProfile, PolicyDraftSource, SetupConfiguration } from '../api/setupAssistant';
import { policySources } from '../lib/policyBinder';

const MAX_FILE_BYTES = 20 * 1024 * 1024;
const MAX_TEXT_CHARACTERS = 5_000_000;

export function SetupPolicySource({ value, onChange, onOpenChange, profile, onProfileChange, onReviewOnboardingDraft }: {
  value: SetupConfiguration;
  onChange: (value: SetupConfiguration) => void;
  onOpenChange?: (open: boolean) => void;
  profile?: CompanyProfile;
  onProfileChange?: (patch: Partial<CompanyProfile>) => void;
  /** Embedded onboarding uses the same intake, without the starter-wizard handoff. */
  onReviewOnboardingDraft?: () => void;
}) {
  const t = useT();
  const { hasRole, hasPermission } = useAuth();
  const canUpload = hasPermission('organization.write') && (hasRole('Admin') || hasRole('HR Manager') || hasRole('HR Officer'));
  const [open, setOpen] = useState(!!onReviewOnboardingDraft);
  const [step, setStep] = useState(0);
  const [intake, setIntake] = useState<'document' | 'text'>('document');
  const [files, setFiles] = useState<File[]>([]);
  const reportPendingFiles = useContext(SetupPendingFilesState);
  useLayoutEffect(() => { reportPendingFiles?.(files.length > 0); return () => reportPendingFiles?.(false); }, [files.length, reportPendingFiles]);
  const [text, setText] = useState(value.policyIntakeText ?? value.policySourceText ?? '');
  const [replaceId, setReplaceId] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const [status, setStatus] = useState('');
  const [error, setError] = useState('');
  const [library, setLibrary] = useState<PolicyDocument[] | null>(null);
  const request = useRef<AbortController | null>(null);
  const picker = useRef<HTMLInputElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const panelId = useId();
  const latest = useRef({ value, onChange }); latest.current = { value, onChange };
  const sources = policySources(value);
  const updateText = (next: string) => { setText(next); onChange({ ...value, policyIntakeText: next }); };
  useEffect(() => () => request.current?.abort(), []);
  const show = (next = 0) => { setStep(next); setOpen(true); onOpenChange?.(true); };
  const cancel = () => { request.current?.abort(); setUploading(false); setStatus(t('Upload cancelled. Documents already added are kept.')); };
  const close = () => { if (uploading) cancel(); setOpen(false); onOpenChange?.(false); requestAnimationFrame(() => trigger.current?.focus()); };
  const updateSources = (next: PolicyDraftSource[]) => {
    setStatus(''); setError('');
    const current = latest.current;
    const nextValue = { ...current.value, policySources: next, policyDocumentId: next[0]?.documentId, policySourceHash: next[0]?.contentSha256, policySourceText: undefined, policyExtraction: undefined, localOnboardingPreparation: undefined, usePolicySourceForAi: false };
    latest.current = { ...current, value: nextValue };
    current.onChange(nextValue);
  };
  const addDocument = (doc: PolicyDocument, replacing?: string | null) => {
    const current = policySources(latest.current.value);
    if (!replacing && current.length >= 50) { setError(t('A binder supports up to 50 documents. Replace or remove a source before adding another.')); return; }
    if (current.some(source => source.contentSha256 === doc.contentSha256 && source.documentId !== replacing)) {
      setStatus(t('This document is already in the binder.')); return;
    }
    const source = { documentId: doc.id, contentSha256: doc.contentSha256, name: doc.originalName };
    updateSources(replacing ? current.map(old => old.documentId === replacing ? source : old) : [...current, source]);
  };
  const upload = async () => {
    const pending = intake === 'text' ? [new File([text], 'Pasted policy.txt', { type: 'text/plain' })] : files;
    if (!pending.length || uploading || !canUpload) return;
    if (sources.length + pending.length - (replaceId ? 1 : 0) > 50) { setError(t('A binder supports up to 50 documents. Replace or remove a source before adding another.')); return; }
    if (intake === 'text' && (!text.trim() || text.length > MAX_TEXT_CHARACTERS)) { setError(t('Paste between 1 and 5,000,000 characters.')); return; }
    if (pending.some(file => !/\.(pdf|docx|txt)$/i.test(file.name) || !file.size || file.size > MAX_FILE_BYTES)) { setError(t('Each file must be a PDF, DOCX or TXT between 1 byte and 20 MB.')); return; }
    const controller = new AbortController(); request.current = controller; setUploading(true); setError('');
    let remaining = [...pending];
    try {
      for (const [index, file] of pending.entries()) {
        setStatus(`${t('Uploading')} ${index + 1}/${pending.length}: ${file.name}`);
        const doc = await policyDocumentsApi.upload(file, controller.signal);
        if (controller.signal.aborted) return;
        if (doc.status !== 'Ready' || !doc.contentSha256) throw new Error(doc.errorMessage || t('This document could not be prepared. Check the policy library and retry.'));
        addDocument(doc, index === 0 ? replaceId : undefined);
        remaining = remaining.slice(1); setFiles(remaining); setReplaceId(null);
      }
      setStatus(t('Documents added. Extract from policy, then review the proposals to fill your forms.'));
      if (intake === 'text') { setText(''); latest.current.onChange({ ...latest.current.value, policyIntakeText: undefined }); }
      show(1);
    } catch (cause) { if (!controller.signal.aborted) setError(apiErrorReason(cause, t('Upload failed. Retry the remaining files.'))); }
    finally { if (!controller.signal.aborted) setUploading(false); }
  };
  const loadLibrary = async () => {
    setError('');
    try { setLibrary((await policyDocumentsApi.list()).filter(doc => doc.status === 'Ready' && doc.contentSha256)); }
    catch (cause) { setError(apiErrorReason(cause, t('Could not load the policy library.'))); }
  };

  return <section className="setup-policy-source mb-4 rounded-xl border border-sapphire/30 bg-sapphire/5 p-4 dark:border-blue-400/30" aria-label={t('Existing HR policy')}>
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div className="flex items-start gap-3"><FileText className="mt-1 h-5 w-5 shrink-0 text-sapphire" aria-hidden="true" /><div><h3 className="font-semibold">{t(onReviewOnboardingDraft ? 'Onboarding policy documents' : 'Start from your existing HR policy')}</h3><p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{sources.length ? t('{count} policy documents in this draft. Extract and review to fill your forms.', { count: sources.length }) : t('Upload a whole HR binder or add its chapters as separate documents.')}</p></div></div>
      {!onReviewOnboardingDraft && <div className="flex flex-wrap gap-2"><button ref={trigger} type="button" className="btn-primary" aria-expanded={open} aria-controls={panelId} onClick={() => open ? close() : show()}>{t(open ? 'Close guide' : sources.length ? 'Review policy text' : 'Add your HR policy')}</button>{!open && <button type="button" className="btn-secondary" onClick={() => show(2)}>{t('How this works')}</button>}</div>}
    </div>
    {open && <div id={panelId} className="setup-policy-source-panel mt-4 space-y-3 border-t border-sapphire/20 pt-4">
      <nav className="flex flex-wrap gap-2" aria-label={t('Policy setup guide')}>{['Policy documents', 'Prepare form values', 'How to review and save'].map((label, index) => <button key={label} type="button" className={step === index ? 'btn-primary' : 'btn-secondary'} aria-current={step === index ? 'step' : undefined} onClick={() => setStep(index)}>{index + 1}. {t(label)}</button>)}</nav>
      <div hidden={step !== 0} className="space-y-3">
        {sources.length > 0 && <p className="text-sm">{t('Add, replace or remove draft sources here. Reviewed form values are preserved; replacing a source requires a new extraction and review.')}</p>}
        <p className="text-xs text-slate-600 dark:text-slate-300">{t('Up to 50 documents can be combined in one policy binder.')}</p>
        <ul className="space-y-2" aria-label={t('Policy binder documents')}>{sources.map(source => <li key={source.documentId} className="flex flex-wrap items-center justify-between gap-2 rounded-lg border bg-white p-3 dark:bg-slate-950"><span className="min-w-0 break-words text-sm">{source.name}</span><div className="flex gap-2"><button type="button" className="btn-secondary" disabled={uploading} onClick={() => { setReplaceId(source.documentId); setIntake('document'); setFiles([]); picker.current?.click(); }}>{t('Replace')}<span className="sr-only"> {source.name}</span></button><button type="button" className="btn-secondary" disabled={uploading} onClick={() => { updateSources(sources.filter(item => item.documentId !== source.documentId)); setStatus(t('Source removed from this draft. The library document and reviewed form values are kept.')); }}>{t('Remove')}<span className="sr-only"> {source.name}</span></button></div></li>)}</ul>
        {canUpload ? <>
          <div className="flex flex-wrap gap-2"><button type="button" className={intake === 'document' ? 'btn-primary' : 'btn-secondary'} disabled={uploading} onClick={() => setIntake('document')}>{t('Upload document')}</button><button type="button" className={intake === 'text' ? 'btn-primary' : 'btn-secondary'} disabled={uploading} onClick={() => { setIntake('text'); setReplaceId(null); }}>{t('Paste policy text')}</button><button type="button" className="btn-secondary" disabled={uploading} onClick={() => void loadLibrary()}>{t('Choose from policy library')}</button></div>
          {library && <div className="max-h-48 overflow-auto rounded-lg border bg-white p-3 dark:bg-slate-950"><p className="mb-2 text-sm font-medium">{t('Existing policy documents')}</p>{!library.length && <p className="text-sm">{t('No ready documents available.')}</p>}{library.map(doc => <div className="flex items-center justify-between gap-2 py-1" key={doc.id}><span className="break-words text-sm">{doc.originalName}</span><button type="button" className="btn-secondary" disabled={uploading || sources.some(source => source.contentSha256 === doc.contentSha256)} onClick={() => addDocument(doc)}>{t('Add to binder')}</button></div>)}</div>}
          {intake === 'document' ? <div className="space-y-2"><label className="block text-sm font-medium">{t('Upload a document for extraction')}<input ref={picker} type="file" accept=".pdf,.docx,.txt" multiple={!replaceId} disabled={uploading} className="mt-2 block w-full text-sm" onChange={event => { setFiles(Array.from(event.target.files ?? [])); setError(''); }} /></label>{replaceId && <p className="text-sm">{t('Choose a replacement document, then upload it.')} <button type="button" className="underline" onClick={() => { setReplaceId(null); setFiles([]); }}>{t('Cancel replacement')}</button></p>}<p className="text-xs">{t('PDF, DOCX or TXT: 20 MB and 5,000,000 extracted characters per file; PDFs up to 2,000 pages. Add multiple files to one binder. AI processes sections automatically.')}</p></div> : <div className="space-y-1"><div className="flex flex-wrap items-baseline justify-between gap-2"><label className="block text-sm font-medium" htmlFor={`${panelId}-text`}>{t('Policy excerpts')}</label><p className="text-xs">{t('{count} of {limit} characters', { count: text.length, limit: MAX_TEXT_CHARACTERS })}</p></div><textarea id={`${panelId}-text`} className="setup-policy-textarea input h-40 w-full" maxLength={MAX_TEXT_CHARACTERS} value={text} disabled={uploading} onChange={event => updateText(event.target.value)} /><p className="text-xs">{t('Pasted text becomes a private document in the same binder and uses the same extraction and review process.')}</p></div>}
        </> : <p className="text-sm">{t('An authorized HR administrator can add policy documents. You can review the draft sources already attached.')}</p>}
      </div>
      <div hidden={step !== 1}>{sources.length > 0 && profile && onProfileChange ? onReviewOnboardingDraft
        ? <LocalOnboardingPreparation value={value} onChange={onChange} profile={profile} onProfileChange={onProfileChange} onReviewOnboardingDraft={onReviewOnboardingDraft} />
        : <SetupPolicyExtraction value={value} onChange={onChange} profile={profile} onProfileChange={onProfileChange} />
        : <p className="text-sm">{t('Add a policy document first, then extract its settings into draft forms.')}</p>}</div>
      <div hidden={step !== 2} className="space-y-2 text-sm"><p>{t(onReviewOnboardingDraft ? 'Local preparation runs automatically after upload. Review local form values, or optionally improve the suggestions with AI.' : 'Extract from policy to read every document. Review each proposed value beside the current form value and its supporting policy passage. Choose which suggestions to copy into your forms.')}</p><p>{t(onReviewOnboardingDraft ? 'Choose Review prefilled forms to review onboarding tasks here. Only the existing task Save action changes a checklist. Other policy topics are not applied in this area.' : 'For onboarding tasks, choose Review prefilled forms, review each task in the existing form, then use its Save action. For setup settings, review your draft before choosing Apply.')}</p><p>{t('Return to Policy documents at any time to add or replace a chapter. Closing this guide keeps the draft sources and reviewed form values.')}</p></div>
      {status && step === 0 && <p role="status" className="text-sm">{status}</p>}{error && <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{error}</p>}
      <div className="setup-policy-guide-footer flex flex-wrap justify-between gap-2 border-t border-sapphire/20 pt-3">{step > 0 ? <button type="button" className="btn-secondary" onClick={() => setStep(step - 1)}>{t('Previous guide step')}</button> : canUpload ? <div className="flex flex-wrap gap-2"><button type="button" className="btn-primary" disabled={uploading || (intake === 'document' ? !files.length : !text.trim())} onClick={() => void upload()}>{t(uploading ? 'Uploading…' : 'Upload and review')}</button>{uploading && <button type="button" className="btn-secondary" onClick={cancel}>{t('Cancel upload')}</button>}</div> : <span />}{step < 2 ? <button type="button" className="btn-secondary" disabled={uploading || !sources.length} onClick={() => setStep(step + 1)}>{t(step === 0 ? 'Next: prepare form values' : 'How to review and save')}</button> : <button type="button" className="btn-primary" onClick={() => onReviewOnboardingDraft ? show(0) : close()}>{t(onReviewOnboardingDraft ? 'Policy documents' : 'Continue company setup')}</button>}</div>
    </div>}
  </section>;
}
