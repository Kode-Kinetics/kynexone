'use client';

import { useRef, useState } from 'react';
import { AlertTriangle, CheckCircle2, Circle, Database, Download, Eye, FileSpreadsheet, GitBranch, Info, RefreshCw, Rocket, ShieldCheck, Sparkles, Trash2, UploadCloud, Wand2, XCircle } from 'lucide-react';
import { orgStructureImportApi, setupAssistantApi, type CompanyProfile, type MigrationImportBatchDto, type OrgStructureImportRequest, type OrgStructureImportResult, type SetupDraft } from '../api/setupAssistant';

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
  const selectedCount = Object.values(sections).filter(Boolean).length;
  // Full Record<SectionKey, boolean> literal — an Object.fromEntries shortcut would widen
  // to { [k: string]: boolean } and fail the strict Record<SectionKey, boolean> assignment.
  const setAllSections = (value: boolean) =>
    setSections({ entity: value, org: value, leave: value, shifts: value, payroll: value, governance: value });

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
      const err = e as { response?: { status?: number; data?: { message?: string } } };
      if (err?.response?.status === 403) {
        setError("You don't have access to the setup assistant (Admin or HR Manager role required).");
      } else {
        setError(err?.response?.data?.message ?? 'Could not generate a setup. Try again.');
      }
    } finally { setLoading(false); }
  };

  const apply = async () => {
    if (!draft) return;
    setApplying(true); setError('');
    try {
      const r = await setupAssistantApi.apply(draft, country, currency, legalEntityName.trim() || undefined);
      setDone(r);
    } catch (e: unknown) {
      // Keep the draft in state on 403 so an admin can apply the exact reviewed draft.
      // The axios interceptor already fires a global access-denied toast; add only the
      // inline, domain-specific message here (no second toast).
      const err = e as { response?: { status?: number; data?: { message?: string } } };
      if (err?.response?.status === 403) {
        setError("Applying requires the 'organization.setup.apply' permission. Your account can preview a setup but not commit it — ask an administrator to apply this reviewed draft, or to grant you the permission.");
      } else {
        setError(err?.response?.data?.message ?? 'Could not apply the setup.');
      }
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
        <fieldset role="group" aria-label="Sections to include in the draft" className="sm:col-span-2">
          <div className="mb-1.5 flex flex-wrap items-center justify-between gap-2">
            <span className="text-xs font-medium text-slate-700 dark:text-slate-300">Sections to include in the draft</span>
            <div className="flex items-center gap-3">
              <span className="text-xs text-slate-400">{selectedCount} of 6 selected</span>
              <button type="button" className="text-[11px] text-sapphire hover:underline dark:text-cyanAccent" onClick={() => setAllSections(true)}>Select all</button>
              <button type="button" className="text-[11px] text-sapphire hover:underline dark:text-cyanAccent" onClick={() => setAllSections(false)}>Clear all</button>
            </div>
          </div>
          <p className="mb-2 text-xs text-slate-400">Pick which parts of the starter configuration the assistant proposes. These are selections, not actions — nothing is generated until you choose Generate draft below.</p>
          <div className="flex flex-wrap gap-2">
            {([['entity', 'Entity & cost centers'], ['org', 'Org structure'], ['leave', 'Leave types'], ['shifts', 'Shifts & working week'], ['payroll', 'Payroll & statutory'], ['governance', 'Governance & IDs']] as [SectionKey, string][]).map(([k, label]) => (
              <label key={k}
                className={`flex cursor-pointer items-center gap-2 rounded-lg border px-3 py-1.5 text-xs transition ${
                  sections[k]
                    ? 'border-sapphire bg-sapphire/[0.06] text-slate-900 dark:border-cyanAccent/40 dark:bg-cyanAccent/[0.06] dark:text-white'
                    : 'border-slate-200 text-slate-500 hover:border-slate-300 dark:border-white/10 dark:text-slate-300'
                }`}>
                <input type="checkbox" checked={sections[k]} onChange={() => toggle(k)} aria-label={`Include ${label} in the generated draft`} className="h-4 w-4 accent-sapphire" />
                {sections[k] ? <CheckCircle2 className="h-3.5 w-3.5 text-sapphire dark:text-cyanAccent" /> : <Circle className="h-3.5 w-3.5 text-slate-300 dark:text-white/30" />}
                {label}
              </label>
            ))}
          </div>
        </fieldset>
      </div>

      {error && <p className="text-sm text-red-500">{error}</p>}

      {/* Two-step ribbon: preview (Step 1) then review & apply (Step 2) */}
      <div className="flex flex-wrap items-center gap-2 text-[11px] font-medium">
        <span className={`rounded-full px-2.5 py-1 ${!draft ? 'bg-sapphire text-white dark:bg-cyanAccent/80' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-300'}`}>Step 1 · Generate draft (preview only)</span>
        <span className="text-slate-300 dark:text-white/30">→</span>
        <span className={`rounded-full px-2.5 py-1 ${draft ? 'bg-sapphire text-white dark:bg-cyanAccent/80' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-300'}`}>Step 2 · Review &amp; apply</span>
      </div>

      <div className="flex flex-wrap items-start gap-3">
        <div>
          <button type="button" className={`${draft ? 'btn-secondary' : 'btn-primary'} flex items-center gap-1.5`} onClick={generate}
            disabled={loading || selectedCount === 0 || !industry.trim()}
            title={!industry.trim() ? 'Add your industry first.' : selectedCount === 0 ? 'Select at least one section to include.' : undefined}>
            <Wand2 className="h-4 w-4" />
            {loading ? 'Thinking…' : draft ? 'Regenerate draft' : 'Generate draft'}
          </button>
          <p className={`mt-1 text-[11px] ${!industry.trim() || selectedCount === 0 ? 'text-rose-500' : 'text-slate-400'}`}>
            {!industry.trim() ? 'Add your industry first.' : selectedCount === 0 ? 'Select at least one section to include.' : 'Preview only — nothing is written to your workspace yet.'}
          </p>
        </div>
        {draft && (
          <div>
            <button type="button" className="btn-primary flex items-center gap-1.5" onClick={apply} disabled={applying || totalItems === 0}
              title={totalItems === 0 ? 'Remove-all left nothing to apply — regenerate or add rows.' : undefined}>
              <CheckCircle2 className="h-4 w-4" />
              {applying ? 'Applying…' : `Apply ${totalItems} item(s) to workspace`}
            </button>
            {totalItems === 0 && <p className="mt-1 text-[11px] text-rose-500">Remove-all left nothing to apply — regenerate or add rows.</p>}
          </div>
        )}
      </div>

      {/* Preview */}
      {draft && (
        <div className="space-y-4">
          {/* Draft-only banner */}
          <div className="flex items-start gap-3 rounded-xl border border-sapphire/20 bg-sapphire/[0.04] p-4 dark:border-cyanAccent/20 dark:bg-cyanAccent/[0.04]">
            <Eye className="mt-0.5 h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" />
            <p className="text-xs text-slate-600 dark:text-slate-300">
              Draft only — nothing has been saved yet. Review the <span className="font-semibold text-slate-900 dark:text-white">{totalItems}</span> item(s) below, remove anything you don&apos;t want, then apply to write them to your workspace.
            </p>
          </div>

          {/* Provenance: how the draft was generated (surfaces engine + genNotes, incl. the deterministic-template note) */}
          <div className="rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-white/10 dark:bg-white/[0.04]">
            <div className="flex flex-wrap items-center gap-2">
              <Info className="h-4 w-4 text-slate-500 dark:text-slate-400" />
              <p className="text-sm font-semibold text-slate-900 dark:text-white">How this draft was generated</p>
              {engine && <span className="rounded-full bg-sapphire/10 px-2.5 py-1 text-xs font-medium text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent">{engine}</span>}
            </div>
            {genNotes.length > 0 && (
              <div className="mt-3 space-y-2">
                {genNotes.map((n, i) => (
                  <p key={i} className="flex items-start gap-2 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700 dark:bg-amber-500/10 dark:text-amber-200">
                    <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />{n}
                  </p>
                ))}
              </div>
            )}
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

function OrgStructureImportPanel() {
  const [payload, setPayload] = useState<OrgStructureImportRequest>({});
  const [batch, setBatch] = useState<MigrationImportBatchDto | null>(null);
  const [loading, setLoading] = useState('');
  const [error, setError] = useState('');
  const [packageName, setPackageName] = useState('');
  const [fileNames, setFileNames] = useState<Partial<Record<keyof OrgStructureImportRequest, string>>>({});
  const [templateDownloaded, setTemplateDownloaded] = useState(false);
  const resultsRef = useRef<HTMLDivElement>(null);

  // Any payload mutation changes the package checksum, so a prior dry-run is no longer
  // committable — drop the batch to force re-validation (§6 invalidation rule).
  const setFile = async (key: keyof OrgStructureImportRequest, file?: File) => {
    if (!file) return;
    setPayload(p => ({ ...p, [key]: undefined }));
    const text = await file.text();
    setPayload(p => ({ ...p, [key]: text }));
    setFileNames(f => ({ ...f, [key]: file.name }));
    setTemplateDownloaded(false);
    setBatch(null);
  };

  const clearFile = (key: keyof OrgStructureImportRequest) => {
    setPayload(p => ({ ...p, [key]: undefined }));
    setFileNames(f => { const next = { ...f }; delete next[key]; return next; });
    setBatch(null);
  };

  const setPackageFile = async (file?: File) => {
    if (!file) return;
    setError('');
    const text = await file.text();
    const parsed = splitOrgPackage(text);
    if (Object.values(parsed).every(v => !v)) {
      setError('Package file was not recognized. Use the generated package format with # companies, # branches, # departments, and related sections.');
      return;
    }
    setPayload(p => ({ ...p, ...parsed }));
    setPackageName(file.name);
    setTemplateDownloaded(false);
    setBatch(null);
  };

  const clearPackage = () => {
    setPayload({});
    setFileNames({});
    setPackageName('');
    setBatch(null);
  };

  const template = async () => {
    setLoading('template'); setError('');
    try {
      downloadText(await orgStructureImportApi.template(), 'organization-structure-import-package.txt');
      setTemplateDownloaded(true);
    }
    catch { setError('Could not download organization structure template.'); }
    finally { setLoading(''); }
  };

  const runValidation = async () => {
    setLoading('dryrun'); setError('');
    try {
      const dto = await orgStructureImportApi.dryRun(payload);
      setBatch(dto);
      requestAnimationFrame(() => resultsRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }));
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Could not validate the organization structure package.');
    } finally { setLoading(''); }
  };

  const commitImport = async () => {
    if (!batch) return;
    setLoading('commit'); setError('');
    try {
      const dto = await orgStructureImportApi.commitBatch(batch.id);
      setBatch(dto);
      requestAnimationFrame(() => resultsRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }));
    } catch (e: unknown) {
      const err = e as { response?: { status?: number; data?: unknown } };
      const status = err?.response?.status;
      const data = err?.response?.data;
      // 422: CommitBatch re-validates against live DB state and returns a full batch DTO
      // carrying the blocking findings when the workspace changed since dry-run (or the
      // stored payload can't be read). Surface those findings and prompt a re-validate.
      if (status === 422 && isBatchDto(data)) {
        setBatch(data);
        setError('The workspace changed since validation — resolve the findings and run validation again.');
      } else if (status === 409) {
        // Fires when the batch is no longer DryRunPassed (e.g. Committing/Failed). §6 nulls
        // the batch on any payload change, so in practice we only ever commit a clean batch;
        // this is a defensive branch and the copy avoids overstating a checksum re-check.
        if (isBatchDto(data)) setBatch(data);
        setError('This batch is no longer in a committable state — run validation again.');
      } else {
        setError((data as { message?: string })?.message ?? 'Could not commit the governed import.');
      }
    } finally { setLoading(''); }
  };

  const refreshStatus = async () => {
    if (!batch) return;
    setLoading('refresh'); setError('');
    try { setBatch(await orgStructureImportApi.getBatch(batch.id)); }
    catch { setError('Could not refresh the batch status.'); }
    finally { setLoading(''); }
  };

  const hasAny = Object.values(payload).some(Boolean);
  const loadedCount = IMPORT_KEYS.filter(x => payload[x.key]).length;
  const totalRows = IMPORT_KEYS.reduce((sum, x) => sum + countCsvRows(payload[x.key]), 0);
  const blockingCount = batch?.errorRows ?? 0;
  const warningCount = batch?.result?.warnings ?? 0;
  const blockingGroups = groupFindings(batch?.result ?? null, 'errors');
  const warningGroups = groupFindings(batch?.result ?? null, 'warnings');
  const canCommit = batch?.status === 'DryRunPassed';
  const commitReason = !batch ? 'Run validation first.'
    : batch.status === 'DryRunBlocked' ? `Resolve ${blockingCount} blocking issue(s), then run validation again.`
    : batch.status === 'Committed' ? 'Already committed.'
    : batch.status === 'DryRunPassed' ? ''
    : 'This batch is no longer committable — run validation again.';
  const readiness: { label: string; tone: 'neutral' | 'success' | 'danger' } =
    batch?.status === 'DryRunBlocked' ? { label: 'Blocked', tone: 'danger' }
    : batch?.status === 'DryRunPassed' ? { label: 'Ready', tone: 'success' }
    : batch?.status === 'Committed' ? { label: 'Committed', tone: 'success' }
    : { label: 'Pending', tone: 'neutral' };
  const validationBanner = batch?.status === 'DryRunPassed'
    ? 'Validation complete — package is clean and ready to commit.'
    : batch?.status === 'DryRunBlocked'
    ? `Validation complete — ${blockingCount} blocking issue(s) and ${warningCount} warning(s) found.`
    : batch?.status === 'Committed'
    ? 'Committed to master data.'
    : '';
  const runway = [
    { label: 'Load', done: hasAny, active: hasAny && !batch },
    { label: 'Validate', done: Boolean(batch && batch.status !== 'DryRunBlocked'), active: batch?.status === 'DryRunBlocked' },
    { label: 'Commit', done: batch?.status === 'Committed', active: batch?.status === 'DryRunPassed' },
  ];

  return (
    <div className="overflow-hidden rounded-xl border border-slate-200 bg-white dark:border-white/10 dark:bg-white/[0.03]">
      <div className="border-b border-slate-100 bg-slate-50/80 p-5 dark:border-white/[0.06] dark:bg-white/[0.04]">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h3 className="text-sm font-semibold text-slate-900 dark:text-white">Organization Migration Cockpit</h3>
            <p className="mt-1 max-w-3xl text-xs text-slate-500 dark:text-slate-400">
              Import a complete existing organization structure with dependency validation across legal entity, branch, cost center, department, grade, payroll breakdown, and designation eligibility before anything is committed.
            </p>
          </div>
          <div className="flex flex-col items-end gap-1">
            <button type="button" className="btn-secondary text-xs" onClick={template} disabled={loading === 'template'}><Download className="h-3.5 w-3.5" /> {loading === 'template' ? 'Downloading…' : 'Download template'}</button>
            {templateDownloaded && <span className="text-[11px] font-medium text-emerald-600 dark:text-emerald-300">Template downloaded — fill it in and upload it above.</span>}
          </div>
        </div>
        <div className="mt-4 grid gap-3 sm:grid-cols-3">
          {runway.map((step, idx) => (
            <div key={step.label} className={`rounded-lg border p-3 ${step.done ? 'border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-500/20 dark:bg-emerald-500/10 dark:text-emerald-200' : step.active ? 'border-amber-200 bg-amber-50 text-amber-800 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-200' : 'border-slate-200 bg-white text-slate-500 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-400'}`}>
              <div className="flex items-center gap-2 text-xs font-semibold">
                <span className="grid h-6 w-6 place-items-center rounded-full bg-white/70 text-[11px] dark:bg-black/20">{idx + 1}</span>
                {step.label}
              </div>
            </div>
          ))}
        </div>
      </div>

      <div className="grid gap-5 p-5 xl:grid-cols-[minmax(0,1.05fr)_minmax(360px,0.95fr)]">
        <div>
          <div className="rounded-xl border border-dashed border-sapphire/30 bg-sapphire/[0.03] p-4 dark:border-cyanAccent/25 dark:bg-cyanAccent/[0.04]">
            <p className="mb-3 flex items-start gap-2 rounded-lg bg-white/70 px-3 py-2 text-[11px] text-slate-500 dark:bg-black/20 dark:text-slate-300">
              <ShieldCheck className="mt-0.5 h-3.5 w-3.5 shrink-0 text-sapphire dark:text-cyanAccent" />
              Files are read in your browser only. Nothing is sent to the server until you Run validation, and nothing is written until you Commit.
            </p>
            <div className="flex items-start gap-3">
              <UploadCloud className="mt-0.5 h-5 w-5 text-sapphire dark:text-cyanAccent" />
              <div className="min-w-0 flex-1">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">Upload one migration package</p>
                <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">Use the generated package with named sections, or upload individual CSV files below for controlled remediation.</p>
                <input type="file" accept=".txt,.csv,text/plain,text/csv" onChange={e => setPackageFile(e.target.files?.[0])} className="mt-3 block w-full text-xs text-slate-500" />
                {packageName && (
                  <p className="mt-2 flex flex-wrap items-center gap-2 text-xs font-medium text-emerald-600 dark:text-emerald-300">
                    <CheckCircle2 className="h-3.5 w-3.5" />
                    Loaded {packageName} — {loadedCount} section(s), {totalRows} row(s). Not written yet.
                    <button type="button" className="text-slate-400 underline hover:text-slate-600 dark:hover:text-slate-200" onClick={clearPackage}>Clear</button>
                  </p>
                )}
                <p className="mt-2 text-[11px] text-slate-400">Need the format? Download template — a starter package (.txt) with the exact section headers and one filled, consistent example row per section. Edit it, then upload it above. Some sections (companies, grades) require group-level access to import.</p>
              </div>
            </div>
          </div>

          <div className="mt-4 grid gap-3 sm:grid-cols-2">
            {IMPORT_KEYS.map(item => {
              const rows = countCsvRows(payload[item.key]);
              const ready = rows > 0;
              const missingDeps = item.dependsOn.filter(dep => !payload[IMPORT_KEYS.find(x => x.section === dep)?.key ?? 'companiesCsv']);
              return (
                <div key={item.key} className={`block rounded-xl border p-3 text-xs transition ${ready ? 'border-emerald-200 bg-emerald-50/70 dark:border-emerald-500/20 dark:bg-emerald-500/10' : 'border-slate-200 hover:border-slate-300 dark:border-white/10 dark:hover:border-white/20'}`}>
                  <div className="flex items-start justify-between gap-2">
                    <div>
                      <span className="block font-semibold text-slate-800 dark:text-white">{item.label}</span>
                      <span className="mt-0.5 block text-slate-500 dark:text-slate-400">{item.phase}</span>
                    </div>
                    <span className={`rounded-full px-2 py-0.5 font-medium ${ready ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-400/15 dark:text-emerald-200' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-300'}`}>{rows} rows</span>
                  </div>
                  <div className="mt-2 flex flex-wrap gap-1">
                    {item.required.slice(0, 3).map(req => <span key={req} className="rounded bg-white px-1.5 py-0.5 text-[10px] text-slate-500 ring-1 ring-slate-200 dark:bg-black/20 dark:text-slate-300 dark:ring-white/10">{req}</span>)}
                  </div>
                  {missingDeps.length > 0 && <div className="mt-2 flex items-center gap-1 text-amber-600 dark:text-amber-300"><GitBranch className="h-3 w-3" /> Depends on {missingDeps.join(', ')}</div>}
                  <input type="file" accept=".csv,text/csv" onChange={e => setFile(item.key, e.target.files?.[0])} className="mt-3 block w-full text-xs text-slate-500" />
                  {ready && (
                    <p className="mt-2 flex flex-wrap items-center gap-2 font-medium text-emerald-600 dark:text-emerald-300">
                      <CheckCircle2 className="h-3.5 w-3.5" /> Loaded {fileNames[item.key] ?? 'file'} — {rows} row(s)
                      <button type="button" className="text-slate-400 underline hover:text-slate-600 dark:hover:text-slate-200" onClick={() => clearFile(item.key)}>Clear</button>
                    </p>
                  )}
                </div>
              );
            })}
          </div>
        </div>

        <div className="space-y-4">
          <div className="grid grid-cols-3 gap-2">
            <MetricCard icon={<FileSpreadsheet className="h-4 w-4" />} label="Sections loaded" value={`${loadedCount}/${IMPORT_KEYS.length}`} />
            <MetricCard icon={<Database className="h-4 w-4" />} label="Rows" value={String(batch?.receivedRows ?? totalRows)} />
            <MetricCard icon={<ShieldCheck className="h-4 w-4" />} label="Readiness" value={readiness.label} tone={readiness.tone} />
          </div>

          {error && <p className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600 dark:border-red-500/20 dark:bg-red-500/10 dark:text-red-300">{error}</p>}

          <div className="flex flex-wrap gap-2">
            <div>
              <button type="button" className="btn-secondary" onClick={runValidation} disabled={!hasAny || loading === 'dryrun'}
                title={!hasAny ? 'Load the package or at least one section file to validate.' : undefined} aria-describedby="run-validation-reason">
                <Eye className="h-4 w-4" /> {loading === 'dryrun' ? 'Validating…' : 'Run validation'}
              </button>
              {!hasAny && <p id="run-validation-reason" className="mt-1 text-[11px] text-rose-500">Load the package or at least one section file to validate.</p>}
            </div>
            <div>
              <button type="button" className="btn-primary" onClick={commitImport} disabled={!canCommit || loading === 'commit'}
                title={commitReason || undefined} aria-describedby="commit-reason">
                <Rocket className="h-4 w-4" /> {loading === 'commit' ? 'Committing…' : 'Commit governed import'}
              </button>
              {commitReason && <p id="commit-reason" className={`mt-1 text-[11px] ${batch?.status === 'DryRunBlocked' ? 'text-rose-500' : 'text-slate-400'}`}>{commitReason}</p>}
            </div>
            {batch && (
              <button type="button" className="btn-secondary" onClick={refreshStatus} disabled={loading === 'refresh'}>
                <RefreshCw className="h-4 w-4" /> {loading === 'refresh' ? 'Refreshing…' : 'Refresh status'}
              </button>
            )}
          </div>

          {batch && (
            <>
              <p aria-live="polite" role="status" className={`rounded-lg px-3 py-2 text-xs font-medium ${
                batch.status === 'DryRunBlocked' ? 'bg-red-50 text-red-700 dark:bg-red-500/10 dark:text-red-200'
                : 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-200'}`}>
                {validationBanner}
              </p>

              <div ref={resultsRef} className="rounded-xl border border-slate-200 bg-slate-50 p-4 text-xs dark:border-white/10 dark:bg-white/[0.04]">
                {/* Batch audit header — the trust centerpiece */}
                <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-200 pb-3 dark:border-white/10">
                  <div className="min-w-0">
                    <p className="font-semibold text-slate-900 dark:text-white">Batch {batch.externalBatchId ?? batch.id.slice(0, 8)}</p>
                    <p className="mt-0.5 font-mono text-[11px] text-slate-500 dark:text-slate-400">
                      checksum {batch.packageChecksum.slice(0, 8)} · created {formatUtc(batch.createdAtUtc)}{batch.completedAtUtc ? ` · completed ${formatUtc(batch.completedAtUtc)}` : ''}
                    </p>
                  </div>
                  <StatusPill status={batch.status} />
                </div>

                {/* Impact tiles */}
                <div className="mt-3 grid gap-2 sm:grid-cols-4">
                  <Impact label="Create" value={batch.createdRows} />
                  <Impact label="Update" value={batch.updatedRows} />
                  <Impact label="Skip" value={batch.skippedRows} />
                  <Impact label="Blocked" value={batch.errorRows} />
                </div>

                <CountStrip label="Package contents" counts={batch.reconciliation.sectionCounts} />
                <CountStrip label="In your workspace already" counts={{ ...batch.reconciliation.identityCounts, ...batch.reconciliation.operationalCounts }} muted />

                {batch.status === 'Committed' && batch.result && Object.keys(batch.result.applied ?? {}).length > 0 && (
                  <p className="mt-3 flex items-center gap-2 rounded-lg bg-emerald-50 px-3 py-2 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-200">
                    <CheckCircle2 className="h-4 w-4" /> Applied — {Object.entries(batch.result.applied).map(([k, v]) => `${k}: ${v}`).join(' · ')}
                  </p>
                )}

                <FindingGroup title="Blocking findings" icon={<XCircle className="h-4 w-4" />} groups={blockingGroups} tone="danger" />
                <FindingGroup title="Review findings" icon={<AlertTriangle className="h-4 w-4" />} groups={warningGroups} tone="warning" />
                {batch.status === 'DryRunPassed' && warningCount === 0 && (
                  <p className="mt-3 flex items-center gap-2 rounded-lg bg-emerald-50 px-3 py-2 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-200"><CheckCircle2 className="h-4 w-4" /> Package is clean and ready to commit.</p>
                )}
              </div>
            </>
          )}
        </div>
      </div>
    </div>
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
    DryRunPassed: { label: 'Ready to commit', cls: 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-200' },
    DryRunBlocked: { label: 'Blocked', cls: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-200' },
    Committed: { label: 'Committed', cls: 'bg-slate-200 text-slate-700 dark:bg-white/10 dark:text-slate-200' },
    Failed: { label: 'Failed', cls: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-200' },
  };
  const s = map[status] ?? { label: status || 'Pending', cls: 'bg-slate-100 text-slate-600 dark:bg-white/10 dark:text-slate-300' };
  return <span className={`rounded-full px-2.5 py-1 text-[11px] font-semibold ${s.cls}`}>{s.label}</span>;
}

function CountStrip({ label, counts, muted = false }: { label: string; counts: Record<string, number>; muted?: boolean }) {
  const entries = Object.entries(counts ?? {});
  if (entries.length === 0) return null;
  return (
    <div className="mt-3">
      <p className="text-[11px] font-semibold uppercase tracking-wide text-slate-400">{label}</p>
      <div className="mt-1.5 flex flex-wrap gap-1.5">
        {entries.map(([k, v]) => (
          <span key={k} className={`rounded-full px-2 py-0.5 text-[11px] ${muted ? 'bg-white text-slate-500 ring-1 ring-slate-200 dark:bg-black/20 dark:text-slate-300 dark:ring-white/10' : 'bg-sapphire/10 text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent'}`}>{k}: {v}</span>
        ))}
      </div>
    </div>
  );
}

function MetricCard({ icon, label, value, tone = 'neutral' }: { icon: React.ReactNode; label: string; value: string; tone?: 'neutral' | 'success' | 'danger' }) {
  const color = tone === 'success' ? 'text-emerald-600 dark:text-emerald-300' : tone === 'danger' ? 'text-red-600 dark:text-red-300' : 'text-slate-700 dark:text-slate-200';
  return <div className="rounded-lg border border-slate-200 bg-white p-3 dark:border-white/10 dark:bg-white/[0.03]"><div className={`mb-1 ${color}`}>{icon}</div><p className="text-[11px] text-slate-500 dark:text-slate-400">{label}</p><p className={`mt-0.5 text-sm font-bold ${color}`}>{value}</p></div>;
}

function Impact({ label, value }: { label: string; value: number }) {
  return <div className="rounded-lg bg-white px-3 py-2 ring-1 ring-slate-200 dark:bg-black/20 dark:ring-white/10"><p className="text-[11px] text-slate-500 dark:text-slate-400">{label}</p><p className="text-base font-bold text-slate-900 dark:text-white">{value}</p></div>;
}

function FindingGroup({ title, icon, groups, tone }: { title: string; icon: React.ReactNode; groups: Record<string, string[]>; tone: 'danger' | 'warning' }) {
  const entries = Object.entries(groups);
  if (entries.length === 0) return null;
  const text = tone === 'danger' ? 'text-red-700 dark:text-red-200' : 'text-amber-700 dark:text-amber-200';
  const bg = tone === 'danger' ? 'bg-red-50 dark:bg-red-500/10' : 'bg-amber-50 dark:bg-amber-500/10';
  return (
    <div className={`mt-3 rounded-lg p-3 ${bg}`}>
      <p className={`flex items-center gap-2 font-semibold ${text}`}>{icon}{title}</p>
      <div className="mt-2 max-h-56 space-y-2 overflow-auto pr-1">
        {entries.slice(0, 8).map(([section, findings]) => (
          <div key={section}>
            <p className="font-semibold capitalize text-slate-800 dark:text-white">{section}</p>
            {findings.slice(0, 5).map((finding, idx) => <p key={`${section}-${idx}`} className={text}>{finding}</p>)}
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
  if (!result) return {};
  return result.rows.reduce<Record<string, string[]>>((acc, row) => {
    const findings = row[key] ?? [];
    if (findings.length === 0) return acc;
    const section = row.entityCode?.split(':')[0] || 'general';
    acc[section] ??= [];
    for (const finding of findings) acc[section].push(`Row ${row.rowNumber || '-'} ${row.entityCode ?? ''}: ${finding}`.trim());
    return acc;
  }, {});
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
