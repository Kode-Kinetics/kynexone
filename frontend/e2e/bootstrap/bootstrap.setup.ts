import { test as setup } from '@playwright/test';
import { provisionWorld } from './provision';
import { resolveTarget } from '../identity/env';
import { runPreflight } from '../preflight/run';

/**
 * Entry point for the fixture-world bootstrap.
 *
 * It is a Playwright "test" purely so it can be invoked with the toolchain the repo already has —
 * `npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts` — with no ts-node/tsx
 * dependency added and no second transpile configuration to keep in step with the specs.
 *
 * It deliberately does NOT use e2e/global-setup.ts: that pre-flight refuses a stack with zero
 * active tenants, which is precisely the state this step exists to leave behind. Instead it brackets
 * provisioning with the two preflight phases that fit it (register item F07):
 *   • `target` BEFORE writing anything — the right, disposable, expected-build stack, a non-production
 *     database, and a platform owner that authenticates. The 28 Sep run skipped this and learned about
 *     a mismatched platform identity from a 401 inside provisioning;
 *   • `world` AFTER — every tenant, legal entity and employee link exists and every persona signs in
 *     with exactly its declared role, scope and catalog permissions. It records the verified world in
 *     e2e/.auth/preflight.json, which every lane's global setup then requires.
 */
setup('provision the e2e fixture world through the platform-admin API', async () => {
  // Four tenants, ~14 companies, ~35 users and ~60 employees over HTTP. Slower than a seeder, and
  // that is the trade: every row here came through a real, audited product endpoint.
  setup.setTimeout(900_000);
  await runPreflight('target');
  await provisionWorld(resolveTarget().baseUrl);
  await runPreflight('world');
});
