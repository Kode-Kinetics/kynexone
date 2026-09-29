'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { Plus, RefreshCw, X, ChevronRight, ArrowRight } from 'lucide-react';
import { platformApi, type PlatformLead } from '@/src/api/platform';

const PIPELINE: PlatformLead['status'][] = ['New', 'Contacted', 'DemoScheduled', 'Converted', 'Lost'];

const STATUS_CLS: Record<string, string> = {
  New:           'text-cyan-400 bg-cyan-900/30 border-cyan-700/30',
  Contacted:     'text-blue-400 bg-blue-900/30 border-blue-700/30',
  DemoScheduled: 'text-purple-400 bg-purple-900/30 border-purple-700/30',
  Converted:     'text-emerald-400 bg-emerald-900/30 border-emerald-700/30',
  Lost:          'text-slate-600 bg-transparent border-slate-800',
};

function NewLeadModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState({ companyName: '', contactName: '', contactEmail: '', phone: '', message: '', source: 'Manual' });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true); setErr('');
    try { await platformApi.createLead(form); onSaved(); onClose(); }
    catch (ex: unknown) { setErr((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed.'); }
    finally { setSaving(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-sm bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl overflow-hidden">
        <div className="flex items-center justify-between px-6 py-4 border-b border-white/[0.07]">
          <h2 className="text-sm font-semibold text-white">New Lead</h2>
          <button type="button" onClick={onClose} aria-label="Close" className="text-slate-500 hover:text-white transition-colors"><X className="h-4 w-4" /></button>
        </div>
        <form onSubmit={submit} className="px-6 py-5 space-y-3">
          {err && <div className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2">{err}</div>}
          {[
            { label: 'Company Name *', key: 'companyName', placeholder: 'Acme Corp' },
            { label: 'Contact Name *', key: 'contactName', placeholder: 'John Smith' },
            { label: 'Contact Email *', key: 'contactEmail', placeholder: 'john@acmecorp.com', type: 'email' },
            { label: 'Phone', key: 'phone', placeholder: '+971 50 000 0000' },
          ].map(({ label, key, placeholder, type }) => (
            <div key={key}>
              <label className="block text-xs text-slate-400 mb-1">{label}</label>
              <input type={type ?? 'text'} required={label.includes('*')}
                value={(form as Record<string, string>)[key]}
                onChange={e => setForm(f => ({ ...f, [key]: e.target.value }))}
                placeholder={placeholder}
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600" />
            </div>
          ))}
          <div>
            <label className="block text-xs text-slate-400 mb-1">Initial Message</label>
            <textarea rows={2} value={form.message} onChange={e => setForm(f => ({ ...f, message: e.target.value }))}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 resize-none placeholder-slate-600"
              placeholder="Looking for HR solution for 200 employees…" />
          </div>
          <div className="flex gap-3 pt-1">
            <button type="button" onClick={onClose} className="flex-1 border border-white/10 text-slate-400 rounded-lg py-2 text-sm transition-colors">Cancel</button>
            <button type="submit" disabled={saving} className="flex-1 bg-sapphire text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
              {saving ? 'Creating…' : 'Add Lead'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

interface ConvertLeadModalProps {
  lead: PlatformLead;
  onClose: () => void;
  onConverted: (result: { tenantId: string; tenantSlug: string }) => void;
}

function ConvertLeadModal({ lead, onClose, onConverted }: ConvertLeadModalProps) {
  const [form, setForm] = useState({
    tenantName: lead.companyName,
    tenantSlug: lead.companyName.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, ''),
    adminEmail: lead.contactEmail,
    adminPassword: '',
    plan: 'Trial',
    billingEmail: '',
  });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true); setErr('');
    try {
      const result = await platformApi.convertLead(lead.id, {
        tenantName: form.tenantName,
        tenantSlug: form.tenantSlug,
        adminEmail: form.adminEmail,
        adminPassword: form.adminPassword,
        plan: form.plan,
        billingEmail: form.billingEmail || undefined,
      });
      onConverted(result);
    } catch (ex: unknown) {
      setErr((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Conversion failed.');
    } finally { setSaving(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-md bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl overflow-hidden">
        <div className="flex items-center justify-between px-6 py-4 border-b border-white/[0.07]">
          <div>
            <h2 className="text-sm font-semibold text-white">Convert to Tenant</h2>
            <p className="text-xs text-slate-500 mt-0.5">Provisioning a live tenant for <span className="text-slate-300">{lead.companyName}</span></p>
          </div>
          <button type="button" onClick={onClose} aria-label="Close" className="text-slate-500 hover:text-white transition-colors"><X className="h-4 w-4" /></button>
        </div>
        <form onSubmit={submit} className="px-6 py-5 space-y-3">
          {err && <div className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2">{err}</div>}
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label htmlFor="cvt-tenant-name" className="block text-xs text-slate-400 mb-1">Tenant Name *</label>
              <input id="cvt-tenant-name" type="text" required value={form.tenantName}
                onChange={e => setForm(f => ({ ...f, tenantName: e.target.value }))}
                title="Tenant Name"
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
            </div>
            <div>
              <label htmlFor="cvt-tenant-slug" className="block text-xs text-slate-400 mb-1">Tenant Slug *</label>
              <input id="cvt-tenant-slug" type="text" required value={form.tenantSlug}
                onChange={e => setForm(f => ({ ...f, tenantSlug: e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, '') }))}
                title="Tenant Slug"
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 font-mono" />
            </div>
          </div>
          <div>
            <label htmlFor="cvt-admin-email" className="block text-xs text-slate-400 mb-1">Admin Email *</label>
            <input id="cvt-admin-email" type="email" required value={form.adminEmail}
              onChange={e => setForm(f => ({ ...f, adminEmail: e.target.value }))}
              title="Admin Email"
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
          </div>
          <div>
            <label htmlFor="cvt-admin-password" className="block text-xs text-slate-400 mb-1">Admin Password * <span className="text-slate-600">(min 10 chars)</span></label>
            <input id="cvt-admin-password" type="password" required minLength={10} value={form.adminPassword}
              onChange={e => setForm(f => ({ ...f, adminPassword: e.target.value }))}
              placeholder="Min 10 characters"
              title="Admin Password"
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600" />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label htmlFor="cvt-plan" className="block text-xs text-slate-400 mb-1">Plan</label>
              <select id="cvt-plan" value={form.plan} onChange={e => setForm(f => ({ ...f, plan: e.target.value }))}
                title="Plan"
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60">
                {['Trial', 'Starter', 'Growth', 'Enterprise'].map(p => <option key={p} value={p}>{p}</option>)}
              </select>
            </div>
            <div>
              <label htmlFor="cvt-billing-email" className="block text-xs text-slate-400 mb-1">Billing Email</label>
              <input id="cvt-billing-email" type="email" value={form.billingEmail}
                onChange={e => setForm(f => ({ ...f, billingEmail: e.target.value }))}
                placeholder="Same as admin"
                title="Billing Email"
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600" />
            </div>
          </div>
          <div className="flex gap-3 pt-1">
            <button type="button" onClick={onClose} className="flex-1 border border-white/10 text-slate-400 rounded-lg py-2 text-sm transition-colors">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex-1 bg-emerald-600 hover:bg-emerald-500 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40 flex items-center justify-center gap-1.5">
              {saving ? 'Provisioning…' : <><ArrowRight className="h-3.5 w-3.5" /> Confirm &amp; Convert</>}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

export default function LeadsPage() {
  const router = useRouter();
  const [leads, setLeads]     = useState<PlatformLead[]>([]);
  const [loading, setLoading] = useState(true);
  const [showNew, setShowNew] = useState(false);
  const [filter, setFilter]   = useState('');
  const [updating, setUpdating] = useState<string | null>(null);
  const [msg, setMsg]         = useState<{ text: string; ok: boolean } | null>(null);
  const [convertingLead, setConvertingLead] = useState<PlatformLead | null>(null);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    try { setLeads(await platformApi.listLeads()); }
    finally { setLoading(false); }
  }, []);

  async function advance(lead: PlatformLead) {
    const currentIdx = PIPELINE.indexOf(lead.status);
    if (currentIdx === -1 || currentIdx >= PIPELINE.length - 2) return;
    const next = PIPELINE[currentIdx + 1];
    setUpdating(lead.id);
    try {
      await platformApi.updateLead(lead.id, { status: next });
      setMsg({ text: `${lead.companyName} moved to ${next}.`, ok: true });
      await load();
    } catch { setMsg({ text: 'Update failed.', ok: false }); }
    finally { setUpdating(null); }
  }

  async function markLost(lead: PlatformLead) {
    setUpdating(lead.id);
    try {
      await platformApi.updateLead(lead.id, { status: 'Lost' });
      setMsg({ text: `${lead.companyName} marked as Lost.`, ok: true });
      await load();
    } catch { setMsg({ text: 'Update failed.', ok: false }); }
    finally { setUpdating(null); }
  }

  function handleConverted(result: { tenantId: string; tenantSlug: string }) {
    setConvertingLead(null);
    setMsg({
      text: `Tenant "${result.tenantSlug}" provisioned successfully! `,
      ok: true,
    });
    // Store the tenantSlug so we can display a link in the banner
    setConvertResult(result);
    load();
  }

  const [convertResult, setConvertResult] = useState<{ tenantId: string; tenantSlug: string } | null>(null);

  const filtered = leads.filter(l => !filter || l.status === filter);
  const counts = PIPELINE.reduce((acc, s) => ({ ...acc, [s]: leads.filter(l => l.status === s).length }), {} as Record<string, number>);

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Leads & Demo Requests</h1>
          <p className="text-xs text-slate-500 mt-0.5">{leads.length} total leads</p>
        </div>
        <div className="flex items-center gap-2">
          <button type="button" onClick={load} disabled={loading} aria-label="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
          <button type="button" onClick={() => setShowNew(true)}
            className="flex items-center gap-1.5 bg-sapphire hover:bg-blue-500 text-white px-3 py-1.5 rounded-lg text-sm font-semibold transition-colors">
            <Plus className="h-3.5 w-3.5" />
            New Lead
          </button>
        </div>
      </div>

      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          <span>
            {msg.text}
            {convertResult && msg.ok && (
              <button
                type="button"
                onClick={() => router.push('/platform/tenants')}
                className="ms-2 underline hover:text-emerald-300 transition-colors"
              >
                View Tenants →
              </button>
            )}
          </span>
          <button type="button" aria-label="Dismiss" onClick={() => { setMsg(null); setConvertResult(null); }}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}

      {/* Pipeline summary */}
      <div className="flex gap-2 overflow-x-auto pb-1">
        {PIPELINE.map(s => (
          <button type="button" key={s}
            onClick={() => setFilter(filter === s ? '' : s)}
            className={`flex items-center gap-1.5 px-3 py-1.5 rounded-lg border text-xs font-medium whitespace-nowrap transition-colors
              ${filter === s ? 'border-sapphire/50 bg-sapphire/10 text-blue-300' : 'border-white/[0.08] text-slate-500 hover:text-slate-300 hover:border-white/20'}`}>
            <span>{s}</span>
            <span className="text-[10px] tabular-nums opacity-70">{counts[s] ?? 0}</span>
          </button>
        ))}
      </div>

      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-16">
            <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
          </div>
        ) : filtered.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-16 gap-3">
            <p className="text-sm text-slate-600">No leads {filter ? `with status "${filter}"` : 'yet'}.</p>
            {!filter && <button type="button" onClick={() => setShowNew(true)} className="text-xs text-sapphire hover:text-blue-300">+ Add first lead</button>}
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[700px]">
              <thead>
                <tr className="border-b border-white/[0.06]">
                  {['Company', 'Contact', 'Status', 'Source', 'Created', ''].map(h => (
                    <th key={h} className="px-4 py-2.5 text-start text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {filtered.map(l => {
                  const idx = PIPELINE.indexOf(l.status);
                  const canAdvance = idx < PIPELINE.length - 2;
                  const isActive = l.status !== 'Lost' && l.status !== 'Converted';
                  const canConvert = l.status === 'DemoScheduled' && !l.convertedToTenantId;
                  return (
                    <tr key={l.id} className={`border-b border-white/[0.04] last:border-0 hover:bg-white/[0.02] transition-colors ${!isActive ? 'opacity-50' : ''}`}>
                      <td className="px-4 py-3">
                        <p className="text-sm text-white font-medium">{l.companyName}</p>
                        {l.notes && <p className="text-[11px] text-slate-600 truncate max-w-[180px]">{l.notes}</p>}
                      </td>
                      <td className="px-3 py-3">
                        <p className="text-xs text-slate-300">{l.contactName}</p>
                        <p className="text-[11px] text-slate-600">{l.contactEmail}</p>
                      </td>
                      <td className="px-3 py-3">
                        <span className={`text-[10px] font-semibold uppercase px-1.5 py-0.5 rounded border ${STATUS_CLS[l.status]}`}>
                          {l.status}
                        </span>
                      </td>
                      <td className="px-3 py-3 text-xs text-slate-500">{l.source}</td>
                      <td className="px-3 py-3 text-[11px] text-slate-600 whitespace-nowrap">
                        {new Date(l.createdAtUtc).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}
                      </td>
                      <td className="px-3 py-3">
                        <div className="flex items-center gap-1 justify-end">
                          {canConvert && (
                            <button type="button"
                              onClick={() => setConvertingLead(l)}
                              disabled={updating === l.id}
                              className="flex items-center gap-1 text-[11px] text-emerald-400 border border-emerald-500/30 hover:border-emerald-500/50 hover:bg-emerald-500/10 px-2 py-1 rounded transition-colors disabled:opacity-40 whitespace-nowrap">
                              <ArrowRight className="h-3 w-3" />
                              Convert
                            </button>
                          )}
                          {canAdvance && isActive && (
                            <button type="button"
                              onClick={() => advance(l)}
                              disabled={updating === l.id}
                              className="flex items-center gap-1 text-[11px] text-blue-400 border border-blue-500/20 hover:border-blue-500/40 px-2 py-1 rounded transition-colors disabled:opacity-40">
                              <ChevronRight className="h-3 w-3" />
                              {PIPELINE[PIPELINE.indexOf(l.status) + 1]}
                            </button>
                          )}
                          {isActive && l.status !== 'Converted' && (
                            <button type="button"
                              onClick={() => markLost(l)}
                              disabled={updating === l.id}
                              className="text-[11px] text-rose-400 border border-rose-500/20 hover:border-rose-500/40 px-2 py-1 rounded transition-colors disabled:opacity-40">
                              Lost
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {showNew && <NewLeadModal onClose={() => setShowNew(false)} onSaved={load} />}
      {convertingLead && (
        <ConvertLeadModal
          lead={convertingLead}
          onClose={() => setConvertingLead(null)}
          onConverted={handleConverted}
        />
      )}
    </div>
  );
}
