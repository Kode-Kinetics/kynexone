'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import {
  Plus, Search, RefreshCw, Building2, X,
  MoreHorizontal, CheckCircle, AlertTriangle, XCircle, Clock, Eye, EyeOff,
  Power, PowerOff, Trash2, Zap, Globe,
} from 'lucide-react';
import {
  platformApi,
  type PlatformTenantSummary,
  type CreateTenantBody,
  type BulkOpResult,
} from '@/src/api/platform';

// ── Status helpers ────────────────────────────────────────────────────────────

const STATUS_DOT: Record<string, string> = {
  active:    'bg-emerald-500',
  trial:     'bg-cyan-400',
  pastdue:   'bg-amber-400',
  suspended: 'bg-rose-500',
  cancelled: 'bg-slate-600',
};

const STATUS_LABEL: Record<string, string> = {
  active:    'text-emerald-400',
  trial:     'text-cyan-400',
  pastdue:   'text-amber-400',
  suspended: 'text-rose-400',
  cancelled: 'text-slate-500',
};

const PLAN_BADGE: Record<string, string> = {
  trial:      'bg-slate-700 text-slate-300 border-slate-600',
  starter:    'bg-blue-900/50 text-blue-300 border-blue-700/40',
  growth:     'bg-purple-900/50 text-purple-300 border-purple-700/40',
  enterprise: 'bg-amber-900/50 text-amber-300 border-amber-700/40',
};

function statusKey(s: string) { return s?.toLowerCase() ?? 'unknown'; }

function daysUntil(dateStr: string | null) {
  if (!dateStr) return null;
  return Math.ceil((new Date(dateStr).getTime() - Date.now()) / 86400000);
}

function expiryLabel(days: number | null): { text: string; cls: string } {
  if (days === null) return { text: '—', cls: 'text-slate-700' };
  if (days < 0)      return { text: 'Expired', cls: 'text-rose-400' };
  if (days === 0)    return { text: 'Today', cls: 'text-rose-400' };
  if (days <= 7)     return { text: `${days}d`, cls: 'text-amber-400' };
  if (days <= 30)    return { text: `${days}d`, cls: 'text-yellow-500' };
  return { text: `${days}d`, cls: 'text-slate-600' };
}

function isAtRisk(t: PlatformTenantSummary) {
  const s = statusKey(t.subscription?.status ?? '');
  if (s === 'suspended' || s === 'cancelled' || s === 'pastdue') return 'rose';
  const d = daysUntil(t.subscription?.expiresAtUtc ?? null);
  if (d !== null && d <= 7) return 'amber';
  return null;
}

// ── New Tenant Modal ──────────────────────────────────────────────────────────

const PLANS = ['Trial', 'Starter', 'Growth', 'Enterprise'];

function NewTenantModal({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const [form, setForm] = useState<CreateTenantBody>({
    name: '', slug: '', adminEmail: '', adminFullName: '', adminPassword: '',
    plan: 'Starter', maxUsers: 20, maxEmployees: 50, maxCompanies: 1, maxAdminUsers: 10,
    billingEmail: '', billingCycle: 'Monthly', monthlyAmount: 0, currencyCode: 'USD',
    expiresAtUtc: null,
  });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');
  const [showPwd, setShowPwd] = useState(false);

  function autoSlug(name: string) {
    return name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
  }

  function change(k: keyof CreateTenantBody, v: unknown) {
    setForm(f => ({ ...f, [k]: v }));
    if (k === 'name') setForm(f => ({ ...f, name: v as string, slug: autoSlug(v as string) }));
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true); setErr('');
    try {
      await platformApi.createTenant(form);
      onCreated();
      onClose();
    } catch (ex: unknown) {
      const msg = (ex as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setErr(msg ?? 'Failed to create tenant.');
    } finally { setSaving(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-lg bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl overflow-hidden">
        <div className="flex items-center justify-between px-6 py-4 border-b border-white/[0.07]">
          <h2 className="text-sm font-semibold text-white">Provision New Tenant</h2>
          <button type="button" onClick={onClose} className="text-slate-500 hover:text-white transition-colors">
            <X className="h-4 w-4" />
          </button>
        </div>

        <form onSubmit={submit} className="px-6 py-5 space-y-4 max-h-[80vh] overflow-y-auto">
          {err && (
            <div className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2">{err}</div>
          )}

          {/* Tenant info */}
          <fieldset className="space-y-3">
            <legend className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest mb-2">Tenant Info</legend>
            <div>
              <label className="block text-xs text-slate-400 mb-1">Company Name *</label>
              <input required value={form.name} onChange={e => change('name', e.target.value)}
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600"
                placeholder="Acme Corp" />
            </div>
            <div>
              <label className="block text-xs text-slate-400 mb-1">Slug *</label>
              <input required value={form.slug} onChange={e => change('slug', e.target.value)}
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white font-mono focus:outline-none focus:border-sapphire/60 placeholder-slate-600"
                placeholder="acme-corp" />
            </div>
          </fieldset>

          {/* Admin user */}
          <fieldset className="space-y-3">
            <legend className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest mb-2">Admin User</legend>
            <div>
              <label className="block text-xs text-slate-400 mb-1">Admin Email *</label>
              <input required type="email" value={form.adminEmail} onChange={e => change('adminEmail', e.target.value)}
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600"
                placeholder="admin@acmecorp.com" />
            </div>
            <div>
              <label className="block text-xs text-slate-400 mb-1">Admin Full Name</label>
              <input value={form.adminFullName ?? ''} onChange={e => change('adminFullName', e.target.value)}
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600"
                placeholder="John Doe" />
            </div>
            <div>
              <label className="block text-xs text-slate-400 mb-1">Temp Password *</label>
              <div className="relative">
                <input required type={showPwd ? 'text' : 'password'} value={form.adminPassword} onChange={e => change('adminPassword', e.target.value)}
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 pr-9 text-sm text-white focus:outline-none focus:border-sapphire/60 font-mono"
                  placeholder="Min 8 characters" />
                <button type="button" onClick={() => setShowPwd(p => !p)} title={showPwd ? 'Hide password' : 'Show password'}
                  className="absolute right-2.5 top-1/2 -translate-y-1/2 text-slate-500 hover:text-white transition-colors">
                  {showPwd ? <EyeOff className="h-3.5 w-3.5" /> : <Eye className="h-3.5 w-3.5" />}
                </button>
              </div>
            </div>
          </fieldset>

          {/* Subscription */}
          <fieldset className="space-y-3">
            <legend className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest mb-2">Subscription</legend>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="block text-xs text-slate-400 mb-1">Plan</label>
                <select value={form.plan} onChange={e => change('plan', e.target.value)}
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60">
                  {PLANS.map(p => <option key={p} value={p}>{p}</option>)}
                </select>
              </div>
              <div>
                <label className="block text-xs text-slate-400 mb-1">Billing Cycle</label>
                <select value={form.billingCycle} onChange={e => change('billingCycle', e.target.value)}
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60">
                  <option>Monthly</option>
                  <option>Quarterly</option>
                  <option>Annual</option>
                </select>
              </div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="block text-xs text-slate-400 mb-1">Monthly Amount</label>
                <input type="number" min={0} value={form.monthlyAmount ?? 0} onChange={e => change('monthlyAmount', parseFloat(e.target.value) || 0)}
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
              </div>
              <div>
                <label className="block text-xs text-slate-400 mb-1">Currency</label>
                <input value={form.currencyCode ?? 'USD'} onChange={e => change('currencyCode', e.target.value)}
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white font-mono focus:outline-none focus:border-sapphire/60" />
              </div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="block text-xs text-slate-400 mb-1">Max Users</label>
                <input type="number" min={1} value={form.maxUsers ?? ''} onChange={e => change('maxUsers', parseInt(e.target.value) || null)}
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
              </div>
              <div>
                <label className="block text-xs text-slate-400 mb-1">Max Employees</label>
                <input type="number" min={1} value={form.maxEmployees ?? ''} onChange={e => change('maxEmployees', parseInt(e.target.value) || null)}
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
              </div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="block text-xs text-slate-400 mb-1">Max Companies</label>
                <input type="number" min={1} value={form.maxCompanies ?? ''} onChange={e => change('maxCompanies', parseInt(e.target.value) || null)}
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
              </div>
              <div>
                <label className="block text-xs text-slate-400 mb-1">Max Admin Users</label>
                <input type="number" min={1} value={form.maxAdminUsers ?? ''} onChange={e => change('maxAdminUsers', parseInt(e.target.value) || null)}
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
              </div>
            </div>
            <div>
              <label className="block text-xs text-slate-400 mb-1">Billing Email</label>
              <input type="email" value={form.billingEmail ?? ''} onChange={e => change('billingEmail', e.target.value)}
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600"
                placeholder="billing@acmecorp.com" />
            </div>
            <div>
              <label className="block text-xs text-slate-400 mb-1">Expiry Date</label>
              <input type="date" value={form.expiresAtUtc?.slice(0, 10) ?? ''} onChange={e => change('expiresAtUtc', e.target.value ? e.target.value + 'T00:00:00Z' : null)}
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
            </div>
          </fieldset>

          <div className="flex gap-3 pt-1">
            <button type="button" onClick={onClose}
              className="flex-1 border border-white/10 text-slate-400 hover:text-white rounded-lg py-2 text-sm transition-colors">
              Cancel
            </button>
            <button type="submit" disabled={saving}
              className="flex-1 bg-sapphire text-white rounded-lg py-2 text-sm font-semibold hover:bg-blue-500 transition-colors disabled:opacity-40">
              {saving ? 'Creating…' : 'Provision Tenant'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

// ── Confirm Modal ─────────────────────────────────────────────────────────────

function ConfirmModal({ title, message, onConfirm, onClose, danger = false, requireReason = false }: {
  title: string; message: string;
  onConfirm: (reason: string) => void;
  onClose: () => void;
  danger?: boolean;
  requireReason?: boolean;
}) {
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);

  async function confirm() {
    if (requireReason && !reason.trim()) return;
    setBusy(true);
    try { await onConfirm(reason); } finally { setBusy(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-sm bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl p-6 space-y-4">
        <h3 className="text-sm font-semibold text-white">{title}</h3>
        <p className="text-sm text-slate-400">{message}</p>
        {requireReason && (
          <textarea value={reason} onChange={e => setReason(e.target.value)} rows={2}
            placeholder="Reason (required)"
            className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white placeholder-slate-600 focus:outline-none focus:border-sapphire/60 resize-none" />
        )}
        <div className="flex gap-3">
          <button type="button" onClick={onClose}
            className="flex-1 border border-white/10 text-slate-400 hover:text-white rounded-lg py-2 text-sm transition-colors">
            Cancel
          </button>
          <button type="button" onClick={confirm} disabled={busy || (requireReason && !reason.trim())}
            className={`flex-1 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40 ${
              danger ? 'bg-rose-600 hover:bg-rose-500' : 'bg-sapphire hover:bg-blue-500'
            }`}>
            {busy ? 'Working…' : 'Confirm'}
          </button>
        </div>
      </div>
    </div>
  );
}

// ── Bulk Feature Modal ────────────────────────────────────────────────────────

function BulkFeatureModal({ selectedCount, totalCount, onClose, onApply }: {
  selectedCount: number;
  totalCount: number;
  onClose: () => void;
  onApply: (featureKey: string, isEnabled: boolean, applyToAll: boolean) => Promise<void>;
}) {
  const [features, setFeatures] = useState<{ key: string; label: string; category: string }[]>([]);
  const [featureKey, setFeatureKey] = useState('');
  const [isEnabled, setIsEnabled] = useState(true);
  const [applyToAll, setApplyToAll] = useState(selectedCount === 0);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    platformApi.getFeatureFlags()
      .then(fs => { setFeatures(fs); if (fs.length) setFeatureKey(fs[0].key); })
      .catch(() => setFeatures([]));
  }, []);

  const targetLabel = applyToAll
    ? `ALL ${totalCount} tenants`
    : `${selectedCount} selected tenant${selectedCount === 1 ? '' : 's'}`;

  async function apply() {
    if (!featureKey) return;
    setBusy(true);
    try { await onApply(featureKey, isEnabled, applyToAll); } finally { setBusy(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-md bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl p-6 space-y-4">
        <div className="flex items-center gap-2">
          <Zap className="h-4 w-4 text-sapphire" />
          <h3 className="text-sm font-semibold text-white">Apply Feature in Bulk</h3>
        </div>

        <div>
          <label className="block text-xs text-slate-400 mb-1">Feature</label>
          <select aria-label="Feature to apply" value={featureKey} onChange={e => setFeatureKey(e.target.value)}
            className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60">
            {features.length === 0 && <option value="">No features available</option>}
            {features.map(f => <option key={f.key} value={f.key}>{f.label} · {f.category}</option>)}
          </select>
        </div>

        <div>
          <label className="block text-xs text-slate-400 mb-1.5">Action</label>
          <div className="grid grid-cols-2 gap-2">
            <button type="button" onClick={() => setIsEnabled(true)}
              className={`flex items-center justify-center gap-1.5 rounded-lg py-2 text-sm font-medium border transition-colors ${isEnabled ? 'bg-emerald-500/15 border-emerald-500/40 text-emerald-300' : 'border-white/10 text-slate-400 hover:text-white'}`}>
              <Power className="h-3.5 w-3.5" /> Enable
            </button>
            <button type="button" onClick={() => setIsEnabled(false)}
              className={`flex items-center justify-center gap-1.5 rounded-lg py-2 text-sm font-medium border transition-colors ${!isEnabled ? 'bg-rose-500/15 border-rose-500/40 text-rose-300' : 'border-white/10 text-slate-400 hover:text-white'}`}>
              <PowerOff className="h-3.5 w-3.5" /> Disable
            </button>
          </div>
        </div>

        <div>
          <label className="block text-xs text-slate-400 mb-1.5">Target</label>
          <div className="space-y-1.5">
            <button type="button" disabled={selectedCount === 0} onClick={() => setApplyToAll(false)}
              className={`w-full flex items-center gap-2 rounded-lg px-3 py-2 text-sm border transition-colors text-left disabled:opacity-40 ${!applyToAll ? 'bg-sapphire/15 border-sapphire/40 text-white' : 'border-white/10 text-slate-400 hover:text-white'}`}>
              <CheckCircle className={`h-3.5 w-3.5 ${!applyToAll ? 'text-sapphire' : 'text-slate-600'}`} />
              Selected tenants ({selectedCount})
            </button>
            <button type="button" onClick={() => setApplyToAll(true)}
              className={`w-full flex items-center gap-2 rounded-lg px-3 py-2 text-sm border transition-colors text-left ${applyToAll ? 'bg-amber-500/15 border-amber-500/40 text-white' : 'border-white/10 text-slate-400 hover:text-white'}`}>
              <Globe className={`h-3.5 w-3.5 ${applyToAll ? 'text-amber-400' : 'text-slate-600'}`} />
              ALL tenants ({totalCount}) — platform-wide
            </button>
          </div>
        </div>

        <p className="text-xs text-slate-500">
          {isEnabled ? 'Enable' : 'Disable'} <span className="text-slate-300 font-medium">{featureKey || '—'}</span> for {targetLabel}.
        </p>

        <div className="flex gap-3 pt-1">
          <button type="button" onClick={onClose}
            className="flex-1 border border-white/10 text-slate-400 hover:text-white rounded-lg py-2 text-sm transition-colors">
            Cancel
          </button>
          <button type="button" onClick={apply} disabled={busy || !featureKey}
            className="flex-1 bg-sapphire hover:bg-blue-500 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
            {busy ? 'Applying…' : 'Apply'}
          </button>
        </div>
      </div>
    </div>
  );
}

// ── Tenant Row ────────────────────────────────────────────────────────────────

function TenantRow({ t, onAction, selected, onToggle }: {
  t: PlatformTenantSummary;
  onAction: (action: 'suspend' | 'reactivate', t: PlatformTenantSummary) => void;
  selected: boolean;
  onToggle: (id: string) => void;
}) {
  const router = useRouter();
  const status  = statusKey(t.subscription?.status ?? 'unknown');
  const plan    = (t.subscription?.plan ?? 'trial').toLowerCase();
  const risk    = isAtRisk(t);
  const days    = daysUntil(t.subscription?.expiresAtUtc ?? null);
  const expiry  = expiryLabel(days);
  const maxEmp  = t.subscription?.maxEmployees ?? 0;
  const empPct  = maxEmp > 0 ? Math.round((t.activeEmployeeCount / maxEmp) * 100) : 0;
  const mrr     = t.subscription?.monthlyAmount ?? 0;

  return (
    <tr
      className={`border-b border-white/[0.04] hover:bg-white/[0.03] transition-colors cursor-pointer
        ${selected ? 'bg-sapphire/[0.07]' : ''}
        ${risk === 'rose' ? 'bg-rose-950/20 border-l-2 border-l-rose-700' : ''}
        ${risk === 'amber' ? 'bg-amber-950/20 border-l-2 border-l-amber-600' : ''}`}
      onClick={() => router.push(`/platform/tenants/${t.id}`)}
    >
      {/* Select checkbox */}
      <td className="px-3 py-3 w-9" onClick={e => e.stopPropagation()}>
        <input
          type="checkbox"
          aria-label={`Select ${t.name}`}
          checked={selected}
          onChange={() => onToggle(t.id)}
          className="h-3.5 w-3.5 rounded border-white/20 bg-white/[0.04] accent-sapphire cursor-pointer"
        />
      </td>
      {/* Status dot + name */}
      <td className="px-4 py-3">
        <div className="flex items-center gap-3">
          <span className={`h-1.5 w-1.5 rounded-full shrink-0 ${STATUS_DOT[status] ?? 'bg-slate-600'}`} />
          <div className="min-w-0">
            <p className="text-sm text-white font-medium truncate">{t.name}</p>
            <p className="text-[11px] text-slate-600 font-mono">/{t.slug}</p>
          </div>
        </div>
      </td>
      {/* Plan */}
      <td className="px-3 py-3">
        <span className={`text-[10px] font-semibold uppercase tracking-wider px-1.5 py-0.5 rounded border ${PLAN_BADGE[plan] ?? PLAN_BADGE.trial}`}>
          {t.subscription?.plan ?? 'Trial'}
        </span>
      </td>
      {/* Status */}
      <td className="px-3 py-3">
        <span className={`text-xs font-medium capitalize ${STATUS_LABEL[status] ?? 'text-slate-400'}`}>
          {t.subscription?.status ?? 'Unknown'}
        </span>
      </td>
      {/* MRR */}
      <td className="px-3 py-3 text-right tabular-nums">
        <span className="text-xs text-slate-300">
          {mrr > 0 ? `$${mrr.toLocaleString()}` : <span className="text-slate-700">—</span>}
        </span>
      </td>
      {/* Employees */}
      <td className="px-3 py-3">
        <div className="min-w-[80px]">
          <p className="text-[11px] text-slate-400 tabular-nums">
            {t.activeEmployeeCount}{maxEmp > 0 ? `/${maxEmp}` : ''}
          </p>
          {maxEmp > 0 && (
            <div className="mt-1 h-1 w-20 rounded-full bg-white/[0.06] overflow-hidden">
              <div
                className={`h-full rounded-full transition-all ${empPct >= 90 ? 'bg-rose-500' : empPct >= 75 ? 'bg-amber-500' : 'bg-sapphire'}`}
                style={{ width: `${Math.min(empPct, 100)}%` }}
              />
            </div>
          )}
        </div>
      </td>
      {/* Expiry */}
      <td className="px-3 py-3 text-right tabular-nums">
        <span className={`text-xs ${expiry.cls}`}>{expiry.text}</span>
      </td>
      {/* Actions */}
      <td className="px-3 py-3" onClick={e => e.stopPropagation()}>
        <div className="flex items-center gap-1 justify-end">
          {status === 'suspended' ? (
            <button type="button"
              onClick={() => onAction('reactivate', t)}
              className="text-[11px] text-emerald-400 hover:text-emerald-300 border border-emerald-500/20 hover:border-emerald-500/40 px-2 py-1 rounded transition-colors">
              Reactivate
            </button>
          ) : status !== 'cancelled' ? (
            <button type="button"
              onClick={() => onAction('suspend', t)}
              className="text-[11px] text-rose-400 hover:text-rose-300 border border-rose-500/20 hover:border-rose-500/40 px-2 py-1 rounded transition-colors">
              Suspend
            </button>
          ) : null}
          <Link href={`/platform/tenants/${t.id}`}
            className="h-6 w-6 flex items-center justify-center text-slate-600 hover:text-white hover:bg-white/[0.06] rounded transition-colors">
            <MoreHorizontal className="h-3.5 w-3.5" />
          </Link>
        </div>
      </td>
    </tr>
  );
}

// ── Main Page ─────────────────────────────────────────────────────────────────

const STATUS_FILTERS = ['All', 'Active', 'Trial', 'PastDue', 'Suspended', 'Cancelled'];

export default function TenantsPage() {
  const router = useRouter();
  const [tenants, setTenants]     = useState<PlatformTenantSummary[]>([]);
  const [loading, setLoading]     = useState(true);
  const [search, setSearch]       = useState('');
  const [statusFilter, setStatus] = useState('All');
  const [showNew, setShowNew]     = useState(false);
  const [confirm, setConfirm]     = useState<{ action: 'suspend' | 'reactivate'; tenant: PlatformTenantSummary } | null>(null);
  const [opMsg, setOpMsg]         = useState<{ text: string; ok: boolean } | null>(null);
  const [selected, setSelected]   = useState<Set<string>>(new Set());
  const [bulkConfirm, setBulkConfirm] = useState<'suspend' | 'reactivate' | 'delete' | null>(null);
  const [showBulkFeature, setShowBulkFeature] = useState(false);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [router]);

  const load = useCallback(async () => {
    setLoading(true);
    try { setTenants(await platformApi.listTenants()); }
    finally { setLoading(false); }
  }, []);

  async function doAction(reason: string) {
    if (!confirm) return;
    const { action, tenant } = confirm;
    try {
      if (action === 'suspend')     await platformApi.suspendTenant(tenant.id, reason);
      else                          await platformApi.reactivateTenant(tenant.id, reason);
      setOpMsg({ text: `${tenant.name} ${action === 'suspend' ? 'suspended' : 'reactivated'}.`, ok: true });
      setConfirm(null);
      await load();
    } catch {
      setOpMsg({ text: `Failed to ${action} tenant.`, ok: false });
      setConfirm(null);
    }
  }

  const filtered = tenants.filter(t => {
    const q = search.toLowerCase();
    const matchQ = !q || t.name.toLowerCase().includes(q) || t.slug.toLowerCase().includes(q);
    const matchS = statusFilter === 'All' || (t.subscription?.status ?? '').toLowerCase() === statusFilter.toLowerCase();
    return matchQ && matchS;
  });

  // ── Selection helpers ────────────────────────────────────────────────────
  function toggleOne(id: string) {
    setSelected(prev => {
      const next = new Set(prev);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  }
  const filteredIds = filtered.map(t => t.id);
  const allFilteredSelected = filteredIds.length > 0 && filteredIds.every(id => selected.has(id));
  function toggleAll() {
    setSelected(prev => {
      const next = new Set(prev);
      if (allFilteredSelected) filteredIds.forEach(id => next.delete(id));
      else filteredIds.forEach(id => next.add(id));
      return next;
    });
  }
  function clearSelection() { setSelected(new Set()); }

  function summaryText(r: BulkOpResult, verb: string) {
    const parts = [`${r.succeeded} ${verb}`];
    if (r.skipped) parts.push(`${r.skipped} skipped`);
    if (r.failed)  parts.push(`${r.failed} failed`);
    return parts.join(' · ');
  }

  async function doBulkAction(reason: string) {
    if (!bulkConfirm) return;
    const ids = [...selected];
    try {
      let r: BulkOpResult;
      if (bulkConfirm === 'suspend')         r = await platformApi.bulkSuspendTenants(ids, reason);
      else if (bulkConfirm === 'reactivate') r = await platformApi.bulkReactivateTenants(ids, reason);
      else                                   r = await platformApi.bulkDeleteTenants(ids);
      const verb = bulkConfirm === 'suspend' ? 'suspended' : bulkConfirm === 'reactivate' ? 'reactivated' : 'deleted';
      setOpMsg({ text: summaryText(r, verb), ok: r.failed === 0 });
      setBulkConfirm(null);
      clearSelection();
      await load();
    } catch {
      setOpMsg({ text: `Bulk ${bulkConfirm} failed.`, ok: false });
      setBulkConfirm(null);
    }
  }

  async function doBulkFeature(featureKey: string, isEnabled: boolean, applyToAll: boolean) {
    try {
      const r = await platformApi.bulkSetFeature(
        applyToAll ? { applyToAll: true, featureKey, isEnabled }
                   : { tenantIds: [...selected], featureKey, isEnabled });
      setOpMsg({ text: `${featureKey} ${isEnabled ? 'enabled' : 'disabled'}: ${summaryText(r, 'updated')}`, ok: r.failed === 0 });
      setShowBulkFeature(false);
      clearSelection();
      await load();
    } catch {
      setOpMsg({ text: 'Bulk feature update failed.', ok: false });
      setShowBulkFeature(false);
    }
  }

  const counts = {
    total: tenants.length,
    active: tenants.filter(t => statusKey(t.subscription?.status ?? '') === 'active').length,
    trial: tenants.filter(t => statusKey(t.subscription?.status ?? '') === 'trial').length,
    suspended: tenants.filter(t => statusKey(t.subscription?.status ?? '') === 'suspended').length,
    atRisk: tenants.filter(t => isAtRisk(t) !== null).length,
  };

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">All Tenants</h1>
          <p className="text-xs text-slate-500 mt-0.5">{counts.total} tenants · {counts.active} active · {counts.trial} trial</p>
        </div>
        <div className="flex items-center gap-2">
          <button type="button" onClick={load} disabled={loading} title="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
          <button type="button" onClick={() => setShowBulkFeature(true)}
            className="flex items-center gap-1.5 border border-white/10 text-slate-300 hover:text-white hover:border-white/20 px-3 py-1.5 rounded-lg text-sm font-medium transition-colors">
            <Zap className="h-3.5 w-3.5 text-sapphire" />
            Feature Rollout
          </button>
          <button type="button" onClick={() => setShowNew(true)}
            className="flex items-center gap-1.5 bg-sapphire hover:bg-blue-500 text-white px-3 py-1.5 rounded-lg text-sm font-semibold transition-colors">
            <Plus className="h-3.5 w-3.5" />
            New Tenant
          </button>
        </div>
      </div>

      {/* Op message */}
      {opMsg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${opMsg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          <span>{opMsg.text}</span>
          <button type="button" onClick={() => setOpMsg(null)} className="opacity-60 hover:opacity-100">
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      )}

      {/* Summary chips */}
      <div className="flex flex-wrap gap-2">
        {[
          { label: 'Active', count: counts.active, icon: CheckCircle, cls: 'text-emerald-400 border-emerald-500/20 bg-emerald-500/5' },
          { label: 'Trial', count: counts.trial, icon: Clock, cls: 'text-cyan-400 border-cyan-500/20 bg-cyan-500/5' },
          { label: 'Suspended', count: counts.suspended, icon: XCircle, cls: 'text-rose-400 border-rose-500/20 bg-rose-500/5' },
          { label: 'At Risk', count: counts.atRisk, icon: AlertTriangle, cls: 'text-amber-400 border-amber-500/20 bg-amber-500/5' },
        ].map(({ label, count, icon: Icon, cls }) => (
          <div key={label} className={`flex items-center gap-1.5 px-2.5 py-1 rounded-full border text-xs font-medium ${cls}`}>
            <Icon className="h-3 w-3" />
            {count} {label}
          </div>
        ))}
      </div>

      {/* Filters */}
      <div className="flex items-center gap-3">
        <div className="relative flex-1 max-w-xs">
          <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-600 pointer-events-none" />
          <input type="text" value={search} onChange={e => setSearch(e.target.value)}
            placeholder="Search tenants…"
            className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg pl-8 pr-3 py-1.5 text-sm text-slate-300 placeholder-slate-600 focus:outline-none focus:border-sapphire/60 transition-colors" />
        </div>
        <select aria-label="Filter by status" value={statusFilter} onChange={e => setStatus(e.target.value)}
          className="bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-1.5 text-sm text-slate-300 focus:outline-none focus:border-sapphire/60">
          {STATUS_FILTERS.map(s => <option key={s} value={s}>{s}</option>)}
        </select>
      </div>

      {/* Bulk action bar */}
      {selected.size > 0 && (
        <div className="flex flex-wrap items-center gap-2 px-4 py-2.5 rounded-lg border border-sapphire/30 bg-sapphire/[0.08]">
          <span className="text-sm font-medium text-white">{selected.size} selected</span>
          <button type="button" onClick={clearSelection} className="text-xs text-slate-400 hover:text-white">Clear</button>
          <div className="flex-1" />
          <button type="button" onClick={() => setShowBulkFeature(true)}
            className="flex items-center gap-1.5 text-xs font-medium text-sapphire hover:text-blue-300 border border-sapphire/30 hover:border-sapphire/50 px-2.5 py-1.5 rounded-lg transition-colors">
            <Zap className="h-3.5 w-3.5" /> Apply Feature
          </button>
          <button type="button" onClick={() => setBulkConfirm('reactivate')}
            className="flex items-center gap-1.5 text-xs font-medium text-emerald-400 hover:text-emerald-300 border border-emerald-500/20 hover:border-emerald-500/40 px-2.5 py-1.5 rounded-lg transition-colors">
            <Power className="h-3.5 w-3.5" /> Reactivate
          </button>
          <button type="button" onClick={() => setBulkConfirm('suspend')}
            className="flex items-center gap-1.5 text-xs font-medium text-amber-400 hover:text-amber-300 border border-amber-500/20 hover:border-amber-500/40 px-2.5 py-1.5 rounded-lg transition-colors">
            <PowerOff className="h-3.5 w-3.5" /> Suspend
          </button>
          <button type="button" onClick={() => setBulkConfirm('delete')}
            className="flex items-center gap-1.5 text-xs font-medium text-rose-400 hover:text-rose-300 border border-rose-500/20 hover:border-rose-500/40 px-2.5 py-1.5 rounded-lg transition-colors">
            <Trash2 className="h-3.5 w-3.5" /> Delete
          </button>
        </div>
      )}

      {/* Table */}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-16">
            <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
          </div>
        ) : filtered.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-16 gap-3">
            <Building2 className="h-8 w-8 text-slate-700" />
            <p className="text-sm text-slate-600">
              {tenants.length === 0 ? 'No tenants yet. Provision your first.' : 'No tenants match your filter.'}
            </p>
            {tenants.length === 0 && (
              <button type="button" onClick={() => setShowNew(true)}
                className="text-xs text-sapphire hover:text-blue-300 transition-colors">
                + Provision Tenant
              </button>
            )}
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[700px]">
              <thead>
                <tr className="border-b border-white/[0.06]">
                  <th className="px-3 py-2.5 w-9">
                    <input type="checkbox" aria-label="Select all tenants"
                      checked={allFilteredSelected} onChange={toggleAll}
                      className="h-3.5 w-3.5 rounded border-white/20 bg-white/[0.04] accent-sapphire cursor-pointer" />
                  </th>
                  {['Tenant', 'Plan', 'Status', 'MRR', 'Employees', 'Expires', ''].map(h => (
                    <th key={h} className="px-4 py-2.5 text-left text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {filtered.map(t => (
                  <TenantRow key={t.id} t={t}
                    onAction={(action, tenant) => setConfirm({ action, tenant })}
                    selected={selected.has(t.id)}
                    onToggle={toggleOne} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Modals */}
      {showNew && <NewTenantModal onClose={() => setShowNew(false)} onCreated={load} />}
      {confirm && (
        <ConfirmModal
          title={confirm.action === 'suspend' ? `Suspend ${confirm.tenant.name}?` : `Reactivate ${confirm.tenant.name}?`}
          message={confirm.action === 'suspend'
            ? 'This will immediately disable all tenant users. Provide a reason for the record.'
            : 'This will restore access for all tenant users.'}
          onConfirm={doAction}
          onClose={() => setConfirm(null)}
          danger={confirm.action === 'suspend'}
          requireReason={confirm.action === 'suspend'}
        />
      )}
      {bulkConfirm && (
        <ConfirmModal
          title={
            bulkConfirm === 'suspend'    ? `Suspend ${selected.size} tenant${selected.size === 1 ? '' : 's'}?` :
            bulkConfirm === 'reactivate' ? `Reactivate ${selected.size} tenant${selected.size === 1 ? '' : 's'}?` :
                                           `Delete ${selected.size} tenant${selected.size === 1 ? '' : 's'}?`}
          message={
            bulkConfirm === 'suspend'    ? 'This will disable all users across the selected tenants. Provide a reason for the record.' :
            bulkConfirm === 'reactivate' ? 'This will restore access for all users across the selected tenants.' :
                                           'This permanently deactivates the selected tenants, revokes all their sessions, and frees their slugs. This cannot be undone from here.'}
          onConfirm={doBulkAction}
          onClose={() => setBulkConfirm(null)}
          danger={bulkConfirm !== 'reactivate'}
          requireReason={bulkConfirm === 'suspend'}
        />
      )}
      {showBulkFeature && (
        <BulkFeatureModal
          selectedCount={selected.size}
          totalCount={tenants.length}
          onClose={() => setShowBulkFeature(false)}
          onApply={doBulkFeature}
        />
      )}
    </div>
  );
}
