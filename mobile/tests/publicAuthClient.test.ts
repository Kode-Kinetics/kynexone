import test from 'node:test';
import assert from 'node:assert/strict';
import axios, { AxiosError, type AxiosRequestConfig, type AxiosResponse } from 'axios';
import { createIsolatedPublicAuthClient } from '../src/api/publicAuthClient.ts';

const PUBLIC_ROUTES = [
  '/auth/login',
  '/auth/refresh',
  '/auth/forgot-password',
  '/auth/reset-password',
  '/auth/accept-invitation',
  '/auth/mfa/challenge/verify',
  '/auth/mfa/enrollment/setup',
  '/auth/mfa/enrollment/verify-setup',
] as const;

test('public auth routes strip mixed-case security headers and never replay a 401', async () => {
  const oldAuthorization = axios.defaults.headers.common.AUTHORIZATION;
  axios.defaults.headers.common.AUTHORIZATION = 'Bearer stale-global';
  try {
    for (const route of PUBLIC_ROUTES) {
      const client = createIsolatedPublicAuthClient({
        baseURL: 'https://api.example.test',
        timeout: 1_000,
        tenantHeader: 'X-Tenant-Id',
      });
      let calls = 0;
      let adaptedHeaders: Record<string, unknown> = {};
      client.defaults.adapter = async (config: AxiosRequestConfig) => {
        calls += 1;
        adaptedHeaders = (config.headers as { toJSON?: () => Record<string, unknown> })?.toJSON?.() ?? {};
        const response: AxiosResponse = {
          data: { message: 'Rejected' },
          status: 401,
          statusText: 'Unauthorized',
          headers: {},
          config: config as any,
        };
        throw new AxiosError('Rejected', 'ERR_BAD_REQUEST', config as any, undefined, response);
      };

      await assert.rejects(() => client.post(route, {}, {
        headers: {
          AuThOrIzAtIoN: 'Bearer stale-request',
          'x-tenant-id': 'stale-tenant',
          'X-COMPANY-ID': 'stale-company',
        },
      }));

      assert.equal(calls, 1, route);
      const names = Object.keys(adaptedHeaders).map((name) => name.toLowerCase());
      assert.equal(names.includes('authorization'), false, route);
      assert.equal(names.includes('x-tenant-id'), false, route);
      assert.equal(names.includes('x-company-id'), false, route);
    }
  } finally {
    if (oldAuthorization === undefined) delete axios.defaults.headers.common.AUTHORIZATION;
    else axios.defaults.headers.common.AUTHORIZATION = oldAuthorization;
  }
});
