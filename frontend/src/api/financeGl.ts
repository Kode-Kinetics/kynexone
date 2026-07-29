import client from './client';

// ─────────────────────────────────────────────────────────────────────────────
// Finance GL — Phase 2 (per-company scope + extensible driver store).
//
// Every read/write carries an optional `companyId`:
//   • omitted / null  → the GROUP (tenant-default) scope — CompanyId IS NULL rows.
//                        Writing group defaults requires a group-scoped caller server-side.
//   • a company guid  → that legal entity's overrides (server authorises CanAccessCompany).
//
// The server layers company-over-group (company row wins per driver), so a company
// view shows its own overrides plus inherited group defaults flagged `inherited: true`.
// ─────────────────────────────────────────────────────────────────────────────

/** Company-id query param, omitted entirely for the group/tenant-default scope. */
const scoped = (companyId?: string | null) => (companyId ? { companyId } : undefined);

export interface GlAccount {
  id: string;
  companyId: string | null;
  code: string;
  name: string;
  accountType: string; // Asset | Liability | Expense | Equity | Revenue
  isActive: boolean;
}

export interface GlAccountRequest {
  code: string;
  name: string;
  accountType: string;
  isActive: boolean;
}

export interface GlMappingRow {
  driverKey: string;
  label: string;
  category: string; // Earning | Deduction | Balancing
  accountType: string;
  defaultAccount: string;
  mappedAccountId: string | null;
  segmentCostCenterId: string | null;
  /** True when the effective mapping for this driver is inherited from the group default. */
  inherited: boolean;
}

export interface GlMappingInput {
  driverKey: string;
  accountId: string;
  segmentCostCenterId?: string | null;
}

export interface GlDriver {
  id: string;
  companyId: string | null;
  key: string;
  label: string;
  category: string; // Earning | Deduction | Balancing
  postingSide: string; // DR | CR
  accountType: string;
  defaultCode: string;
  defaultName: string;
  matchSource: string | null;
  matchMode: string; // Exact | Suffix | Prefix | Any
  matchComponentCode: string | null;
  emitsEmployerExpensePair: boolean;
  pairedExpenseDriverKey: string | null;
  isSystem: boolean;
  isActive: boolean;
  sortOrder: number;
  /** True when this is a group-level driver shown inside a company view. */
  inherited: boolean;
}

export interface GlDriverRequest {
  key: string;
  label: string;
  category: string;
  postingSide: string;
  defaultCode: string;
  defaultName: string;
  accountType?: string;
  matchSource?: string | null;
  matchMode?: string;
  matchComponentCode?: string | null;
  emitsEmployerExpensePair?: boolean;
  pairedExpenseDriverKey?: string | null;
  sortOrder?: number;
  isActive?: boolean;
}

export const GL_DRIVER_CATEGORIES = ['Earning', 'Deduction', 'Balancing'] as const;
export const GL_DRIVER_MATCH_MODES = ['Exact', 'Suffix', 'Prefix', 'Any'] as const;
export const GL_ACCOUNT_TYPES = ['Asset', 'Liability', 'Expense', 'Equity', 'Revenue'] as const;

export const financeGlApi = {
  // ── Chart of accounts ──────────────────────────────────────────────────────
  listAccounts: (companyId?: string | null) =>
    client.get<GlAccount[]>('/api/finance/gl/accounts', { params: scoped(companyId) }).then((r) => r.data),
  createAccount: (data: GlAccountRequest, companyId?: string | null) =>
    client.post<GlAccount>('/api/finance/gl/accounts', data, { params: scoped(companyId) }).then((r) => r.data),
  updateAccount: (id: string, data: GlAccountRequest, opts?: { force?: boolean }) =>
    client
      .put<GlAccount>(`/api/finance/gl/accounts/${id}`, data, { params: opts?.force ? { force: true } : undefined })
      .then((r) => r.data),
  deleteAccount: (id: string) => client.delete(`/api/finance/gl/accounts/${id}`),

  // ── Driver → account mappings ──────────────────────────────────────────────
  listMappings: (companyId?: string | null) =>
    client.get<GlMappingRow[]>('/api/finance/gl/mappings', { params: scoped(companyId) }).then((r) => r.data),
  setMappings: (mappings: GlMappingInput[], companyId?: string | null) =>
    client
      .put<{ count: number; changed: number; removed: number }>('/api/finance/gl/mappings', mappings, {
        params: scoped(companyId),
      })
      .then((r) => r.data),

  // ── Extensible driver store ────────────────────────────────────────────────
  listDrivers: (companyId?: string | null) =>
    client.get<GlDriver[]>('/api/finance/gl/drivers', { params: scoped(companyId) }).then((r) => r.data),
  createDriver: (data: GlDriverRequest, companyId?: string | null) =>
    client
      .post<{ driver: GlDriver; warning: string | null }>('/api/finance/gl/drivers', data, { params: scoped(companyId) })
      .then((r) => r.data),
  updateDriver: (id: string, data: GlDriverRequest) =>
    client.put<GlDriver>(`/api/finance/gl/drivers/${id}`, data).then((r) => r.data),
  deleteDriver: (id: string) => client.delete(`/api/finance/gl/drivers/${id}`),

  // ── Seed defaults (group scope only) ───────────────────────────────────────
  seedDefaults: () =>
    client
      .post<{ accounts: number; mappings: number; drivers: number; rateDefinitions: number }>(
        '/api/finance/gl/seed-defaults',
        {},
      )
      .then((r) => r.data),
};
