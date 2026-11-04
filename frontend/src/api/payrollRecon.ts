import client from './client';

/**
 * Payroll manpower + value reconciliation: the run read against the prior month, signed off by a
 * person before the run can be approved. Endpoints (all under /api/payroll/runs/{id}/recon):
 *
 *   GET    /recon?priorRunId=          payroll.read      ReconDto
 *   GET    /recon/employees            payroll.read      drill-down, paged (max 200)
 *   GET    /recon/export?format=       payroll.read      xlsx | csv file
 *   GET    /recon/signoff              payroll.read      SignoffDto | null
 *   POST   /recon/signoff              payroll.approve   SignoffRequest -> SignoffDto
 *   DELETE /recon/signoff              payroll.approve   withdraw, reason required
 *
 * Kept out of ./payroll.ts on purpose: the shapes below are the frozen contract shared with the
 * backend (see the fixtures in e2e/fixtures/recon-*.json), and that file is edited by many hands.
 *
 * Money is in the company currency, with no FX. Amounts in the exit columns are NEGATIVE (they take
 * the prior amount away), so every row reads prior + adds + exits + continuing = current.
 */

export type ReconMode = 'Full' | 'NoPrior' | 'NotApplicable';
export type ReconSectionKind = 'Earning' | 'Deduction' | 'Employer';

export interface ReconScope {
  /** How much of the company the caller can see: Organization, Department, ... */
  level: string;
  /** True when the caller sees a slice, so every figure is a slice and nothing here is signable. */
  partial: boolean;
  canSignOff: boolean;
  /** Machine reasons the caller cannot sign (shown in words by the screen). */
  reasons: string[];
}

export interface ReconThresholds { pct: number; minAmount: number; source: 'company' | 'default' }

export type VolumeAddCategory = 'NEW_JOINER' | 'REHIRE' | 'LATE_JOINER' | 'REINSTATED' | 'TRANSFER_IN';
export type VolumeExitCategory = 'LEAVER' | 'TRANSFER_OUT' | 'NOT_PAID' | 'UNEXPLAINED';
export type VolumeCategory = VolumeAddCategory | VolumeExitCategory;

export interface ReconVolumeLine {
  category: VolumeCategory;
  kind: 'Add' | 'Exit';
  count: number;
  /** LEAVER only: leavers whose final settlement is accrued / not yet there. */
  fsApproved?: number;
  fsMissing?: number;
}

/** The server's own tie result: a bare boolean, or an object carrying one. */
export type ReconTies = boolean | { ok: boolean };

export interface ReconOffCycleMemo { count: number; gross: number | null }

export interface ReconVolume {
  opening: number;
  closing: number;
  lines: ReconVolumeLine[];
  ties: ReconTies;
  memo: {
    finalWageMonth: number;
    /** A count and gross per side; a bare count is tolerated. */
    offCycle: { prior: ReconOffCycleMemo | number; current: ReconOffCycleMemo | number };
  };
}

export interface ReconAdds { newJoiner: number; rehire: number; lateJoiner: number; reinstated: number; transferIn: number }
export interface ReconExits { leaver: number; transferOut: number; notPaid: number; unexplained: number }
export interface ReconContinuing { package: number; proration: number; variable: number; arrears: number; attendance: number; loans: number; statutory: number; other: number }

/** One row of the value bridge: prior, what joined, what left, what moved among the continuing, now. */
export interface ReconBridgeRow {
  prior: number;
  adds: ReconAdds;
  exits: ReconExits;
  continuing: ReconContinuing;
  current: number;
  delta: number;
  /** null when the prior figure is zero (a percentage of nothing is not a number). */
  deltaPct: number | null;
}

export interface ReconItem extends ReconBridgeRow {
  /** The component code, unique within its section. */
  code: string;
  label: string;
  flagged: boolean;
  /** The written reason recorded at sign-off, when signed. */
  explanation?: string | null;
}

export interface ReconSection { kind: ReconSectionKind; items: ReconItem[] }

export interface ReconHeadline { gross: ReconBridgeRow; deductions: ReconBridgeRow; net: ReconBridgeRow; employer: ReconBridgeRow }

export interface ReconValue {
  sections: ReconSection[];
  headline: ReconHeadline;
  ties: ReconTies;
  /** Line totals less the slip register, within the payslip tolerance. Exposed, never hidden. */
  registerDifference: number;
}

export interface ReconFsComponent { code: string; label?: string; prior: number; current: number; delta: number }
export interface ReconFsLeaver { employeeId: number; employeeCode?: string; employeeName?: string; lastWorkingDay?: string | null }

export interface ReconFinalSettlement {
  /** Every accrued settlement sits in exactly one cell. A list, or a code -> count map. */
  cells: Array<{ code: string; count: number }> | Record<string, number>;
  perComponent: ReconFsComponent[];
  leaversWithoutFs: ReconFsLeaver[];
  ties: ReconTies;
}

export interface ReconIssue { code?: string; message?: string }

export type SignoffSupersededReason = 'Reprocessed' | 'Reopened' | 'SentBack' | 'Voided' | 'Withdrawn' | 'PriorChanged';

export interface SignoffDto {
  id: string;
  status: 'Active' | 'Superseded';
  signedOffByName: string;
  signedOffAtUtc: string;
  snapshotHash: string;
  supersededReason?: SignoffSupersededReason | null;
}

export interface ReconDto {
  runId: string;
  period: string;
  mode: ReconMode;
  priorRunId?: string | null;
  priorPeriod?: string | null;
  /** The newest earlier period-owning run's period, when there is no prior month (NoPrior). */
  lastRunPeriod?: string | null;
  priorNotFinal: boolean;
  priorVoided: boolean;
  currency: string;
  /**
   * The processing version and content hash this reconciliation was computed from. A sign-off is
   * bound to both: if either moved by the time the signer submits, the server refuses (signoff_stale).
   */
  processVersion?: number;
  snapshotHash?: string;
  scope: ReconScope;
  thresholds: ReconThresholds;
  volume: ReconVolume;
  value: ReconValue;
  finalSettlement: ReconFinalSettlement;
  integrity: { ok: boolean; issues: Array<ReconIssue | string> };
  /** The Active sign-off, or the latest superseded one (with its reason), or null. */
  signoff: SignoffDto | null;
}

export interface ExplanationInput { scope: 'Item' | 'Employee'; key: string; text: string }

export interface SignoffRequest {
  expectedProcessVersion: number;
  expectedSnapshotHash: string;
  priorRunId?: string;
  acknowledgedFlaggedEmployeeCount: number;
  explanations: ExplanationInput[];
  notes?: string;
}

// ── Drill-down ─────────────────────────────────────────────────────────────────

export interface ReconEmployeesQuery {
  section: 'volume' | 'value' | 'finalSettlement';
  /** Volume or add/exit category (NEW_JOINER, LEAVER, ...), or OPENING / CLOSING / CONTINUING. */
  category?: string;
  /** Value section: Earning | Deduction | Employer, with the component code. */
  kind?: ReconSectionKind;
  itemCode?: string;
  /** prior | current | a continuing driver (package, proration, ...) | flagged. */
  bucket?: string;
  page?: number;
  pageSize?: number;
}

export interface ReconEmployeeRow {
  employeeId: number;
  employeeCode?: string;
  employeeName?: string;
  /** The bridge category the employee sits in. */
  category?: string;
  /** One-line detail (final settlement status, proration, ...). */
  note?: string | null;
  prior?: number | null;
  current?: number | null;
  delta?: number | null;
}

export interface ReconEmployeesPage { items: ReconEmployeeRow[]; total: number; page: number; pageSize: number }

// ── Errors ─────────────────────────────────────────────────────────────────────

export const RECON_ERROR_CODES = [
  'reconciliation_not_reviewed', 'signoff_requires_organization_scope', 'maker_checker_violation', 'signoff_stale',
  'variance_explanation_required', 'flagged_employees_not_acknowledged', 'recon_integrity_failed', 'run_processing',
  'run_not_signable', 'recon_not_applicable',
] as const;
export type ReconErrorCode = typeof RECON_ERROR_CODES[number];

// ── Calls ──────────────────────────────────────────────────────────────────────

const base = (runId: string) => `/api/payroll/runs/${encodeURIComponent(runId)}/recon`;

function filenameFromDisposition(header: unknown): string | null {
  if (typeof header !== 'string') return null;
  const star = /filename\*=UTF-8''([^;]+)/i.exec(header);
  if (star) { try { return decodeURIComponent(star[1].trim()); } catch { /* fall through to the plain form */ } }
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain ? plain[1].trim() : null;
}

export const payrollReconApi = {
  get: (runId: string, priorRunId?: string | null) =>
    client.get<ReconDto>(base(runId), { params: priorRunId ? { priorRunId } : undefined }).then(r => r.data),

  employees: (runId: string, q: ReconEmployeesQuery) =>
    client.get<ReconEmployeesPage>(`${base(runId)}/employees`, { params: q }).then(r => r.data),

  export: async (runId: string, format: 'xlsx' | 'csv', priorRunId?: string | null) => {
    const r = await client.get<Blob>(`${base(runId)}/export`, { params: { format, ...(priorRunId ? { priorRunId } : {}) }, responseType: 'blob' });
    const filename = filenameFromDisposition(r.headers?.['content-disposition']) ?? `payroll-reconciliation.${format}`;
    return { blob: r.data, filename };
  },

  getSignoff: (runId: string) => client.get<SignoffDto | null>(`${base(runId)}/signoff`).then(r => r.data),

  signoff: (runId: string, body: SignoffRequest) => client.post<SignoffDto>(`${base(runId)}/signoff`, body).then(r => r.data),

  withdraw: (runId: string, reason: string) => client.delete(`${base(runId)}/signoff`, { data: { reason } }).then(r => r.data),
};
