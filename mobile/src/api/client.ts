// ============================================================
// ZAYRA MOBILE — API Client
// ============================================================

import axios, {
  AxiosInstance,
  AxiosRequestConfig,
  AxiosResponse,
  InternalAxiosRequestConfig,
} from 'axios';
import { APP_CONFIG, APP_VERSION, APP_VERSION_HEADER, API_TIMEOUT_MS } from '@/config';
import { tokenStorage, clearAllAuthData } from '@/storage';
import i18n from '@/config/i18n';
import { createIsolatedPublicAuthClient } from './publicAuthClient';
import { ResponseShapeError, validateRefreshResponse, validateResponse } from './schemas';
import { useCompatibilityStore } from '@/safety/compatibilityStore';
import { reportError } from '@/observability/reportError';
import {
  NoRefreshTokenError,
  isDefinitiveAuthRefusal,
  localizedErrorKey,
  retryTransient,
  shouldRetryGet,
  backoffDelayMs,
} from './sessionErrors';

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
    extraHeaders: { [APP_VERSION_HEADER]: APP_VERSION },
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

function guardRefresh(data: unknown) {
  try {
    return validateRefreshResponse(data);
  } catch (error) {
    noteShapeMismatch(error, true);
    throw error;
  }
}

/**
 * A reply that no longer matches this app version: say so plainly (never a crash or NaN), tell the
 * UI to offer the update, and report the route (never the values) to crash reporting.
 */
function noteShapeMismatch(error: unknown, blocking = false): void {
  if (!(error instanceof ResponseShapeError)) return;
  error.message = i18n.t('safety.update.degraded');
  useCompatibilityStore.getState().flag(blocking);
  reportError(error, { source: 'api.schema', tags: { route: error.route, issues: error.issues.join(',') } });
}

function guard<T>(method: string, url: string, data: T): T {
  try {
    return validateResponse(method, url, data);
  } catch (error) {
    noteShapeMismatch(error, url.split('?')[0] === '/auth/me');
    throw error;
  }
}

/** Words the errors the app itself owns (permission, lock, connectivity) in the user's language. */
function localizeError(error: any): void {
  const key = localizedErrorKey(error);
  if (key) error.message = i18n.t(key);
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
  if (!refreshToken) throw new NoRefreshTokenError();

  // A weak signal is not a sign-out: transient failures are retried with backoff
  // here, and the caller keeps the session if they still fail.
  const response = await retryTransient(
    () =>
      createPublicAuthClient().post<{
        accessToken: string;
        refreshToken: string;
        expiresAtUtc: string;
      }>('/auth/refresh', { refreshToken }, { timeout: 10_000 }),
    { retries: 2, sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)), jitter: Math.random },
  );

  // A refresh reply without real tokens must not be saved over the working ones.
  const { accessToken, refreshToken: newRefreshToken, expiresAtUtc } = guardRefresh(response.data);
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
      [APP_VERSION_HEADER]: APP_VERSION,
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
        _getRetries?: number;
      };
      if (__DEV__) {
        const method = originalRequest?.method?.toUpperCase() ?? 'GET';
        console.log(
          `[API] ERROR ${error.response?.status ?? 'NETWORK'} ${method} ${originalRequest?.url ?? ''}`,
          error.response?.data ?? error.message
        );
      }

      // Reads on a weak connection are replayed (twice, with backoff) before the screen sees an error.
      const attempt = originalRequest?._getRetries ?? 0;
      if (originalRequest && shouldRetryGet(originalRequest.method, error, attempt)) {
        originalRequest._getRetries = attempt + 1;
        await new Promise((resolve) => setTimeout(resolve, backoffDelayMs(attempt, Math.random(), 800, 4000)));
        return client(originalRequest);
      }

      // Only attempt token refresh for requests that were already authenticated.
      // Public auth bootstrap requests (login/MFA/etc.) legitimately return 401
      // for bad credentials and must surface the server's error instead of
      // being replaced by a misleading "No refresh token" failure.
      const hadAuthorization = Boolean(originalRequest?.headers?.Authorization);
      if (error.response?.status === 401 && !originalRequest._retry && hadAuthorization) {
        originalRequest._retry = true;
        try {
          const newToken = await getOrStartRefresh();
          originalRequest.headers['Authorization'] = `Bearer ${newToken}`;
          return client(originalRequest);
        } catch (refreshError) {
          // Only a definitive refusal (401/403/423, revoked grant, no token) ends the
          // session. Timeouts, offline and 5xx keep the tokens so the next request
          // or the retry screen can recover without a fresh password + 2FA.
          if (isDefinitiveAuthRefusal(refreshError)) {
            await clearAllAuthData();
            _onSessionExpired?.();
          } else {
            localizeError(refreshError);
          }
          return Promise.reject(refreshError);
        }
      }

      // Surface the server's own message (or the first ASP.NET validation error)
      // so screens that show error.message say something a user can act on,
      // instead of "Request failed with status code 400".
      applyServerMessage(error);

      if (error.response?.status === 426) useCompatibilityStore.getState().flag(false);
      localizeError(error);

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
  return guard('GET', url, unwrapApiData<T>(response.data));
}

export async function apiPost<T>(
  url: string,
  data?: unknown,
  config?: AxiosRequestConfig
): Promise<T> {
  const response = await getApiClient().post(url, data, config);
  return guard('POST', url, unwrapApiData<T>(response.data));
}

export async function apiPatch<T>(
  url: string,
  data?: unknown,
  config?: AxiosRequestConfig
): Promise<T> {
  const response = await getApiClient().patch(url, data, config);
  return guard('PATCH', url, unwrapApiData<T>(response.data));
}

export async function apiPut<T>(
  url: string,
  data?: unknown,
  config?: AxiosRequestConfig
): Promise<T> {
  const response = await getApiClient().put(url, data, config);
  return guard('PUT', url, unwrapApiData<T>(response.data));
}

export async function apiDelete<T>(url: string, config?: AxiosRequestConfig): Promise<T> {
  const response = await getApiClient().delete(url, config);
  return guard('DELETE', url, unwrapApiData<T>(response.data));
}

export function extractErrorMessage(error: unknown): string {
  if (axios.isAxiosError(error)) {
    const serverMsg = error.response?.data?.message || error.message;
    return serverMsg ?? 'An unexpected error occurred.';
  }
  if (error instanceof Error) return error.message;
  return 'An unexpected error occurred.';
}
