'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { apiErrorReason } from '../../api/client';
import {
  financeErpApi, saveJournalFile,
  type CompanyBalance, type JournalExportDetail, type JournalExportFormat, type JournalExportManifest,
  type JournalExportScope, type JournalExportSummary, type JournalOutcome,
} from '../../api/financeErp';
import { useCompany } from '../../contexts/CompanyContext';
import { usePayrollCompanies } from '../../hooks/usePayrollCompanies';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { msg } from '../../i18n/translations';

/**
 * ERP journal hand-off for a period: check → create → download → record what the ERP said.
 *
 * The server owns every rule (double-post guard, per-company balance, maker/checker); this panel shows its
 * answer in plain words and never computes a total of its own — every figure comes from the manifest or the
 * persisted export it describes.
 */

const STATUS_LABEL: Record<string, string> = {
  Exported: msg('Waiting for ERP result'),
  Confirmed: msg('Posted in ERP'),
  Rejected: msg('Rejected by ERP'),
  Superseded: msg('Replaced by a newer file'),
  PartiallyConfirmed: msg('Partly posted in ERP'),
  Pending: msg('Waiting for ERP result'),
};
const STATUS_TONE: Record<string, string> = {
  Exported: 'bg-amber-100 text-amber-800 dark:bg-amber-500/15 dark:text-amber-300',
  Pending: 'bg-amber-100 text-amber-800 dark:bg-amber-500/15 dark:text-amber-300',
  Confirmed: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-300',
  PartiallyConfirmed: 'bg-sky-100 text-sky-800 dark:bg-sky-500/15 dark:text-sky-300',
  Rejected: 'bg-rose-100 text-rose-700 dark:bg-rose-500/15 dark:text-rose-300',
  Superseded: 'bg-slate-100 text-slate-600 dark:bg-white/10 dark:text-slate-300',
};
const KIND_LABEL: Record<string, string> = {
  Full: msg('Full export'),
  Supplementary: msg('Supplementary export'),
  Replacement: msg('Replacement export'),
};

function Pill({ tone, children }: { tone: string; children: React.ReactNode }) {
  return <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-semibold ${tone}`}>{children}</span>;
}

function thisMonth(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}

interface Refusal { message: string; byCompany: CompanyBalance[] }

function refusalOf(err: unknown, fallback: string): Refusal {
  const data = (err as { response?: { data?: { byCompany?: CompanyBalance[] } } })?.response?.data;
  return { message: apiErrorReason(err, fallback), byCompany: Array.isArray(data?.byCompany) ? data!.byCompany : [] };
}

export function JournalExportsPanel({ defaultPeriod }: { defaultPeriod?: string }) {
  const t = useT();
  const f = useFormat();
  const { companies } = usePayrollCompanies();
  // Only a group-scoped user may export (or list) "all companies"; the API forbids it to anyone else, so a
  // company-scoped user starts on their own company and never sees the group option.
  const { isGroupScope } = useCompany();
  const [formats, setFormats] = useState<JournalExportFormat[] | null>(null);
  const [formatsError, setFormatsError] = useState('');
  const [period, setPeriod] = useState(defaultPeriod ?? thisMonth());
  const [companyId, setCompanyId] = useState('');
  const [formatKey, setFormatKey] = useState('generic-csv');
  const [options, setOptions] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<'' | 'preview' | 'create'>('');
  const [manifest, setManifest] = useState<JournalExportManifest | null>(null);
  const [refusal, setRefusal] = useState<Refusal | null>(null);
  const [history, setHistory] = useState<JournalExportSummary[] | null>(null);
  const [historyError, setHistoryError] = useState('');
  const [openId, setOpenId] = useState<string | null>(null);

  useEffect(() => { if (defaultPeriod) setPeriod(defaultPeriod); }, [defaultPeriod]);
  useEffect(() => {
    if (isGroupScope || companies.length === 0) return;
    if (!companies.some(c => c.id === companyId)) setCompanyId(companies[0].id);
  }, [isGroupScope, companies, companyId]);
  /** A company-scoped user has no valid scope until their company is known: nothing is asked of the API before. */
  const scopeReady = isGroupScope || companyId !== '';

  useEffect(() => {
    financeErpApi.formats()
      .then(list => { setFormats(list); setFormatsError(''); })
      .catch(err => setFormatsError(apiErrorReason(err, t('Export formats could not be loaded.'))));
  }, [t]);

  const loadHistory = useCallback(() => {
    setHistory(null);
    setHistoryError('');
    if (!scopeReady) return;
    financeErpApi.list({ period, companyId: companyId || null })
      .then(setHistory)
      .catch(err => setHistoryError(apiErrorReason(err, t('Export history could not be loaded.'))));
  }, [period, companyId, scopeReady, t]);
  useEffect(() => { loadHistory(); }, [loadHistory]);

  const format = useMemo(() => formats?.find(x => x.key === formatKey) ?? null, [formats, formatKey]);
  const scope: JournalExportScope = { period, companyId: companyId || null, format: formatKey, options };
  const currencyOf = (m: { currency: string }) => m.currency || null;

  const run = async (kind: 'preview' | 'create') => {
    setBusy(kind);
    setRefusal(null);
    try {
      const result = kind === 'preview' ? await financeErpApi.preview(scope) : await financeErpApi.create(scope);
      setManifest(result);
      if (kind === 'create') { loadHistory(); if (result.exportId) setOpenId(result.exportId); }
    } catch (err) {
      setManifest(null);
      setRefusal(refusalOf(err, kind === 'preview' ? t('The journal could not be checked.') : t('The export file could not be created.')));
    } finally {
      setBusy('');
    }
  };

  const download = async (row: { id: string; fileName: string }) => {
    try {
      saveJournalFile(await financeErpApi.download(row.id, row.fileName));
    } catch (err) {
      // The file comes back as a blob, so a refusal carries no sentence to read. 403 is the read-only finance user (AM-21).
      const status = (err as { response?: { status?: number } } | null)?.response?.status;
      setRefusal(status === 403
        ? { message: t('Your access lets you see totals and balances, but not download the file.'), byCompany: [] }
        : refusalOf(err, t('The file could not be downloaded.')));
    }
  };

  return (
    <section className="surface space-y-4 p-4" aria-labelledby="erp-journal-export-heading">
      <div>
        <h3 id="erp-journal-export-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Send journal to your ERP')}</h3>
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
          {t('Create the posting file for a month, import it into your ERP, then record the ERP document number here. Rows your ERP already posted are never sent twice.')}
        </p>
      </div>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <label className="text-xs font-medium text-slate-600 dark:text-slate-300">
          {t('Accounting month')}
          <input type="month" className="input mt-1 w-full" value={period} onChange={e => setPeriod(e.target.value)} />
        </label>
        <label className="text-xs font-medium text-slate-600 dark:text-slate-300">
          {t('Company')}
          <select className="select mt-1 w-full" value={companyId} onChange={e => setCompanyId(e.target.value)}>
            {isGroupScope && <option value="">{t('All companies (one journal per company)')}</option>}
            {!isGroupScope && companies.length === 0 && <option value="">{t('Loading companies…')}</option>}
            {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
        <label className="text-xs font-medium text-slate-600 dark:text-slate-300 lg:col-span-2">
          {t('File format')}
          <select className="select mt-1 w-full" value={formatKey} disabled={!formats}
            onChange={e => { setFormatKey(e.target.value); setOptions({}); setManifest(null); }}>
            {!formats && <option value={formatKey}>{t('Loading formats…')}</option>}
            {formats?.map(x => <option key={x.key} value={x.key}>{x.displayName}</option>)}
          </select>
        </label>
      </div>

      {formatsError && <p role="alert" className="text-xs text-rose-600 dark:text-rose-400">{formatsError}</p>}
      {format && (
        <div className="rounded-lg bg-slate-50 p-3 text-xs text-slate-600 dark:bg-white/5 dark:text-slate-300">
          {format.status === 'Template' && (
            <p className="mb-1 font-semibold text-amber-700 dark:text-amber-300">{t('Template layout: import one test file into your ERP test company before using it for real postings.')}</p>
          )}
          <p>{format.disclaimer}</p>
          {format.singleCompany && companyId === '' && (
            <p className="mt-1 font-semibold text-amber-700 dark:text-amber-300" data-testid="journal-export-single-company">
              {t('This format holds one company per file: choose a company above.')}
            </p>
          )}
          {format.options.length > 0 && (
            <details className="mt-2" open={format.options.some(o => o.required)}>
              <summary className="cursor-pointer font-medium">
                {format.options.some(o => o.required) ? t('ERP settings for this format') : t('ERP settings for this format (optional)')}
              </summary>
              <div className="mt-2 grid gap-2 sm:grid-cols-2">
                {format.options.map(o => (
                  <label key={o.key} className="text-xs">
                    {o.required ? t('{label} (required)', { label: o.label }) : o.label}
                    <input className="input mt-1 w-full" maxLength={o.maxLength} placeholder={o.defaultValue} required={o.required}
                      value={options[o.key] ?? ''} onChange={e => setOptions(prev => ({ ...prev, [o.key]: e.target.value }))} />
                    <span className="mt-0.5 block text-[11px] text-slate-400">{o.description}</span>
                  </label>
                ))}
              </div>
            </details>
          )}
        </div>
      )}

      <div className="flex flex-wrap gap-2">
        <button type="button" className="btn-secondary text-xs" disabled={!!busy || !period || !scopeReady} onClick={() => run('preview')}>
          {busy === 'preview' ? t('Checking…') : t('Check balances')}
        </button>
        <button type="button" className="btn-primary text-xs" disabled={!!busy || !period || !formats || !scopeReady} onClick={() => run('create')}>
          {busy === 'create' ? t('Creating file…') : t('Create export file')}
        </button>
      </div>

      {refusal && (
        <div role="alert" className="rounded-lg bg-rose-50 p-3 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-300" data-testid="journal-export-refusal">
          <p>{refusal.message}</p>
          {refusal.byCompany.length > 0 && <CompanyTable rows={refusal.byCompany} currency={null} />}
        </div>
      )}

      {manifest && (
        <div className="space-y-2 rounded-lg border border-slate-200 p-3 dark:border-white/10" data-testid="journal-export-manifest">
          <div className="flex flex-wrap items-center gap-2">
            <Pill tone={manifest.kind === 'Full' ? STATUS_TONE.Superseded : STATUS_TONE.PartiallyConfirmed}>{t(KIND_LABEL[manifest.kind] ?? manifest.kind)}</Pill>
            <span className="text-xs text-slate-500">{manifest.exportId ? t('File created: {name}', { name: manifest.fileName }) : t('Check only: nothing has been saved yet.')}</span>
          </div>
          {manifest.previouslyConfirmedExcluded.count > 0 && (
            <p className="text-xs text-sky-700 dark:text-sky-300">
              {t('{count} ledger rows already posted in your ERP are left out, so nothing is posted twice.', { count: manifest.previouslyConfirmedExcluded.count })}
            </p>
          )}
          <p className="text-sm text-slate-700 dark:text-slate-200">
            {t('{journals} journals, debits {debits}, credits {credits}', {
              journals: manifest.journalCount,
              debits: f.money(manifest.totalDebits, currencyOf(manifest)),
              credits: f.money(manifest.totalCredits, currencyOf(manifest)),
            })}
          </p>
          <CompanyTable rows={manifest.byCompany} currency={currencyOf(manifest)} />
          {manifest.warnings.map(w => <p key={w} className="text-xs text-amber-700 dark:text-amber-300">{w}</p>)}
          {/* AM-21: a read-only finance user gets no example lines; say so instead of showing nothing. */}
          {manifest.sample === null && (
            <p data-testid="journal-export-lines-hidden" className="text-xs text-slate-500 dark:text-slate-400">
              {t('Line detail is shown to people who can download the file. You can see totals and balances.')}
            </p>
          )}
          {manifest.exportId && (
            <button type="button" className="btn-secondary text-xs" onClick={() => download({ id: manifest.exportId!, fileName: manifest.fileName })}>
              {t('Download file')}
            </button>
          )}
        </div>
      )}

      <div>
        <h4 className="mb-2 text-xs font-semibold uppercase text-slate-500">{t('Export history for this month')}</h4>
        {historyError ? (
          <div role="alert" className="text-xs text-rose-600 dark:text-rose-400">
            {historyError} <button type="button" className="underline" onClick={loadHistory}>{t('Try again')}</button>
          </div>
        ) : history === null ? (
          <p className="text-xs text-slate-400">{t('Loading export history…')}</p>
        ) : history.length === 0 ? (
          <p className="text-xs text-slate-500">{t('No export files for this month yet.')}</p>
        ) : (
          <ul className="divide-y divide-slate-100 dark:divide-white/5" data-testid="journal-export-history">
            {history.map(row => (
              <li key={row.id} className="py-2">
                <div className="flex flex-wrap items-center gap-2 text-xs">
                  <span className="font-mono text-slate-700 dark:text-slate-200">{row.fileName}</span>
                  <Pill tone={STATUS_TONE[row.status] ?? STATUS_TONE.Superseded}>{t(STATUS_LABEL[row.status] ?? row.status)}</Pill>
                  {row.kind !== 'Full' && <Pill tone={STATUS_TONE.PartiallyConfirmed}>{t(KIND_LABEL[row.kind] ?? row.kind)}</Pill>}
                  <span className="text-slate-500">{f.money(row.totalDebits, row.currency || null)}</span>
                  <span className="text-slate-400">{t('Exported by {name} on {date}', { name: row.exportedByName, date: f.dateTime(row.exportedAtUtc) })}</span>
                  <span className="ms-auto flex gap-2">
                    <button type="button" className="btn-secondary h-7 px-2 text-xs" onClick={() => download(row)}>{t('Download file')}</button>
                    <button type="button" className="btn-secondary h-7 px-2 text-xs" aria-expanded={openId === row.id}
                      onClick={() => setOpenId(openId === row.id ? null : row.id)}>
                      {openId === row.id ? t('Hide ERP result') : t('Record ERP result')}
                    </button>
                  </span>
                </div>
                {row.status === 'Superseded' && row.supersedeReason && (
                  <p className="mt-1 text-xs text-slate-500">{row.supersedeReason}</p>
                )}
                {openId === row.id && <ExportOutcome id={row.id} onChanged={loadHistory} />}
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

function CompanyTable({ rows, currency }: { rows: CompanyBalance[]; currency: string | null }) {
  const t = useT();
  const f = useFormat();
  if (rows.length === 0) return null;
  return (
    <table className="mt-2 w-full text-xs">
      <thead>
        <tr className="text-start text-slate-500">
          <th className="py-1 text-start font-medium">{t('Company code')}</th>
          <th className="py-1 text-end font-medium">{t('Debits')}</th>
          <th className="py-1 text-end font-medium">{t('Credits')}</th>
          <th className="py-1 text-end font-medium">{t('Balance check')}</th>
        </tr>
      </thead>
      <tbody>
        {rows.map(r => (
          <tr key={r.companyCode} className="border-t border-slate-100 dark:border-white/5">
            <td className="py-1 font-mono">{r.companyCode}</td>
            <td className="py-1 text-end tabular-nums">{f.plain.money(r.debits, currency)}</td>
            <td className="py-1 text-end tabular-nums">{f.plain.money(r.credits, currency)}</td>
            <td className={`py-1 text-end font-semibold ${r.balanced ? 'text-emerald-700 dark:text-emerald-300' : 'text-rose-700 dark:text-rose-300'}`}>
              {r.balanced ? t('Balanced') : t('Out of balance')}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

interface OutcomeRow {
  journalRef: string;
  status: JournalOutcome;
  debits: number;
  companyCode: string;
  erpDocumentNumber: string | null;
  reason: string | null;
}

/** What the ERP said, journal by journal (or for the whole file on an older export). */
function ExportOutcome({ id, onChanged }: { id: string; onChanged: () => void }) {
  const t = useT();
  const f = useFormat();
  const [detail, setDetail] = useState<JournalExportDetail | null>(null);
  const [loadError, setLoadError] = useState('');
  const [numbers, setNumbers] = useState<Record<string, string>>({});
  const [dates, setDates] = useState<Record<string, string>>({});
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [actionError, setActionError] = useState('');
  const [saving, setSaving] = useState('');

  const load = useCallback(() => {
    setLoadError('');
    financeErpApi.get(id).then(setDetail).catch(err => setLoadError(apiErrorReason(err, t('This export could not be loaded.'))));
  }, [id, t]);
  useEffect(() => { load(); }, [load]);

  const act = async (key: string, call: () => Promise<unknown>) => {
    setSaving(key);
    setActionError('');
    try { await call(); load(); onChanged(); }
    catch (err) { setActionError(apiErrorReason(err, t('The ERP result could not be saved.'))); }
    finally { setSaving(''); }
  };

  if (loadError) return <p role="alert" className="mt-2 text-xs text-rose-600 dark:text-rose-400">{loadError}</p>;
  if (!detail) return <p className="mt-2 text-xs text-slate-400">{t('Loading journals…')}</p>;

  const currency = detail.export.currency || null;
  const wholeFileOnly = detail.journals.length === 0;
  const keyOf = (ref: string) => (wholeFileOnly ? '__file' : ref);
  const outcomeLabel = (s: JournalOutcome) => t(STATUS_LABEL[s] ?? s);
  // An export taken before per-journal outcomes existed is decided as one file.
  const rows: OutcomeRow[] = wholeFileOnly
    ? [{ journalRef: t('Whole file'), status: detail.export.status, debits: detail.export.totalDebits, companyCode: detail.export.companyCode,
        erpDocumentNumber: detail.export.erpDocumentNumber, reason: detail.export.rejectionReason }]
    : detail.journals;

  return (
    <div className="mt-2 space-y-2 rounded-lg bg-slate-50 p-3 dark:bg-white/5" data-testid={`journal-export-outcome-${id}`}>
      {detail.newRowsInScopeSinceExport.count > 0 && (
        <p className="text-xs text-amber-700 dark:text-amber-300">
          {t('{count} ledger rows were posted to this month after this file was created. Create a supplementary export to send them.', { count: detail.newRowsInScopeSinceExport.count })}
        </p>
      )}
      {actionError && <p role="alert" className="text-xs text-rose-600 dark:text-rose-400" data-testid="journal-outcome-error">{actionError}</p>}
      {rows.map(j => {
          const k = keyOf(j.journalRef);
          const pending = j.status === 'Pending' || j.status === 'Exported';
          return (
            <div key={k} className="rounded-md border border-slate-200 bg-white p-2 text-xs dark:border-white/10 dark:bg-transparent">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-mono">{j.journalRef}</span>
                <span className="text-slate-500">{j.companyCode}</span>
                <span className="tabular-nums text-slate-600 dark:text-slate-300">{f.plain.money(j.debits, currency)}</span>
                <Pill tone={STATUS_TONE[j.status] ?? STATUS_TONE.Superseded}>{outcomeLabel(j.status)}</Pill>
                {j.erpDocumentNumber && <span className="text-slate-500">{t('ERP document {number}', { number: j.erpDocumentNumber })}</span>}
                {j.reason && <span className="text-rose-600 dark:text-rose-300">{j.reason}</span>}
              </div>
              {pending && detail.export.status !== 'Superseded' && (
                <div className="mt-2 grid gap-2 sm:grid-cols-[1fr_auto_auto] sm:items-end">
                  <div className="grid grid-cols-2 gap-2">
                    <label>{t('ERP document number')}
                      <input className="input mt-1 w-full" value={numbers[k] ?? ''} onChange={e => setNumbers(p => ({ ...p, [k]: e.target.value }))} />
                    </label>
                    <label>{t('Posted on')}
                      <input type="date" className="input mt-1 w-full" value={dates[k] ?? ''} onChange={e => setDates(p => ({ ...p, [k]: e.target.value }))} />
                    </label>
                  </div>
                  <button type="button" className="btn-primary h-8 px-3 text-xs" disabled={!!saving || !(numbers[k] ?? '').trim()}
                    onClick={() => act(`c-${k}`, () => {
                      const postedAtUtc = dates[k] ? `${dates[k]}T00:00:00Z` : null;
                      return wholeFileOnly
                        ? financeErpApi.confirm(id, { erpDocumentNumber: numbers[k].trim(), postedAtUtc })
                        : financeErpApi.confirmJournals(id, [{ journalRef: j.journalRef, erpDocumentNumber: numbers[k].trim(), postedAtUtc }]);
                    })}>
                    {saving === `c-${k}` ? t('Saving…') : t('Confirm posted')}
                  </button>
                  <div className="flex items-end gap-2">
                    <label className="flex-1">{t('Reason the ERP refused it')}
                      <input className="input mt-1 w-full" value={reasons[k] ?? ''} onChange={e => setReasons(p => ({ ...p, [k]: e.target.value }))} />
                    </label>
                    <button type="button" className="btn-secondary h-8 px-3 text-xs" disabled={!!saving || !(reasons[k] ?? '').trim()}
                      onClick={() => act(`r-${k}`, () => wholeFileOnly
                        ? financeErpApi.reject(id, reasons[k].trim())
                        : financeErpApi.rejectJournals(id, [{ journalRef: j.journalRef, reason: reasons[k].trim() }]))}>
                      {saving === `r-${k}` ? t('Saving…') : t('Record rejection')}
                    </button>
                  </div>
                </div>
              )}
            </div>
          );
        })}
    </div>
  );
}

export default JournalExportsPanel;
