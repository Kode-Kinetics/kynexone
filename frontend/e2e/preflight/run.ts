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
 *           against a different stack), the old readiness floor, and a match against the `world`
 *           record: same URLs, same builds, same database, same tenant ids. Persona logins are not
 *           repeated here — the record proves them for this exact world, and the lanes' own setup
 *           projects sign every persona in again anyway.
 *
 * Decision logic lives in ./rules.ts (pure, unit-tested); this file only gathers observations.
 */
import { execSync } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import { E2E_ENV, isCi, resolveTarget, type Env } from '../identity/env';
import { expectedPermissions, loadRoleCatalog, type RoleCatalog } from '../identity/role-catalog';
import {
  PERSONAS, PLATFORM_EMAIL, PLATFORM_PASSWORD, TENANTS, WORLD_MANIFEST, type Persona, type WorldManifest,
} from '../world';
import {
  checkBuild, checkDatabase, checkEnvironment, checkPersonaSession, checkTargetUrl, databaseHostAllowlistFrom,
  formatFindings, hostAllowlistFrom, type DatabaseIdentity, type Finding,
} from './rules';

export type Phase = 'target' | 'world' | 'lane';

/** Written by the `world` phase; read by every `lane` phase. Gitignored with the rest of e2e/.auth. */
export const PREFLIGHT_RECORD = 'e2e/.auth/preflight.json';

export interface PreflightRecord {
  phase: 'world';
  verifiedAtUtc: string;
  baseUrl: string;
  apiBaseUrl: string;
  apiCommit: string | null;
  frontendCommit: string | null;
  database: DatabaseIdentity | null;
  platformEmail: string;
  tenants: Record<string, string>;
  personas: string[];
}

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
  const inCi = isCi(env);
  const expectedCommit = env[E2E_ENV.expectedCommit]?.trim() || (inCi ? null : gitHead());
  f.push(...checkBuild({
    apiCommit: ctx.apiCommit,
    frontendCommit: ctx.frontendCommit,
    expectedCommit,
    allowUnverified: !!env[E2E_ENV.allowUnverifiedBuild]?.trim(),
    inCi,
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

async function tenantIds(ctx: TargetContext, f: Finding[]): Promise<Record<string, string>> {
  const ids: Record<string, string> = {};
  const res = await http('GET', `${ctx.apiBaseUrl}/api/platform/tenants`, { token: ctx.platformToken! });
  const rows: any[] = Array.isArray(res.json) ? res.json : res.json?.items ?? [];
  if (res.status !== 200) {
    f.push(fail('declared tenants exist', `GET /api/platform/tenants → ${describe(res)}`));
    return ids;
  }
  for (const tenant of TENANTS) {
    const row = rows.find((t) => t.slug === tenant.slug);
    if (row) ids[tenant.slug] = String(row.id);
  }
  const missing = TENANTS.filter((t) => !ids[t.slug]).map((t) => t.slug);
  f.push(missing.length
    ? fail('declared tenants exist', `missing: ${missing.join(', ')}. Run the bootstrap.`)
    : pass('declared tenants exist', TENANTS.map((t) => t.slug).join(', ')));
  return ids;
}

async function checkWorld(ctx: TargetContext, env: Env): Promise<PreflightRecord | null> {
  const f = ctx.findings;
  const manifest = await readManifest();
  if (!manifest) {
    f.push(fail('the bootstrap recorded this world', `${WORLD_MANIFEST} does not exist. Run the bootstrap first.`));
    return null;
  }
  f.push(manifest.baseUrl.replace(/\/+$/, '') === ctx.baseUrl
    ? pass('the bootstrap recorded this world', `${WORLD_MANIFEST} for ${manifest.baseUrl}`)
    : fail('the bootstrap recorded this world', `${WORLD_MANIFEST} was written for ${manifest.baseUrl}, not ${ctx.baseUrl}`));

  const ids = await tenantIds(ctx, f);
  for (const t of manifest.tenants) {
    if (ids[t.slug] && ids[t.slug] !== t.tenantId) {
      f.push(fail(`tenant ${t.slug} is the provisioned one`,
        `the API has ${t.slug}=${ids[t.slug]}, the bootstrap recorded ${t.tenantId}: this is a different database`));
    }
  }

  let catalog: RoleCatalog;
  try {
    catalog = loadRoleCatalog();
    f.push(pass('role catalog generated from AuthSeeder.cs', `${catalog.roles.length} roles, ${catalog.permissions.length} permissions`));
  } catch (error) {
    f.push(fail('role catalog generated from AuthSeeder.cs', error instanceof Error ? error.message : String(error)));
    return null;
  }

  const verified: string[] = [];
  for (const tenant of TENANTS) {
    const personas = bootstrapPersonas().filter((p) => p.tenantSlug === tenant.slug);
    const declaredCodes = tenant.companies.map((c) => c.code);
    for (const persona of personas) {
      const res = await login(ctx.apiBaseUrl, persona, env);
      const user = res.json?.user ?? {};
      const session = {
        status: res.status === 200 && !(res.json?.accessToken ?? res.json?.token) ? 0 : res.status,
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
      const results = checkPersonaSession({
        key: persona.key, email: persona.email, tenantSlug: tenant.slug, role: persona.role!,
        scope: persona.scope === 'companies' ? 'companies' : 'group', companyCodes: persona.companyCodes,
        employeeLinked: persona.employeeLinked, tenantCompanyCodes: declaredCodes, expectedPermissions: expected,
      }, session);
      f.push(...results);
      if (results.every((r) => r.ok)) verified.push(persona.key);

      // The tenant administrator can read what the API actually installed: the legal entities and
      // the live role catalog. Both are compared with the declarations they must match.
      const token = res.json?.accessToken ?? res.json?.token;
      if (persona.email === tenant.admin.email && token) {
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
    }
  }

  return {
    phase: 'world',
    verifiedAtUtc: new Date().toISOString(),
    baseUrl: ctx.baseUrl,
    apiBaseUrl: ctx.apiBaseUrl,
    apiCommit: ctx.apiCommit,
    frontendCommit: ctx.frontendCommit,
    database: ctx.database,
    platformEmail: PLATFORM_EMAIL,
    tenants: ids,
    personas: verified,
  };
}

// ── Lane ──────────────────────────────────────────────────────────────────────────────────────

async function checkLane(ctx: TargetContext): Promise<void> {
  const f = ctx.findings;
  f.push((ctx.activeTenants ?? 0) >= 1
    ? pass('the database is provisioned', `${ctx.activeTenants} active tenant(s)`)
    : fail('the database is provisioned', `/health/ready reports ${ctx.activeTenants ?? 'no'} active tenants. Run the bootstrap.`));

  let record: PreflightRecord | null = null;
  try { record = JSON.parse(await readFile(PREFLIGHT_RECORD, 'utf8')) as PreflightRecord; } catch { /* missing */ }
  if (!record || record.phase !== 'world') {
    f.push(fail('this world passed the world preflight',
      `${PREFLIGHT_RECORD} does not exist. The bootstrap writes it after verifying every persona; run\n`
      + '      npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts\n'
      + '      (or, for an already-provisioned stack, E2E_PREFLIGHT_PHASE=world npx playwright test -c e2e/preflight/playwright.preflight.config.ts).'));
    return;
  }
  const mismatches: string[] = [];
  if (record.baseUrl !== ctx.baseUrl) mismatches.push(`frontend ${record.baseUrl} → ${ctx.baseUrl}`);
  if (record.apiBaseUrl !== ctx.apiBaseUrl) mismatches.push(`API ${record.apiBaseUrl} → ${ctx.apiBaseUrl}`);
  if ((record.apiCommit ?? '') !== (ctx.apiCommit ?? '')) mismatches.push(`API build ${record.apiCommit} → ${ctx.apiCommit}`);
  if ((record.frontendCommit ?? '') !== (ctx.frontendCommit ?? '')) mismatches.push(`frontend build ${record.frontendCommit} → ${ctx.frontendCommit}`);
  if (JSON.stringify(record.database) !== JSON.stringify(ctx.database)) {
    mismatches.push(`database ${record.database?.name}@${record.database?.host} → ${ctx.database?.name}@${ctx.database?.host}`);
  }
  if (record.platformEmail !== PLATFORM_EMAIL) mismatches.push(`platform owner ${record.platformEmail} → ${PLATFORM_EMAIL}`);
  if (ctx.platformToken) {
    const ids = await tenantIds(ctx, f);
    for (const [slug, id] of Object.entries(record.tenants)) {
      if (ids[slug] !== id) mismatches.push(`tenant ${slug} ${id} → ${ids[slug] ?? 'absent'}`);
    }
  }
  const unverified = bootstrapPersonas().map((p) => p.key).filter((k) => !record!.personas.includes(k));
  if (unverified.length) mismatches.push(`personas not verified: ${unverified.join(', ')}`);
  f.push(mismatches.length
    ? fail('this world passed the world preflight',
      `${PREFLIGHT_RECORD} (verified ${record.verifiedAtUtc}) describes a different world: ${mismatches.join('; ')}`)
    : pass('this world passed the world preflight',
      `verified ${record.verifiedAtUtc}: ${record.personas.length} personas across ${Object.keys(record.tenants).length} tenants`));
}

// ── Entry point ───────────────────────────────────────────────────────────────────────────────

export async function runPreflight(phase: Phase, env: Env = process.env): Promise<PreflightRecord | null> {
  const ctx = await checkTarget(env);
  const targetOk = ctx.findings.every((x) => x.ok);

  let record: PreflightRecord | null = null;
  if (targetOk && phase === 'world') record = await checkWorld(ctx, env);
  if (targetOk && phase === 'lane') await checkLane(ctx);

  const { ok, report } = formatFindings(phase, ctx.findings);
  console.log(report);
  if (!ok) {
    throw new Error(
      `${report}\nThis is a FAILURE, not a skip: a run against the wrong world must never be able to go green.`,
    );
  }
  if (record) {
    await mkdir(dirname(PREFLIGHT_RECORD), { recursive: true });
    await writeFile(PREFLIGHT_RECORD, JSON.stringify(record, null, 2), { mode: 0o600 });
    console.log(`[preflight] World verified and recorded in ${PREFLIGHT_RECORD}.`);
  }
  return record;
}
