'use client';

import { useId } from 'react';
import { AlertTriangle } from 'lucide-react';
import { useLocale } from '../contexts/LocaleContext';
import type { ImportPreview } from './ImportExportToolbar';

/**
 * The dry-run review for an organisation file (departments): counts, the rows with their notes, and, when the file would
 * remove existing cost-centre or parent links, a gate that needs an acknowledgement and a reason before Confirm.
 */
export function OrgPreviewBody({
  preview, help, ack, onAck, reason, onReason,
}: {
  preview: ImportPreview; help?: string; ack: boolean; onAck: (v: boolean) => void; reason: string; onReason: (v: string) => void;
}) {
  const { t } = useLocale();
  const id = useId();
  const org = preview.org!;
  const rejected = org.rows.filter((r) => r.outcome === 'error').length;
  const outcome = (o: string) => (o === 'error' ? t('Rejected') : o === 'warning' ? t('Warning') : o === 'skipped' ? t('Skipped') : t('Ready'));
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap gap-2 text-xs">
        <span className="rounded-lg bg-slate-100 px-2 py-1 font-semibold text-slate-700 dark:bg-white/10 dark:text-slate-200">{t('{count} rows', { count: preview.received })}</span>
        <span className="rounded-lg bg-emerald-50 px-2 py-1 font-semibold text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300">{t('{count} to create', { count: preview.wouldCreate })}</span>
        <span className="rounded-lg bg-sky-50 px-2 py-1 font-semibold text-sky-800 dark:bg-sky-500/10 dark:text-sky-300">{t('{count} to update', { count: org.wouldUpdate })}</span>
        {rejected > 0 && <span className="rounded-lg bg-rose-50 px-2 py-1 font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">{t('{count} rejected', { count: rejected })}</span>}
      </div>

      {help && <p data-testid="cc-import-help" className="text-xs text-slate-600 dark:text-slate-300">{t(help)}</p>}

      {org.linksToClear > 0 && (
        <div data-testid="cc-import-clear-warning" role="alert" className="space-y-3 rounded-lg border border-amber-300 bg-amber-50 px-3 py-3 text-sm text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
          <p className="flex items-start gap-2 font-semibold">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
            <span>{t('{count, plural, one {This file would clear # existing cost-centre or parent link.} other {This file would clear # existing cost-centre or parent links.}}', { count: org.linksToClear })}</span>
          </p>
          <label className="flex items-start gap-2 text-sm">
            <input data-testid="cc-import-clear-ack" type="checkbox" checked={ack} onChange={(e) => onAck(e.target.checked)} className="mt-1 h-4 w-4" />
            <span>{t('I understand these links will be removed from the existing departments.')}</span>
          </label>
          <div>
            <label htmlFor={`${id}-reason`} className="mb-1 block text-sm font-medium">{t('Reason')} <span aria-hidden="true" className="text-rose-600">*</span></label>
            <input id={`${id}-reason`} data-testid="cc-import-clear-reason" value={reason} onChange={(e) => onReason(e.target.value)} maxLength={500} className="input w-full" />
            <p className="mt-1 text-xs">{t('Recorded in the audit log.')}</p>
          </div>
        </div>
      )}

      <div className="max-h-[45vh] overflow-auto rounded-lg border border-slate-200 dark:border-white/10" tabIndex={0} role="group" aria-label={t('Rows in the file')}>
        <table className="w-full min-w-[560px] text-sm">
          <thead className="sticky top-0 bg-slate-50 dark:bg-slate-900">
            <tr className="text-start text-caption font-semibold uppercase text-slate-500">
              <th scope="col" className="px-3 py-2 text-start">{t('Row')}</th>
              <th scope="col" className="px-3 py-2 text-start">{t('Code')}</th>
              <th scope="col" className="px-3 py-2 text-start">{t('Name')}</th>
              <th scope="col" className="px-3 py-2 text-start">{t('Outcome')}</th>
              <th scope="col" className="px-3 py-2 text-start">{t('Notes')}</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {org.rows.map((r) => (
              <tr key={r.row}>
                <td className="px-3 py-2 text-slate-500">{r.row}</td>
                <td className="px-3 py-2 font-mono text-xs"><bdi dir="ltr">{r.code}</bdi></td>
                <td className="px-3 py-2"><bdi dir="auto">{r.name}</bdi></td>
                <td className={`px-3 py-2 text-xs font-semibold ${r.outcome === 'error' ? 'text-rose-700 dark:text-rose-300' : r.outcome === 'warning' ? 'text-amber-800 dark:text-amber-300' : 'text-slate-700 dark:text-slate-200'}`}>{outcome(r.outcome)}</td>
                <td className="px-3 py-2 text-xs text-slate-600 dark:text-slate-300">{[...r.errors, ...r.warnings].map((m) => t(m)).join(' ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
