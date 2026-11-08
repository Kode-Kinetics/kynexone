'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { CheckCircle, Download, FileText } from 'lucide-react';
import { employeesApi, type PendingDocumentRow } from '../../api/employees';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { describeApiError } from '../../lib/apiError';
import { employeeWorkspaceHref } from '../../lib/peopleRoutes';
import { useDocumentDecision } from './useDocumentDecision';

const PAGE_SIZE = 25;

/**
 * G-004: the cross-employee queue of documents waiting for HR to verify them (Compliance → Employee documents → To verify).
 * Oldest first. A protected document (identity, payroll, contract) is listed only to a caller who may verify it. Verify and
 * Reject use the endpoints the employee record already had; this is the screen they never had.
 */
export function DocumentsToVerifyQueue() {
  const t = useT();
  const fmt = useFormat();
  const [rows, setRows] = useState<PendingDocumentRow[] | null>(null);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [error, setError] = useState('');

  const load = useCallback(async (target: number) => {
    setError('');
    try {
      const result = await employeesApi.pendingDocuments(target, PAGE_SIZE);
      // The last row of a page was decided: fall back one page rather than show an empty one.
      if (result.items.length === 0 && result.total > 0 && target > 1) { setPage(target - 1); return; }
      setRows(result.items); setTotal(result.total);
    } catch (e) {
      setRows(null);
      setError(describeApiError(e, t));
    }
  }, [t]);

  useEffect(() => { void load(page); }, [load, page]);

  const decision = useDocumentDecision(() => { void load(page); });
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  if (error && rows === null) {
    return (
      <div role="alert" className="surface flex flex-wrap items-center gap-3 border-s-4 border-s-rose-500 p-4 text-sm text-rose-600 dark:text-rose-400">
        <span>{t('The documents waiting for verification could not be loaded.')} {error}</span>
        <button type="button" className="btn-secondary ms-auto" onClick={() => void load(page)}>{t('Try again')}</button>
      </div>
    );
  }
  if (rows === null) return <p className="py-8 text-center text-sm text-slate-400">{t('Loading…')}</p>;

  return (
    <div className="space-y-4" data-testid="documents-to-verify">
      {decision.dialogs}
      <p className="text-sm text-slate-500 dark:text-slate-400">{t('Documents employees uploaded that HR has not checked yet. Open the file, then verify it, or reject it with a reason the employee will read.')}</p>
      {decision.error && <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-400/20 dark:bg-rose-500/10 dark:text-rose-400">{decision.error}</p>}
      {rows.length === 0 ? (
        <div className="py-12 text-center">
          <CheckCircle className="mx-auto mb-3 h-10 w-10 text-emerald-400" aria-hidden="true" />
          <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{t('Nothing is waiting for verification.')}</p>
          <p className="mt-1 text-xs text-slate-400">{t('Every uploaded document has been checked.')}</p>
        </div>
      ) : (
        <>
          <p className="text-sm text-slate-500 dark:text-slate-400">{t('{count, plural, one {# document is waiting} other {# documents are waiting}}', { count: total })}</p>
          <div className="surface overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-slate-200 dark:border-white/10">
                  {[t('Employee'), t('Document'), t('Uploaded'), t('Decision')].map(h => (
                    <th key={h} className="p-3 text-start text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {rows.map(row => {
                  const target = { employeeId: row.employeeId, documentId: row.documentId, documentType: row.documentType, fileName: row.fileName, employeeName: row.employeeName };
                  return (
                    <tr key={row.documentId} data-testid={`pending-doc-${row.documentId}`}>
                      <td className="p-3">
                        <Link href={employeeWorkspaceHref(row.employeeId, { section: 'compliance-documents', returnTo: '/compliance?tab=employee-documents' })} className="font-medium text-sapphire hover:underline dark:text-cyanAccent">{row.employeeName}</Link>
                        <p className="font-mono text-xs text-slate-500">{row.employeeCode}</p>
                      </td>
                      <td className="p-3">
                        <p className="flex items-center gap-2 font-medium text-slate-800 dark:text-slate-200"><FileText className="h-4 w-4 text-blue-600" aria-hidden="true" />{row.documentType}</p>
                        <p className="text-xs text-slate-500">{row.fileName}</p>
                      </td>
                      <td className="p-3 text-xs text-slate-500 dark:text-slate-400">{fmt.date(row.uploadedAtUtc)}</td>
                      <td className="p-3">
                        <div className="flex flex-wrap gap-2">
                          <button type="button" className="btn-secondary" onClick={() => void decision.download(target)}><Download className="h-4 w-4" aria-hidden="true" />{t('Open file')}</button>
                          <button type="button" className="btn-primary" disabled={decision.busy} onClick={() => void decision.verify(target)}>{t('Verify document')}</button>
                          <button type="button" className="btn-danger" disabled={decision.busy} onClick={() => decision.askReject(target)}>{t('Reject document')}</button>
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          {pages > 1 && (
            <div className="flex items-center justify-between text-sm">
              <button type="button" className="btn-secondary" disabled={page <= 1} onClick={() => setPage(p => Math.max(1, p - 1))}>{t('Previous')}</button>
              <span className="text-slate-500">{t('Page {page} of {pages}', { page, pages })}</span>
              <button type="button" className="btn-secondary" disabled={page >= pages} onClick={() => setPage(p => Math.min(pages, p + 1))}>{t('Next')}</button>
            </div>
          )}
        </>
      )}
    </div>
  );
}
