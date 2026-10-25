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
  accountType: string; // Asset | Liability | Equity | Income | Expense (older rows may say Revenue)
  isActive: boolean;
  /** The account code in the client's ERP, when it differs from `code`. Journal exports send it. */
  externalCode?: string | null;
  /** Required | Optional | NotAllowed. Absent on a server that predates cost accounting (reads as Optional). */
  costCenterRule?: string;
}

export interface GlAccountRequest {
  code: string;
  name: string;
  accountType: string;
  isActive: boolean;
  /** Create: optional. Update: omitted/null leaves it unchanged, '' clears it. */
  externalCode?: string | null;
  /** Required | Optional | NotAllowed. Create: omitted = Optional. Update: omitted leaves it unchanged. */
  costCenterRule?: string;
}

/** POST /accounts/cost-centre-rule-preset: the recommended rule per account type, counted before it is applied. */
export interface GlRulePresetResult {
  dryRun: boolean;
  /** Accounts in the scope whose rule differs from the recommended one. */
  changed: number;
  total: number;
  byType: { accountType: string; to: string; count: number }[];
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
  /** One entry per functional class that has its own account for this driver (company beats group). */
  classRules?: { functionalClass: string; accountId: string; inherited: boolean }[];
}

export interface GlMappingInput {
  driverKey: string;
  accountId: string;
  /** The fixed cost centre of the posting rule. Company mappings only; not together with a functional class. */
  segmentCostCenterId?: string | null;
  /** null = every class. COGS | SELLING | G_AND_A | R_AND_D | OPERATIONS (CAPITALISABLE is reserved). */
  functionalClass?: string | null;
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
export const GL_ACCOUNT_TYPES = ['Asset', 'Liability', 'Equity', 'Income', 'Expense'] as const;

// ── Where does each driver post? (GET /gl/resolve) ───────────────────────────
export type GlResolveSource = 'CompanyMapping' | 'GroupMapping' | 'CompanyDriverDefault' | 'GroupDriverDefault' | 'BuiltIn';
export type GlResolveProblem = 'Unmapped' | 'InactiveAccount' | 'NotInChart' | 'InactiveDriver';

export interface GlResolveRow {
  driverKey: string;
  label: string;
  category: string;
  side: string; // DR | CR
  componentCodes?: string[];
  account: { id: string | null; code: string; name: string };
  source: GlResolveSource | string;
  existsInChart?: boolean;
  isActive?: boolean;
  problem?: GlResolveProblem | string | null;
}

export interface GlResolveResponse {
  companyId: string | null;
  rows: GlResolveRow[];
}

// ── Control-account sign health (GET /gl/control-accounts/health) ───────────
export interface GlControlAccountHealthRow {
  driver: string;
  account: string;
  nature: string;
  scopedBalance: number;
  tenantBalance: number;
  isHealthy: boolean;
  violation: string | null;
}

export interface GlControlAccountHealth {
  generatedAtUtc: string;
  companyId: string | null;
  healthy: boolean;
  violations: string[];
  accounts: GlControlAccountHealthRow[];
}

// ── Chart of accounts CSV import ─────────────────────────────────────────────
export interface GlAccountImportResult {
  dryRun: boolean;
  created: number;
  updated: number;
  unchanged: number;
  errors: { row: number; code: string; message: string }[];
}

/**
 * Applying an import that fails validation is refused with 422 and the SAME body as a dry run
 * (per-row errors, nothing written). True when an error response body has that shape.
 */
export function isGlAccountImportResult(value: unknown): value is GlAccountImportResult {
  const v = value as Partial<GlAccountImportResult> | null;
  return !!v && typeof v === 'object' && Array.isArray(v.errors)
    && typeof v.created === 'number' && typeof v.updated === 'number';
}

export interface GlSeedResult {
  accounts: number;
  mappings: number;
  drivers: number;
  rateDefinitions: number;
}

// ── Cost accounting: readiness and the per-company switch ───────────────────
export type ReadinessSeverity = 'ok' | 'warning' | 'blocker';

/** One capped example under a readiness check: an id and a code, plus the AM-1 detail for employees. */
export interface ReadinessSample {
  id: string | number;
  code: string;
  /** invalid-reference only: department | position | employee | company. */
  kind?: string;
  employeeId?: number;
  employeeCode?: string;
  /** employee | position | department | parentDepartment | company */
  level?: string;
  departmentId?: string | null;
  positionId?: string | null;
  parentDepartmentId?: string | null;
  reason?: string;
}

export interface ReadinessCheck {
  key: string;
  severity: ReadinessSeverity | string;
  count: number;
  /** Capped (employees 10, the rest 25); `count` is the true total. */
  sample: ReadinessSample[];
}

export interface CostAccountingSettings {
  enabled: boolean;
  defaultCostCenterId: string | null;
  smallCellMin: number;
  /** A payroll run of the company awaits finance review or lock (M-7): the switch and default cannot move. */
  awaitingRun: boolean;
}

export interface CostAccountingReadiness {
  companyId: string;
  settings: CostAccountingSettings;
  checks: ReadinessCheck[];
  smallCellExposure: { count: number; threshold: number };
}

/** PUT /cost-accounting: a field left out stays as it is. Every change needs a reason. */
export interface CostAccountingChange {
  enabled?: boolean;
  defaultCostCenterId?: string | null;
  clearDefaultCostCenter?: boolean;
  reason: string;
  /** Turning ON with readiness blockers or small-cell exposure is refused without this. */
  acknowledgeBlockers?: boolean;
}

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
  /** GET /cost-accounting/readiness: the company's switch settings and the dry-run checks (needs finance.gl.read). */
  costAccountingReadiness: (companyId: string) =>
    client.get<CostAccountingReadiness>('/api/finance/gl/cost-accounting/readiness', { params: { companyId } }).then((r) => r.data),
  /** PUT /cost-accounting (needs finance.gl.manage). */
  setCostAccounting: (companyId: string, change: CostAccountingChange) =>
    client.put<{ companyId: string; changed: string[]; settings: CostAccountingSettings }>('/api/finance/gl/cost-accounting', change, { params: { companyId } }).then((r) => r.data),
  /** Dry run: counts what "Apply recommended rules" would change and writes nothing. */
  previewRulePreset: (companyId?: string | null) =>
    client
      .post<GlRulePresetResult>('/api/finance/gl/accounts/cost-centre-rule-preset', {}, { params: { ...(scoped(companyId) ?? {}), dryRun: true } })
      .then((r) => r.data),
  /** Applies the recommended rules to the scope's accounts. Needs a reason; refused (409) while a run awaits lock. */
  applyRulePreset: (reason: string, companyId?: string | null) =>
    client
      .post<GlRulePresetResult>('/api/finance/gl/accounts/cost-centre-rule-preset', { reason }, { params: { ...(scoped(companyId) ?? {}), dryRun: false } })
      .then((r) => r.data),

  // ── Driver → account mappings ──────────────────────────────────────────────
  listMappings: (companyId?: string | null) =>
    client.get<GlMappingRow[]>('/api/finance/gl/mappings', { params: scoped(companyId) }).then((r) => r.data),
  /** `reason` is recorded in the audit log (M-10); the server requires it for classes and fixed cost centres. */
  setMappings: (mappings: GlMappingInput[], companyId?: string | null, reason?: string) =>
    client
      .put<{ count: number; changed: number; removed: number }>('/api/finance/gl/mappings', mappings, {
        params: { ...(scoped(companyId) ?? {}), ...(reason?.trim() ? { reason: reason.trim() } : {}) },
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

  // ── Seed defaults (group scope only): adds what is missing, never changes existing rows ──
  seedDefaults: () => client.post<GlSeedResult>('/api/finance/gl/seed-defaults', {}).then((r) => r.data),

  // ── Effective routing + health ─────────────────────────────────────────────
  resolve: (companyId?: string | null) =>
    client.get<GlResolveResponse>('/api/finance/gl/resolve', { params: scoped(companyId) }).then((r) => r.data),
  controlAccountHealth: (companyId?: string | null) =>
    client
      .get<GlControlAccountHealth>('/api/finance/gl/control-accounts/health', { params: scoped(companyId) })
      .then((r) => r.data),

  // ── Chart of accounts CSV ──────────────────────────────────────────────────
  exportAccounts: (companyId?: string | null) =>
    client
      .get<Blob>('/api/finance/gl/accounts/export', { params: scoped(companyId), responseType: 'blob' })
      .then((r) => r.data),
  importAccounts: (file: File, companyId: string | null | undefined, dryRun: boolean) => {
    const form = new FormData();
    form.append('file', file);
    return client
      .post<GlAccountImportResult>('/api/finance/gl/accounts/import', form, {
        params: { ...(scoped(companyId) ?? {}), dryRun },
      })
      .then((r) => r.data);
  },
};
