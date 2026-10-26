import client from './client';
import type { EmployeeDetail } from './employees';
import type { HireCreateRequest } from '../lib/hire/payload';

export interface HireDraftSaved { updatedAtUtc: string; readiness?: { blockers?: string[]; warnings?: string[] } }
export interface HireReview { blockers: string[]; warnings: string[]; notProvided: string[]; essSections: string[] }
export interface ApiNationality { code: string; nameEn: string; nameAr: string; class?: string }

/** 40-ARCHITECTURE section 3.2 / 3.4. One Idempotency-Key per wizard session; the server replays instead of creating twice. */
export const employeeHireApi = {
  create: (body: HireCreateRequest, idempotencyKey: string) =>
    client.post<EmployeeDetail & { updatedAtUtc?: string }>('/api/employees', body, { headers: { 'Idempotency-Key': idempotencyKey } }).then(r => r.data),
  saveDraft: (id: number, baseUpdatedAtUtc: string, request: HireCreateRequest) =>
    client.put<HireDraftSaved>(`/api/employees/${id}/hire-draft`, { baseUpdatedAtUtc, request }).then(r => r.data),
  discardDraft: (id: number) => client.delete(`/api/employees/${id}/draft`).then(() => undefined),
  rehire: (id: number, body: { startDate: string; designationId?: string; gradeId?: string; reason: string }) =>
    client.post<EmployeeDetail & { updatedAtUtc?: string }>(`/api/employees/${id}/rehire`, body).then(r => r.data),
  review: (id: number) => client.get<HireReview>(`/api/employees/${id}/hire-review`).then(r => r.data),
  /** Null when the endpoint is not there yet: the picker then uses the built-in ISO list. */
  nationalities: () => client.get<ApiNationality[]>('/api/reference/nationalities').then(r => (Array.isArray(r.data) ? r.data : null)).catch(() => null),
};
