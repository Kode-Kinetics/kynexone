'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { Plus, RefreshCw, X, UserCog } from 'lucide-react';
import { platformApi, type PlatformTeamMember, PLATFORM_ROLES } from '@/src/api/platform';

const ROLE_BADGE: Record<string, string> = {
  Owner:     'text-amber-300 bg-amber-900/40 border-amber-700/30',
  Admin:     'text-blue-300 bg-blue-900/40 border-blue-700/30',
  Finance:   'text-emerald-300 bg-emerald-900/40 border-emerald-700/30',
  Support:   'text-cyan-300 bg-cyan-900/40 border-cyan-700/30',
  Marketing: 'text-purple-300 bg-purple-900/40 border-purple-700/30',
  Auditor:   'text-slate-300 bg-slate-700/50 border-slate-600',
};

function InviteModal({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const [form, setForm] = useState({ email: '', fullName: '', password: '', role: 'Admin' });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true); setErr('');
    try {
      await platformApi.createTeamMember(form);
      onCreated(); onClose();
    } catch (ex: unknown) {
      setErr((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to create member.');
    } finally { setSaving(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div role="dialog" aria-modal="true" aria-label="Add Platform Team Member" className="relative z-10 w-full max-w-sm bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl overflow-hidden">
        <div className="flex items-center justify-between px-6 py-4 border-b border-white/[0.07]">
          <h2 className="text-sm font-semibold text-white">Add Platform Team Member</h2>
          <button type="button" onClick={onClose} aria-label="Close" className="text-slate-500 hover:text-white transition-colors">
            <X className="h-4 w-4" />
          </button>
        </div>
        <form onSubmit={submit} className="px-6 py-5 space-y-3">
          {err && <div className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2">{err}</div>}
          <div>
            <label htmlFor="invite-email" className="block text-xs text-slate-400 mb-1">Email *</label>
            <input id="invite-email" required type="email" value={form.email} onChange={e => setForm(f => ({ ...f, email: e.target.value }))}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600"
              placeholder="team@company.com" />
          </div>
          <div>
            <label className="block text-xs text-slate-400 mb-1">Full Name</label>
            <input value={form.fullName} onChange={e => setForm(f => ({ ...f, fullName: e.target.value }))}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600"
              placeholder="Jane Smith" />
          </div>
          <div>
            <label className="block text-xs text-slate-400 mb-1">Temp Password *</label>
            <input required type="password" value={form.password} onChange={e => setForm(f => ({ ...f, password: e.target.value }))}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60"
              placeholder="••••••••" />
          </div>
          <div>
            <label className="block text-xs text-slate-400 mb-1">Role</label>
            <select aria-label="Role" value={form.role} onChange={e => setForm(f => ({ ...f, role: e.target.value }))}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60">
              {PLATFORM_ROLES.filter(r => r !== 'Owner').map(r => (
                <option key={r} value={r}>{r}</option>
              ))}
            </select>
          </div>
          <div className="flex gap-3 pt-1">
            <button type="button" onClick={onClose}
              className="flex-1 border border-white/10 text-slate-400 rounded-lg py-2 text-sm transition-colors">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex-1 bg-sapphire text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
              {saving ? 'Creating…' : 'Add Member'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

export default function PlatformTeamPage() {
  const router = useRouter();
  const [members, setMembers] = useState<PlatformTeamMember[]>([]);
  const [loading, setLoading] = useState(true);
  const [showInvite, setShowInvite] = useState(false);
  const [updating, setUpdating] = useState<string | null>(null);
  const [msg, setMsg] = useState<{ text: string; ok: boolean } | null>(null);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    try { setMembers(await platformApi.listTeam()); }
    finally { setLoading(false); }
  }, []);

  async function toggleActive(m: PlatformTeamMember) {
    if (m.role === 'Owner') { setMsg({ text: 'Cannot deactivate the Owner account.', ok: false }); return; }
    setUpdating(m.id);
    try {
      await platformApi.updateTeamMember(m.id, { isActive: !m.isActive });
      setMsg({ text: `${m.fullName} ${m.isActive ? 'deactivated' : 'reactivated'}.`, ok: true });
      await load();
    } catch { setMsg({ text: 'Update failed.', ok: false }); }
    finally { setUpdating(null); }
  }

  async function changeRole(m: PlatformTeamMember, role: string) {
    if (m.role === 'Owner') { setMsg({ text: 'Cannot change the Owner role.', ok: false }); return; }
    setUpdating(m.id);
    try {
      await platformApi.updateTeamMember(m.id, { role });
      await load();
    } catch { setMsg({ text: 'Role update failed.', ok: false }); }
    finally { setUpdating(null); }
  }

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Platform Team</h1>
          <p className="text-xs text-slate-500 mt-0.5">
            {members.length} member{members.length !== 1 ? 's' : ''} · Platform users are separate from all tenant company users
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button type="button" onClick={load} disabled={loading} aria-label="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
          <button type="button" onClick={() => setShowInvite(true)}
            className="flex items-center gap-1.5 bg-sapphire hover:bg-blue-500 text-white px-3 py-1.5 rounded-lg text-sm font-semibold transition-colors">
            <Plus className="h-3.5 w-3.5" />
            Add Member
          </button>
        </div>
      </div>

      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" aria-label="Dismiss" onClick={() => setMsg(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}

      {/* Role legend */}
      <div className="flex flex-wrap gap-2">
        {PLATFORM_ROLES.map(role => (
          <span key={role} className={`text-[10px] font-semibold uppercase tracking-wider px-2 py-0.5 rounded border ${ROLE_BADGE[role] ?? ''}`}>
            {role}
          </span>
        ))}
      </div>

      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-10">
            <div className="h-4 w-4 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
          </div>
        ) : members.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-10 gap-3">
            <UserCog className="h-8 w-8 text-slate-700" />
            <p className="text-sm text-slate-600">No platform team members yet.</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[650px]">
              <thead>
                <tr className="border-b border-white/[0.06]">
                  {['Member', 'Role', 'Status', 'Last Login', ''].map(h => (
                    <th key={h} className="px-4 py-2.5 text-start text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {members.map(m => (
                  <tr key={m.id} className={`border-b border-white/[0.04] last:border-0 hover:bg-white/[0.02] transition-colors ${!m.isActive ? 'opacity-40' : ''}`}>
                    <td className="px-4 py-3">
                      <div className="flex items-center gap-3">
                        <div className="h-7 w-7 rounded-full bg-slate-800 border border-white/10 flex items-center justify-center shrink-0">
                          <span className="text-[10px] font-bold text-slate-400">{m.fullName.slice(0, 2).toUpperCase()}</span>
                        </div>
                        <div className="min-w-0">
                          <p className="text-sm text-white font-medium">{m.fullName}</p>
                          <p className="text-[11px] text-slate-600">{m.email}</p>
                        </div>
                      </div>
                    </td>
                    <td className="px-3 py-3">
                      {m.role === 'Owner' ? (
                        <span className={`text-[10px] font-semibold uppercase tracking-wider px-1.5 py-0.5 rounded border ${ROLE_BADGE[m.role]}`}>
                          {m.role}
                        </span>
                      ) : (
                        <select aria-label={`Role for ${m.fullName}`} value={m.role} disabled={updating === m.id}
                          onChange={e => changeRole(m, e.target.value)}
                          className="bg-white/[0.04] border border-white/[0.08] rounded px-2 py-1 text-xs text-slate-300 focus:outline-none">
                          {PLATFORM_ROLES.filter(r => r !== 'Owner').map(r => (
                            <option key={r} value={r}>{r}</option>
                          ))}
                        </select>
                      )}
                    </td>
                    <td className="px-3 py-3">
                      <span className={`text-xs ${m.isActive ? 'text-emerald-400' : 'text-rose-400'}`}>
                        {m.isActive ? 'Active' : 'Deactivated'}
                      </span>
                    </td>
                    <td className="px-3 py-3 text-[11px] text-slate-600 whitespace-nowrap">
                      {m.lastLoginAtUtc
                        ? new Date(m.lastLoginAtUtc).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })
                        : '—'}
                      {m.lastLoginIp && <span className="text-slate-700 font-mono ms-1">· {m.lastLoginIp}</span>}
                    </td>
                    <td className="px-3 py-3">
                      {m.role !== 'Owner' && (
                        <button type="button"
                          onClick={() => toggleActive(m)}
                          disabled={updating === m.id}
                          className={`text-[11px] border px-2 py-1 rounded transition-colors disabled:opacity-40 ${
                            m.isActive
                              ? 'text-rose-400 border-rose-500/20 hover:border-rose-500/40'
                              : 'text-emerald-400 border-emerald-500/20 hover:border-emerald-500/40'
                          }`}>
                          {updating === m.id ? '…' : m.isActive ? 'Deactivate' : 'Reactivate'}
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {showInvite && <InviteModal onClose={() => setShowInvite(false)} onCreated={load} />}
    </div>
  );
}
