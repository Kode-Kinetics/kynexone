import { request as pwRequest } from '@playwright/test';

/**
 * Hard pre-flight for every Playwright lane. Throws — which aborts the whole run before a single
 * test reports a result.
 *
 * ── Why a pre-flight and NOT `webServer` ─────────────────────────────────────
 * The obvious fix for "neither config has a webServer" is to add one. It is the wrong fix here:
 *
 *  1. The system under test is a FOUR-service stack (Postgres, Redis, the .NET API, the Next
 *     frontend), wired by docker compose with a health-gated start order. `webServer` runs a list
 *     of commands with a port check; it cannot express that dependency graph.
 *  2. `webServer` proves a PORT IS OPEN. The failure mode that actually bites this product is not
 *     "nothing is listening" — it is "everything is listening and the tenant has no data". A
 *     webServer entry would have been perfectly green for every blank-module bug in this repo's
 *     history.
 *  3. `reuseExistingServer` (the default outside CI) would silently adopt whatever is already on
 *     :5173 — including an unrelated project's dev server, or this stack holding last week's seed.
 *  4. The backend needs run-specific env (`RateLimit__LoginPermitLimit`, the seed flags). Encoding
 *     that in playwright.config.ts duplicates docker-compose and the two drift.
 *
 * So the stack stays externally managed and the test run VERIFIES it, loudly, up front. That is
 * strictly more honest than starting a server and assuming the rest.
 */

const BASE_URL = process.env.PLAYWRIGHT_BASE_URL ?? process.env.E2E_BASE_URL ?? 'http://localhost:5173';

/** Minimum row counts the demo lanes assert against. A stack below these is seeded wrong. */
const SEED_FLOOR = { employees: 1 };

export default async function globalSetup(): Promise<void> {
  const api = await pwRequest.newContext({ baseURL: BASE_URL, timeout: 20_000 });
  const fail = (msg: string): never => {
    throw new Error(
      `\n──────────────────────────────────────────────────────────────────\n` +
      `E2E PRE-FLIGHT FAILED — no tests were run.\n${msg}\n` +
      `This is a FAILURE, not a skip: a dead or unseeded backend must never produce a green run.\n` +
      `──────────────────────────────────────────────────────────────────\n`,
    );
  };

  try {
    // 1. Frontend is serving.
    let page;
    try {
      page = await api.get('/');
    } catch (error) {
      return fail(`Frontend unreachable at ${BASE_URL}: ${error instanceof Error ? error.message : String(error)}`);
    }
    if (page.status() >= 500) return fail(`Frontend at ${BASE_URL}/ returned ${page.status()}.`);

    // 2. The /api proxy reaches the REAL backend auth endpoint. A 401 proves the whole chain;
    //    accepting any sub-500 response would let a stray dev server pass as a healthy HRM API.
    let me;
    try {
      me = await api.get('/api/auth/me');
    } catch (error) {
      return fail(`API proxy unreachable via ${BASE_URL}/api: ${error instanceof Error ? error.message : String(error)}`);
    }
    if (me.status() !== 401) {
      const preview = (await me.text()).replace(/\s+/g, ' ').slice(0, 200);
      return fail(
        `GET ${BASE_URL}/api/auth/me must return 401, got ${me.status()}.\n` +
        `${BASE_URL} may point at an unrelated frontend or a broken API proxy. Body: ${preview}`,
      );
    }

    // 3. The backend considers itself ready, with migrations applied. `pendingMigrations > 0` is
    //    the "code shipped ahead of its migrations" failure, and it produces blank modules rather
    //    than errors — exactly the shape a length-based smoke test cannot see.
    const apiBase = process.env.E2E_API_BASE_URL ?? 'http://localhost:5117';
    const direct = await pwRequest.newContext({ baseURL: apiBase, timeout: 20_000 });
    try {
      const ready = await direct.get('/health/ready');
      if (!ready.ok()) return fail(`GET ${apiBase}/health/ready returned ${ready.status()}.`);
      const health = await ready.json();
      if (health.status !== 'ready') return fail(`Backend reports status="${health.status}".`);
      if (health.dependencies?.database?.healthy !== true)
        return fail(`Backend cannot reach its database: ${JSON.stringify(health.dependencies?.database)}`);
      if ((health.pendingMigrations ?? 0) > 0)
        return fail(`Backend has ${health.pendingMigrations} PENDING MIGRATIONS — the schema is behind the code.`);
      if ((health.activeTenants ?? 0) < SEED_FLOOR.employees)
        return fail(`Backend reports ${health.activeTenants} active tenants — the database is not seeded.`);
      console.log(
        `[pre-flight] OK — ${BASE_URL} → ${apiBase}: ready, ` +
        `${health.activeTenants} active tenants, 0 pending migrations.`,
      );
    } catch (error) {
      if (error instanceof Error && error.message.includes('PRE-FLIGHT FAILED')) throw error;
      return fail(`Backend health check failed at ${apiBase}/health/ready: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      await direct.dispose();
    }
  } finally {
    await api.dispose();
  }
}
