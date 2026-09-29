import client from './client';

export interface Offboarding {
  id: string;
  employeeId: number;
  employeeName: string;
  employeeCode: string;
  department: string;
  designation: string;
  separationType: string;
  reason: string;
  noticeDate: string;
  noticePeriodDays: number;
  lastWorkingDay: string;
  rehireEligible: boolean;
  status: string; // InProgress | Completed | Cancelled
  exitInterviewStatus: string; // Pending | Scheduled | Completed | Waived
  exitInterviewDate: string | null;
  exitReasonCategory: string;
  exitInterviewRating: number;
  exitInterviewNotes: string;
  assetsReturned: boolean;
  accessRevoked: boolean;
  accessRevokedAtUtc: string | null;
  knowledgeHandover: boolean;
  finalSettlementDone: boolean;
  backfillRequisitionId: string | null;
  completedAtUtc: string | null;
  cancelledAtUtc: string | null;
  cancelReason: string | null;
}

/**
 * S2-B3 — the separation vocabulary, served by the API so the screen and the domain cannot drift.
 * The screen used to hard-code its own list, which could not offer Article 80 (the one type that
 * forfeits the end-of-service award) and offered two values the domain rejects.
 */
export interface SeparationTypeInfo {
  code: string;
  label: string;
  description: string;
  forfeitsEndOfServiceAward: boolean;
  requiresReason: boolean;
}

/** Local mirror of the served catalogue — used only if the fetch fails, so the modal still works. */
export const SEPARATION_TYPE_FALLBACK: SeparationTypeInfo[] = [
  { code: 'Resignation', label: 'Resignation', description: 'The employee resigned. KSA Art. 85 reduces the end-of-service award by length of service.', forfeitsEndOfServiceAward: false, requiresReason: false },
  { code: 'Termination', label: 'Termination by employer', description: 'Employer-initiated termination with notice. Full Art. 84 end-of-service award.', forfeitsEndOfServiceAward: false, requiresReason: false },
  { code: 'EndOfContract', label: 'End of fixed-term contract', description: 'A fixed-term contract expired and was not renewed. Full Art. 84 award.', forfeitsEndOfServiceAward: false, requiresReason: false },
  { code: 'Redundancy', label: 'Redundancy', description: 'The role was eliminated. Full Art. 84 award.', forfeitsEndOfServiceAward: false, requiresReason: false },
  { code: 'Retirement', label: 'Retirement', description: 'The employee reached retirement. Full Art. 84 award.', forfeitsEndOfServiceAward: false, requiresReason: false },
  { code: 'ProbationFailure', label: 'Probation not passed', description: 'Separation during or at the end of probation.', forfeitsEndOfServiceAward: false, requiresReason: false },
  { code: 'Death', label: 'Death in service', description: 'The employee died in service. The award is payable to the estate / legal heirs.', forfeitsEndOfServiceAward: false, requiresReason: false },
  { code: 'Article80', label: 'Article 80 — summary dismissal for cause', description: 'Dismissal for one of the grounds in KSA Labour Law Art. 80 (1)–(9). This FORFEITS the end-of-service award in full.', forfeitsEndOfServiceAward: true, requiresReason: true },
];

export interface OffboardingSummary {
  inNotice: number;
  completed: number;
  exitInterviewsPending: number;
  avgExitRating: number;
  reasons: { category: string; count: number }[];
}

export const offboardingApi = {
  list: (status?: string) =>
    client.get<Offboarding[]>('/api/offboarding', { params: { status } }).then(r => r.data),
  summary: () => client.get<OffboardingSummary>('/api/offboarding/summary').then(r => r.data),
  separationTypes: () =>
    client.get<SeparationTypeInfo[]>('/api/offboarding/separation-types').then(r => r.data),
  initiate: (body: {
    employeeId: number; separationType: string; reason?: string;
    noticeDate?: string; noticePeriodDays: number; lastWorkingDay?: string;
    rehireEligible?: boolean; raiseBackfill?: boolean;
  }) => client.post<{ offboarding: Offboarding; noticeShortfallDays: number; forfeitsEndOfServiceAward: boolean }>(
    '/api/offboarding/initiate', body).then(r => r.data),
  exitInterview: (id: string, body: { status?: string; date?: string; reasonCategory?: string; rating: number; notes?: string }) =>
    client.patch<Offboarding>(`/api/offboarding/${id}/exit-interview`, body).then(r => r.data),
  checklist: (id: string, body: Partial<{ assetsReturned: boolean; accessRevoked: boolean; knowledgeHandover: boolean; finalSettlementDone: boolean }>) =>
    client.patch<Offboarding>(`/api/offboarding/${id}/checklist`, body).then(r => r.data),
  /** S2-B2 — revokes the leaver's login for real, on their last working day, without waiting for the settlement. */
  revokeAccess: (id: string) =>
    client.post<{ id: string; accessRevoked: boolean; accessRevokedAtUtc: string | null; alreadyRevoked: boolean }>(
      `/api/offboarding/${id}/revoke-access`).then(r => r.data),
  /** S2-B2 — a settlement paid by bank transfer / cheque / cash. Posts the discharge journal. */
  recordExternalSettlementPayment: (id: string, body: { method: string; reference: string; amount: number; paidOn?: string }) =>
    client.post(`/api/offboarding/${id}/settlement/external-payment`, body).then(r => r.data),
  complete: (id: string) => client.post<Offboarding>(`/api/offboarding/${id}/complete`).then(r => r.data),
  cancel: (id: string, reason?: string) =>
    client.post<{ offboarding: Offboarding; accessRestored: boolean; backfillWithdrawn: boolean }>(
      `/api/offboarding/${id}/cancel`, { reason }).then(r => r.data),
};
