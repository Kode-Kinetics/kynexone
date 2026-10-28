'use client';

import Link from 'next/link';
import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { ArrowLeft, CheckCircle2, Download, Loader2, PenLine } from 'lucide-react';
import { esignApi, deviceId, openBlob, problemOf, sha256Hex, type ConsentView, type SignReceipt, type VerifyResult } from '../../../api/esign';
import { useLocale } from '../../../contexts/LocaleContext';
import { EssCard, EssLoadError, EssNotice, EssPageHeader, essInput, essPrimaryButton, useCanWriteEss } from '../../../components/ess/EssParts';
import { ConsentText, NotQualifiedBadge, PasswordDialog, PdfPreview } from './SignParts';

/** Where a refused signature is explained: some codes ask the person to reload, the rest are shown beside the form. */
const RELOAD_CODES = new Set(['document_changed', 'consent_version_stale']);

/**
 * Read a document, type your name, confirm with your password. Everything that counts as evidence is fixed by the server
 * (the time, the file's hash, the wording you agreed to); this page only shows what the employee is agreeing to.
 */
export function SignDocumentPage({ documentId }: { documentId: string }) {
  const { t, locale } = useLocale();
  const canWrite = useCanWriteEss();
  const [consent, setConsent] = useState<ConsentView | null>(null);
  const [blob, setBlob] = useState<Blob | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [read, setRead] = useState(false);
  const [name, setName] = useState('');
  const [fieldError, setFieldError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [stepUpOpen, setStepUpOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [receipt, setReceipt] = useState<SignReceipt | null>(null);
  const [verified, setVerified] = useState<VerifyResult | 'failed' | null>(null);
  const nameId = useId(); const nameErrorId = useId(); const readId = useId();
  const nameRef = useRef<HTMLInputElement>(null);
  const idemKey = useRef(`${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`);
  const signButtonRef = useRef<HTMLButtonElement>(null);

  const load = useCallback(async () => {
    setLoadError(null); setConsent(null); setBlob(null);
    try {
      const [c, file] = await Promise.all([esignApi.consent(documentId, locale === 'ar' ? 'ar' : 'en'), esignApi.document(documentId)]);
      setConsent(c); setBlob(file);
    } catch (e) { setLoadError(e); }
  }, [documentId, locale]);
  useEffect(() => { void load(); }, [load]);

  const ready = read && name.trim().length > 0 && !busy && !!consent && !!blob && !consent.integrityFlagged;

  const sign = async (stepUpToken: string) => {
    if (!consent || !blob) return;
    setBusy(true); setFormError(null); setFieldError(null);
    try {
      const shown = await sha256Hex(blob, consent.documentSha256);
      const done = await esignApi.sign(documentId, { documentSha256: shown, consentVersion: consent.consentVersion, typedName: name.trim(), language: locale === 'ar' ? 'ar' : 'en', deviceId: deviceId() }, stepUpToken, idemKey.current);
      setStepUpOpen(false);
      setReceipt(done);
    } catch (e) {
      const problem = problemOf(e);
      setStepUpOpen(false);
      idemKey.current = `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`;
      if (problem.code === 'typed_name_mismatch') {
        setFieldError(t('esign.error.typed_name_mismatch'));
        setTimeout(() => nameRef.current?.focus(), 120);   // after the dialog has handed focus back to its opener
      } else if (problem.code === 'already_signed') {
        setFormError(t('esign.error.already_signed'));
      } else {
        setFormError(t(problem.titleKey ?? 'esign.error.validation_failed'));
        if (problem.code && RELOAD_CODES.has(problem.code)) void load();
      }
    } finally { setBusy(false); }
  };

  if (receipt) {
    return (
      <div className="space-y-4" data-testid="sign-success">
        <EssPageHeader title={t('Document acknowledged')} subtitle={t('Your acknowledgement is recorded and cannot be changed.')} />
        <EssCard title={consent?.title ?? t('Document')}>
          <div className="space-y-3 text-sm">
            <p className="flex items-center gap-2 font-semibold text-emerald-700 dark:text-emerald-300" role="status"><CheckCircle2 className="h-5 w-5" aria-hidden="true" />{t('Acknowledged')}</p>
            <p>{t('Verification code')}: <bdi dir="ltr" data-testid="verification-code" className="font-mono font-bold">{receipt.verificationCode}</bdi></p>
            <NotQualifiedBadge />
            <div className="flex flex-wrap gap-2">
              <button type="button" className={essPrimaryButton}
                onClick={async () => openBlob(await esignApi.certificate(receipt.ackId), `acknowledgement-${receipt.verificationCode}.pdf`)}>
                <Download className="h-4 w-4" aria-hidden="true" />{t('Download your certificate')}
              </button>
              <button type="button" className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-700 dark:border-white/10 dark:text-slate-200"
                onClick={async () => { try { setVerified(await esignApi.verify(receipt.ackId)); } catch { setVerified('failed'); } }}>
                {t('Check the record')}
              </button>
              <Link href="/ess/sign" className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-700 dark:border-white/10 dark:text-slate-200">{t('Back to documents to sign')}</Link>
            </div>
            {verified && (
              <EssNotice tone={verified !== 'failed' && verified.valid ? 'ok' : 'error'}>
                {verified !== 'failed' && verified.valid ? t('The record is intact: the document and the chain of records both check out.') : t('The record could not be verified. Tell HR.')}
              </EssNotice>
            )}
          </div>
        </EssCard>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <Link href="/ess/sign" className="inline-flex items-center gap-1 text-xs font-semibold text-sapphire dark:text-cyanAccent"><ArrowLeft className="h-3.5 w-3.5 rtl:rotate-180" aria-hidden="true" />{t('Back to documents to sign')}</Link>
      <EssPageHeader title={consent?.title ?? t('Sign a document')} subtitle={t('Read the document, then type your name and confirm with your password.')} />
      {loadError ? <EssLoadError error={loadError} onRetry={() => void load()} /> : (
        <>
          <EssCard title={t('The document')}>
            <PdfPreview blob={blob} fileName={consent?.fileName ?? 'document.pdf'} title={t('The document you are acknowledging')} />
          </EssCard>
          {consent?.integrityFlagged && <EssNotice tone="error">{t('esign.error.document_tampered')}</EssNotice>}
          {consent && <ConsentText consent={consent} />}
          <EssCard title={t('Your acknowledgement')}>
            {!canWrite ? <p className="text-sm text-slate-600 dark:text-slate-300">{t('Your account can view this document but cannot sign it. Ask HR if you need to.')}</p> : (
              <form className="space-y-4" noValidate onSubmit={e => { e.preventDefault(); if (ready) setStepUpOpen(true); }}>
                <label htmlFor={readId} className="flex items-start gap-2 text-sm">
                  <input id={readId} type="checkbox" className="mt-0.5 h-4 w-4" checked={read} onChange={e => setRead(e.target.checked)} />
                  <span>{t('I have read this document')}</span>
                </label>
                <label htmlFor={nameId} className="block space-y-1">
                  <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Type your full name exactly as it is on your record')} <span className="font-normal text-slate-500">{t('(required)')}</span></span>
                  <input id={nameId} ref={nameRef} className={essInput} autoComplete="name" dir="auto" maxLength={200} value={name}
                    onChange={e => { setName(e.target.value); setFieldError(null); }}
                    aria-invalid={fieldError ? true : undefined} aria-describedby={fieldError ? nameErrorId : undefined} />
                </label>
                <div id={nameErrorId} role="alert" className="min-h-[1rem] text-xs font-medium text-rose-700 dark:text-rose-300">{fieldError}</div>
                {formError && <EssNotice tone="error">{formError}</EssNotice>}
                <div className="flex flex-wrap items-center gap-3">
                  <button ref={signButtonRef} data-testid="sign-submit" type="submit" disabled={!ready} className={essPrimaryButton}>
                    {busy ? <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" /> : <PenLine className="h-4 w-4" aria-hidden="true" />}{busy ? t('Signing…') : t('Sign')}
                  </button>
                  <NotQualifiedBadge />
                </div>
              </form>
            )}
          </EssCard>
        </>
      )}
      <PasswordDialog open={stepUpOpen} onClose={() => setStepUpOpen(false)} title={t('Confirm it is you')} submitLabel={t('Confirm and sign')}
        intro={t('Enter your password to sign. This proves the acknowledgement is yours.')}
        verify={async (password, code) => (await esignApi.stepUp(password, code)).stepUpToken}
        onVerified={sign} />
    </div>
  );
}
