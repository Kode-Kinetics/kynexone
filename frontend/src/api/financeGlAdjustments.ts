import client from './client';

// ─────────────────────────────────────────────────────────────────────────────
// End-of-service provision + maker-checker finance journals (GlAdjustmentsController).
//
//   GET  /api/finance/gl/eosb/provision?companyId=        provision panel (balance, employees, true-ups)
//   POST /api/finance/gl/eosb/true-up                     request an actuarial true-up (PENDING)
//   GET  /api/finance/gl/reclassifications/legacy-absence  2104/2105 balances still on the books
//   POST /api/finance/gl/reclassifications/legacy-absence  request a move of one balance (PENDING)
//   POST /api/finance/gl/adjustments/{id}/approve|reject   a second person decides; approval posts the journal
//
// Turning monthly provisioning on/off is an ordinary company rate (payparameter.eosb_provisioning),
// written through /api/finance/rates/company like every other one.
// ─────────────────────────────────────────────────────────────────────────────

const BASE = '/api/finance/gl';

export const EOSB_PROVISIONING_KEY = 'payparameter.eosb_provisioning';
export type EosbProvisioningMode = 'Off' | 'StatutoryMonthly';
export type GlAdjustmentStatus = 'Pending' | 'Approved' | 'Rejected';

export interface GlAdjustment {
  id: string;
  companyId: string;
  kind: 'EosbTrueUp' | 'LegacyAbsenceReclass';
  status: GlAdjustmentStatus;
  period: string;
  accountingDate: string;
  amount: number;
  currency: string;
  sourceAccount: string | null;
  targetAccount: string | null;
  reason: string;
  reportReference: string | null;
  requestedByName: string | null;
  requestedAtUtc: string;
  decidedByName: string | null;
  decidedAtUtc: string | null;
  decisionNote: string | null;
  /** The requester never approves their own journal (the server refuses it too). */
  requestedByMe: boolean;
  /** The server says this viewer may approve or reject it (pending, not their own request, may decide). */
  canApprove: boolean;
}

export interface EosbProvisionEmployee {
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  lastMeasuredPeriod: string;
  obligation: number;
  lastMovement: number;
  wageBase: number;
  serviceYears: number;
  rule: string;
  provisionCarried: number;
}

export interface EosbProvisionSummary {
  companyId: string;
  provisionAccount: string;
  expenseAccount: string;
  provisioning: {
    enabled: boolean;
    value: string;
    effectiveFrom: string | null;
    rateKey: string;
    currentRateId: string | null;
    latestValue: string | null;
    latestEffectiveFrom: string | null;
    inheritedFromTenant: boolean;
    scenario: string;
  };
  glBalance: number;
  employeeObligation: number;
  employeeProvisionCarried: number;
  trueUpLayer: number;
  employees: EosbProvisionEmployee[];
  history: { period: string; employees: number; obligation: number; movement: number; catchUp: number }[];
  trueUps: GlAdjustment[];
}

export interface LegacyAbsenceAccount {
  account: string;
  code: string;
  debits: number;
  credits: number;
  balance: number;
  suggestedTarget: string;
  pendingRequestId: string | null;
}

export interface LegacyAbsenceSummary {
  companyId: string;
  accounts: LegacyAbsenceAccount[];
  unattributed: { account: string; code: string; balance: number }[];
  requests: GlAdjustment[];
}

export const financeGlAdjustmentsApi = {
  provision: (companyId: string) =>
    client.get<EosbProvisionSummary>(`${BASE}/eosb/provision`, { params: { companyId } }).then((r) => r.data),
  requestTrueUp: (data: { companyId: string; period: string; amount: number; reason: string; reportReference: string }) =>
    client.post<GlAdjustment>(`${BASE}/eosb/true-up`, data).then((r) => r.data),
  legacyAbsence: (companyId: string) =>
    client.get<LegacyAbsenceSummary>(`${BASE}/reclassifications/legacy-absence`, { params: { companyId } }).then((r) => r.data),
  requestLegacyReclass: (data: { companyId: string; sourceAccount: string; accountingDate: string; targetAccountCode?: string | null; reason: string }) =>
    client.post<GlAdjustment>(`${BASE}/reclassifications/legacy-absence`, data).then((r) => r.data),
  approve: (id: string, note?: string) => client.post(`${BASE}/adjustments/${id}/approve`, { note: note ?? null }).then((r) => r.data),
  reject: (id: string, note: string) => client.post(`${BASE}/adjustments/${id}/reject`, { note }).then((r) => r.data),
};
