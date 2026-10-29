'use client';

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, Landmark, Pencil, Plus, RefreshCw, Trash2 } from 'lucide-react';
import { notifyApiError, apiErrorReason } from '../api/client';
import { taxPoliciesApi, type CompanyTaxPolicy, type CompanyTaxPolicyInput } from '../api/governance';
import { useConfirm } from '../components/gl/glUi';
import { useAuth } from '../contexts/AuthContext';
import { useCompany } from '../contexts/CompanyContext';
import { useT } from '../hooks/useT';
import { taxPolicyActions } from '../lib/taxPolicyActions';
import { EnumLabel } from '../components/EnumLabel';

// The six GCC states levy 0% statutory personal income tax (mirrors the server's
// CountryTier.IsZeroPersonalIncomeTax). Setting a non-zero CompanyTaxPolicy rate for one of
// them requires an explicit, logged acknowledgement or the write is rejected 400 by
// StatutoryRateGuard. ISO-2 and ISO-3 forms are both listed so free-text entry is covered.
const GCC_ZERO_PIT_CODES = new Set(['SA', 'SAU', 'AE', 'ARE', 'QA', 'QAT', 'KW', 'KWT', 'OM', 'OMN', 'BH', 'BHR']);
const isGccZeroPit = (code: string) => GCC_ZERO_PIT_CODES.has(code.trim().toUpperCase());

/**
 * Per-company tax policy admin. Configurable policy foundation — NOT legal or tax advice. Shown as the Setup section
 * Tax policies, which carries the title; the "New policy" action stays with Admin and Payroll Manager.
 */
export function TaxPoliciesPage() {
  const t = useT();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const { hasRole } = useAuth();
  const { companies, companyVersion } = useCompany();
  const [policies, setPolicies] = useState<CompanyTaxPolicy[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showCreate, setShowCreate] = useState(false);
  const [editing, setEditing] = useState<CompanyTaxPolicy | null>(null);
  const [retiring, setRetiring] = useState<CompanyTaxPolicy | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const canManage = hasRole('Admin') || hasRole('Payroll Manager');

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setPolicies(await taxPoliciesApi.list());
    } catch {
      setError(t('Could not load tax policies.'));
    } finally {
      setLoading(false);
    }
  }, [t]);

  useEffect(() => { void load(); }, [load, companyVersion]);

  const companyName = (id: string | null) =>
    id === null ? t('Tenant default') : (companies.find((c) => c.id === id)?.name ?? t('Company'));

  const today = new Date().toISOString().slice(0, 10);

  const remove = async (p: CompanyTaxPolicy) => {
    const ok = await confirm({
      title: t('Remove tax policy'),
      message: t('Remove this tax policy? It has never been used by a finalized payroll run. This cannot be undone.'),
      confirmLabel: t('Remove'),
      tone: 'danger',
    });
    if (!ok) return;
    setBusyId(p.id);
    try { await taxPoliciesApi.remove(p.id); await load(); } catch (e) { notifyApiError(e); await load(); }
    finally { setBusyId(null); }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-xs text-slate-400">{t('Per-legal-entity income tax configuration')}</p>
        {canManage && (
          <button type="button" onClick={() => setShowCreate(true)}
            className="flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-2 text-xs font-semibold text-white transition hover:opacity-90">
            <Plus className="h-3.5 w-3.5" aria-hidden="true" /> {t('New policy')}
          </button>
        )}
      </div>

      <div className="rounded-xl border border-blue-200 bg-blue-50 px-4 py-2.5 text-xs font-medium text-blue-800 dark:border-blue-500/20 dark:bg-blue-500/[0.06] dark:text-blue-300">
        {t('Configurable policy foundation — not legal or tax advice. Values are customer-entered configuration and require validation by qualified advisors.')}
      </div>

      {loading ? (
        <div className="h-56 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
      ) : error ? (
        <div className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <AlertTriangle className="h-8 w-8 text-amber-500" />
          <p className="text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" /> {t('Retry')}
          </button>
        </div>
      ) : policies.length === 0 ? (
        <div className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 bg-white p-10 text-center dark:border-white/[0.06] dark:bg-white/[0.03]">
          <Landmark className="h-10 w-10 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-300">{t('No tax policies configured yet.')}</p>
          <p className="text-xs text-slate-400">{t('Payroll falls back to the legacy tenant-wide setting until a policy is created.')}</p>
        </div>
      ) : (
        <div className="overflow-x-auto rounded-2xl border border-slate-200/80 bg-white shadow-sm dark:border-white/[0.06] dark:bg-white/[0.03]">
          <table className="w-full min-w-[640px] text-start text-sm">
            <thead>
              <tr className="border-b border-slate-100 text-caption uppercase tracking-wide text-slate-400 dark:border-white/[0.06]">
                <th className="px-4 py-3 font-semibold">{t('Scope')}</th>
                <th className="px-4 py-3 font-semibold">{t('Country')}</th>
                <th className="px-4 py-3 font-semibold">{t('Effective')}</th>
                <th className="px-4 py-3 font-semibold">{t('Rate %')}</th>
                <th className="px-4 py-3 font-semibold">{t('Bonus')}</th>
                <th className="px-4 py-3 font-semibold">{t('Status')}</th>
                {canManage && <th className="px-4 py-3 font-semibold"><span className="sr-only">{t('Actions')}</span></th>}
              </tr>
            </thead>
            <tbody>
              {policies.map((p) => (
                <tr key={p.id} className="border-b border-slate-50 last:border-0 dark:border-white/[0.03]">
                  <td className="px-4 py-3 font-medium text-slate-800 dark:text-slate-100">{companyName(p.companyId)}</td>
                  <td className="px-4 py-3 text-slate-500 dark:text-slate-400">{p.countryCode}</td>
                  <td className="px-4 py-3 text-slate-500 dark:text-slate-400">{p.effectiveFrom}{p.effectiveTo ? ` → ${p.effectiveTo}` : ''}</td>
                  <td className="px-4 py-3 font-semibold text-slate-700 dark:text-slate-200">{p.incomeTaxRatePercent ?? '—'}</td>
                  <td className="px-4 py-3 text-slate-500">{p.appliesToBonus ? t('Included') : t('Excluded')}</td>
                  <td className="px-4 py-3">
                    <span className={`rounded-full px-2 py-0.5 text-caption font-semibold ${p.status === 'Active' ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300' : 'bg-slate-200 text-slate-600 dark:bg-white/[0.08] dark:text-slate-300'}`}><EnumLabel enum="Status" value={p.status} /></span>
                  </td>
                  {canManage && (() => {
                    const a = taxPolicyActions(p, today);
                    return (
                      <td className="px-4 py-3">
                        <div className="flex flex-wrap items-center gap-1">
                          <button type="button" onClick={() => setEditing(p)} className="btn-secondary h-7 px-2 text-xs">
                            <Pencil className="h-3 w-3" aria-hidden="true" /> {t('Edit')}
                          </button>
                          {a.canRetire && (
                            <button type="button" onClick={() => setRetiring(p)} className="btn-secondary h-7 px-2 text-xs">{t('End from date')}</button>
                          )}
                          {a.canRemove ? (
                            <button type="button" onClick={() => remove(p)} disabled={busyId === p.id} aria-label={t('Remove tax policy')}
                              className="inline-flex h-7 items-center gap-1 rounded-lg border border-slate-200 px-2 text-xs font-medium text-slate-600 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-600 disabled:opacity-40 dark:border-white/10 dark:text-slate-300 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400">
                              <Trash2 className="h-3.5 w-3.5" aria-hidden="true" /> {t('Remove')}
                            </button>
                          ) : a.removeBlockedReason ? (
                            <span className="max-w-[16rem] text-caption text-slate-500 dark:text-slate-400">{t(a.removeBlockedReason)}</span>
                          ) : null}
                        </div>
                      </td>
                    );
                  })()}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {(showCreate || editing) && (
        <CreatePolicyModal
          key={editing?.id ?? 'new'}
          existing={editing}
          companies={companies.map((c) => ({ id: c.id, name: c.name }))}
          onClose={() => { setShowCreate(false); setEditing(null); }}
          onCreated={() => { setShowCreate(false); setEditing(null); void load(); }}
        />
      )}
      {retiring && (
        <RetireDialog
          policy={retiring}
          onClose={() => setRetiring(null)}
          onDone={() => { setRetiring(null); void load(); }}
        />
      )}
      {confirmDialog}
    </div>
  );
}

function RetireDialog({ policy, onClose, onDone }: { policy: CompanyTaxPolicy; onClose: () => void; onDone: () => void }) {
  const t = useT();
  const [date, setDate] = useState(policy.effectiveTo ?? new Date().toISOString().slice(0, 10));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true); setError(null);
    try { await taxPoliciesApi.retire(policy.id, date); onDone(); }
    catch (err) { setError(apiErrorReason(err, t('Could not end the policy.'))); }
    finally { setSaving(false); }
  };
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true" aria-label={t('End tax policy from a date')}>
      <form onSubmit={submit} className="w-full max-w-sm space-y-3 rounded-2xl border border-slate-200 bg-white p-5 shadow-e3 dark:border-white/[0.08] dark:bg-surface">
        <h2 className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('End tax policy from a date')}</h2>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('The policy stays on record and payroll already run is not changed. Add a new policy for the period after this date.')}</p>
        {error && <p className="rounded-lg bg-rose-50 px-3 py-2 text-xs font-medium text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300">{error}</p>}
        <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
          {t('Last day this policy applies')}
          <input type="date" required min={policy.effectiveFrom} value={date} onChange={(e) => setDate(e.target.value)}
            className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100" />
        </label>
        <div className="flex justify-end gap-2 pt-1">
          <button type="button" onClick={onClose} className="rounded-lg px-3 py-2 text-xs font-semibold text-slate-500 hover:bg-slate-100 dark:hover:bg-white/[0.05]">{t('Cancel')}</button>
          <button type="submit" disabled={saving || !date} className="rounded-lg bg-sapphire px-4 py-2 text-xs font-semibold text-white transition hover:opacity-90 disabled:opacity-50">
            {saving ? t('Saving…') : t('End policy')}
          </button>
        </div>
      </form>
    </div>
  );
}

function CreatePolicyModal({ existing, companies, onClose, onCreated }: {
  existing?: CompanyTaxPolicy | null;
  companies: { id: string; name: string }[];
  onClose: () => void;
  onCreated: () => void;
}) {
  const t = useT();
  const locked = !!existing?.usedByPayroll;
  const [form, setForm] = useState<CompanyTaxPolicyInput>(existing ? {
    companyId: existing.companyId,
    countryCode: existing.countryCode,
    effectiveFrom: existing.effectiveFrom,
    effectiveTo: existing.effectiveTo,
    status: existing.status,
    incomeTaxRatePercent: existing.incomeTaxRatePercent ?? 0,
    appliesToBonus: existing.appliesToBonus,
    notes: existing.notes,
    acknowledgeNonZeroGccTax: false,
  } : {
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
      setError(t('This is a zero personal-income-tax GCC jurisdiction. Tick the acknowledgement to set a non-zero income tax rate — the action is logged.'));
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      // Only send the flag when the gate actually applies; otherwise leave it unset (default false).
      const body = { ...form, acknowledgeNonZeroGccTax: showAck ? form.acknowledgeNonZeroGccTax : undefined };
      if (existing) await taxPoliciesApi.update(existing.id, body); else await taxPoliciesApi.create(body);
      onCreated();
    } catch (err: unknown) {
      const data = (err as { response?: { data?: { message?: string; error?: string } } })?.response?.data;
      const message = data?.message ?? data?.error;
      if (message && /acknowledgeNonZeroGccTax/i.test(message)) setServerAckRequired(true);
      setError(message ?? (existing ? t('Could not save the policy.') : t('Could not create the policy.')));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true" aria-label={existing ? t('Edit tax policy') : t('Create tax policy')}>
      <form onSubmit={submit} className="w-full max-w-md space-y-3 rounded-2xl border border-slate-200 bg-white p-5 shadow-e3 dark:border-white/[0.08] dark:bg-surface">
        <h2 className="text-sm font-semibold text-slate-800 dark:text-slate-100">{existing ? t('Edit tax policy') : t('New tax policy')}</h2>
        {locked && <p className="rounded-lg bg-amber-50 px-3 py-2 text-xs font-medium text-amber-800 dark:bg-amber-500/[0.08] dark:text-amber-300">{t('Payroll has already been finalized under this policy, so the rate and start date are fixed. You can change the end date and notes, or add a new policy.')}</p>}
        {error && <p className="rounded-lg bg-rose-50 px-3 py-2 text-xs font-medium text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300">{error}</p>}

        <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
          {t('Scope')}
          <select
            disabled={locked}
            value={form.companyId ?? ''}
            onChange={(e) => setForm((f) => ({ ...f, companyId: e.target.value === '' ? null : e.target.value }))}
            className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100"
          >
            <option value="">{t('Tenant default (all companies without an override)')}</option>
            {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>

        <div className="grid grid-cols-2 gap-3">
          <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
            {t('Country code (ISO-2)')} <span className="text-rose-500">*</span>
            <input value={form.countryCode} required disabled={locked} onChange={(e) => setForm((f) => ({ ...f, countryCode: e.target.value.toUpperCase() }))}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100" />
          </label>
          <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
            {t('Rate %')}
            <input type="number" disabled={locked} min={0} max={100} step={0.01} value={form.incomeTaxRatePercent ?? 0}
              onChange={(e) => setForm((f) => ({ ...f, incomeTaxRatePercent: Number(e.target.value) }))}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100" />
          </label>
        </div>

        {showAck && (
          <div className="rounded-lg border border-amber-300 bg-amber-50 p-3 dark:border-amber-500/30 dark:bg-amber-500/[0.08]">
            <div className="flex items-start gap-2">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-500" />
              <div className="space-y-2">
                <p className="text-caption font-medium leading-snug text-amber-800 dark:text-amber-300">
                  <span className="font-semibold">{t('{country} is a zero personal-income-tax GCC jurisdiction. Setting a non-zero rate overrides the statutory default and is recorded.', { country: form.countryCode.trim().toUpperCase() || t('This jurisdiction') })}</span>
                </p>
                <label className="flex items-start gap-2 text-caption font-medium text-amber-900 dark:text-amber-200">
                  <input type="checkbox" className="mt-0.5 shrink-0" checked={form.acknowledgeNonZeroGccTax ?? false}
                    onChange={(e) => setForm((f) => ({ ...f, acknowledgeNonZeroGccTax: e.target.checked }))} />
                  {t('I acknowledge this non-zero income tax rate for a zero-PIT GCC jurisdiction. This action is logged.')}
                </label>
              </div>
            </div>
          </div>
        )}

        <div className="grid grid-cols-2 gap-3">
          <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
            {t('Effective from')} <span className="text-rose-500">*</span>
            <input type="date" required disabled={locked} value={form.effectiveFrom} onChange={(e) => setForm((f) => ({ ...f, effectiveFrom: e.target.value }))}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100" />
          </label>
          <label className="block text-xs font-medium text-slate-600 dark:text-slate-300">
            {t('Effective to')}
            <input type="date" value={form.effectiveTo ?? ''} onChange={(e) => setForm((f) => ({ ...f, effectiveTo: e.target.value || null }))}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100" />
          </label>
        </div>

        <label className="flex items-center gap-2 text-xs font-medium text-slate-600 dark:text-slate-300">
          <input type="checkbox" disabled={locked} checked={form.appliesToBonus ?? true} onChange={(e) => setForm((f) => ({ ...f, appliesToBonus: e.target.checked }))} />
          {t('Apply to bonuses')}
        </label>

        <div className="flex justify-end gap-2 pt-1">
          <button type="button" onClick={onClose} className="rounded-lg px-3 py-2 text-xs font-semibold text-slate-500 hover:bg-slate-100 dark:hover:bg-white/[0.05]">{t('Cancel')}</button>
          <button type="submit" disabled={submitting || ackBlocksSubmit} className="rounded-lg bg-sapphire px-4 py-2 text-xs font-semibold text-white transition hover:opacity-90 disabled:opacity-50">
            {submitting ? t('Saving…') : existing ? t('Save policy') : t('Create policy')}
          </button>
        </div>
      </form>
    </div>
  );
}
