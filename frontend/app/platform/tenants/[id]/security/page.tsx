'use client';

import { useState, useEffect, useCallback } from 'react';
import { useParams } from 'next/navigation';
import Link from 'next/link';
import { ArrowLeft, RefreshCw, X, Shield, CheckCircle, Activity } from 'lucide-react';
import { platformApi, type TenantSecurityPolicy, type LoginActivity, type TwoStepPolicy } from '@/src/api/platform';

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
          <p className="text-caption font-semibold text-slate-500 uppercase tracking-widest">Login Activity</p>
          {total > 0 && <span className="text-caption text-slate-600">({total} total)</span>}
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
                  <th key={h} className="px-4 py-2.5 text-start text-caption font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
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
                    <span className={`text-caption font-semibold px-1.5 py-0.5 rounded border ${EVENT_BADGE[a.eventType] ?? 'text-slate-400 border-slate-700/30 bg-slate-800/30'}`}>
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
          <span className="text-caption text-slate-600">Page {page} of {Math.ceil(total / PAGE_SIZE)}</span>
          <button type="button" onClick={() => setPage(p => p + 1)} disabled={page * PAGE_SIZE >= total}
            className="text-xs text-slate-400 hover:text-white disabled:opacity-30 transition-colors">Next →</button>
        </div>
      )}
    </div>
  );
}

// ── Two-step sign-in ──────────────────────────────────────────────────────────

const TWO_STEP_OPTIONS: { value: TwoStepPolicy; title: string; hint: string }[] = [
  { value: 'Optional', title: 'Each person chooses (two-step off for now)', hint: 'Two-step sign-in is available but nobody is forced to use it. Recommended only while testing or for a demo.' },
  { value: 'PrivilegedRoles', title: 'Required for payroll, banking and access roles', hint: 'Everyone else chooses for themselves.' },
  { value: 'Everyone', title: 'Required for everyone', hint: 'People who have not set it up are signed out and set it up at their next sign-in.' },
];
const TRUST_CHOICES = [0, 7, 14, 30, 60, 90];
const trustLabel = (d: number) => (d === 0 ? 'Off (always ask for the code)' : `${d} days`);

function TwoStepPanel({ tenantId, policy, onSaved }: {
  tenantId: string;
  policy: TenantSecurityPolicy;
  onSaved: () => Promise<void>;
}) {
  const [mode, setMode]     = useState<TwoStepPolicy>(policy.mfaPolicy);
  const [days, setDays]     = useState(policy.mfaTrustedDeviceDays);
  const [reason, setReason] = useState('');
  const [saving, setSaving] = useState(false);
  const [msg, setMsg]       = useState<{ text: string; ok: boolean } | null>(null);

  useEffect(() => { setMode(policy.mfaPolicy); setDays(policy.mfaTrustedDeviceDays); }, [policy.mfaPolicy, policy.mfaTrustedDeviceDays]);

  const relaxing = mode === 'Optional' && policy.mfaPolicy !== 'Optional';
  const dirty = mode !== policy.mfaPolicy || days !== policy.mfaTrustedDeviceDays;
  const choices = TRUST_CHOICES.includes(days) ? TRUST_CHOICES : [...TRUST_CHOICES, days].sort((a, b) => a - b);

  async function save() {
    if (relaxing && reason.trim().length === 0) { setMsg({ text: 'Give a reason for letting each person choose.', ok: false }); return; }
    setSaving(true); setMsg(null);
    try {
      await platformApi.updateTenantTwoStepPolicy(tenantId, {
        mfaPolicy: mode !== policy.mfaPolicy ? mode : undefined,
        mfaTrustedDeviceDays: days !== policy.mfaTrustedDeviceDays ? days : undefined,
        reason: relaxing ? reason.trim() : undefined,
      });
      setReason('');
      setMsg({ text: 'Two-step sign-in saved.', ok: true });
      await onSaved();
    } catch (e: unknown) {
      const detail = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setMsg({ text: detail ?? 'Save failed. Please try again.', ok: false });
    } finally { setSaving(false); }
  }

  return (
    <div className="bg-slate-900 border border-white/[0.07] rounded-xl overflow-hidden" data-testid="platform-two-step">
      <div className="px-5 py-3 border-b border-white/[0.06] flex items-center gap-2">
        <Shield className="h-3.5 w-3.5 text-slate-500" />
        <p className="text-caption font-semibold text-slate-500 uppercase tracking-widest">Two-step sign-in (authenticator)</p>
      </div>
      <div className="px-5 py-4 space-y-4">
        <fieldset className="space-y-2">
          <legend className="sr-only">Who must use two-step sign-in</legend>
          {TWO_STEP_OPTIONS.map(o => (
            <label key={o.value} htmlFor={`two-step-${o.value}`}
              className={`grid cursor-pointer grid-cols-[auto_1fr] items-start gap-x-3 rounded-lg border px-3 py-2.5 ${mode === o.value ? 'border-sapphire/50 bg-sapphire/[0.07]' : 'border-white/[0.08]'}`}>
              <input id={`two-step-${o.value}`} type="radio" name="two-step-policy" value={o.value} checked={mode === o.value}
                onChange={() => setMode(o.value)} className="row-span-2 mt-1 h-4 w-4 shrink-0 accent-sapphire" />
              <span className="block text-sm text-slate-200">{o.title}</span>
              <span className="block text-xs text-slate-500">{o.hint}</span>
            </label>
          ))}
        </fieldset>

        {relaxing && (
          <div className="rounded-lg border border-amber-500/20 bg-amber-500/10 px-3 py-2.5">
            <label htmlFor="two-step-reason" className="block space-y-1.5">
              <span className="block text-xs font-medium text-amber-300">Reason (required, recorded in the audit log)</span>
              <textarea id="two-step-reason" rows={2} maxLength={500} value={reason} onChange={e => setReason(e.target.value)}
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 resize-none" />
            </label>
          </div>
        )}

        <div className="max-w-xs">
          <label htmlFor="two-step-trust-days" className="block">
            <span className="mb-1 block text-sm text-slate-300">Trusted browsers</span>
            <select id="two-step-trust-days" value={days} onChange={e => setDays(Number(e.target.value))}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60">
              {choices.map(d => <option key={d} value={d} className="bg-slate-950">{trustLabel(d)}</option>)}
            </select>
          </label>
          <p className="mt-1 text-xs text-slate-500">How long a browser can skip the code after the person ticks &ldquo;Trust this browser&rdquo;. Shortening it also shortens browsers already trusted.</p>
        </div>

        {msg && (
          <p className={`text-sm ${msg.ok ? 'text-emerald-400' : 'text-rose-400'}`} role="status">{msg.text}</p>
        )}
        <div className="flex justify-end">
          <button type="button" onClick={save} disabled={saving || !dirty}
            className="bg-sapphire hover:bg-blue-500 text-white px-6 py-2 rounded-lg text-sm font-semibold transition-colors disabled:opacity-40">
            {saving ? 'Saving…' : 'Save two-step sign-in'}
          </button>
        </div>
      </div>
    </div>
  );
}

function InvitationHandoverPanel({ tenantId, policy, onSaved }: {
  tenantId: string;
  policy: TenantSecurityPolicy;
  onSaved: () => Promise<void>;
}) {
  const [enabled, setEnabled] = useState(policy.allowAdminInvitationHandover);
  const [reason, setReason]   = useState('');
  const [saving, setSaving]   = useState(false);
  const [msg, setMsg]         = useState<{ text: string; ok: boolean } | null>(null);

  useEffect(() => { setEnabled(policy.allowAdminInvitationHandover); }, [policy.allowAdminInvitationHandover]);

  const dirty = enabled !== policy.allowAdminInvitationHandover;

  async function save() {
    if (enabled && reason.trim().length < 3) { setMsg({ text: 'Give a reason for switching this on.', ok: false }); return; }
    setSaving(true); setMsg(null);
    try {
      await platformApi.updateTenantInvitationHandover(tenantId, { enabled, reason: reason.trim() || undefined });
      setReason('');
      setMsg({ text: 'Saved.', ok: true });
      await onSaved();
    } catch (e: unknown) {
      const detail = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setMsg({ text: detail ?? 'Save failed. Please try again.', ok: false });
    } finally { setSaving(false); }
  }

  return (
    <div className="bg-slate-900 border border-white/[0.07] rounded-xl overflow-hidden" data-testid="platform-invitation-handover">
      <div className="px-5 py-3 border-b border-white/[0.06] flex items-center gap-2">
        <Shield className="h-3.5 w-3.5 text-slate-500" />
        <p className="text-caption font-semibold text-slate-500 uppercase tracking-widest">Hand-over of pending invitations</p>
      </div>
      <div className="px-5 py-4 space-y-4">
        <Toggle checked={enabled} onChange={setEnabled} label="Let workspace administrators set a temporary password for someone whose invitation is still pending" />
        <p className="text-xs text-slate-500">
          For people whose invitation email never arrives. The administrator hands the temporary password over in person, the login becomes active, and the person must choose their own password at first sign-in.
          This skips the person&apos;s proof of email ownership, so it is off by default. Each use is recorded in the workspace audit log.
        </p>
        {enabled && !policy.allowAdminInvitationHandover && (
          <label htmlFor="handover-reason" className="block space-y-1.5">
            <span className="block text-xs font-medium text-amber-300">Reason (required, recorded in the audit log)</span>
            <textarea id="handover-reason" rows={2} maxLength={500} value={reason} onChange={e => setReason(e.target.value)}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 resize-none" />
          </label>
        )}
        {msg && <p className={`text-sm ${msg.ok ? 'text-emerald-400' : 'text-rose-400'}`} role="status">{msg.text}</p>}
        <div className="flex justify-end">
          <button type="button" onClick={save} disabled={saving || !dirty}
            className="bg-sapphire hover:bg-blue-500 text-white px-6 py-2 rounded-lg text-sm font-semibold transition-colors disabled:opacity-40">
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}

function fmtDate(iso?: string | null) {
  return iso ? new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : 'not set';
}

function PrivilegedMfaPanel({ tenantId, policy, onSaved }: {
  tenantId: string;
  policy: TenantSecurityPolicy;
  onSaved: () => Promise<void>;
}) {
  const [date, setDate]     = useState('');
  const [reason, setReason] = useState('');
  const [saving, setSaving] = useState(false);
  const [msg, setMsg]       = useState<{ text: string; ok: boolean } | null>(null);

  async function save(clear: boolean) {
    if (reason.trim().length < 10) { setMsg({ text: 'Give a reason of at least 10 characters.', ok: false }); return; }
    if (!clear && !date) { setMsg({ text: 'Pick the date mandatory two-step should start.', ok: false }); return; }
    setSaving(true); setMsg(null);
    try {
      await platformApi.setTenantPrivilegedMfaEnforcement(tenantId, {
        enforceFromUtc: clear ? null : `${date}T00:00:00Z`,
        reason: reason.trim(),
      });
      setReason(''); setDate('');
      setMsg({ text: clear ? 'Now following the platform date.' : 'Saved.', ok: true });
      await onSaved();
    } catch (e: unknown) {
      const detail = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setMsg({ text: detail ?? 'Save failed. Please try again.', ok: false });
    } finally { setSaving(false); }
  }

  return (
    <div className="bg-slate-900 border border-white/[0.07] rounded-xl overflow-hidden" data-testid="platform-privileged-mfa">
      <div className="px-5 py-3 border-b border-white/[0.06] flex items-center gap-2">
        <Shield className="h-3.5 w-3.5 text-slate-500" />
        <p className="text-caption font-semibold text-slate-500 uppercase tracking-widest">Mandatory two-step for payroll, banking and access roles</p>
      </div>
      <div className="px-5 py-4 space-y-4">
        <p className="text-xs text-slate-500">
          People who can see or move payroll, banking or access control must use two-step sign-in from a start date, whatever the policy above says.
          Postponing the start date lets this workspace sign in without it until then (for example during a demo). It never switches two-step off for people who have already set it up.
        </p>
        <dl className="grid grid-cols-2 gap-3 text-sm">
          <div><dt className="text-xs text-slate-500">This workspace starts</dt><dd className="text-slate-200" data-testid="privileged-mfa-tenant-date">{policy.privilegedMfaEnforceFromUtc ? fmtDate(policy.privilegedMfaEnforceFromUtc) : 'follows the platform date'}</dd></div>
          <div><dt className="text-xs text-slate-500">Platform date</dt><dd className="text-slate-200">{fmtDate(policy.platformPrivilegedMfaEnforceFromUtc)}</dd></div>
        </dl>
        <label htmlFor="privileged-mfa-date" className="block space-y-1.5">
          <span className="block text-xs font-medium text-slate-400">New start date</span>
          <input id="privileged-mfa-date" type="date" value={date} onChange={e => setDate(e.target.value)}
            className="bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
        </label>
        <label htmlFor="privileged-mfa-reason" className="block space-y-1.5">
          <span className="block text-xs font-medium text-amber-300">Reason (required, recorded in the audit log)</span>
          <textarea id="privileged-mfa-reason" rows={2} maxLength={500} value={reason} onChange={e => setReason(e.target.value)}
            className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 resize-none" />
        </label>
        {msg && <p className={`text-sm ${msg.ok ? 'text-emerald-400' : 'text-rose-400'}`} role="status">{msg.text}</p>}
        <div className="flex justify-end gap-3">
          {policy.privilegedMfaEnforceFromUtc && (
            <button type="button" onClick={() => save(true)} disabled={saving}
              className="px-4 py-2 rounded-lg text-sm font-medium text-slate-300 border border-white/10 hover:bg-white/[0.04] disabled:opacity-40">
              Follow the platform date
            </button>
          )}
          <button type="button" onClick={() => save(false)} disabled={saving || !date}
            className="bg-sapphire hover:bg-blue-500 text-white px-6 py-2 rounded-lg text-sm font-semibold transition-colors disabled:opacity-40">
            {saving ? 'Saving…' : 'Save start date'}
          </button>
        </div>
      </div>
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
          <h1 className="text-lg font-semibold text-white">Security Policy</h1>
          {policy && <p className="text-xs text-slate-500">{policy.tenantName}</p>}
        </div>
        <div className="ms-auto flex items-center gap-2">
          {policy?.isCustomPolicy && (
            <span className="text-caption font-semibold text-amber-400 bg-amber-500/10 border border-amber-500/20 px-2 py-0.5 rounded">
              Custom Policy
            </span>
          )}
          {!policy?.isCustomPolicy && (
            <span className="text-caption font-semibold text-slate-500 bg-white/[0.04] border border-white/[0.06] px-2 py-0.5 rounded">
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

      {policy && <TwoStepPanel tenantId={id} policy={policy} onSaved={load} />}
      {policy && <InvitationHandoverPanel tenantId={id} policy={policy} onSaved={load} />}
      {policy && <PrivilegedMfaPanel tenantId={id} policy={policy} onSaved={load} />}

      <form onSubmit={save} className="space-y-5">
        {/* Password Policy */}
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          <div className="px-5 py-3 border-b border-white/[0.06] flex items-center gap-2">
            <Shield className="h-3.5 w-3.5 text-slate-500" />
            <p className="text-caption font-semibold text-slate-500 uppercase tracking-widest">Password Policy</p>
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
            <p className="text-caption font-semibold text-slate-500 uppercase tracking-widest">Lockout Policy</p>
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
            <p className="text-caption font-semibold text-slate-500 uppercase tracking-widest">Session Policy</p>
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
          <p className="text-caption text-slate-700">
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
