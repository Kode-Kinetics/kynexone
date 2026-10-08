import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// The Self-Service workspace against a mocked API (e2e/ess-workspace.spec.ts): no stack, a dev server only.
export default defineConfig({
  testDir: '.', testMatch: /ess-workspace\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 120_000,
  outputDir: '/tmp/kynex-ess-workspace-playwright-results',
  use: { baseURL: 'http://localhost:5184', trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: { command: 'npx next dev -p 5184', url: 'http://localhost:5184/login', cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000 },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'phone', use: { ...devices['Pixel 7'] } },
  ],
});
