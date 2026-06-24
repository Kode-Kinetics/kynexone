import client from './client';
import { type RawImportCommit, toImportResult } from './organization';

// ── Types ──────────────────────────────────────────────────────────────────────

export interface MasterDataType {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  description: string;
  isSystemDefined: boolean;
  allowCustomValues: boolean;
  isActive: boolean;
}

export interface MasterDataValue {
  id: string;
  typeId: string;
  code: string;
  valueEn: string;
  valueAr: string;
  extraJson?: string;
  sortOrder: number;
  isDefault: boolean;
  isSystemDefined: boolean;
  isActive: boolean;
}

export interface NumberingRule {
  id: string;
  entityType: string;
  prefix: string;
  suffix: string;
  paddingLength: number;
  separator: string;
  includeYear: boolean;
  includeMonth: boolean;
  currentSequence: number;
  resetYearly: boolean;
  isActive: boolean;
}

export interface SystemSetting {
  id: string;
  category: string;
  settingKey: string;
  settingValue: string;
  dataType: string;
  description: string;
  isReadOnly: boolean;
}

export interface GCCComplianceSetting {
  id: string;
  countryCode: string;
  wpsEnabled: boolean;
  wpsAgentId: string;
  wpsMolCode: string;
  sifEnabled: boolean;
  eosbEnabled: boolean;
  eosbYears1To5Rate: number;
  eosbYearsAbove5Rate: number;
  eosbMinYears: number;
  workWeek: string;
  weekendDays: string;
  visaTrackingEnabled: boolean;
  visaAlertDays: number;
  iqamaRequired: boolean;
  iqamaAlertDays: number;
  emiratesIdRequired: boolean;
  ramadanHoursEnabled: boolean;
  ramadanReducedHoursPerDay: number;
}

export interface Location {
  id: string;
  branchId?: string;
  code: string;
  nameEn: string;
  nameAr: string;
  addressLine1: string;
  city: string;
  countryCode: string;
  postalCode: string;
  latitude?: number;
  longitude?: number;
  geofenceRadiusMeters?: number;
  isActive: boolean;
}

export interface FiscalYear {
  id: string;
  code: string;
  year: number;
  startDate: string;
  endDate: string;
  status: string;
  isCurrent: boolean;
}

export interface NotificationTemplate {
  id: string;
  code: string;
  eventType: string;
  channel: string;
  subjectEn: string;
  subjectAr: string;
  bodyEn: string;
  bodyAr: string;
  variables: string;
  isActive: boolean;
}

export interface AdminAuditLog {
  id: string;
  entityType: string;
  entityId: string;
  action: string;
  oldValuesJson: string;
  newValuesJson: string;
  performedByName: string;
  createdAtUtc: string;
}

// ── API clients ───────────────────────────────────────────────────────────────

export const masterDataApi = {
  listTypes: (activeOnly = true) =>
    client.get<MasterDataType[]>('/api/admin/master-data/types', { params: { activeOnly } }).then(r => r.data),

  createType: (body: { code: string; nameEn: string; nameAr?: string; description?: string; allowCustomValues: boolean }) =>
    client.post<MasterDataType>('/api/admin/master-data/types', body).then(r => r.data),

  updateType: (id: string, body: { nameEn: string; nameAr?: string; description?: string; allowCustomValues: boolean }) =>
    client.put<MasterDataType>(`/api/admin/master-data/types/${id}`, body).then(r => r.data),

  deleteType: (id: string) =>
    client.delete(`/api/admin/master-data/types/${id}`),

  listValues: (typeId: string, activeOnly = true) =>
    client.get<MasterDataValue[]>(`/api/admin/master-data/types/${typeId}/values`, { params: { activeOnly } }).then(r => r.data),

  listValuesByCode: (typeCode: string, activeOnly = true) =>
    client.get<MasterDataValue[]>('/api/admin/master-data/values', { params: { typeCode, activeOnly } }).then(r => r.data),

  createValue: (typeId: string, body: { code: string; valueEn: string; valueAr?: string; extraJson?: string; sortOrder: number; isDefault: boolean }) =>
    client.post<MasterDataValue>(`/api/admin/master-data/types/${typeId}/values`, body).then(r => r.data),

  updateValue: (id: string, body: Partial<MasterDataValue>) =>
    client.put<MasterDataValue>(`/api/admin/master-data/values/${id}`, body).then(r => r.data),

  deleteValue: (id: string) =>
    client.delete(`/api/admin/master-data/values/${id}`),
};

export const numberingRulesApi = {
  list: () =>
    client.get<NumberingRule[]>('/api/admin/numbering-rules').then(r => r.data),

  upsert: (body: { entityType: string; prefix: string; suffix?: string; paddingLength: number; separator: string; includeYear: boolean; includeMonth: boolean; resetYearly: boolean }) =>
    client.post<NumberingRule>('/api/admin/numbering-rules', body).then(r => r.data),

  delete: (id: string) => client.delete(`/api/admin/numbering-rules/${id}`),
};

export const systemSettingsApi = {
  list: (category?: string) =>
    client.get<SystemSetting[]>('/api/admin/system-settings', { params: { category } }).then(r => r.data),

  upsert: (body: { category: string; settingKey: string; settingValue: string; dataType?: string; description?: string }) =>
    client.post<SystemSetting>('/api/admin/system-settings', body).then(r => r.data),
};

export const gccSettingsApi = {
  list: (countryCode?: string) =>
    client.get<GCCComplianceSetting[]>('/api/admin/gcc-settings', { params: { countryCode } }).then(r => r.data),

  upsert: (body: Partial<GCCComplianceSetting> & { countryCode: string }) =>
    client.post<GCCComplianceSetting>('/api/admin/gcc-settings', body).then(r => r.data),
};

export const fiscalYearsApi = {
  list: () =>
    client.get<FiscalYear[]>('/api/admin/fiscal-years').then(r => r.data),

  create: (body: { year: number; startDate: string; endDate: string }) =>
    client.post<FiscalYear>('/api/admin/fiscal-years', body).then(r => r.data),

  close: (id: string) =>
    client.patch<FiscalYear>(`/api/admin/fiscal-years/${id}/close`).then(r => r.data),

  delete: (id: string) => client.delete(`/api/admin/fiscal-years/${id}`),
};

export const locationsApi = {
  list: (branchId?: string) =>
    client.get<Location[]>('/api/admin/locations', { params: { branchId } }).then(r => r.data),

  create: (body: { branchId?: string; code: string; nameEn: string; nameAr?: string; addressLine1?: string; city?: string; countryCode?: string; postalCode?: string; latitude?: number; longitude?: number; geofenceRadiusMeters?: number }) =>
    client.post<Location>('/api/admin/locations', body).then(r => r.data),

  update: (id: string, body: Partial<Location>) =>
    client.put<Location>(`/api/admin/locations/${id}`, body).then(r => r.data),

  delete: (id: string) => client.delete(`/api/admin/locations/${id}`),

  export: () => client.get<string>('/api/locations/export', { responseType: 'text' }).then(r => r.data),
  importTemplate: () => client.get<string>('/api/locations/import-template', { responseType: 'text' }).then(r => r.data),
  import: (csv: string) => client.post<RawImportCommit>('/api/locations/import', { csv }).then(r => toImportResult(r.data)),
};

export const notificationTemplatesApi = {
  list: (channel?: string) =>
    client.get<NotificationTemplate[]>('/api/admin/notification-templates', { params: { channel } }).then(r => r.data),

  create: (body: { code: string; eventType: string; channel: string; subjectEn?: string; subjectAr?: string; bodyEn: string; bodyAr?: string; variables?: string }) =>
    client.post<NotificationTemplate>('/api/admin/notification-templates', body).then(r => r.data),

  update: (id: string, body: Partial<NotificationTemplate>) =>
    client.put<NotificationTemplate>(`/api/admin/notification-templates/${id}`, body).then(r => r.data),

  delete: (id: string) => client.delete(`/api/admin/notification-templates/${id}`),
};

export const adminAuditApi = {
  list: (params: { entityType?: string; from?: string; to?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; pageSize: number; items: AdminAuditLog[] }>('/api/admin/audit-logs', { params }).then(r => r.data),
};
