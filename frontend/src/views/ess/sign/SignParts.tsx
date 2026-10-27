'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { Download, ExternalLink, FileText, Loader2, ShieldCheck } from 'lucide-react';
import { Modal } from '../../../components/Modal';
import { useLocale } from '../../../contexts/LocaleContext';
import { essInput, essPrimaryButton } from '../../../components/ess/EssParts';
import { openBlob, problemOf, type ConsentView } from '../../../api/esign';

/** Always the same words: this is an acknowledgement, not a qualified electronic signature (CD13). */
export function NotQualifiedBadge() {
  const { t } = useLocale();
  return (
    <p data-testid="not-qualified" className="inline-flex items-center gap-1.5 rounded-full bg-slate-100 px-3 py-1 text-xs font-semibold text-slate-700 dark:bg-white/[0.06] dark:text-slate-200">
      <ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" />{t('Electronic acknowledgement (not a qualified signature)')}
    </p>
  );
}

/** The consent text in BOTH languages, as real text, each in its own direction. */
export function ConsentText({ consent }: { consent: ConsentView }) {
  const { t } = useLocale();
  return (
    <section aria-label={t('What you are acknowledging')} data-testid="consent-text" className="space-y-3 rounded-xl border border-slate-200 bg-slate-50 p-4 text-sm leading-relaxed text-slate-800 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-200">
      <p lang="en" dir="ltr" className="text-start">{consent.textEn}</p>
      <p lang="ar" dir="rtl" className="text-start">{consent.textAr}</p>
      <p className="text-[11px] text-slate-500">{t('Consent text {version}', { version: consent.consentVersion })}</p>
    </section>
  );
}

/**
 * The stored PDF, shown in the page from an authenticated fetch (a same-origin blob: iframe, which the site's headers allow; no header is
 * loosened for it). If the browser cannot show it in the page, the link below is the way to read it.
 */
export function PdfPreview({ blob, fileName, title }: { blob: Blob | null; fileName: string; title: string }) {
  const { t } = useLocale();
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!blob) { setUrl(null); return; }
    const u = URL.createObjectURL(blob);
    setUrl(u);
    return () => URL.revokeObjectURL(u);
  }, [blob]);
  if (!blob) return <div className="flex h-64 items-center justify-center rounded-xl border border-slate-200 text-sm text-slate-500 dark:border-white/10"><Loader2 className="me-2 h-4 w-4 motion-safe:animate-spin" aria-hidden="true" />{t('Loading the document…')}</div>;
  return (
    <div className="space-y-2">
      {url && <iframe data-testid="pdf-preview" src={url} title={title} className="h-[28rem] w-full rounded-xl border border-slate-200 bg-white dark:border-white/10" />}
      <div className="flex flex-wrap gap-3 text-xs">
        {url && <a href={url} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 font-semibold text-blue-800 underline dark:text-cyanAccent"><ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />{t('Open the PDF in a new tab')}</a>}
        <button type="button" onClick={() => openBlob(blob, fileName)} className="inline-flex items-center gap-1 font-semibold text-blue-800 underline dark:text-cyanAccent"><Download className="h-3.5 w-3.5" aria-hidden="true" />{t('Download the PDF')}</button>
        <span className="inline-flex items-center gap-1 text-slate-500"><FileText className="h-3.5 w-3.5" aria-hidden="true" /><bdi dir="ltr">{fileName}</bdi></span>
      </div>
    </div>
  );
}

/**
 * The person signing re-enters THEIR OWN password (and authenticator code when they use one). `verify` returns the step-up token.
 * On HR's device the same dialog is used by the employee, and nothing typed is ever echoed or kept after a failure.
 */
export function PasswordDialog({ open, title, intro, verify, onClose, onVerified, submitLabel }: {
  open: boolean; title: string; intro: string; submitLabel: string;
  verify: (password: string, mfaCode?: string) => Promise<string>;
  onClose: () => void; onVerified: (token: string) => void | Promise<void>;
}) {
  const { t } = useLocale();
  const passwordId = useId(); const codeId = useId(); const errorId = useId();
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [needsCode, setNeedsCode] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const passwordRef = useRef<HTMLInputElement>(null);
  useEffect(() => { if (open) { setPassword(''); setCode(''); setError(null); setNeedsCode(false); requestAnimationFrame(() => passwordRef.current?.focus()); } }, [open]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy(true); setError(null);
    try {
      const token = await verify(password, code);
      setPassword(''); setCode('');
      await onVerified(token);
    } catch (e) {
      const problem = problemOf(e);
      setPassword('');
      if (problem.detail === 'mfa_required' || problem.code === 'mfa_required') setNeedsCode(true);
      setError(t(problem.titleKey ?? 'esign.error.employee_device_auth_failed'));
    } finally { setBusy(false); }
  };

  return (
    <Modal isOpen={open} onClose={onClose} title={title} size="sm" closeLabel={t('Close')} closeDisabled={busy}>
      <form onSubmit={submit} className="space-y-4" noValidate autoComplete="off">
        <p className="text-sm text-slate-600 dark:text-slate-300">{intro}</p>
        <label htmlFor={passwordId} className="block space-y-1">
          <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Password')} <span className="font-normal text-slate-500">{t('(required)')}</span></span>
          <input id={passwordId} ref={passwordRef} type="password" autoComplete="new-password" dir="ltr" required className={essInput}
            value={password} onChange={e => setPassword(e.target.value)} aria-invalid={error ? true : undefined} aria-describedby={error ? errorId : undefined} />
        </label>
        {needsCode && (
          <label htmlFor={codeId} className="block space-y-1">
            <span className="text-xs font-semibold text-slate-600 dark:text-slate-300">{t('Authenticator code')} <span className="font-normal text-slate-500">{t('(required)')}</span></span>
            <input id={codeId} inputMode="numeric" autoComplete="one-time-code" dir="ltr" maxLength={12} className={essInput}
              value={code} onChange={e => setCode(e.target.value)} aria-invalid={error ? true : undefined} aria-describedby={error ? errorId : undefined} />
          </label>
        )}
        <div id={errorId} role="alert" aria-live="assertive" className="min-h-[1rem] text-xs font-medium text-rose-700 dark:text-rose-300">{error}</div>
        <div className="flex flex-wrap justify-end gap-2">
          <button type="button" onClick={onClose} disabled={busy} className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-700 dark:border-white/10 dark:text-slate-200">{t('Cancel')}</button>
          <button type="submit" disabled={busy || !password} className={essPrimaryButton}>
            {busy && <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" />}{busy ? t('Checking…') : submitLabel}
          </button>
        </div>
      </form>
    </Modal>
  );
}
