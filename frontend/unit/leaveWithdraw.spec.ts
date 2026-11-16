import { expect, test } from '@playwright/test';
import type { InternalAxiosRequestConfig } from 'axios';
import client from '../src/api/client';
import { leaveRequestsApi } from '../src/api/leave';

// Withdraw posts a JSON body. The endpoint binds `[FromBody] WithdrawLeaveRequest`; a POST with no content at all
// (what axios sends when no data is passed) is answered 400/415 by ASP.NET Core and the request stayed pending.
// The mobile app has always posted `{ reason }`; the web must too.

async function captureWithdraw(call: () => Promise<unknown>): Promise<InternalAxiosRequestConfig> {
  // The shared client's request interceptor reads the token from localStorage, which Node does not have.
  const storage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
  (globalThis as { localStorage?: unknown }).localStorage = storage;
  const original = client.defaults.adapter;
  let captured: InternalAxiosRequestConfig | undefined;
  client.defaults.adapter = async config => {
    captured = config;
    return { data: {}, status: 200, statusText: 'OK', headers: {}, config };
  };
  try {
    await call();
  } finally {
    client.defaults.adapter = original;
  }
  if (!captured) throw new Error('no request was sent');
  return captured;
}

test('withdraw sends a JSON body, not an empty POST', async () => {
  const config = await captureWithdraw(() => leaveRequestsApi.withdraw('11111111-1111-1111-1111-111111111111'));
  expect(config.method).toBe('post');
  expect(config.url).toBe('/api/leave/requests/11111111-1111-1111-1111-111111111111/withdraw');
  expect(config.data).toBeDefined();
  expect(JSON.parse(String(config.data))).toEqual({});
});

test('withdraw carries the reason when one is given', async () => {
  const config = await captureWithdraw(() => leaveRequestsApi.withdraw('abc', 'changed plans'));
  expect(JSON.parse(String(config.data))).toEqual({ reason: 'changed plans' });
});
