import { defineConfig, devices } from '@playwright/test';
import { resolveTarget } from '../identity/env';

/**
 * The EVIDENCE lane. One story, run end to end against a disposable stack, producing the bundle
 * under `docs/acceptance/payroll-journey/` (override with E2E_EVIDENCE_DIR).
 *
 * `globalSetup` is the shared LANE preflight (e2e/global-setup.ts, register item F07): the bundle's
 * whole claim is that it is evidence about a particular build of a particular world, so the run
 * refuses to start unless the frontend and the API are disposable hosts, are the expected build,
 * proxy to the same non-production database, and match the world the bootstrap provisioned and
 * verified. Evidence gathered without that is evidence about nothing in particular.
 *
 * retries: 0, deliberately and not negotiably. A retry would overwrite the bundle with a second
 * attempt and file it as the first — a passing picture of a step that failed. If the story is
 * flaky, that is a finding about the story.
 *
 * A fixed 1280×900 viewport so captures are comparable between runs and small enough to commit.
 */
export default defineConfig({
  testDir: '.',
  testMatch: /payroll-journey\.evidence\.spec\.ts/,
  globalSetup: '../global-setup.ts',
  fullyParallel: false,
  retries: 0,
  workers: 1,
  forbidOnly: !!process.env.CI,
  timeout: 900_000,
  reporter: [['list'], ['../identity/actor-reporter.ts']],
  use: {
    baseURL: resolveTarget().baseUrl,
    ...devices['Desktop Chrome'],
    viewport: { width: 1280, height: 900 },
    trace: 'retain-on-failure',
    // The harness takes every screenshot itself, only when the screen is settled. Playwright's own
    // failure screenshot is left OFF so an unsettled frame can never land beside the bundle and be
    // mistaken for part of it.
    screenshot: 'off',
  },
});
