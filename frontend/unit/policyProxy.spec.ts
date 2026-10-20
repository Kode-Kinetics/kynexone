import { expect, test } from '@playwright/test';
import { createServer, type Server } from 'node:http';
import { parse } from 'node:url';
import { proxyRequest } from 'next/dist/server/lib/router-utils/proxy-request';
import nextConfig from '../next.config';

// Characterizes the installed Next rewrite transport that previously ended valid
// policy extraction POSTs after 30 seconds. Local loopback only; no real API/AI.
async function listen(server: Server) {
  await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Missing loopback listener');
  return `http://127.0.0.1:${address.port}`;
}

async function close(server: Server) {
  server.closeAllConnections();
  await new Promise<void>(resolve => server.close(() => resolve()));
}

test('policy proxy carries one delayed POST beyond the old 30-second cutoff', async () => {
  test.setTimeout(45_000);
  let requests = 0;
  const upstream = createServer((req, res) => {
    requests++;
    req.resume();
    const timer = setTimeout(() => {
      res.setHeader('Content-Type', 'application/json');
      res.end(JSON.stringify({ prepared: 3 }));
    }, 31_000);
    res.on('close', () => clearTimeout(timer));
  });
  const upstreamUrl = await listen(upstream);
  const proxy = createServer((req, res) => {
    void proxyRequest(req, res, parse(`${upstreamUrl}/api/setup-assistant/policy/extract`, true), undefined, undefined, nextConfig.experimental?.proxyTimeout).catch(() => {});
  });
  try {
    const proxyUrl = await listen(proxy);
    const response = await fetch(`${proxyUrl}/api/setup-assistant/policy/extract`, { method: 'POST', body: '{}' });
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ prepared: 3 });
    expect(requests).toBe(1);
  } finally {
    await close(proxy);
    await close(upstream);
  }
});

test('policy proxy still cancels its upstream when the caller disconnects without retrying', async () => {
  let requests = 0;
  let received!: () => void;
  const requestReceived = new Promise<void>(resolve => { received = resolve; });
  let upstreamClosed = false;
  const upstream = createServer((req, res) => {
    requests++;
    req.resume();
    res.on('close', () => { upstreamClosed = true; });
    received();
  });
  const upstreamUrl = await listen(upstream);
  const proxy = createServer((req, res) => {
    void proxyRequest(req, res, parse(`${upstreamUrl}/api/setup-assistant/policy/extract`, true), undefined, undefined, nextConfig.experimental?.proxyTimeout).catch(() => {});
  });
  try {
    const proxyUrl = await listen(proxy);
    const controller = new AbortController();
    const response = fetch(`${proxyUrl}/api/setup-assistant/policy/extract`, { method: 'POST', body: '{}', signal: controller.signal });
    const rejected = expect(response).rejects.toThrow();
    await requestReceived;
    controller.abort();
    await rejected;
    await expect.poll(() => upstreamClosed).toBe(true);
    expect(requests).toBe(1);
  } finally {
    await close(proxy);
    await close(upstream);
  }
});
