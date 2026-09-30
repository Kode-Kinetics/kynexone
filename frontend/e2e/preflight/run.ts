/**
 * THE E2E PREFLIGHT — proves the suites are pointed at the world this run prepared, and refuses
 * (non-zero exit, a report naming every failed check) otherwise. Register item F07.
 *
 * Three phases, each a superset of the one before where it matters:
 *
 *   target  Before anything writes. The environment contract is complete; the frontend and API are
 *           disposable hosts (checked BEFORE any request, so fixture credentials are never sent to a
 *           deployed host); both are up and are the expected build; the frontend proxies to the same
 *           API and database the bootstrap talks to; the database is not production; the platform
 *           owner authenticates. Run by CI as its own first step, and by the bootstrap before it
 *           provisions anything.
 *
 *   world   After the bootstrap. Everything in `target`, plus: every declared tenant exists with the
 *           id the bootstrap recorded; every legal entity exists; the live role catalog is the one
 *           AuthSeeder.cs defines; every bootstrap persona signs in and holds exactly its declared
 *           role, scope, employee link and catalog permissions. Writes e2e/.auth/preflight.json.
 *
 *   lane    The globalSetup of every browser lane. Everything in `target` (so a lane can never start
 *           against a different stack), the old readiness floor, a match against the `world` record,
 *           and a LIVE re-check of the world itself: every declared tenant still exists and was not
 *           re-created after the world was verified, every legal entity exists, and the live role
 *           catalog is still AuthSeeder's. Persona logins are not repeated here; each lane's own setup
 *           project verifies every persona it signs in against the same contract, at the moment it
 *           signs in.
 *
 * ── What the record holds, and why only that ─────────────────────────────────────────────────
 * The record is the EXPECTED world, computed from the environment and this checkout alone (URLs,
 * expected commit and database, platform email, the declared tenants and personas, a hash of
 * AuthSeeder.cs) plus the local time the world passed. Nothing a server returned is written to disk.
 * The lane recomputes the expected world and compares; the live stack is then checked against the
 * same expectations directly, so a lane still cannot run against a world other than the one verified.
 *
 * Decision logic lives in ./rules.ts (pure, unit-tested); this file only gathers observations.
 */
import { execSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import { E2E_ENV, isCi, resolveTarget, type Env } from '../identity/env';
import { AUTH_SEEDER_PATH, expectedPermissions, loadRoleCatalog, type RoleCatalog } from '../identity/role-catalog';
import {
  PERSONAS, PLATFORM_EMAIL, PLATFORM_PASSWORD, TENANTS, WORLD_MANIFEST,
  type FixtureTenant, type Persona, type WorldManifest,
} from '../world';
import {
  checkBuild, checkDatabase, checkEnvironment, checkPersonaSession, checkTargetUrl, databaseHostAllowlistFrom,
  formatFindings, hostAllowlistFrom, type DatabaseIdentity, type Finding,
} from './rules';

export type Phase = 'target' | 'world' | 'lane';

/** Written by the `world` phase; read by every `lane` phase. Gitignored with the rest of e2e/.auth. */
export const PREFLIGHT_RECORD = 'e2e/.auth/preflight.json';

/**
 * The world a run EXPECTS — every field comes from the environment or the checkout, never from a
 * server response. Both the `world` phase (which records it) and the `lane` phase (which recomputes it)
 * build it the same way, so the comparison says whether the lane is set up for the world that passed.
 */
export interface ExpectedWorld {
  baseUrl: string;
  apiBaseUrl: string;
  expectedCommit: string | null;
  expectedDatabase: string | null;
  platformEmail: string;
  /** `<slug>: <company codes>` for every declared tenant. */
  tenants: string[];
  /** Keys of every persona the bootstrap provisions. */
  personas: string[];
  /** sha256 of the AuthSeeder.cs the role matrix is generated from. */
  authSeederSha256: string;
}

export interface PreflightRecord {
  phase: 'world';
  /** This machine's clock when every world check passed. */
  verifiedAtUtc: string;
  expected: ExpectedWorld;
}

/** How far the API's clock may run ahead of this one before a tenant counts as created after verification. */
const CLOCK_TOLERANCE_MS = 120_000;

interface Response {
  status: number;
  json: any;
  text: string;
  headers: Headers | null;
  error?: string;
}

async function http(
  method: string, url: string, opts: { token?: string; body?: unknown; timeoutMs?: number } = {},
): Promise<Response> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (opts.body !== undefined) headers['Content-Type'] = 'application/json';
  if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
  try {
    const res = await fetch(url, {
      method,
      headers,
      body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
      redirect: 'manual',
      signal: AbortSignal.timeout(opts.timeoutMs ?? 20_000),
    });
    const text = await res.text();
    let json: any = null;
    try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
    return { status: res.status, json, text, headers: res.headers };
  } catch (error) {
    const cause = error instanceof Error ? `${error.message}${(error as any).cause ? ` (${(error as any).cause.code ?? (error as any).cause.message ?? ''})` : ''}` : String(error);
    return { status: 0, json: null, text: '', headers: null, error: cause };
  }
}

const pass = (check: string, detail: string): Finding => ({ check, ok: true, detail });
const fail = (check: string, detail: string): Finding => ({ check, ok: false, detail });
const describe = (r: Response): string => (r.status === 0 ? `unreachable: ${r.error}` : `HTTP ${r.status} ${r.text.replace(/\s+/g, ' ').slice(0, 200)}`);
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

function gitHead(): string | null {
  try {
    return execSync('git rev-parse HEAD', { stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim() || null;
  } catch {
    return null;
  }
}

/** The commit the stack must have been built from: E2E_EXPECTED_COMMIT, else (locally) this checkout's HEAD. */
function expectedCommitFrom(env: Env): string | null {
  return env[E2E_ENV.expectedCommit]?.trim() || (isCi(env) ? null : gitHead());
}

/** The expected world, from the environment and the checkout only. See {@link ExpectedWorld}. */
export function expectedWorld(env: Env = process.env): ExpectedWorld {
  const { baseUrl, apiBaseUrl } = resolveTarget(env);
  let authSeederSha256 = 'unreadable';
  try {
    authSeederSha256 = createHash('sha256').update(readFileSync(AUTH_SEEDER_PATH)).digest('hex');
  } catch { /* reported by the catalog check */ }
  return {
    baseUrl,
    apiBaseUrl,
    expectedCommit: expectedCommitFrom(env),
    expectedDatabase: env[E2E_ENV.expectedDatabase]?.trim() || null,
    platformEmail: PLATFORM_EMAIL,
    tenants: TENANTS.map((t) => `${t.slug}: ${t.companies.map((c) => c.code).join(', ')}`),
    personas: bootstrapPersonas().map((p) => p.key).sort(),
    authSeederSha256,
  };
}

/** Field-by-field differences between two expected worlds, for a message a person can act on. */
function expectedWorldDiff(recorded: ExpectedWorld, current: ExpectedWorld): string[] {
  const out: string[] = [];
  for (const key of Object.keys(current) as Array<keyof ExpectedWorld>) {
    const a = JSON.stringify(recorded?.[key] ?? null);
    const b = JSON.stringify(current[key]);
    if (a !== b) out.push(`${key}: verified ${a.slice(0, 120)} → now ${b.slice(0, 120)}`);
  }
  return out;
}

interface TargetContext {
  findings: Finding[];
  baseUrl: string;
  apiBaseUrl: string;
  apiCommit: string | null;
  frontendCommit: string | null;
  database: DatabaseIdentity | null;
  platformToken: string | null;
  activeTenants: number | null;
}

function databaseFrom(health: any): DatabaseIdentity | null {
  const db = health?.components?.database;
  if (!db || (db.name == null && db.host == null)) return null;
  return { name: db.name ?? null, host: db.host ?? null };
}

async function checkTarget(env: Env): Promise<TargetContext> {
  const { baseUrl, apiBaseUrl } = resolveTarget(env);
  const ctx: TargetContext = {
    findings: [], baseUrl, apiBaseUrl, apiCommit: null, frontendCommit: null, database: null,
    platformToken: null, activeTenants: null,
  };
  const f = ctx.findings;

  // 1. Contract and hosts — decided before a single request leaves this process.
  f.push(...checkEnvironment(env));
  const hosts = hostAllowlistFrom(env);
  f.push(checkTargetUrl('frontend (PLAYWRIGHT_BASE_URL)', baseUrl, hosts));
  f.push(checkTargetUrl('API (E2E_API_BASE_URL)', apiBaseUrl, hosts));
  if (f.some((x) => !x.ok && /disposable stack/.test(x.check))) return ctx;

  // 2. The API is up, is this product, and is ready.
  const live = await http('GET', `${apiBaseUrl}/health/live`);
  if (live.status === 200 && live.json?.service === 'zayra-api') {
    f.push(pass('API is reachable and is the KynexOne API', `${apiBaseUrl}/health/live`));
    ctx.apiCommit = typeof live.json.commit === 'string' ? live.json.commit : null;
  } else {
    f.push(fail('API is reachable and is the KynexOne API',
      `GET ${apiBaseUrl}/health/live → ${describe(live)}. Expected 200 with service "zayra-api".`));
    return ctx;
  }
  const ready = await http('GET', `${apiBaseUrl}/health/ready`);
  const health = ready.json ?? {};
  if (ready.status === 200 && health.status === 'ready' && health.dependencies?.database?.healthy === true
      && (health.pendingMigrations ?? 0) === 0) {
    f.push(pass('API is ready with its schema applied', `pendingMigrations=0, activeTenants=${health.activeTenants ?? '?'}`));
    ctx.activeTenants = typeof health.activeTenants === 'number' ? health.activeTenants : null;
  } else {
    f.push(fail('API is ready with its schema applied',
      `GET ${apiBaseUrl}/health/ready → ${describe(ready)}. Pending migrations produce blank modules, not errors.`));
  }

  // 3. The frontend is up, is this product's frontend, and proxies /api to a live backend.
  const root = await http('GET', `${baseUrl}/`);
  f.push(root.status > 0 && root.status < 500
    ? pass('frontend is serving', `${baseUrl}/ → ${root.status}`)
    : fail('frontend is serving', `GET ${baseUrl}/ → ${describe(root)}`));
  const buildInfo = await http('GET', `${baseUrl}/build-info`);
  if (buildInfo.status === 200 && buildInfo.json?.service === 'kynexone-web') {
    ctx.frontendCommit = typeof buildInfo.json.commit === 'string' ? buildInfo.json.commit : null;
  }
  const me = await http('GET', `${baseUrl}/api/auth/me`);
  f.push(me.status === 401
    ? pass('frontend proxies /api to a live API', 'GET /api/auth/me → 401 unauthenticated')
    : fail('frontend proxies /api to a live API',
      `GET ${baseUrl}/api/auth/me → ${describe(me)}; must be 401. ${baseUrl} may be an unrelated server or a broken proxy.`));

  // 4. The build.
  f.push(...checkBuild({
    apiCommit: ctx.apiCommit,
    frontendCommit: ctx.frontendCommit,
    expectedCommit: expectedCommitFrom(env),
    allowUnverified: !!env[E2E_ENV.allowUnverifiedBuild]?.trim(),
    inCi: isCi(env),
  }));

  // 5. The platform owner authenticates against THIS API — the F07 check.
  if (!PLATFORM_EMAIL || !PLATFORM_PASSWORD) return ctx; // already reported by checkEnvironment
  const login = await http('POST', `${apiBaseUrl}/api/platform/auth/login`, {
    body: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD },
  });
  const token = login.json?.token ?? login.json?.accessToken;
  if (login.status === 200 && token) {
    ctx.platformToken = token as string;
    f.push(pass('platform owner authenticates', `${PLATFORM_EMAIL} → 200`));
  } else {
    f.push(fail('platform owner authenticates',
      `POST ${apiBaseUrl}/api/platform/auth/login as ${PLATFORM_EMAIL} → ${describe(login)}.\n`
      + (login.status === 429
        ? '      That is the platform login limiter, not a wrong password. Wait a minute and retry.'
        : '      The API under test was started with a different platform owner than this process presents.\n'
          + `      PlatformOwnerBootstrap creates it once, at API boot, from ${E2E_ENV.platformEmail}/${E2E_ENV.platformPassword}\n`
          + '      (plus PLATFORM_ADMIN_BOOTSTRAP=true on Production-like deployments). Export the SAME two values to\n'
          + '      the API process and to this one — in CI they are generated once and written to GITHUB_ENV.')));
    return ctx;
  }

  // 6. The database the API is connected to — only a platform operator may see its name and host.
  const platformHealth = await http('GET', `${apiBaseUrl}/api/platform/health`, { token: ctx.platformToken });
  ctx.database = platformHealth.status === 200 ? databaseFrom(platformHealth.json) : null;
  f.push(...checkDatabase(ctx.database, {
    expectedName: env[E2E_ENV.expectedDatabase]?.trim() || undefined,
    hostAllowlist: databaseHostAllowlistFrom(env),
  }));

  // 7. The frontend's /api proxy reaches the same API and the same database. A token minted directly
  //    by the API is only accepted through the proxy when both sides share signing key and session store.
  const viaProxy = await http('GET', `${baseUrl}/api/platform/health`, { token: ctx.platformToken });
  const proxied = viaProxy.status === 200 ? databaseFrom(viaProxy.json) : null;
  f.push(proxied && ctx.database && proxied.name === ctx.database.name && proxied.host === ctx.database.host
    ? pass('frontend proxies to the same API and database', `${ctx.database.name} on ${ctx.database.host}`)
    : fail('frontend proxies to the same API and database',
      `GET ${baseUrl}/api/platform/health with a token from ${apiBaseUrl} → ${describe(viaProxy)}. `
      + 'The browser would talk to a different API or database from the one the bootstrap provisioned.'));
  return ctx;
}

// ── World ─────────────────────────────────────────────────────────────────────────────────────

const bootstrapPersonas = (): Persona[] => PERSONAS.filter((p) => p.provisionedBy === 'bootstrap');

async function login(apiBaseUrl: string, persona: Persona, env: Env): Promise<Response> {
  const pacing = Number(env[E2E_ENV.loginPacingMs] ?? 0) || 0;
  for (let attempt = 1; ; attempt++) {
    if (pacing) await sleep(pacing);
    const res = await http('POST', `${apiBaseUrl}/api/auth/login`, {
      body: { email: persona.email, password: persona.password, tenantSlug: persona.tenantSlug },
    });
    if (res.status !== 429 || attempt >= 8) return res;
    // The limiter is respected, never raised: wait out the window and try again.
    const retryAfter = Number(res.headers?.get('retry-after') ?? 0);
    await sleep(Math.max(retryAfter * 1000, 10_000));
  }
}

async function readManifest(): Promise<WorldManifest | null> {
  try { return JSON.parse(await readFile(WORLD_MANIFEST, 'utf8')) as WorldManifest; } catch { return null; }
}

interface LiveTenant { id: string; createdAtUtc: string | null }

async function liveTenants(ctx: TargetContext, f: Finding[]): Promise<Record<string, LiveTenant>> {
  const found: Record<string, LiveTenant> = {};
  const res = await http('GET', `${ctx.apiBaseUrl}/api/platform/tenants`, { token: ctx.platformToken! });
  const rows: any[] = Array.isArray(res.json) ? res.json : res.json?.items ?? [];
  if (res.status !== 200) {
    f.push(fail('declared tenants exist', `GET /api/platform/tenants → ${describe(res)}`));
    return found;
  }
  for (const tenant of TENANTS) {
    const row = rows.find((t) => t.slug === tenant.slug);
    if (row) found[tenant.slug] = { id: String(row.id), createdAtUtc: row.createdAtUtc ?? null };
  }
  const missing = TENANTS.filter((t) => !found[t.slug]).map((t) => t.slug);
  f.push(missing.length
    ? fail('declared tenants exist', `missing: ${missing.join(', ')}. Run the bootstrap.`)
    : pass('declared tenants exist', TENANTS.map((t) => t.slug).join(', ')));
  return found;
}

/** A UTC timestamp from the API; one without a zone designator is UTC (the field says so). */
const utcMillis = (value: string | null): number => {
  if (!value) return Number.NaN;
  return Date.parse(/[zZ]|[+-]\d\d:?\d\d$/.test(value) ? value : `${value}Z`);
};

function loadCatalog(f: Finding[]): RoleCatalog | null {
  try {
    const catalog = loadRoleCatalog();
    f.push(pass('role catalog generated from AuthSeeder.cs', `${catalog.roles.length} roles, ${catalog.permissions.length} permissions`));
    return catalog;
  } catch (error) {
    f.push(fail('role catalog generated from AuthSeeder.cs', error instanceof Error ? error.message : String(error)));
    return null;
  }
}

/**
 * What the tenant administrator can see the API actually installed — the legal entities and the live
 * role catalog — compared with the declarations they must match. Run by `world` and again, live, by
 * every `lane`.
 */
async function checkTenantInstall(
  ctx: TargetContext, tenant: FixtureTenant, token: string, catalog: RoleCatalog, f: Finding[],
): Promise<void> {
  const declaredCodes = tenant.companies.map((c) => c.code);
  const companies = await http('GET', `${ctx.apiBaseUrl}/api/companies?page=1&pageSize=100`, { token });
  const rows: any[] = Array.isArray(companies.json) ? companies.json : companies.json?.items ?? [];
  const codes = rows.map((c) => String(c.legalNameEn ?? c.LegalNameEn ?? '')).sort();
  const missing = declaredCodes.filter((c) => !codes.includes(c));
  const extra = codes.filter((c) => !declaredCodes.includes(c));
  f.push(companies.status === 200 && !missing.length && !extra.length
    ? pass(`tenant ${tenant.slug}: legal entities`, codes.join(', '))
    : fail(`tenant ${tenant.slug}: legal entities`,
      companies.status !== 200 ? describe(companies)
        : `${missing.length ? `missing [${missing.join(', ')}] ` : ''}${extra.length ? `undeclared [${extra.join(', ')}]` : ''}`));

  const roles = await http('GET', `${ctx.apiBaseUrl}/api/access/roles`, { token });
  const live: any[] = Array.isArray(roles.json) ? roles.json : [];
  const drift: string[] = [];
  for (const role of catalog.roles) {
    const row = live.find((r) => r.name === role.name && r.isSystem !== false);
    if (!row) { drift.push(`${role.name} not installed`); continue; }
    const perms = [...new Set<string>(row.permissions ?? [])].sort();
    const missingP = role.permissions.filter((p) => !perms.includes(p));
    const extraP = perms.filter((p) => !role.permissions.includes(p));
    if (missingP.length || extraP.length) {
      drift.push(`${role.name}: ${missingP.length ? `missing [${missingP.join(', ')}] ` : ''}${extraP.length ? `extra [${extraP.join(', ')}]` : ''}`);
    }
  }
  for (const row of live.filter((r) => r.isSystem === true)) {
    if (!catalog.roles.some((role) => role.name === row.name)) drift.push(`${row.name} is a system role AuthSeeder does not define`);
  }
  f.push(roles.status === 200 && !drift.length
    ? pass(`tenant ${tenant.slug}: live role catalog is AuthSeeder's`, `${catalog.roles.length} roles match`)
    : fail(`tenant ${tenant.slug}: live role catalog is AuthSeeder's`,
      roles.status !== 200 ? describe(roles) : `${drift.join('; ')}. The API or its database is not this checkout's.`));
}

const tokenOf = (res: Response): string | undefined => res.json?.accessToken ?? res.json?.token;

async function checkWorld(ctx: TargetContext, env: Env): Promise<boolean> {
  const f = ctx.findings;
  const manifest = await readManifest();
  if (!manifest) {
    f.push(fail('the bootstrap recorded this world', `${WORLD_MANIFEST} does not exist. Run the bootstrap first.`));
    return false;
  }
  f.push(manifest.baseUrl.replace(/\/+$/, '') === ctx.baseUrl
    ? pass('the bootstrap recorded this world', `${WORLD_MANIFEST} for ${manifest.baseUrl}`)
    : fail('the bootstrap recorded this world', `${WORLD_MANIFEST} was written for ${manifest.baseUrl}, not ${ctx.baseUrl}`));

  const tenants = await liveTenants(ctx, f);
  for (const t of manifest.tenants) {
    if (tenants[t.slug] && tenants[t.slug].id !== t.tenantId) {
      f.push(fail(`tenant ${t.slug} is the provisioned one`,
        `the API has ${t.slug}=${tenants[t.slug].id}, the bootstrap recorded ${t.tenantId}: this is a different database`));
    }
  }

  const catalog = loadCatalog(f);
  if (!catalog) return false;

  for (const tenant of TENANTS) {
    const personas = bootstrapPersonas().filter((p) => p.tenantSlug === tenant.slug);
    const declaredCodes = tenant.companies.map((c) => c.code);
    for (const persona of personas) {
      const res = await login(ctx.apiBaseUrl, persona, env);
      const user = res.json?.user ?? {};
      const session = {
        status: res.status === 200 && !tokenOf(res) ? 0 : res.status,
        body: res.status === 200 ? 'no access token (MFA or password-setup challenge?)' : describe(res),
        tenantSlug: user.tenantSlug,
        roles: user.roles,
        permissions: user.permissions,
        companyCodes: (user.companies ?? []).map((c: any) => String(c.code)),
        isGroupScope: user.isGroupScope,
        employeeId: user.employeeId ?? null,
      };
      let expected: string[];
      try {
        expected = expectedPermissions(catalog, [persona.role!]);
      } catch (error) {
        f.push(fail(`persona ${persona.email}: role exists in AuthSeeder`, error instanceof Error ? error.message : String(error)));
        continue;
      }
      f.push(...checkPersonaSession({
        key: persona.key, email: persona.email, tenantSlug: tenant.slug, role: persona.role!,
        scope: persona.scope === 'companies' ? 'companies' : 'group', companyCodes: persona.companyCodes,
        employeeLinked: persona.employeeLinked, tenantCompanyCodes: declaredCodes, expectedPermissions: expected,
      }, session));

      const token = tokenOf(res);
      if (persona.email === tenant.admin.email && token) await checkTenantInstall(ctx, tenant, token, catalog, f);
    }
  }
  return true;
}

// ── Lane ──────────────────────────────────────────────────────────────────────────────────────

async function checkLane(ctx: TargetContext, env: Env): Promise<void> {
  const f = ctx.findings;
  f.push((ctx.activeTenants ?? 0) >= 1
    ? pass('the database is provisioned', `${ctx.activeTenants} active tenant(s)`)
    : fail('the database is provisioned', `/health/ready reports ${ctx.activeTenants ?? 'no'} active tenants. Run the bootstrap.`));

  let record: PreflightRecord | null = null;
  try { record = JSON.parse(await readFile(PREFLIGHT_RECORD, 'utf8')) as PreflightRecord; } catch { /* missing */ }
  if (!record || record.phase !== 'world' || !record.expected) {
    f.push(fail('this world passed the world preflight',
      `${PREFLIGHT_RECORD} does not exist. The bootstrap writes it after verifying every persona; run\n`
      + '      npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts\n'
      + '      (or, for an already-provisioned stack, E2E_PREFLIGHT_PHASE=world npx playwright test -c e2e/preflight/playwright.preflight.config.ts).'));
    return;
  }

  // 1. The lane is set up for the same world that passed: same URLs, expected build and database,
  //    platform owner, tenants, personas and role catalog source.
  const diff = expectedWorldDiff(record.expected, expectedWorld(env));
  f.push(diff.length
    ? fail('this world passed the world preflight',
      `${PREFLIGHT_RECORD} (verified ${record.verifiedAtUtc}) was verified for a different world:\n      ${diff.join('\n      ')}`)
    : pass('this world passed the world preflight',
      `verified ${record.verifiedAtUtc}: ${record.expected.personas.length} personas across ${record.expected.tenants.length} tenants`));
  if (diff.length || !ctx.platformToken) return;

  // 2. And the live stack still IS that world. The target checks above already proved the URLs, the
  //    builds, the database and the platform owner live; here the tenants must still exist and must
  //    predate the verification (a database re-provisioned since would show newer tenants), and each
  //    tenant's legal entities and role catalog are re-read from the API.
  const verifiedAt = Date.parse(record.verifiedAtUtc);
  const tenants = await liveTenants(ctx, f);
  const recreated = Object.entries(tenants)
    .filter(([, t]) => !(utcMillis(t.createdAtUtc) <= verifiedAt + CLOCK_TOLERANCE_MS))
    .map(([slug, t]) => `${slug} (created ${t.createdAtUtc ?? 'at an unknown time'})`);
  f.push(recreated.length
    ? fail('tenants predate the verification',
      `${recreated.join(', ')} after the world was verified at ${record.verifiedAtUtc}: this database was `
      + 're-provisioned since. Run the world preflight again.')
    : pass('tenants predate the verification', `all ${Object.keys(tenants).length} created before ${record.verifiedAtUtc}`));

  const catalog = loadCatalog(f);
  if (!catalog) return;
  for (const tenant of TENANTS) {
    if (!tenants[tenant.slug]) continue; // already reported
    const admin = PERSONAS.find((p) => p.tenantSlug === tenant.slug && p.email === tenant.admin.email)!;
    const res = await login(ctx.apiBaseUrl, admin, env);
    const token = tokenOf(res);
    if (res.status !== 200 || !token) {
      f.push(fail(`tenant ${tenant.slug}: administrator signs in`, describe(res)));
      continue;
    }
    await checkTenantInstall(ctx, tenant, token, catalog, f);
  }
}

// ── Entry point ───────────────────────────────────────────────────────────────────────────────

export async function runPreflight(phase: Phase, env: Env = process.env): Promise<PreflightRecord | null> {
  const ctx = await checkTarget(env);
  const targetOk = ctx.findings.every((x) => x.ok);

  let worldChecked = false;
  if (targetOk && phase === 'world') worldChecked = await checkWorld(ctx, env);
  if (targetOk && phase === 'lane') await checkLane(ctx, env);

  const { ok, report } = formatFindings(phase, ctx.findings);
  console.log(report);
  if (!ok) {
    throw new Error(
      `${report}\nThis is a FAILURE, not a skip: a run against the wrong world must never be able to go green.`,
    );
  }
  if (!worldChecked) return null;

  // Only locally computed values are recorded — the expected world and this machine's clock. Every
  // check that looked at the live stack has already passed above, or this line is never reached.
  const record: PreflightRecord = {
    phase: 'world',
    verifiedAtUtc: new Date().toISOString(),
    expected: expectedWorld(env),
  };
  await mkdir(dirname(PREFLIGHT_RECORD), { recursive: true });
  await writeFile(PREFLIGHT_RECORD, JSON.stringify(record, null, 2), { mode: 0o600 });
  console.log(`[preflight] World verified and recorded in ${PREFLIGHT_RECORD}.`);
  return record;
}
