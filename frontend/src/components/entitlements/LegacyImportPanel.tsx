'use client';

import { useState } from 'react';
import { entitlementsApi, type LegacyImportResult } from '../../api/entitlements';
import { useLocale } from '../../contexts/LocaleContext';
import { fillTemplate, localName } from '../../lib/gradeLoanLimits';
import { loanErrorMessage } from '../../lib/loanWorkflow';

/** Plain-language reasons for a legacy row that is not imported (codes from EntitlementMatrixService.MapPayScaleLine). */
const reasonKeys: Record<string, string> = {
  basic_salary: 'Basic salary is set on the salary record and the grade pay range, not here.',
  no_matching_benefit: 'No benefit here matches this line.',
  inactive_line: 'This line was switched off.',
  not_monthly: 'The allowance is monthly but the line holds a yearly figure. Enter the monthly value in the grid.',
  percentage_invalid: 'The percentage must be above 0 and at most 100.',
  zero_amount: 'The line has no amount.',
  needs_per_child_and_cap: 'Education is a yearly amount per child, up to a number of children. The old line says neither, so enter both in the grid.',
  not_an_allowance: 'This line is not an allowance paid to the employee. Set the value in the grid if one applies.',
  needs_ticket_details: 'A ticket needs a count, a class and who travels. Set them in the grid.',
  needs_medical_class: 'Medical cover needs its class. Set it in the grid.',
  needs_daily_rate: 'Per diem is a daily rate. Enter it in the grid.',
  duplicate_mapping: 'More than one line of this grade matches the same benefit. Set the value in the grid.',
  already_set: 'The grid already has a value for this grade. It is kept.',
  eligibility_only: 'An eligibility rule says who may enrol, not what they get. Set the value in the grid.',
};

/**
 * Brings the old grade pay scales into the grid, once, after a preview. Nothing is guessed: a line that can't be
 * mapped exactly is listed with the reason and left for HR to set by hand. The old rows are kept, not deleted.
 */
export function LegacyImportPanel({ today, onImported }: { today: string; onImported: () => void }) {
  const { t, locale } = useLocale();
  const [open, setOpen] = useState(false);
  const [effectiveFrom, setEffectiveFrom] = useState(today);
  const [preview, setPreview] = useState<LegacyImportResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  const run = async (commit: boolean) => {
    setBusy(true); setError(''); setNotice('');
    try {
      const result = await entitlementsApi.importLegacy(commit, effectiveFrom);
      setPreview(result);
      if (commit) {
        setNotice(fillTemplate(t('Imported {count} value(s) into the grid, starting {date}.'), { count: result.imported, date: result.effectiveFrom }));
        onImported();
      }
    } catch (e) { setError(loanErrorMessage(e, t('Unable to read the old grade pay scales.'))); }
    finally { setBusy(false); }
  };

  return <details className="surface rounded-2xl p-4" open={open} onToggle={e => setOpen((e.target as HTMLDetailsElement).open)}>
    <summary className="cursor-pointer text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Bring in the old grade pay scales')}</summary>
    <div className="mt-3 space-y-3 text-sm">
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('Grade pay scales and benefit eligibility rules are now read-only. Preview what can be carried into this grid; nothing is written until you import, and the old rows are kept.')}</p>
      <div className="flex flex-wrap items-end gap-2">
        <label>{t('Start the imported values on')}
          <input type="date" className="input ms-2" min={today} value={effectiveFrom} onChange={e => { setEffectiveFrom(e.target.value); setPreview(null); }} />
        </label>
        <button type="button" className="btn-secondary" disabled={busy} onClick={() => void run(false)}>{t('Preview')}</button>
        {preview && !preview.committed && preview.toImport > 0 && <button type="button" className="btn-primary" disabled={busy} onClick={() => void run(true)}>
          {fillTemplate(t('Import {count} value(s)'), { count: preview.toImport })}
        </button>}
      </div>
      {error && <p role="alert" className="text-red-600">{error}</p>}
      {notice && <p role="status" className="text-emerald-700 dark:text-emerald-300">{notice}</p>}
      {preview && (preview.items.length === 0
        ? <p className="text-slate-500">{t('There is nothing to bring in: no grade pay scales or grade eligibility rules are on file.')}</p>
        : <div className="overflow-x-auto">
          <p className="mb-2 text-xs text-slate-500">{fillTemplate(t('{import} can be imported · {skip} need setting by hand'), { import: preview.toImport, skip: preview.skipped })}</p>
          <table className="w-full min-w-[560px] text-xs">
            <thead><tr>{[t('Grade'), t('Old line'), t('Value'), t('What happens')].map(h => <th key={h} scope="col" className="p-2 text-start text-slate-500">{h}</th>)}</tr></thead>
            <tbody>{preview.items.map(item => <tr key={`${item.source}-${item.sourceId}`} className="border-t border-slate-100 align-top dark:border-white/10">
              <td className="p-2">{localName(locale, item.gradeName, item.gradeNameAr)}<span className="block text-slate-400">{item.gradeCode}</span></td>
              <td className="p-2">{item.sourceName}<span className="block text-slate-400">{item.source === 'Eligibility' ? t('Eligibility rule') : t('Pay scale line')} · {item.sourceCode}</span></td>
              <td className="p-2 text-slate-600 dark:text-slate-300">{item.detail}</td>
              <td className="p-2">{item.outcome === 'Import'
                ? <span className="font-semibold text-emerald-700 dark:text-emerald-300">{t(preview.committed ? 'Imported' : 'Will be imported')}</span>
                : <span className="text-amber-800 dark:text-amber-200">{t(reasonKeys[item.reasonCode ?? ''] ?? 'Set this value in the grid.')}</span>}</td>
            </tr>)}</tbody>
          </table>
        </div>)}
    </div>
  </details>;
}
