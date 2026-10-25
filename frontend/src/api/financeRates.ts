import client from './client';

// ─────────────────────────────────────────────────────────────────────────────
// Finance Rates — Phase 2. TWO DISJOINT SURFACES with different governance:
//
//  A. /company   — FULL client CRUD over NON-statutory, client-configurable rates
//                  (custom allowances/deductions, company pay parameters, ABOVE-floor
//                  EOSB enhancement). Allow-list gated, typed + bounded, effective-dated,
//                  superseded (not mutated) on value change. Perm: payroll.rates.manage.
//
//  B. /statutory — BOUNDED per-company OVERRIDE of a statutory rate. Never a free edit
//                  of the statutory value: requires reason + effective-date + review date
//                  + companyId, is visibly flagged, audited, and MAKER-CHECKER approved
//                  (approver ≠ creator) before it goes Active. The seeded platform default
//                  stays the authoritative fallback. Perm: payroll.rates.statutory_override
//                  to create/revert; approvals.decide to approve.
// ─────────────────────────────────────────────────────────────────────────────

/** Company-id query param, omitted for the group/tenant-default scope. */
const scoped = (companyId?: string | null) => (companyId ? { companyId } : undefined);

// ── Surface A — company rate policies ────────────────────────────────────────

export interface CompanyRate {
  id: string;
  companyId: string | null;
  rateKey: string;
  rateCategory: string; // Allowance | Deduction | EOSB | PayParameter
  rateValue: string;
  dataType: string; // decimal
  unit: string; // amount | percent | days | multiplier
  effectiveFrom: string; // yyyy-MM-dd
  effectiveTo: string | null;
  status: string; // Active | PendingApproval | Superseded | Archived
  notes: string;
  /** True when shown in a company view but owned at group level. */
  inherited?: boolean;
  /** Plain-language name from the rate registry (newer servers). */
  label?: string | null;
  /** ISO currency for `amount` rates (newer servers). */
  currency?: string | null;
  /** The row payroll uses today for this key in this scope (newer servers). */
  inEffect?: boolean;
  /** False = recorded only: payroll never reads this key. The row can be end-dated, never changed. */
  appliedByPayroll?: boolean | null;
}

/** Seeded allow-list entry — the ONLY keys that may be created as company rates. */
export interface RateRegistryEntry {
  rateKey: string;
  rateCategory: string;
  dataType: string;
  unit: string;
  minValue: number | null;
  maxValue: number | null;
  description: string;
  /** Closed list of admissible words for a text-valued key (shown as a dropdown); absent for free text. */
  allowedValues?: string[] | null;
  /** False = recorded only: payroll never reads this key, so the server refuses a new value (422 COMPANY_RATE_NOT_APPLIED). */
  appliedByPayroll?: boolean | null;
  /** Plain-language reason, set when appliedByPayroll is false. */
  notAppliedReason?: string | null;
}

export interface CompanyRateRequest {
  rateKey: string;
  rateValue: string;
  effectiveFrom: string; // yyyy-MM-dd
  effectiveTo?: string | null;
  notes?: string | null;
}

// ── Surface B — statutory bounded overrides ──────────────────────────────────

export interface StatutoryRateRow {
  ruleKey: string;
  resolvedValue: number | null;
  platformDefault: number | null;
  isOverride: boolean;
  overrideId: string | null;
  overrideValue: string | null;
  reason: string | null;
  effectiveFrom: string | null;
  effectiveTo: string | null;
  reviewBy: string | null;
  /** True when the platform default changed after this override was created. */
  defaultDriftedSinceOverride: boolean;
  /** GOSI rate or ceiling: statutory, applied from the GOSI-published platform value; cannot be overridden. */
  statutoryLocked?: boolean;
  /** An override exists for a statutory GOSI key: it was saved but payroll never applied it. */
  neverApplied?: boolean;
  notice?: string | null;
  /** A tenant-level value below the statutory floor: payroll ignores it and pays the platform value. */
  belowFloorIgnored?: boolean | null;
  // ── Newer servers (all optional so an older API still renders) ──
  /** Plain-language rule name, when the server sends one. */
  label?: string | null;
  ruleEffectiveFrom?: string | null;
  ruleEffectiveTo?: string | null;
  /** Legal source citation for the platform value. */
  source?: string | null;
  /** False = the platform value has not yet been verified against the law. */
  verified?: boolean | null;
  /** False = reference value only; payroll does not read it. */
  appliedByPayroll?: boolean | null;
  direction?: 'floor' | 'ceiling' | 'either' | null;
  displayUnit?: StatutoryDisplayUnit | null;
  pendingOverride?: StatutoryPendingOverride | null;
  /** Group scope only. */
  countryCode?: string | null;
  /** Group scope only: how many companies have an active override of this rule. */
  overriddenInCompanies?: number | null;
}

export type StatutoryDisplayUnit = 'percent' | 'multiplier' | 'days' | 'hours' | 'minutes' | 'amount' | 'text';

/** What a pending request does: set a value, or retire (return to the statutory value). */
export type StatutoryRequestKind = 'change' | 'retire';

export interface StatutoryPendingOverride {
  id: string;
  /** Absent on older servers: treat as 'change'. */
  kind?: StatutoryRequestKind | null;
  /** True when no value of this scope is in force today, so approving it adds a first override. */
  isNew?: boolean | null;
  value: string;
  effectiveFrom: string | null;
  effectiveTo: string | null;
  reason: string | null;
  reviewBy: string | null;
  requestedById?: string | null;
  requestedByName?: string | null;
  requestedAt?: string | null;
  /** True only when the viewer may approve it (holds the permission and did not request it). */
  canApprove?: boolean;
}

export interface StatutoryPendingListItem extends StatutoryPendingOverride {
  /** Null for a tenant-level request (all companies). */
  companyId: string | null;
  companyName?: string | null;
  ruleKey: string;
  countryCode?: string | null;
}

export interface StatutoryOverrideRequest {
  companyId: string;
  countryCode: string;
  jurisdiction: string;
  ruleKey: string;
  overrideValue: string;
  effectiveFrom: string; // yyyy-MM-dd — required
  reason: string; // required
  reviewBy: string; // yyyy-MM-dd — required (expiry/review)
  effectiveTo?: string | null;
  dataType?: string | null;
}

export interface StatutoryOverride {
  id: string;
  companyId: string | null;
  countryCode: string;
  jurisdiction: string;
  ruleKey: string;
  overrideValue: string;
  dataType: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  reviewBy: string | null;
  reason: string;
  status: string; // PendingApproval | Active | Archived
  isOverride: boolean;
  overridesStatutoryDefault: string;
  approvedBy: string | null;
  approvedAtUtc: string | null;
  kind?: StatutoryRequestKind | null;
}

/**
 * DELETE on an override. 204 = a pending request was withdrawn (or nothing was left to end);
 * 202 = an ACTIVE override: only a RETIREMENT REQUEST was filed, payroll keeps the override until a
 * second person approves it.
 */
export interface RevertOverrideResult {
  status: number;
  retirementRequested: boolean;
  request: StatutoryOverride | null;
}

export const financeRatesApi = {
  // ── Surface A ──────────────────────────────────────────────────────────────
  listCompanyRates: (companyId?: string | null) =>
    client.get<CompanyRate[]>('/api/finance/rates/company', { params: scoped(companyId) }).then((r) => r.data),
  registry: () =>
    client.get<RateRegistryEntry[]>('/api/finance/rates/company/registry').then((r) => r.data),
  createCompanyRate: (data: CompanyRateRequest, companyId?: string | null) =>
    client.post<CompanyRate>('/api/finance/rates/company', data, { params: scoped(companyId) }).then((r) => r.data),
  updateCompanyRate: (id: string, data: CompanyRateRequest) =>
    client.put<CompanyRate>(`/api/finance/rates/company/${id}`, data).then((r) => r.data),
  deleteCompanyRate: (id: string) => client.delete(`/api/finance/rates/company/${id}`),

  // ── Surface B ──────────────────────────────────────────────────────────────
  // `country` and `countryCode` carry the same value: older servers read the first, newer the second.
  listStatutory: (companyId: string | null | undefined, country: string, jurisdiction: string) =>
    client
      .get<StatutoryRateRow[]>('/api/finance/rates/statutory', {
        params: { companyId: companyId || undefined, country, countryCode: country, jurisdiction },
      })
      .then((r) => r.data),
  listPendingOverrides: () =>
    client.get<StatutoryPendingListItem[]>('/api/finance/rates/statutory/pending').then((r) => r.data),
  createOverride: (data: StatutoryOverrideRequest) =>
    client.post<StatutoryOverride>('/api/finance/rates/statutory/override', data).then((r) => r.data),
  approveOverride: (id: string) =>
    client.post<StatutoryOverride>(`/api/finance/rates/statutory/override/${id}/approve`, {}).then((r) => r.data),
  revertOverride: (id: string): Promise<RevertOverrideResult> =>
    client
      .delete<StatutoryOverride | ''>(`/api/finance/rates/statutory/override/${id}`)
      .then((r) => ({
        status: r.status,
        retirementRequested: r.status === 202,
        request: r.status === 202 && r.data ? (r.data as StatutoryOverride) : null,
      })),
};
