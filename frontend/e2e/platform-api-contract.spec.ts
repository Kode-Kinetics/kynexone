import { expect, test } from '@playwright/test';
import platform, { platformApi } from '../src/api/platform';

type Captured = { method: string; url: string; params?: unknown; data: unknown };

/** Replaces the axios adapter so each call is recorded instead of sent; no browser, no server. */
async function capture(call: () => Promise<unknown>): Promise<Captured> {
  const seen: Captured[] = [];
  const original = platform.defaults.adapter;
  platform.defaults.adapter = async cfg => {
    seen.push({
      method: String(cfg.method).toUpperCase(),
      url: String(cfg.url),
      params: cfg.params,
      data: typeof cfg.data === 'string' ? JSON.parse(cfg.data) : cfg.data,
    });
    return { data: {}, status: 200, statusText: 'OK', headers: {}, config: cfg };
  };
  try { await call(); } finally { platform.defaults.adapter = original; }
  expect(seen).toHaveLength(1);
  return seen[0];
}

const ID = '11111111-1111-1111-1111-111111111111';
const base = `/api/platform/tenants/${ID}`;

// Each route and body shape below was checked against PlatformController.cs.
test.describe('platform console API contracts', () => {
  test('purge sends the literal confirm token the controller accepts', async () => {
    const c = await capture(() => platformApi.purgeTenant(ID));
    expect(c.method).toBe('DELETE');
    expect(c.url).toBe(`${base}/purge?confirm=PURGE`);
  });

  test('restore is a bodiless POST', async () => {
    const c = await capture(() => platformApi.restoreTenant(ID));
    expect([c.method, c.url, c.data]).toEqual(['POST', `${base}/restore`, undefined]);
  });

  test('account export is a GET for a blob', async () => {
    const c = await capture(() => platformApi.exportTenantAccount(ID));
    expect([c.method, c.url]).toEqual(['GET', `${base}/export`]);
  });

  test('per-tenant mandatory two-step date: PUT with enforceFromUtc and reason', async () => {
    const body = { enforceFromUtc: '2026-12-01T00:00:00Z', reason: 'demo week postponement' };
    const c = await capture(() => platformApi.setTenantPrivilegedMfaEnforcement(ID, body));
    expect([c.method, c.url, c.data]).toEqual(['PUT', `${base}/privileged-mfa-enforcement`, body]);
    const clear = { enforceFromUtc: null, reason: 'follow the platform date again' };
    const c2 = await capture(() => platformApi.setTenantPrivilegedMfaEnforcement(ID, clear));
    expect(c2.data).toEqual(clear);
  });

  test('platform-wide mandatory two-step date: GET and PUT', async () => {
    const g = await capture(() => platformApi.getPlatformPrivilegedMfaEnforcement());
    expect([g.method, g.url]).toEqual(['GET', '/api/platform/security/privileged-mfa-enforcement']);
    const body = { enforceFromUtc: '2026-12-01T00:00:00Z', reason: 'move the whole app date' };
    const p = await capture(() => platformApi.setPlatformPrivilegedMfaEnforcement(body));
    expect([p.method, p.url, p.data]).toEqual(['PUT', '/api/platform/security/privileged-mfa-enforcement', body]);
  });

  test('invitation hand-over: PUT enabled and reason', async () => {
    const body = { enabled: true, reason: 'mail never arrived' };
    const c = await capture(() => platformApi.updateTenantInvitationHandover(ID, body));
    expect([c.method, c.url, c.data]).toEqual(['PUT', `${base}/invitation-handover`, body]);
  });

  test('two-step policy: PUT mfaPolicy, trusted days and reason', async () => {
    const body = { mfaPolicy: 'PrivilegedRoles' as const, mfaTrustedDeviceDays: 30, reason: 'tighten sign-in' };
    const c = await capture(() => platformApi.updateTenantTwoStepPolicy(ID, body));
    expect([c.method, c.url, c.data]).toEqual(['PUT', `${base}/two-step-policy`, body]);
  });

  test('operator two-step reset: bodiless POST on the team member', async () => {
    const c = await capture(() => platformApi.resetTeamMfa(ID));
    expect([c.method, c.url, c.data]).toEqual(['POST', `/api/platform/team/${ID}/reset-mfa`, undefined]);
  });

  test('security policy PUT carries the reason next to the settings', async () => {
    const body = { passwordMinLength: 12, reason: 'align with client policy' };
    const c = await capture(() => platformApi.updateTenantSecurityPolicy(ID, body));
    expect([c.method, c.url, c.data]).toEqual(['PUT', `${base}/security-policy`, body]);
  });
});
