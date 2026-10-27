'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { Loader2, PenLine } from 'lucide-react';
import { Modal } from '../../components/Modal';
import { deviceId, hrEsignApi, problemOf, sha256Hex, type SignatureRow, type ConsentView, type SignReceipt } from '../../api/esign';
import { useLocale } from '../../contexts/LocaleContext';
import { essInput, essPrimaryButton, EssNotice } from '../../components/ess/EssParts';
import { ConsentText, NotQualifiedBadge, PdfPreview } from '../ess/sign/SignParts';

/**
 * HR's device, the EMPLOYEE's signature. HR opens this next to the employee; the document is shown, the employee types their own name
 * and their OWN password (never HR's), and the server records both people: the employee as signer and the HR user as witness.
 * HR sees success or failure and never a password: the field is not echoed, is not autofilled, and is cleared after every attempt.
 */
export function SignOnDeviceDialog({ open, employeeId, employeeName, row, onClose, onSigned }: {
  open: boolean; employeeId: number; employeeName: string; row: SignatureRow | null; onClose: () => void; onSigned: (receipt: SignReceipt) => void;
}) {
  const { t, locale } = useLocale();
  const [blob, setBlob] = useState<Blob | null>(null);
  const [consent, setConsent] = useState<ConsentView | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [read, setRead] = useState(false);
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [needsCode, setNeedsCode] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const idem = useRef('');
  const nameId = useId(); const pwId = useId(); const codeId = useId(); const readId = useId(); const errId = useId();

  useEffect(() => {
    if (!open || !row) return;
    setBlob(null); setConsent(null); setLoadError(false); setRead(false); setName(''); setPassword(''); setCode(''); setNeedsCode(false); setError(null);
    idem.current = `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`;
    let cancelled = false;
    (async () => {
      try {
        const [file, wording] = await Promise.all([hrEsignApi.download(row.documentId), hrEsignApi.consent(employeeId, row.documentId, locale === 'ar' ? 'ar' : 'en')]);
        if (cancelled) return;
        setBlob(file); setConsent(wording);   // the wording is the server's; the hash of the bytes on screen is computed when signing
      } catch { if (!cancelled) setLoadError(true); }
    })();
    return () => { cancelled = true; };
  }, [open, row, employeeId, locale]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!row || !consent || !blob) return;
    setBusy(true); setError(null);
    try {
      // The hash of the bytes on screen is what the step-up token is bound to, together with this employee and this document.
      const documentSha256 = await sha256Hex(blob, consent.documentSha256);
      // 1. The employee proves it is them (their password, on this device). HR's own password is refused by the server.
      const token = (await hrEsignApi.deviceStepUp(employeeId, row.documentId, documentSha256, password, code)).stepUpToken;
      setPassword(''); setCode('');
      // 2. The server records the acknowledgement: signer = employee, witness = the HR user holding the device.
      const receipt = await hrEsignApi.signOnDevice(employeeId, row.documentId,
        { documentSha256, consentVersion: consent.consentVersion, typedName: name.trim(), language: locale === 'ar' ? 'ar' : 'en', deviceId: deviceId() }, token, idem.current);
      onSigned(receipt);
    } catch (e) {
      const problem = problemOf(e);
      setPassword('');
      if (problem.detail === 'mfa_required') setNeedsCode(true);
      setError(t(problem.titleKey ?? 'esign.error.validation_failed'));
    } finally { setBusy(false); }
  };

  const ready = read && name.trim().length > 0 && password.length > 0 && !busy && !!blob;
  return (
    <Modal isOpen={open} onClose={onClose} title={t('Sign in person: {name}', { name: employeeName })} size="lg" closeLabel={t('Close')} closeDisabled={busy}>
      <form onSubmit={submit} className="space-y-4" noValidate autoComplete="off">
        <p className="text-sm text-slate-600 dark:text-slate-300">{t('Hand this screen to {name}. They read the document and sign with their own password. You will not see it.', { name: employeeName })}</p>
        {loadError ? <EssNotice tone="error">{t('The document could not be loaded.')}</EssNotice> : <PdfPreview blob={blob} fileName={row?.fileName ?? 'document.pdf'} title={t('The document the employee is acknowledging')} />}
        {consent && <ConsentText consent={consent} />}
        <label htmlFor={readId} className="flex items-start gap-2 text-sm"><input id={readId} type="checkbox" className="mt-0.5 h-4 w-4" checked={read} onChange={e => setRead(e.target.checked)} /><span>{t('The employee has read this document')}</span></label>
        <label htmlFor={nameId} className="block space-y-1">
          <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('The employee types their full name')} <span className="font-normal text-slate-500">{t('(required)')}</span></span>
          <input id={nameId} className={essInput} dir="auto" autoComplete="off" maxLength={200} value={name} onChange={e => setName(e.target.value)} />
        </label>
        <label htmlFor={pwId} className="block space-y-1">
          <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t("The employee's own password")} <span className="font-normal text-slate-500">{t('(required)')}</span></span>
          <input id={pwId} type="password" dir="ltr" autoComplete="new-password" className={essInput} value={password} onChange={e => setPassword(e.target.value)} aria-describedby={error ? errId : undefined} aria-invalid={error ? true : undefined} />
        </label>
        {needsCode && (
          <label htmlFor={codeId} className="block space-y-1">
            <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Authenticator code')} <span className="font-normal text-slate-500">{t('(required)')}</span></span>
            <input id={codeId} inputMode="numeric" dir="ltr" autoComplete="one-time-code" maxLength={12} className={essInput} value={code} onChange={e => setCode(e.target.value)} />
          </label>
        )}
        <div id={errId} role="alert" aria-live="assertive" className="min-h-[1rem] text-xs font-medium text-rose-700 dark:text-rose-300">{error}</div>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <NotQualifiedBadge />
          <div className="flex gap-2">
            <button type="button" onClick={onClose} disabled={busy} className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-700 dark:border-white/10 dark:text-slate-200">{t('Cancel')}</button>
            <button type="submit" disabled={!ready} className={essPrimaryButton}>{busy ? <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" /> : <PenLine className="h-4 w-4" aria-hidden="true" />}{busy ? t('Signing…') : t('Sign')}</button>
          </div>
        </div>
      </form>
    </Modal>
  );
}
