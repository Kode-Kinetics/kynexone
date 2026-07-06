'use client';

import { useState } from 'react';
import { Building2, CheckCircle2 } from 'lucide-react';
import { platformApi, type PlatformTenantDetail } from '@/src/api/platform';

/**
 * Customer/group governance — deliberately separate from the Billing tab (SaaS
 * commercial controls) and the Companies list below (legal-entity operations):
 * account type, company creation mode, and draft-company approval.
 */
export function GovernanceTab({ tenant, onRefresh }: { tenant: PlatformTenantDetail; onRefresh: () => void }) {
  const [accountType, setAccountType] = useState(tenant.accountType ?? 'SingleCompany');
  const [creationMode, setCreationMode] = useState(tenant.companyCreationMode ?? 'GroupSelfServiceWithinLimit');
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);
  const companies = tenant.companies ?? [];
  const strictModePending = process.env.NEXT_PUBLIC_STRICTMODE_PENDING === 'true';

  const saveAccountType = async (next: 'SingleCompany' | 'Group') => {
    setSaving(true);
    setMessage(null);
    try {
      await platformApi.setAccountType(tenant.id, next);
      setAccountType(next);
      setMessage({ kind: 'ok', text: `Account type set to ${next}.` });
      onRefresh();
    } catch (e: unknown) {
      const data = (e as { response?: { data?: { error?: string; message?: string } } })?.response?.data;
      setMessage({
        kind: 'err',
        text: data?.error === 'multiple_active_companies'
          ? 'Downgrade blocked: this tenant has multiple active companies. Deactivate the extra companies first.'
          : data?.message ?? 'Could not change the account type.',
      });
    } finally {
      setSaving(false);
    }
  };

  const saveCreationMode = async (mode: string) => {
    setSaving(true);
    setMessage(null);
    try {
      await platformApi.setCompanyCreationMode(tenant.id, mode);
      setCreationMode(mode);
      setMessage({ kind: 'ok', text: 'Company creation mode updated.' });
      onRefresh();
    } catch {
      setMessage({ kind: 'err', text: 'Could not change the creation mode.' });
    } finally {
      setSaving(false);
    }
  };

  const approve = async (companyId: string) => {
    setSaving(true);
    setMessage(null);
    try {
      await platformApi.approveCompany(tenant.id, companyId);
      setMessage({ kind: 'ok', text: 'Company approved and activated.' });
      onRefresh();
    } catch {
      setMessage({ kind: 'err', text: 'Could not approve the company.' });
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-5">
      {strictModePending && (
        <div className="rounded-xl border border-amber-500/30 bg-amber-500/[0.08] px-4 py-2.5 text-xs font-medium text-amber-300">
          Company-scope StrictMode cutover is pending — see docs/GROUP_COMPANY_STRICTMODE_CUTOVER.md before enabling group features for production customers.
        </div>
      )}

      {message && (
        <div className={`rounded-xl px-4 py-2.5 text-xs font-medium ${message.kind === 'ok' ? 'border border-emerald-500/30 bg-emerald-500/[0.08] text-emerald-300' : 'border border-rose-500/30 bg-rose-500/[0.08] text-rose-300'}`}>
          {message.text}
        </div>
      )}

      {/* Customer governance */}
      <div className="rounded-2xl border border-white/[0.06] bg-white/[0.02] p-5">
        <h2 className="mb-1 text-sm font-bold text-white">Customer governance</h2>
        <p className="mb-4 text-xs text-slate-500">Product behavior for this customer — distinct from the commercial limits on the Billing tab.</p>
        <div className="grid gap-4 md:grid-cols-2">
          <div>
            <label className="mb-1 block text-xs text-slate-400">Account type</label>
            <select
              value={accountType}
              disabled={saving}
              onChange={(e) => void saveAccountType(e.target.value as 'SingleCompany' | 'Group')}
              className="w-full rounded-lg border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-slate-200 outline-none focus:border-indigo-400/50 disabled:opacity-40"
            >
              <option value="SingleCompany">Single company</option>
              <option value="Group">Group (multiple legal entities)</option>
            </select>
            <p className="mt-1 text-[10px] text-slate-500">Downgrade is blocked while more than one active company exists.</p>
          </div>
          <div>
            <label className="mb-1 block text-xs text-slate-400">Company creation mode</label>
            <select
              value={creationMode}
              disabled={saving || accountType !== 'Group'}
              onChange={(e) => void saveCreationMode(e.target.value)}
              className="w-full rounded-lg border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-slate-200 outline-none focus:border-indigo-400/50 disabled:opacity-40"
            >
              <option value="GroupSelfServiceWithinLimit">Group self-service (within limit)</option>
              <option value="GroupDraftPlatformApproval">Group drafts + platform approval</option>
              <option value="PlatformControlled">Platform controlled</option>
            </select>
            <p className="mt-1 text-[10px] text-slate-500">Who creates legal entities for this customer and how they activate.</p>
          </div>
        </div>
      </div>

      {/* Legal-entity operations */}
      <div className="rounded-2xl border border-white/[0.06] bg-white/[0.02] p-5">
        <h2 className="mb-1 text-sm font-bold text-white">Companies (legal entities)</h2>
        <p className="mb-4 text-xs text-slate-500">{companies.length} compan{companies.length === 1 ? 'y' : 'ies'} in this account</p>
        {companies.length === 0 ? (
          <div className="flex flex-col items-center gap-2 py-8 text-center">
            <Building2 className="h-8 w-8 text-slate-600" />
            <p className="text-xs text-slate-500">No companies yet — the default company is created by the backfill on first boot.</p>
          </div>
        ) : (
          <table className="w-full text-left text-sm">
            <thead>
              <tr className="border-b border-white/[0.06] text-[11px] uppercase tracking-wide text-slate-500">
                <th className="px-2 py-2 font-semibold">Company</th>
                <th className="px-2 py-2 font-semibold">Code</th>
                <th className="px-2 py-2 font-semibold">Country</th>
                <th className="px-2 py-2 font-semibold">Status</th>
                <th className="px-2 py-2 text-right font-semibold">Actions</th>
              </tr>
            </thead>
            <tbody>
              {companies.map((c) => (
                <tr key={c.id} className="border-b border-white/[0.03] last:border-0">
                  <td className="px-2 py-2.5 font-medium text-slate-200">{c.name}</td>
                  <td className="px-2 py-2.5 text-slate-400">{c.code}</td>
                  <td className="px-2 py-2.5 text-slate-400">{c.countryCode}</td>
                  <td className="px-2 py-2.5">
                    {c.approvalStatus === 'Draft' ? (
                      <span className="rounded-full bg-amber-500/[0.12] px-2 py-0.5 text-[10px] font-semibold text-amber-300">Draft — awaiting approval</span>
                    ) : c.isActive ? (
                      <span className="rounded-full bg-emerald-500/[0.12] px-2 py-0.5 text-[10px] font-semibold text-emerald-300">Active</span>
                    ) : (
                      <span className="rounded-full bg-white/[0.08] px-2 py-0.5 text-[10px] font-semibold text-slate-300">Suspended</span>
                    )}
                  </td>
                  <td className="px-2 py-2.5 text-right">
                    {c.approvalStatus === 'Draft' && (
                      <button
                        type="button"
                        disabled={saving}
                        onClick={() => void approve(c.id)}
                        className="inline-flex items-center gap-1 rounded-lg border border-emerald-500/30 px-2.5 py-1 text-[11px] font-semibold text-emerald-300 transition hover:bg-emerald-500/[0.08] disabled:opacity-40"
                      >
                        <CheckCircle2 className="h-3 w-3" /> Approve
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
