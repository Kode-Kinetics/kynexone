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
  status: string; // Active | Archived
  notes: string;
  /** True when shown in a company view but owned at group level. */
  inherited?: boolean;
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
  listStatutory: (companyId: string | null | undefined, country: string, jurisdiction: string) =>
    client
      .get<StatutoryRateRow[]>('/api/finance/rates/statutory', {
        params: { companyId: companyId || undefined, country, jurisdiction },
      })
      .then((r) => r.data),
  createOverride: (data: StatutoryOverrideRequest) =>
    client.post<StatutoryOverride>('/api/finance/rates/statutory/override', data).then((r) => r.data),
  approveOverride: (id: string) =>
    client.post<StatutoryOverride>(`/api/finance/rates/statutory/override/${id}/approve`, {}).then((r) => r.data),
  revertOverride: (id: string) => client.delete(`/api/finance/rates/statutory/override/${id}`),
};
