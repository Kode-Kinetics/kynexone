import client from './client';

// ─────────────────────────────────────────────────────────────────────────────
// ERP API access — machine keys a tenant Admin issues so the client's ERP can pull GL journal
// export files and confirm or reject them. Managing keys needs the Admin role AND an enrolled
// two-step sign-in; the server answers 403 { error: 'mfa_required' | 'admin_required' } otherwise.
// The plaintext key is returned exactly once, by create; every other read carries only `prefix`.
// ─────────────────────────────────────────────────────────────────────────────

const BASE = '/api/finance/gl/api-keys';

export type ApiKeyScope = 'finance.gl.exports.read' | 'finance.gl.exports.confirm';
export const API_KEY_SCOPE_READ: ApiKeyScope = 'finance.gl.exports.read';
export const API_KEY_SCOPE_CONFIRM: ApiKeyScope = 'finance.gl.exports.confirm';

/** The longest life a key may be given, in days (the server enforces the same limit). */
export const API_KEY_MAX_DAYS = 365;

export type ApiKeyStatus = 'Active' | 'Expired' | 'Revoked';

export interface ApiKeySummary {
  id: string;
  name: string;
  /** Non-secret leading part of the key (e.g. 'kx_live_AbC12xYz'), safe to display. */
  prefix: string;
  /** null = every company in the group. */
  companyId: string | null;
  companyName: string | null;
  scopes: string[];
  expiresAtUtc: string;
  ipAllowList: string[];
  createdAtUtc: string;
  createdByName: string | null;
  lastUsedAtUtc: string | null;
  lastUsedIp: string | null;
  revokedAtUtc: string | null;
  revokedByName: string | null;
  status: ApiKeyStatus;
}

export interface CreateApiKeyRequest {
  name: string;
  companyId: string | null;
  scopes: ApiKeyScope[];
  /** ISO instant, in the future and no more than 365 days ahead. */
  expiresAtUtc: string;
  /** CIDR ranges or single addresses; empty allows any address. */
  ipAllowList: string[];
}

export interface CreateApiKeyResponse {
  key: ApiKeySummary;
  /** The full key. Shown once; never stored by the client. */
  plaintext: string;
}

/** The machine reason on a refusal: 'mfa_required', 'admin_required', 'invalid_request', … */
export function apiKeyRefusal(err: unknown): string | null {
  const data = (err as { response?: { data?: { error?: unknown } } })?.response?.data;
  return typeof data?.error === 'string' ? data.error : null;
}

export const glApiKeysApi = {
  list: () =>
    client.get<{ keys: ApiKeySummary[] }>(BASE).then((r) => (Array.isArray(r.data?.keys) ? r.data.keys : [])),

  create: (body: CreateApiKeyRequest) => client.post<CreateApiKeyResponse>(BASE, body).then((r) => r.data),

  revoke: (id: string) =>
    client.delete<{ key: ApiKeySummary }>(`${BASE}/${encodeURIComponent(id)}`).then((r) => r.data.key),

  /** The OpenAPI description of the ERP endpoints, as a file to hand to the ERP team. */
  openApi: async () => {
    const r = await client.get<Blob>(`${BASE}/openapi.json`, { responseType: 'blob' });
    return { blob: r.data, filename: 'kynexone-erp-api.json' };
  },
};
