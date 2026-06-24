import axios from 'axios';

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

client.interceptors.request.use((config) => {
  const url = config.url ?? '';
  const isAuthEndpoint = url.includes('/api/auth/login') || url.includes('/api/auth/refresh');
  if (!isAuthEndpoint) {
    const token = localStorage.getItem('zayra_access_token');
    if (token) config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

let isRefreshing = false;
let pending: Array<(token: string) => void> = [];

client.interceptors.response.use(
  (res) => res,
  async (err) => {
    const original = err.config;
    const url: string = original?.url ?? '';
    const isAuthEndpoint = url.includes('/api/auth/login') || url.includes('/api/auth/refresh');

    // Never intercept 401s from auth endpoints — propagate directly so the login
    // form can display its own error message without triggering a redirect loop.
    if (isAuthEndpoint) {
      return Promise.reject(err);
    }

    // 402 — subscription expired or inactive; redirect to tenant admin with alert
    if (err.response?.status === 402) {
      // Only redirect for non-tenant-admin URLs to avoid redirect loops
      const url: string = original?.url ?? '';
      if (!url.includes('/api/tenant-admin/usage') && !url.includes('/api/tenant-admin/subscription')) {
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
      return new Promise((resolve, reject) => {
        pending.push((token) => {
          original.headers.Authorization = `Bearer ${token}`;
          resolve(client(original));
        });
      });
    }

    isRefreshing = true;
    try {
      const refreshToken = localStorage.getItem('zayra_refresh_token');
      if (!refreshToken) throw new Error('No refresh token');
      const { data } = await axios.post(`${resolveBaseUrl()}/api/auth/refresh`, { refreshToken });
      localStorage.setItem('zayra_access_token', data.accessToken);
      localStorage.setItem('zayra_refresh_token', data.refreshToken);
      pending.forEach((cb) => cb(data.accessToken));
      pending = [];
      original.headers.Authorization = `Bearer ${data.accessToken}`;
      return client(original);
    } catch {
      localStorage.clear();
      window.location.href = '/login';
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
  const e = err as { response?: { status?: number; data?: { message?: string; error?: string } } };
  const status = e?.response?.status;
  if (status === 401 || status === 402 || status === 403) return; // handled globally by the interceptor
  const msg = e?.response?.data?.message ?? e?.response?.data?.error ?? fallback;
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent('zayra:error', { detail: msg }));
  }
}
