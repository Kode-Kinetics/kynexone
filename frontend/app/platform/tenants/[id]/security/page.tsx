'use client';

import { useState, useEffect, useCallback } from 'react';
import { useParams } from 'next/navigation';
import Link from 'next/link';
import { ArrowLeft, RefreshCw, X, Shield, CheckCircle, Activity } from 'lucide-react';
import { platformApi, type TenantSecurityPolicy, type LoginActivity } from '@/src/api/platform';

// ── Login Activity Feed ───────────────────────────────────────────────────────

const EVENT_BADGE: Record<string, string> = {
  login_success:              'text-emerald-400 bg-emerald-900/20 border-emerald-700/30',
  login_failed:               'text-rose-400 bg-rose-900/20 border-rose-700/30',
  login_blocked_lockout:      'text-rose-400 bg-rose-900/20 border-rose-700/30',
  account_locked:             'text-rose-400 bg-rose-900/20 border-rose-700/30',
  password_reset_requested:   'text-amber-400 bg-amber-900/20 border-amber-700/30',
  password_reset_completed:   'text-amber-400 bg-amber-900/20 border-amber-700/30',
  mfa_reset:                  'text-purple-400 bg-purple-900/20 border-purple-700/30',
  session_revoked:            'text-slate-400 bg-slate-800/50 border-slate-700/30',
  platform_login_success:     'text-blue-400 bg-blue-900/20 border-blue-700/30',
  platform_login_failed:      'text-rose-400 bg-rose-900/20 border-rose-700/30',
};

function LoginActivityPanel({ tenantId }: { tenantId: string }) {
  const [items, setItems]         = useState<LoginActivity[]>([]);
  const [total, setTotal]         = useState(0);
  const [loading, setLoading]     = useState(true);
  const [eventType, setEventType] = useState('');
  const [page, setPage]           = useState(1);
  const PAGE_SIZE = 25;

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const res = await platformApi.listLoginActivity({
        tenantId,
        eventType: eventType || undefined,
        page,
        pageSize: PAGE_SIZE,
      });
      setItems(res.items);
      setTotal(res.total);
    } catch {
      setItems([]);
    } finally { setLoading(false); }
  }, [tenantId, eventType, page]);

  useEffect(() => { load(); }, [load]);

  const EVENT_TYPES = [
    '', 'login_success', 'login_failed', 'login_blocked_lockout',
    'account_locked', 'password_reset_requested', 'password_reset_completed',
    'mfa_reset', 'session_revoked',
  ];

  return (
    <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
      <div className="px-5 py-3 border-b border-white/[0.06] flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Activity className="h-3.5 w-3.5 text-slate-500" />
          <p className="text-[10px] font-semibold text-slate-500 uppercase tracking-widest">Login Activity</p>
          {total > 0 && <span className="text-[10px] text-slate-600">({total} total)</span>}
        </div>
        <div className="flex items-center gap-2">
          <select
            value={eventType}
            onChange={e => { setEventType(e.target.value); setPage(1); }}
            aria-label="Filter by event type"
            className="text-xs bg-white/[0.04] border border-white/[0.08] rounded px-2 py-1 text-slate-400 focus:outline-none focus:border-blue-500/60"
          >
            <option value="">All events</option>
            {EVENT_TYPES.slice(1).map(t => (
              <option key={t} value={t}>{t.replace(/_/g, ' ')}</option>
            ))}
          </select>
          <button type="button" onClick={load} disabled={loading} aria-label="Refresh activity"
            className="h-6 w-6 flex items-center justify-center text-slate-500 hover:text-white transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3 w-3 ${loading ? 'animate-spin' : ''}`} />
          </button>
        </div>
      </div>

      {loading ? (
        <div className="flex items-center justify-center py-8">
          <div className="h-4 w-4 animate-spin rounded-full border-2 border-blue-500 border-t-transparent" />
        </div>
      ) : items.length === 0 ? (
        <div className="text-center py-8">
          <p className="text-xs text-slate-600">No login activity found.</p>
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[640px] text-xs">
            <thead>
              <tr className="border-b border-white/[0.06]">
                {['Time', 'Event', 'Email', 'IP', 'Reason'].map(h => (
                  <th key={h} className="px-4 py-2.5 text-start text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {items.map(a => (
                <tr key={a.id} className="border-b border-white/[0.04] hover:bg-white/[0.01]">
                  <td className="px-4 py-2 text-slate-600 whitespace-nowrap">
                    {new Date(a.occurredAtUtc).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}
                  </td>
                  <td className="px-4 py-2 whitespace-nowrap">
                    <span className={`text-[10px] font-semibold px-1.5 py-0.5 rounded border ${EVENT_BADGE[a.eventType] ?? 'text-slate-400 border-slate-700/30 bg-slate-800/30'}`}>
                      {a.eventType.replace(/_/g, ' ')}
                    </span>
                  </td>
                  <td className="px-4 py-2 text-slate-400 max-w-[160px] truncate">{a.emailAttempted ?? '—'}</td>
                  <td className="px-4 py-2 text-slate-600 font-mono">{a.ipAddress ?? '—'}</td>
                  <td className="px-4 py-2 text-slate-600">{a.failureReason ? a.failureReason.replace(/_/g, ' ') : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {total > PAGE_SIZE && (
        <div className="flex items-center justify-between px-5 py-3 border-t border-white/[0.06]">
          <button type="button" onClick={() => setPage(p => Math.max(1, p - 1))} disabled={page === 1}
            className="text-xs text-slate-400 hover:text-white disabled:opacity-30 transition-colors">← Prev</button>
          <span className="text-[11px] text-slate-600">Page {page} of {Math.ceil(total / PAGE_SIZE)}</span>
          <button type="button" onClick={() => setPage(p => p + 1)} disabled={page * PAGE_SIZE >= total}
            className="text-xs text-slate-400 hover:text-white disabled:opacity-30 transition-colors">Next →</button>
        </div>
      )}
    </div>
  );
}

function Toggle({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <label className="flex items-center justify-between cursor-pointer py-1">
      <span className="text-sm text-slate-300">{label}</span>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        onClick={() => onChange(!checked)}
        className={`relative h-5 w-9 rounded-full border transition-colors shrink-0 ${
          checked ? 'bg-sapphire border-sapphire/60' : 'bg-white/10 border-white/10'
        }`}
      >
        <span className={`absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-transform ${
          checked ? 'translate-x-4 rtl:-translate-x-4' : 'translate-x-0.5 rtl:-translate-x-0.5'
        }`} />
      </button>
    </label>
  );
}

function NumInput({ label, value, onChange, min, max, step = 1, unit }: {
  label: string; value: number; onChange: (v: number) => void;
  min?: number; max?: number; step?: number; unit?: string;
}) {
  return (
    <div className="flex items-center justify-between py-1">
      <span className="text-sm text-slate-300">{label}</span>
      <div className="flex items-center gap-1.5">
        <input
          type="number"
          value={value}
          min={min}
          max={max}
          step={step}
          onChange={e => onChange(parseInt(e.target.value) || 0)}
          aria-label={label}
          className="w-20 bg-white/[0.04] border border-white/[0.08] rounded-lg px-2 py-1 text-sm text-white text-end focus:outline-none focus:border-sapphire/60"
        />
        {unit && <span className="text-xs text-slate-600 w-10">{unit}</span>}
      </div>
    </div>
  );
}

export default function TenantSecurityPage() {
  const { id } = useParams<{ id: string }>();
  const [policy, setPolicy]   = useState<TenantSecurityPolicy | null>(null);
  const [form, setForm]       = useState<Partial<TenantSecurityPolicy>>({});
  const [loading, setLoading] = useState(true);
  const [saving, setSaving]   = useState(false);
  const [msg, setMsg]         = useState<{ text: string; ok: boolean } | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const p = await platformApi.getTenantSecurityPolicy(id);
      setPolicy(p);
      setForm({
        passwordMinLength:        p.passwordMinLength,
        passwordRequireUppercase: p.passwordRequireUppercase,
        passwordRequireLowercase: p.passwordRequireLowercase,
        passwordRequireDigit:     p.passwordRequireDigit,
        passwordRequireSpecial:   p.passwordRequireSpecial,
        passwordExpiryDays:       p.passwordExpiryDays,
        passwordHistoryCount:     p.passwordHistoryCount,
        maxFailedLoginAttempts:   p.maxFailedLoginAttempts,
        lockoutDurationMinutes:   p.lockoutDurationMinutes,
        sessionTimeoutMinutes:    p.sessionTimeoutMinutes,
        refreshTokenExpiryDays:   p.refreshTokenExpiryDays,
        allowMultipleSessions:    p.allowMultipleSessions,
      });
    } catch {
      setMsg({ text: 'Failed to load security policy.', ok: false });
    } finally { setLoading(false); }
  }, [id]);

  useEffect(() => { load(); }, [load]);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true); setMsg(null);
    try {
      await platformApi.updateTenantSecurityPolicy(id, form);
      setMsg({ text: 'Security policy saved.', ok: true });
      await load();
    } catch {
      setMsg({ text: 'Save failed. Please try again.', ok: false });
    } finally { setSaving(false); }
  }

  function set<K extends keyof TenantSecurityPolicy>(key: K, value: TenantSecurityPolicy[K]) {
    setForm(f => ({ ...f, [key]: value }));
  }

  if (loading) return (
    <div className="flex items-center justify-center min-h-[60vh]">
      <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
    </div>
  );

  return (
    <div className="space-y-5">
      <div className="flex items-center gap-3">
        <Link href={`/platform/tenants/${id}`}
          className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors shrink-0">
          <ArrowLeft className="h-4 w-4" />
        </Link>
        <div>
          <h1 className="text-lg font-bold text-white">Security Policy</h1>
          {policy && <p className="text-xs text-slate-500">{policy.tenantName}</p>}
        </div>
        <div className="ms-auto flex items-center gap-2">
          {policy?.isCustomPolicy && (
            <span className="text-[10px] font-semibold text-amber-400 bg-amber-500/10 border border-amber-500/20 px-2 py-0.5 rounded">
              Custom Policy
            </span>
          )}
          {!policy?.isCustomPolicy && (
            <span className="text-[10px] font-semibold text-slate-500 bg-white/[0.04] border border-white/[0.06] px-2 py-0.5 rounded">
              Platform Defaults
            </span>
          )}
          <button type="button" onClick={load} disabled={loading} aria-label="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
        </div>
      </div>

      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${
          msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'
        }`}>
          <div className="flex items-center gap-2">
            {msg.ok && <CheckCircle className="h-3.5 w-3.5 shrink-0" />}
            {msg.text}
          </div>
          <button type="button" aria-label="Dismiss" onClick={() => setMsg(null)}>
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      )}

      <form onSubmit={save} className="space-y-5">
        {/* Password Policy */}
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          <div className="px-5 py-3 border-b border-white/[0.06] flex items-center gap-2">
            <Shield className="h-3.5 w-3.5 text-slate-500" />
            <p className="text-[10px] font-semibold text-slate-500 uppercase tracking-widest">Password Policy</p>
          </div>
          <div className="px-5 py-4 space-y-2 divide-y divide-white/[0.04]">
            <NumInput label="Minimum length" value={form.passwordMinLength ?? 10}
              onChange={v => set('passwordMinLength', v)} min={6} max={32} unit="chars" />
            <div className="pt-2 space-y-1">
              <Toggle label="Require uppercase" checked={form.passwordRequireUppercase ?? true}
                onChange={v => set('passwordRequireUppercase', v)} />
              <Toggle label="Require lowercase" checked={form.passwordRequireLowercase ?? true}
                onChange={v => set('passwordRequireLowercase', v)} />
              <Toggle label="Require digit" checked={form.passwordRequireDigit ?? true}
                onChange={v => set('passwordRequireDigit', v)} />
              <Toggle label="Require special character" checked={form.passwordRequireSpecial ?? true}
                onChange={v => set('passwordRequireSpecial', v)} />
            </div>
            <div className="pt-2 space-y-1">
              <NumInput label="Password expiry" value={form.passwordExpiryDays ?? 90}
                onChange={v => set('passwordExpiryDays', v)} min={0} max={365} unit="days" />
              <NumInput label="Password history" value={form.passwordHistoryCount ?? 5}
                onChange={v => set('passwordHistoryCount', v)} min={0} max={24} unit="prev" />
            </div>
          </div>
        </div>

        {/* Lockout Policy */}
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          <div className="px-5 py-3 border-b border-white/[0.06] flex items-center gap-2">
            <Shield className="h-3.5 w-3.5 text-slate-500" />
            <p className="text-[10px] font-semibold text-slate-500 uppercase tracking-widest">Lockout Policy</p>
          </div>
          <div className="px-5 py-4 space-y-2">
            <NumInput label="Max failed login attempts" value={form.maxFailedLoginAttempts ?? 5}
              onChange={v => set('maxFailedLoginAttempts', v)} min={3} max={20} unit="tries" />
            <NumInput label="Lockout duration" value={form.lockoutDurationMinutes ?? 30}
              onChange={v => set('lockoutDurationMinutes', v)} min={5} max={1440} unit="min" />
          </div>
        </div>

        {/* Session Policy */}
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          <div className="px-5 py-3 border-b border-white/[0.06] flex items-center gap-2">
            <Shield className="h-3.5 w-3.5 text-slate-500" />
            <p className="text-[10px] font-semibold text-slate-500 uppercase tracking-widest">Session Policy</p>
          </div>
          <div className="px-5 py-4 space-y-2">
            <NumInput label="Session timeout (inactivity)" value={form.sessionTimeoutMinutes ?? 480}
              onChange={v => set('sessionTimeoutMinutes', v)} min={15} max={10080} unit="min" />
            <NumInput label="Refresh token expiry" value={form.refreshTokenExpiryDays ?? 30}
              onChange={v => set('refreshTokenExpiryDays', v)} min={1} max={365} unit="days" />
            <Toggle label="Allow multiple concurrent sessions" checked={form.allowMultipleSessions ?? true}
              onChange={v => set('allowMultipleSessions', v)} />
          </div>
        </div>

        {policy?.updatedAtUtc && (
          <p className="text-[11px] text-slate-700">
            Last updated {new Date(policy.updatedAtUtc).toLocaleString('en-GB')}
          </p>
        )}

        <div className="flex justify-end">
          <button type="submit" disabled={saving}
            className="bg-sapphire hover:bg-blue-500 text-white px-8 py-2.5 rounded-lg text-sm font-semibold transition-colors disabled:opacity-40">
            {saving ? 'Saving…' : 'Save Security Policy'}
          </button>
        </div>
      </form>

      <LoginActivityPanel tenantId={id} />
    </div>
  );
}
