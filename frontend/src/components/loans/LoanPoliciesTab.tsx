'use client';

import { useCallback, useEffect, useState } from 'react';
import { loanGovernanceApi, type LoanPolicy, type LoanPolicyInput } from '../../api/loanGovernance';
import type { LoanType } from '../../api/loans';
import { companiesApi, type CompanyDto } from '../../api/organization';
import { useCompany } from '../../contexts/CompanyContext';
import { loanErrorMessage, repaymentMethodLabels } from '../../lib/loanWorkflow';
import { Modal } from '../Modal';

const defaults: LoanPolicyInput = {
  companyId: '', loanTypeId: '', policyName: '', maxAmount: 0, maxTotalOutstanding: 0,
  maxMultiplierOfSalary: 0, maxInstallmentPercentOfSalary: 0, minServiceMonths: 0, maxInstallments: 12,
  requireProbationCompleted: true, blockDuringNotice: true, blockOnOverdue: true,
  allowedEmploymentStatuses: ['Active'], allowedContractTypes: [], allowedRepaymentMethods: ['BankTransfer'],
  allowedRepaymentFrequencies: ['Monthly', 'Weekly', 'BiWeekly', 'Quarterly'],
  maxConcurrentLoans: 1, cooldownMonthsAfterRepayment: 0, additionalApprovalThreshold: 0,
  additionalApproverRole: 'HR Director', allowExceptions: false, allowEarlySettlement: true, allowRescheduling: false, isActive: true,
};
const numericFields = [
  ['maxAmount', 'Maximum loan amount'], ['maxTotalOutstanding', 'Maximum total exposure'],
  ['maxMultiplierOfSalary', 'Maximum salary multiple'], ['maxInstallmentPercentOfSalary', 'Maximum installment (% of salary)'],
  ['minServiceMonths', 'Minimum service (months)'], ['maxInstallments', 'Maximum installments'],
  ['maxConcurrentLoans', 'Maximum concurrent loans'], ['cooldownMonthsAfterRepayment', 'Cooldown after repayment (months)'],
  ['additionalApprovalThreshold', 'Additional approval threshold'],
] as const;
const checks = [
  ['requireProbationCompleted', 'Require completed probation'], ['blockDuringNotice', 'Block during notice'],
  ['blockOnOverdue', 'Block while a loan is overdue'], ['allowExceptions', 'Allow approved policy exceptions'],
  ['allowEarlySettlement', 'Allow early settlement'], ['allowRescheduling', 'Allow approved rescheduling'],
] as const;

export function LoanPoliciesTab({ loanTypes }: { loanTypes: LoanType[] }) {
  const { companies: accessibleCompanies, selectedCompanyId } = useCompany();
  const [companies, setCompanies] = useState<Pick<CompanyDto, 'id' | 'legalNameEn'>[]>(accessibleCompanies.map(c => ({ id: c.id, legalNameEn: c.name })));
  const [companyId, setCompanyId] = useState(selectedCompanyId ?? accessibleCompanies[0]?.id ?? '');
  const [typeId, setTypeId] = useState('');
  const [policies, setPolicies] = useState<LoanPolicy[]>([]);
  const [form, setForm] = useState<LoanPolicyInput>(defaults);
  const [open, setOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [contractTypesText, setContractTypesText] = useState('');
  const load = useCallback(async () => {
    if (!companyId) { setPolicies([]); return; }
    try { setPolicies(await loanGovernanceApi.policies({ companyId, loanTypeId: typeId || undefined })); }
    catch (e) { setError(loanErrorMessage(e, 'Unable to load company loan policies.')); }
  }, [companyId, typeId]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    if (accessibleCompanies.length) return;
    companiesApi.listAll().then(items => { setCompanies(items); setCompanyId(current => current || items[0]?.id || ''); }).catch(() => {});
  }, [accessibleCompanies.length]);
  const save = async () => {
    if (!form.companyId || !form.loanTypeId || !form.policyName.trim() || !form.allowedRepaymentMethods.length) { setError('Choose a company, loan type, policy name and at least one repayment method.'); return; }
    setSaving(true); setError('');
    try { const policy = await loanGovernanceApi.createPolicy({ ...form, companyId: form.companyId || companyId, additionalApproverRole: 'HR Director', allowedContractTypes: contractTypesText.split(',').map(value => value.trim()).filter(Boolean) }); setOpen(false); setNotice(`Policy version ${policy.version} created. Existing applications retain their policy version.`); await load(); }
    catch (e) { setError(loanErrorMessage(e, 'Unable to create policy version.')); }
    finally { setSaving(false); }
  };
  const begin = (policy?: LoanPolicy) => { setForm(policy ? { ...policy, companyId } : { ...defaults, companyId, loanTypeId: typeId || loanTypes[0]?.id || '' }); setContractTypesText(policy?.allowedContractTypes.join(', ') ?? ''); setError(''); setOpen(true); };
  return <div className="space-y-4">
    <div className="flex flex-wrap items-end justify-between gap-3"><div><h2 className="font-semibold">Company loan policy versions</h2><p className="text-sm text-slate-500">Eligibility is checked before application and payment. Each change creates a new version; existing versions remain in history.</p></div><button className="btn-primary" onClick={() => begin()} disabled={!companyId}>New Policy Version</button></div>
    {error && !open && <p role="alert" className="text-sm text-red-600">{error}</p>}
    {notice && <p role="status" className="text-sm text-emerald-700">{notice}</p>}
    <div className="flex flex-wrap gap-3"><label className="text-sm">Company<select className="select ms-2" value={companyId} onChange={e => setCompanyId(e.target.value)}><option value="">Select company</option>{companies.map(c => <option key={c.id} value={c.id}>{c.legalNameEn}</option>)}</select></label><label className="text-sm">Loan type<select className="select ms-2" value={typeId} onChange={e => setTypeId(e.target.value)}><option value="">All types</option>{loanTypes.map(t => <option key={t.id} value={t.id}>{t.nameEn}</option>)}</select></label></div>
    <div className="surface overflow-x-auto"><table className="w-full text-sm"><thead><tr>{['Policy', 'Type', 'Version', 'Limits', 'Approval route', 'Status', ''].map((label, i) => <th key={i} className="p-3 text-start text-xs text-slate-500">{label}</th>)}</tr></thead><tbody>{policies.length === 0 ? <tr><td colSpan={7} className="p-6 text-center text-slate-500">No policy versions for this selection.</td></tr> : policies.map(policy => <tr key={policy.id} className="border-t border-slate-100 dark:border-white/10"><td className="p-3">{policy.policyName}</td><td className="p-3">{loanTypes.find(type => type.id === policy.loanTypeId)?.nameEn ?? policy.loanTypeId}</td><td className="p-3">v{policy.version}</td><td className="p-3">{policy.maxInstallments} installments · {policy.maxConcurrentLoans} concurrent</td><td className="p-3">HR Manager{policy.additionalApprovalThreshold > 0 ? ` → ${policy.additionalApproverRole} above ${policy.additionalApprovalThreshold.toLocaleString()}` : ''}</td><td className="p-3">{policy.isActive ? 'Active' : 'Inactive'}</td><td className="p-3"><button className="text-sapphire" onClick={() => begin(policy)}>Review / New Version</button></td></tr>)}</tbody></table></div>
    <Modal isOpen={open} title="New Company Loan Policy Version" size="lg" onClose={() => !saving && setOpen(false)} footer={<><button className="btn-secondary" disabled={saving} onClick={() => setOpen(false)}>Cancel</button><button className="btn-primary" disabled={saving} onClick={save}>{saving ? 'Saving…' : 'Create Policy Version'}</button></>}>
      <div className="space-y-4">{error && <p role="alert" className="text-sm text-red-600">{error}</p>}<p className="text-sm text-slate-500">Amount limits use the employee company currency. Zero monetary or salary limits mean no additional limit; zero additional approval threshold disables the extra approval. Publishing makes this the active version.</p>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <label className="text-sm">Company<select className="select mt-1 w-full" value={form.companyId} onChange={e => setForm(f => ({ ...f, companyId: e.target.value }))}><option value="">Select company</option>{companies.map(c => <option key={c.id} value={c.id}>{c.legalNameEn}</option>)}</select></label>
          <label className="text-sm">Loan type<select className="select mt-1 w-full" value={form.loanTypeId} onChange={e => setForm(f => ({ ...f, loanTypeId: e.target.value }))}>{loanTypes.map(t => <option key={t.id} value={t.id}>{t.nameEn}</option>)}</select></label>
          <label className="text-sm sm:col-span-2">Policy name<input className="input mt-1 w-full" maxLength={160} value={form.policyName} onChange={e => setForm(f => ({ ...f, policyName: e.target.value }))} /></label>
          {numericFields.map(([key, label]) => <label key={key} className="text-sm">{label}<input type="number" min="0" step={['maxMultiplierOfSalary', 'maxInstallmentPercentOfSalary', 'maxAmount', 'maxTotalOutstanding', 'additionalApprovalThreshold'].includes(key) ? '0.01' : '1'} className="input mt-1 w-full" value={form[key]} onChange={e => setForm(f => ({ ...f, [key]: Number(e.target.value) }))} /></label>)}
          <label className="text-sm">Additional approver role<input className="input mt-1 w-full" value="HR Director" readOnly /></label>
          <label className="text-sm">Contract types (comma-separated, blank for all)<input className="input mt-1 w-full" value={contractTypesText} onChange={e => setContractTypesText(e.target.value)} /></label>
        </div>
        <fieldset><legend className="mb-2 text-sm font-semibold">Allowed employment statuses</legend><div className="flex flex-wrap gap-3">{['Active', 'Offboarded'].map(value => <label key={value} className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.allowedEmploymentStatuses.includes(value)} onChange={e => setForm(f => ({ ...f, allowedEmploymentStatuses: e.target.checked ? [...f.allowedEmploymentStatuses, value] : f.allowedEmploymentStatuses.filter(status => status !== value) }))} />{value}</label>)}</div></fieldset>
        <fieldset><legend className="mb-2 text-sm font-semibold">Allowed repayment methods</legend><div className="flex flex-wrap gap-3">{Object.entries(repaymentMethodLabels).map(([value, label]) => <label key={value} className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.allowedRepaymentMethods.includes(value)} onChange={e => setForm(f => ({ ...f, allowedRepaymentMethods: e.target.checked ? [...f.allowedRepaymentMethods, value] : f.allowedRepaymentMethods.filter(method => method !== value) }))} />{label}</label>)}</div></fieldset>
        <fieldset><legend className="mb-2 text-sm font-semibold">Allowed repayment frequencies</legend><div className="flex flex-wrap gap-3">{['Monthly', 'Weekly', 'BiWeekly', 'Quarterly'].map(value => <label key={value} className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form.allowedRepaymentFrequencies.includes(value)} onChange={e => setForm(f => ({ ...f, allowedRepaymentFrequencies: e.target.checked ? [...f.allowedRepaymentFrequencies, value] : f.allowedRepaymentFrequencies.filter(frequency => frequency !== value) }))} />{value}</label>)}</div></fieldset>
        <div className="grid gap-2 sm:grid-cols-2">{checks.map(([key, label]) => <label key={key} className="flex items-center gap-2 text-sm"><input type="checkbox" checked={form[key]} onChange={e => setForm(f => ({ ...f, [key]: e.target.checked }))} />{label}</label>)}</div>
      </div>
    </Modal>
  </div>;
}
