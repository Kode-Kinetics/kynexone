// ============================================================
// ZAYRA MOBILE — API Client
// ============================================================

import axios, {
  AxiosInstance,
  AxiosRequestConfig,
  AxiosResponse,
  InternalAxiosRequestConfig,
} from 'axios';
import { APP_CONFIG, API_TIMEOUT_MS } from '@/config';
import { tokenStorage, clearAllAuthData } from '@/storage';
import { createIsolatedPublicAuthClient } from './publicAuthClient';

// We lazily import authStore to avoid circular deps
let _onSessionExpired: (() => void) | null = null;

export function setSessionExpiredHandler(handler: () => void) {
  _onSessionExpired = handler;
}

let refreshPromise: Promise<string> | null = null;

/**
 * Anonymous auth traffic must never inherit an old mobile session. This client
 * deliberately has no token-storage import path, refresh interceptor, auth
 * clearing, session-expired callback, or request replay.
 */
export function createPublicAuthClient(): AxiosInstance {
  const client = createIsolatedPublicAuthClient({
    baseURL: APP_CONFIG.API_BASE_URL,
    timeout: API_TIMEOUT_MS,
    tenantHeader: APP_CONFIG.TENANT_HEADER,
  });

  client.interceptors.response.use(
    (response) => response,
    (error) => {
      applyServerMessage(error);
      return Promise.reject(error);
    },
  );
  return client;
}

function applyServerMessage(error: any): void {
  const body = error.response?.data;
  if (!body || typeof body !== 'object') return;
  const validation = body.errors && typeof body.errors === 'object'
    ? (Object.values(body.errors).flat()[0] as string | undefined)
    : undefined;
  const serverMessage = (typeof body.message === 'string' && body.message) || validation;
  if (serverMessage) error.message = serverMessage;
}

/**
 * All requests that encounter the same expired access token await one shared
 * refresh operation. A failed refresh rejects every waiter; no request is left
 * hanging in a subscriber queue.
 */
function getOrStartRefresh(): Promise<string> {
  if (!refreshPromise) {
    refreshPromise = refreshAccessToken().finally(() => {
      refreshPromise = null;
    });
  }
  return refreshPromise;
}

async function refreshAccessToken(): Promise<string> {
  const refreshToken = await tokenStorage.getRefreshToken();
  if (!refreshToken) throw new Error('No refresh token');

  const response = await createPublicAuthClient().post<{
    accessToken: string;
    refreshToken: string;
    expiresAtUtc: string;
  }>(
    '/auth/refresh',
    { refreshToken },
    { timeout: 10_000 },
  );

  const { accessToken, refreshToken: newRefreshToken, expiresAtUtc } = response.data;
  const expiresAt = new Date(expiresAtUtc).getTime();
  await tokenStorage.saveTokens({ accessToken, refreshToken: newRefreshToken, expiresAt });
  return accessToken;
}

export function createApiClient(tenantId: string): AxiosInstance {
  const client = axios.create({
    baseURL: APP_CONFIG.API_BASE_URL,
    timeout: API_TIMEOUT_MS,
    headers: {
      'Content-Type': 'application/json',
      [APP_CONFIG.TENANT_HEADER]: tenantId,
    },
  });

  // Request interceptor: attach Bearer token
  client.interceptors.request.use(
    async (config: InternalAxiosRequestConfig) => {
      const token = await tokenStorage.getAccessToken();
      if (token) {
        config.headers['Authorization'] = `Bearer ${token}`;
      }
      if (__DEV__) {
        const method = config.method?.toUpperCase() ?? 'GET';
        console.log(`[API] ${method} ${APP_CONFIG.API_BASE_URL}${config.url ?? ''}`);
      }
      return config;
    },
    (error) => Promise.reject(error)
  );

  // Response interceptor: handle 401 → refresh
  client.interceptors.response.use(
    (response: AxiosResponse) => {
      if (__DEV__) {
        const method = response.config.method?.toUpperCase() ?? 'GET';
        console.log(`[API] ${response.status} ${method} ${response.config.url ?? ''}`);
      }
      return response;
    },
    async (error) => {
      const originalRequest = error.config as InternalAxiosRequestConfig & {
        _retry?: boolean;
      };
      if (__DEV__) {
        const method = originalRequest?.method?.toUpperCase() ?? 'GET';
        console.log(
          `[API] ERROR ${error.response?.status ?? 'NETWORK'} ${method} ${originalRequest?.url ?? ''}`,
          error.response?.data ?? error.message
        );
      }

      if (error.response?.status === 401 && !originalRequest._retry) {
        originalRequest._retry = true;
        try {
          const newToken = await getOrStartRefresh();
          originalRequest.headers['Authorization'] = `Bearer ${newToken}`;
          return client(originalRequest);
        } catch (refreshError) {
          await clearAllAuthData();
          _onSessionExpired?.();
          return Promise.reject(refreshError);
        }
      }

      // Surface the server's own message (or the first ASP.NET validation error)
      // so screens that show error.message say something a user can act on,
      // instead of "Request failed with status code 400".
      applyServerMessage(error);

      // 403: permission denied
      if (error.response?.status === 403) {
        error.message = 'You do not have permission to perform this action.';
      }

      // 423: account locked
      if (error.response?.status === 423) {
        error.message = 'Your account is locked. Please contact HR.';
      }

      return Promise.reject(error);
    }
  );

  return client;
}

// Global API client (initialized after login)
let _apiClient: AxiosInstance | null = null;

export function initApiClient(tenantId: string): AxiosInstance {
  _apiClient = createApiClient(tenantId);
  return _apiClient;
}

export function getApiClient(): AxiosInstance {
  if (!_apiClient) {
    throw new Error('API client not initialized. Call initApiClient first.');
  }
  return _apiClient;
}

// Convenience wrappers
export function unwrapApiData<T>(payload: unknown): T {
  if (
    payload &&
    typeof payload === 'object' &&
    'data' in payload &&
    Object.prototype.hasOwnProperty.call(payload, 'data')
  ) {
    return (payload as { data: T }).data;
  }
  return payload as T;
}

export async function apiGet<T>(url: string, config?: AxiosRequestConfig): Promise<T> {
  const response = await getApiClient().get(url, config);
  return unwrapApiData<T>(response.data);
}

export async function apiPost<T>(
  url: string,
  data?: unknown,
  config?: AxiosRequestConfig
): Promise<T> {
  const response = await getApiClient().post(url, data, config);
  return unwrapApiData<T>(response.data);
}

export async function apiPatch<T>(
  url: string,
  data?: unknown,
  config?: AxiosRequestConfig
): Promise<T> {
  const response = await getApiClient().patch(url, data, config);
  return unwrapApiData<T>(response.data);
}

export async function apiPut<T>(
  url: string,
  data?: unknown,
  config?: AxiosRequestConfig
): Promise<T> {
  const response = await getApiClient().put(url, data, config);
  return unwrapApiData<T>(response.data);
}

export async function apiDelete<T>(url: string, config?: AxiosRequestConfig): Promise<T> {
  const response = await getApiClient().delete(url, config);
  return unwrapApiData<T>(response.data);
}

export function extractErrorMessage(error: unknown): string {
  if (axios.isAxiosError(error)) {
    const serverMsg = error.response?.data?.message || error.message;
    return serverMsg ?? 'An unexpected error occurred.';
  }
  if (error instanceof Error) return error.message;
  return 'An unexpected error occurred.';
}
