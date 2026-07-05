'use client';

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, Building2, Plus, RefreshCw } from 'lucide-react';
import { companiesApi, type CompanyDto, type CompanyRequest } from '@/src/api/organization';
import { useAuth } from '@/src/contexts/AuthContext';
import { useCompany } from '@/src/contexts/CompanyContext';

/** Group Admin company management: list, create (mode-aware), suspend/reactivate. */
export default function CompaniesPage() {
  const { hasRole } = useAuth();
  const { companyVersion } = useCompany();
  const [companies, setCompanies] = useState<CompanyDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [showCreate, setShowCreate] = useState(false);
  const canManage = hasRole('Admin') || hasRole('HR Manager');

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const result = await companiesApi.list(1, 100);
      setCompanies(result.items);
    } catch {
      setError('Could not load companies.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load, companyVersion]);

  const toggleStatus = async (company: CompanyDto) => {
    const action = company.isActive ? 'Suspend' : 'Reactivate';
    if (!window.confirm(`${action} ${company.tradeName || company.legalNameEn}?`)) return;
    setNotice(null);
    try {
      await companiesApi.setStatus(company.id, !company.isActive);
      await load();
    } catch (e: unknown) {
      const data = (e as { response?: { data?: { error?: string } } })?.response?.data;
      setNotice(data?.error === 'last_active_company'
        ? 'Cannot deactivate the only active company — activate another company first.'
        : `Could not ${action.toLowerCase()} the company.`);
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">Companies</h1>
          <p className="text-xs text-slate-400">Legal entities in this account</p>
        </div>
        {canManage && (
          <button type="button" onClick={() => setShowCreate(true)}
            className="flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-2 text-xs font-semibold text-white transition hover:opacity-90">
            <Plus className="h-3.5 w-3.5" /> New company
          </button>
        )}
      </div>

      {notice && (
        <div className="flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-2.5 text-xs font-medium text-amber-800 dark:border-amber-500/20 dark:bg-amber-500/[0.06] dark:text-amber-300">
          <AlertTriangle className="h-4 w-4 shrink-0" /> {notice}
        </div>
      )}

      {loading ? (
        <div className="h-64 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
      ) : error ? (
        <div className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <p className="text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" /> Retry
          </button>
        </div>
      ) : companies.length === 0 ? (
        <div className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 bg-white p-10 text-center dark:border-white/[0.06] dark:bg-white/[0.03]">
          <Building2 className="h-10 w-10 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-300">No companies visible to your access.</p>
        </div>
      ) : (
        <div className="overflow-x-auto rounded-2xl border border-slate-200/80 bg-white shadow-sm dark:border-white/[0.06] dark:bg-white/[0.03]">
          <table className="w-full min-w-[640px] text-left text-sm">
            <thead>
              <tr className="border-b border-slate-100 text-[11px] uppercase tracking-wide text-slate-400 dark:border-white/[0.06]">
                <th className="px-4 py-3 font-semibold">Company</th>
                <th className="px-4 py-3 font-semibold">Code</th>
                <th className="px-4 py-3 font-semibold">Country</th>
                <th className="px-4 py-3 font-semibold">Status</th>
                {canManage && <th className="px-4 py-3 text-right font-semibold">Actions</th>}
              </tr>
            </thead>
            <tbody>
              {companies.map((c) => (
                <tr key={c.id} className="border-b border-slate-50 last:border-0 dark:border-white/[0.03]">
                  <td className="px-4 py-3 font-medium text-slate-800 dark:text-slate-100">{c.tradeName || c.legalNameEn}</td>
                  <td className="px-4 py-3 text-slate-500 dark:text-slate-400">{c.legalNameEn}</td>
                  <td className="px-4 py-3 text-slate-500 dark:text-slate-400">{c.countryCode}</td>
                  <td className="px-4 py-3">
                    {c.approvalStatus === 'Draft' ? (
                      <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-semibold text-amber-700 dark:bg-amber-500/[0.12] dark:text-amber-300">Draft — awaiting platform approval</span>
                    ) : c.isActive ? (
                      <span className="rounded-full bg-emerald-100 px-2 py-0.5 text-[10px] font-semibold text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300">Active</span>
                    ) : (
                      <span className="rounded-full bg-slate-200 px-2 py-0.5 text-[10px] font-semibold text-slate-600 dark:bg-white/[0.08] dark:text-slate-300">Suspended</span>
                    )}
                  </td>
                  {canManage && (
                    <td className="px-4 py-3 text-right">
                      {c.approvalStatus !== 'Draft' && (
                        <button type="button" onClick={() => void toggleStatus(c)}
                          className="rounded-lg border border-slate-200 px-2.5 py-1 text-[11px] font-semibold text-slate-600 transition hover:bg-slate-50 dark:border-white/[0.08] dark:text-slate-300 dark:hover:bg-white/[0.05]">
                          {c.isActive ? 'Suspend' : 'Reactivate'}
                        </button>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {showCreate && <CreateCompanyModal onClose={() => setShowCreate(false)} onCreated={() => { setShowCreate(false); void load(); }} />}
    </div>
  );
}

function CreateCompanyModal({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const [form, setForm] = useState<CompanyRequest>({
    legalNameEn: '', countryCode: 'SA', registrationNumber: '', defaultCurrency: 'SAR',
  } as CompanyRequest);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await companiesApi.create(form);
      onCreated();
    } catch (err: unknown) {
      const resp = (err as { response?: { data?: { error?: string; message?: string } } })?.response?.data;
      if (resp?.error === 'account_type_single_company')
        setError('This account is a single-company account. Ask your platform administrator to enable the Group account type.');
      else if (resp?.error === 'company_limit_reached')
        setError('Your plan’s company limit is reached. Contact your account manager to upgrade.');
      else if (resp?.error === 'company_creation_platform_controlled')
        setError('Company creation for this account is managed by the platform. Contact your account manager.');
      else setError(resp?.message ?? 'Could not create the company.');
    } finally {
      setSubmitting(false);
    }
  };

  const set = (patch: Partial<CompanyRequest>) => setForm((f) => ({ ...f, ...patch }));

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true" aria-label="Create company">
      <form onSubmit={submit} className="w-full max-w-md space-y-3 rounded-2xl border border-slate-200 bg-white p-5 shadow-2xl dark:border-white/[0.08] dark:bg-[#0c1120]">
        <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">New legal entity</h2>
        {error && <p className="rounded-lg bg-rose-50 px-3 py-2 text-xs font-medium text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300">{error}</p>}
        <Field label="Legal name (English)" required value={form.legalNameEn} onChange={(v) => set({ legalNameEn: v })} />
        <Field label="Trade name" value={form.tradeName ?? ''} onChange={(v) => set({ tradeName: v })} />
        <div className="grid grid-cols-2 gap-3">
          <Field label="Country code (ISO-2)" required value={form.countryCode} onChange={(v) => set({ countryCode: v.toUpperCase() })} />
          <Field label="Currency" required value={form.defaultCurrency} onChange={(v) => set({ defaultCurrency: v.toUpperCase() })} />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Registration number" required value={form.registrationNumber} onChange={(v) => set({ registrationNumber: v })} />
          <Field label="Jurisdiction" value={form.jurisdiction ?? ''} onChange={(v) => set({ jurisdiction: v })} />
        </div>
        <div className="flex justify-end gap-2 pt-1">
          <button type="button" onClick={onClose} className="rounded-lg px-3 py-2 text-xs font-semibold text-slate-500 hover:bg-slate-100 dark:hover:bg-white/[0.05]">Cancel</button>
          <button type="submit" disabled={submitting} className="rounded-lg bg-sapphire px-4 py-2 text-xs font-semibold text-white transition hover:opacity-90 disabled:opacity-50">
            {submitting ? 'Creating…' : 'Create company'}
          </button>
        </div>
      </form>
    </div>
  );
}

function Field({ label, value, onChange, required = false }: { label: string; value: string; onChange: (v: string) => void; required?: boolean }) {
  return (
    <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
      {label}{required && <span className="text-rose-500"> *</span>}
      <input
        value={value}
        required={required}
        onChange={(e) => onChange(e.target.value)}
        className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 outline-none transition focus:border-sapphire dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100"
      />
    </label>
  );
}
