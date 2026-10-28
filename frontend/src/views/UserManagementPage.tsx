'use client';

import { useHelpTexts } from '../contexts/HelpTextContext';
import { useEffect, useLayoutEffect, useState, useCallback, useId } from 'react';
import { createPortal } from 'react-dom';
import { useRouter, useSearchParams } from 'next/navigation';
import {
  Shield, Lock, CheckCircle, XCircle,
  RefreshCw, Plus, Search, ChevronLeft, ChevronRight, Eye, EyeOff,
  UserCheck, UserX, Unlock, RotateCcw,
  CheckSquare, MinusSquare, Square, Edit2, Power, PowerOff, Trash2,
  Building2, Circle,
} from 'lucide-react';
import {
  usersApi, rolesApi, delegationsApi, authoritiesApi, securitySettingsApi,
  identityAuditApi, grantorsApi, permissionGrantApi,
  entityGrantsApi, effectiveMfaPolicy,
} from '../api/identity';
import { companiesApi } from '../api/organization';
import client, { notifyApiError } from '../api/client';
import { useCapabilities } from '../hooks/useCapabilities';
import { CapabilityNote } from '../components/setup/CapabilityNote';
import type {
  UserListItem, RoleItem, PermissionItem, ApprovalDelegation,
  ApprovalAuthority, SecuritySetting, AuditLogItem, PermissionGrantorRecord,
  UserAccess, PermissionMatrix, EntityGrant, AccessCeiling, MfaPolicy,
} from '../api/identity';
import type { CompanyDto } from '../api/organization';
import { evaluatePasswordRequirements, type PasswordPolicy } from '../lib/passwordRequirements';
import { assignBlock, canGrantPermission, editBlock, isSelf, localizedRefusal } from '../lib/accessCeiling';
import { localReturnPathOrNull } from '../lib/publicAuth';
import { useLocale } from '../contexts/LocaleContext';
import { useConfirm } from '../components/ui/useConfirm';
import { useCompany } from '../contexts/CompanyContext';
import { LinkEmployeeLoginDialog } from '../components/access/LinkEmployeeLoginDialog';
import { ResetTwoStepDialog } from '../components/access/ResetTwoStepDialog';
import { ResetPasswordDialog } from '../components/access/ResetPasswordDialog';
import { useAuth } from '../contexts/AuthContext';
import { useAppToast } from '../components/ui/AppToast';
import { Toggle } from '../components/ui/Toggle';
import { requestFailureReason } from '../lib/requestFailure';

// ── Shared helpers ─────────────────────────────────────────────────────────────

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
    PendingPasswordSetup: 'bg-sky-50 text-sky-800 ring-1 ring-inset ring-sky-200 dark:bg-sky-900/30 dark:text-sky-300 dark:ring-sky-800',
    Suspended: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300',
    Locked: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300',
    Deactivated: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-400',
    NoLogin: 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400',
    Cancelled: 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400',
  };
  const { t } = useLocale();
  return (
    <span className={`inline-flex items-center whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium ${colors[value] ?? 'bg-slate-100 text-slate-600'}`}>
      {t(STATUS_LABELS[value] ?? value)}
    </span>
  );
}

/** The API's status and access-mode codes, in words. */
const STATUS_LABELS: Record<string, string> = {
  PendingPasswordSetup: 'Password not set yet',
  NoLogin: 'No login',
};
const ACCESS_MODE_LABELS: Record<string, string> = {
  FullPortal: 'Full portal', SelfServiceOnly: 'Self-Service only', ManagerSelfService: 'Manager Self-Service',
  ReadOnlyAuditor: 'Read-only auditor', Mobile: 'Mobile app', KioskOnly: 'Kiosk only', NoLogin: 'No login',
};

/** At most two role names inline; the rest behind "+N", all of them in the tooltip. */
function RoleChips({ roles }: { roles: string[] }) {
  const { t } = useLocale();
  if (!roles.length) return <span className="text-slate-400">—</span>;
  const shown = roles.slice(0, 2);
  const rest = roles.length - shown.length;
  return (
    <span className="flex flex-wrap gap-1" title={roles.join(', ')}>
      {shown.map(r => (
        <span key={r} className="whitespace-nowrap rounded-lg bg-slate-100 px-1.5 py-0.5 text-caption font-medium text-slate-700 dark:bg-slate-700/60 dark:text-slate-300">{r}</span>
      ))}
      {rest > 0 && (
        <span className="whitespace-nowrap rounded-lg bg-violet-50 px-1.5 py-0.5 text-caption font-medium text-violet-700 dark:bg-violet-500/10 dark:text-violet-300"
          aria-label={t('{count} more roles: {names}', { count: rest, names: roles.slice(2).join(', ') })}>
          +{rest}
        </span>
      )}
    </span>
  );
}

function FormField({ label, htmlFor, children }: { label: string; htmlFor?: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={htmlFor} className="text-xs font-medium text-slate-600 dark:text-slate-400">{label}</label>
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

export function UsersTab() {
  const { t } = useLocale();
  const { user: me, hasPermission } = useAuth();
  const toast = useAppToast();
  const router = useRouter();
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
  // Reset another user's password (a reset link, or a temporary password), opened from a row.
  const [resetPasswordFor, setResetPasswordFor] = useState<UserListItem | null>(null);
  const [actionErr, setActionErr] = useState('');
  const [actionLoading, setActionLoading] = useState(false);
  const [editUser, setEditUser] = useState<UserListItem | null>(null);
  const [editFields, setEditFields] = useState({ fullName: '', phoneNumber: '', preferredLanguage: '', timezone: '' });
  const [editErr, setEditErr] = useState('');
  const [editLoading, setEditLoading] = useState(false);
  const [roles, setRoles] = useState<RoleItem[]>([]);
  const [allPermissions, setAllPermissions] = useState<PermissionItem[]>([]);
  // What the server will let THIS caller assign (privilege ceiling); roles above it are greyed out.
  const [ceiling, setCeiling] = useState<AccessCeiling | null>(null);
  // Link a login to its employee record (opened from a row), or invite an employee (opened from the toolbar).
  const [employeeLink, setEmployeeLink] = useState<{ user: UserListItem | null } | null>(null);
  // Reset another user's two-step sign-in (opened from a row).
  const [resetTwoStep, setResetTwoStep] = useState<UserListItem | null>(null);
  // The same permission as every other user action here (AccessController's security.manage), never on
  // yourself, and only for someone who has two-step set up. The server enforces all three again.
  const canManageUsers = hasPermission('security.manage');
  const isMe = (u: UserListItem) => (!!me && me.id.toLowerCase() === u.id.toLowerCase()) || isSelf(ceiling, u.id);
  // The workspace owner's account is changed by nobody else (the server refuses: access_owner_protected).
  const protectedOwner = (u: UserListItem) => u.isOwner === true && !isMe(u);
  // Whether YOU are the owner is read from your own account, not from whichever page of the list is showing.
  const [callerIsOwner, setCallerIsOwner] = useState(false);
  const [ownerCheck, setOwnerCheck] = useState(0);
  useEffect(() => {
    if (!me?.id) return;
    let live = true;
    usersApi.get(me.id).then(u => { if (live) setCallerIsOwner(u?.isOwner === true); }).catch(() => { if (live) setCallerIsOwner(false); });
    return () => { live = false; };
  }, [me?.id, ownerCheck]);
  const meIsOwner = callerIsOwner;
  const canMakeOwner = (u: UserListItem) => meIsOwner && !isMe(u) && u.status === 'Active' && u.roles.includes('Admin');
  const canResetTwoStep = (u: UserListItem) => canManageUsers && u.mfaEnabled === true && !isMe(u) && !protectedOwner(u);

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
    rolesApi.ceiling().then(setCeiling).catch(() => setCeiling(null));
  }, []);
  useEffect(() => {
    if (searchParams?.get('openCreate') === '1') setShowCreate(true);
  }, [searchParams]);

  const createdFromGuidedFlow = () => {
    setShowCreate(false);
    load();
    const returnTo = localReturnPathOrNull(searchParams?.get('returnTo'));
    if (returnTo) router.push(returnTo);
  };

  const deletedView = statusFilter === 'Deleted';
  // Delete is shown disabled, with the reason, where the server would refuse: your own account, or the last active administrator
  // (decided from the list when the whole list is loaded; otherwise the server's refusal is shown).
  const activeAdmins = users.filter(x => x.isActive && x.roles.includes('Admin'));
  const wholeListLoaded = total <= users.length;
  const deleteBlockedReason = (u: UserListItem): string | undefined => {
    if (me?.id && u.id === me.id) return t('You cannot delete your own account.');
    if (u.isActive && u.roles.includes('Admin') && wholeListLoaded && activeAdmins.length <= 1) return t('This is the last active administrator. Make someone else an administrator first.');
    return undefined;
  };
  const [restoringId, setRestoringId] = useState<string | null>(null);
  const restoreUser = async (u: UserListItem) => {
    setRestoringId(u.id);
    try { const r = await usersApi.restore(u.id); toast.success?.(r.message); load(); }
    catch (e) { notifyApiError(e, t('The account could not be restored.')); }
    finally { setRestoringId(null); }
  };

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
      else if (type === 'make-owner') { await usersApi.makeOwner(userId); setOwnerCheck(n => n + 1); }
      setShowAction(null); setActionReason('');
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
          <Search className="absolute start-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
          <input className={inp('ps-9')} placeholder="Search name or email…" value={search}
            onChange={e => { setSearch(e.target.value); setPage(1); }} />
        </div>
        <select className={inp('w-40')} value={statusFilter} onChange={e => { setStatusFilter(e.target.value); setPage(1); }}>
          <option value="">All Statuses</option>
          {USER_STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
          <option value="Deleted">{t('Deleted (restorable)')}</option>
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
            <div className="absolute bottom-full start-0 mb-1.5 w-64 rounded-lg bg-slate-800 px-3 py-2 text-xs text-white shadow-lg hidden group-hover:block z-10">
              User limit reached ({usage.activeUsers}/{usage.maxUsers}). Upgrade your plan to add more users.
            </div>
          )}
        </div>
        <button
          type="button"
          onClick={() => setEmployeeLink({ user: null })}
          className="flex items-center gap-2 rounded-lg border border-violet-300 px-3 py-2 text-sm font-medium text-violet-700 hover:bg-violet-50 dark:border-violet-700 dark:text-violet-300 dark:hover:bg-violet-900/30"
        >
          <UserCheck className="h-4 w-4" aria-hidden="true" /> {t('Invite employee')}
        </button>
      </div>

      {err && <p className="text-sm text-red-500">{err}</p>}

      {/* Table */}
      <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-700">
        <table className="w-full text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/60">
            <tr>
              {['Name / Email', 'Status', 'Roles', 'Access Mode', 'Last Login', 'Actions'].map(h => (
                <th key={h} className="px-4 py-2.5 text-start text-xs font-semibold text-slate-500">{h}</th>
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
                  <p className="flex items-center gap-1.5 font-medium text-slate-800 dark:text-slate-200">
                    {u.fullName}
                    {u.isOwner && (
                      <span className="rounded-lg bg-amber-50 px-1.5 py-0.5 text-caption font-semibold text-amber-800 ring-1 ring-inset ring-amber-200 dark:bg-amber-500/10 dark:text-amber-300 dark:ring-amber-800"
                        title={t('Workspace owner: no other administrator can change this account.')} data-testid="user-owner-badge">
                        {t('Owner')}
                      </span>
                    )}
                  </p>
                  <p className="text-xs text-slate-500">{u.email}</p>
                  {u.mfaEnabled && (
                    <p className="mt-0.5 flex items-center gap-1 text-xs text-emerald-700 rtl:text-body-sm dark:text-emerald-400" data-testid="user-two-step-on">
                      <Shield className="h-3 w-3" aria-hidden="true" /> {t('Two-step sign-in is on')}
                    </p>
                  )}
                  {u.employeeId && (
                    <p className="text-xs text-violet-500">
                      {!u.employeeName
                        ? `Emp #${u.employeeId}`
                        : u.employeeCode
                          ? t('Employee: {name} ({code})', { name: u.employeeName, code: u.employeeCode })
                          : t('Employee: {name}', { name: u.employeeName })}
                    </p>
                  )}
                </td>
                <td className="px-4 py-3"><StatusBadge value={u.status} /></td>
                <td className="max-w-[14rem] px-4 py-3 text-xs text-slate-600 dark:text-slate-400">
                  <RoleChips roles={u.roles} />
                </td>
                <td className="whitespace-nowrap px-4 py-3 text-xs text-slate-600 dark:text-slate-400">{t(ACCESS_MODE_LABELS[u.accessMode] ?? u.accessMode)}</td>
                <td className="whitespace-nowrap px-4 py-3 text-xs text-slate-500">{u.lastLoginAtUtc ? fmtDateTime(u.lastLoginAtUtc) : t('Never')}</td>
                <td className="px-4 py-3">
                  {deletedView ? (
                    <button type="button" onClick={() => restoreUser(u)} disabled={restoringId === u.id} data-testid="user-restore"
                      className="inline-flex h-7 items-center gap-1 rounded-lg border border-slate-200 px-2 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5">
                      <RotateCcw className="h-3 w-3" aria-hidden="true" /> {t('Restore')}<span className="sr-only"> {u.fullName}</span>
                    </button>
                  ) : (
                  <div className="flex items-center gap-1">
                    <button title="View Access" onClick={() => setSelected(u)} className="rounded p-1 hover:bg-slate-100 dark:hover:bg-slate-700">
                      <Eye className="h-3.5 w-3.5 text-slate-500" />
                    </button>
                    {!protectedOwner(u) && (<>
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
                    {/* Never your own: that goes through Change password, which proves the current one. */}
                    {!isMe(u) && (
                      <button title={t('Reset password')} aria-label={t('Reset password')} onClick={() => setResetPasswordFor(u)} className="rounded p-1 hover:bg-violet-100 dark:hover:bg-violet-900/30" data-testid="user-reset-password">
                        <RotateCcw className="h-3.5 w-3.5 text-violet-600" />
                      </button>
                    )}
                    <button type="button" title={deleteBlockedReason(u) ?? t('Delete user')} aria-label={`${t('Delete user')} ${u.fullName}`} disabled={!!deleteBlockedReason(u)}
                      onClick={() => setShowAction({ type: 'delete', userId: u.id })} data-testid="user-delete"
                      className="rounded p-1 hover:bg-red-100 disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:bg-transparent dark:hover:bg-red-900/30">
                      <Trash2 className="h-3.5 w-3.5 text-red-500" />
                    </button>
                    </>)}
                    {canMakeOwner(u) && (
                      <button
                        type="button"
                        onClick={() => setShowAction({ type: 'make-owner', userId: u.id })}
                        className="ms-1 whitespace-nowrap rounded-lg border border-amber-200 px-2 py-1 text-xs font-medium text-amber-800 hover:bg-amber-50 dark:border-amber-800 dark:text-amber-300 dark:hover:bg-amber-900/30"
                        data-testid="user-make-owner"
                      >
                        {t('Make owner')}
                      </button>
                    )}
                    {canResetTwoStep(u) && (
                      <button
                        type="button"
                        onClick={() => setResetTwoStep(u)}
                        className="ms-1 whitespace-nowrap rounded-lg border border-rose-200 px-2 py-1 text-xs font-medium text-rose-700 rtl:text-body-sm hover:bg-rose-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-rose-500 dark:border-rose-800 dark:text-rose-300 dark:hover:bg-rose-900/30"
                        data-testid="user-reset-two-step"
                      >
                        {t('Reset two-step sign-in')}
                      </button>
                    )}
                    {!u.employeeId && (
                      <button
                        type="button"
                        onClick={() => setEmployeeLink({ user: u })}
                        className="ms-1 whitespace-nowrap rounded-lg border border-violet-200 px-2 py-1 text-xs font-medium text-violet-700 hover:bg-violet-50 dark:border-violet-800 dark:text-violet-300 dark:hover:bg-violet-900/30"
                      >
                        {t('Link to employee record')}
                      </button>
                    )}
                  </div>
                  )}
                  {!deletedView && deleteBlockedReason(u) && <p className="mt-1 max-w-[16rem] text-caption leading-4 text-slate-500 dark:text-slate-400" data-testid="user-delete-reason">{deleteBlockedReason(u)}</p>}
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
      {selected && <UserAccessModal user={selected} roles={roles} allPermissions={allPermissions} ceiling={ceiling} onClose={() => { setSelected(null); load(); }} />}

      {/* Link a login to its employee record / invite an employee to Self-Service */}
      {employeeLink && (
        <LinkEmployeeLoginDialog user={employeeLink.user} onClose={() => setEmployeeLink(null)} onChanged={load} />
      )}

      {/* Reset another user's password: a reset link, or a temporary password */}
      {resetPasswordFor && (
        <ResetPasswordDialog user={resetPasswordFor} onClose={() => setResetPasswordFor(null)} onChanged={load} />
      )}

      {/* Reset another user's two-step sign-in */}
      {resetTwoStep && (
        <ResetTwoStepDialog
          user={resetTwoStep}
          onClose={() => setResetTwoStep(null)}
          onReset={() => {
            const name = resetTwoStep.fullName?.trim() || resetTwoStep.email;
            setResetTwoStep(null);
            toast.success(t('Two-step sign-in reset for {name}', { name }));
            load();
          }}
          onStale={load}
        />
      )}

      {/* Create User Modal */}
      {showCreate && <CreateUserModal roles={roles} ceiling={ceiling} onClose={() => setShowCreate(false)} onCreated={createdFromGuidedFlow} />}

      {/* Action confirmation modal */}
      {showAction && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="w-full max-w-md rounded-2xl bg-white p-6 shadow-xl dark:bg-slate-900">
            <h3 className={`text-base font-semibold capitalize mb-4 ${showAction.type === 'delete' ? 'text-red-600 dark:text-red-400' : 'text-slate-800 dark:text-slate-200'}`}>
              {showAction.type === 'make-owner' ? t('Make workspace owner?') : `${showAction.type.replace('-', ' ')} User`}
            </h3>
            {showAction.type === 'make-owner' && (
              <p className="mb-4 text-sm text-slate-600 dark:text-slate-400" data-testid="make-owner-explainer">
                {t('{name} will become the workspace owner, and no other administrator will be able to change their account. You stay an Admin, but you will no longer be protected.', { name: users.find(x => x.id === showAction.userId)?.fullName ?? '' })}
              </p>
            )}
            {showAction.type === 'delete' && (
              <p className="mb-4 text-sm text-slate-600 dark:text-slate-400">
                This will permanently remove the user&apos;s access. This action cannot be undone.
              </p>
            )}
            {['suspend', 'lock'].includes(showAction.type) && (
              <FormField label="Reason">
                <input className={inp()} value={actionReason} onChange={e => setActionReason(e.target.value)} placeholder="Optional reason…" />
              </FormField>
            )}
            {actionErr && <ErrMsg msg={actionErr} />}
            <div className="mt-4 flex justify-end gap-2">
              <button onClick={() => { setShowAction(null); setActionErr(''); setActionReason(''); }}
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
                <input className={inp('field-ltr')} value={editFields.phoneNumber} onChange={e => setEditFields(f => ({ ...f, phoneNumber: e.target.value }))} placeholder="+1 555 000 0000" />
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

function UserAccessModal({ user, roles, allPermissions, ceiling, onClose }: {
  user: UserListItem;
  roles: RoleItem[];
  allPermissions: PermissionItem[];
  ceiling: AccessCeiling | null;
  onClose: () => void;
}) {
  const { t, locale } = useLocale();
  const ownAccount = isSelf(ceiling, user.id);
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
  const [confirmAllow, setConfirmAllow] = useState(false); // two-step confirm for bulk "Allow all"
  const [confirmDeny, setConfirmDeny] = useState(false);   // two-step confirm for bulk "Deny all"
  const [saving, setSaving] = useState(false);
  const [saveErr, setSaveErr] = useState('');
  const [saveOk, setSaveOk] = useState('');

  const reload = useCallback(() => {
    usersApi.getAccess(user.id).then(a => { setAccess(a); }).catch(() => {}).finally(() => setLoading(false));
  }, [user.id]);
  useEffect(() => { reload(); }, [reload]);
  const reloadCompanyAccess = useCallback(async () => {
    const [grants, allCompanies] = await Promise.all([
      entityGrantsApi.list(user.id),
      companiesApi.listAll(),
    ]);
    setCompanyGrants(grants);
    setCompanies(allCompanies);
    if (!grantCompanyId && allCompanies.length > 0) setGrantCompanyId(allCompanies[0].id);
  }, [grantCompanyId, user.id]);
  useEffect(() => { reloadCompanyAccess().catch(() => {}); }, [reloadCompanyAccess]);
  // Reset pending bulk confirmations whenever the filter changes so a confirm always reflects the current scope.
  useEffect(() => { setConfirmAllow(false); setConfirmDeny(false); }, [permSearch]);

  const saveRoles = async () => {
    setSaving(true); setSaveErr(''); setSaveOk('');
    try { await usersApi.assignRoles(user.id, selectedRoles); setSaveOk(t('Roles updated.')); }
    catch (e: unknown) { setSaveErr(localizedRefusal(e, locale) ?? t('Roles could not be updated.')); }
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

  // Single unified save path for BOTH per-row edits and the bulk-action buttons:
  // one atomic request — all-or-nothing, one refresh-token revocation pass, audited per key + a batch summary.
  const saveOverrides = async () => {
    const entries = Object.entries(pendingOverrides);
    if (entries.length === 0) { setSaveErr('No changes to save.'); return; }
    setSaving(true); setSaveErr(''); setSaveOk('');
    try {
      const latest = await permissionGrantApi.grantBulk(user.id, {
        items: entries.map(([permissionKey, v]) => ({ permissionKey, effect: v.effect })),
        reason: overrideReason || undefined,
      });
      setAccess(latest); setPendingOverrides({}); setOverrideReason('');
      setConfirmAllow(false); setConfirmDeny(false);
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

  // ── Bulk actions ────────────────────────────────────────────────────────────
  // Operate on EXACTLY the rows the user sees: filteredPerms is the same array the
  // list renders from, so the acted-on set can never drift from what's on screen.
  const targetPerms = filteredPerms;
  const isFiltered = permSearch.trim().length > 0;
  const scopeLabel = isFiltered ? `${targetPerms.length} filtered` : `all ${targetPerms.length}`;

  // Effective post-save effect for a row: pending change wins, else the current explicit override.
  const effectiveOf = (key: string): 'Allow' | 'Deny' | 'Remove' | 'default' => {
    const pending = pendingOverrides[key];
    if (pending) return pending.effect;
    return getOverrideState(key) ?? 'default';
  };

  const bulkSet = (target: 'Allow' | 'Deny' | 'Reset') => {
    setSaveErr(''); setSaveOk('');
    setPendingOverrides(prev => {
      const next = { ...prev };
      for (const p of targetPerms) {
        const key = p.key;
        if (target === 'Reset') {
          // Stage Remove for EVERY targeted key. getOverrideState() is blind to Allow overrides that
          // coincide with a role grant (returns null for them), so we cannot skip "clean-looking" rows
          // or a prior Allow-all's role-coincident overrides would silently survive. The backend no-ops
          // keys with no active override, so this is safe and is the only true "clear the override" reset.
          next[key] = { effect: 'Remove', reason: prev[key]?.reason ?? '' };
        } else {
          // Allow/Deny: skip rows already ending in the target state (dedupe exact no-ops); always
          // enqueue where it changes the outcome (incl. flipping the opposite override).
          if (effectiveOf(key) !== target) next[key] = { effect: target, reason: prev[key]?.reason ?? '' };
        }
      }
      return next;
    });
  };

  const onAllowAll = () => { setConfirmDeny(false); if (!confirmAllow) { setConfirmAllow(true); return; } bulkSet('Allow'); setConfirmAllow(false); };
  const onDenyAll = () => { setConfirmAllow(false); if (!confirmDeny) { setConfirmDeny(true); return; } bulkSet('Deny'); setConfirmDeny(false); };
  const onResetAll = () => { setConfirmAllow(false); setConfirmDeny(false); bulkSet('Reset'); };

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
                  {ownAccount ? (
                    <p data-testid="access-self-change" className="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:bg-amber-900/20 dark:text-amber-300">
                      {t('You cannot change your own roles. Another administrator must do it.')}
                    </p>
                  ) : ceiling && ceiling.roles.some(rc => !rc.canAssign) && (
                    <p className="text-xs text-slate-500">{t('Roles above your own access are greyed out, with the reason.')}</p>
                  )}
                  <div className="grid grid-cols-2 gap-2">
                    {roles.map(r => {
                      const blocked = assignBlock(ceiling, r.id, locale);
                      const disabled = ownAccount || !!blocked;
                      return (
                        <label key={r.id} data-testid={`role-option-${r.name}`} data-blocked={blocked ? 'true' : 'false'} title={blocked ?? undefined}
                          className={`flex items-start gap-2 rounded-lg border border-slate-200 p-2.5 dark:border-slate-700 ${disabled ? 'cursor-not-allowed opacity-60' : 'cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-800'}`}>
                          <input type="checkbox" className="mt-0.5" checked={selectedRoles.includes(r.name)} disabled={disabled}
                            onChange={e => setSelectedRoles(prev => e.target.checked ? [...prev, r.name] : prev.filter(x => x !== r.name))} />
                          <div className="min-w-0">
                            <p className="text-sm font-medium text-slate-700 dark:text-slate-300">{r.name}</p>
                            <p className="text-xs text-slate-500">{r.description}</p>
                            {blocked && (
                              <p className="mt-1 text-xs text-amber-700 dark:text-amber-400">
                                <span className="font-medium">{t('Above your access')}</span> · {blocked}
                              </p>
                            )}
                          </div>
                        </label>
                      );
                    })}
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

                  <div className="flex flex-wrap items-center gap-2 rounded-lg border border-slate-200 dark:border-slate-700 px-3 py-2">
                    <span className="text-xs font-medium text-slate-500">
                      Bulk actions <span className="text-slate-400">({isFiltered ? 'filtered set' : 'all permissions'})</span>:
                    </span>
                    <button type="button" onClick={onAllowAll} disabled={saving || targetPerms.length === 0}
                      className="flex items-center gap-1 rounded px-2 py-1 text-xs font-medium text-green-700 bg-green-50 hover:bg-green-100 disabled:opacity-40 disabled:cursor-not-allowed dark:bg-green-900/30 dark:text-green-400">
                      <CheckSquare className="h-3 w-3" /> Allow {scopeLabel}
                    </button>
                    <button type="button" onClick={onDenyAll} disabled={saving || targetPerms.length === 0}
                      className="flex items-center gap-1 rounded px-2 py-1 text-xs font-medium text-red-700 bg-red-50 hover:bg-red-100 disabled:opacity-40 disabled:cursor-not-allowed dark:bg-red-900/30 dark:text-red-400">
                      <MinusSquare className="h-3 w-3" /> Deny {scopeLabel}
                    </button>
                    <button type="button" onClick={onResetAll} disabled={saving || targetPerms.length === 0}
                      className="flex items-center gap-1 rounded px-2 py-1 text-xs font-medium text-slate-600 bg-slate-100 hover:bg-slate-200 disabled:opacity-40 disabled:cursor-not-allowed dark:bg-slate-700 dark:text-slate-300">
                      <Square className="h-3 w-3" /> Reset {scopeLabel}
                    </button>
                  </div>

                  {confirmAllow && (
                    <div className="rounded-lg border border-amber-300 bg-amber-50 dark:bg-amber-900/20 dark:border-amber-700 px-3 py-2 text-xs text-amber-800 dark:text-amber-300 space-y-2">
                      <p className="flex items-start gap-1 font-medium">
                        <Shield className="h-3.5 w-3.5 shrink-0 mt-0.5" />
                        <span>Grant this user <strong>every {isFiltered ? 'filtered' : ''} permission</strong> ({targetPerms.length}). This stages explicit Allow overrides — even on permissions already granted by role — and is a high-privilege change. Add a reason above, then confirm.</span>
                      </p>
                      <div className="flex gap-2">
                        <button type="button" onClick={onAllowAll}
                          className="rounded bg-amber-600 px-3 py-1 font-medium text-white hover:bg-amber-700">Confirm Allow all</button>
                        <button type="button" onClick={() => setConfirmAllow(false)}
                          className="rounded border border-amber-300 px-3 py-1 hover:bg-amber-100 dark:border-amber-700">Cancel</button>
                      </div>
                    </div>
                  )}

                  {confirmDeny && (
                    <div className="rounded-lg border border-red-300 bg-red-50 dark:bg-red-900/20 dark:border-red-700 px-3 py-2 text-xs text-red-800 dark:text-red-300 space-y-2">
                      <p className="flex items-start gap-1 font-medium">
                        <Shield className="h-3.5 w-3.5 shrink-0 mt-0.5" />
                        <span>Deny this user <strong>every {isFiltered ? 'filtered' : ''} permission</strong> ({targetPerms.length}). Deny overrides beat role grants, so this can lock the user out of portal features. Add a reason above, then confirm.</span>
                      </p>
                      <div className="flex gap-2">
                        <button type="button" onClick={onDenyAll}
                          className="rounded bg-red-600 px-3 py-1 font-medium text-white hover:bg-red-700">Confirm Deny all</button>
                        <button type="button" onClick={() => setConfirmDeny(false)}
                          className="rounded border border-red-300 px-3 py-1 hover:bg-red-100 dark:border-red-700">Cancel</button>
                      </div>
                    </div>
                  )}

                  <FormField label="Bulk reason (optional — applies to all changes in this batch)">
                    <input className={inp()} value={overrideReason} onChange={e => setOverrideReason(e.target.value)} placeholder="e.g. Temporary project access" />
                  </FormField>
                  <div className="relative">
                    <Search className="absolute start-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
                    <input className={inp('ps-9')} placeholder="Filter permissions…" value={permSearch} onChange={e => setPermSearch(e.target.value)} />
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
                                <div className="min-w-0 flex-1 me-3">
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
            <button onClick={saveRoles} disabled={saving || ownAccount} className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
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

function CreateUserModal({ roles, ceiling, onClose, onCreated }: { roles: RoleItem[]; ceiling: AccessCeiling | null; onClose: () => void; onCreated: () => void }) {
  const { t, locale } = useLocale();
  const searchParams = useSearchParams();
  const { companies, selectedCompanyId, isGroupScope, accountType } = useCompany();
  const fullNameId = useId();
  const emailId = useId();
  const passwordId = useId();
  const dialogTitleId = useId();
  const requirementsId = `${passwordId}-requirements`;
  const [email, setEmail] = useState('');
  const [fullName, setFullName] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [selectedRoles, setSelectedRoles] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState('');
  const [passwordPolicy, setPasswordPolicy] = useState<PasswordPolicy | null>(null);
  const [policyLoading, setPolicyLoading] = useState(true);
  const [policyAttempt, setPolicyAttempt] = useState(0);
  const requestedCompanyId = searchParams?.get('companyId') ?? '';
  const companyAdministratorFlow = searchParams?.get('purpose') === 'companyAdmin';
  const canGrantGroupAccess = accountType === 'Group' && isGroupScope;
  const [accessScope, setAccessScope] = useState<'company' | 'group'>('company');
  const [companyOptions, setCompanyOptions] = useState(() => companies.map(company => ({
    id: company.id,
    name: company.name,
    isActive: company.isActive,
  })));
  const [companyId, setCompanyId] = useState('');

  useEffect(() => {
    let active = true;
    const contextOptions = companies.map(company => ({ id: company.id, name: company.name, isActive: company.isActive }));
    if (!canGrantGroupAccess) {
      setCompanyOptions(contextOptions);
      return () => { active = false; };
    }
    companiesApi.listAll()
      .then(rows => {
        if (active) setCompanyOptions(rows.map(company => ({ id: company.id, name: company.legalNameEn, isActive: company.isActive })));
      })
      .catch(() => { if (active) setCompanyOptions(contextOptions); });
    return () => { active = false; };
  }, [canGrantGroupAccess, companies]);

  useEffect(() => {
    if (companyAdministratorFlow) {
      const requested = companyOptions.find(company => company.id === requestedCompanyId && company.isActive);
      setCompanyId(requested?.id ?? '');
      return;
    }
    if (companyId && companyOptions.some(company => company.id === companyId && company.isActive)) return;
    const preferred = companyOptions.find(company => company.id === selectedCompanyId && company.isActive)
      ?? companyOptions.find(company => company.isActive);
    setCompanyId(preferred?.id ?? '');
  }, [companyAdministratorFlow, companyId, companyOptions, requestedCompanyId, selectedCompanyId]);

  useEffect(() => {
    let active = true;
    setPolicyLoading(true);
    securitySettingsApi.get()
      .then(policy => { if (active) setPasswordPolicy(policy); })
      .catch(() => { if (active) setPasswordPolicy(null); })
      .finally(() => { if (active) setPolicyLoading(false); });
    return () => { active = false; };
  }, [policyAttempt]);

  useEffect(() => {
    if (!companyAdministratorFlow) return;
    const adminRole = roles.find(role => role.name.toLowerCase() === 'admin');
    if (adminRole && !assignBlock(ceiling, adminRole.id, locale)) {
      setSelectedRoles(current => current.some(role => role.toLowerCase() === 'admin') ? current : [...current, adminRole.name]);
    }
  }, [companyAdministratorFlow, roles, ceiling, locale]);

  const passwordCheck = passwordPolicy ? evaluatePasswordRequirements(password, passwordPolicy) : null;
  const metCount = passwordCheck?.requirements.filter(requirement => requirement.met).length ?? 0;
  const createsAdministrator = selectedRoles.some(role => role.toLowerCase() === 'admin');
  const requiredAdminRole = roles.find(role => role.name.toLowerCase() === 'admin');
  const requiredAdminRoleBlocked = companyAdministratorFlow
    ? !requiredAdminRole || !!assignBlock(ceiling, requiredAdminRole.id, locale)
    : false;
  const selectedCompany = companyOptions.find(company => company.id === companyId);

  const submit = async () => {
    if (!email || !fullName || !password) { setErr('All fields are required.'); return; }
    if (companyAdministratorFlow && companyId !== requestedCompanyId) { setErr(t('The new company could not be loaded. Return to Guided setup and try again.')); return; }
    if (companyAdministratorFlow && (!createsAdministrator || requiredAdminRoleBlocked)) { setErr(t('You cannot grant the required administrator role. Ask a group administrator with sufficient authority to complete this step.')); return; }
    if (accessScope === 'company' && !companyId) { setErr(t('Choose the company this user will support.')); return; }
    if (policyLoading) return;
    if (passwordCheck && !passwordCheck.valid) { setErr('Please meet all password requirements below.'); return; }
    if (Array.from(password).length < 10) { setErr('Password must be at least 10 characters.'); return; }
    setLoading(true); setErr('');
    try {
      await usersApi.create({
        email,
        fullName,
        password,
        roles: selectedRoles,
        companyId: accessScope === 'company' ? companyId : undefined,
        isGroupScope: accessScope === 'group',
      });
      onCreated();
    } catch (e: unknown) {
      const status = (e as { response?: { status?: number; data?: { error?: string; message?: string; current?: number; limit?: number } } })?.response?.status;
      const data = (e as { response?: { data?: { error?: string; message?: string; current?: number; limit?: number } } })?.response?.data;
      if (status === 402) {
        setErr('Your subscription is inactive or expired. Please contact support.');
      } else if (status === 422 && data?.error === 'user_limit_reached') {
        setErr(data.message ?? `User limit reached (${data.current}/${data.limit}). Please upgrade your subscription.`);
      } else {
        setErr(localizedRefusal(e, locale) ?? 'Failed to create user.');
      }
    }
    setLoading(false);
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby={dialogTitleId}
        className="max-h-[calc(100dvh-2rem)] w-full max-w-lg overflow-y-auto rounded-2xl bg-white p-6 shadow-xl dark:bg-slate-900 space-y-4"
      >
        <div>
          <h3 id={dialogTitleId} className="text-base font-semibold text-slate-800 dark:text-slate-200">{t(companyAdministratorFlow ? 'Create company administrator' : 'Create User')}</h3>
          <p className="mt-1 text-xs leading-5 text-slate-500 dark:text-slate-400">{t(companyAdministratorFlow ? 'Add the administrator for this company, then continue to Guided setup.' : 'Choose access before assigning roles. Company access is the safe default.')}</p>
        </div>
        <FormField label="Full Name" htmlFor={fullNameId}><input id={fullNameId} className={inp()} value={fullName} onChange={e => setFullName(e.target.value)} /></FormField>
        <FormField label="Email" htmlFor={emailId}><input id={emailId} type="email" className={inp()} value={email} onChange={e => setEmail(e.target.value)} /></FormField>
        <fieldset className="space-y-2">
          <legend className="text-xs font-medium text-slate-600 dark:text-slate-400">{t('Access scope')}</legend>
          <label className={`flex cursor-pointer gap-3 rounded-xl border p-3 ${accessScope === 'company' ? 'border-violet-400 bg-violet-50/70 dark:border-violet-500 dark:bg-violet-500/10' : 'border-slate-200 dark:border-slate-700'}`}>
            <input type="radio" name="new-user-scope" value="company" checked={accessScope === 'company'} onChange={() => setAccessScope('company')} className="mt-1 accent-violet-600" />
            <span className="min-w-0 flex-1">
              <span className="block text-sm font-medium text-slate-800 dark:text-slate-100">{t('One company')}</span>
              <span className="block text-xs leading-5 text-slate-500 dark:text-slate-400">{t('Access stays within the selected legal entity.')}</span>
              {accessScope === 'company' && (
                <>
                  <select aria-label={t('Company')} className={inp('mt-2')} value={companyId} onChange={event => setCompanyId(event.target.value)} disabled={companyAdministratorFlow}>
                    <option value="">{t('Choose a company…')}</option>
                    {companyOptions.filter(company => company.isActive).map(company => <option key={company.id} value={company.id}>{company.name}</option>)}
                  </select>
                  {companyAdministratorFlow && <span className="mt-1 block text-xs text-slate-500 dark:text-slate-400">{t('Locked to the company you just created.')}</span>}
                </>
              )}
            </span>
          </label>
          {canGrantGroupAccess && !companyAdministratorFlow && (
            <label className={`flex cursor-pointer gap-3 rounded-xl border p-3 ${accessScope === 'group' ? 'border-amber-400 bg-amber-50/70 dark:border-amber-500 dark:bg-amber-500/10' : 'border-slate-200 dark:border-slate-700'}`}>
              <input type="radio" name="new-user-scope" value="group" checked={accessScope === 'group'} onChange={() => setAccessScope('group')} className="mt-1 accent-amber-600" />
              <span><span className="block text-sm font-medium text-slate-800 dark:text-slate-100">{t('Entire group')}</span><span className="block text-xs leading-5 text-slate-500 dark:text-slate-400">{t('Access includes every current and future company. Use only for central group administrators.')}</span></span>
            </label>
          )}
        </fieldset>
        <FormField label="Password" htmlFor={passwordId}>
          <div className="relative">
            <input
              id={passwordId}
              type={showPassword ? 'text' : 'password'}
              className={inp('h-11 pe-12')}
              value={password}
              onChange={e => { setPassword(e.target.value); setErr(''); }}
              autoComplete="new-password"
              spellCheck={false}
              aria-describedby={requirementsId}
            />
            <button
              type="button"
              onClick={() => setShowPassword(visible => !visible)}
              aria-label={showPassword ? 'Hide password' : 'Show password'}
              aria-pressed={showPassword}
              aria-controls={passwordId}
              title={showPassword ? 'Hide password' : 'Show password'}
              className="absolute end-0 top-0 grid h-11 w-11 place-items-center rounded-lg text-slate-500 hover:text-violet-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-violet-500 dark:text-slate-400 dark:hover:text-violet-300"
            >
              {showPassword ? <EyeOff className="h-4 w-4" aria-hidden="true" /> : <Eye className="h-4 w-4" aria-hidden="true" />}
            </button>
          </div>
          <div id={requirementsId} className="mt-1 rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs dark:border-slate-700 dark:bg-slate-800">
            <p className="mb-2 text-slate-600 dark:text-slate-300" data-testid="create-user-temporary-note">{t('This is a temporary password. Give it to them in person; they choose their own the first time they sign in.')}</p>
            <p className="font-medium text-slate-700 dark:text-slate-200">Password requirements</p>
            {policyLoading ? (
              <p className="mt-2 text-slate-500 dark:text-slate-400" role="status">Loading workspace rules…</p>
            ) : passwordCheck ? (
              <>
                <ul className="mt-2 space-y-1.5">
                  {passwordCheck.requirements.map(requirement => (
                    <li key={requirement.id} className={`flex items-center gap-2 ${requirement.met ? 'text-emerald-700 dark:text-emerald-400' : 'text-slate-600 dark:text-slate-400'}`}>
                      {requirement.met ? <CheckCircle className="h-3.5 w-3.5 shrink-0" aria-hidden="true" /> : <Circle className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />}
                      <span><span className="sr-only">{requirement.met ? 'Met: ' : 'Not met: '}</span>{requirement.label}</span>
                    </li>
                  ))}
                </ul>
                {passwordCheck.hasInvalidCharacters && <p className="mt-2 text-red-700 dark:text-red-400">Remove hidden or unsupported characters.</p>}
                <p role="status" aria-live="polite" aria-atomic="true" className="sr-only">
                  {passwordCheck.valid ? 'All password requirements met.' : `${metCount} of ${passwordCheck.requirements.length} password requirements met.${passwordCheck.hasInvalidCharacters ? ' Remove hidden or unsupported characters.' : ''}`}
                </p>
              </>
            ) : (
              <p className="mt-2 text-slate-600 dark:text-slate-400">
                Workspace rules couldn’t be loaded. Your password will be checked when you create the user.{' '}
                <button type="button" onClick={() => setPolicyAttempt(attempt => attempt + 1)} className="font-medium text-violet-700 underline underline-offset-2 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-violet-500 dark:text-violet-300">Retry</button>
              </p>
            )}
          </div>
        </FormField>
        <FormField label="Roles">
          <div className="grid grid-cols-2 gap-1.5 max-h-48 overflow-y-auto">
            {roles.map(r => {
              const blocked = assignBlock(ceiling, r.id, locale);
              const requiredByCompanyFlow = companyAdministratorFlow && r.name.toLowerCase() === 'admin';
              return (
                <label key={r.id} title={blocked ?? undefined} data-testid={`create-role-option-${r.name}`} data-blocked={blocked ? 'true' : 'false'}
                  className={`flex items-start gap-2 text-sm ${blocked ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'}`}>
                  <input type="checkbox" className="mt-0.5" checked={selectedRoles.includes(r.name)} disabled={!!blocked || requiredByCompanyFlow}
                    onChange={e => setSelectedRoles(prev => e.target.checked ? [...prev, r.name] : prev.filter(x => x !== r.name))} />
                  <span className="min-w-0">
                    {r.name}
                    {requiredByCompanyFlow && !blocked && <span className="block text-xs text-slate-500 dark:text-slate-400">{t('Required for this company administrator')}</span>}
                    {blocked && <span className="block text-xs text-amber-700 dark:text-amber-400">{t('Above your access')}</span>}
                  </span>
                </label>
              );
            })}
          </div>
        </FormField>
        {createsAdministrator && (
          <p className="rounded-lg bg-slate-50 px-3 py-2 text-xs leading-5 text-slate-600 dark:bg-slate-800 dark:text-slate-300">
            {accessScope === 'group'
              ? t('This person will be a group administrator across all companies.')
              : selectedCompany
                ? t('This person will administer {company} only.', { company: selectedCompany.name })
                : t('This person will administer the selected company only.')}
          </p>
        )}
        {companyAdministratorFlow && requiredAdminRoleBlocked && (
          <ErrMsg msg={t('You cannot grant the required administrator role. Ask a group administrator with sufficient authority to complete this step.')} />
        )}
        {err && <ErrMsg msg={err} />}
        <div className="flex justify-end gap-2 pt-2">
          <button onClick={onClose} className="rounded-lg border border-slate-200 px-3 py-2 text-sm hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-700">Cancel</button>
          <button onClick={submit} disabled={loading || policyLoading || (companyAdministratorFlow && (companyId !== requestedCompanyId || !createsAdministrator || requiredAdminRoleBlocked))} className="rounded-lg bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-60">
            {t(loading ? 'Creating…' : companyAdministratorFlow ? 'Create administrator and continue' : 'Create')}
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

// ── Roles Tab ──────────────────────────────────────────────────────────────────

export function RolesTab() {
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
  const [ceiling, setCeiling] = useState<AccessCeiling | null>(null);
  const { t, locale } = useLocale();
  const toast = useAppToast();

  const reload = useCallback(() => {
    setLoading(true);
    Promise.all([rolesApi.list(), rolesApi.permissions()])
      .then(([r, p]) => { setRoles(r); setAllPermissions(p); })
      .catch(() => {})
      .finally(() => setLoading(false));
    rolesApi.ceiling().then(setCeiling).catch(() => setCeiling(null));
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
    } catch (e: unknown) { setErr(localizedRefusal(e, locale) ?? 'Failed to update role'); }
    finally { setSaving(false); }
  };

  const { confirm, dialog: confirmDialog } = useConfirm();
  const [roleNotice, setRoleNotice] = useState<{ text: string; reassign?: boolean } | null>(null);

  // Turning a role off signs out everyone who holds it, so the dialog names how many (the server counts them).
  const toggleRoleActive = async (r: RoleItem) => {
    try {
      if (r.isActive) {
        const impact = await rolesApi.impact(r.id).catch(() => null);
        const people = impact?.assignedUsers ?? 0;
        const ok = await confirm({
          title: t('Turn off role'),
          message: people > 0
            ? t('Role holders will be signed out: {count} hold {name}', { count: people, name: r.name })
            : t('Nobody holds {name} right now. Turn it off?', { name: r.name }),
          confirmLabel: t('Turn off'),
          tone: 'danger',
        });
        if (!ok) return;
        await rolesApi.deactivate(r.id);
      } else await rolesApi.activate(r.id);
      reload();
    } catch (e: unknown) { setRoleNotice({ text: localizedRefusal(e, locale) ?? t('The role could not be changed.') }); }
  };

  // Remove a custom role: only when nobody holds it. Otherwise say who, and send the admin to reassign them.
  const removeRole = async (r: RoleItem) => {
    const ok = await confirm({
      title: t('Remove role'),
      message: t('Remove {name}? Only a role nobody holds can be removed. If people hold it, you are told how many and can reassign them first.', { name: r.name }),
      confirmLabel: t('Remove'),
      tone: 'danger',
    });
    if (!ok) return;
    try {
      await rolesApi.remove(r.id);
      setRoleNotice({ text: t('Removed {name}.', { name: r.name }) });
      reload();
    } catch (e: unknown) {
      const body = (e as { response?: { data?: { code?: string; message?: string; assignedUsers?: number } } })?.response?.data;
      if (body?.code === 'ROLE_ASSIGNED') setRoleNotice({ text: t('Role still held: {count} hold {name}', { count: body.assignedUsers ?? 0, name: r.name }), reassign: true });
      else setRoleNotice({ text: localizedRefusal(e, locale) ?? body?.message ?? t('The role could not be removed.') });
    }
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
      toast.success(t('Role summary copied to clipboard.'));
    } catch {
      toast.error(t('Copy failed. Please copy from the visible summary.'));
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
                    <span className="inline-flex h-5 w-5 items-center justify-center rounded-full bg-violet-100 text-caption font-semibold text-violet-700 dark:bg-violet-900/40 dark:text-violet-300">
                      {r.authorityLevel}
                    </span>
                    <p className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">{r.name}</p>
                  </div>
                  <p className="mt-0.5 truncate text-xs text-slate-500">{r.description}</p>
                </div>
                <span className="shrink-0 rounded-full bg-violet-50 px-2 py-0.5 text-caption font-medium text-violet-700 dark:bg-violet-900/30 dark:text-violet-300">
                  {r.permissions.length} perms
                </span>
              </div>
              <div className="mt-2 flex flex-wrap gap-1">
                {!r.isActive && <span className="rounded-full bg-slate-100 px-2 py-0.5 text-caption text-slate-500 dark:bg-slate-700">Inactive</span>}
                {r.isSystem && <span className="rounded-full bg-blue-50 px-2 py-0.5 text-caption text-blue-600 dark:bg-blue-900/30 dark:text-blue-400">System</span>}
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
                      {perms.map(p => {
                        const notHeld = !canGrantPermission(ceiling, p.key) && !editPerms.includes(p.key);
                        return (
                          <label key={p.id} title={notHeld ? t('You do not hold this permission, so you cannot grant it.') : p.description}
                            className={`flex items-center gap-2 text-xs ${notHeld ? 'cursor-not-allowed opacity-50' : 'cursor-pointer'}`}>
                            <input type="checkbox" checked={editPerms.includes(p.key)} disabled={notHeld} onChange={() => togglePermInEdit(p.key)}
                              className="rounded border-slate-300 text-violet-600 focus:ring-violet-500" />
                            <span className="font-mono text-violet-700 dark:text-violet-400 truncate">{p.key}</span>
                          </label>
                        );
                      })}
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

      {confirmDialog}
      {roleNotice && (
        <p role="status" data-testid="role-notice" className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-300">
          <span><bdi>{roleNotice.text}</bdi> {roleNotice.reassign && <a className="font-semibold text-blue-700 underline dark:text-sky-300" href="/setup?tab=users">{t('Open Users')}</a>}</span>
          <button type="button" className="font-semibold underline" onClick={() => setRoleNotice(null)}>{t('Dismiss')}</button>
        </p>
      )}
      {loading ? <p className="text-sm text-slate-500">Loading…</p> : (
        <div className="space-y-2">
          {roles.map(r => (
            <div key={r.id} className={`rounded-xl border ${r.isActive ? 'border-slate-200 dark:border-slate-700' : 'border-slate-200 dark:border-slate-700 opacity-60'}`}>
              <div className="flex items-center gap-3 px-4 py-3">
                <button type="button" onClick={() => setExpanded(prev => prev === r.id ? null : r.id)} className="flex-1 text-start min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="inline-flex h-5 w-5 items-center justify-center rounded-full bg-violet-100 text-caption font-semibold text-violet-700 dark:bg-violet-900/40 dark:text-violet-300 shrink-0">
                      {r.authorityLevel}
                    </span>
                    <p className="font-medium text-slate-800 dark:text-slate-200 truncate">{r.name}</p>
                    {!r.isActive && <span className="rounded-full bg-slate-100 px-2 py-0.5 text-caption text-slate-500 dark:bg-slate-700">Inactive</span>}
                    {r.isSystem && <span className="rounded-full bg-blue-50 px-2 py-0.5 text-caption text-blue-600 dark:bg-blue-900/30 dark:text-blue-400">System</span>}
                  </div>
                  <p className="text-xs text-slate-500 mt-0.5 truncate">{r.description}</p>
                </button>
                <span className="text-xs text-violet-600 font-medium shrink-0">{r.permissions.length} perms</span>
                <div className="flex items-center gap-1 shrink-0">
                  {editBlock(ceiling, r.id, locale) && (
                    <span data-testid={`role-locked-${r.name}`} title={editBlock(ceiling, r.id, locale) ?? undefined}
                      className="rounded-full bg-amber-50 px-2 py-0.5 text-caption text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">
                      {t('Not editable by you')}
                    </span>
                  )}
                  <button type="button" title={editBlock(ceiling, r.id, locale) ?? 'Edit role'} onClick={() => openEdit(r)} disabled={!!editBlock(ceiling, r.id, locale)}
                    className="h-7 w-7 flex items-center justify-center rounded-lg text-slate-400 hover:bg-slate-100 hover:text-slate-700 disabled:cursor-not-allowed disabled:opacity-40 dark:hover:bg-slate-700">
                    <Edit2 className="h-3.5 w-3.5" />
                  </button>
                  {r.isSystem && (
                    <span data-testid={`role-system-locked-${r.name}`} className="text-caption text-slate-500 dark:text-slate-400">{t('Built-in role: it cannot be turned off or removed.')}</span>
                  )}
                  {!r.isSystem && (
                    <button type="button" aria-label={t('Remove role')} title={editBlock(ceiling, r.id, locale) ?? t('Remove role')} onClick={() => removeRole(r)} disabled={!!editBlock(ceiling, r.id, locale)}
                      className="h-7 w-7 flex items-center justify-center rounded-lg text-rose-500 hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-40 dark:hover:bg-rose-900/20">
                      <Trash2 className="h-3.5 w-3.5" />
                    </button>
                  )}
                  {!r.isSystem && (
                    <button type="button" title={editBlock(ceiling, r.id, locale) ?? (r.isActive ? 'Deactivate' : 'Activate')} onClick={() => toggleRoleActive(r)} disabled={!!editBlock(ceiling, r.id, locale)}
                      className={`h-7 w-7 flex items-center justify-center rounded-lg disabled:cursor-not-allowed disabled:opacity-40 ${r.isActive ? 'text-amber-500 hover:bg-amber-50 dark:hover:bg-amber-900/20' : 'text-green-500 hover:bg-green-50 dark:hover:bg-green-900/20'}`}>
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

export function PermissionsTab() {
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
        <Search className="absolute start-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
        <input className={inp('ps-9 max-w-xs')} placeholder="Filter permissions…" value={search} onChange={e => setSearch(e.target.value)} />
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

export function PermissionMatrixTab() {
  const [matrix, setMatrix] = useState<PermissionMatrix | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [moduleFilter, setModuleFilter] = useState('');
  const [overrides, setOverrides] = useState<Record<string, Record<string, boolean>>>({});
  const [ceiling, setCeiling] = useState<AccessCeiling | null>(null);
  const { t, locale } = useLocale();
  const toast = useAppToast();

  const load = useCallback(() => {
    setLoading(true);
    rolesApi.ceiling().then(setCeiling).catch(() => setCeiling(null));
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
    } catch (e: unknown) { toast.error(localizedRefusal(e, locale) ?? t('Failed to save')); }
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
                <th className="sticky start-0 z-10 bg-slate-50 dark:bg-slate-800/60 px-3 py-2.5 text-start font-semibold text-slate-600 dark:text-slate-400 min-w-[200px] border-b border-e border-slate-200 dark:border-slate-700">
                  Permission
                </th>
                {matrix.roles.map(role => {
                  const locked = editBlock(ceiling, role.id, locale);
                  return (
                    <th key={role.id} data-testid={`matrix-role-${role.name}`} data-locked={locked ? 'true' : 'false'} title={locked ?? role.description}
                      className="px-2 py-2.5 text-center font-medium text-slate-600 dark:text-slate-400 border-b border-slate-200 dark:border-slate-700 min-w-[80px]">
                      <span className="block truncate max-w-[76px]">{role.name}</span>
                      <span className="text-caption text-slate-400 font-normal block">L{role.authorityLevel}</span>
                      {locked && <span className="block text-caption font-normal text-amber-700 dark:text-amber-400">{t('Not editable by you')}</span>}
                    </th>
                  );
                })}
              </tr>
            </thead>
            <tbody>
              {filteredMatrix.map((row, i) => (
                <tr key={row.permissionKey} className={i % 2 === 0 ? 'bg-white dark:bg-slate-900' : 'bg-slate-50/50 dark:bg-slate-800/20'}>
                  <td className="sticky start-0 z-10 bg-inherit px-3 py-1.5 border-e border-slate-200 dark:border-slate-700">
                    <p className="font-mono text-violet-700 dark:text-violet-400 truncate">{row.permissionKey}</p>
                    <p className="text-slate-400 truncate text-caption">{row.description}</p>
                  </td>
                  {matrix.roles.map(role => {
                    const checked = overrides[row.permissionKey]?.[role.id] ?? row.roles[role.id] ?? false;
                    const locked = editBlock(ceiling, role.id, locale)
                      ?? (!checked && !canGrantPermission(ceiling, row.permissionKey) ? t('You do not hold this permission, so you cannot grant it.') : null);
                    return (
                      <td key={role.id} className="text-center px-2 py-1.5 border-b border-slate-100 dark:border-slate-800/50">
                        <input
                          type="checkbox"
                          title={locked ?? `${role.name} — ${row.permissionKey}`}
                          checked={checked}
                          disabled={!!locked}
                          onChange={() => toggle(row.permissionKey, role.id)}
                          className="h-3.5 w-3.5 rounded border-slate-300 text-violet-600 focus:ring-violet-500 disabled:cursor-not-allowed disabled:opacity-40"
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

export function PermissionGrantorsTab() {
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
      usersApi.listAll(),
      rolesApi.permissions(),
    ]).then(([g, u, p]) => {
      setGrantors(g);
      setUsers(u);
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
    catch (e) { notifyApiError(e, 'Failed to revoke grantor authority.'); }
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
                <th key={h} className="px-4 py-2.5 text-start text-xs font-semibold text-slate-500">{h}</th>
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

export function DelegationsTab() {
  const { off } = useCapabilities();
  const canWrite = !off('approvalDelegations.write');
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
    try { await delegationsApi.cancel(id); load(); } catch (e) { notifyApiError(e, 'Failed to cancel delegation.'); }
  };

  return (
    <div className="space-y-4">
      {canWrite ? (
        <div className="flex justify-end">
          <button type="button" onClick={() => setShowCreate(true)} className="flex items-center gap-2 rounded-lg bg-violet-600 px-3 py-2 text-sm font-medium text-white hover:bg-violet-700">
            <Plus className="h-4 w-4" /> New Delegation
          </button>
        </div>
      ) : (
        <CapabilityNote title="Delegations are not available yet.">Nothing in the approval flow reads a delegation today, so a new one could not take effect. Existing delegations are listed below and can be cancelled. To cover an absence, change the approver on the approval workflow&apos;s step.</CapabilityNote>
      )}
      <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-700">
        <table className="w-full text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/60">
            <tr>
              {['From Emp', 'To Emp', 'Scope', 'Period', 'Status', 'Reason', 'Actions'].map(h => (
                <th key={h} className="px-4 py-2.5 text-start text-xs font-semibold text-slate-500">{h}</th>
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

export function AuthoritiesTab() {
  const { off } = useCapabilities();
  const canWrite = !off('approvalAuthorities.write');
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
      {canWrite ? (
        <div className="flex justify-end">
          <button type="button" onClick={() => setShowCreate(true)} className="flex items-center gap-2 rounded-lg bg-violet-600 px-3 py-2 text-sm font-medium text-white hover:bg-violet-700">
            <Plus className="h-4 w-4" /> New Authority
          </button>
        </div>
      ) : (
        <CapabilityNote title="Approval limits are not available yet.">An amount limit has nothing to be compared against today, so a new one could not be enforced. Existing entries are listed below. For approval seniority, use ordered steps on the approval workflow.</CapabilityNote>
      )}
      <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-700">
        <table className="w-full text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/60">
            <tr>
              {['Employee', 'Scope', 'Role', 'Limit', 'Final Approver', 'Status'].map(h => (
                <th key={h} className="px-4 py-2.5 text-start text-xs font-semibold text-slate-500">{h}</th>
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

// ── Sign-in security ───────────────────────────────────────────────────────────
// ONE editor of /api/access/security-settings (it was two: this tab, and Tenant Admin → Security). It offers every field
// either had: the password rules, expiry and history, the lockout, the idle timeout, the maximum session length, the
// remembered sign-in and concurrent sessions, and the two-step policy with trusted browsers.

/** The trusted-browser lifetimes offered; 0 = off. A stored value outside these is shown as well. */
const TRUST_DAY_CHOICES = [0, 7, 14, 30, 90] as const;
const DEFAULT_TRUST_DAYS = 30;

/** The maximum session lengths offered, in hours. A stored value outside these is shown as well. */
const SESSION_MAX_HOUR_CHOICES = [4, 8, 12, 24, 72, 168] as const;
const DEFAULT_SESSION_MAX_HOURS = 12;

type SecurityPatch = (patch: Partial<SecuritySetting>) => void;

const GROUP_CLASS = 'space-y-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-700 dark:bg-slate-900';
const GROUP_HEADING_CLASS = 'border-b border-slate-100 pb-2 text-sm font-semibold text-slate-700 dark:border-slate-800 dark:text-slate-200';
const LABEL_CLASS = 'block text-xs font-medium text-slate-600 dark:text-slate-400 rtl:text-body-sm';
const HINT_CLASS = 'mt-1 text-xs leading-5 text-slate-500 dark:text-slate-400 rtl:text-body-sm';

function SecurityNumberField({ id, label, unit, range, hint, infoKey, value, min, max, onChange }: {
  id: string; label: string; unit?: string; range?: string; hint?: string; infoKey?: string;
  value: number | undefined; min: number; max?: number; onChange: (value: number) => void;
}) {
  // A company's own wording for this field (Setup → Field help text, key `infoKey`) replaces the built-in hint.
  const { overrides } = useHelpTexts();
  const shown = (infoKey && overrides[infoKey]) || hint;
  return (
    <div>
      <label htmlFor={id} className={`${LABEL_CLASS} mb-1`}>
        {label}{unit && <> <span className="text-slate-400">{unit}</span></>}{range && <> <bdi dir="ltr" className="text-slate-400">{range}</bdi></>}
      </label>
      <input id={id} type="number" min={min} max={max} value={value ?? ''} aria-describedby={shown ? `${id}-hint` : undefined}
        onChange={event => onChange(Number(event.target.value))} className={inp('w-full')} />
      {shown && <p id={`${id}-hint`} className={HINT_CLASS}>{shown}</p>}
    </div>
  );
}

function SecuritySwitch({ id, label, checked, onChange }: { id: string; label: string; checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <div className="flex items-center gap-2">
      <Toggle id={id} checked={checked} onChange={onChange} />
      <label htmlFor={id} className="cursor-pointer text-sm text-slate-700 dark:text-slate-300">{label}</label>
    </div>
  );
}

function PasswordPolicyGroup({ security, onChange }: { security: SecuritySetting; onChange: SecurityPatch }) {
  const { t } = useLocale();
  return (
    <section className={GROUP_CLASS} aria-labelledby="sec-password-heading" data-testid="password-policy">
      <h3 id="sec-password-heading" className={GROUP_HEADING_CLASS}>{t('Password policy')}</h3>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <SecurityNumberField id="sec-minlen" label={t('Minimum length')} range="(6–64)" min={6} max={64} value={security.passwordMinLength}
          infoKey="security.passwordMinLength" hint={t('Fewest characters a password may have. 10-12+ recommended; applies to new passwords only.')}
          onChange={value => onChange({ passwordMinLength: value })} />
        <SecurityNumberField id="sec-pw-expiry" label={t('Password expiry')} unit={t('(days, 0 = never)')} min={0} max={365} value={security.passwordExpiryDays}
          infoKey="security.passwordExpiryDays" hint={t('Days until staff must change their password. 0 turns off forced rotation.')}
          onChange={value => onChange({ passwordExpiryDays: value })} />
        <SecurityNumberField id="sec-pw-history" label={t('Password history count')} min={0} max={24} value={security.passwordHistoryCount}
          infoKey="security.passwordHistoryCount" hint={t('How many previous passwords are remembered and blocked from reuse. 0 allows reuse.')}
          onChange={value => onChange({ passwordHistoryCount: value })} />
      </div>
      <div className="flex flex-wrap gap-x-6 gap-y-3">
        <SecuritySwitch id="sec-upper" label={t('Require uppercase letter')} checked={security.passwordRequireUppercase === true} onChange={value => onChange({ passwordRequireUppercase: value })} />
        <SecuritySwitch id="sec-lower" label={t('Require lowercase letter')} checked={security.passwordRequireLowercase === true} onChange={value => onChange({ passwordRequireLowercase: value })} />
        <SecuritySwitch id="sec-digit" label={t('Require digit')} checked={security.passwordRequireDigit === true} onChange={value => onChange({ passwordRequireDigit: value })} />
        {/* The API field is passwordRequireSpecial; a form once sent passwordRequireSpecialChar, which the server ignored. */}
        <SecuritySwitch id="sec-special" label={t('Require special character')} checked={security.passwordRequireSpecial === true} onChange={value => onChange({ passwordRequireSpecial: value })} />
      </div>
    </section>
  );
}

/**
 * Sessions and lockout. Two limits end a session: inactivity (`sessionTimeoutMinutes`, the idle timeout) and the maximum
 * session length (`sessionMaxHours`, counted from sign-in, active or not); a shorter maximum applies to sessions already
 * open at their next refresh. The remembered sign-in and concurrent sessions are written with them.
 */
function SessionLimitsGroup({ security, onChange }: { security: SecuritySetting; onChange: SecurityPatch }) {
  const { t } = useLocale();
  const hours = Number.isInteger(security.sessionMaxHours) ? (security.sessionMaxHours as number) : DEFAULT_SESSION_MAX_HOURS;
  const choices: number[] = (SESSION_MAX_HOUR_CHOICES as readonly number[]).includes(hours)
    ? [...SESSION_MAX_HOUR_CHOICES]
    : [...SESSION_MAX_HOUR_CHOICES, hours].sort((a, b) => a - b);
  const label = (h: number) => {
    if (h === 4) return t('4 hours');
    if (h === 8) return t('8 hours');
    if (h === DEFAULT_SESSION_MAX_HOURS) return t('12 hours (default)');
    if (h === 24) return t('24 hours');
    if (h === 72) return t('72 hours (3 days)');
    if (h === 168) return t('168 hours (7 days)');
    return t('{hours, plural, one {# hour (current)} other {# hours (current)}}', { hours: h });
  };
  return (
    <section className={GROUP_CLASS} aria-labelledby="sec-sessions-heading" data-testid="session-limits">
      <h3 id="sec-sessions-heading" className={GROUP_HEADING_CLASS}>{t('Sessions and lockout')}</h3>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <SecurityNumberField id="sec-timeout" label={t('Sign out after inactivity')} unit={t('(minutes)')} min={15} max={1440} value={security.sessionTimeoutMinutes}
          infoKey="security.sessionTimeoutMinutes" hint={t('Ends a session nobody has used for this long.')}
          onChange={value => onChange({ sessionTimeoutMinutes: value })} />
        <div>
          <label htmlFor="sec-max-hours" className={`${LABEL_CLASS} mb-1`}>{t('Maximum session length')}</label>
          <select id="sec-max-hours" value={hours} aria-describedby="sec-max-hours-hint"
            onChange={event => onChange({ sessionMaxHours: Number(event.target.value) })} className={inp('w-full')}>
            {choices.map(h => <option key={h} value={h}>{label(h)}</option>)}
          </select>
          <p id="sec-max-hours-hint" className={HINT_CLASS}>{t('Everyone signs in again after this long, even if they are active. Shorter is safer.')}</p>
        </div>
        <SecurityNumberField id="sec-maxfail" label={t('Maximum failed attempts')} range="(1–20)" min={1} max={20} value={security.maxFailedLoginAttempts}
          infoKey="security.maxFailedLoginAttempts" hint={t('Wrong-password tries allowed before the account is temporarily locked. 5 is a common default.')}
          onChange={value => onChange({ maxFailedLoginAttempts: value })} />
        <SecurityNumberField id="sec-lockout" label={t('Lockout duration')} unit={t('(minutes)')} min={5} max={1440} value={security.lockoutDurationMinutes}
          infoKey="security.lockoutDurationMinutes" hint={t('How long a locked account stays locked before it can try again. Admins can unlock earlier from Users.')}
          onChange={value => onChange({ lockoutDurationMinutes: value })} />
        <SecurityNumberField id="sec-refresh" label={t('Remembered sign-in')} unit={t('(days)')} min={1} max={90} value={security.refreshTokenExpiryDays}
          infoKey="security.refreshTokenExpiryDays" hint={t('The longest a sign-in is remembered without asking for the password again. Shorter is safer, and means more sign-ins.')}
          onChange={value => onChange({ refreshTokenExpiryDays: value })} />
      </div>
      <SecuritySwitch id="sec-multi" label={t('Allow multiple concurrent sessions')} checked={security.allowMultipleSessions === true} onChange={value => onChange({ allowMultipleSessions: value })} />
    </section>
  );
}

/**
 * Two-step sign-in: the company policy (each person chooses / payroll, banking and access roles / everyone) and how long
 * a trusted browser skips the code. Writes `mfaPolicy` (+ `mfaPolicyReason` when moving to "Each person chooses") and the
 * legacy `mfaRequired`. `savedPolicy` is the policy as last saved, so the reason is asked for on every relaxation.
 */
function TwoStepPolicyGroup({ security, savedPolicy, onChange }: { security: SecuritySetting; savedPolicy: MfaPolicy; onChange: SecurityPatch }) {
  const { t } = useLocale();
  const policy = effectiveMfaPolicy(security);
  const policyOptions: { value: MfaPolicy; title: string; hint: string }[] = [
    {
      value: 'Optional',
      title: t('Each person chooses'),
      hint: t('Nobody is forced to use two-step sign-in, including people with payroll, banking or access rights. Each person turns it on or off on their own page.'),
    },
    {
      value: 'PrivilegedRoles',
      title: t('Required for people who can see or change payroll, banking or access'),
      hint: t('Everyone else chooses for themselves.'),
    },
    {
      value: 'Everyone',
      title: t('Required for everyone'),
      hint: t('Every person in the company must use it.'),
    },
  ];
  const days = Number.isInteger(security.mfaTrustedDeviceDays) ? (security.mfaTrustedDeviceDays as number) : DEFAULT_TRUST_DAYS;
  const choices: number[] = (TRUST_DAY_CHOICES as readonly number[]).includes(days) ? [...TRUST_DAY_CHOICES] : [...TRUST_DAY_CHOICES, days].sort((a, b) => a - b);
  const label = (d: number) => {
    if (d === 0) return t('Off');
    if (d === 7) return t('7 days');
    if (d === 14) return t('14 days');
    if (d === DEFAULT_TRUST_DAYS) return t('30 days (default)');
    if (d === 90) return t('90 days');
    return t('{days, plural, one {# day (current)} other {# days (current)}}', { days: d });
  };
  return (
    <section className={GROUP_CLASS} aria-labelledby="sec-two-step-heading" data-testid="two-step-policy">
      <h3 id="sec-two-step-heading" className={GROUP_HEADING_CLASS}>{t('Two-step sign-in')}</h3>

      <fieldset className="space-y-3">
        <legend className="mb-1 text-xs font-medium text-slate-600 dark:text-slate-400 rtl:text-body-sm">{t('Who must use two-step sign-in')}</legend>
        {policyOptions.map(opt => (
          <label key={opt.value} htmlFor={`sec-mfa-policy-${opt.value}`}
            className={`flex cursor-pointer items-start gap-3 rounded-lg border px-3 py-2.5 ${policy === opt.value
              ? 'border-violet-300 bg-violet-50/60 dark:border-violet-700 dark:bg-violet-950/30'
              : 'border-slate-200 dark:border-slate-700'}`}>
            <input id={`sec-mfa-policy-${opt.value}`} type="radio" name="sec-mfa-policy" value={opt.value}
              checked={policy === opt.value}
              onChange={() => onChange({ mfaPolicy: opt.value, mfaRequired: opt.value === 'Everyone' })}
              className="mt-1 h-4 w-4 shrink-0 accent-violet-600" />
            <span className="min-w-0">
              <span className="block text-sm font-medium text-slate-800 dark:text-slate-200">{opt.title}</span>
              <span className="mt-0.5 block text-xs leading-5 text-slate-500 dark:text-slate-400 rtl:text-body-sm">{opt.hint}</span>
            </span>
          </label>
        ))}
        {policy === 'Optional' && (
          <div className="space-y-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2.5 dark:border-amber-900/60 dark:bg-amber-950/30" data-testid="two-step-optional-note">
            <p role="note" className="text-sm text-amber-900 dark:text-amber-200">
              {t('Recommended only while testing. A customer going live should require it at least for payroll, banking and access.')}
            </p>
            {savedPolicy !== 'Optional' && (
              <div>
                <label htmlFor="sec-mfa-policy-reason" className="mb-1 block text-xs font-medium text-amber-900 dark:text-amber-200 rtl:text-body-sm">{t('Reason (required)')}</label>
                <textarea id="sec-mfa-policy-reason" rows={2} maxLength={500} required
                  value={security.mfaPolicyReason ?? ''}
                  onChange={event => onChange({ mfaPolicyReason: event.target.value })}
                  className={inp('w-full')} />
              </div>
            )}
          </div>
        )}
        {policy === 'Everyone' && savedPolicy !== 'Everyone' && (
          <p id="sec-mfa-everyone-hint" className={`px-1 ${HINT_CLASS}`}>
            {t("When you turn this on and save, everyone who hasn't set it up yet is signed out, and sets it up the next time they sign in.")}
          </p>
        )}
      </fieldset>

      <div className="max-w-xs">
        <label htmlFor="sec-trust-days" className={`${LABEL_CLASS} mb-1`}>{t('Trusted browsers')}</label>
        <select id="sec-trust-days" value={days} aria-describedby="sec-trust-days-hint"
          onChange={event => onChange({ mfaTrustedDeviceDays: Number(event.target.value) })} className={inp('w-full')}>
          {choices.map(d => <option key={d} value={d}>{label(d)}</option>)}
        </select>
        <p id="sec-trust-days-hint" className={HINT_CLASS}>{t('Shorter is safer. Shortening it also shortens browsers already trusted.')}</p>
      </div>
    </section>
  );
}

export function SignInSecurityTab({ onDirtyChange }: { onDirtyChange?: (dirty: boolean) => void } = {}) {
  const { t } = useLocale();
  const [saved, setSaved] = useState<SecuritySetting | null>(null);
  const [form, setForm] = useState<SecuritySetting | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [justSaved, setJustSaved] = useState(false);
  const [saveError, setSaveError] = useState('');

  const load = useCallback(async () => {
    setLoading(true); setLoadError(null);
    try {
      const settings = await securitySettingsApi.get();
      setSaved(settings); setForm(settings);
    } catch (error) { setLoadError(error); }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const dirty = form !== null && saved !== null && JSON.stringify(form) !== JSON.stringify(saved);
  useLayoutEffect(() => { onDirtyChange?.(dirty); return () => onDirtyChange?.(false); }, [dirty, onDirtyChange]);

  const patch: SecurityPatch = changes => { setForm(current => (current ? { ...current, ...changes } : current)); setJustSaved(false); setSaveError(''); };

  const save = async () => {
    if (!form) return;
    setSaving(true); setSaveError(''); setJustSaved(false);
    try {
      const updated = await securitySettingsApi.update(form);
      // The reason belonged to this one change; the saved policy becomes the baseline for the next relaxation.
      const next = { ...form, ...updated, mfaPolicyReason: undefined };
      setSaved(next); setForm(next); setJustSaved(true);
    } catch (error) {
      const response = (error as { response?: { status?: number; data?: { code?: unknown } } } | null)?.response;
      setSaveError(response?.data?.code === 'ERR_SESSION_MAX_HOURS_RANGE' ? t('Maximum session length must be between 1 and 168 hours.')
        : response?.data?.code === 'ERR_MFA_TRUST_DAYS_RANGE' ? t('Trusted browser days must be between 0 (off) and 90.')
        : response?.data?.code === 'ERR_MFA_POLICY_REASON' ? t('Give a reason for letting each person choose two-step sign-in.')
        : response?.status === 403 ? t('Settings for the whole organisation can only be viewed and changed by an administrator with access to all companies.')
        : t('The sign-in security settings could not be saved. Please try again.'));
    } finally { setSaving(false); }
  };

  if (loading) return <p role="status" className="text-sm text-slate-500">{t('Loading…')}</p>;
  if (!form) {
    const forbidden = (loadError as { response?: { status?: number } } | null)?.response?.status === 403;
    return (
      <div role="alert" className="rounded-xl border border-rose-200 bg-rose-50 p-5 text-sm text-rose-700 dark:border-rose-900/60 dark:bg-rose-950/30 dark:text-rose-300">
        <p>{forbidden
          ? t('Settings for the whole organisation can only be viewed and changed by an administrator with access to all companies.')
          : t('The sign-in security settings could not be loaded.')}</p>
        {!forbidden && <p className="mt-1 text-rose-600 dark:text-rose-400">{t(requestFailureReason(loadError))}</p>}
        {!forbidden && <button type="button" onClick={() => void load()} className="btn-secondary mt-3 h-8 px-3 text-sm">{t('Retry')}</button>}
      </div>
    );
  }

  return (
    <div className="max-w-3xl space-y-5">
      <PasswordPolicyGroup security={form} onChange={patch} />
      <SessionLimitsGroup security={form} onChange={patch} />
      <TwoStepPolicyGroup security={form} savedPolicy={saved ? effectiveMfaPolicy(saved) : 'PrivilegedRoles'} onChange={patch} />

      {saveError && <p role="alert" className="rounded-lg border border-red-100 bg-red-50 px-3 py-2 text-sm text-red-600 dark:border-red-900/60 dark:bg-red-950/30 dark:text-red-300">{saveError}</p>}

      <div className="flex items-center gap-3">
        <button type="button" onClick={save} disabled={saving} className="rounded-lg bg-violet-600 px-6 py-2.5 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-50">
          {saving ? t('Saving…') : t('Save Settings')}
        </button>
        {justSaved && <span role="status" className="text-sm font-medium text-green-600">{t('Saved!')}</span>}
      </div>
    </div>
  );
}

// ── Audit Logs Tab ─────────────────────────────────────────────────────────────

export function AuditLogsTab() {
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
                <th key={h} className="px-4 py-2.5 text-start text-xs font-semibold text-slate-500">{h}</th>
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
