'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import {
  RefreshCw, Edit3, Check, X, ChevronDown, ChevronUp,
  ArrowRight, Building2, Users, Globe, AlertCircle, Info, Plus,
} from 'lucide-react';
import { platformApi, type PlatformPricingConfig, type PlatformPricingModule, type PlatformQuote } from '@/src/api/platform';

// ── Helpers ───────────────────────────────────────────────────────────────────

const fmt = (n: number) => `$${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
const fmtInt = (n: number) => `$${n.toLocaleString('en-US', { maximumFractionDigits: 0 })}`;

const GROUP_LABELS: Record<string, string> = {
  base: 'Base Plan Prices',
  per_employee: 'Per Extra Employee (Overage)',
  per_company: 'Per Extra Company (Overage)',
  per_admin_user: 'Per Extra Admin User (Overage)',
  supplement: 'Supplements',
  discount: 'Discounts',
  implementation: 'Implementation Estimates',
};

const QUOTE_STATUS_BADGE: Record<string, string> = {
  New:       'bg-blue-900/50 text-blue-300',
  Contacted: 'bg-amber-900/50 text-amber-300',
  Converted: 'bg-emerald-900/50 text-emerald-300',
  Lost:      'bg-slate-700 text-slate-400',
};

type Tab = 'modules' | 'config' | 'quotes';

const INPUT_CLS = 'w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600';
const LABEL_CLS = 'block text-xs text-slate-400 mb-1';

function NewQuoteModal({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState({
    companyName: '',
    contactEmail: '',
    contactName: '',
    numEmployees: '',
    estimatedMonthlyAmount: '',
    notes: '',
  });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true); setErr('');
    try {
      await platformApi.createQuote({
        companyName: form.companyName,
        contactEmail: form.contactEmail,
        contactName: form.contactName || undefined,
        numEmployees: form.numEmployees ? parseInt(form.numEmployees, 10) : undefined,
        estimatedMonthlyAmount: form.estimatedMonthlyAmount ? parseFloat(form.estimatedMonthlyAmount) : undefined,
        notes: form.notes || undefined,
      });
      onSaved();
      onClose();
    } catch (ex: unknown) {
      setErr((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to create quote.');
    } finally { setSaving(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-sm bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl overflow-hidden">
        <div className="flex items-center justify-between px-6 py-4 border-b border-white/[0.07]">
          <h2 className="text-sm font-semibold text-white">New Quote</h2>
          <button type="button" onClick={onClose} aria-label="Close" className="text-slate-500 hover:text-white transition-colors">
            <X className="h-4 w-4" />
          </button>
        </div>
        <form onSubmit={submit} className="px-6 py-5 space-y-3">
          {err && <div className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2">{err}</div>}

          <div>
            <label htmlFor="nq-company" className={LABEL_CLS}>Company Name *</label>
            <input id="nq-company" type="text" required title="Company Name"
              value={form.companyName}
              onChange={e => setForm(f => ({ ...f, companyName: e.target.value }))}
              placeholder="Acme Corp"
              className={INPUT_CLS} />
          </div>

          <div>
            <label htmlFor="nq-email" className={LABEL_CLS}>Contact Email *</label>
            <input id="nq-email" type="email" required title="Contact Email"
              value={form.contactEmail}
              onChange={e => setForm(f => ({ ...f, contactEmail: e.target.value }))}
              placeholder="contact@company.com"
              className={INPUT_CLS} />
          </div>

          <div>
            <label htmlFor="nq-contact-name" className={LABEL_CLS}>Contact Name</label>
            <input id="nq-contact-name" type="text" title="Contact Name"
              value={form.contactName}
              onChange={e => setForm(f => ({ ...f, contactName: e.target.value }))}
              placeholder="John Smith"
              className={INPUT_CLS} />
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div>
              <label htmlFor="nq-employees" className={LABEL_CLS}>Employees</label>
              <input id="nq-employees" type="number" min="1" title="Number of Employees"
                value={form.numEmployees}
                onChange={e => setForm(f => ({ ...f, numEmployees: e.target.value }))}
                placeholder="200"
                className={INPUT_CLS} />
            </div>
            <div>
              <label htmlFor="nq-amount" className={LABEL_CLS}>Est. Monthly ($)</label>
              <input id="nq-amount" type="number" min="0" step="0.01" title="Estimated Monthly Amount"
                value={form.estimatedMonthlyAmount}
                onChange={e => setForm(f => ({ ...f, estimatedMonthlyAmount: e.target.value }))}
                placeholder="0.00"
                className={INPUT_CLS} />
            </div>
          </div>

          <div>
            <label htmlFor="nq-notes" className={LABEL_CLS}>Notes</label>
            <textarea id="nq-notes" rows={3}
              value={form.notes}
              onChange={e => setForm(f => ({ ...f, notes: e.target.value }))}
              placeholder="Any additional context…"
              className={`${INPUT_CLS} resize-none`} />
          </div>

          <div className="flex gap-3 pt-1">
            <button type="button" onClick={onClose}
              className="flex-1 border border-white/10 text-slate-400 rounded-lg py-2 text-sm transition-colors">
              Cancel
            </button>
            <button type="submit" disabled={saving}
              className="flex-1 bg-sapphire hover:bg-blue-500 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
              {saving ? 'Creating…' : 'Create Quote'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

export default function PlatformPricingPage() {
  const router = useRouter();
  const [tab, setTab]             = useState<Tab>('modules');
  const [configs, setConfigs]     = useState<PlatformPricingConfig[]>([]);
  const [modules, setModules]     = useState<PlatformPricingModule[]>([]);
  const [quotes, setQuotes]       = useState<PlatformQuote[]>([]);
  const [quoteTotal, setQuoteTotal] = useState(0);
  const [loading, setLoading]     = useState(true);
  const [editing, setEditing]     = useState<string | null>(null);
  const [editVal, setEditVal]     = useState(0);
  const [saving, setSaving]       = useState(false);
  const [expandedQuote, setExpandedQuote] = useState<string | null>(null);
  const [msg, setMsg]             = useState<{ text: string; ok: boolean } | null>(null);
  const [showNewQuote, setShowNewQuote] = useState(false);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    loadAll();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const loadAll = useCallback(async () => {
    setLoading(true);
    try {
      const [c, m, q] = await Promise.all([
        platformApi.getPricingConfig(),
        platformApi.getPricingModules(),
        platformApi.listQuotes(),
      ]);
      setConfigs(c);
      setModules(m);
      setQuotes(q.items);
      setQuoteTotal(q.total);
    } catch { /* handled by 401 interceptor */ }
    finally { setLoading(false); }
  }, []);

  const saveConfig = async (key: string) => {
    setSaving(true);
    try {
      await platformApi.updatePricingConfig(key, editVal);
      setMsg({ text: 'Pricing updated.', ok: true });
      setEditing(null);
      const updated = await platformApi.getPricingConfig();
      setConfigs(updated);
    } catch { setMsg({ text: 'Update failed.', ok: false }); }
    finally { setSaving(false); }
  };

  const saveModule = async (key: string, field: string, value: boolean | number) => {
    try {
      await platformApi.updatePricingModule(key, { [field]: value });
      setMsg({ text: 'Module updated.', ok: true });
      const updated = await platformApi.getPricingModules();
      setModules(updated);
    } catch { setMsg({ text: 'Update failed.', ok: false }); }
  };

  const patchQuote = async (id: string, status: string) => {
    try {
      await platformApi.patchQuote(id, { status });
      setMsg({ text: `Quote marked ${status}.`, ok: true });
      const updated = await platformApi.listQuotes();
      setQuotes(updated.items);
    } catch { setMsg({ text: 'Failed.', ok: false }); }
  };

  const grouped = configs.reduce<Record<string, PlatformPricingConfig[]>>((acc, c) => {
    (acc[c.group] ??= []).push(c);
    return acc;
  }, {});

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Pricing Configuration</h1>
          <p className="text-xs text-slate-500 mt-0.5">
            Configure plan prices, module add-ons, and manage quote requests
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button type="button" onClick={loadAll} disabled={loading} title="Refresh" aria-label="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
          <button type="button" onClick={() => { setTab('quotes'); setShowNewQuote(true); }}
            className="flex items-center gap-1.5 bg-sapphire hover:bg-blue-500 text-white px-3 py-1.5 rounded-lg text-sm font-semibold transition-colors">
            <Plus className="h-3.5 w-3.5" />
            New Quote
          </button>
        </div>
      </div>

      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" onClick={() => setMsg(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}

      {/* Tabs */}
      <div className="flex gap-1 bg-white/[0.03] border border-white/[0.07] rounded-xl p-1">
        {([['modules', 'Module Pricing'], ['config', 'Scalar Config'], ['quotes', `Quotes (${quoteTotal})`]] as [Tab, string][]).map(([key, label]) => (
          <button
            key={key}
            type="button"
            onClick={() => setTab(key)}
            className={`flex-1 py-2 px-3 rounded-lg text-xs font-medium transition-colors ${tab === key ? 'bg-[#161b22] text-white border border-white/10' : 'text-slate-500 hover:text-white'}`}
          >
            {label}
          </button>
        ))}
      </div>

      {loading ? (
        <div className="flex items-center justify-center py-20">
          <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
        </div>
      ) : (
        <>
          {/* ── Module Pricing Tab ─────────────────────────────────────────── */}
          {tab === 'modules' && (
            <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
              <div className="grid grid-cols-[1fr_80px_80px_80px_80px_100px_100px] text-[10px] uppercase tracking-wider text-slate-500 px-4 py-2.5 border-b border-white/[0.07]">
                <span>Module</span>
                <span className="text-center">Trial</span>
                <span className="text-center">Starter</span>
                <span className="text-center">Growth</span>
                <span className="text-center">Enterprise</span>
                <span className="text-center">Add-On $/mo</span>
                <span className="text-center">Ent. Only</span>
              </div>
              {modules.map(m => (
                <div key={m.moduleKey} className="grid grid-cols-[1fr_80px_80px_80px_80px_100px_100px] px-4 py-3 border-b border-white/[0.05] last:border-0 items-center hover:bg-white/[0.02] transition-colors">
                  <div>
                    <p className="text-sm text-white">{m.moduleName}</p>
                    <p className="text-[10px] text-slate-500 font-mono">{m.moduleKey}</p>
                  </div>
                  {(
                    [
                      ['includedInTrial', m.includedInTrial],
                      ['includedInStarter', m.includedInStarter],
                      ['includedInGrowth', m.includedInGrowth],
                      ['includedInEnterprise', m.includedInEnterprise],
                    ] as [string, boolean][]
                  ).map(([field, val]) => (
                    <div key={field} className="flex justify-center">
                      <button
                        type="button"
                        onClick={() => saveModule(m.moduleKey, field, !val)}
                        className={`h-5 w-5 rounded flex items-center justify-center border transition-colors ${val ? 'bg-emerald-500/20 border-emerald-500/40 text-emerald-400' : 'border-white/10 text-transparent'}`}
                      >
                        <Check className="h-3 w-3" />
                      </button>
                    </div>
                  ))}
                  {/* Add-on price */}
                  <div className="flex justify-center">
                    {editing === `addon_${m.moduleKey}` ? (
                      <div className="flex items-center gap-1">
                        <input
                          type="number"
                          value={editVal}
                          onChange={e => setEditVal(parseFloat(e.target.value) || 0)}
                          className="w-16 bg-white/[0.05] border border-white/20 rounded px-1.5 py-0.5 text-xs text-white text-center focus:outline-none"
                          autoFocus
                        />
                        <button type="button" onClick={() => saveModule(m.moduleKey, 'addonPriceMonthly', editVal).then(() => setEditing(null))} className="text-emerald-400 hover:text-emerald-300">
                          <Check className="h-3.5 w-3.5" />
                        </button>
                        <button type="button" onClick={() => setEditing(null)} className="text-slate-500 hover:text-white">
                          <X className="h-3.5 w-3.5" />
                        </button>
                      </div>
                    ) : (
                      <button
                        type="button"
                        onClick={() => { setEditing(`addon_${m.moduleKey}`); setEditVal(m.addonPriceMonthly); }}
                        className="text-xs text-slate-300 hover:text-white flex items-center gap-1 group"
                      >
                        {fmt(m.addonPriceMonthly)}
                        <Edit3 className="h-3 w-3 opacity-0 group-hover:opacity-100 text-slate-500" />
                      </button>
                    )}
                  </div>
                  {/* Enterprise only */}
                  <div className="flex justify-center">
                    <button
                      type="button"
                      onClick={() => saveModule(m.moduleKey, 'isEnterpriseOnly', !m.isEnterpriseOnly)}
                      className={`h-5 w-5 rounded flex items-center justify-center border transition-colors ${m.isEnterpriseOnly ? 'bg-amber-500/20 border-amber-500/40 text-amber-400' : 'border-white/10 text-transparent'}`}
                    >
                      <Check className="h-3 w-3" />
                    </button>
                  </div>
                </div>
              ))}
            </div>
          )}

          {/* ── Scalar Config Tab ──────────────────────────────────────────── */}
          {tab === 'config' && (
            <div className="space-y-4">
              {Object.entries(grouped).map(([group, items]) => (
                <div key={group} className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
                  <div className="px-4 py-2.5 border-b border-white/[0.07]">
                    <h3 className="text-xs font-semibold text-slate-400 uppercase tracking-wider">
                      {GROUP_LABELS[group] ?? group}
                    </h3>
                  </div>
                  {items.map(c => (
                    <div key={c.key} className="flex items-center px-4 py-3 border-b border-white/[0.05] last:border-0 hover:bg-white/[0.02]">
                      <div className="flex-1">
                        <p className="text-sm text-white">{c.label}</p>
                        <p className="text-[10px] text-slate-500 font-mono">{c.key} · {c.plan}</p>
                      </div>
                      {editing === c.key ? (
                        <div className="flex items-center gap-2">
                          <input
                            type="number"
                            value={editVal}
                            onChange={e => setEditVal(parseFloat(e.target.value) || 0)}
                            className="w-24 bg-white/[0.05] border border-white/20 rounded-lg px-2 py-1 text-sm text-white text-end focus:outline-none"
                            autoFocus
                          />
                          <button type="button" onClick={() => saveConfig(c.key)} disabled={saving} className="text-emerald-400 hover:text-emerald-300 disabled:opacity-40">
                            <Check className="h-4 w-4" />
                          </button>
                          <button type="button" onClick={() => setEditing(null)} className="text-slate-500 hover:text-white">
                            <X className="h-4 w-4" />
                          </button>
                        </div>
                      ) : (
                        <div className="flex items-center gap-2">
                          <span className="text-sm font-medium text-slate-200">
                            {group === 'discount' ? `${c.value}%` : fmtInt(c.value)}
                          </span>
                          <button
                            type="button"
                            onClick={() => { setEditing(c.key); setEditVal(c.value); }}
                            className="text-slate-600 hover:text-white transition-colors"
                          >
                            <Edit3 className="h-3.5 w-3.5" />
                          </button>
                        </div>
                      )}
                    </div>
                  ))}
                </div>
              ))}
            </div>
          )}

          {/* ── Quotes Tab ────────────────────────────────────────────────── */}
          {tab === 'quotes' && (
            <div className="space-y-3">
              {quotes.length === 0 ? (
                <div className="text-center py-16 text-slate-500 text-sm">No quote requests yet.</div>
              ) : (
                quotes.map(q => {
                  const isOpen = expandedQuote === q.id;
                  const modules = (() => {
                    try { return JSON.parse(q.selectedModulesJson) as string[]; } catch { return []; }
                  })();
                  return (
                    <div key={q.id} className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
                      <button
                        type="button"
                        onClick={() => setExpandedQuote(isOpen ? null : q.id)}
                        className="w-full flex items-center gap-3 px-4 py-3.5 hover:bg-white/[0.02] transition-colors text-start"
                      >
                        <div className="flex-1 min-w-0">
                          <div className="flex items-center gap-2 flex-wrap">
                            <span className="text-sm font-medium text-white">{q.companyName}</span>
                            <span className={`text-[10px] px-1.5 py-0.5 rounded font-medium ${QUOTE_STATUS_BADGE[q.status] ?? 'bg-slate-700 text-slate-400'}`}>
                              {q.status}
                            </span>
                            <span className="text-xs text-slate-500">{q.orgType}</span>
                          </div>
                          <div className="flex items-center gap-3 mt-0.5 text-xs text-slate-500">
                            <span>{q.contactEmail}</span>
                            <span>{q.numEmployees.toLocaleString()} employees</span>
                            <span className="text-slate-300 font-medium">${q.estimatedMonthlyAmount.toLocaleString()}/mo</span>
                          </div>
                        </div>
                        <div className="flex items-center gap-2">
                          <span className="text-xs text-slate-500">{new Date(q.createdAtUtc).toLocaleDateString()}</span>
                          {isOpen ? <ChevronUp className="h-4 w-4 text-slate-500" /> : <ChevronDown className="h-4 w-4 text-slate-500" />}
                        </div>
                      </button>

                      {isOpen && (
                        <div className="border-t border-white/[0.07] px-4 py-4 space-y-4">
                          <div className="grid grid-cols-2 sm:grid-cols-3 gap-3 text-xs">
                            {[
                              ['Contact', `${q.contactName}${q.phone ? ` · ${q.phone}` : ''}`],
                              ['Email', q.contactEmail],
                              ['Org Type', q.orgType],
                              ['Companies', q.numCompanies],
                              ['Branches', q.numBranches],
                              ['Employees', q.numEmployees.toLocaleString()],
                              ['Admin Users', q.numAdminUsers],
                              ['Countries', q.numCountries],
                              ['Arabic/RTL', q.needsArabic ? 'Yes' : 'No'],
                            ].map(([k, v]) => (
                              <div key={String(k)}>
                                <p className="text-slate-500 uppercase tracking-wider text-[10px]">{k}</p>
                                <p className="text-slate-200 mt-0.5">{String(v)}</p>
                              </div>
                            ))}
                          </div>

                          {modules.length > 0 && (
                            <div>
                              <p className="text-[10px] text-slate-500 uppercase tracking-wider mb-1">Selected Modules</p>
                              <div className="flex flex-wrap gap-1.5">
                                {modules.map(m => (
                                  <span key={m} className="text-[10px] px-2 py-0.5 rounded bg-white/[0.05] text-slate-300 border border-white/[0.07]">{m}</span>
                                ))}
                              </div>
                            </div>
                          )}

                          {q.notes && (
                            <div>
                              <p className="text-[10px] text-slate-500 uppercase tracking-wider mb-1">Notes</p>
                              <p className="text-xs text-slate-300">{q.notes}</p>
                            </div>
                          )}

                          <div className="bg-white/[0.03] border border-white/[0.07] rounded-lg p-3 flex items-center justify-between">
                            <div>
                              <p className="text-xs text-slate-500">Estimated</p>
                              <p className="text-lg font-bold text-white">${q.estimatedMonthlyAmount.toLocaleString()}<span className="text-slate-500 text-xs font-normal">/mo</span></p>
                              <p className="text-xs text-slate-500">${q.estimatedAnnualAmount.toLocaleString()} / year</p>
                            </div>
                            <div className="flex flex-col gap-2 items-end">
                              {q.status !== 'Converted' && (
                                <>
                                  <button
                                    type="button"
                                    onClick={() => patchQuote(q.id, 'Contacted')}
                                    className="text-xs px-3 py-1.5 rounded-lg border border-amber-500/30 text-amber-400 hover:bg-amber-500/10 transition-colors"
                                  >
                                    Mark Contacted
                                  </button>
                                  <button
                                    type="button"
                                    onClick={() => router.push(`/platform/quotes/${q.id}/convert`)}
                                    className="text-xs px-3 py-1.5 rounded-lg bg-emerald-600/20 border border-emerald-500/30 text-emerald-400 hover:bg-emerald-600/30 transition-colors flex items-center gap-1"
                                  >
                                    Convert to Tenant <ArrowRight className="h-3 w-3" />
                                  </button>
                                  <button
                                    type="button"
                                    onClick={() => patchQuote(q.id, 'Lost')}
                                    className="text-xs text-slate-600 hover:text-slate-400 transition-colors"
                                  >
                                    Mark Lost
                                  </button>
                                </>
                              )}
                              {q.status === 'Converted' && q.convertedToTenantId && (
                                <button
                                  type="button"
                                  onClick={() => router.push(`/platform/tenants/${q.convertedToTenantId}`)}
                                  className="text-xs px-3 py-1.5 rounded-lg bg-emerald-600/20 border border-emerald-500/30 text-emerald-400"
                                >
                                  View Tenant →
                                </button>
                              )}
                            </div>
                          </div>
                        </div>
                      )}
                    </div>
                  );
                })
              )}
            </div>
          )}
        </>
      )}

      {showNewQuote && (
        <NewQuoteModal
          onClose={() => setShowNewQuote(false)}
          onSaved={async () => {
            const q = await platformApi.listQuotes();
            setQuotes(q.items);
            setQuoteTotal(q.total);
            setTab('quotes');
          }}
        />
      )}
    </div>
  );
}
