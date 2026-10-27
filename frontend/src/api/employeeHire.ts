import client from './client';
import type { EmployeeDetail } from './employees';
import type { HireCreateRequest } from '../lib/hire/payload';

export interface HireDraftSaved { updatedAtUtc: string; readiness?: { blockers?: string[]; warnings?: string[] } }
import type { SaBank } from '../lib/hire/iban';
import { normaliseReviewItems, type ReviewItem } from '../lib/hire/reviewLabels';

/** Items are { key, label } (English-only label); render them with reviewItemLabel so Arabic works. */
export interface HireReview { blockers: ReviewItem[]; warnings: ReviewItem[]; notProvided: ReviewItem[]; essSections: string[] }
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
  review: (id: number) => client.get<Record<string, unknown>>(`/api/employees/${id}/hire-review`).then((r): HireReview => ({
    blockers: normaliseReviewItems(r.data.blockers), warnings: normaliseReviewItems(r.data.warnings), notProvided: normaliseReviewItems(r.data.notProvided),
    essSections: Array.isArray(r.data.essSections) ? (r.data.essSections as string[]) : [],
  })),
  /** CD29: the SAMA bank list (code = IBAN characters 5 and 6). Null when it cannot be loaded: no bank name is shown, nothing else changes. */
  saBanks: () => client.get<SaBank[]>('/api/reference/sa-banks').then(r => (Array.isArray(r.data) ? r.data : null)).catch(() => null),
  /** CD26: the existing employee GET, used to resume a draft. The server scopes it; a 404 or 403 means the draft is not available to this user. */
  loadDraft: (id: number) => client.get<EmployeeDetail & { updatedAtUtc?: string }>(`/api/employees/${id}`).then(r => r.data),
  /** Null when the endpoint is not there yet: the picker then uses the built-in ISO list. */
  nationalities: () => client.get<ApiNationality[]>('/api/reference/nationalities').then(r => (Array.isArray(r.data) ? r.data : null)).catch(() => null),
};
