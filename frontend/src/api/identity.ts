import client from './client';

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
  lastLoginAtUtc?: string;
  createdAtUtc: string;
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
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

// ── API clients ───────────────────────────────────────────────────────────────

export const usersApi = {
  list: (params: { search?: string; status?: string; role?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<UserListItem>>('/api/access/users', { params }).then(r => r.data),

  get: (userId: string) =>
    client.get<UserListItem>(`/api/access/users/${userId}`).then(r => r.data),

  create: (body: { email: string; fullName: string; password: string; roles: string[] }) =>
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

  adminResetPassword: (userId: string, newPassword: string, mustChangePassword = true) =>
    client.post(`/api/access/users/${userId}/admin-reset-password`, { newPassword, mustChangePassword }),

  delete: (userId: string) =>
    client.delete(`/api/access/users/${userId}`),

  inviteEmployee: (body: { employeeId: number; email?: string; accessMode: string; roles?: string[]; invitationHours?: number }) =>
    client.post('/api/access/employee-logins/invite', body).then(r => r.data),
};

export const rolesApi = {
  list: () =>
    client.get<RoleItem[]>('/api/access/roles').then(r => r.data),

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
  grant: (userId: string, body: { permissionKey: string; effect: string; reason?: string; expiresAtUtc?: string }) =>
    client.post<UserAccess>(`/api/access/users/${userId}/grant-permission`, body).then(r => r.data),
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
};
