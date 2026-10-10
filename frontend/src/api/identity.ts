import client from './client';
import { fetchAllPages } from '../lib/paging';
import type { AuditIntegrityReport } from './payroll';

export type { AuditIntegrityReport } from './payroll';

// ── Types ──────────────────────────────────────────────────────────────────────

export interface UserListItem {
  id: string;
  email: string;
  fullName: string;
  phoneNumber: string;
  status: string;
  isActive: boolean;
  isLocked: boolean;
  mustChangePassword: boolean;
  roles: string[];
  accessMode: string;
  employeeId?: number;
  /** The employee record this login is linked to (name and code), when it is linked. */
  employeeName?: string | null;
  employeeCode?: string | null;
  lastLoginAtUtc?: string;
  createdAtUtc: string;
}

/** A login as the employee-link dialog shows it. */
export interface LinkedLogin {
  userId: string;
  email: string;
  status: string;
  accessMode: string;
  isActive: boolean;
}

export type EmployeeLoginNextAction = 'linked' | 'link_existing' | 'invite' | 'needs_work_email' | 'blocked';

/** GET /api/access/employee-logins/{employeeId}: where an employee stands on the way to Self-Service. */
export interface EmployeeLoginStatus {
  employeeId: number;
  employeeName: string;
  workEmail: string;
  linkedLogin: LinkedLogin | null;
  matchingLogin: LinkedLogin | null;
  nextAction: EmployeeLoginNextAction;
  /** Plain-language reason, for blocked / needs_work_email. */
  reason: string | null;
  /** Stable code for a refusal the screen words itself, e.g. 'login_other_company'. */
  reasonCode?: string | null;
  /** The name a coded refusal cites: a company (login_other_company) or an employee (login_pointer_conflict). */
  reasonSubject?: string | null;
  /** link_existing only: linking will reset the login's password (someone other than the person has handled it). */
  willResetCredential?: boolean;
  /** Who last set the employee's work email (every credential is sent there), and when (UTC ISO). */
  workEmailSetBy?: string | null;
  workEmailSetAtUtc?: string | null;
  /** The work email was changed after the record was created and there is no activated login: confirm it first. */
  workEmailChangedAfterCreation?: boolean;
}

export interface EmployeeLoginLinkResult {
  employeeId: number;
  userId: string;
  email: string;
  status: string;
  accessMode: string;
  isActive: boolean;
  alreadyLinked: boolean;
  /** Someone other than the person had held a credential for the login: its password was made unusable and the
   *  person sets their own from a fresh invitation to their work email. */
  credentialReset?: boolean;
  /** Returned only when the invitation could not be emailed — pass it on by hand. */
  invitationUrl?: string | null;
  emailSent?: boolean;
  deliveryMessage?: string;
  /** The caller entered this employee's work email: the invitation was never emailed — hand it over in person. */
  handOverInPerson?: boolean;
}

/** POST /api/access/employee-logins/invite. `invitationUrl` must be shared by hand when `emailSent` is false. */
export interface EmployeeLoginInvitation {
  userId: string;
  employeeId: number;
  email: string;
  accessMode: string;
  status: string;
  invitationExpiresAtUtc?: string | null;
  invitationUrl: string;
  emailDeliveryConfigured: boolean;
  emailSent: boolean;
  deliveryMessage: string;
  /** The caller entered this employee's work email: the link was never emailed — hand it over in person. */
  handOverInPerson?: boolean;
}

export interface UserAccess {
  userId: string;
  employeeId?: number;
  email: string;
  fullName: string;
  accessMode: string;
  requiresPasswordSetup: boolean;
  roles: string[];
  permissions: string[];
  deniedPermissions: string[];
}

export interface RoleItem {
  id: string;
  name: string;
  description: string;
  isSystem: boolean;
  isActive: boolean;
  isEditable: boolean;
  authorityLevel: number;
  permissions: string[];
}

/**
 * The caller's privilege ceiling (GET /api/access/ceiling): what the server will let THIS caller assign or edit.
 * The reasons are the server's own sentences (EN and AR), so the screen and the 403 always say the same thing.
 */
export interface RoleCeiling {
  roleId: string;
  name: string;
  canAssign: boolean;
  assignRefusalCode?: string | null;
  assignRefusalEn?: string | null;
  assignRefusalAr?: string | null;
  canEdit: boolean;
  editRefusalCode?: string | null;
  editRefusalEn?: string | null;
  editRefusalAr?: string | null;
}

export interface AccessCeiling {
  userId: string;
  isAdmin: boolean;
  heldPermissions: string[];
  roles: RoleCeiling[];
}

export interface PermissionMatrixRow {
  permissionKey: string;
  module: string;
  description: string;
  roles: Record<string, boolean>;
}

export interface PermissionMatrix {
  roles: RoleItem[];
  matrix: PermissionMatrixRow[];
}

export interface EffectivePermissions {
  userId: string;
  email: string;
  roles: string[];
  grantedByRole: string[];
  explicitlyAllowed: string[];
  explicitlyDenied: string[];
  effective: string[];
}

export interface PermissionItem {
  id: string;
  key: string;
  module: string;
  description: string;
}

export interface PermissionGrantorRecord {
  id: string;
  grantorUserId: string;
  grantorEmail: string;
  grantorName: string;
  permissionScope: string;
  canSubDelegate: boolean;
  grantedByUserId?: string;
  expiresAtUtc?: string;
  isActive: boolean;
  reason: string;
  createdAtUtc: string;
}

export interface EntityGrant {
  id: string;
  userId: string;
  companyId?: string;
  role: string;
  createdAtUtc: string;
  grantMode: 'SelectedCompanies' | 'AllCurrentCompanies' | 'AllCurrentAndFutureCompanies';
}

export interface ApprovalDelegation {
  id: string;
  fromEmployeeId: number;
  toEmployeeId: number;
  fromUserId?: string;
  toUserId?: string;
  scope: string;
  startDate: string;
  endDate: string;
  status: string;
  reason: string;
}

export interface ApprovalAuthority {
  id: string;
  employeeId: number;
  userId?: string;
  authorityScope: string;
  approverRole: string;
  amountLimit?: number;
  currency: string;
  canFinalApprove: boolean;
  isActive: boolean;
}

export interface SecuritySetting {
  id: string;
  tenantId: string;
  passwordMinLength: number;
  passwordRequireUppercase: boolean;
  passwordRequireLowercase: boolean;
  passwordRequireDigit: boolean;
  passwordRequireSpecial: boolean;
  passwordExpiryDays: number;
  passwordHistoryCount: number;
  maxFailedLoginAttempts: number;
  lockoutDurationMinutes: number;
  sessionTimeoutMinutes: number;
  refreshTokenExpiryDays: number;
  allowMultipleSessions: boolean;
  mfaRequired: boolean;
  updatedAtUtc: string;
}

export interface AuditLogItem {
  id: string;
  tenantId: string;
  action: string;
  entityName: string;
  entityId?: string;
  ipAddress?: string;
  userAgent?: string;
  metadata?: string;
  userId?: string;
  createdAtUtc: string;
  /** True when metadata, IP and user agent were withheld (caller lacks security.manage). */
  rawDetailRedacted?: boolean;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

/**
 * The outcome of issuing a password-reset link. `emailSent` is the only field that means the user
 * actually received something; `resetUrl` is populated exactly when it is false, so the
 * administrator can pass the link on instead of being told about an email that never left.
 */
export interface PasswordResetLinkResult {
  userId: string;
  email: string;
  expiresAtUtc: string;
  emailDeliveryConfigured: boolean;
  emailSent: boolean;
  resetUrl: string | null;
  message: string;
}

// ── API clients ───────────────────────────────────────────────────────────────

export const usersApi = {
  list: (params: { search?: string; status?: string; role?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<UserListItem>>('/api/access/users', { params }).then(r => r.data),

  listAll: (params: { search?: string; status?: string; role?: string } = {}) =>
    fetchAllPages((page, pageSize) => usersApi.list({ ...params, page, pageSize })),

  get: (userId: string) =>
    client.get<UserListItem>(`/api/access/users/${userId}`).then(r => r.data),

  create: (body: { email: string; fullName: string; password: string; roles: string[]; companyId?: string; isGroupScope?: boolean }) =>
    client.post('/api/access/users', body).then(r => r.data),

  update: (userId: string, body: { fullName?: string; phoneNumber?: string; preferredLanguage?: string; timezone?: string }) =>
    client.put<UserListItem>(`/api/access/users/${userId}`, body).then(r => r.data),

  assignRoles: (userId: string, roles: string[]) =>
    client.put(`/api/access/users/${userId}/roles`, { roles }).then(r => r.data),

  getAccess: (userId: string) =>
    client.get<UserAccess>(`/api/access/users/${userId}/access`).then(r => r.data),

  setAccessMode: (userId: string, accessMode: string, reason?: string) =>
    client.put<UserAccess>(`/api/access/users/${userId}/access-mode`, { accessMode, reason }).then(r => r.data),

  setPermissionOverride: (userId: string, body: { permissionKey: string; effect: string; reason?: string; expiresAtUtc?: string }) =>
    client.post<UserAccess>(`/api/access/users/${userId}/permission-overrides`, body).then(r => r.data),

  activate: (userId: string) =>
    client.patch(`/api/access/users/${userId}/activate`),

  suspend: (userId: string, reason?: string) =>
    client.patch(`/api/access/users/${userId}/suspend`, { reason }),

  lock: (userId: string, reason?: string) =>
    client.patch(`/api/access/users/${userId}/lock`, { reason }),

  unlock: (userId: string) =>
    client.patch(`/api/access/users/${userId}/unlock`),

  /**
   * Issues a single-use, one-hour password-reset link for the user. Administrators never choose
   * another person's password; the server emails the link when the workspace has a mail transport
   * and otherwise hands it back here to be passed on by hand. `resetUrl` is present only in that
   * second case, and only once — it cannot be fetched again.
   */
  issuePasswordResetLink: (userId: string) =>
    client
      .post<PasswordResetLinkResult>(`/api/access/users/${userId}/password-reset-link`)
      .then(r => r.data),

  delete: (userId: string) =>
    client.delete(`/api/access/users/${userId}`),

  inviteEmployee: (body: { employeeId: number; accessMode: string; confirmedWorkEmail?: boolean; roles?: string[]; invitationHours?: number }) =>
    client.post<EmployeeLoginInvitation>('/api/access/employee-logins/invite', body).then(r => r.data),

  /** Where one employee record stands on the way to Self-Service, and the one next step. */
  employeeLoginStatus: (employeeId: number) =>
    client.get<EmployeeLoginStatus>(`/api/access/employee-logins/${employeeId}`).then(r => r.data),

  /** Links an existing, active login to the employee record whose work email it carries. */
  linkExistingLogin: (body: { employeeId: number; userId: string; reason: string; confirmedWorkEmail?: boolean }) =>
    client.post<EmployeeLoginLinkResult>('/api/access/employee-logins/link-existing', body).then(r => r.data),
};

export const rolesApi = {
  list: () =>
    client.get<RoleItem[]>('/api/access/roles').then(r => r.data),

  // A reply that is not a ceiling (an older server, a proxy page, a test double) is treated as "unavailable",
  // exactly like a failed request — the screen then falls back to the server's own 403s, never crashes.
  ceiling: () =>
    client.get<AccessCeiling>('/api/access/ceiling').then(r => {
      const d = r.data as Partial<AccessCeiling> | null | undefined;
      if (!d || !Array.isArray(d.roles) || !Array.isArray(d.heldPermissions)) throw new Error('access ceiling unavailable');
      return d as AccessCeiling;
    }),

  permissions: () =>
    client.get<PermissionItem[]>('/api/access/permissions').then(r => r.data),

  create: (body: { name: string; description?: string; authorityLevel?: number; permissions?: string[] }) =>
    client.post<RoleItem>('/api/access/roles', body).then(r => r.data),

  update: (roleId: string, body: { name?: string; description?: string; authorityLevel?: number }) =>
    client.put<RoleItem>(`/api/access/roles/${roleId}`, body).then(r => r.data),

  activate: (roleId: string) =>
    client.patch(`/api/access/roles/${roleId}/activate`),

  deactivate: (roleId: string) =>
    client.patch(`/api/access/roles/${roleId}/deactivate`),

  setPermissions: (roleId: string, permissions: string[]) =>
    client.put<RoleItem>(`/api/access/roles/${roleId}/permissions`, { permissions }).then(r => r.data),

  getMatrix: () =>
    client.get<PermissionMatrix>('/api/access/permission-matrix').then(r => r.data),

  saveMatrix: (rolePermissions: Record<string, string[]>) =>
    client.put('/api/access/permission-matrix', { rolePermissions }),

  getEffectivePermissions: (userId: string) =>
    client.get<EffectivePermissions>(`/api/access/users/${userId}/effective-permissions`).then(r => r.data),

  deletePermissionOverride: (userId: string, overrideId: string) =>
    client.delete(`/api/access/users/${userId}/permission-overrides/${overrideId}`),
};

export const grantorsApi = {
  list: () =>
    client.get<PermissionGrantorRecord[]>('/api/access/permission-grantors').then(r => r.data),

  add: (body: { grantorUserId: string; permissionScope: string; canSubDelegate?: boolean; expiresAtUtc?: string; reason?: string }) =>
    client.post<PermissionGrantorRecord>('/api/access/permission-grantors', body).then(r => r.data),

  revoke: (recordId: string) =>
    client.delete(`/api/access/permission-grantors/${recordId}`),
};

export const permissionGrantApi = {
  grantBulk: (userId: string, body: { items: { permissionKey: string; effect: 'Allow' | 'Deny' | 'Remove' }[]; reason?: string }) =>
    client.post<UserAccess>(`/api/access/users/${userId}/grant-permissions-bulk`, body).then(r => r.data),
};

export const entityGrantsApi = {
  list: (userId?: string) =>
    client.get<EntityGrant[]>('/api/access/entity-grants', { params: userId ? { userId } : undefined }).then(r => r.data),

  create: (body: { userId: string; companyId?: string; role: string; grantMode?: string }) =>
    client.post<EntityGrant>('/api/access/entity-grants', body).then(r => r.data),

  remove: (id: string) =>
    client.delete(`/api/access/entity-grants/${id}`),

  setGroupScope: (userId: string, isGroupScope: boolean) =>
    client.patch(`/api/access/users/${userId}/group-scope`, { isGroupScope }),
};

export const delegationsApi = {
  list: () =>
    client.get<ApprovalDelegation[]>('/api/access/approval-delegations').then(r => r.data),

  create: (body: { fromEmployeeId: number; toEmployeeId: number; scope: string; startDate: string; endDate: string; reason?: string }) =>
    client.post<ApprovalDelegation>('/api/access/approval-delegations', body).then(r => r.data),

  cancel: (delegationId: string) =>
    client.patch(`/api/access/approval-delegations/${delegationId}/cancel`),
};

export const authoritiesApi = {
  list: () =>
    client.get<ApprovalAuthority[]>('/api/access/approval-authorities').then(r => r.data),

  create: (body: { employeeId: number; authorityScope: string; approverRole: string; amountLimit?: number; currency?: string; canFinalApprove: boolean }) =>
    client.post<ApprovalAuthority>('/api/access/approval-authorities', body).then(r => r.data),

  update: (authorityId: string, body: { employeeId: number; authorityScope: string; approverRole: string; amountLimit?: number; currency?: string; canFinalApprove: boolean }) =>
    client.put<ApprovalAuthority>(`/api/access/approval-authorities/${authorityId}`, body).then(r => r.data),
};

export const securitySettingsApi = {
  get: () =>
    client.get<SecuritySetting>('/api/access/security-settings').then(r => r.data),

  update: (body: Partial<Omit<SecuritySetting, 'id' | 'tenantId' | 'updatedAtUtc'>>) =>
    client.put<SecuritySetting>('/api/access/security-settings', body).then(r => r.data),
};

export const identityAuditApi = {
  list: (params: { limit?: number } = {}) =>
    client.get<AuditLogItem[]>('/api/audit-logs', { params }).then(r => r.data),

  /**
   * The tenant-wide tamper-evident audit chain, verified end to end (Admin only). The sibling
   * verifier for the payroll chain is `payrollApi.auditIntegrity`. Both existed with no caller:
   * the product could prove its own audit trail had not been altered and no human could ask it to.
   */
  integrity: () =>
    client.get<AuditIntegrityReport>('/api/audit-logs/integrity').then(r => r.data),
};
