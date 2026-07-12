'use client';

import { useState } from 'react';
import { FileUp, Wand2, CheckCircle2, Trash2, Sparkles } from 'lucide-react';
import { orgStructureImportApi, setupAssistantApi, type CompanyProfile, type OrgStructureImportRequest, type OrgStructureImportResult, type SetupDraft } from '../api/setupAssistant';

const COUNTRIES = [
  { code: 'SA', label: 'Saudi Arabia' }, { code: 'AE', label: 'United Arab Emirates' },
  { code: 'QA', label: 'Qatar' }, { code: 'KW', label: 'Kuwait' },
  { code: 'BH', label: 'Bahrain' }, { code: 'OM', label: 'Oman' },
  { code: 'EG', label: 'Egypt' }, { code: 'IN', label: 'India' }, { code: 'GB', label: 'United Kingdom' }, { code: 'US', label: 'United States' },
];
const SIZES = ['1-50', '51-200', '201-500', '500+'];
const CURRENCIES = ['SAR', 'AED', 'QAR', 'KWD', 'BHD', 'OMR', 'USD', 'EUR', 'GBP', 'INR', 'EGP'];

type SectionKey = 'entity' | 'org' | 'leave' | 'shifts' | 'payroll' | 'governance';

export function AiSetupAssistant() {
  const [country, setCountry] = useState('SA');
  const [industry, setIndustry] = useState('');
  const [size, setSize] = useState('51-200');
  const [currency, setCurrency] = useState('SAR');
  const [legalEntityName, setLegalEntityName] = useState('');
  const [branchCity, setBranchCity] = useState('Riyadh');
  const [operatingModel, setOperatingModel] = useState('Functional');
  const [payrollModel, setPayrollModel] = useState('GradeBased');
  const [approvalModel, setApprovalModel] = useState('DepartmentHead');
  const [strictEntityScope, setStrictEntityScope] = useState(true);
  const [requireCostCenterForPayroll, setRequireCostCenterForPayroll] = useState(true);
  const [requireGradeForApprovalPolicy, setRequireGradeForApprovalPolicy] = useState(true);
  const [notes, setNotes] = useState('');
  const [sections, setSections] = useState<Record<SectionKey, boolean>>({ entity: true, org: true, leave: true, shifts: true, payroll: true, governance: true });

  const [loading, setLoading] = useState(false);
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState('');
  const [engine, setEngine] = useState('');
  const [genNotes, setGenNotes] = useState<string[]>([]);
  const [draft, setDraft] = useState<SetupDraft | null>(null);
  const [done, setDone] = useState<{ applied: Record<string, number>; total: number } | null>(null);

  const toggle = (k: SectionKey) => setSections(s => ({ ...s, [k]: !s[k] }));

  const generate = async () => {
    if (!industry.trim()) { setError('Tell me your industry so the suggestions fit.'); return; }
    setLoading(true); setError(''); setDone(null);
    try {
      const profile: CompanyProfile = {
        countryCode: country, industry: industry.trim(), companySize: size, currencyCode: currency,
        legalEntityName: legalEntityName.trim() || undefined,
        branchCity: branchCity.trim() || undefined,
        operatingModel,
        payrollModel,
        approvalModel,
        strictEntityScope,
        requireCostCenterForPayroll,
        requireGradeForApprovalPolicy,
        notes: notes.trim() || undefined,
        sections,
      };
      const r = await setupAssistantApi.preview(profile);
      setDraft(r.draft); setEngine(r.engine); setGenNotes(r.notes);
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Could not generate a setup. Try again.');
    } finally { setLoading(false); }
  };

  const apply = async () => {
    if (!draft) return;
    setApplying(true); setError('');
    try {
      const r = await setupAssistantApi.apply(draft, country, currency, legalEntityName.trim() || undefined);
      setDone(r);
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Could not apply the setup.');
    } finally { setApplying(false); }
  };

  // remove a row from a draft list
  function removeAt<K extends keyof SetupDraft>(key: K, idx: number) {
    setDraft(d => {
      if (!d) return d;
      const list = d[key];
      if (!Array.isArray(list)) return d;
      return { ...d, [key]: list.filter((_, i) => i !== idx) };
    });
  }

  const totalItems = draft
    ? draft.departments.length + draft.designations.length + draft.grades.length +
      draft.branches.length + draft.costCenters.length + draft.gradePayComponents.length +
      draft.leaveTypes.length + draft.shifts.length + draft.payComponents.length +
      draft.statutoryRules.length + (draft.workingWeek ? 1 : 0) +
      (draft.employeeIdRule ? 1 : 0) + (draft.hrConfig ? 1 : 0)
    : 0;

  if (done) {
    return (
      <div className="mx-auto max-w-md rounded-2xl border border-slate-200 bg-white p-8 text-center dark:border-white/10 dark:bg-white/[0.03]">
        <CheckCircle2 className="mx-auto mb-4 h-14 w-14 text-emerald-500" />
        <h3 className="text-lg font-bold text-slate-900 dark:text-white">Setup Applied</h3>
        <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">
          Created <span className="font-semibold text-sapphire dark:text-cyanAccent">{done.total}</span> item(s):
        </p>
        <div className="mt-3 flex flex-wrap justify-center gap-1.5">
          {Object.entries(done.applied ?? {}).map(([k, n]) => (
            <span key={k} className="rounded-full bg-slate-100 px-2.5 py-1 text-xs text-slate-600 dark:bg-white/10 dark:text-slate-300">{k}: {n}</span>
          ))}
        </div>
        <button type="button" className="btn-primary mt-6 w-full" onClick={() => { setDone(null); setDraft(null); }}>Run again</button>
      </div>
    );
  }

  return (
    <div className="space-y-5">
      {/* Intro */}
      <div className="flex items-start gap-3 rounded-xl border border-sapphire/20 bg-sapphire/[0.04] p-4 dark:border-cyanAccent/20 dark:bg-cyanAccent/[0.04]">
        <Sparkles className="mt-0.5 h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" />
        <div>
          <h3 className="text-sm font-semibold text-slate-900 dark:text-white">AI Setup Assistant</h3>
          <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
            Describe your company and I&apos;ll propose a starter configuration wired to legal entity, branch, cost center, grade, designation, payroll, governance, and statutory setup. You review everything before it&apos;s applied.
          </p>
        </div>
      </div>

      {/* Guided form */}
      <div className="grid gap-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-white/[0.03] sm:grid-cols-2">
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Country</span>
          <select className="select w-full" value={country} onChange={e => { setCountry(e.target.value); setDraft(null); }}>
            {COUNTRIES.map(c => <option key={c.code} value={c.code}>{c.label}</option>)}
          </select>
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Industry</span>
          <input className="input w-full" value={industry} onChange={e => { setIndustry(e.target.value); setDraft(null); }} placeholder="e.g. Construction, Retail, Healthcare" />
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Legal entity name</span>
          <input className="input w-full" value={legalEntityName} onChange={e => { setLegalEntityName(e.target.value); setDraft(null); }} placeholder="e.g. Zayra Demo LLC" />
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Head office city</span>
          <input className="input w-full" value={branchCity} onChange={e => { setBranchCity(e.target.value); setDraft(null); }} />
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Company size</span>
          <select className="select w-full" value={size} onChange={e => setSize(e.target.value)}>
            {SIZES.map(s => <option key={s} value={s}>{s} employees</option>)}
          </select>
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Currency</span>
          <select className="select w-full" value={currency} onChange={e => setCurrency(e.target.value)}>
            {CURRENCIES.map(c => <option key={c} value={c}>{c}</option>)}
          </select>
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Operating model</span>
          <select className="select w-full" value={operatingModel} onChange={e => setOperatingModel(e.target.value)}>
            {['Functional', 'Matrix', 'Multi-Branch', 'Project-Based', 'Shared Services'].map(x => <option key={x} value={x}>{x}</option>)}
          </select>
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Payroll model</span>
          <select className="select w-full" value={payrollModel} onChange={e => setPayrollModel(e.target.value)}>
            {['GradeBased', 'PositionBased', 'ProjectAllowance', 'HourlyShift', 'Mixed'].map(x => <option key={x} value={x}>{x}</option>)}
          </select>
        </label>
        <label className="block">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Approval routing</span>
          <select className="select w-full" value={approvalModel} onChange={e => setApprovalModel(e.target.value)}>
            {['DepartmentHead', 'SupervisorFirst', 'HRFinal', 'FinancePayrollReview'].map(x => <option key={x} value={x}>{x}</option>)}
          </select>
        </label>
        <div className="grid gap-2 rounded-lg border border-slate-200 p-3 dark:border-white/10">
          {[
            ['Strict entity scope', strictEntityScope, setStrictEntityScope],
            ['Require cost center for payroll', requireCostCenterForPayroll, setRequireCostCenterForPayroll],
            ['Require grade for approval policy', requireGradeForApprovalPolicy, setRequireGradeForApprovalPolicy],
          ].map(([label, value, setter]) => (
            <label key={String(label)} className="flex items-center gap-2 text-xs text-slate-700 dark:text-slate-300">
              <input type="checkbox" checked={Boolean(value)} onChange={e => (setter as (v: boolean) => void)(e.target.checked)} className="h-4 w-4 accent-sapphire" />
              {String(label)}
            </label>
          ))}
        </div>
        <label className="block sm:col-span-2">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Anything specific? (optional)</span>
          <input className="input w-full" value={notes} onChange={e => setNotes(e.target.value)} placeholder="e.g. we run 24/7 operations with field crews" />
        </label>
        <div className="sm:col-span-2">
          <span className="mb-1.5 block text-xs font-medium text-slate-700 dark:text-slate-300">Generate</span>
          <div className="flex flex-wrap gap-2">
            {([['entity', 'Entity & cost centers'], ['org', 'Org structure'], ['leave', 'Leave types'], ['shifts', 'Shifts & working week'], ['payroll', 'Payroll & statutory'], ['governance', 'Governance & IDs']] as [SectionKey, string][]).map(([k, label]) => (
              <button key={k} type="button" onClick={() => toggle(k)}
                className={`rounded-lg border px-3 py-1.5 text-xs font-medium transition ${
                  sections[k] ? 'border-transparent bg-sapphire text-white dark:bg-cyanAccent/80' : 'border-slate-200 text-slate-600 hover:border-slate-300 dark:border-white/10 dark:text-slate-300'
                }`}>
                {label}
              </button>
            ))}
          </div>
        </div>
      </div>

      {error && <p className="text-sm text-red-500">{error}</p>}

      <div className="flex items-center gap-3">
        <button type="button" className="btn-primary flex items-center gap-1.5" onClick={generate} disabled={loading}>
          <Wand2 className="h-4 w-4" />
          {loading ? 'Thinking…' : draft ? 'Regenerate' : 'Generate Setup'}
        </button>
        {draft && (
          <button type="button" className="btn-secondary flex items-center gap-1.5" onClick={apply} disabled={applying || totalItems === 0}>
            <CheckCircle2 className="h-4 w-4" />
            {applying ? 'Applying…' : `Apply ${totalItems} item(s)`}
          </button>
        )}
      </div>

      {/* Preview */}
      {draft && (
        <div className="space-y-4">
          <div className="flex flex-wrap items-center gap-2">
            <span className="rounded-full bg-sapphire/10 px-2.5 py-1 text-xs font-medium text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent">{engine}</span>
            {genNotes.map((n, i) => <span key={i} className="text-xs text-amber-600 dark:text-amber-400">{n}</span>)}
          </div>

          <DraftSection title="Branches" rows={draft.branches.map(x => [x.code, `${x.nameEn} · ${x.city}${x.isHeadOffice ? ' · Head office' : ''}`])} onRemove={i => removeAt('branches', i)} />
          <DraftSection title="Departments" rows={draft.departments.map(x => [x.code, x.nameEn])} onRemove={i => removeAt('departments', i)} />
          <DraftSection title="Cost Centers" rows={draft.costCenters.map(x => [x.code, `${x.name}${x.departmentCode ? ` · ${x.departmentCode}` : ''}`])} onRemove={i => removeAt('costCenters', i)} />
          <DraftSection title="Designations" rows={draft.designations.map(x => [x.code, `${x.titleEn}${x.departmentCode ? ` · ${x.departmentCode}` : ''}${x.gradeCode ? ` · ${x.gradeCode}` : ''}${x.isManagerRole ? ' · Manager' : ''}`])} onRemove={i => removeAt('designations', i)} />
          <DraftSection title="Grades" rows={draft.grades.map(x => [x.code, `${x.name} (L${x.level}) · ${x.currency} ${x.minSalary}-${x.maxSalary}`])} onRemove={i => removeAt('grades', i)} />
          <DraftSection title="Grade Pay Components" rows={draft.gradePayComponents.map(x => [x.componentCode, `${x.gradeCode} · ${x.componentName} · ${x.calculationType === 'PercentOfBasic' ? `${x.percentage}%` : x.amount}`])} onRemove={i => removeAt('gradePayComponents', i)} />
          <DraftSection title="Leave Types" rows={draft.leaveTypes.map(x => [x.code, `${x.nameEn} · ${x.isPaid ? 'Paid' : 'Unpaid'} · max ${x.maxConsecutiveDays}d`])} onRemove={i => removeAt('leaveTypes', i)} />
          <DraftSection title="Shifts" rows={draft.shifts.map(x => [x.code, `${x.name} · ${x.start}–${x.end}`])} onRemove={i => removeAt('shifts', i)} />
          {draft.workingWeek && (
            <DraftSection title="Working Week" rows={[['WEEK', `${draft.workingWeek.workWeek} · starts ${draft.workingWeek.weekStartDay}`]]} onRemove={() => setDraft(d => d ? { ...d, workingWeek: null } : d)} />
          )}
          <DraftSection title="Payroll Components" rows={draft.payComponents.map(x => [x.code, `${x.name} · ${x.componentType} · ${x.calculationType === 'Percentage' ? `${x.percentage}%` : x.amount}`])} onRemove={i => removeAt('payComponents', i)} />
          <DraftSection title="Statutory Rules" rows={draft.statutoryRules.map(x => [x.ruleKey, `${x.ruleValue} — ${x.description}`])} onRemove={i => removeAt('statutoryRules', i)} />
          {draft.employeeIdRule && <DraftSection title="Employee ID Rule" rows={[['ID', `${draft.employeeIdRule.companyPrefix} · pad ${draft.employeeIdRule.paddingLength} · next ${draft.employeeIdRule.nextSequence}`]]} onRemove={() => setDraft(d => d ? { ...d, employeeIdRule: null } : d)} />}
          {draft.hrConfig && <DraftSection title="HR Governance" rows={[['GOV', `${draft.hrConfig.requireImportPreviewBeforeCommit ? 'Preview required' : 'Direct import'} · ${draft.hrConfig.requireCostCenterForPayroll ? 'Cost center required' : 'Cost center optional'} · ${draft.hrConfig.requireGradeForApprovalPolicy ? 'Grade approval rules' : 'General approval rules'}`]]} onRemove={() => setDraft(d => d ? { ...d, hrConfig: null } : d)} />}
        </div>
      )}

      <OrgStructureImportPanel />
    </div>
  );
}

const IMPORT_KEYS: { key: keyof OrgStructureImportRequest; label: string }[] = [
  { key: 'companiesCsv', label: 'Companies CSV' },
  { key: 'branchesCsv', label: 'Branches CSV' },
  { key: 'costCentersCsv', label: 'Cost centers CSV' },
  { key: 'departmentsCsv', label: 'Departments CSV' },
  { key: 'gradesCsv', label: 'Grades CSV' },
  { key: 'gradePayComponentsCsv', label: 'Grade pay components CSV' },
  { key: 'designationsCsv', label: 'Designations CSV' },
];

function OrgStructureImportPanel() {
  const [payload, setPayload] = useState<OrgStructureImportRequest>({});
  const [result, setResult] = useState<OrgStructureImportResult | null>(null);
  const [loading, setLoading] = useState('');
  const [error, setError] = useState('');

  const setFile = async (key: keyof OrgStructureImportRequest, file?: File) => {
    if (!file) return;
    setPayload(p => ({ ...p, [key]: undefined }));
    const text = await file.text();
    setPayload(p => ({ ...p, [key]: text }));
    setResult(null);
  };

  const template = async () => {
    setLoading('template'); setError('');
    try { downloadText(await orgStructureImportApi.template(), 'organization-structure-import-package.txt'); }
    catch { setError('Could not download organization structure template.'); }
    finally { setLoading(''); }
  };

  const preview = async () => {
    setLoading('preview'); setError('');
    try { setResult(await orgStructureImportApi.preview(payload)); }
    catch { setError('Could not preview organization structure import.'); }
    finally { setLoading(''); }
  };

  const commit = async () => {
    setLoading('commit'); setError('');
    try { setResult(await orgStructureImportApi.commit(payload)); }
    catch (e: unknown) {
      const data = (e as { response?: { data?: OrgStructureImportResult } })?.response?.data;
      if (data) setResult(data);
      setError('Import was not committed. Resolve blocking errors and preview again.');
    } finally { setLoading(''); }
  };

  const hasAny = Object.values(payload).some(Boolean);
  return (
    <div className="rounded-xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-white/[0.03]">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 className="text-sm font-semibold text-slate-900 dark:text-white">Bulk Organization Structure Import</h3>
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">Load the structure in dependency order: companies, branches, cost centers, departments, grades, pay components, designations.</p>
        </div>
        <button type="button" className="btn-secondary text-xs" onClick={template} disabled={loading === 'template'}><FileUp className="h-3.5 w-3.5" /> Template</button>
      </div>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {IMPORT_KEYS.map(({ key, label }) => (
          <label key={key} className="block rounded-lg border border-slate-200 p-3 text-xs dark:border-white/10">
            <span className="mb-2 block font-medium text-slate-700 dark:text-slate-300">{label}</span>
            <input type="file" accept=".csv,text/csv" onChange={e => setFile(key, e.target.files?.[0])} className="block w-full text-xs text-slate-500" />
            {payload[key] && <span className="mt-2 block text-emerald-600">Loaded</span>}
          </label>
        ))}
      </div>
      {error && <p className="mt-3 text-sm text-red-500">{error}</p>}
      <div className="mt-4 flex gap-2">
        <button type="button" className="btn-secondary" onClick={preview} disabled={!hasAny || loading === 'preview'}>Preview</button>
        <button type="button" className="btn-primary" onClick={commit} disabled={!result || result.hasBlockingErrors || loading === 'commit'}>Commit Import</button>
      </div>
      {result && (
        <div className="mt-4 rounded-lg bg-slate-50 p-3 text-xs dark:bg-white/[0.04]">
          <p className="font-semibold text-slate-800 dark:text-white">{result.committed ? 'Committed' : 'Preview'} · {result.received} rows · {result.errors} errors · {result.warnings} warnings</p>
          {Object.keys(result.applied ?? {}).length > 0 && <p className="mt-1 text-slate-600 dark:text-slate-300">{Object.entries(result.applied).map(([k, v]) => `${k}: ${v}`).join(' · ')}</p>}
          <div className="mt-2 max-h-48 overflow-auto">
            {result.rows.filter(r => r.errors.length || r.warnings.length).slice(0, 20).map((row, idx) => (
              <p key={`${row.entityCode}-${idx}`} className={row.errors.length ? 'text-red-600' : 'text-amber-600'}>
                Row {row.rowNumber} {row.entityCode}: {[...row.errors, ...row.warnings].join('; ')}
              </p>
            ))}
          </div>
        </div>
      )}
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

function DraftSection({ title, rows, onRemove }: { title: string; rows: [string, string][]; onRemove: (idx: number) => void }) {
  if (rows.length === 0) return null;
  return (
    <div className="rounded-xl border border-slate-200 dark:border-white/10">
      <div className="flex items-center justify-between border-b border-slate-100 px-4 py-2.5 dark:border-white/[0.06]">
        <h4 className="text-sm font-semibold text-slate-900 dark:text-white">{title}</h4>
        <span className="text-xs text-slate-400">{rows.length}</span>
      </div>
      <ul className="divide-y divide-slate-50 dark:divide-white/[0.04]">
        {rows.map(([code, desc], i) => (
          <li key={`${code}-${i}`} className="flex items-center gap-3 px-4 py-2 text-sm">
            <span className="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-xs text-slate-600 dark:bg-white/10 dark:text-slate-300">{code}</span>
            <span className="text-slate-700 dark:text-slate-200">{desc}</span>
            <button type="button" aria-label={`Remove ${code}`} onClick={() => onRemove(i)}
              className="ml-auto grid h-6 w-6 place-items-center rounded text-slate-400 hover:bg-rose-50 hover:text-rose-500 dark:hover:bg-rose-500/10">
              <Trash2 className="h-3.5 w-3.5" />
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}
