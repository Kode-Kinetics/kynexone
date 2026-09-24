import { defineConfig } from '@playwright/test';

/**
 * The bootstrap lane. Run it once, after migrations and before any spec lane:
 *
 *   npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts
 *
 * Its own config, for two reasons that both matter:
 *
 *  1. `playwright.config.ts` and `playwright.security.config.ts` both use e2e/global-setup.ts,
 *     whose pre-flight FAILS when the backend reports zero active tenants. That guard is correct —
 *     an unprovisioned stack must never produce a green spec run — but it is exactly the state the
 *     bootstrap starts from, so the bootstrap cannot share it.
 *  2. The bootstrap needs no browser at all. Keeping it out of the browser configs means a
 *     provisioning failure is reported as a provisioning failure, not as "the setup project of the
 *     chromium lane threw, so 112 tests did not run".
 *
 * retries:0 — re-running a partially applied provisioning pass is what the idempotency in
 * provision.ts is for, not something a retry should paper over on the way to a green tick.
 */
export default defineConfig({
  testDir: '.',
  testMatch: /bootstrap\.setup\.ts/,
  retries: 0,
  workers: 1,
  reporter: 'list',
  timeout: 600_000,
});
