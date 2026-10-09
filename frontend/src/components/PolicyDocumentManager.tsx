'use client';

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { FileText } from 'lucide-react';
import { policyDocumentsApi, type PolicyDocument, type PolicyAskResponse } from '../api/policyDocuments';
import { companiesApi, type CompanyDto } from '../api/organization';
import { useAuth } from '../contexts/AuthContext';
import { useT } from '../hooks/useT';
import { useFormat } from '../hooks/useFormat';
import { Modal } from './Modal';
import { PolicyAnswer } from './PolicyAnswer';

export function PolicyDocumentManager() {
  const id = useId();
  const t = useT(); const fmt = useFormat(); const { hasRole, hasPermission } = useAuth();
  const canAuthor = hasPermission('organization.write') && (hasRole('Admin') || hasRole('HR Manager') || hasRole('HR Officer'));
  const canPublish = hasPermission('organization.write') && (hasRole('Admin') || hasRole('HR Manager'));
  const [documents, setDocuments] = useState<PolicyDocument[]>([]);
  const [companies, setCompanies] = useState<CompanyDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [page, setPage] = useState(0);
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [review, setReview] = useState<PolicyDocument | null>(null);
  const [text, setText] = useState('');
  const [textLoading, setTextLoading] = useState(false);
  const [companyId, setCompanyId] = useState('');
  const [from, setFrom] = useState(''); const [to, setTo] = useState('');
  const [confirmation, setConfirmation] = useState<{ document: PolicyDocument; action: 'delete' | 'withdraw' } | null>(null);
  const [question, setQuestion] = useState(''); const [asking, setAsking] = useState(false);
  const [answer, setAnswer] = useState<PolicyAskResponse | null>(null);
  const [askError, setAskError] = useState('');
  const generation = useRef(0);
  const filePicker = useRef<HTMLInputElement>(null);
  const uploadRequest = useRef<AbortController | null>(null);
  const askRequest = useRef<AbortController | null>(null);
  useEffect(() => () => { generation.current++; uploadRequest.current?.abort(); askRequest.current?.abort(); }, []);
  const load = useCallback(async () => {
    const token = ++generation.current; setLoading(true); setError('');
    try { const docs = await policyDocumentsApi.list(); if (token === generation.current) { setDocuments(docs); setPage(0); } }
    catch { if (token === generation.current) setError(t('Could not load the policy library. Try Refresh.')); }
    finally { if (token === generation.current) setLoading(false); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    if (!canPublish) return;
    let active = true;
    companiesApi.listAll().then(items => { if (active) setCompanies(items); }).catch(() => { if (active) setError(t('Could not load companies. Refresh before publishing.')); });
    return () => { active = false; };
  }, [canPublish, t]);
  useEffect(() => {
    if (!review) return;
    const controller = new AbortController(); setText(''); setTextLoading(true); setError('');
    setCompanyId(review.companyId ?? '');
    setFrom(review.effectiveFromUtc?.slice(0, 10) ?? ''); setTo(review.effectiveToUtc?.slice(0, 10) ?? '');
    policyDocumentsApi.text(review.id, controller.signal).then(result => { if (!controller.signal.aborted) setText(result.text); })
      .catch(() => { if (!controller.signal.aborted) setError(t('Could not read the document. Close and retry.')); })
      .finally(() => { if (!controller.signal.aborted) setTextLoading(false); });
    return () => controller.abort();
  }, [review, t]);
  const upload = async () => {
    if (!selectedFile || busy || !canAuthor) return;
    if (!/\.(pdf|docx|txt)$/i.test(selectedFile.name) || !selectedFile.size || selectedFile.size > 20 * 1024 * 1024) {
      setError(t('Choose a PDF, DOCX or TXT file between 1 byte and 20 MB.')); return;
    }
    const controller = new AbortController(); uploadRequest.current = controller;
    setBusy(true); setError(''); setNotice('');
    try {
      const doc = await policyDocumentsApi.upload(selectedFile, controller.signal);
      if (!controller.signal.aborted) { setDocuments(previous => [doc, ...previous.filter(item => item.id !== doc.id)]); setSelectedFile(null); setPage(0); setNotice(t('Document saved as a draft. Employees cannot access it until publication.')); }
    } catch { if (!controller.signal.aborted) setError(t('Document upload failed. Your file is kept; try again.')); }
    finally { if (!controller.signal.aborted) setBusy(false); uploadRequest.current = null; }
  };
  const publish = async () => {
    if (!review || busy || !companyId || !from || !text || !canPublish) return;
    if (to && to < from) { setError(t('The end date must be on or after the start date.')); return; }
    setBusy(true); setError('');
    try {
      const doc = await policyDocumentsApi.publish(review.id, { companyId, contentSha256: review.contentSha256, effectiveFromUtc: `${from}T00:00:00Z`, effectiveToUtc: to ? `${to}T23:59:59Z` : null });
      setDocuments(previous => previous.map(item => item.id === doc.id ? doc : item)); setReview(null); setNotice(t('Policy published for the selected company and effective dates.'));
    } catch { setError(t('Could not publish. Check your access and refresh the document before trying again.')); }
    finally { setBusy(false); }
  };
  const confirmAction = async () => {
    if (!confirmation || busy) return;
    setBusy(true); setError('');
    try {
      if (confirmation.action === 'delete') {
        await policyDocumentsApi.delete(confirmation.document.id);
        setDocuments(previous => previous.filter(item => item.id !== confirmation.document.id));
        setNotice(t('Document deleted.'));
      } else {
        const doc = await policyDocumentsApi.withdraw(confirmation.document.id);
        setDocuments(previous => previous.map(item => item.id === doc.id ? doc : item));
        setNotice(t('Policy withdrawn from employee answers.'));
      }
      setPage(0); setConfirmation(null);
    } catch { setError(t('The document could not be changed. Refresh and try again.')); }
    finally { setBusy(false); }
  };
  const ask = async () => {
    if (!question.trim() || asking) return;
    const controller = new AbortController(); askRequest.current = controller;
    setAsking(true); setAskError(''); setAnswer(null);
    try { const result = await policyDocumentsApi.ask(question.trim(), controller.signal); if (!controller.signal.aborted) setAnswer(result); }
    catch { if (!controller.signal.aborted) setAskError(t('Could not answer from the policy library. Your question is kept.')); }
    finally { if (!controller.signal.aborted) setAsking(false); }
  };
  const visible = documents.slice(page * 10, (page + 1) * 10);
  return <div className="space-y-4">
    <div><h2 className="text-lg font-semibold">{t('Policy library')}</h2><p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{t('Review documents, publish the approved version, and let employees ask Kody about their company policies.')}</p></div>
    {error && <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{error}</p>}
    {notice && <p role="status" className="text-sm text-emerald-700 dark:text-emerald-300">{notice}</p>}
    {canAuthor && <div className="flex flex-wrap items-end gap-3 rounded-xl border border-dashed border-slate-300 p-4 dark:border-white/20">
      <label className="min-w-0 flex-1 text-sm font-medium">{t('Add a policy document')}<input ref={filePicker} type="file" accept=".pdf,.docx,.txt" disabled={busy} className="hidden" onChange={event => { setSelectedFile(event.target.files?.[0] ?? null); setError(''); }} /><span className="mt-2 block text-xs font-normal text-slate-600 dark:text-slate-400">{t('PDF, DOCX or TXT, up to 20 MB. Uploads remain private drafts until published.')}</span></label>
      <button type="button" className="btn-secondary" disabled={busy} onClick={() => filePicker.current?.click()}>{t('Choose file')}</button><span className="break-words text-xs">{selectedFile?.name || t('No file selected')}</span>
      <button type="button" className="btn-primary" disabled={busy || !selectedFile} onClick={() => void upload()}>{busy ? t('Working…') : t('Upload draft')}</button>
      {busy && uploadRequest.current && <button type="button" className="btn-secondary" onClick={() => { uploadRequest.current?.abort(); uploadRequest.current = null; setBusy(false); }}>{t('Cancel upload')}</button>}
    </div>}
    {documents.length >= 100 && <p className="text-xs text-slate-600 dark:text-slate-300">{t('Only the latest 100 documents are shown.')}</p>}
    <section className="rounded-xl border border-slate-200 dark:border-white/10" aria-label={t('Policy documents')}>
      <div className="flex items-center justify-between border-b border-slate-200 p-3 dark:border-white/10"><span className="text-sm">{t('Most recent {count} documents', { count: documents.length })}</span><button type="button" className="btn-secondary" disabled={loading || busy} onClick={() => void load()}>{t('Refresh')}</button></div>
      {loading ? <p role="status" className="p-4 text-sm">{t('Loading…')}</p> : documents.length === 0 ? <p className="p-5 text-sm text-slate-600 dark:text-slate-300">{t('No policy documents yet. Add an approved policy to start your library.')}</p> : <ul className="divide-y divide-slate-200 dark:divide-white/10">{visible.map(doc => <li key={doc.id} className="flex flex-wrap items-center gap-3 p-3">
        <FileText className="h-5 w-5 text-sapphire" aria-hidden="true" />
        <div className="min-w-0 flex-1"><p className="break-words text-sm font-semibold">{doc.originalName}</p><p className="mt-1 text-xs text-slate-600 dark:text-slate-400">{t(doc.status)} · {t(doc.publicationStatus || 'Draft')} · {fmt.date(doc.createdAtUtc)}</p>{doc.errorMessage && <p className="text-xs text-rose-700 dark:text-rose-300">{doc.errorMessage}</p>}</div>
        {canAuthor && doc.status === 'Ready' && <button type="button" className="btn-secondary" onClick={() => setReview(doc)}>{t('Review document')}</button>}
        {canPublish && doc.publicationStatus === 'Published' && <button type="button" className="btn-secondary" disabled={busy} onClick={() => setConfirmation({ document: doc, action: 'withdraw' })}>{t('Withdraw')}</button>}
        {canPublish && (!doc.publicationStatus || doc.publicationStatus === 'Draft') && <button type="button" className="btn-secondary text-rose-700 dark:text-rose-300" disabled={busy} onClick={() => setConfirmation({ document: doc, action: 'delete' })}>{t('Delete')}</button>}
      </li>)}</ul>}
      {documents.length > 10 && <div className="flex items-center justify-between border-t p-3 text-sm dark:border-white/10"><button type="button" className="btn-secondary" disabled={page === 0} onClick={() => setPage(page - 1)}>{t('Previous')}</button><span>{t('Page {page} of {pages}', { page: page + 1, pages: Math.ceil(documents.length / 10) })}</span><button type="button" className="btn-secondary" disabled={(page + 1) * 10 >= documents.length} onClick={() => setPage(page + 1)}>{t('Next')}</button></div>}
    </section>
    <section className="space-y-3 rounded-xl border border-slate-200 p-4 dark:border-white/10" aria-label={t('Policy answer preview')}>
      <h3 className="font-semibold">{t('Policy answer preview')}</h3><p className="text-xs text-slate-600 dark:text-slate-300">{t('HR preview can use accessible drafts. Employee Kody answers use only published, effective policies for their company.')}</p>
      <form noValidate className="flex flex-wrap gap-2" onSubmit={event => { event.preventDefault(); void ask(); }}><label className="min-w-0 flex-1"><span className="sr-only">{t('Ask about your policy')}</span><input className="input w-full" maxLength={2000} value={question} onChange={event => setQuestion(event.target.value)} placeholder={t('Ask about your policy')} /></label><button type="submit" className="btn-primary" disabled={asking || !question.trim()}>{asking ? t('Checking policies…') : t('Ask Kody')}</button></form>
      {askError && <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{askError}</p>}{answer && <PolicyAnswer answer={answer} />}
    </section>
    <Modal isOpen={!!review} onClose={() => { if (!busy) setReview(null); }} title={t('Review document')} size="lg" footer={<><button type="button" className="btn-secondary" disabled={busy} onClick={() => setReview(null)}>{t('Close')}</button>{canPublish && <button type="button" className="btn-primary" disabled={busy || textLoading || !text || !companyId || !from} onClick={() => void publish()}>{busy ? t('Working…') : t('Publish policy')}</button>}</>}>
      <div className="space-y-4"><p className="font-medium">{review?.originalName}</p>{error && <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{error}</p>}
      <div tabIndex={0} role="region" aria-label={t('Document text')} className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded-lg border border-slate-200 p-3 text-sm dark:border-white/10">{textLoading ? t('Loading…') : text}</div>
      {canPublish && <><p className="text-sm text-slate-600 dark:text-slate-300">{t('Publishing makes this version available to employees of the selected company during its effective dates. Dates use UTC.')}</p><p className="text-sm font-medium">{t('Every employee in this company can ask about the whole document. Remove confidential salary bands or individual information before publishing.')}</p><div className="grid gap-3 sm:grid-cols-3">
        <label htmlFor={`${id}-company`} className="text-sm">{t('Company')}<select id={`${id}-company`} className="input mt-1 w-full" value={companyId} onChange={event => setCompanyId(event.target.value)}><option value="">{t('Select company')}</option>{companies.map(company => <option key={company.id} value={company.id}>{company.legalNameEn}</option>)}</select></label>
        <label className="text-sm">{t('Effective from')}<input type="date" className="input mt-1 w-full" value={from} onChange={event => setFrom(event.target.value)} /></label>
        <label className="text-sm">{t('Effective to (optional)')}<input type="date" className="input mt-1 w-full" value={to} min={from} onChange={event => setTo(event.target.value)} /></label>
      </div></>}
      </div>
    </Modal>
    <Modal isOpen={!!confirmation} onClose={() => { if (!busy) setConfirmation(null); }} title={confirmation?.action === 'delete' ? t('Delete document') : t('Withdraw policy')} size="sm" footer={<><button type="button" className="btn-secondary" disabled={busy} onClick={() => setConfirmation(null)}>{t('Cancel')}</button><button type="button" className="btn-primary bg-rose-700" disabled={busy} onClick={() => void confirmAction()}>{busy ? t('Working…') : confirmation?.action === 'delete' ? t('Delete document') : t('Withdraw policy')}</button></>}>
      <p className="break-words font-medium">{confirmation?.document.originalName}</p><p className="mt-2 text-sm">{confirmation?.action === 'delete' ? t('This removes the draft from the library. Source text is retained for audit.') : t('This removes the policy from future employee answers. The document stays in the library.')}</p>{error && <p role="alert" className="mt-2 text-sm text-rose-700 dark:text-rose-300">{error}</p>}
    </Modal>
  </div>;
}
