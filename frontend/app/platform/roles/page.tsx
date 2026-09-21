'use client';

import { useCallback, useEffect, useState } from 'react';
import { Shield, Plus, UserX, RefreshCw } from 'lucide-react';
import { platformApi, PLATFORM_ROLES, type PlatformTeamMember } from '@/src/api/platform';

// Actual RBAC matrix — mirrors RequirePlatformRoleAttribute on PlatformController
const MATRIX: { category: string; actions: { label: string; roles: string[] }[] }[] = [
  {
    category: 'Dashboard & Stats',
    actions: [
      { label: 'View stats / metrics', roles: ['Owner', 'Admin', 'Finance', 'Support', 'Auditor'] },
      { label: 'System health', roles: ['Owner', 'Admin', 'Support', 'Auditor'] },
    ],
  },
  {
    category: 'Tenant Management',
    actions: [
      { label: 'List / view tenants', roles: ['Owner', 'Admin', 'Finance', 'Support', 'Auditor'] },
      { label: 'Create tenant', roles: ['Owner', 'Admin'] },
      { label: 'Edit tenant name', roles: ['Owner', 'Admin'] },
      { label: 'Suspend / reactivate', roles: ['Owner', 'Admin'] },
      { label: 'Toggle feature flags', roles: ['Owner', 'Admin'] },
      { label: 'Impersonate user', roles: ['Owner', 'Admin', 'Support'] },
    ],
  },
  {
    category: 'Billing & Invoices',
    actions: [
      { label: 'View subscription', roles: ['Owner', 'Admin', 'Finance'] },
      { label: 'Update subscription', roles: ['Owner', 'Admin', 'Finance'] },
      { label: 'View / create invoices', roles: ['Owner', 'Admin', 'Finance'] },
      { label: 'Download PDF / send email', roles: ['Owner', 'Admin', 'Finance'] },
      { label: 'Delete invoice', roles: ['Owner', 'Admin', 'Finance'] },
      { label: 'View / set plan pricing', roles: ['Owner', 'Admin', 'Finance', 'Auditor'] },
      { label: 'Update plan pricing', roles: ['Owner', 'Admin', 'Finance'] },
      { label: 'Billing summary', roles: ['Owner', 'Admin', 'Finance'] },
    ],
  },
  {
    category: 'Platform Team',
    actions: [
      { label: 'View team members', roles: ['Owner', 'Admin', 'Support'] },
      { label: 'Invite team member', roles: ['Owner', 'Admin'] },
      { label: 'Change role', roles: ['Owner', 'Admin'] },
      { label: 'Deactivate member', roles: ['Owner'] },
    ],
  },
  {
    category: 'Support & Users',
    actions: [
      { label: 'View tenant users', roles: ['Owner', 'Admin', 'Support'] },
      { label: 'View tenant admins', roles: ['Owner', 'Admin', 'Support'] },
      { label: 'Edit user profile (name, email, status, role)', roles: ['Owner', 'Admin'] },
      { label: 'Send password reset email', roles: ['Owner', 'Admin', 'Support'] },
      { label: 'Force password reset (set temp password)', roles: ['Owner', 'Admin'] },
      { label: 'Unlock locked account', roles: ['Owner', 'Admin', 'Support'] },
      { label: 'Disable MFA for user', roles: ['Owner', 'Admin'] },
      { label: 'Revoke all sessions for user', roles: ['Owner', 'Admin', 'Support'] },
      { label: 'Start support session', roles: ['Owner', 'Admin', 'Support'] },
      { label: 'End support session', roles: ['Owner', 'Admin', 'Support'] },
      { label: 'View support sessions', roles: ['Owner', 'Admin', 'Support', 'Auditor'] },
    ],
  },
  {
    category: 'Branding & Localization',
    actions: [
      { label: 'Update tenant branding (logo, colors, title)', roles: ['Owner', 'Admin'] },
      { label: 'Update tenant localization (language, timezone, calendar)', roles: ['Owner', 'Admin'] },
      { label: 'Delete / deactivate tenant', roles: ['Owner'] },
      { label: 'List tenant roles', roles: ['Owner', 'Admin', 'Support'] },
    ],
  },
  {
    category: 'Audit & Compliance',
    actions: [
      { label: 'View audit logs', roles: ['Owner', 'Admin', 'Support', 'Auditor'] },
      { label: 'View AI usage', roles: ['Owner', 'Admin', 'Auditor'] },
      { label: 'View app version', roles: ['Owner', 'Admin', 'Auditor'] },
    ],
  },
  {
    category: 'Marketing & Leads',
    actions: [
      { label: 'View announcements', roles: ['Owner', 'Admin', 'Marketing'] },
      { label: 'Create / edit / publish', roles: ['Owner', 'Admin', 'Marketing'] },
      { label: 'Archive announcement', roles: ['Owner', 'Admin', 'Marketing'] },
      { label: 'View leads', roles: ['Owner', 'Admin', 'Marketing'] },
      { label: 'Create / advance leads', roles: ['Owner', 'Admin', 'Marketing'] },
      { label: 'Convert lead to tenant', roles: ['Owner', 'Admin', 'Marketing'] },
    ],
  },
  {
    category: 'Settings',
    actions: [
      { label: 'View SMTP / platform settings', roles: ['Owner', 'Admin'] },
      { label: 'Update SMTP settings', roles: ['Owner', 'Admin'] },
      { label: 'Send test email', roles: ['Owner', 'Admin'] },
      { label: 'View diagnostics', roles: ['Owner', 'Admin'] },
      { label: 'Toggle maintenance mode', roles: ['Owner', 'Admin'] },
    ],
  },
  {
    category: 'Compliance',
    actions: [
      { label: 'View compliance controls & incidents', roles: ['Owner', 'Admin', 'Auditor'] },
      { label: 'Update compliance control status', roles: ['Owner', 'Admin'] },
      { label: 'Log security incident', roles: ['Owner', 'Admin', 'Support'] },
      { label: 'Update / resolve incident', roles: ['Owner', 'Admin'] },
    ],
  },
];

const ROLE_CLS: Record<string, string> = {
  Owner:     'bg-amber-500/20 text-amber-300',
  Admin:     'bg-blue-500/20 text-blue-300',
  Finance:   'bg-emerald-500/20 text-emerald-300',
  Support:   'bg-cyan-500/20 text-cyan-300',
  Marketing: 'bg-purple-500/20 text-purple-300',
  Auditor:   'bg-slate-600/50 text-slate-400',
};

type Tab = 'users' | 'matrix';

export default function PlatformRolesPage() {
  const [tab, setTab] = useState<Tab>('users');
  const [members, setMembers] = useState<PlatformTeamMember[]>([]);
  const [loadingTeam, setLoadingTeam] = useState(false);
  const [teamErr, setTeamErr] = useState('');
  const [actionMsg, setActionMsg] = useState('');

  // Invite modal
  const [showInvite, setShowInvite] = useState(false);
  const [invEmail, setInvEmail] = useState('');
  const [invName, setInvName] = useState('');
  const [invRole, setInvRole] = useState('Admin');
  const [invPassword, setInvPassword] = useState('');
  const [inviting, setInviting] = useState(false);

  const loadTeam = useCallback(async () => {
    setLoadingTeam(true);
    setTeamErr('');
    try {
      const data = await platformApi.listTeam();
      setMembers(data);
    } catch {
      setTeamErr('Could not load platform team.');
    } finally {
      setLoadingTeam(false);
    }
  }, []);

  useEffect(() => { loadTeam(); }, [loadTeam]);

  const invite = async () => {
    if (!invEmail.trim() || !invPassword.trim()) return;
    setInviting(true);
    try {
      await platformApi.createTeamMember({ email: invEmail.trim(), fullName: invName.trim(), role: invRole, password: invPassword });
      setShowInvite(false);
      setInvEmail(''); setInvName(''); setInvRole('Admin'); setInvPassword('');
      setActionMsg('Team member invited.');
      await loadTeam();
    } catch {
      setActionMsg('Could not invite member — check that the email is unique.');
    } finally {
      setInviting(false);
    }
  };

  const deactivate = async (id: string, name: string) => {
    if (!confirm(`Deactivate ${name}? They will lose platform access immediately.`)) return;
    try {
      await platformApi.deactivateTeamMember(id);
      setActionMsg(`${name} deactivated.`);
      await loadTeam();
    } catch {
      setActionMsg('Could not deactivate member.');
    }
  };

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Roles & Permissions</h1>
          <p className="text-xs text-slate-500 mt-0.5">
            Enforced by <span className="font-mono text-slate-400">RequirePlatformRoleAttribute</span> on every API endpoint.
          </p>
        </div>
        {tab === 'users' && (
          <button
            type="button"
            onClick={() => setShowInvite(true)}
            className="flex items-center gap-1.5 rounded-lg bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-500"
          >
            <Plus className="h-4 w-4" /> Invite Member
          </button>
        )}
      </div>

      {actionMsg && (
        <div className="rounded-lg border border-blue-500/20 bg-blue-500/10 px-4 py-2.5 text-sm text-blue-300">{actionMsg}</div>
      )}

      {/* Legend */}
      <div className="flex flex-wrap gap-2">
        {PLATFORM_ROLES.map(role => (
          <div key={role} className={`flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-semibold ${ROLE_CLS[role] ?? ''}`}>
            <Shield className="h-3 w-3" />
            {role}
          </div>
        ))}
      </div>

      {/* Tab bar */}
      <div className="flex gap-1 border-b border-white/[0.07]">
        {(['users', 'matrix'] as Tab[]).map(t => (
          <button
            key={t}
            type="button"
            onClick={() => setTab(t)}
            className={`px-4 py-2 text-sm font-medium transition-colors ${tab === t ? 'border-b-2 border-blue-500 text-white' : 'text-slate-400 hover:text-white'}`}
          >
            {t === 'users' ? 'Platform Users' : 'Permission Matrix'}
          </button>
        ))}
      </div>

      {/* Platform Users tab */}
      {tab === 'users' && (
        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          <div className="flex items-center justify-between px-4 py-3 border-b border-white/[0.06]">
            <p className="text-xs font-semibold uppercase tracking-widest text-slate-500">Team Members</p>
            <button type="button" aria-label="Refresh team" onClick={loadTeam} disabled={loadingTeam} className="text-slate-600 hover:text-white">
              <RefreshCw className={`h-3.5 w-3.5 ${loadingTeam ? 'animate-spin' : ''}`} />
            </button>
          </div>
          {teamErr && <p className="px-4 py-3 text-sm text-red-400">{teamErr}</p>}
          {!teamErr && (
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-white/[0.05]">
                  {['Name', 'Email', 'Role', 'Status', ''].map(h => (
                    <th key={h} className="px-4 py-2 text-start text-xs font-semibold uppercase text-slate-500">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-white/[0.04]">
                {loadingTeam && (
                  <tr><td colSpan={5} className="px-4 py-4 text-center text-sm text-slate-500">Loading…</td></tr>
                )}
                {!loadingTeam && members.length === 0 && (
                  <tr><td colSpan={5} className="px-4 py-4 text-center text-sm text-slate-500">No team members found.</td></tr>
                )}
                {members.map(m => (
                  <tr key={m.id} className="hover:bg-white/[0.02]">
                    <td className="px-4 py-3 font-medium text-white">{m.fullName}</td>
                    <td className="px-4 py-3 text-slate-400">{m.email}</td>
                    <td className="px-4 py-3">
                      <span className={`text-[10px] font-semibold px-2 py-0.5 rounded ${ROLE_CLS[m.role] ?? 'bg-slate-700/50 text-slate-400'}`}>{m.role}</span>
                    </td>
                    <td className="px-4 py-3">
                      <span className={`text-xs ${m.isActive ? 'text-emerald-400' : 'text-slate-500'}`}>
                        {m.isActive ? 'Active' : 'Inactive'}
                      </span>
                    </td>
                    <td className="px-4 py-3">
                      {m.isActive && (
                        <button
                          type="button"
                          onClick={() => deactivate(m.id, m.fullName)}
                          className="flex items-center gap-1 text-xs text-red-400 hover:text-red-300"
                        >
                          <UserX className="h-3.5 w-3.5" /> Deactivate
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {/* Permission Matrix tab */}
      {tab === 'matrix' && (
        <div className="space-y-4">
          {MATRIX.map(section => (
            <div key={section.category} className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
              <div className="px-4 py-2.5 bg-white/[0.02] border-b border-white/[0.06]">
                <p className="text-[11px] font-semibold text-slate-500 uppercase tracking-widest">{section.category}</p>
              </div>
              <div className="divide-y divide-white/[0.04]">
                {section.actions.map(action => (
                  <div key={action.label} className="flex items-center gap-4 px-4 py-2.5">
                    <span className="text-sm text-slate-300 flex-1 min-w-0">{action.label}</span>
                    <div className="flex flex-wrap gap-1 justify-end">
                      {action.roles.map(role => (
                        <span key={role} className={`text-[10px] font-semibold px-1.5 py-0.5 rounded ${ROLE_CLS[role] ?? 'text-slate-400 bg-slate-700/50'}`}>
                          {role}
                        </span>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          ))}
          <p className="text-[11px] text-slate-700">
            Owner is always allowed regardless of the roles listed. Role is read from the <span className="font-mono">platform_role</span> JWT claim issued at login.
          </p>
        </div>
      )}

      {/* Invite modal */}
      {showInvite && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
          <div className="w-full max-w-md rounded-xl border border-white/[0.1] bg-[#0d1117] p-6 space-y-4">
            <h2 className="font-bold text-white">Invite Platform Team Member</h2>
            <label className="block">
              <span className="text-xs text-slate-400">Full name</span>
              <input value={invName} onChange={e => setInvName(e.target.value)} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white" placeholder="Jane Smith" />
            </label>
            <label className="block">
              <span className="text-xs text-slate-400">Email *</span>
              <input type="email" value={invEmail} onChange={e => setInvEmail(e.target.value)} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white" placeholder="jane@example.com" />
            </label>
            <label className="block">
              <span className="text-xs text-slate-400">Temporary password *</span>
              <input type="password" value={invPassword} onChange={e => setInvPassword(e.target.value)} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white" placeholder="Min. 8 characters" />
            </label>
            <label className="block">
              <span className="text-xs text-slate-400">Role</span>
              <select value={invRole} onChange={e => setInvRole(e.target.value)} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white">
                {PLATFORM_ROLES.filter(r => r !== 'Owner').map(r => <option key={r} value={r}>{r}</option>)}
              </select>
            </label>
            <div className="flex justify-end gap-2 pt-2">
              <button type="button" onClick={() => setShowInvite(false)} className="rounded-lg border border-white/[0.1] px-4 py-2 text-sm text-slate-300 hover:bg-white/[0.05]">Cancel</button>
              <button
                type="button"
                onClick={invite}
                disabled={inviting || !invEmail.trim() || !invPassword.trim()}
                className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-500 disabled:opacity-50"
              >
                {inviting ? 'Inviting…' : 'Invite'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
