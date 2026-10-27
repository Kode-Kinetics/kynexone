'use client';

import { useCallback, useEffect, useState } from 'react';
import { BadgeCheck, FileSignature, Loader2, ShieldAlert } from 'lucide-react';
import type { EmployeeDetail } from '../../api/employees';
import { hrEsignApi, openBlob, problemOf, type SignatureRow, type VerifyResult } from '../../api/esign';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { StatusChip } from '../../components/StatusChip';
import { SignOnDeviceDialog } from './SignOnDeviceDialog';

/** Documents HR asked this employee to acknowledge: request one, sign it in person on this device, check a record, take the certificate. */
export function EmployeeSignaturesPanel({ employee }: { employee: EmployeeDetail }) {
  const { t } = useLocale();
  const fx = useFormat();
  const { hasPermission } = useAuth();
  const canRequest = hasPermission('employees.write') && hasPermission('employees.documents');
  const [rows, setRows] = useState<SignatureRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [pick, setPick] = useState('');
  const [busy, setBusy] = useState(false);
  const [device, setDevice] = useState<SignatureRow | null>(null);
  const [checks, setChecks] = useState<Record<string, VerifyResult | 'failed'>>({});

  const load = useCallback(async () => { try { setRows(await hrEsignApi.list(employee.id)); setError(null); } catch { setError(t('Signatures could not be loaded.')); } }, [employee.id, t]);
  useEffect(() => { void load(); }, [load]);

  const requested = new Set((rows ?? []).map(r => r.documentId));
  const candidates = employee.documents.filter(d => !requested.has(d.id) && /pdf/i.test(d.contentType ?? '') && d.documentType !== 'Signed acknowledgement');

  const request = async () => {
    if (!pick) return;
    setBusy(true); setError(null); setNotice(null);
    try { await hrEsignApi.request(employee.id, pick); setPick(''); setNotice(t('Signature requested. The employee sees it under Documents to sign.')); await load(); }
    catch (e) { setError(t(problemOf(e).titleKey ?? 'esign.error.document_not_signable')); }
    finally { setBusy(false); }
  };

  const verify = async (row: SignatureRow) => {
    if (!row.ackId) return;
    try { setChecks(c => ({ ...c, [row.documentId]: undefined as never })); const r = await hrEsignApi.verify(employee.id, row.ackId); setChecks(c => ({ ...c, [row.documentId]: r })); }
    catch { setChecks(c => ({ ...c, [row.documentId]: 'failed' })); }
  };

  const statusLabel = (r: SignatureRow) => r.status === 'Signed' ? t('Signed') : r.status === 'Superseded' ? t('Replaced by a newer version') : t('Awaiting signature');
  return (
    <section data-testid="signatures-panel" aria-labelledby="signatures-heading" className="border-t border-slate-200 p-5 dark:border-white/10">
      <h3 id="signatures-heading" className="text-lg font-extrabold text-slate-950 dark:text-white">{t('Signatures')}</h3>
      <p className="mt-1 text-sm text-slate-500">{t('Electronic acknowledgement of contracts and policies. It is not a qualified electronic signature.')}</p>
      {canRequest && (
        <div className="mt-3 flex flex-wrap items-end gap-2">
          <label className="text-sm font-semibold">{t('Ask the employee to sign')}
            <select aria-label={t('Document to sign')} className="select mt-1.5 block min-w-[14rem]" value={pick} onChange={e => setPick(e.target.value)} disabled={busy || candidates.length === 0}>
              <option value="">{candidates.length === 0 ? t('No PDF documents to request') : t('Choose a document')}</option>
              {candidates.map(d => <option key={d.id} value={d.id}>{d.documentType} - {d.fileName}</option>)}
            </select>
          </label>
          <button type="button" className="btn-primary" onClick={() => void request()} disabled={!pick || busy}>{busy ? <Loader2 className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true" /> : <FileSignature className="h-4 w-4" aria-hidden="true" />}{t('Request signature')}</button>
        </div>
      )}
      {notice && <p role="status" className="mt-3 rounded-lg bg-emerald-50 px-3 py-2 text-sm font-semibold text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-200">{notice}</p>}
      {error && <p role="alert" className="mt-3 rounded-lg bg-rose-50 px-3 py-2 text-sm font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-200">{error}</p>}
      {rows === null ? null : rows.length === 0 ? <p className="mt-3 text-sm text-slate-500">{t('No signatures have been requested for this employee.')}</p> : (
        <ul className="mt-4 grid gap-3">
          {rows.map(r => {
            const check = checks[r.documentId];
            return (
              <li key={r.documentId} data-testid="signature-row" className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div><p className="font-bold">{r.title}</p><p className="text-sm text-slate-500"><bdi dir="ltr">{r.fileName}</bdi></p></div>
                  <StatusChip label={statusLabel(r)} tone={r.status === 'Signed' ? 'emerald' : r.status === 'Awaiting' ? 'amber' : 'slate'} dot />
                </div>
                {r.integrityFlagged && <p role="alert" className="mt-2 flex items-center gap-1.5 text-sm font-semibold text-rose-700 dark:text-rose-300"><ShieldAlert className="h-4 w-4" aria-hidden="true" />{t('esign.error.document_tampered')}</p>}
                {r.status === 'Signed' && (
                  <p className="mt-2 text-sm text-slate-600 dark:text-slate-300">
                    {t('Signed by {name} on {date}', { name: r.typedName ?? '', date: fx.dateTime(r.signedAtUtc) })}
                    {r.method === 'esign_hr_device' ? ` · ${t('on the HR device')}` : ''} · <bdi dir="ltr" className="font-mono text-xs">{r.verificationCode}</bdi>
                  </p>
                )}
                <div className="mt-3 flex flex-wrap gap-2">
                  {r.status === 'Awaiting' && canRequest && <button type="button" className="btn-secondary" onClick={() => setDevice(r)}>{t('Sign in person')}</button>}
                  {r.status === 'Signed' && <button type="button" className="btn-secondary" onClick={() => void verify(r)}><BadgeCheck className="h-4 w-4" aria-hidden="true" />{t('Check the record')}</button>}
                  {r.status === 'Signed' && r.certificateDocumentId && <button type="button" className="btn-secondary" onClick={async () => openBlob(await hrEsignApi.download(r.certificateDocumentId!), `acknowledgement-${r.verificationCode}.pdf`)}>{t('Download the certificate')}</button>}
                </div>
                {check && <p role={check !== 'failed' && check.valid ? 'status' : 'alert'} className={`mt-2 text-sm font-semibold ${check !== 'failed' && check.valid ? 'text-emerald-700 dark:text-emerald-300' : 'text-rose-700 dark:text-rose-300'}`}>
                  {check !== 'failed' && check.valid ? t('The record is intact: the document and the chain of records both check out.') : t('The record could not be verified: the file or the chain of records no longer matches what was signed.')}</p>}
              </li>
            );
          })}
        </ul>
      )}
      <SignOnDeviceDialog open={!!device} employeeId={employee.id} employeeName={employee.fullName} row={device} onClose={() => setDevice(null)}
        onSigned={() => { setDevice(null); setNotice(t('Signed. The certificate is stored with the employee documents.')); void load(); }} />
    </section>
  );
}
