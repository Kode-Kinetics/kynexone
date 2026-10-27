import axios, { type AxiosInstance } from 'axios';

export interface PublicAuthClientOptions {
  baseURL: string;
  timeout: number;
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
    headers: { 'Content-Type': 'application/json' },
  });

  client.interceptors.request.use((config) => {
    config.headers.delete('Authorization');
    // No longer sent by any build, but an old interceptor or default must not leak one either.
    config.headers.delete('X-Tenant-Id');
    config.headers.delete('X-Company-Id');
    return config;
  });

  return client;
}
