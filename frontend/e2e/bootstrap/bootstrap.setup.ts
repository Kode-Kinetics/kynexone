import { test as setup } from '@playwright/test';
import { provisionWorld } from './provision';

/**
 * Entry point for the fixture-world bootstrap.
 *
 * It is a Playwright "test" purely so it can be invoked with the toolchain the repo already has —
 * `npx playwright test -c e2e/bootstrap/playwright.bootstrap.config.ts` — with no ts-node/tsx
 * dependency added and no second transpile configuration to keep in step with the specs.
 *
 * It deliberately does NOT use e2e/global-setup.ts: that pre-flight refuses a stack with zero
 * active tenants, which is precisely the state this step exists to leave behind.
 */
setup('provision the e2e fixture world through the platform-admin API', async () => {
  // Four tenants, ~14 companies, ~30 users and ~60 employees over HTTP. Slower than a seeder, and
  // that is the trade: every row here came through a real, audited product endpoint.
  setup.setTimeout(600_000);
  const baseUrl = process.env.PLAYWRIGHT_BASE_URL ?? process.env.E2E_BASE_URL ?? 'http://localhost:5173';
  await provisionWorld(baseUrl);
});
