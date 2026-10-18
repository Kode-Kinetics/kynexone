'use client';

import Link from 'next/link';
import { FileText, ShieldAlert } from 'lucide-react';
import type { EmployeeDetail } from '../../api/employees';
import { useAuth } from '../../contexts/AuthContext';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';

export function EmployeeComplianceDocumentsSection({ employee, panel, returnTo }: { employee: EmployeeDetail; panel?: string; returnTo: string }) {
  const { hasPermission } = useAuth();
  const canSeeSensitive = hasPermission('employees.sensitive');
  const canManageDocuments = hasPermission('employees.documents');
  return <section className="surface overflow-hidden">
    <header className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 p-5 dark:border-white/10"><div><h2 className="text-2xl font-extrabold text-slate-950 dark:text-white">Compliance &amp; Documents</h2><p className="mt-1 text-sm text-slate-500">Jurisdiction-specific identity records, expiries, and required documents.</p></div>{canManageDocuments && <Link href={employeeWorkspaceHref(employee.id, { edit: true, section: 'compliance-documents', panel: 'documents', returnTo })} className="btn-secondary">Upload document</Link>}</header>
    {!canSeeSensitive ? <div className="m-5 flex items-start gap-3 rounded-xl border border-amber-200 bg-amber-50 p-5 dark:border-amber-500/30 dark:bg-amber-500/10"><ShieldAlert className="h-5 w-5 text-amber-600" /><div><p className="font-bold text-amber-900 dark:text-amber-100">Sensitive identity details are restricted.</p><p className="mt-1 text-sm text-amber-800 dark:text-amber-200">Document status can remain visible without exposing protected identity values.</p></div></div> : <dl className="grid gap-4 p-5 sm:grid-cols-2 xl:grid-cols-3">{[['Passport number', employee.passportNumber], ['Iqama number', employee.iqamaNumber], ['Muqeem number', employee.muqeemNumber], ['GOSI reference', employee.gosiReference], ['Qiwa contract', employee.qiwaContractNumber], ['Visa number', employee.visaNumber]].map(([label, value]) => <div key={label} className="rounded-xl border border-slate-200 p-4 dark:border-white/10"><dt className="text-xs font-bold uppercase tracking-wide text-slate-400">{label}</dt><dd className="mt-1 font-semibold text-slate-900 dark:text-white">{value || 'Not provided'}</dd></div>)}</dl>}
    <div className="border-t border-slate-200 p-5 dark:border-white/10"><h3 className="text-lg font-extrabold text-slate-950 dark:text-white">{panel === 'documents' ? 'Documents' : 'Document status'}</h3>{employee.documents.length ? <ul className="mt-4 grid gap-3 sm:grid-cols-2">{employee.documents.map(document => <li key={document.id} className="flex gap-3 rounded-xl border border-slate-200 p-4 dark:border-white/10"><FileText className="h-5 w-5 text-blue-600" /><div><p className="font-bold">{document.documentType}</p><p className="text-sm text-slate-500">{document.fileName} · {document.approvalStatus}</p></div></li>)}</ul> : <p className="mt-3 text-sm text-slate-500">No documents are recorded for this employee.</p>}</div>
  </section>;
}
