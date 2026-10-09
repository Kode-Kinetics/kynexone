'use client';

import { useId, useRef, useState } from 'react';
import { AlertTriangle, ArrowRight, CheckCircle2, ChevronDown, Download, FileSpreadsheet, RefreshCw, ShieldCheck, UploadCloud, XCircle } from 'lucide-react';
import { orgStructureImportApi, type MigrationImportBatchDto, type OrgStructureImportRequest, type OrgStructureImportResult } from '../api/setupAssistant';
import { useReleaseA } from '../lib/releaseA';

const IMPORT_KEYS: { key: keyof OrgStructureImportRequest; section: string; label: string; phase: string; dependsOn: string[]; required: string[] }[] = [
  { key: 'companiesCsv', section: 'companies', label: 'Legal entities', phase: 'Foundation', dependsOn: [], required: ['LegalNameEn', 'CountryCode', 'DefaultCurrency'] },
  { key: 'branchesCsv', section: 'branches', label: 'Branches', phase: 'Entity wiring', dependsOn: ['companies'], required: ['CompanyLegalName', 'Code', 'NameEn'] },
  { key: 'costCentersCsv', section: 'costCenters', label: 'Cost centers', phase: 'Finance wiring', dependsOn: ['companies'], required: ['CompanyLegalName', 'Code', 'Name'] },
  { key: 'departmentsCsv', section: 'departments', label: 'Departments', phase: 'Org hierarchy', dependsOn: ['branches', 'costCenters'], required: ['Code', 'NameEn'] },
  { key: 'gradesCsv', section: 'grades', label: 'Grades & salary bands', phase: 'Compensation rules', dependsOn: [], required: ['Code', 'Name', 'MinSalary', 'MaxSalary'] },
  { key: 'gradePayComponentsCsv', section: 'gradePayComponents', label: 'Grade pay breakdown', phase: 'Payroll rules', dependsOn: ['grades'], required: ['GradeCode', 'ComponentCode', 'ComponentName'] },
  { key: 'designationsCsv', section: 'designations', label: 'Designations', phase: 'Position eligibility', dependsOn: ['departments', 'grades'], required: ['Code', 'TitleEn'] },
  { key: 'positionsCsv', section: 'positions', label: 'Positions', phase: 'Headcount control', dependsOn: ['companies', 'branches', 'departments', 'designations', 'grades'], required: ['Code', 'Title'] },
];

export function OrgStructureImportPanel() {
  // Release A: the grade pay breakdown file is not offered — the server would skip its rows (they are set in Benefits by grade).
  const releaseA = useReleaseA();
  const importKeys = releaseA ? IMPORT_KEYS.filter(x => x.key !== 'gradePayComponentsCsv') : IMPORT_KEYS;
  const [payload, setPayload] = useState<OrgStructureImportRequest>({});
  const [batch, setBatch] = useState<MigrationImportBatchDto | null>(null);
  const [loading, setLoading] = useState('');
  const [error, setError] = useState('');
  const [packageName, setPackageName] = useState('');
  const [fileNames, setFileNames] = useState<Partial<Record<keyof OrgStructureImportRequest, string>>>({});
  const [templateDownloaded, setTemplateDownloaded] = useState(false);
  const resultsRef = useRef<HTMLDivElement>(null);
  const busyRef = useRef(false);
  const payloadRevision = useRef(0);
  const inputId = useId();

  // The ref closes the gap before React paints disabled controls. Every operation
  // shares this lock so file reads, validation, refresh and apply cannot overlap.
  const begin = (operation: string) => {
    if (busyRef.current) return false;
    busyRef.current = true;
    setLoading(operation);
    setError('');
    return true;
  };
  const finish = () => { busyRef.current = false; setLoading(''); };
  const invalidateBatch = () => {
    payloadRevision.current += 1;
    setBatch(null);
    return payloadRevision.current;
  };

  // Any payload mutation changes the package checksum, so a prior dry-run is no longer
  // committable — drop the batch to force re-validation (§6 invalidation rule).
  const setFile = async (key: keyof OrgStructureImportRequest, file?: File) => {
    if (!file || !begin('reading')) return;
    const revision = invalidateBatch();
    try {
      const text = await file.text();
      if (revision !== payloadRevision.current) return;
      setPayload(p => ({ ...p, [key]: text }));
      setFileNames(f => ({ ...f, [key]: file.name }));
      setTemplateDownloaded(false);
    } catch {
      setError(`Could not read ${file.name}. Choose the file again.`);
    } finally { finish(); }
  };

  const clearFile = (key: keyof OrgStructureImportRequest) => {
    if (busyRef.current) return;
    invalidateBatch();
    setPayload(p => ({ ...p, [key]: undefined }));
    setFileNames(f => { const next = { ...f }; delete next[key]; return next; });
    setError('');
  };

  const setPackageFile = async (file?: File) => {
    if (!file || !begin('reading')) return;
    const revision = invalidateBatch();
    try {
      const text = await file.text();
      if (revision !== payloadRevision.current) return;
      const parsed = splitOrgPackage(text);
      if (Object.values(parsed).every(v => !v)) {
        setError('This package format was not recognized. Download the template, fill in your organization details, and upload it again.');
        return;
      }
      // A package is a replacement. Only individual section uploads intentionally
      // merge into it; retaining omitted sections would import a previous file's data.
      setPayload(parsed);
      setFileNames({});
      setPackageName(file.name);
      setTemplateDownloaded(false);
    } catch {
      setError(`Could not read ${file.name}. Choose the file again.`);
    } finally { finish(); }
  };

  const clearPackage = () => {
    if (busyRef.current) return;
    invalidateBatch();
    setPayload({});
    setFileNames({});
    setPackageName('');
    setError('');
  };

  const template = async () => {
    if (!begin('template')) return;
    try {
      downloadText(await orgStructureImportApi.template(), 'organization-structure-import-package.txt');
      setTemplateDownloaded(true);
    }
    catch { setError('Could not download organization structure template.'); }
    finally { finish(); }
  };

  const runValidation = async () => {
    if (!Object.values(payload).some(Boolean) || !begin('dryrun')) return;
    const revision = payloadRevision.current;
    // A failed revalidation must not leave an earlier passing batch committable.
    setBatch(null);
    try {
      const dto = await orgStructureImportApi.dryRun(payload);
      if (revision !== payloadRevision.current) return;
      setBatch(dto);
      requestAnimationFrame(() => resultsRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }));
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Could not validate the organization structure package.');
    } finally { finish(); }
  };

  const commitImport = async () => {
    if (batch?.status !== 'DryRunPassed' || !begin('commit')) return;
    const revision = payloadRevision.current;
    try {
      const dto = await orgStructureImportApi.commitBatch(batch.id);
      if (revision !== payloadRevision.current) return;
      setBatch(dto);
      requestAnimationFrame(() => resultsRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }));
    } catch (e: unknown) {
      const err = e as { response?: { status?: number; data?: unknown } };
      const status = err?.response?.status;
      const data = err?.response?.data;
      // 422: CommitBatch re-validates against live DB state and returns a full batch DTO
      // carrying the blocking findings when the workspace changed since dry-run (or the
      // stored payload can't be read). Surface those findings and prompt a re-validate.
      if (revision !== payloadRevision.current) return;
      if (status === 422) {
        setBatch(isBatchDto(data) ? data : null);
        setError('The import could not be applied. Review the findings and validate your files again.');
      } else if (status === 409) {
        // Fires when the batch is no longer DryRunPassed (e.g. Committing/Failed). §6 nulls
        // the batch on any payload change, so in practice we only ever commit a clean batch;
        // this is a defensive branch and the copy avoids overstating a checksum re-check.
        setBatch(isBatchDto(data) ? data : null);
        setError('This import can no longer be applied. Validate your files again.');
      } else {
        setError((data as { message?: string })?.message ?? 'Could not apply the import. Try again or validate your files again.');
      }
    } finally { finish(); }
  };

  const refreshStatus = async () => {
    if (!batch || !begin('refresh')) return;
    const revision = payloadRevision.current;
    try {
      const dto = await orgStructureImportApi.getBatch(batch.id);
      if (revision === payloadRevision.current) setBatch(dto);
    }
    catch { setError('Could not refresh the batch status.'); }
    finally { finish(); }
  };

  const hasAny = Object.values(payload).some(Boolean);
  const loadedCount = importKeys.filter(x => payload[x.key]).length;
  const totalRows = importKeys.reduce((sum, x) => sum + countCsvRows(payload[x.key]), 0);
  const blockingCount = batch?.errorRows ?? 0;
  const warningCount = batch?.result?.warnings ?? 0;
  const blockingGroups = groupFindings(batch?.result ?? null, 'errors');
  const warningGroups = groupFindings(batch?.result ?? null, 'warnings');
  const batchErrors = Array.isArray(batch?.errors) ? batch.errors : [];
  const canCommit = batch?.status === 'DryRunPassed';
  const commitReason = !batch ? 'Validate your files to review the changes.'
    : batch.status === 'DryRunBlocked' ? `Resolve ${blockingCount} blocking issue(s), then validate again.`
    : batch.status === 'Committed' ? 'This import has been applied.'
    : batch.status === 'DryRunPassed' ? ''
    : 'Validate again before applying this import.';
  const validationBanner = batch?.status === 'DryRunPassed'
    ? 'Validation complete. Review the changes below before applying your import.'
    : batch?.status === 'DryRunBlocked'
    ? `Resolve ${blockingCount} blocking issue(s) before applying. ${warningCount} warning(s) to review.`
    : batch?.status === 'Committed'
    ? 'Your organization import has been applied.'
    : 'Review the import status below. You may need to validate your files again.';
  const runway = [
    { label: 'Upload files', done: hasAny, active: !hasAny },
    { label: 'Validate', done: Boolean(batch && ['DryRunPassed', 'Committed'].includes(batch.status)), active: hasAny && !canCommit && batch?.status !== 'Committed' },
    { label: 'Review & apply', done: batch?.status === 'Committed', active: canCommit },
  ];
  const fileInputClass = 'block w-full min-w-0 rounded-lg text-sm text-slate-600 file:me-3 file:cursor-pointer file:rounded-md file:border file:border-slate-200 file:bg-white file:px-3 file:py-2 file:text-sm file:font-medium file:text-slate-700 hover:file:border-sapphire/40 hover:file:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire/50 focus-visible:ring-offset-2 dark:text-slate-300 dark:file:border-white/15 dark:file:bg-white/5 dark:file:text-slate-200 dark:hover:file:bg-white/10 dark:focus-visible:ring-offset-slate-900';

  return (
    <section aria-labelledby={`${inputId}-heading`} className="overflow-hidden rounded-xl border border-slate-200 bg-white dark:border-white/10 dark:bg-white/[0.03]">
      <fieldset disabled={Boolean(loading)} aria-busy={Boolean(loading)} className="min-w-0">
      <div className="border-b border-slate-200 px-5 py-6 sm:px-7 dark:border-white/10">
        <h3 id={`${inputId}-heading`} className="text-xl font-semibold tracking-tight text-slate-900 dark:text-white">Import your organization</h3>
        <p className="mt-2 max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-300">
          Bring your existing companies, teams, and job structure into KynexOne. Review every change before it is applied.
        </p>
        <ol aria-label="Import progress" className="mt-5 flex flex-wrap items-center gap-x-5 gap-y-3">
          {runway.map((step, idx) => (
            <li key={step.label} aria-current={step.active ? 'step' : undefined} className={`flex items-center gap-2 text-sm ${step.active ? 'font-semibold text-sapphire dark:text-cyanAccent' : 'text-slate-600 dark:text-slate-300'}`}>
              <span className={`flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-xs font-semibold ${step.done ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300' : step.active ? 'bg-sapphire text-white dark:bg-cyanAccent dark:text-slate-900' : 'bg-slate-100 dark:bg-white/10'}`}>
                {step.done ? <CheckCircle2 className="h-4 w-4" aria-hidden="true" /> : idx + 1}
              </span>
              {step.label}
              {idx < runway.length - 1 && <ArrowRight className="ms-2 hidden h-4 w-4 text-slate-400 sm:block" aria-hidden="true" />}
            </li>
          ))}
        </ol>
      </div>

      <div className="px-5 py-6 sm:px-7">
        <div className="flex flex-col gap-5 rounded-lg border border-dashed border-slate-300 bg-slate-50/70 p-5 sm:flex-row sm:items-start dark:border-white/20 dark:bg-white/[0.02]">
          <UploadCloud className="h-7 w-7 shrink-0 text-sapphire dark:text-cyanAccent" aria-hidden="true" />
          <div className="min-w-0 flex-1">
            <label htmlFor={`${inputId}-package`} className="block text-base font-semibold text-slate-900 dark:text-white">Upload an organization package</label>
            <p id={`${inputId}-package-help`} className="mt-1 max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-300">Use the template to prepare your organization, then choose your completed .txt or .csv package. A new package replaces the files currently loaded.</p>
            <input id={`${inputId}-package`} type="file" accept=".txt,.csv,text/plain,text/csv" aria-describedby={`${inputId}-package-help`} onChange={e => { void setPackageFile(e.target.files?.[0]); e.target.value = ''; }} className={`mt-4 ${fileInputClass}`} />
            {packageName && (
              <div className="mt-3 flex flex-wrap items-center gap-2 text-sm" role="status">
                <CheckCircle2 className="h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-300" aria-hidden="true" />
                <span className="min-w-0 break-all font-medium text-slate-800 dark:text-slate-200">{packageName}</span>
                <button type="button" className="rounded text-slate-600 underline underline-offset-4 hover:text-slate-900 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire dark:text-slate-300 dark:hover:text-white" onClick={clearPackage}>Remove package</button>
              </div>
            )}
          </div>
          <div className="sm:max-w-48">
            <button type="button" className="btn-secondary whitespace-nowrap disabled:cursor-wait disabled:opacity-60" onClick={template} disabled={loading === 'template'}><Download className="h-4 w-4" aria-hidden="true" />{loading === 'template' ? 'Downloading…' : 'Download template'}</button>
            {templateDownloaded && <p role="status" className="mt-2 text-sm leading-5 text-emerald-700 dark:text-emerald-300">Template downloaded. Fill it in, then upload it here.</p>}
          </div>
        </div>

        <details className="group mt-5 border-b border-slate-200 pb-5 dark:border-white/10">
          <summary className="flex cursor-pointer list-none items-center justify-between gap-3 rounded text-sm font-semibold text-slate-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire [&::-webkit-details-marker]:hidden dark:text-slate-200">
            Upload individual CSV files instead
            <ChevronDown className="h-4 w-4 shrink-0 transition-transform group-open:rotate-180" aria-hidden="true" />
          </summary>
          <p className="mt-2 max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-300">Add only the sections you need. Related records can be in these files or already in your workspace; validation checks the connections.</p>
          <div className="mt-4 divide-y divide-slate-200 dark:divide-white/10">
            {importKeys.map(item => {
              const rows = countCsvRows(payload[item.key]);
              const loaded = Boolean(payload[item.key]);
              return (
                <div key={item.key} className="py-4 first:pt-0 last:pb-0">
                  <div className="grid items-start gap-3 sm:grid-cols-[minmax(180px,0.7fr)_minmax(0,1fr)]">
                    <div>
                      <label htmlFor={`${inputId}-${item.key}`} className="text-sm font-medium text-slate-900 dark:text-white">{item.label}</label>
                      {loaded && <p className="mt-1 break-all text-sm text-slate-600 dark:text-slate-300">{fileNames[item.key] ?? 'From organization package'} · {rows} {rows === 1 ? 'row' : 'rows'}</p>}
                      <details className="mt-1 text-xs text-slate-600 dark:text-slate-300">
                        <summary className="w-fit cursor-pointer rounded py-1 underline underline-offset-4 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire">File format</summary>
                        <div className="mt-1 space-y-2 leading-5">
                          <p>Required columns: <span className="break-words font-mono">{item.required.join(', ')}</span>.</p>
                          {item.dependsOn.length > 0 && <p>References {item.dependsOn.map(sectionLabel).join(', ')} in your files or workspace.</p>}
                          {['companiesCsv', 'gradesCsv'].includes(item.key) && <p>Requires group-level access to import.</p>}
                        </div>
                      </details>
                    </div>
                    <div className="min-w-0">
                      <input id={`${inputId}-${item.key}`} type="file" accept=".csv,text/csv" onChange={e => { void setFile(item.key, e.target.files?.[0]); e.target.value = ''; }} className={fileInputClass} />
                      {loaded && <button type="button" className="mt-2 rounded text-sm text-slate-600 underline underline-offset-4 hover:text-slate-900 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire dark:text-slate-300 dark:hover:text-white" onClick={() => clearFile(item.key)} aria-label={`Remove ${item.label.toLowerCase()} file`}>Remove file</button>}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        </details>

        {hasAny && (
          <div className="mt-5" aria-live="polite">
            <p className="text-sm font-medium text-slate-900 dark:text-white">{loadedCount} {loadedCount === 1 ? 'section' : 'sections'} loaded · approximately {totalRows} {totalRows === 1 ? 'row' : 'rows'}</p>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-300">{importKeys.filter(item => payload[item.key]).map(item => item.label).join(', ')}</p>
          </div>
        )}

        {loading === 'reading' && <p role="status" className="mt-5 text-sm text-slate-600 dark:text-slate-300">Reading your file…</p>}
        {error && <p role="alert" className="mt-5 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm leading-6 text-red-700 dark:border-red-500/20 dark:bg-red-500/10 dark:text-red-300">{error}</p>}

        <div className="mt-5 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <p id={`${inputId}-validation-help`} className="flex max-w-2xl items-start gap-2 text-sm leading-6 text-slate-600 dark:text-slate-300">
            <ShieldCheck className="mt-1 h-4 w-4 shrink-0" aria-hidden="true" />
            {hasAny ? 'Validation checks your files against your workspace. Organization records change only when you apply the import.' : 'Choose a package or CSV file to begin. You will review the proposed changes before applying them.'}
          </p>
          <button type="button" className={`${batch ? 'btn-secondary' : 'btn-primary'} shrink-0 self-start disabled:cursor-not-allowed disabled:opacity-50 disabled:transform-none sm:self-auto`} onClick={runValidation} disabled={!hasAny || loading === 'dryrun'} aria-describedby={`${inputId}-validation-help`}>
            <ShieldCheck className="h-4 w-4" aria-hidden="true" />{loading === 'dryrun' ? 'Validating…' : 'Validate files'}
          </button>
        </div>
      </div>

      <div ref={resultsRef} className="border-t border-slate-200 px-5 py-5 sm:px-7 dark:border-white/10">
        {!batch ? (
          <div className="flex items-start gap-3 text-slate-600 dark:text-slate-300">
            <FileSpreadsheet className="mt-0.5 h-5 w-5 shrink-0" aria-hidden="true" />
            <div>
              <h4 className="text-sm font-semibold text-slate-800 dark:text-slate-200">Your review appears here</h4>
              <p className="mt-1 text-sm leading-6">After validation, see what will be added or updated and any issues to resolve.</p>
            </div>
          </div>
        ) : (
          <>
            <div className="flex flex-wrap items-center justify-between gap-3">
              <h4 className="text-base font-semibold text-slate-900 dark:text-white">Review your import</h4>
              <StatusPill status={batch.status} />
            </div>
            <p aria-live="polite" role="status" className={`mt-2 text-sm leading-6 ${batch.status === 'DryRunBlocked' || batch.status === 'Failed' ? 'text-red-700 dark:text-red-300' : 'text-slate-600 dark:text-slate-300'}`}>{validationBanner}</p>
            <dl className="mt-5 grid grid-cols-2 gap-x-6 gap-y-4 border-y border-slate-200 py-4 sm:grid-cols-4 dark:border-white/10">
              <Impact label={batch.status === 'Committed' ? 'Added' : 'To add'} value={batch.createdRows} />
              <Impact label={batch.status === 'Committed' ? 'Updated' : 'To update'} value={batch.updatedRows} />
              <Impact label="Skipped" value={batch.skippedRows} />
              <Impact label="Blocking issues" value={batch.errorRows} />
            </dl>
            <CountStrip label="Files checked" counts={batch.reconciliation.sectionCounts} />
            <FindingGroup title="Issues to resolve" icon={<XCircle className="h-4 w-4" />} groups={blockingGroups} tone="danger" />
            {Object.keys(blockingGroups).length === 0 && batchErrors.length > 0 && (
              <div role="alert" className="mt-4 rounded-lg bg-red-50 p-4 text-sm leading-6 text-red-700 dark:bg-red-500/10 dark:text-red-200">
                <p className="font-semibold">Issues to resolve</p>
                <ul className="mt-2 list-disc space-y-1 ps-5">{batchErrors.map((message, index) => <li key={index}>{message}</li>)}</ul>
              </div>
            )}
            <FindingGroup title="Warnings to review" icon={<AlertTriangle className="h-4 w-4" />} groups={warningGroups} tone="warning" />
            {batch.status === 'Committed' && batch.result && Object.keys(batch.result.applied ?? {}).length > 0 && (
              <p className="mt-4 flex items-start gap-2 text-sm leading-6 text-emerald-700 dark:text-emerald-300"><CheckCircle2 className="mt-1 h-4 w-4 shrink-0" aria-hidden="true" />Applied: {Object.entries(batch.result.applied).map(([k, v]) => `${sectionLabel(k)}: ${v}`).join(' · ')}</p>
            )}
            <details className="mt-5 text-sm text-slate-600 dark:text-slate-300">
              <summary className="w-fit cursor-pointer rounded py-1 underline underline-offset-4 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sapphire">Import details & audit record</summary>
              <div className="mt-3 space-y-2 break-words text-xs leading-5">
                <p><span className="font-medium">Batch:</span> {batch.externalBatchId ?? batch.id}</p>
                <p><span className="font-medium">Checksum:</span> <span className="break-all font-mono">{batch.packageChecksum}</span></p>
                <p>Created {formatUtc(batch.createdAtUtc)}{batch.completedAtUtc ? ` · Completed ${formatUtc(batch.completedAtUtc)}` : ''}</p>
                <p>{batch.receivedRows} rows received.</p>
                <CountStrip label="Existing workspace records" counts={{ ...batch.reconciliation.identityCounts, ...batch.reconciliation.operationalCounts }} muted />
              </div>
            </details>
            <div className="mt-5 flex flex-wrap items-center justify-between gap-4 border-t border-slate-200 pt-5 dark:border-white/10">
              <button type="button" className="btn-secondary disabled:cursor-wait disabled:opacity-60" onClick={refreshStatus} disabled={loading === 'refresh'}><RefreshCw className="h-4 w-4" aria-hidden="true" />{loading === 'refresh' ? 'Refreshing…' : 'Refresh status'}</button>
              <div className="flex flex-wrap items-center gap-3">
                {commitReason && <p id={`${inputId}-commit-reason`} className={`max-w-md text-sm ${batch.status === 'DryRunBlocked' ? 'text-red-700 dark:text-red-300' : 'text-slate-600 dark:text-slate-300'}`}>{commitReason}</p>}
                <button type="button" className="btn-primary disabled:cursor-not-allowed disabled:opacity-50 disabled:transform-none" onClick={commitImport} disabled={!canCommit || loading === 'commit'} aria-describedby={commitReason ? `${inputId}-commit-reason` : undefined}><CheckCircle2 className="h-4 w-4" aria-hidden="true" />{loading === 'commit' ? 'Applying…' : batch.status === 'Committed' ? 'Import applied' : 'Apply import'}</button>
              </div>
            </div>
          </>
        )}
      </div>
      </fieldset>
    </section>
  );
}

function isBatchDto(v: unknown): v is MigrationImportBatchDto {
  return typeof v === 'object' && v !== null && 'id' in v && 'status' in v && 'reconciliation' in v;
}

function formatUtc(iso?: string): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString();
}

function StatusPill({ status }: { status: string }) {
  const map: Record<string, { label: string; cls: string }> = {
    DryRunPassed: { label: 'Ready to apply', cls: 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-200' },
    DryRunBlocked: { label: 'Needs attention', cls: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-200' },
    Committed: { label: 'Applied', cls: 'bg-slate-200 text-slate-700 dark:bg-white/10 dark:text-slate-200' },
    Failed: { label: 'Failed', cls: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-200' },
  };
  const s = map[status] ?? { label: status || 'Pending', cls: 'bg-slate-100 text-slate-600 dark:bg-white/10 dark:text-slate-300' };
  return <span className={`rounded-full px-2.5 py-1 text-[11px] font-semibold ${s.cls}`}>{s.label}</span>;
}

function sectionLabel(section: string): string {
  return IMPORT_KEYS.find(item => item.section.toLowerCase() === section.toLowerCase())?.label
    ?? section.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, letter => letter.toUpperCase());
}

function CountStrip({ label, counts, muted = false }: { label: string; counts: Record<string, number>; muted?: boolean }) {
  const entries = Object.entries(counts ?? {});
  if (entries.length === 0) return null;
  return (
    <div className="mt-3">
      <p className="text-sm font-medium text-slate-700 dark:text-slate-300">{label}</p>
      <div className="mt-1.5 flex flex-wrap gap-1.5">
        {entries.map(([k, v]) => (
          <span key={k} className={`rounded px-2 py-1 text-xs ${muted ? 'bg-white text-slate-600 ring-1 ring-slate-200 dark:bg-black/20 dark:text-slate-300 dark:ring-white/10' : 'bg-slate-100 text-slate-700 dark:bg-white/5 dark:text-slate-300'}`}>{sectionLabel(k)}: {v}</span>
        ))}
      </div>
    </div>
  );
}

function Impact({ label, value }: { label: string; value: number }) {
  return <div><dt className="text-sm text-slate-600 dark:text-slate-300">{label}</dt><dd className="mt-1 text-xl font-semibold tabular-nums text-slate-900 dark:text-white">{value}</dd></div>;
}

function FindingGroup({ title, icon, groups, tone }: { title: string; icon: React.ReactNode; groups: Record<string, string[]>; tone: 'danger' | 'warning' }) {
  const entries = Object.entries(groups);
  if (entries.length === 0) return null;
  const text = tone === 'danger' ? 'text-red-700 dark:text-red-200' : 'text-amber-700 dark:text-amber-200';
  const bg = tone === 'danger' ? 'bg-red-50 dark:bg-red-500/10' : 'bg-amber-50 dark:bg-amber-500/10';
  return (
    <div className={`mt-4 rounded-lg p-4 text-sm leading-6 ${bg}`}>
      <p className={`flex items-center gap-2 font-semibold ${text}`}>{icon}{title}</p>
      <div className="mt-2 max-h-56 space-y-2 overflow-auto pe-1">
        {entries.map(([section, findings]) => (
          <div key={section}>
            <p className="font-semibold text-slate-800 dark:text-white">{sectionLabel(section)}</p>
            {findings.map((finding, idx) => <p key={`${section}-${idx}`} className={`break-words ${text}`}>{finding}</p>)}
          </div>
        ))}
      </div>
    </div>
  );
}

function downloadText(content: string, filename: string) {
  const blob = new Blob([content], { type: 'text/plain' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

function countCsvRows(content?: string): number {
  if (!content) return 0;
  return content.split(/\r?\n/).map(x => x.trim()).filter(Boolean).slice(1).length;
}

function splitOrgPackage(content: string): OrgStructureImportRequest {
  const out: OrgStructureImportRequest = {};
  const sectionToKey = Object.fromEntries(IMPORT_KEYS.map(x => [x.section.toLowerCase(), x.key])) as Record<string, keyof OrgStructureImportRequest>;
  let current: keyof OrgStructureImportRequest | null = null;
  const buffers: Partial<Record<keyof OrgStructureImportRequest, string[]>> = {};
  for (const line of content.replace(/\r\n/g, '\n').split('\n')) {
    const match = line.trim().match(/^#\s*([A-Za-z]+)\s*$/);
    if (match) {
      current = sectionToKey[match[1].toLowerCase()] ?? null;
      if (current && !buffers[current]) buffers[current] = [];
      continue;
    }
    if (current) buffers[current]?.push(line);
  }
  for (const [key, lines] of Object.entries(buffers) as [keyof OrgStructureImportRequest, string[]][]) {
    const csv = lines.join('\n').trim();
    if (csv) out[key] = csv;
  }
  return out;
}

function groupFindings(result: OrgStructureImportResult | null, key: 'errors' | 'warnings'): Record<string, string[]> {
  if (!Array.isArray(result?.rows)) return {};
  return result.rows.reduce<Record<string, string[]>>((acc, row) => {
    const findings = row[key] ?? [];
    if (findings.length === 0) return acc;
    const section = row.entityCode?.split(':')[0] || 'general';
    acc[section] ??= [];
    for (const finding of findings) acc[section].push(`Row ${row.rowNumber || '-'} ${row.entityCode ?? ''}: ${finding}`.trim());
    return acc;
  }, {});
}
