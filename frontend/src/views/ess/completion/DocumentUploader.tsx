'use client';

import { useId, useRef, useState } from 'react';
import { Loader2, Upload } from 'lucide-react';
import { ACCEPTED_TYPES, DOCUMENT_KINDS, MAX_ATTACHMENTS, MAX_FILE_BYTES, essCompletionApi, isStepUpRequired, problemOf, type DocumentKind, type EssDocument, type SectionKey } from '../../../api/essCompletion';
import { useLocale } from '../../../contexts/LocaleContext';
import { essInput, essPrimaryButton } from '../../../components/ess/EssParts';

const KINDS_BY_SECTION: Record<string, DocumentKind[]> = {
  documents: ['national_id_copy', 'iqama_copy', 'passport_copy', 'other'],
  bank: ['bank_letter'],
  id: ['national_id_copy', 'iqama_copy', 'passport_copy'],
};

/** Attach up to three files to a section. Type, size and count are checked here first; the server checks them again. */
export function DocumentUploader({ section, documents, disabled, getStepUp, onNeedStepUp, onUploaded }: {
  section: SectionKey; documents: EssDocument[]; disabled?: boolean;
  getStepUp: () => string | undefined; onNeedStepUp: (retry: () => void) => void; onUploaded: (doc: EssDocument) => void;
}) {
  const { t } = useLocale();
  const kinds = KINDS_BY_SECTION[section] ?? DOCUMENT_KINDS;
  const [kind, setKind] = useState<DocumentKind>(kinds[0]);
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const kindId = useId(); const fileId = useId(); const errorId = useId();
  const input = useRef<HTMLInputElement>(null);
  const full = documents.length >= MAX_ATTACHMENTS;

  const check = (f: File | null): string | null => {
    if (!f) return null;
    if (f.size > MAX_FILE_BYTES) return t('This file is larger than 5 MB.');
    if (!/\.(pdf|jpe?g|png)$/i.test(f.name)) return t('Choose a PDF, JPG or PNG file.');
    return null;
  };
  const send = async () => {
    if (!file) return;
    setBusy(true); setError(null);
    try {
      const doc = await essCompletionApi.upload(section, kind, file, getStepUp());
      setFile(null); if (input.current) input.current.value = '';
      onUploaded({ ...doc, fileName: file.name });
    } catch (e) {
      if (isStepUpRequired(e)) onNeedStepUp(() => void send());
      else { const p = problemOf(e); setError(t(`ess.completion.error.${p.code ?? 'upload_rejected'}`)); }
    } finally { setBusy(false); }
  };

  return (
    <div className="space-y-3 rounded-xl border border-slate-200 p-3 dark:border-white/10">
      <h3 className="text-sm font-bold text-slate-800 dark:text-slate-100">{t('Attach a copy')}</h3>
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('You can attach up to {count} files here.', { count: MAX_ATTACHMENTS })}</p>
      <div className="grid gap-3 sm:grid-cols-2">
        <label htmlFor={kindId} className="block space-y-1">
          <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Document type')}</span>
          <select id={kindId} className={essInput} value={kind} disabled={disabled || busy || full} onChange={e => setKind(e.target.value as DocumentKind)}>
            {kinds.map(k => <option key={k} value={k}>{t(`ess.completion.doc.${k}`)}</option>)}
          </select>
        </label>
        <label htmlFor={fileId} className="block space-y-1">
          <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('File (PDF, JPG or PNG, up to 5 MB)')}</span>
          <input id={fileId} ref={input} type="file" accept={ACCEPTED_TYPES} disabled={disabled || busy || full} className="block w-full text-sm"
            aria-invalid={error ? true : undefined} aria-describedby={error ? errorId : undefined}
            onChange={e => { const f = e.target.files?.[0] ?? null; setFile(f); setError(check(f)); }} />
        </label>
      </div>
      <div id={errorId} role="alert" className="min-h-[1rem] text-xs font-medium text-rose-700 dark:text-rose-300">{error}</div>
      <button type="button" className={essPrimaryButton} disabled={disabled || busy || !file || !!check(file) || full} onClick={() => void send()}>
        {busy ? <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" /> : <Upload className="h-4 w-4" aria-hidden="true" />}{busy ? t('Uploading…') : t('Upload')}
      </button>
      <div>
        <h4 className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Attached files')}</h4>
        {documents.length === 0 ? <p className="text-xs text-slate-500">{t('No files attached yet.')}</p> : (
          <ul className="mt-1 divide-y divide-slate-100 text-sm dark:divide-white/10">
            {documents.map(d => (
              <li key={d.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
                <span className="min-w-0 break-words"><bdi>{d.fileName ?? t(`ess.completion.doc.${d.kind}`)}</bdi> · {t(`ess.completion.doc.${d.kind}`)}</span>
                <span className="text-xs font-medium">{t(`ess.completion.doc.scan.${d.scanStatus}`)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
