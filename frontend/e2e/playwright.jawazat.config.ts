import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '.', testMatch: /jawazat\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 90_000,
  outputDir: '/tmp/kynex-jawazat-playwright-results',
  use: { baseURL: 'http://localhost:5183', trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: { command: 'npx next dev -p 5183', url: 'http://localhost:5183/ess', cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000 },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'phone', use: { ...devices['Pixel 7'] } },
  ],
});
