import { runPreflight } from './preflight/run';

/**
 * Hard pre-flight for every browser lane. Throws — which aborts the whole run before a single
 * test reports a result.
 *
 * It is the `lane` phase of e2e/preflight/run.ts (register item F07), which re-proves, every time a
 * lane starts:
 *  • the frontend and the API are disposable hosts (never a deployed one), are up, and the API is
 *    ready with no pending migrations — the old checks this file made, kept;
 *  • both are the expected build, and the frontend proxies to the same API and database;
 *  • the database is not production, and the platform owner this process presents authenticates;
 *  • the world matches the one the bootstrap provisioned and the `world` preflight verified —
 *    same URLs, builds, database and tenant ids, every persona verified.
 *
 * ── Why a pre-flight and NOT `webServer` ─────────────────────────────────────
 * The obvious fix for "neither config has a webServer" is to add one. It is the wrong fix here:
 *
 *  1. The system under test is a FOUR-service stack (Postgres, Redis, the .NET API, the Next
 *     frontend), wired by docker compose with a health-gated start order. `webServer` runs a list
 *     of commands with a port check; it cannot express that dependency graph.
 *  2. `webServer` proves a PORT IS OPEN. The failure mode that actually bites this product is not
 *     "nothing is listening" — it is "everything is listening and the tenant has no data", or (F07)
 *     "everything is listening and it is a different world". A webServer entry would be green for both.
 *  3. `reuseExistingServer` (the default outside CI) would silently adopt whatever is already on
 *     :5173 — including an unrelated project's dev server, or a stale prebuilt image.
 *  4. The backend needs run-specific env (`RateLimit__LoginPermitLimit`, the platform owner).
 *     Encoding that in playwright.config.ts duplicates docker-compose and the two drift.
 *
 * So the stack stays externally managed and the test run VERIFIES it, loudly, up front.
 */
export default async function globalSetup(): Promise<void> {
  await runPreflight('lane');
}
