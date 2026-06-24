'use client';

import { useState, useEffect, useCallback } from 'react';
import { useParams, useRouter } from 'next/navigation';
import Link from 'next/link';
import {
  ArrowLeft, Search, RefreshCw, UserCog, Key, X, Shield,
  LogOut, Unlock, ShieldOff, Edit2, Mail, Check, AlertTriangle,
  ChevronDown, Eye, EyeOff,
} from 'lucide-react';
import { platformApi, type TenantUser, type TenantRole } from '@/src/api/platform';

// ── types ─────────────────────────────────────────────────────────────────────

type Toast = { text: string; ok: boolean };

type ManagePanel =
  | { kind: 'edit' }
  | { kind: 'set-password' }
  | { kind: 'send-reset' }
  | null;

// ── helpers ───────────────────────────────────────────────────────────────────

const statusBadge = (u: TenantUser) => {
  if (u.isLocked) return 'bg-rose-500/10 text-rose-400 border-rose-500/20';
  if (!u.isActive) return 'bg-slate-500/10 text-slate-500 border-slate-500/20';
  if (u.status === 'Active') return 'bg-emerald-500/10 text-emerald-400 border-emerald-500/20';
  return 'bg-amber-500/10 text-amber-400 border-amber-500/20';
};

const statusLabel = (u: TenantUser) => (u.isLocked ? 'Locked' : u.status);

// ── component ─────────────────────────────────────────────────────────────────

export default function TenantUsersPage() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();

  const [users, setUsers]     = useState<TenantUser[]>([]);
  const [roles, setRoles]     = useState<TenantRole[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch]   = useState('');
  const [toast, setToast]     = useState<Toast | null>(null);

  const [selected, setSelected]   = useState<TenantUser | null>(null);
  const [panel, setPanel]         = useState<ManagePanel>(null);
  const [saving, setSaving]       = useState(false);

  // edit form
  const [editForm, setEditForm] = useState({ fullName: '', email: '', status: '', roleName: '' });

  // set-password form
  const [newPwd, setNewPwd]   = useState('');
  const [showPwd, setShowPwd] = useState(false);

  // ── auth guard ──────────────────────────────────────────────────────────────

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    Promise.all([load(), platformApi.listTenantRoles(id).then(setRoles).catch(e => console.warn('Failed to load tenant roles for the role dropdown:', e))]);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  // ── data load ───────────────────────────────────────────────────────────────

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const data = await platformApi.listTenantUsers(id, search || undefined);
      setUsers(data);
    } finally {
      setLoading(false);
    }
  }, [id, search]);

  // ── notify helper ────────────────────────────────────────────────────────────

  function notify(text: string, ok = true) {
    setToast({ text, ok });
    setTimeout(() => setToast(null), 4000);
  }

  // ── open manage panel ────────────────────────────────────────────────────────

  function openManage(user: TenantUser) {
    setSelected(user);
    setEditForm({
      fullName: user.fullName,
      email: user.email,
      status: user.status,
      roleName: user.roles[0] ?? '',
    });
    setNewPwd('');
    setPanel({ kind: 'edit' });
  }

  function closePanel() {
    setSelected(null);
    setPanel(null);
    setNewPwd('');
    setShowPwd(false);
  }

  // ── actions ──────────────────────────────────────────────────────────────────

  async function saveEdit() {
    if (!selected) return;
    setSaving(true);
    try {
      await platformApi.editUser(selected.id, {
        fullName: editForm.fullName || undefined,
        email: editForm.email || undefined,
        status: editForm.status || undefined,
        roleName: editForm.roleName || undefined,
      });
      notify('User updated successfully.');
      closePanel();
      await load();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      notify(msg || 'Failed to update user.', false);
    } finally { setSaving(false); }
  }

  async function setPassword() {
    if (!selected) return;
    if (!newPwd || newPwd.length < 10) { notify('Password must be at least 10 characters.', false); return; }
    setSaving(true);
    try {
      await platformApi.forcePasswordReset(selected.id, newPwd);
      notify('Password set. User must change it on next login. All sessions revoked.');
      closePanel();
      await load();
    } catch { notify('Failed to set password.', false); }
    finally { setSaving(false); }
  }

  async function sendReset() {
    if (!selected) return;
    setSaving(true);
    try {
      const r = await platformApi.sendPasswordReset(selected.id);
      notify(r.emailSent ? `Reset email sent to ${r.userEmail}.` : 'Reset token created. SMTP not configured — share reset link manually.');
      closePanel();
    } catch { notify('Failed to send reset.', false); }
    finally { setSaving(false); }
  }

  async function unlock(u: TenantUser) {
    try {
      await platformApi.unlockUser(u.id);
      notify(`${u.fullName} unlocked.`);
      await load();
    } catch { notify('Unlock failed.', false); }
  }

  async function disableMfa(u: TenantUser) {
    try {
      await platformApi.disableMfa(u.id);
      notify(`MFA disabled for ${u.fullName}.`);
      await load();
    } catch { notify('Failed to disable MFA.', false); }
  }

  async function revokeSessions(u: TenantUser) {
    try {
      const r = await platformApi.revokeSessions(u.id) as { sessionsRevoked: number };
      notify(`${r.sessionsRevoked} session(s) revoked for ${u.fullName}.`);
    } catch { notify('Failed to revoke sessions.', false); }
  }

  async function impersonate(u: TenantUser) {
    try {
      const { token } = await platformApi.impersonate(id, u.id);
      window.open(`${window.location.origin}/login?impersonate=${token}`, '_blank');
      notify(`Impersonating ${u.email} — new tab opened.`);
    } catch { notify('Impersonation failed.', false); }
  }

  // ── render ───────────────────────────────────────────────────────────────────

  return (
    <div className="space-y-5">

      {/* header */}
      <div className="flex items-center gap-3">
        <Link href={`/platform/tenants/${id}`}
          className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors shrink-0">
          <ArrowLeft className="h-4 w-4" />
        </Link>
        <h1 className="text-lg font-bold text-white">Tenant Users</h1>
      </div>

      {/* toast */}
      {toast && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm
          ${toast.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          <div className="flex items-center gap-2">
            {toast.ok ? <Check className="h-3.5 w-3.5 shrink-0" /> : <AlertTriangle className="h-3.5 w-3.5 shrink-0" />}
            {toast.text}
          </div>
          <button type="button" title="Dismiss" onClick={() => setToast(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}

      {/* search + refresh */}
      <div className="flex items-center gap-3">
        <div className="relative flex-1 max-w-xs">
          <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-600 pointer-events-none" />
          <input
            type="text" value={search} onChange={e => setSearch(e.target.value)} onKeyDown={e => e.key === 'Enter' && load()}
            placeholder="Search by name or email…"
            className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg pl-8 pr-3 py-1.5 text-sm text-slate-300 placeholder-slate-600 focus:outline-none focus:border-sapphire/60 transition-colors" />
        </div>
        <button type="button" title="Refresh" onClick={load} disabled={loading}
          className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
        </button>
      </div>

      {/* user table */}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-10">
            <div className="h-4 w-4 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
          </div>
        ) : users.length === 0 ? (
          <p className="text-sm text-slate-600 text-center py-10">No users found.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[700px]">
              <thead>
                <tr className="border-b border-white/[0.06]">
                  {['User', 'Roles', 'Status', 'Flags', 'Joined', ''].map(h => (
                    <th key={h} className="px-4 py-2.5 text-left text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {users.map(u => (
                  <tr key={u.id} className="border-b border-white/[0.04] last:border-0 hover:bg-white/[0.02] transition-colors">
                    <td className="px-4 py-3">
                      <p className="text-sm text-white font-medium">{u.fullName}</p>
                      <p className="text-[11px] text-slate-600">{u.email}</p>
                    </td>
                    <td className="px-3 py-3">
                      <div className="flex flex-wrap gap-1">
                        {(u.roles ?? []).slice(0, 3).map(r => (
                          <span key={r} className="text-[10px] bg-white/[0.05] text-slate-400 px-1.5 py-0.5 rounded">{r}</span>
                        ))}
                        {(u.roles ?? []).length > 3 && (
                          <span className="text-[10px] text-slate-600">+{u.roles.length - 3}</span>
                        )}
                      </div>
                    </td>
                    <td className="px-3 py-3">
                      <span className={`text-[10px] font-medium px-1.5 py-0.5 rounded border ${statusBadge(u)}`}>
                        {statusLabel(u)}
                      </span>
                    </td>
                    <td className="px-3 py-3">
                      <div className="flex items-center gap-1">
                        {u.mFAEnabled && (
                          <span title="MFA enabled" className="text-[10px] bg-blue-500/10 text-blue-400 border border-blue-500/20 px-1.5 py-0.5 rounded">MFA</span>
                        )}
                        {u.mustChangePassword && (
                          <span title="Must change password" className="text-[10px] bg-amber-500/10 text-amber-400 border border-amber-500/20 px-1.5 py-0.5 rounded">PWD</span>
                        )}
                      </div>
                    </td>
                    <td className="px-3 py-3 text-xs text-slate-600">
                      {new Date(u.createdAtUtc).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}
                    </td>
                    <td className="px-3 py-3">
                      <div className="flex items-center gap-1 justify-end">
                        <button type="button" onClick={() => openManage(u)}
                          className="flex items-center gap-1 text-[11px] text-sapphire border border-sapphire/20 hover:border-sapphire/50 px-2 py-1 rounded transition-colors">
                          <Edit2 className="h-3 w-3" />
                          Manage
                        </button>
                        <button type="button" onClick={() => impersonate(u)}
                          className="flex items-center gap-1 text-[11px] text-blue-400 border border-blue-500/20 hover:border-blue-500/40 px-2 py-1 rounded transition-colors">
                          <UserCog className="h-3 w-3" />
                          Login As
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* ── Manage Panel (slide-in) ────────────────────────────────────────────── */}
      {selected && panel && (
        <div className="fixed inset-0 z-50 flex">
          {/* backdrop */}
          <div className="flex-1 bg-black/60 backdrop-blur-sm" onClick={closePanel} />

          {/* drawer */}
          <div className="w-full max-w-md bg-[#0d1117] border-l border-white/[0.08] flex flex-col overflow-y-auto">
            <div className="flex items-center justify-between px-5 py-4 border-b border-white/[0.07]">
              <div>
                <p className="text-sm font-semibold text-white">{selected.fullName}</p>
                <p className="text-[11px] text-slate-600">{selected.email}</p>
              </div>
              <button type="button" title="Close panel" onClick={closePanel} className="h-7 w-7 flex items-center justify-center rounded-lg hover:bg-white/[0.06] text-slate-500 hover:text-white transition-colors">
                <X className="h-4 w-4" />
              </button>
            </div>

            {/* tab bar */}
            <div className="flex gap-0.5 px-5 pt-4 border-b border-white/[0.06] pb-0">
              {[
                { kind: 'edit' as const, label: 'Edit Profile', icon: Edit2 },
                { kind: 'set-password' as const, label: 'Set Password', icon: Key },
                { kind: 'send-reset' as const, label: 'Send Reset', icon: Mail },
              ].map(tab => (
                <button key={tab.kind} type="button" onClick={() => setPanel({ kind: tab.kind })}
                  className={`flex items-center gap-1.5 text-xs px-3 py-2 rounded-t-lg border-b-2 transition-colors
                    ${panel.kind === tab.kind
                      ? 'text-white border-sapphire bg-white/[0.04]'
                      : 'text-slate-500 border-transparent hover:text-slate-300'}`}>
                  <tab.icon className="h-3 w-3" />
                  {tab.label}
                </button>
              ))}
            </div>

            <div className="flex-1 px-5 py-5 space-y-4">

              {/* ── Edit Profile ─────────────────────────────────────────── */}
              {panel.kind === 'edit' && (
                <>
                  <div className="space-y-3">
                    <Field label="Full Name">
                      <input value={editForm.fullName} onChange={e => setEditForm(f => ({ ...f, fullName: e.target.value }))}
                        placeholder="Full name" title="Full name" className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
                    </Field>
                    <Field label="Email Address">
                      <input value={editForm.email} onChange={e => setEditForm(f => ({ ...f, email: e.target.value }))}
                        placeholder="Email address" title="Email address" className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" type="email" />
                    </Field>
                    <Field label="Status">
                      <div className="relative">
                        <select value={editForm.status} onChange={e => setEditForm(f => ({ ...f, status: e.target.value }))}
                          title="Account status" className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 appearance-none pr-7">
                          <option value="Active">Active</option>
                          <option value="Suspended">Suspended</option>
                          <option value="Deactivated">Deactivated</option>
                          <option value="Invited">Invited</option>
                          <option value="PendingPasswordSetup">Pending Password Setup</option>
                        </select>
                        <ChevronDown className="absolute right-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-600 pointer-events-none" />
                      </div>
                    </Field>
                    {roles.length > 0 && (
                      <Field label="Role">
                        <div className="relative">
                          <select value={editForm.roleName} onChange={e => setEditForm(f => ({ ...f, roleName: e.target.value }))}
                            title="Assign role" className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 appearance-none pr-7">
                            <option value="">— no change —</option>
                            {roles.map(r => <option key={r.id} value={r.name}>{r.name}</option>)}
                          </select>
                          <ChevronDown className="absolute right-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-600 pointer-events-none" />
                        </div>
                      </Field>
                    )}
                  </div>

                  <button type="button" onClick={saveEdit} disabled={saving}
                    className="w-full flex items-center justify-center gap-2 bg-sapphire/90 hover:bg-sapphire text-white text-sm font-medium py-2 rounded-lg transition-colors disabled:opacity-50">
                    {saving ? <RefreshCw className="h-3.5 w-3.5 animate-spin" /> : <Check className="h-3.5 w-3.5" />}
                    Save Changes
                  </button>
                </>
              )}

              {/* ── Set Password ─────────────────────────────────────────── */}
              {panel.kind === 'set-password' && (
                <>
                  <p className="text-xs text-slate-500">Directly set a temporary password. The user will be forced to change it on next login and all current sessions will be revoked.</p>
                  <Field label="New Temporary Password">
                    <div className="relative">
                      <input
                        type={showPwd ? 'text' : 'password'}
                        value={newPwd} onChange={e => setNewPwd(e.target.value)}
                        placeholder="Min. 10 characters"
                        className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 pr-8" />
                      <button type="button" title={showPwd ? 'Hide password' : 'Show password'} onClick={() => setShowPwd(v => !v)}
                        className="absolute right-2.5 top-1/2 -translate-y-1/2 text-slate-600 hover:text-slate-300 transition-colors">
                        {showPwd ? <EyeOff className="h-3.5 w-3.5" /> : <Eye className="h-3.5 w-3.5" />}
                      </button>
                    </div>
                  </Field>
                  <button type="button" onClick={setPassword} disabled={saving || newPwd.length < 10}
                    className="w-full flex items-center justify-center gap-2 bg-amber-600/80 hover:bg-amber-600 text-white text-sm font-medium py-2 rounded-lg transition-colors disabled:opacity-50">
                    {saving ? <RefreshCw className="h-3.5 w-3.5 animate-spin" /> : <Key className="h-3.5 w-3.5" />}
                    Set Password &amp; Revoke Sessions
                  </button>
                </>
              )}

              {/* ── Send Reset Email ─────────────────────────────────────── */}
              {panel.kind === 'send-reset' && (
                <>
                  <p className="text-xs text-slate-500">Generate a password reset link and email it to <span className="text-white">{selected.email}</span>. If SMTP is not configured, the token is saved and you can share the link manually.</p>
                  <button type="button" onClick={sendReset} disabled={saving}
                    className="w-full flex items-center justify-center gap-2 bg-blue-600/80 hover:bg-blue-600 text-white text-sm font-medium py-2 rounded-lg transition-colors disabled:opacity-50">
                    {saving ? <RefreshCw className="h-3.5 w-3.5 animate-spin" /> : <Mail className="h-3.5 w-3.5" />}
                    Send Password Reset Email
                  </button>
                </>
              )}

              {/* ── Quick Actions ─────────────────────────────────────────── */}
              <div className="border-t border-white/[0.06] pt-4 space-y-2">
                <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-wider">Quick Actions</p>

                {selected.isLocked && (
                  <ActionButton icon={Unlock} label="Unlock Account" desc="Clear lockout and re-activate login"
                    color="text-emerald-400 border-emerald-500/20 hover:border-emerald-500/40"
                    onClick={() => { unlock(selected); closePanel(); }} />
                )}

                {selected.mFAEnabled && (
                  <ActionButton icon={ShieldOff} label="Disable MFA" desc="Remove MFA requirement from this user"
                    color="text-amber-400 border-amber-500/20 hover:border-amber-500/40"
                    onClick={() => { disableMfa(selected); closePanel(); }} />
                )}

                <ActionButton icon={LogOut} label="Revoke All Sessions" desc="Force logout from all devices immediately"
                  color="text-rose-400 border-rose-500/20 hover:border-rose-500/40"
                  onClick={() => { revokeSessions(selected); closePanel(); }} />

                <ActionButton icon={UserCog} label="Login As User" desc="Impersonate in a new tab (support access)"
                  color="text-blue-400 border-blue-500/20 hover:border-blue-500/40"
                  onClick={() => { impersonate(selected); closePanel(); }} />

                <ActionButton icon={Shield} label="View Audit Logs" desc="See all actions for this user"
                  color="text-slate-400 border-slate-500/20 hover:border-slate-500/40"
                  onClick={() => { router.push(`/platform/tenants/${id}/audit`); closePanel(); }} />
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// ── sub-components ────────────────────────────────────────────────────────────

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="space-y-1">
      <label className="text-[10px] font-semibold text-slate-500 uppercase tracking-wider">{label}</label>
      {children}
    </div>
  );
}

function ActionButton({ icon: Icon, label, desc, color, onClick }: {
  icon: React.ElementType; label: string; desc: string; color: string; onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick}
      className={`w-full flex items-start gap-3 p-3 rounded-lg border bg-white/[0.02] hover:bg-white/[0.04] transition-colors ${color}`}>
      <Icon className="h-4 w-4 shrink-0 mt-0.5" />
      <div className="text-left">
        <p className="text-xs font-medium">{label}</p>
        <p className="text-[10px] text-slate-600">{desc}</p>
      </div>
    </button>
  );
}
