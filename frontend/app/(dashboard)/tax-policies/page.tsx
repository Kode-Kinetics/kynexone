'use client';

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, Landmark, Plus, RefreshCw } from 'lucide-react';
import { taxPoliciesApi, type CompanyTaxPolicy, type CompanyTaxPolicyInput } from '@/src/api/governance';
import { useAuth } from '@/src/contexts/AuthContext';
import { useCompany } from '@/src/contexts/CompanyContext';

// The six GCC states levy 0% statutory personal income tax (mirrors the server's
// CountryTier.IsZeroPersonalIncomeTax). Setting a non-zero CompanyTaxPolicy rate for one of
// them requires an explicit, logged acknowledgement or the write is rejected 400 by
// StatutoryRateGuard. ISO-2 and ISO-3 forms are both listed so free-text entry is covered.
const GCC_ZERO_PIT_CODES = new Set(['SA', 'SAU', 'AE', 'ARE', 'QA', 'QAT', 'KW', 'KWT', 'OM', 'OMN', 'BH', 'BHR']);
const isGccZeroPit = (code: string) => GCC_ZERO_PIT_CODES.has(code.trim().toUpperCase());

/** Per-company tax policy admin. Configurable policy foundation — NOT legal or tax advice. */
export default function TaxPoliciesPage() {
  const { hasRole } = useAuth();
  const { companies, companyVersion } = useCompany();
  const [policies, setPolicies] = useState<CompanyTaxPolicy[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showCreate, setShowCreate] = useState(false);
  const canManage = hasRole('Admin') || hasRole('Payroll Manager');

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setPolicies(await taxPoliciesApi.list());
    } catch {
      setError('Could not load tax policies.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load, companyVersion]);

  const companyName = (id: string | null) =>
    id === null ? 'Tenant default' : (companies.find((c) => c.id === id)?.name ?? 'Company');

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">Tax Policies</h1>
          <p className="text-xs text-slate-400">Per-legal-entity income tax configuration</p>
        </div>
        {canManage && (
          <button type="button" onClick={() => setShowCreate(true)}
            className="flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-2 text-xs font-semibold text-white transition hover:opacity-90">
            <Plus className="h-3.5 w-3.5" /> New policy
          </button>
        )}
      </div>

      <div className="rounded-xl border border-blue-200 bg-blue-50 px-4 py-2.5 text-xs font-medium text-blue-800 dark:border-blue-500/20 dark:bg-blue-500/[0.06] dark:text-blue-300">
        Configurable policy foundation — not legal or tax advice. Values are customer-entered configuration and require validation by qualified advisors.
      </div>

      {loading ? (
        <div className="h-56 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
      ) : error ? (
        <div className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <AlertTriangle className="h-8 w-8 text-amber-500" />
          <p className="text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" /> Retry
          </button>
        </div>
      ) : policies.length === 0 ? (
        <div className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 bg-white p-10 text-center dark:border-white/[0.06] dark:bg-white/[0.03]">
          <Landmark className="h-10 w-10 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-300">No tax policies configured yet.</p>
          <p className="text-xs text-slate-400">Payroll falls back to the legacy tenant-wide setting until a policy is created.</p>
        </div>
      ) : (
        <div className="overflow-x-auto rounded-2xl border border-slate-200/80 bg-white shadow-sm dark:border-white/[0.06] dark:bg-white/[0.03]">
          <table className="w-full min-w-[640px] text-left text-sm">
            <thead>
              <tr className="border-b border-slate-100 text-[11px] uppercase tracking-wide text-slate-400 dark:border-white/[0.06]">
                <th className="px-4 py-3 font-semibold">Scope</th>
                <th className="px-4 py-3 font-semibold">Country</th>
                <th className="px-4 py-3 font-semibold">Effective</th>
                <th className="px-4 py-3 font-semibold">Rate %</th>
                <th className="px-4 py-3 font-semibold">Bonus</th>
                <th className="px-4 py-3 font-semibold">Status</th>
              </tr>
            </thead>
            <tbody>
              {policies.map((p) => (
                <tr key={p.id} className="border-b border-slate-50 last:border-0 dark:border-white/[0.03]">
                  <td className="px-4 py-3 font-medium text-slate-800 dark:text-slate-100">{companyName(p.companyId)}</td>
                  <td className="px-4 py-3 text-slate-500 dark:text-slate-400">{p.countryCode}</td>
                  <td className="px-4 py-3 text-slate-500 dark:text-slate-400">{p.effectiveFrom}{p.effectiveTo ? ` → ${p.effectiveTo}` : ''}</td>
                  <td className="px-4 py-3 font-semibold text-slate-700 dark:text-slate-200">{p.incomeTaxRatePercent ?? '—'}</td>
                  <td className="px-4 py-3 text-slate-500">{p.appliesToBonus ? 'Included' : 'Excluded'}</td>
                  <td className="px-4 py-3">
                    <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${p.status === 'Active' ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300' : 'bg-slate-200 text-slate-600 dark:bg-white/[0.08] dark:text-slate-300'}`}>{p.status}</span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {showCreate && (
        <CreatePolicyModal
          companies={companies.map((c) => ({ id: c.id, name: c.name }))}
          onClose={() => setShowCreate(false)}
          onCreated={() => { setShowCreate(false); void load(); }}
        />
      )}
    </div>
  );
}

function CreatePolicyModal({ companies, onClose, onCreated }: {
  companies: { id: string; name: string }[];
  onClose: () => void;
  onCreated: () => void;
}) {
  const [form, setForm] = useState<CompanyTaxPolicyInput>({
    companyId: companies[0]?.id ?? null,
    countryCode: 'SA',
    effectiveFrom: new Date().toISOString().slice(0, 10),
    incomeTaxRatePercent: 0,
    appliesToBonus: true,
    acknowledgeNonZeroGccTax: false,
  });
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Reactive fallback: reveal the acknowledgement even if local GCC detection missed the code
  // the user typed, once the server has told us it's required.
  const [serverAckRequired, setServerAckRequired] = useState(false);

  // The compliance gate only applies to a NON-ZERO rate in a zero-PIT GCC jurisdiction.
  const needsGccAck = isGccZeroPit(form.countryCode) && (form.incomeTaxRatePercent ?? 0) > 0;
  const showAck = needsGccAck || serverAckRequired;
  const ackBlocksSubmit = showAck && !form.acknowledgeNonZeroGccTax;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    // Mirror the server gate client-side so we never round-trip a guaranteed 400: the acknowledgement
    // must be a deliberate, explicit act before a non-zero PIT can be set in a GCC state.
    if (ackBlocksSubmit) {
      setError('This is a zero personal-income-tax GCC jurisdiction. Tick the acknowledgement to set a non-zero income tax rate — the action is logged.');
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      // Only send the flag when the gate actually applies; otherwise leave it unset (default false).
      await taxPoliciesApi.create({ ...form, acknowledgeNonZeroGccTax: showAck ? form.acknowledgeNonZeroGccTax : undefined });
      onCreated();
    } catch (err: unknown) {
      const data = (err as { response?: { data?: { message?: string; error?: string } } })?.response?.data;
      const message = data?.message ?? data?.error;
      if (message && /acknowledgeNonZeroGccTax/i.test(message)) setServerAckRequired(true);
      setError(message ?? 'Could not create the policy.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true" aria-label="Create tax policy">
      <form onSubmit={submit} className="w-full max-w-md space-y-3 rounded-2xl border border-slate-200 bg-white p-5 shadow-2xl dark:border-white/[0.08] dark:bg-[#0c1120]">
        <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">New tax policy</h2>
        {error && <p className="rounded-lg bg-rose-50 px-3 py-2 text-xs font-medium text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300">{error}</p>}

        <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
          Scope
          <select
            value={form.companyId ?? ''}
            onChange={(e) => setForm((f) => ({ ...f, companyId: e.target.value === '' ? null : e.target.value }))}
            className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100"
          >
            <option value="">Tenant default (all companies without an override)</option>
            {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>

        <div className="grid grid-cols-2 gap-3">
          <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
            Country code (ISO-2) <span className="text-rose-500">*</span>
            <input value={form.countryCode} required onChange={(e) => setForm((f) => ({ ...f, countryCode: e.target.value.toUpperCase() }))}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100" />
          </label>
          <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
            Rate %
            <input type="number" min={0} max={100} step={0.01} value={form.incomeTaxRatePercent ?? 0}
              onChange={(e) => setForm((f) => ({ ...f, incomeTaxRatePercent: Number(e.target.value) }))}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100" />
          </label>
        </div>

        {showAck && (
          <div className="rounded-lg border border-amber-300 bg-amber-50 p-3 dark:border-amber-500/30 dark:bg-amber-500/[0.08]">
            <div className="flex items-start gap-2">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-500" />
              <div className="space-y-2">
                <p className="text-[11px] font-medium leading-snug text-amber-800 dark:text-amber-300">
                  <span className="font-semibold">{form.countryCode.trim().toUpperCase() || 'This jurisdiction'}</span> is a zero personal-income-tax GCC jurisdiction. Setting a non-zero rate overrides the statutory default and is recorded.
                </p>
                <label className="flex items-start gap-2 text-[11px] font-medium text-amber-900 dark:text-amber-200">
                  <input type="checkbox" className="mt-0.5 shrink-0" checked={form.acknowledgeNonZeroGccTax ?? false}
                    onChange={(e) => setForm((f) => ({ ...f, acknowledgeNonZeroGccTax: e.target.checked }))} />
                  I acknowledge this non-zero income tax rate for a zero-PIT GCC jurisdiction. This action is logged.
                </label>
              </div>
            </div>
          </div>
        )}

        <div className="grid grid-cols-2 gap-3">
          <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
            Effective from <span className="text-rose-500">*</span>
            <input type="date" required value={form.effectiveFrom} onChange={(e) => setForm((f) => ({ ...f, effectiveFrom: e.target.value }))}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100" />
          </label>
          <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
            Effective to
            <input type="date" value={form.effectiveTo ?? ''} onChange={(e) => setForm((f) => ({ ...f, effectiveTo: e.target.value || null }))}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100" />
          </label>
        </div>

        <label className="flex items-center gap-2 text-xs font-medium text-slate-600 dark:text-slate-300">
          <input type="checkbox" checked={form.appliesToBonus ?? true} onChange={(e) => setForm((f) => ({ ...f, appliesToBonus: e.target.checked }))} />
          Apply to bonuses
        </label>

        <div className="flex justify-end gap-2 pt-1">
          <button type="button" onClick={onClose} className="rounded-lg px-3 py-2 text-xs font-semibold text-slate-500 hover:bg-slate-100 dark:hover:bg-white/[0.05]">Cancel</button>
          <button type="submit" disabled={submitting || ackBlocksSubmit} className="rounded-lg bg-sapphire px-4 py-2 text-xs font-semibold text-white transition hover:opacity-90 disabled:opacity-50">
            {submitting ? 'Creating…' : 'Create policy'}
          </button>
        </div>
      </form>
    </div>
  );
}
