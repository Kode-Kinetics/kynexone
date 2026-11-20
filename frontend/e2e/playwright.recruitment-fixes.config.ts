import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// Recruitment walkthrough fixes against a mocked API, no stack, a dev server only (recruitment-fixes-A|B|C.spec.ts, one per screen region).
// RECRUITMENT_FIXES_PORT picks the port (default 5178), RECRUITMENT_FIXES_DIST the server's NEXT_DIST_DIR so a run never touches a shared .next build.
// RECRUITMENT_FIXES_SHOTS=<dir> also saves screenshots. Parallel runs use different ports and dist dirs.
const PORT = process.env.RECRUITMENT_FIXES_PORT || '5178';
export default defineConfig({
  testDir: '.', testMatch: /recruitment-fixes-[ABC]\.spec\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 120_000,
  forbidOnly: !!process.env.CI,
  outputDir: `/tmp/kynex-recruitment-fixes-results-${PORT}`,
  use: { baseURL: `http://localhost:${PORT}`, trace: 'retain-on-failure', actionTimeout: 15_000 },
  webServer: {
    command: `npx next dev -p ${PORT}`, url: `http://localhost:${PORT}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 180_000,
    env: { NEXT_DIST_DIR: process.env.RECRUITMENT_FIXES_DIST || '.next-claude-recfix' },
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1366, height: 768 } } },
    { name: 'phone', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 } } },
  ],
});
