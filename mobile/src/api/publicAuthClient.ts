import axios, { type AxiosInstance } from 'axios';

export interface PublicAuthClientOptions {
  baseURL: string;
  timeout: number;
  tenantHeader: string;
  /** Static headers added to every request, e.g. the app-version header. */
  extraHeaders?: Record<string, string>;
}

/**
 * Lightweight and independently testable anonymous client. It intentionally
 * has no dependency on token storage, refresh coordination, auth stores, or
 * navigation callbacks.
 */
export function createIsolatedPublicAuthClient(options: PublicAuthClientOptions): AxiosInstance {
  const client = axios.create({
    baseURL: options.baseURL,
    timeout: options.timeout,
    headers: { ...options.extraHeaders, 'Content-Type': 'application/json' },
  });

  client.interceptors.request.use((config) => {
    config.headers.delete('Authorization');
    config.headers.delete(options.tenantHeader);
    config.headers.delete('X-Company-Id');
    return config;
  });

  return client;
}
