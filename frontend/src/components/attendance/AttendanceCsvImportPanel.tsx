'use client';

import { ChangeEvent, useState } from 'react';
import { Download, Upload } from 'lucide-react';
import { attendanceApi } from '../../api/attendance';
import { useLocale } from '../../contexts/LocaleContext';
import { useTenantSettings } from '../../contexts/TenantSettingsContext';
import { describeBulkError } from '../../lib/attendanceBulkError';
import {
  IMPORT_ALL_KEY,
  IMPORT_MAX_ROWS,
  IMPORT_PARTIAL_KEY,
  buildImportErrorReportCsv,
  importButtonLabel,
  importGate,
  type ImportPreview,
} from '../../lib/attendanceImport';
import { downloadCsv } from '../ImportExportToolbar';
import { StatusChip } from '../StatusChip';
import { SectionTitle } from './attendanceUi';

const CSV_EXAMPLE = 'employeeCode,punchTimestamp,punchDirection,location,method\nEMP-001,2026-06-16 09:00,In,Main office,Manual\nEMP-001,2026-06-16 18:00,Out,Main office,Manual';

type Outcome = { kind: 'validated'; result: ImportPreview } | { kind: 'imported'; result: ImportPreview } | null;

/**
 * CSV attendance import in three explicit steps: download the template, Validate (a dry run that saves nothing),
 * then Import the valid rows. Rows with problems are listed with their real line number and reason and can be
 * downloaded as an error report. Importing is only offered for exactly the content that was validated.
 */
export function AttendanceCsvImportPanel({ onImported }: { onImported: () => void }) {
  const { t } = useLocale();
  const { defaultTimezone } = useTenantSettings();
  const [content, setContent] = useState('');
  const [fileName, setFileName] = useState('');
  const [validatedContent, setValidatedContent] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<Outcome>(null);
  const [busy, setBusy] = useState<'validate' | 'import' | null>(null);
  const [error, setError] = useState('');

  const result = outcome?.result ?? null;
  const gate = importGate({
    content, validatedContent,
    preview: outcome?.kind === 'validated' ? outcome.result : null,
    busy: busy !== null,
  });
  const label = importButtonLabel(outcome?.kind === 'validated' ? outcome.result : null);
  const zone = result?.timeZone || defaultTimezone;
  const effectiveName = fileName || 'attendance-import.csv';

  const changeContent = (next: string) => {
    setContent(next);
    setError('');
  };

  const handleFile = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    setFileName(file.name);
    const reader = new FileReader();
    reader.onload = (ev) => changeContent(typeof ev.target?.result === 'string' ? ev.target.result : '');
    reader.onerror = () => setError(t('Could not read the file.'));
    reader.readAsText(file);
  };

  const clear = () => {
    setContent('');
    setFileName('');
    setValidatedContent(null);
    setOutcome(null);
    setError('');
  };

  const downloadTemplate = async () => {
    setError('');
    try {
      downloadCsv(await attendanceApi.events.importTemplate(), 'attendance-import-template.csv');
    } catch (err) {
      setError(`${t('Could not download the template.')} ${describeBulkError(err, t)}`);
    }
  };

  const validate = async () => {
    if (!gate.canValidate) return;
    setBusy('validate');
    setError('');
    const sent = content;
    try {
      const preview = await attendanceApi.events.importPreview({ fileName: effectiveName, csvContent: sent });
      setOutcome({ kind: 'validated', result: preview });
      setValidatedContent(sent);
    } catch (err) {
      setOutcome(null);
      setValidatedContent(null);
      setError(describeBulkError(err, t));
    } finally {
      setBusy(null);
    }
  };

  const runImport = async () => {
    if (!gate.canImport) return;
    setBusy('import');
    setError('');
    try {
      const committed = await attendanceApi.events.importCsv({ fileName: effectiveName, csvContent: content });
      setOutcome({ kind: 'imported', result: committed });
      setValidatedContent(null);
      onImported();
    } catch (err) {
      setError(describeBulkError(err, t));
    } finally {
      setBusy(null);
    }
  };

  const downloadErrors = () => {
    if (!result) return;
    downloadCsv(
      buildImportErrorReportCsv(result.errors, { row: t('Row'), column: t('Column'), reason: t('Reason'), original: t('Original line') }),
      `${effectiveName.replace(/\.csv$/i, '')}-errors.csv`,
    );
  };

  const importLabel = busy === 'import'
    ? t('Importing…')
    : label.mode === 'partial' ? t(IMPORT_PARTIAL_KEY, { count: label.valid, skipped: label.skipped })
      : label.mode === 'all' ? t(IMPORT_ALL_KEY, { count: label.valid })
        : t('Import CSV');

  return (
    <section className="surface p-4">
      <SectionTitle icon={Upload} title={t('CSV Attendance Import')} subtitle={t('Download the template, fill it in, check it, then import the valid rows.')} />
      <div className="mt-4 flex flex-wrap items-center gap-2">
        <button type="button" className="btn-secondary flex items-center gap-1.5 text-sm" onClick={downloadTemplate}>
          <Download className="h-3.5 w-3.5" aria-hidden="true" />{t('Download template')}
        </button>
        <label className="btn-secondary flex cursor-pointer items-center gap-1.5 text-sm focus-within:ring-2 focus-within:ring-sapphire/40">
          <Upload className="h-3.5 w-3.5" aria-hidden="true" />
          {fileName || t('Choose CSV file')}
          <input type="file" accept=".csv,text/csv" className="sr-only" onChange={handleFile} />
        </label>
        {(fileName || content) && <button type="button" onClick={clear} className="text-xs text-slate-500 hover:text-rose-600">{t('Clear')}</button>}
      </div>
      <textarea className="input mt-3 min-h-28 w-full font-mono text-xs" dir="ltr" aria-label={t('CSV content')} value={content}
        onChange={(e) => changeContent(e.target.value)} placeholder={CSV_EXAMPLE} />
      <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('Times without a time zone are read in {zone}. Up to {max} rows per file. Uploading the same file again is safe: punches already recorded are skipped.', { zone, max: IMPORT_MAX_ROWS })}</p>

      <div className="mt-3 flex flex-wrap gap-2">
        <button type="button" className="btn-secondary justify-center" disabled={!gate.canValidate} onClick={validate}>
          {busy === 'validate' ? t('Checking…') : t('Validate file')}
        </button>
        <button type="button" className="btn-primary justify-center" disabled={!gate.canImport} onClick={runImport}>{importLabel}</button>
      </div>

      {error && <p role="alert" className="mt-3 text-sm text-rose-700 dark:text-rose-400">{error}</p>}
      {gate.stale && outcome?.kind === 'validated' && <p role="status" className="mt-3 text-xs text-amber-700 dark:text-amber-400">{t('The file changed after it was validated. Validate it again before importing.')}</p>}

      {result && (
        <div className="mt-4 space-y-3">
          {outcome?.kind === 'validated' && <p className="text-xs text-slate-500 dark:text-slate-400">{t('Nothing has been saved yet. Review the result, then import.')}</p>}
          {outcome?.kind === 'imported' && (
            <p role="status" className="text-sm font-medium text-emerald-700 dark:text-emerald-400">
              {t('Imported {imported, plural, one {# row} other {# rows}}. {duplicates} already recorded, {failed} skipped.', { imported: result.importedRows, duplicates: result.duplicateRows, failed: result.failedRows })}
            </p>
          )}
          <div className="flex flex-wrap gap-1.5">
            <StatusChip tone="slate" label={t('Rows in file: {count}', { count: result.totalRows })} />
            <StatusChip tone="emerald" label={t('Valid: {count}', { count: result.validRows })} />
            <StatusChip tone="blue" label={t('Already recorded: {count}', { count: result.duplicateRows })} />
            <StatusChip tone={result.failedRows > 0 ? 'rose' : 'slate'} label={t('Need fixing: {count}', { count: result.failedRows })} />
          </div>
          {outcome?.kind === 'validated' && result.validRows === 0 && <p className="text-xs text-slate-500">{t('No valid rows to import. Fix the problems and validate again.')}</p>}

          {result.errors.length > 0 && (
            <div className="space-y-2">
              <div className="max-h-64 overflow-auto rounded-lg border border-slate-200 dark:border-white/10">
                <table className="w-full text-xs">
                  <caption className="sr-only">{t('Rows that need fixing')}</caption>
                  <thead className="sticky top-0 bg-slate-50 dark:bg-slate-900">
                    <tr>
                      <th scope="col" className="px-3 py-2 text-start font-semibold text-slate-500">{t('Row')}</th>
                      <th scope="col" className="px-3 py-2 text-start font-semibold text-slate-500">{t('Column')}</th>
                      <th scope="col" className="px-3 py-2 text-start font-semibold text-slate-500">{t('Reason')}</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                    {result.errors.map((e, i) => (
                      <tr key={`${e.rowNumber}-${i}`}>
                        <td className="px-3 py-2 font-mono text-slate-700 dark:text-slate-300">{e.rowNumber}</td>
                        <td className="px-3 py-2 text-slate-600 dark:text-slate-400">{e.column || '-'}</td>
                        <td className="px-3 py-2 text-slate-800 dark:text-slate-200">{e.message}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {result.failedRows > result.errors.length && (
                <p className="text-xs text-slate-500">{t('Showing the first {shown} of {count} problems. Fix these, then validate again to see the rest.', { shown: result.errors.length, count: result.failedRows })}</p>
              )}
              <button type="button" className="btn-secondary flex items-center gap-1.5 text-xs" onClick={downloadErrors}>
                <Download className="h-3.5 w-3.5" aria-hidden="true" />{t('Download error report')}
              </button>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
