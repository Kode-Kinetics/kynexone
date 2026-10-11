'use client';

import { useCallback, useEffect, useState } from 'react';
import { Download, Upload } from 'lucide-react';
import {
  compensationApi, type BenchmarkImportMeta, type BenchmarkRow, type BenchmarkSet, type ImportIssue, type ImportResult, type PayBasis,
} from '../../api/compensation';
import { designationsApi, gradesApi, type DesignationDto, type GradeDto } from '../../api/organization';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { Badge, Field, FieldError, PanelState, serverMessage, theadClass, thClass } from '../gl/glUi';
import { SyntheticBanner, SyntheticChip } from './compUi';

/**
 * Customer-owned pay benchmarks. KynexOne ships no market data: every figure here was imported by the customer with a
 * provider, a licence or usage-rights statement, a survey date, a country and a currency. Data older than 18 months is
 * labelled stale; a synthetic test set is labelled on every screen that shows it.
 */
export function BenchmarksPanel() {
  const t = useT();
  const f = useFormat();
  const { hasPermission } = useAuth();
  const canManage = hasPermission('compensation.benchmarks.manage');
  const [sets, setSets] = useState<BenchmarkSet[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [importing, setImporting] = useState(false);

  const load = useCallback(async () => {
    setError(null);
    try { setSets(await compensationApi.benchmarkSets()); } catch (err) { setError(serverMessage(err, t)); }
  }, [t]);
  useEffect(() => { void load(); }, [load]);

  const downloadTemplate = async () => {
    const csv = await compensationApi.benchmarkTemplate();
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv' }));
    const a = document.createElement('a');
    a.href = url; a.download = 'pay-benchmark-template.csv'; a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <section className="space-y-4" aria-labelledby="benchmarks-heading">
      <div className="surface flex flex-wrap items-start justify-between gap-3 p-4">
        <div className="max-w-2xl">
          <h3 id="benchmarks-heading" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Pay benchmarks')}</h3>
          <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{t('Import the pay surveys your company has bought or is licensed to use. KynexOne never supplies market figures. Map each job family and level to a designation or grade; worksheets then show where pay sits against the survey median.')}</p>
        </div>
        {canManage && (
          <div className="flex flex-wrap gap-2">
            <button type="button" className="btn-secondary h-9 px-3 text-sm" onClick={downloadTemplate}><Download className="h-4 w-4" aria-hidden /> {t('Download template')}</button>
            <button type="button" className="btn-primary" onClick={() => setImporting((x) => !x)}><Upload className="h-4 w-4" aria-hidden /> {t('Import a survey')}</button>
          </div>
        )}
      </div>
      {importing && <ImportForm onDone={(s) => { setImporting(false); void load(); setOpen(s.id); }} />}
      <PanelState loading={sets == null} error={error} onRetry={load} empty={(sets ?? []).length === 0} emptyLabel={t('No benchmark set yet. Worksheets show “No market data” until one is imported and mapped.')}>
        <div className="overflow-x-auto surface">
          <table className="w-full min-w-[760px] text-sm" data-testid="benchmark-sets">
            <thead className={theadClass}><tr>
              <th className={thClass}>{t('Set')}</th><th className={thClass}>{t('Provider and licence')}</th><th className={thClass}>{t('Survey date')}</th>
              <th className={thClass}>{t('Scope')}</th><th className={thClass}>{t('Rows')}</th><th className={thClass}><span className="sr-only">{t('Open')}</span></th>
            </tr></thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/5">
              {(sets ?? []).map((s) => (
                <tr key={s.id} className={s.isActive ? '' : 'opacity-60'}>
                  <td className="px-3 py-2">
                    <div className="font-medium">{s.name}</div>
                    <div className="mt-1 flex flex-wrap gap-1">
                      {s.isSynthetic && <SyntheticChip t={t} />}
                      {s.isStale && <Badge tone="amber">{t('Older than 18 months')}</Badge>}
                      {!s.isActive && <Badge tone="slate">{t('Retired')}</Badge>}
                    </div>
                  </td>
                  <td className="px-3 py-2 text-xs"><div className="font-medium">{s.provider}</div><div className="max-w-xs text-slate-500 dark:text-slate-400">{s.licenceStatement}</div></td>
                  <td className="px-3 py-2">{f.date(s.surveyDate)}</td>
                  <td className="px-3 py-2 text-xs">{[s.geography, s.currency, s.payBasis === 'MonthlyGross' ? t('Monthly gross') : t('Monthly basic')].join(' · ')}</td>
                  <td className="px-3 py-2 text-xs tabular-nums">{t('{mapped} of {rows} mapped', { mapped: s.mappedRows, rows: s.rowCount })}</td>
                  <td className="px-3 py-2 text-end"><button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={() => setOpen(open === s.id ? null : s.id)}>{t('Open set {name}', { name: s.name })}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </PanelState>
      {open && <SetDetail setId={open} canManage={canManage} onChanged={load} />}
    </section>
  );
}

function ImportForm({ onDone }: { onDone: (s: BenchmarkSet) => void }) {
  const t = useT();
  const { companies, selectedCompanyId } = useCompany();
  const [file, setFile] = useState<File | null>(null);
  const [meta, setMeta] = useState<BenchmarkImportMeta>({
    companyId: selectedCompanyId ?? companies[0]?.id ?? '', name: '', provider: '', licenceStatement: '', surveyDate: '', geography: 'SA', currency: 'SAR', payBasis: 'MonthlyBasic', isSynthetic: false,
  });
  const [issues, setIssues] = useState<ImportIssue[]>([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<BenchmarkImportMeta>) => setMeta((m) => ({ ...m, ...patch }));
  const submit = async () => {
    if (!file) return;
    setBusy(true); setIssues([]); setError('');
    try {
      const r = await compensationApi.importBenchmarks(file, meta);
      if (r.set) onDone(r.set);
    } catch (err) {
      const data = (err as { response?: { data?: ImportResult } })?.response?.data;
      if (data?.issues?.length) setIssues(data.issues); else setError(serverMessage(err, t));
    } finally { setBusy(false); }
  };
  return (
    <div className="surface space-y-3 p-4" data-testid="benchmark-import">
      <h4 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Import a survey')}</h4>
      <div className="grid gap-3 md:grid-cols-2">
        <Field label={t('Company')} required>
          <select className="input w-full" value={meta.companyId} onChange={(e) => set({ companyId: e.target.value })}>
            {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </Field>
        <Field label={t('Set name')} required><input className="input w-full" value={meta.name} maxLength={200} onChange={(e) => set({ name: e.target.value })} /></Field>
        <Field label={t('Survey provider or source')} required><input className="input w-full" value={meta.provider} maxLength={200} onChange={(e) => set({ provider: e.target.value })} /></Field>
        <Field label={t('Survey date')} required><input type="date" className="input w-full" value={meta.surveyDate} onChange={(e) => set({ surveyDate: e.target.value })} /></Field>
        <Field label={t('Country covered (ISO code)')} required><input className="input w-24 uppercase" value={meta.geography} maxLength={3} onChange={(e) => set({ geography: e.target.value.toUpperCase() })} /></Field>
        <Field label={t('Currency')} required><input className="input w-24 uppercase" value={meta.currency} maxLength={3} onChange={(e) => set({ currency: e.target.value.toUpperCase() })} /></Field>
        <Field label={t('Percentiles measure')} required>
          <select className="input w-full" value={meta.payBasis} onChange={(e) => set({ payBasis: e.target.value as PayBasis })}>
            <option value="MonthlyBasic">{t('Monthly basic salary')}</option>
            <option value="MonthlyGross">{t('Monthly gross (basic + fixed allowances)')}</option>
          </select>
        </Field>
        <Field label={t('File (CSV or XLSX)')} required hint={t('Columns: job_family, level, sample_size, p25, p50, p75, p90. Amounts without thousands separators.')}>
          <input type="file" accept=".csv,.xlsx" className="block w-full text-sm" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        </Field>
      </div>
      <Field label={t('Licence or usage-rights statement')} required hint={t('Say what entitles your company to use these figures, for example the contract or subscription.')}>
        <textarea className="input min-h-[64px] w-full" value={meta.licenceStatement} maxLength={2000} onChange={(e) => set({ licenceStatement: e.target.value })} />
      </Field>
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={meta.isSynthetic} onChange={(e) => set({ isSynthetic: e.target.checked })} /> {t('This is synthetic test data, not market data')}</label>
      {issues.length > 0 && (
        <div role="alert" className="rounded-lg border border-rose-300 bg-rose-50 px-3 py-2 text-xs text-rose-800 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200" data-testid="import-issues">
          <p className="mb-1 font-semibold">{t('Nothing was imported. Fix these and try again.')}</p>
          <ul className="list-disc space-y-0.5 ps-5">
            {issues.map((i, n) => <li key={n}>{i.row > 0 ? t('Row {row}, {field}: {message}', { row: i.row, field: i.field, message: i.message }) : `${i.field}: ${i.message}`}</li>)}
          </ul>
        </div>
      )}
      <FieldError error={error} />
      <div className="flex justify-end"><button type="button" className="btn-primary" onClick={submit} disabled={!file || busy}>{busy ? t('Importing…') : t('Import')}</button></div>
    </div>
  );
}

function SetDetail({ setId, canManage, onChanged }: { setId: string; canManage: boolean; onChanged: () => void }) {
  const t = useT();
  const f = useFormat();
  const [data, setData] = useState<{ set: BenchmarkSet; rows: BenchmarkRow[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [designations, setDesignations] = useState<DesignationDto[]>([]);
  const [grades, setGrades] = useState<GradeDto[]>([]);
  const load = useCallback(async () => {
    setError(null);
    try { setData(await compensationApi.benchmarkSet(setId)); } catch (err) { setError(serverMessage(err, t)); }
  }, [setId, t]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    designationsApi.listAll().then(setDesignations).catch(() => setDesignations([]));
    gradesApi.listAll().then(setGrades).catch(() => setGrades([]));
  }, []);
  const map = async (row: BenchmarkRow, designationId: string | null, gradeId: string | null) => {
    try { await compensationApi.mapRow(row.id, designationId, gradeId); await load(); onChanged(); } catch (err) { setError(serverMessage(err, t)); }
  };
  return (
    <PanelState loading={!data && !error} error={error} onRetry={load} empty={false} emptyLabel="">
      {data && (
        <div className="surface space-y-3 p-4" data-testid="benchmark-set-detail">
          <h4 className="text-sm font-semibold text-slate-900 dark:text-white">{data.set.name}</h4>
          {data.set.isSynthetic && <SyntheticBanner t={t} />}
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('Imported by {name} on {date} from {file} (SHA-256 {hash}).', { name: data.set.importedByName, date: f.date(data.set.importedAtUtc), file: data.set.sourceFileName, hash: data.set.sourceFileSha256.slice(0, 12) })}
          </p>
          <div className="overflow-x-auto">
            <table className="w-full min-w-[860px] text-sm">
              <thead className={theadClass}><tr>
                <th className={thClass}>{t('Job family')}</th><th className={thClass}>{t('Level')}</th><th className={thClass}>{t('Sample')}</th>
                <th className={thClass}>{t('P25')}</th><th className={thClass}>{t('P50')}</th><th className={thClass}>{t('P75')}</th><th className={thClass}>{t('P90')}</th>
                <th className={thClass}>{t('Mapped to')}</th>
              </tr></thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {data.rows.map((r) => (
                  <tr key={r.id} data-family={r.jobFamily}>
                    <td className="px-3 py-2 font-medium">{r.jobFamily}</td>
                    <td className="px-3 py-2">{r.level}</td>
                    <td className="px-3 py-2 tabular-nums">{f.integer(r.sampleSize)}</td>
                    {[r.p25, r.p50, r.p75, r.p90].map((p, i) => <td key={i} className="px-3 py-2 tabular-nums">{f.money(p, data.set.currency)}</td>)}
                    <td className="px-3 py-2">
                      {canManage ? (
                        <div className="flex flex-wrap gap-1">
                          <select className="input h-8 text-xs" value={r.designationId ?? ''} aria-label={t('Designation for {family} {level}', { family: r.jobFamily, level: r.level })}
                            onChange={(e) => map(r, e.target.value || null, r.gradeId)}>
                            <option value="">{t('No designation')}</option>
                            {designations.map((d) => <option key={d.id} value={d.id}>{d.titleEn}</option>)}
                          </select>
                          <select className="input h-8 text-xs" value={r.gradeId ?? ''} aria-label={t('Grade for {family} {level}', { family: r.jobFamily, level: r.level })}
                            onChange={(e) => map(r, r.designationId, e.target.value || null)}>
                            <option value="">{t('No grade')}</option>
                            {grades.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
                          </select>
                        </div>
                      ) : (r.designationId || r.gradeId ? t('Mapped') : t('Not mapped'))}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {canManage && data.set.isActive && (
            <button type="button" className="btn-secondary h-8 px-3 text-xs" onClick={async () => { await compensationApi.retireSet(data.set.id); await load(); onChanged(); }}>{t('Retire this set')}</button>
          )}
        </div>
      )}
    </PanelState>
  );
}
