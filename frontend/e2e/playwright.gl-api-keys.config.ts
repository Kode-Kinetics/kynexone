import { defineConfig, devices } from '@playwright/test';

// Setup → GL & rates → ERP API access with every API request intercepted by gl-api-keys.spec.ts.
// Start a dev server separately (e.g. `npx next dev -p 5523`); this suite never touches a real tenant.
export default defineConfig({
  testDir: '.',
  testMatch: /gl-api-keys\.spec\.ts/,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 90_000,
  reporter: 'list',
  outputDir: '/tmp/kynex-gl-api-keys-results',
  use: {
    baseURL: process.env.GL_API_KEYS_BASE_URL || 'http://127.0.0.1:5523',
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1440, height: 1000 },
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
});
