'use client';

import { useId, useState } from 'react';
import { FileCheck2, ShieldAlert, Upload } from 'lucide-react';
import { employeesApi } from '../../api/employees';
import type { LoanArt92Check } from '../../api/loanGovernance';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';

/** The document type the server requires for the Art. 92 consent (a restricted HR-evidence type). */
export const LOAN_DEDUCTION_CONSENT = 'LoanDeductionConsent';

/**
 * Art. 92 consent step on the loan request form (Release A slice R3). Shown only when the eligibility check says the
 * instalment deducted from pay is above 10% of the wage (the server returns `art92` only for Release A tenants). HR
 * uploads the employee's signed consent to their file and the request carries its id; an employee cannot upload this
 * evidence themselves, so they are told how to bring the instalment within 10% or to sign with HR.
 */
export function LoanConsentStep({ art92, self, employeeId, consentDocumentId, onConsent }: {
  art92: LoanArt92Check;
  self: boolean;
  employeeId: number | undefined;
  consentDocumentId: string | null;
  onConsent: (documentId: string | null) => void;
}) {
  const { t } = useLocale();
  const fx = useFormat();
  const inputId = useId();
  const [uploading, setUploading] = useState(false);
  const [fileName, setFileName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  if (!art92.requiresConsent) return null;

  const pct = art92.pct == null ? null : fx.percent(art92.pct, Number.isInteger(art92.pct) ? 0 : 2);
  const why = pct == null
    ? t('The wage could not be confirmed, so the 10% limit cannot be checked. A signed consent is needed before this instalment can be deducted from pay.')
    : self
      ? t('Your instalment is {pct} of your wage. Above 10%, it can be deducted from your pay only with your signed consent.', { pct })
      : t("This instalment is {pct} of the employee's wage. Above 10%, it can be deducted from pay only with the employee's signed consent.", { pct });

  const upload = async (file: File | undefined) => {
    if (!file || employeeId == null) return;
    setUploading(true); setError(null);
    try {
      const doc = await employeesApi.uploadDocument(employeeId, { documentType: LOAN_DEDUCTION_CONSENT, isRequired: false }, file);
      setFileName(file.name);
      onConsent(doc.id);
    } catch {
      setError(t('The consent could not be uploaded. Please try again.'));
      onConsent(null);
    } finally {
      setUploading(false);
    }
  };

  return (
    <div className="space-y-2 rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm dark:border-amber-500/40 dark:bg-amber-500/10" data-testid="loan-consent-step">
      <p className="flex items-center gap-1.5 font-semibold text-amber-900 dark:text-amber-200">
        <ShieldAlert className="h-4 w-4" aria-hidden /> {t('Written consent needed (Article 92)')}
      </p>
      <p className="text-xs text-amber-900 dark:text-amber-100">{why}</p>
      {self ? (
        <p className="text-xs text-amber-900 dark:text-amber-100">
          {t('Choose more instalments to bring it to 10% or less, or sign the consent form with HR, who will submit the request with it.')}
        </p>
      ) : consentDocumentId ? (
        <p className="flex items-center gap-1.5 text-xs font-semibold text-emerald-700 dark:text-emerald-300" role="status">
          <FileCheck2 className="h-4 w-4" aria-hidden />
          {fileName ? t('Signed consent attached: {file}', { file: fileName }) : t('Signed consent attached.')}
        </p>
      ) : (
        <div className="space-y-1">
          <label htmlFor={inputId} className="inline-flex cursor-pointer items-center gap-1.5 rounded-lg bg-white px-3 py-1.5 text-xs font-semibold text-slate-800 ring-1 ring-amber-300 hover:bg-amber-100 dark:bg-white/[0.06] dark:text-slate-100 dark:ring-amber-500/40">
            <Upload className="h-3.5 w-3.5" aria-hidden />
            {uploading ? t('Uploading…') : t("Upload the employee's signed consent")}
          </label>
          <input id={inputId} type="file" accept="application/pdf,image/*" className="sr-only" disabled={uploading || employeeId == null}
            onChange={(e) => void upload(e.target.files?.[0])} />
          <p className="text-[11px] text-amber-800 dark:text-amber-200">{t('It is kept on the employee’s file as HR evidence and is not shown in self-service.')}</p>
        </div>
      )}
      {error && <p role="alert" className="text-xs text-rose-700 dark:text-rose-300">{error}</p>}
    </div>
  );
}
