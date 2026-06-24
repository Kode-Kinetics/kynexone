'use client';

import { useState, useEffect, useCallback } from 'react';
import { useParams, useRouter } from 'next/navigation';
import Link from 'next/link';
import {
  ArrowLeft, RefreshCw, Edit3, Check, X, AlertTriangle,
  Zap, Users, CreditCard, FileText, Shield, ExternalLink,
  UserCog, PlayCircle, StopCircle, Palette, Globe, Trash2,
} from 'lucide-react';
import {
  platformApi,
  type PlatformTenantDetail,
  type TenantAdminUser,
  type TenantBranding,
  type TenantLocalization,
} from '@/src/api/platform';

// Feature flag descriptions — UI-only supplemental text (keys match backend catalog)
const FLAG_DESCRIPTIONS: Record<string, string> = {
  payroll:              'Payroll processing and payslip generation',
  recruitment:          'Job postings, applications and hiring pipeline',
  performance:          'Goal setting, reviews and performance cycles',
  compliance:           'Labour law compliance and regulatory reporting',
  finance:              'Loans, advances and financial module access',
  shifts:               'Shift rosters and workforce scheduling',
  overtime:             'Overtime requests, approvals and calculation',
  ai_assistant:         'Enable natural-language workspace queries',
  resume_screening:     'Automated CV analysis (advisory)',
  payroll_ai_validation:'Variance detection for payroll',
  risk_scores:          'Churn and burnout risk indicators (advisory)',
  wps_export:           'GCC WPS payroll file generation',
  eosb_calc:            'End-of-service benefit computation',
  qiwa_integration:     'Saudi Arabia Qiwa platform sync',
  hijri_calendar:       'Show Hijri dates alongside Gregorian',
  mobile_app:           'Enable mobile API endpoints',
};

const PLAN_BADGE: Record<string, string> = {
  trial:      'bg-slate-700 text-slate-300 border-slate-600',
  starter:    'bg-blue-900/50 text-blue-300 border-blue-700/40',
  growth:     'bg-purple-900/50 text-purple-300 border-purple-700/40',
  enterprise: 'bg-amber-900/50 text-amber-300 border-amber-700/40',
};

const STATUS_CLS: Record<string, string> = {
  active:    'text-emerald-400',
  trial:     'text-cyan-400',
  pastdue:   'text-amber-400',
  suspended: 'text-rose-400',
  cancelled: 'text-slate-500',
};

type Tab = 'overview' | 'features' | 'users' | 'billing' | 'audit' | 'security' | 'branding' | 'localization';
const TABS: { id: Tab; label: string; icon: React.ElementType }[] = [
  { id: 'overview',     label: 'Overview',     icon: Edit3 },
  { id: 'features',     label: 'Features',     icon: Zap },
  { id: 'users',        label: 'Users',        icon: Users },
  { id: 'billing',      label: 'Billing',      icon: CreditCard },
  { id: 'branding',     label: 'Branding',     icon: Palette },
  { id: 'localization', label: 'Localization', icon: Globe },
  { id: 'audit',        label: 'Audit',        icon: FileText },
  { id: 'security',     label: 'Security',     icon: Shield },
];

// ── Overview Tab ──────────────────────────────────────────────────────────────

function OverviewTab({ tenant, onRefresh, onDelete }: { tenant: PlatformTenantDetail; onRefresh: () => void; onDelete: () => void }) {
  const [editingName, setEditingName]   = useState(false);
  const [editingSub,  setEditingSub]    = useState(false);
  const [name, setName]                 = useState(tenant.name);
  const [saving, setSaving]             = useState(false);
  const [suspendOpen, setSuspendOpen]   = useState(false);
  const [suspendReason, setSuspendReason] = useState('');
  const [msg, setMsg]                   = useState<{ text: string; ok: boolean } | null>(null);

  const sub = tenant.subscription;
  const status = (sub?.status ?? '').toLowerCase();
  const plan   = (sub?.plan   ?? 'trial').toLowerCase();

  const [subForm, setSubForm] = useState({
    plan:          sub?.plan          ?? 'Trial',
    status:        sub?.status        ?? 'Trial',
    billingEmail:  sub?.billingEmail  ?? '',
    billingCycle:  sub?.billingCycle  ?? 'Monthly',
    currencyCode:  sub?.currencyCode  ?? 'AED',
    monthlyAmount: sub?.monthlyAmount ?? 0,
    maxUsers:      sub?.maxUsers      ?? 10,
    maxEmployees:  sub?.maxEmployees  ?? 50,
    maxCompanies:  sub?.maxCompanies  ?? 1,
    maxAdminUsers: sub?.maxAdminUsers ?? 3,
    startedAtUtc:  sub?.startedAtUtc  ? sub.startedAtUtc.slice(0, 10) : new Date().toISOString().slice(0, 10),
    expiresAtUtc:  sub?.expiresAtUtc  ? sub.expiresAtUtc.slice(0, 10) : '',
  });

  function openSubEdit() { setSubForm({
    plan:          sub?.plan          ?? 'Trial',
    status:        sub?.status        ?? 'Trial',
    billingEmail:  sub?.billingEmail  ?? '',
    billingCycle:  sub?.billingCycle  ?? 'Monthly',
    currencyCode:  sub?.currencyCode  ?? 'AED',
    monthlyAmount: sub?.monthlyAmount ?? 0,
    maxUsers:      sub?.maxUsers      ?? 10,
    maxEmployees:  sub?.maxEmployees  ?? 50,
    maxCompanies:  sub?.maxCompanies  ?? 1,
    maxAdminUsers: sub?.maxAdminUsers ?? 3,
    startedAtUtc:  sub?.startedAtUtc  ? sub.startedAtUtc.slice(0, 10) : new Date().toISOString().slice(0, 10),
    expiresAtUtc:  sub?.expiresAtUtc  ? sub.expiresAtUtc.slice(0, 10) : '',
  }); setEditingSub(true); }

  async function saveName() {
    setSaving(true);
    try {
      await platformApi.updateTenant(tenant.id, name);
      setMsg({ text: 'Name updated.', ok: true });
      setEditingName(false);
      onRefresh();
    } catch { setMsg({ text: 'Save failed.', ok: false }); }
    finally { setSaving(false); }
  }

  async function saveSubscription() {
    setSaving(true);
    try {
      await platformApi.updateSubscription(tenant.id, {
        plan:          subForm.plan,
        status:        subForm.status,
        billingEmail:  subForm.billingEmail,
        billingCycle:  subForm.billingCycle,
        currencyCode:  subForm.currencyCode,
        monthlyAmount: Number(subForm.monthlyAmount),
        maxUsers:      Number(subForm.maxUsers),
        maxEmployees:  Number(subForm.maxEmployees),
        maxCompanies:  Number(subForm.maxCompanies),
        maxAdminUsers: Number(subForm.maxAdminUsers),
        startedAtUtc:  subForm.startedAtUtc ? new Date(subForm.startedAtUtc).toISOString() : new Date().toISOString(),
        expiresAtUtc:  subForm.expiresAtUtc  ? new Date(subForm.expiresAtUtc).toISOString()  : null,
      });
      setMsg({ text: 'Subscription updated.', ok: true });
      setEditingSub(false);
      onRefresh();
    } catch (e: unknown) {
      const m = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setMsg({ text: m ?? 'Save failed.', ok: false });
    }
    finally { setSaving(false); }
  }

  async function doSuspend() {
    try {
      await platformApi.suspendTenant(tenant.id, suspendReason);
      setMsg({ text: 'Tenant suspended.', ok: true });
      setSuspendOpen(false);
      onRefresh();
    } catch { setMsg({ text: 'Failed.', ok: false }); }
  }

  async function doReactivate() {
    try {
      await platformApi.reactivateTenant(tenant.id, 'Reactivated via platform admin');
      setMsg({ text: 'Tenant reactivated.', ok: true });
      onRefresh();
    } catch { setMsg({ text: 'Failed.', ok: false }); }
  }

  const inputCls = 'w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-1.5 text-sm text-white focus:outline-none focus:border-sapphire/60';
  const selectCls = inputCls;

  return (
    <div className="space-y-5">
      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" title="Dismiss" onClick={() => setMsg(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}

      {/* Tenant identity card */}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl divide-y divide-white/[0.04]">
        <div className="px-5 py-3.5 flex items-center justify-between gap-4">
          <span className="text-xs text-slate-500 w-32 shrink-0">Name</span>
          {editingName ? (
            <div className="flex items-center gap-2 flex-1">
              <input value={name} onChange={e => setName(e.target.value)} autoFocus title="Tenant name" placeholder="Tenant name"
                className="flex-1 bg-white/[0.05] border border-white/[0.12] rounded-lg px-3 py-1.5 text-sm text-white focus:outline-none focus:border-sapphire/60" />
              <button type="button" title="Save name" onClick={saveName} disabled={saving} className="text-emerald-400 hover:text-emerald-300 transition-colors disabled:opacity-40">
                <Check className="h-4 w-4" />
              </button>
              <button type="button" title="Cancel" onClick={() => { setEditingName(false); setName(tenant.name); }} className="text-slate-500 hover:text-white transition-colors">
                <X className="h-4 w-4" />
              </button>
            </div>
          ) : (
            <div className="flex items-center gap-2 flex-1">
              <span className="text-sm text-white flex-1">{tenant.name}</span>
              <button type="button" title="Edit name" onClick={() => setEditingName(true)} className="text-slate-600 hover:text-white transition-colors">
                <Edit3 className="h-3.5 w-3.5" />
              </button>
            </div>
          )}
        </div>
        <div className="px-5 py-3.5 flex items-center gap-4">
          <span className="text-xs text-slate-500 w-32 shrink-0">Slug</span>
          <span className="text-sm text-slate-300 font-mono">/{tenant.slug}</span>
        </div>
        <div className="px-5 py-3.5 flex items-center gap-4">
          <span className="text-xs text-slate-500 w-32 shrink-0">Users / Employees</span>
          <span className="text-sm text-slate-300">{tenant.userCount} users · {tenant.employeeCount} employees</span>
        </div>
      </div>

      {/* Subscription card */}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        <div className="px-5 py-3 border-b border-white/[0.06] flex items-center justify-between">
          <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Subscription</p>
          {!editingSub && (
            <button type="button" onClick={openSubEdit}
              className="flex items-center gap-1 text-xs text-slate-500 hover:text-white transition-colors">
              <Edit3 className="h-3 w-3" /> Edit
            </button>
          )}
        </div>

        {editingSub ? (
          <div className="px-5 py-4 space-y-4">
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Plan</label>
                <select aria-label="Plan" value={subForm.plan} onChange={e => setSubForm(f => ({ ...f, plan: e.target.value }))} className={selectCls}>
                  {['Trial','Starter','Growth','Enterprise'].map(p => <option key={p}>{p}</option>)}
                </select>
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Status</label>
                <select aria-label="Status" value={subForm.status} onChange={e => setSubForm(f => ({ ...f, status: e.target.value }))} className={selectCls}>
                  {['Trial','Active','PastDue','Suspended','Cancelled'].map(s => <option key={s}>{s}</option>)}
                </select>
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Billing Email</label>
                <input type="email" title="Billing email" value={subForm.billingEmail} onChange={e => setSubForm(f => ({ ...f, billingEmail: e.target.value }))} className={inputCls} />
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Billing Cycle</label>
                <select aria-label="Billing cycle" value={subForm.billingCycle} onChange={e => setSubForm(f => ({ ...f, billingCycle: e.target.value }))} className={selectCls}>
                  {['Monthly','Quarterly','Annually'].map(c => <option key={c}>{c}</option>)}
                </select>
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Monthly Amount</label>
                <input type="number" title="Monthly amount" min={0} value={subForm.monthlyAmount} onChange={e => setSubForm(f => ({ ...f, monthlyAmount: Number(e.target.value) }))} className={inputCls} />
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Currency</label>
                <select aria-label="Currency" value={subForm.currencyCode} onChange={e => setSubForm(f => ({ ...f, currencyCode: e.target.value }))} className={selectCls}>
                  {['AED','USD','EUR','GBP','SAR','QAR','KWD','BHD','OMR'].map(c => <option key={c}>{c}</option>)}
                </select>
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Max Users</label>
                <input type="number" title="Max users" min={1} value={subForm.maxUsers} onChange={e => setSubForm(f => ({ ...f, maxUsers: Number(e.target.value) }))} className={inputCls} />
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Max Employees</label>
                <input type="number" title="Max employees" min={1} value={subForm.maxEmployees} onChange={e => setSubForm(f => ({ ...f, maxEmployees: Number(e.target.value) }))} className={inputCls} />
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Max Companies</label>
                <input type="number" title="Max companies" min={1} value={subForm.maxCompanies} onChange={e => setSubForm(f => ({ ...f, maxCompanies: Number(e.target.value) }))} className={inputCls} />
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Max Admin Users</label>
                <input type="number" title="Max admin users" min={1} value={subForm.maxAdminUsers} onChange={e => setSubForm(f => ({ ...f, maxAdminUsers: Number(e.target.value) }))} className={inputCls} />
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Started</label>
                <input type="date" title="Started date" value={subForm.startedAtUtc} onChange={e => setSubForm(f => ({ ...f, startedAtUtc: e.target.value }))} className={inputCls} />
              </div>
              <div>
                <label className="block text-[11px] text-slate-500 mb-1">Expires (optional)</label>
                <input type="date" title="Expires date" value={subForm.expiresAtUtc} onChange={e => setSubForm(f => ({ ...f, expiresAtUtc: e.target.value }))} className={inputCls} />
              </div>
            </div>
            <div className="flex gap-3 pt-1">
              <button type="button" onClick={() => setEditingSub(false)}
                className="flex-1 border border-white/10 text-slate-400 rounded-lg py-2 text-sm transition-colors">Cancel</button>
              <button type="button" onClick={saveSubscription} disabled={saving}
                className="flex-1 bg-sapphire hover:bg-blue-500 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
                {saving ? 'Saving…' : 'Save Subscription'}
              </button>
            </div>
          </div>
        ) : (
          <div className="divide-y divide-white/[0.04]">
            <div className="px-5 py-3 flex items-center gap-4">
              <span className="text-xs text-slate-500 w-32 shrink-0">Plan</span>
              <span className={`text-[10px] font-semibold uppercase tracking-wider px-1.5 py-0.5 rounded border ${PLAN_BADGE[plan] ?? PLAN_BADGE.trial}`}>
                {sub?.plan ?? 'Trial'}
              </span>
            </div>
            <div className="px-5 py-3 flex items-center gap-4">
              <span className="text-xs text-slate-500 w-32 shrink-0">Status</span>
              <span className={`text-sm font-medium capitalize ${STATUS_CLS[status] ?? 'text-slate-400'}`}>
                {sub?.status ?? 'Unknown'}
              </span>
            </div>
            <div className="px-5 py-3 flex items-center gap-4">
              <span className="text-xs text-slate-500 w-32 shrink-0">Monthly</span>
              <span className="text-sm text-slate-300">
                {sub?.monthlyAmount
                  ? `${sub.currencyCode} ${sub.monthlyAmount.toLocaleString()}`
                  : <span className="text-slate-600">—</span>}
              </span>
            </div>
            <div className="px-5 py-3 flex items-center gap-4">
              <span className="text-xs text-slate-500 w-32 shrink-0">Billing Email</span>
              <span className="text-sm text-slate-300 font-mono">{sub?.billingEmail || <span className="text-slate-600">—</span>}</span>
            </div>
            <div className="px-5 py-3 flex items-center gap-4">
              <span className="text-xs text-slate-500 w-32 shrink-0">Limits</span>
              <span className="text-sm text-slate-300">{sub?.maxUsers ?? '—'} users · {sub?.maxEmployees ?? '—'} employees</span>
            </div>
            {sub?.expiresAtUtc && (
              <div className="px-5 py-3 flex items-center gap-4">
                <span className="text-xs text-slate-500 w-32 shrink-0">Expires</span>
                <span className="text-sm text-slate-300">
                  {new Date(sub.expiresAtUtc).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}
                </span>
              </div>
            )}
          </div>
        )}
      </div>

      {/* Actions */}
      <div className="flex flex-wrap gap-3">
        {status === 'suspended' ? (
          <button type="button" onClick={doReactivate}
            className="flex items-center gap-1.5 text-sm text-emerald-400 border border-emerald-500/30 hover:border-emerald-500/60 px-4 py-2 rounded-lg transition-colors">
            <PlayCircle className="h-3.5 w-3.5" />
            Reactivate Tenant
          </button>
        ) : (
          <button type="button" onClick={() => setSuspendOpen(true)}
            className="flex items-center gap-1.5 text-sm text-rose-400 border border-rose-500/30 hover:border-rose-500/60 px-4 py-2 rounded-lg transition-colors">
            <StopCircle className="h-3.5 w-3.5" />
            Suspend Tenant
          </button>
        )}
        <a href={typeof window !== 'undefined' ? `${window.location.origin}/login?tenant=${tenant.slug}` : ''}
          target="_blank" rel="noopener noreferrer"
          className="flex items-center gap-1.5 text-sm text-slate-400 border border-white/10 hover:border-white/20 px-4 py-2 rounded-lg transition-colors">
          <ExternalLink className="h-3.5 w-3.5" />
          Open Tenant App
        </a>
        <button type="button" onClick={onDelete}
          className="flex items-center gap-1.5 text-sm text-rose-500 border border-rose-900/40 hover:border-rose-500/40 px-4 py-2 rounded-lg transition-colors ml-auto">
          <Trash2 className="h-3.5 w-3.5" />
          Delete Tenant
        </button>
      </div>

      {/* Suspend modal */}
      {suspendOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
          <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={() => setSuspendOpen(false)} />
          <div className="relative w-full max-w-sm bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl p-6 space-y-4">
            <h3 className="text-sm font-semibold text-white">Suspend {tenant.name}?</h3>
            <p className="text-sm text-slate-400">This will immediately disable all tenant users. Provide a reason.</p>
            <textarea value={suspendReason} onChange={e => setSuspendReason(e.target.value)} rows={2}
              placeholder="Reason (required)"
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white placeholder-slate-600 focus:outline-none focus:border-sapphire/60 resize-none" />
            <div className="flex gap-3">
              <button type="button" onClick={() => setSuspendOpen(false)}
                className="flex-1 border border-white/10 text-slate-400 rounded-lg py-2 text-sm transition-colors">Cancel</button>
              <button type="button" onClick={doSuspend} disabled={!suspendReason.trim()}
                className="flex-1 bg-rose-600 hover:bg-rose-500 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
                Suspend
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// ── Features Tab ──────────────────────────────────────────────────────────────

type FeatureFlagDef = { key: string; label: string; category: string };

function FeaturesTab({ tenant, onRefresh, featureFlags }: {
  tenant: PlatformTenantDetail;
  onRefresh: () => void;
  featureFlags: FeatureFlagDef[];
}) {
  const [toggling, setToggling] = useState<string | null>(null);
  const [msg, setMsg] = useState<{ text: string; ok: boolean } | null>(null);
  const flagMap = Object.fromEntries(tenant.featureFlags.map(f => [f.featureKey, f.isEnabled]));

  async function toggle(key: string, current: boolean) {
    setToggling(key);
    try {
      await platformApi.setFeature(tenant.id, key, !current);
      const flag = featureFlags.find(f => f.key === key);
      setMsg({ text: `${flag?.label ?? key} ${!current ? 'enabled' : 'disabled'}.`, ok: true });
      onRefresh();
    } catch { setMsg({ text: `Failed to update ${key}.`, ok: false }); }
    finally { setToggling(null); }
  }

  const categories = Array.from(new Set(featureFlags.map(f => f.category)));

  if (featureFlags.length === 0) {
    return (
      <div className="flex items-center justify-center py-12">
        <div className="h-4 w-4 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
      </div>
    );
  }

  return (
    <div className="space-y-5">
      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" title="Dismiss" onClick={() => setMsg(null)}><X className="h-3 w-3" /></button>
        </div>
      )}
      {categories.map(cat => (
        <div key={cat}>
          <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest mb-2">{cat}</p>
          <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden divide-y divide-white/[0.04]">
            {featureFlags.filter(f => f.category === cat).map(f => {
              const enabled = flagMap[f.key] ?? false;
              const description = FLAG_DESCRIPTIONS[f.key];
              return (
                <div key={f.key} className="flex items-center gap-4 px-5 py-3.5">
                  <div className="flex-1 min-w-0">
                    <p className="text-sm text-white">{f.label}</p>
                    {description && <p className="text-[11px] text-slate-500">{description}</p>}
                  </div>
                  <button
                    type="button"
                    title={`${enabled ? 'Disable' : 'Enable'} ${f.label}`}
                    onClick={() => toggle(f.key, enabled)}
                    disabled={toggling === f.key}
                    role="switch"
                    aria-checked={enabled ? 'true' : 'false'}
                    className={`relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors focus:outline-none disabled:opacity-40
                      ${enabled ? 'bg-sapphire' : 'bg-slate-700'}`}
                  >
                    <span className={`inline-block h-3.5 w-3.5 transform rounded-full bg-white transition-transform
                      ${enabled ? 'translate-x-4' : 'translate-x-1'}`} />
                  </button>
                </div>
              );
            })}
          </div>
        </div>
      ))}
    </div>
  );
}

// ── Users Tab ─────────────────────────────────────────────────────────────────

function UsersTab({ tenantId }: { tenantId: string }) {
  const [users, setUsers] = useState<import('@/src/api/platform').TenantUser[]>([]);
  const [loading, setLoading] = useState(true);
  const [impersonating, setImpersonating] = useState<string | null>(null);
  const [msg, setMsg] = useState<{ text: string; ok: boolean } | null>(null);

  useEffect(() => { load(); }, [tenantId]); // eslint-disable-line react-hooks/exhaustive-deps

  async function load() {
    setLoading(true);
    try { setUsers(await platformApi.listTenantUsers(tenantId)); }
    finally { setLoading(false); }
  }

  async function impersonate(userId: string, email: string) {
    setImpersonating(userId);
    try {
      const { token } = await platformApi.impersonate(tenantId, userId);
      window.open(`${window.location.origin}/login?impersonate=${token}`, '_blank');
      setMsg({ text: `Impersonating ${email} — new tab opened.`, ok: true });
    } catch {
      setMsg({ text: 'Impersonation failed.', ok: false });
    } finally { setImpersonating(null); }
  }

  return (
    <div className="space-y-4">
      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" title="Dismiss" onClick={() => setMsg(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        <div className="px-5 py-3 border-b border-white/[0.06] flex items-center justify-between">
          <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">All Users ({users.length})</p>
          <Link href={`/platform/tenants/${tenantId}/users`} className="text-[11px] text-sapphire hover:text-blue-300 transition-colors">
            Manage users →
          </Link>
        </div>
        {loading ? (
          <div className="flex items-center justify-center py-10">
            <div className="h-4 w-4 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
          </div>
        ) : users.length === 0 ? (
          <p className="text-sm text-slate-600 text-center py-10">No users found.</p>
        ) : (
          users.map(u => (
            <div key={u.id} className="flex items-center gap-3 px-5 py-2.5 border-b border-white/[0.04] last:border-0">
              <div className="h-7 w-7 rounded-full bg-slate-800 border border-white/10 flex items-center justify-center shrink-0">
                <span className="text-[10px] font-bold text-slate-400">{u.fullName?.slice(0, 2).toUpperCase() ?? '??'}</span>
              </div>
              <div className="flex-1 min-w-0">
                <p className="text-sm text-white font-medium">{u.fullName}</p>
                <p className="text-[11px] text-slate-500">{u.email}</p>
              </div>
              <span className="text-[10px] text-slate-500 bg-white/5 border border-white/10 px-1.5 py-0.5 rounded">{u.roles?.[0] ?? '—'}</span>
              <span className={`text-[10px] px-1.5 py-0.5 rounded border ${u.isActive ? 'text-emerald-400 border-emerald-500/20 bg-emerald-500/5' : 'text-slate-500 border-white/10'}`}>
                {u.isActive ? 'Active' : 'Inactive'}
              </span>
              <button type="button"
                onClick={() => impersonate(u.id, u.email)}
                disabled={impersonating === u.id || !u.isActive}
                title="Impersonate"
                className="flex items-center gap-1 text-[11px] text-blue-400 border border-blue-500/20 hover:border-blue-500/40 px-2 py-1 rounded transition-colors disabled:opacity-30">
                <UserCog className="h-3 w-3" />
                {impersonating === u.id ? '…' : 'Login as'}
              </button>
            </div>
          ))
        )}
      </div>
    </div>
  );
}

// ── Billing Tab ───────────────────────────────────────────────────────────────

function BillingTab({ tenantId }: { tenantId: string }) {
  return (
    <div className="flex flex-col items-center justify-center py-14 gap-4">
      <div className="h-12 w-12 rounded-2xl bg-blue-500/10 border border-blue-500/20 flex items-center justify-center">
        <CreditCard className="h-5 w-5 text-blue-400" />
      </div>
      <div className="text-center">
        <p className="text-sm font-semibold text-white mb-1">Billing & Invoices</p>
        <p className="text-xs text-slate-500">Create invoices, track payments, download PDFs and send to the tenant.</p>
      </div>
      <Link
        href={`/platform/tenants/${tenantId}/billing`}
        className="inline-flex items-center gap-2 bg-blue-600 hover:bg-blue-500 text-white px-5 py-2.5 rounded-xl text-sm font-semibold transition-colors"
      >
        <CreditCard className="h-3.5 w-3.5" />
        Open Billing Manager
      </Link>
    </div>
  );
}

// ── Audit Tab ─────────────────────────────────────────────────────────────────

function AuditTab({ tenantId }: { tenantId: string }) {
  return (
    <div className="text-center py-10 text-sm text-slate-500">
      Tenant-scoped audit log →{' '}
      <Link href={`/platform/tenants/${tenantId}/audit`} className="text-sapphire hover:text-blue-300">Open Audit Page</Link>
    </div>
  );
}

// ── Security Tab ──────────────────────────────────────────────────────────────

function SecurityTabContent({ tenantId }: { tenantId: string }) {
  return (
    <div className="text-center py-10 text-sm text-slate-500">
      Tenant security details →{' '}
      <Link href={`/platform/tenants/${tenantId}/security`} className="text-sapphire hover:text-blue-300">Open Security Page</Link>
    </div>
  );
}

// ── Branding Tab ──────────────────────────────────────────────────────────────

function BrandingTab({ tenant, onRefresh }: { tenant: PlatformTenantDetail; onRefresh: () => void }) {
  const initial: Partial<TenantBranding> = {
    logoUrl:       tenant.branding?.logoUrl       ?? '',
    faviconUrl:    tenant.branding?.faviconUrl     ?? '',
    primaryColor:  tenant.branding?.primaryColor   ?? '#2563EB',
    accentColor:   tenant.branding?.accentColor    ?? '#7C3AED',
    portalTitle:   tenant.branding?.portalTitle    ?? '',
    companyNameEn: tenant.branding?.companyNameEn  ?? '',
    companyNameAr: tenant.branding?.companyNameAr  ?? '',
  };
  const [form, setForm] = useState(initial);
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState<{ text: string; ok: boolean } | null>(null);

  function f(k: keyof typeof form, v: string) { setForm(p => ({ ...p, [k]: v })); }

  async function save() {
    setSaving(true);
    try {
      await platformApi.updateBranding(tenant.id, form);
      setMsg({ text: 'Branding saved.', ok: true });
      onRefresh();
    } catch { setMsg({ text: 'Save failed.', ok: false }); }
    finally { setSaving(false); }
  }

  return (
    <div className="space-y-5">
      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" title="Dismiss" onClick={() => setMsg(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl divide-y divide-white/[0.04]">
        {([
          { label: 'Portal Title',     key: 'portalTitle',   placeholder: 'My Company HR Portal' },
          { label: 'Company (English)', key: 'companyNameEn', placeholder: 'Acme Corp' },
          { label: 'Company (Arabic)',  key: 'companyNameAr', placeholder: 'أكمي كورب' },
          { label: 'Logo URL',          key: 'logoUrl',       placeholder: 'https://cdn.example.com/logo.png' },
          { label: 'Favicon URL',       key: 'faviconUrl',    placeholder: 'https://cdn.example.com/favicon.ico' },
        ] as { label: string; key: keyof typeof form; placeholder: string }[]).map(row => (
          <div key={row.key} className="flex items-center gap-4 px-5 py-3">
            <span className="text-xs text-slate-500 w-36 shrink-0">{row.label}</span>
            <input
              title={row.label}
              value={form[row.key] as string ?? ''}
              onChange={e => f(row.key, e.target.value)}
              placeholder={row.placeholder}
              className="flex-1 bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-1.5 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-700"
            />
          </div>
        ))}
        <div className="flex items-center gap-4 px-5 py-3">
          <span className="text-xs text-slate-500 w-36 shrink-0">Primary Color</span>
          <div className="flex items-center gap-2">
            <input type="color" title="Primary color" value={form.primaryColor ?? '#2563EB'} onChange={e => f('primaryColor', e.target.value)}
              className="h-8 w-8 rounded cursor-pointer bg-transparent border-0" />
            <span className="text-sm text-slate-400 font-mono">{form.primaryColor}</span>
          </div>
        </div>
        <div className="flex items-center gap-4 px-5 py-3">
          <span className="text-xs text-slate-500 w-36 shrink-0">Accent Color</span>
          <div className="flex items-center gap-2">
            <input type="color" title="Accent color" value={form.accentColor ?? '#7C3AED'} onChange={e => f('accentColor', e.target.value)}
              className="h-8 w-8 rounded cursor-pointer bg-transparent border-0" />
            <span className="text-sm text-slate-400 font-mono">{form.accentColor}</span>
          </div>
        </div>
      </div>
      {(form.logoUrl || form.portalTitle) && (
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl p-4">
          <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-wider mb-3">Preview</p>
          <div className="flex items-center gap-3">
            {form.logoUrl && <img src={form.logoUrl} alt="Logo preview" className="h-8 object-contain" onError={e => { (e.target as HTMLImageElement).style.display = 'none'; }} />}
            {form.portalTitle && <span className="text-sm text-white">{form.portalTitle}</span>}
          </div>
        </div>
      )}
      <button type="button" onClick={save} disabled={saving}
        className="flex items-center gap-2 bg-sapphire/90 hover:bg-sapphire text-white text-sm font-medium px-5 py-2 rounded-lg transition-colors disabled:opacity-50">
        {saving ? <RefreshCw className="h-3.5 w-3.5 animate-spin" /> : <Check className="h-3.5 w-3.5" />}
        Save Branding
      </button>
    </div>
  );
}

// ── Localization Tab ───────────────────────────────────────────────────────────

function LocalizationTab({ tenant, onRefresh }: { tenant: PlatformTenantDetail; onRefresh: () => void }) {
  const initial: Partial<TenantLocalization> = {
    defaultLanguage:  tenant.localization?.defaultLanguage  ?? 'en',
    defaultTimezone:  tenant.localization?.defaultTimezone  ?? 'Asia/Riyadh',
    dateFormat:       tenant.localization?.dateFormat       ?? 'DD/MM/YYYY',
    currencyCode:     tenant.localization?.currencyCode     ?? 'SAR',
    countryCode:      tenant.localization?.countryCode      ?? 'SA',
    calendarSystem:   tenant.localization?.calendarSystem   ?? 'Gregorian',
    workWeek:         tenant.localization?.workWeek         ?? 'Sun-Thu',
    weekStartDay:     tenant.localization?.weekStartDay     ?? 'Sunday',
    rtlEnabled:       tenant.localization?.rtlEnabled       ?? false,
    hijriDatesEnabled: tenant.localization?.hijriDatesEnabled ?? false,
  };
  const [form, setForm] = useState(initial);
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState<{ text: string; ok: boolean } | null>(null);

  function sf(k: keyof typeof form, v: string | boolean) { setForm(p => ({ ...p, [k]: v })); }

  async function save() {
    setSaving(true);
    try {
      await platformApi.updateLocalization(tenant.id, form);
      setMsg({ text: 'Localization saved.', ok: true });
      onRefresh();
    } catch { setMsg({ text: 'Save failed.', ok: false }); }
    finally { setSaving(false); }
  }

  const selectCls = 'w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60';
  const inputCls  = 'w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60';

  return (
    <div className="space-y-5">
      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" title="Dismiss" onClick={() => setMsg(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div>
          <label className="block text-[10px] font-semibold text-slate-500 uppercase tracking-wider mb-1">Default Language</label>
          <select title="Default language" value={form.defaultLanguage} onChange={e => sf('defaultLanguage', e.target.value)} className={selectCls}>
            <option value="en">English</option>
            <option value="ar">Arabic</option>
          </select>
        </div>
        <div>
          <label className="block text-[10px] font-semibold text-slate-500 uppercase tracking-wider mb-1">Timezone</label>
          <input title="Default timezone" value={form.defaultTimezone ?? ''} onChange={e => sf('defaultTimezone', e.target.value)} placeholder="Asia/Riyadh" className={inputCls} />
        </div>
        <div>
          <label className="block text-[10px] font-semibold text-slate-500 uppercase tracking-wider mb-1">Date Format</label>
          <select title="Date format" value={form.dateFormat} onChange={e => sf('dateFormat', e.target.value)} className={selectCls}>
            <option value="DD/MM/YYYY">DD/MM/YYYY</option>
            <option value="MM/DD/YYYY">MM/DD/YYYY</option>
            <option value="YYYY-MM-DD">YYYY-MM-DD</option>
          </select>
        </div>
        <div>
          <label className="block text-[10px] font-semibold text-slate-500 uppercase tracking-wider mb-1">Currency</label>
          <input title="Currency code" value={form.currencyCode ?? ''} onChange={e => sf('currencyCode', e.target.value)} placeholder="SAR" className={inputCls} />
        </div>
        <div>
          <label className="block text-[10px] font-semibold text-slate-500 uppercase tracking-wider mb-1">Country</label>
          <input title="Country code" value={form.countryCode ?? ''} onChange={e => sf('countryCode', e.target.value)} placeholder="SA" className={inputCls} />
        </div>
        <div>
          <label className="block text-[10px] font-semibold text-slate-500 uppercase tracking-wider mb-1">Calendar System</label>
          <select title="Calendar system" value={form.calendarSystem} onChange={e => sf('calendarSystem', e.target.value)} className={selectCls}>
            <option value="Gregorian">Gregorian</option>
            <option value="Hijri">Hijri</option>
          </select>
        </div>
        <div>
          <label className="block text-[10px] font-semibold text-slate-500 uppercase tracking-wider mb-1">Work Week</label>
          <select title="Work week" value={form.workWeek} onChange={e => sf('workWeek', e.target.value)} className={selectCls}>
            <option value="Sun-Thu">Sun–Thu (GCC)</option>
            <option value="Mon-Fri">Mon–Fri</option>
            <option value="Mon-Sat">Mon–Sat</option>
          </select>
        </div>
        <div>
          <label className="block text-[10px] font-semibold text-slate-500 uppercase tracking-wider mb-1">Week Starts On</label>
          <select title="Week start day" value={form.weekStartDay} onChange={e => sf('weekStartDay', e.target.value)} className={selectCls}>
            <option value="Sunday">Sunday</option>
            <option value="Monday">Monday</option>
            <option value="Saturday">Saturday</option>
          </select>
        </div>
      </div>
      <div className="flex flex-wrap gap-4">
        {([
          { key: 'rtlEnabled', label: 'RTL Layout' },
          { key: 'hijriDatesEnabled', label: 'Show Hijri Dates' },
        ] as { key: 'rtlEnabled' | 'hijriDatesEnabled'; label: string }[]).map(tog => (
          <label key={tog.key} className="flex items-center gap-2 cursor-pointer">
            <input type="checkbox" checked={!!form[tog.key]} onChange={e => sf(tog.key, e.target.checked)}
              className="h-4 w-4 rounded accent-sapphire" />
            <span className="text-sm text-slate-300">{tog.label}</span>
          </label>
        ))}
      </div>
      <button type="button" onClick={save} disabled={saving}
        className="flex items-center gap-2 bg-sapphire/90 hover:bg-sapphire text-white text-sm font-medium px-5 py-2 rounded-lg transition-colors disabled:opacity-50">
        {saving ? <RefreshCw className="h-3.5 w-3.5 animate-spin" /> : <Check className="h-3.5 w-3.5" />}
        Save Localization
      </button>
    </div>
  );
}

// ── Main ──────────────────────────────────────────────────────────────────────

export default function TenantDetailPage() {
  const { id } = useParams<{ id: string }>();
  const router  = useRouter();
  const [tenant, setTenant] = useState<PlatformTenantDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [tab, setTab] = useState<Tab>('overview');
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteErr, setDeleteErr] = useState('');
  const [featureFlags, setFeatureFlags] = useState<FeatureFlagDef[]>([]);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
    platformApi.getFeatureFlags().then(setFeatureFlags).catch(() => {/* non-fatal: UI falls back to empty list */});
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  const load = useCallback(async () => {
    setLoading(true);
    try { setTenant(await platformApi.getTenant(id)); }
    catch { router.replace('/platform/tenants'); }
    finally { setLoading(false); }
  }, [id, router]);

  if (loading || !tenant) {
    return (
      <div className="flex items-center justify-center min-h-[60vh]">
        <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
      </div>
    );
  }

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex items-start gap-4">
        <Link href="/platform/tenants" className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors shrink-0 mt-0.5">
          <ArrowLeft className="h-4 w-4" />
        </Link>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <h1 className="text-lg font-bold text-white">{tenant.name}</h1>
            <span className="text-[10px] font-semibold uppercase tracking-wider px-1.5 py-0.5 rounded border bg-slate-700 text-slate-300 border-slate-600">
              {tenant.slug}
            </span>
          </div>
          <p className="text-xs text-slate-500 mt-0.5">
            {tenant.userCount} users · {tenant.employeeCount} employees
          </p>
        </div>
        <button type="button" title="Refresh" onClick={load} className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors shrink-0">
          <RefreshCw className="h-3.5 w-3.5" />
        </button>
      </div>

      {/* Tab bar */}
      <div className="flex gap-1 border-b border-white/[0.06] overflow-x-auto">
        {TABS.map(t => {
          const Icon = t.icon;
          return (
            <button type="button" key={t.id} onClick={() => setTab(t.id)}
              className={`flex items-center gap-1.5 px-4 py-2.5 text-sm font-medium border-b-2 whitespace-nowrap transition-colors
                ${tab === t.id
                  ? 'border-sapphire text-white'
                  : 'border-transparent text-slate-500 hover:text-slate-300'}`}>
              <Icon className="h-3.5 w-3.5" />
              {t.label}
            </button>
          );
        })}
      </div>

      {/* Tab content */}
      {tab === 'overview'     && <OverviewTab tenant={tenant} onRefresh={load} onDelete={() => setDeleteOpen(true)} />}
      {tab === 'features'     && <FeaturesTab tenant={tenant} onRefresh={load} featureFlags={featureFlags} />}
      {tab === 'users'        && <UsersTab tenantId={tenant.id} />}
      {tab === 'billing'      && <BillingTab tenantId={tenant.id} />}
      {tab === 'branding'     && <BrandingTab tenant={tenant} onRefresh={load} />}
      {tab === 'localization' && <LocalizationTab tenant={tenant} onRefresh={load} />}
      {tab === 'audit'        && <AuditTab tenantId={tenant.id} />}
      {tab === 'security'     && <SecurityTabContent tenantId={tenant.id} />}

      {/* Delete tenant modal — Owner only */}
      {deleteOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
          <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={() => setDeleteOpen(false)} />
          <div className="relative w-full max-w-sm bg-[#0d1117] border border-rose-900/40 rounded-2xl shadow-2xl p-6 space-y-4">
            <div className="flex items-center gap-3">
              <div className="h-9 w-9 rounded-xl bg-rose-500/10 border border-rose-500/20 flex items-center justify-center shrink-0">
                <Trash2 className="h-4 w-4 text-rose-400" />
              </div>
              <div>
                <h3 className="text-sm font-semibold text-white">Delete Tenant?</h3>
                <p className="text-xs text-slate-500">{tenant.name}</p>
              </div>
            </div>
            <p className="text-xs text-slate-400">This will deactivate the tenant, cancel the subscription, and revoke all active sessions. This action is irreversible.</p>
            {deleteErr && <p className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2">{deleteErr}</p>}
            <div className="flex gap-3">
              <button type="button" onClick={() => { setDeleteOpen(false); setDeleteErr(''); }}
                className="flex-1 border border-white/10 text-slate-400 rounded-lg py-2 text-sm transition-colors">Cancel</button>
              <button type="button" disabled={deleting} onClick={async () => {
                setDeleting(true); setDeleteErr('');
                try { await platformApi.deleteTenant(tenant.id); router.replace('/platform/tenants'); }
                catch (e: unknown) {
                  const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
                  setDeleteErr(msg ?? 'Delete failed — you may need Owner role to perform this action.');
                  setDeleting(false);
                }
              }}
                className="flex-1 bg-rose-600 hover:bg-rose-500 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
                {deleting ? 'Deleting…' : 'Delete Tenant'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
