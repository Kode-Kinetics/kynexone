// ============================================================
// ZAYRA MOBILE — API Client
// ============================================================

import axios, {
  type AxiosInstance,
  type AxiosRequestConfig,
  type AxiosResponse,
  type InternalAxiosRequestConfig,
} from 'axios';
import { APP_CONFIG, API_TIMEOUT_MS } from '@/config';
import {
  clearAllAuthData,
  loadCredentialEnvelope,
  saveCredentialEnvelope,
} from '@/storage';
import {
  executeSessionRefresh,
  expireRefreshFailureIfOwned,
  StaleSessionRefreshError,
} from '@/auth/sessionRefresh';
import {
  captureSession,
  belongsToCurrentLogin,
  invalidateSessionIfCurrent,
  mayCommit,
  type SessionSnapshot,
} from '@/auth/sessionGeneration';
import { terminateSessionIfCurrent } from '@/auth/sessionLifecycle';
import { createIsolatedPublicAuthClient } from './publicAuthClient';

// We lazily import authStore to avoid circular deps
let _onSessionExpired: (() => void) | null = null;

export class SessionExpiredError extends Error {
  constructor() {
    super('Your session has expired. Please sign in again.');
    this.name = 'SessionExpiredError';
  }
}

export function isSessionExpiredError(error: unknown): error is SessionExpiredError {
  return error instanceof SessionExpiredError;
}

export function setSessionExpiredHandler(handler: () => void) {
  _onSessionExpired = handler;
}

interface RefreshedSession {
  accessToken: string;
  session: SessionSnapshot;
}

type SessionRequestConfig = InternalAxiosRequestConfig & {
  _retry?: boolean;
  _session?: SessionSnapshot;
};

const refreshPromises = new Map<number, Promise<RefreshedSession>>();

async function clearExpiredSession(): Promise<void> {
  try {
    await clearAllAuthData();
  } finally {
    _onSessionExpired?.();
  }
}

async function expireUnavailableCredentials(
  session: ReturnType<typeof captureSession>,
): Promise<never> {
  if (session.refreshToken === null) {
    if (invalidateSessionIfCurrent(session)) _onSessionExpired?.();
    throw new SessionExpiredError();
  }
  await terminateSessionIfCurrent(session, clearExpiredSession);
  throw new SessionExpiredError();
}

async function loadProtectedCredentialEnvelope(session: SessionSnapshot) {
  try {
    const envelope = await loadCredentialEnvelope();
    if (envelope) {
      if (!belongsToCurrentLogin(session) || !mayCommit(captureSession(), envelope.refreshToken)) throw new SessionExpiredError();
      return envelope;
    }
  } catch (error) {
    if (error instanceof SessionExpiredError) throw error;
    return expireUnavailableCredentials(session);
  }

  return expireUnavailableCredentials(session);
}

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
function getOrStartRefresh(session: SessionSnapshot): Promise<RefreshedSession> {
  if (!mayCommit(session, session.refreshToken)) return Promise.reject(new StaleSessionRefreshError());
  let promise = refreshPromises.get(session.generation);
  if (!promise) {
    promise = refreshAccessToken(session).finally(() => {
      refreshPromises.delete(session.generation);
    });
    refreshPromises.set(session.generation, promise);
  }
  return promise;
}

async function refreshAccessToken(session: SessionSnapshot): Promise<RefreshedSession> {
  const accessToken = await executeSessionRefresh({
    getRefreshToken: async () => (await loadCredentialEnvelope())?.refreshToken ?? null,
    requestRefresh: async (refreshToken) => {
      const response = await createPublicAuthClient().post<{
        accessToken: string;
        refreshToken: string;
        expiresAtUtc: string;
      }>(
        '/auth/refresh',
        { refreshToken },
        { timeout: 10_000 },
      );
      return {
        accessToken: response.data.accessToken,
        refreshToken: response.data.refreshToken,
        expiresAt: new Date(response.data.expiresAtUtc).getTime(),
      };
    },
    saveTokens: async (tokens) => {
      const current = await loadCredentialEnvelope();
      if (!current) throw new Error('No credential envelope to update.');
      await saveCredentialEnvelope({
        ...current,
        ...tokens,
        persistedAt: Date.now(),
      });
    },
  }, session);
  const refreshedSession = captureSession();
  // Rotations retain the login epoch; logout or login must never authorize
  // replay of the original request under another identity.
  if (!belongsToCurrentLogin(session)) {
    throw new StaleSessionRefreshError();
  }
  return { accessToken, session: refreshedSession };
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
    async (config: SessionRequestConfig) => {
      const session = config._session ?? captureSession();
      config._session = session;
      if (session.refreshToken !== null && !belongsToCurrentLogin(session)) throw new SessionExpiredError();
      const envelope = await loadProtectedCredentialEnvelope(session);
      const dispatchSession = captureSession();
      if (!belongsToCurrentLogin(session) || !mayCommit(dispatchSession, envelope.refreshToken)) throw new SessionExpiredError();
      config._session = dispatchSession;
      config.headers['Authorization'] = `Bearer ${envelope.accessToken}`;
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
      const originalRequest = error.config as SessionRequestConfig | undefined;
      if (__DEV__) {
        const method = originalRequest?.method?.toUpperCase() ?? 'GET';
        console.log(
          `[API] ERROR ${error.response?.status ?? 'NETWORK'} ${method} ${originalRequest?.url ?? ''}`,
          error.response?.data ?? error.message
        );
      }

      // Only attempt token refresh for requests that were already authenticated.
      // Public auth bootstrap requests (login/MFA/etc.) legitimately return 401
      // for bad credentials and must surface the server's error instead of
      // being replaced by a misleading "No refresh token" failure.
      const hadAuthorization = Boolean(originalRequest?.headers?.Authorization);
      if (error.response?.status === 401 && originalRequest && !originalRequest._retry && hadAuthorization) {
        if (!originalRequest._session || !belongsToCurrentLogin(originalRequest._session)) {
          return Promise.reject(new SessionExpiredError());
        }
        originalRequest._retry = true;
        try {
          // Another request may already have rotated this login's token. Replay
          // once with its successor rather than refreshing the retired token.
          const current = captureSession();
          if (current.generation !== originalRequest._session.generation) {
            originalRequest._session = current;
            return client(originalRequest);
          }
          const refreshed = await getOrStartRefresh(originalRequest._session);
          if (!belongsToCurrentLogin(refreshed.session)) throw new StaleSessionRefreshError();
          originalRequest._session = refreshed.session;
          originalRequest.headers['Authorization'] = `Bearer ${refreshed.accessToken}`;
          return client(originalRequest);
        } catch (refreshError) {
          await expireRefreshFailureIfOwned(refreshError, {
            expireSession: clearExpiredSession,
          });
          return Promise.reject(new SessionExpiredError());
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
