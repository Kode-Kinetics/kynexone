'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { Download, FileText, Loader2, ShieldAlert } from 'lucide-react';
import { employeesApi, type EmployeeDetail, type EmployeeDocument, type EmployeeReadiness } from '../../api/employees';
import {
  complianceProfileForCountry,
  employeeFieldCatalogApi,
  LOCAL_FIELD_CATALOG,
  resolveFieldCatalog,
  type EmployeeComplianceField,
} from '../../api/employeeFieldCatalog';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { describeApiError } from '../../lib/apiError';
import { downloadAuthenticated } from '../../lib/downloadFile';
import { documentDownloadState } from '../../lib/documentDownload';
import { canDownloadScanned, scanLabelKey, scanRefusalKey, scanState } from '../../lib/scanState';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { EmployeeSignaturesPanel } from './EmployeeSignaturesPanel';
import { useFormat } from '../../hooks/useFormat';
import { recordDate } from '../../lib/recordFormat';
import { RecordField, RecordFieldGrid } from '../../components/employee/RecordField';
import { RecordSection } from '../../components/employee/RecordSection';
import { useDocumentDecision } from '../../components/employees/useDocumentDecision';
import { canDecideDocument, documentStatusWords } from '../../lib/documentVerification';

export function EmployeeComplianceDocumentsSection({ employee, readiness, panel, returnTo }: { employee: EmployeeDetail; readiness: EmployeeReadiness | null; panel?: string; returnTo: string }) {
  const { hasPermission } = useAuth();
  const { t } = useLocale();
  const fx = useFormat();
  const canSeeSensitive = hasPermission('employees.sensitive');
  const canManageDocuments = hasPermission('employees.documents');
  // Identity numbers are edited in the Identity step of Edit Employee, which offers them only to a writer who may see them.
  const canEditIdentity = hasPermission('employees.write') && canSeeSensitive;
  const [documents, setDocuments] = useState<EmployeeDocument[]>(employee.documents);
  const [documentType, setDocumentType] = useState('Passport');
  const [expiryDate, setExpiryDate] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [uploading, setUploading] = useState(false);
  const [notice, setNotice] = useState('');
  const [error, setError] = useState('');
  const [downloading, setDownloading] = useState<string | null>(null);
  const [downloadError, setDownloadError] = useState('');
  const [identityFields, setIdentityFields] = useState<EmployeeComplianceField[]>([]);
  const fileRef = useRef<HTMLInputElement>(null);
  // G-004: verify or reject a document that is waiting, here on the record as well as in the Compliance queue.
  const decision = useDocumentDecision(updated => setDocuments(current => current.map(d => d.id === updated.id ? { ...d, ...updated } : d)));
  const needsContract = readiness?.recommended.some(item => item.fix?.kind === 'document' && item.label === 'Contract document') ?? false;
  useEffect(() => {
    let cancelled = false;
    employeeFieldCatalogApi.get({ companyId: employee.companyId, countryCode: employee.countryCode, nationality: employee.nationality })
      .then((remote) => {
        if (cancelled) return;
        const profile = complianceProfileForCountry(resolveFieldCatalog(remote), employee.countryCode);
        if (remote) {
          setIdentityFields(profile);
          return;
        }
        const recordedKeys = new Set(employee.complianceRecords.map((record) => record.fieldKey));
        setIdentityFields(complianceProfileForCountry(LOCAL_FIELD_CATALOG, employee.countryCode).filter((field) => {
          const scalar = field.entityKey ? String((employee as unknown as Record<string, unknown>)[field.entityKey] ?? '') : '';
          return field.entityKey === 'passportNumber' || recordedKeys.has(field.fieldKey) || scalar.trim().length > 0;
        }));
      });
    return () => { cancelled = true; };
  }, [employee]);

  const complianceValue = (field: EmployeeComplianceField) => {
    const scalar = field.entityKey ? String((employee as unknown as Record<string, unknown>)[field.entityKey] ?? '') : '';
    return scalar || employee.complianceRecords.find((record) => record.fieldKey === field.fieldKey)?.fieldValue || '';
  };
  const upload = async () => {
    if (!file || uploading) return;
    setUploading(true); setNotice(''); setError('');
    try {
      const uploaded = await employeesApi.uploadDocument(employee.id, { documentType, expiryDate: expiryDate || undefined, isRequired: false, approvalStatus: 'Pending' }, file);
      setDocuments(current => [uploaded, ...current]);
      setNotice(documentType !== 'Contract' && needsContract
        ? `${documentType} uploaded. The Contract document checklist still requires a Contract file.`
        : `${documentType} uploaded.`);
      setFile(null); setExpiryDate('');
      if (fileRef.current) fileRef.current.value = '';
    } catch (uploadError) {
      setError(describeApiError(uploadError, t));
    } finally {
      setUploading(false);
    }
  };
  /** The route is bearer-protected: fetch through the API client and save from a blob (a plain link would answer 401). */
  const download = async (document: EmployeeDocument) => {
    if (downloading) return;
    setDownloading(document.id); setDownloadError('');
    try {
      await downloadAuthenticated(() => employeesApi.downloadDocument(document.id), document.fileName || 'document');
    } catch (downloadFailure) {
      const code = (downloadFailure as { response?: { data?: { code?: string } } } | null)?.response?.data?.code;
      setDownloadError(code === 'document_quarantined' ? t('ess.completion.error.document_quarantined') : t('The file could not be downloaded. {reason}', { reason: describeApiError(downloadFailure, t) }));
    } finally {
      setDownloading(null);
    }
  };
  const downloadAction = (document: EmployeeDocument) => {
    const state = documentDownloadState(document, { documents: canManageDocuments, sensitive: canSeeSensitive });
    if (state === 'needs_documents_permission') return <p className="mt-1 text-xs text-slate-500" data-testid="doc-download-reason">{t('You need the documents permission to download this file.')}</p>;
    if (state === 'needs_sensitive_permission') return <p className="mt-1 text-xs text-slate-500" data-testid="doc-download-reason">{t('You need the sensitive-data permission to download this file.')}</p>;
    if (state === 'scan_blocked') return null;
    const busy = downloading === document.id;
    return <button type="button" data-testid="doc-download" disabled={downloading !== null} aria-label={t('Download {file}', { file: document.fileName || document.documentType })} onClick={() => void download(document)} className="mt-1 inline-flex items-center gap-1 text-xs font-semibold text-sapphire underline disabled:opacity-60 dark:text-cyanAccent">{busy ? <Loader2 className="h-3.5 w-3.5 motion-safe:animate-spin" aria-hidden="true" /> : <Download className="h-3.5 w-3.5" aria-hidden="true" />}{busy ? t('Downloading…') : t('Download')}</button>;
  };
  return <RecordSection
    bare
    title={t('Compliance & Documents')}
    description={t('Jurisdiction-specific identity records, expiries, and required documents.')}
    actions={<>{canEditIdentity && <Link href={employeeWorkspaceHref(employee.id, { section: 'compliance-documents', edit: true, returnTo })} className="btn-secondary">{t('Edit identity')}</Link>}{canManageDocuments && panel !== 'documents' && <Link href={employeeWorkspaceHref(employee.id, { section: 'compliance-documents', panel: 'documents', returnTo })} className="btn-secondary">{t('Upload document')}</Link>}</>}
  >
    {decision.dialogs}
    {!canSeeSensitive ? <div className="m-4 flex items-start gap-3 rounded-lg border border-amber-200 bg-amber-50 p-3 dark:border-amber-500/30 dark:bg-amber-500/10"><ShieldAlert className="h-4 w-4 text-amber-600" /><div><p className="text-sm font-semibold text-amber-900 dark:text-amber-100">{t('Sensitive identity details are restricted.')}</p><p className="mt-0.5 text-xs text-amber-800 dark:text-amber-200">{t('Document status can remain visible without exposing protected identity values.')}</p></div></div> : <div className="p-4"><RecordFieldGrid>{identityFields.map((field) => {
      const expiry = field.expiryEntityKey ? String((employee as unknown as Record<string, unknown>)[field.expiryEntityKey] ?? '') : '';
      return <RecordField key={field.fieldKey} label={field.fieldLabel} value={complianceValue(field)} code caption={expiry ? t('Expires on {date}', { date: recordDate(fx, expiry) }) : undefined} />;
    })}</RecordFieldGrid></div>}
    <div className="border-t border-slate-200 p-4 dark:border-white/10"><h3 className="text-sm font-semibold text-slate-950 dark:text-white">{panel === 'documents' ? t('Documents') : t('Document status')}</h3>
      {panel === 'documents' && canManageDocuments && <div className="mt-4 grid gap-3 rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-white/10 dark:bg-white/[0.03] sm:grid-cols-2">
        <label className="text-sm font-semibold">{t('Document type')}<select aria-label={t('Document type')} className="select mt-1.5 w-full" value={documentType} disabled={uploading} onChange={event => { setDocumentType(event.target.value); setNotice(''); setError(''); }}><option>Passport</option><option>Contract</option><option>Offer letter</option></select></label>
        <label className="text-sm font-semibold">{t('Document expiry')}<input aria-label={t('Document expiry')} className="input mt-1.5 w-full" type="date" value={expiryDate} disabled={uploading} onChange={event => setExpiryDate(event.target.value)} /></label>
        <label className="text-sm font-semibold sm:col-span-2">{t('Document file')}<input ref={fileRef} aria-label={t('Upload document file')} className="input mt-1.5 w-full" type="file" disabled={uploading} onChange={event => { setFile(event.target.files?.[0] ?? null); setNotice(''); setError(''); }} /></label>
        {notice && <p role="status" className="rounded-lg bg-emerald-50 px-3 py-2 text-sm font-semibold text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-200 sm:col-span-2">{notice}</p>}
        {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-200 sm:col-span-2">{error}</p>}
        <button type="button" onClick={() => void upload()} disabled={!file || uploading} className="btn-primary justify-center disabled:opacity-50 sm:col-span-2">{uploading ? t('Uploading…') : t('Upload {type}', { type: documentType })}</button>
      </div>}
      {decision.error && <p role="alert" className="mt-3 rounded-lg bg-rose-50 px-3 py-2 text-sm font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-200">{decision.error}</p>}
      {downloadError && <p role="alert" data-testid="doc-download-error" className="mt-3 rounded-lg bg-rose-50 px-3 py-2 text-sm font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-200">{downloadError}</p>}
      {documents.length ? <ul className="mt-2 divide-y divide-slate-100 dark:divide-white/10">{documents.map(document => <li key={document.id} className="flex gap-3 py-2.5"><FileText className="mt-0.5 h-4 w-4 shrink-0 text-blue-600" /><div className="min-w-0"><p className="text-sm font-semibold">{document.documentType}</p><p className="break-words text-xs text-slate-500">{document.fileName} · <span data-testid={`doc-status-${document.id}`}>{t(documentStatusWords(document.approvalStatus))}</span></p>{document.rejectionReason && <p className="text-sm text-rose-700 dark:text-rose-300" data-testid={`doc-reason-${document.id}`}>{t('Reason: {reason}', { reason: document.rejectionReason })}</p>}{canDecideDocument(document, canManageDocuments && (canSeeSensitive || !document.isProtected)) && <div className="mt-2 flex flex-wrap gap-2"><button type="button" className="btn-primary" disabled={decision.busy} onClick={() => void decision.verify({ employeeId: employee.id, documentId: document.id, documentType: document.documentType, fileName: document.fileName })}>{t('Verify document')}</button><button type="button" className="btn-danger" disabled={decision.busy} onClick={() => decision.askReject({ employeeId: employee.id, documentId: document.id, documentType: document.documentType, fileName: document.fileName })}>{t('Reject document')}</button></div>}{scanLabelKey(scanState(document.scanStatus)) && <p className={`text-xs ${canDownloadScanned(scanState(document.scanStatus)) ? 'text-slate-500' : 'font-medium text-rose-700 dark:text-rose-300'}`} data-testid="doc-scan">{t(scanLabelKey(scanState(document.scanStatus)) as string)}{scanRefusalKey(scanState(document.scanStatus)) && ` ${t(scanRefusalKey(scanState(document.scanStatus)) as string)}`}</p>}{downloadAction(document)}</div></li>)}</ul> : <p className="mt-2 text-sm text-slate-500">{t('No documents are recorded for this employee.')}</p>}
    </div>
    {canManageDocuments && <EmployeeSignaturesPanel employee={employee} />}
  </RecordSection>;
}
