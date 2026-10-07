'use client';

import { useId, useState } from 'react';
import { FileCheck2, ShieldAlert, Upload } from 'lucide-react';
import { loansApi, type EmployeeLoan } from '../../api/loans';
import { useLocale } from '../../contexts/LocaleContext';
import { useReleaseA } from '../../lib/releaseA';

/**
 * Art. 92 on a PENDING payroll-deducted loan (Release A slice R3): shows whether the employee's signed consent is on the
 * loan and lets the borrower or HR attach it. Approval re-checks Art. 92 and refuses an instalment above 10% of the wage
 * without it, so this is how a request made before the consent existed moves on. Hidden without release_a.
 */
export function LoanConsentAttach({ loan, self, onAttached }: { loan: EmployeeLoan; self: boolean; onAttached: () => void }) {
  const enabled = useReleaseA();
  const { t } = useLocale();
  const inputId = useId();
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  if (!enabled || loan.status !== 'Pending' || loan.repaymentMethod !== 'PayrollDeduction') return null;

  if (loan.consentOnFile) {
    return (
      <p role="status" data-testid="loan-consent-on-file" className="flex items-center gap-1.5 rounded-lg bg-emerald-50 p-3 text-sm font-semibold text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-200">
        <FileCheck2 className="h-4 w-4" aria-hidden /> {t('Signed consent on file (Article 92).')}
      </p>
    );
  }

  const upload = async (file: File | undefined) => {
    if (!file) return;
    setUploading(true); setError(null);
    try { await loansApi.attachConsent(loan.id, file); onAttached(); }
    catch { setError(t('The consent could not be uploaded. Please try again.')); }
    finally { setUploading(false); }
  };

  return (
    <div data-testid="loan-consent-attach" className="space-y-2 rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm dark:border-amber-500/40 dark:bg-amber-500/10">
      <p className="flex items-center gap-1.5 font-semibold text-amber-900 dark:text-amber-200">
        <ShieldAlert className="h-4 w-4" aria-hidden /> {t('Written consent (Article 92)')}
      </p>
      <p className="text-xs text-amber-900 dark:text-amber-100">
        {self
          ? t('If your instalment is above 10% of your wage, this request can be approved only with your signed consent.')
          : t("If the instalment is above 10% of the employee's wage, this request can be approved only with the employee's signed consent.")}
      </p>
      <label htmlFor={inputId} className="inline-flex cursor-pointer items-center gap-1.5 rounded-lg bg-white px-3 py-1.5 text-xs font-semibold text-slate-800 ring-1 ring-amber-300 hover:bg-amber-100 dark:bg-white/[0.06] dark:text-slate-100 dark:ring-amber-500/40">
        <Upload className="h-3.5 w-3.5" aria-hidden />
        {uploading ? t('Uploading…') : self ? t('Upload your signed consent') : t("Upload the employee's signed consent")}
      </label>
      <input id={inputId} type="file" accept="application/pdf,image/*" className="sr-only" disabled={uploading}
        onChange={(e) => void upload(e.target.files?.[0])} />
      {error && <p role="alert" className="text-xs text-rose-700 dark:text-rose-300">{error}</p>}
    </div>
  );
}
