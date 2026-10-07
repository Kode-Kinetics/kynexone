'use client';

import { useEffect, useState } from 'react';
import { ClipboardCheck } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { employeesApi, type EmployeeDocument } from '../../api/employees';
import { packageApi, type PackageProposal } from '../../api/package';
import { date, fill, isContractDocument, reasonText, valueText, type FormatContext } from './packageFormat';

/**
 * A proposed package from the bulk run (Release A R2): nothing is fixed until another HR user checks it against the
 * employee's signed contract on file and confirms it — or rejects it with a reason. The person who asked for the run
 * sees it but cannot decide it.
 */
export function ProposalCard({ employeeId, contractId, contractFileUrl, proposal, labels, ctx, onDecided }: {
  employeeId: number; contractId: string; contractFileUrl?: string | null; proposal: PackageProposal; labels: (code: string) => string;
  ctx: FormatContext; onDecided: (message: string) => void;
}) {
  const { t, locale } = useLocale();
  const [documents, setDocuments] = useState<EmployeeDocument[]>([]);
  const [documentId, setDocumentId] = useState('');
  const [rejecting, setRejecting] = useState(false);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Only the signed contract counts (the server refuses anything else): the term's own file or a contract-type document.
  useEffect(() => {
    employeesApi.documents(employeeId).then((docs) => setDocuments(docs.filter((d) => isContractDocument(d, contractFileUrl)))).catch(() => setDocuments([]));
  }, [employeeId, contractFileUrl]);

  const decide = async (kind: 'confirm' | 'reject') => {
    setBusy(true);
    setError(null);
    try {
      if (kind === 'confirm') await packageApi.confirmProposal(proposal.batchId, contractId, documentId);
      else await packageApi.rejectProposal(proposal.batchId, contractId, reason.trim());
      onDecided(kind === 'confirm' ? t('The package is fixed for this contract year.') : t('The proposed package was rejected.'));
    } catch (e) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('The package could not be changed.'));
    } finally { setBusy(false); }
  };

  return (
    <section aria-label={t('Proposed package')} className="space-y-2 rounded-2xl border border-amber-200 bg-amber-50/60 p-4 text-sm dark:border-amber-500/20 dark:bg-amber-500/[0.05]">
      <h4 className="flex items-center gap-2 font-semibold text-amber-900 dark:text-amber-200"><ClipboardCheck className="h-4 w-4" aria-hidden="true" />{t('Proposed package — not fixed yet')}</h4>
      <p className="text-xs text-amber-900/80 dark:text-amber-200/80">
        {proposal.to
          ? fill(t('Proposed from the grade table for {from} to {to}. Check it against the signed contract before confirming.'), { from: date(proposal.from, locale), to: date(proposal.to, locale) })
          : fill(t('Proposed from the grade table from {from}, with no end date. Check it against the signed contract before confirming.'), { from: date(proposal.from, locale) })}
      </p>
      <ul className="space-y-0.5 text-xs text-slate-700 dark:text-slate-200">
        {proposal.rows.map((row) => <li key={row.componentCode}>{labels(row.componentCode)}: {valueText({ ...row, monthlyCash: null, dependantsCovered: 0 }, ctx)}</li>)}
      </ul>
      {proposal.skips.filter((x) => x.code !== 'ENTITLEMENT_NOT_IN_GRADE').length > 0 && (
        <div className="text-xs text-slate-700 dark:text-slate-200">
          <p className="font-medium">{t('These benefits are not in the proposal, for the reason shown.')}</p>
          <ul className="list-disc ps-4">
            {proposal.skips.filter((x) => x.code !== 'ENTITLEMENT_NOT_IN_GRADE')
              .map((x) => <li key={x.componentCode}>{fill(t('{benefit} — {reason}'), { benefit: labels(x.componentCode), reason: reasonText(x.code, t) })}</li>)}
          </ul>
        </div>
      )}
      {proposal.requestedByYou ? (
        <p className="text-xs text-slate-600 dark:text-slate-300">{t('You asked for these proposals, so another HR user must confirm or reject them.')}</p>
      ) : (
        <div className="flex flex-wrap items-end gap-2">
          <label className="text-xs text-slate-600 dark:text-slate-300">{t('Signed contract on file')}
            <select className="mt-0.5 block rounded-lg border border-slate-300 bg-white px-2 py-1.5 text-sm dark:border-white/15 dark:bg-white/[0.04]"
              value={documentId} onChange={(e) => setDocumentId(e.target.value)}>
              <option value="">{t('Choose the signed contract')}</option>
              {documents.map((d) => <option key={d.id} value={d.id}>{d.documentType} · {d.fileName}</option>)}
            </select>
            {documents.length === 0 && <span className="mt-0.5 block text-[11px] text-amber-700 dark:text-amber-300">{t('No signed contract is on file. Upload it to the employee’s documents first.')}</span>}
          </label>
          <button type="button" disabled={busy || !documentId} onClick={() => void decide('confirm')}
            className="rounded-lg bg-emerald-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-emerald-700 disabled:opacity-50">{t('Confirm it matches the signed contract')}</button>
          {!rejecting
            ? <button type="button" onClick={() => setRejecting(true)} className="rounded-lg px-3 py-1.5 text-xs text-rose-700 hover:bg-rose-50 dark:text-rose-300 dark:hover:bg-rose-500/10">{t('Reject')}</button>
            : (
              <span className="flex flex-wrap items-end gap-2">
                <input aria-label={t('Why it does not match')} placeholder={t('Why it does not match')} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)}
                  className="rounded-lg border border-slate-300 bg-white px-2 py-1.5 text-sm dark:border-white/15 dark:bg-white/[0.04]" />
                <button type="button" disabled={busy || !reason.trim()} onClick={() => void decide('reject')}
                  className="rounded-lg bg-rose-600 px-3 py-1.5 text-xs font-semibold text-white disabled:opacity-50">{t('Reject the proposal')}</button>
              </span>
            )}
        </div>
      )}
      {error && <p role="alert" className="text-xs text-rose-700 dark:text-rose-300">{error}</p>}
    </section>
  );
}
