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
  knowledgeHandover: boolean;
  finalSettlementDone: boolean;
  backfillRequisitionId: string | null;
  completedAtUtc: string | null;
}

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
  initiate: (body: {
    employeeId: number; separationType: string; reason?: string;
    noticeDate?: string; noticePeriodDays: number; lastWorkingDay?: string;
    rehireEligible?: boolean; raiseBackfill?: boolean;
  }) => client.post<Offboarding>('/api/offboarding/initiate', body).then(r => r.data),
  exitInterview: (id: string, body: { status?: string; date?: string; reasonCategory?: string; rating: number; notes?: string }) =>
    client.patch<Offboarding>(`/api/offboarding/${id}/exit-interview`, body).then(r => r.data),
  checklist: (id: string, body: Partial<{ assetsReturned: boolean; accessRevoked: boolean; knowledgeHandover: boolean; finalSettlementDone: boolean }>) =>
    client.patch<Offboarding>(`/api/offboarding/${id}/checklist`, body).then(r => r.data),
  complete: (id: string) => client.post<Offboarding>(`/api/offboarding/${id}/complete`).then(r => r.data),
  cancel: (id: string) => client.post<Offboarding>(`/api/offboarding/${id}/cancel`).then(r => r.data),
};
