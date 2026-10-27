// ============================================================
// HR letters — /api/ess/document-requests (lane C)
// ============================================================
//
// The employee asks; HR issues on company letterhead. Shapes are EssLetterTypeDto and
// EssDocumentRequestResponse in EmployeeSelfServiceController.cs.

import * as FileSystem from 'expo-file-system/legacy';
import { tokenStorage } from '@/storage';
import { apiGet, apiPost, getApiClient } from './client';
import {
  letterFileName,
  letterRequestBody,
  type LetterDraft,
  type LetterRequest,
  type LetterType,
} from '@/features/letters/letterRequest';

function text(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function mapRequest(raw: any): LetterRequest {
  return {
    id: String(raw?.id ?? ''),
    letterType: text(raw?.letterType),
    language: text(raw?.language),
    purpose: text(raw?.purpose),
    addresseeName: text(raw?.addresseeName),
    status: text(raw?.status),
    createdAtUtc: text(raw?.createdAtUtc),
    decidedAtUtc: raw?.decidedAtUtc ? String(raw.decidedAtUtc) : null,
    decisionNote: text(raw?.decisionNote),
    referenceNumber: raw?.referenceNumber ? String(raw.referenceNumber) : null,
    isIssued: Boolean(raw?.isIssued),
  };
}

/** The bearer token, as the payslip download sends it (downloadAsync bypasses axios). */
async function downloadHeaders(): Promise<Record<string, string>> {
  const headers: Record<string, string> = { Accept: 'application/pdf' };
  const token = await tokenStorage.getAccessToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  return headers;
}

export const lettersApi = {
  /** Only the letter types this company has a template for. */
  async getTypes(): Promise<LetterType[]> {
    const rows = await apiGet<any[]>('/ess/document-requests/types');
    return (rows ?? []).map((r) => ({
      letterType: text(r?.letterType),
      nameEn: text(r?.nameEn) || text(r?.letterType),
      nameAr: text(r?.nameAr),
    }));
  },

  async getRequests(): Promise<LetterRequest[]> {
    const rows = await apiGet<any[]>('/ess/document-requests');
    return (rows ?? []).map(mapRequest);
  },

  async create(draft: LetterDraft): Promise<LetterRequest> {
    return mapRequest(await apiPost<any>('/ess/document-requests', letterRequestBody(draft)));
  },

  /**
   * Downloads an issued letter into the cache directory and returns its local uri. The caller shares
   * it and then deletes it (deleteLocalCopy) — the letter is never kept on the device.
   * Throws { status } on a non-200 answer so the screen can map it.
   */
  async downloadPdf(request: LetterRequest): Promise<string> {
    const root = FileSystem.cacheDirectory;
    if (!root) throw Object.assign(new Error('No cache directory'), { status: undefined });
    const target = `${root.endsWith('/') ? root : `${root}/`}${letterFileName(request)}`;
    const headers = await downloadHeaders();
    const result = await FileSystem.downloadAsync(
      `${getApiClient().defaults.baseURL}/ess/document-requests/${encodeURIComponent(request.id)}/pdf`,
      target,
      { headers },
    );
    if (result.status !== 200) {
      await lettersApi.deleteLocalCopy(result.uri);
      throw Object.assign(new Error(`HTTP ${result.status}`), { status: result.status });
    }
    return result.uri;
  },

  async deleteLocalCopy(uri: string | null | undefined): Promise<void> {
    if (!uri) return;
    try {
      await FileSystem.deleteAsync(uri, { idempotent: true });
    } catch (error) {
      console.warn('[Letters] Could not delete the cached letter:', error);
    }
  },
};
