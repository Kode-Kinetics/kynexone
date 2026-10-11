import path from 'node:path';
import { defineConfig } from '@playwright/test';

// LRN acceptance journey against a REAL disposable stack (see learning-journey.spec.ts). Headed Google Chrome, one
// worker, no retries: each step writes evidence into docs/acceptance/capability-closure/evidence/lrn/.
// The sample cmi5 unit is a TEST FIXTURE (e2e/fixtures/cmi5-fire-safety), never shipped in the app: this config serves
// it from its own origin (127.0.0.1:5411) for the run only.
export default defineConfig({
  webServer: {
    command: 'python3 -m http.server 5411 --bind 127.0.0.1',
    cwd: path.resolve(__dirname, 'fixtures/cmi5-fire-safety'),
    url: 'http://127.0.0.1:5411/index.html',
    reuseExistingServer: false,
    timeout: 30_000,
  },
  testDir: '.',
  testMatch: /learning-journey\.spec\.ts/,
  workers: 1,
  retries: 0,
  timeout: 240_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-lrn-journey-results',
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL || 'http://localhost:5311',
    channel: 'chrome',
    headless: false,
    actionTimeout: 20_000,
    navigationTimeout: 60_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
});
