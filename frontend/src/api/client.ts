import axios from 'axios';
import { RefreshQueue } from './refreshQueue';
import { isRefreshRefused } from '../lib/authLoadState';
import { clearSessionKeepingLocale } from './clearSession';

// In the browser, use relative URLs so Next.js proxy handles CORS.
// On the server (SSR), we need the absolute URL since there's no proxy.
function resolveBaseUrl(): string {
  if (typeof window !== 'undefined') return '';
  const raw = process.env.NEXT_PUBLIC_API_BASE_URL ?? process.env.NEXT_PUBLIC_API_URL;
  if (!raw) return 'http://localhost:5117';
  if (raw.startsWith('http://') || raw.startsWith('https://')) return raw;
  return `https://${raw}`;
}
export const BASE_URL = resolveBaseUrl();

const client = axios.create({ baseURL: BASE_URL });

// Anonymous authentication traffic has its own deliberately minimal client.
// It never reads localStorage, attaches an old bearer, refreshes a session,
// clears auth state, or redirects on an expected 4xx response.
export const publicAuthClient = axios.create({ baseURL: BASE_URL });
publicAuthClient.interceptors.request.use((config) => {
  // AxiosHeaders.delete is case-insensitive; direct JS property deletion is
  // not and would let AUTHORIZATION/AuThOrIzAtIoN defaults survive.
  config.headers.delete('Authorization');
  config.headers.delete('X-Company-Id');
  config.headers.delete('X-Tenant-Id');
  return config;
});

// Company-switcher selection, set by CurrentCompanyProvider. Travels as the
// X-Company-Id header: the backend intersects it with the token scope, so it can only
// NARROW access — an inaccessible value yields empty data server-side (fail closed).
let activeCompanyId: string | null = null;
export function setActiveCompanyId(companyId: string | null) {
  activeCompanyId = companyId;
}

client.interceptors.request.use((config) => {
  const token = localStorage.getItem('zayra_access_token');
  if (token) config.headers.Authorization = `Bearer ${token}`;
  if (activeCompanyId) config.headers['X-Company-Id'] = activeCompanyId;
  return config;
});

let isRefreshing = false;
const pendingRefreshes = new RefreshQueue();

// Backend restarts (Render redeploys behind the Vercel /api/* rewrite) surface as a brief
// window of 502/503/504 or a dropped connection. GET requests are safe to replay — they have
// no side effect — so retry them ONCE, after a short delay, to ride out that window instead of
// bubbling a spurious error to the UI. A request-scoped flag (_getRetried) caps this at exactly
// one attempt per request, so a persistently-down backend fails fast instead of looping.
// Never applies to POST/PUT/PATCH/DELETE: those include single-use calls (login, password
// reset, accept-invitation) and other mutations where a blind replay could duplicate a write.
const RETRYABLE_GATEWAY_STATUSES = new Set([502, 503, 504]);
const GET_RETRY_DELAY_MS = 1500;

function isRetryableGetFailure(err: { code?: string; config?: any; response?: { status?: number } }): boolean {
  const original = err.config;
  if (!original || original._getRetried) return false;
  // A caller-aborted request (unmount, query cancellation) also has no response; replaying it
  // would resurrect work the caller deliberately discarded.
  if (err.code === 'ERR_CANCELED') return false;
  const method = (original.method ?? 'get').toLowerCase();
  if (method !== 'get') return false;
  if (!err.response) return true; // network error / timeout — no response at all
  return RETRYABLE_GATEWAY_STATUSES.has(err.response.status ?? 0);
}

client.interceptors.response.use(
  (res) => res,
  async (err) => {
    const original = err.config;

    if (isRetryableGetFailure(err)) {
      original._getRetried = true;
      await new Promise((resolve) => setTimeout(resolve, GET_RETRY_DELAY_MS));
      return client(original);
    }

    // The tenant API client must never drive navigation or auth side-effects on
    // the platform console — it has its own axios (platform.ts) and auth flow.
    // Without this, the globally-mounted tenant providers' calls hijacked the
    // platform area: a 402 redirected /platform/login → /tenant-admin, and a 401
    // would clear localStorage (wiping the platform token) and bounce to /login.
    if (typeof window !== 'undefined' && window.location.pathname.startsWith('/platform')) {
      return Promise.reject(err);
    }

    // 402 — subscription expired/inactive. Redirect tenant users to the tenant
    // admin subscription alert, but NEVER from /tenant-admin itself: when the
    // subscription is inactive the tenant-admin page's own calls also 402, so a
    // self-redirect would loop infinitely. (Platform routes are already excluded
    // by the guard above.)
    if (err.response?.status === 402 && typeof window !== 'undefined') {
      if (!window.location.pathname.startsWith('/tenant-admin')) {
        window.location.href = '/tenant-admin?alert=subscription';
      }
      return Promise.reject(err);
    }

    // 403 — authenticated but not authorized.
    // Feature-gate 403s are silently rejected (the caller handles them).
    // All other 403s fire a toast via the 'zayra:access-denied' custom event so the
    // AppToastProvider can display the message without a full-page navigation.
    if (err.response?.status === 403) {
      const isFeatureGated = err.response?.data?.error === 'feature_not_enabled';
      if (!isFeatureGated && typeof window !== 'undefined') {
        // Prefer the server's reason (e.g. a separation-of-duties refusal) over the generic text.
        const reason = typeof err.response?.data?.message === 'string' && err.response.data.message.trim()
          ? err.response.data.message
          : 'You do not have permission to perform this action. Please contact your administrator.';
        window.dispatchEvent(new CustomEvent('zayra:access-denied', { detail: reason }));
      }
      return Promise.reject(err);
    }

    if (err.response?.status !== 401 || original._retry) {
      return Promise.reject(err);
    }
    original._retry = true;

    if (isRefreshing) {
      return pendingRefreshes.wait().then((token) => {
        original.headers.Authorization = `Bearer ${token}`;
        return client(original);
      });
    }

    isRefreshing = true;
    try {
      const refreshToken = localStorage.getItem('zayra_refresh_token');
      if (!refreshToken) throw Object.assign(new Error('No refresh token'), { noRefreshToken: true });
      const { data } = await publicAuthClient.post('/api/auth/refresh', { refreshToken });
      localStorage.setItem('zayra_access_token', data.accessToken);
      localStorage.setItem('zayra_refresh_token', data.refreshToken);
      pendingRefreshes.resolve(data.accessToken);
      original.headers.Authorization = `Bearer ${data.accessToken}`;
      return client(original);
    } catch (refreshError) {
      pendingRefreshes.reject(refreshError);
      // Only a REFUSED refresh ends the session. When the refresh endpoint could not be reached
      // (offline, a deploy's 502s) the refresh token may still be good: keep the session and let
      // the caller show "can't reach the server" instead of signing the user out over a blip.
      if (!isRefreshRefused(refreshError)) return Promise.reject(refreshError);
      clearSessionKeepingLocale();
      window.location.href = '/login';
      // The original 401: callers (AuthContext's /me) read it as a definite "signed out".
      return Promise.reject(err);
    } finally {
      isRefreshing = false;
    }
  }
);

export default client;

/**
 * Extracts a human-readable message from an API error (backend `message`/`error`,
 * or a sensible fallback) and shows it via the global toast (the AppToastProvider
 * listens for the `zayra:error` event). Use in action handlers so a failed write
 * never fails silently. 403/402 are already toasted by the interceptor, so they are
 * skipped here to avoid double toasts.
 */
export function notifyApiError(err: unknown, fallback = 'Something went wrong. Please try again.'): void {
  const status = (err as { response?: { status?: number } } | null)?.response?.status;
  if (status === 401 || status === 402 || status === 403) return; // handled globally by the interceptor
  const msg = apiErrorReason(err, fallback);
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent('zayra:error', { detail: msg }));
  }
}

/**
 * The plain-words reason for a failed action: the server's own sentence when it sent one (a
 * domain refusal, a validation message), "could not be reached" when nothing answered, otherwise
 * the caller's fallback. Never a bare error code ("maker_checker_violation") or an HTML page.
 */
export function apiErrorReason(err: unknown, fallback: string): string {
  const e = err as { isAxiosError?: boolean; response?: { status?: number; data?: unknown } } | null;
  if (e?.isAxiosError && !e.response) return 'The server could not be reached. Check your connection, then retry.';
  // A 5xx body is server exception text, not a reason for the user: keep the caller's sentence.
  const status = e?.response?.status;
  if (status != null && status >= 500) return fallback;
  const data = e?.response?.data;
  const sentence = (v: unknown): string | null => (typeof v === 'string' && v.trim() && !/^\s*</.test(v) && v.length < 500 ? v.trim() : null);
  if (typeof data === 'string') return sentence(data) ?? fallback;
  if (data && typeof data === 'object') {
    const body = data as { message?: unknown; detail?: unknown; error?: unknown; errors?: unknown; title?: unknown };
    const fromErrors = body.errors && typeof body.errors === 'object'
      ? Object.values(body.errors as Record<string, unknown>).flat().map(sentence).filter((m): m is string => !!m).slice(0, 3).join(' ') || null
      : null;
    // `error` is often a machine code; only a value with a space in it is a sentence.
    const errorText = sentence(body.error);
    return sentence(body.message) ?? sentence(body.detail) ?? fromErrors ?? (errorText && /\s/.test(errorText) ? errorText : null) ?? fallback;
  }
  return fallback;
}
