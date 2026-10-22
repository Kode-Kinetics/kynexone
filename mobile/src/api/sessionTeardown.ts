import axios from 'axios';
import { APP_CONFIG } from '../config';

interface TeardownCredentials {
  accessToken: string;
  refreshToken: string;
  tenantId: string;
}

/** Only revocation endpoints are exposed; no storage, refresh, or replay path. */
export function createSessionTeardownTransport(credentials: TeardownCredentials) {
  const { accessToken, refreshToken, tenantId } = credentials;
  const client = axios.create({
    baseURL: APP_CONFIG.API_BASE_URL,
    timeout: 5_000,
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
      [APP_CONFIG.TENANT_HEADER]: tenantId,
    },
  });
  return {
    revoke: () => client.post('/auth/logout', { refreshToken }),
    unregister: (deviceId: string) => client.delete(`/mobile/register-device/${encodeURIComponent(deviceId)}`),
  };
}
