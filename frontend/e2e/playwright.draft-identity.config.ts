import { defineConfig, devices } from '@playwright/test';

// Mocked-API browser lane for the New hires identity field. Run against `NEXT_DIST_DIR=.next-claude-draftid npx next dev -p 5180`.
export default defineConfig({
  testDir: '.', testMatch: /new-hire-draft-identity\.spec\.ts/,
  workers: 1, retries: 0, timeout: 60_000, reporter: 'list', outputDir: '/tmp/kynex-draft-identity-results',
  use: { baseURL: process.env.DRAFT_IDENTITY_BASE_URL || 'http://127.0.0.1:5180', actionTimeout: 15_000, screenshot: 'only-on-failure', trace: 'retain-on-failure' },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 900 } } }],
});
