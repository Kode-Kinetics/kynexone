import client from './client';

export interface JurisdictionOption {
  code: string;
  label: string;
}

export interface CountryPackOption {
  countryCode: string;
  nameEn: string;
  nameAr: string;
  jurisdictions: JurisdictionOption[];
}

export interface StatutorySummary {
  countryCode: string;
  jurisdiction: string;
  countryNameEn: string;
  countryNameAr: string;
  socialInsuranceScheme: string;
  socialInsuranceDescription: string;
  eosbFormula: string;
  wpsFormat: string;
  wpsFormatLabel: string;
  nationalizationScheme: string;
  currencyCode: string;
  currencySymbol: string;
  localeCode: string;
  isRtl: boolean;
  calendarSystem: string;
}

export interface StatutoryRuleDto {
  id: string;
  countryCode: string;
  jurisdiction: string;
  ruleKey: string;
  ruleValue: string;
  dataType: string;
  description: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  isTenantOverride: boolean;
}

export interface CreateStatutoryRuleRequest {
  countryCode: string;
  jurisdiction: string;
  ruleKey: string;
  ruleValue: string;
  dataType: string;
  description: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
}

export interface UpdateStatutoryRuleRequest {
  ruleValue: string;
  description: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
}

/**
 * A tenant-level statutory change waiting for a second administrator. POST/PUT/DELETE on
 * /api/statutory-rules answer 202 Accepted with this body: nothing reaches payroll until another
 * person with payroll.rates.statutory_override approves it at /api/finance/rates/statutory/override/{id}/approve.
 */
export interface StatutoryChangeRequest {
  status: string; // PendingApproval
  requestId: string;
  kind: 'change' | 'retire';
  countryCode: string;
  jurisdiction: string;
  ruleKey: string;
  value: string;
  effectiveFrom: string; // yyyy-MM-dd
  effectiveTo: string | null; // yyyy-MM-dd, inclusive
  reason: string;
  requestedById: string | null;
  requestedAt: string;
  message: string;
}

/** What a statutory-rule write did: filed a request (the normal case), or nothing left to do (204). */
export type StatutoryRuleWriteResult =
  | { outcome: 'pending'; request: StatutoryChangeRequest }
  | { outcome: 'saved'; rule: StatutoryRuleDto | null };

/** One row of GET /api/finance/rates/statutory/pending. Tenant-level requests have companyId null. */
export interface StatutoryPendingRequest {
  id: string;
  kind: 'change' | 'retire';
  value: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  reason: string | null;
  reviewBy: string | null;
  requestedById: string | null;
  requestedByName: string | null;
  requestedAt: string;
  /** Server-decided: holds the permission, has the scope, and is not the requester. */
  canApprove: boolean;
  companyId: string | null;
  companyName: string | null;
  ruleKey: string;
  countryCode: string;
  jurisdiction: string;
  platformDefault: string | null;
}

const writeResult = (r: { status: number; data: unknown }): StatutoryRuleWriteResult =>
  r.status === 202
    ? { outcome: 'pending', request: r.data as StatutoryChangeRequest }
    : { outcome: 'saved', rule: (r.data as StatutoryRuleDto | '') || null };

export const countryPacksApi = {
  available: () =>
    client.get<CountryPackOption[]>('/api/country-packs/available').then((r) => r.data),

  statutorySummary: (companyId: string) =>
    client.get<StatutorySummary>(`/api/country-packs/company/${companyId}/statutory-summary`).then((r) => r.data),
};

export const statutoryRulesApi = {
  list: (countryCode?: string, jurisdiction?: string) =>
    client
      .get<StatutoryRuleDto[]>('/api/statutory-rules', { params: { countryCode, jurisdiction } })
      .then((r) => r.data),

  /** Files a tenant-level override request (202). Payroll is unchanged until a second admin approves. */
  create: (data: CreateStatutoryRuleRequest) =>
    client.post('/api/statutory-rules', data).then(writeResult),

  /** Files a supersede request (202) for the open tenant version. */
  update: (id: string, data: UpdateStatutoryRuleRequest) =>
    client.put(`/api/statutory-rules/${id}`, data).then(writeResult),

  /** Files a retirement request (202); 204 means the version already ends by tomorrow. */
  remove: (id: string) => client.delete(`/api/statutory-rules/${id}`).then(writeResult),

  /** Tenant-level requests waiting for a decision (company overrides are managed in GL & rates). */
  pending: () =>
    client
      .get<StatutoryPendingRequest[]>('/api/finance/rates/statutory/pending')
      .then((r) => r.data.filter((row) => row.companyId == null)),

  approve: (requestId: string) =>
    client.post(`/api/finance/rates/statutory/override/${requestId}/approve`, {}).then((r) => r.data),

  reject: (requestId: string, reason?: string) =>
    client.post(`/api/finance/rates/statutory/override/${requestId}/reject`, { reason: reason || null }).then((r) => r.data),

  /** The requester withdraws their own pending request. */
  withdraw: (requestId: string) =>
    client.delete(`/api/finance/rates/statutory/override/${requestId}`).then(() => undefined),
};
