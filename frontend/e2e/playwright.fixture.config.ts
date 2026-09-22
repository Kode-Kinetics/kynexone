import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

/**
 * Fixture lane: renders real pages against route-intercepted API responses. Needs neither the
 * docker stack nor seeded data — only `next dev`, which this config starts itself.
 *
 *   npx playwright test -c e2e/playwright.fixture.config.ts
 *
 * What it can prove: layout, states, derivations and accessibility for a KNOWN payload
 * (including awkward ones copied from a real tenant). What it cannot: that the API really
 * returns that payload — that is the stack-backed suite's job.
 */
const PORT = Number(process.env.FIXTURE_PORT ?? 5180);

export default defineConfig({
  testDir: '.',
  testMatch: /dashboard-glass\.spec\.ts/,
  fullyParallel: false,
  retries: 0,
  workers: 1,
  reporter: 'list',
  timeout: 60_000,
  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: 'retain-on-failure',
  },
  webServer: {
    command: `npx next dev -p ${PORT}`,
    url: `http://localhost:${PORT}/dashboard`,
    cwd: path.resolve(__dirname, '..'),
    reuseExistingServer: true,
    timeout: 180_000,
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'tablet', use: { ...devices['Desktop Chrome'], viewport: { width: 834, height: 1112 }, hasTouch: true } },
    { name: 'phone', use: { ...devices['Pixel 7'] } },
  ],
});
