'use client';

import { InfoTip } from '../components/InfoTip';
import { useEffect, useState, useCallback } from 'react';
import { useSearchParams } from 'next/navigation';
import {
  Shield, Users, Key, GitBranch, Award, Lock, CheckCircle, XCircle,
  RefreshCw, Plus, Search, ChevronLeft, ChevronRight, Eye,
  UserCheck, UserX, Unlock, RotateCcw, ClipboardList, UserCog,
  CheckSquare, MinusSquare, Square, Edit2, Power, PowerOff, Table2, Trash2,
  Building2,
} from 'lucide-react';
import {
  usersApi, rolesApi, delegationsApi, authoritiesApi, securitySettingsApi,
  identityAuditApi, grantorsApi, permissionGrantApi,
  entityGrantsApi,
} from '../api/identity';
import { companiesApi } from '../api/organization';
import client from '../api/client';
import type {
  UserListItem, RoleItem, PermissionItem, ApprovalDelegation,
  ApprovalAuthority, SecuritySetting, AuditLogItem, PermissionGrantorRecord,
  UserAccess, PermissionMatrix, EntityGrant,
} from '../api/identity';
import type { CompanyDto } from '../api/organization';

// ── Shared helpers ─────────────────────────────────────────────────────────────

type Tab = 'users' | 'roles' | 'permissionMatrix' | 'permissions' | 'grantors' | 'delegations' | 'authorities' | 'security' | 'auditLogs';

const TABS: { id: Tab; label: string; icon: React.ElementType }[] = [
  { id: 'users', label: 'Users', icon: Users },
  { id: 'roles', label: 'Roles', icon: Shield },
  { id: 'permissionMatrix', label: 'Permission Matrix', icon: Table2 },
  { id: 'permissions', label: 'Permissions', icon: Key },
  { id: 'grantors', label: 'Permission Grantors', icon: UserCog },
  { id: 'delegations', label: 'Approval Delegations', icon: GitBranch },
  { id: 'authorities', label: 'Approval Authorities', icon: Award },
  { id: 'security', label: 'Security Settings', icon: Lock },
  { id: 'auditLogs', label: 'Audit Logs', icon: ClipboardList },
];

const ACCESS_MODES = [
  'FullPortal', 'ESSOnly', 'ManagerPortal', 'HRPortal',
  'PayrollPortal', 'FinancePortal', 'SupervisorPortal',
  'ReadOnlyAuditor', 'Mobile', 'KioskOnly', 'NoLogin',
];

const USER_STATUSES = ['Active', 'Invited', 'Suspended', 'Locked', 'Deactivated', 'PendingPasswordSetup'];

function StatusBadge({ value }: { value: string }) {
  const colors: Record<string, string> = {
    Active: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300',
    Invited: 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300',
    Suspended: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300',
    Locked: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300',
    Deactivated: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-400',
    NoLogin: 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400',
    Cancelled: 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400',
  };
  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${colors[value] ?? 'bg-slate-100 text-slate-600'}`}>
      {value}
    </span>
  );
}

function FormField({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <label className="text-xs font-medium text-slate-600 dark:text-slate-400">{label}</label>
      {children}
    </div>
  );
}

function inp(extra = '') {
  return `w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 shadow-sm focus:border-violet-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 ${extra}`;
}

function ErrMsg({ msg }: { msg: string }) {
  return msg ? <p className="text-xs text-red-500 mt-1">{msg}</p> : null;
}

function fmtDate(d?: string) {
  if (!d) return '—';
  return new Date(d).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

function fmtDateTime(d?: string) {
  if (!d) return '—';
  return new Date(d).toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

// ── Users Tab ─────────────────────────────────────────────────────────────────

interface UsageData {
  activeUsers: number;
  maxUsers: number;
  activeEmployees: number;
  maxEmployees: number;
  storageUsedMb: number;
}

function UsersTab() {
  const searchParams = useSearchParams();
  const [users, setUsers] = useState<UserListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [statusFilter, setStatusFilter] = useState('');
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState('');
  const [subscriptionBanner, setSubscriptionBanner] = useState('');
  const [usage, setUsage] = useState<UsageData | null>(null);
  const [selected, setSelected] = useState<UserListItem | null>(null);
  const [showCreate, setShowCreate] = useState(false);
  const [showAction, setShowAction] = useState<{ type: string; userId: string } | null>(null);
  const [actionReason, setActionReason] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [actionErr, setActionErr] = useState('');
  const [actionLoading, setActionLoading] = useState(false);
  const [editUser, setEditUser] = useState<UserListItem | null>(null);
  const [editFields, setEditFields] = useState({ fullName: '', phoneNumber: '', preferredLanguage: '', timezone: '' });
  const [editErr, setEditErr] = useState('');
  const [editLoading, setEditLoading] = useState(false);
  const [roles, setRoles] = useState<RoleItem[]>([]);
  const [allPermissions, setAllPermissions] = useState<PermissionItem[]>([]);

  const pageSize = 20;

  const atUserLimit = usage !== null && usage.maxUsers > 0 && usage.activeUsers >= usage.maxUsers;

  const loadUsage = useCallback(async () => {
    try {
      const data = await client.get<UsageData>('/api/tenant-admin/usage').then(r => r.data);
      setUsage(data);
    } catch (e: unknown) {
      const status = (e as { response?: { status?: number } })?.response?.status;
      if (status === 402) {
        setSubscriptionBanner('Your subscription is inactive or expired. Please contact support.');
      }
    }
  }, []);

  const load = useCallback(async () => {
    setLoading(true); setErr('');
    try {
      const r = await usersApi.list({ search: search || undefined, status: statusFilter || undefined, page, pageSize });
      setUsers(r.items); setTotal(r.total);
    } catch { setErr('Failed to load users.'); }
    finally { setLoading(false); }
  }, [search, statusFilter, page]);

  useEffect(() => { loadUsage(); }, [loadUsage]);
  useEffect(() => { load(); }, [load]);
  useEffect(() => {
    const searchFromUrl = searchParams?.get('search') ?? null;
    if (searchFromUrl !== null && searchFromUrl !== search) {
      setSearch(searchFromUrl);
      setPage(1);
    }
  }, [search, searchParams]);
  useEffect(() => {
    rolesApi.list().then(setRoles).catch(() => {});
    rolesApi.permissions().then(setAllPermissions).catch(() => {});
  }, []);

  const doAction = async () => {
    if (!showAction) return;
    setActionLoading(true); setActionErr('');
    try {
      const { type, userId } = showAction;
      if (type === 'activate') await usersApi.activate(userId);
      else if (type === 'suspend') await usersApi.suspend(userId, actionReason);
      else if (type === 'lock') await usersApi.lock(userId, actionReason);
      else if (type === 'unlock') await usersApi.unlock(userId);
      else if (type === 'delete') await usersApi.delete(userId);
      else if (type === 'reset-password') {
        if (newPassword.length < 10) { setActionErr('Password must be at least 10 characters.'); setActionLoading(false); return; }
        await usersApi.adminResetPassword(userId, newPassword, true);
      }
      setShowAction(null); setActionReason(''); setNewPassword('');
      load();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setActionErr(msg ?? 'Action failed.');
    }
    setActionLoading(false);
  };

  const openEdit = (u: UserListItem) => {
    setEditUser(u);
    setEditFields({ fullName: u.fullName, phoneNumber: u.phoneNumber ?? '', preferredLanguage: '', timezone: '' });
    setEditErr('');
  };

  const doEdit = async () => {
    if (!editUser) return;
    setEditLoading(true); setEditErr('');
    try {
      await usersApi.update(editUser.id, editFields);
      setEditUser(null);
      load();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setEditErr(msg ?? 'Failed to save changes.');
    }
    setEditLoading(false);
  };

  const totalPages = Math.ceil(total / pageSize);

  return (
    <div className="space-y-4">
      {/* Subscription inactive banner */}
      {subscriptionBanner && (
        <div className="rounded-lg bg-amber-50 border border-amber-200 px-4 py-3 text-sm text-amber-800 dark:bg-amber-900/20 dark:border-amber-800 dark:text-amber-300">
          {subscriptionBanner}
        </div>
      )}

      {/* Toolbar */}
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative flex-1 min-w-52">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
          <input className={inp('pl-9')} placeholder="Search name or email…" value={search}
            onChange={e => { setSearch(e.target.value); setPage(1); }} />
        </div>
        <select className={inp('w-40')} value={statusFilter} onChange={e => { setStatusFilter(e.target.value); setPage(1); }}>
          <option value="">All Statuses</option>
          {USER_STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
        </select>
        <button onClick={load} className="rounded-lg border border-slate-200 p-2 hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700">
          <RefreshCw className="h-4 w-4 text-slate-500" />
        </button>
        <div className="relative group">
          <button
            type="button"
            onClick={() => { if (!atUserLimit) setShowCreate(true); }}
            disabled={atUserLimit}
            className="flex items-center gap-2 rounded-lg bg-violet-600 px-3 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            <Plus className="h-4 w-4" /> Create User
          </button>
          {atUserLimit && usage && (
            <div className="absolute bottom-full left-0 mb-1.5 w-64 rounded-lg bg-slate-800 px-3 py-2 text-xs text-white shadow-lg hidden group-hover:block z-10">
              User limit reached ({usage.activeUsers}/{usage.maxUsers}). Upgrade your plan to add more users.
            </div>
          )}
        </div>
      </div>

      {err && <p className="text-sm text-red-500">{err}</p>}

      {/* Table */}
      <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-700">
        <table className="w-full text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/60">
            <tr>
              {['Name / Email', 'Status', 'Roles', 'Access Mode', 'Last Login', 'Actions'].map(h => (
                <th key={h} className="px-4 py-2.5 text-left text-xs font-semibold text-slate-500">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
            {loading ? (
              <tr><td colSpan={6} className="py-8 text-center text-sm text-slate-500">Loading…</td></tr>
            ) : users.length === 0 ? (
              <tr><td colSpan={6} className="py-8 text-center text-sm text-slate-500">No users found.</td></tr>
            ) : users.map(u => (
              <tr key={u.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/40">
                <td className="px-4 py-3">
                  <p className="font-medium text-slate-800 dark:text-slate-200">{u.fullName}</p>
                  <p className="text-xs text-slate-500">{u.email}</p>
                  {u.employeeId && <p className="text-xs text-violet-500">Emp #{u.employeeId}</p>}
                </td>
                <td className="px-4 py-3"><StatusBadge value={u.status} /></td>
                <td className="px-4 py-3 text-xs text-slate-600 dark:text-slate-400">
                  {u.roles.length ? u.roles.join(', ') : '—'}
                </td>
                <td className="px-4 py-3 text-xs text-slate-600 dark:text-slate-400">{u.accessMode}</td>
                <td className="px-4 py-3 text-xs text-slate-500">{fmtDateTime(u.lastLoginAtUtc)}</td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-1">
                    <button title="View Access" onClick={() => setSelected(u)} className="rounded p-1 hover:bg-slate-100 dark:hover:bg-slate-700">
                      <Eye className="h-3.5 w-3.5 text-slate-500" />
                    </button>
                    <button title="Edit User" onClick={() => openEdit(u)} className="rounded p-1 hover:bg-violet-100 dark:hover:bg-violet-900/30">
                      <Edit2 className="h-3.5 w-3.5 text-violet-500" />
                    </button>
                    {u.status !== 'Active' && (
                      <button title="Activate" onClick={() => setShowAction({ type: 'activate', userId: u.id })} className="rounded p-1 hover:bg-green-100 dark:hover:bg-green-900/30">
                        <UserCheck className="h-3.5 w-3.5 text-green-600" />
                      </button>
                    )}
                    {u.isLocked ? (
                      <button title="Unlock" onClick={() => setShowAction({ type: 'unlock', userId: u.id })} className="rounded p-1 hover:bg-amber-100 dark:hover:bg-amber-900/30">
                        <Unlock className="h-3.5 w-3.5 text-amber-600" />
                      </button>
                    ) : (
                      <button title="Lock" onClick={() => setShowAction({ type: 'lock', userId: u.id })} className="rounded p-1 hover:bg-red-100 dark:hover:bg-red-900/30">
                        <Lock className="h-3.5 w-3.5 text-red-500" />
                      </button>
                    )}
                    {u.isActive && (
                      <button title="Suspend" onClick={() => setShowAction({ type: 'suspend', userId: u.id })} className="rounded p-1 hover:bg-amber-100 dark:hover:bg-amber-900/30">
                        <UserX className="h-3.5 w-3.5 text-amber-600" />
                      </button>
                    )}
                    <button title="Reset Password" onClick={() => setShowAction({ type: 'reset-password', userId: u.id })} className="rounded p-1 hover:bg-violet-100 dark:hover:bg-violet-900/30">
                      <RotateCcw className="h-3.5 w-3.5 text-violet-600" />
                    </button>
                    <button title="Delete User" onClick={() => setShowAction({ type: 'delete', userId: u.id })} className="rounded p-1 hover:bg-red-100 dark:hover:bg-red-900/30">
                      <Trash2 className="h-3.5 w-3.5 text-red-500" />
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      {totalPages > 1 && (
        <div className="flex items-center justify-between text-sm text-slate-500">
          <span>{total} users total</span>
          <div className="flex items-center gap-2">
            <button disabled={page <= 1} onClick={() => setPage(p => p - 1)} className="rounded p-1 hover:bg-slate-100 disabled:opacity-40 dark:hover:bg-slate-700">
              <ChevronLeft className="h-4 w-4" />
            </button>
            <span>Page {page} of {totalPages}</span>
            <button disabled={page >= totalPages} onClick={() => setPage(p => p + 1)} className="rounded p-1 hover:bg-slate-100 disabled:opacity-40 dark:hover:bg-slate-700">
              <ChevronRight className="h-4 w-4" />
            </button>
          </div>
        </div>
      )}

      {/* User Access Detail Modal */}
      {selected && <UserAccessModal user={selected} roles={roles} allPermissions={allPermissions} onClose={() => { setSelected(null); load(); }} />}

      {/* Create User Modal */}
      {showCreate && <CreateUserModal roles={roles} onClose={() => setShowCreate(false)} onCreated={() => { setShowCreate(false); load(); }} />}

      {/* Action confirmation modal */}
      {showAction && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="w-full max-w-md rounded-2xl bg-white p-6 shadow-xl dark:bg-slate-900">
            <h3 className={`text-base font-semibold capitalize mb-4 ${showAction.type === 'delete' ? 'text-red-600 dark:text-red-400' : 'text-slate-800 dark:text-slate-200'}`}>
              {showAction.type.replace('-', ' ')} User
            </h3>
            {showAction.type === 'delete' && (
              <p className="mb-4 text-sm text-slate-600 dark:text-slate-400">
                This will permanently remove the user's access. This action cannot be undone.
              </p>
            )}
            {['suspend', 'lock'].includes(showAction.type) && (
              <FormField label="Reason">
                <input className={inp()} value={actionReason} onChange={e => setActionReason(e.target.value)} placeholder="Optional reason…" />
              </FormField>
            )}
            {showAction.type === 'reset-password' && (
              <FormField label="New Password (min 10 chars)">
                <input type="password" className={inp()} value={newPassword} onChange={e => setNewPassword(e.target.value)} />
              </FormField>
            )}
            {actionErr && <ErrMsg msg={actionErr} />}
            <div className="mt-4 flex justify-end gap-2">
              <button onClick={() => { setShowAction(null); setActionErr(''); setActionReason(''); setNewPassword(''); }}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700">
                Cancel
              </button>
              <button onClick={doAction} disabled={actionLoading}
                className={`rounded-lg px-4 py-2 text-sm font-medium text-white disabled:opacity-60 ${showAction.type === 'delete' ? 'bg-red-600 hover:bg-red-700' : 'bg-violet-600 hover:bg-violet-700'}`}>
                {actionLoading ? 'Processing…' : showAction.type === 'delete' ? 'Delete' : 'Confirm'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Edit User Modal */}
      {editUser && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="w-full max-w-md rounded-2xl bg-white p-6 shadow-xl dark:bg-slate-900 animate-fade-in">
            <h3 className="text-base font-semibold text-slate-800 dark:text-slate-200 mb-1">Edit User</h3>
            <p className="text-xs text-slate-500 dark:text-slate-400 mb-4">{editUser.email}</p>
            <div className="space-y-3">
              <FormField label="Full Name">
                <input className={inp()} value={editFields.fullName} onChange={e => setEditFields(f => ({ ...f, fullName: e.target.value }))} placeholder="Full name" />
              </FormField>
              <FormField label="Phone Number">
                <input className={inp()} value={editFields.phoneNumber} onChange={e => setEditFields(f => ({ ...f, phoneNumber: e.target.value }))} placeholder="+1 555 000 0000" />
              </FormField>
              <FormField label="Preferred Language">
                <select title="Preferred Language" className={inp()} value={editFields.preferredLanguage} onChange={e => setEditFields(f => ({ ...f, preferredLanguage: e.target.value }))}>
                  <option value="">— Default —</option>
                  {['en','ar','de','fr','es','zh','hi','ur','tl','id'].map(l => <option key={l} value={l}>{l.toUpperCase()}</option>)}
                </select>
              </FormField>
              <FormField label="Timezone">
                <select title="Timezone" className={inp()} value={editFields.timezone} onChange={e => setEditFields(f => ({ ...f, timezone: e.target.value }))}>
                  <option value="">— Default —</option>
                  {['UTC','Asia/Dubai','Asia/Riyadh','Asia/Qatar','Asia/Kuwait','America/New_York','America/Chicago','America/Denver','America/Los_Angeles','America/Toronto','Europe/London','Europe/Berlin','Europe/Paris','Asia/Kolkata','Asia/Karachi','Asia/Manila','Asia/Singapore','Australia/Sydney','Africa/Cairo','Africa/Lagos','Africa/Johannesburg'].map(tz => <option key={tz} value={tz}>{tz}</option>)}
                </select>
              </FormField>
            </div>
            {editErr && <ErrMsg msg={editErr} />}
            <div className="mt-5 flex justify-end gap-2">
              <button onClick={() => setEditUser(null)}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700">
                Cancel
              </button>
              <button onClick={doEdit} disabled={editLoading}
                className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
                {editLoading ? 'Saving…' : 'Save Changes'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function UserAccessModal({ user, roles, allPermissions, onClose }: {
  user: UserListItem;
  roles: RoleItem[];
  allPermissions: PermissionItem[];
  onClose: () => void;
}) {
  const [access, setAccess] = useState<UserAccess | null>(null);
  const [loading, setLoading] = useState(true);
  const [tab, setTab] = useState<'info' | 'roles' | 'company-access' | 'access-mode' | 'overrides'>('info');
  const [selectedRoles, setSelectedRoles] = useState<string[]>(user.roles);
  const [accessMode, setAccessMode] = useState(user.accessMode);
  const [companyGrants, setCompanyGrants] = useState<EntityGrant[]>([]);
  const [companies, setCompanies] = useState<CompanyDto[]>([]);
  const [grantMode, setGrantMode] = useState('SelectedCompanies');
  const [grantCompanyId, setGrantCompanyId] = useState('');
  const [grantRole, setGrantRole] = useState('HR');
  const [permSearch, setPermSearch] = useState('');
  const [pendingOverrides, setPendingOverrides] = useState<Record<string, { effect: 'Allow' | 'Deny' | 'Remove'; reason: string }>>({});
  const [overrideReason, setOverrideReason] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveErr, setSaveErr] = useState('');
  const [saveOk, setSaveOk] = useState('');

  const reload = useCallback(() => {
    usersApi.getAccess(user.id).then(a => { setAccess(a); }).catch(() => {}).finally(() => setLoading(false));
  }, [user.id]);
  useEffect(() => { reload(); }, [reload]);
  const reloadCompanyAccess = useCallback(async () => {
    const [grants, companyPage] = await Promise.all([
      entityGrantsApi.list(user.id),
      companiesApi.list(1, 200),
    ]);
    setCompanyGrants(grants);
    setCompanies(companyPage.items);
    if (!grantCompanyId && companyPage.items.length > 0) setGrantCompanyId(companyPage.items[0].id);
  }, [grantCompanyId, user.id]);
  useEffect(() => { reloadCompanyAccess().catch(() => {}); }, [reloadCompanyAccess]);

  const saveRoles = async () => {
    setSaving(true); setSaveErr(''); setSaveOk('');
    try { await usersApi.assignRoles(user.id, selectedRoles); setSaveOk('Roles updated.'); }
    catch { setSaveErr('Failed to update roles.'); }
    setSaving(false);
  };

  const saveAccessMode = async () => {
    setSaving(true); setSaveErr(''); setSaveOk('');
    try { await usersApi.setAccessMode(user.id, accessMode); setSaveOk('Access mode updated.'); }
    catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setSaveErr(msg ?? 'Failed to update access mode.');
    }
    setSaving(false);
  };

  const addCompanyGrant = async () => {
    setSaving(true); setSaveErr(''); setSaveOk('');
    try {
      await entityGrantsApi.create({
        userId: user.id,
        companyId: grantMode === 'SelectedCompanies' ? grantCompanyId : undefined,
        role: grantRole || 'HR',
        grantMode,
      });
      await reloadCompanyAccess();
      setSaveOk('Company access updated. The user must sign in again for token scope changes.');
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setSaveErr(msg ?? 'Failed to update company access.');
    }
    setSaving(false);
  };

  const removeCompanyGrant = async (id: string) => {
    setSaving(true); setSaveErr(''); setSaveOk('');
    try {
      await entityGrantsApi.remove(id);
      await reloadCompanyAccess();
      setSaveOk('Company access removed. The user must sign in again for token scope changes.');
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setSaveErr(msg ?? 'Failed to remove company access.');
    }
    setSaving(false);
  };

  const setGroupScope = async (enabled: boolean) => {
    setSaving(true); setSaveErr(''); setSaveOk('');
    try {
      await entityGrantsApi.setGroupScope(user.id, enabled);
      setSaveOk(enabled
        ? 'Group-wide scope granted. The user must sign in again for token scope changes.'
        : 'Group-wide scope revoked. The user must sign in again for token scope changes.');
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setSaveErr(msg ?? 'Failed to update group scope.');
    }
    setSaving(false);
  };

  const saveOverrides = async () => {
    const entries = Object.entries(pendingOverrides);
    if (entries.length === 0) { setSaveErr('No changes to save.'); return; }
    setSaving(true); setSaveErr(''); setSaveOk('');
    try {
      let latest: UserAccess | null = access;
      for (const [permKey, { effect, reason }] of entries) {
        latest = await permissionGrantApi.grant(user.id, { permissionKey: permKey, effect, reason: reason || overrideReason || undefined });
      }
      setAccess(latest); setPendingOverrides({}); setOverrideReason('');
      setSaveOk(`${entries.length} permission override(s) saved.`);
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setSaveErr(msg ?? 'Failed to save overrides.');
    }
    setSaving(false);
  };

  // Determine current override state for a permission key
  const getOverrideState = (key: string): 'Allow' | 'Deny' | null => {
    if (!access) return null;
    if (access.deniedPermissions.includes(key)) return 'Deny';
    // Check if it's an Allow override (in permissions but not in role-based)
    const rolePerms = new Set(roles.flatMap(r => selectedRoles.includes(r.name) ? r.permissions : []));
    if (access.permissions.includes(key) && !rolePerms.has(key)) return 'Allow';
    return null;
  };

  const togglePermission = (key: string, effect: 'Allow' | 'Deny' | 'Remove') => {
    setPendingOverrides(prev => ({ ...prev, [key]: { effect, reason: prev[key]?.reason ?? '' } }));
  };

  const filteredPerms = allPermissions.filter(p =>
    !permSearch || p.key.includes(permSearch.toLowerCase()) || p.description.toLowerCase().includes(permSearch.toLowerCase())
  );
  const groupedPerms = filteredPerms.reduce<Record<string, PermissionItem[]>>((acc, p) => {
    (acc[p.module] ??= []).push(p);
    return acc;
  }, {});

  const subTabs = [
    { id: 'info' as const, label: 'Info' },
    { id: 'roles' as const, label: 'Roles' },
    { id: 'company-access' as const, label: 'Company Access' },
    { id: 'access-mode' as const, label: 'Access Mode' },
    { id: 'overrides' as const, label: `Overrides${Object.keys(pendingOverrides).length ? ` (${Object.keys(pendingOverrides).length})` : ''}` },
  ];

  const companyName = (id?: string) => companies.find(c => c.id === id)?.legalNameEn ?? id ?? 'All companies';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-2xl rounded-2xl bg-white shadow-xl dark:bg-slate-900 flex flex-col max-h-[90vh]">
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4 dark:border-slate-700">
          <div>
            <h3 className="font-semibold text-slate-800 dark:text-slate-200">{user.fullName}</h3>
            <p className="text-xs text-slate-500">{user.email}</p>
          </div>
          <button onClick={onClose} className="rounded-lg p-2 hover:bg-slate-100 dark:hover:bg-slate-700 text-slate-500">✕</button>
        </div>
        <div className="flex border-b border-slate-200 dark:border-slate-700 px-6">
          {subTabs.map(st => (
            <button key={st.id} onClick={() => { setTab(st.id); setSaveErr(''); setSaveOk(''); }}
              className={`px-3 py-2.5 text-sm font-medium border-b-2 transition-colors ${tab === st.id ? 'border-violet-500 text-violet-600' : 'border-transparent text-slate-500 hover:text-slate-700'}`}>
              {st.label}
            </button>
          ))}
        </div>
        <div className="overflow-y-auto p-6 space-y-4 flex-1">
          {loading ? <p className="text-sm text-slate-500">Loading…</p> : (
            <>
              {tab === 'info' && (
                <div className="space-y-3">
                  <div className="grid grid-cols-2 gap-3 text-sm">
                    <div><span className="text-slate-500 text-xs">Status</span><div><StatusBadge value={user.status} /></div></div>
                    <div><span className="text-slate-500 text-xs">Access Mode</span><div className="font-medium text-slate-700 dark:text-slate-300">{user.accessMode}</div></div>
                    <div><span className="text-slate-500 text-xs">Last Login</span><div className="text-slate-700 dark:text-slate-300">{fmtDateTime(user.lastLoginAtUtc)}</div></div>
                    <div><span className="text-slate-500 text-xs">Created</span><div className="text-slate-700 dark:text-slate-300">{fmtDate(user.createdAtUtc)}</div></div>
                    <div><span className="text-slate-500 text-xs">Locked</span><div className="text-slate-700 dark:text-slate-300">{user.isLocked ? 'Yes' : 'No'}</div></div>
                    <div><span className="text-slate-500 text-xs">Must Change Password</span><div className="text-slate-700 dark:text-slate-300">{user.mustChangePassword ? 'Yes' : 'No'}</div></div>
                  </div>
                  {access && (
                    <>
                      <div>
                        <p className="text-xs font-medium text-slate-500 mb-1">Effective Permissions ({access.permissions.length})</p>
                        <div className="flex flex-wrap gap-1 max-h-40 overflow-y-auto">
                          {access.permissions.map(p => <span key={p} className="rounded bg-slate-100 px-1.5 py-0.5 text-xs text-slate-600 dark:bg-slate-800 dark:text-slate-400">{p}</span>)}
                        </div>
                      </div>
                      {access.deniedPermissions.length > 0 && (
                        <div>
                          <p className="text-xs font-medium text-red-500 mb-1">Denied Permissions</p>
                          <div className="flex flex-wrap gap-1">
                            {access.deniedPermissions.map(p => <span key={p} className="rounded bg-red-50 px-1.5 py-0.5 text-xs text-red-600">{p}</span>)}
                          </div>
                        </div>
                      )}
                    </>
                  )}
                </div>
              )}

              {tab === 'roles' && (
                <div className="space-y-3">
                  <p className="text-xs text-slate-500">Select roles to assign to this user. This replaces all current roles.</p>
                  <div className="grid grid-cols-2 gap-2">
                    {roles.map(r => (
                      <label key={r.id} className="flex items-center gap-2 rounded-lg border border-slate-200 p-2.5 cursor-pointer hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800">
                        <input type="checkbox" checked={selectedRoles.includes(r.name)}
                          onChange={e => setSelectedRoles(prev => e.target.checked ? [...prev, r.name] : prev.filter(x => x !== r.name))} />
                        <div>
                          <p className="text-sm font-medium text-slate-700 dark:text-slate-300">{r.name}</p>
                          <p className="text-xs text-slate-500">{r.description}</p>
                        </div>
                      </label>
                    ))}
                  </div>
                </div>
              )}

              {tab === 'company-access' && (
                <div className="space-y-4">
                  <div className="rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                    Company access controls legal-entity visibility. Role permissions decide what the user can do; this decides where they can do it.
                  </div>
                  <div className="space-y-2">
                    <p className="text-xs font-medium text-slate-500">Current grants</p>
                    {companyGrants.length === 0 ? (
                      <div className="rounded-lg border border-dashed border-slate-200 px-3 py-4 text-sm text-slate-500 dark:border-slate-700">
                        No explicit company grants.
                      </div>
                    ) : companyGrants.map(g => (
                      <div key={g.id} className="flex items-center justify-between rounded-lg border border-slate-200 px-3 py-2 dark:border-slate-700">
                        <div className="flex items-center gap-2 min-w-0">
                          <Building2 className="h-4 w-4 text-violet-500 shrink-0" />
                          <div className="min-w-0">
                            <p className="text-sm font-medium text-slate-700 dark:text-slate-200">
                              {g.grantMode === 'SelectedCompanies' ? companyName(g.companyId) : g.grantMode === 'AllCurrentCompanies' ? 'All current companies' : 'All current and future companies'}
                            </p>
                            <p className="text-xs text-slate-500">{g.role || 'Company access'} · {fmtDate(g.createdAtUtc)}</p>
                          </div>
                        </div>
                        <button type="button" onClick={() => removeCompanyGrant(g.id)} disabled={saving}
                          className="rounded p-1 text-red-500 hover:bg-red-50 disabled:opacity-50">
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    ))}
                  </div>
                  <div className="grid grid-cols-1 gap-3 rounded-xl border border-slate-200 p-3 dark:border-slate-700">
                    <FormField label="Grant mode">
                      <select className={inp()} value={grantMode} onChange={e => setGrantMode(e.target.value)}>
                        <option value="SelectedCompanies">Selected company</option>
                        <option value="AllCurrentCompanies">All current companies</option>
                        <option value="AllCurrentAndFutureCompanies">All current and future companies</option>
                      </select>
                    </FormField>
                    {grantMode === 'SelectedCompanies' && (
                      <FormField label="Company">
                        <select className={inp()} value={grantCompanyId} onChange={e => setGrantCompanyId(e.target.value)}>
                          {companies.map(c => <option key={c.id} value={c.id}>{c.legalNameEn}</option>)}
                        </select>
                      </FormField>
                    )}
                    <FormField label="Grant role label">
                      <input className={inp()} value={grantRole} onChange={e => setGrantRole(e.target.value)} placeholder="HR, Payroll, Auditor" />
                    </FormField>
                    {grantMode === 'AllCurrentAndFutureCompanies' && (
                      <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-700 dark:border-amber-800 dark:bg-amber-900/20 dark:text-amber-300">
                        This is dynamic group-wide access and will include future companies.
                      </div>
                    )}
                    <button type="button" onClick={addCompanyGrant} disabled={saving || (grantMode === 'SelectedCompanies' && !grantCompanyId)}
                      className="w-fit rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
                      Add Grant
                    </button>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <button type="button" onClick={() => setGroupScope(true)} disabled={saving}
                      className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 hover:bg-slate-50 disabled:opacity-60 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800">
                      Grant Group Scope
                    </button>
                    <button type="button" onClick={() => setGroupScope(false)} disabled={saving}
                      className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 hover:bg-slate-50 disabled:opacity-60 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800">
                      Revoke Group Scope
                    </button>
                  </div>
                </div>
              )}

              {tab === 'access-mode' && (
                <div className="space-y-3">
                  <FormField label="Access Mode">
                    <select className={inp()} value={accessMode} onChange={e => setAccessMode(e.target.value)}>
                      {ACCESS_MODES.map(m => <option key={m} value={m}>{m}</option>)}
                    </select>
                  </FormField>
                  <p className="text-xs text-slate-400">Controls which portal sections this user can access after login.</p>
                </div>
              )}

              {tab === 'overrides' && (
                <div className="space-y-3">
                  <div className="rounded-lg bg-violet-50 dark:bg-violet-900/20 px-3 py-2 text-xs text-violet-700 dark:text-violet-300">
                    Individually grant or deny permissions regardless of role. Changes here are saved in bulk when you click Save.
                  </div>
                  <FormField label="Bulk reason (optional — applies to all changes in this batch)">
                    <input className={inp()} value={overrideReason} onChange={e => setOverrideReason(e.target.value)} placeholder="e.g. Temporary project access" />
                  </FormField>
                  <div className="relative">
                    <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
                    <input className={inp('pl-9')} placeholder="Filter permissions…" value={permSearch} onChange={e => setPermSearch(e.target.value)} />
                  </div>
                  <div className="space-y-2 max-h-80 overflow-y-auto">
                    {Object.entries(groupedPerms).sort().map(([module, perms]) => (
                      <div key={module} className="rounded-xl border border-slate-200 dark:border-slate-700 overflow-hidden">
                        <div className="bg-slate-50 dark:bg-slate-800/60 px-3 py-1.5 text-xs font-semibold text-slate-600 dark:text-slate-400">{module}</div>
                        <div className="divide-y divide-slate-100 dark:divide-slate-800">
                          {perms.map(p => {
                            const current = getOverrideState(p.key);
                            const pending = pendingOverrides[p.key];
                            const effectiveState = pending?.effect ?? (current ?? 'default');
                            return (
                              <div key={p.key} className="flex items-center justify-between px-3 py-2 hover:bg-slate-50 dark:hover:bg-slate-800/40">
                                <div className="min-w-0 flex-1 mr-3">
                                  <p className="text-xs font-mono text-violet-700 dark:text-violet-400 truncate">{p.key}</p>
                                  <p className="text-xs text-slate-500 truncate">{p.description}</p>
                                </div>
                                <div className="flex items-center gap-1 shrink-0">
                                  <button type="button"
                                    title="Allow"
                                    onClick={() => togglePermission(p.key, 'Allow')}
                                    className={`flex items-center gap-1 rounded px-2 py-0.5 text-xs font-medium transition-colors ${effectiveState === 'Allow' ? 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400' : 'text-slate-400 hover:bg-green-50 hover:text-green-600'}`}>
                                    <CheckSquare className="h-3 w-3" /> Allow
                                  </button>
                                  <button type="button"
                                    title="Deny"
                                    onClick={() => togglePermission(p.key, 'Deny')}
                                    className={`flex items-center gap-1 rounded px-2 py-0.5 text-xs font-medium transition-colors ${effectiveState === 'Deny' ? 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400' : 'text-slate-400 hover:bg-red-50 hover:text-red-600'}`}>
                                    <MinusSquare className="h-3 w-3" /> Deny
                                  </button>
                                  {(current !== null || pending) && (
                                    <button type="button"
                                      title="Remove override"
                                      onClick={() => togglePermission(p.key, 'Remove')}
                                      className={`flex items-center gap-1 rounded px-2 py-0.5 text-xs font-medium transition-colors ${effectiveState === 'Remove' ? 'bg-slate-200 text-slate-700 dark:bg-slate-600 dark:text-slate-300' : 'text-slate-400 hover:bg-slate-100 hover:text-slate-600'}`}>
                                      <Square className="h-3 w-3" /> Reset
                                    </button>
                                  )}
                                </div>
                              </div>
                            );
                          })}
                        </div>
                      </div>
                    ))}
                  </div>
                  {Object.keys(pendingOverrides).length > 0 && (
                    <div className="rounded-lg border border-amber-200 bg-amber-50 dark:bg-amber-900/20 dark:border-amber-800 px-3 py-2 text-xs text-amber-700 dark:text-amber-300">
                      {Object.keys(pendingOverrides).length} unsaved change(s): {Object.entries(pendingOverrides).map(([k, v]) => `${k} → ${v.effect}`).join(', ')}
                    </div>
                  )}
                </div>
              )}

              {saveErr && <ErrMsg msg={saveErr} />}
              {saveOk && <p className="text-xs text-green-600">{saveOk}</p>}
            </>
          )}
        </div>
        <div className="border-t border-slate-200 dark:border-slate-700 px-6 py-4 flex justify-end gap-2">
          <button onClick={onClose} className="rounded-lg border border-slate-200 px-3 py-2 text-sm hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700">Close</button>
          {tab === 'roles' && (
            <button onClick={saveRoles} disabled={saving} className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
              {saving ? 'Saving…' : 'Save Roles'}
            </button>
          )}
          {tab === 'access-mode' && (
            <button onClick={saveAccessMode} disabled={saving} className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
              {saving ? 'Saving…' : 'Save Mode'}
            </button>
          )}
          {tab === 'overrides' && (
            <button onClick={saveOverrides} disabled={saving || Object.keys(pendingOverrides).length === 0} className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
              {saving ? 'Saving…' : `Save ${Object.keys(pendingOverrides).length || ''} Override(s)`}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

function CreateUserModal({ roles, onClose, onCreated }: { roles: RoleItem[]; onClose: () => void; onCreated: () => void }) {
  const [email, setEmail] = useState('');
  const [fullName, setFullName] = useState('');
  const [password, setPassword] = useState('');
  const [selectedRoles, setSelectedRoles] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState('');

  const submit = async () => {
    if (!email || !fullName || !password) { setErr('All fields are required.'); return; }
    if (password.length < 10) { setErr('Password must be at least 10 characters.'); return; }
    setLoading(true); setErr('');
    try {
      await usersApi.create({ email, fullName, password, roles: selectedRoles });
      onCreated();
    } catch (e: unknown) {
      const status = (e as { response?: { status?: number; data?: { error?: string; message?: string; current?: number; limit?: number } } })?.response?.status;
      const data = (e as { response?: { data?: { error?: string; message?: string; current?: number; limit?: number } } })?.response?.data;
      if (status === 402) {
        setErr('Your subscription is inactive or expired. Please contact support.');
      } else if (status === 422 && data?.error === 'user_limit_reached') {
        setErr(data.message ?? `User limit reached (${data.current}/${data.limit}). Please upgrade your subscription.`);
      } else {
        setErr(data?.message ?? 'Failed to create user.');
      }
    }
    setLoading(false);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-md rounded-2xl bg-white p-6 shadow-xl dark:bg-slate-900 space-y-4">
        <h3 className="text-base font-semibold text-slate-800 dark:text-slate-200">Create User</h3>
        <FormField label="Full Name"><input className={inp()} value={fullName} onChange={e => setFullName(e.target.value)} /></FormField>
        <FormField label="Email"><input type="email" className={inp()} value={email} onChange={e => setEmail(e.target.value)} /></FormField>
        <FormField label="Password (min 10 chars)"><input type="password" className={inp()} value={password} onChange={e => setPassword(e.target.value)} /></FormField>
        <FormField label="Roles">
          <div className="grid grid-cols-2 gap-1.5 max-h-48 overflow-y-auto">
            {roles.map(r => (
              <label key={r.id} className="flex items-center gap-2 text-sm cursor-pointer">
                <input type="checkbox" checked={selectedRoles.includes(r.name)}
                  onChange={e => setSelectedRoles(prev => e.target.checked ? [...prev, r.name] : prev.filter(x => x !== r.name))} />
                {r.name}
              </label>
            ))}
          </div>
        </FormField>
        {err && <ErrMsg msg={err} />}
        <div className="flex justify-end gap-2 pt-2">
          <button onClick={onClose} className="rounded-lg border border-slate-200 px-3 py-2 text-sm hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700">Cancel</button>
          <button onClick={submit} disabled={loading} className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
            {loading ? 'Creating…' : 'Create'}
          </button>
        </div>
      </div>
    </div>
  );
}

// ── Roles Tab ──────────────────────────────────────────────────────────────────

function RolesTab() {
  const [roles, setRoles] = useState<RoleItem[]>([]);
  const [allPermissions, setAllPermissions] = useState<PermissionItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [editRole, setEditRole] = useState<RoleItem | null>(null);
  const [showCreate, setShowCreate] = useState(false);
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');
  const [createForm, setCreateForm] = useState({ name: '', description: '', authorityLevel: 99 });
  const [editForm, setEditForm] = useState({ name: '', description: '', authorityLevel: 99 });
  const [editPerms, setEditPerms] = useState<string[]>([]);

  const reload = useCallback(() => {
    setLoading(true);
    Promise.all([rolesApi.list(), rolesApi.permissions()])
      .then(([r, p]) => { setRoles(r); setAllPermissions(p); })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { reload(); }, [reload]);

  const openEdit = (r: RoleItem) => {
    setEditRole(r);
    setEditForm({ name: r.name, description: r.description, authorityLevel: r.authorityLevel });
    setEditPerms([...r.permissions]);
  };

  const handleCreate = async () => {
    if (!createForm.name.trim()) { setErr('Role name is required'); return; }
    setSaving(true); setErr('');
    try {
      await rolesApi.create({ name: createForm.name.trim(), description: createForm.description.trim(), authorityLevel: createForm.authorityLevel });
      setShowCreate(false);
      setCreateForm({ name: '', description: '', authorityLevel: 99 });
      reload();
    } catch (e: any) { setErr(e?.response?.data?.message ?? 'Failed to create role'); }
    finally { setSaving(false); }
  };

  const handleUpdate = async () => {
    if (!editRole) return;
    setSaving(true); setErr('');
    try {
      await rolesApi.update(editRole.id, { name: editForm.name.trim(), description: editForm.description.trim(), authorityLevel: editForm.authorityLevel });
      await rolesApi.setPermissions(editRole.id, editPerms);
      setEditRole(null);
      reload();
    } catch (e: any) { setErr(e?.response?.data?.message ?? 'Failed to update role'); }
    finally { setSaving(false); }
  };

  const toggleRoleActive = async (r: RoleItem) => {
    try {
      if (r.isActive) await rolesApi.deactivate(r.id);
      else await rolesApi.activate(r.id);
      reload();
    } catch (e: any) { alert(e?.response?.data?.message ?? 'Failed'); }
  };

  const togglePermInEdit = (key: string) => {
    setEditPerms(prev => prev.includes(key) ? prev.filter(p => p !== key) : [...prev, key]);
  };

  const sortedRoles = [...roles].sort((a, b) => a.authorityLevel - b.authorityLevel || a.name.localeCompare(b.name));

  const permsByModule = allPermissions.reduce<Record<string, PermissionItem[]>>((acc, p) => {
    if (!acc[p.module]) acc[p.module] = [];
    acc[p.module].push(p);
    return acc;
  }, {});

  const copyRoleSummary = async () => {
    const text = sortedRoles
      .map(r => `${r.name} (L${r.authorityLevel}) - ${r.permissions.length} permissions${r.isSystem ? ' - system' : ''}${r.isActive ? '' : ' - inactive'}`)
      .join('\n');
    try {
      await navigator.clipboard.writeText(text);
      alert('Role summary copied to clipboard.');
    } catch {
      alert('Copy failed. Please copy from the visible summary.');
    }
  };

  return (
    <div className="space-y-4">
      <div className="rounded-2xl border border-slate-200 bg-slate-50/80 p-4 dark:border-slate-700 dark:bg-slate-800/30">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <p className="text-xs font-semibold uppercase tracking-wide text-violet-600 dark:text-violet-400">Role ladder snapshot</p>
            <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-200">Authority levels and permission counts</h3>
          </div>
          <div className="flex items-center gap-2">
            <button type="button" onClick={copyRoleSummary}
              className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-white dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-700">
              Copy Summary
            </button>
            <button type="button" onClick={() => { setShowCreate(true); setErr(''); }}
              className="flex items-center gap-1.5 rounded-lg bg-violet-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-violet-700">
              <Plus className="h-4 w-4" /> Create Role
            </button>
          </div>
        </div>
        <div className="mt-4 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
          {sortedRoles.map(r => (
            <div key={r.id} className="rounded-xl border border-slate-200 bg-white px-3 py-2 shadow-sm dark:border-slate-700 dark:bg-slate-900/60">
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="inline-flex h-5 w-5 items-center justify-center rounded-full bg-violet-100 text-[10px] font-bold text-violet-700 dark:bg-violet-900/40 dark:text-violet-300">
                      {r.authorityLevel}
                    </span>
                    <p className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">{r.name}</p>
                  </div>
                  <p className="mt-0.5 truncate text-xs text-slate-500">{r.description}</p>
                </div>
                <span className="shrink-0 rounded-full bg-violet-50 px-2 py-0.5 text-[10px] font-medium text-violet-700 dark:bg-violet-900/30 dark:text-violet-300">
                  {r.permissions.length} perms
                </span>
              </div>
              <div className="mt-2 flex flex-wrap gap-1">
                {!r.isActive && <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[10px] text-slate-500 dark:bg-slate-700">Inactive</span>}
                {r.isSystem && <span className="rounded-full bg-blue-50 px-2 py-0.5 text-[10px] text-blue-600 dark:bg-blue-900/30 dark:text-blue-400">System</span>}
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* Create Modal */}
      {showCreate && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
          <div className="w-full max-w-md rounded-2xl bg-white p-6 shadow-xl dark:bg-slate-900">
            <h3 className="text-base font-semibold text-slate-800 dark:text-slate-200 mb-4">Create Role</h3>
            <div className="space-y-3">
              <FormField label="Role Name *">
                <input className={inp()} value={createForm.name} onChange={e => setCreateForm(f => ({ ...f, name: e.target.value }))} placeholder="e.g. Regional HR" />
              </FormField>
              <FormField label="Description">
                <input className={inp()} value={createForm.description} onChange={e => setCreateForm(f => ({ ...f, description: e.target.value }))} placeholder="Brief description" />
              </FormField>
              <FormField label="Authority Level (1=highest, 99=lowest)">
                <input type="number" title="Authority Level" placeholder="99" className={inp()} value={createForm.authorityLevel} min={1} max={99}
                  onChange={e => setCreateForm(f => ({ ...f, authorityLevel: parseInt(e.target.value) || 99 }))} />
              </FormField>
              {err && <ErrMsg msg={err} />}
            </div>
            <div className="flex justify-end gap-2 mt-4">
              <button type="button" onClick={() => setShowCreate(false)} className="px-4 py-2 text-sm text-slate-600 hover:text-slate-800">Cancel</button>
              <button type="button" onClick={handleCreate} disabled={saving} className="px-4 py-2 rounded-lg bg-violet-600 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-50">
                {saving ? 'Creating…' : 'Create'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Edit Modal */}
      {editRole && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
          <div className="w-full max-w-2xl rounded-2xl bg-white p-6 shadow-xl dark:bg-slate-900 max-h-[90vh] overflow-y-auto">
            <h3 className="text-base font-semibold text-slate-800 dark:text-slate-200 mb-4">Edit Role — {editRole.name}</h3>
            <div className="space-y-3 mb-4">
              <FormField label="Role Name">
                <input title="Role Name" placeholder="e.g. Regional HR" className={inp()} value={editForm.name} disabled={!editRole.isEditable}
                  onChange={e => setEditForm(f => ({ ...f, name: e.target.value }))} />
              </FormField>
              <FormField label="Description">
                <input title="Description" placeholder="Brief description" className={inp()} value={editForm.description} onChange={e => setEditForm(f => ({ ...f, description: e.target.value }))} />
              </FormField>
              <FormField label="Authority Level">
                <input type="number" title="Authority Level" placeholder="99" className={inp()} value={editForm.authorityLevel} min={1} max={99}
                  onChange={e => setEditForm(f => ({ ...f, authorityLevel: parseInt(e.target.value) || 99 }))} />
              </FormField>
            </div>
            <div className="mb-4">
              <p className="text-xs font-semibold text-slate-500 uppercase tracking-wide mb-2">Permissions ({editPerms.length} selected)</p>
              <div className="space-y-3 max-h-72 overflow-y-auto border border-slate-200 dark:border-slate-700 rounded-xl p-3">
                {Object.entries(permsByModule).sort().map(([module, perms]) => (
                  <div key={module}>
                    <p className="text-xs font-semibold text-slate-600 dark:text-slate-400 mb-1">{module}</p>
                    <div className="grid grid-cols-2 gap-1">
                      {perms.map(p => (
                        <label key={p.id} className="flex items-center gap-2 cursor-pointer text-xs">
                          <input type="checkbox" checked={editPerms.includes(p.key)} onChange={() => togglePermInEdit(p.key)}
                            className="rounded border-slate-300 text-violet-600 focus:ring-violet-500" />
                          <span className="font-mono text-violet-700 dark:text-violet-400 truncate" title={p.description}>{p.key}</span>
                        </label>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            </div>
            {err && <ErrMsg msg={err} />}
            <div className="flex justify-end gap-2">
              <button type="button" onClick={() => setEditRole(null)} className="px-4 py-2 text-sm text-slate-600 hover:text-slate-800">Cancel</button>
              <button type="button" onClick={handleUpdate} disabled={saving} className="px-4 py-2 rounded-lg bg-violet-600 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-50">
                {saving ? 'Saving…' : 'Save Changes'}
              </button>
            </div>
          </div>
        </div>
      )}

      {loading ? <p className="text-sm text-slate-500">Loading…</p> : (
        <div className="space-y-2">
          {roles.map(r => (
            <div key={r.id} className={`rounded-xl border ${r.isActive ? 'border-slate-200 dark:border-slate-700' : 'border-slate-200 dark:border-slate-700 opacity-60'}`}>
              <div className="flex items-center gap-3 px-4 py-3">
                <button type="button" onClick={() => setExpanded(prev => prev === r.id ? null : r.id)} className="flex-1 text-left min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="inline-flex h-5 w-5 items-center justify-center rounded-full bg-violet-100 text-[10px] font-bold text-violet-700 dark:bg-violet-900/40 dark:text-violet-300 shrink-0">
                      {r.authorityLevel}
                    </span>
                    <p className="font-medium text-slate-800 dark:text-slate-200 truncate">{r.name}</p>
                    {!r.isActive && <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[10px] text-slate-500 dark:bg-slate-700">Inactive</span>}
                    {r.isSystem && <span className="rounded-full bg-blue-50 px-2 py-0.5 text-[10px] text-blue-600 dark:bg-blue-900/30 dark:text-blue-400">System</span>}
                  </div>
                  <p className="text-xs text-slate-500 mt-0.5 truncate">{r.description}</p>
                </button>
                <span className="text-xs text-violet-600 font-medium shrink-0">{r.permissions.length} perms</span>
                <div className="flex items-center gap-1 shrink-0">
                  <button type="button" title="Edit role" onClick={() => openEdit(r)}
                    className="h-7 w-7 flex items-center justify-center rounded-lg text-slate-400 hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-slate-700">
                    <Edit2 className="h-3.5 w-3.5" />
                  </button>
                  {!r.isSystem && (
                    <button type="button" title={r.isActive ? 'Deactivate' : 'Activate'} onClick={() => toggleRoleActive(r)}
                      className={`h-7 w-7 flex items-center justify-center rounded-lg ${r.isActive ? 'text-amber-500 hover:bg-amber-50 dark:hover:bg-amber-900/20' : 'text-green-500 hover:bg-green-50 dark:hover:bg-green-900/20'}`}>
                      {r.isActive ? <PowerOff className="h-3.5 w-3.5" /> : <Power className="h-3.5 w-3.5" />}
                    </button>
                  )}
                </div>
              </div>
              {expanded === r.id && (
                <div className="border-t border-slate-200 dark:border-slate-700 px-4 py-3">
                  <div className="flex flex-wrap gap-1">
                    {r.permissions.map(p => (
                      <span key={p} className="rounded bg-violet-50 px-2 py-0.5 text-xs text-violet-700 dark:bg-violet-900/30 dark:text-violet-300">{p}</span>
                    ))}
                    {r.permissions.length === 0 && <span className="text-xs text-slate-400">No permissions assigned</span>}
                  </div>
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Permissions Tab ────────────────────────────────────────────────────────────

function PermissionsTab() {
  const [permissions, setPermissions] = useState<PermissionItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');

  useEffect(() => {
    rolesApi.permissions().then(setPermissions).catch(() => {}).finally(() => setLoading(false));
  }, []);

  const grouped = permissions
    .filter(p => !search || p.key.includes(search) || p.description.toLowerCase().includes(search.toLowerCase()))
    .reduce<Record<string, PermissionItem[]>>((acc, p) => {
      if (!acc[p.module]) acc[p.module] = [];
      acc[p.module].push(p);
      return acc;
    }, {});

  return (
    <div className="space-y-4">
      <div className="relative">
        <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
        <input className={inp('pl-9 max-w-xs')} placeholder="Filter permissions…" value={search} onChange={e => setSearch(e.target.value)} />
      </div>
      {loading ? <p className="text-sm text-slate-500">Loading…</p> : Object.entries(grouped).sort().map(([module, perms]) => (
        <div key={module} className="rounded-xl border border-slate-200 dark:border-slate-700 overflow-hidden">
          <div className="bg-slate-50 dark:bg-slate-800/60 px-4 py-2.5 border-b border-slate-200 dark:border-slate-700">
            <h4 className="text-sm font-semibold text-slate-700 dark:text-slate-300">{module}</h4>
          </div>
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {perms.map(p => (
              <div key={p.id} className="flex items-center justify-between px-4 py-2.5">
                <div>
                  <p className="text-sm font-mono text-violet-700 dark:text-violet-400">{p.key}</p>
                  <p className="text-xs text-slate-500">{p.description}</p>
                </div>
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

// ── Permission Matrix Tab ─────────────────────────────────────────────────────

function PermissionMatrixTab() {
  const [matrix, setMatrix] = useState<PermissionMatrix | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [moduleFilter, setModuleFilter] = useState('');
  const [overrides, setOverrides] = useState<Record<string, Record<string, boolean>>>({});

  const load = useCallback(() => {
    setLoading(true);
    rolesApi.getMatrix()
      .then(m => {
        setMatrix(m);
        const init: Record<string, Record<string, boolean>> = {};
        m.matrix.forEach(row => { init[row.permissionKey] = { ...row.roles }; });
        setOverrides(init);
        setDirty(false);
      })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { load(); }, [load]);

  const toggle = (permKey: string, roleId: string) => {
    setOverrides(prev => ({
      ...prev,
      [permKey]: { ...prev[permKey], [roleId]: !prev[permKey]?.[roleId] },
    }));
    setDirty(true);
  };

  const save = async () => {
    if (!matrix) return;
    setSaving(true);
    try {
      const rolePermissions: Record<string, string[]> = {};
      matrix.roles.forEach(role => {
        rolePermissions[role.id] = matrix.matrix
          .filter(row => overrides[row.permissionKey]?.[role.id] ?? row.roles[role.id])
          .map(row => row.permissionKey);
      });
      await rolesApi.saveMatrix(rolePermissions);
      setDirty(false);
      load();
    } catch (e: any) { alert(e?.response?.data?.message ?? 'Failed to save'); }
    finally { setSaving(false); }
  };

  const modules = matrix ? [...new Set(matrix.matrix.map(r => r.module))].sort() : [];
  const filteredMatrix = matrix ? matrix.matrix.filter(r => !moduleFilter || r.module === moduleFilter) : [];

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3 flex-wrap">
        <div className="flex items-center gap-3">
          <select title="Filter by module" className={inp('w-48')} value={moduleFilter} onChange={e => setModuleFilter(e.target.value)}>
            <option value="">All Modules</option>
            {modules.map(m => <option key={m} value={m}>{m}</option>)}
          </select>
          <p className="text-xs text-slate-400">{filteredMatrix.length} permissions</p>
        </div>
        {dirty && (
          <button type="button" onClick={save} disabled={saving}
            className="flex items-center gap-1.5 rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-50">
            {saving ? 'Saving…' : 'Save Changes'}
          </button>
        )}
      </div>

      {loading ? <p className="text-sm text-slate-500">Loading matrix…</p> : !matrix ? null : (
        <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-700">
          <table className="w-full text-xs border-collapse">
            <thead>
              <tr className="bg-slate-50 dark:bg-slate-800/60">
                <th className="sticky left-0 z-10 bg-slate-50 dark:bg-slate-800/60 px-3 py-2.5 text-left font-semibold text-slate-600 dark:text-slate-400 min-w-[200px] border-b border-r border-slate-200 dark:border-slate-700">
                  Permission
                </th>
                {matrix.roles.map(role => (
                  <th key={role.id} className="px-2 py-2.5 text-center font-medium text-slate-600 dark:text-slate-400 border-b border-slate-200 dark:border-slate-700 min-w-[80px]">
                    <span title={role.description} className="block truncate max-w-[76px]">{role.name}</span>
                    <span className="text-[9px] text-slate-400 font-normal block">L{role.authorityLevel}</span>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {filteredMatrix.map((row, i) => (
                <tr key={row.permissionKey} className={i % 2 === 0 ? 'bg-white dark:bg-slate-900' : 'bg-slate-50/50 dark:bg-slate-800/20'}>
                  <td className="sticky left-0 z-10 bg-inherit px-3 py-1.5 border-r border-slate-200 dark:border-slate-700">
                    <p className="font-mono text-violet-700 dark:text-violet-400 truncate">{row.permissionKey}</p>
                    <p className="text-slate-400 truncate text-[10px]">{row.description}</p>
                  </td>
                  {matrix.roles.map(role => {
                    const checked = overrides[row.permissionKey]?.[role.id] ?? row.roles[role.id] ?? false;
                    return (
                      <td key={role.id} className="text-center px-2 py-1.5 border-b border-slate-100 dark:border-slate-800/50">
                        <input
                          type="checkbox"
                          title={`${role.name} — ${row.permissionKey}`}
                          checked={checked}
                          onChange={() => toggle(row.permissionKey, role.id)}
                          className="h-3.5 w-3.5 rounded border-slate-300 text-violet-600 focus:ring-violet-500"
                        />
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Permission Grantors Tab ────────────────────────────────────────────────────

function PermissionGrantorsTab() {
  const [grantors, setGrantors] = useState<PermissionGrantorRecord[]>([]);
  const [users, setUsers] = useState<UserListItem[]>([]);
  const [permissions, setPermissions] = useState<PermissionItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [showAdd, setShowAdd] = useState(false);
  const [form, setForm] = useState({ grantorUserId: '', permissionScope: 'all', canSubDelegate: false, reason: '', expiresAtUtc: '' });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');

  const SCOPE_PRESETS = [
    { label: 'All permissions', value: 'all' },
    { label: 'Leave module', value: 'leave' },
    { label: 'Attendance module', value: 'attendance' },
    { label: 'Payroll module', value: 'payroll' },
    { label: 'Recruitment module', value: 'recruitment' },
    { label: 'Performance module', value: 'performance' },
    { label: 'Loans module', value: 'loans' },
    { label: 'Reports module', value: 'reports' },
    { label: 'Custom (enter keys below)', value: '' },
  ];

  const load = () => {
    setLoading(true);
    Promise.all([
      grantorsApi.list(),
      usersApi.list({ pageSize: 100 }),
      rolesApi.permissions(),
    ]).then(([g, u, p]) => {
      setGrantors(g);
      setUsers(u.items);
      setPermissions(p);
    }).catch(() => {}).finally(() => setLoading(false));
  };
  useEffect(() => { load(); }, []);

  const add = async () => {
    if (!form.grantorUserId) { setErr('Select a user.'); return; }
    if (!form.permissionScope.trim()) { setErr('Scope is required.'); return; }
    setSaving(true); setErr('');
    try {
      await grantorsApi.add({
        grantorUserId: form.grantorUserId,
        permissionScope: form.permissionScope.trim(),
        canSubDelegate: form.canSubDelegate,
        reason: form.reason || undefined,
        expiresAtUtc: form.expiresAtUtc || undefined,
      });
      setShowAdd(false);
      setForm({ grantorUserId: '', permissionScope: 'all', canSubDelegate: false, reason: '', expiresAtUtc: '' });
      load();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setErr(msg ?? 'Failed to add grantor.');
    }
    setSaving(false);
  };

  const revoke = async (id: string) => {
    try { await grantorsApi.revoke(id); load(); }
    catch { alert('Failed to revoke grantor authority.'); }
  };

  const selectedPreset = SCOPE_PRESETS.find(p => p.value === form.permissionScope);
  const isCustomScope = !SCOPE_PRESETS.some(p => p.value === form.permissionScope && p.value !== '');

  // Group all permission keys by module for scope reference
  const permModules = [...new Set(permissions.map(p => p.module))].sort();

  return (
    <div className="space-y-4">
      <div className="rounded-lg border border-violet-200 bg-violet-50 dark:bg-violet-900/10 dark:border-violet-800 px-4 py-3 text-sm text-violet-800 dark:text-violet-300">
        <strong>How it works:</strong> Designate users as Permission Grantors. They can then manually Allow or Deny individual permissions for other users — without needing the Admin role. Enable <em>Can Sub-Delegate</em> to let them further designate others within their scope.
      </div>
      <div className="flex justify-end">
        <button type="button" onClick={() => setShowAdd(true)} className="flex items-center gap-2 rounded-lg bg-violet-600 px-3 py-2 text-sm font-medium text-white hover:bg-violet-700">
          <Plus className="h-4 w-4" /> Designate Grantor
        </button>
      </div>

      <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-700">
        <table className="w-full text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/60">
            <tr>
              {['User', 'Permission Scope', 'Can Sub-Delegate', 'Expires', 'Reason', 'Actions'].map(h => (
                <th key={h} className="px-4 py-2.5 text-left text-xs font-semibold text-slate-500">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
            {loading ? (
              <tr><td colSpan={6} className="py-8 text-center text-sm text-slate-500">Loading…</td></tr>
            ) : grantors.length === 0 ? (
              <tr><td colSpan={6} className="py-8 text-center text-sm text-slate-500">No permission grantors designated yet.</td></tr>
            ) : grantors.map(g => (
              <tr key={g.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/40">
                <td className="px-4 py-3">
                  <p className="font-medium text-slate-800 dark:text-slate-200">{g.grantorName}</p>
                  <p className="text-xs text-slate-500">{g.grantorEmail}</p>
                </td>
                <td className="px-4 py-3">
                  <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${g.permissionScope === 'all' ? 'bg-violet-100 text-violet-700 dark:bg-violet-900/30 dark:text-violet-300' : 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400'}`}>
                    {g.permissionScope === 'all' ? 'All Permissions' : g.permissionScope}
                  </span>
                </td>
                <td className="px-4 py-3">
                  {g.canSubDelegate
                    ? <CheckCircle className="h-4 w-4 text-green-500" />
                    : <XCircle className="h-4 w-4 text-slate-300" />}
                </td>
                <td className="px-4 py-3 text-xs text-slate-500">{g.expiresAtUtc ? fmtDate(g.expiresAtUtc) : 'Never'}</td>
                <td className="px-4 py-3 text-xs text-slate-500">{g.reason || '—'}</td>
                <td className="px-4 py-3">
                  <button type="button" onClick={() => revoke(g.id)} className="rounded px-2 py-1 text-xs text-red-500 hover:bg-red-50 dark:hover:bg-red-900/20">
                    Revoke
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {showAdd && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="w-full max-w-lg rounded-2xl bg-white p-6 shadow-xl dark:bg-slate-900 space-y-4">
            <h3 className="text-base font-semibold text-slate-800 dark:text-slate-200">Designate Permission Grantor</h3>

            <FormField label="User *">
              <select title="Select User" className={inp()} value={form.grantorUserId} onChange={e => setForm(f => ({ ...f, grantorUserId: e.target.value }))}>
                <option value="">Select a user…</option>
                {users.map(u => <option key={u.id} value={u.id}>{u.fullName} — {u.email}</option>)}
              </select>
            </FormField>

            <FormField label="Permission Scope *">
              <select title="Permission Scope" className={inp()} value={selectedPreset ? form.permissionScope : ''} onChange={e => setForm(f => ({ ...f, permissionScope: e.target.value }))}>
                {SCOPE_PRESETS.map(p => <option key={p.value} value={p.value}>{p.label}</option>)}
              </select>
            </FormField>

            {(isCustomScope || form.permissionScope === '') && (
              <FormField label="Custom scope — module name or comma-separated keys">
                <input className={inp()} value={isCustomScope ? form.permissionScope : ''} placeholder="e.g. leave  or  leave.read,leave.write"
                  onChange={e => setForm(f => ({ ...f, permissionScope: e.target.value }))} />
                <p className="text-xs text-slate-400 mt-1">
                  Available modules: {permModules.join(', ')}
                </p>
              </FormField>
            )}

            <label className="flex items-center gap-3 cursor-pointer">
              <input type="checkbox" checked={form.canSubDelegate} onChange={e => setForm(f => ({ ...f, canSubDelegate: e.target.checked }))} />
              <div>
                <p className="text-sm text-slate-700 dark:text-slate-300 font-medium">Can Sub-Delegate</p>
                <p className="text-xs text-slate-500">Allow this user to further designate others as grantors within their scope</p>
              </div>
            </label>

            <div className="grid grid-cols-2 gap-3">
              <FormField label="Reason"><input className={inp()} value={form.reason} onChange={e => setForm(f => ({ ...f, reason: e.target.value }))} placeholder="e.g. HR delegation" /></FormField>
              <FormField label="Expires (optional)"><input type="date" title="Expiry date" className={inp()} value={form.expiresAtUtc} onChange={e => setForm(f => ({ ...f, expiresAtUtc: e.target.value }))} /></FormField>
            </div>

            {err && <ErrMsg msg={err} />}
            <div className="flex justify-end gap-2 pt-2">
              <button type="button" onClick={() => { setShowAdd(false); setErr(''); }} className="rounded-lg border border-slate-200 px-3 py-2 text-sm hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700">Cancel</button>
              <button type="button" onClick={add} disabled={saving} className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
                {saving ? 'Saving…' : 'Designate'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// ── Delegations Tab ────────────────────────────────────────────────────────────

function DelegationsTab() {
  const [items, setItems] = useState<ApprovalDelegation[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [form, setForm] = useState({ fromEmployeeId: '', toEmployeeId: '', scope: 'All', startDate: '', endDate: '', reason: '' });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');

  const load = () => {
    setLoading(true);
    delegationsApi.list().then(setItems).catch(() => {}).finally(() => setLoading(false));
  };
  useEffect(() => { load(); }, []);

  const create = async () => {
    if (!form.fromEmployeeId || !form.toEmployeeId || !form.startDate || !form.endDate) { setErr('All required fields must be filled.'); return; }
    setSaving(true); setErr('');
    try {
      await delegationsApi.create({ fromEmployeeId: +form.fromEmployeeId, toEmployeeId: +form.toEmployeeId, scope: form.scope, startDate: form.startDate, endDate: form.endDate, reason: form.reason });
      setShowCreate(false); setForm({ fromEmployeeId: '', toEmployeeId: '', scope: 'All', startDate: '', endDate: '', reason: '' }); load();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setErr(msg ?? 'Failed to create delegation.');
    }
    setSaving(false);
  };

  const cancel = async (id: string) => {
    try { await delegationsApi.cancel(id); load(); } catch { alert('Failed to cancel delegation.'); }
  };

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <button type="button" onClick={() => setShowCreate(true)} className="flex items-center gap-2 rounded-lg bg-violet-600 px-3 py-2 text-sm font-medium text-white hover:bg-violet-700">
          <Plus className="h-4 w-4" /> New Delegation
        </button>
      </div>
      <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-700">
        <table className="w-full text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/60">
            <tr>
              {['From Emp', 'To Emp', 'Scope', 'Period', 'Status', 'Reason', 'Actions'].map(h => (
                <th key={h} className="px-4 py-2.5 text-left text-xs font-semibold text-slate-500">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
            {loading ? (
              <tr><td colSpan={7} className="py-8 text-center text-sm text-slate-500">Loading…</td></tr>
            ) : items.length === 0 ? (
              <tr><td colSpan={7} className="py-8 text-center text-sm text-slate-500">No delegations found.</td></tr>
            ) : items.map(d => (
              <tr key={d.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/40">
                <td className="px-4 py-3 text-slate-700 dark:text-slate-300">#{d.fromEmployeeId}</td>
                <td className="px-4 py-3 text-slate-700 dark:text-slate-300">#{d.toEmployeeId}</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-400">{d.scope}</td>
                <td className="px-4 py-3 text-xs text-slate-500">{d.startDate} → {d.endDate}</td>
                <td className="px-4 py-3"><StatusBadge value={d.status} /></td>
                <td className="px-4 py-3 text-xs text-slate-500">{d.reason || '—'}</td>
                <td className="px-4 py-3">
                  {d.status === 'Active' && (
                    <button type="button" onClick={() => cancel(d.id)} className="rounded px-2 py-1 text-xs text-red-500 hover:bg-red-50 dark:hover:bg-red-900/20">
                      Cancel
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {showCreate && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="w-full max-w-md rounded-2xl bg-white p-6 shadow-xl dark:bg-slate-900 space-y-4">
            <h3 className="text-base font-semibold text-slate-800 dark:text-slate-200">New Delegation</h3>
            <div className="grid grid-cols-2 gap-3">
              <FormField label="From Employee ID *"><input className={inp()} type="number" placeholder="e.g. 1001" value={form.fromEmployeeId} onChange={e => setForm(f => ({ ...f, fromEmployeeId: e.target.value }))} /></FormField>
              <FormField label="To Employee ID *"><input className={inp()} type="number" placeholder="e.g. 1002" value={form.toEmployeeId} onChange={e => setForm(f => ({ ...f, toEmployeeId: e.target.value }))} /></FormField>
              <FormField label="Scope"><input className={inp()} placeholder="e.g. All, Leave, Payroll" value={form.scope} onChange={e => setForm(f => ({ ...f, scope: e.target.value }))} /></FormField>
              <FormField label="Reason"><input className={inp()} placeholder="Optional reason…" value={form.reason} onChange={e => setForm(f => ({ ...f, reason: e.target.value }))} /></FormField>
              <FormField label="Start Date *"><input type="date" title="Start date" className={inp()} value={form.startDate} onChange={e => setForm(f => ({ ...f, startDate: e.target.value }))} /></FormField>
              <FormField label="End Date *"><input type="date" title="End date" className={inp()} value={form.endDate} onChange={e => setForm(f => ({ ...f, endDate: e.target.value }))} /></FormField>
            </div>
            {err && <ErrMsg msg={err} />}
            <div className="flex justify-end gap-2 pt-2">
              <button type="button" onClick={() => setShowCreate(false)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700">Cancel</button>
              <button type="button" onClick={create} disabled={saving} className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
                {saving ? 'Creating…' : 'Create'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// ── Authorities Tab ────────────────────────────────────────────────────────────

function AuthoritiesTab() {
  const [items, setItems] = useState<ApprovalAuthority[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [form, setForm] = useState({ employeeId: '', authorityScope: '', approverRole: '', amountLimit: '', currency: 'USD', canFinalApprove: false });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');

  const load = () => {
    setLoading(true);
    authoritiesApi.list().then(setItems).catch(() => {}).finally(() => setLoading(false));
  };
  useEffect(() => { load(); }, []);

  const create = async () => {
    if (!form.employeeId || !form.authorityScope || !form.approverRole) { setErr('Required fields missing.'); return; }
    setSaving(true); setErr('');
    try {
      await authoritiesApi.create({ employeeId: +form.employeeId, authorityScope: form.authorityScope, approverRole: form.approverRole, amountLimit: form.amountLimit ? +form.amountLimit : undefined, currency: form.currency, canFinalApprove: form.canFinalApprove });
      setShowCreate(false); setForm({ employeeId: '', authorityScope: '', approverRole: '', amountLimit: '', currency: 'USD', canFinalApprove: false }); load();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setErr(msg ?? 'Failed to create authority.');
    }
    setSaving(false);
  };

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <button type="button" onClick={() => setShowCreate(true)} className="flex items-center gap-2 rounded-lg bg-violet-600 px-3 py-2 text-sm font-medium text-white hover:bg-violet-700">
          <Plus className="h-4 w-4" /> New Authority
        </button>
      </div>
      <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-700">
        <table className="w-full text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/60">
            <tr>
              {['Employee', 'Scope', 'Role', 'Limit', 'Final Approver', 'Status'].map(h => (
                <th key={h} className="px-4 py-2.5 text-left text-xs font-semibold text-slate-500">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
            {loading ? (
              <tr><td colSpan={6} className="py-8 text-center text-sm text-slate-500">Loading…</td></tr>
            ) : items.length === 0 ? (
              <tr><td colSpan={6} className="py-8 text-center text-sm text-slate-500">No authorities configured.</td></tr>
            ) : items.map(a => (
              <tr key={a.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/40">
                <td className="px-4 py-3 text-slate-700 dark:text-slate-300">#{a.employeeId}</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-400">{a.authorityScope}</td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-400">{a.approverRole}</td>
                <td className="px-4 py-3 text-xs text-slate-500">{a.amountLimit ? `${a.currency} ${a.amountLimit.toLocaleString()}` : 'No limit'}</td>
                <td className="px-4 py-3">
                  {a.canFinalApprove ? <CheckCircle className="h-4 w-4 text-green-500" /> : <XCircle className="h-4 w-4 text-slate-300" />}
                </td>
                <td className="px-4 py-3">
                  <StatusBadge value={a.isActive ? 'Active' : 'Inactive'} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {showCreate && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="w-full max-w-md rounded-2xl bg-white p-6 shadow-xl dark:bg-slate-900 space-y-4">
            <h3 className="text-base font-semibold text-slate-800 dark:text-slate-200">New Approval Authority</h3>
            <FormField label="Employee ID *"><input className={inp()} type="number" placeholder="e.g. 1001" value={form.employeeId} onChange={e => setForm(f => ({ ...f, employeeId: e.target.value }))} /></FormField>
            <FormField label="Authority Scope *"><input className={inp()} value={form.authorityScope} onChange={e => setForm(f => ({ ...f, authorityScope: e.target.value }))} placeholder="e.g. Leave, Payroll, Overtime" /></FormField>
            <FormField label="Approver Role *"><input className={inp()} value={form.approverRole} onChange={e => setForm(f => ({ ...f, approverRole: e.target.value }))} placeholder="e.g. HR Manager, Finance Approver" /></FormField>
            <div className="grid grid-cols-2 gap-3">
              <FormField label="Amount Limit"><input className={inp()} type="number" value={form.amountLimit} onChange={e => setForm(f => ({ ...f, amountLimit: e.target.value }))} placeholder="Leave empty for no limit" /></FormField>
              <FormField label="Currency"><input className={inp()} value={form.currency} onChange={e => setForm(f => ({ ...f, currency: e.target.value }))} placeholder="e.g. USD" /></FormField>
            </div>
            <label className="flex items-center gap-2 cursor-pointer">
              <input type="checkbox" checked={form.canFinalApprove} onChange={e => setForm(f => ({ ...f, canFinalApprove: e.target.checked }))} />
              <span className="text-sm text-slate-700 dark:text-slate-300">Can Final Approve</span>
            </label>
            {err && <ErrMsg msg={err} />}
            <div className="flex justify-end gap-2 pt-2">
              <button type="button" onClick={() => setShowCreate(false)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700">Cancel</button>
              <button type="button" onClick={create} disabled={saving} className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
                {saving ? 'Creating…' : 'Create'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// ── Security Settings Tab ──────────────────────────────────────────────────────

function SecurityTab() {
  const [settings, setSettings] = useState<SecuritySetting | null>(null);
  const [form, setForm] = useState<Partial<SecuritySetting>>({});
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [msg, setMsg] = useState('');

  useEffect(() => {
    securitySettingsApi.get().then(s => { setSettings(s); setForm(s); }).catch(() => {}).finally(() => setLoading(false));
  }, []);

  const save = async () => {
    setSaving(true); setMsg('');
    try {
      const updated = await securitySettingsApi.update(form);
      setSettings(updated); setForm(updated); setMsg('Security settings saved.');
    } catch { setMsg('Failed to save settings.'); }
    setSaving(false);
  };

  if (loading) return <p className="text-sm text-slate-500">Loading…</p>;

  const numField = (key: keyof SecuritySetting, label: string, min: number, max: number, info?: string) => (
    <div key={key} className="flex flex-col gap-1">
      <label className="flex items-center gap-1.5 text-xs font-medium text-slate-600 dark:text-slate-400">{label}{info && <InfoTip text={info} fieldKey={`security.${String(key)}`} />}</label>
      <input type="number" min={min} max={max} title={label} className={inp('w-full')}
        value={form[key] as number ?? settings?.[key] as number ?? 0}
        onChange={e => setForm(f => ({ ...f, [key]: +e.target.value }))} />
    </div>
  );

  const boolField = (key: keyof SecuritySetting, label: string) => (
    <label key={key} className="flex items-center gap-3 cursor-pointer">
      <input type="checkbox" checked={!!(form[key] ?? settings?.[key])}
        onChange={e => setForm(f => ({ ...f, [key]: e.target.checked }))} />
      <span className="text-sm text-slate-700 dark:text-slate-300">{label}</span>
    </label>
  );

  return (
    <div className="space-y-6 max-w-2xl">
      <div className="rounded-xl border border-slate-200 dark:border-slate-700 p-5 space-y-4">
        <h4 className="text-sm font-semibold text-slate-700 dark:text-slate-300">Password Policy</h4>
        <div className="grid grid-cols-2 gap-4">
          {numField('passwordMinLength', 'Minimum Length', 6, 64, 'Fewest characters a password may have. 10-12+ recommended; applies to new passwords only.')}
          {numField('passwordExpiryDays', 'Expiry Days (0 = never)', 0, 365, 'Days until staff must change their password. 0 disables forced rotation.')}
          {numField('passwordHistoryCount', 'Password History Count', 0, 24, 'How many previous passwords are remembered and blocked from reuse. 0 allows reuse.')}
        </div>
        <div className="grid grid-cols-2 gap-2">
          {boolField('passwordRequireUppercase', 'Require Uppercase')}
          {boolField('passwordRequireLowercase', 'Require Lowercase')}
          {boolField('passwordRequireDigit', 'Require Digit')}
          {boolField('passwordRequireSpecial', 'Require Special Character')}
        </div>
      </div>

      <div className="rounded-xl border border-slate-200 dark:border-slate-700 p-5 space-y-4">
        <h4 className="text-sm font-semibold text-slate-700 dark:text-slate-300">Lockout Policy</h4>
        <div className="grid grid-cols-2 gap-4">
          {numField('maxFailedLoginAttempts', 'Max Failed Login Attempts', 1, 20, 'Wrong-password tries allowed before the account is temporarily locked. 5 is a common default.')}
          {numField('lockoutDurationMinutes', 'Lockout Duration (minutes)', 5, 1440, 'How long a locked account stays locked before it can try again. Admins can unlock earlier from the Users tab.')}
        </div>
      </div>

      <div className="rounded-xl border border-slate-200 dark:border-slate-700 p-5 space-y-4">
        <h4 className="text-sm font-semibold text-slate-700 dark:text-slate-300">Session Policy</h4>
        <div className="grid grid-cols-2 gap-4">
          {numField('sessionTimeoutMinutes', 'Session Timeout (minutes)', 15, 1440, 'Idle time before a signed-in user is logged out automatically.')}
          {numField('refreshTokenExpiryDays', 'Refresh Token Expiry (days)', 1, 90, 'Maximum days a login can stay remembered without re-entering the password. Lower = more secure, more frequent logins.')}
        </div>
        {boolField('allowMultipleSessions', 'Allow Multiple Concurrent Sessions')}
      </div>

      {msg && <p className={`text-sm ${msg.includes('Failed') ? 'text-red-500' : 'text-green-600'}`}>{msg}</p>}

      <div className="flex justify-end">
        <button type="button" onClick={save} disabled={saving} className="rounded-lg bg-violet-600 px-5 py-2.5 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
          {saving ? 'Saving…' : 'Save Settings'}
        </button>
      </div>
    </div>
  );
}

// ── Audit Logs Tab ─────────────────────────────────────────────────────────────

function AuditLogsTab() {
  const [logs, setLogs] = useState<AuditLogItem[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    identityAuditApi.list({ limit: 100 })
      .then(setLogs)
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  return (
    <div className="space-y-3">
      <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-700">
        <table className="w-full text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/60">
            <tr>
              {['Action', 'Entity', 'Entity ID', 'IP Address', 'Date'].map(h => (
                <th key={h} className="px-4 py-2.5 text-left text-xs font-semibold text-slate-500">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
            {loading ? (
              <tr><td colSpan={5} className="py-8 text-center text-sm text-slate-500">Loading…</td></tr>
            ) : logs.length === 0 ? (
              <tr><td colSpan={5} className="py-8 text-center text-sm text-slate-500">No audit logs found.</td></tr>
            ) : logs.map(l => (
              <tr key={l.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/40">
                <td className="px-4 py-2.5">
                  <span className="font-mono text-xs text-violet-700 dark:text-violet-400">{l.action}</span>
                </td>
                <td className="px-4 py-2.5 text-xs text-slate-600 dark:text-slate-400">{l.entityName}</td>
                <td className="px-4 py-2.5 text-xs text-slate-500 font-mono">{l.entityId ? l.entityId.slice(0, 8) + '…' : '—'}</td>
                <td className="px-4 py-2.5 text-xs text-slate-500">{l.ipAddress || '—'}</td>
                <td className="px-4 py-2.5 text-xs text-slate-500">{fmtDateTime(l.createdAtUtc)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ── Main Page ─────────────────────────────────────────────────────────────────

export function UserManagementPage() {
  const [activeTab, setActiveTab] = useState<Tab>('users');

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-extrabold text-slate-950 dark:text-slate-100">User Management & Access Control</h1>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">Manage users, roles, permissions, delegation, and security policy.</p>
      </div>

      {/* Tab bar */}
      <div className="overflow-x-auto">
        <div className="flex items-center gap-1 border-b border-slate-200 dark:border-slate-700 min-w-max">
          {TABS.map(({ id, label, icon: Icon }) => (
            <button type="button" key={id} onClick={() => setActiveTab(id)}
              className={`flex items-center gap-1.5 px-4 py-2.5 text-sm font-medium border-b-2 transition-colors whitespace-nowrap ${activeTab === id ? 'border-violet-500 text-violet-600 dark:text-violet-400' : 'border-transparent text-slate-500 hover:text-slate-700 dark:hover:text-slate-300'}`}>
              <Icon className="h-4 w-4" />
              {label}
            </button>
          ))}
        </div>
      </div>

      {/* Tab content */}
      <div className="min-h-[400px]">
        {activeTab === 'users' && <UsersTab />}
        {activeTab === 'roles' && <RolesTab />}
        {activeTab === 'permissionMatrix' && <PermissionMatrixTab />}
        {activeTab === 'permissions' && <PermissionsTab />}
        {activeTab === 'grantors' && <PermissionGrantorsTab />}
        {activeTab === 'delegations' && <DelegationsTab />}
        {activeTab === 'authorities' && <AuthoritiesTab />}
        {activeTab === 'security' && <SecurityTab />}
        {activeTab === 'auditLogs' && <AuditLogsTab />}
      </div>
    </div>
  );
}
