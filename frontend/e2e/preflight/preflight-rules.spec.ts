import { test, expect } from '@playwright/test';
import { resolveTarget } from '../identity/env';
import {
  checkBuild, checkDatabase, checkEnvironment, checkPersonaSession, checkTargetUrl, formatFindings, isPrivateHost,
  sameCommit, type PersonaExpectation,
} from './rules';

/**
 * Browserless proof of the preflight's refusals (register item F07). Every "must refuse" below is
 * the decision the live preflight takes; e2e/preflight/run.ts only gathers the observations.
 */

const failed = (findings: { ok: boolean; check: string }[]) => findings.filter((f) => !f.ok).map((f) => f.check);
const SHA = 'a'.repeat(40);
const OTHER = 'b'.repeat(40);

test.describe('preflight: environment contract', () => {
  const complete = { PLATFORM_ADMIN_EMAIL: 'owner@example.com', PLATFORM_ADMIN_PASSWORD: 'x' };

  test('refuses a missing platform identity instead of defaulting it (the 28 Sep failure)', () => {
    expect(failed(checkEnvironment({}))).toEqual([
      'platform identity: email set', 'platform identity: password set',
    ]);
    expect(failed(checkEnvironment(complete))).toEqual([]);
  });

  test('in CI, requires every generated fixture password and the expected commit', () => {
    const ci = { ...complete, CI: 'true' };
    expect(failed(checkEnvironment(ci))).toEqual(['fixture passwords generated (CI)', 'expected commit declared (CI)']);
    const full = {
      ...ci, E2E_EXPECTED_COMMIT: SHA, E2E_INTELLIFLOW_PASSWORD: 'p', E2E_RASALMANAR_PASSWORD: 'p',
      E2E_GROUP_PASSWORD: 'p', E2E_EVOSTEL_PASSWORD: 'p',
    };
    expect(failed(checkEnvironment(full))).toEqual([]);
    expect(failed(checkEnvironment({ ...full, E2E_ALLOW_UNVERIFIED_BUILD: '1' })))
      .toEqual(['no unverified-build escape hatch in CI']);
  });

  test('refuses the retired second-identity overrides', () => {
    const findings = checkEnvironment({ ...complete, E2E_DEFAULT_ADMIN_EMAIL: 'someone@else.com' });
    expect(failed(findings)).toEqual(['no retired identity overrides']);
    expect(findings.find((f) => !f.ok)!.detail).toContain('E2E_DEFAULT_ADMIN_EMAIL is retired');
  });

  test('resolves one target, with the documented alias and defaults', () => {
    expect(resolveTarget({})).toEqual({ baseUrl: 'http://localhost:5173', apiBaseUrl: 'http://localhost:5117' });
    expect(resolveTarget({ E2E_BASE_URL: 'http://127.0.0.1:5218/', E2E_API_BASE_URL: 'http://127.0.0.1:5248' }))
      .toEqual({ baseUrl: 'http://127.0.0.1:5218', apiBaseUrl: 'http://127.0.0.1:5248' });
    expect(resolveTarget({ PLAYWRIGHT_BASE_URL: 'http://a:1', E2E_BASE_URL: 'http://b:2' }).baseUrl).toBe('http://a:1');
  });
});

test.describe('preflight: target hosts', () => {
  test('refuses every deployed host, even when allowlisted', () => {
    for (const url of [
      'https://kynexone.vercel.app', 'https://kynexone-api.onrender.com', 'https://app.kynexone.com',
      'https://ep-cool-1234.eu-central-1.aws.neon.tech', 'https://x.render.com',
    ]) {
      const host = new URL(url).hostname;
      const finding = checkTargetUrl('frontend', url, [host]);
      expect(finding.ok, url).toBe(false);
      expect(finding.detail).toContain('deployed environment');
    }
  });

  test('accepts loopback, compose service names and private addresses; refuses unknown public hosts', () => {
    for (const url of ['http://localhost:5173', 'http://127.0.0.1:5248', 'http://backend:5117', 'http://10.1.2.3:80']) {
      expect(checkTargetUrl('API', url, []).ok, url).toBe(true);
    }
    expect(checkTargetUrl('API', 'https://staging.example.org', []).ok).toBe(false);
    expect(checkTargetUrl('API', 'https://staging.example.org', ['staging.example.org']).ok).toBe(true);
    expect(checkTargetUrl('API', 'not a url', []).ok).toBe(false);
    expect(isPrivateHost('postgres')).toBe(true);
    expect(isPrivateHost('db.example.com')).toBe(false);
  });
});

test.describe('preflight: database identity', () => {
  test('refuses the production database names', () => {
    for (const name of ['kynexone_clean', 'neondb', 'NEONDB']) {
      expect(failed(checkDatabase({ name, host: 'localhost' }, { hostAllowlist: [] })), name)
        .toEqual(['database is not a production database']);
    }
  });

  test('refuses a Neon or Render database host even when allowlisted, and unknown remote hosts', () => {
    const neon = 'ep-cool-1234-pooler.eu-central-1.aws.neon.tech';
    expect(failed(checkDatabase({ name: 'zayra', host: neon }, { hostAllowlist: [neon] })))
      .toEqual(['database host is disposable']);
    expect(failed(checkDatabase({ name: 'zayra', host: 'dpg-x.oregon-postgres.render.com' }, { hostAllowlist: [] })))
      .toEqual(['database host is disposable']);
    expect(failed(checkDatabase({ name: 'zayra', host: 'db.example.org' }, { hostAllowlist: [] })))
      .toEqual(['database host is disposable']);
    expect(failed(checkDatabase({ name: 'zayra', host: 'db.example.org' }, { hostAllowlist: ['db.example.org'] })))
      .toEqual([]);
  });

  test('refuses an unidentified database and a database other than the expected one', () => {
    expect(failed(checkDatabase(null, { hostAllowlist: [] }))).toEqual(['database identity reported']);
    expect(failed(checkDatabase({ name: null, host: null }, { hostAllowlist: [] }))).toEqual(['database identity reported']);
    expect(failed(checkDatabase({ name: 'kynexone_cto_20260928', host: 'localhost' }, { expectedName: 'zayra', hostAllowlist: [] })))
      .toEqual(['database is the expected one']);
    expect(failed(checkDatabase({ name: 'zayra', host: 'postgres' }, { expectedName: 'zayra', hostAllowlist: [] }))).toEqual([]);
    expect(failed(checkDatabase({ name: 'zayra', host: 'localhost:5432' }, { hostAllowlist: [] }))).toEqual([]);
  });
});

test.describe('preflight: build identity', () => {
  const base = { allowUnverified: false, inCi: true };

  test('passes only when both builds report the expected commit', () => {
    expect(failed(checkBuild({ ...base, apiCommit: SHA, frontendCommit: SHA, expectedCommit: SHA }))).toEqual([]);
    expect(failed(checkBuild({ ...base, apiCommit: SHA, frontendCommit: SHA.slice(0, 7), expectedCommit: SHA }))).toEqual([]);
  });

  test('refuses a stale image, a mixed stack and a stack that cannot say what it is', () => {
    expect(failed(checkBuild({ ...base, apiCommit: OTHER, frontendCommit: OTHER, expectedCommit: SHA })))
      .toEqual(['API is the expected build', 'frontend is the expected build']);
    expect(failed(checkBuild({ ...base, apiCommit: SHA, frontendCommit: OTHER, expectedCommit: SHA })))
      .toEqual(['frontend and API are the same build', 'frontend is the expected build']);
    expect(failed(checkBuild({ ...base, apiCommit: 'local', frontendCommit: 'unknown', expectedCommit: SHA })))
      .toEqual(['API reports its build commit', 'frontend reports its build commit']);
    expect(failed(checkBuild({ ...base, apiCommit: SHA, frontendCommit: SHA, expectedCommit: null })))
      .toEqual(['expected build declared']);
  });

  test('the unverified-build escape hatch works locally and never in CI', () => {
    const unknown = { apiCommit: 'local', frontendCommit: null, expectedCommit: null };
    expect(failed(checkBuild({ ...unknown, allowUnverified: true, inCi: false }))).toEqual([]);
    expect(failed(checkBuild({ ...unknown, allowUnverified: true, inCi: true }))).not.toEqual([]);
  });

  test('commit comparison needs real hex of at least 7 characters', () => {
    expect(sameCommit(SHA, SHA.slice(0, 7))).toBe(true);
    expect(sameCommit(SHA, SHA.slice(0, 6))).toBe(false);
    expect(sameCommit('local', 'local')).toBe(false);
    expect(sameCommit(SHA, OTHER)).toBe(false);
  });
});

test.describe('preflight: persona sessions', () => {
  const persona: PersonaExpectation = {
    key: 'almarai-test|payroll@alm-dairy-ksa.almarai-test.local',
    email: 'payroll@alm-dairy-ksa.almarai-test.local',
    tenantSlug: 'almarai-test',
    role: 'Payroll Officer',
    scope: 'companies',
    companyCodes: ['ALM-DAIRY-KSA'],
    employeeLinked: false,
    tenantCompanyCodes: ['ALM-DAIRY-KSA', 'ALM-BAKERY-KSA'],
    expectedPermissions: ['payroll.read', 'payroll.write'],
  };
  const good = {
    status: 200, tenantSlug: 'almarai-test', roles: ['Payroll Officer'], permissions: ['payroll.write', 'payroll.read'],
    companyCodes: ['ALM-DAIRY-KSA'], isGroupScope: false, employeeId: null,
  };

  test('accepts the declared persona', () => {
    expect(failed(checkPersonaSession(persona, good))).toEqual([]);
  });

  test('refuses a failed login, a wrong role, drifted permissions, a widened scope and a missing employee link', () => {
    expect(failed(checkPersonaSession(persona, { status: 401, body: 'Invalid credentials' })))
      .toEqual([`persona ${persona.email} @ almarai-test: signs in`]);
    expect(failed(checkPersonaSession(persona, { ...good, roles: ['Payroll Officer', 'Finance Approver'] })))
      .toEqual([`persona ${persona.email} @ almarai-test: holds exactly role Payroll Officer`]);
    const drift = checkPersonaSession(persona, { ...good, permissions: ['payroll.read', 'payroll.approve'] });
    expect(failed(drift)).toEqual([`persona ${persona.email} @ almarai-test: holds exactly the catalog permissions`]);
    expect(drift.find((f) => !f.ok)!.detail).toContain('missing [payroll.write] unexpected [payroll.approve]');
    expect(failed(checkPersonaSession(persona, { ...good, isGroupScope: true, companyCodes: ['ALM-BAKERY-KSA', 'ALM-DAIRY-KSA'] })))
      .toEqual([`persona ${persona.email} @ almarai-test: is companies-scoped`]);
    expect(failed(checkPersonaSession({ ...persona, employeeLinked: true }, good)))
      .toEqual([`persona ${persona.email} @ almarai-test: is linked to an employee`]);
    expect(failed(checkPersonaSession(persona, { ...good, tenantSlug: 'intelliflow' })))
      .toEqual([`persona ${persona.email} @ almarai-test: is in the declared tenant`]);
  });
});

test('the report names every failed check and says it is a failure', () => {
  const { ok, report } = formatFindings('target', [
    { check: 'a', ok: true, detail: 'fine' },
    { check: 'platform owner authenticates', ok: false, detail: 'HTTP 401' },
  ]);
  expect(ok).toBe(false);
  expect(report).toContain('E2E PREFLIGHT (target) FAILED — 1 of 2 checks');
  expect(report).toContain('FAIL  platform owner authenticates — HTTP 401');
});
