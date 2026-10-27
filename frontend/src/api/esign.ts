import client from './client';

/**
 * P4 — click-to-sign ACKNOWLEDGEMENT of a document. Not a qualified electronic signature (the screens say so).
 * The employee reads the stored PDF, types their name and re-enters their password; the server records the evidence.
 */

export type SignatureStatus = 'Awaiting' | 'Signed' | 'Superseded';

export interface SignatureRow {
  documentId: string;
  title: string;
  fileName: string;
  status: SignatureStatus;
  requestedAtUtc: string | null;
  ackId?: string | null;
  signedAtUtc?: string | null;
  sha256Prefix?: string | null;
  method?: 'esign_ess' | 'esign_hr_device' | null;
  typedName?: string | null;
  verificationCode?: string | null;
  certificateDocumentId?: string | null;
  integrityFlagged?: boolean;
}

export interface ConsentView {
  consentVersion: string;
  text: string;
  textEn: string;
  textAr: string;
  textSha256: string;
  documentSha256: string;
  documentVersion: number;
  title: string;
  fileName: string;
  integrityFlagged: boolean;
}

export interface SignBody { documentSha256: string; consentVersion: string; typedName: string; language: 'en' | 'ar'; deviceId: string }
export interface SignReceipt { ackId: string; signedAtUtc: string; recordHash: string; certificateDocumentId: string; verificationCode: string }
export interface VerifyResult { valid: boolean; documentIntact: boolean; chainIntact: boolean; recordHash: string; checkedAtUtc: string }

/** The server's problem+json for this flow (`esign.error.<code>` is its `titleKey`). */
export interface EsignProblem { code?: string; titleKey?: string; status?: number; ackId?: string; detail?: string }
export function problemOf(error: unknown): EsignProblem {
  const data = (error as { response?: { data?: unknown } } | null)?.response?.data;
  return data && typeof data === 'object' ? (data as EsignProblem) : {};
}

/** A wrong password or a step-up challenge is answered 401 on purpose; it must not enter the session-refresh path. */
const noRefresh = { _retry: true } as Record<string, unknown>;
const stepUpHeaders = (token: string, idempotencyKey: string) => ({ 'X-Step-Up': token, 'Idempotency-Key': idempotencyKey });

/** The browser's stable label for this device (shown to nobody; recorded as evidence). */
export function deviceId(): string {
  try {
    const key = 'kynexone-device-id';
    let id = window.localStorage.getItem(key);
    if (!id) { id = (globalThis.crypto?.randomUUID?.() ?? `d-${Date.now().toString(36)}`).slice(0, 36); window.localStorage.setItem(key, id); }
    return id;
  } catch { return 'unknown'; }
}

/** SHA-256 (hex) of the bytes the signer was shown; falls back to the server's own figure where the browser has no SubtleCrypto. */
export async function sha256Hex(blob: Blob, fallback: string): Promise<string> {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) return fallback;
  const digest = await subtle.digest('SHA-256', await blob.arrayBuffer());
  return Array.from(new Uint8Array(digest)).map(b => b.toString(16).padStart(2, '0')).join('');
}

const asPdfBlob = (data: Blob) => new Blob([data], { type: 'application/pdf' });

export const esignApi = {
  list: () => client.get<SignatureRow[]>('/api/ess/signatures').then(r => r.data),
  consent: (documentId: string, lang: 'en' | 'ar') => client.get<ConsentView>(`/api/ess/signatures/${documentId}/consent`, { params: { lang } }).then(r => r.data),
  document: (documentId: string) => client.get<Blob>(`/api/ess/signatures/${documentId}/document`, { responseType: 'blob' }).then(r => asPdfBlob(r.data)),
  certificate: (ackId: string) => client.get<Blob>(`/api/ess/signatures/${ackId}/certificate`, { responseType: 'blob' }).then(r => asPdfBlob(r.data)),
  stepUp: (password: string, mfaCode?: string) =>
    client.post<{ stepUpToken: string; expiresAtUtc: string }>('/api/auth/step-up', { purpose: 'esign', password, mfaCode: mfaCode || undefined }, noRefresh).then(r => r.data),
  sign: (documentId: string, body: SignBody, stepUp: string, idempotencyKey: string) =>
    client.post<SignReceipt>(`/api/ess/signatures/${documentId}/sign`, body, { ...noRefresh, headers: stepUpHeaders(stepUp, idempotencyKey) }).then(r => r.data),
  verify: (ackId: string) => client.get<VerifyResult>(`/api/ess/signatures/${ackId}/verify`).then(r => r.data),
};

export const hrEsignApi = {
  list: (employeeId: number) => client.get<SignatureRow[]>(`/api/employees/${employeeId}/signatures`).then(r => r.data),
  consent: (employeeId: number, documentId: string, lang: 'en' | 'ar') => client.get<ConsentView>(`/api/employees/${employeeId}/signatures/${documentId}/consent`, { params: { lang } }).then(r => r.data),
  request: (employeeId: number, documentId: string) => client.post<{ documentId: string; requestedAtUtc: string }>(`/api/employees/${employeeId}/signature-requests`, { documentId }).then(r => r.data),
  verify: (employeeId: number, ackId: string) => client.get<VerifyResult>(`/api/employees/${employeeId}/signatures/${ackId}/verify`).then(r => r.data),
  /** The stored file through the same authenticated route every employee document uses. */
  download: (documentId: string) => client.get<Blob>(`/api/employees/documents/${documentId}/download`, { responseType: 'blob' }).then(r => asPdfBlob(r.data)),
  /** The EMPLOYEE's own password, typed on HR's device. HR's password is refused. */
  deviceStepUp: (employeeId: number, password: string, mfaCode?: string) =>
    client.post<{ stepUpToken: string; expiresAtUtc: string }>('/api/auth/step-up/employee-device', { employeeId, password, mfaCode: mfaCode || undefined }, noRefresh).then(r => r.data),
  signOnDevice: (employeeId: number, documentId: string, body: SignBody, stepUp: string, idempotencyKey: string) =>
    client.post<SignReceipt>(`/api/employees/${employeeId}/signatures/${documentId}/sign-on-device`, body, { ...noRefresh, headers: stepUpHeaders(stepUp, idempotencyKey) }).then(r => r.data),
};

/** Opens a PDF the browser already holds (an authenticated fetch), never a URL the server serves unauthenticated. */
export function openBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = fileName; a.rel = 'noopener';
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
