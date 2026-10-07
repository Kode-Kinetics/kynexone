'use client';

import { useEffect, useState } from 'react';
import { CheckCircle2, Download, Loader2, Send } from 'lucide-react';
import { essDocumentsApi, type EssDocumentRequest, type EssLetterType } from '@/src/api/hrLetters';
import { useLocale } from '@/src/contexts/LocaleContext';
import { useFormat } from '@/src/hooks/useFormat';
import { enumLabel } from '@/src/i18n/enumLabel';
import { StatusChip } from '@/src/components/StatusChip';
import {
  EssCard, EssEmpty, EssField, EssLoadError, EssNotice, EssPageHeader, EssReadOnly, essInput, essPrimaryButton, useCanWriteEss,
} from '@/src/components/ess/EssParts';

/**
 * The employee asks; HR issues. There is no "produce it myself" button here, and there is no
 * endpoint behind one: a salary certificate an employee could mint would not be worth the paper
 * to the bank that asked for it.
 */
export function MyDocumentsPage() {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const canWrite = useCanWriteEss();
  const [types, setTypes] = useState<EssLetterType[] | null>(null);
  const [requests, setRequests] = useState<EssDocumentRequest[] | null>(null);
  // A failed load is shown as one, never as "nothing requested" or "not set up".
  const [typesError, setTypesError] = useState<unknown>(null);
  const [listError, setListError] = useState<unknown>(null);
  const [letterType, setLetterType] = useState('');
  const [language, setLanguage] = useState('bilingual');
  const [purpose, setPurpose] = useState('');
  const [addressee, setAddressee] = useState('');
  const [busy, setBusy] = useState(false);
  const [downloading, setDownloading] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);

  const typeName = (code: string) => {
    const ty = types?.find((x) => x.letterType === code);
    if (!ty) return code;
    return locale === 'ar' && ty.nameAr ? ty.nameAr : ty.nameEn;
  };

  /** Reloads the list. A failure keeps a list already on screen (returns false) instead of wiping it. */
  const refresh = async (): Promise<boolean> => {
    setListError(null);
    try { setRequests(await essDocumentsApi.list()); return true; }
    catch (e) {
      if (requests === null) setListError(e);
      return false;
    }
  };

  const loadTypes = async () => {
    setTypesError(null); setTypes(null);
    try {
      const list = await essDocumentsApi.types();
      setTypes(list);
      if (list.length > 0) setLetterType(list[0].letterType);
    } catch (e) { setTypesError(e); }
  };

  useEffect(() => {
    void loadTypes();
    void refresh();
  }, []);

  const submit = async () => {
    if (!letterType) return;
    setBusy(true); setNotice(null);
    try {
      await essDocumentsApi.create({ letterType, language, purpose, addresseeName: addressee });
      setPurpose(''); setAddressee('');
      const refreshed = await refresh();
      // The request went through either way; say so, and say when the list below is out of date.
      setNotice({ tone: 'ok', text: refreshed
        ? t('Requested. HR will issue it and it will appear below to download.')
        : t('Requested. HR will issue it. The list below could not be refreshed, so reload the page to see it.') });
    } catch (e) {
      const detail = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setNotice({ tone: 'error', text: detail ?? t('The request could not be submitted. Please try again.') });
    } finally {
      setBusy(false);
    }
  };

  const download = async (id: string) => {
    setDownloading(id);
    try { await essDocumentsApi.download(id); }
    catch { setNotice({ tone: 'error', text: t('That document could not be downloaded. Please contact HR.') }); }
    finally { setDownloading(null); }
  };

  return (
    <div className="space-y-4">
      <EssPageHeader title={t('My letters')} subtitle={t('Ask HR for a salary certificate or another letter, and download it once issued.')} />
      {notice && <EssNotice tone={notice.tone}>{notice.text}</EssNotice>}

      <div className="grid gap-4 lg:grid-cols-[minmax(0,22rem)_1fr]">
        <EssCard title={t('Request a letter')} testId="ess-documents-request">
          {typesError ? (
            <EssLoadError error={typesError} onRetry={() => void loadTypes()} />
          ) : types === null ? (
            <p className="flex items-center gap-2 text-sm text-slate-400 dark:text-slate-500">
              <Loader2 className="h-3.5 w-3.5 animate-spin" /> {t('Loading…')}
            </p>
          ) : types.length === 0 ? (
            <EssEmpty text={t('Your organisation has not set up HR letters yet. Raise an HR request instead and someone will help.')} />
          ) : !canWrite ? (
            <EssReadOnly />
          ) : (
            <div className="space-y-3">
              <EssField label={t('Document type')}>
                <select value={letterType} onChange={(e) => setLetterType(e.target.value)} className={essInput}>
                  {types.map((ty) => <option key={ty.letterType} value={ty.letterType}>{locale === 'ar' && ty.nameAr ? ty.nameAr : ty.nameEn}</option>)}
                </select>
              </EssField>
              <EssField label={t('Language')}>
                <select value={language} onChange={(e) => setLanguage(e.target.value)} className={essInput}>
                  <option value="bilingual">{t('Bilingual (English and Arabic)')}</option>
                  <option value="en">{t('English only')}</option>
                  <option value="ar">{t('Arabic only')}</option>
                </select>
              </EssField>
              <EssField label={t('Purpose')}>
                <input value={purpose} onChange={(e) => setPurpose(e.target.value)} placeholder={t('What do you need it for? For example, a bank loan')} className={essInput} />
              </EssField>
              <EssField label={t('Addressed to (optional)')}>
                <input value={addressee} onChange={(e) => setAddressee(e.target.value)} placeholder={t('For example, Riyad Bank')} className={essInput} />
              </EssField>
              <button type="button" onClick={submit} disabled={busy || !letterType} className={`${essPrimaryButton} w-full`}>
                {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
                {busy ? t('Requesting…') : t('Request letter')}
              </button>
            </div>
          )}
        </EssCard>

        <EssCard title={t('Letters I asked for')} testId="ess-documents-list">
          {listError ? (
            <EssLoadError error={listError} onRetry={() => void refresh()} />
          ) : requests === null ? (
            <div className="h-24 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
          ) : requests.length === 0 ? (
            <EssEmpty text={t('You have not asked for any letters yet.')} />
          ) : (
            <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
              {requests.map((r) => (
                <li key={r.id} className="flex items-center justify-between gap-3 py-2.5">
                  <div className="min-w-0">
                    <p className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">{typeName(r.letterType)}</p>
                    <p className="truncate text-xs text-slate-400 dark:text-slate-500">
                      <bdi>{r.referenceNumber ?? fx.date(r.createdAtUtc)}</bdi>
                      {r.status === 'Declined' && r.decisionNote ? <> · <bdi>{r.decisionNote}</bdi></> : null}
                    </p>
                  </div>
                  {r.isIssued ? (
                    <button
                      type="button"
                      onClick={() => download(r.id)}
                      disabled={downloading !== null}
                      className="inline-flex shrink-0 items-center gap-1 rounded-lg border border-slate-200 px-2.5 py-1 text-xs font-semibold text-slate-700 transition hover:bg-slate-50 disabled:opacity-50 dark:border-white/[0.08] dark:text-slate-200 dark:hover:bg-white/[0.04]"
                    >
                      {downloading === r.id ? <Loader2 className="h-3 w-3 animate-spin" /> : <Download className="h-3 w-3" />} {t('Download PDF')}
                    </button>
                  ) : r.status === 'Declined' ? (
                    <StatusChip label={enumLabel(t, 'Status', r.status)} tone="rose" dot />
                  ) : (
                    <StatusChip label={enumLabel(t, 'Status', r.status)} tone="amber" dot />
                  )}
                </li>
              ))}
            </ul>
          )}
          {!listError && requests && requests.some((r) => r.isIssued) && (
            <p className="mt-3 flex items-center gap-1.5 text-xs text-slate-500 dark:text-slate-400">
              <CheckCircle2 className="h-3.5 w-3.5 text-emerald-500" /> {t('Issued letters carry HR’s reference number and can be downloaded again at any time.')}
            </p>
          )}
        </EssCard>
      </div>
    </div>
  );
}
