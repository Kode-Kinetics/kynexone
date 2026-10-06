import client from './client';

// Contract renewals (Release A slice R4): the dashboard ("radar"), the shared case DTO, the contract chain, open-now and
// hold / release / cancel. Every route is under /api/contracts, which the server closes unless the tenant has
// release_a on. Dates are ISO yyyy-MM-dd strings (DateOnly on the server).

export type RenewalAction = 'RenewAsIs' | 'RenewWithChanges' | 'ConvertIndefinite' | 'NonRenew';
export type NationalityClass = 'Saudi' | 'NonSaudi';
export type HoldReason = 'Resignation' | 'UnpaidLeave' | 'Abroad' | 'Transfer' | 'LabourDispute';

export interface BlockReason {
  code: string;
  titleEn: string;
  titleAr: string;
  whyEn: string;
  whyAr: string;
  fixEn: string;
  fixAr: string;
  ownerRole: string;
}

export interface RenewalEmployee {
  id: number;
  publicId: string;
  name: string;
  nameAr: string | null;
  code: string;
}

export interface RenewalNext {
  step: string;
  dueOn: string | null;
  consequence: string;
  overdue: boolean;
  daysLeft: number | null;
}

export interface RenewalBadge {
  code: string;
  params: Record<string, string>;
  blockCode: string | null;
}

export interface RenewalDeadlines {
  offerDueOn: string | null;
  noticeDueOn: string | null;
  qiwaSubmitDueOn: string | null;
  qiwaGateDueOn: string | null;
  qiwaRespondByOn: string | null;
  nextHardDeadline: string | null;
}

export interface RenewalCaseItem {
  caseId: string;
  contractId: string;
  contractNumber: string;
  employee: RenewalEmployee;
  companyId: string;
  companyName: string;
  nationalityClass: NationalityClass;
  expiringEndDate: string;
  daysLeft: number;
  stage: 'Preparing' | 'Approving' | 'WithEmployee' | 'Qiwa' | 'Done';
  state: string;
  holdReason: HoldReason | null;
  allowedActions: RenewalAction[];
  contractAction: RenewalAction | null;
  renewalNumber: number | null;
  chainStartedOn: string | null;
  next: RenewalNext | null;
  badges: RenewalBadge[];
  blockReasons: BlockReason[];
  deadlines: RenewalDeadlines;
  fastLaneEligible: boolean;
}

export interface RenewalBucket {
  key: string;
  fromDays: number;
  toDays: number;
  count: number;
  caseIds: string[];
}

export interface RenewalUnopened {
  contractId: string;
  contractNumber: string;
  employee: RenewalEmployee | null;
  endDate: string;
  opensOn: string;
  reason: string;
  blockReason: BlockReason | null;
}

export interface RenewalClosedReview {
  contractId: string;
  contractNumber: string;
  employee: RenewalEmployee | null;
  endDate: string;
  caseId: string;
  caseState: string;
  closedAt: string | null;
}

/** Every due Active fixed-term contract counted once: withOpenReview + withoutReview + withClosedReviewOnly = dueActiveContracts. */
export interface RenewalReconciliation {
  dueActiveContracts: number;
  withOpenReview: number;
  withoutReview: number;
  withClosedReviewOnly: number;
  notYetDue: number;
}

export interface RenewalExceptionTile {
  count: number;
  caseIds: string[];
}

export interface RenewalRadar {
  today: string;
  days: number;
  openLeadDays: number;
  buckets: RenewalBucket[];
  reconciliation: RenewalReconciliation;
  exceptions: {
    expiringWithoutCase: RenewalUnopened[];
    activeWithoutOpenReview: RenewalClosedReview[];
    needsConfirmation: RenewalExceptionTile;
    noticeDatePassed: RenewalExceptionTile;
    qiwaOverdue: RenewalExceptionTile;
    art55Threshold: RenewalExceptionTile;
    expiredNoOutcome: RenewalExceptionTile;
    expiredHoldoverPending: RenewalExceptionTile;
  };
  items: RenewalCaseItem[];
}

export interface Art55Meter {
  nationalityClass: NationalityClass | null;
  renewalsUsed: number | null;
  maxRenewals: number;
  yearsServed: number | null;
  yearsIfRenewed: number | null;
  maxYears: number;
  thresholdReached: boolean;
  reading: string;
}

export interface RenewalCase {
  summary: RenewalCaseItem;
  contractStartDate: string;
  contractSignedOn: string | null;
  art55: Art55Meter;
  nextStates: string[];
  qiwaRequired: boolean;
  openedAt: string;
  closedAt: string | null;
  version: string;
}

export interface ChainTerm {
  contractId: string;
  contractNumber: string;
  status: string;
  startDate: string;
  endDate: string | null;
  signedOn: string | null;
  renewalNumber: number | null;
  linkKind: 'Original' | 'Renewal' | 'Amendment' | 'Recorded' | 'Unconfirmed';
  linkedToContractId: string | null;
  gapReason: string | null;
  isCurrent: boolean;
}

export interface ContractChain {
  contractId: string;
  terms: ChainTerm[];
  renewalNumber: number | null;
  chainStartedOn: string | null;
  confirmed: boolean;
  nationalityClass: NationalityClass | null;
  autoRenew: boolean;
  nonRenewalNoticeDays: number | null;
  art55: Art55Meter;
  nextAllowedActions: RenewalAction[];
  blockReasons: BlockReason[];
  caseId: string | null;
  caseState: string | null;
  opensOn: string | null;
}

export interface ChainConfirmInput {
  renewedFromContractId: string | null;
  chainStartedOn: string;
  workerNationalityClass: NationalityClass;
  autoRenew: boolean;
  nonRenewalNoticeDays: number | null;
  renewalNumber: number;
}

export const renewalsApi = {
  radar: (params: { companyId?: string; days?: number } = {}) =>
    client.get<RenewalRadar>('/api/contracts/renewals/radar', { params }).then((r) => r.data),
  getCase: (caseId: string) => client.get<RenewalCase>(`/api/contracts/renewals/${caseId}`).then((r) => r.data),
  chain: (contractId: string) => client.get<ContractChain>(`/api/contracts/${contractId}/chain`).then((r) => r.data),
  confirmChain: (contractId: string, body: ChainConfirmInput) =>
    client.post<ContractChain>(`/api/contracts/${contractId}/chain/confirm`, body).then((r) => r.data),
  openNow: (companyId?: string) =>
    client.post<{ opened: number; alreadyOpen: number; notOpened: { contractId: string; reason: string }[] }>(
      '/api/contracts/renewals/open-now', { companyId: companyId ?? null }).then((r) => r.data),
  hold: (caseId: string, reason: HoldReason, note?: string) =>
    client.post<RenewalCase>(`/api/contracts/renewals/${caseId}/hold`, { reason, note: note ?? null }).then((r) => r.data),
  release: (caseId: string) => client.post<RenewalCase>(`/api/contracts/renewals/${caseId}/release`).then((r) => r.data),
  cancel: (caseId: string, reason: string) =>
    client.post<RenewalCase>(`/api/contracts/renewals/${caseId}/cancel`, { reason }).then((r) => r.data),
};
