import { expect, test } from '@playwright/test';
import { getPathMatch } from 'next/dist/shared/lib/router/utils/path-match';
import nextConfig from '../next.config';

type HeaderRule = { source: string; headers: { key: string; value: string }[] };

/** The headers Next would send for `pathname`: rules in order, a later rule overriding an earlier key. */
async function headersFor(pathname: string): Promise<Record<string, string>> {
  const rules = (await nextConfig.headers!()) as HeaderRule[];
  const out: Record<string, string> = {};
  for (const rule of rules) {
    if (!getPathMatch(rule.source, { strict: true, removeUnnamedParams: true })(pathname)) continue;
    for (const { key, value } of rule.headers) out[key.toLowerCase()] = value;
  }
  return out;
}

test('every page carries the baseline browser hardening headers', async () => {
  for (const path of ['/', '/login', '/dashboard', '/platform/login', '/accept-invitation', '/learning/course/abc', '/_next/static/chunks/app.js', '/favicon.ico']) {
    const headers = await headersFor(path);
    expect(headers['x-content-type-options'], path).toBe('nosniff');
    expect(headers['referrer-policy'], path).toBe('strict-origin-when-cross-origin');
    expect(headers['x-frame-options'], path).toBe('SAMEORIGIN');
    expect(headers['content-security-policy'], path).toBe("frame-ancestors 'self'");
    expect(headers['permissions-policy'], path).toContain('camera=()');
    expect(headers['permissions-policy'], path).toContain('microphone=()');
    // Same-origin only, for Setup's "Use my current location"; no third-party frame may ask.
    expect(headers['permissions-policy'], path).toContain('geolocation=(self)');
    expect(headers['permissions-policy'], path).not.toMatch(/geolocation=\*|geolocation=\(\s*\*/);
  }
});

test('the welcome page keeps its stricter referrer policy', async () => {
  const headers = await headersFor('/welcome');
  expect(headers['referrer-policy']).toBe('no-referrer');
  expect(headers['x-frame-options']).toBe('SAMEORIGIN');
});

test('the API proxy is left to the API’s own security headers', async () => {
  const headers = await headersFor('/api/auth/login');
  expect(headers['cache-control']).toBe('no-store');
  expect(headers['x-frame-options']).toBeUndefined();
  expect(headers['content-security-policy']).toBeUndefined();
});

test('the image optimizer is not an open proxy', () => {
  expect(nextConfig.images?.remotePatterns ?? []).toEqual([]);
  expect(nextConfig.images?.domains ?? []).toEqual([]);
});
