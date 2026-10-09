'use client';

import { useEffect, useRef, useState } from 'react';
import { Loader2, Send, Upload } from 'lucide-react';
import { COMPLETION_FIELDS, employeeCompletionApi, type CompletionKey, type EmployeeCompletion } from '../api/employeeCompletion';
import { essApi, type EssDocument } from '../api/ess';
import { useLocale } from '../contexts/LocaleContext';
import { EssCard, EssField, EssLoadError, EssNotice, EssPageHeader, EssReadOnly, essInput, essPrimaryButton, useCanWriteEss } from '../components/ess/EssParts';

export function MyOnboardingPage() {
  const { t } = useLocale();
  const canWrite = useCanWriteEss();
  const [state, setState] = useState<EmployeeCompletion | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [form, setForm] = useState<Partial<Record<CompletionKey, string>>>({});
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  const [documents, setDocuments] = useState<EssDocument[]>([]);
  const [documentError, setDocumentError] = useState(false);
  const [documentType, setDocumentType] = useState('Passport');
  const [documentNumber, setDocumentNumber] = useState('');
  const [expiryDate, setExpiryDate] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [uploading, setUploading] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);

  const loadDocuments = async () => {
    setDocumentError(false);
    try { setDocuments(await essApi.documents()); } catch { setDocumentError(true); }
  };
  const load = async () => {
    setLoadError(null);
    try {
      const result = await employeeCompletionApi.my();
      setState(result); setForm({ ...result.profile, ...result.changes });
    } catch (e) { setLoadError(e); }
  };
  useEffect(() => { void load(); void loadDocuments(); }, []);

  const submit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault(); setBusy(true); setNotice(null);
    try {
      setState(await employeeCompletionApi.submit(form));
      setNotice({ tone: 'ok', text: t('Your details were sent to HR for review. They do not change your record until approved.') });
    } catch (e) {
      setNotice({ tone: 'error', text: (e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('Your details could not be submitted. Please try again.') });
    } finally { setBusy(false); }
  };
  const upload = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault(); if (!file) return;
    if (file.size > 10 * 1024 * 1024) { setNotice({ tone: 'error', text: t('Choose a file no larger than 10 MB.') }); return; }
    setUploading(true); setNotice(null);
    const body = new FormData(); body.append('file', file); body.append('documentType', documentType);
    if (documentNumber && documentType !== 'Bank proof') body.append('documentNumber', documentNumber);
    if (expiryDate && documentType !== 'Bank proof') body.append('expiryDate', expiryDate);
    try {
      await employeeCompletionApi.upload(body); setFile(null); setDocumentNumber(''); setExpiryDate('');
      if (fileRef.current) fileRef.current.value = '';
      setNotice({ tone: 'ok', text: t('Document uploaded for HR review. Uploading bank proof does not change your payment details.') });
      await loadDocuments();
    } catch (e) {
      setNotice({ tone: 'error', text: (e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('The document could not be uploaded. Please try again.') });
    } finally { setUploading(false); }
  };

  return <div className="space-y-4" data-testid="employee-self-completion">
    <EssPageHeader title={t('My employee details')} subtitle={t('Complete your contact details and share documents securely with HR.')} />
    {notice && <EssNotice tone={notice.tone}>{notice.text}</EssNotice>}
    {loadError ? <EssLoadError error={loadError} onRetry={() => void load()} /> : !state ? <p aria-busy="true">{t('Loading…')}</p> : <>
      <EssCard title={t('Contact details')}>
        {!state.requested ? <p className="text-sm text-slate-500">{t('HR has not requested your details yet. You can still share documents below.')}</p> : <>
          {state.status === 'PendingHR' && <EssNotice tone="ok">{t('Employee details awaiting HR review')}</EssNotice>}
          {state.status === 'Approved' && <EssNotice tone="ok">{t('Employee details approved')}</EssNotice>}
          {state.status === 'Rejected' && <EssNotice tone="error">{t('Please correct your details and send them to HR again.')}{state.reviewNote && <span className="mt-1 block"><bdi>{state.reviewNote}</bdi></span>}</EssNotice>}
          {!canWrite ? <EssReadOnly /> : <form onSubmit={submit} className="mt-3 space-y-4">
            <fieldset disabled={busy || state.status === 'PendingHR' || state.status === 'Approved'} className="grid gap-4 sm:grid-cols-2">
              {COMPLETION_FIELDS.map(field => <EssField key={field.key} label={t(field.label)}>
                {field.key === 'maritalStatus' ? <select className={essInput} value={form[field.key] ?? ''} onChange={e => setForm({ ...form, [field.key]: e.target.value })}><option value="">{t('Select')}</option>{['Single', 'Married', 'Divorced', 'Widowed'].map(v => <option key={v} value={v}>{t(v)}</option>)}</select> : <input className={essInput} type={field.key === 'personalEmail' ? 'email' : field.key.toLowerCase().includes('phone') ? 'tel' : 'text'} maxLength={field.maxLength} value={form[field.key] ?? ''} onChange={e => setForm({ ...form, [field.key]: e.target.value })} />}
              </EssField>)}
            </fieldset>
            {(state.status === 'Open' || state.status === 'Rejected') && <button type="submit" className={essPrimaryButton} disabled={busy || !Object.values(form).some(v => v?.trim())}>{busy ? <Loader2 className="h-4 w-4 motion-safe:animate-spin" /> : <Send className="h-4 w-4" />}{t('Send details to HR')}</button>}
          </form>}
        </>}
      </EssCard>
      <EssCard title={t('Identity and bank documents')}>
        <p className="mb-4 text-sm text-slate-500">{t('Share the documents HR requested. HR verifies official names, identity numbers and payment details before updating your record.')}</p>
        {!canWrite ? <EssReadOnly /> : <form onSubmit={upload} className="grid gap-4 sm:grid-cols-2">
          <EssField label={t('Document type')}><select disabled={uploading} className={essInput} value={documentType} onChange={e => { setDocumentType(e.target.value); setDocumentNumber(''); setExpiryDate(''); }}>{['Passport', 'Identity document', 'Bank proof'].map(v => <option key={v} value={v}>{t(v)}</option>)}</select></EssField>
          {documentType !== 'Bank proof' && <><EssField label={t('Document number')}><input disabled={uploading} className={essInput} value={documentNumber} maxLength={64} onChange={e => setDocumentNumber(e.target.value)} /></EssField><EssField label={t('Expiry date')}><input disabled={uploading} type="date" className={essInput} value={expiryDate} onChange={e => setExpiryDate(e.target.value)} /></EssField></>}
          <EssField label={t('Document file')}><input disabled={uploading} ref={fileRef} type="file" required accept=".pdf,.jpg,.jpeg,.png" onChange={e => setFile(e.target.files?.[0] ?? null)} className="block w-full text-sm" /><span className="text-xs text-slate-500">{t('PDF, JPG or PNG, up to 10 MB.')}</span></EssField>
          <div className="sm:col-span-2"><button type="submit" className={essPrimaryButton} disabled={uploading || !file}>{uploading ? <Loader2 className="h-4 w-4 motion-safe:animate-spin" /> : <Upload className="h-4 w-4" />}{t('Upload for HR review')}</button></div>
        </form>}
        {documentError ? <p role="alert" className="mt-4 text-sm text-red-600">{t('Documents could not be loaded.')} <button type="button" className="underline" onClick={() => void loadDocuments()}>{t('Retry')}</button></p> : <ul className="mt-4 divide-y divide-slate-100 dark:divide-white/10">{documents.map(doc => <li key={doc.id} className="flex flex-wrap justify-between gap-2 py-3 text-sm"><span className="min-w-0 break-words"><bdi>{doc.fileName}</bdi> · {t(doc.documentType)}</span><span>{t(doc.approvalStatus)}</span></li>)}</ul>}
      </EssCard>
    </>}
  </div>;
}
