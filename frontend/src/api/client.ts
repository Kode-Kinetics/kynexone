import axios from 'axios';
import { RefreshQueue } from './refreshQueue';

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

client.interceptors.response.use(
  (res) => res,
  async (err) => {
    const original = err.config;
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
        window.dispatchEvent(new CustomEvent('zayra:access-denied', {
          detail: 'You do not have permission to perform this action. Please contact your administrator.',
        }));
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
      if (!refreshToken) throw new Error('No refresh token');
      const { data } = await publicAuthClient.post('/api/auth/refresh', { refreshToken });
      localStorage.setItem('zayra_access_token', data.accessToken);
      localStorage.setItem('zayra_refresh_token', data.refreshToken);
      pendingRefreshes.resolve(data.accessToken);
      original.headers.Authorization = `Bearer ${data.accessToken}`;
      return client(original);
    } catch (refreshError) {
      pendingRefreshes.reject(refreshError);
      localStorage.clear();
      window.location.href = '/login';
      return Promise.reject(refreshError);
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
  const e = err as { response?: { status?: number; data?: { message?: string; error?: string } } };
  const status = e?.response?.status;
  if (status === 401 || status === 402 || status === 403) return; // handled globally by the interceptor
  const msg = e?.response?.data?.message ?? e?.response?.data?.error ?? fallback;
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent('zayra:error', { detail: msg }));
  }
}
