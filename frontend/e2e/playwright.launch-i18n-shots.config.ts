import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

/**
 * Arabic screenshot tour of the launch screens (Payroll, Leave, Saudi compliance, GOSI filing, Attendance,
 * Recruitment, New hires) against a mocked API: nothing is read or written, only a dev server is needed.
 *
 *   LAUNCH_I18N_SHOTS=/tmp/claude/demo/i18n npx playwright test -c e2e/playwright.launch-i18n-shots.config.ts
 *
 * LAUNCH_I18N_PORT picks the port (default 5185) and LAUNCH_I18N_DIST the server's NEXT_DIST_DIR, so a run never
 * touches a shared .next build. The spec file is named *.shots.ts so no other config collects it.
 */
const PORT = process.env.LAUNCH_I18N_PORT || '5185';
export default defineConfig({
  testDir: '.', testMatch: /launch-i18n-shots\.shots\.ts/, workers: 1, retries: 0, reporter: 'list', timeout: 180_000,
  outputDir: '/tmp/kynex-launch-i18n-results',
  use: { baseURL: `http://localhost:${PORT}`, actionTimeout: 15_000 },
  webServer: {
    command: `npx next dev -p ${PORT}`, url: `http://localhost:${PORT}/login`, cwd: path.resolve(__dirname, '..'), reuseExistingServer: true, timeout: 240_000,
    env: { NEXT_DIST_DIR: process.env.LAUNCH_I18N_DIST || '.next-claude-i18n' },
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1366, height: 768 } } },
    { name: 'phone', use: { ...devices['Pixel 7'], viewport: { width: 390, height: 844 } } },
  ],
});
