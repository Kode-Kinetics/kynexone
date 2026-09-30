/**
 * The preflight's DECISION LOGIC — pure functions from observations to findings.
 *
 * Kept free of I/O so every refusal below is unit-tested without a stack
 * (e2e/preflight/preflight-rules.spec.ts). e2e/preflight/run.ts gathers the observations over HTTP
 * and feeds them here; nothing in this file decides anything by talking to the network.
 */
import { csv, E2E_ENV, FIXTURE_PASSWORD_VARS, isCi, RETIRED_ENV, type Env } from '../identity/env';

export interface Finding {
  check: string;
  ok: boolean;
  detail: string;
}

const pass = (check: string, detail: string): Finding => ({ check, ok: true, detail });
const fail = (check: string, detail: string): Finding => ({ check, ok: false, detail });

// ── Production refusal lists ──────────────────────────────────────────────────────────────────

/**
 * Hosts that are deployed environments. A match is refused even if someone allowlists it: the
 * frontend pilot is on Vercel, the API on Render, the database on Neon, the product domain is ours.
 */
export const PRODUCTION_HOST_SUFFIXES = ['vercel.app', 'onrender.com', 'render.com', 'neon.tech', 'kynexone.com'];

/** Production database names: the live pilot (`kynexone_clean`) and its rollback (`neondb`). */
export const PRODUCTION_DATABASE_NAMES = ['kynexone_clean', 'neondb'];

const LOOPBACK_HOSTS = new Set(['localhost', '127.0.0.1', '::1', '[::1]', '0.0.0.0', 'host.docker.internal']);

const matchesSuffix = (host: string, suffix: string): boolean => host === suffix || host.endsWith(`.${suffix}`);

export const productionSuffixFor = (host: string): string | undefined =>
  PRODUCTION_HOST_SUFFIXES.find((suffix) => matchesSuffix(host.toLowerCase(), suffix));

/**
 * A host a test stack can legitimately live on without anyone naming it: loopback, a docker-compose
 * service name (single label, e.g. `postgres`), or an RFC 1918 / link-local address.
 */
export function isPrivateHost(host: string): boolean {
  const h = host.toLowerCase().replace(/^\[|\]$/g, '');
  if (LOOPBACK_HOSTS.has(h) || LOOPBACK_HOSTS.has(`[${h}]`)) return true;
  if (/^127\./.test(h)) return true;
  if (/^10\./.test(h) || /^192\.168\./.test(h) || /^172\.(1[6-9]|2\d|3[01])\./.test(h)) return true;
  if (/^[a-z0-9-]+$/.test(h)) return true; // single-label name: resolvable only on a private network
  return false;
}

// ── Environment contract ──────────────────────────────────────────────────────────────────────

export function checkEnvironment(env: Env): Finding[] {
  const findings: Finding[] = [];
  const email = env[E2E_ENV.platformEmail]?.trim();
  const password = env[E2E_ENV.platformPassword];

  findings.push(email
    ? pass('platform identity: email set', `${E2E_ENV.platformEmail}=${email}`)
    : fail('platform identity: email set',
      `${E2E_ENV.platformEmail} is not set. It has NO default on purpose: the platform owner is created by `
      + 'the API process at boot, so the tests must present exactly the value that process was started with. '
      + `Export the same ${E2E_ENV.platformEmail} and ${E2E_ENV.platformPassword} the API was launched with `
      + '(docker-compose.yml passes them through to the backend service).'));
  findings.push(password
    ? pass('platform identity: password set', `${E2E_ENV.platformPassword} is set (${password.length} chars)`)
    : fail('platform identity: password set',
      `${E2E_ENV.platformPassword} is not set. Export the value the API process was started with.`));

  if (isCi(env)) {
    const missing = FIXTURE_PASSWORD_VARS.filter((name) => !env[name]?.trim());
    findings.push(missing.length
      ? fail('fixture passwords generated (CI)',
        `${missing.join(', ')} not set. CI must generate every fixture password per run and export it to the `
        + 'bootstrap and every Playwright step; falling back to a committed default is refused.')
      : pass('fixture passwords generated (CI)', `${FIXTURE_PASSWORD_VARS.length} per-run passwords present`));

    findings.push(env[E2E_ENV.expectedCommit]?.trim()
      ? pass('expected commit declared (CI)', `${E2E_ENV.expectedCommit}=${env[E2E_ENV.expectedCommit]}`)
      : fail('expected commit declared (CI)',
        `${E2E_ENV.expectedCommit} is not set. CI must name the commit the stack under test was built from.`));

    if (env[E2E_ENV.allowUnverifiedBuild]?.trim()) {
      findings.push(fail('no unverified-build escape hatch in CI',
        `${E2E_ENV.allowUnverifiedBuild} is set in CI. It exists for local stacks that cannot report their build; `
        + 'a CI run must prove the build it tests.'));
    }
  }

  const retired = Object.keys(RETIRED_ENV).filter((name) => env[name] !== undefined && env[name] !== '');
  findings.push(retired.length
    ? fail('no retired identity overrides',
      retired.map((name) => `${name} is retired — ${RETIRED_ENV[name]}. Unset it.`).join(' '))
    : pass('no retired identity overrides', 'none set'));

  return findings;
}

// ── Target URLs ───────────────────────────────────────────────────────────────────────────────

export function checkTargetUrl(label: string, url: string, hostAllowlist: string[]): Finding {
  const check = `${label} is a disposable stack`;
  let host: string;
  try {
    host = new URL(url).hostname.toLowerCase();
  } catch {
    return fail(check, `'${url}' is not a parseable URL.`);
  }
  const banned = productionSuffixFor(host);
  if (banned) {
    return fail(check,
      `${url} is a deployed environment ('${banned}'). The e2e suites create tenants, users and payroll runs; `
      + 'they never run against a deployed host, and no allowlist overrides this.');
  }
  if (isPrivateHost(host)) return pass(check, `${url} (private host)`);
  if (hostAllowlist.includes(host)) return pass(check, `${url} (named in ${E2E_ENV.hostAllowlist})`);
  return fail(check,
    `${url} is neither loopback/private nor named in ${E2E_ENV.hostAllowlist}. If it really is a throwaway `
    + `stack, name it: ${E2E_ENV.hostAllowlist}=${host}`);
}

// ── Database identity ─────────────────────────────────────────────────────────────────────────

export interface DatabaseIdentity {
  name: string | null;
  host: string | null;
}

export function checkDatabase(
  identity: DatabaseIdentity | null,
  opts: { expectedName?: string; hostAllowlist: string[] },
): Finding[] {
  if (!identity || (!identity.name && !identity.host)) {
    return [fail('database identity reported',
      'The API did not report which database it is connected to (GET /api/platform/health → '
      + 'components.database.name/host). Either the platform login failed, or the API is a build older than '
      + 'this checkout. Refusing: an unidentified database may be production.')];
  }
  const findings: Finding[] = [pass('database identity reported', `${identity.name ?? '?'} on ${identity.host ?? '?'}`)];
  const name = (identity.name ?? '').toLowerCase();
  const host = (identity.host ?? '').toLowerCase();

  findings.push(PRODUCTION_DATABASE_NAMES.includes(name)
    ? fail('database is not a production database',
      `The API is connected to '${identity.name}', a production database name (${PRODUCTION_DATABASE_NAMES.join(', ')}). `
      + 'Stop: nothing in this suite may run against it.')
    : pass('database is not a production database', `'${identity.name}' is not ${PRODUCTION_DATABASE_NAMES.join('/')}`));

  const hosts = host.split(',').map((h) => h.trim().replace(/:\d+$/, '')).filter(Boolean);
  if (!hosts.length) {
    findings.push(fail('database host is disposable', 'The API reported no database host.'));
  } else {
    const bad = hosts.flatMap((h) => {
      const banned = productionSuffixFor(h);
      if (banned) return [`${h} is a deployed database host ('${banned}')`];
      if (isPrivateHost(h) || opts.hostAllowlist.includes(h)) return [];
      return [`${h} is neither private nor named in ${E2E_ENV.databaseHostAllowlist}`];
    });
    findings.push(bad.length
      ? fail('database host is disposable', `${bad.join('; ')}. Refusing a database that may hold real data.`)
      : pass('database host is disposable', hosts.join(', ')));
  }

  if (opts.expectedName) {
    findings.push(opts.expectedName.toLowerCase() === name
      ? pass('database is the expected one', `${E2E_ENV.expectedDatabase}=${opts.expectedName}`)
      : fail('database is the expected one',
        `The API is connected to '${identity.name}', but ${E2E_ENV.expectedDatabase}=${opts.expectedName}. `
        + 'The suite would be testing a different world from the one this run prepared.'));
  }
  return findings;
}

// ── Build identity ────────────────────────────────────────────────────────────────────────────

/** Commits compare on the shorter of the two, which must be at least 7 hex characters. */
export function sameCommit(a: string | null | undefined, b: string | null | undefined): boolean {
  const x = (a ?? '').trim().toLowerCase();
  const y = (b ?? '').trim().toLowerCase();
  if (!/^[0-9a-f]{7,40}$/.test(x) || !/^[0-9a-f]{7,40}$/.test(y)) return false;
  const n = Math.min(x.length, y.length);
  return x.slice(0, n) === y.slice(0, n);
}

const isCommit = (value: string | null | undefined): boolean => /^[0-9a-f]{7,40}$/i.test((value ?? '').trim());

export interface BuildObservation {
  apiCommit: string | null;
  frontendCommit: string | null;
  expectedCommit: string | null;
  allowUnverified: boolean;
  inCi: boolean;
}

export function checkBuild(obs: BuildObservation): Finding[] {
  const findings: Finding[] = [];
  const unverifiedOk = obs.allowUnverified && !obs.inCi;
  const hint = `Build with the commit baked in (CI passes ${'`'}-p:SourceRevisionId${'`'} to the API and BUILD_COMMIT to the `
    + 'frontend; docker compose forwards BUILD_COMMIT to both images).';

  for (const [label, commit, where] of [
    ['API', obs.apiCommit, 'GET /health/live → commit'],
    ['frontend', obs.frontendCommit, 'GET /build-info → commit'],
  ] as const) {
    if (isCommit(commit)) {
      findings.push(pass(`${label} reports its build commit`, `${commit}`));
    } else if (unverifiedOk) {
      findings.push(pass(`${label} reports its build commit`,
        `UNVERIFIED (${where} = '${commit ?? 'unreachable'}'); accepted only because ${E2E_ENV.allowUnverifiedBuild} is set locally`));
    } else {
      findings.push(fail(`${label} reports its build commit`,
        `${where} returned '${commit ?? 'nothing'}'. A stack that cannot say what it was built from may be a stale `
        + `image (the :5173 Docker image is one). ${hint} Locally you may set ${E2E_ENV.allowUnverifiedBuild}=1 `
        + 'to proceed without this proof; CI may not.'));
    }
  }

  if (isCommit(obs.apiCommit) && isCommit(obs.frontendCommit)) {
    findings.push(sameCommit(obs.apiCommit, obs.frontendCommit)
      ? pass('frontend and API are the same build', `${obs.apiCommit}`)
      : fail('frontend and API are the same build',
        `The frontend was built from ${obs.frontendCommit} and the API from ${obs.apiCommit}. The browser would `
        + 'exercise one version of the product against another.'));
  }

  if (obs.expectedCommit) {
    for (const [label, commit] of [['API', obs.apiCommit], ['frontend', obs.frontendCommit]] as const) {
      if (!isCommit(commit)) continue; // already reported above
      findings.push(sameCommit(commit, obs.expectedCommit)
        ? pass(`${label} is the expected build`, `${commit} = ${E2E_ENV.expectedCommit}`)
        : fail(`${label} is the expected build`,
          `${label} was built from ${commit}, but ${E2E_ENV.expectedCommit}=${obs.expectedCommit}. The suite would `
          + 'test a different build from the one under review.'));
    }
  } else if (obs.inCi) {
    findings.push(fail('expected build declared', `${E2E_ENV.expectedCommit} is required in CI.`));
  }
  return findings;
}

// ── Personas ──────────────────────────────────────────────────────────────────────────────────

export interface PersonaExpectation {
  key: string;
  email: string;
  tenantSlug: string;
  role: string;
  scope: 'group' | 'companies';
  companyCodes: string[];
  employeeLinked: boolean;
  /** Every company code the tenant declares (a group persona must see exactly these). */
  tenantCompanyCodes: string[];
  /** The role's permission set as the backend catalog defines it. */
  expectedPermissions: string[];
}

export interface SessionObservation {
  status: number;
  body?: string;
  tenantSlug?: string;
  roles?: string[];
  permissions?: string[];
  companyCodes?: string[];
  isGroupScope?: boolean;
  employeeId?: number | null;
}

const setDiff = (a: string[], b: string[]): string[] => a.filter((x) => !b.includes(x));

export function checkPersonaSession(p: PersonaExpectation, s: SessionObservation): Finding[] {
  const check = (what: string) => `persona ${p.email} @ ${p.tenantSlug}: ${what}`;
  if (s.status !== 200) {
    return [fail(check('signs in'),
      `POST /api/auth/login → HTTP ${s.status}${s.body ? ` ${s.body.slice(0, 160)}` : ''}. `
      + (s.status === 429
        ? 'This is the login limiter; raise E2E_LOGIN_PACING_MS, never the API limit.'
        : 'The account the suites will act as does not exist with this password in the world under test.'))];
  }
  const findings: Finding[] = [pass(check('signs in'), 'HTTP 200')];

  findings.push((s.tenantSlug ?? '').toLowerCase() === p.tenantSlug.toLowerCase()
    ? pass(check('is in the declared tenant'), s.tenantSlug ?? '')
    : fail(check('is in the declared tenant'), `session tenant is '${s.tenantSlug}', expected '${p.tenantSlug}'`));

  const roles = [...(s.roles ?? [])].sort();
  findings.push(roles.length === 1 && roles[0] === p.role
    ? pass(check(`holds exactly role ${p.role}`), roles.join(', '))
    : fail(check(`holds exactly role ${p.role}`), `session roles are [${roles.join(', ')}]`));

  const actual = [...new Set(s.permissions ?? [])].sort();
  const missing = setDiff(p.expectedPermissions, actual);
  const extra = setDiff(actual, p.expectedPermissions);
  findings.push(!missing.length && !extra.length
    ? pass(check('holds exactly the catalog permissions'), `${actual.length} permissions`)
    : fail(check('holds exactly the catalog permissions'),
      `${missing.length ? `missing [${missing.join(', ')}] ` : ''}${extra.length ? `unexpected [${extra.join(', ')}]` : ''}`
      + ` versus the ${p.role} role in AuthSeeder.cs`));

  const companies = [...(s.companyCodes ?? [])].sort();
  const wanted = [...(p.scope === 'companies' ? p.companyCodes : p.tenantCompanyCodes)].sort();
  const scopeOk = JSON.stringify(companies) === JSON.stringify(wanted)
    && (p.scope === 'group' ? s.isGroupScope === true : s.isGroupScope === false);
  findings.push(scopeOk
    ? pass(check(`is ${p.scope}-scoped`), companies.join(', '))
    : fail(check(`is ${p.scope}-scoped`),
      `session is ${s.isGroupScope ? 'group' : 'company'}-scoped over [${companies.join(', ')}], `
      + `declared ${p.scope} over [${wanted.join(', ')}]`));

  if (p.employeeLinked) {
    findings.push(typeof s.employeeId === 'number'
      ? pass(check('is linked to an employee'), `employeeId ${s.employeeId}`)
      : fail(check('is linked to an employee'),
        'the login has no employee behind it; every self-service surface would render empty for it'));
  }
  return findings;
}

// ── Reporting ─────────────────────────────────────────────────────────────────────────────────

export function formatFindings(phase: string, findings: Finding[]): { ok: boolean; report: string } {
  const failed = findings.filter((f) => !f.ok);
  const lines = findings.map((f) => `  ${f.ok ? 'ok  ' : 'FAIL'}  ${f.check}${f.detail ? ` — ${f.detail}` : ''}`);
  const header = failed.length
    ? `E2E PREFLIGHT (${phase}) FAILED — ${failed.length} of ${findings.length} checks. No suite may run against this world.`
    : `E2E PREFLIGHT (${phase}) passed — ${findings.length} checks.`;
  const rule = '─'.repeat(90);
  return { ok: failed.length === 0, report: `\n${rule}\n${header}\n${lines.join('\n')}\n${rule}\n` };
}

export const hostAllowlistFrom = (env: Env): string[] => csv(env[E2E_ENV.hostAllowlist]);
export const databaseHostAllowlistFrom = (env: Env): string[] => csv(env[E2E_ENV.databaseHostAllowlist]);
