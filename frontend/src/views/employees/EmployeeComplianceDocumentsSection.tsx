'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { FileText, ShieldAlert } from 'lucide-react';
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
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';

export function EmployeeComplianceDocumentsSection({ employee, readiness, panel, returnTo }: { employee: EmployeeDetail; readiness: EmployeeReadiness | null; panel?: string; returnTo: string }) {
  const { hasPermission } = useAuth();
  const { t } = useLocale();
  const canSeeSensitive = hasPermission('employees.sensitive');
  const canManageDocuments = hasPermission('employees.documents');
  const [documents, setDocuments] = useState<EmployeeDocument[]>(employee.documents);
  const [documentType, setDocumentType] = useState('Passport');
  const [expiryDate, setExpiryDate] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [uploading, setUploading] = useState(false);
  const [notice, setNotice] = useState('');
  const [error, setError] = useState('');
  const [identityFields, setIdentityFields] = useState<EmployeeComplianceField[]>([]);
  const fileRef = useRef<HTMLInputElement>(null);
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
  return <section className="surface overflow-hidden">
    <header className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 p-5 dark:border-white/10"><div><h2 className="text-2xl font-extrabold text-slate-950 dark:text-white">Compliance &amp; Documents</h2><p className="mt-1 text-sm text-slate-500">Jurisdiction-specific identity records, expiries, and required documents.</p></div>{canManageDocuments && panel !== 'documents' && <Link href={employeeWorkspaceHref(employee.id, { section: 'compliance-documents', panel: 'documents', returnTo })} className="btn-secondary">Upload document</Link>}</header>
    {!canSeeSensitive ? <div className="m-5 flex items-start gap-3 rounded-xl border border-amber-200 bg-amber-50 p-5 dark:border-amber-500/30 dark:bg-amber-500/10"><ShieldAlert className="h-5 w-5 text-amber-600" /><div><p className="font-bold text-amber-900 dark:text-amber-100">Sensitive identity details are restricted.</p><p className="mt-1 text-sm text-amber-800 dark:text-amber-200">Document status can remain visible without exposing protected identity values.</p></div></div> : <dl className="grid gap-4 p-5 sm:grid-cols-2 xl:grid-cols-3">{identityFields.map((field) => {
      const expiry = field.expiryEntityKey ? String((employee as unknown as Record<string, unknown>)[field.expiryEntityKey] ?? '') : '';
      return <div key={field.fieldKey} className="rounded-xl border border-slate-200 p-4 dark:border-white/10"><dt className="text-xs font-bold uppercase tracking-wide text-slate-400">{field.fieldLabel}</dt><dd className="mt-1 font-semibold text-slate-900 dark:text-white">{complianceValue(field) || 'Not provided'}</dd>{expiry && <dd className="mt-1 text-xs text-slate-500">Expires {expiry}</dd>}</div>;
    })}</dl>}
    <div className="border-t border-slate-200 p-5 dark:border-white/10"><h3 className="text-lg font-extrabold text-slate-950 dark:text-white">{panel === 'documents' ? 'Documents' : 'Document status'}</h3>
      {panel === 'documents' && canManageDocuments && <div className="mt-4 grid gap-3 rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-white/10 dark:bg-white/[0.03] sm:grid-cols-2">
        <label className="text-sm font-semibold">{t('Document type')}<select aria-label={t('Document type')} className="select mt-1.5 w-full" value={documentType} disabled={uploading} onChange={event => { setDocumentType(event.target.value); setNotice(''); setError(''); }}><option>Passport</option><option>Contract</option><option>Offer letter</option></select></label>
        <label className="text-sm font-semibold">{t('Document expiry')}<input aria-label={t('Document expiry')} className="input mt-1.5 w-full" type="date" value={expiryDate} disabled={uploading} onChange={event => setExpiryDate(event.target.value)} /></label>
        <label className="text-sm font-semibold sm:col-span-2">{t('Document file')}<input ref={fileRef} aria-label={t('Upload document file')} className="input mt-1.5 w-full" type="file" disabled={uploading} onChange={event => { setFile(event.target.files?.[0] ?? null); setNotice(''); setError(''); }} /></label>
        {notice && <p role="status" className="rounded-lg bg-emerald-50 px-3 py-2 text-sm font-semibold text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-200 sm:col-span-2">{notice}</p>}
        {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-200 sm:col-span-2">{error}</p>}
        <button type="button" onClick={() => void upload()} disabled={!file || uploading} className="btn-primary justify-center disabled:opacity-50 sm:col-span-2">{uploading ? t('Uploading…') : t('Upload {type}', { type: documentType })}</button>
      </div>}
      {documents.length ? <ul className="mt-4 grid gap-3 sm:grid-cols-2">{documents.map(document => <li key={document.id} className="flex gap-3 rounded-xl border border-slate-200 p-4 dark:border-white/10"><FileText className="h-5 w-5 text-blue-600" /><div><p className="font-bold">{document.documentType}</p><p className="text-sm text-slate-500">{document.fileName} · {document.approvalStatus}</p></div></li>)}</ul> : <p className="mt-3 text-sm text-slate-500">No documents are recorded for this employee.</p>}
    </div>
  </section>;
}
